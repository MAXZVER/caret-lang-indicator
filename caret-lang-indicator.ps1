#Requires -Version 5.1
<#
.SYNOPSIS
    Shows a small macOS-style badge next to the text you are typing in, with
    the current keyboard layout and Caps Lock state.
.DESCRIPTION
    Finding where you type, in order of precision:

      1. GetGUIThreadInfo - the real Windows caret. Classic Win32 fields,
         Office, text inputs in browsers. Badge sits below-right of it.
      2. UI Automation TextPattern - the selection rectangle. This is how
         Chromium and Electron expose the caret, since they never publish a
         Win32 one. Badge sits below-right of it as well.
      3. UI Automation bounding box of a focused text control, when neither
         of the above works. Badge sits to the LEFT of the field.

    The badge is a WPF window with real per-pixel transparency: proper
    rounded corners, a soft shadow, no jagged edges.
.EXAMPLE
    .\caret-lang-indicator.ps1
    .\caret-lang-indicator.ps1 -OnlyWhenCaps
    .\caret-lang-indicator.ps1 -Diagnose
    .\caret-lang-indicator.ps1 -Demo
#>
[CmdletBinding()]
param(
    [ValidateSet('None','Mouse','Corner')]
    [string]$Fallback = 'None',
    [switch]$OnlyWhenCaps,
    [int]$Interval = 120,
    [switch]$NoUia,
    [switch]$Diagnose,
    [switch]$Demo,
    [string]$Log,
    # gap in pixels between the caret and the visible edge of the badge
    [int]$OffsetX = 4,
    [int]$OffsetY = 1,
    # gap between the badge and the left edge of a field, when only the field
    # rectangle is known
    [int]$FieldGap = 10,
    # which side of the field the badge sits on when only the field rectangle
    # is known
    [ValidateSet('Left','Right')]
    [string]$Side = 'Left',
    # What the badge is pinned to.
    #   Field  - the edge of the focused input box. Stable, does not jump
    #            around while you type. Default.
    #   Caret  - follows the text cursor. Precise, but bumps into text and
    #            into app buttons next to the field.
    #   Corner - a fixed spot on screen, never in the way of anything.
    [ValidateSet('Field','Caret','Corner')]
    [string]$Anchor = 'Caret',
    # Below  - the badge hangs under the caret, clear of the line of text
    # Center - level with the line the caret sits on
    [ValidateSet('Below','Center')]
    [string]$VAlign = 'Below',
    # Windows 11 acrylic material instead of the flat macOS-style pill
    [switch]$Glass
)

# the XAML border carries Margin="8" so the drop shadow has room; that margin
# is part of the window, so it has to come off every position we compute
$script:shadowMargin = 8

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName PresentationFramework
Add-Type -AssemblyName PresentationCore
Add-Type -AssemblyName WindowsBase

