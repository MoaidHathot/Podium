// Podium foundation: everything except the web Container App (which references Key Vault secrets that are
// populated between the two deployments). Deploy at resource-group scope.
targetScope = 'resourceGroup'

@description('Azure region for all resources.')
param location string = resourceGroup().location

@description('Short name used as a prefix for resources.')
@minLength(3)
@maxLength(12)
param baseName string = 'podium'

@description('GitHub repository allowed to deploy via OIDC, e.g. MoaidHathot/Podium.')
param githubRepository string

@description('Branch allowed to deploy.')
param githubBranch string = 'main'

@description('Exact OIDC subjects GitHub presents (GitHub may embed owner/repo ids). Branch and environment subjects are always added.')
param githubExtraSubjects array = []

@description('Builder container image.')
param builderImage string = 'ghcr.io/moaidhathot/podium/builder:latest'

@description('Monthly budget in USD for the resource group.')
param budgetAmount int = 8

@description('E-mail that receives budget alerts.')
param budgetEmail string

@description('Deployment timestamp; used only to anchor the budget period.')
param deploymentTime string = utcNow('yyyy-MM')

var suffix = uniqueString(resourceGroup().id)
var storageName = toLower('st${baseName}${take(suffix, 10)}')
var kvName = toLower('kv-${baseName}-${take(suffix, 8)}')

// ---------------------------------------------------------------------------------------------------------------
// Identities
// ---------------------------------------------------------------------------------------------------------------
resource webIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-07-31-preview' = {
  name: 'id-${baseName}-web'
  location: location
}

resource deployIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-07-31-preview' = {
  name: 'id-${baseName}-deploy'
  location: location
}

// GitHub Actions authenticates as this identity through workload identity federation: no client secret anywhere.
var githubSubjects = union([
  'repo:${githubRepository}:ref:refs/heads/${githubBranch}'
  'repo:${githubRepository}:environment:production'
], githubExtraSubjects)

@batchSize(1) // federated credentials on one identity cannot be written concurrently
resource deployFederation 'Microsoft.ManagedIdentity/userAssignedIdentities/federatedIdentityCredentials@2023-07-31-preview' = [for (subject, i) in githubSubjects: {
  parent: deployIdentity
  name: 'github-${i}'
  properties: {
    issuer: 'https://token.actions.githubusercontent.com'
    subject: subject
    audiences: [ 'api://AzureADTokenExchange' ]
  }
}]
// ---------------------------------------------------------------------------------------------------------------
// Observability
// ---------------------------------------------------------------------------------------------------------------
resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: 'log-${baseName}'
  location: location
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
    workspaceCapping: { dailyQuotaGb: 1 }
  }
}

// ---------------------------------------------------------------------------------------------------------------
// Storage (Entra-only: shared keys disabled, no public blobs)
// ---------------------------------------------------------------------------------------------------------------
resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: storageName
  location: location
  kind: 'StorageV2'
  sku: { name: 'Standard_LRS' }
  properties: {
    accessTier: 'Hot'
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
    defaultToOAuthAuthentication: true
    publicNetworkAccess: 'Enabled'
    networkAcls: { defaultAction: 'Allow', bypass: 'AzureServices' }
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: storage
  name: 'default'
  properties: {
    deleteRetentionPolicy: { enabled: true, days: 7 }
    containerDeleteRetentionPolicy: { enabled: true, days: 7 }
  }
}

// ---------------------------------------------------------------------------------------------------------------
// Key Vault (RBAC) for the GitHub App credentials and the signing key
// ---------------------------------------------------------------------------------------------------------------
resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: kvName
  location: location
  properties: {
    tenantId: subscription().tenantId
    sku: { family: 'A', name: 'standard' }
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 7
    // Purge protection: a deleted vault/secret cannot be permanently erased before the retention period ends, so a
    // compromised deploy identity cannot destroy the signing key or GitHub App credentials. Irreversible once set.
    enablePurgeProtection: true
    publicNetworkAccess: 'Enabled'
  }
}

// Wraps the ASP.NET data-protection key ring (cookie encryption keys) so the XML stored in Blob is not usable without
// Key Vault access.
resource dataProtectionKey 'Microsoft.KeyVault/vaults/keys@2023-07-01' = {
  parent: keyVault
  name: 'podium-dataprotection'
  properties: {
    kty: 'RSA'
    keySize: 2048
    keyOps: [ 'wrapKey', 'unwrapKey' ]
  }
}

// ---------------------------------------------------------------------------------------------------------------
// Container Apps environment + builder job
// ---------------------------------------------------------------------------------------------------------------
resource env 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: 'cae-${baseName}'
  location: location
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logs.properties.customerId
        sharedKey: logs.listKeys().primarySharedKey
      }
    }
    workloadProfiles: [ { name: 'Consumption', workloadProfileType: 'Consumption' } ]
  }
}

