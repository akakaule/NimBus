#Requires -Version 7.0
<#
.SYNOPSIS
    Resets the Dynamics 365 Sales and Business Central demo to go-live morning and gets it ready to
    present.

.DESCRIPTION
    Restarts the demo's Aspire AppHost. SQL Server runs without a persistent volume and the NimBus
    Service Bus emulator keeps its state in memory, so the restart clears every database, message,
    scheduled retry, blocked session, circuit state and alert. The demo starts again on go-live
    morning: CRM holds the leads, one prospect and the warm-up account, and none of Business
    Central's customers yet.

    Then it does the talk track's prep:
      - the warm-up on the throwaway Wingtip Marine (warm-up) records: two credit checks, a change
        to OPP-10099 that must reach Business Central (put back afterwards), and a Business Central
        quote on OPP-10099 that must reach Sales Hub;
      - switches on the nimbus-ops heartbeat schedule and sends the first heartbeat.

    A real Service Bus namespace (NIMBUS_SB_EMULATOR=false) keeps its messages, scheduled retries and
    session state across restarts, so the script stops there: purge the demo's subscriptions in
    nimbus-ops as the README describes, or use a namespace of its own, then run it with
    -NamespacePurged.

.PARAMETER NoBuild
    Start without building. Use it when nothing changed since the last run; it is faster.

.PARAMETER RestartOnly
    Only restart the stack; skip the warm-up and the heartbeat schedule.

.PARAMETER Open
    Open the presenter's pages in the default browser afterwards, in the talk track's order.

.PARAMETER NamespacePurged
    With a real Service Bus namespace, confirms that its demo subscriptions were purged (or that the
    namespace is new), so a restart does reset the demo.

.EXAMPLE
    pwsh samples/DynamicsBcDemo/Reset-Demo.ps1

.EXAMPLE
    pwsh samples/DynamicsBcDemo/Reset-Demo.ps1 -NoBuild -Open
