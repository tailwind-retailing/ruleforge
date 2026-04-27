# AERO Rules Engine — implementation brief

A self-contained brief for a fresh session that's building the **runtime
rules engine** — the .NET service that consumes rules authored by AERO
(this admin app), reads from DocumentForge, evaluates incoming requests
against rule graphs, and returns decisions.

> **Read this once and you have everything you need to start.**
>
> Companion docs:
> - DocumentForge — https://documentforge-docs.onrender.com/index.html
> - AERO admin (this app) — same repo, deployed at the Render URL on
>   `andrewfblake/airline-rules-engine`. The TypeScript evaluators in
>   `src/shared/evaluators/` are the **reference implementations**;
>   reproduce their semantics in C#.

---

## 1. What you're building

A .NET 8 (or 9) HTTP service: **AERO Engine**.

Single responsibility: take an inbound JSON request at a rule-defined
endpoint (e.g. `POST /v1/ancillary/bag-policy`), evaluate the configured
rule graph, return an envelope with the decision + (in debug mode) a
trace.

```
   ┌──────────────────────┐         ┌────────────────────┐
   │ Caller (e.g. PSS,    │  HTTP   │  AERO Engine       │
   │ retailing platform)  ├────────▶│  ASP.NET Core      │
   └──────────────────────┘         │                    │
                                    │  ┌──────────────┐  │   HTTP
                                    │  │  Rule loader ├──┼─────┐
                                    │  └──────────────┘  │     │
                                    │  ┌──────────────┐  │     ▼
                                    │  │  Evaluator   │  │  ┌──────────────┐
                                    │  │  (DAG walker)│  │  │ DocumentForge│
                                    │  └──────────────┘  │  │ (rules,      │
                                    │  ┌──────────────┐  │  │  refs, env   │
                                    │  │ DF client    ├──┼──┤  bindings)   │
                                    │  └──────────────┘  │  └──────────────┘
                                    └────────────────────┘
```

Key design tenets:

- **Open source**, MIT-licensed, sibling to DocumentForge
- **Deployable on Render** as a Docker container
- **No proprietary deps** beyond DocumentForge, ASP.NET Core, JSON.NET / `System.Text.Json`
- **Schema lives in DocumentForge** — engine pulls + caches rules at boot,
  refreshes on env binding changes
- **Stateless requests** — every evaluation is a fresh DAG walk over the
  pinned rule version for the active environment

---

## 2. Where rules live

### Collections in DocumentForge

| Collection | Purpose | Key paths |
|---|---|---|
| `rules` | Latest editable copy of every rule (the "main branch") | `id`, `endpoint`, `currentVersion`, `status` |
| `ruleversions` | **Immutable** snapshots, one per publish | `ruleId`, `version`, `snapshot` (full rule), `publishedAt`, `bundleId` |
| `environments` | dev / staging / prod with `ruleBindings: { ruleId → version }` | `name` |
| `referencesets` + `referencesetversions` | Lookup data + history | `id`, `name` |
| `nodetemplates` | Palette templates (system + custom domain templates) | `id`, `basedOn` |

### What the engine actually reads

For a request hitting `POST /v1/ancillary/bag-policy`:

1. Look up `environments[NAME].ruleBindings` for the rule with `endpoint = /v1/ancillary/bag-policy` and the right `method`. That gives you the **pinned version**.
2. Load `ruleversions/{ruleId}/{version}` — that's the immutable rule snapshot to execute.
3. Resolve any sub-rule calls (`subRuleCall.ruleId` + optional `pinnedVersion`) the same way.
4. Resolve reference sets named in node configs against `referencesetversions`.

Cache aggressively. Versions are immutable — once loaded, never invalidated. Env bindings + the latest `currentVersion` of each rule should be polled (e.g. every 30s) or pushed via a simple `/api/_admin/notify-published` webhook from AERO admin.

### DocumentForge access

Auth: `Authorization: Bearer <api-key>`.

