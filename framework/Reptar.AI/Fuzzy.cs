#if !NETWASM
namespace Reptar.AI;

/// <summary>
/// Fuzzy matching for the slash-command and file dropdowns. A direct port of pi's
/// <c>packages/tui/src/fuzzy.ts</c>.
/// </summary>
/// <remarks>
/// The scoring is what makes a dropdown feel right rather than merely functional. All query
/// characters must appear in order, and the score rewards:
/// <list type="bullet">
/// <item>consecutive matches (so typing "mod" ranks <c>/model</c> above <c>/mode-switch</c>),</item>
/// <item>matches at a word boundary (so "s" ranks <c>/sessions</c> above <c>/tools</c>),</item>
/// <item>earlier matches (so "h" ranks <c>/help</c> above <c>/history</c>).</item>
/// </list>
/// Lower score is better. Matching is case-insensitive.
/// </remarks>
public static class Fuzzy
{
    /// <summary>Result of matching a query against one candidate string.</summary>
    public readonly record struct Match(bool Matches, double Score)
    {
        public static readonly Match No = new(false, double.MaxValue);
    }

    /// <summary>
    /// Scores <paramref name="query"/> against <paramref name="text"/>.
    /// </summary>
    public static Match Score(string query, string text)
    {
        if (string.IsNullOrEmpty(query))
            return new Match(true, 0);

        var normalizedQuery = query.ToLowerInvariant();
        var haystack = text.ToLowerInvariant();

        if (normalizedQuery.Length > haystack.Length)
            return Match.No;

        // Greedy alignment: each query character at its first occurrence after the previous one.
        var positions = new int[normalizedQuery.Length];
        var lastMatchIndex = -1;

        for (var queryIndex = 0; queryIndex < normalizedQuery.Length; queryIndex++)
        {
            var found = haystack.IndexOf(normalizedQuery[queryIndex], lastMatchIndex + 1);
            if (found == -1)
                return Match.No;

            positions[queryIndex] = found;
            lastMatchIndex = found;
        }

        var best = ScorePositions(haystack, positions);

        // Greedy alignment can scatter the query over an earlier, unrelated part of the text:
        // "model" in "models and providers" picks up m-o-d from "models" but could start
        // anywhere. Also score every contiguous occurrence and keep the best alignment.
        for (var start = haystack.IndexOf(normalizedQuery, StringComparison.Ordinal);
             start != -1;
             start = haystack.IndexOf(normalizedQuery, start + 1, StringComparison.Ordinal))
        {
            for (var k = 0; k < positions.Length; k++)
                positions[k] = start + k;

            best = Math.Min(best, ScorePositions(haystack, positions));
        }

        return new Match(true, best);
    }

    /// <summary>
    /// Scores a query against several fields, keeping the best. Used so a command matches on its
    /// name or its description, which is how you find <c>/compact</c> by typing "summarize".
    /// </summary>
    public static Match ScoreAny(string query, params string?[] fields)
    {
        if (string.IsNullOrEmpty(query))
            return new Match(true, 0);

        var best = Match.No;

        foreach (var field in fields)
        {
            if (string.IsNullOrEmpty(field))
                continue;

            var candidate = Score(query, field);
            if (candidate.Matches && candidate.Score < best.Score)
                best = candidate;
        }

        return best;
    }

    /// <summary>
    /// Ranks <paramref name="items"/> against a query, best first. Non-matching items are
    /// dropped; an empty query preserves the original order.
    /// </summary>
    public static List<T> Rank<T>(string query, IEnumerable<T> items, Func<T, string?[]> fields)
    {
        if (string.IsNullOrEmpty(query))
            return [.. items];

        var scored = new List<(T Item, double Score, int Order)>();
        var order = 0;

        foreach (var item in items)
        {
            var match = ScoreAny(query, fields(item));
            if (match.Matches)
                scored.Add((item, match.Score, order));
            order++;
        }

        // Stable: ties keep their original order.
        scored.Sort((left, right) =>
        {
            var byScore = left.Score.CompareTo(right.Score);
            return byScore != 0 ? byScore : left.Order.CompareTo(right.Order);
        });

        return [.. scored.Select(entry => entry.Item)];
    }

    private static double ScorePositions(string text, IReadOnlyList<int> positions)
    {
        double score = 0;
        var lastMatchIndex = -1;
        var consecutive = 0;

        for (var i = 0; i < positions.Count; i++)
        {
            var position = positions[i];

            if (lastMatchIndex == position - 1)
            {
                consecutive++;
                score -= consecutive * 5;
            }
            else
            {
                consecutive = 0;
                if (lastMatchIndex >= 0)
                    score += (position - lastMatchIndex - 1) * 2;
            }

            // Word-boundary bonus: start of string, or after a separator.
            if (position == 0 || IsBoundary(text[position - 1]))
                score -= 10;

            // Slight penalty for later matches.
            score += position * 0.1;

            lastMatchIndex = position;
        }

        return score;
    }

    private static bool IsBoundary(char value) =>
        value is ' ' or '-' or '_' or '.' or '/' or ':';
}
#endif
