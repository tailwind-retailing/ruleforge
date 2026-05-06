using System.Text.Json;

namespace RuleForge.Core.Models;

/// <summary>
/// Configuration for a <c>constant</c> node — emits a literal JSON value
/// (number, string, boolean, object, or array). Define-once-use-many: e.g.
/// a tax rate <c>0.15</c> defined in one place and referenced from several
/// calc nodes.
/// <para>
/// The runtime (<c>RuleRunner.cs</c> case <see cref="Models.NodeCategory.Constant"/>)
/// reads the <c>value</c> field directly without deserializing into this
/// record; this type exists as the canonical schema contract for the
/// <c>schemas</c> CLI verb and editor type generation.
/// </para>
/// </summary>
public sealed record ConstantConfig(JsonElement? Value);
