namespace PeerOnQ.Application.Sessions;

public sealed record SessionOptions
{
    /// <summary>How long the viewer waits for the server to route the request.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>How long the sharer's dialog stays open. No answer means decline.</summary>
    public TimeSpan PermissionTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan NegotiationTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Maximum time spent recovering one interruption, including backoff.</summary>
    public TimeSpan ReconnectTimeout { get; init; } = TimeSpan.FromSeconds(45);

    public TimeSpan ReconnectInitialDelay { get; init; } = TimeSpan.FromMilliseconds(500);

    public TimeSpan ReconnectMaxDelay { get; init; } = TimeSpan.FromSeconds(8);

    /// <summary>
    /// Retry count is intentionally high enough for backoff to cover the bounded confirmation
    /// window; the time limits below remain authoritative and prevent an endless reconnect loop.
    /// </summary>
    public int MaxReconnectAttempts { get; init; } = 20;

    /// <summary>Random variation applied to every retry delay to avoid synchronized retries.</summary>
    public double ReconnectJitterFraction { get; init; } = 0.20;

    /// <summary>
    /// Beyond this interruption the previous security context is considered uncertain. The
    /// session ends and a new permission prompt is required instead of silently resuming.
    /// </summary>
    public TimeSpan ResumeWithoutConfirmationLimit { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Longest wait for one session-release step (collaboration, media close/dispose, capture).
    /// A step that hangs is logged and left behind, so the session still ends, logs its reason and
    /// removes its indicators, and the application can exit (0.9.87 needed End Task).
    /// </summary>
    public TimeSpan ReleaseStepTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// False (0.9.88 default): one screen-sharing session at a time; a second request waits for a
    /// session that is ending or is declined as busy. 0.9.87 ran two capture/encode pipelines on a
    /// 2-core laptop. True restores concurrent viewers of one host.
    /// </summary>
    public bool AllowConcurrentScreenSharing { get; init; }

    public static SessionOptions Default { get; } = new();
}
