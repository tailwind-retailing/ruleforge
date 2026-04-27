using RuleForge.Core.Evaluators;
using RuleForge.Core.Models;
using Xunit;

namespace RuleForge.Core.Tests;

public class LogicEvaluatorTests
{
    private static Verdict[] Vs(params Verdict[] vs) => vs;

    [Fact]
    public void And_pass_when_all_pass_else_fail()
    {
        Assert.Equal(Verdict.Pass, LogicEvaluator.And(Vs(Verdict.Pass, Verdict.Pass)));
        Assert.Equal(Verdict.Fail, LogicEvaluator.And(Vs(Verdict.Pass, Verdict.Fail)));
        Assert.Equal(Verdict.Fail, LogicEvaluator.And(Vs(Verdict.Pass, Verdict.Skip)));
    }

    [Fact]
    public void Or_pass_if_any_pass()
    {
        Assert.Equal(Verdict.Pass, LogicEvaluator.Or(Vs(Verdict.Fail, Verdict.Pass)));
        Assert.Equal(Verdict.Fail, LogicEvaluator.Or(Vs(Verdict.Fail, Verdict.Skip)));
    }

    [Fact]
    public void Xor_pass_only_when_exactly_one_pass()
    {
        Assert.Equal(Verdict.Pass, LogicEvaluator.Xor(Vs(Verdict.Pass, Verdict.Fail)));
        Assert.Equal(Verdict.Fail, LogicEvaluator.Xor(Vs(Verdict.Pass, Verdict.Pass)));
        Assert.Equal(Verdict.Fail, LogicEvaluator.Xor(Vs(Verdict.Fail, Verdict.Fail)));
    }

    [Fact]
    public void Not_inverts_single_input()
    {
        Assert.Equal(Verdict.Fail, LogicEvaluator.Not(Vs(Verdict.Pass)));
        Assert.Equal(Verdict.Pass, LogicEvaluator.Not(Vs(Verdict.Fail)));
        Assert.Equal(Verdict.Pass, LogicEvaluator.Not(Vs(Verdict.Skip)));
        Assert.Equal(Verdict.Error, LogicEvaluator.Not(Vs(Verdict.Error)));
    }

    [Fact]
    public void Not_with_zero_or_two_inputs_throws()
    {
        Assert.Throws<InvalidOperationException>(() => LogicEvaluator.Not(Vs()));
        Assert.Throws<InvalidOperationException>(() => LogicEvaluator.Not(Vs(Verdict.Pass, Verdict.Pass)));
    }

    [Theory]
    [InlineData(LogicEvaluator.Op.And)]
    [InlineData(LogicEvaluator.Op.Or)]
    [InlineData(LogicEvaluator.Op.Xor)]
    public void Error_input_propagates_to_error(LogicEvaluator.Op op)
    {
        Assert.Equal(Verdict.Error, LogicEvaluator.Apply(op, Vs(Verdict.Pass, Verdict.Error)));
    }

    [Theory]
    [InlineData("sys-and",  null,    LogicEvaluator.Op.And)]
    [InlineData("sys-or",   "OR",    LogicEvaluator.Op.Or)]
    [InlineData(null,       "XOR",   LogicEvaluator.Op.Xor)]
    [InlineData("sys-not",  null,    LogicEvaluator.Op.Not)]
    [InlineData(null,       "AND",   LogicEvaluator.Op.And)]
    public void Parse_picks_op_from_template_or_label(string? template, string? label, LogicEvaluator.Op expected)
    {
        Assert.Equal(expected, LogicEvaluator.Parse(template, label));
    }
}
