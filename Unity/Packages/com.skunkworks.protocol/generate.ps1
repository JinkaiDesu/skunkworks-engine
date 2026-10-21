param([switch]$Check)
$ErrorActionPreference = 'Stop'
$schema = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot 'schema.json') | ConvertFrom-Json
$cs = "// Generated from schema.json. Run Unity/Packages/com.skunkworks.protocol/generate.ps1.\n"
foreach ($pair in @(@('MessageKind','message_kinds'),@('ValueKind','value_kinds'))) {
    $cs += "public enum $($pair[0]) : byte\n{\n"
    foreach ($property in $schema.($pair[1]).PSObject.Properties) { $cs += "    $($property.Name) = $($property.Value),\n" }
    $cs += "}\n"
}
$cs += "public static class WireSchema { public const string Protocol = `"$($schema.protocol)`"; public const int MaxFrame = $($schema.max_frame); public const int MaxValues = $($schema.max_values); }\n"
$outputs = @{ (Join-Path $PSScriptRoot 'WireSchema.g.cs') = $cs }
# This schema is canonical and published: clients copy it and generate their own codecs from it.
foreach ($path in @($outputs.Keys)) {
    $outputs[$path] = $outputs[$path].Replace('\n', "`n")
    if ($Check) { if (!(Test-Path -LiteralPath $path) -or [IO.File]::ReadAllText($path).Replace("`r`n","`n") -ne $outputs[$path]) { throw "Generated protocol differs: $path" } }
    else { [IO.File]::WriteAllText($path, $outputs[$path], [Text.UTF8Encoding]::new($false)) }
}
