[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $OutputDirectory,
    [string] $NimBusVersion,
    [string] $AdapterVersion = '0.1.0-preview.1'
)
$ErrorActionPreference = 'Stop'
$adapterRoot = Split-Path $PSScriptRoot
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $outputRoot) { throw 'Choose a new output directory; existing bundles are never overwritten.' }
New-Item -ItemType Directory -Path $outputRoot | Out-Null
$properties = @("/p:Version=$AdapterVersion")
if ($NimBusVersion) { $properties += @('/p:UsePublishedNimBus=true', "/p:NimBusVersion=$NimBusVersion") }
function Invoke-DotNet([string[]] $Arguments) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($Arguments[0]) failed." }
}
$functionProject = Join-Path $adapterRoot 'src/NimBus.Adapters.Dataverse.Functions/NimBus.Adapters.Dataverse.Functions.csproj'
$testProject = Join-Path $adapterRoot 'tests/NimBus.Adapters.Dataverse.Tests/NimBus.Adapters.Dataverse.Tests.csproj'
Invoke-DotNet (@('build', (Join-Path $adapterRoot 'NimBus.Adapters.Dataverse.sln'), '-c', 'Release', '--verbosity', 'quiet') + $properties)
Invoke-DotNet (@('test', $testProject, '-c', 'Release', '--no-build', '--verbosity', 'quiet') + $properties)
$published = Join-Path $outputRoot 'function'
Invoke-DotNet (@('publish', $functionProject, '-c', 'Release', '--no-build', '-o', $published) + $properties)
$metadata = Get-Content -LiteralPath (Join-Path $published 'functions.metadata') -Raw | ConvertFrom-Json
$binding = ($metadata | Where-Object name -eq 'DataverseIngress').bindings | Where-Object type -eq 'serviceBusTrigger'
if (-not $binding -or $binding.isSessionsEnabled -ne $false -or $binding.autoCompleteMessages -ne $false) { throw 'Invalid generated ingress trigger metadata.' }
Compress-Archive -Path (Join-Path $published '*') -DestinationPath (Join-Path $outputRoot 'function.zip')
Copy-Item -LiteralPath (Join-Path $adapterRoot 'README.md') -Destination $outputRoot
Copy-Item -LiteralPath (Join-Path $adapterRoot 'docs') -Destination $outputRoot -Recurse
$deployOutput = New-Item -ItemType Directory -Path (Join-Path $outputRoot 'deploy')
foreach ($name in @('main.bicep', 'parameters.example.json', 'deploy.ps1')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination $deployOutput.FullName
}
if ($NimBusVersion) {
    foreach ($name in @('NimBus.Adapters.Dataverse.Contracts', 'NimBus.Adapters.Dataverse')) {
        Invoke-DotNet (@('pack', (Join-Path $adapterRoot "src/$name/$name.csproj"), '-c', 'Release', '--no-build', '-o', (Join-Path $outputRoot 'packages')) + $properties)
    }
}
$files = @('function.zip', 'README.md') + @(Get-ChildItem -LiteralPath $deployOutput.FullName -File | ForEach-Object { [IO.Path]::GetRelativePath($outputRoot, $_.FullName) }) + @(Get-ChildItem -LiteralPath (Join-Path $outputRoot 'docs') -File | ForEach-Object { [IO.Path]::GetRelativePath($outputRoot, $_.FullName) })
$manifest = [ordered]@{
    adapterVersion = $AdapterVersion
    nimbusVersion = $(if ($NimBusVersion) { $NimBusVersion } else { 'development-project-references' })
    qualification = 'preview-external-smoke-test-required'
    files = @($files | ForEach-Object { @{ path = $_.Replace('\', '/'); sha256 = (Get-FileHash -LiteralPath (Join-Path $outputRoot $_) -Algorithm SHA256).Hash } })
}
$manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $outputRoot 'manifest.json') -Encoding utf8
Write-Output "Verified preview bundle: $outputRoot"
