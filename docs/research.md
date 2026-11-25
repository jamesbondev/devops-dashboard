# DevOps Dashboard — Initial Research (2026-11-25)

Scope: a dashboard for the Azure DevOps repos one team owns, running **only on one developer's machine** in Docker (single user). Copilot usage cost is not a concern.

---

## 1. Existing tools (build vs. adopt)

| Tool | ADO support | Hosting | Run locally by one person? | Verdict |
|---|---|---|---|---|
| **Apache DevLake** | Strong: repos, PRs, commits, pipelines, DORA. **Full refresh only** for ADO | OSS (Apache-2.0), docker compose (~4 containers) + Grafana | Moderate | Best "adopt" option for metrics. No AI audit; Grafana drill-down is clunky |
| **Backstage** + ADO plugins | Catalog discovery, pipelines/PR plugin (stale-ish), Tech Insights scorecards | OSS, it's a framework you build an app from | Hard | Too heavy for one person |
| **Red Hat Developer Hub Local** | ADO plugins in Tech Preview | `docker compose up` packaged Backstage | Easy to start | Developer Preview, not for production use |
| **Port** | Good, with a self-hosted sync agent | SaaS (free tier: 5 seats) | Easy, but data goes to Port's cloud | Only if "local" is a preference, not a rule |
| **Cortex / OpsLevel / Roadie** | Cortex/OpsLevel good; Roadie's is "basic" | SaaS, enterprise pricing | n/a | Not a fit, but good scorecard ideas |
| **Atlassian Compass** | Free Marketplace app | SaaS | n/a | **Avoid**: closed to new customers since May 2026, end of life 31 Dec 2027 |
| **Middleware (OSS DORA)** | **None**, GitHub only | OSS | Easy | Doesn't fit |
| **LinearB / Swarmia / Sleuth** | LinearB ok; Swarmia only Azure Boards (beta); Sleuth unclear | SaaS | n/a | Not a fit |
| **ADO built-in dashboards / Analytics + Power BI** | Native, but **Analytics has no Repos or PR data** | Built in | Nothing to run | No cross-repo health view |
| **DeepWiki-Open / OpenDeepWiki / Repomix / Gitingest** | Local clones only | OSS | Easy | Generate docs or prep LLM input; they don't score repos |

**Gaps nothing fills:** an AI architecture audit of ADO repos; a personal "my N repos, one score each, click to drill down" view with no `catalog-info.yaml` in each repo; one score combining delivery metrics with AI audit findings; cheap incremental re-audits keyed to new commits.

**Ideas worth stealing**
- **Cortex-style rules:**
  - at least 1 required reviewer
  - default branch protected
  - last commit under 30 days old
  - build success rate at least 95%
  - a pipeline exists
  - fewer than 3 PRs in 14 days counts as low activity
- **OpsLevel "file present" checks:** README, CODEOWNERS, Dockerfile, .editorconfig.
- **Maturity tiers** (Bronze/Silver/Gold) instead of a single 0–100 number.
- **Port's pattern:** "scorecards set the rules, agents reason within them". Fixed rules produce the score; the LLM explains it and suggests fixes.
- **DevLake's trick:** tell deployments apart from ordinary builds by a pipeline-name pattern.

---

## 2. Azure DevOps data (REST, api-version 7.1; 7.2-preview where needed)

`{o}`=org, `{p}`=project, `{r}`=repo id. Base `https://dev.azure.com/{o}/{p}/_apis/`.

