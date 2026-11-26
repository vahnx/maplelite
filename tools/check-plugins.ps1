# Shows each built copy of a bundled plugin and whether it contains a given piece of text.
# Usage: .\tools\check-plugins.ps1 MapleLite.Plugins.SaleCard 'found by its name tag'
param([string]$Plugin = 'MapleLite.Plugins.SaleCard', [string]$Text = 'found by its name tag')
$root = Join-Path $env:LOCALAPPDATA 'MapleLite-build\MapleLite'
Get-ChildItem -Recurse $root -Filter "$Plugin.dll" | Where-Object { $_.FullName -notmatch '\\ref(int)?\\' } | ForEach-Object {
    $has = [Text.Encoding]::Unicode.GetString([IO.File]::ReadAllBytes($_.FullName)).Contains($Text)
    '{0:HH:mm:ss}  has text: {1,-5}  {2}' -f $_.LastWriteTime, $has, $_.FullName.Substring($root.Length)
}
