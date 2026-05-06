namespace RuleForge.Core.Models;

/// <summary>
/// Configuration for an <c>assert</c> node — fails the rule with a structured
/// error if <c>Condition</c> evaluates falsy. The condition is an NCalc
/// expression with the same variable namespace as the <c>calc</c> node:
/// upstream object fields shadow ctx keys shadow request fields, plus
/// iteration frames.
/// <para>
/// On success, the node passes the upstream value through unchanged so
/// downstream consumers see the original data. On failure, the runner's
/// catch records <c>{ErrorCode}: {ErrorMessage}</c> in the trace and the
/// envelope decision becomes <see cref="Decision.Error"/>.
/// </para>
/// <para>
/// Use this instead of faking an assertion with a filter — a dedicated
/// node makes intent + the structured error envelope clean.
/// </para>
/// </summary>
public sealed record AssertConfig(
    string Condition,
    string? ErrorCode = null,
    string? ErrorMessage = null);
