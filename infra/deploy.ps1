<#
.SYNOPSIS
  Deploys Podium to Azure. Idempotent; run phases individually or all at once.

.EXAMPLE
  ./infra/deploy.ps1 -Phase foundation
  ./infra/deploy.ps1 -Phase secrets            # after scripts/github-app-manifest.ps1 stored the GitHub App credentials
  ./infra/deploy.ps1 -Phase app -GitHubAppId 123456 -GitHubAppSlug podium-slides
  ./infra/deploy.ps1 -Phase domain             # after the CNAME + TXT records exist
#>
param(
    [ValidateSet('foundation', 'secrets', 'app', 'domain', 'all')] [string] $Phase = 'all',
    [string] $ResourceGroup = 'rg-podium',
    [string] $Location = 'westeurope',
    [string] $BaseName = 'podium',
    [string] $PublicHostname = 'slides.moaid.codes',
    [long] $OwnerGitHubId = 8770486,
    [string] $BudgetEmail = 'moaid.hathot@outlook.com',
    [string] $GitHubRepository = 'MoaidHathot/Podium',
    [string] $GitHubAppId,
    [string] $GitHubAppSlug,
    # Images default to whatever is currently deployed (CI pins digests); the :latest tags are used only when the
    # resource does not exist yet, so re-running a phase never rolls production to a different image.
    [string] $WebImage,
    # 0 = scale to zero when idle (default); 1 = always-warm replica (app phase).
    [ValidateRange(0, 1)] [int] $MinReplicas = 0,
    [string] $BuilderImage
)
$ErrorActionPreference = 'Stop'
$infra = $PSScriptRoot

function Resolve-DeployedImage {
    # Returns the image reference the given container app / job currently runs, or $null when it does not exist.
    param([ValidateSet('app', 'job')] [string] $Kind, [string] $Name)
    $cmd = if ($Kind -eq 'job') { @('containerapp', 'job', 'show') } else { @('containerapp', 'show') }
    $img = & az @cmd -g $ResourceGroup -n $Name --query 'properties.template.containers[0].image' -o tsv 2>$null | Where-Object { "$_" -notmatch 'WARNING' } | Out-String
    if ($LASTEXITCODE -ne 0 -or -not "$img".Trim()) { return $null }
    return "$img".Trim()
}

if (-not $BuilderImage) {
    $BuilderImage = Resolve-DeployedImage -Kind job -Name "$BaseName-builder"
    if ($BuilderImage) { Write-Host "builder image: keeping deployed $BuilderImage" } else { $BuilderImage = 'ghcr.io/moaidhathot/podium/builder:latest' }
}
if (-not $WebImage) {
    $WebImage = Resolve-DeployedImage -Kind app -Name "$BaseName-web"
    if ($WebImage) { Write-Host "web image: keeping deployed $WebImage" } else { $WebImage = 'ghcr.io/moaidhathot/podium/web:latest' }
}

function Invoke-Az {
    # Usage: Invoke-Az @('group', 'create', '-n', $rg). An explicit array avoids clashes with PowerShell common parameters (-o).
    param([string[]] $AzArgs)
    $out = & az @AzArgs 2>&1 | Where-Object { "$_" -notmatch '^WARNING: The behavior of this command has been altered' }
    if ($LASTEXITCODE -ne 0) { throw "az $($AzArgs -join ' ') failed:`n$out" }
    return $out
}

function Write-ParametersFile {
    param([hashtable] $Parameters)
    $file = Join-Path ([IO.Path]::GetTempPath()) "podium-$([guid]::NewGuid().ToString('N')).parameters.json"
    $wrapped = @{}
    foreach ($k in $Parameters.Keys) { $wrapped[$k] = @{ value = $Parameters[$k] } }
    @{
        '$schema'      = 'https://schema.management.azure.com/schemas/2019-04-01/deploymentParameters.json#'
        contentVersion = '1.0.0.0'
        parameters     = $wrapped
    } | ConvertTo-Json -Depth 8 | Set-Content $file -Encoding utf8
    return $file
}

