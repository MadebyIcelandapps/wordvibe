# Installs SoundSpell for the current user: no admin rights, a couple of seconds.
# Puts it in %LOCALAPPDATA%\Programs\SoundSpell, starts it with Windows,
# adds a Start menu entry and an entry in Settings > Apps for uninstalling.
# Running it again updates SoundSpell and keeps settings, words and picks.
$ErrorActionPreference = 'Stop'
$src = $PSScriptRoot
$dest = Join-Path $env:LOCALAPPDATA 'Programs\SoundSpell'
$exe = Join-Path $dest 'SoundSpell.exe'

Write-Host 'Installing SoundSpell...'
Get-Process SoundSpell -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500
New-Item -ItemType Directory -Force $dest | Out-Null

$prebuilt = Join-Path $src 'SoundSpell.exe'
if (Test-Path $prebuilt) { Copy-Item $prebuilt $exe -Force }
else { & (Join-Path $src 'build.ps1') -Out $exe }
foreach ($f in 'uninstall.ps1', 'update.ps1') { Copy-Item (Join-Path $src $f) $dest -Force }

# Admin mode (turned on in Settings) starts SoundSpell from a scheduled task instead.
cmd.exe /c "schtasks /Query /TN SoundSpell >nul 2>&1"
$adminMode = $LASTEXITCODE -eq 0

if (-not $adminMode) {
    $run = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    if (-not (Test-Path $run)) { New-Item $run | Out-Null }   # missing on a brand-new account
    Set-ItemProperty $run -Name SoundSpell -Value "`"$exe`""
}

# Start menu shortcut.
$shell = New-Object -ComObject WScript.Shell
$lnk = $shell.CreateShortcut((Join-Path ([Environment]::GetFolderPath('Programs')) 'SoundSpell.lnk'))
$lnk.TargetPath = $exe
$lnk.Description = 'Type a word the way it sounds and get the real spelling'
$lnk.Save()

# Settings > Apps entry.
$un = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\SoundSpell'
New-Item $un -Force | Out-Null
Set-ItemProperty $un DisplayName 'SoundSpell'
Set-ItemProperty $un DisplayIcon $exe
Set-ItemProperty $un DisplayVersion ((Get-Content (Join-Path $src 'VERSION') -ErrorAction SilentlyContinue) -join '').Trim()
Set-ItemProperty $un Publisher 'SoundSpell'
Set-ItemProperty $un InstallLocation $dest
Set-ItemProperty $un UninstallString "powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$dest\uninstall.ps1`""
Set-ItemProperty $un NoModify 1 -Type DWord
Set-ItemProperty $un NoRepair 1 -Type DWord

if ($adminMode) { schtasks.exe /Run /TN SoundSpell | Out-Null }
else { Start-Process $exe }
Write-Host ''
Write-Host 'Done. SoundSpell is running with the hidden icons by the clock (the ^ arrow).'
Write-Host 'Type a word the way it sounds, then tap Shift twice. Example: nesesary -> necessary'
