param name string
param location string = resourceGroup().location

resource ai 'Microsoft.Insights/components@2020-02-02-preview' = {
  name: name
  location: location
  kind: 'web'
  properties: {
    Application_Type: 'web'
  }
}

@secure()
output instrumentationKey string = ai.properties.InstrumentationKey
output appId string = ai.properties.AppId
@secure()
output connectionString string = ai.properties.ConnectionString
