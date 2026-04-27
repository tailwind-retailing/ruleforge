namespace RuleForge.Core.Models;

/// <summary>
/// Calc node config. Evaluates an arithmetic / boolean expression against
/// a flat variable namespace built from (in override order, highest-wins):
///
///   - the upstream node's output object's top-level fields
///   - the run's execution context entries
///   - the request's top-level fields
///
/// When <c>Target</c> is set, the result replaces that field on the upstream
/// object and the modified object is emitted. When <c>Target</c> is null the
/// raw computed value is emitted as the node's output.
/// </summary>
public sealed record CalcConfig(
    string Expression,
    string? Target = null);
