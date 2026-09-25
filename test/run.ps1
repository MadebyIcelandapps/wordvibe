# Windows version of test/run.sh: builds the speller checks with the built-in csc and runs them.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$fw = @("$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319", "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319") |
    Where-Object { Test-Path (Join-Path $_ 'csc.exe') } | Select-Object -First 1
& (Join-Path $fw 'csc.exe') /nologo "/out:$root\test\spelltest.exe" "$root\src\Speller.cs" "$root\src\Homophones.cs" "$root\test\SpellTest.cs"
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
& "$root\test\spelltest.exe" "$root\words.txt"
exit $LASTEXITCODE
