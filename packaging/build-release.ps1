#Requires -Version 7.2
<#
.SYNOPSIS
  The Skunkworks website demo, skunkworks-v<version>-demo: a fresh Windows player from this repository's Unity project.
  Skunkworks depends only on Unity and the .NET CLR; it bundles no shell, client or other project.

.DESCRIPTION
  -Acceptance     tests/demo-release.ps1, run on the result
  The release is Skunkworks/ (the player) and Licenses/, with Licenses/Provenance/build-inputs.json and
  release.receipt.json. The player has no terminal configuration: a product supplies one with SKUNK_TERMINAL_CONFIG.

.EXAMPLE
  pwsh packaging/build-release.ps1 -UnityEditor <Unity.exe> -Acceptance tests/demo-release.ps1
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$UnityEditor,
    [Parameter(Mandatory)][string]$Acceptance,
    [string]$OutputDirectory,                                     # default: <this repository>/Release/skunkworks-v<version>-demo
    [string]$WorkDirectory,                                       # default: <this repository>/Release/work
    [string]$PlayerReleaseRoot = $env:SKUNK_PLAYER_RELEASE_ROOT,  # reuse a verified player built from identical sources
    [ValidatePattern('^\d+\.\d+(\.\d+)?([.-][A-Za-z0-9]+)*$')][string]$Version   # default: component.json
)
. (Join-Path $PSScriptRoot 'common.ps1')
Assert-PackagingHost
$repoRoot = Split-Path $PSScriptRoot -Parent
$component = Read-Json (Join-Path $repoRoot 'component.json')
if (!$Version) { $Version = $component.version }
if (!$OutputDirectory) { $OutputDirectory = Join-Path $repoRoot "Release\skunkworks-v$Version-demo" }
if (!$WorkDirectory) { $WorkDirectory = Join-Path $repoRoot 'Release\work' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$WorkDirectory = [IO.Path]::GetFullPath($WorkDirectory)
$UnityEditor = [IO.Path]::GetFullPath($UnityEditor)
if (!(Test-Path -LiteralPath $UnityEditor -PathType Leaf)) { throw "Unity Editor not found at explicit -UnityEditor: $UnityEditor" }
if (!(Test-Path -LiteralPath $Acceptance -PathType Leaf)) { throw "The acceptance is missing: $Acceptance" }
$unityProject = Join-Path $repoRoot 'Unity'
$unityVersion = (Get-Content -LiteralPath (Join-Path $unityProject 'ProjectSettings\ProjectVersion.txt') | Where-Object { $_ -match '^m_EditorVersion: ' }) -replace '^m_EditorVersion: ', ''
$editorProduct = [Diagnostics.FileVersionInfo]::GetVersionInfo($UnityEditor).ProductVersion.Trim()
$editorVersion = ($editorProduct -split '_')[0]
if ($editorVersion -cne $unityVersion) { throw "Unity Editor version mismatch: required $unityVersion, supplied $editorVersion." }
Assert-EmptyDirectory $OutputDirectory
[IO.Directory]::CreateDirectory($WorkDirectory) | Out-Null
& (Join-Path $unityProject 'Packages\com.skunkworks.protocol\generate.ps1') -Check

$unityFiles = @(foreach ($folder in @('Assets', 'Packages', 'ProjectSettings')) {
        Get-TreeFiles (Join-Path $unityProject $folder) | ForEach-Object {
            [ordered]@{ path = "Unity/$folder/$($_.path)"; size = $_.size; sha256 = $_.sha256 }
        }
    })
$unityLog = Join-Path $WorkDirectory "unity-build-$([guid]::NewGuid().ToString('N')).log"
$playerReuseReceipt = $null
# A player offered through SKUNK_PLAYER_RELEASE_ROOT (a CI cache) is reused only when it was built from exactly these
# sources with this editor; otherwise a fresh player is built. An explicit -PlayerReleaseRoot must match.
$reuseOffered = !$PSBoundParameters.ContainsKey('PlayerReleaseRoot') -and [bool]$PlayerReleaseRoot
if ($PlayerReleaseRoot) {
    $PlayerReleaseRoot = [IO.Path]::GetFullPath($PlayerReleaseRoot)
    try {
        $previousInputs = Read-Json (Join-Path $PlayerReleaseRoot 'Licenses/Provenance/build-inputs.json')
        $previousReceipt = Read-Json (Join-Path $PlayerReleaseRoot 'release.receipt.json')
        if ((Get-TextHash ($previousInputs | ConvertTo-Json -Depth 100 -Compress)) -cne $previousReceipt.inputSha256 -or
            $previousInputs.unity.version -cne $unityVersion -or $previousInputs.unity.editorSha256 -cne (Get-Sha256 $UnityEditor) -or
            @($previousInputs.unity.files).Count -ne $unityFiles.Count) { throw 'Reusable player source/compiler receipt differs.' }
        $previousFiles = @{}; foreach ($file in $previousInputs.unity.files) { $previousFiles[$file.path] = $file.sha256 }
        foreach ($file in $unityFiles) { if (!$previousFiles.ContainsKey($file.path) -or $previousFiles[$file.path] -cne $file.sha256) { throw "Reusable player source differs: $($file.path)" } }
    } catch {
        if (!$reuseOffered) { throw }
        Write-Warning "SKUNK_PLAYER_RELEASE_ROOT is not reusable for these sources ($($_.Exception.Message)); building a fresh player."
        $PlayerReleaseRoot = $null
    }
}
if ($PlayerReleaseRoot) {
    $previousPlayer = Join-Path $PlayerReleaseRoot 'Skunkworks'
    if ($OutputDirectory.StartsWith($previousPlayer + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Output cannot be inside the reusable player.' }
    Copy-Item -LiteralPath $previousPlayer -Destination (Join-Path $OutputDirectory 'Skunkworks') -Recurse
    $copiedFiles = @(Get-TreeFiles (Join-Path $OutputDirectory 'Skunkworks'))
    $expected = @{}; foreach ($file in $previousReceipt.files) { if ($file.path.StartsWith('Skunkworks/', [StringComparison]::Ordinal)) { $expected[$file.path.Substring(11)] = $file.sha256 } }
    if ($copiedFiles.Count -ne $expected.Count) { throw 'Reusable player payload count differs.' }
    foreach ($file in $copiedFiles) { if (!$expected.ContainsKey($file.path) -or $expected[$file.path] -cne $file.sha256) { throw "Reusable player payload differs: $($file.path)" } }
    $playerReuseReceipt = Get-Sha256 (Join-Path $PlayerReleaseRoot 'release.receipt.json')
    Write-Host 'Reused player: exact source/compiler identity and copied payload hashes verified.'
} else {
    $previousOutput = $env:UNITY_BUILD_OUTPUT
    try {
        $env:UNITY_BUILD_OUTPUT = Join-Path $OutputDirectory 'Skunkworks'
        Invoke-Checked $UnityEditor @('-batchmode', '-quit', '-projectPath', $unityProject, '-buildTarget', 'Win64', '-executeMethod', 'Build.Windows', '-logFile', $unityLog)
    } finally {
        if ($null -eq $previousOutput) { Remove-Item Env:\UNITY_BUILD_OUTPUT -ErrorAction SilentlyContinue } else { $env:UNITY_BUILD_OUTPUT = $previousOutput }
    }
}
$player = Join-Path $OutputDirectory 'Skunkworks\Skunkworks.exe'
if (!(Test-Path -LiteralPath $player) -or !(Test-Path -LiteralPath (Join-Path $OutputDirectory 'Skunkworks\Skunkworks_Data'))) {
    throw "Unity must produce Skunkworks.exe and Skunkworks_Data. See $unityLog; Build.Windows owns the player name."
}
[IO.Directory]::CreateDirectory((Join-Path $OutputDirectory 'Licenses')) | Out-Null
$rootLicense = Join-Path $repoRoot 'LICENSE'
if (Test-Path -LiteralPath $rootLicense) { Copy-Item -LiteralPath $rootLicense -Destination (Join-Path $OutputDirectory 'Licenses\Skunkworks-LICENSE.txt') }
$rootNotice = Join-Path $repoRoot 'NOTICE'
if (Test-Path -LiteralPath $rootNotice) { Copy-Item -LiteralPath $rootNotice -Destination (Join-Path $OutputDirectory 'Licenses\Skunkworks-NOTICE.txt') }
$rootNotices = Join-Path $repoRoot 'Licenses'
if (Test-Path -LiteralPath $rootNotices) {
    foreach ($file in Get-ChildItem -LiteralPath $rootNotices -Recurse -File) {
        $target = Join-Path $OutputDirectory ('Licenses/' + [IO.Path]::GetRelativePath($rootNotices, $file.FullName))
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $target
    }
}
# Provenance: every input the player was built from.
$provenance = Join-Path $OutputDirectory 'Licenses\Provenance'
[IO.Directory]::CreateDirectory($provenance) | Out-Null
$inputs = [ordered]@{
    unity = [ordered]@{ version = $unityVersion; editorProduct = $editorProduct; editorSha256 = Get-Sha256 $UnityEditor; reusedPlayerReceiptSha256 = $playerReuseReceipt; files = $unityFiles }
    rootLicenseSha256 = Get-Sha256 $rootLicense
    metadataFiles = @('.gitattributes', '.gitignore' | ForEach-Object { [ordered]@{ path = $_; sha256 = Get-Sha256 (Join-Path $repoRoot $_) } })
    noticesFiles = @(Get-TreeFiles $rootNotices)
    packagingFiles = @(Get-TreeFiles $PSScriptRoot)
}
$inputsHash = Get-TextHash ($inputs | ConvertTo-Json -Depth 100 -Compress)
Write-Json (Join-Path $provenance 'build-inputs.json') $inputs
$files = @(Get-TreeFiles $OutputDirectory)
Write-Json (Join-Path $OutputDirectory 'release.receipt.json') ([ordered]@{
        schemaVersion = 1; edition = 'demo'; demoReady = $true; inputSha256 = $inputsHash
        version = $Version; experimentalApis = @('simulation-boundary commits', 'coordinated checkpoints')
        tools = [ordered]@{ unity = $unityVersion; unityEditorVersion = $editorProduct }
        capabilities = @('skunkworks.wire/1', 'Experimental boundary commits', 'Experimental coordinated checkpoints'); files = $files
        reproducibility = 'Input identity and output hashes; Unity output bytes are not asserted bit-for-bit deterministic.'
    })
& $Acceptance -ReleaseRoot $OutputDirectory
Write-Host "Release built and checked: $OutputDirectory"
