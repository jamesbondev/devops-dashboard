# DevOps Dashboard

A personal dashboard for the Azure DevOps repos my team owns. It runs locally only, with one user, and is launched with docker compose.

- **Now:**
  - an attention queue: approvals waiting for me, main branch red, failed Production deploys, slow releases, stuck PRs, failed PR validations
  - a component × environment matrix
  - running builds
  - what shipped
- **Health:**
  - per-repo build stability, PR flow and flakiness
  - cumulative Bronze/Silver/Gold tiers with explicit gaps
  - drill-down into pipelines, PRs, deployments, template compliance and audit
- **Gates:** which environments can escalate to my approval, what is waiting now, and escalation history.
- **Audit:**
  - static facts about each repo: .NET projects, frameworks, EF/Dapper/sqlproj, Service Bus, skaffold/k8s/Docker, Terraform, Hiera, pipelines
  - GitHub Copilot judgement against a versioned rubric
  - findings tracked as a backlog (New, Accepted, Dismissed, Done)
- **Insights / Weekly:**
  - the same failure across repos (likely platform or templates)
  - flakiness cost and quarantine candidates
  - fleet outliers
  - template release impact
  - weekly digest, plus a Copilot-written brief

Design docs: [`docs/research.md`](docs/research.md) (research and decisions), [`docs/plan.md`](docs/plan.md) (implementation plan).

---

## Quick start (mock data, no credentials)

```bash
cp .env.example .env          # defaults: ADO_MODE=mock, AUDITOR_ENGINE=mock
docker compose up -d --build
# open http://localhost:8080
```

Mock mode serves a deterministic synthetic organisation with about 40 repos, 30 of them pre-assigned. It includes PRs, CI/CD runs with retries and flaky jobs, approvals (some escalated to you), template branches and policies, so every page has data. History grows in real time.

## Going live (Azure DevOps)

1. **Create an org-scoped PAT** (not "All accessible organizations"; those stop working on 27 Jan 2027) with these **read** scopes:
   - Code (Read)
   - Build (Read)
   - Project and Team (Read)
   - Test Management (Read)
   - Identity (Read)
   - Graph (Read)

   `vso.environment_manage` is *not* needed: deployments are derived from pipeline stages.
2. **Edit `.env`:** set `ADO_MODE=live`, `ADO_ORGANIZATION`, `ADO_PROJECT` and `ADO_PAT`, and a long random `AUDITOR_TOKEN`.
3. **Edit `config/dashboard.yaml`:**
   - `environments[].stagePatterns`: regexes matched against your **stage names** for team, staging, sim and prod. Sim and prod are both Production tier.
   - `environments[].adoId` (optional): the ADO environment id for sim/prod, used to read their check configurations (the "gates that can escalate to me" list). It's in the environment's URL: `.../_environments/<id>`.
   - `templates.project` / `templates.repository`: the shared pipeline templates repo.
   - `botIdentities`, `time.timeZone`, and the working hours.
4. **Restart:** `docker compose up -d`. Switching mock ↔ live needs a restart. Mock data stays in the database, so for a clean start run `docker compose down -v` first. **This deletes the volumes.**
5. **Assign repos:** open **Settings**, click **Check PAT scopes**, then assign your repos (search the project and tick them). Each assigned repo is backfilled: PRs for 180 days, builds for 90 days, in budgeted passes so the TSTU rate limit isn't hit.

## Copilot auditor

The auditor is a separate container. It has **no ADO PAT**, only a **read-only** view of the cloned worktrees, read-only file tools (`view`, `glob`, `grep`) and a path guard.

```bash
docker compose run --rm auditor login   # device-code login; stored in the copilot-auth volume
# then in .env: AUDITOR_ENGINE=copilot
docker compose up -d
```

Alternatively, set `COPILOT_GITHUB_TOKEN` to a fine-grained GitHub PAT with the "Copilot Requests" permission. Classic `ghp_` tokens are not supported. Don't put `GH_TOKEN`/`GITHUB_TOKEN` in the auditor's environment.

**Settings → Check auditor** shows the engine, auth status and available models. Choose the model in `config/dashboard.yaml` → `audit.model` (it falls back to the strongest available). Set `audit.votes: 3` for majority voting on judgement checks.

**Corporate proxy / `certificate verify failed: unable to get local issuer certificate`:** your network re-signs HTTPS
with a company root CA that the Linux containers don't trust. Export that root CA (Windows: `certmgr.msc` → Trusted Root
Certification Authorities → your company's root → Export, Base-64 .cer) into `./certs/`, then `docker compose up -d`.
Both containers add it to their trust store at start-up (see `certs/README.md`).

If the auditor is unreachable, audits still run **facts-only**: static checks plus component discovery. They re-run with Copilot once it's available.

## Configuration notes

- **Rules and thresholds** (`rules:` in `config/dashboard.yaml`):
  - Insufficient data never passes; a tier blocked only by insufficient data is marked provisional (`*`).
  - Calibrate the thresholds after the first full sync.
- **Components:**
  - Discovered from `skaffold.yaml` artifacts and `.sqlproj` files.
  - Add, hide or rename them with `components.overrides`.
  - Map generic deploy job names with `components.deployRules`.
  - Then use **Settings → Re-derive deployments**.
- **Team standards:** add `audit/rubric/team/*.yaml` (same schema as `audit/rubric/v1/checks.yaml`) and `audit/rubric/team/*.md` (extra guidance for Copilot). A rubric change starts a new **baseline**, so findings aren't auto-resolved just because the model or rubric changed.
- **Reloading:** edit the config and use **Settings → Reload config**. An invalid config is rejected and the last valid one is kept.

## Data and backups

- **Storage:** SQLite in the `data` volume, with WAL mode.
- **Automatic backups:** before every schema migration the app takes a `VACUUM INTO` backup to `/data/backups` and keeps the last 7. **Settings → Back up database now** does it on demand.
- **Raw data:** run and PR JSON is kept (gzip), so metrics can be re-derived if a parser is fixed after ADO retention has deleted the original.
- **Clones:** partial clones and per-audit worktrees live in the `clones` volume. Nothing from an audited repo is ever built or executed: no restore, no `terraform init`, no hooks, no LFS.

## Running without Docker (development)

```bash
ASPNETCORE_URLS=http://127.0.0.1:5081 AUDITOR_ALLOWED_ROOT=$PWD/data/clones dotnet run --project src/Dashboard.Auditor
DATA_DIR=$PWD/data CLONES_PATH=$PWD/data/clones AUDITOR_URL=http://127.0.0.1:5081 dotnet run --project src/Dashboard.Web
dotnet test
```

## Troubleshooting

| Symptom | Where to look |
|---|---|
| Red banner "PAT rejected" | PAT expired, revoked or for another org. Update `.env`, then `docker compose up -d` |
| Data looks stale | Freshness indicator (top right); **Jobs** page shows each source's last success/error and TSTU usage |
| "Throttled" banner | ADO is delaying requests; the sync backs off on its own. Lower `sync.hotSeconds`/budgets if it persists |
| No deployments / empty matrix | `environments[].stagePatterns` don't match your stage names. Check a run's stages on the repo's Pipelines tab |
| Approvals not shown as "mine" | Approval assigned to a group: needs the Identity (Read) scope for group membership |
| Audit failed | Repo → Audit tab shows the error; **Settings → Check auditor**; `docker compose logs auditor` |