Hot endpoints:
- `GET /collections/{c}/by/{field}/{value}` — single-doc fetch by any field
- `POST /query` with `{sql}` — SQL-ish over JSON, plan in response
- `GET /stats` — health + per-collection counts

See **DocumentForge docs** (link above) for the full surface — they shipped
multi-field ops, pagination, and ORDER BY since the AERO admin was first
written. Use the latest features.

---

## 3. The rule schema

The **canonical** definition lives at
`src/shared/types.ts` in this repo. Reproduce in C# as a 1:1 mirror.
TypeScript snippets below are the source of truth.

### Rule

```ts
type Rule = {
  id: string;
  projectId?: string;
  name: string;
  description: string;
  tags: string[];
  category?: string;
  parentId?: string;
  endpoint: string;          // /v1/ancillary/bag-policy
  method: 'GET' | 'POST';
  status: 'draft' | 'review' | 'published';
  currentVersion: number;     // 0 = never published
  inputSchema: JsonSchema;    // declares the request shape
  outputSchema: JsonSchema;   // envelope + rule-specific result shape
  contextSchema?: JsonSchema; // declares ctx keys the rule reads/writes
  nodes: RuleNode[];          // React Flow nodes — see §4
  edges: RuleEdge[];          // React Flow edges with branch tags — see §4
  updatedAt: string;
  updatedBy?: string;
};
```

### Output envelope (every rule emits this shape)

```jsonc
{
  "ruleId": "rule-bag-policy",
  "ruleVersion": 12,
  "decision": "apply" | "skip" | "error",
  "evaluatedAt": "2026-04-27T08:00:00.000Z",
  "result": { /* rule-specific payload built from the Result node's upstream graph */ },
  "trace": [ /* TraceEntry[]; only emitted when ?debug=true or X-Debug: true */ ]
}
```

### TraceEntry (one per node executed)

```ts
type TraceEntry = {
  nodeId: string;
  startedAt: string;
  durationMs: number;
  outcome: 'pass' | 'fail' | 'skip' | 'error';
  input?: unknown;
  output?: unknown;
  ctxRead?: Record<string, unknown>;
  ctxWritten?: Record<string, unknown>;
  subRuleRunId?: string;       // for sub-rule invocations
  error?: string;
};
```

### Sub-rule invocation (capability on any node)

```ts
type SubRuleCall = {
  ruleId: string;
  inputMapping: Record<string, string>;   // parent → sub-rule input. JSONPath strings.
  outputMapping: Record<string, string>;  // sub-rule.result → parent ctx / object
  onError: 'skip' | 'fail' | 'default';
  defaultValue?: unknown;
  pinnedVersion: number | 'latest';
};
```

A node with `subRuleCall` set invokes the sub-rule **before** running its own logic, with the sub-rule's `result` available to it.

---

## 4. Nodes & edges

### Node categories

`input` · `output` · `logic (and/or/xor/not)` · `filter (string/number/date)` · `product` · `sql` · `api` · `reference` · `ruleRef` · `calc` · `constant` · `mutator`

### Edge branching

Edges carry a `branch` tag — `'pass' | 'fail' | 'default'` — and optional `sourceHandle` / `targetHandle`. Filter / logic nodes emit a verdict; the engine picks edges whose branch matches.

```ts
type RuleEdge = {
  id: string;
  source: string;
  target: string;
  branch?: 'pass' | 'fail' | 'default';
  sourceHandle?: string;
  targetHandle?: string;
  label?: string;
};
```

### Walking the DAG

1. Start at the unique `input` node. Its initial output = the request body.
2. For each outgoing edge, push the target onto a queue.
3. Each node runs once. Inputs come from incoming edges' source-node outputs (multi-input nodes get a `Record<sourceNodeId, output>`).
4. Filter/logic nodes emit a verdict + their normal output. Outgoing edges are followed only if their `branch` matches the verdict (or `branch === 'default'`).
5. The `output` node ("Result") receives all upstream outputs and assembles the final `result`.

