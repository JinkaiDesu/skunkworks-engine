#Requires -Version 7.2
<#
.SYNOPSIS
  Skunkworks' own CI/CD, run by hand.
  Skunkworks depends on nothing but Unity and the .NET CLR, and its CI tests nothing but Skunkworks.
    verify   fast checks, no Unity: the generated protocol, and the protocol's behavior against the headless player
             (tests/host selftest, .NET only). Run it before every push.
    build    the website demo, Release/skunkworks-v<version>-demo: the Unity build and its acceptance, the zip, the
             release tests against the running player, then its manifest (emergence.manifest/1).
    publish  the built demo to Cloudflare R2: the zip and its manifest under skunkworks/<version>/ (immutable), then the
             moving skunkworks/manifest.json, which the website and launcher poll. Both are read back and checked.

.DESCRIPTION
  ci.config.json beside this script (git-ignored, this machine's) names the inputs:
    { "unityEditor": "<Unity.exe>"   (optional; default: the Hub's editor for ProjectVersion.txt),
      "r2": { "bucket": "<bucket>", "publicBase": "https://<public host>" } }
  publish uploads from this machine with wrangler, which reads the user's CLOUDFLARE_API_TOKEN and
  CLOUDFLARE_ACCOUNT_ID from the environment. Nothing is signed here.

.EXAMPLE
  pwsh ci.ps1 verify
#>
param([Parameter(Mandatory, Position = 0)][ValidateSet('verify', 'build', 'publish')][string]$Step)
. (Join-Path $PSScriptRoot 'packaging/common.ps1')
Assert-PackagingHost
$root = $PSScriptRoot
$wrangler = 'wrangler@4.149.0'
$version = (Read-Json (Join-Path $root 'component.json')).version
$name = "skunkworks-v$version-demo"
$folder = Join-Path $root "Release/$name"
$zip = "$folder.zip"
$manifest = "$folder.manifest.json"
$key = "skunkworks/$version"

$configPath = Join-Path $root 'ci.config.json'
$config = if (Test-Path -LiteralPath $configPath) { Read-Json $configPath } else { [pscustomobject]@{} }
function Setting([string]$Name) {
    $value = $config
    foreach ($part in $Name.Split('.')) { $value = if ($null -ne $value -and $value.PSObject.Properties[$part]) { $value.$part } else { $null } }
    if (-not $value) { throw "ci.config.json names no $Name." }
    "$value"
}
function UnityEditor {
    if ($config.PSObject.Properties['unityEditor']) { return Setting 'unityEditor' }
    $project = (Get-Content -LiteralPath (Join-Path $root 'Unity/ProjectSettings/ProjectVersion.txt') | Where-Object { $_ -match '^m_EditorVersion: ' }) -replace '^m_EditorVersion: ', ''
    $editor = "C:\Program Files\Unity\Hub\Editor\$project\Editor\Unity.exe"
    if (!(Test-Path -LiteralPath $editor -PathType Leaf)) { throw "Unity $project is not installed at $editor; name it as unityEditor in ci.config.json." }
    $editor
}
function PublicBase {
    $base = (Setting 'r2.publicBase').TrimEnd('/')
    if ($base -notmatch '^https://' -and $base -notmatch '^http://(127\.0\.0\.1|localhost)(:\d+)?$') { throw 'r2.publicBase must be https (plain http only on loopback).' }
    $base
}
# Runs one of this repository's scripts in its own pwsh; it must succeed and, when named, print its marker.
function Invoke-Script([string]$Script, [string[]]$Arguments, [string]$Marker) {
    Write-Host "== $Script"
    $lines = @(& pwsh -NoProfile -File (Join-Path $root $Script) @Arguments 2>&1 | ForEach-Object { $text = "$_"; Write-Host $text; $text })
    if ($LASTEXITCODE -ne 0) { throw "$Script failed (exit $LASTEXITCODE)." }
    if ($Marker -and -not @($lines | Where-Object { $_ -match "^$Marker\b" }).Count) { throw "$Script did not report $Marker." }
}
function Invoke-Wrangler([string]$ObjectKey, [string]$File, [string]$ContentType, [string]$CacheControl) {
    Write-Host "== r2 put $ObjectKey"
    & npx --yes $wrangler r2 object put "$(Setting 'r2.bucket')/$ObjectKey" --file $File --content-type $ContentType --cache-control $CacheControl --remote
    if ($LASTEXITCODE -ne 0) { throw "Uploading $ObjectKey failed." }
}
function Get-Remote([string]$Url, [string]$OutFile) {
    try { Invoke-WebRequest -Uri $Url -OutFile $OutFile -Headers @{ 'Cache-Control' = 'no-cache' } -ErrorAction Stop; $true }
    catch { if ($_.Exception.Response -and [int]$_.Exception.Response.StatusCode -eq 404) { $false } else { throw } }
}

switch ($Step) {
    'verify' {
        Invoke-Script 'Unity/Packages/com.skunkworks.protocol/generate.ps1' @('-Check')
        Write-Host '== tests/host selftest'
        $build = Join-Path ([IO.Path]::GetTempPath()) "skunkworks host $([guid]::NewGuid().ToString('N').Substring(0, 8))"
        try {
            $lines = @(& dotnet run --project (Join-Path $root 'tests/host/SkunkworksHost.csproj') -c Release --artifacts-path $build "-p:SkunkworksSource=$root" -- selftest 2>&1 |
                ForEach-Object { $text = "$_"; Write-Host $text; $text })
            if ($LASTEXITCODE -ne 0 -or -not @($lines | Where-Object { $_ -match '^SKUNKWORKS_HOST_OK\b' }).Count) { throw 'The protocol selftest failed.' }
        } finally { Remove-Item -LiteralPath $build -Recurse -Force -ErrorAction SilentlyContinue }
        Write-Host 'SKUNKWORKS_VERIFY_OK'
    }
    'build' {
        if (@(git -C $root status --porcelain).Count) { throw 'The checkout has uncommitted changes; the demo is built from a commit.' }
        $commit = (git -C $root rev-parse HEAD).Trim()
        # The website links "build from source" to this commit, so it must already be on GitHub.
        if (-not @(git -C $root branch -r --contains $commit).Count) { throw "Commit $commit is not pushed; push it, then build." }
        $base = PublicBase
        foreach ($old in $folder, $zip, $manifest) { if (Test-Path -LiteralPath $old) { Remove-Item -LiteralPath $old -Recurse -Force } }
        Invoke-Script 'packaging/build-release.ps1' @('-UnityEditor', (UnityEditor), '-Acceptance', (Join-Path $root 'tests/demo-release.ps1'), '-OutputDirectory', $folder)
        Invoke-Script 'packaging/pack.ps1' @('-From', $folder, '-To', $zip)
        Invoke-Script 'tests/release.tests.ps1' @('-Archive', $zip, '-Version', $version) 'SKUNKWORKS_RELEASE_OK'
        $file = Split-Path $zip -Leaf
        Write-Json $manifest ([ordered]@{
                schema = 'emergence.manifest/1'; id = 'skunkworks'; name = 'Skunkworks'; edition = 'demo'; platform = 'windows-x64'
                version = $version; commit = $commit; built = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')
                artifact = [ordered]@{ file = $file; key = "$key/$file"; url = "$base/$key/$file"; bytes = (Get-Item -LiteralPath $zip).Length; sha256 = Get-Sha256 $zip }
            })
        Write-Host "SKUNKWORKS_BUILD_OK $zip"
    }
    'publish' {
        foreach ($file in $zip, $manifest) { if (!(Test-Path -LiteralPath $file -PathType Leaf)) { throw "Nothing built to publish: $file is missing. Run ci.ps1 build." } }
        foreach ($variable in 'CLOUDFLARE_API_TOKEN', 'CLOUDFLARE_ACCOUNT_ID') { if (-not [Environment]::GetEnvironmentVariable($variable)) { throw "$variable is not set." } }
        $base = PublicBase
        $local = Read-Json $manifest
        if ($local.artifact.url -cne "$base/$($local.artifact.key)") { throw 'The manifest was built for another r2.publicBase; rebuild.' }
        $work = Join-Path ([IO.Path]::GetTempPath()) "skunkworks publish $([guid]::NewGuid().ToString('N').Substring(0, 8))"
        [IO.Directory]::CreateDirectory($work) | Out-Null
        try {
            # A version is published once: its zip and manifest never change after.
            $remote = Join-Path $work 'version.manifest.json'
            if (Get-Remote "$base/$key/manifest.json" $remote) {
                if ((Read-Json $remote).artifact.sha256 -cne $local.artifact.sha256) { throw "skunkworks $version is already published with another build; bump the version in component.json." }
                Write-Host "skunkworks $version is already published; only the latest pointer is refreshed."
            } else {
                Invoke-Wrangler $local.artifact.key $zip 'application/zip' 'public, max-age=31536000, immutable'
                Invoke-Wrangler "$key/manifest.json" $manifest 'application/json' 'public, max-age=31536000, immutable'
            }
            Invoke-Wrangler 'skunkworks/manifest.json' $manifest 'application/json' 'no-cache'

            $download = Join-Path $work $local.artifact.file
            if (!(Get-Remote $local.artifact.url $download)) { throw "$($local.artifact.url) is not served." }
            if ((Get-Item -LiteralPath $download).Length -ne $local.artifact.bytes -or (Get-Sha256 $download) -cne $local.artifact.sha256) { throw "$($local.artifact.url) differs from the build." }
            $latest = Join-Path $work 'manifest.json'
            if (!(Get-Remote "$base/skunkworks/manifest.json" $latest) -or (Get-Sha256 $latest) -cne (Get-Sha256 $manifest)) { throw "$base/skunkworks/manifest.json is not this build's manifest." }
        } finally { Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue }
        Write-Host "SKUNKWORKS_PUBLISH_OK $base/skunkworks/manifest.json"
    }
}
