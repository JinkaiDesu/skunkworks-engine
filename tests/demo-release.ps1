#Requires -Version 7.2
<#
.SYNOPSIS
  Acceptance for the Skunkworks website demo, skunkworks-v<version>-demo (packaging/build-release.ps1's output): the
  payload is exactly what its receipt records, the licenses and provenance ship, and the provenance agrees with the
  receipt. A signed demo records what it signed in its receipt's signing block: every target carries a valid
  Authenticode signature by the recorded certificate. Running the player is tests/release.tests.ps1's.

.EXAMPLE
  pwsh tests/demo-release.ps1 -ReleaseRoot <release folder>
#>
[CmdletBinding()]
param([Parameter(Mandatory)][string]$ReleaseRoot)
$ErrorActionPreference = 'Stop'
function Get-Sha256([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Get-TextHash([string]$Text) { [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($Text))).ToLowerInvariant() }
function Read-Json([string]$Path) { Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json }
function Get-TreeFiles([string]$Root, [string[]]$Exclude = @()) {
    $base = [IO.Path]::GetFullPath($Root)
    $paths = @(Get-ChildItem -LiteralPath $base -Recurse -File -Force | ForEach-Object { [IO.Path]::GetRelativePath($base, $_.FullName).Replace('\', '/') } | Where-Object { $_ -cnotin $Exclude })
    [Array]::Sort($paths, [StringComparer]::Ordinal)
    @($paths | ForEach-Object { $f = [IO.FileInfo]::new([IO.Path]::Combine($base, $_)); [ordered]@{ path = $_; size = $f.Length; sha256 = Get-Sha256 $f.FullName } })
}
$ReleaseRoot = [IO.Path]::GetFullPath($ReleaseRoot)
$receipt = Read-Json (Join-Path $ReleaseRoot 'release.receipt.json')
if ($receipt.schemaVersion -ne 1) { throw 'Unsupported release receipt.' }
if ($receipt.edition -cne 'demo' -or !$receipt.demoReady) { throw 'The release is not the demo build.' }

# Skunkworks' license, its fonts' licenses and the build's provenance ship with it.
$required = @('Licenses/Skunkworks-LICENSE.txt', 'Licenses/RedditMono/OFL.txt', 'Licenses/CascadiaCode/OFL.txt', 'Licenses/Provenance/build-inputs.json')
foreach ($path in $required) { if (!(Test-Path -LiteralPath (Join-Path $ReleaseRoot $path) -PathType Leaf)) { throw "Release file missing: $path" } }
if (!(Test-Path -LiteralPath (Join-Path $ReleaseRoot 'Skunkworks/Skunkworks.exe') -PathType Leaf)) { throw 'The release has no player.' }

# Exactly the receipt's files, byte for byte.
$expected = @{}
foreach ($file in $receipt.files) {
    if ($file.path -match '^/|^[A-Za-z]:|(^|/)\.\.(/|$)' -or $expected.ContainsKey($file.path)) { throw 'Unsafe or duplicate receipt path.' }
    $expected[$file.path] = $file
}
$actual = @(Get-TreeFiles $ReleaseRoot @('release.receipt.json'))
if ($actual.Count -ne $expected.Count) { throw 'Release payload file count differs from its receipt (stale or missing files).' }
foreach ($file in $actual) {
    if (!$expected.ContainsKey($file.path) -or $file.sha256 -cne $expected[$file.path].sha256 -or $file.size -ne $expected[$file.path].size) { throw "Release payload differs from its receipt: $($file.path)" }
}

# Signing: an unsigned release has no signing block. A signed candidate's targets each carry a valid Authenticode
# signature whose signer is the recorded certificate; $unsigned keeps each target's SHA-256 before signing.
$unsigned = @{}
if ($receipt.PSObject.Properties['signing']) {
    $signing = $receipt.signing
    $thumbprint = "$($signing.certificateThumbprint)".ToLowerInvariant()
    if ($thumbprint -cnotmatch '^[0-9a-f]{40}$') { throw 'The signing record names no certificate thumbprint.' }
    $targets = if ($signing.PSObject.Properties['targets']) { @($signing.targets | Where-Object { $null -ne $_ }) } else { @() }
    if (!$targets.Count) { throw 'The signing record lists no signed files.' }
    foreach ($target in $targets) {
        $path = "$($target.path)"
        if (!$expected.ContainsKey($path) -or $unsigned.ContainsKey($path)) { throw "Signing target is not a release file, or is listed twice: $path" }
        if ("$($target.unsignedSha256)" -cnotmatch '^[0-9a-f]{64}$') { throw "Signing target $path records no SHA-256 before signing." }
        $signature = Get-AuthenticodeSignature -LiteralPath (Join-Path $ReleaseRoot $path)
        if ($signature.Status -ne [Management.Automation.SignatureStatus]::Valid) { throw "$path carries no valid signature: $($signature.Status) $($signature.StatusMessage)" }
        if (!$signature.SignerCertificate -or $signature.SignerCertificate.Thumbprint.ToLowerInvariant() -cne $thumbprint) { throw "$path is not signed by the recorded certificate $thumbprint." }
        $unsigned[$path] = "$($target.unsignedSha256)"
    }
}

# Provenance: the recorded inputs are the ones the receipt hashed, built with the receipt's Unity.
$inputs = Read-Json (Join-Path $ReleaseRoot 'Licenses/Provenance/build-inputs.json')
if ((Get-TextHash ($inputs | ConvertTo-Json -Depth 100 -Compress)) -cne $receipt.inputSha256) { throw 'Build input receipt hash mismatch.' }
if ($inputs.unity.version -cne $receipt.tools.unity) { throw "The build inputs name Unity $($inputs.unity.version), the receipt $($receipt.tools.unity)." }
Write-Host "SKUNKWORKS_DEMO_OK files=$($actual.Count)"