#>
[CmdletBinding()]
param(
    [switch]$NoBuild,
    [switch]$RestartOnly,
    [switch]$Open,
    [switch]$NamespacePurged
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
# aspire reports "nothing to stop" through its exit code; the script checks exit codes itself.
$PSNativeCommandUseErrorActionPreference = $false

$appHost = (Resolve-Path (Join-Path $PSScriptRoot 'DynamicsBcDemo.AppHost' 'DynamicsBcDemo.AppHost.csproj')).Path
$d365 = 'http://localhost:5280'
$bc = 'http://localhost:5290'
$ops = 'https://localhost:18543'
$resources = 'd365-api', 'bc-api', 'd365-adapter', 'bc-adapter', 'nimbus-ops', 'd365-web', 'bc-web'

# Seed ids from DynamicsBcDemo.Contracts/Demo/SeedData.cs.
$alexRivera = '5e11e700-0000-4000-8000-000000000001'
$warmUpAccount = 'acc00000-0000-4000-8000-000000000199'
$warmUpOpportunity = '0bb00000-0000-4000-8000-000000000199'

function Write-Step([string]$Text) {
    Write-Host "==> $Text" -ForegroundColor Cyan
}

function Invoke-Demo {
    param([string]$Method, [string]$Uri, $Body)

    $request = @{ Method = $Method; Uri = $Uri; TimeoutSec = 60 }
    if ($null -ne $Body) {
        $request.Body = $Body | ConvertTo-Json -Depth 5
        $request.ContentType = 'application/json'
    }
    if ($Uri.StartsWith($ops)) {
        # nimbus-ops uses the ASP.NET Core development certificate.
        $request.SkipCertificateCheck = $true
        $request.Headers = @{ 'api-version' = '2' }
    }
    Invoke-RestMethod @request
}

function Wait-Until {
    param([string]$What, [scriptblock]$Condition, [int]$TimeoutSeconds = 120)

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $lastError = $null
    while ($true) {
        try {
            if (& $Condition) { return }
        }
        catch {
            $lastError = $_.Exception.Message
        }
        if ((Get-Date) -gt $deadline) {
            $detail = if ($lastError) { " Last error: $lastError" } else { '' }
            throw "Timed out after $TimeoutSeconds s waiting for $What.$detail"
        }
        Start-Sleep -Seconds 1
    }
}

function Get-BcOpportunity {
    # Invoke-RestMethod passes a JSON array down the pipeline as one object, so loop instead of
    # piping it into Where-Object.
    $opportunities = Invoke-Demo GET "$bc/api/app/crm-opportunities"
    foreach ($opportunity in $opportunities) {
        if ($opportunity.id -eq $warmUpOpportunity) { return $opportunity }
    }
}

function Set-WarmUpEstimatedValue($Opportunity, [decimal]$Value) {
    Invoke-Demo PUT "$d365/api/app/opportunities/$warmUpOpportunity" @{
        name               = $Opportunity.name
        estimatedCloseDate = $Opportunity.estimatedCloseDate
        closeProbability   = $Opportunity.closeProbability
        stepName           = $Opportunity.stepName
        estimatedValue     = $Value
        productGroupId     = $Opportunity.csProductGroupId
    } | Out-Null
    Wait-Until "OPP-10099 at $Value in Business Central" {
        $inBc = Get-BcOpportunity
        $inBc -and [decimal]$inBc.estimatedValue -eq $Value
    } -TimeoutSeconds 90
}

if ($env:NIMBUS_SB_EMULATOR -eq 'false' -and -not $NamespacePurged) {
    throw 'NIMBUS_SB_EMULATOR=false: a real Service Bus namespace keeps its messages, scheduled retries and session state across restarts, so a restart does not reset the demo. Purge the demo''s subscriptions in nimbus-ops (Admin → Subscriptions) as the README describes, or use a namespace of its own, then run this again with -NamespacePurged.'
}
if (-not (Get-Command aspire -ErrorAction SilentlyContinue)) {
    throw 'The Aspire CLI (aspire) is not on the PATH. Install it from https://aka.ms/aspire/cli.'
}

$clock = [Diagnostics.Stopwatch]::StartNew()

Write-Step 'Stopping the demo'
& aspire stop --apphost $appHost --non-interactive --nologo | Out-Null
if ($LASTEXITCODE -ne 0) {
    Write-Host '    It was not running.'
}

Write-Step ($NoBuild ? 'Starting it again without building' : 'Building and starting it again')
$startArguments = @('start', '--apphost', $appHost, '--non-interactive', '--nologo')
if ($NoBuild) { $startArguments += '--no-build' }
& aspire @startArguments | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "aspire start failed (exit code $LASTEXITCODE). Run it by hand to see why: aspire start --apphost `"$appHost`""
}

Write-Step 'Waiting for every service'
foreach ($resource in $resources) {
    & aspire wait $resource --status up --timeout 600 --apphost $appHost --non-interactive --nologo | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "$resource did not come up. Check it in the Aspire dashboard."
    }
}

Write-Step 'Waiting for both systems to seed their data'
Wait-Until 'Sales Hub to seed' { (Invoke-Demo GET "$d365/api/app/opportunities/$warmUpOpportunity").opportunity } -TimeoutSeconds 180
Wait-Until 'Business Central to seed' { Get-BcOpportunity } -TimeoutSeconds 180

if (-not $RestartOnly) {
    Write-Step 'Warm-up: credit check on Wingtip Marine (warm-up)'
    foreach ($attempt in 1..2) {
        $timer = [Diagnostics.Stopwatch]::StartNew()
        # The first request after a start warms up the request/reply path and can take a while.
        Wait-Until 'a credit check answer' {
            (Invoke-Demo POST "$d365/api/app/accounts/$warmUpAccount/credit-check" @{ userId = $alexRivera }).status
        } -TimeoutSeconds 180
        Write-Host ('    Answered in {0:N1} s' -f $timer.Elapsed.TotalSeconds)
    }

    Write-Step 'Warm-up: a change to OPP-10099 reaches Business Central'
    $opportunity = (Invoke-Demo GET "$d365/api/app/opportunities/$warmUpOpportunity").opportunity
    $estimatedValue = [decimal]$opportunity.estimatedValue
    Set-WarmUpEstimatedValue $opportunity ($estimatedValue + 1)
    Set-WarmUpEstimatedValue $opportunity $estimatedValue

    Write-Step 'Warm-up: a Business Central quote on OPP-10099 reaches Sales Hub'
    $quote = Invoke-Demo POST "$bc/api/app/quotes" @{ crmOpportunityId = $warmUpOpportunity }
    Invoke-Demo PUT "$bc/api/app/quotes/$($quote.id)/lines" @{
        lines = @(@{ itemNumber = 'CONN-WM8'; quantity = 1; unitPrice = 2450; discountPercent = 0 })
    } | Out-Null
    Wait-Until "quote $($quote.number) in Sales Hub" {
        (Invoke-Demo GET "$d365/api/app/opportunities/$warmUpOpportunity").opportunity.csBcQuoteNumber -eq $quote.number
    } -TimeoutSeconds 90
    Write-Host "    $($quote.number) shows on OPP-10099"

    Write-Step 'Switching on the nimbus-ops heartbeat schedule'
    $heartbeat = Invoke-Demo GET "$ops/api/admin/heartbeat/settings"
    Invoke-Demo PUT "$ops/api/admin/heartbeat/settings" @{
        enabled         = $true
        intervalSeconds = $heartbeat.intervalSeconds
        timeoutSeconds  = $heartbeat.timeoutSeconds
    } | Out-Null
    Invoke-Demo POST "$ops/api/admin/heartbeat/send" | Out-Null
}

$dashboard = (& aspire ps --format Json --nologo | ConvertFrom-Json) |
    Where-Object { $_.appHostPath -eq $appHost } |
    Select-Object -First 1 -ExpandProperty dashboardUrl

Write-Host ''
Write-Host ('Reset to go-live morning in {0:m\:ss}.' -f $clock.Elapsed) -ForegroundColor Green
Write-Host '  Sales Hub            http://localhost:5283'
Write-Host '  Business Central     http://localhost:5293'
Write-Host '  Demo cockpit         http://localhost:5293/demo'
Write-Host '  #integration-alerts  http://localhost:5293/demo/alerts'
Write-Host '  nimbus-ops           https://localhost:18543'
if ($dashboard) {
    Write-Host "  Aspire dashboard     $dashboard"
}
if ($RestartOnly) {
    Write-Host 'Warm-up and heartbeat schedule skipped: do them by hand (talk track, before the meeting).'
}
else {
    Write-Host 'Still to do by hand: in Sales Hub, set Signed in as to Alex Rivera.'
}

if ($Open) {
    # The talk track's order: the shared screen's tabs, then the presenter's cockpit.
    'http://localhost:5283/accounts', 'http://localhost:5293/', "$ops/Endpoints",
    'http://localhost:5293/demo/alerts', 'http://localhost:5293/demo' | ForEach-Object { Start-Process $_ }
}
