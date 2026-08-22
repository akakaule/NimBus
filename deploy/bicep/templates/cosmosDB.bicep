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

// Shared containers the apps otherwise create lazily via the SDK. Lazy
// creation only works with account keys — Entra data-plane RBAC (which the
// deployed apps use) allows item reads/writes but NOT container management —
// so every shared container must be declared here. Per-endpoint containers
// (one per catalog endpoint, PK /id) are catalog-dependent and cannot be
// declared statically; create them alongside topology provisioning.
var sharedContainers = [
  { name: 'subscriptions', pk: '/id' }   // endpoint notification subscriptions
  { name: 'eventschemas', pk: '/id' }    // agent-defined event schemas (spec 022)
  { name: 'eventreports', pk: '/EndpointId' } // per-event reported markers
  { name: 'accesscontrol', pk: '/id' }   // site + endpoint ACLs (spec 026)
  { name: 'Metadata', pk: '/id' }        // endpoint metadata (owner, heartbeat opt-in)
  { name: 'settings', pk: '/id' }        // operator-tuned platform settings
  { name: 'servicehealth', pk: '/id' }   // Resolver liveness beats
  { name: 'heartbeatuptimedays', pk: '/EndpointId', ttl: -1 } // heartbeat uptime rollups; TTL on, items decide expiry
  { name: 'heartbeatgaps', pk: '/EndpointId', ttl: -1 }       // heartbeat silent periods; TTL on, items decide expiry
]

resource sharedContainerResources 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2021-06-15' = [for c in sharedContainers: {
  parent: sqlDb
  name: c.name
  properties: {
    resource: {
      id: c.name
      partitionKey: {
        paths: [c.pk]
        kind: 'Hash'
      }
      defaultTtl: c.?ttl
    }
  }
}]

@secure()
output connectionString string = cosmosDbAccount.listConnectionStrings().connectionStrings[0].connectionString
output accountEndpoint string = cosmosDbAccount.properties.documentEndpoint
