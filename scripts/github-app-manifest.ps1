<#
.SYNOPSIS
  Creates the Podium GitHub App through the "app manifest" flow and stores its credentials in Key Vault.
  Nothing secret is written to disk; the private key, client secret and webhook secret go straight to Key Vault.

.DESCRIPTION
  1. Starts a tiny local HTTP listener.
  2. Opens the browser on a page that POSTs the manifest to GitHub.
  3. You confirm the app in GitHub; GitHub redirects back with a one-time code.
  4. The code is exchanged for the app credentials and stored as Key Vault secrets.

.EXAMPLE
  ./scripts/github-app-manifest.ps1 -KeyVaultName kv-podium-xxxx -PublicHostname slides.moaid.codes
#>
param(
    [Parameter(Mandatory)] [string] $KeyVaultName,
    [string] $PublicHostname = 'slides.moaid.codes',
    [string] $AppName = 'Podium Slides',
    [int] $Port = 8123,
    [string] $DevCallback = 'http://localhost:5187/signin-github',
    [string] $Browser,
    [int] $TimeoutMinutes = 20
)
$ErrorActionPreference = 'Stop'

$manifest = @{
    name          = $AppName
    url           = "https://$PublicHostname"
    description   = 'Serves Slidev and presenterm decks straight from GitHub repositories.'
    public        = $false
    redirect_url  = "http://localhost:$Port/callback"
    callback_urls = @("https://$PublicHostname/signin-github", $DevCallback)
    setup_url     = "https://$PublicHostname/sources"
    setup_on_update = $false
    hook_attributes = @{ url = "https://$PublicHostname/api/github/webhook"; active = $true }
    default_permissions = @{ contents = 'read'; metadata = 'read' }
    default_events = @('push', 'repository')   # installation* events are always delivered and must not be listed
} | ConvertTo-Json -Depth 5 -Compress

$state = [Guid]::NewGuid().ToString('N')
$encoded = [System.Net.WebUtility]::HtmlEncode($manifest)
$page = @"
<!doctype html><html><body style="font-family:system-ui;padding:2rem">
<h2>Creating the GitHub App "$AppName"</h2>
<p>You will be redirected to GitHub to confirm. If nothing happens, click the button.</p>
<form id="f" method="post" action="https://github.com/settings/apps/new?state=$state">
<input type="hidden" name="manifest" value="$encoded">
<button type="submit">Create GitHub App</button>
</form>
<script>document.getElementById('f').submit();</script>
</body></html>
"@

$listener = [System.Net.HttpListener]::new()
$listener.Prefixes.Add("http://localhost:$Port/")
$listener.Start()
Write-Host "Listening on http://localhost:$Port/ ... opening browser"
if ($Browser) { Start-Process -FilePath $Browser -ArgumentList "http://localhost:$Port/" } else { Start-Process "http://localhost:$Port/" }

$code = $null
$deadline = (Get-Date).AddMinutes($TimeoutMinutes)
try {
    while (-not $code -and (Get-Date) -lt $deadline) {
        $task = $listener.GetContextAsync()
        while (-not $task.Wait(1000)) { if ((Get-Date) -gt $deadline) { throw 'Timed out waiting for GitHub redirect' } }
        $ctx = $task.Result
        $path = $ctx.Request.Url.AbsolutePath
        if ($path -eq '/callback') {
            $q = [System.Web.HttpUtility]::ParseQueryString($ctx.Request.Url.Query)
            if ($q['state'] -ne $state) { $body = 'state mismatch'; $ctx.Response.StatusCode = 400 }
            else { $code = $q['code']; $body = '<h2>Done. You can close this tab; credentials are being stored in Key Vault.</h2>' }
        }
        else { $body = $page }
        $bytes = [Text.Encoding]::UTF8.GetBytes($body)
        $ctx.Response.ContentType = 'text/html; charset=utf-8'
        $ctx.Response.ContentLength64 = $bytes.Length
        $ctx.Response.OutputStream.Write($bytes, 0, $bytes.Length)
        $ctx.Response.Close()
    }
}
finally { $listener.Stop() }

if (-not $code) { throw 'No code received' }

Write-Host 'Exchanging code for credentials...'
$app = Invoke-RestMethod -Method Post -Uri "https://api.github.com/app-manifests/$code/conversions" -Headers @{ Accept = 'application/vnd.github+json'; 'User-Agent' = 'Podium-setup' } -TimeoutSec 60

Write-Host "App created: id=$($app.id) slug=$($app.slug) html_url=$($app.html_url)"

function Set-KvSecret([string] $name, [string] $value) {
    $tmp = New-TemporaryFile
    try {
        Set-Content -Path $tmp -Value $value -NoNewline -Encoding utf8
        az keyvault secret set --vault-name $KeyVaultName -n $name --file $tmp -o none
        if ($LASTEXITCODE -ne 0) { throw "failed to store $name" }
    }
    finally { Remove-Item $tmp -Force -ErrorAction SilentlyContinue }
}

Set-KvSecret 'github-private-key' $app.pem
Set-KvSecret 'github-client-id' $app.client_id
Set-KvSecret 'github-client-secret' $app.client_secret
Set-KvSecret 'github-webhook-secret' $app.webhook_secret
Write-Host "Stored 4 secrets in $KeyVaultName."
Write-Host ''
Write-Host 'Next:' -ForegroundColor Cyan
Write-Host "  ./infra/deploy.ps1 -Phase app -GitHubAppId $($app.id) -GitHubAppSlug $($app.slug)"
Write-Host "  then install the app on your slides repositories: $($app.html_url)/installations/new"

[pscustomobject]@{ AppId = $app.id; Slug = $app.slug; Url = $app.html_url }
