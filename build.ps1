# Builds SoundSpell.exe with the C# compiler that ships inside Windows (.NET Framework 4).
# Nothing to download. Usage: build.ps1 [-Out <path to exe>]
param([string]$Out = (Join-Path $PSScriptRoot 'SoundSpell.exe'))
$ErrorActionPreference = 'Stop'

$fw = @("$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319", "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319") |
    Where-Object { Test-Path (Join-Path $_ 'csc.exe') } | Select-Object -First 1
if (-not $fw) { throw 'The .NET Framework 4 C# compiler (csc.exe) was not found. It comes with Windows 10 and 11.' }

$speech = @("$fw\System.Speech.dll", "$fw\WPF\System.Speech.dll") | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $speech) { throw 'System.Speech.dll was not found next to csc.exe.' }

$outDir = Split-Path -Parent $Out
if ($outDir) { New-Item -ItemType Directory -Force $outDir | Out-Null }

& (Join-Path $fw 'csc.exe') /nologo /target:winexe /optimize+ /platform:anycpu "/out:$Out" `
    /r:System.Windows.Forms.dll /r:System.Drawing.dll "/r:$speech" `
    "/resource:$PSScriptRoot\words.txt,SoundSpell.words.txt" `
    "$PSScriptRoot\src\Speller.cs" "$PSScriptRoot\src\SoundSpell.cs"
if ($LASTEXITCODE -ne 0) { throw "Build failed ($LASTEXITCODE)" }
