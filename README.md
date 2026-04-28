# RuleForge

**A runtime rules engine for airline retailing — and any other domain whose
business logic lives in a directed graph of filters, lookups, mutations and
sub-rule calls.** Rules are authored visually (in your own admin app), pinned
per environment, and evaluated at request time with a sub-millisecond hot
path.

> Sibling project to [DocumentForge](https://github.com/tailwind-retailing/documentforge) —
> RuleForge stores its rules and reference data inside DocumentForge, but
> the engine is a clean HTTP service you can drop in anywhere.

```text
   ┌──────────────────────┐         ┌────────────────────┐
   │ Caller (PSS,         │  HTTP   │  RuleForge         │
   │ retailing platform)  ├────────▶│  ASP.NET Core      │
   └──────────────────────┘         │                    │
                                    │  ┌──────────────┐  │   HTTP
                                    │  │ Rule loader  ├──┼─────┐
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

## What's in the box

- **DAG walker** — input → filters → logic → product/mutator/calc → output, with cycle detection at validate time.
- **Filter primitives** — string, number and date filters, each with five array selectors (`any/all/none/first/only`) and three on-missing modes (`fail/pass/skip`).
- **Logic ops** — `and`, `or`, `xor`, `not`. Verdicts route through 3-tag edges (`pass | fail | default`) with the rule documented in [`docs/rule-schema.html`](https://ruleforge-docs.onrender.com/rule-schema.html).
- **Sub-rule calls** — any node can invoke another rule with isolated context, `inputMapping` / `outputMapping`, and `onError: skip|fail|default`.
- **Reference set lookups** — `lookup-and-replace` mutator pulls a row out of a versioned reference set and writes one column onto the upstream object.
- **Calc nodes** — arbitrary arithmetic, comparison and conditional expressions via [NCalc](https://github.com/ncalc/ncalc), with variables resolved from upstream object fields, execution context and request fields.
- **Auto-routing** — every endpoint that has an environment binding becomes a live HTTP route at boot.
- **API auth** — `X-AERO-Key` or `Authorization: Bearer …`, constant-time compare, `/health` always open.

## Performance (single laptop, NVMe)

| Mode | p50 | p95 | p99 | req/s |
|---|---|---|---|---|
| Warm steady-state, 1 worker     | 0.07 ms | 0.09 ms | 0.14 ms | ~14k  |
| Warm steady-state, 16 workers   | 0.13 ms | 0.23 ms | 1.45 ms | ~74k  |
| Cold (fresh source per request, local DocumentForge) | 2.5 ms | 3.7 ms | 6.2 ms | ~375 |
| Cold (fresh source per request, cross-region DF)     | 1474 ms | 1664 ms | 1838 ms | ~1 |

The brief targets <5ms for typical 10–20-node rule graphs. The engine clears
that target by ~70× at warm steady-state.

## Quick start

```bash
git clone https://github.com/tailwind-retailing/ruleforge.git
cd ruleforge
dotnet build
dotnet test                                  # 126/126 green

# Run a sample rule against a sample request, in-process, against the
# bundled local fixture pack (no DocumentForge required for this path):
dotnet run --project src/RuleForge.Cli -- run \
    --endpoint /v1/ancillary/bag-policy \
    --request  '@fixtures/scenarios/s-bag-3pc-markup15.json' \
    --debug
```

You should see an envelope like:

```json
{
  "ruleId": "rule-bag-policy",
  "ruleVersion": 7,
  "decision": "apply",
  "evaluatedAt": "2026-04-27T12:00:00.000Z",
  "result": {
    "code": "BAG",
    "weightKg": 23,
    "currency": "AED",
    "fee": 517.5,
    "pieces": 3
  },
  "trace": [ /* per-node trace, in --debug mode */ ],
  "durationMs": 47
}
```

## CLI verbs

| Verb | What it does |
|---|---|
| `run`     | Execute a rule against a request, in-process or via HTTP |
| `publish` | Push a local rule snapshot into DocumentForge as a new ruleversion |
| `mirror`  | Idempotent copy of any DocumentForge instance into another (handy for spinning up a co-located dfdb) |
| `bench`   | Sequential or concurrent throughput / latency probe |
| `schemas` | Emit JSON Schemas for every config record (for UI builders) |

Each verb supports `--help` for the full option list. Full reference:
[`cli-reference.html`](https://ruleforge-docs.onrender.com/cli-reference.html).

## Project layout

```
src/
  RuleForge.Core            Models, evaluators, DAG walker, loader interfaces
  RuleForge.DocumentForge   Thin HTTP client + IRuleSource / IReferenceSetSource impls
  RuleForge.Api             ASP.NET Core entrypoint with auto-binding
  RuleForge.Cli             run · publish · mirror · bench

tests/
  RuleForge.Core.Tests      138 unit + integration tests

fixtures/
  rules/                    Versioned rule snapshots + endpoint bindings
  refs/                     Reference sets (price matrix, tax rates, etc.)
  scenarios/                Sample request payloads

Dockerfile                  Multi-stage build → 210 MB framework-dependent image
render.yaml                 Render Blueprint — single web service co-located with DF
```

## Deploying to Render

The repo ships with a [Render Blueprint](https://render.com/docs/blueprint-spec) at
`render.yaml`. One-click deploy gives you a Docker web service co-located with
DocumentForge (oregon region) so cold-path lookups stay loopback-fast.

**Steps**

1. In the Render dashboard: **New +** → **Blueprint** → select
   `tailwind-retailing/ruleforge`. Render reads `render.yaml` and creates the
   service.
2. Set the two `sync: false` secrets in the Render dashboard:
   - `RULEFORGE_DF_API_KEY` — bearer token for the DocumentForge HTTP API
   - `RULEFORGE_API_KEY` — caller-side `X-AERO-Key` shared secret (set this to
     a long random string)
3. Render builds the Docker image, deploys, and gives you a `*.onrender.com`
   URL. `/health` is open; `/admin/bindings` lists what the auto-router
   bound at boot; everything else is gated by `RULEFORGE_API_KEY`.

**Default settings** (override in `render.yaml` or the dashboard)

| Variable | Value | Purpose |
|---|---|---|
| `RULEFORGE_RULE_SOURCE` | `df` | Read rules from DocumentForge |
| `RULEFORGE_DF_BASE_URL` | `https://documentforge.onrender.com` | The public DocumentForge instance |
| `RULEFORGE_ENV` | `staging` | Which `environments[*].ruleBindings` to enumerate at boot |
| `RULEFORGE_DF_API_KEY` | *(secret)* | DF bearer token |
| `RULEFORGE_API_KEY` | *(secret)* | Caller auth |
| `ASPNETCORE_ENVIRONMENT` | `Production` | Standard ASP.NET Core flag |

**First request once deployed**

```bash
curl -X POST https://ruleforge.onrender.com/v1/tax/pnr \
  -H 'X-AERO-Key: <your-secret>' \
  -H 'content-type: application/json' \
  -d '{
        "orig": "LHR",
        "taxCode": "GB1",
        "pax": [
          { "id": "p1", "ageCategory": "ADT" },
          { "id": "p2", "ageCategory": "CHD" }
        ]
      }'
```

This hits the per-pax tax fixture (`rule-pnr-taxes@1`) shipped in the image.
Substitute your own published rules once the AERO admin team has authored
them and bound them to your environment.

**Operational note**

The auto-router enumerates environment bindings **once at boot**. After
publishing a new rule binding, trigger a redeploy in Render (or push to
main — autoDeploy is on) so the new endpoint registers. We'll add a
`/admin/reload-bindings` endpoint in a future slice to avoid the restart.

## Status

Production-ready for sub-millisecond evaluation of rule graphs against either
DocumentForge (HTTP, with caching) or a local file source. The runtime is
deliberately decoupled from any particular admin UI — RuleForge consumes a
JSON rule schema and emits an envelope with the result, optionally with a
full per-node trace.

## License

MIT.
