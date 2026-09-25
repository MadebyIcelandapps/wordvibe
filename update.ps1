# Updates SoundSpell to the newest version on GitHub: downloads it, unzips it,
# and runs its installer (which keeps settings, words and picks).
# SoundSpell runs this from "Check for updates". -Ref picks a branch or commit.
param([string]$Ref = 'main')
$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

$work = Join-Path $env:TEMP 'SoundSpell-update'
Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory $work | Out-Null
$zip = Join-Path $work 'soundspell.zip'
Invoke-WebRequest "https://github.com/MadebyIcelandapps/wordvibe/archive/$Ref.zip" -OutFile $zip -UseBasicParsing
Expand-Archive $zip -DestinationPath $work -Force
$installer = Get-ChildItem $work -Recurse -Filter install.ps1 | Select-Object -First 1
if (-not $installer) { throw 'install.ps1 not found in the download' }
& $installer.FullName
