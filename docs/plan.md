# DevOps Dashboard — Implementation Plan (v2, 2026-11-25)

> **Status:** all phases implemented against mock ADO/Copilot — see [STATUS.md](STATUS.md) for what was built, deviations, and the first-live-run checklist.

The background research, decisions and review notes are in [research.md](research.md) (§4 = decisions, §6 = review synthesis). This document covers **what to build and in what order**.

**v2 changes** (from the Fable plan review, which checked the API and SDK claims against current docs):
- The design no longer needs the `vso.environment_manage` scope: deployments are derived from timeline stages.
- Spike S2 now starts by examining a real escalated approval.
- Phase 1 is re-sliced so the attention queue (with live approvals) comes first and tiers come last.
- Metric definitions are corrected.
- The Copilot section is rewritten for SDK v2.
- Scope is cut for one user and ~30 repos.
- Missing operational pieces are added: first run, backups, time zones, WSL, observability.

---

## 1. Goals and non-goals

**Goals**
1. Pick the ~30 repos the team owns from the ADO project and track them.
2. **Now view:** what needs my attention right now across our services (my approvals first).
3. **Health view:** build stability, PR flow and flakiness per repo, with tiers and drill-down.
4. **Template compliance** with the platform team's shared pipeline templates.
5. **Copilot audit:** facts plus best-practice findings per repo, tracked over time as a backlog.

**Non-goals (v1)**
- Security scanning or alerts.
- Any writes to ADO (read-only).
- AKS or cluster access.
- Multi-user use, auth or hosting.
- Per-person metrics.
- Classic release pipelines.
- UI editors for rules, components or deploy rules (YAML config instead).

---

## 2. Architecture

```
docker compose (localhost only)
┌───────────────────────────────────────────┐ HTTP  ┌────────────────────────────────┐
│ app  (ASP.NET Core .NET 10, Blazor Server) │──────▶│ auditor (small .NET worker     │
│  • UI (Now / Health / Repo / Gates /       │ token │   hosting the Copilot SDK)     │
│    Findings / Settings / Jobs)             │◀──────│  • NO ADO PAT                  │
│  • Sync jobs (hot / warm / daily)          │ result│  • audit worktrees mounted RO  │
│  • Metrics + rules (queries over raw data) │       │  • tools: read-only file tools │
│  • Fact scanners (static parsing only)     │       │    + pre-tool-use path guard   │
│  • Clone/worktree manager                  │       │  • Copilot auth (see S1)       │
│  • EF Core + SQLite (WAL, busy timeout)    │       │  • pinned SDK + runtime pair   │
└───────────────────────────────────────────┘       └────────────────────────────────┘
named volumes: data (sqlite + backups) · clones (rw app, ro auditor) · copilot-auth (auditor)
port:    127.0.0.1:8080 → app      (auditor: compose network only, no published port)
secrets: ADO_PAT (app only) · COPILOT_GITHUB_TOKEN (auditor only, if S1 picks token auth)
         · AUDITOR_TOKEN (shared random)
config:  ./config/dashboard.yaml (read-only mount, validated at startup and on reload)
```

**Key choices**

