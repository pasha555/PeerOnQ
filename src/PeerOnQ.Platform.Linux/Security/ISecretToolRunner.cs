namespace PeerOnQ.Platform.Linux.Security;

internal interface ISecretToolRunner
{
    Task<SecretToolResult> RunAsync(
        IReadOnlyList<string> arguments,
        string? standardInput,
        CancellationToken cancellationToken);
}

internal sealed record SecretToolResult(int ExitCode, string StandardOutput);
