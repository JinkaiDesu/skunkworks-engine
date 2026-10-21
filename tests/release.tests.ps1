#Requires -Version 7.2
<#
.SYNOPSIS
  The Skunkworks website demo archive (skunkworks-v<version>-demo.zip) works on its own: unpacked far from any source
  or build folder, every file matches its receipt, the player launches and answers skunkworks.wire/1 (asked by
  tests/host, a client built from this repository's codec), its terminal stays stopped without a configuration, and a
  configuration named by SKUNK_TERMINAL_CONFIG starts that shell.
#>
param(
    [Parameter(Mandatory)][string]$Archive,
    [Parameter(Mandatory)][string]$Version,
    [string]$UnityVersion = '6000.5.6f1'
)
$ErrorActionPreference = 'Stop'
$tag = [guid]::NewGuid().ToString('N').Substring(0, 8)
$root = Join-Path ([IO.Path]::GetTempPath()) "skunkworks release $tag"
$user = Join-Path ([IO.Path]::GetTempPath()) "skunkworks user $tag"
$hostBuild = Join-Path ([IO.Path]::GetTempPath()) "skunkworks host $tag"
$passed = 0
function Check([bool]$Okay, [string]$Name) { if (-not $Okay) { throw "FAIL: $Name" }; $script:passed++; Write-Host "PASS $Name" }

# Launches the unpacked player with its own user folder and an optional terminal configuration; returns it once its pipe exists.
function Start-Player([string]$Config) {
    $saved = @{ SKUNK_USER = $env:SKUNK_USER; SKUNK_TERMINAL_CONFIG = $env:SKUNK_TERMINAL_CONFIG }
    try {
        $env:SKUNK_USER = $user
        if ($Config) { $env:SKUNK_TERMINAL_CONFIG = $Config } else { Remove-Item Env:SKUNK_TERMINAL_CONFIG -ErrorAction SilentlyContinue }
        # Start-Process joins -ArgumentList with spaces and quotes nothing; the log path has spaces.
        $player = Start-Process -FilePath (Join-Path $root 'Skunkworks/Skunkworks.exe') -PassThru -WorkingDirectory ([IO.Path]::GetTempPath()) `
            -ArgumentList @('-screen-fullscreen', '0', '-screen-width', '1024', '-screen-height', '640', '-logFile', ('"{0}"' -f (Join-Path $user "player-$([guid]::NewGuid().ToString('N').Substring(0, 6)).log")))
    } finally { foreach ($name in $saved.Keys) { [Environment]::SetEnvironmentVariable($name, $saved[$name]) } }
    $pipe = "skunkworks.unity.$($player.Id)"
    for ($i = 0; $i -lt 120 -and -not (Test-Path -LiteralPath "\\.\pipe\$pipe") -and -not $player.HasExited; $i++) { Start-Sleep -Milliseconds 500 }
    Check (Test-Path -LiteralPath "\\.\pipe\$pipe") "the player launches and opens its pipe ($pipe)"
    [pscustomobject]@{ Process = $player; Pipe = $pipe }
}
# The player's answers through tests/host: "unity <version>", "roots ...", "terminal <status>".
function Probe($Player) {
    $lines = @(& $script:hostExe probe $Player.Pipe 2>&1 | ForEach-Object { "$_" })
    if ($LASTEXITCODE -ne 0) { throw "probe failed: $($lines -join ' ')" }
    $answers = @{}; foreach ($line in $lines) { $name, $value = $line -split ' ', 2; $answers[$name] = $value }
    $answers
}
function Stop-Player($Player) {
    & $script:hostExe quit $Player.Pipe | Out-Null
    Check ($LASTEXITCODE -eq 0 -and $Player.Process.WaitForExit(60000)) 'the player quits when asked'
}

$players = @()
try {
    & dotnet build (Join-Path $PSScriptRoot 'host/SkunkworksHost.csproj') -c Release -nologo -v:q --artifacts-path $hostBuild "-p:SkunkworksSource=$(Split-Path $PSScriptRoot -Parent)"
    if ($LASTEXITCODE) { throw 'the test client (tests/host) did not build' }
    $script:hostExe = Join-Path $hostBuild 'bin/SkunkworksHost/release/SkunkworksHost.exe'

    Expand-Archive -LiteralPath $Archive -DestinationPath $root
    $receipt = Get-Content -LiteralPath (Join-Path $root 'release.receipt.json') -Raw | ConvertFrom-Json
    Check ($receipt.version -eq $Version) "the receipt names $Version"
    $bad = @($receipt.files | Where-Object { (Get-FileHash -LiteralPath (Join-Path $root $_.path) -Algorithm SHA256).Hash.ToLowerInvariant() -ne $_.sha256 })
    Check ($bad.Count -eq 0 -and @($receipt.files).Count -eq @(Get-ChildItem -LiteralPath $root -Recurse -File).Count - 1) 'every file matches the receipt, and nothing else is there'
    New-Item -ItemType Directory -Force $user | Out-Null

    # Without a configuration the player runs and its terminal says why it has no shell.
    $player = Start-Player ''; $players += $player
    $answers = Probe $player
    Check ($answers['unity'] -eq $UnityVersion) "the reflection server answers (Unity $($answers['unity']))"
    Check ($answers['roots'] -eq 'reflection window terminal') 'the generic roots are registered (reflection, window, terminal)'
    Check ($answers['terminal'] -match '^Stopped: No terminal configuration') "the terminal stays stopped without a configuration ($($answers['terminal']))"
    Stop-Player $player

    # A configuration named by SKUNK_TERMINAL_CONFIG starts its shell.
    $config = Join-Path $user 'terminal.config'
    Set-Content -LiteralPath $config -Encoding utf8 -Value "<?xml version=`"1.0`" encoding=`"utf-8`"?>`n<terminal executable=`"%SystemRoot%\System32\cmd.exe`" arguments=`"`" />"
    $player = Start-Player $config; $players += $player
    $running = $false
    for ($i = 0; $i -lt 60 -and -not $running; $i++) { $running = (Probe $player)['terminal'] -match '^Running'; if (-not $running) { Start-Sleep -Milliseconds 500 } }
    Check $running 'SKUNK_TERMINAL_CONFIG starts the shell it names'
    Stop-Player $player
} finally {
    foreach ($player in $players) { if (-not $player.Process.HasExited) { $player.Process.Kill($true) } }
    foreach ($folder in $root, $user, $hostBuild) { Remove-Item -LiteralPath $folder -Recurse -Force -ErrorAction SilentlyContinue }
}
"SKUNKWORKS_RELEASE_OK tests=$passed"