| Choice | Decision | Why |
|---|---|---|
| UI | Blazor Server, **Fluent UI Blazor**, ApexCharts.Blazor | Everything in C#; live updates; one user |
| Persistence | EF Core + SQLite, WAL + busy timeout (no writer queue). **Migrations run at startup after a `VACUUM INTO` backup** (keep the last 7) | Simple and safe. Copying a live WAL file is not a valid backup |
| Raw data | Keep the **raw JSON** of runs, timelines and PRs (compressed) next to the parsed rows | ADO retention makes parsing bugs unrecoverable otherwise; metrics can be re-derived |
| Background work | `BackgroundService`s plus a `Jobs` table with history. **Overdue jobs run on startup** (the laptop sleeps) | No Hangfire or Quartz |
| Time | Store **UTC**. An IANA time zone and working hours come from config; DST-aware working-hours maths | Containers run in UTC |
| ADO client | Typed `HttpClient` over REST 7.1 (previews where needed) with a throttling `DelegatingHandler` (Retry-After, `X-RateLimit-*` counters) | Control over continuation tokens and preview APIs |
| Audit isolation | A separate **`auditor` service of our own** that hosts the Copilot SDK and exposes a tiny internal HTTP API (`POST /audits`, `GET /audits/{id}`). The app never exposes or talks to the Copilot runtime directly | Keeps the PAT and DB out of the agent's reach without a network-exposed runtime server |
| Clones | Persistent partial clones (`--filter=blob:none`). Each audit gets an **immutable `git worktree` at the exact SHA** | A fetch can't change files mid-audit; `git diff lastAuditSha..sha` is available |
| WSL | DB and clones live on **named Docker volumes**, never bind mounts under `/mnt/c` | 9p I/O is slow, and SQLite locking is unreliable over it |
| Scanners | Static parsing in C#, plus NuGet / Terraform registry and .NET release-metadata lookups | Never run restore, `terraform init` or anything else that executes repo code |

**Solution layout** (kept small)
```
src/
  Dashboard.Web       Blazor UI, hosted services, EF Core, ADO client, sync, clone manager
  Dashboard.Core      pure logic: entities, metric calculators, rule engine, scanners (unit-tested)
  Dashboard.Auditor   Copilot SDK host + internal HTTP API (Phase 2)
audit/rubric/v1/      check catalogue (YAML) + prompt templates, versioned
config/dashboard.yaml
tests/Dashboard.Tests unit tests + recorded-fixture tests (sanitised ADO JSON) + fixture repos
docker/               app.Dockerfile, auditor.Dockerfile, compose.yaml, .env.example
```

**PAT scopes, read only:** `vso.code`, `vso.build`, `vso.project`, `vso.graph`, plus `vso.test` in Phase 3. The design **avoids `vso.environment_manage`**, which is high-privilege and the only scope documented for listing environments and reading deployment records. Environment names and ids live in config, and deployments are derived from timeline stages. S2 confirms whether `vso.build` alone is enough.

**First run:**
- A setup page **probes each endpoint** with the PAT and reports which scopes are missing.
- It validates the config.
- It shows backfill progress and the measured TSTU cost.
- Pages show empty states until data arrives.

**Self-observability:**
- a `/health` endpoint
- a **Jobs** page (history, durations, errors, last success per source)
- TSTU counters
- stored audit transcripts

---

## 3. Data model (core tables)

| Table | Key fields | Notes |
|---|---|---|
| `Repo` | adoId, name, defaultBranch, type (Service/Library/Infra), assigned, pinned, muted | Type selects the rule set |
| `Pipeline` | adoDefinitionId, repoId, yamlPath, name | From build definitions for the repo |
| `Component` | repoId, name, kind (AksService/Database/Other), source (Discovered/Config), skaffoldArtifact, imageName, sqlProjPath | Discovered from `skaffold.yaml` / `.sqlproj`; overrides go in YAML |
| `Environment` | name, adoId?, tier (Team/Staging/Production), stagePatterns[] | **From config.** sim + prod = Production tier |
| `ComponentDeployRule` | pipeline/stage/job patterns → componentId (+ environment) | Config, with defaults inferred during S3 |
| `Run` | buildId, pipelineId, branch, sha, reason, queued/start/finish, result, rawJson, deletedInAdo | Upserted |
| `TimelineRecord` | runId, type, name, parentId, attempt, previousAttempts[], result, start/finish, rawJson | Stages, jobs and checkpoints; previous-attempt timelines are fetched too |
| `TemplateUsage` | runId, alias, refName, templateSha | From `Runs - Get` resources (S2 confirms template aliases appear) |
| `Deployment` | runId, stageRecordId, environment, componentId, attempt, result, start/finish, waitTime, version | **Derived from timeline stages** matching the environment's stage patterns |
| `Approval` | adoId, status, assignedApprover (user or group), **assignedToMe**, createdOn, steps, link → run/stage/env (per S2) | Hot sync |
| `EnvironmentCheck` | environment, checkType, settingsJson, approverIds, requiresMe | Daily; depends on S2 (needs environment ids) |
| `PullRequest` | adoId, repoId, status, isDraft, author, createdOn, publishedOn, closedOn, mergeCommit, size, policyStatus, rawJson | |
| `PrEvent` | prId, actor, kind (Vote/Comment/Published/Completed), time | From threads |
| `Commit` | sha, repoId, author, date | Default branch |
| `RepoFacts` | repoId, sha, scannerVersion, factsJson | Phase 2 |
| `AuditRun` / `Finding` / `FindingState` | see §9 | Phase 2 |
| `RuleResult` | repoId, date, ruleId, configHash, value, status (Pass/Fail/Insufficient), sampleSize | Daily snapshots. **Recomputed from raw data when thresholds change** |
| `SyncCursor` / `Job` | source, watermark / type, status, attempts, runAfter, history | |