| Metric | Endpoint | Notes |
|---|---|---|
| Repo list / size / disabled | `git/repositories` | Skip `isDisabled` repos (calls fail); `creationDate` needs 7.2-preview |
| Content search across repos | `POST almsearch.dev.azure.com/{o}/{p}/_apis/search/codesearchresults` | e.g. "which repos have a Dockerfile". `infoCode`≠0 means results are incomplete |
| Pipelines per repo | `build/definitions?repositoryId={r}&repositoryType=TfsGit` | Several YAML definitions per repo is possible (filter not re-verified) |
| Run history / success rate | `build/builds?repositoryId={r}&repositoryType=TfsGit&minTime=…` | Continuation token in the `x-ms-continuationtoken` header; `reason` tells PR builds from CI builds |
| Stage/job durations | `build/builds/{id}/timeline` | |
| Pipeline trends | OData `PipelineRuns`, `PipelineRunActivityResults` (`analytics.dev.azure.com/.../_odata/v4.0-preview`) | Aggregates on the server; needs `vso.analytics` |
| Deployments (DORA) | `distributedtask/environments/{id}/environmentdeploymentrecords` | Needs the **`vso.environment_manage`** scope (a high-privilege manage scope) |
| Classic releases | `vsrm.dev.azure.com/{o}/{p}/_apis/release/deployments` | |
| Lead time for changes | `build/builds/{id}/changes` and the deployment it led to | Commit date → deployment finish |
| CFR / time to restore | Derived from deployment records | ADO has no incident concept |
| PRs | `git/repositories/{r}/pullrequests?searchCriteria.status=all&searchCriteria.minTime=…` | Description truncated to 400 chars; returns only active PRs unless `status=all` |
| Time to first review | `.../pullRequests/{id}/threads` | Earliest non-author comment or vote |
| PR iterations | `.../pullRequests/{id}/iterations` | |
| Policy evaluations | `policy/evaluations?artifactId=vstfs:///CodeReview/CodeReviewId/{projectId}/{prId}` | 7.1-preview.1 |
| Branch policies | `git/policy/configurations?repositoryId=&refName=` | Minimum reviewers, build validation |
| Commits / contributors | `git/repositories/{r}/commits?searchCriteria.fromDate=…` | `$top`/`$skip` paging |
| Branches / stale branches | `.../stats/branches` | Last commit date plus ahead/behind counts |
| Security alerts (GHAS for ADO) | `advsec.dev.azure.com/{o}/{p}/_apis/alert/repositories/{r}/alerts` (7.2-preview.1) | Needs Advanced Security enabled on the repo and read permission |
| File tree | `.../items?scopePath=/&recursionLevel=Full` | One call gets every path |
| Clone for the audit | `git -c http.extraheader="AUTHORIZATION: bearer $TOKEN" clone --depth 1 --filter=blob:none` | Better than the Items API for more than a few files |

**Auth (local, single user)**
- **Org-scoped PAT:** the simplest option.
  - Global (all-orgs) PATs stop working on **27 Jan 2027**.
  - Maximum lifetime is 1 year, and admins may restrict or ban PATs.
  - Read scopes: `vso.code`, `vso.build`, `vso.release`, `vso.analytics`, `vso.project`, `vso.work`, `vso.advsec`, plus `vso.environment_manage` if you want DORA from environments.
- **Entra via `az login`:** `az account get-access-token --resource 499b84ac-1321-427f-aa17-267ca6975798`. Tokens last about an hour.
  - In Docker: `az login --use-device-code` once, with `AZURE_CONFIG_DIR` on a named volume.
  - **Don't** mount a Windows `%USERPROFILE%\.azure`: the cache is encrypted to the Windows user, so the container can't read it.
- **ADO OAuth apps:** retiring in 2026. Don't use them.

**Sync:** poll; service hooks need a reachable endpoint.
- Check the default branch head SHA and only pull commits when it moves.
- Keep a `minTime` cursor for builds.
- Re-fetch active PRs on each sync.
- Keep your own history, because build retention policies delete old runs.

**Rate limits:** 200 TSTUs (a load unit, not a request count) per user per 5-minute window. Throttling first shows up as *slow* 200 responses, then 429s. Honour `Retry-After` and `X-RateLimit-*`.

**SDKs:**
- Node `azure-devops-node-api` 17.0.2: active.
- .NET `Microsoft.TeamFoundationServer.Client`: active.
- Python `azure-devops`: no release since 2023; call REST directly.

---

## 3. Copilot integration

