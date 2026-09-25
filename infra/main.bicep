targetScope = 'resourceGroup'

@description('Globally unique lowercase prefix for the Web App and storage account.')
param namePrefix string
param location string = resourceGroup().location
param tenantId string
param apiAudience string
param appIdUri string
param browserClientId string

var storageName = 'smt${uniqueString(resourceGroup().id, namePrefix)}'
var tableName = 'SmtOrders'

resource plan 'Microsoft.Web/serverfarms@2024-04-01' = {
  name: '${namePrefix}-plan'
  location: location
  kind: 'app'
  sku: {
    name: 'F1'
    tier: 'Free'
    capacity: 1
  }
  properties: {
    reserved: false
  }
}

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: storageName
  location: location
  kind: 'StorageV2'
  sku: { name: 'Standard_LRS' }
  properties: {
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false
    minimumTlsVersion: 'TLS1_2'
  }
}

resource tableService 'Microsoft.Storage/storageAccounts/tableServices@2023-05-01' = {
  parent: storage
  name: 'default'
}

resource ordersTable 'Microsoft.Storage/storageAccounts/tableServices/tables@2023-05-01' = {
  parent: tableService
  name: tableName
}

resource site 'Microsoft.Web/sites@2024-04-01' = {
  name: '${namePrefix}-api'
  location: location
  kind: 'app'
  identity: { type: 'SystemAssigned' }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    siteConfig: {
      netFrameworkVersion: 'v8.0'
      use32BitWorkerProcess: true
      ftpsState: 'Disabled'
      minTlsVersion: '1.2'
      appSettings: [
        { name: 'Storage__TableServiceUri', value: 'https://${storage.name}.table.${environment().suffixes.storage}' }
        { name: 'Storage__TableName', value: tableName }
        { name: 'Entra__TenantId', value: tenantId }
        { name: 'Entra__Audience', value: apiAudience }
        { name: 'Entra__AppIdUri', value: appIdUri }
        { name: 'Entra__BrowserClientId', value: browserClientId }
        { name: 'Production__Destination', value: 'SMT-LINE-1' }
      ]
    }
  }
}

resource tableRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storage.id, site.id, 'Storage Table Data Contributor')
  scope: storage
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '0a9a7e1f-b9d0-4cc4-a60d-0319b160aaa3')
    principalId: site.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

output apiUrl string = 'https://${site.properties.defaultHostName}'
output tableEndpoint string = 'https://${storage.name}.table.${environment().suffixes.storage}'