**Rule:** metrics are always computed from raw rows, never accumulated. ADO retries rewrite a run's result and finish time in place.

---

## 4. Sync design

| Tier | Interval | Calls | Purpose |
|---|---|---|---|
| **Hot** | fixed 60 s while the app runs | project-wide in-progress/queued builds; active PRs; `pipelines/approvals?state=pending&$expand=steps` | Now view |
| **Warm** | 15 min | completed builds per assigned pipeline since cursor (1 h overlap) → timeline (+ previous-attempt timelines) and `Runs - Get`; closed PRs since cursor (`queryTimeRangeType=closed`) + threads; PRs that left the active list (fetch by id); default-branch head → commits if moved; template repo heads (`main`, `next`) | History |
| **Daily** | once a day, **on startup if overdue** | repo metadata, pipeline definitions, branch policies, check configurations (if S2 allows), `RuleResult` snapshots, audit triggers | Slow-changing data |
| **On assign** | once | backfill all retained builds and 180 days of PRs, rate-limited, with progress shown | History |

**Identity:**
- `connectionData` gives my user id.
- Graph memberships are expanded so group-assigned approvals count as "mine".

**Freshness:**
- each source shows "synced X ago";
- banners appear for an expired PAT, missing scopes or throttling.

---

## 5. Metric definitions

**Result classification** (configurable mapping, enumerated explicitly):

| ADO result | Treated as |
|---|---|
| `succeeded` | pass |
| `succeededWithIssues` (timeline) / `partiallySucceeded` (build) | **fail** ("a failed step = failed") |
| `failed` | fail |
| `canceled`, `abandoned`, `skipped` | **excluded** (neither pass nor fail) |

**Build stage** = a stage that matches no environment's stage patterns and has no checkpoint/approval records. The timeline doesn't mark deployment jobs, so the config patterns are the source of truth; S2 checks what the timeline does expose.

**General rules**
- Windows are 30 days unless stated.
- Bots and service identities come from a config list.
- Drafts are excluded from PR metrics.
- **Statistics:** median when n < 20; p85 only when n ≥ 20. A minimum sample (10 runs / 5 PRs) applies, otherwise the value is **Insufficient**.
- **Time units:** rule thresholds use **working hours** (config time zone and hours); displays also show wall-clock time.

