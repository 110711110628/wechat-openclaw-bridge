$ErrorActionPreference = "Continue"
$log = "C:\setup\heal.log"
function L($m) {
    $line = "{0}  {1}" -f (Get-Date -Format "MM-dd HH:mm:ss"), $m
    Add-Content -Path $log -Value $line -Encoding UTF8
}

$DENG = [string]([char]0x767B) + [string]([char]0x5F55)   # 登录

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @"
using System; using System.Runtime.InteropServices;
public class HM {
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, IntPtr e);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int n);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    public static void Click(int x, int y) {
        SetCursorPos(x, y);
        System.Threading.Thread.Sleep(150);
        mouse_event(0x0002, 0, 0, 0, IntPtr.Zero);
        System.Threading.Thread.Sleep(80);
        mouse_event(0x0004, 0, 0, 0, IntPtr.Zero);
    }
}
"@

$root = [System.Windows.Automation.AutomationElement]::RootElement
$children = $root.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)

$loginWin = $null
$mainWin = $null
foreach ($e in $children) {
    $cn = ""
    try { $cn = $e.Current.ClassName } catch {}
    if ($cn -eq "mmui::LoginWindow") { $loginWin = $e }
    if ($cn -eq "mmui::MainWindow") { $mainWin = $e }
}

# 情况一：退登了。记住的账号还在，直接点「登录」。
if ($loginWin -ne $null) {
    L "DETECTED login window - account was logged out"
    $kids = $loginWin.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
    $slot = $null
    foreach ($x in $kids) {
        $cn = ""; $nm = ""
        try { $cn = $x.Current.ClassName } catch {}
        try { $nm = $x.Current.Name } catch {}
        if ($nm -eq $DENG -and $cn -eq "mmui::XOutlineButton") { $slot = $x; break }
    }
    if ($slot -ne $null) {
        $r = $slot.Current.BoundingRectangle
        $cx = [int]($r.X + $r.Width / 2)
        $cy = [int]($r.Y + $r.Height / 2)
        [HM]::Click($cx, $cy)
        L ("CLICKED login button at {0},{1}" -f $cx, $cy)
        Start-Sleep -Seconds 8
    } else {
        L "login button not found (maybe QR mode or phone confirm)"
    }
    exit
}

# 情况二：主窗口在，但被最小化了。SDK 看不到最小化窗口。
if ($mainWin -ne $null) {
    $h = [IntPtr]$mainWin.Current.NativeWindowHandle
    if ([HM]::IsIconic($h)) {
        [void][HM]::ShowWindow($h, 9)
        [void][HM]::SetForegroundWindow($h)
        L "RESTORED minimized main window"
    }
}

# 情况三：微信进程没了，什么都不做（交给人工，避免乱拉进程）
if ($loginWin -eq $null -and $mainWin -eq $null) {
    $p = Get-Process -Name Weixin -ErrorAction SilentlyContinue
    if (-not $p) { L "WARN no Weixin process at all" }
}
