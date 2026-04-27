namespace RuleForge.Core.Models;

/// <summary>
/// Filter / logic node verdict. Maps 1:1 to TS FilterVerdict.
/// Edge routing rule: <c>pass</c> matches edges with branch=pass or default;
/// <c>fail</c> matches branch=fail or default; <c>skip</c> matches default only;
/// <c>error</c> halts evaluation.
/// </summary>
public enum Verdict { Pass, Fail, Skip, Error }
