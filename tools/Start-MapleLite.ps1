<#
  Start-MapleLite.ps1: get the latest MapleLite from GitHub, build it if anything changed, and run it.
  The desktop shortcut made by Install-DesktopShortcut.ps1 runs this.

  Safe by design:
  - Updates only with a fast-forward pull. If that can't be done (no internet, local changes, diverged history),
    it says why and starts the copy you already have. It never overwrites or discards anything.
  - If MapleLite is already running, it just says so. It never closes the running app.
  - Builds outside the repo folder (%LOCALAPPDATA%\MapleLite-build), as the project's build settings do.
  A log of each run is kept in %LOCALAPPDATA%\MapleLite-build\launcher.log.
#>
param(
    [switch]$NoPull,
    # Anything else is passed to MapleLite, e.g. --replay capture.png
    [Parameter(ValueFromRemainingArguments = $true)][string[]]$AppArgs
)

# 'Continue', not 'Stop': in Windows PowerShell 5.1, git's normal stderr output would otherwise abort the script.
# Every step checks its own result instead.
$ErrorActionPreference = 'Continue'
$repo   = Split-Path -Parent $PSScriptRoot
$build  = Join-Path $env:LOCALAPPDATA 'MapleLite-build'
$exe    = Join-Path $build 'bin\MapleLite.Host\release_net8.0-windows10.0.19041.0\MapleLite.exe'
$log    = Join-Path $build 'launcher.log'
$Host.UI.RawUI.WindowTitle = 'MapleLite launcher'
New-Item -ItemType Directory -Force $build | Out-Null

function Say([string]$text, [string]$color = 'Gray') {
    Write-Host $text -ForegroundColor $color
    Add-Content -Path $log -Value ("{0:yyyy-MM-dd HH:mm:ss} {1}" -f (Get-Date), $text) -Encoding utf8
}

function Fail([string]$text) {
    Say $text 'Red'
    Write-Host ''
    Read-Host 'Press Enter to close'
    exit 1
}

function Run-Git([string[]]$gitArgs) {
    $out = & git -C $repo @gitArgs 2>&1 | Out-String
    return @{ Ok = ($LASTEXITCODE -eq 0); Text = $out.Trim() }
}

Say "MapleLite launcher ($repo)" 'Cyan'

# 1. Already running? Don't rebuild under it, and never close it.
$running = Get-Process -Name 'MapleLite' -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe }
if ($running) {
    Say 'MapleLite is already running. Close it first to update; this window closes in 5 seconds.' 'Yellow'
    Start-Sleep -Seconds 5
    exit 0
}

foreach ($tool in 'git', 'dotnet') {
    if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) { Fail "$tool isn't installed or isn't on PATH. Install it, then try again." }
}

# 2. Update from GitHub (fast-forward only).
$before = (Run-Git @('rev-parse', 'HEAD')).Text
if (-not $NoPull) {
    # Google Drive drops desktop.ini files into .git, which breaks fetching. They hold no repo data.
    Get-ChildItem -Path (Join-Path $repo '.git') -Filter 'desktop.ini' -Recurse -Force -File -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
    Say 'Checking GitHub for updates...'
    $pull = Run-Git @('pull', '--ff-only', '--quiet')
    if ($pull.Ok) {
        $after = (Run-Git @('rev-parse', 'HEAD')).Text
        if ($after -ne $before) {
            $count = (Run-Git @('rev-list', '--count', "$before..$after")).Text
            Say "Updated: $count new change(s)." 'Green'
            (Run-Git @('log', '--oneline', '--no-decorate', "$before..$after")).Text -split "`n" | Select-Object -First 8 | ForEach-Object { Say "  $_" }
        } else { Say 'Already up to date.' 'Green' }
    } else {
        Say "Couldn't update, so starting the copy you have. Git said:" 'Yellow'
        Say ("  " + ($pull.Text -replace "`n", "`n  ")) 'Yellow'
    }
}
$after = (Run-Git @('rev-parse', 'HEAD')).Text

# 3. Build when the code changed, or when there's no build yet.
$stamp = Join-Path $build 'launcher-built-commit.txt'
$builtCommit = if (Test-Path $stamp) { (Get-Content $stamp -Raw).Trim() } else { '' }
$dirty = (Run-Git @('status', '--porcelain', '--untracked-files=no')).Text
if (-not (Test-Path $exe) -or $builtCommit -ne $after -or $dirty) {
    Say 'Building MapleLite (the first build takes a minute)...'
    $hostProject = Join-Path $repo 'src\MapleLite.Host'
    $buildOut = & dotnet build $hostProject -f net8.0-windows10.0.19041.0 -c Release -nologo -v quiet 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) {
        # A stuck compiler server can report files that exist as missing. Restart it and try once more.
        Say 'The build failed; restarting the .NET build servers and trying again...' 'Yellow'
        & dotnet build-server shutdown 2>&1 | Out-Null
        $buildOut = & dotnet build $hostProject -f net8.0-windows10.0.19041.0 -c Release -nologo -v quiet 2>&1 | Out-String
    }
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $exe)) {
        Add-Content -Path $log -Value $buildOut -Encoding utf8
        $errors = ($buildOut -split "`n" | Where-Object { $_ -match ' error ' } | Select-Object -First 5) -join "`n"
        if (Test-Path $exe) {
            Say "The build failed, so starting the previous build. Details are in $log" 'Yellow'
            if ($errors) { Say $errors 'Yellow' }
        } else {
            Fail "The build failed and there's no previous build to start. Details are in $log`n$errors"
        }
    } else {
        Set-Content -Path $stamp -Value $after -Encoding ascii
        Say 'Build finished.' 'Green'
    }
} else { Say 'No changes since the last build.' }

# 4. Run.
Say 'Starting MapleLite.' 'Cyan'
if ($AppArgs) { Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe) -ArgumentList ($AppArgs | ForEach-Object { if ($_ -match '\s') { "`"$_`"" } else { $_ } }) }
else { Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe) }
