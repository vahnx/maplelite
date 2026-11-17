<#
  Install-DesktopShortcut.ps1: puts a "MapleLite" icon on your desktop. Double-clicking it runs
  Start-MapleLite.ps1, which pulls the latest from GitHub, builds if anything changed, and starts MapleLite.
  Run it once:  powershell -ExecutionPolicy Bypass -File tools\Install-DesktopShortcut.ps1
#>
$repo    = Split-Path -Parent $PSScriptRoot
$script  = Join-Path $PSScriptRoot 'Start-MapleLite.ps1'
$icon    = Join-Path $repo 'src\MapleLite.Host\Assets\MapleLite.ico'
$desktop = [Environment]::GetFolderPath('Desktop')   # follows OneDrive desktop redirection
$link    = Join-Path $desktop 'MapleLite.lnk'

$shell = New-Object -ComObject WScript.Shell
$s = $shell.CreateShortcut($link)
$s.TargetPath       = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
# -ExecutionPolicy Bypass applies to this one run only; it doesn't change any system setting.
$s.Arguments        = "-NoProfile -ExecutionPolicy Bypass -File `"$script`""
$s.WorkingDirectory = $repo
$s.IconLocation     = "$icon,0"
$s.Description      = 'Update MapleLite from GitHub and start it'
$s.WindowStyle      = 1
$s.Save()
Write-Host "Created $link"
