using System.Collections.Concurrent;
using System.Text.Json;

namespace PeerOnQ.Signaling.Server.Security;

/// <summary>
/// Where the server keeps the public key it pinned for each PeerOnQ ID.
/// Only public material is ever stored here.
/// </summary>
public interface IDevicePinStore
{
    IReadOnlyDictionary<string, string> Load();

    void Save(IReadOnlyDictionary<string, string> pins);
}

/// <summary>Non-persistent store; useful in tests and for a throwaway server.</summary>
public sealed class InMemoryDevicePinStore : IDevicePinStore
{
    private IReadOnlyDictionary<string, string> _pins = new Dictionary<string, string>();

    public IReadOnlyDictionary<string, string> Load() => _pins;

    public void Save(IReadOnlyDictionary<string, string> pins) => _pins = new Dictionary<string, string>(pins);
}

/// <summary>
/// JSON file store. Writes go to a temporary file and are then moved into place, so a crash
/// mid-write cannot leave a truncated pin file that would lock every device out.
/// </summary>
public sealed class FileDevicePinStore(string path, ILogger<FileDevicePinStore>? logger = null) : IDevicePinStore
{
    private readonly Lock _gate = new();

    public string Path { get; } = path;

    public IReadOnlyDictionary<string, string> Load()
    {
        lock (_gate)
        {
            if (!File.Exists(Path)) return new Dictionary<string, string>();

            try
            {
                var json = File.ReadAllText(Path);
                return JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                       ?? new Dictionary<string, string>();
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                logger?.LogError(ex,
                    "Pin file at {Path} is unreadable. Starting with no pins; the first registration " +
                    "for each device will pin again", Path);
                return new Dictionary<string, string>();
            }
        }
    }

    public void Save(IReadOnlyDictionary<string, string> pins)
    {
        lock (_gate)
        {
            var directory = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temporary = Path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(pins, new JsonSerializerOptions
            {
                WriteIndented = true,
            }));

            File.Move(temporary, Path, overwrite: true);
        }
    }
}