resource builderJob 'Microsoft.App/jobs@2024-03-01' = {
  name: '${baseName}-builder'
  location: location
  properties: {
    environmentId: env.id
    workloadProfileName: 'Consumption'
    configuration: {
      triggerType: 'Manual'
      replicaTimeout: 1500
      replicaRetryLimit: 0
      manualTriggerConfig: { parallelism: 1, replicaCompletionCount: 1 }
    }
    template: {
      containers: [
        {
          name: 'builder'
          image: builderImage
          resources: { cpu: json('2.0'), memory: '4Gi' }
          env: [ { name: 'PODIUM_WORKDIR', value: '/work' } ]
        }
      ]
    }
  }
}

// ---------------------------------------------------------------------------------------------------------------
// Role assignments
// ---------------------------------------------------------------------------------------------------------------
var roles = {
  storageBlobDataContributor: 'ba92f5b4-2d11-453d-a403-e96b0029c9fe'
  storageTableDataContributor: '0a9a7e1f-b9d0-4cc4-a60d-0319b160aaa3'
  storageBlobDelegator: 'db58b8e5-c6ad-4a2a-8342-4190687cbf4a'
  keyVaultSecretsUser: '4633458b-17de-408a-b874-0445c86b69e6'
  keyVaultCryptoUser: '12338af0-0e69-4776-bea7-57ae8d297424'
  keyVaultSecretsOfficer: 'b86a8fe4-44ce-4948-aee5-eccb2c155cd7'
  contributor: 'b24988ac-6180-42a0-ab88-20f7382dd24c'
}

resource webBlob 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storage.id, webIdentity.id, roles.storageBlobDataContributor)
  scope: storage
  properties: { principalId: webIdentity.properties.principalId, principalType: 'ServicePrincipal', roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.storageBlobDataContributor) }
}
resource webTable 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storage.id, webIdentity.id, roles.storageTableDataContributor)
  scope: storage
  properties: { principalId: webIdentity.properties.principalId, principalType: 'ServicePrincipal', roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.storageTableDataContributor) }
}
resource webDelegator 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storage.id, webIdentity.id, roles.storageBlobDelegator)
  scope: storage
  properties: { principalId: webIdentity.properties.principalId, principalType: 'ServicePrincipal', roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.storageBlobDelegator) }
}
resource webKv 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyVault.id, webIdentity.id, roles.keyVaultSecretsUser)
  scope: keyVault
  properties: { principalId: webIdentity.properties.principalId, principalType: 'ServicePrincipal', roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.keyVaultSecretsUser) }
}
resource webKvCrypto 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyVault.id, webIdentity.id, roles.keyVaultCryptoUser)
  scope: keyVault
  properties: { principalId: webIdentity.properties.principalId, principalType: 'ServicePrincipal', roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.keyVaultCryptoUser) }
}
// Starting job executions needs write access on the job resource; scoped to the job only.
resource webJob 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(builderJob.id, webIdentity.id, roles.contributor)
  scope: builderJob
  properties: { principalId: webIdentity.properties.principalId, principalType: 'ServicePrincipal', roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.contributor) }
}
// The deploy identity updates images on the app and the job.
resource deployRg 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(resourceGroup().id, deployIdentity.id, roles.contributor)
  scope: resourceGroup()
  properties: { principalId: deployIdentity.properties.principalId, principalType: 'ServicePrincipal', roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.contributor) }
}

// ---------------------------------------------------------------------------------------------------------------
// Budget
// ---------------------------------------------------------------------------------------------------------------
resource budget 'Microsoft.Consumption/budgets@2023-11-01' = {
  name: 'budget-${baseName}'
  properties: {
    category: 'Cost'
    amount: budgetAmount
    timeGrain: 'Monthly'
    timePeriod: { startDate: '${deploymentTime}-01' }
    notifications: {
      actual80: { enabled: true, operator: 'GreaterThan', threshold: 80, thresholdType: 'Actual', contactEmails: [ budgetEmail ] }
      forecast100: { enabled: true, operator: 'GreaterThan', threshold: 100, thresholdType: 'Forecasted', contactEmails: [ budgetEmail ] }
    }
  }
}

output storageAccountName string = storage.name
output keyVaultName string = keyVault.name
output keyVaultUri string = keyVault.properties.vaultUri
output dataProtectionKeyId string = dataProtectionKey.properties.keyUriWithVersion
output environmentId string = env.id
output environmentDefaultDomain string = env.properties.defaultDomain
output builderJobId string = builderJob.id
output webIdentityId string = webIdentity.id
output webIdentityClientId string = webIdentity.properties.clientId
output webIdentityPrincipalId string = webIdentity.properties.principalId
output deployIdentityClientId string = deployIdentity.properties.clientId
output logAnalyticsId string = logs.id
