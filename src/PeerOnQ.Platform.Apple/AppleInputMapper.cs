namespace PeerOnQ.Platform.Apple;

/// <summary>Maps bounded printable input to the Windows virtual keys used by protocol v2.</summary>
public static class AppleInputMapper
{
    public static bool TryMapAscii(char character, out ushort virtualKey, out bool shift)
    {
        shift = false;
        if (character is >= 'a' and <= 'z')
        {
            virtualKey = (ushort)char.ToUpperInvariant(character);
            return true;
        }
        if (character is >= 'A' and <= 'Z')
        {
            virtualKey = character;
            shift = true;
            return true;
        }
        if (character is >= '0' and <= '9')
        {
            virtualKey = character;
            return true;
        }

        var direct = character switch
        {
            ' ' => 0x20,
            ',' => 0xbc,
            '.' => 0xbe,
            '-' => 0xbd,
            '=' => 0xbb,
            '[' => 0xdb,
            ']' => 0xdd,
            '\\' => 0xdc,
            ';' => 0xba,
            '\'' => 0xde,
            '/' => 0xbf,
            '`' => 0xc0,
            '\n' or '\r' => 0x0d,
            '\t' => 0x09,
            '\b' => 0x08,
            _ => 0,
        };
        if (direct != 0)
        {
            virtualKey = (ushort)direct;
            return true;
        }

        const string shifted = "!@#$%^&*()_+{}|:\"<>?~";
        const string bases = "1234567890-=[]\\;',./`";
        var index = shifted.IndexOf(character, StringComparison.Ordinal);
        if (index >= 0)
        {
            shift = true;
            return TryMapAscii(bases[index], out virtualKey, out _);
        }

        virtualKey = 0;
        return false;
    }
}
