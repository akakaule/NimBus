param solutionId string
param environment string = 'dev'
param locationParam string = 'westeurope'
param resolverId string
param uniqueDeploy string

//##############################################
// Define names Azure resource names
//##############################################
var location = locationParam

var sbNamespace = 'sb-${toLower(solutionId)}-${toLower(environment)}'

var managementAppServicePlanName = 'asp-${toLower(solutionId)}-${toLower(environment)}-management'

var coreAppServicePlanName = 'asp-${toLower(solutionId)}-${toLower(environment)}-core'

var cosmosAccountName = 'cosmos-${toLower(solutionId)}-${toLower(environment)}'

var cosmosDbName = 'MessageDatabase'

var resolverFunctionAppName = 'func-${toLower(solutionId)}-${toLower(environment)}-resolver'

var eventPublisherFunctionAppName = 'func-${toLower(solutionId)}-${toLower(environment)}-event'

var funcStorageAccountName = 'st${toLower(solutionId)}${toLower(environment)}func'

var appInsightsName = 'ai-${toLower(solutionId)}-${toLower(environment)}-global-tracelog'

//##############################################
//# Create Service Bus namespace
//##############################################

module serviceBusNamespace 'templates/servicebusNamespace.bicep' = {
  name : 'ServicebusNamespaceDeploy'
  params : {
    name: sbNamespace
    location: location
  }
}

//##############################################
//# Create Application Insights (for global trace log)
//##############################################

//TODO: MANGLER API KEY

module applicationinsights 'templates/applicationInsights.bicep' = {
  name: 'AppinsightsDeploy'
  params: {
    name: appInsightsName
    location: location
  }
}

//##############################################
//# Cosmos DB: Create Account and Database
//##############################################

module cosmosAccount 'templates/cosmosDB.bicep' = {
  name: 'cosmosDBDeploy'
  params: {
    name: cosmosAccountName
    dbname: cosmosDbName
    location: location
  }
}

//##############################################
//# Function Apps: Create storage account
//##############################################

module funcstorageaccount 'templates/storageaccount.bicep' = {
  name : 'FuncStorageAccountDeploy'
  params : {
    name: funcStorageAccountName
    location: location
  }
}

//##############################################
//# Create App Service Plan for management app
//##############################################

module appserviceplan 'templates/appServicePlan.bicep' = {
  name: 'ManagementPlanDeploy'
  params: {
    name: managementAppServicePlanName
    skuName: 'S1'
    location: location
  }
}

//##############################################
//# Create App Service Plan for function apps
//##############################################

module functionappplan 'templates/functionAppPlan.bicep' = {
  name: 'functionAppplanDeploy'
  params: {
    name: coreAppServicePlanName
    skuName: 'EP1'
    location: location
  }
}


//##############################################
//# Resolver: Create Function app
//##############################################

var resolverappsettings = [
  {
    name: 'GlobalTraceLogInstrKey'
    value: applicationinsights.outputs.instrumentationKey
  }
  {
    name: 'ServiceBusNamespace'
    value: sbNamespace
  }
  {
    name: 'ResolverId'
    value: resolverId
  }
  {
    name: 'CosmosConnection'
    value: cosmosAccount.outputs.connectionString
  }
  {
    name: 'WEBSITE_CONTENTAZUREFILECONNECTIONSTRING'
    value: funcstorageaccount.outputs.connectionString
  }
  {
    name: 'WEBSITE_CONTENTSHARE'
    value: '${toLower(resolverFunctionAppName)}${uniqueString(uniqueDeploy)}'
  }
  {
    name: 'AzureWebJobsServiceBus'
    value: serviceBusNamespace.outputs.SharedAccessKey
  }
]

module resolverFunction 'templates/functionApp.bicep' = {
  name: 'resolverDeploy'
  params: {
    appName: resolverFunctionAppName
    appInsightsInstrumentationKey: applicationinsights.outputs.instrumentationKey
    appServicePlanId: functionappplan.outputs.id
    functionAppVersion: '4'
    storageConnectionString: funcstorageaccount.outputs.connectionString
    location: location
    settings: resolverappsettings
  }

}

//##############################################
//# EventPublisher: Create Function app 
//##############################################

var eventpublisherappsettings = [
  {
    name: 'GlobalTraceLogInstrKey'
    value: applicationinsights.outputs.instrumentationKey
  }
  {
    name: 'ServiceBusConnection'
    value: serviceBusNamespace.outputs.SharedAccessKey
  }
  {
    name: 'WEBSITE_CONTENTAZUREFILECONNECTIONSTRING'
    value: funcstorageaccount.outputs.connectionString
  }
  {
    name: 'WEBSITE_CONTENTSHARE'
    value: '${toLower(eventPublisherFunctionAppName)}${uniqueString(uniqueDeploy)}'
  }
]

module eventpublisher 'templates/functionApp.bicep' = {
  name: 'eventpublisherDeploy'
  params: {
    appName: eventPublisherFunctionAppName
    appInsightsInstrumentationKey: applicationinsights.outputs.instrumentationKey
    appServicePlanId: functionappplan.outputs.id
    functionAppVersion: '4'
    storageConnectionString: funcstorageaccount.outputs.connectionString
    location: location
    settings: eventpublisherappsettings
  }
}
