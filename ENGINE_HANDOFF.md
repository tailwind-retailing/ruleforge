# RuleForge Engine — Catch-up Brief

**Hand-off from the editor track to the engine track.** The editor (`/ruleforge-editor/`) and the engine repo are separate concerns; this brief lets you ship engine-strengthening features in parallel without colliding with editor work.

---

## TL;DR

We're building a visual rules engine for high-volume request-pricing / validation flows (target use case: airline offer pricing — taxes, ancillaries, fares — where a single endpoint may fan out to ~1,500 small rules). The **authoring experience** is good (visual DAG, decoupled node-library + bindings model, see "Conceptual model" below). The **engine** is behind enterprise rule engines on three things: **performance at scale, governance/explainability, and runtime safety**. Your job: close those gaps.

**Hard constraint: do not touch the editor.** Editor lives at `C:\DATA\14. ruleForge\ruleforge-editor\` and is mid-refactor. If you need a feature surfaced in the UI, write the engine API and document it; the editor track will wire the UI later.

---

## Conceptual model (what we're building on)

A RuleForge rule is a **DAG** of node-instances. Two axes are decoupled:

1. **Node library** (`/nodes/*.json`) — global, reusable building blocks. Each captures a *business intention* ("filter by string", "translate code to name via reference table", "iterate passengers"). A node declares its **ports** (inputs / params / outputs) and optional engine defaults. It does NOT know about any specific rule's schema.

2. **Per-rule bindings** (`/rules/[id]/bindings/[instanceId].json`) — wire each port to an actual JSONPath, literal, reference table, or context key in *this* rule's schema. The same node-id can be reused across hundreds of rules with totally different shapes.

Disk layout:
```
workspace/
  nodes/                        # global library
    node-iterator.json
    node-filter-string-in.json
    node-mutator-lookup.json
    ...
  rules/[id]/
    rule.json                   # DAG: instances (refs to nodes by id) + edges
    schema/{input,output,context}.json
    bindings/[instanceId].json
    tests/*.json
  refs/                         # global lookup tables (ref-airports.json etc.)
  workspace.json
```

In-memory `Rule` shape (`ruleforge-editor/src/lib/types/rule.ts`):
```ts
type Rule = {
  id, name, endpoint, method, status, currentVersion, ...
  inputSchema, outputSchema, contextSchema   // JSON Schema
  instances: RuleNodeInstance[]              // { instanceId, nodeId, position, label? }
  edges: RuleEdge[]                          // { id, source, target, branch }
  bindings: Record<instanceId, NodeBindings> // each NodeBindings = { instanceId, ruleId, bindings: Record<port, PortBinding>, extras? }
  tests: RuleTest[]
}

type PortBinding =
  | { kind: "path",     path: string }       // JSONPath into rule's input/context
  | { kind: "literal",  value: unknown }
  | { kind: "reference", referenceId: string }
  | { kind: "context",  key: string }        // iteration frame ($pax.foo) or $ctx.bar
```

Node categories: `input | output | iterator | merge | filter | mutator | calc | constant | ruleRef | logic | product | reference | sql | api`.
Edge branches: `pass | fail | default`.

JSONPath subset supported (today): `$`, `.key`, `[N]`, `[*]`, plus context frames `$ctx.foo`, `$pax.x` (for the open iteration frame).

Two seed rules to study:
- `rules/offer-tax/` — per-pax tax lookup against `ref-offer-tax-rates`. Linear chain with iterator + merge.
- `rules/pax-validation/` — paxType vs DOB sanity check. Branchy: 3 parallel paxType filters, each gating a number-range filter for the expected age band, then merged.

---

## Where we're behind (your scope)

### 1. Performance at scale — shared-condition dedupe (NOT full RETE)
Drools / Easy Rules use RETE — alpha/beta networks, working memory, agenda — to win when 10k facts match 10k rules. **We do not need that.** Our shape is "one endpoint → one rule graph → maybe sub-rule fan-out", which is a pipeline, not a working-memory match problem. Building RETE here would be ~20× the complexity of the actual win.

What we DO need is **common-subexpression elimination across sub-rules sharing an endpoint**. If 800 of the 1,500 sub-rules behind `/v1/offer/price` all start with `country == "GB"`, evaluate that filter once. ~5% of RETE's complexity, biggest perf win on the 1,500-rules pattern.

**What to ship:**
- Benchmark harness FIRST (synthesize 1,500 small rules sharing 50 distinct conditions; measure cold/warm latency vs naive eval). Without this, we can't tell whether the cache or the dedupe pass actually wins. Becomes the perf-regression CI gate.
- Memo cache keyed on `(instanceId, request-fingerprint)` for `node-mutator-lookup` results — reference-table lookups are pure functions of their inputs. Bound the cache size; reference-set row count guard goes here too (fail closed if a ref set is >100k rows rather than OOM the pod).
- Rule-set compiler that detects shared filter conditions across sub-rules (same node-id + same path binding + same literal) and dedupes evaluation.

### 2. Explainability — "why did this rule branch this way?" (paired with redaction)
The editor already produces traces (`traversedEdges`, `nodeOutcomes`). The engine needs richer trace output: for each filter node, capture the actual resolved values on both sides of the comparison, plus the operator. The editor will surface this; you ship the data shape.

**Critical: ship redaction WITH enrichment, not after.** Airline workloads put names, PNRs, payment tokens, and frequent-flyer numbers into the upstream object. Returning enriched traces to callers without redaction is a DPO/compliance landmine — and once the trace shape ships unredacted, fixing it is a breaking API change. Today the trace already returns `e.Message` from `RuleRunner.cs:135` raw, leaking parse positions and internal node ids; that goes too.

**What to ship:**
- Extend trace schema: `nodeOutcomes[instanceId] = { outcome: pass|fail|skip|error, evaluatedSource?: any, evaluatedLiteral?: any, operator?: string, error?: string, durationMs: number }`.
- Redaction contract: a `sensitive: true` flag on `inputSchema` field properties (or a JSON-pointer list at rule level — pick one and document). Trace post-processor masks tagged paths at emit time as `"***"`. One pass, one test.
- Production traces return error codes + a stable error id; full `e.Message` and stack traces go to server-side logs only.
- Trace serialization format (newline-delimited JSON?) suitable for streaming back to the editor over SSE for long-running rules.
- Endpoint: `POST /v1/rules/[id]/evaluate?explain=true` returns the result + full (already-redacted) trace.

**Note for editor team:** the redaction tag is engine-owned schema. The editor will need to surface a "Sensitive (mask in trace)" toggle on schema field UI later — see "Schema contract handoff" below.

### 3. Governance — draft / review / published flow
The Rule has `status: "draft" | "review" | "published"` but no actual gates. Anyone can publish anything.

**What to ship:**
- Audit log: who changed what, when (write-only journal in `workspace/_audit/*.jsonl`).
- Approval workflow: a "review" rule cannot serve traffic; promotion to "published" requires an `approverId` distinct from the last `updatedBy`.
- Diff API: `GET /v1/rules/[id]/diff?from=v3&to=v4` returns a structured diff (added/removed/changed instances + binding changes).
- Rollback: `POST /v1/rules/[id]/rollback` takes the rule back to a previous version.

### 4. Runtime safety

**Schema gates** (block bad input from reaching the evaluator):
- **Schema validation gate** at request entry — reject the request before evaluation if it doesn't conform to the rule's `inputSchema`. Return structured 400 with the first violation path.
- **Output validation gate** — verify the assembled response against `outputSchema`; downgrade to warning + log if mismatch (don't 500 on minor type drift).
- **Request body size cap + `JsonReaderOptions.MaxDepth`** at `Program.cs:170`. Today a 30MB POST silently buffers in memory and deeply-nested JSON parses to `{}` silently (which is wrong — should be 400).

**Author error guards** (block accidentally-broken rules from taking down the engine — airline staff author rules, mistakes are inevitable):
- **Sub-rule recursion depth limit + inter-rule cycle detection** in `RuleRunner.cs:InvokeSubRuleAsync` (~line 906). `HasCycle()` at `RuleRunner.cs:1267-1280` runs intra-rule only today. Rule A → forEach → sub-rule B → sub-rule A is undetected. Stack-overflows the engine on the sub-millisecond hot path.
- **NCalc cancellation timeout** at `CalcEvaluator.cs:43`. `expr.Evaluate()` ignores the request's `CancellationToken`. `pow(pow(pow(...)))` spins. Wrap in `Task.Run` with a deadline (~10 lines). The `EvaluateFunction` whitelist hook should be wired here too — even an empty whitelist documents intent and survives future NCalc upgrades that might broaden built-ins.
- **Circuit breaker** per ruleRef call (sub-rule fan-out should fail-fast if a downstream sub-rule starts erroring or exceeding latency budget).
- **Timeouts** per-node (especially `node-mutator-lookup` against external references — currently embedded JSON, but eventually network).

**Hardening:**
- **Auth timing fix** at `ApiKeyMiddleware.cs:55`. The `FixedTimeEquals` itself is genuinely constant-time (verified at line 78-85) but `TryReadKey` short-circuits before reaching it; run the comparison against a dummy key when no header is present. Small leak, cheap fix.
- **Trace error redaction** — see section 2 above; the `e.Message` returned from `RuleRunner.cs:135` is in scope here too.

### 5. Shadow / A/B mode
For risky rule changes, run the new draft version in shadow alongside published — log the diff in outputs but serve the published version. After N hours of clean shadow, allow promotion.

**What to ship:**
- `POST /v1/rules/[id]/shadow-eval` — evaluates both `published` and `draft` versions, returns the published result + a diff payload.
- Diff metric: % of requests where the two versions disagree, broken down by which fields differ.

### 6. Not-yet-needed (nice to have, push later)
- DSL escape hatch — sometimes business users want a one-liner inline expression that doesn't fit nodes. Drools/DMN allow this; we don't yet. Defer until users actually ask. (We see this as a positioning trap — slippery slope to half the rules being in DSL and the visual graph becoming decoration. The calc node already covers most "one-liner" needs.)
- Decision-table coverage analysis (DMN-style gap detection). Defer until we have enough rules to need it.
- Parallel evaluation across pax / bounds (our iterator is sequential). Profile first.
- **New node templates and unimplemented categories.** The `NodeCategory` enum at `Models/RuleNode.cs` declares 14 categories. The switch at `RuleRunner.cs:308` implements 12 of them; **only `api` and `sql` actually fall through to `NotSupportedException` at line 348.** Separate concern: `product` is implemented but its name is misleading — the implementation is output-shape assembly, not Cartesian product (see GitHub issue #5). If we want a `call endpoint` node (under the existing `api` category), engine ships the evaluator first, editor curates the template after. Out of scope for THIS pass — see "Schema contract handoff" for the flow.

---

## What NOT to touch

- `/ruleforge-editor/src/**` — the editor. Pure consumer of your engine APIs. The editor is mid-refactor and any cross-cutting changes (e.g., new schema fields the editor needs to surface) flow through "Schema contract handoff" below — never edit editor types directly.
- `/test-workspace/nodes/*.json`, `/test-workspace/rules/[id]/**` — these are seed examples curated by the editor track. Don't refactor them.

## What you DO own (clarification — schema control sits with the engine)

- **The canonical schema** for rules, nodes, bindings, and traces. The engine publishes the schema (already wired via the `schemas` CLI verb in `README.md:98`); the editor consumes it and generates its TS types from that output. **New schema additions flow engine → editor, not the other way around.** The conceptual model — nodes / bindings / DAG / branches — is locked at the *category* level, but adding fields within those categories is fair game (with a handoff note to the editor team).
- **All evaluators for declared node categories**: `input | output | iterator | merge | filter | mutator | calc | constant | ruleRef | logic | product | reference | sql | api`. Today only `api` and `sql` are declared but unimplemented — the switch at `RuleRunner.cs:308` falls through to `NotSupportedException` at line 348 for those two. Shipping those evaluators is engine-track work. The editor track curates *templates* within those categories.

---

## Schema contract handoff (engine ↔ editor)

The engine owns the canonical schema. The editor consumes it. New schema additions ship as:

1. **Engine ships:** schema spec change (the `schemas` CLI verb output reflects it) + runtime support + a one-page note on how the editor should surface it (UI suggestion only — editor team decides actual UX).
2. **Editor wires:** type generation from engine's JSON schema, UI for editing the new field, end-to-end test through to evaluation.

**Open items the editor team needs to know about (for THIS engine pass):**

- **Trace shape change** (item #2). Editor's existing trace UI needs to consume the enriched `nodeOutcomes` shape. New fields are additive (no breaks), but the explainability UI is gated on the new fields landing.
- **`sensitive` field tag on inputSchema properties** (item #2 redaction). Editor needs a "Sensitive (mask in trace)" toggle on schema field properties when authoring `inputSchema`. Engine handles the masking at trace-emit time — editor only stores the bit.
- **Audit + diff + rollback API** (item #3). New endpoints; editor UI for change history, diff view, rollback button is downstream work.
- **Shadow mode** (item #7). New endpoint; editor UI for "promote draft to shadow → observe → promote to published" is downstream work.

**Future (out of scope for this pass) but worth flagging:**

- **New node templates** (e.g., `call endpoint` for outbound HTTP, under the existing `api` category). Engine ships the evaluator + binding shape; editor curates a template under `test-workspace/nodes/`. Discussed but not scoped here — raise it as a separate brief.

---

## Suggested order

**Tier 0 — house cleaning (½ day, do first):**

0. **Fix README test-count drift.** `README.md:60` says "126/126", `README.md:113` says "138", actual count is 151. Add a CI check that compares the README claim to the test count and fails on drift. Tiny, but protects against further doc/code rot.

**Tier 1 — safety bundle (1 day, blocks everything else):**

1. **Schema validation gate + safety bundle** (1 day, was ½). Bundles input/output schema gates, request size + JSON depth caps, sub-rule depth limit, inter-rule cycle detection, NCalc cancellation timeout + empty whitelist hook, auth timing fix, and trace error message redaction. See section 4 for file:line targets.

**Tier 2 — explainability + governance foundations (3-4 days):**

2. **Benchmark harness** (½ day, split out of old #4). Ship before any perf-touching work so #5 and #7 are measurable. Becomes the perf-regression CI gate.
3. **Trace enrichment + redaction model together** (1 day, was ½). The redaction tag MUST land with the enriched shape, not after — see section 2.
4. **Audit log + diff + rollback API** (1-2 days, unchanged).

**Tier 3 — perf wins (4-6 days, now measurable against the harness from #2):**

5. **Memo cache for ref lookups + ref-set size guard** (1 day).
6. **Circuit breaker + per-node timeouts** (1 day). The inter-rule cycle detection from Tier 1 lives in spirit here too.
7. **Shared-condition dedupe across sub-rule fan-out** (3-5 days, was old #6, renamed). Biggest perf win on the 1,500-rules pattern. NOT full RETE — see section 1.

**Tier 4 — risky-change safety (2 days):**

8. **Shadow mode** (2 days, unchanged).

Total: ~11-13 days for one engineer. Each item lands as a self-contained PR with tests + a short doc. Editor track will wire UI for explainability + audit + diff + shadow over the following week — see "Schema contract handoff" for what they need.

---

## Concrete files to read first

**Type model (read-only — engine should mirror, not edit):**
- `ruleforge-editor/src/lib/types/rule.ts` — `Rule`, `RuleEdge`, `RuleSummary`, `RuleTest`
- `ruleforge-editor/src/lib/types/node-def.ts` — `NodeDef`, `RuleNodeInstance`, `PortBinding`, `NodeBindings`
- `ruleforge-editor/src/lib/server/workspace.ts` — `readRule`, `writeRule`, `listNodeDefs` — this is the persistence contract
- `test-workspace/rules/offer-tax/rule.json` + `bindings/n5.json` — concrete example of the model in action (lookup against reference table)
- `test-workspace/nodes/node-mutator-lookup.json` — the airport-code-to-name pattern, the canonical "reusable business intention" demo

**Engine code touched by Tier 1 (read these before writing the safety bundle):**
- `ruleforge/src/RuleForge.Api/ApiKeyMiddleware.cs` — auth handler. The `TryReadKey`/`FixedTimeEquals` interaction is the timing-leak surface. `FixedTimeEquals` itself is sound; the leak is in early-returns before reaching it.
- `ruleforge/src/RuleForge.Api/Program.cs:170` — JSON parsing entry point. Size cap + `JsonReaderOptions.MaxDepth` + 400-on-malformed go here.
- `ruleforge/src/RuleForge.Core/Graph/RuleRunner.cs:906-968` — `InvokeSubRuleAsync`. Depth limit + inter-rule cycle detection go here.
- `ruleforge/src/RuleForge.Core/Graph/RuleRunner.cs:135` — trace `e.Message` leak point.
- `ruleforge/src/RuleForge.Core/Graph/RuleRunner.cs:1267-1280` — current `HasCycle` (intra-rule only); reference for the inter-rule version.
- `ruleforge/src/RuleForge.Core/Graph/RuleRunner.cs:308` — the category switch. Only `api` and `sql` fall through to `NotSupportedException` at line 348 today; the other 12 categories are implemented inline.
- `ruleforge/src/RuleForge.Core/Evaluators/CalcEvaluator.cs:43` — NCalc evaluation. Cancellation timeout wraps this call; `expr.EvaluateFunction` whitelist hook also lives at line 32.

Questions: ping the editor track for type/storage questions, but the engine track owns the schema — see "Schema contract handoff". Don't guess at types or storage layout.
