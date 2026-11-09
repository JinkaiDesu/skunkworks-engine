#Requires -Version 7.2
<#
.SYNOPSIS
  The Skunkworks website demo archive (skunkworks-v<version>-demo.zip) works on its own: unpacked far from any source
  or build folder, every file matches its receipt, the player opens its window and reports that it started with input
  capture running, keeps running, and quits cleanly when its window is closed.
#>
param(
    [Parameter(Mandatory)][string]$Archive,
    [Parameter(Mandatory)][string]$Version
)
$ErrorActionPreference = 'Stop'
$tag = [guid]::NewGuid().ToString('N').Substring(0, 8)
$root = Join-Path ([IO.Path]::GetTempPath()) "skunkworks release $tag"
$log = Join-Path ([IO.Path]::GetTempPath()) "skunkworks player $tag.log"
$passed = 0
function Check([bool]$Okay, [string]$Name) { if (-not $Okay) { throw "FAIL: $Name" }; $script:passed++; Write-Host "PASS $Name" }
# The player's log, read while the player still holds it open.
function Read-Log {
    if (!(Test-Path -LiteralPath $log)) { return '' }
    $stream = [IO.File]::Open($log, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]'ReadWrite, Delete')
    try { [IO.StreamReader]::new($stream).ReadToEnd() } finally { $stream.Dispose() }
}
function Wait-Until([scriptblock]$Condition, [int]$Seconds) {
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    do { if (& $Condition) { return $true }; Start-Sleep -Milliseconds 250 } while ([DateTime]::UtcNow -lt $deadline)
    $false
}

$player = $null
try {
    Expand-Archive -LiteralPath $Archive -DestinationPath $root
    $receipt = Get-Content -LiteralPath (Join-Path $root 'release.receipt.json') -Raw | ConvertFrom-Json
    Check ($receipt.version -eq $Version) "the receipt names $Version"
    $bad = @($receipt.files | Where-Object { (Get-FileHash -LiteralPath (Join-Path $root $_.path) -Algorithm SHA256).Hash.ToLowerInvariant() -ne $_.sha256 })
    Check ($bad.Count -eq 0 -and @($receipt.files).Count -eq @(Get-ChildItem -LiteralPath $root -Recurse -File).Count - 1) 'every file matches the receipt, and nothing else is there'

    # Start-Process joins -ArgumentList with spaces and quotes nothing; the log path has spaces.
    $player = Start-Process -FilePath (Join-Path $root 'Skunkworks/Skunkworks.exe') -PassThru -WorkingDirectory ([IO.Path]::GetTempPath()) `
        -ArgumentList @('-screen-fullscreen', '0', '-screen-width', '1024', '-screen-height', '640', '-logFile', ('"{0}"' -f $log))
    $null = $player.Handle   # keeps the exit code readable after the process ends

    [void](Wait-Until { $player.HasExited -or (Read-Log) -match '\[skunkworks\] started' } 120)
    Check ((Read-Log) -match '\[skunkworks\] started: .*capturing=True') 'the player starts and captures input'
    Check (Wait-Until { $player.Refresh(); $player.MainWindowHandle -ne [IntPtr]::Zero } 30) 'the player opens a window'
    Start-Sleep -Seconds 3
    Check (-not $player.HasExited) 'the player keeps running'

    [void]$player.CloseMainWindow()
    Check ($player.WaitForExit(60000) -and $player.ExitCode -eq 0) 'the player quits cleanly when its window is closed'
    Check ((Read-Log) -match '\[skunkworks\] stopped') 'the player reports that it stopped'
} finally {
    if ($player -and -not $player.HasExited) { $player.Kill($true) }
    foreach ($path in $root, $log) { Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction SilentlyContinue }
}
"SKUNKWORKS_RELEASE_OK tests=$passed"