- **Copilot SDK.** GA since 2026-06-02 and being renamed the **Microsoft Copilot SDK** (`@microsoft/copilot-sdk`, `microsoft-copilot-sdk`, `Microsoft.Copilot.SDK`).
  - Languages: TS, Python, Go, .NET, Java, Rust.
  - It drives the Copilot CLI's agent runtime: model choice, reasoning effort, a permission handler that approves or denies each tool call, custom tools, MCP, sub-agents, skills.
  - BYOK for OpenAI, Foundry and Anthropic keys.
  - Works on **any local directory**, so cloned ADO repos are fine.
- **Copilot CLI `copilot -p`** works too: `--allow-tool`/`--deny-tool`, `--output-format json` (a JSONL event stream, so you validate the JSON yourself), `--share` for a transcript.
- **GitHub Models API:** **retired 2026-07-30**. Don't use it.
- **Auth inside Docker:**
  - Preferred: a fine-grained personal PAT with the **"Copilot Requests"** permission, passed as `COPILOT_GITHUB_TOKEN`.
  - Fallback: `copilot login --device-code` once, with `~/.copilot` on a named volume.
  - Classic `ghp_` tokens are not supported.
  - Running everything in the container is preferred, because it sandboxes the agent's shell tools.
- **Policy:**
  - Copilot CLI must be enabled by the org that gives you your seat; assume the SDK follows the same policy.
  - Only models your org enables are available, so use `listModels()` instead of hardcoding names.
  - MCP policy and content exclusions apply.
  - Rate-limit guidance asks you to spread automated requests out, so run audits one repo at a time.
- **ADO MCP server:**
  - Remote server (`https://mcp.dev.azure.com/{org}`): GA since Aug 2026, but needs Entra OAuth.
  - Local server (`npx -y @azure-devops/mcp <org>`) with a PAT: easier in Docker.
  - Better still, fetch metrics with REST yourself; use MCP only when the agent needs to explore.

**Audit design: facts first, then judgement**
1. **Deterministic scanners → `facts.json`:**
   - languages: enry/linguist
   - size: scc/tokei
   - .NET projects: `.sln`/`.slnx`/`.csproj`
   - JavaScript projects: `package.json`/lockfiles
   - ORMs/migrations: EF Core, Dapper, Prisma, Flyway
   - Dockerfiles, pipelines YAML, test projects
   - optional: osv-scanner, gitleaks, semgrep
2. **Copilot SDK agent:**
   - runs in the clone, read-only tools only
   - given the facts
   - confirms the architecture style with evidence (file paths), scores practices, suggests improvements
   - returns its verdict through a `submit_audit` tool whose parameters are a JSON Schema
3. **Store** the schema version, model, commit SHA, facts and verdict. Give suggestions stable IDs so you can diff resolved and new ones over time. Re-audit only when the default branch moves.
4. **Pluggability:** an `AuditEngine` interface with Copilot SDK, BYOK and Copilot CLI implementations.

---

## 4. Decisions so far (from Q&A, 2026-11-25)

