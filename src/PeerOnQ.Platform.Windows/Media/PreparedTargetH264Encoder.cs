using PeerOnQ.Media.Codecs;
using static PeerOnQ.Platform.Windows.Media.MediaFoundationVideoUtilities;

namespace PeerOnQ.Platform.Windows.Media;

/// <summary>A hardware encoder whose MFT can be created for given targets before its first frame.</summary>
internal interface IRetargetableH264Encoder : IHardwareH264Encoder
{
    void Prepare(int width, int height, int bitrateKbps, int frameRate);

    long NextSampleTime { get; set; }
}

/// <summary>
/// Changes the bitrate or frame rate without stalling the encode loop. A hardware MFT only honours
/// the target it was created with (HardwareVideoRateControlTests), and creating one in place held the
/// loop for 150-230 ms at every quality change. The current MFT keeps encoding at its old target while
/// a replacement for the new one is created on the thread pool; the first frame after the switch is
/// the replacement's key frame. A new size still recreates in place (the old MFT cannot encode it),
/// and a replacement the driver refuses falls back to that in-place path.
/// </summary>
internal sealed class PreparedTargetH264Encoder : IHardwareH264Encoder
{
    private readonly Func<IRetargetableH264Encoder> _create;
    private readonly Lock _gate = new();
    private IRetargetableH264Encoder _active;
    private Targets? _activeTargets;
    private Task<IRetargetableH264Encoder>? _replacement;
    private Targets _replacementTargets;
    private bool _disposed;

    private readonly record struct Targets(int Width, int Height, int BitrateKbps, int FrameRate);

    public PreparedTargetH264Encoder() : this(() => new MediaFoundationH264Encoder()) { }

    internal PreparedTargetH264Encoder(Func<IRetargetableH264Encoder> create)
    {
        _create = create;
        _active = create();
    }

    public string Name
    {
        get { lock (_gate) return _active.Name; }
    }

    /// <summary>Quality changes applied by switching to a prepared MFT.</summary>
    internal int Switches { get; private set; }

    /// <summary>Quality changes that recreated the MFT in place because no replacement was possible.</summary>
    internal int InPlaceFallbacks { get; private set; }

    internal bool IsReplacementPending
    {
        get { lock (_gate) return _replacement is not null; }
    }

    public bool SupportsDimensions(int width, int height) => CompatibleH264Dimensions(width, height);

    public byte[] EncodeI420(int width, int height, byte[] frame, int bitrateKbps, int frameRate, bool forceKeyFrame) =>
        Encode(width, height, frame, bitrateKbps, frameRate, forceKeyFrame, nv12: false);

    public byte[] EncodeNv12(int width, int height, byte[] frame, int bitrateKbps, int frameRate, bool forceKeyFrame) =>
        Encode(width, height, frame, bitrateKbps, frameRate, forceKeyFrame, nv12: true);

    private byte[] Encode(int width, int height, byte[] frame, int bitrateKbps, int frameRate, bool forceKeyFrame,
        bool nv12)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var (kbps, fps) = MediaFoundationH264Encoder.ClampTargets(bitrateKbps, frameRate);
            var requested = new Targets(width, height, kbps, fps);
            if (_activeTargets is not { } active || active.Width != width || active.Height != height)
            {
                DiscardReplacement();
                _activeTargets = requested;
            }
            else if (active != requested)
            {
                Retarget(requested);
            }
            else
            {
                // Back at the current target before an abandoned replacement was needed.
                DiscardReplacement();
            }

            var targets = _activeTargets.Value;
            return nv12
                ? _active.EncodeNv12(width, height, frame, targets.BitrateKbps, targets.FrameRate, forceKeyFrame)
                : _active.EncodeI420(width, height, frame, targets.BitrateKbps, targets.FrameRate, forceKeyFrame);
        }
    }

    private void Retarget(Targets requested)
    {
        if (_replacement is null)
        {
            Prepare(requested);
            return;
        }
        if (!_replacement.IsCompleted) return;

        var finished = _replacement;
        _replacement = null;
        if (!finished.IsCompletedSuccessfully)
        {
            _ = finished.Exception; // Observed: the in-place path below handles the refusal.
            _activeTargets = requested;
            InPlaceFallbacks++;
            return;
        }
        if (_replacementTargets != requested)
        {
            // The target moved again while this one was being created.
            DisposeLater(finished.Result);
            Prepare(requested);
            return;
        }

        var previous = _active;
        _active = finished.Result;
        _active.NextSampleTime = previous.NextSampleTime;
        _activeTargets = requested;
        Switches++;
        DisposeLater(previous);
    }

    private void Prepare(Targets targets)
    {
        _replacementTargets = targets;
        var create = _create;
        _replacement = Task.Run(() =>
        {
            var next = create();
            try
            {
                next.Prepare(targets.Width, targets.Height, targets.BitrateKbps, targets.FrameRate);
                return next;
            }
            catch
            {
                next.Dispose();
                throw;
            }
        });
    }

    private void DiscardReplacement()
    {
        if (_replacement is not { } pending) return;
        _replacement = null;
        _ = pending.ContinueWith(static task =>
        {
            if (!task.IsCompletedSuccessfully) _ = task.Exception;
            else DisposeQuietly(task.Result);
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    private static void DisposeLater(IRetargetableH264Encoder encoder) =>
        _ = Task.Run(() => DisposeQuietly(encoder));

    private static void DisposeQuietly(IRetargetableH264Encoder encoder)
    {
        try { encoder.Dispose(); }
        catch (Exception) { /* A retired MFT must not fail the session that replaced it. */ }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            DiscardReplacement();
            _active.Dispose();
        }
    }
}
