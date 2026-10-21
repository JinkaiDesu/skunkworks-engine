Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
function Assert-PackagingHost {
    if ($env:OS -ne 'Windows_NT' -or $PSVersionTable.PSVersion.Major -lt 7) { throw 'Use PowerShell 7 on Windows x64. No global MSYS installation is required.' }
}
function Get-Sha256([string]$Path) {
    $stream=[IO.File]::OpenRead($Path)
    try { [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant() }
    finally { $stream.Dispose() }
}
function Get-TextHash([string]$Text) { [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($Text))).ToLowerInvariant() }
function Read-Json([string]$Path) { Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json }
function Write-Json([string]$Path, $Value) {
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($Path))) | Out-Null
    $text = ($Value | ConvertTo-Json -Depth 100).Replace(([string][char]13 + [char]10), [string][char]10) + [char]10
    [IO.File]::WriteAllText($Path, $text, [Text.UTF8Encoding]::new($false))
}
function Invoke-Checked([string]$Executable,[string[]]$Arguments,[string]$WorkingDirectory='',[hashtable]$Environment) {
    $start=[Diagnostics.ProcessStartInfo]::new()
    $start.FileName=$Executable
    $start.UseShellExecute=$false
    $start.CreateNoWindow=$true
    $start.WindowStyle=[Diagnostics.ProcessWindowStyle]::Hidden
    if ($WorkingDirectory) { $start.WorkingDirectory=$WorkingDirectory }
    if ($Environment) { $start.Environment.Clear(); foreach ($key in $Environment.Keys) { $start.Environment[$key]=[string]$Environment[$key] } }
    $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process=[Diagnostics.Process]::new(); $process.StartInfo=$start
    try {
        [void]$process.Start()
        $stdout=$process.StandardOutput.ReadToEndAsync()
        $stderr=$process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        $outText=$stdout.GetAwaiter().GetResult(); if ($outText) { $outText | Out-Host }
        $errorText=$stderr.GetAwaiter().GetResult(); if ($errorText) { $errorText | Out-Host }
        if ($process.ExitCode -ne 0) { throw "$Executable failed with exit code $($process.ExitCode)." }
    } finally { $process.Dispose() }
}
function Assert-EmptyDirectory([string]$Path) {
    if (Test-Path -LiteralPath $Path) {
        if (!(Test-Path -LiteralPath $Path -PathType Container) -or @(Get-ChildItem -LiteralPath $Path -Force).Count) {
            throw "Output must be empty: $Path. Choose a fresh output directory; existing releases are never deleted."
        }
    } else { [IO.Directory]::CreateDirectory($Path) | Out-Null }
}
function Get-TreeFiles([string]$Root,[string[]]$Exclude=@(),[string[]]$ExcludeDirectories=@(),[string]$IncludePattern='') {
    $base=[IO.Path]::GetFullPath($Root)
    $paths=[Collections.Generic.List[string]]::new()
    $pending=[Collections.Generic.Stack[string]]::new(); $pending.Push($base)
    while ($pending.Count) {
        $directory=$pending.Pop()
        foreach ($item in Get-ChildItem -LiteralPath $directory -Force) {
            if ($item.PSIsContainer) {
                # Do not traverse directory junctions/symlinks or generated/cache trees.
                if ($item.Name -notin $ExcludeDirectories -and !($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { $pending.Push($item.FullName) }
            } else {
                $relative=[IO.Path]::GetRelativePath($base,$item.FullName).Replace('\','/')
                if ($relative -cnotin $Exclude -and (!$IncludePattern -or $relative -match $IncludePattern)) { $paths.Add($relative) }
            }
        }
    }
    $sorted=$paths.ToArray(); [Array]::Sort($sorted,[StringComparer]::Ordinal)
    @($sorted | ForEach-Object {
        $file=[IO.FileInfo]::new([IO.Path]::Combine($base,$_))
        [ordered]@{path=$_;size=$file.Length;sha256=Get-Sha256 $file.FullName}
    })
}
