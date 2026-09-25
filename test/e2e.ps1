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

# Types like a keyboard: plain key presses with scan codes, one at a time
# (SendKeys does its own juggling of key state, which is not what a person does).
Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices; using System.Threading;
public static class Kbd {
  [StructLayout(LayoutKind.Sequential)] struct MI { public int dx, dy; public uint md, fl, t; public IntPtr ex; }
  [StructLayout(LayoutKind.Sequential)] struct KI { public ushort vk, sc; public uint fl, t; public IntPtr ex; }
  [StructLayout(LayoutKind.Explicit)] struct U { [FieldOffset(0)] public MI mi; [FieldOffset(0)] public KI ki; }
  [StructLayout(LayoutKind.Sequential)] struct IN { public uint type; public U u; }
  [DllImport("user32.dll")] static extern uint SendInput(uint n, IN[] i, int s);
  [DllImport("user32.dll")] static extern uint MapVirtualKey(uint c, uint t);
  [DllImport("user32.dll")] static extern short VkKeyScan(char c);
  static void Key(int vk, bool up) {
    var i = new IN(); i.type = 1; i.u.ki.vk = (ushort)vk; i.u.ki.sc = (ushort)MapVirtualKey((uint)vk, 0); i.u.ki.fl = up ? 2u : 0u;
    SendInput(1, new[] { i }, Marshal.SizeOf(typeof(IN))); Thread.Sleep(15);
  }
  public static void Press(int vk, bool shift, bool ctrl) {
    if (ctrl) Key(0x11, false); if (shift) Key(0x10, false);
    Key(vk, false); Key(vk, true);
    if (shift) Key(0x10, true); if (ctrl) Key(0x11, true);
  }
  public static void Tap(int vk) { Key(vk, false); Key(vk, true); }
  public static void Char(char c) { short r = VkKeyScan(c); Press(r & 0xFF, (r & 0x100) != 0, false); }
}
'@

function Type-Slowly([string]$keys) {
    $i = 0
    while ($i -lt $keys.Length) {
        if ($keys[$i] -eq '{') {
            $end = $keys.IndexOf('}', $i); $name = $keys.Substring($i + 1, $end - $i - 1); $i = $end + 1
            if ($name -eq 'ENTER') { [Kbd]::Press(0x0D, $false, $false) }
            if ($name -eq 'SHIFT2') { [Kbd]::Tap(0xA0); Start-Sleep -Milliseconds 120; [Kbd]::Tap(0xA0); Start-Sleep -Milliseconds 300 }
        }
        elseif ($keys[$i] -eq '^') { [Kbd]::Press([int][char]$keys[$i + 1], $false, $true); $i += 2 }
        else { [Kbd]::Char($keys[$i]); $i++ }
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
    if ($got -ceq $want) { Write-Host "ok   $typed"; $script:summary += "ok   $typed"; return $true }
    $line = "FAIL $typed   want [$want]   got [$got]"
    Write-Host $line; $script:summary += $line
    return $false
}

$script:summary = @()
$results = @(
    # Space swaps the word and keeps the space.
    (Check 'hello @@nesesary and' "hello necessary and"),
    # Enter only fixes the word; the next Enter is a normal Enter.
    (Check 'hi @@wensday{ENTER}{ENTER}next' "hi Wednesday`nnext"),
    # Punctuation ends the word too; capitals carry over.
    (Check '@@Sykology.' "Psychology."),
    # Ctrl+0 puts back what was typed.
    (Check 'my @@frend ^0ok' "my frend ok"),
    # No @@, no shortcut: nothing changes.
    (Check 'plain nesesary words' "plain nesesary words"),
    # The shortcut: type the word, tap Shift twice.
    (Check 'hello nesesary{SHIFT2}' "hello necessary"),
    (Check 'see you on wensday.{SHIFT2}' "see you on Wednesday."),
    # Shift for a capital letter is not a tap.
    (Check 'Hello There' "Hello There"),
    # Words that sound alike: the word before decides.
    (Check 'the @@wether ' "the weather "),
    (Check 'lost @@there bags' "lost their bags"),
    # Irish and British spelling.
    (Check 'my @@favrit ' "my favourite "),
    (Check 'hi @@neev ' "hi Niamh "),
    # Learning: pick the 2nd match twice, then it comes first by itself.
    (Check '@@gril ^2' "girl "),
    (Check '@@gril ^2' "girl "),
    (Check '@@gril ' "girl ")
)

Stop-Process $app.Id -Force
if ($results -contains $false -and (Test-Path $env:SOUNDSPELL_LOG)) { Write-Host '--- SoundSpell log (end) ---'; Get-Content $env:SOUNDSPELL_LOG -Tail 80 }
Write-Host '--- results ---'
$script:summary | ForEach-Object { Write-Host $_ }
if ($results -contains $false) { exit 1 }
