[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $ResourceGroup,
    [Parameter(Mandatory)][string] $ParametersFile
)
$ErrorActionPreference = 'Stop'
$bundleRoot = Split-Path $PSScriptRoot
$manifest = Get-Content -LiteralPath (Join-Path $bundleRoot 'manifest.json') -Raw | ConvertFrom-Json
foreach ($file in $manifest.files) {
    $candidate = [IO.Path]::GetFullPath((Join-Path $bundleRoot $file.path))
    $boundary = [IO.Path]::GetFullPath($bundleRoot) + [IO.Path]::DirectorySeparatorChar
    if (-not $candidate.StartsWith($boundary, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid bundle file path.' }
    if ((Get-FileHash -LiteralPath $candidate -Algorithm SHA256).Hash -ne $file.sha256) { throw "Artifact hash mismatch: $($file.path)" }
}
$parameters = Get-Content -LiteralPath $ParametersFile -Raw | ConvertFrom-Json
if ($parameters.parameters.organizationId.value -eq '00000000-0000-0000-0000-000000000000') { throw 'Set the actual Dataverse organization ID.' }
$appName = $parameters.parameters.functionName.value
if ([string]::IsNullOrWhiteSpace($appName)) { throw 'functionName must be supplied in the parameter file.' }
az deployment group create --resource-group $ResourceGroup --template-file (Join-Path $PSScriptRoot 'main.bicep') --parameters "@$ParametersFile" --output none
if ($LASTEXITCODE -ne 0) { throw 'Infrastructure deployment failed.' }
az functionapp deployment source config-zip --resource-group $ResourceGroup --name $appName --src (Join-Path $bundleRoot 'function.zip') --output none
if ($LASTEXITCODE -ne 0) { throw 'Function code deployment failed.' }
Write-Output 'Deployment completed. Verify trigger enablement and run the documented smoke test.'
