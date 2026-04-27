using RuleForge.Core.Models;

namespace RuleForge.Core.Loader;

public interface IRuleSource
{
    /// <summary>Resolve the rule pinned to (endpoint, method) for the active environment.</summary>
    Task<Rule?> GetByEndpointAsync(string endpoint, HttpMethodKind method, CancellationToken ct = default);

    /// <summary>
    /// Resolve a rule by id at a specific version. Pass <c>null</c> for the
    /// version to mean "latest published" (engine reads <c>currentVersion</c>
    /// from the rule header). Used for <c>subRuleCall</c> resolution.
    /// </summary>
    Task<Rule?> GetByIdAsync(string ruleId, int? version, CancellationToken ct = default);

    /// <summary>
    /// Enumerate the (endpoint, method, ruleId, version) tuples that the
    /// engine should bind at boot. Sources can use this to drive
    /// <c>app.MapPost(...)</c> for every active binding rather than
    /// hardcoding endpoints.
    /// </summary>
    Task<IReadOnlyList<RuleBinding>> ListBindingsAsync(CancellationToken ct = default);
}

public sealed record RuleBinding(string RuleId, int Version, string Endpoint, HttpMethodKind Method);
