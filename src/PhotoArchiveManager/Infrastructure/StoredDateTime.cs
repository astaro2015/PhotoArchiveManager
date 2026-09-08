using System.Globalization;

namespace PhotoArchiveManager.Infrastructure;

/// <summary>
/// Culture-independent parser/formatter for date/time text persisted by PAM.
/// Capture/event dates are intentionally stored without a timezone; audit timestamps use round-trip UTC text.
/// Keeping this logic shared prevents behaviour from changing with Windows regional settings.
/// </summary>
public static class StoredDateTime
{
    private static readonly string[] LocalFormats =
    [
        "yyyy-MM-dd HH:mm:ss",
        "yyyy-MM-dd HH:mm:ss.FFFFFFF",
        "yyyy-MM-dd HH.mm.ss",
        "yyyy-MM-dd HH.mm.ss.FFFFFFF",
        "yyyy-MM-dd'T'HH:mm:ss",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF"
    ];

    public static string Format(DateTime value) =>
        value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    public static string FormatPreservingFraction(DateTime value) =>
        value.Ticks % TimeSpan.TicksPerSecond == 0
            ? Format(value)
            : value.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture);

    public static bool TryNormalize(string? value, out string normalized)
    {
        if (TryParse(value, out var parsed))
        {
            normalized = FormatPreservingFraction(parsed);
            return true;
        }

        normalized = value?.Trim() ?? "";
        return false;
    }

    public static bool TryParse(string? value, out DateTime result)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            result = default;
            return false;
        }

        if (DateTime.TryParseExact(
                value,
                LocalFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces,
                out result))
            return true;

        // Legacy PAM catalogues can contain invariant date text with a different fractional precision.
        // This fallback remains deterministic across machines because CurrentCulture is never consulted.
        return DateTime.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces,
            out result);
    }

    public static bool TryParseRoundTripUtc(string? value, out DateTime result)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            result = default;
            return false;
        }

        return DateTime.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.RoundtripKind,
            out result);
    }
}
