using System.Diagnostics;

namespace PeerOnQ.Platform.Linux.Security;

internal sealed class ProcessSecretToolRunner : ISecretToolRunner
{
    private const int MaxOutputCharacters = 128 * 1024;
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(30);
    private readonly string _executablePath;

    public ProcessSecretToolRunner()
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("The Linux keyring adapter can run only on Linux.");

        _executablePath = new[] { "/usr/bin/secret-tool", "/bin/secret-tool" }
            .FirstOrDefault(File.Exists)
            ?? throw new PlatformNotSupportedException(
                "PeerOnQ requires secret-tool (libsecret-tools) to protect the device identity in the Linux keyring.");
    }

    public async Task<SecretToolResult> RunAsync(
        IReadOnlyList<string> arguments,
        string? standardInput,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _executablePath,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(OperationTimeout);
        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
            throw new InvalidOperationException("The Linux keyring helper could not be started.");

        try
        {
            if (standardInput is not null)
                await process.StandardInput.WriteAsync(standardInput.AsMemory(), timeout.Token).ConfigureAwait(false);
            process.StandardInput.Close();

            var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);

            var output = await outputTask.ConfigureAwait(false);
            _ = await errorTask.ConfigureAwait(false);
            if (output.Length > MaxOutputCharacters)
                throw new InvalidOperationException("The Linux keyring returned an unexpectedly large response.");

            return new SecretToolResult(process.ExitCode, output);
        }
        catch (OperationCanceledException exception)
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            if (!cancellationToken.IsCancellationRequested)
                throw new TimeoutException("The Linux keyring operation did not complete in time.", exception);
            throw;
        }
    }
}
