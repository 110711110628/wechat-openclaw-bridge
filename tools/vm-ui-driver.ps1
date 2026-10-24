#requires -Version 5.1
# Minimal UI driver for WeChat on the VM. Command comes from cmd.txt so the
# scheduled task never has to be recreated with new arguments.
# cmd.txt lines:  info | shot | focus <titlepart> | click <x> <y> |
#                 dclick <x> <y> | rclick <x> <y> | key <vk> | type <text> |
#                 sleep <ms> | move <x> <y> | movewin <title> <x> <y> <w> <h>
#                 | shot <name>
$ErrorActionPreference = 'Continue'
$dir = 'C:\setup\avatar'
$cmdFile = Join-Path $dir 'cmd.txt'
$logFile = Join-Path $dir 'ui.log'

function Log([string]$m) {
    $line = ('{0} {1}' -f (Get-Date -Format 'HH:mm:ss'), $m)
    Add-Content -Path $logFile -Value $line -Encoding UTF8
}

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public class W {
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, int dx, int dy, uint d, UIntPtr e);
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte sc, uint f, UIntPtr e);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  public struct RECT { public int Left, Top, Right, Bottom; }
}
'@

$LEFTDOWN = 0x0002; $LEFTUP = 0x0004; $RIGHTDOWN = 0x0008; $RIGHTUP = 0x0010
$KEYUP = 0x0002

function Get-Windows {
    $list = New-Object System.Collections.ArrayList
    $cb = [W+EnumProc]{
        param($h, $l)
        if ([W]::IsWindowVisible($h)) {
            $sb = New-Object System.Text.StringBuilder 512
            [void][W]::GetWindowTextW($h, $sb, 512)
            $cn = New-Object System.Text.StringBuilder 256
            [void][W]::GetClassNameW($h, $cn, 256)
            $r = New-Object W+RECT
            [void][W]::GetWindowRect($h, [ref]$r)
            $t = $sb.ToString()
            if ($t -ne '' -or $cn.ToString() -like '*WeChat*') {
                [void]$list.Add([pscustomobject]@{
                    H = $h.ToInt64(); Class = $cn.ToString(); Title = $t
                    X = $r.Left; Y = $r.Top; W = ($r.Right - $r.Left); Hh = ($r.Bottom - $r.Top)
                })
            }
        }
        return $true
    }
    [void][W]::EnumWindows($cb, [IntPtr]::Zero)
    return $list
}

function Find-Win([string]$part) {
    foreach ($w in Get-Windows) {
        if ($w.Title -like "*$part*") { return $w }
    }
    return $null
}

function Do-Click([int]$x, [int]$y, [string]$kind) {
    [void][W]::SetCursorPos($x, $y)
    Start-Sleep -Milliseconds 120
    if ($kind -eq 'r') {
        [W]::mouse_event($RIGHTDOWN, 0, 0, 0, [UIntPtr]::Zero)
        Start-Sleep -Milliseconds 60
        [W]::mouse_event($RIGHTUP, 0, 0, 0, [UIntPtr]::Zero)
    } else {
        [W]::mouse_event($LEFTDOWN, 0, 0, 0, [UIntPtr]::Zero)
        Start-Sleep -Milliseconds 60
        [W]::mouse_event($LEFTUP, 0, 0, 0, [UIntPtr]::Zero)
    }
    if ($kind -eq 'd') {
        Start-Sleep -Milliseconds 90
        [W]::mouse_event($LEFTDOWN, 0, 0, 0, [UIntPtr]::Zero)
        Start-Sleep -Milliseconds 60
        [W]::mouse_event($LEFTUP, 0, 0, 0, [UIntPtr]::Zero)
    }
}

Log ('--- run pid=' + $PID + ' ---')
if (-not (Test-Path $cmdFile)) { Log 'no cmd.txt'; exit 1 }

