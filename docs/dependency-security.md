# Dependency security audit

The [`Dependency security audit`](../.github/workflows/dependency-security.yml) workflow runs
daily at 04:17 UTC (and on demand via **Actions → Dependency security audit → Run workflow**).
It scans every third-party package the repository pulls in, applies the version bumps that
clear published advisories, and opens a pull request with the result. Nothing is ever pushed
to `master`.

## What it covers

| Ecosystem | Scanner | Scope |
| --- | --- | --- |
| NuGet | `dotnet list package --vulnerable --include-transitive` | Every tracked solution, plus any tracked `.csproj` no solution covers (today `src/NimBus.Manager`). Top-level and transitive packages. |
| npm | `npm audit --package-lock-only` | Every tracked `package-lock.json`: `src/NimBus.WebApp/ClientApp`, `samples/CrmErpDemo/{Crm.Web,Erp.Web,e2e}`. |

Targets are discovered from `git ls-files` on every run, so a new project or SPA is picked up
without editing the workflow.

`src/NimBus.WebApp/ClientApp/src/api-client` has no lockfile and is therefore not audited; it is
a generated client whose dependencies are not installed by any build.

## How it fixes things

**NuGet.** For each advisory on a vulnerable package the workflow asks the GitHub Advisory API
which version range is affected and what the first patched version is, then bumps to the lowest
version no advisory still flags — `4.5.0` becomes `4.5.1`, not "whatever is latest". The
`Version` attribute is rewritten in place in every `.csproj`/`.props`/`.targets` that pins the
package, so surrounding formatting survives.

Left alone deliberately, and reported under **Needs a human** in the PR instead:

- **Transitive packages.** The report includes the `dotnet nuget why` dependency path. Usually
  bumping the top-level parent is enough (and often already happened in the same run); pinning a
  transitive package directly across 60+ projects is a decision for a person.
- **Floating versions** (`[1.0,2.0)`, `1.*`) — rewriting one changes the resolution strategy,
  not just the version.
- **Advisories with no published fix.**

**npm.** `npm audit fix --package-lock-only` — no install, no postinstall scripts (which also
keeps the Playwright suite from downloading browsers). Without `--force` it stays inside the
semver range declared in `package.json`, so anything needing a major bump is reported rather
than applied.

## The pull request

Fixes land on the bot-owned branch `deps/security-audit`, which is **rebuilt from `master` and
force-pushed on every run** — the same model Dependabot uses. Do not commit to it by hand; if
you need to change something, branch off it. An already-open PR is updated in place rather than
duplicated.

Before the PR is opened or refreshed, the workflow builds every solution in `Release` (the
configuration CI uses, where `TreatWarningsAsErrors` is on) and re-runs the NuGet scan so the
attached report describes the tree as it would merge. If that build fails the PR is still
opened, with a warning at the top of the body — a broken bump is more useful visible than
silently dropped.

The run itself fails when a vulnerability at or above **high** survives the audit, so the daily
red X means "something needs a person", not "something was found". The threshold is a
`workflow_dispatch` input (`none`/`low`/`moderate`/`high`/`critical`).

## Repository setup

Two things are worth checking if the workflow cannot open a PR:

1. **Settings → Actions → General → Workflow permissions**: *Allow GitHub Actions to create and
   approve pull requests* must be enabled for the default `GITHUB_TOKEN` to work.
2. **Optional `DEPS_PR_TOKEN` secret.** A PR opened with `GITHUB_TOKEN` does not trigger other
   workflows, so the `.NET` CI will not run on it. Setting `DEPS_PR_TOKEN` to a PAT or GitHub App
   token with `contents:write` + `pull_requests:write` gives the generated PR full CI. Without
   it, closing and reopening the PR once also starts CI.

## Running it locally

Both scripts work on Windows and Linux under PowerShell 7 and are safe to run without `-Fix`:

```powershell
pwsh .github/scripts/Invoke-NuGetAudit.ps1                    # report only
pwsh .github/scripts/Invoke-NuGetAudit.ps1 -Fix               # rewrite versions in place
pwsh .github/scripts/Invoke-NpmAudit.ps1 -Fix -MarkdownOut npm.md
```

Set `GITHUB_TOKEN` before running the NuGet script to avoid the unauthenticated GitHub Advisory
API rate limit (60 requests/hour); `gh auth token` produces a usable value.

## Relationship to the build-time audit

`Directory.Build.props` sets `NuGetAudit`/`NuGetAuditMode=all`/`NuGetAuditLevel=moderate`, which
raises NU1901-NU1904 at restore time. That gate only fires when someone triggers a build and
only covers packages restored by that build. This workflow runs on a clock, so an advisory
published against a dependency nobody touched is still caught, and it covers npm as well.
