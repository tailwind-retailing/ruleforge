using System.Text.Json;
using RuleForge.Core.Models;

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
    public static IReadOnlyList<JsonElement?> Resolve(JsonElement? root, string path) =>
        Resolve(root, path, frames: null);

    /// <summary>
    /// Resolve <paramref name="path"/> against <paramref name="root"/>, with
    /// optional <paramref name="frames"/> from the runner's iteration stack.
    /// When a path begins with <c>$&lt;name&gt;</c> matching a frame's name
    /// (or <c>$&lt;name&gt;Index</c> / <c>$&lt;name&gt;Count</c>), the resolver
    /// switches root to that frame and traverses the rest of the path against it.
    /// </summary>
    public static IReadOnlyList<JsonElement?> Resolve(
        JsonElement? root,
        string path,
        IReadOnlyList<IterationFrame>? frames)
    {
        // First — see if the path's leading variable matches an iteration frame.
        // Frames are scanned innermost-first so closer scopes shadow outer ones.
        if (frames is { Count: > 0 } && TrySwitchRootToFrame(path, frames, out var newRoot, out var rest))
        {
            root = newRoot;
            path = rest;
        }

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

    /// <summary>
    /// If <paramref name="path"/> opens with <c>$&lt;frame-name&gt;</c> (optionally
    /// followed by <c>Index</c> or <c>Count</c>), switch root to that frame's
    /// item / index / count value and return the remaining path. Returns false
    /// if no frame matched — the caller falls back to root resolution.
    /// </summary>
    private static bool TrySwitchRootToFrame(
        string path,
        IReadOnlyList<IterationFrame> frames,
        out JsonElement? newRoot,
        out string rest)
    {
        newRoot = null;
        rest = path;
        if (!path.StartsWith('$') || path.Length < 2 || path[1] is '.' or '[' || path == "$") return false;

        // Find the longest run of identifier chars after the leading $.
        var i = 1;
        while (i < path.Length && (char.IsLetterOrDigit(path[i]) || path[i] == '_')) i++;
        var name = path.Substring(1, i - 1);
        var tail = path[i..];

        // Innermost-first: a name shadows outer frames.
        for (var f = frames.Count - 1; f >= 0; f--)
        {
            var frame = frames[f];

            // Direct match: $<name> → frame.Item
            if (string.Equals(name, frame.Name, StringComparison.Ordinal))
            {
                newRoot = frame.Item;
                rest = tail.Length == 0 ? "$" : "$" + tail;
                return true;
            }

            // $<name>Index → integer
            if (string.Equals(name, frame.Name + "Index", StringComparison.Ordinal))
            {
                newRoot = JsonDocument.Parse(frame.Index.ToString()).RootElement;
                rest = tail.Length == 0 ? "$" : "$" + tail;
                return true;
            }

            // $<name>Count → integer
            if (string.Equals(name, frame.Name + "Count", StringComparison.Ordinal))
            {
                newRoot = JsonDocument.Parse(frame.Count.ToString()).RootElement;
                rest = tail.Length == 0 ? "$" : "$" + tail;
                return true;
            }
        }
        return false;
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