| Topic | Decision |
|---|---|
| Platform | Azure DevOps Services (dev.azure.com). About 1000 repos company-wide; **the team owns about 30, all in one project** |
| Stack in the audited repos | Mostly .NET/C#, SQL Server, Terraform, Hiera, Azure Service Bus, Azure infrastructure; some Python and TS/JS |
| App stack | .NET (Microsoft house): ASP.NET Core with background workers, EF Core with **SQLite**, one container |
| ADO auth | Org-scoped PAT with read scopes (plus environment scope for deployments) |
| Source control | **Azure DevOps only.** The only GitHub touchpoint is the Copilot seat sign-in that Copilot itself needs |
| LLM | Copilot SDK (.NET), best available model (Anthropic models enabled), 75k credits a month, so cost isn't a constraint |
| Pipelines | YAML only, for both CI and releases, so deployments come from YAML environments |
| Audit rules | General best practice to start; designed so team standards can be added later |
| Health signals | **Build stability, PR flow, flaky pipelines.** Security is **out of scope** (the security team uses Phoenix, Aikido, Defender) |
| Views | (1) Health overview with drill-down; (2) **"What's happening now"** across the team's services: open PRs, running and pending releases, failed builds |
| Policy | Copilot is the approved tool; sending code to it is fine |
| Environments | Team environments (several, pre-production), then **staging**, then **sim** and **prod**. **Both sim and prod count as production** for delivery metrics. Sim is the customer's own locked-down test environment, used for friends-and-family testing |
| Pipeline templates | An extensive shared template library owned by **another team**. The audit checks whether our repos **use and keep up with the latest templates**; it does not audit the templates themselves |
| Repo ↔ service | **One repo can deploy several components** (e.g. a DB, an API, a QueueListener). The model needs Repo → Components |
| Audience | Built for the user alone, but **assume it may be shown to the team**. No per-person rankings; the screens should hold up on a shared screen |
| Component discovery | **Deterministic, from files.** Everything except the DB deploys to **AKS via `skaffold.yaml`** (artifacts/images, deploy manifests or Helm, profiles, modules). **DBs deploy as dacpacs** (`.sqlproj` plus dacpac deploy tasks in the pipelines). The LLM isn't needed for this |
| Template refs | Pipelines **follow template branches** (no version tags). Compliance therefore means: which branch is referenced, whether the template version used by the last run (`resources.repositories[].version`) is behind the branch HEAD (pipeline not run since the templates changed), `extends` usage, inline stages that bypass the templates, and deprecated templates or parameters |
| Approvals | The team releases to sim/prod themselves. **Automated gates escalate to the user's personal approval** (an ongoing incident, Service Bus dead letters, outside the deployment window). Wanted: (a) a static view of **which pipelines/environments can require the user's approval**, from the environments' checks configuration; (b) the live **"awaiting MY approval"** item at the top of the attention queue; (c) ideally *why* a gate escalated (the check's status) |
| ADO writes | **Read-only.** No work-item creation; use Markdown/clipboard export instead. The PAT has read scopes only |
| Tests / retention | Test results are published to ADO, so test-level flakiness is possible with `vso.test` (read). Retention is believed to be ≥90 days; confirm in the Phase 0 spike and backfill as far back as is available |
| Run result | **Any failed step = failed.** `partiallySucceeded` counts as red |
| Now view contents | The user takes the recommended layout and wants to adjust it once they've used it, so the layout must be easy to change |
| Template branches | Pipelines mostly follow **`main`** (compliant). **`next`** is for experimental changes: report it, but don't count it as a failure (only warn if `next` reached production). Any other ref is non-compliant |
| Cluster access | No AKS read access for upper environments. **Out of scope**: deployed versions come from ADO deployment records only |

---

## 5. Draft proposed design (for review)

**Shape:** one ASP.NET Core container. Blazor UI; EF Core + SQLite on a volume; background services for syncing and audits; .NET Copilot SDK; the image also contains git, the .NET SDK and terraform for scanners. Copilot login runs once with `copilot login --device-code`, persisted to a volume. ADO auth is an org-scoped PAT.

**Repo picker:** search the project's repos through the API and assign them to the dashboard (about 30).

**Sync**
- Incremental polling.
- **"Now" data** comes from project-wide calls filtered to assigned repos, refreshed every 30–60 s:
  - active PRs: `git/pullrequests?searchCriteria.status=active`
  - in-progress and queued builds
  - pending approvals: `pipelines/approvals`
- **History** syncs every 15–60 min:
  - builds and timelines
  - closed PRs and threads
  - environment deployment records
  - commits on the default branch
- Keep our own history beyond ADO's build retention.

**Views**
1. **Now:**
   - open PRs, with age, reviewers being waited on and policy status
   - running and queued builds
   - releases currently deploying or awaiting approval, by environment
   - failures in the last 24 h
   - recent merges
2. **Health overview:** one tile per repo, with its components listed, plus a tier (Bronze/Silver/Gold) and trend arrows.
3. **Repo drill-down:**
   - pipelines and runs
   - PR flow
   - activity
   - deployments per environment
   - latest audit, plus what changed since the previous one

**Health signals**
- **Build stability:**
  - main-branch success rate
  - how often main is broken, and how long it stays broken
  - duration trends
- **PR flow:**
  - time to first review, time to merge
  - PRs open more than N days
  - PR size
