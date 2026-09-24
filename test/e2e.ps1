# End-to-end check on a Windows desktop: starts SoundSpell, types into Notepad
# like a person would, and checks what ends up in the text.
param([string]$Exe = (Join-Path (Split-Path -Parent $PSScriptRoot) 'SoundSpell.exe'))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms

Get-Process SoundSpell, notepad -ErrorAction SilentlyContinue | Stop-Process -Force
$env:SOUNDSPELL_LOG = Join-Path $env:TEMP 'soundspell-e2e.log'
Remove-Item $env:SOUNDSPELL_LOG -ErrorAction SilentlyContinue
$app = Start-Process $Exe -PassThru
Start-Sleep -Seconds 3   # word list loads

$shell = New-Object -ComObject WScript.Shell
function Type-Slowly([string]$keys) {
    # One key at a time with a pause, about as fast as quick typing.
    $i = 0
    while ($i -lt $keys.Length) {
        if ($keys[$i] -eq '{') { $end = $keys.IndexOf('}', $i); $k = $keys.Substring($i, $end - $i + 1); $i = $end + 1 }
        else { $k = [string]$keys[$i]; $i++ }
        [System.Windows.Forms.SendKeys]::SendWait($k)
        Start-Sleep -Milliseconds 90
    }
}

function Check([string]$typed, [string]$want) {
    $np = Start-Process notepad -PassThru
    Start-Sleep -Seconds 2
    $null = $shell.AppActivate($np.Id)
    Start-Sleep -Milliseconds 500
    Type-Slowly $typed
    Start-Sleep -Milliseconds 500
    [System.Windows.Forms.SendKeys]::SendWait('^a')
    [System.Windows.Forms.SendKeys]::SendWait('^c')
    Start-Sleep -Milliseconds 300
    $got = (Get-Clipboard -Raw) -replace "`r`n", "`n"
    Stop-Process $np.Id -Force
    if ($got -ceq $want) { Write-Host "ok   $typed" ; return $true }
    Write-Host "FAIL $typed"; Write-Host "     want: [$want]"; Write-Host "     got:  [$got]"
    return $false
}

$results = @(
    # Space swaps the word and keeps the space.
    (Check 'hello @@nesesary and' "hello necessary and"),
    # Enter only fixes the word; the next Enter is a normal Enter.
    (Check 'hi @@wensday{ENTER}{ENTER}next' "hi Wednesday`nnext"),
    # Punctuation ends the word too; capitals carry over.
    (Check '@@Sykology.' "Psychology."),
    # Ctrl+0 puts back what was typed.
    (Check 'my @@frend ^0ok' "my frend ok"),
    # No @@, nothing changes.
    (Check 'plain nesesary words' "plain nesesary words")
)

Stop-Process $app.Id -Force
if ($results -contains $false -and (Test-Path $env:SOUNDSPELL_LOG)) { Write-Host '--- SoundSpell log ---'; Get-Content $env:SOUNDSPELL_LOG }
if ($results -contains $false) { exit 1 }
