param name string
param location string = resourceGroup().location
param dbname string

resource cosmosDbAccount 'Microsoft.DocumentDB/databaseAccounts@2022-05-15' = {
  name: name
  location: location
  kind: 'GlobalDocumentDB'
  properties: {
    consistencyPolicy: {
      defaultConsistencyLevel: 'Session'
      maxStalenessPrefix: 100
      maxIntervalInSeconds: 5
    }
    locations: [
      {
        locationName: location
        failoverPriority: 0
      }
    ]
    databaseAccountOfferType: 'Standard'  
  }
}


resource sqlDb 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases@2021-06-15' = {
  parent: cosmosDbAccount
  name: dbname
  properties: {
    resource: {
      id: dbname
    }
  }
}

resource messagesContainer 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2021-06-15' = {
  parent: sqlDb
  name: 'messages'
  properties: {
    resource: {
      id: 'messages'
      partitionKey: {
        paths: ['/eventId']
        kind: 'Hash'
      }
      defaultTtl: 7776000 // 90 days
      indexingPolicy: {
        indexingMode: 'consistent'
        includedPaths: [
          { path: '/*' }
        ]
        excludedPaths: [
          { path: '/message/messageContent/*' }
          { path: '/"_etag"/?' }
        ]
      }
    }
  }
}

resource auditsContainer 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2021-06-15' = {
  parent: sqlDb
  name: 'audits'
  properties: {
    resource: {
      id: 'audits'
      partitionKey: {
        paths: ['/eventId']
        kind: 'Hash'
      }
      defaultTtl: 31536000 // 1 year
    }
  }
}

output connectionString string = cosmosDbAccount.listConnectionStrings().connectionStrings[0].connectionString