- **Flakiness:**
  - same commit fails, then passes when re-run
  - jobs re-run within one build (timeline `attempt` > 1)
  - ADO's flaky-test flag
- **Delivery metrics (bonus):** deploy frequency, lead time and change failure rate for sim and prod.

**Scoring:** deterministic rules decide the tier; the LLM explains failures and suggests fixes but never sets the score.

**Audit pipeline**, re-run when the default branch moves:
1. Shallow-clone the repo.
2. Deterministic scanners write `facts.json`:
   - .NET: project/solution counts, target frameworks (out-of-support ones flagged), central package management, outdated NuGet packages, test projects, analyzers/nullable settings
   - data: EF Core, Dapper, `.sqlproj`, DbUp, Flyway
   - messaging: Service Bus via Azure SDK, MassTransit or NServiceBus
   - Terraform: azurerm/module versions, backend
   - Hiera: layout
   - YAML pipelines: template `extends`/version compared with the latest template version, stages/environments, approvals
3. Copilot SDK agent, with read-only tools, given the facts. It classifies the architecture with file-path evidence, maps Azure service dependencies and the repo's components, and makes suggestions.
4. Results come back through a `submit_audit` tool with a JSON Schema.
5. Stored per commit, with stable suggestion IDs so audits can be compared over time.
6. Team standards are added later as Copilot instruction/skill files.

**Out of scope:** security scanning and alerts (the security team owns them through Phoenix, Aikido and Defender).

---

## 6. Multi-model review synthesis (Opus: architecture, Sonnet: product/UX, Fable: independent design from the requirements only)

Fable designed the system without seeing §5. It **arrived at the same overall shape**: one .NET app, Blazor Server, SQLite in WAL mode, three-tier polling, deterministic facts plus LLM judgement, and no DevLake. The changes below are what the reviews added or corrected.

### 6.1 Corrections to the draft
| Draft said | Change | Source |
|---|---|---|
| Audit agent runs inside the app | **Run it in a separate audit container**, connected over `RuntimeConnection.ForTcp`. It gets no ADO PAT, mounts the clones read-only, has only view/grep/glob tools, and the permission handler rejects paths outside the clone. The SDK's default in-process runtime has no sandbox, so a prompt injection in a repo could read the PAT or the DB | Opus (verified) |
| Run `dotnet list package --outdated` / `terraform init` | **Never run build or restore tooling on audited repos** (MSBuild targets can run arbitrary code, and private feeds need auth). Parse csproj, `Directory.Packages.props` and `.terraform.lock.hcl` statically, then query the NuGet and Terraform registry APIs. This removes the SDK and terraform from the image | Opus |
| `--depth 1 --filter=blob:none` clone | Keep **persistent partial clones** (`--filter=blob:none`, no depth) and update them with `git fetch`, so the agent can see `git diff lastAuditSha..HEAD` | Opus |
| Build success rate based on run result | CI and CD share pipelines, so **stability and flakiness use build stages only** (stages without deployment jobs). A run's result mixes in deploy failures and rejected approvals | Opus |
| Flaky = same commit fails, then passes | Strong signal: a job with `attempt>1` whose final attempt passed (read `previousAttempts`; an in-place retry flips the run green). Weak signal: same pipeline, SHA, template SHA and params go fail→pass. **Exclude PR builds** (their merge commit changes) | Opus, Fable |
| Accumulate metrics incrementally | **Store raw facts and compute metrics as queries.** Stage retries change a run's result and finish time in place. Upsert from a cursor with about 1 h of overlap, then recompute the windows | Opus |
| Parse YAML for template `extends`/version | **Use what actually ran:** `Runs - Get` → `resources.repositories[alias].refName/version` (covers nested templates). Compare with the template repo's highest semver tag. Classes: pinned-current / minor-behind / major-behind / floating / vendored. 14-day grace period after a new tag. Still parse YAML for `extends` usage and inline stages that bypass the templates | Opus, Fable (both independently) |
| "PRs have no updated-since filter" | `queryTimeRangeType=closed` + `minTime` covers closed PRs; active PRs are re-fetched anyway. Time to first review comes from threads (vote-update and comment, human, non-author), starting when a draft is published | Opus |
| ADO flaky-test flag | Needs the `vso.test` scope and flaky detection switched on in project settings, so it is optional | Opus |
| Three `AuditEngine` implementations | One implementation behind an interface | Opus, Sonnet |
| Suggestion IDs come from the LLM | **A fixed, versioned check catalogue** (`ARCH-001`…): the LLM answers pass/partial/fail/n/a with evidence. Finding fingerprint = check id + component + normalised path. **Only compare audits with the same model, prompt and rubric version**; a change means a re-baseline. Uses SDK structured output (`SendAndWaitAsync<T>`, experimental), validated with one retry | Opus, Fable |
| `vso.environment_manage` for deployments | Check whether run timelines plus the final YAML are enough (unverified). Otherwise accept the scope | Opus |

