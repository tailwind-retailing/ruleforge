using System.Text.Json;

namespace RuleForge.Core.Evaluators;

/// <summary>
/// Tiny JSONPath subset, ported from src/shared/evaluators/string-filter.ts (jsonpathResolve).
///
///   $              root
///   .key  ['key']  property access
///   [N]            numeric index
///   [*]            wildcard over array elements
///   $ctx.foo.bar   shorthand for context paths â€” handled by source.kind === 'context'
///                  before reaching here (the $ctx prefix is also tolerated and stripped)
///
/// Always returns a list â€” empty when nothing matched. Filters out values that
/// resolved to <c>undefined</c> equivalents (missing properties) but keeps
/// JSON nulls in the result, matching the TS reference.
/// </summary>
public static class JsonPath
{
    public static IReadOnlyList<JsonElement?> Resolve(JsonElement? root, string path)
    {
        var cleaned = StripPrefix(path);
        if (cleaned.Length == 0)
            return root is null ? Array.Empty<JsonElement?>() : new[] { root };

        var tokens = Tokenize(cleaned);
        IList<JsonElement?> frontier = new List<JsonElement?> { root };

        foreach (var tok in tokens)
        {
            var next = new List<JsonElement?>();
            foreach (var cur in frontier)
            {
                if (cur is null) continue;
                var v = cur.Value;
                if (v.ValueKind == JsonValueKind.Null) continue;

                if (tok == "*")
                {
                    if (v.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in v.EnumerateArray())
                            next.Add(item);
                    }
                }
                else if (IsInteger(tok))
                {
                    if (v.ValueKind == JsonValueKind.Array)
                    {
                        var idx = int.Parse(tok);
                        if (idx >= 0 && idx < v.GetArrayLength())
                            next.Add(v[idx]);
                        // negative or out-of-range â†’ undefined â†’ contributes nothing
                    }
                }
                else
                {
                    if (v.ValueKind == JsonValueKind.Object)
                    {
                        if (v.TryGetProperty(tok, out var prop))
                            next.Add(prop);
                        // missing prop â†’ undefined â†’ contributes nothing
                    }
                }
            }
            frontier = next;
        }

        // Final filter: drop undefined-equivalents. JSON null is preserved as
        // a JsonElement with ValueKind.Null so downstream null-aware operators
        // (is_null, is_empty) can see it.
        return frontier.Where(e => e is not null).ToList()!;
    }

    private static string StripPrefix(string path)
    {
        // Mirrors TS regex /^\$ctx\.?/ then /^\$\.?/ â€” strip the prefix and an
        // optional dot, leaving any leading '[' intact so $['a'] still works.
        if (path.StartsWith("$ctx"))
        {
            var rest = path[4..];
            if (rest.StartsWith(".")) rest = rest[1..];
            return rest;
        }
        if (path.StartsWith("$"))
        {
            var rest = path[1..];
            if (rest.StartsWith(".")) rest = rest[1..];
            return rest;
        }
        return path;
    }

    private static List<string> Tokenize(string path)
    {
        var tokens = new List<string>();
        var buf = new System.Text.StringBuilder();

        void Flush()
        {
            if (buf.Length > 0)
            {
                tokens.Add(buf.ToString());
                buf.Clear();
            }
        }

        var i = 0;
        while (i < path.Length)
        {
            var c = path[i];
            if (c == '.')
            {
                Flush();
                i++;
            }
            else if (c == '[')
            {
                Flush();
                var end = path.IndexOf(']', i);
                if (end < 0)
                    throw new ArgumentException($"unterminated [ in path: {path}");
                var inner = path.Substring(i + 1, end - i - 1);
                if (inner.Length >= 2 &&
                    ((inner[0] == '\'' && inner[^1] == '\'') ||
                     (inner[0] == '"'  && inner[^1] == '"')))
                {
                    inner = inner.Substring(1, inner.Length - 2);
                }
                tokens.Add(inner);
                i = end + 1;
            }
            else
            {
                buf.Append(c);
                i++;
            }
        }
        Flush();
        return tokens;
    }

    private static bool IsInteger(string s)
    {
        if (s.Length == 0) return false;
        var i = 0;
        if (s[0] == '-') i++;
        if (i == s.Length) return false;
        for (; i < s.Length; i++)
            if (!char.IsDigit(s[i])) return false;
        return true;
    }
}
