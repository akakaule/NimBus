Param (
	[Parameter(Mandatory=$True)]
    [string]$solutionId,
    [Parameter(Mandatory=$True)]
    [string]$environment,
	[Parameter(Mandatory=$True)]
    [string]$resourceGroupName,
    [Parameter(Mandatory=$True)]
    [string]$webAppVersion,
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

##############################################
# Upgrade az cli
##############################################
az upgrade

##############################################
# Set default resource group and location
##############################################
Set-Default-Resource-Group -resourceGroupName $resourceGroupName

# Enable Microsoft.EventGrid in the subscription if it isn't already registered
az provider register --namespace Microsoft.EventGrid

##############################################
# Define names Azure resource names
##############################################

$sbNamespace = "sb-{0}-{1}" -f $solutionId, $environment

$appInsightsName = "ai-{0}-{1}-global-tracelog" -f $solutionId, $environment

$cosmosAccountName = "cosmos-{0}-{1}" -f $solutionId, $environment

$cosmosDbName = "MessageDatabase"


##############################################
# Install Azure CLI extensions
##############################################

Try {
    Add-Extension -name application-insights
}
Catch {
    if($_.Exception.Message -eq "WARNING: Extension 'application-insights' is already installed.")
    {
        Write-Host "Extension 'application-insights' is already installed."
    }
    else
    {
        throw
    }
}


######################################################
# Create key object and get apiKey from managementapp
######################################################

#Creating api key, renew if the key allready exists (Delete old and make new in the same name)

try { 
$keyObject = az monitor app-insights api-key show --app $appInsightsName --api-key "management-app"
  If ($keyObject) {
	az monitor app-insights api-key delete --app $appInsightsName --api-key "management-app"
  }
  $apiKey = az monitor app-insights api-key create --api-key "management-app" --read-properties ReadTelemetry --app $appInsightsName --write-properties "WriteAnnotations"
  $apiKey = $apiKey | ConvertFrom-Json
  $apiKey = $apiKey.apiKey
}
catch {
   Write-Error $_.Exception
}

##############################################
# Cosmos DB: Get ConnectionString
##############################################

$cosmosDbConnectionString="Placeholder"

$dbResult = az cosmosdb sql database exists --account-name $cosmosAccountName --name $cosmosDbName
if($dbResult -eq 'true')
{
    $cosmosDbConnectionString=$(az cosmosdb keys list `
    -n $cosmosAccountName `
    --type connection-strings `
    --query connectionStrings[0].connectionString `
    --output tsv)

    Write-Output $cosmosDbConnectionString
}


##############################################
# Create Web App: Conflict Resolution Web App
##############################################

# Use Shared Access Policy instead for now
$managerServiceBusConnection = Get-ServiceBusPrimaryConnectionString -serviceBusNamespace $sbNamespace -resourceGroup $resourceGroupName
#az functionapp config appsettings set --name $managementWebAppName --settings "AzureWebJobsServiceBus=$managerServiceBusConnection"

#Getting appid from appinsights
$appInsightsAppId = ( `
    az monitor app-insights component show `
    --app "ai-$solutionId-$environment-global-tracelog" `
| ConvertFrom-Json).appId

$instrumentationKey = ( `
    az monitor app-insights component show `
    --app "ai-$solutionId-$environment-global-tracelog" `
| ConvertFrom-Json).instrumentationKey

##############################################
# Bicep deployment 
##############################################

$output = az deployment group create `
  --resource-group $resourceGroupName `
  --template-file bicep/deploy.webapp.bicep `
  --parameters `
    solutionId=$solutionId `
    environment=$environment `
    webAppVersion=$webAppVersion `
    apiKey=$apiKey `
    appInsightsAppId=$appInsightsAppId `
    instrumentationKey=$instrumentationKey `
    cosmosDbConnectionString=$cosmosDbConnectionString `
    managerServiceBusConnection=$managerServiceBusConnection `
    
    

Throw-WhenError -output $output

