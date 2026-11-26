# Implementation status (2026-11-26, overnight build)

Every phase in [plan.md](plan.md) is implemented and tested against **mock** Azure DevOps and a **mock** Copilot engine. None of it has run against real credentials yet. The Phase 0 spikes (S0–S3) become the checklist for the first live run below.

**Verified:**
- `dotnet test` passes (275+ tests: core metrics, scanners, fact checks, auditor, sync against the mock, app boot).
- `docker compose up` works end to end in mock mode: sync → clone → scan → auditor → findings.
- A headless-browser pass over every page showed no console or circuit errors.

## What's built

| Plan item | Status | Notes |
|---|---|---|
| 1a Foundations | ✅ | compose, config validation and reload, EF migrations with `VACUUM INTO` backup, throttling handler, setup page with PAT-scope probe, repo picker, hot/warm/daily sync, budgeted backfill, raw run/PR JSON, Jobs page, freshness and banners |
| 1b Attention queue | ✅ | approvals for me (direct and via groups), main red, failed Production deploy, slow release, stuck PRs, failed PR validations, running/queued, shipped, "since you last looked", tab badge |
| 1c Components and deployments | ✅ | skaffold/sqlproj discovery plus config overrides and deploy rules, deployments derived from stages (no `environment_manage` scope), matrix, Gates page |
| 1d Health | ✅ | calculators (retry-aware red intervals, flaky jobs), tiers with Insufficient/provisional, daily snapshots, Health table/tiles, repo tabs, template compliance |
| 2 Copilot audit | ✅ (mock-tested) | auditor container (Copilot SDK 2.0.3, in-process runtime, read-only tools plus path guard), immutable worktrees, scanners, rubric v1, findings backlog, baselines, facts-only fallback |
| 3 Extras | ✅ | delivery metrics (sim+prod), wasted minutes and quarantine, test-level flakes, cross-repo failure fingerprints, template release overlay and impact, Copilot template-change summary, fleet outliers, votes, weekly digest and Copilot brief, browser notifications, presentation mode |

**Not done / partial:**
- **Why a gate escalated:** the check-runs API needs a `checkSuiteId` we don't know how to obtain. In live mode the reason shown is the approval's *instructions* text. This is spike S2 ①.
- **Golden-repo calibration:** `audit.goldenRepos` exists in config, but the comparison is manual.
- **Timeline raw JSON:** not stored; only parsed records are kept (run and PR JSON are).
- **Lead time:** uses "first Production deploy of a run queued after the merge", an approximation that works with squash merges.

## First live run — checklist

1. **Clean start:**
   - Run `docker compose down -v`. This drops the mock data, so the database, clones and Copilot login volumes are recreated.
   - In `.env`, set:
     - `ADO_MODE=live`
     - `ADO_ORGANIZATION`
     - `ADO_PROJECT`
     - `ADO_PAT` (org-scoped; read scopes: Code, Build, Project and Team, Test Management, Identity, Graph)
2. **Start it:** `docker compose up -d --build`, then open **Settings → Check PAT scopes**. Every row should be ✅.
3. **Assign two or three repos first,** including one with API + listener + DB, and watch **Jobs**:
   - **S2 ⑦ (API cost):** the cost of a backfill. Each warm pass is capped at 300 timeline and 300 PR-thread fetches.
   - **S2 ⑦ (retention):** how far back builds go.
   - **TSTU:** the Jobs KPIs show TSTU used and rate-limit remaining.
4. **Stage names (S2 ⑥ / S3):** open a repo → **Pipelines** tab and hover the stage dots.
   - Adjust `environments[].stagePatterns` until the deploy stages map to team/staging/sim/prod.
   - Then **Settings → Reload config** and **Re-derive deployments**.
   - Check the **Deployments** tab and the matrix on Now.
   - If job names are generic, add `components.deployRules`.
   - Target: at least 80% of deployments attributed automatically.
5. **Templates (S2 ⑤):** **Templates** tab. Confirm the ref (`main`/`next`) and commit appear from the run resources. If they're missing, the YAML fallback is used; check the `templates.aliasPatterns` and `templates.repository` config.
6. **Approvals (S2 ① ②):**
   - When an escalated approval is pending, check that it shows on Now as "waiting for you" with the right environment and stage.
   - Set `environments[].adoId` for sim/prod to get the Gates list.
   - If escalations use a `ManualValidation` task, they won't appear in the approvals API; tell me and we'll read them from the timeline instead.