| Signal | Definition |
|---|---|
| Main success rate | Default-branch non-PR runs: final build-stage outcome pass ÷ (pass + fail) |
| Red interval | Per pipeline, ordered by event time. Starts at the first failed build-stage **attempt** (including failed earlier attempts of a run later retried green). Ends at the next passing attempt. Retries therefore don't erase red time |
| Main red now | An open red interval: its duration and the run link |
| Time to recover | Median red-interval length |
| Build duration | Build-stage duration of the final attempt, weekly median (p95 when n ≥ 20) |
| PR validation failure rate | Shown separately; not instability |
| **Flaky job** | A job whose final attempt passed **and** which has a previous attempt that **failed**. A whole-stage rerun of jobs that passed doesn't count |
| **Flaky run rate** (FLK-001) | Default-branch runs with ≥1 flaky job ÷ completed default-branch runs |
| Same-SHA flake (weak signal) | Same pipeline + SHA + template SHA: a run failed and a later run passed (non-PR only). Informational |
| Wasted minutes | Σ duration of failed attempts/runs that were later green (from previous-attempt records) |
| Time to first review | First human non-author Vote/Comment event − `publishedOn` (or `createdOn`), in working hours |
| Time to merge | `closedOn − publishedOn` for completed PRs |
| Stuck PR | Active, not a draft, and either no human review activity for more than 1 working day, red policy, or a merge conflict |
| Waiting on | Required reviewers with vote 0; the author when a vote is −5 |
| Stuck release | A deployment stage running longer than its usual duration, **excluding time in checkpoints/approvals** (wait time tracked separately, per S2) |
| **Production** | Always the **Production tier** (sim **and** prod). Matrix and "not yet deployed" show sim and prod separately |
| Deploy frequency (Phase 3) | Distinct (component, version) pairs reaching Production per week |
| Lead time (Phase 3) | PR `publishedOn` → first Production deployment of a run whose SHA contains the PR's merge commit, split into review / pipeline / approval wait. Works with squash merges |

---

## 6. Health tiers and rules

The rule catalogue lives in `config/dashboard.yaml` (id, threshold, applicable repo types, tier).

**How tiers work**
- Tiers are cumulative gates, and the UI lists unmet rules ("Silver — 1 rule from Gold").
- **Insufficient data never passes.** The repo gets the highest tier whose rules all *pass*, and a "provisional" marker if an Insufficient rule is what blocks the next tier.
- **Dormant** = no default-branch commits **and** no runs in the 30-day window. Activity-based rules show as dormant rather than failed.
- Thresholds are calibrated after the first full sync.

**Phase 1 rules** (data available from sync)

| Tier | Rules |
|---|---|
| **Bronze** | PIPE-001 CI pipeline on the default branch · POL-001 ≥1 required reviewer · POL-002 build validation policy · TPL-001 pipelines `extends` the shared templates |
| **Silver** | BLD-001 main success ≥90% · BLD-002 no red interval > 8 working hours · FLK-001 flaky run rate < 5% · PR-001 time to first review (median/p85) < 16 working hours · TPL-002 template ref is `main` or `next` |
| **Gold** | BLD-003 main success ≥95% · PR-002 no active PR older than 5 working days · DEP-001 deployed to Production in the last 30 days (Service type only) |

**Phase 2 adds:**
- DOC-001 README (Bronze)
- NET-001 supported .NET target framework (Bronze)
- TST-001 test projects exist for each deployable (Gold)
- AUD-001 no open high-severity *fact-based* finding (Gold)

**Template staleness** (behind the branch head) is **informational, not a rule**. It would otherwise fail quiet repos and duplicate "last run age".

---

## 7. Template compliance

Config holds the template repo and the alias pattern(s).

For each pipeline, from the latest default-branch run:
1. **Ref branch:**
   - `main` → ✓
   - `next` → ⚑ experimental (⚠ if that run deployed to Production)
   - anything else → ✗
2. **Structure** (static YAML parse): `extends: …@alias` is used; stages, jobs or steps defined inline (bypass) are flagged.
3. **Staleness (info):** template commits between the SHA used and the current branch head, plus the date of the last run, e.g. "hasn't run since 12 template changes".
4. **Phase 3:** an LLM summary of template changes since that SHA, and deprecated templates/parameters.

If S2 shows template aliases are missing from `Runs - Get` resources (for nested templates), fall back to parsing the YAML `resources` block statically.

