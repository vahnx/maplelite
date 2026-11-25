# Lists the Kerning Exchange item pages whose IDs are missing from the bundled catalog.
$ErrorActionPreference = 'Stop'

$catalogPath = Join-Path $PSScriptRoot '../src/MapleLite.Core/GameData/Items/items.json'
$catalog = Get-Content -LiteralPath $catalogPath -Raw | ConvertFrom-Json
$knownIds = New-Object 'System.Collections.Generic.HashSet[int]'
foreach ($item in $catalog) {
    [void]$knownIds.Add([int]$item.id)
}

[xml]$sitemap = (Invoke-WebRequest -Uri 'https://kerning.gg/sitemap.xml' -UseBasicParsing -TimeoutSec 30).Content
$itemPages = @($sitemap.urlset.url.loc | ForEach-Object {
    if ($_ -match '^https://kerning\.gg/item/(\d+)-([^/]+)$') {
        [pscustomobject]@{ Id = [int]$Matches[1]; Slug = $Matches[2]; Url = $_ }
    }
} | Sort-Object Id -Unique)

if ($itemPages.Count -eq 0) {
    throw 'No item pages found in the Kerning Exchange sitemap.'
}

$itemPages | Where-Object { -not $knownIds.Contains($_.Id) }
