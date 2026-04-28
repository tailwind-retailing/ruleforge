using RuleForge.DocumentForge;
using Xunit;

namespace RuleForge.Core.Tests;

/// <summary>
/// Coverage for the collection-prefix feature. We don't need a live DF
/// to verify the prefix gets applied — the sources expose their effective
/// prefix as a property and we can confirm construction is well-formed.
/// </summary>
public class PrefixTests
{
    [Fact]
    public void RuleSource_default_prefix_is_empty()
    {
        var df = new DfClient(new System.Net.Http.HttpClient(), "http://localhost", "x");
        var src = new DocumentForgeRuleSource(df, "staging");
        Assert.Equal(string.Empty, src.CollectionPrefix);
    }

    [Theory]
    [InlineData("aerotoys.tax.")]
    [InlineData("tax_")]
    [InlineData("offer-")]
    public void RuleSource_records_explicit_prefix(string prefix)
    {
        var df = new DfClient(new System.Net.Http.HttpClient(), "http://localhost", "x");
        var src = new DocumentForgeRuleSource(df, "staging", prefix);
        Assert.Equal(prefix, src.CollectionPrefix);
    }

    [Fact]
    public void RefSource_default_prefix_is_empty()
    {
        var df = new DfClient(new System.Net.Http.HttpClient(), "http://localhost", "x");
        var src = new DocumentForgeReferenceSetSource(df);
        Assert.Equal(string.Empty, src.CollectionPrefix);
    }

    [Fact]
    public void RefSource_records_explicit_prefix()
    {
        var df = new DfClient(new System.Net.Http.HttpClient(), "http://localhost", "x");
        var src = new DocumentForgeReferenceSetSource(df, "aerotoys.tax.");
        Assert.Equal("aerotoys.tax.", src.CollectionPrefix);
    }

    [Fact]
    public void Null_prefix_normalises_to_empty()
    {
        var df = new DfClient(new System.Net.Http.HttpClient(), "http://localhost", "x");
        var rules = new DocumentForgeRuleSource(df, "staging", null);
        var refs = new DocumentForgeReferenceSetSource(df, null);
        Assert.Equal(string.Empty, rules.CollectionPrefix);
        Assert.Equal(string.Empty, refs.CollectionPrefix);
    }
}
