#Requires -Version 7.0
<#
.SYNOPSIS
    Audits every NuGet package the repository restores and, optionally, bumps the
    vulnerable ones to the lowest patched version.

.DESCRIPTION
    Scanning uses `dotnet list package --vulnerable --include-transitive`, which reports
    both top-level and transitive packages carrying GitHub Security Advisories.

    Remediation is deliberately minimal: for every advisory the GitHub Advisory API is
    asked which version range is affected and what the first patched version is, so a
    package on 4.5.0 is bumped to 4.5.1 rather than to whatever "latest" happens to be.
    That keeps the automated pull request reviewable and unlikely to break the build.

    Only top-level PackageReference/GlobalPackageReference/PackageVersion items are
    edited. Transitive advisories are reported (with the dependency path from
    `dotnet nuget why`) but never auto-pinned: pinning a transitive package across 60+
    projects is a change a human should make deliberately.

.PARAMETER Fix
    Rewrite the version attributes in place. Without it the script only reports.

.PARAMETER JsonOut
    Path to write the machine-readable findings to.

.PARAMETER MarkdownOut
    Path to write the human-readable report fragment to (used as the PR body).

.EXAMPLE
    pwsh .github/scripts/Invoke-NuGetAudit.ps1
    pwsh .github/scripts/Invoke-NuGetAudit.ps1 -Fix -JsonOut nuget.json -MarkdownOut nuget.md