if (-not ('Native' -as [type])) {
Add-Type @"
using System;
using System.Runtime.InteropServices;

public class Native {
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct GUITHREADINFO {
        public int cbSize;
        public int flags;
        public IntPtr hwndActive;
        public IntPtr hwndFocus;
        public IntPtr hwndCapture;
        public IntPtr hwndMenuOwner;
        public IntPtr hwndMoveSize;
        public IntPtr hwndCaret;
        public RECT rcCaret;
    }

    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern int GetWindowThreadProcessId(IntPtr hWnd, IntPtr pid);
    [DllImport("user32.dll", EntryPoint = "GetWindowThreadProcessId")]
    public static extern int GetWindowThreadPid(IntPtr hWnd, out int pid);
    [DllImport("user32.dll")] public static extern IntPtr GetKeyboardLayout(int idThread);
    [DllImport("user32.dll")] public static extern bool GetGUIThreadInfo(int idThread, ref GUITHREADINFO lpgui);
    [DllImport("user32.dll")] public static extern short GetKeyState(int nVirtKey);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);
    [DllImport("user32.dll", SetLastError = true)] public static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", SetLastError = true)] public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    public const int GWL_EXSTYLE       = -20;
    public const int WS_EX_NOACTIVATE  = 0x08000000;
    public const int WS_EX_TOOLWINDOW  = 0x00000080;
    public const int WS_EX_TRANSPARENT = 0x00000020;
    public const int VK_CAPITAL        = 0x14;

    // click-through, never takes focus, stays out of alt-tab
    public static void MakePassive(IntPtr hWnd) {
        int ex = GetWindowLong(hWnd, GWL_EXSTYLE);
        SetWindowLong(hWnd, GWL_EXSTYLE, ex | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT);
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MARGINS { public int Left, Right, Top, Bottom; }

    [DllImport("dwmapi.dll")]
    public static extern int DwmExtendFrameIntoClientArea(IntPtr hWnd, ref MARGINS m);

    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hWnd, int attr, ref int value, int size);

    public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    public const int DWMWA_SYSTEMBACKDROP_TYPE      = 38;

    // Windows 11 acrylic backdrop plus rounded corners, both drawn by DWM
    // itself - that is what makes the edges smooth without any manual work.
    public static string EnableGlass(IntPtr h) {
        MARGINS m = new MARGINS();
        m.Left = -1; m.Right = -1; m.Top = -1; m.Bottom = -1;
        int r1 = DwmExtendFrameIntoClientArea(h, ref m);

        int backdrop = 3;   // DWMSBT_TRANSIENTWINDOW = acrylic
        int r2 = DwmSetWindowAttribute(h, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, 4);

        int corner = 2;     // DWMWCP_ROUND
        int r3 = DwmSetWindowAttribute(h, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, 4);

        return "extendFrame=" + r1 + " backdrop=" + r2 + " corners=" + r3;
    }
}
"@
}

$script:uiaOk = $false
if (-not $NoUia) {
    try {
        Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
        $script:uiaOk = $true
    } catch {
        $script:uiaOk = $false
    }
}

# --- detection -------------------------------------------------------------
$script:textTypes = @('Edit', 'Document', 'ComboBox')

# UI Automation is by far the most expensive call here, so the focused element
# is fetched once per focus change and reused between ticks. Only the cheap
# part - the selection range - is asked for on every tick, which is what keeps
# the badge glued to the caret without the cost of a full focus lookup.
$script:focusEl      = $null
$script:focusElType  = ''
$script:focusIsText  = $null
$script:lastFgWindow = [IntPtr]::Zero
$script:focusStamp   = 0

function Get-TextPatternCaret($el) {
    try {
        $pattern = $null
        if (-not $el.TryGetCurrentPattern([System.Windows.Automation.TextPattern]::Pattern, [ref]$pattern)) {
            return $null
        }
        $sel = $pattern.GetSelection()
        if ($null -eq $sel -or $sel.Count -eq 0) { return $null }

        $range = $sel[0].Clone()
        $rects = $range.GetBoundingRectangles()
        if ($rects.Count -eq 0) {
            $range.ExpandToEnclosingUnit([System.Windows.Automation.Text.TextUnit]::Character)
            $rects = $range.GetBoundingRectangles()
        }
        if ($rects.Count -eq 0) { return $null }

        $r = $rects[0]
        if ($r.Height -le 0 -or $r.Height -gt 200) { return $null }
        return $r
    } catch {
        return $null
    }
}

function Test-TextElement($el) {
    try {
        $type = $el.Current.ControlType.ProgrammaticName.Replace('ControlType.', '')
        if ($type -eq 'Button' -or $type -eq 'Hyperlink' -or $type -eq 'MenuItem') { return $false }
        if ($script:textTypes -contains $type) { return $true }
        foreach ($p in $el.GetSupportedPatterns()) {
            if ($p.ProgrammaticName -like '*TextPattern*' -or $p.ProgrammaticName -like '*ValuePattern*') {
                return $true
            }
        }
    } catch { }
    return $false
}