Cycle detection: refuse to evaluate a rule that has a cycle (validate at publish, not at run).

---

## 5. Filter primitives — the most important part

Three filter shapes share an identical 5-section structure: **source**, **comparison**, **array reduction**, **on-missing**, plus the verdict the node emits.

The **TypeScript reference implementations** in
`src/shared/evaluators/{string,number,date}-filter.ts` are pure functions
with zero IO — read them, port directly to C#.

### Common shape

```ts
type FilterEvalResult = {
  verdict: 'pass' | 'fail' | 'skip' | 'error';
  resolvedValues: Array<string | null | undefined>;
  perElement?: boolean[];   // per-resolved-value compare result
  reason?: string;
  error?: string;
};

type ArraySelector = 'any' | 'all' | 'none' | 'first' | 'only';
//   any   → at least one element matches
//   all   → every element matches
//   none  → no element matches
//   first → check only [0]
//   only  → exactly one element matches
```

### String filter

```ts
type StringFilterConfig = {
  source: { kind: 'request' | 'context' | 'literal'; path?: string; literal?: string };
  compare: {
    operator: 'equals' | 'not_equals' | 'starts_with' | 'ends_with'
            | 'contains' | 'not_contains' | 'in' | 'not_in'
            | 'regex' | 'is_null' | 'is_empty';
    value?: string;
    values?: string[];
    caseInsensitive?: boolean;
    trim?: boolean;
  };
  arraySelector: ArraySelector;
  onMissing: 'fail' | 'pass' | 'skip';
  referenceId?: string;       // hint for UI; the runtime can ignore
  referenceColumn?: string;
};
```

### Number filter

```ts
type NumberFilterConfig = {
  source: { kind: 'request' | 'context' | 'literal'; path?: string; literal?: number };
  compare: {
    operator: 'equals' | 'not_equals' | 'gt' | 'gte' | 'lt' | 'lte'
            | 'between' | 'not_between' | 'in' | 'not_in' | 'is_null';
    value?: number;
    values?: number[];
    min?: number; max?: number;
    minInclusive?: boolean;     // default true
    maxInclusive?: boolean;     // default true
    round?: 'floor' | 'ceil' | 'round';
  };
  arraySelector: ArraySelector;
  onMissing: 'fail' | 'pass' | 'skip';
};
```

Coercion rules: tolerate string-numeric (`"23"` → 23), boolean (`true` → 1), `null` / `undefined` → "missing".

### Date filter

```ts
type DateFilterConfig = {
  source: { kind: 'request' | 'context' | 'literal'; path?: string; literal?: string };
  compare: {
    operator: 'equals' | 'not_equals' | 'before' | 'after'
            | 'between' | 'not_between' | 'within_last' | 'within_next' | 'is_null';
    value?: string;
    from?: string; to?: string;
    amount?: number;
    unit?: 'minutes' | 'hours' | 'days' | 'weeks' | 'months';
    granularity: 'date' | 'datetime' | 'time';
    timezone?: string;          // IANA, e.g. "Asia/Dubai"
    fromInclusive?: boolean;    // default true
    toInclusive?: boolean;      // default true
  };
  arraySelector: ArraySelector;
  onMissing: 'fail' | 'pass' | 'skip';
};
```

**Granularity behavior** (port carefully):
- `datetime` — compare full instant
- `date` — strip time-of-day; compare `Y-M-D` only
- `time` — strip date; compare `H:M:S` only

**Timezone handling**:
- ISO-8601 strings with offset (`...Z` / `+04:00`) are trusted
- Naive strings (`2026-04-27T14:00:00`) are interpreted in the configured `timezone` (or runtime default if absent)
- `within_last N days` / `within_next N days` resolve `now` in the configured zone

### JSONPath subset

