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
  [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] static extern void mouse_event(uint flags, int x, int y, uint data, IntPtr extra);
  public static void Click(int x, int y) { SetCursorPos(x, y); Thread.Sleep(150); mouse_event(2, 0, 0, 0, IntPtr.Zero); Thread.Sleep(40); mouse_event(4, 0, 0, 0, IntPtr.Zero); }
  public static void Char(char c) { short r = VkKeyScan(c); Press(r & 0xFF, (r & 0x100) != 0, false); }
}
'@

function Type-Slowly([string]$keys) {
    $i = 0
    while ($i -lt $keys.Length) {
        if ($keys[$i] -eq '{') {
            $end = $keys.IndexOf('}', $i); $name = $keys.Substring($i + 1, $end - $i - 1); $i = $end + 1
            if ($name -eq 'ENTER') { [Kbd]::Press(0x0D, $false, $false) }
            if ($name -eq 'CTRL2') { [Kbd]::Tap(0xA2); Start-Sleep -Milliseconds 120; [Kbd]::Tap(0xA2); Start-Sleep -Milliseconds 300 }
            if ($name -eq 'SHIFT2') { [Kbd]::Tap(0xA0); Start-Sleep -Milliseconds 120; [Kbd]::Tap(0xA0); Start-Sleep -Milliseconds 300 }
        }
        elseif ($keys[$i] -eq '^') { [Kbd]::Press([int][char]$keys[$i + 1], $false, $true); $i += 2 }
        else { [Kbd]::Char($keys[$i]); $i++ }
        Start-Sleep -Milliseconds 90
    }
}

function Check([string]$typed, [string]$want) {
    $logStart = if (Test-Path $env:SOUNDSPELL_LOG) { (Get-Content $env:SOUNDSPELL_LOG).Count } else { 0 }
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
    if (($want -split '\|') -ccontains $got) { Write-Host "ok   $typed"; $script:summary += "ok   $typed"; return $true }
    $line = "FAIL $typed   want [$want]   got [$got]"
    Write-Host "--- trace for: $typed"
    Get-Content $env:SOUNDSPELL_LOG | Select-Object -Skip $logStart | ForEach-Object { Write-Host "    $_" }
    Write-Host $line; $script:summary += $line
    return $false
}

Add-Type -AssemblyName System.Drawing

# The strip shows while typing: find where it is from the log, then look at the pixels.
function StripCheck {
    $np = Start-Process notepad -PassThru
    Start-Sleep -Seconds 2
    $null = $shell.AppActivate($np.Id)
    Start-Sleep -Milliseconds 500
    Type-Slowly 'we went to nesesary lengths'
    Start-Sleep -Milliseconds 900
    $line = Get-Content $env:SOUNDSPELL_LOG | Where-Object { $_ -match 'strip at (-?\d+),(-?\d+),(\d+),(\d+) glass=(\w+)' } | Select-Object -Last 1
    $ok = $false
    if ($line -and $line -match 'strip at (-?\d+),(-?\d+),(\d+),(\d+) glass=(\w+) words: (.*)$') {
        $x = [int]$Matches[1]; $y = [int]$Matches[2]; $w = [int]$Matches[3]; $h = [int]$Matches[4]; $glass = $Matches[5]; $words = $Matches[6]
        $bmp = New-Object System.Drawing.Bitmap $w, $h
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.CopyFromScreen($x, $y, 0, 0, $bmp.Size)
        $red = 0; $green = 0; $sum = 0; $n = 0
        for ($i = 0; $i -lt $w; $i += 2) { for ($j = 0; $j -lt $h; $j += 2) {
            $c = $bmp.GetPixel($i, $j); $n++; $sum += ($c.R + $c.G + $c.B) / 3
            if ($c.R -gt $c.G + 40 -and $c.R -gt $c.B + 40) { $red++ }
            if ($c.G -gt $c.R + 25 -and $c.G -gt $c.B + 10) { $green++ }
        } }
        $bmp.Save((Join-Path (Split-Path -Parent $PSScriptRoot) 'strip.png'))
        $avg = [int]($sum / $n)
        $info = "strip ${w}x${h} glass=$glass red=$red green=$green brightness=$avg words: $words"
        Write-Host $info
        $ok = ($words -match 'nesesary=Bad') -and ($words -match 'went=Good') -and $red -gt 5 -and $green -gt 5 -and $avg -gt 90 -and $h -le 32

        # Click the speaker: it reads the sentence and the strip stays up.
        $before = (Get-Content $env:SOUNDSPELL_LOG).Count
        [Kbd]::Click($x + [int]($h / 2), $y + [int]($h / 2))
        Start-Sleep -Milliseconds 900
        $after = Get-Content $env:SOUNDSPELL_LOG | Select-Object -Skip $before
        $spoke = [bool]($after | Where-Object { $_ -match 'speak: we went to nesesary lengths' })
        $hid = [bool]($after | Where-Object { $_ -match 'strip hidden' })
        $info += " speaker: spoke=$spoke hidden=$hid"
        $ok = $ok -and $spoke -and -not $hid
    }
    Stop-Process $np.Id -Force
    $line2 = if ($ok) { "ok   strip shows green and red ($info)" } else { "FAIL strip ($info) [$line]" }
    Write-Host $line2; $script:summary += $line2
    return $ok
}

