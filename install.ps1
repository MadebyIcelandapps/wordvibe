# Installs SoundSpell for the current user: no admin rights, a couple of seconds.
# Puts it in %LOCALAPPDATA%\Programs\SoundSpell, starts it with Windows,
# adds a Start menu entry and an entry in Settings > Apps for uninstalling.
$ErrorActionPreference = 'Stop'
$src = $PSScriptRoot
$dest = Join-Path $env:LOCALAPPDATA 'Programs\SoundSpell'
$exe = Join-Path $dest 'SoundSpell.exe'

Write-Host 'Installing SoundSpell...'
Get-Process SoundSpell -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 300
New-Item -ItemType Directory -Force $dest | Out-Null

$prebuilt = Join-Path $src 'SoundSpell.exe'
if (Test-Path $prebuilt) { Copy-Item $prebuilt $exe -Force }
else { & (Join-Path $src 'build.ps1') -Out $exe }
Copy-Item (Join-Path $src 'uninstall.ps1') $dest -Force

# Start with Windows.
Set-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name SoundSpell -Value "`"$exe`""

# Start menu shortcut.
$shell = New-Object -ComObject WScript.Shell
$lnk = $shell.CreateShortcut((Join-Path ([Environment]::GetFolderPath('Programs')) 'SoundSpell.lnk'))
$lnk.TargetPath = $exe
$lnk.Description = 'Type @@ and a word the way it sounds'
$lnk.Save()

# Settings > Apps entry.
$un = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\SoundSpell'
New-Item $un -Force | Out-Null
Set-ItemProperty $un DisplayName 'SoundSpell'
Set-ItemProperty $un DisplayIcon $exe
Set-ItemProperty $un Publisher 'SoundSpell'
Set-ItemProperty $un InstallLocation $dest
Set-ItemProperty $un UninstallString "powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$dest\uninstall.ps1`""
Set-ItemProperty $un NoModify 1 -Type DWord
Set-ItemProperty $un NoRepair 1 -Type DWord

Start-Process $exe
Write-Host ''
Write-Host 'Done. SoundSpell is running with the hidden icons by the clock (the ^ arrow).'
Write-Host 'Type @@ and a word the way it sounds, then space. Example: @@nesesary -> necessary'