The reference evaluator implements a **tiny JSONPath subset** — `$`, `.key`, `[N]`, `[*]`, plus a `$ctx.` prefix for context paths. **Port exactly the subset**, not a full JSONPath spec. See `jsonpathResolve()` in `src/shared/evaluators/string-filter.ts`.

---

## 6. Domain extension templates ("hide the complexity")

A template like `Loyalty tier (Skywards)` (id `cus-tier`) wraps the raw string-filter with:

- **Pre-bound config**: `source.path: "$.pax[*].tier"`, `compare.operator: "in"`, `referenceId: "ref-skywards-tiers"`, `arraySelector: "any"`, etc.
- **`lockedPaths`**: the dotted-leaf paths the consumer node may NOT edit
- **`paramLabels`**: friendly labels for the leaves left editable

For the **runtime** these are just node configs — the engine doesn't care which leaves the UI exposed. **Templates are an authoring concern, not an evaluation concern**. Engine reads a node's `data.config` and runs it.

Practical implication: **the engine never sees `cus-tier-skywards`** — by the time the rule reaches DocumentForge, the node's `config` already has `compare.operator: "in"` and `compare.values: ["GOLD", "PLAT"]`. Just evaluate the filter.

---

## 7. Execution context

A shared scratch space passed alongside the request through the rule run. Two surfaces:

1. **Runtime**: an object under `ctx.*` keys that nodes read from and write to.
2. **Static**: each node declares `readsContext: string[]` and `writesContext: string[]` — the engine can validate at boot that no node reads a key not written upstream.

Sub-rule calls get an **isolated** context derived from `inputMapping`. They can't read or write the parent's context except through that mapping.

---

## 8. Debug vs production mode

```
POST /v1/ancillary/bag-policy        ← lean response (envelope without trace)
POST /v1/ancillary/bag-policy?debug=true   ← envelope + trace
X-Debug: true header equivalent.
```

Production mode:
- Skip building TraceEntry objects
- Skip per-node timing (or use a single overall timer)
- Skip ctxRead/ctxWritten capture
- Aim for **<5ms** for typical rule graphs (10–20 nodes)

Debug mode:
- Full trace per node with input/output/durationMs
- ctxRead/ctxWritten snapshots
- Expand sub-rule traces inline (link via `subRuleRunId`)
- ~10× the work, that's fine

---

## 9. Suggested project structure

```
aero-engine/
├── src/
│   ├── Aero.Engine.Api/           # ASP.NET Core entrypoint
│   │   ├── Program.cs
│   │   ├── RuleRoutingMiddleware.cs   # maps endpoint+method → ruleId
│   │   ├── DebugFlag.cs
│   │   └── appsettings.json
│   │
│   ├── Aero.Engine.Core/          # the actual engine
│   │   ├── Models/
│   │   │   ├── Rule.cs
│   │   │   ├── RuleNode.cs
│   │   │   ├── RuleEdge.cs
│   │   │   ├── StringFilterConfig.cs
│   │   │   ├── NumberFilterConfig.cs
│   │   │   ├── DateFilterConfig.cs
│   │   │   └── Envelope.cs
│   │   ├── Evaluators/
│   │   │   ├── IFilterEvaluator.cs
│   │   │   ├── StringFilterEvaluator.cs
│   │   │   ├── NumberFilterEvaluator.cs
│   │   │   ├── DateFilterEvaluator.cs
│   │   │   └── JsonPath.cs       # tiny subset
│   │   ├── Graph/
│   │   │   ├── RuleRunner.cs     # DAG walker
│   │   │   ├── ExecutionContext.cs
│   │   │   └── EdgeRouter.cs     # branch tag matching
│   │   └── Loader/
│   │       ├── RuleLoader.cs     # DocumentForge fetch + cache
│   │       └── EnvBindingsCache.cs
│   │
│   └── Aero.Engine.DocumentForge/    # thin DF HTTP client
│       ├── DfClient.cs
│       └── DfModels.cs
│
├── tests/
│   ├── Aero.Engine.Core.Tests/    # port the TS evaluator tests
│   └── Aero.Engine.Api.Tests/
│
├── Dockerfile
├── render.yaml
└── README.md
```