function Get-FoundationOutputs {
    # Resolve from the resource group itself so partial deployments do not block later phases.
    $storage = (Invoke-Az @('storage', 'account', 'list', '-g', $ResourceGroup, '--query', "[?starts_with(name,'st$BaseName')].name | [0]", '-o', 'tsv') | Out-String).Trim()
    $kv = (Invoke-Az @('keyvault', 'list', '-g', $ResourceGroup, '--query', "[?starts_with(name,'kv-$BaseName')].name | [0]", '-o', 'tsv') | Out-String).Trim()
    $dpKey = (& az keyvault key show --vault-name $kv -n podium-dataprotection --query key.kid -o tsv 2>$null | Out-String).Trim()
    $envId = (Invoke-Az @('containerapp', 'env', 'show', '-g', $ResourceGroup, '-n', "cae-$BaseName", '--query', 'id', '-o', 'tsv') | Out-String).Trim()
    $envDomain = (Invoke-Az @('containerapp', 'env', 'show', '-g', $ResourceGroup, '-n', "cae-$BaseName", '--query', 'properties.defaultDomain', '-o', 'tsv') | Out-String).Trim()
    $jobId = (& az containerapp job show -g $ResourceGroup -n "$BaseName-builder" --query id -o tsv 2>$null | Where-Object { "$_" -notmatch 'WARNING' } | Out-String).Trim()
    $web = (Invoke-Az @('identity', 'show', '-g', $ResourceGroup, '-n', "id-$BaseName-web", '-o', 'json') | Out-String) | ConvertFrom-Json
    $deploy = (Invoke-Az @('identity', 'show', '-g', $ResourceGroup, '-n', "id-$BaseName-deploy", '-o', 'json') | Out-String) | ConvertFrom-Json
    $appInsights = (& az monitor app-insights component show -g $ResourceGroup -a "appi-$BaseName" --query connectionString -o tsv 2>$null | Where-Object { "$_" -notmatch 'WARNING' } | Out-String).Trim()
    return [pscustomobject]@{
        storageAccountName       = $storage
        keyVaultName             = $kv
        dataProtectionKeyId      = $dpKey
        environmentId            = $envId
        environmentDefaultDomain = $envDomain
        builderJobId             = $jobId
        webIdentityId            = $web.id
        webIdentityClientId      = $web.clientId
        deployIdentityClientId   = $deploy.clientId
        appInsightsConnectionString = $appInsights
    }
}

if ($Phase -in 'foundation', 'all') {
    Write-Host "== Foundation ($ResourceGroup, $Location)" -ForegroundColor Cyan
    Invoke-Az @('group', 'create', '-n', $ResourceGroup, '-l', $Location, '-o', 'none') | Out-Null
    # GitHub embeds owner/repo ids in the OIDC subject (repo:Owner@id/Repo@id:...); register those exact forms too.
    $extraSubjects = @()
    try {
        $repoInfo = gh api "repos/$GitHubRepository" -q '{id: .id, ownerId: .owner.id}' | ConvertFrom-Json
        $owner, $repo = $GitHubRepository.Split('/')
        $idForm = "$owner@$($repoInfo.ownerId)/$repo@$($repoInfo.id)"
        $extraSubjects = @("repo:${idForm}:ref:refs/heads/main", "repo:${idForm}:environment:production")
    } catch { Write-Warning 'could not resolve GitHub ids via gh; only plain OIDC subjects will be registered' }
    $paramFile = Write-ParametersFile @{
        baseName            = $BaseName
        githubRepository    = $GitHubRepository
        builderImage        = $BuilderImage
        webImage            = $WebImage
        budgetEmail         = $BudgetEmail
        githubExtraSubjects = @($extraSubjects)
    }
    try { Invoke-Az @('deployment', 'group', 'create', '-g', $ResourceGroup, '-n', 'podium-foundation', '-f', (Join-Path $infra 'foundation.bicep'), '-p', "@$paramFile", '-o', 'none') | Out-Null }
    finally { Remove-Item $paramFile -Force -ErrorAction SilentlyContinue }
    $o = Get-FoundationOutputs
    Write-Host "storage: $($o.storageAccountName)  keyvault: $($o.keyVaultName)"
    Write-Host "deploy identity client id (GitHub variable AZURE_CLIENT_ID): $($o.deployIdentityClientId)"
}

