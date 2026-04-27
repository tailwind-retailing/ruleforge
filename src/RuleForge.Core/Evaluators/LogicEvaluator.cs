using RuleForge.Core.Models;

namespace RuleForge.Core.Evaluators;

/// <summary>
/// Combines incoming verdicts into the logic node's emitted verdict.
///
/// Convention across all four operators:
///   - any input = error â†’ emit error (engine halts)
///   - empty input set   â†’ fail (no upstream activated us)
///   - skip is treated as "not pass"
///
/// AND emits pass iff every input is pass.
/// OR  emits pass iff at least one input is pass.
/// XOR emits pass iff exactly one input is pass.
/// NOT requires exactly one input and inverts: passâ†’fail, failâ†’pass,
///     skipâ†’pass (treats skip as "not pass"), errorâ†’error.
/// </summary>
public static class LogicEvaluator
{
    public enum Op { And, Or, Xor, Not }

    public static Verdict Apply(Op op, IReadOnlyCollection<Verdict> inputs) => op switch
    {
        Op.And => And(inputs),
        Op.Or  => Or(inputs),
        Op.Xor => Xor(inputs),
        Op.Not => Not(inputs),
        _ => Verdict.Error,
    };

    public static Verdict And(IReadOnlyCollection<Verdict> inputs)
    {
        if (inputs.Count == 0) return Verdict.Fail;
        if (inputs.Any(v => v == Verdict.Error)) return Verdict.Error;
        return inputs.All(v => v == Verdict.Pass) ? Verdict.Pass : Verdict.Fail;
    }

    public static Verdict Or(IReadOnlyCollection<Verdict> inputs)
    {
        if (inputs.Count == 0) return Verdict.Fail;
        if (inputs.Any(v => v == Verdict.Error)) return Verdict.Error;
        return inputs.Any(v => v == Verdict.Pass) ? Verdict.Pass : Verdict.Fail;
    }

    public static Verdict Xor(IReadOnlyCollection<Verdict> inputs)
    {
        if (inputs.Count == 0) return Verdict.Fail;
        if (inputs.Any(v => v == Verdict.Error)) return Verdict.Error;
        return inputs.Count(v => v == Verdict.Pass) == 1 ? Verdict.Pass : Verdict.Fail;
    }

    public static Verdict Not(IReadOnlyCollection<Verdict> inputs)
    {
        if (inputs.Count != 1)
            throw new InvalidOperationException(
                $"NOT logic node requires exactly one input, got {inputs.Count}");
        var v = inputs.First();
        return v switch
        {
            Verdict.Pass  => Verdict.Fail,
            Verdict.Fail  => Verdict.Pass,
            Verdict.Skip  => Verdict.Pass, // skip = not-pass, so NOT(skip) = pass
            Verdict.Error => Verdict.Error,
            _ => Verdict.Error,
        };
    }

    public static Op Parse(string? templateId, string? label)
    {
        var t = (templateId ?? string.Empty).ToLowerInvariant();
        var l = (label ?? string.Empty).ToLowerInvariant();
        if (t.Contains("xor") || l == "xor") return Op.Xor;
        if (t.Contains("not") || l == "not") return Op.Not;
        if (t.Contains("or")  || l == "or")  return Op.Or;
        if (t.Contains("and") || l == "and") return Op.And;
        throw new InvalidOperationException(
            $"unknown logic op: templateId='{templateId}' label='{label}'");
    }
}
