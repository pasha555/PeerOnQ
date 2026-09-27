using Microsoft.Extensions.Options;

namespace PeerOnQ.Downloads.Service;

public sealed class DownloadsOptions
{
    public const string SectionName = "PeerOnQ:Downloads";
    public string[] AllowedArtifactHosts { get; set; } = [];
    public bool AllowHttpArtifacts { get; set; }
    public int CompletionTokenMinutes { get; set; } = 120;
    public string? CompletionTokenKey { get; set; }
    public long MaximumArtifactBytes { get; set; } = 1024L * 1024 * 1024;
    public int MaximumConcurrentStreams { get; set; } = 16;
    public int StreamBufferBytes { get; set; } = 128 * 1024;
    public int StreamDeadlineMinutes { get; set; } = 30;
    public string CacheDirectory { get; set; } = "/var/lib/peeronq/download-cache";
    public long MaximumCacheBytes { get; set; } = 4L * 1024 * 1024 * 1024;
}

public sealed class DownloadsOptionsValidator(IHostEnvironment environment) : IValidateOptions<DownloadsOptions>
{
    public ValidateOptionsResult Validate(string? name, DownloadsOptions options)
    {
        var errors = new List<string>();
        if (options.AllowedArtifactHosts.Length == 0
            || options.AllowedArtifactHosts.Any(host => string.IsNullOrWhiteSpace(host) || host.Length > 253))
            errors.Add("At least one valid artifact host is required.");
        if (options.AllowHttpArtifacts && !environment.IsDevelopment() && !environment.IsEnvironment("Testing"))
            errors.Add("HTTP artifacts are restricted to Development and Testing.");
        if (options.CompletionTokenMinutes is < 15 or > 1440)
            errors.Add("CompletionTokenMinutes must be between 15 and 1440.");
        if (string.IsNullOrWhiteSpace(options.CompletionTokenKey) || options.CompletionTokenKey.Length < 32)
            errors.Add("CompletionTokenKey must contain at least 32 characters.");
        if (options.MaximumArtifactBytes is < 1024L * 1024 or > 4L * 1024 * 1024 * 1024)
            errors.Add("MaximumArtifactBytes must be between 1 MiB and 4 GiB.");
        if (options.MaximumConcurrentStreams is < 1 or > 1024)
            errors.Add("MaximumConcurrentStreams must be between 1 and 1024.");
        if (options.StreamBufferBytes is < 16 * 1024 or > 1024 * 1024)
            errors.Add("StreamBufferBytes must be between 16 KiB and 1 MiB.");
        if (options.StreamDeadlineMinutes is < 1 or > 120)
            errors.Add("StreamDeadlineMinutes must be between 1 and 120.");
        if (string.IsNullOrWhiteSpace(options.CacheDirectory) || !Path.IsPathRooted(options.CacheDirectory))
            errors.Add("CacheDirectory must be an absolute dedicated cache path.");
        else
        {
            try
            {
                if (!environment.IsDevelopment()
                    && !environment.IsEnvironment("Testing")
                    && IsWithin(Path.GetFullPath(options.CacheDirectory), Path.GetFullPath(Path.GetTempPath())))
                    errors.Add("CacheDirectory must not use the operating-system temporary directory.");
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                errors.Add("CacheDirectory is invalid.");
            }
        }
        if (options.MaximumCacheBytes < options.MaximumArtifactBytes || options.MaximumCacheBytes > 64L * 1024 * 1024 * 1024)
            errors.Add("MaximumCacheBytes must cover MaximumArtifactBytes and not exceed 64 GiB.");
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }

    private static bool IsWithin(string candidate, string root)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var trimmedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(candidate.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), trimmedRoot, comparison))
            return true;
        return candidate.StartsWith(trimmedRoot + Path.DirectorySeparatorChar, comparison);
    }
}
