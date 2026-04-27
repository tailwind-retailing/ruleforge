using RuleForge.Core.Graph;
using RuleForge.Core.Models;
using Xunit;

namespace RuleForge.Core.Tests;

public class EdgeRouterTests
{
    [Theory]
    [InlineData(Verdict.Pass, EdgeBranch.Pass, true)]
    [InlineData(Verdict.Pass, EdgeBranch.Fail, false)]
    [InlineData(Verdict.Pass, EdgeBranch.Default, true)]
    [InlineData(Verdict.Pass, null, true)] // null treated as default
    [InlineData(Verdict.Fail, EdgeBranch.Pass, false)]
    [InlineData(Verdict.Fail, EdgeBranch.Fail, true)]
    [InlineData(Verdict.Fail, EdgeBranch.Default, true)]
    [InlineData(Verdict.Skip, EdgeBranch.Pass, false)]
    [InlineData(Verdict.Skip, EdgeBranch.Fail, false)]
    [InlineData(Verdict.Skip, EdgeBranch.Default, true)]
    [InlineData(Verdict.Error, EdgeBranch.Pass, false)]
    [InlineData(Verdict.Error, EdgeBranch.Default, false)]
    public void Routing_table(Verdict v, EdgeBranch? b, bool expected)
    {
        Assert.Equal(expected, EdgeRouter.Matches(v, b));
    }
}
