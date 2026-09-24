targetScope = 'resourceGroup'

@description('Globally unique prefix for the App Service and PostgreSQL names.')
param namePrefix string
@description('Azure region checked in the pricing calculator before deployment.')
param location string = resourceGroup().location
@secure()
param postgresPassword string
param tenantId string
param apiAudience string
param appIdUri string
param browserClientId string

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

resource postgres 'Microsoft.DBforPostgreSQL/flexibleServers@2024-08-01' = {
  name: '${namePrefix}-pg'
  location: location
  sku: {
    name: 'Standard_B1ms'
    tier: 'Burstable'
  }
  properties: {
    administratorLogin: 'smtadmin'
    administratorLoginPassword: postgresPassword
    version: '16'
    storage: {
      storageSizeGB: 32
      autoGrow: 'Disabled'
    }
    backup: {
      backupRetentionDays: 7
      geoRedundantBackup: 'Disabled'
    }
    highAvailability: {
      mode: 'Disabled'
    }
    network: {
      publicNetworkAccess: 'Enabled'
    }
  }
}

resource azureServicesRule 'Microsoft.DBforPostgreSQL/flexibleServers/firewallRules@2024-08-01' = {
  parent: postgres
  name: 'AllowAzureServices'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
}

resource database 'Microsoft.DBforPostgreSQL/flexibleServers/databases@2024-08-01' = {
  parent: postgres
  name: 'smt'
  properties: {
    charset: 'UTF8'
    collation: 'en_US.utf8'
  }
}

resource site 'Microsoft.Web/sites@2024-04-01' = {
  name: '${namePrefix}-api'
  location: location
  kind: 'app'
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    siteConfig: {
      netFrameworkVersion: 'v8.0'
      use32BitWorkerProcess: true
      ftpsState: 'Disabled'
      minTlsVersion: '1.2'
      appSettings: [
        { name: 'ConnectionStrings__Postgres', value: 'Host=${postgres.properties.fullyQualifiedDomainName};Port=5432;Database=smt;Username=smtadmin;Password=${postgresPassword};SSL Mode=Require' }
        { name: 'Entra__TenantId', value: tenantId }
        { name: 'Entra__Audience', value: apiAudience }
        { name: 'Entra__AppIdUri', value: appIdUri }
        { name: 'Entra__BrowserClientId', value: browserClientId }
        { name: 'Production__Destination', value: 'SMT-LINE-1' }
      ]
    }
  }
}

output apiUrl string = 'https://${site.properties.defaultHostName}'
output postgresHost string = postgres.properties.fullyQualifiedDomainName
