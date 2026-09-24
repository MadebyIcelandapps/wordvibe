# Removes SoundSpell. Her own word list in %APPDATA%\SoundSpell is kept.
$dest = Join-Path $env:LOCALAPPDATA 'Programs\SoundSpell'
Get-Process SoundSpell -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 300
Remove-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name SoundSpell -ErrorAction SilentlyContinue
Remove-Item 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\SoundSpell' -Recurse -ErrorAction SilentlyContinue
Remove-Item 'HKCU:\Software\SoundSpell' -Recurse -ErrorAction SilentlyContinue
Remove-Item (Join-Path ([Environment]::GetFolderPath('Programs')) 'SoundSpell.lnk') -ErrorAction SilentlyContinue
Remove-Item $dest -Recurse -Force -ErrorAction SilentlyContinue
