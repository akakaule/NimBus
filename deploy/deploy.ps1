Param (
	[Parameter(Mandatory=$True)]
    [string]$solutionId,
    [Parameter(Mandatory=$True)]
    [string]$environment,
	[Parameter(Mandatory=$True)]
    [string]$resourceGroupName,
    [Parameter(Mandatory=$True)]
    [string]$resourceNamePostFix    
)

$ErrorActionPreference = "Stop"

##############################################
# Import utility functions
##############################################
. ".\deploy.utilities.ps1"
. ".\deploy.servicebus.ps1"

##############################################
# Remove non-alpha-numeric characters
##############################################
$solutionId = $solutionId.ToLower() -replace "\W"
$environment = $environment.ToLower() -replace "\W"
$resolverId = "Resolver"

##############################################
# Set default resource group and location
##############################################
Set-Default-Resource-Group -resourceGroupName $resourceGroupName

# Enable Microsoft.EventGrid in the subscription if it isn't already registered
az provider register --namespace Microsoft.EventGrid

##############################################
# Bicep deployment 
##############################################

$rndString = Get-RandomAlphanumericString

Write-Output "now deploying  $environment"

$output = az deployment group create `
  --resource-group $resourceGroupName `
  --template-file bicep/deploy.core.bicep `
  --parameters `
    solutionId=$solutionId `
    environment=$environment `
    resolverId=$resolverId `
    uniqueDeploy=$rndString

Throw-WhenError -output $output
