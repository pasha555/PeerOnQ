namespace PeerOnQ.Platform.Android;

/// <summary>Maps Android hardware-key codes to the Windows virtual keys used by protocol v2.</summary>
public static class AndroidKeyCodeMapper
{
    public static bool TryMap(int keyCode, out ushort virtualKey, out bool extended)
    {
        extended = false;
        if (keyCode is >= 7 and <= 16)
        {
            virtualKey = (ushort)(0x30 + keyCode - 7);
            return true;
        }
        if (keyCode is >= 29 and <= 54)
        {
            virtualKey = (ushort)(0x41 + keyCode - 29);
            return true;
        }
        if (keyCode is >= 131 and <= 142)
        {
            virtualKey = (ushort)(0x70 + keyCode - 131);
            return true;
        }

        var mapped = keyCode switch
        {
            19 => (0x26, true),  // D-pad up
            20 => (0x28, true),  // D-pad down
            21 => (0x25, true),  // D-pad left
            22 => (0x27, true),  // D-pad right
            55 => (0xbc, false), // comma
            56 => (0xbe, false), // period
            57 => (0x12, false), // left alt
            58 => (0x12, true),  // right alt
            59 => (0x10, false), // left shift
            60 => (0x10, false), // right shift
            61 => (0x09, false), // tab
            62 => (0x20, false), // space
            66 => (0x0d, false), // enter
            67 => (0x08, false), // backspace
            68 => (0xc0, false), // grave
            69 => (0xbd, false), // minus
            70 => (0xbb, false), // equals
            71 => (0xdb, false), // left bracket
            72 => (0xdd, false), // right bracket
            73 => (0xdc, false), // backslash
            74 => (0xba, false), // semicolon
            75 => (0xde, false), // apostrophe
            76 => (0xbf, false), // slash
            92 => (0x21, true),  // page up
            93 => (0x22, true),  // page down
            111 => (0x1b, false), // escape
            112 => (0x2e, true),  // delete
            113 => (0x11, false), // left control
            114 => (0x11, true),  // right control
            117 => (0x5b, true),  // left meta
            118 => (0x5c, true),  // right meta
            122 => (0x24, true),  // home
            123 => (0x23, true),  // end
            124 => (0x2d, true),  // insert
            _ => (0, false),
        };
        virtualKey = (ushort)mapped.Item1;
        extended = mapped.Item2;
        return virtualKey != 0;
    }
}
