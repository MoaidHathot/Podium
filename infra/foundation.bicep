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

@description('Web image, used by the scheduled backup job (same image as the app, xport mode).')
param webImage string = 'ghcr.io/moaidhathot/podium/web:latest'

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

// Application Insights (workspace-based): requests, dependencies, exceptions and traces from the web app via
// OpenTelemetry. Shares the Log Analytics quota above.
resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: 'appi-${baseName}'
  location: location
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logs.id
    IngestionMode: 'LogAnalytics'
    RetentionInDays: 30
  }
}

// ---------------------------------------------------------------------------------------------------------------
// Alerts: e-mail on crash loops / failed probes, build-failure streaks and server errors. Cents per month.
// ---------------------------------------------------------------------------------------------------------------
resource alertGroup 'Microsoft.Insights/actionGroups@2023-01-01' = {
  name: 'ag-${baseName}'
  location: 'global'
  properties: {
    groupShortName: take(baseName, 12)
    enabled: true
    emailReceivers: [ { name: 'owner', emailAddress: budgetEmail, useCommonAlertSchema: true } ]
  }
}

resource alertContainerFailures 'Microsoft.Insights/scheduledQueryRules@2023-03-15-preview' = {
  name: 'alert-${baseName}-web-restarts'
  location: location
  properties: {
    displayName: 'Podium web: container terminated or probe failed'
    description: 'The web container exited unexpectedly or failed its probes (crash loop, bad image, startup error).'
    severity: 1
    enabled: true
    evaluationFrequency: 'PT5M'
    windowSize: 'PT15M'
    scopes: [ logs.id ]
    criteria: {
      allOf: [
        {
          query: 'ContainerAppSystemLogs_CL | where ContainerAppName_s == "${baseName}-web" | where Reason_s in ("ContainerTerminated", "ProbeFailed") | where Log_s !has "ManuallyStopped" and Log_s !has "ScaledToZero"'
          timeAggregation: 'Count'
          operator: 'GreaterThanOrEqual'
          threshold: 2
          failingPeriods: { numberOfEvaluationPeriods: 1, minFailingPeriodsToAlert: 1 }
        }
      ]
    }
    autoMitigate: true
    actions: { actionGroups: [ alertGroup.id ] }
  }
}

resource alertBuildFailures 'Microsoft.Insights/scheduledQueryRules@2023-03-15-preview' = {
  name: 'alert-${baseName}-build-failures'
  location: location
  properties: {
    displayName: 'Podium: repeated build failures'
    description: 'Three or more deck builds failed within an hour (builder image problem, GitHub outage, or a broken deck being retried).'
    severity: 2
    enabled: true
    evaluationFrequency: 'PT15M'
    windowSize: 'PT1H'
    scopes: [ logs.id ]
    criteria: {
      allOf: [
        {
          query: 'ContainerAppConsoleLogs_CL | where ContainerAppName_s == "${baseName}-web" | where Log_s has "finished: Failed"'
          timeAggregation: 'Count'
          operator: 'GreaterThanOrEqual'
          threshold: 3
          failingPeriods: { numberOfEvaluationPeriods: 1, minFailingPeriodsToAlert: 1 }
        }
      ]
    }
    autoMitigate: true
    actions: { actionGroups: [ alertGroup.id ] }
  }
}

resource alertServerErrors 'Microsoft.Insights/scheduledQueryRules@2023-03-15-preview' = {
  name: 'alert-${baseName}-5xx'
  location: location
  properties: {
    displayName: 'Podium web: server errors'
    description: 'Ten or more 5xx responses in 15 minutes (as recorded by Application Insights).'
    severity: 2
    enabled: true
    evaluationFrequency: 'PT5M'
    windowSize: 'PT15M'
    scopes: [ logs.id ]
    criteria: {
      allOf: [
        {
          query: 'AppRequests | where toint(ResultCode) >= 500'
          timeAggregation: 'Count'
          operator: 'GreaterThanOrEqual'
          threshold: 10
          failingPeriods: { numberOfEvaluationPeriods: 1, minFailingPeriodsToAlert: 1 }
        }
      ]
    }
    autoMitigate: true
    actions: { actionGroups: [ alertGroup.id ] }
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

// Daily backup of the index tables into the "backups" container (the artifacts are reproducible from git). Runs the
// web image in `export` mode with the web identity; runs older than 30 days are deleted by the lifecycle rule.
resource backupJob 'Microsoft.App/jobs@2024-03-01' = {
  name: '${baseName}-backup'
  location: location
  identity: { type: 'UserAssigned', userAssignedIdentities: { '${webIdentity.id}': {} } }
  properties: {
    environmentId: env.id
    workloadProfileName: 'Consumption'
    configuration: {
      triggerType: 'Schedule'
      replicaTimeout: 600
      replicaRetryLimit: 1
      scheduleTriggerConfig: { cronExpression: '15 3 * * *', parallelism: 1, replicaCompletionCount: 1 }
    }
    template: {
      containers: [
        {
          name: 'backup'
          image: webImage
          args: [ 'export' ]
          resources: { cpu: json('0.25'), memory: '0.5Gi' }
          env: [
            { name: 'Storage__AccountName', value: storage.name }
            { name: 'AZURE_CLIENT_ID', value: webIdentity.properties.clientId }
            { name: 'Podium__PublicBaseUrl', value: 'https://localhost' }
            { name: 'Podium__OwnerGitHubId', value: '1' }
            { name: 'Podium__SigningKey', value: 'export-only-no-requests-are-served-0123456789abcdef' }
            { name: 'DataProtection__KeyVaultKeyId', value: dataProtectionKey.properties.keyUriWithVersion }
            { name: 'Builder__Mode', value: 'LocalProcess' }
          ]
        }
      ]
    }
  }
}

resource backupLifecycle 'Microsoft.Storage/storageAccounts/managementPolicies@2023-05-01' = {
  parent: storage
  name: 'default'
  properties: {
    policy: {
      rules: [
        {
          name: 'expire-backups'
          enabled: true
          type: 'Lifecycle'
          definition: {
            filters: { blobTypes: [ 'blockBlob' ], prefixMatch: [ 'backups/' ] }
            actions: { baseBlob: { delete: { daysAfterModificationGreaterThan: 30 } } }
          }
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
output appInsightsConnectionString string = appInsights.properties.ConnectionString
