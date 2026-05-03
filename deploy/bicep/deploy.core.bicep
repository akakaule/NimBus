param solutionId string
param environment string = 'dev'
param locationParam string = 'westeurope'
param resolverId string
param uniqueDeploy string

// Storage provider selection. 'cosmos' (default, backwards-compatible) provisions a
// Cosmos DB account. 'sqlserver' skips Cosmos and may optionally provision Azure SQL
// when sqlMode is 'provision'; 'external' expects a customer-supplied connection string.
@allowed([
  'cosmos'
  'sqlserver'
])
param storageProvider string = 'cosmos'

@allowed([
  'provision'
  'external'
])
param sqlMode string = 'provision'

param sqlAdminLogin string = ''
@secure()
param sqlAdminPassword string = ''

//##############################################
// Define names Azure resource names
//##############################################
var location = locationParam

var sbNamespace = 'sb-${toLower(solutionId)}-${toLower(environment)}'

var managementAppServicePlanName = 'asp-${toLower(solutionId)}-${toLower(environment)}-management'

var coreAppServicePlanName = 'asp-${toLower(solutionId)}-${toLower(environment)}-core'

var cosmosAccountName = 'cosmos-${toLower(solutionId)}-${toLower(environment)}'

var cosmosDbName = 'MessageDatabase'

var sqlServerName = 'sql-${toLower(solutionId)}-${toLower(environment)}'

var sqlDbName = 'MessageDatabase'

var resolverFunctionAppName = 'func-${toLower(solutionId)}-${toLower(environment)}-resolver'

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

module cosmosAccount 'templates/cosmosDB.bicep' = if (storageProvider == 'cosmos') {
  name: 'cosmosDBDeploy'
  params: {
    name: cosmosAccountName
    dbname: cosmosDbName
    location: location
  }
}

//##############################################
//# Azure SQL: Create server + database (only when storageProvider=sqlserver and sqlMode=provision)
//##############################################

module azureSql 'templates/azureSql.bicep' = if (storageProvider == 'sqlserver' && sqlMode == 'provision') {
  name: 'azureSqlDeploy'
  params: {
    serverName: sqlServerName
    databaseName: sqlDbName
    location: location
    administratorLogin: sqlAdminLogin
    administratorPassword: sqlAdminPassword
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

var commonResolverSettings = [
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
    name: 'WEBSITE_CONTENTAZUREFILECONNECTIONSTRING'
    value: funcstorageaccount.outputs.connectionString
  }
  {
    name: 'WEBSITE_CONTENTSHARE'
    value: '${toLower(resolverFunctionAppName)}${uniqueString(uniqueDeploy)}'
  }
  {
    name: 'AzureWebJobsServiceBus__fullyQualifiedNamespace'
    value: serviceBusNamespace.outputs.fullyQualifiedNamespace
  }
]

var cosmosResolverSetting = storageProvider == 'cosmos' ? [
  {
    name: 'CosmosAccountEndpoint'
    value: cosmosAccount.outputs.accountEndpoint
  }
] : []

var sqlResolverSetting = storageProvider == 'sqlserver' && sqlMode == 'provision' ? [
  {
    name: 'SqlConnection'
    value: 'Server=tcp:${azureSql.outputs.serverFqdn},1433;Initial Catalog=${sqlDbName};Authentication=Active Directory Default;Encrypt=true;'
  }
] : []

var resolverappsettings = concat(commonResolverSettings, cosmosResolverSetting, sqlResolverSetting)

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
//# Resolver: RBAC role assignments
//##############################################

// Service Bus RBAC must always be granted to the resolver identity (managed-identity
// access to receive/complete messages). Cosmos role assignment is gated inside the
// module so SQL deployments don't try to create Cosmos sqlRoleAssignments resources.
module resolverRoleAssignments 'templates/roleAssignments.bicep' = {
  name: 'resolverRoleAssignmentsDeploy'
  params: {
    serviceBusNamespaceName: sbNamespace
    cosmosAccountName: storageProvider == 'cosmos' ? cosmosAccountName : ''
    principalId: resolverFunction.outputs.principalId
    storageProvider: storageProvider
  }
}
