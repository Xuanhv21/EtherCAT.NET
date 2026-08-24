using System.Globalization;

namespace EtherCAT.NET.Engine.Esi;

/// <summary>
/// Parses the numeric literal formats used throughout ESI (EtherCAT Slave Information) XML files.
/// ESI documents mix two conventions for numbers within the same file: EtherCAT's own
/// hexadecimal notation <c>#x1234</c> (also seen as <c>#X1234</c> or lower-case digits such as
/// <c>#x1a00</c>), and plain decimal literals such as <c>16</c>. <c>XmlSerializer</c> has
/// no built-in notion of this dual format, which is why the parser in this namespace is built on
/// <see cref="System.Xml.Linq.XDocument"/> instead and funnels every numeric attribute/element
/// value through <see cref="Parse(string)"/>.
/// </summary>
public static class EsiNumber
{
    /// <summary>
    /// Parses <paramref name="text"/> as either an ESI hex literal (<c>#x...</c> / <c>0x...</c>)
    /// or a plain decimal integer.
    /// </summary>
    /// <exception cref="FormatException">The text is null, empty, or not a recognized numeric literal.</exception>
    public static ulong Parse(string text)
    {
        if (!TryParse(text, out var value))
        {
            throw new FormatException($"'{text}' is not a valid ESI numeric literal (expected '#xHEX' or a decimal integer).");
        }

        return value;
    }

    /// <summary>
    /// Attempts to parse <paramref name="text"/> as either an ESI hex literal (<c>#x...</c> /
    /// <c>0x...</c>) or a plain decimal integer. Returns <c>false</c> instead of throwing when the
    /// text is missing or malformed.
    /// </summary>
    public static bool TryParse(string? text, out ulong value)
    {
        value = 0;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var span = text.AsSpan().Trim();

        if (span.Length > 2 && span[0] == '#' && (span[1] == 'x' || span[1] == 'X'))
        {
            return ulong.TryParse(span[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        }

        if (span.Length > 2 && span[0] == '0' && (span[1] == 'x' || span[1] == 'X'))
        {
            return ulong.TryParse(span[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        }

        return ulong.TryParse(span, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }
}