function Get-InputState {
    $result = [pscustomobject]@{
        Layout   = '??'
        Caps     = $false
        HasCaret = $false
        X        = 0
        Y        = 0          # bottom of the caret
        CaretT   = 0          # top of the caret, for centring on the line
        CaretH   = 0
        HasField = $false
        FieldL   = 0
        FieldR   = 0
        FieldT   = 0
        FieldH   = 0
        ElType   = ''
        Source   = 'none'
        App      = ''
    }

    $fg = [Native]::GetForegroundWindow()
    if ($fg -eq [IntPtr]::Zero) { return $result }

    $procId = 0
    $tid = [Native]::GetWindowThreadPid($fg, [ref]$procId)
    if ($tid -eq 0) { return $result }
    try { $result.App = (Get-Process -Id $procId -ErrorAction Stop).ProcessName } catch { }

    $hkl = [Native]::GetKeyboardLayout($tid)
    $langId = [int]($hkl.ToInt64() -band 0xFFFF)
    try {
        $result.Layout = ([System.Globalization.CultureInfo]::GetCultureInfo($langId)).TwoLetterISOLanguageName.ToUpper()
    } catch {
        $result.Layout = '??'
    }

    $result.Caps = (([Native]::GetKeyState([Native]::VK_CAPITAL)) -band 1) -eq 1

    $gti = New-Object Native+GUITHREADINFO
    $gti.cbSize = [System.Runtime.InteropServices.Marshal]::SizeOf($gti)
    if ([Native]::GetGUIThreadInfo($tid, [ref]$gti)) {
        $r = $gti.rcCaret
        if ($gti.hwndCaret -ne [IntPtr]::Zero -and ($r.Bottom - $r.Top) -gt 0) {
            $pt = New-Object Native+POINT
            $pt.X = $r.Left
            $pt.Y = $r.Top
            if ([Native]::ClientToScreen($gti.hwndCaret, [ref]$pt)) {
                $result.HasCaret = $true
                $result.X = $pt.X
                $result.CaretT = $pt.Y
                $result.CaretH = $r.Bottom - $r.Top
                $result.Y = $pt.Y + $result.CaretH
                $result.Source = 'caret'
            }
        }
    }

    # Nothing to ask UI Automation when the Win32 caret already answered and
    # the badge follows the caret anyway.
    $needUia = $script:uiaOk -and ((-not $result.HasCaret) -or $Anchor -eq 'Field')

    if ($needUia) {
        try {
            $now = [Environment]::TickCount
            if ($fg -ne $script:lastFgWindow -or ($now - $script:focusStamp) -gt 800 -or $null -eq $script:focusEl) {
                $script:focusEl      = [System.Windows.Automation.AutomationElement]::FocusedElement
                $script:lastFgWindow = $fg
                $script:focusStamp   = $now
                $script:focusIsText  = $null
            }

            $el = $script:focusEl
            if ($el) {
                if ($null -eq $script:focusIsText) {
                    $script:focusElType = $el.Current.ControlType.ProgrammaticName.Replace('ControlType.', '')
                    $script:focusIsText = Test-TextElement $el
                }
                $result.ElType = $script:focusElType

                if ($script:focusIsText -and ((-not $result.HasCaret) -or $Anchor -eq 'Field')) {
                    $fr = $el.Current.BoundingRectangle
                    if (-not $fr.IsEmpty -and $fr.Width -gt 0 -and $fr.Height -gt 0 -and $fr.Height -le 200) {
                        $result.HasField = $true
                        $result.FieldL = [int]$fr.Left
                        $result.FieldR = [int]$fr.Right
                        $result.FieldT = [int]$fr.Top
                        $result.FieldH = [int]$fr.Height
                        if ($result.Source -eq 'none') { $result.Source = 'field' }
                    }
                }

                $caretRect = $null
                if (-not $result.HasCaret) { $caretRect = Get-TextPatternCaret $el }
                if ($caretRect) {
                    $result.HasCaret = $true
                    $result.X = [int]$caretRect.Left
                    $result.CaretT = [int]$caretRect.Top
                    $result.CaretH = [int]$caretRect.Height
                    $result.Y = [int]$caretRect.Bottom
                    $result.Source = 'textpattern'
                }
            }
        } catch {
            # a dead or replaced element throws; drop it and re-fetch next tick
            $script:focusEl = $null
        }
    }

    return $result
}