---

## 10. Test strategy — port the TS scenarios

The AERO admin app has scenarios stored in DocumentForge (`scenarios` collection) keyed by `ruleId`. Each scenario has:

```ts
type Scenario = {
  id: string;
  ruleId: string;
  name: string;
  request: string;       // JSON template; placeholders like {{randomPnr}} OK
  expectedOutput?: string;
  status: 'pass' | 'fail' | 'pending';
};
```

**Test harness**: `Aero.Engine.Tests.IntegrationTests`:

1. Load all scenarios from DocumentForge for a known rule
2. Resolve placeholders (port `src/lib/placeholders.ts`)
3. POST each request to the engine
4. Assert verdict matches `status` and (when `expectedOutput` is set) `result` matches
5. Use the trace in failures for debugging

This gives you a regression suite you can run against any rule version without writing C# tests by hand.

---

## 11. First milestone — a single rule end to end

Don't try to support every node category at once. **Walking skeleton**:

1. **Boot**: load a known published rule (`rule-bag-policy`, version 1) from DocumentForge into memory
2. **Endpoint**: register `POST /v1/ancillary/bag-policy` to dispatch to the engine
3. **Walk**: support exactly:
   - `input` node (passthrough)
   - `filter` (string only) with the `cus-cabin` config
   - `logic / and` (combine two filter passes)
   - `output` node (echo a hardcoded result if all upstream filters passed)
4. **Output envelope** with optional trace
5. **Test**: POST one of the seeded scenarios, get a 200 with the expected verdict

That's the proof point. Then layer in number, date, sub-rule call, mutator, etc. — each new node category is a new evaluator implementation and a few unit tests.

---

## 12. Open questions to resolve early

1. **Rule routing** — when two rules share an endpoint (one in staging, one in dev), how does the engine pick? **Suggested**: bake env into the URL (`/v1/ancillary/bag-policy?env=staging`) OR a per-deployment env var (`AERO_ENGINE_ENV=staging`). I'd start with the env var.
2. **Auth** — does the engine accept anonymous traffic, JWT, or API key? **Suggested**: API key initially, header `X-AERO-Key`. Add JWT for partner traffic later.
3. **Rate limiting** — none in v1. Add when traffic shape is known.
4. **Observability** — OpenTelemetry from day one. Each rule run is a span; sub-rule calls are child spans. Free traces in any OTLP-compatible backend.
5. **Schema registry** — when the rule schema evolves, how do we keep .NET ↔ TS in sync? **Suggested**: this `RULES_ENGINE_BRIEF.md` is the contract; bump a `SchemaVersion` on rule docs once we change the shape.

---

## 13. Starting the new session — recipe

Two repos, one mental model.

**Recommended setup:**

1. Create a new repo: `aero-rules-engine-dotnet` (or whatever name fits)
2. In that repo, `docs/BRIEF.md` = a copy of this file (or symlink to here)
3. First message to the new Claude session:
   ```
   We're building the .NET runtime engine described in docs/BRIEF.md.
   Read it end-to-end, then read the TS reference evaluators at:
     https://github.com/andrewfblake/airline-rules-engine/tree/main/src/shared/evaluators
     https://github.com/andrewfblake/airline-rules-engine/blob/main/src/shared/types.ts
   Then propose a concrete first slice that ships milestone 11 — single
   rule, walking skeleton, one scenario green.
   ```

That single prompt + the brief gives the new session full context. No need for fancy memory tooling.

If you want stronger context portability later, **export the schema as JSON Schema** from the zod definitions — then the .NET project can use a JSON Schema → C# generator. But for v1, hand-port from the TS types in this brief.

---

*Last updated: 2026-04-27. Maintained alongside the AERO admin schema.*