#>
[CmdletBinding()]
param(
    [string]$RepositoryRoot,
    [switch]$Fix,
    [string]$JsonOut,
    [string]$MarkdownOut
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
# `dotnet list --vulnerable` reports findings through its output, not its exit code, and
# pins the exit code to non-zero in several benign cases. Exit codes are checked where they
# actually mean something instead.
$PSNativeCommandUseErrorActionPreference = $false

if (-not $RepositoryRoot) {
    $RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..' '..')).Path
}
$RepositoryRoot = (Resolve-Path $RepositoryRoot).Path

# ---------------------------------------------------------------------------
# NuGet version comparison
#
# [semver] cannot be used: NuGet allows a fourth numeric field (SonarAnalyzer ships
# 10.19.0.132793), which System.Management.Automation.SemanticVersion rejects.
# ---------------------------------------------------------------------------

function ConvertTo-NuGetVersionInfo {
    param([Parameter(Mandatory)][string]$Version)

    $value = $Version.Trim()
    $plus = $value.IndexOf('+')            # build metadata is ignored for ordering
    if ($plus -ge 0) { $value = $value.Substring(0, $plus) }

    $prerelease = @()
    $dash = $value.IndexOf('-')
    if ($dash -ge 0) {
        $prerelease = $value.Substring($dash + 1).Split('.')
        $value = $value.Substring(0, $dash)
    }

    $numbers = @(0, 0, 0, 0)
    $parts = $value.Split('.')
    for ($i = 0; $i -lt [Math]::Min(4, $parts.Length); $i++) {
        $parsed = 0
        if ([int]::TryParse($parts[$i], [ref]$parsed)) { $numbers[$i] = $parsed }
    }

    [pscustomobject]@{
        Original   = $Version.Trim()
        Numbers    = $numbers
        Prerelease = $prerelease
    }
}

function Compare-NuGetVersion {
    param([Parameter(Mandatory)][string]$Left, [Parameter(Mandatory)][string]$Right)

    $a = ConvertTo-NuGetVersionInfo $Left
    $b = ConvertTo-NuGetVersionInfo $Right

    for ($i = 0; $i -lt 4; $i++) {
        if ($a.Numbers[$i] -ne $b.Numbers[$i]) { return [Math]::Sign($a.Numbers[$i] - $b.Numbers[$i]) }
    }

    # SemVer: a release outranks any prerelease of the same numbers.
    if ($a.Prerelease.Count -eq 0 -and $b.Prerelease.Count -eq 0) { return 0 }
    if ($a.Prerelease.Count -eq 0) { return 1 }
    if ($b.Prerelease.Count -eq 0) { return -1 }

    $max = [Math]::Max($a.Prerelease.Count, $b.Prerelease.Count)
    for ($i = 0; $i -lt $max; $i++) {
        if ($i -ge $a.Prerelease.Count) { return -1 }
        if ($i -ge $b.Prerelease.Count) { return 1 }

        $left = $a.Prerelease[$i]
        $right = $b.Prerelease[$i]
        $leftNum = 0; $rightNum = 0
        $leftIsNum = [int]::TryParse($left, [ref]$leftNum)
        $rightIsNum = [int]::TryParse($right, [ref]$rightNum)

        if ($leftIsNum -and $rightIsNum) {
            if ($leftNum -ne $rightNum) { return [Math]::Sign($leftNum - $rightNum) }
        }
        elseif ($leftIsNum) { return -1 }
        elseif ($rightIsNum) { return 1 }
        else {
            $cmp = [string]::CompareOrdinal($left, $right)
            if ($cmp -ne 0) { return [Math]::Sign($cmp) }
        }
    }
    return 0
}

# Advisory ranges look like ">= 4.0.0, < 4.5.1" or "= 5.0.0"; clauses are ANDed.
function Test-VersionInRange {
    param([Parameter(Mandatory)][string]$Version, [Parameter(Mandatory)][string]$Range)

    foreach ($clause in $Range.Split(',')) {
        $trimmed = $clause.Trim()
        if (-not $trimmed) { continue }

        $match = [regex]::Match($trimmed, '^(?<op>>=|<=|>|<|=)\s*(?<version>.+)$')
        if (-not $match.Success) { return $false }

        $cmp = Compare-NuGetVersion $Version $match.Groups['version'].Value
        $satisfied = switch ($match.Groups['op'].Value) {
            '>=' { $cmp -ge 0 }
            '<=' { $cmp -le 0 }
            '>' { $cmp -gt 0 }
            '<' { $cmp -lt 0 }
            '=' { $cmp -eq 0 }
        }
        if (-not $satisfied) { return $false }
    }
    return $true
}

# ---------------------------------------------------------------------------
# Scanning
# ---------------------------------------------------------------------------

function Get-TrackedFile {
    param([Parameter(Mandatory)][string[]]$Pattern)

    Push-Location $RepositoryRoot
    try {
        $files = & git ls-files @Pattern
        if ($LASTEXITCODE -ne 0) { throw "git ls-files failed for $($Pattern -join ', ')" }
        return @($files | Where-Object { $_ -and $_ -notmatch '(^|/)node_modules/' })
    }
    finally { Pop-Location }
}

function Invoke-VulnerabilityScan {
    param([Parameter(Mandatory)][string]$Target)

    Write-Host "  scanning $Target"
    Push-Location $RepositoryRoot
    try {
        $raw = & dotnet list $Target package --vulnerable --include-transitive --format json --output-version 1 2>&1
    }
    finally { Pop-Location }

    $text = ($raw | ForEach-Object { $_.ToString() }) -join "`n"
    $start = $text.IndexOf('{')
    if ($start -lt 0) {
        Write-Warning "No JSON returned for '$Target'. Output was:`n$text"
        return $null
    }

    try { return $text.Substring($start) | ConvertFrom-Json }
    catch {
        Write-Warning "Could not parse the scan result for '$Target': $($_.Exception.Message)"
        return $null
    }
}

function Get-Property {
    param($InputObject, [Parameter(Mandatory)][string]$Name)

    if ($null -eq $InputObject) { return $null }
    $property = $InputObject.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

# Enumerating an array property has to go through this: `@($null)` is a one-element array
# holding $null, which would iterate once and manufacture a phantom finding for every
# project that reported no vulnerabilities at all.
function Get-PropertyItem {
    param($InputObject, [Parameter(Mandatory)][string]$Name)

    $value = Get-Property $InputObject $Name
    if ($null -eq $value) { return }
    $value
}

$severityRank = @{ 'low' = 1; 'moderate' = 2; 'high' = 3; 'critical' = 4 }

# Scan the solutions first, then any tracked project they do not cover (today that is
# src/NimBus.Manager, but discovering it keeps the audit honest as the tree changes).
$solutions = @(Get-TrackedFile @('*.sln', '*.slnx'))
$projects = @(Get-TrackedFile @('*.csproj'))

Write-Host "Scanning $($solutions.Count) solution(s) and $($projects.Count) project(s) for vulnerable NuGet packages..."

$results = [System.Collections.Generic.List[object]]::new()
$covered = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)

function Register-ScanResult {
    param($ScanResult)

    if (-not $ScanResult) { return }
    foreach ($project in @(Get-PropertyItem $ScanResult 'projects')) {
        $path = Get-Property $project 'path'
        if ($path) { [void]$covered.Add(((Resolve-Path -LiteralPath $path -ErrorAction SilentlyContinue)?.Path ?? $path)) }
        $results.Add($project)
    }
}

foreach ($solution in $solutions) { Register-ScanResult (Invoke-VulnerabilityScan $solution) }

foreach ($project in $projects) {
    $full = (Resolve-Path -LiteralPath (Join-Path $RepositoryRoot $project)).Path
    if ($covered.Contains($full)) { continue }
    Register-ScanResult (Invoke-VulnerabilityScan $project)
}

# Collapse per-project rows into one finding per (package, resolved version).
$findings = [ordered]@{}
foreach ($project in $results) {
    $projectPath = Get-Property $project 'path'
    $relative = if ($projectPath) { [IO.Path]::GetRelativePath($RepositoryRoot, $projectPath).Replace('\', '/') } else { '(unknown)' }

    foreach ($framework in @(Get-PropertyItem $project 'frameworks')) {
        foreach ($scope in @('topLevelPackages', 'transitivePackages')) {
            foreach ($package in @(Get-PropertyItem $framework $scope)) {
                $vulnerabilities = @(Get-PropertyItem $package 'vulnerabilities')
                if ($vulnerabilities.Count -eq 0) { continue }

                $id = Get-Property $package 'id'
                $resolved = Get-Property $package 'resolvedVersion'
                $key = "$id/$resolved"

                if (-not $findings.Contains($key)) {
                    $findings[$key] = [pscustomobject]@{
                        Id              = $id
                        ResolvedVersion = $resolved
                        IsTransitive    = ($scope -eq 'transitivePackages')
                        Severity        = 'Low'
                        Advisories      = [System.Collections.Generic.List[string]]::new()
                        Projects        = [System.Collections.Generic.List[string]]::new()
                        FixedVersion    = $null
                        Status          = 'reported'
                        Detail          = $null
                    }
                }

                $finding = $findings[$key]
                # A package can be top-level in one project and transitive in another;
                # top-level wins because that is the copy we can actually edit.
                if ($scope -eq 'topLevelPackages') { $finding.IsTransitive = $false }
                if (-not $finding.Projects.Contains($relative)) { $finding.Projects.Add($relative) }

                foreach ($vulnerability in $vulnerabilities) {
                    $url = Get-Property $vulnerability 'advisoryurl'
                    if ($url -and -not $finding.Advisories.Contains($url)) { $finding.Advisories.Add($url) }

                    $severity = [string](Get-Property $vulnerability 'severity')
                    if ($severity -and $severityRank[$severity.ToLowerInvariant()] -gt $severityRank[$finding.Severity.ToLowerInvariant()]) {
                        $finding.Severity = $severity
                    }
                }
            }
        }
    }
}

$findings = @($findings.Values)
Write-Host "Found $($findings.Count) vulnerable package version(s)."

# ---------------------------------------------------------------------------
# Remediation
# ---------------------------------------------------------------------------

$advisoryCache = @{}

function Get-Advisory {
    param([Parameter(Mandatory)][string]$Url)

    $id = $Url.TrimEnd('/').Split('/')[-1]
    if ($advisoryCache.ContainsKey($id)) { return $advisoryCache[$id] }

    $headers = @{ 'User-Agent' = 'nimbus-dependency-audit'; 'Accept' = 'application/vnd.github+json' }
    if ($env:GITHUB_TOKEN) { $headers['Authorization'] = "Bearer $env:GITHUB_TOKEN" }

    try {
        $advisory = Invoke-RestMethod -Uri "https://api.github.com/advisories/$id" -Headers $headers -MaximumRetryCount 3 -RetryIntervalSec 5
    }
    catch {
        Write-Warning "Could not read advisory ${id}: $($_.Exception.Message)"
        $advisory = $null
    }

    $advisoryCache[$id] = $advisory
    return $advisory
}

# Lowest version that no advisory for this package still marks as vulnerable.
function Resolve-PatchedVersion {
    param(
        [Parameter(Mandatory)][string]$PackageId,
        [Parameter(Mandatory)][string]$CurrentVersion,
        [Parameter(Mandatory)][string[]]$AdvisoryUrls
    )

    $entries = [System.Collections.Generic.List[object]]::new()
    foreach ($url in $AdvisoryUrls) {
        $advisory = Get-Advisory $url
        if (-not $advisory) { return [pscustomobject]@{ Version = $null; Reason = "advisory $url could not be read" } }

        foreach ($entry in @(Get-PropertyItem $advisory 'vulnerabilities')) {
            $package = Get-Property $entry 'package'
            if ((Get-Property $package 'ecosystem') -ne 'nuget') { continue }
            if ((Get-Property $package 'name') -ne $PackageId) { continue }
            $entries.Add($entry)
        }
    }

    if ($entries.Count -eq 0) {
        return [pscustomobject]@{ Version = $null; Reason = 'no NuGet range found in the advisories' }
    }

    # Walk upward: a patched version can itself fall inside a later advisory's range.
    $candidate = $CurrentVersion
    for ($iteration = 0; $iteration -lt 10; $iteration++) {
        $moved = $false
        foreach ($entry in $entries) {
            $range = Get-Property $entry 'vulnerable_version_range'
            if (-not $range -or -not (Test-VersionInRange $candidate $range)) { continue }

            $patched = Get-Property $entry 'first_patched_version'
            if (-not $patched) {
                return [pscustomobject]@{ Version = $null; Reason = "no fixed version published for range '$range'" }
            }
            if ((Compare-NuGetVersion $patched $candidate) -gt 0) {
                $candidate = $patched
                $moved = $true
            }
        }
        if (-not $moved) { break }
    }

    if ((Compare-NuGetVersion $candidate $CurrentVersion) -le 0) {
        return [pscustomobject]@{ Version = $null; Reason = 'advisory ranges do not cover the resolved version' }
    }
    return [pscustomobject]@{ Version = $candidate; Reason = $null }
}

# Guard against advisories naming a version that was never published.
function Test-PackageVersionPublished {
    param([Parameter(Mandatory)][string]$PackageId, [Parameter(Mandatory)][string]$Version)

    try {
        $index = Invoke-RestMethod -Uri "https://api.nuget.org/v3-flatcontainer/$($PackageId.ToLowerInvariant())/index.json" -MaximumRetryCount 3 -RetryIntervalSec 5
        $published = @(Get-PropertyItem $index 'versions')   # the feed normalises these to lower case
        return $published -contains $Version.ToLowerInvariant()
    }
    catch {
        Write-Warning "Could not list published versions of ${PackageId}: $($_.Exception.Message)"
        return $true   # do not block a legitimate bump on a registry hiccup
    }
}

function Update-PackageVersion {
    param(
        [Parameter(Mandatory)][string]$PackageId,
        [Parameter(Mandatory)][string]$NewVersion,
        [Parameter(Mandatory)][string[]]$Files
    )

    $changed = [System.Collections.Generic.List[string]]::new()
    $pattern = '<(?<tag>PackageReference|GlobalPackageReference|PackageVersion)\b(?<attrs>[^>]*?)(?<close>/?>)'

    foreach ($file in $Files) {
        $path = Join-Path $RepositoryRoot $file
        $content = Get-Content -LiteralPath $path -Raw

        $updated = [regex]::Replace($content, $pattern, {
                param($match)

                $attributes = $match.Groups['attrs'].Value
                $name = [regex]::Match($attributes, '(?i)\b(?:Include|Update)\s*=\s*"(?<value>[^"]+)"')
                if (-not $name.Success -or $name.Groups['value'].Value -ne $PackageId) { return $match.Value }

                $version = [regex]::Match($attributes, '(?i)\bVersion\s*=\s*"(?<value>[^"]+)"')
                if (-not $version.Success) {
                    Write-Warning "$file pins $PackageId without an inline Version attribute; update it by hand."
                    return $match.Value
                }

                $current = $version.Groups['value'].Value
                # Floating ranges ([1.0,2.0), 1.*) are left alone: rewriting them would
                # change the resolution strategy, not just the version.
                if ($current -match '[\[\](),*$]') {
                    Write-Warning "$file pins $PackageId with the range '$current'; update it by hand."
                    return $match.Value
                }
                if ((Compare-NuGetVersion $current $NewVersion) -ge 0) { return $match.Value }

                # Splice the new version into the attribute list so surrounding
                # formatting (attribute order, spacing, extra attributes) survives.
                $offset = $version.Groups['value'].Index
                $newAttributes = $attributes.Remove($offset, $current.Length).Insert($offset, $NewVersion)
                return "<$($match.Groups['tag'].Value)$newAttributes$($match.Groups['close'].Value)"
            })

        if ($updated -ne $content) {
            Set-Content -LiteralPath $path -Value $updated -NoNewline
            $changed.Add($file)
            Write-Host "  updated $file"
        }
    }

    return $changed
}

$editableFiles = @(Get-TrackedFile @('*.csproj', '*.props', '*.targets'))

foreach ($finding in $findings) {
    if ($finding.IsTransitive) {
        $finding.Status = 'manual-transitive'
        $finding.Detail = 'Transitive dependency; bump the package that pulls it in, or add an explicit reference.'
        continue
    }

    $patched = Resolve-PatchedVersion -PackageId $finding.Id -CurrentVersion $finding.ResolvedVersion -AdvisoryUrls @($finding.Advisories)
    if (-not $patched.Version) {
        $finding.Status = 'manual-no-fix'
        $finding.Detail = $patched.Reason
        continue
    }

    if (-not (Test-PackageVersionPublished -PackageId $finding.Id -Version $patched.Version)) {
        $finding.Status = 'manual-no-fix'
        $finding.Detail = "The advisory names $($patched.Version) as the fix, but it is not published on nuget.org."
        continue
    }

    $finding.FixedVersion = $patched.Version
    if (-not $Fix) {
        $finding.Status = 'fixable'
        continue
    }

    $changed = @(Update-PackageVersion -PackageId $finding.Id -NewVersion $patched.Version -Files $editableFiles)
    if ($changed.Count -gt 0) {
        $finding.Status = 'fixed'
        $finding.Detail = "Updated in: $($changed -join ', ')"
    }
    else {
        $finding.Status = 'manual-not-pinned'
        $finding.Detail = "No project pins $($finding.Id) with an editable inline version."
    }
}

# ---------------------------------------------------------------------------
# Reporting
# ---------------------------------------------------------------------------

$fixed = @($findings | Where-Object { $_.Status -eq 'fixed' })
$outstanding = @($findings | Where-Object { $_.Status -like 'manual-*' })

# Show reviewers how a transitive package got in, so the PR is actionable.
foreach ($finding in $outstanding | Where-Object { $_.Status -eq 'manual-transitive' }) {
    $project = $finding.Projects | Select-Object -First 1
    if (-not $project) { continue }
    Push-Location $RepositoryRoot
    try {
        # `dotnet nuget why` highlights the package with ANSI colour codes, which are
        # noise once the output is embedded in a markdown code fence.
        $why = & dotnet nuget why $project $finding.Id 2>&1 |
            Select-Object -First 25 |
            ForEach-Object { [regex]::Replace($_.ToString(), '\x1b\[[0-9;]*m', '').TrimEnd() }
        $finding.Detail = $finding.Detail + "`n`n" + '```text' + "`n" + ($why -join "`n") + "`n" + '```'
    }
    catch { Write-Warning "dotnet nuget why failed for $($finding.Id): $($_.Exception.Message)" }
    finally { Pop-Location }
}

$markdown = [System.Text.StringBuilder]::new()
[void]$markdown.AppendLine('## NuGet')
[void]$markdown.AppendLine()

if ($findings.Count -eq 0) {
    [void]$markdown.AppendLine('No known vulnerabilities in any restored NuGet package.')
}
else {
    [void]$markdown.AppendLine('| Package | Version | Severity | Action | Advisories |')
    [void]$markdown.AppendLine('| --- | --- | --- | --- | --- |')
    foreach ($finding in $findings | Sort-Object @{ Expression = { $severityRank[$_.Severity.ToLowerInvariant()] }; Descending = $true }, Id) {
        $action = switch ($finding.Status) {
            'fixed' { "bumped to **$($finding.FixedVersion)**" }
            'fixable' { "fix available: $($finding.FixedVersion)" }
            'manual-transitive' { 'manual - transitive' }
            'manual-no-fix' { 'manual - no fix published' }
            default { 'manual' }
        }
        $links = ($finding.Advisories | ForEach-Object { "[$($_.TrimEnd('/').Split('/')[-1])]($_)" }) -join '<br>'
        [void]$markdown.AppendLine("| $($finding.Id) | $($finding.ResolvedVersion) | $($finding.Severity) | $action | $links |")
    }

    if ($outstanding.Count -gt 0) {
        [void]$markdown.AppendLine()
        [void]$markdown.AppendLine('### Needs a human')
        foreach ($finding in $outstanding) {
            [void]$markdown.AppendLine()
            [void]$markdown.AppendLine("**$($finding.Id) $($finding.ResolvedVersion)** ($($finding.Severity)) - referenced by: $(($finding.Projects | Select-Object -First 5) -join ', ')")
            [void]$markdown.AppendLine()
            [void]$markdown.AppendLine($finding.Detail)
        }
    }
}

$report = [pscustomobject]@{
    ecosystem   = 'nuget'
    total       = $findings.Count
    fixed       = $fixed.Count
    outstanding = $outstanding.Count
    findings    = $findings
}

if ($JsonOut) { $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $JsonOut }
if ($MarkdownOut) { $markdown.ToString() | Set-Content -LiteralPath $MarkdownOut }

if ($env:GITHUB_OUTPUT) {
    "nuget-total=$($findings.Count)" | Add-Content -LiteralPath $env:GITHUB_OUTPUT
    "nuget-fixed=$($fixed.Count)" | Add-Content -LiteralPath $env:GITHUB_OUTPUT
    "nuget-outstanding=$($outstanding.Count)" | Add-Content -LiteralPath $env:GITHUB_OUTPUT
    # The gate cares about what is still vulnerable after the run, not what was fixed.
    "nuget-max-severity=$(($outstanding | Sort-Object @{ Expression = { $severityRank[$_.Severity.ToLowerInvariant()] } } -Descending | Select-Object -First 1).Severity ?? 'none')" |
        Add-Content -LiteralPath $env:GITHUB_OUTPUT
}

Write-Host $markdown.ToString()
