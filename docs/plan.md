# DevOps Dashboard — Implementation Plan (v1 draft, 2026-11-25)

The background research, decisions and review notes are in [research.md](research.md) (§4 = decisions, §6 = review synthesis). This document covers **what to build and in what order**.

---

## 1. Goals and non-goals

**Goals**
1. Pick the ~30 repos the team owns from the ADO project and track them.
2. **Now view:** what needs my attention right now across our services (my approvals first).
3. **Health view:** build stability, PR flow and flakiness per repo, with tiers and drill-down.
4. **Template compliance** with the platform team's shared pipeline templates.
5. **Copilot audit:** facts about each repo plus best-practice findings, tracked over time as a backlog.

**Non-goals (v1)**
- Security scanning or alerts (owned by the security team).
- Writing to ADO of any kind (the tool is read-only).
- AKS or cluster access.
- Multi-user use, auth or hosting.
- Per-person metrics.
- Classic release pipelines (only YAML is used).

---

## 2. Architecture

```
docker compose (localhost only)
┌──────────────────────────────────────────┐      ┌───────────────────────────────┐
│ app  (ASP.NET Core .NET 10, Blazor Server)│ TCP  │ auditor  (Copilot runtime      │
│  • UI (Now / Health / Repo / Gates /      │─────▶│   server, headless)            │
│    Findings / Settings)                   │token │  • NO ADO PAT                  │
│  • Sync workers (hot / warm / daily)      │      │  • clones mounted READ-ONLY    │
│  • Metrics engine (queries over raw data) │      │  • tools: view / grep / glob   │
│  • Fact scanners (static parsing only)    │      │  • Copilot login on own volume │
│  • Audit orchestrator (Copilot SDK client)│      └───────────────────────────────┘
│  • EF Core + SQLite (WAL)                 │
└──────────────────────────────────────────┘
volumes: data (sqlite) · clones (rw in app, ro in auditor) · copilot-auth (auditor only)
port:    127.0.0.1:8080 → app
secrets: ADO_PAT via .env / docker secret (app only); COPILOT_CONNECTION_TOKEN (shared, random)
config:  ./config/dashboard.yaml mounted read-only
```

**Key choices**

| Choice | Decision | Why |
|---|---|---|
| UI | Blazor Server (Interactive Server), **Fluent UI Blazor** components, ApexCharts.Blazor for charts | Everything in C#; live updates over SignalR with no separate API; one user |
| Persistence | EF Core + SQLite, WAL mode, single writer through a `Channel<T>` queue | Simple; a backup is one file copy |
| Background work | `BackgroundService`s plus a plain `Jobs` table | No Hangfire or Quartz needed at this scale |
| ADO client | Typed `HttpClient` over REST 7.1 (7.1-preview / 7.2-preview where needed) with a throttling `DelegatingHandler` | Full control of continuation tokens and preview endpoints. `Microsoft.TeamFoundationServer.Client` stays an option for DTOs |
| Audit isolation | Separate `auditor` container; the app connects via `RuntimeConnection.ForTcp` | The SDK's in-process runtime has no sandbox, so a prompt injection could read the PAT or the DB |
| Scanners | Static file parsing in C#, plus NuGet / Terraform registry and .NET release metadata lookups | Never run `dotnet restore`, `terraform init` or anything else that executes repo code |
| Clones | Persistent partial clones (`--filter=blob:none`) on a Docker volume, updated with `git fetch` | Gives the agent `git diff lastAuditSha..HEAD`; avoids slow `/mnt/c` I/O on WSL |
| Config | `config/dashboard.yaml` (org, project, environment tiers, template repo, thresholds, bot identities, working hours/time zone) | Rules and thresholds can change without a rebuild |

**Solution layout**
```
src/
  Dashboard.Web            Blazor pages and components, DI wiring, hosted services
  Dashboard.Core           domain entities, metric calculators, rule engine (pure, unit-tested)
  Dashboard.AzureDevOps    REST client, DTOs, throttling handler, sync jobs
  Dashboard.Data           EF Core DbContext, migrations, writer queue
  Dashboard.Scanners       fact scanners (sln/csproj/skaffold/k8s/docker/terraform/pipelines…)
  Dashboard.Audit          clone manager, Copilot SDK orchestration, rubric loader, finding diffing
audit/rubric/v1/           check catalogue (YAML) + prompt templates — versioned
config/dashboard.yaml
tests/                     unit tests + recorded-fixture tests (sanitised ADO JSON), no live calls
docker/                    app.Dockerfile, auditor.Dockerfile, compose.yaml
```