### 6.2 Product/UX changes
- **"Now" = attention queue first**, ranked by what needs acting on:
  - approvals waiting (sim and prod flagged)
  - main branch red, with "red for X h" and who touched it last
  - stuck PRs (no review for more than 1 working day, a red policy, a conflict)
  - releases stuck at more than their usual duration
  - failed PR validations

  Raw lists sit below. Everything deep-links to ADO; ADO's UI is not rebuilt.
- **Component × environment matrix:** components down the side, team envs/staging/sim/prod across, with version and age in each cell and drift highlighted. It answers "what's in prod, and what's waiting?"
- **Health overview = a sortable table, worst first**, with a "changed this week" filter and a tile toggle. Columns: tier, main red now, main%, flaky, PR p85, last prod deploy, audit age, trend.
- **The three signals stay separate per repo**, with a tier as the summary. Tiers are cumulative gates that show the unmet rules ("Silver, 1 rule from Gold"). Thresholds live in config, calibrated from the current distribution. Gold can require that the last audit has no open high-severity findings, but only deterministic facts ever change a tier.
- **Minimum sample** (≥10 builds or ≥5 PRs in 30 days) before any red/green; below it the cell shows "insufficient data". **Repo types** (service / library / infra) with their own rule sets, and a "dormant" label instead of failing quiet repos.
- **Avoid vanity metrics:** no per-person rankings, no commit/LOC counts. PR size is context, not a score. Use medians/p85, not means. Exclude drafts and bot PRs.
- **Audit findings become a tracked backlog:** New / Accepted / Dismissed (with a reason) / Done (detected automatically when the finding disappears). Optionally create an ADO work item. Dismissals feed back as suppressions. **Fleet themes** group findings ("9 repos not on the latest template").
- **Trust:** "synced X ago" for each source, PAT-expired or throttled banners, a formula popover for each metric, and pin/mute per repo.
- **Rituals:**
  - standup preset with a large-font mode
  - "shipped this sprint"
  - weekly digest exported as Markdown
  - "since you last looked" cursor
  - tab-title badge and optional browser notifications

### 6.3 New ideas worth adopting
1. **Flakiness as cost:** rank pipelines by wasted minutes (reruns × duration) and list quarantine candidates. *(Sonnet, Fable)*
2. **Cross-repo failure fingerprints:** hash the normalised first error of each failed task. The same fingerprint across repos points to a platform, template or agent problem, and the tool drafts an evidence note for the template team. *(Fable)*
3. **Template release overlay** on stability charts. *(Fable)*
4. **Fleet-relative audit:** "26/30 repos use central package management; this one doesn't". Conventions are inferred from the facts table, with the LLM explaining the outliers. *(Fable)*
5. **Stuck-change lens:** merged but not yet in prod, sim/prod version skew, and lead time split into review wait, pipeline time and approval wait. *(Fable, Opus)*
6. **Golden-repo calibration:** hand-label 3 repos, and any rubric or model change must reproduce those labels before a fleet re-baseline. Run judgement checks 3× and take the majority; disagreement shows as "uncertain". *(Fable)*
7. **Dismissals → proposed standards:** the LLM proposes rubric rules from accept/dismiss patterns. *(Fable)*
8. **Weekly LLM brief from deltas only:** about 10 lines, each linked. *(Fable)*
9. **Template upgrade dry run** via a pipeline preview with an overridden ref (needs queue permission, so opt-in and later). LLM-written upgrade notes from the template changes since each repo's pinned version. *(Fable)*

