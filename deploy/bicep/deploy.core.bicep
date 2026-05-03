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

// Resolver Function App hosting plan. 'ElasticPremium' (default, EP1, Windows)
// preserves the existing behavior. 'FlexConsumption' provisions a Flex Consumption
// plan (FC1, Linux) that scales to zero — significantly cheaper for dev/test.
@allowed([
  'ElasticPremium'
  'FlexConsumption'
])
param resolverPlan string = 'ElasticPremium'

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
    // Flex Consumption needs a blob container holding the app package, referenced
    // from the Function App via SystemAssignedIdentity. Provision it inline so
    // the container exists before resolverFunctionFlex tries to bind to it.
    createDeploymentContainer: resolverPlan == 'FlexConsumption'
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
//# Resolver: Function App settings shared by both plan types
//##############################################

// Settings every resolver host needs regardless of plan type.
var sharedResolverSettings = [
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
    name: 'AzureWebJobsServiceBus__fullyQualifiedNamespace'
    value: serviceBusNamespace.outputs.fullyQualifiedNamespace
  }
]

// Elastic Premium needs the Windows host to know where its content share lives.
// Flex Consumption rejects these settings.
var elasticPremiumExtraSettings = resolverPlan == 'ElasticPremium' ? [
  {
    name: 'WEBSITE_CONTENTAZUREFILECONNECTIONSTRING'
    value: funcstorageaccount.outputs.connectionString
  }
  {
    name: 'WEBSITE_CONTENTSHARE'
    value: '${toLower(resolverFunctionAppName)}${uniqueString(uniqueDeploy)}'
  }
] : []

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

var resolverappsettings = concat(sharedResolverSettings, elasticPremiumExtraSettings, cosmosResolverSetting, sqlResolverSetting)

//##############################################
//# Resolver: Hosting plan + Function App (Elastic Premium branch)
//##############################################

module functionappplanElastic 'templates/functionAppPlan.bicep' = if (resolverPlan == 'ElasticPremium') {
  name: 'functionAppplanDeploy'
  params: {
    name: coreAppServicePlanName
    skuName: 'EP1'
    location: location
  }
}

module resolverFunctionElastic 'templates/functionApp.bicep' = if (resolverPlan == 'ElasticPremium') {
  name: 'resolverDeploy'
  params: {
    appName: resolverFunctionAppName
    appInsightsInstrumentationKey: applicationinsights.outputs.instrumentationKey
    appServicePlanId: functionappplanElastic.outputs.id
    functionAppVersion: '4'
    storageConnectionString: funcstorageaccount.outputs.connectionString
    location: location
    settings: resolverappsettings
  }
}

//##############################################
//# Resolver: Hosting plan + Function App (Flex Consumption branch)
//##############################################

module functionappplanFlex 'templates/flexConsumptionPlan.bicep' = if (resolverPlan == 'FlexConsumption') {
  name: 'functionAppplanFlexDeploy'
  params: {
    name: coreAppServicePlanName
    location: location
  }
}

module resolverFunctionFlex 'templates/flexConsumptionFunctionApp.bicep' = if (resolverPlan == 'FlexConsumption') {
  name: 'resolverFlexDeploy'
  params: {
    appName: resolverFunctionAppName
    appServicePlanId: functionappplanFlex.outputs.id
    storageAccountName: funcstorageaccount.outputs.storageName
    deploymentStorageBlobUri: '${funcstorageaccount.outputs.blobEndpoint}app-package-resolver'
    location: location
    settings: resolverappsettings
  }
}

// Branch-aware principal id; one of the two modules deploys, the other is skipped.
var resolverPrincipalId = resolverPlan == 'FlexConsumption'
  ? resolverFunctionFlex.outputs.principalId
  : resolverFunctionElastic.outputs.principalId

//##############################################
//# Resolver: RBAC role assignments
//##############################################

// Service Bus RBAC must always be granted to the resolver identity (managed-identity
// access to receive/complete messages). Cosmos role assignment is gated inside the
// module so SQL deployments don't try to create Cosmos sqlRoleAssignments resources.
// Flex Consumption additionally needs Storage Blob Data Contributor on the function
// storage account for the deployment package + AzureWebJobsStorage host runtime.
module resolverRoleAssignments 'templates/roleAssignments.bicep' = {
  name: 'resolverRoleAssignmentsDeploy'
  params: {
    serviceBusNamespaceName: sbNamespace
    cosmosAccountName: storageProvider == 'cosmos' ? cosmosAccountName : ''
    principalId: resolverPrincipalId
    storageProvider: storageProvider
    funcStorageAccountName: funcStorageAccountName
    grantFuncStorageBlobDataContributor: resolverPlan == 'FlexConsumption'
  }
}