# Select text, tap Ctrl twice: it is read out (the log shows what was said).
function ReadCheck {
    $np = Start-Process notepad -PassThru
    Start-Sleep -Seconds 2
    $null = $shell.AppActivate($np.Id)
    Start-Sleep -Milliseconds 500
    Type-Slowly 'please read this out'
    [System.Windows.Forms.SendKeys]::SendWait('^a')
    Start-Sleep -Milliseconds 300
    Type-Slowly '{CTRL2}'
    Start-Sleep -Milliseconds 1200
    $said = Get-Content $env:SOUNDSPELL_LOG | Where-Object { $_ -match 'speak: please read this out' }
    Stop-Process $np.Id -Force
    $ok = [bool]$said
    $line = if ($ok) { "ok   Ctrl twice reads the selected text" } else { "FAIL Ctrl twice did not read the selection" }
    Write-Host $line; $script:summary += $line
    return $ok
}

$script:summary = @()
$results = @(
    # Space swaps the word and keeps the space.
    (Check 'hello @@nesesary and' "hello necessary and"),
    # Enter only fixes the word; the next Enter is a normal Enter.
    (Check 'hi @@wensday{ENTER}{ENTER}next' "hi Wednesday`nnext"),
    # Again a few times: this once came out wrong on one run (timing).
    (Check 'so @@beleev{ENTER}{ENTER}yes' "so believe`nyes"),
    (Check 'what a @@serprize{ENTER}{ENTER}ok' "what a surprise`nok"),
    (Check 'the @@peepul{ENTER}{ENTER}here' "the people`nhere"),
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
    # US and UK spellings both count; Irish names are known.
    (Check 'my @@favrit ' "my favourite |my favorite "),
    (Check 'hi @@neev ' "hi Niamh "),
    # The strip: the last word is fine, so Shift twice fixes the red word before it.
    (Check 'I like wensday bananas{SHIFT2}' "I like Wednesday bananas"),
    # Learning: pick the 2nd match once, and next time it comes first by itself.
    (Check '@@gril ^2' "girl "),
    (Check '@@gril ' "girl "),
    # Fixed the same way 3 times above (wensday -> Wednesday): now it happens by itself.
    (Check 'on wensday ' "on Wednesday "),
    (StripCheck),
    (ReadCheck)
)

Stop-Process $app.Id -Force
if ($results -contains $false -and (Test-Path $env:SOUNDSPELL_LOG)) { Write-Host '--- SoundSpell log (end) ---'; Get-Content $env:SOUNDSPELL_LOG -Tail 80 }
Write-Host '--- results ---'
$script:summary | ForEach-Object { Write-Host $_ }
if ($results -contains $false) { exit 1 }
