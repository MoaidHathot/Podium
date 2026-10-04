// Podium web app. Requires foundation.bicep to be deployed and the Key Vault secrets to exist:
//   podium-signing-key, github-private-key, github-client-id, github-client-secret, github-webhook-secret
targetScope = 'resourceGroup'

param location string = resourceGroup().location
param baseName string = 'podium'
param webImage string = 'ghcr.io/moaidhathot/podium/web:latest'

@description('Public origin, e.g. https://slides.moaid.codes')
param publicBaseUrl string

@description('Numeric GitHub user id of the owner.')
param ownerGitHubId int

param gitHubAppId string
param gitHubAppSlug string

param environmentId string

@description('Default domain of the Container Apps environment, e.g. thankfulforest-8ac5ebef.westeurope.azurecontainerapps.io')
param environmentDefaultDomain string
param builderJobId string
param storageAccountName string
param keyVaultName string
@description('Key Vault key (URI with version) that wraps the data-protection key ring.')
param dataProtectionKeyId string
param webIdentityId string
param webIdentityClientId string

@description('Optional custom hostnames already verified in DNS; bound with managed certificates via deploy.ps1.')
param customDomains array = []

@description('Idle time (seconds) before the last replica is removed. Default 1800 (30 min) so a presentation session never pays a cold start; the platform default is 300.')
@minValue(60)
param scaleToZeroAfterSeconds int = 1800

@description('Replicas kept running at all times. 0 (default) scales to zero when idle and costs almost nothing; 1 keeps the app warm (no cold start, a few dollars a month). The sync socket and presenter state are per replica, so maxReplicas stays 1.')
@minValue(0)
@maxValue(1)
param minReplicas int = 0

@description('Show a gallery of Public decks to visitors at / (portfolio). Off keeps the root login-only.')
param publicGallery bool = true

resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' existing = { name: keyVaultName }

var secretNames = [ 'podium-signing-key', 'github-private-key', 'github-client-id', 'github-client-secret', 'github-webhook-secret' ]

resource web 'Microsoft.App/containerApps@2025-01-01' = {
  name: '${baseName}-web'
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${webIdentityId}': {} }
  }
  properties: {
    environmentId: environmentId
    workloadProfileName: 'Consumption'
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: true
        targetPort: 8080
        transport: 'http'
        allowInsecure: false
        customDomains: [for d in customDomains: { name: d.name, certificateId: d.certificateId, bindingType: 'SniEnabled' }]
      }
      secrets: [for s in secretNames: {
        name: s
        keyVaultUrl: '${keyVault.properties.vaultUri}secrets/${s}'
        identity: webIdentityId
      }]
    }
    template: {
      scale: {
        minReplicas: minReplicas
        maxReplicas: 1
        cooldownPeriod: scaleToZeroAfterSeconds
        rules: [ { name: 'http', http: { metadata: { concurrentRequests: '100' } } } ]
      }
      containers: [
        {
          name: 'web'
          image: webImage
          resources: { cpu: json('0.5'), memory: '1Gi' }
          env: [
            { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
            { name: 'AZURE_CLIENT_ID', value: webIdentityClientId }
            { name: 'Podium__PublicBaseUrl', value: publicBaseUrl }
            { name: 'Podium__OwnerGitHubId', value: string(ownerGitHubId) }
            // Builder callbacks use the platform FQDN so they never depend on custom-domain/certificate state.
            { name: 'Podium__CallbackBaseUrl', value: 'https://${baseName}-web.${environmentDefaultDomain}' }
            // Decks from repositories the owner does not control are served from this second origin (platform FQDN).
            { name: 'Podium__ExternalBaseUrl', value: 'https://${baseName}-web.${environmentDefaultDomain}' }
            { name: 'Podium__SigningKey', secretRef: 'podium-signing-key' }
          { name: 'Podium__PublicGallery', value: string(publicGallery) }
            { name: 'Storage__AccountName', value: storageAccountName }
            { name: 'DataProtection__KeyVaultKeyId', value: dataProtectionKeyId }
            { name: 'Builder__Mode', value: 'ContainerAppsJob' }
            { name: 'Builder__JobResourceId', value: builderJobId }
            { name: 'Builder__Cpu', value: '2' }
            { name: 'Builder__Memory', value: '4Gi' }
            { name: 'GitHub__AppId', value: gitHubAppId }
            { name: 'GitHub__AppSlug', value: gitHubAppSlug }
            { name: 'GitHub__PrivateKeyPem', secretRef: 'github-private-key' }
            { name: 'GitHub__ClientId', secretRef: 'github-client-id' }
            { name: 'GitHub__ClientSecret', secretRef: 'github-client-secret' }
            { name: 'GitHub__WebhookSecret', secretRef: 'github-webhook-secret' }
          ]
          // Readiness is checked every 2 s with a generous timeout: after a cold start the app receives traffic within
          // ~2 s of being ready, instead of losing up to 10 s to a failed first probe and a long period.
          probes: [
            { type: 'Startup', httpGet: { path: '/healthz', port: 8080 }, initialDelaySeconds: 1, periodSeconds: 2, timeoutSeconds: 3, failureThreshold: 30 }
            { type: 'Readiness', httpGet: { path: '/healthz', port: 8080 }, periodSeconds: 2, timeoutSeconds: 3, failureThreshold: 3 }
            { type: 'Liveness', httpGet: { path: '/healthz', port: 8080 }, periodSeconds: 30, timeoutSeconds: 5, failureThreshold: 3 }
          ]
        }
      ]
    }
  }
}

output fqdn string = web.properties.configuration.ingress.fqdn
output customDomainVerificationId string = web.properties.customDomainVerificationId
