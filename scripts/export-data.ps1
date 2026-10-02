<#
.SYNOPSIS
  Exports Podium's index (Table storage) to JSON files and, optionally, the artifacts of the served builds.

.DESCRIPTION
  Everything Podium knows lives in one storage account: tables for sources, decks, builds, grants, share links and
  view history, and one blob container per build. This script copies the tables to JSON (one file per table) so the
  index can be inspected, backed up or imported elsewhere, and can also download the artifacts of every deck's
  currently served build. It uses your Azure CLI login (Entra RBAC), never account keys: you need "Storage Table Data
  Reader" and, with -IncludeArtifacts, "Storage Blob Data Reader" on the account (the Owner role alone is not enough).

.EXAMPLE
  ./scripts/export-data.ps1 -ResourceGroup rg-podium -Out ./export
  ./scripts/export-data.ps1 -ResourceGroup rg-podium -Out ./export -IncludeArtifacts
#>
param(
    [string] $ResourceGroup = 'rg-podium',
    [string] $StorageAccount,
    [string] $Out = (Join-Path (Get-Location) ("podium-export-" + (Get-Date -Format 'yyyyMMdd-HHmmss'))),
    # Table name prefix (Storage:TablePrefix in the app; "podium" by default).
    [string] $TablePrefix = 'podium',
    [switch] $IncludeArtifacts
)
$ErrorActionPreference = 'Stop'

function Invoke-Az([string[]] $cliArgs) {
    # az may print warnings or a first-run banner before the payload; keep only the JSON document.
    $raw = @(& az @cliArgs '-o' 'json' 2>&1 | ForEach-Object { "$_" })
    if ($LASTEXITCODE -ne 0) { throw "az $($cliArgs -join ' ') failed: $($raw -join "`n")" }
    $start = [array]::FindIndex([string[]]$raw, [Predicate[string]]{ param($l) $l -match '^\s*[\[{"]' })
    if ($start -lt 0) { throw "az $($cliArgs -join ' ') returned no JSON: $($raw -join "`n")" }
    ($raw[$start..($raw.Length - 1)] -join "`n") | ConvertFrom-Json
}

if (-not $StorageAccount) {
    $StorageAccount = Invoke-Az @('storage', 'account', 'list', '-g', $ResourceGroup, '--query', "[?starts_with(name, 'stpodium')].name | [0]")
    if (-not $StorageAccount) { throw "No Podium storage account found in $ResourceGroup; pass -StorageAccount." }
}
New-Item -ItemType Directory -Force -Path $Out | Out-Null
Write-Host "Exporting $StorageAccount to $Out"

$tables = 'sources', 'decks', 'builds', 'activebuilds', 'grants', 'sharelinks', 'views', 'deckviews', 'recentdismissals'
$existing = @(Invoke-Az @('storage', 'table', 'list', '--account-name', $StorageAccount, '--auth-mode', 'login', '--query', '[].name'))
$decks = @()
foreach ($t in $tables) {
    $items = @()
    if ($existing -notcontains "$TablePrefix$t") { Write-Host ("  {0,-18} (not created yet)" -f $t); continue }
    $marker = $null
    do {
        $q = @('storage', 'entity', 'query', '--account-name', $StorageAccount, '--auth-mode', 'login', '--table-name', "$TablePrefix$t", '--num-results', '1000')
        if ($marker) { $q += @('--marker') + $marker }
        $page = Invoke-Az $q
        $items += $page.items
        # Continuation token comes back as an object; az expects it as key=value pairs. An empty object means done.
        $nm = $page.nextMarker
        $marker = if ($nm -and @($nm.PSObject.Properties).Count -gt 0) { @($nm.PSObject.Properties | ForEach-Object { "$($_.Name)=$($_.Value)" }) } else { $null }
    } while ($marker)
    # Entities store the record as JSON in a "Json" column; unwrap it so the export is directly readable.
    $records = $items | ForEach-Object { if ($_.Json) { $_.Json | ConvertFrom-Json } else { $_ } }
    $records | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $Out "$t.json") -Encoding utf8
    if ($t -eq 'decks') { $decks = $records }
    Write-Host ("  {0,-18} {1,6} records" -f $t, @($items).Count)
}

if ($IncludeArtifacts) {
    $artifactsDir = Join-Path $Out 'artifacts'
    foreach ($d in $decks | Where-Object { $_.CurrentBuildId -and -not $_.Archived }) {
        # Container name mirrors BlobArtifactStore.ContainerName: b-{buildId}-{first 12 hex chars of SHA-256(slug)}
        $sha = [System.Security.Cryptography.SHA256]::HashData([System.Text.Encoding]::UTF8.GetBytes($d.Slug))
        $hash = ([System.BitConverter]::ToString($sha) -replace '-', '').Substring(0, 12).ToLowerInvariant()
        $container = "b-$($d.CurrentBuildId.ToLowerInvariant())-$hash"
        $dest = Join-Path $artifactsDir $d.Slug
        New-Item -ItemType Directory -Force -Path $dest | Out-Null
        Write-Host "  artifacts: $($d.Slug) <- $container"
        & az storage blob download-batch --account-name $StorageAccount --auth-mode login -s $container -d $dest -o none 2>&1 | Where-Object { "$_" -notmatch '^WARNING' } | Out-Null
        if ($LASTEXITCODE -ne 0) { Write-Warning "download of $container failed" }
    }
}
Write-Host "Done: $Out"