---

## 8. Approvals and gates

**This section depends on S2.** How a gate escalates to my approval is not yet known. The candidate mechanisms:

| Mechanism | Shows up via |
|---|---|
| (a) Environment approval check naming me or my group | `pipelines/approvals` + check configurations |
| (b) `ManualValidation` task in an agentless job | Unclear whether it appears in the approvals API; maybe only as a timeline record |
| (c) A separate "escalation" environment with an approval check | Same as (a) |
| (d) Something else in the templates, e.g. Invoke REST check plus manual re-run | Check evaluations / timeline |

**Planned features**, adjusted after S2:
- **Live "Waiting for me":**
  - pending approvals assigned to me or my groups, linked to the run, stage, environment and component
  - top of the attention queue
  - wait time and a deep link
- **Why it escalated:**
  - `pipelines/checks/runs/{checkSuiteId}` returns per-check `status` and `resultMessage`
  - S2 must find how to get the `checkSuiteId` for a run (e.g. from timeline checkpoint records)
- **"Gates that can escalate to me" (static):** check configurations per environment, which need environment ids from config (or from S2). Otherwise this view is built from observed escalations, i.e. which pipelines/stages have needed my approval in the past N days.

---

## 9. Audit pipeline (Phase 2)

**Copilot SDK v2 facts** (verified 2026-11-25):
- `Microsoft.Copilot.SDK` 2.0.3 just shipped, so **pin the SDK and runtime versions as a pair**.
- `ListModelsAsync()` lists the available models.
- `SendAndWaitAsync<T>` gives structured output, but is experimental, has a **60 s default timeout** and is not full schema validation, so set an explicit timeout and validate yourself.
- `Tools.AvailableTools` / `ExcludedTools` restrict tools.
- `Permissions.OnRequest` handles permission requests; pending requests block if unhandled.
- A pre-tool-use hook sees `toolName` and `toolArgs` and can deny the call (used for the path guard).
- There is no official container image, so we build our own from the release tarball.

**Flow**
1. **Trigger:** the default-branch head moved and either more than 24 h passed or relevant files changed. There is also an "Audit now" button.
2. **The app** fetches, creates an immutable worktree at the SHA, runs the scanners, saves `RepoFacts`, then `POST /audits` to the auditor with (repo, sha, worktree path, facts, rubric version).
3. **Scanners, v1 scope:**
   - .NET: sln/slnx, csproj (SDK type, target frameworks, nullable, analyzers), central package management, package staleness from the NuGet API, .NET support status, test projects
   - pipelines: `extends`, inline bypass
   - deploy: `skaffold.yaml` artifacts and profiles, Helm/k8s manifests (requests/limits, probes), Dockerfiles (multi-stage, pinned base image, non-root)
   - data: EF Core, Dapper, `.sqlproj`
   - messaging: Service Bus libraries
   - docs: README

   Terraform, Hiera and language statistics come in Phase 3.
4. **Check catalogue v1, about 10 checks:**
   - `ARCH-001` architecture style with evidence
   - `ARCH-002` dependency direction
   - `NET-001` supported target framework *(fact)*
   - `NET-002` central package management *(fact)*
   - `K8S-001` resource requests and limits *(fact)*
   - `K8S-002` probes *(fact)*
   - `DOCK-001` Dockerfile practices *(fact)*
   - `DATA-001` data-access approach is consistent and isolated
   - `MSG-001` retries and dead-letter handling are explicit
   - `TEST-001` test projects exist for each deployable *(fact)*

   More come in Phase 3, and team standards later as `audit/rubric/team/*.yaml`.
5. **Auditor session:**
   - **Model:** the configured model if `ListModelsAsync()` offers it, otherwise the strongest available; high reasoning effort.
   - **Tools:** read-only file tools only (exact tool names confirmed in S1). A pre-tool-use hook denies any path outside the worktree.
   - **Input:** facts + judgement checks + diff since the last audit.
   - **Output:** structured, with an explicit timeout (e.g. 20 min), validated by the app with one retry.
   - **Evidence:** every finding must cite a file that exists, otherwise it is dropped.
   - The full transcript is stored.
