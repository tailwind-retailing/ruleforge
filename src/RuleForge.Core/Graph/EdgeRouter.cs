using RuleForge.Core.Models;

namespace RuleForge.Core.Graph;

public static class EdgeRouter
{
    /// <summary>
    /// Pass â†’ branch=pass or default. Fail â†’ branch=fail or default.
    /// Skip â†’ branch=default only. Error â†’ never matches (caller halts).
    /// Unbranched edges (branch=null) treated as default.
    /// </summary>
    public static bool Matches(Verdict v, EdgeBranch? edgeBranch)
    {
        var b = edgeBranch ?? EdgeBranch.Default;
        return v switch
        {
            Verdict.Pass  => b == EdgeBranch.Pass || b == EdgeBranch.Default,
            Verdict.Fail  => b == EdgeBranch.Fail || b == EdgeBranch.Default,
            Verdict.Skip  => b == EdgeBranch.Default,
            _             => false, // error halts
        };
    }
}