7. **Copilot (S0/S1):**
   - Run `docker compose run --rm auditor login`, then set `AUDITOR_ENGINE=copilot` and run `docker compose up -d`.
   - **Settings → Check auditor** should show authenticated, your models, and the selected `audit.model` (default `claude-opus-5.5`; change it in config if your seat's id differs).
   - Then **Audit now** on one repo, and check:
     - the transcript (Audit history → view)
     - the evidence paths
     - that no tool call outside the worktree was allowed
8. **Calibrate:**
   - After a day of data, tune `rules` thresholds against the Health table.
   - Pick 3 golden repos and hand-label them.

## Adapted to our real pipelines (2026-11-26)
- **Separate pipelines:** CI and release are separate pipelines.
  - Release pipelines are excluded from build stability. They're detected by `pipelines.releaseNamePatterns`, or automatically when a pipeline never runs for PRs and every run deploys.
- **Environments:** `test{env} deploy` = team. `{staging|sim|prod}{green|eu} deploy` = per-region environments.
  - Sim and prod are both Production tier.
  - Drift is only compared within a region.
- **Approval Gate 2** (`pipelines.gateStagePatterns`):
  - not a build stage
  - its time counts as approval wait
  - a release waiting there longer than `gateBlockedMinutes` shows on Now as "release waiting at gate", read from the timeline, so it works for either check type
  - approvals in the gate report the next deploy stage's environment
- **Components:** `skaffold_deploy` → the repo's AKS components; `dacpac_apply` → its database. `pre_apply`, `terraform_*` and `mssql_managed_identity_deploy` are infrastructure.
- **Templates:** `cbi/cbi-yaml-templates`, alias `templates`, branches `main`/`next`.
- **Bots:** clearcoder, product-health and "Build Service" accounts are excluded from PR metrics.
- **Mock:** mirrors all of the above.

## Known risks for the first live run

| Risk | Detail |
|---|---|
| Copilot session options untested | The combination (system message append, empty MCP, skills/extensions/config discovery off, in-process runtime) and strict structured-output schema have never run against a real model. Failures surface as a failed audit with the SDK error; the app keeps working in facts-only mode. |
| Copilot login handoff | Not yet proven that the in-process runtime picks up the device-code login stored in `/copilot-home`. Check **Settings → Check auditor** right after login. Fallback: `COPILOT_GITHUB_TOKEN`. |
| Host GitHub credentials | Don't pass `GH_TOKEN`/`GITHUB_TOKEN` into the auditor; the runtime picks them up. |
| Group-approver resolution | Depends on the Identity (Read) scope and the `queryMembership=Expanded` call. If it fails, only direct assignments count as "mine". |
| Busy repos | The default 90-day build backfill at 300 timelines per pass may take a while. It proceeds in the background and Now works throughout. |
| Auditor image size | About 930 MB, including a probably-unneeded second copy of the runtime. Prune it after a real run proves in-process mode works. |

Additional risks from the live-client review (all endpoints checked against the 7.1 docs; 16 fixture-based parsing tests):
- **Undocumented shapes** — the tests pin them, but verify each against one real PR and one real run:
  - the PR "published" time from `IsDraftUpdate` threads, and the voter identity from `CodeReviewVotedByIdentity`
  - the approvals `pipeline.owner.id` (= build id)
  - previous-attempt timelines via `timeline/{timelineId}`
- **Entra (AAD) groups** may not appear in expanded identity membership. If so, group-assigned approvals won't count as "mine".
- **TSTU usage** can't be measured exactly. ADO only sends `X-RateLimit-*` headers near the limit; the Jobs page shows "rate-limit remaining" when reported.

## Mock mode

`ADO_MODE=mock` (default) serves a deterministic synthetic organisation, which is useful for UI work and demos:
- 40 repos, 30 assigned
- merges trigger CI/CD with team→staging→sim→prod stages
- approvals, about 12% of which escalate to "you"
- retries and flaky jobs, shared platform failures
- template commits on `main`/`next`
- a repo on a feature template branch
- a legacy net6 repo

Mock repo content is generated per SHA, so the scanners and auditor have real files to read.
