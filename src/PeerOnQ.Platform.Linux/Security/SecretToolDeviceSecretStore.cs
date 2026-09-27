using System.Text.RegularExpressions;
using PeerOnQ.Application.Abstractions;

namespace PeerOnQ.Platform.Linux.Security;

/// <summary>
/// Stores private device material in the desktop keyring through libsecret's fixed-path
/// secret-tool helper. Secret values are passed through standard input, never command arguments.
/// </summary>
public sealed partial class SecretToolDeviceSecretStore : IDeviceSecretStore
{
    private const int MaxSecretBytes = 64 * 1024;
    private readonly ISecretToolRunner _runner;
    private readonly string _profileId;

    public SecretToolDeviceSecretStore(string profileId = "default")
        : this(new ProcessSecretToolRunner(), profileId)
    {
    }

    internal SecretToolDeviceSecretStore(ISecretToolRunner runner, string profileId = "default")
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        ValidateName(profileId);
        _profileId = profileId;
    }

    public async Task<byte[]?> TryGetAsync(string name, CancellationToken cancellationToken = default)
    {
        ValidateName(name);
        var result = await _runner.RunAsync(
            ["lookup", "application", "peeronq", "profile", _profileId, "name", name],
            standardInput: null,
            cancellationToken).ConfigureAwait(false);

        if (result.ExitCode == 1 || string.IsNullOrWhiteSpace(result.StandardOutput))
            return null;
        if (result.ExitCode != 0)
            throw new InvalidOperationException("The Linux keyring could not read the requested PeerOnQ secret.");

        try
        {
            var secret = Convert.FromBase64String(result.StandardOutput.Trim());
            if (secret.Length > MaxSecretBytes)
            {
                Array.Clear(secret);
                throw new InvalidOperationException("The Linux keyring returned an invalid PeerOnQ secret.");
            }

            return secret;
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException("The Linux keyring returned an invalid PeerOnQ secret.", exception);
        }
    }

    public async Task SetAsync(string name, byte[] secret, CancellationToken cancellationToken = default)
    {
        ValidateName(name);
        ArgumentNullException.ThrowIfNull(secret);
        if (secret.Length is 0 or > MaxSecretBytes)
            throw new ArgumentOutOfRangeException(nameof(secret), "PeerOnQ secrets must contain between 1 byte and 64 KiB.");

        var encoded = Convert.ToBase64String(secret);
        var result = await _runner.RunAsync(
            ["store", "--label=PeerOnQ device identity", "application", "peeronq", "profile", _profileId, "name", name],
            encoded,
            cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new InvalidOperationException("The Linux keyring could not protect the PeerOnQ secret.");
    }

    public async Task RemoveAsync(string name, CancellationToken cancellationToken = default)
    {
        ValidateName(name);
        var result = await _runner.RunAsync(
            ["clear", "application", "peeronq", "profile", _profileId, "name", name],
            standardInput: null,
            cancellationToken).ConfigureAwait(false);
        if (result.ExitCode is not (0 or 1))
            throw new InvalidOperationException("The Linux keyring could not remove the PeerOnQ secret.");
    }

    private static void ValidateName(string name)
    {
        if (!SecretNamePattern().IsMatch(name))
            throw new ArgumentException("Secret names must contain 1-64 lowercase letters, digits, or hyphens.", nameof(name));
    }

    [GeneratedRegex("^[a-z0-9-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex SecretNamePattern();
}
