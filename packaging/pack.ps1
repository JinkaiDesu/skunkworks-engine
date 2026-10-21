#!/usr/bin/env pwsh
#Requires -Version 7.2
<#
.SYNOPSIS
  Zip release files for a package step: files from one folder, into one archive, in a fixed order,
  with fixed timestamps, so the same inputs give the same bytes.

.EXAMPLE
  pwsh packaging/pack.ps1 -From Release/skunkworks-v{version}-demo -To Release/skunkworks-v{version}-demo.zip
#>
param(
    [Parameter(Mandatory)][string]$From,
    [string[]]$Include = @('*'),
    [Parameter(Mandatory)][string]$To
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
$From = (Resolve-Path -LiteralPath $From).Path
if (Test-Path -LiteralPath $To) { throw "$To already exists" }
$files = @(Get-ChildItem -LiteralPath $From -Recurse -File | Where-Object {
        $relative = [IO.Path]::GetRelativePath($From, $_.FullName) -replace '\\', '/'
        @($Include | Where-Object { $relative -like $_ }).Count -gt 0
    } | Sort-Object { [IO.Path]::GetRelativePath($From, $_.FullName) -replace '\\', '/' } -CaseSensitive)
if (-not $files.Count) { throw "nothing under $From matches $($Include -join ', ')" }
New-Item -ItemType Directory -Force (Split-Path -Parent ([IO.Path]::GetFullPath($To))) | Out-Null
$stamp = [DateTimeOffset]::new(2000, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
$stream = [IO.File]::Open([IO.Path]::GetFullPath($To), [IO.FileMode]::CreateNew)
try {
    $zip = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in $files) {
            $entry = $zip.CreateEntry(([IO.Path]::GetRelativePath($From, $file.FullName) -replace '\\', '/'), [IO.Compression.CompressionLevel]::Optimal)
            $entry.LastWriteTime = $stamp
            $writer = $entry.Open()
            try { $reader = [IO.File]::OpenRead($file.FullName); try { $reader.CopyTo($writer) } finally { $reader.Dispose() } } finally { $writer.Dispose() }
        }
    } finally { $zip.Dispose() }
} finally { $stream.Dispose() }
"$To ($($files.Count) files)"