**PAT scopes, read only:**
- `vso.code`: repos, PRs, commits, policies
- `vso.build`: builds, timelines, runs, approvals and checks
- `vso.project`
- `vso.test`: test results and flakiness
- `vso.graph`: resolves which groups I'm in, for "my approvals"
- `vso.environment_manage`: **only if** spike S2 shows deployment records can't be read any other way

---

## 3. Data model (core tables)

| Table | Key fields | Notes |
|---|---|---|
| `Repo` | adoId, name, defaultBranch, type (Service/Library/Infra), assigned, pinned, muted, dormant | Type selects the rule set |
| `Pipeline` | adoDefinitionId, repoId, yamlPath, name, isTemplateCompliant… | Discovered from build definitions filtered by repository |
| `Component` | repoId, name, kind (AksService/Database/Other), source (Discovered/Manual), skaffoldArtifact, imageName, sqlProjPath | Discovered from `skaffold.yaml` and `.sqlproj`/dacpac tasks; the user can override |
| `ComponentDeployRule` | pipelineId, stagePattern, jobPattern → componentId | Links deployment jobs to components; can be edited |
| `Environment` | adoId, name, tier (Team/Staging/Production), isProduction | Default mapping: `staging` → Staging; `sim`, `prod` → Production; everything else → Team (configurable) |
| `EnvironmentCheck` | environmentId, checkType, settingsJson, approverIds, **requiresMe** | Drives the "Gates that can escalate to me" view |
| `Run` | buildId, pipelineId, branch, sha, reason (CI/PR/Manual/Schedule), queued/start/finish, result, deletedInAdo | Raw; upserted |
| `TimelineRecord` | runId, type (Stage/Job), name, isDeploymentJob, attempt, previousAttempts, result, start/finish | Stages and jobs only; no tasks except the first error |
| `RunFailure` | runId, recordId, errorFingerprint, normalisedMessage | Phase 3: failure fingerprints across repos |
| `TemplateUsage` | runId, alias, refName, templateSha, branchHeadShaAtSync | From `Runs - Get` resources |
| `Deployment` | runId, environmentId, componentId, stage, job, attempt, result, start/finish, version | From environment deployment records or deployment jobs in the timeline |
| `Approval` | adoId, runId, environmentId, status, assignedToMe, createdOn, steps | Live; hot sync |
| `TestFlake` | runId, testName, outcome, rerunPassed | From test results |
| `PullRequest` | adoId, repoId, status, isDraft, author, createdOn, publishedOn, closedOn, mergeStatus, filesChanged, linesChanged, policyStatus | |
| `PrReviewer` / `PrEvent` | prId, identity, vote, isRequired / prId, actor, kind (Vote/Comment/Published/Merged), time | Events derived from threads |
| `Commit` | sha, repoId, author, date | Default branch only |
| `RepoFacts` | repoId, sha, scannerVersion, factsJson | Output of the deterministic scan |
| `AuditRun` | repoId, sha, model, rubricVersion, promptHash, status, startedAt, durationMs | |
| `Finding` | fingerprint (checkId+component+path), auditRunId, checkId, result (Pass/Partial/Fail/NA), severity, evidenceJson, explanation, confidence | |
| `FindingState` | fingerprint, status (New/Accepted/Dismissed/Done), reason, firstSeen, lastSeen | Survives across audits |
| `RuleResult` | repoId, date, ruleId, value, passed, sampleSize | Daily snapshots for trends and tiers |
| `SyncCursor` / `Job` | source, scope, watermark / type, payload, status, attempts, runAfter | |

**Rule:** metrics are always *computed from raw rows* (or nightly `RuleResult` snapshots), never accumulated incrementally. ADO stage retries rewrite a run's result and finish time in place.

---

## 4. Sync design

| Tier | Interval | Calls (project-wide unless noted) | Purpose |
|---|---|---|---|
| **Hot** | 45 s (only while the UI is open, otherwise 5 min) | builds `statusFilter=inProgress,notStarted`; PRs `status=active`; `pipelines/approvals?state=pending&$expand=steps` | Now view |
| **Warm** | 15 min | completed builds per assigned pipeline since cursor (1 h overlap) → timeline, `Runs - Get` resources, test results for new or changed runs; closed PRs since cursor (`queryTimeRangeType=closed`) and threads; PRs that dropped out of the active list (fetch by id); default-branch head per repo → commits if moved; environment deployment records; template repo branch heads (`main`, `next`) | History and metrics |
| **Daily** | 02:00 local | repo metadata; pipeline definitions; branch policies; environments and **check configurations**; branch stats; recompute `RuleResult`; audit triggers | Slow-changing data |
| **On assign** | once | backfill everything still retained (builds) and 180 days of PRs | History |

