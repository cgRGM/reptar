using System.Collections;

namespace Reptar;

/// <summary>
/// Builds LIMIT/OFFSET clauses with bound parameters so paginated queries
/// reuse one SQL template. Interpolated page numbers defeat the per-SQL
/// mapper cache (<see cref="EntityMapper{T}"/>), server-side plan caches,
/// and N+1 template detection, so every driver that supports named
/// parameters binds <c>__limit</c>/<c>__offset</c> instead of inlining them.
/// Exotic parameter shapes (raw POCOs, positional value arrays) fall back to
/// literal interpolation and keep their previous behavior.
/// </summary>
internal static class SqlPagination
{
    public const string LimitParam = "__limit";
    public const string OffsetParam = "__offset";

    /// <summary>Appends D1 pagination bindings to an already-normalized JSON parameter array.</summary>
    internal static (string Sql, string ParamsJson) AppendPositionalJson(
        string sql, string paramsJson, int limit, int offset)
    {
        if (limit <= 0) return (sql, paramsJson);
        var json = paramsJson.Trim();
        if (json.Length < 2 || json[0] != '[' || json[^1] != ']')
            throw new ArgumentException("D1 pagination requires a JSON parameter array.", nameof(paramsJson));
        var bindings = limit.ToString(System.Globalization.CultureInfo.InvariantCulture);
        sql += " limit ?";
        if (offset > 0)
        {
            sql += " offset ?";
            bindings += "," + offset.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        var separator = json.AsSpan(1, json.Length - 2).Trim().IsEmpty ? "" : ",";
        return (sql, json[..^1] + separator + bindings + "]");
    }

    /// <summary>
    /// True when <paramref name="param"/> is null or a named shape the
    /// ADO.NET drivers can bind (<see cref="IReptarEntity"/>, dictionary,
    /// name/value tuple, or a sequence of name/value tuples).
    /// </summary>
    public static bool SupportsNamedParams(object? param)
    {
        if (param is null or IReptarEntity or IDictionary)
            return true;

        if (param is System.Runtime.CompilerServices.ITuple { Length: 2 })
            return true;

        if (param is IEnumerable sequence and not string)
        {
            foreach (var item in sequence)
            {
                if (item is not System.Runtime.CompilerServices.ITuple { Length: 2 })
                    return false;
            }

            return true;
        }

        return false;
    }

    /// <summary>
    /// Copies the named parameters of <paramref name="param"/> into a fresh
    /// dictionary and appends <c>__limit</c> (and <c>__offset</c> when
    /// positive). The caller's parameter object is never mutated.
    /// </summary>
    public static Dictionary<string, object?> MergeNamed(object? param, int limit, int offset)
    {
        var merged = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        switch (param)
        {
            case null:
                break;
            case IReptarEntity entity:
                foreach (var (name, value) in entity.ToDbParameters())
                    merged[name] = value;
                break;
            case IDictionary map:
                foreach (DictionaryEntry entry in map)
                    merged[entry.Key?.ToString() ?? ""] = entry.Value;
                break;
            case System.Runtime.CompilerServices.ITuple { Length: 2 } single:
                merged[single[0]?.ToString() ?? ""] = single[1];
                break;
            case IEnumerable sequence and not string:
                foreach (var item in sequence)
                {
                    if (item is System.Runtime.CompilerServices.ITuple { Length: 2 } tuple)
                        merged[tuple[0]?.ToString() ?? ""] = tuple[1];
                }
                break;
        }

        merged[LimitParam] = limit;

        if (offset > 0)
            merged[OffsetParam] = offset;

        return merged;
    }
}
