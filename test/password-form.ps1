# A window with a single password box, for the Notepad checks. Whatever is typed
# in it is written to $Out, so the check can see SoundSpell left it alone.
param([string]$Kind, [string]$Out)
if ($Kind -eq 'classic') {
    Add-Type -AssemblyName System.Windows.Forms
    $f = New-Object Windows.Forms.Form
    $f.Text = 'Password test'; $f.Width = 420; $f.Height = 140; $f.StartPosition = 'CenterScreen'
    $t = New-Object Windows.Forms.TextBox
    $t.UseSystemPasswordChar = $true; $t.Left = 20; $t.Top = 20; $t.Width = 360
    $t.Add_TextChanged({ [IO.File]::WriteAllText($Out, $t.Text) })
    $f.Controls.Add($t)
    $f.Add_Shown({ $f.Activate(); $t.Focus() })
    [Windows.Forms.Application]::Run($f)
} else {
    Add-Type -AssemblyName PresentationFramework
    $w = New-Object Windows.Window
    $w.Title = 'Password test'; $w.Width = 420; $w.Height = 140; $w.WindowStartupLocation = 'CenterScreen'
    $p = New-Object Windows.Controls.PasswordBox
    $p.Add_PasswordChanged({ [IO.File]::WriteAllText($Out, $p.Password) })
    $w.Content = $p
    $w.Add_ContentRendered({ $w.Activate(); $p.Focus() | Out-Null })
    $w.ShowDialog() | Out-Null
}
