namespace RuleForge.Core.Models;

/// <summary>
/// Iterator node config. <c>Source</c> is a JSONPath that resolves (against
/// the current request + iteration frames) to an array. The downstream
/// sub-graph runs once per element with a new frame named <c>As</c> pushed
/// onto the runner's stack.
/// </summary>
public sealed record IteratorConfig(
    string Source,
    string As);