- **Throttling:** honour `Retry-After`; log `X-RateLimit-*`; back off when `X-RateLimit-Delay` appears. The TSTU budget is shared with my own browser use of ADO.
- **Identity:** `connectionData` gives my user id at startup, and Graph memberships are expanded for group-based approvers.
- **Freshness:** each source records its last success and last error, and the UI shows "synced X ago" plus banners for an expired PAT or throttling.

---

## 5. Metric definitions

General:
- **Build stage** = a stage that contains no deployment jobs. Spike S2 confirms that the timeline exposes this; config stage-name patterns are the fallback.
- **Failed** = any build-stage result other than `succeeded`. `partiallySucceeded` = failed; `canceled` is excluded.
- **Minimum sample:** at least 10 runs or 5 PRs in the window, otherwise "insufficient data".
- **Excluded:** draft PRs, bot or service identities (configured list), PR builds where noted.
- Statistics are **median and p85**, never means.

| Signal | Definition |
|---|---|
| Main success rate (30 d) | Default-branch, non-PR runs whose build stages all succeeded ÷ completed non-cancelled runs |
| Main red now / red hours | Per pipeline, ordered by queue time: red starts at the first failed build stage after a green run and ends at the next green run. "Red now" includes the duration; red hours are summed over 30 days |
| Time to recover | Median length of red intervals |
| Build duration | p50 / p95 of build-stage duration, trended weekly |
| PR validation failure rate | Shown separately (it's feedback, not instability) |
| **Flaky, strong signal** | A job with `attempt > 1` whose final attempt passed |
| **Flaky, weak signal** | Same pipeline + SHA + template SHA fails, then passes in a later run (non-PR runs only) |
| **Flaky tests** | Test results flagged flaky, or failed then passed on a rerun |
| Wasted minutes | Σ duration of failed attempts or runs that were later green (flakiness as cost) |
| Time to first review | First human, non-author vote or comment event − `publishedOn` (or `createdOn` if never a draft), counting working hours |
| Time to merge | `closedOn − publishedOn` for completed PRs; p50/p85 |
| Stuck PR | Active, not a draft, and either no human review activity for more than 1 working day, red policy, or a merge conflict |
| PR age | Open working days; stale when more than 3 |
| Waiting on | Required reviewers with vote 0, and the author when a vote is −5 ("waiting for author") |
| Deploy frequency | Distinct (component, version) pairs reaching any **Production** environment (sim or prod) per week |
| Lead time | First commit of the change → first Production deployment, split into review wait / pipeline time / approval wait |
| Change failure (proxy) | Failed Production deployment stages ÷ Production deployments. **Labelled as a proxy** |
| Not yet in prod | Commits merged to the default branch that are newer than the version currently in prod, per component |

---

## 6. Health tiers and rules

The rule catalogue lives in `config/dashboard.yaml`. Each rule has an ID, a threshold, the repo types it applies to, and a tier. Tiers are **cumulative gates**, and the UI lists the unmet rules ("Silver — 1 rule from Gold"). Thresholds below are the initial values; **calibrate them after the first full sync** against the real distribution.

| Tier | Rules (Service type; Library/Infra get a subset) |
|---|---|
| **Bronze**: hygiene | PIPE-001 has a CI pipeline on the default branch · POL-001 default branch requires ≥1 reviewer · POL-002 build validation policy · TPL-001 pipelines `extends` the shared templates · DOC-001 README present · NET-001 no out-of-support .NET target framework |
| **Silver**: healthy flow | BLD-001 main success ≥90% (30 d) · BLD-002 no red interval > 1 working day (30 d) · FLK-001 flaky job rate < 5% · PR-001 p85 time to first review < 2 working days · TPL-002 template ref is `main` (or `next`) and last run is ≤14 days behind the template branch head |
| **Gold**: excellent | BLD-003 main success ≥95% · PR-002 no PR open > 7 working days · DEP-001 deployed to prod in the last 30 days (Service) · TST-001 tests run and publish in CI · AUD-001 no open high-severity audit finding (deterministic facts only can fail this) |

- **Dormant:** no commits in 60 days. It gets a label and is excluded from activity rules, so it isn't failed for being quiet.
- **Trend arrows:** compare this week's `RuleResult` snapshot with four weeks ago.

---

## 7. Template compliance

Configured in `dashboard.yaml`: the template repo (project/name) and the alias pattern(s) used in `resources.repositories`.

For each pipeline, from the latest default-branch run:
1. **Ref branch** (`TemplateUsage.refName`):
   - `main` → ✓
   - `next` → ⚑ *experimental* (info; ⚠ if that run deployed to Production)
   - anything else → ✗
2. **Staleness:** commits and days between the `templateSha` the run used and the current head of that branch. More than 14 days behind (configurable) → ⚠ "hasn't run since the templates changed".
3. **Structure:** parse the entry YAML statically to check it uses `extends: template@alias` and to find stages, jobs or steps defined inline (template bypass).
4. **Phase 3:** the LLM summarises template-repo changes since a pipeline's last-used SHA ("what you'd pick up by re-running"), and flags deprecated templates or parameters it finds referenced.

---

## 8. Approvals and gates

- **Static: "Gates that can escalate to me".** Read check configurations per environment (`pipelines/checks/configurations?resourceType=environment&resourceId=…&$expand=settings`). List approval checks where I'm an approver, directly or via a group, together with the other checks on that environment (business hours, Invoke REST/Azure Function, Azure Monitor alerts, etc.). Map them to the pipelines and components that deploy there.
- **Live: "Waiting for me".** Pending approvals with a step assigned to me or my groups, shown at the **top of the attention queue** with the environment, component, wait time and a deep link.
- **Why it escalated (spike S2):** if check run results can be read (e.g. which gate failed: incident, dead letters, outside the window), show the reason. Otherwise show the configured gates as context.

---

## 9. Audit pipeline (Phase 2)

1. **Trigger:** the default-branch head moved **and** either more than 24 h since the last audit or relevant files changed (`*.csproj`, `*.sln*`, `skaffold.yaml`, pipelines, Dockerfiles, `*.tf`, Helm or k8s manifests). There is also a manual "Audit now" button.
2. **Clone/fetch** in the app container, using the PAT via `--config-env=http.extraheader`, onto the clones volume.
3. **Deterministic scanners → `RepoFacts`:**

   | Area | What it checks |
   |---|---|
   | .NET | `.sln`/`.slnx`, `*.csproj` (SDK type → component hint, target frameworks, nullable, analyzers, `TreatWarningsAsErrors`), `Directory.Build.props`, `Directory.Packages.props` (central package management), package list compared with the NuGet API's latest versions, .NET support status from Microsoft's release metadata |
   | Tests | Test projects (xUnit/NUnit/MSTest), test files compared with source projects |
   | Data | EF Core (migrations, `ModelSnapshot`), Dapper, `.sqlproj`/dacpac, DbUp |
   | Messaging | `Azure.Messaging.ServiceBus` / MassTransit / NServiceBus, plus queue and topic names from config |
   | Deploy | `skaffold.yaml` (artifacts, profiles, modules); Helm and k8s manifests (resource requests/limits, liveness/readiness probes, replicas, image tag pinning); Dockerfiles (multi-stage, base image and pinning, non-root `USER`) |
   | Terraform | `required_version`, `required_providers` (azurerm version) compared with the registry, backend type, module sources and pinning |
   | Hiera | Hierarchy and layout summary (informational) |
   | Pipelines | Entry YAML: `extends`, stages, environments, inline bypasses |
   | Languages | File-extension counts with generated and vendor folders excluded (bin/obj/node_modules/Migrations designer files) |
   | Docs | README, CODEOWNERS (or ADO equivalent), `.editorconfig` |

4. **Check catalogue** in `audit/rubric/v1/*.yaml`. Each check has an ID, area, severity, kind (`fact` = evaluated in code, `judgement` = LLM), a description, and pass/partial/fail guidance. Initial set, about 25 checks:
   - `ARCH-001` architecture style identified (layered / clean / vertical slice / other), with evidence
   - `ARCH-002` dependency direction respected (domain doesn't reference infrastructure)
   - `ARCH-003` component boundaries match the deployables in `skaffold.yaml`
   - `NET-001` supported target framework *(fact)*
   - `NET-002` central package management *(fact)*
   - `NET-003` nullable and analyzers enabled *(fact)*
   - `NET-004` package staleness *(fact)*
   - `DATA-001` migrations approach is consistent
   - `DATA-002` data access isolated behind repositories or a context, not scattered
   - `MSG-001` Service Bus consumers handle retries and dead letters explicitly
   - `MSG-002` idempotent message handling
   - `K8S-001` resource requests and limits *(fact)*
   - `K8S-002` probes configured *(fact)*
   - `DOCK-001` non-root, multi-stage, pinned base image *(fact)*
   - `TF-001` provider and version pinning *(fact)*
   - `TF-002` remote backend *(fact)*
   - `TEST-001` test projects exist for each deployable *(fact)*
   - `TEST-002` test quality and coverage of critical paths (judgement)
   - `CFG-001` configuration follows the options pattern, with no hard-coded environment values
   - `OBS-001` structured logging and health endpoints
   - `DOC-001` README explains run, build and deploy
   - `PIPE-001` pipeline uses templates without inline bypass *(fact)*
   - …

   Team standards are added later as `audit/rubric/team/*.yaml`.
5. **Copilot session** through the `auditor` container:
   - **Model:** the configured model if `ListModels()` offers it, otherwise the strongest available; high reasoning effort.
   - **Tools:** only view/grep/glob. The permission handler denies any path outside `/clones/{repo}`.
   - **Input:** facts JSON, the judgement checks, and `git diff lastAuditSha..HEAD` (a summary if large).
   - **Output:** structured (`SendAndWaitAsync<AuditResult>` or a `submit_audit` tool), schema-validated with one retry.
   - **Evidence:** every finding cites a file (and line). The app verifies the file exists and drops findings it can't verify.
   - **Stability:** judgement checks run 3× and take the majority; disagreement is shown as "uncertain".
6. **Diff and track:**
   - fingerprint = `checkId + component + normalisedPath`
   - `FindingState` moves New → (Accepted | Dismissed+reason) → Done, automatically once a finding is absent from the latest audit
   - only audits with the same model + rubric + prompt hash are compared; any change triggers a **re-baseline**
7. **Calibration:** hand-label 3 "golden" repos. Any rubric or model change must reproduce their labels before a fleet re-baseline.
8. **Fleet view:** themes per check across repos ("9 repos fail NET-002"), plus fleet-relative outliers from the facts.
9. **Export:** Markdown per repo and a top-N findings digest (clipboard/download), since the tool is read-only.

---

## 10. UI pages

| Page | Contents |
|---|---|
| **Now** (home) | **Attention queue**, ranked: ① approvals waiting for me ② main red (duration, last committer *for context only*) ③ failed Production deployments ④ stuck releases (running > p95 duration, or queued with no agent) ⑤ stuck PRs ⑥ failed PR validations. **Component × environment matrix** (Team… / Staging / Sim / Prod: version, age, drift, in-progress indicator). Running and queued builds. "Shipped since yesterday". "Since you last looked" summary. Each panel is a separate component, so layout is config. |
| **Health** | Sortable table, worst first. Columns: tier (+ "N rules from next"), main red now, main % (30 d), flaky %, wasted min, PR p85 first review, oldest PR, last prod deploy, template status, audit age, trend. Filters: changed this week, type, tier. Tile view toggle. |
| **Repo** | Header: tier, why, next-tier gap. Tabs: **Overview** (12-week sparklines, components and environments), **Pipelines** (runs, stage durations, flaky jobs, failures), **PRs** (open/closed, flow metrics), **Deployments** (per component and environment, lead time split, not-yet-in-prod), **Templates** (per pipeline ref / staleness / bypasses), **Audit** (facts, findings with states, diff since last audit). |
| **Gates** | Environments with checks; which can escalate to me; recent and pending approvals. |
| **Findings** | Fleet backlog grouped by check/theme; filters by state, severity, repo; Markdown export. |
| **Settings** | Repo picker (search the project; assign/unassign; type; pin/mute), environment tier mapping, component overrides and deploy rules, thresholds (read-only view of config, with a reload button), sync status and errors, Copilot connection and model. |

Global elements:
- freshness indicator
- tab-title badge (attention count)
- optional browser notifications (approval waiting for me; prod deploy failed; main red > 1 h)
- `/` quick-jump
- ⓘ popover on every metric showing its formula and sample size
- every row deep-links into ADO

Presentation mode (large font, no names) comes in Phase 3, in case it's shown to the team.

---

## 11. Phases and exit criteria

### Phase 0: Spikes (prove the risky bits before building)
| Spike | Must prove |
|---|---|
| **S1 Copilot** | The `auditor` container runs the Copilot runtime headless after a one-time `copilot login --device-code` (persisted on a volume); the app connects over TCP with a connection token; tool restriction and the path guard work (an attempted read of `/etc/passwd` is denied); structured output returns a valid `AuditResult` for one real repo; `ListModels()` shows the expected models |
| **S2 ADO data shape** (3 real repos) | Timeline marks deployment jobs vs build jobs; `Runs - Get` returns the template repo `refName` + `version`; environment deployment records are readable (and with which scope, so we know whether `vso.environment_manage` is needed); approvals with `$expand=steps` identify me as an approver (directly or via group); check configurations are readable; whether check results show *why* a gate escalated; build retention horizon (oldest run available); test results are available for flaky detection |
| **S3 Components** | Parse `skaffold.yaml` + `.sqlproj` for 3 repos and map them to deploy stages/jobs and versions (image tag/build number) with automatic rules, recording which cases need manual rules |

Output: a short `docs/spikes.md` with findings and any plan changes.

### Phase 1: Core dashboard
- **1a. Foundations:** solution skeleton, compose, config, EF schema, ADO client and throttling, repo picker, hot/warm/daily sync, backfill, freshness UI.
- **1b. Now view:** attention queue, component × environment matrix, running/queued, shipped, "since last looked", tab badge.
- **1c. Health and repo drill-down:** metric calculators (unit-tested against recorded fixtures), `RuleResult` snapshots, tiers, health table, repo tabs (Overview/Pipelines/PRs/Deployments).
- **1d. Templates and gates:** template compliance, Gates page, "waiting for me".

**Exit:** I use it daily for a week. Thresholds calibrated. Every number matches ADO when spot-checked (5 per page).

### Phase 2: Copilot audit
Auditor container; clone manager; scanners; rubric v1; orchestration; findings backlog; repo Audit tab; Findings page; Markdown export.

**Pilot** on 3–5 repos (including the golden 3) before all 30.

**Exit:** findings judged useful (≥70% not dismissed as false positives on the pilot); stable re-runs on an unchanged SHA.

### Phase 3: Extras (prioritise after Phase 2)
- delivery metrics (deploy frequency, lead time split, CFR proxy)
- wasted-minutes ranking and quarantine candidates
- cross-repo failure fingerprints
- template release overlay on charts
- template change summaries
- fleet outliers
- weekly LLM brief from deltas
- browser notifications
- presentation mode

---

## 12. Testing strategy
- **Core metric and rule logic:** pure functions, unit-tested with hand-built scenarios (retries, partial success, red intervals, drafts, bots, working hours).
- **ADO client and sync:** tested against **recorded, sanitised JSON fixtures** captured during S2. No live ADO calls in tests.
- **Scanners:** tested against small fixture repos under `tests/fixtures/repos/*`.
- **Audit:** schema validation tests and an evidence-verification test. Golden-repo calibration is a manual check per rubric or model change.

## 13. Risks
| Risk | Mitigation |
|---|---|
| Copilot SDK API churn (GitHub → Microsoft rename, v2) | Pin versions; one thin wrapper in `Dashboard.Audit` |
| An org policy disables the Copilot CLI/SDK or specific models for my seat | Spike S1 first; choose the model at runtime through `ListModels()` |
| Model retirement breaks audit comparability | Version-aware diffs + re-baseline + golden repos |
| Deploy jobs don't map cleanly to components | Editable `ComponentDeployRule` table; S3 measures how much manual mapping is needed |
| Deployment records need the high-privilege `vso.environment_manage` scope | S2 checks whether the timeline/final YAML are enough |
| PAT expiry (max 1 year) or a policy change | Expiry banner; Entra `az login` token as a later fallback |
| Throttling slows my own ADO browsing | Hot tier only runs while the UI is open; backoff; log TSTU usage |
| Build retention limits history | Our own store from day one; backfill on assign |

## 14. Open items (resolve during Phase 0)
- Exact team environment names (for the tier mapping defaults).
- Bot/service identities to exclude from PR metrics.
- Working hours and time zone for "working days".
- Which Copilot model to prefer by default (e.g. the latest Claude Opus available on the seat).
