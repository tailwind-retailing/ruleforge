namespace RuleForge.Core.Models;

/// <summary>
/// Configuration for a <c>groupBy</c> node — partitions the upstream array
/// by a key and emits a map <c>{ key1: [items...], key2: [items...] }</c>.
/// <para>
/// v1 is the pure variant (emit grouped map). The "group-by-then-apply"
/// pattern (route each group through a downstream sub-graph and merge)
/// can be composed today via <c>groupBy → iterator → sub-rule → merge</c>.
/// A dedicated apply variant may follow if the composition turns out to
/// be too clunky in practice.
/// </para>
/// </summary>
public sealed record GroupByConfig(string GroupKey);