# --- diagnose --------------------------------------------------------------
if ($Diagnose) {
    Write-Host 'Probing for 10 seconds - click into text fields to test.'
    Write-Host ''
    for ($i = 0; $i -lt 20; $i++) {
        $s = Get-InputState
        '{0,-5} layout={1,-3} caps={2,-6} source={3,-12} at {4},{5}  type={6,-10} fieldL={7} fieldT={8} h={9}' -f `
            "[$i]", $s.Layout, $s.Caps, $s.Source, $s.X, $s.Y, $s.ElType, $s.FieldL, $s.FieldT, $s.FieldH
        Start-Sleep -Milliseconds 500
    }
    return
}

# --- single instance -------------------------------------------------------
$created = $false
$mutex = New-Object System.Threading.Mutex($true, 'Local\CaretLangIndicator', [ref]$created)
if (-not $created) {
    Write-Host 'Already running - exiting.'
    return
}

# --- window (WPF) ----------------------------------------------------------
$script:glass = [bool]$Glass

if ($script:glass) {
    # DWM draws the material, the shadow and the rounded corners, so the
    # window needs no transparency layer and no margin for a fake shadow
    $script:shadowMargin = 0

    [xml]$xaml = @"
<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        WindowStyle="None" AllowsTransparency="False" Background="Transparent"
        ShowInTaskbar="False" Topmost="True" ResizeMode="NoResize"
        SizeToContent="WidthAndHeight" ShowActivated="False"
        Left="-2000" Top="-2000" Focusable="False" IsHitTestVisible="False">
  <!-- a light dark scrim over the acrylic: keeps the white text readable when
       whatever is behind the badge happens to be light -->
  <Border x:Name="Pill" Background="#33000000" Padding="11,5,13,6">
    <StackPanel Orientation="Horizontal" VerticalAlignment="Center">
      <Path x:Name="Glyph" Visibility="Collapsed" Margin="0,0,6,0" VerticalAlignment="Center"
            Fill="#F5F5F7" Stretch="Uniform" Width="11" Height="12"
            Data="M 5,0 L 10,5.5 L 7.4,5.5 L 7.4,9 L 2.6,9 L 2.6,5.5 L 0,5.5 Z M 0,11 L 10,11 L 10,13.5 L 0,13.5 Z"/>
      <TextBlock x:Name="Label" Text="EN" Foreground="#F5F5F7"
                 FontFamily="Segoe UI Variable Small, Segoe UI" FontWeight="SemiBold"
                 FontSize="12.5" VerticalAlignment="Center"/>
    </StackPanel>
  </Border>
</Window>
"@
} else {

[xml]$xaml = @"
<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        WindowStyle="None" AllowsTransparency="True" Background="Transparent"
        ShowInTaskbar="False" Topmost="True" ResizeMode="NoResize"
        SizeToContent="WidthAndHeight" ShowActivated="False"
        Left="-2000" Top="-2000" Focusable="False" IsHitTestVisible="False">
  <Border x:Name="Pill" CornerRadius="11" Padding="9,3,11,4" Background="#EE28282B"
          BorderBrush="#26FFFFFF" BorderThickness="1" Margin="8">
    <Border.Effect>
      <DropShadowEffect BlurRadius="12" ShadowDepth="1.5" Direction="270" Opacity="0.5" Color="#000000"/>
    </Border.Effect>
    <StackPanel Orientation="Horizontal" VerticalAlignment="Center">
      <Path x:Name="Glyph" Visibility="Collapsed" Margin="0,0,6,0" VerticalAlignment="Center"
            Fill="#F5F5F7" Stretch="Uniform" Width="11" Height="12"
            Data="M 5,0 L 10,5.5 L 7.4,5.5 L 7.4,9 L 2.6,9 L 2.6,5.5 L 0,5.5 Z M 0,11 L 10,11 L 10,13.5 L 0,13.5 Z"/>
      <TextBlock x:Name="Label" Text="EN" Foreground="#F5F5F7"
                 FontFamily="Segoe UI Variable Small, Segoe UI" FontWeight="SemiBold"
                 FontSize="12.5" VerticalAlignment="Center"/>
    </StackPanel>
  </Border>
</Window>
"@
}

$reader = New-Object System.Xml.XmlNodeReader $xaml
$win    = [Windows.Markup.XamlReader]::Load($reader)
$pill   = $win.FindName('Pill')
$label  = $win.FindName('Label')
$glyph  = $win.FindName('Glyph')

$brushDark     = New-Object Windows.Media.SolidColorBrush ([Windows.Media.ColorConverter]::ConvertFromString('#EE28282B'))
$brushCaps     = New-Object Windows.Media.SolidColorBrush ([Windows.Media.ColorConverter]::ConvertFromString('#F5FFB340'))
$inkLight      = New-Object Windows.Media.SolidColorBrush ([Windows.Media.ColorConverter]::ConvertFromString('#F5F5F7'))
$inkDark       = New-Object Windows.Media.SolidColorBrush ([Windows.Media.ColorConverter]::ConvertFromString('#281C00'))

$win.Show()
$helper = New-Object System.Windows.Interop.WindowInteropHelper($win)
[Native]::MakePassive($helper.Handle)

if ($script:glass) {
    # WPF paints an opaque background behind the client area unless the
    # composition target is told otherwise; without this the acrylic is hidden
    $src = [System.Windows.Interop.HwndSource]::FromHwnd($helper.Handle)
    $src.CompositionTarget.BackgroundColor = [Windows.Media.Colors]::Transparent
    $dwm = [Native]::EnableGlass($helper.Handle)
    Write-Host "glass: $dwm  (0 = ok for each call)"
}

$win.Hide()

$script:shownState = $false
$script:lastLabel  = ''
$script:lastCaps   = $null

function Set-BadgeContent([string]$text, [bool]$caps) {
    if ($text -eq $script:lastLabel -and $caps -eq $script:lastCaps) { return }
    $script:lastLabel = $text
    $script:lastCaps  = $caps

    $label.Text = $text
    if ($script:glass) {
        # the material stays glass; Caps Lock is shown by tinting the ink
        if ($caps) {
            $label.Foreground = $brushCaps
            $glyph.Fill = $brushCaps
            $glyph.Visibility = 'Visible'
        } else {
            $label.Foreground = $inkLight
            $glyph.Visibility = 'Collapsed'
        }
    }
    elseif ($caps) {
        $pill.Background = $brushCaps
        $label.Foreground = $inkDark
        $glyph.Fill = $inkDark
        $glyph.Visibility = 'Visible'
    } else {
        $pill.Background = $brushDark
        $label.Foreground = $inkLight
        $glyph.Visibility = 'Collapsed'
    }
    $win.UpdateLayout()
}

$script:lastX = [double]::NaN
$script:lastY = [double]::NaN

function Show-Badge([double]$x, [double]$y) {
    # moving a WPF window costs a layout pass, so skip identical positions
    if ($x -ne $script:lastX -or $y -ne $script:lastY) {
        $win.Left = $x
        $win.Top  = $y
        $script:lastX = $x
        $script:lastY = $y
    }
    if (-not $script:shownState) {
        $win.Show()
        $script:shownState = $true
    }
}

function Hide-Badge {
    if ($script:shownState) {
        $win.Hide()
        $script:shownState = $false
    }
}

# --- tray ------------------------------------------------------------------
$menu = New-Object System.Windows.Forms.ContextMenuStrip
$exitItem = $menu.Items.Add('Exit')

$tray = New-Object System.Windows.Forms.NotifyIcon
$tray.Icon = [System.Drawing.SystemIcons]::Information
$tray.Text = 'Caret language indicator'
$tray.ContextMenuStrip = $menu
$tray.Visible = $true

$exitItem.Add_Click({
    $tray.Visible = $false
    [System.Windows.Threading.Dispatcher]::CurrentDispatcher.InvokeShutdown()
})

$screen = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea

# --- loop ------------------------------------------------------------------
$timer = New-Object System.Windows.Threading.DispatcherTimer
$timer.Interval = [TimeSpan]::FromMilliseconds($Interval)
$timer.Add_Tick({
    if ($Demo) {
        Set-BadgeContent 'RU' $true
        Show-Badge ($screen.Left + 300) ($screen.Top + 300)
        return
    }

    $s = Get-InputState
    Set-BadgeContent $s.Layout $s.Caps

    if ($Log) {
        $key = '{0}|{1}|{2}' -f $s.App, $s.Source, $s.ElType
        if ($key -ne $script:lastLogKey) {
            $script:lastLogKey = $key
            $line = '{0}  app={1,-16} source={2,-12} type={3,-12} at {4},{5}  field L={6} T={7} h={8}' -f `
                (Get-Date -Format 'HH:mm:ss'), $s.App, $s.Source, $s.ElType, $s.X, $s.Y, $s.FieldL, $s.FieldT, $s.FieldH
            try { Add-Content -Path $Log -Value $line -Encoding UTF8 } catch { }
        }
    }

    $w = $win.ActualWidth
    $h = $win.ActualHeight
    if ($w -le 0) { $w = 60 }
    if ($h -le 0) { $h = 40 }

    $show = $true
    if ($OnlyWhenCaps -and -not $s.Caps) { $show = $false }

    $x = 0.0
    $y = 0.0

    $m = $script:shadowMargin

    $useCaret = $s.HasCaret -and ($Anchor -eq 'Caret' -or -not $s.HasField)
    # the field branch is tested first below, so it must stand aside whenever
    # the caret is the chosen anchor - otherwise the badge sits still instead
    # of following the cursor
    $useField = $s.HasField -and $Anchor -ne 'Corner' -and -not $useCaret

    if ($show -and $Anchor -eq 'Corner') {
        $x = $screen.Right  - $w + $m - 12
        $y = $screen.Bottom - $h + $m - 12
    } elseif ($show -and $useField) {
        # No caret anywhere, so anchor to the field itself. Left by default:
        # outside the field entirely, so it never sits on the text.
        if ($Side -eq 'Right') {
            $x = $s.FieldR - $FieldGap - $w + $m
        } else {
            $x = $s.FieldL - $FieldGap - $w + $m
            if ($x -lt $screen.Left) { $x = $s.FieldR - $FieldGap - $w + $m }
        }
        if ($s.FieldH -le 120) {
            $y = $s.FieldT + ($s.FieldH - ($h - 2 * $m)) / 2 - $m
        } else {
            $y = $s.FieldT - $m
        }
    } elseif ($show -and $useCaret) {
        # Right of the caret, centred on the line it sits on. Centring is what
        # keeps it level with the text instead of hanging below the line.
        $pillH = $h - 2 * $m
        $x = $s.X + $OffsetX - $m
        if ($VAlign -eq 'Center' -and $s.CaretH -gt 0) {
            $y = $s.CaretT + ($s.CaretH - $pillH) / 2 - $m
        } else {
            # hang under the caret: $s.Y is the bottom of the caret
            $y = $s.Y + $OffsetY - $m
        }
    } elseif ($show -and $Fallback -eq 'Mouse') {
        $p = [System.Windows.Forms.Cursor]::Position
        $x = $p.X + 14
        $y = $p.Y + 14
    } elseif ($show -and $Fallback -eq 'Corner') {
        $x = $screen.Right  - $w - 10
        $y = $screen.Bottom - $h - 10
    } else {
        $show = $false
    }

    if ($show) {
        if ($x + $w -gt $screen.Right)  { $x = $screen.Right  - $w }
        if ($y + $h -gt $screen.Bottom) { $y = $screen.Bottom - $h }
        if ($x -lt $screen.Left) { $x = $screen.Left }
        if ($y -lt $screen.Top)  { $y = $screen.Top }
        Show-Badge $x $y
    } else {
        Hide-Badge
    }
})
$timer.Start()

[System.Windows.Threading.Dispatcher]::Run()

$timer.Stop()
$tray.Visible = $false
$tray.Dispose()
