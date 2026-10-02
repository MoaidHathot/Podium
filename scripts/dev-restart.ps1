# Development helper: (re)starts Podium.Web fully detached from the calling console so automation never blocks on it.
# Usage: pwsh scripts/dev-restart.ps1 [-Port 5187] [-Build]
param(
    [int] $Port = 5187,
    [switch] $Build
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$stateDir = Join-Path $env:TEMP 'opencode'
New-Item -ItemType Directory -Force -Path $stateDir | Out-Null
$pidFile = Join-Path $stateDir 'podium-web.pid'
$log = Join-Path $stateDir 'podium-web.log'

if (Test-Path $pidFile) {
    Stop-Process -Id (Get-Content $pidFile) -Force -ErrorAction SilentlyContinue
}
# Also stop any stray instance holding the build output (e.g. started from another console).
Get-CimInstance Win32_Process -Filter "Name = 'dotnet.exe' or Name = 'Podium.Web.exe'" |
    Where-Object { $_.CommandLine -like '*Podium.Web*' } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
Start-Sleep -Milliseconds 800

if ($Build) {
    dotnet build (Join-Path $root 'Podium.slnx') -nologo -v q | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'build failed' }
}

# Azurite backs the Development configuration (UseDevelopmentStorage=true); start it when it is not listening.
if (-not (Get-NetTCPConnection -LocalPort 10000 -State Listen -ErrorAction SilentlyContinue)) {
    $azurite = Join-Path $env:APPDATA 'npm\node_modules\azurite\dist\src\azurite.js'
    if (Test-Path $azurite) {
        $azDir = Join-Path $stateDir 'azurite'
        New-Item -ItemType Directory -Force -Path $azDir | Out-Null
        $hidden = New-CimInstance -ClassName Win32_ProcessStartup -ClientOnly -Property @{ ShowWindow = [uint16]0 }
        Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{ CommandLine = "node `"$azurite`" --silent --location `"$azDir`" --skipApiVersionCheck"; ProcessStartupInformation = $hidden } | Out-Null
        $azDeadline = (Get-Date).AddSeconds(30)
        while (-not (Get-NetTCPConnection -LocalPort 10000 -State Listen -ErrorAction SilentlyContinue) -and (Get-Date) -lt $azDeadline) { Start-Sleep -Milliseconds 500 }
        Write-Output 'Azurite started'
    }
    else { Write-Warning 'Azurite not installed (npm i -g azurite); storage calls will fail' }
}

$token = (gh auth token)
# The apphost (Podium.Web.exe) rather than "dotnet Podium.Web.dll": other tooling on this machine has been observed
# killing dotnet.exe processes by name, which took the dev server down mid-test.
$exe = Join-Path $root 'src\Podium.Web\bin\Debug\net10.0\Podium.Web.exe'
$cwd = Join-Path $root 'src\Podium.Web'
# The previous cmd wrapper may still hold the log open for a moment after its child died; a failed redirect would
# silently skip the launch, so wait until the file is writable (or rotate it).
$logDeadline = (Get-Date).AddSeconds(10)
while ((Get-Date) -lt $logDeadline) {
    try { $fs = [IO.File]::Open($log, 'OpenOrCreate', 'Write', 'None'); $fs.SetLength(0); $fs.Dispose(); break }
    catch { Start-Sleep -Milliseconds 300 }
}
if ((Get-Date) -ge $logDeadline) { $log = "$log.$((Get-Date).ToString('HHmmss'))"; Write-Warning "log still locked; using $log" }
# Environment is passed through a small cmd wrapper so nothing needs to live in a config file.
$cmd = "cmd.exe /c `"set GitHub__Token=$token&& set ASPNETCORE_ENVIRONMENT=Development&& set ASPNETCORE_URLS=http://localhost:$Port&& cd /d `"$cwd`" && `"$exe`" > `"$log`" 2>&1`""
# ShowWindow = 0 (SW_HIDE): no console window that could be closed by accident, killing the server with it.
$startup = New-CimInstance -ClassName Win32_ProcessStartup -ClientOnly -Property @{ ShowWindow = [uint16]0 }
$r = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{ CommandLine = $cmd; ProcessStartupInformation = $startup }
if ($r.ReturnValue -ne 0) { throw "Win32_Process.Create failed: $($r.ReturnValue)" }
$r.ProcessId | Out-File $pidFile -NoNewline

$deadline = (Get-Date).AddSeconds(25)
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 1
    try { if ((Invoke-WebRequest "http://localhost:$Port/healthz" -UseBasicParsing -TimeoutSec 3).StatusCode -eq 200) { Write-Output "Podium.Web up on http://localhost:$Port (cmd pid $($r.ProcessId))"; exit 0 } } catch { }
}
Write-Output 'Podium.Web did not become healthy in time; last log lines:'
Get-Content $log -Tail 30 -ErrorAction SilentlyContinue
exit 1
