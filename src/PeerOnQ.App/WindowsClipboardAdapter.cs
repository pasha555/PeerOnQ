using PeerOnQ.Application.Collaboration;
using Windows.ApplicationModel.DataTransfer;

namespace PeerOnQ.App;

public sealed class WindowsClipboardAdapter : IClipboardAdapter, IDisposable
{
    public WindowsClipboardAdapter() => Clipboard.ContentChanged += OnContentChanged;

    public event EventHandler? ContentChanged;

    public async Task<string?> ReadTextAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var content = Clipboard.GetContent();
        if (!content.Contains(StandardDataFormats.Text)) return null;
        return await content.GetTextAsync().AsTask(cancellationToken);
    }

    public Task WriteTextAsync(string text, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        package.SetText(text);
        Clipboard.SetContent(package);
        Clipboard.Flush();
        return Task.CompletedTask;
    }

    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Clipboard.Clear();
        return Task.CompletedTask;
    }

    private void OnContentChanged(object? sender, object args) => ContentChanged?.Invoke(this, EventArgs.Empty);

    public void Dispose() => Clipboard.ContentChanged -= OnContentChanged;
}
