# Which kinds of injected keys does Notepad accept here? Prints one line per variant.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices;
public static class Inj {
  [StructLayout(LayoutKind.Sequential)] public struct MI { public int dx, dy; public uint md, fl, t; public IntPtr ex; }
  [StructLayout(LayoutKind.Sequential)] public struct KI { public ushort vk, sc; public uint fl, t; public IntPtr ex; }
  [StructLayout(LayoutKind.Explicit)] public struct U { [FieldOffset(0)] public MI mi; [FieldOffset(0)] public KI ki; }
  [StructLayout(LayoutKind.Sequential)] public struct IN { public uint type; public U u; }
  [DllImport("user32.dll", SetLastError=true)] static extern uint SendInput(uint n, IN[] i, int s);
  [DllImport("user32.dll")] static extern uint MapVirtualKey(uint c, uint t);
  static IN K(ushort vk, ushort sc, uint fl, long ex) { var i = new IN(); i.type = 1; i.u.ki.vk = vk; i.u.ki.sc = sc; i.u.ki.fl = fl; i.u.ki.ex = new IntPtr(ex); return i; }
  public static uint Uni(string s, long ex) { var l = new System.Collections.Generic.List<IN>(); foreach (char c in s) { l.Add(K(0, c, 4, ex)); l.Add(K(0, c, 6, ex)); } return SendInput((uint)l.Count, l.ToArray(), Marshal.SizeOf(typeof(IN))); }
  public static uint Vk(ushort vk, bool scan, long ex) { ushort sc = scan ? (ushort)MapVirtualKey(vk, 0) : (ushort)0; var a = new[] { K(vk, sc, 0, ex), K(vk, sc, 2, ex) }; return SendInput(2, a, Marshal.SizeOf(typeof(IN))); }
}
'@
$shell = New-Object -ComObject WScript.Shell
function Try-It([string]$name, [scriptblock]$send) {
  $np = Start-Process notepad -PassThru; Start-Sleep 2; $null = $shell.AppActivate($np.Id); Start-Sleep -Milliseconds 500
  [System.Windows.Forms.SendKeys]::SendWait('ab'); Start-Sleep -Milliseconds 200
  $r = & $send; Start-Sleep -Milliseconds 400
  [System.Windows.Forms.SendKeys]::SendWait('^a'); [System.Windows.Forms.SendKeys]::SendWait('^c'); Start-Sleep -Milliseconds 300
  $got = Get-Clipboard -Raw; Stop-Process $np.Id -Force
  Write-Host ("{0,-34} sent={1,-3} text=[{2}]" -f $name, $r, $got)
}
Try-It 'unicode X, no marker'        { [Inj]::Uni('X', 0) }
Try-It 'unicode X, marker'           { [Inj]::Uni('X', 0x5350454C) }
Try-It 'backspace vk only, marker'   { [Inj]::Vk(8, $false, 0x5350454C) }
Try-It 'backspace vk+scan, marker'   { [Inj]::Vk(8, $true, 0x5350454C) }
Try-It 'backspace vk only, no marker'{ [Inj]::Vk(8, $false, 0) }