if ($Phase -in 'secrets', 'all') {
    Write-Host '== Secrets' -ForegroundColor Cyan
    $o = Get-FoundationOutputs
    $kv = $o.keyVaultName
    # The deploying user needs data-plane access to write secrets (RBAC vault: Owner alone is not enough).
    $me = (Invoke-Az @('ad', 'signed-in-user', 'show', '--query', 'id', '-o', 'tsv') | Out-String).Trim()
    $kvId = (Invoke-Az @('keyvault', 'show', '-n', $kv, '--query', 'id', '-o', 'tsv') | Out-String).Trim()
    Invoke-Az @('role', 'assignment', 'create', '--assignee-object-id', $me, '--assignee-principal-type', 'User', '--role', 'Key Vault Administrator', '--scope', $kvId, '-o', 'none') | Out-Null
    Write-Host 'waiting for RBAC propagation...'
    $deadline = (Get-Date).AddMinutes(3)
    do {
        Start-Sleep -Seconds 10
        $existing = & az keyvault secret list --vault-name $kv --query '[].name' -o tsv 2>$null
        $ok = $LASTEXITCODE -eq 0
    } while (-not $ok -and (Get-Date) -lt $deadline)
    if (-not $ok) { throw 'Key Vault access did not propagate in time; rerun -Phase secrets' }

    if ($existing -notcontains 'podium-signing-key') {
        $bytes = New-Object byte[] 48
        [System.Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
        Invoke-Az @('keyvault', 'secret', 'set', '--vault-name', $kv, '-n', 'podium-signing-key', '--value', ([Convert]::ToBase64String($bytes)), '-o', 'none') | Out-Null
        Write-Host 'created podium-signing-key'
    }
    foreach ($s in 'github-private-key', 'github-client-id', 'github-client-secret', 'github-webhook-secret') {
        if ($existing -notcontains $s) { Write-Warning "secret '$s' missing: run scripts/github-app-manifest.ps1 -KeyVaultName $kv" }
    }
}

if ($Phase -in 'app', 'all') {
    Write-Host '== Web app' -ForegroundColor Cyan
    if (-not $GitHubAppId -or -not $GitHubAppSlug) { throw '-GitHubAppId and -GitHubAppSlug are required for the app phase' }
    $o = Get-FoundationOutputs
    if (-not $o.builderJobId) { throw 'builder job not found; run -Phase foundation first (the builder image must be published)' }
    $params = @{
        baseName                 = $BaseName
        webImage                 = $WebImage
        publicBaseUrl            = "https://$PublicHostname"
        ownerGitHubId            = $OwnerGitHubId
        gitHubAppId              = "$GitHubAppId"
        gitHubAppSlug            = $GitHubAppSlug
        environmentId            = $o.environmentId
        environmentDefaultDomain = $o.environmentDefaultDomain
        builderJobId             = $o.builderJobId
        storageAccountName       = $o.storageAccountName
        keyVaultName             = $o.keyVaultName
        dataProtectionKeyId      = $o.dataProtectionKeyId
        webIdentityId            = $o.webIdentityId
        webIdentityClientId      = $o.webIdentityClientId
        customDomains            = @()
    }
    # Preserve any custom domain binding that already exists so re-deploys do not drop it.
    $existingDomains = & az containerapp show -g $ResourceGroup -n "$BaseName-web" --query 'properties.configuration.ingress.customDomains' -o json 2>$null | Where-Object { "$_" -notmatch 'WARNING' }
    if ($LASTEXITCODE -eq 0 -and $existingDomains -and "$existingDomains" -ne 'null') {
        $doms = @(($existingDomains | Out-String | ConvertFrom-Json) | Where-Object { $_.certificateId } | ForEach-Object { @{ name = $_.name; certificateId = $_.certificateId } })
        if ($doms.Count -gt 0) { $params.customDomains = $doms }
    }
    $params.minReplicas = $MinReplicas
    if ($o.appInsightsConnectionString) { $params.appInsightsConnectionString = $o.appInsightsConnectionString }
    $paramFile = Write-ParametersFile $params
    try { Invoke-Az @('deployment', 'group', 'create', '-g', $ResourceGroup, '-n', 'podium-app', '-f', (Join-Path $infra 'app.bicep'), '-p', "@$paramFile", '-o', 'none') | Out-Null }
    finally { Remove-Item $paramFile -Force -ErrorAction SilentlyContinue }
    $app = (Invoke-Az @('deployment', 'group', 'show', '-g', $ResourceGroup, '-n', 'podium-app', '--query', 'properties.outputs', '-o', 'json') | Out-String) | ConvertFrom-Json
    Write-Host "FQDN: $($app.fqdn.value)"
    Write-Host "DNS for $PublicHostname :"
    Write-Host "  CNAME  $($PublicHostname.Split('.')[0])          -> $($app.fqdn.value)"
    Write-Host "  TXT    asuid.$($PublicHostname.Split('.')[0])    -> $($app.customDomainVerificationId.value)"
}

if ($Phase -in 'domain', 'all') {
    Write-Host "== Custom domain $PublicHostname" -ForegroundColor Cyan
    $envName = "cae-$BaseName"
    $bound = & az containerapp hostname list -g $ResourceGroup -n "$BaseName-web" --query "[?name=='$PublicHostname'].bindingType" -o tsv 2>$null | Where-Object { "$_" -notmatch 'WARNING' }
    if ("$bound".Trim() -eq 'SniEnabled') { Write-Host 'already bound'; return }
    if (-not "$bound".Trim()) { Invoke-Az @('containerapp', 'hostname', 'add', '-g', $ResourceGroup, '-n', "$BaseName-web", '--hostname', $PublicHostname, '-o', 'none') | Out-Null }
    Invoke-Az @('containerapp', 'hostname', 'bind', '-g', $ResourceGroup, '-n', "$BaseName-web", '--hostname', $PublicHostname, '--environment', $envName, '--validation-method', 'CNAME', '-o', 'none') | Out-Null
    Write-Host 'bound with a managed certificate'
}