foreach ($raw in (Get-Content -Path $cmdFile -Encoding UTF8)) {
    $line = $raw.Trim()
    if ($line -eq '' -or $line.StartsWith('#')) { continue }
    $p = $line -split '\s+', 2
    $op = $p[0]
    $arg = ''
    if ($p.Count -gt 1) { $arg = $p[1] }
    Log ('op=' + $op + ' arg=' + $arg)
    try {
        switch ($op) {
            'info' {
                foreach ($w in Get-Windows) {
                    Log ('WIN ' + $w.X + ',' + $w.Y + ' ' + $w.W + 'x' + $w.Hh + ' class=' + $w.Class + ' title=' + $w.Title)
                }
            }
            'shot' {
                $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
                $bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
                $g = [System.Drawing.Graphics]::FromImage($bmp)
                $g.CopyFromScreen($b.Location, [System.Drawing.Point]::Empty, $b.Size)
                $nm = 'shot'
                if ($arg -ne '') { $nm = $arg.Trim() }
                $out = Join-Path $dir ($nm + '.png')
                $bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
                $g.Dispose(); $bmp.Dispose()
                Log ('shot saved ' + $nm + ' ' + $b.Width + 'x' + $b.Height)
            }
            'focus' {
                $w = Find-Win $arg
                if ($w -eq $null) { Log ('focus: not found ' + $arg) }
                else {
                    [void][W]::ShowWindow([IntPtr]$w.H, 9)
                    Start-Sleep -Milliseconds 200
                    [void][W]::SetForegroundWindow([IntPtr]$w.H)
                    Log ('focused ' + $w.Title)
                }
                Start-Sleep -Milliseconds 350
            }
            'move' {
                $xy = $arg -split ' '
                [void][W]::SetCursorPos([int]$xy[0], [int]$xy[1])
                Log 'moved'
            }
            'movewin' {
                # main WeChat window sits at y=-48 so its top strip (the account
                # avatar lives there) is off-screen. Pick the biggest window
                # titled with the given text and put it fully on screen.
                $p4 = $arg -split ' '
                $want = $p4[0]
                $best = $null; $area = 0
                foreach ($w in Get-Windows) {
                    if ($w.Title -like "*$want*") {
                        $a = $w.W * $w.Hh
                        if ($a -gt $area) { $area = $a; $best = $w }
                    }
                }
                if ($best -eq $null) { Log 'movewin: not found' }
                else {
                    $flags = 0x0004 -bor 0x0040          # NOZORDER | SHOWWINDOW
                    [void][W]::SetWindowPos([IntPtr]$best.H, [IntPtr]::Zero,
                        [int]$p4[1], [int]$p4[2], [int]$p4[3], [int]$p4[4], $flags)
                    Start-Sleep -Milliseconds 400
                    $r = New-Object W+RECT
                    [void][W]::GetWindowRect([IntPtr]$best.H, [ref]$r)
                    Log ('movewin -> ' + $r.Left + ',' + $r.Top + ' ' + ($r.Right - $r.Left) + 'x' + ($r.Bottom - $r.Top))
                }
            }
            'click'  { $xy = $arg -split ' '; Do-Click ([int]$xy[0]) ([int]$xy[1]) 'l'; Start-Sleep -Milliseconds 400; Log 'clicked' }
            'dclick' { $xy = $arg -split ' '; Do-Click ([int]$xy[0]) ([int]$xy[1]) 'd'; Start-Sleep -Milliseconds 500; Log 'dclicked' }
            'rclick' { $xy = $arg -split ' '; Do-Click ([int]$xy[0]) ([int]$xy[1]) 'r'; Start-Sleep -Milliseconds 400; Log 'rclicked' }
            'key' {
                [W]::keybd_event([byte][int]$arg, 0, 0, [UIntPtr]::Zero)
                Start-Sleep -Milliseconds 60
                [W]::keybd_event([byte][int]$arg, 0, $KEYUP, [UIntPtr]::Zero)
                Log ('key ' + $arg)
                Start-Sleep -Milliseconds 250
            }
            'type' {
                # Non-ASCII never survives SendKeys, so go through the clipboard.
                Set-Clipboard -Value $arg
                Start-Sleep -Milliseconds 150
                [W]::keybd_event(0x11, 0, 0, [UIntPtr]::Zero)          # Ctrl down
                [W]::keybd_event(0x56, 0, 0, [UIntPtr]::Zero)          # V
                Start-Sleep -Milliseconds 60
                [W]::keybd_event(0x56, 0, $KEYUP, [UIntPtr]::Zero)
                [W]::keybd_event(0x11, 0, $KEYUP, [UIntPtr]::Zero)
                Log 'typed via clipboard'
                Start-Sleep -Milliseconds 300
            }
            'sleep' { Start-Sleep -Milliseconds ([int]$arg) }
            default { Log ('unknown op ' + $op) }
        }
    } catch {
        Log ('ERR ' + $_.Exception.Message)
    }
}
Log '--- done ---'
