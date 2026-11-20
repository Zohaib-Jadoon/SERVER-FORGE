using System.Globalization;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace ServerForge.Infrastructure.Persistence;

/// <summary>
/// Persists <see cref="DateTimeOffset"/> values in SQLite as fixed-width canonical UTC ISO-8601 strings
/// (<c>yyyy-MM-ddTHH:mm:ss.fffffffZ</c>).
/// <para>
/// Because the provider CLR type is <see cref="string"/> (SQLite <c>TEXT</c>) and all values are normalized
/// to UTC with fixed 7-digit fractional seconds, EF Core 10 natively translates <c>ORDER BY</c>, range
/// comparisons, <c>MIN</c>/<c>MAX</c>, and B-tree index scans directly in SQLite with exact chronological
/// ordering across arbitrary source UTC offsets while keeping existing SQLite <c>TEXT</c> columns compatible.
/// </para>
/// </summary>
public sealed class UtcIso8601DateTimeOffsetConverter : ValueConverter<DateTimeOffset, string>
{
    public const string CanonicalUtcFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";

    public UtcIso8601DateTimeOffsetConverter()
        : base(
            value => FormatUtc(value),
            raw => ParseUtc(raw))
    {
    }

    public static string FormatUtc(DateTimeOffset value)
        => value.ToUniversalTime().ToString(CanonicalUtcFormat, CultureInfo.InvariantCulture);

    public static DateTimeOffset ParseUtc(string raw)
    {
        if (TryParseUtc(raw, out var parsed))
            return parsed;

        throw new FormatException($"Value '{raw}' is not a valid ISO-8601 / SQLite DateTimeOffset timestamp.");
    }

    public static bool TryParseUtc(string? raw, out DateTimeOffset parsed)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            parsed = default;
            return false;
        }

        if (DateTimeOffset.TryParse(
            raw.Trim(),
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var dto))
        {
            parsed = dto.ToUniversalTime();
            return true;
        }

        parsed = default;
        return false;
    }

    public static bool IsCanonicalUtcFormat(string? raw)
    {
        if (string.IsNullOrEmpty(raw) || raw.Length != 28)
            return false;

        return raw[4] == '-' &&
               raw[7] == '-' &&
               raw[10] == 'T' &&
               raw[13] == ':' &&
               raw[16] == ':' &&
               raw[19] == '.' &&
               raw[27] == 'Z';
    }
}