### 6.4 Revised phasing (proposal)
- **Phase 0, spikes:**
  - Copilot SDK login in a container plus one structured audit on one repo
  - mapping deploy jobs to components on real pipelines
  - `Runs - Get` template resources
- **Phase 1, core dashboard:**
  - repo picker
  - three-tier sync with raw storage and our own history
  - Now view (attention queue plus component × environment matrix)
  - health table with deterministic tiers
  - repo drill-down
  - template compliance
  - freshness/trust UI
- **Phase 2, audit:**
  - audit container
  - fact scanners
  - check catalogue
  - Copilot judgement
  - findings backlog
  - pilot on 3–5 repos before all 30
- **Phase 3:**
  - delivery metrics (sim+prod)
  - flakiness-as-cost
  - failure fingerprints
  - fleet themes
  - weekly brief
  - digest/notifications
  - work-item creation

## Sources
- DevLake ADO plugin: https://devlake.apache.org/docs/Plugins/azuredevops
- Backstage ADO plugin: https://npmjs.com/package/@backstage-community/plugin-azure-devops
- RHDH Local: https://developers.redhat.com/blog/2025/03/31/rhdh-local-test-develop-locally-red-hat-developer-hub-using-containers
- Port ADO: https://docs.port.io/build-your-software-catalog/sync-data-to-catalog/git/azure-devops/installation
- Cortex ADO: https://docs.cortex.io/ingesting-data-into-cortex/integrations/azuredevops
- Compass EOL: https://www.cortex.io/post/atlassian-compass-end-of-life-what-to-do-next
- ADO Analytics data available: https://learn.microsoft.com/en-us/azure/devops/report/powerbi/data-available-in-analytics
- ADO REST (repos): https://learn.microsoft.com/en-us/rest/api/azure/devops/git/repositories/list?view=azure-devops-rest-7.2
- ADO REST (PRs): https://learn.microsoft.com/en-us/rest/api/azure/devops/git/pull-requests/get-pull-requests?view=azure-devops-rest-7.1
- ADO REST (builds): https://learn.microsoft.com/en-us/rest/api/azure/devops/build/builds/list?view=azure-devops-rest-7.1
- ADO environment deployments: https://learn.microsoft.com/en-us/rest/api/azure/devops/distributedtask/environmentdeployment-records/list?view=azure-devops-rest-7.1
- ADO Advanced Security alerts: https://learn.microsoft.com/en-us/rest/api/azure/devops/advancedsecurity/alerts/list
- ADO rate limits: https://learn.microsoft.com/en-us/azure/devops/integrate/concepts/rate-limits
- ADO Entra tokens via CLI: https://learn.microsoft.com/en-us/azure/devops/cli/entra-tokens
- Global PAT retirement: https://devblogs.microsoft.com/devops/retirement-of-global-personal-access-tokens-in-azure-devops
- Copilot SDK: https://github.com/github/copilot-sdk
- Copilot SDK GA: https://github.blog/changelog/2026-06-02-copilot-sdk-is-now-generally-available/
- Copilot SDK rename: https://github.blog/changelog/2026-10-28-github-copilot-sdk-is-becoming-microsoft-copilot-sdk
- Copilot CLI programmatic: https://docs.github.com/en/copilot/reference/copilot-cli-reference/cli-programmatic-reference
- Copilot CLI auth: https://docs.github.com/en/copilot/how-tos/copilot-cli/set-up-copilot-cli/authenticate-copilot-cli
- GitHub Models retired: https://github.blog/changelog/2026-07-30-github-models-is-now-retired/
- ADO remote MCP GA: https://devblogs.microsoft.com/devops/azure-devops-remote-mcp-server-ga/
- ADO local MCP: https://github.com/microsoft/azure-devops-mcp
