# Removes SoundSpell. Your own words and picks in %APPDATA%\SoundSpell are kept.
$dest = Join-Path $env:LOCALAPPDATA 'Programs\SoundSpell'
Get-Process SoundSpell -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 300
# Admin mode's scheduled task needs admin rights to remove: Windows asks once.
cmd.exe /c "schtasks /Query /TN SoundSpell >nul 2>&1"
if ($LASTEXITCODE -eq 0) {
    cmd.exe /c "schtasks /Delete /F /TN SoundSpell >nul 2>&1"
    if ($LASTEXITCODE -ne 0) {
        Start-Process schtasks.exe -ArgumentList '/Delete /F /TN SoundSpell' -Verb RunAs -WindowStyle Hidden -Wait -ErrorAction SilentlyContinue
    }
}
Remove-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name SoundSpell -ErrorAction SilentlyContinue
Remove-Item 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\SoundSpell' -Recurse -ErrorAction SilentlyContinue
Remove-Item 'HKCU:\Software\SoundSpell' -Recurse -ErrorAction SilentlyContinue
Remove-Item (Join-Path ([Environment]::GetFolderPath('Programs')) 'SoundSpell.lnk') -ErrorAction SilentlyContinue
Remove-Item $dest -Recurse -Force -ErrorAction SilentlyContinue
