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
    [string] $WebImage = 'ghcr.io/moaidhathot/podium-web:latest',
    [string] $BuilderImage = 'ghcr.io/moaidhathot/podium-builder:latest'
)
$ErrorActionPreference = 'Stop'
$infra = $PSScriptRoot

function Invoke-Az {
    param([Parameter(ValueFromRemainingArguments)] [string[]] $Args)
    $out = & az @Args 2>&1
    if ($LASTEXITCODE -ne 0) { throw "az $($Args -join ' ') failed:`n$out" }
    return $out
}

function Get-FoundationOutputs {
    $json = Invoke-Az deployment group show -g $ResourceGroup -n podium-foundation --query properties.outputs -o json
    return ($json | ConvertFrom-Json)
}

if ($Phase -in 'foundation', 'all') {
    Write-Host "== Foundation ($ResourceGroup, $Location)" -ForegroundColor Cyan
    Invoke-Az group create -n $ResourceGroup -l $Location -o none
    Invoke-Az deployment group create -g $ResourceGroup -n podium-foundation -f (Join-Path $infra 'foundation.bicep') `
        -p baseName=$BaseName githubRepository=$GitHubRepository builderImage=$BuilderImage budgetEmail=$BudgetEmail -o none
    $o = Get-FoundationOutputs
    Write-Host "storage: $($o.storageAccountName.value)  keyvault: $($o.keyVaultName.value)"
    Write-Host "deploy identity client id (GitHub variable AZURE_CLIENT_ID): $($o.deployIdentityClientId.value)"
}

if ($Phase -in 'secrets', 'all') {
    Write-Host '== Secrets' -ForegroundColor Cyan
    $o = Get-FoundationOutputs
    $kv = $o.keyVaultName.value
    # The deploying user needs data-plane access to write secrets (RBAC vault: Owner alone is not enough).
    $me = Invoke-Az ad signed-in-user show --query id -o tsv
    $kvId = Invoke-Az keyvault show -n $kv --query id -o tsv
    Invoke-Az role assignment create --assignee-object-id $me --assignee-principal-type User --role 'Key Vault Administrator' --scope $kvId -o none
    Write-Host 'waiting for RBAC propagation...'
    $deadline = (Get-Date).AddMinutes(3)
    do {
        Start-Sleep -Seconds 10
        $existing = & az keyvault secret list --vault-name $kv --query "[].name" -o tsv 2>$null
        $ok = $LASTEXITCODE -eq 0
    } while (-not $ok -and (Get-Date) -lt $deadline)
    if (-not $ok) { throw 'Key Vault access did not propagate in time; rerun -Phase secrets' }

    if ($existing -notcontains 'podium-signing-key') {
        $bytes = New-Object byte[] 48
        [System.Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
        Invoke-Az keyvault secret set --vault-name $kv -n podium-signing-key --value ([Convert]::ToBase64String($bytes)) -o none
        Write-Host 'created podium-signing-key'
    }
    foreach ($s in 'github-private-key', 'github-client-id', 'github-client-secret', 'github-webhook-secret') {
        if ($existing -notcontains $s) { Write-Warning "secret '$s' missing: run scripts/github-app-manifest.ps1 first" }
    }
}

if ($Phase -in 'app', 'all') {
    Write-Host '== Web app' -ForegroundColor Cyan
    if (-not $GitHubAppId -or -not $GitHubAppSlug) { throw '-GitHubAppId and -GitHubAppSlug are required for the app phase' }
    $o = Get-FoundationOutputs
    $params = @(
        "baseName=$BaseName", "webImage=$WebImage", "publicBaseUrl=https://$PublicHostname", "ownerGitHubId=$OwnerGitHubId",
        "gitHubAppId=$GitHubAppId", "gitHubAppSlug=$GitHubAppSlug",
        "environmentId=$($o.environmentId.value)", "builderJobId=$($o.builderJobId.value)", "storageAccountName=$($o.storageAccountName.value)",
        "keyVaultName=$($o.keyVaultName.value)", "webIdentityId=$($o.webIdentityId.value)", "webIdentityClientId=$($o.webIdentityClientId.value)"
    )
    # Preserve any custom domain binding that already exists so re-deploys do not drop it.
    $existingDomains = & az containerapp show -g $ResourceGroup -n "$BaseName-web" --query "properties.configuration.ingress.customDomains" -o json 2>$null
    if ($LASTEXITCODE -eq 0 -and $existingDomains -and $existingDomains -ne 'null') {
        $doms = ($existingDomains | ConvertFrom-Json) | Where-Object { $_.certificateId } | ForEach-Object { @{ name = $_.name; certificateId = $_.certificateId } }
        if ($doms) { $params += "customDomains=$(ConvertTo-Json @($doms) -Compress)" }
    }
    Invoke-Az deployment group create -g $ResourceGroup -n podium-app -f (Join-Path $infra 'app.bicep') -p @params -o none
    $app = Invoke-Az deployment group show -g $ResourceGroup -n podium-app --query properties.outputs -o json | ConvertFrom-Json
    Write-Host "FQDN: $($app.fqdn.value)"
    Write-Host "DNS for $PublicHostname :"
    Write-Host "  CNAME  $($PublicHostname.Split('.')[0])          -> $($app.fqdn.value)"
    Write-Host "  TXT    asuid.$($PublicHostname.Split('.')[0])    -> $($app.customDomainVerificationId.value)"
}

if ($Phase -in 'domain', 'all') {
    Write-Host "== Custom domain $PublicHostname" -ForegroundColor Cyan
    $env = "cae-$BaseName"
    $bound = & az containerapp hostname list -g $ResourceGroup -n "$BaseName-web" --query "[?name=='$PublicHostname'].bindingType" -o tsv 2>$null
    if ($bound -eq 'SniEnabled') { Write-Host 'already bound'; return }
    if (-not $bound) { Invoke-Az containerapp hostname add -g $ResourceGroup -n "$BaseName-web" --hostname $PublicHostname -o none }
    Invoke-Az containerapp hostname bind -g $ResourceGroup -n "$BaseName-web" --hostname $PublicHostname --environment $env --validation-method CNAME -o none
    Write-Host 'bound with a managed certificate'
}