6. **Tracking:**
   - fingerprint = `checkId + component + normalisedPath`
   - `FindingState` moves New → Accepted | Dismissed(reason) → Done (automatic once a finding is absent)
   - compare only audits with the same model + rubric version; a change triggers a re-baseline
7. **Calibration:** 3 hand-labelled golden repos. **Single run per check**; add majority voting only if re-run stability falls short of the target.
8. **Export:** Markdown per repo and a fleet digest. Findings are grouped by check across repos ("9 repos fail NET-002").

---

## 10. UI pages

| Page | Phase | Contents |
|---|---|---|
| **Now** (home) | 1b → 1c | **Attention queue**: ① waiting for me ② main red ③ failed Production deployment ④ stuck release ⑤ stuck PR ⑥ failed PR validation. Plus running/queued builds, "shipped since yesterday" and "since you last looked". **Component × environment matrix** added in 1c. Panels are separate components ordered by config |
| **Gates** | 1c | Pending and recent approvals for me; gates that can escalate to me (per §8) |
| **Health** | 1d | Sortable table, worst first: tier (+ gap / provisional), main red now, main %, flaky %, wasted minutes, time to first review, oldest PR, last Production deploy, template status, trend. Filters: changed this week, type |
| **Repo** | 1c–1d | Header: tier, why, and the gap to the next tier. Tabs: Overview, Pipelines, PRs, Deployments, Templates, (Phase 2) Audit |
| **Findings** | 2 | Fleet backlog grouped by check; state, severity and repo filters; Markdown export |
| **Settings / Setup** | 1a | Repo picker (search, assign, type, pin/mute), PAT scope probe, config validation and reload, Copilot connection status (Phase 2) |
| **Jobs** | 1a | Sync/audit job history, errors, TSTU usage |

**Global elements:**
- freshness indicator
- tab-title attention badge
- `/` quick-jump
- ⓘ formula + sample-size popover on every metric
- deep links into ADO everywhere

**Phase 3:**
- browser notifications
- presentation mode (large font, no names)

---

## 11. Phases and exit criteria

### Phase 0: Spikes
| Spike | When | Must prove (pass line) |
|---|---|---|
| **S0 Copilot quick check** | Day 1, about 1 h | On my machine, `copilot` CLI works under my seat and an SDK console app calls `ListModelsAsync()` and sees the preferred model. Fails → rethink the audit approach before investing further |
| **S2 ADO data shape** | First | ① **Inspect a real escalated run end to end** (timeline incl. checkpoints, approvals API, check suites) and write down the mechanism → finalise §8. ② Approval → run/stage/environment linkage. ③ Whether environments and check configs are readable with `vso.build` only. ④ Group-approver resolution via Graph. ⑤ `Runs - Get` includes template aliases (incl. nested). ⑥ What the timeline exposes for deployment stages, checkpoints, attempts and previous attempts. ⑦ Retention horizon. ⑧ **Measured TSTU cost** of backfilling one repo |
| **S3 Components** | After S2 | Parse `skaffold.yaml` + `.sqlproj` for 3 repos and map them to deploy stages and versions. **Pass: ≥80% of deployments map automatically**; the rest need a few config rules |

Output: `docs/spikes.md` with findings and plan amendments, plus recorded fixtures for tests.

### Phase 1: Core dashboard
- **1a Foundations:**
  - skeleton, compose, config + validation, EF schema + backup-then-migrate
  - ADO client + throttling, setup page (scope probe), repo picker
  - hot/warm/daily sync + backfill, raw JSON retention
  - Jobs page, freshness
- **1b Attention queue:**
  - live approvals ("waiting for me"), main red, stuck PRs, failed PR validations
  - running/queued, shipped, "since last looked", tab badge

  **Exit 1b: used daily for a week.**
