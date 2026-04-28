using System.Text.Json;

namespace RuleForge.Core.Models;

/// <summary>
/// One level of iteration scope. Pushed onto the runner's frame stack when an
/// iterator node fires; popped when the iteration ends. Inside a frame, the
/// JSONPath resolver recognises three new roots — derived from <see cref="Name"/>:
///
///   <c>$&lt;name&gt;</c>       → <see cref="Item"/> (the current element)
///   <c>$&lt;name&gt;Index</c>  → <see cref="Index"/> (0-based)
///   <c>$&lt;name&gt;Count</c>  → <see cref="Count"/> (total iteration count)
///
/// Variables are resolved innermost-first, so nested iterators with distinct
/// <c>as</c> names (e.g. <c>journey</c> + <c>segment</c> + <c>pax</c>) are all
/// simultaneously visible at the deepest scope.
/// </summary>
public sealed record IterationFrame(
    string Name,
    JsonElement Item,
    int Index,
    int Count);
