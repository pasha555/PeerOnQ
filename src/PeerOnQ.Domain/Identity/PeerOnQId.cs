using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using PeerOnQ.Domain.Compatibility;

namespace PeerOnQ.Domain.Identity;

/// <summary>
/// Public device address, e.g. <c>LNK-483-921-756-204</c>.
/// Four groups of three digits, generated from a cryptographic RNG.
/// It is never derived from hardware, user or network attributes.
/// </summary>
public readonly record struct PeerOnQId
{
    public const string Prefix = LegacyIdentityCompatibility.DeviceIdPrefix;
    public const int GroupCount = 4;
    public const int GroupLength = 3;

    private readonly string _value;

    private PeerOnQId(string value) => _value = value;

    public string Value => _value ?? throw new InvalidOperationException("PeerOnQ ID is not initialised.");

    /// <summary>Digits only, e.g. <c>483921756204</c>. Useful as a storage key.</summary>
    public string Digits => Value.Replace("-", string.Empty)[Prefix.Length..];

    /// <summary>
    /// Log-safe form: first and last group kept, middle groups masked.
    /// </summary>
    public string Masked
    {
        get
        {
            var groups = Value.Split('-');
            return $"{groups[0]}-{groups[1]}-***-***-{groups[4]}";
        }
    }

    /// <summary>
    /// What the user sees and types: the four groups without the protocol prefix,
    /// e.g. <c>049-769-287-726</c>. The prefix stays on the wire and in storage, and
    /// <see cref="TryParse"/> accepts either form.
    /// </summary>
    public string Display => Value[(Prefix.Length + 1)..];

    /// <summary>Display form with the middle groups masked, e.g. <c>049-***-***-726</c>.</summary>
    public string MaskedDisplay
    {
        get
        {
            var groups = Value.Split('-');
            return $"{groups[1]}-***-***-{groups[4]}";
        }
    }

    /// <summary>
    /// Converts a stored full or masked identifier to the user-facing form without the protocol
    /// prefix. Invalid legacy values retain their masked text after the prefix is removed.
    /// </summary>
    public static string FormatMaskedDisplay(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        if (TryParse(value, out var id)) return id.MaskedDisplay;

        var display = value.Trim();
        return display.StartsWith($"{Prefix}-", StringComparison.OrdinalIgnoreCase)
            ? display[(Prefix.Length + 1)..]
            : display;
    }

    public static PeerOnQId NewId()
    {
        Span<char> buffer = stackalloc char[Prefix.Length + (GroupCount * (GroupLength + 1))];
        Prefix.AsSpan().CopyTo(buffer);

        var position = Prefix.Length;
        for (var group = 0; group < GroupCount; group++)
        {
            buffer[position++] = '-';
            for (var digit = 0; digit < GroupLength; digit++)
            {
                buffer[position++] = (char)('0' + RandomNumberGenerator.GetInt32(0, 10));
            }
        }

        return new PeerOnQId(new string(buffer));
    }

    /// <summary>
    /// Accepts pasted values with spaces, lower case, or no separators at all
    /// (<c>lnk 483 921 756 204</c>, <c>483921756204</c>) and normalises them.
    /// </summary>
    public static bool TryParse(string? input, out PeerOnQId id)
    {
        id = default;
        if (string.IsNullOrWhiteSpace(input)) return false;

        Span<char> digits = stackalloc char[GroupCount * GroupLength];
        var count = 0;

        foreach (var c in input)
        {
            if (char.IsWhiteSpace(c) || c is '-' or '_') continue;
            if (char.IsAsciiDigit(c))
            {
                if (count == digits.Length) return false;
                digits[count++] = c;
                continue;
            }
            if (char.IsAsciiLetter(c))
            {
                // Only the deployed compatibility prefix may contain letters, and only before any digit.
                if (count > 0) return false;
                continue;
            }
            return false;
        }

        if (count != digits.Length) return false;

        var prefixLetters = new string(input.Where(char.IsAsciiLetter).ToArray());
        if (prefixLetters.Length > 0 && !prefixLetters.Equals(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var parts = new string[GroupCount];
        for (var i = 0; i < GroupCount; i++)
        {
            parts[i] = new string(digits.Slice(i * GroupLength, GroupLength));
        }

        id = new PeerOnQId($"{Prefix}-{string.Join('-', parts)}");
        return true;
    }

    public static PeerOnQId Parse(string input) =>
        TryParse(input, out var id)
            ? id
            : throw new FormatException($"'{input}' is not a valid PeerOnQ ID. Expected {Prefix}-000-000-000-000.");

    /// <summary>
    /// Formats an in-progress user entry as four groups of three digits. Non-digits are ignored,
    /// existing separators may be pasted, and input is bounded to the public ID's twelve digits.
    /// </summary>
    public static string FormatDisplayInput(string? input)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;

        Span<char> digits = stackalloc char[GroupCount * GroupLength];
        var count = 0;
        foreach (var character in input)
        {
            if (!char.IsAsciiDigit(character)) continue;
            if (count == digits.Length) break;
            digits[count++] = character;
        }

        if (count == 0) return string.Empty;

        Span<char> formatted = stackalloc char[count + ((count - 1) / GroupLength)];
        var destination = 0;
        for (var source = 0; source < count; source++)
        {
            if (source > 0 && source % GroupLength == 0)
            {
                formatted[destination++] = '-';
            }

            formatted[destination++] = digits[source];
        }

        return new string(formatted);
    }

    public static bool IsValid([NotNullWhen(true)] string? input) => TryParse(input, out _);

    public override string ToString() => Value;
}