- **1c Components and deployments:**
  - component discovery + deploy rules
  - `Deployment` derivation
  - matrix on Now
  - failed/stuck Production deployments in the queue
  - Gates page
  - repo Deployments tab
- **1d Health:**
  - metric calculators
  - `RuleResult` snapshots
  - tiers (Phase 1 rules)
  - Health table
  - repo Overview / Pipelines / PRs / Templates tabs
  - template compliance

**Exit Phase 1:** for 3 repos, **hand-computed values** match the dashboard:
- main success rate exactly;
- red hours and PR medians within ±5%;
- tier thresholds calibrated against the real distribution.

### Phase 2: Copilot audit
- **S1 full Copilot spike first:**
  - build the auditor image (SDK/runtime pinned)
  - **auth in the container:** `COPILOT_GITHUB_TOKEN` (fine-grained token with "Copilot Requests") vs a persisted `copilot login --device-code`; pick whichever works for my account
  - confirm tool names
  - the path-guard hook denies `/etc/passwd`
  - a real audit completes within the explicit timeout
  - valid structured output
- **Then:** scanners → rubric v1 → orchestration → Audit tab → Findings page → export.
- **Pilot** on 3–5 repos (including the 3 golden ones).

**Exit:**
- re-auditing an unchanged SHA gives **≥90% identical check results**;
- golden labels reproduced;
- ≥70% of pilot findings not dismissed as false positives.

### Phase 3: Extras (prioritise after Phase 2)
- **Metrics:** delivery metrics (deploy frequency, lead time split, CFR proxy); wasted-minutes ranking; test-level flakiness (`vso.test`)
- **Patterns across repos:** cross-repo failure fingerprints; template release overlay on charts; fleet outliers
- **Template help:** template change summaries
- **Audit depth:** more scanners (Terraform, Hiera, languages) and checks; majority voting if needed
- **Summaries and output:** weekly LLM brief; browser notifications; presentation mode

---

## 12. Testing strategy
- **Core metrics and rules:** pure functions with scenario tests covering every case in §5:
  - retries with failed previous attempts
  - whole-stage reruns
  - `succeededWithIssues` and skipped stages
  - red intervals across retries
  - drafts, bots
  - working hours across DST changes
  - small-n statistics
  - insufficient-data tiers
- **ADO client and sync:** recorded, sanitised fixtures from S2; no live calls.
- **Scanners:** fixture repos under `tests/fixtures/repos/*`.
- **Audit:** schema validation, evidence verification and a path-guard test. Golden-repo calibration is manual per rubric or model change.

## 13. Risks
| Risk | Mitigation |
|---|---|
| Copilot SDK v2 churn (2.0.3 shipped after six previews in two days) | Pin the SDK/runtime pair; thin wrapper in `Dashboard.Auditor`; S1 just before Phase 2 |
| Headless Copilot auth needs a GitHub token my account can't create | S1 tests the persisted device login; worst case the auditor runs on the host |
| Org policy disables the SDK/CLI or models | S0 on day 1 |
| Escalation mechanism isn't visible through the approvals API | S2 ①; fall back to timeline checkpoint records and observed history |
| Deployments can't be derived reliably from stage names | Config patterns + S3 pass line; `vso.environment_manage` as a last resort, by my choice |
| Template aliases missing from run resources | Static YAML `resources` parse |
| Throttling or a slow backfill | TSTU cost measured in S2; rate-limited backfill with progress |
| PAT expiry | Expiry banner + scope probe |

## 14. Open items
- **Copilot auth** for the auditor container: can I create a fine-grained GitHub token with "Copilot Requests" for my Copilot account? Resolved in S1.
- **Escalation mechanism** for my approvals. Resolved in S2 ①, unless I already know.
- Team environment names → `Environment` config and stage patterns.
- Bot/service identities to exclude; working hours and time zone; preferred Copilot model.
