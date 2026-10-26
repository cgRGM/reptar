namespace Reptar;

/// <summary>
/// Minimal, regex-free cron matcher for the Kestrel scheduler. Supports
/// 5-field (min hour day month dow) and 6-field (sec min hour day month dow)
/// expressions with "*", "a", "a-b", "a-b/n", "*/n", "a/n", and comma lists.
/// The legacy "*" wildcard registered through OnScheduled matches every tick.
/// Under Native AOT and NetWasm this stays a plain parser: no reflection,
/// no regular expressions (see the NetWasm Binaryen note in design.md).
/// </summary>
public static class CronExpr
{
    public static bool Matches(string expression, DateTimeOffset at)
    {
        if (expression == "*")
            return true;

        var fields = expression.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (fields.Length is < 5 or > 6)
            return false;

        var secondOffset = fields.Length == 6 ? 1 : 0;
        var second = at.Second;
        var minute = at.Minute;
        var hour = at.Hour;
        var day = at.Day;
        var month = at.Month;
        var dow = (int)at.DayOfWeek == 0 ? 7 : (int)at.DayOfWeek; // cron: 1=Mon..7=Sun

        if (secondOffset == 1 && !MatchField(fields[0], second, 0, 59))
            return false;

        if (!MatchField(fields[secondOffset], minute, 0, 59) ||
            !MatchField(fields[secondOffset + 1], hour, 0, 23) ||
            !MatchField(fields[secondOffset + 3], month, 1, 12))
        {
            return false;
        }

        var domField = fields[secondOffset + 2];
        var dowField = fields[secondOffset + 4];
        var domMatch = MatchField(domField, day, 1, 31);
        var dowMatch = MatchDow(dowField, dow);

        // Classic cron rule: when both day-of-month and day-of-week are
        // restricted, a match on either satisfies the day constraint.
        var domRestricted = !IsWildcard(domField);
        var dowRestricted = !IsWildcard(dowField);

        if (domRestricted && dowRestricted)
            return domMatch || dowMatch;

        return domMatch && dowMatch;
    }

    public static bool HasSeconds(string expression)
    {
        if (expression == "*")
            return false;

        var count = 0;
        foreach (var _ in expression.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            count++;

        return count == 6;
    }

    private static bool IsWildcard(string field) => field == "*";

    private static bool MatchDow(string field, int value)
    {
        // Cron allows 0-7 where both 0 and 7 mean Sunday; we normalize to 1-7.
        foreach (var part in field.Split(','))
        {
            if (part == "*")
                return true;

            var step = 1;
            var range = part;
            var slash = part.IndexOf('/');
            if (slash >= 0)
            {
                if (!int.TryParse(part[(slash + 1)..], out step) || step < 1)
                    return false;
                range = part[..slash];
            }

            if (range == "*")
                return true;

            int from, to;
            var dash = range.IndexOf('-');
            if (dash >= 0)
            {
                if (!int.TryParse(range[..dash], out from) ||
                    !int.TryParse(range[(dash + 1)..], out to))
                {
                    return false;
                }
            }
            else if (!int.TryParse(range, out from))
            {
                return false;
            }
            else
            {
                to = slash >= 0 ? 7 : from;
            }

            if (from == 0)
                from = 7;
            if (to == 0)
                to = 7;

            if (value >= from && value <= to && (value - from) % step == 0)
                return true;
        }

        return false;
    }

    private static bool MatchField(string field, int value, int min, int max)
    {
        foreach (var part in field.Split(','))
        {
            if (part == "*")
                return true;

            var step = 1;
            var range = part;
            var slash = part.IndexOf('/');
            if (slash >= 0)
            {
                if (!int.TryParse(part[(slash + 1)..], out step) || step < 1)
                    return false;
                range = part[..slash];
            }

            int from, to;
            if (range == "*")
            {
                from = min;
                to = max;
            }
            else
            {
                var dash = range.IndexOf('-');
                if (dash >= 0)
                {
                    if (!int.TryParse(range[..dash], out from) ||
                        !int.TryParse(range[(dash + 1)..], out to))
                    {
                        return false;
                    }
                }
                else if (!int.TryParse(range, out from))
                {
                    return false;
                }
                else
                {
                    // "a/n" means start at a and step to the field maximum.
                    to = slash >= 0 ? max : from;
                }
            }

            if (value >= from && value <= to && (value - from) % step == 0)
                return true;
        }

        return false;
    }
}
