// Caret language indicator - shows the keyboard layout and Caps Lock state
// next to the text caret. Same behaviour as caret-lang-indicator.ps1, compiled
// so it does not carry a PowerShell host around.
//
// Build with the csc.exe that ships with Windows - see build.ps1.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;

using Forms = System.Windows.Forms;

static class Native
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct GUITHREADINFO
    {
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

    [StructLayout(LayoutKind.Sequential)]
    public struct MARGINS { public int Left, Right, Top, Bottom; }

    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern int GetWindowThreadProcessId(IntPtr hWnd, out int pid);
    [DllImport("user32.dll")] public static extern IntPtr GetKeyboardLayout(int idThread);
    [DllImport("user32.dll")] public static extern int GetKeyboardLayoutList(int nBuff, [Out] IntPtr[] lpList);
    [DllImport("user32.dll")] public static extern bool GetGUIThreadInfo(int idThread, ref GUITHREADINFO gti);
    [DllImport("user32.dll")] public static extern short GetKeyState(int nVirtKey);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr hWnd, ref POINT pt);
    [DllImport("user32.dll", SetLastError = true)] public static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", SetLastError = true)] public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    [DllImport("dwmapi.dll")] public static extern int DwmExtendFrameIntoClientArea(IntPtr hWnd, ref MARGINS m);
    [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr hWnd, int attr, ref int value, int size);

    const int GWL_EXSTYLE       = -20;
    const int WS_EX_NOACTIVATE  = 0x08000000;
    const int WS_EX_TOOLWINDOW  = 0x00000080;
    const int WS_EX_TRANSPARENT = 0x00000020;
    public const int VK_CAPITAL = 0x14;

    public static void MakePassive(IntPtr hWnd)
    {
        int ex = GetWindowLong(hWnd, GWL_EXSTYLE);
        SetWindowLong(hWnd, GWL_EXSTYLE, ex | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT);
    }

    public static void EnableGlass(IntPtr h)
    {
        MARGINS m = new MARGINS();
        m.Left = -1; m.Right = -1; m.Top = -1; m.Bottom = -1;
        DwmExtendFrameIntoClientArea(h, ref m);
        int backdrop = 3;               // acrylic
        DwmSetWindowAttribute(h, 38, ref backdrop, 4);
        int corner = 2;                 // rounded
        DwmSetWindowAttribute(h, 33, ref corner, 4);
    }
}

class InputState
{
    public string Layout = "??";
    public bool Caps;
    public bool HasCaret;
    public int X, Y, CaretT, CaretH;
    public bool HasField;
    public int FieldL, FieldR, FieldT, FieldH;
}

static class Options
{
    public static string Anchor   = "Caret";   // Caret | Field | Corner
    public static string VAlign   = "Below";   // Below | Center
    public static string Side     = "Left";    // Left  | Right
    public static string Fallback = "None";    // None  | Mouse | Corner
    public static int OffsetX  = 8;
    public static int OffsetY  = 2;
    public static int FieldGap = 12;
    public static int Interval = 120;
    public static bool OnlyWhenCaps;
    public static bool Glass;
    // macOS-style: stay out of the way, appear for a moment when the input
    // source changes. Also means UI Automation is only touched at that moment
    // instead of several times a second.
    public static bool OnlyOnChange;
    public static int ShowMs = 1200;
    // Render every installed layout in a row with a sliding selection, the way
    // the macOS input source HUD does, instead of a single pill.
    public static bool Switcher;

    public static bool Install;
    public static bool Uninstall;
    public static bool NoPrompt;
    // -Log <file>: record what the panel decides and why, so a "it is always
    // on" report can be checked instead of guessed at
    public static string LogPath;

    // the display options, rebuilt as a command line so the Startup shortcut
    // keeps whatever the user asked for
    public static string PassThrough = "";

    public static void Parse(string[] args)
    {
        var kept = new List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i].TrimStart('-', '/').ToLowerInvariant();
            string next = (i + 1 < args.Length) ? args[i + 1] : null;
            switch (a)
            {
                case "anchor":       Anchor = next; kept.Add("-Anchor"); kept.Add(next); i++; break;
                case "valign":       VAlign = next; kept.Add("-VAlign"); kept.Add(next); i++; break;
                case "side":         Side = next; kept.Add("-Side"); kept.Add(next); i++; break;
                case "fallback":     Fallback = next; kept.Add("-Fallback"); kept.Add(next); i++; break;
                case "offsetx":      OffsetX = int.Parse(next); kept.Add("-OffsetX"); kept.Add(next); i++; break;
                case "offsety":      OffsetY = int.Parse(next); kept.Add("-OffsetY"); kept.Add(next); i++; break;
                case "fieldgap":     FieldGap = int.Parse(next); kept.Add("-FieldGap"); kept.Add(next); i++; break;
                case "interval":     Interval = int.Parse(next); intervalSet = true; kept.Add("-Interval"); kept.Add(next); i++; break;
                case "onlywhencaps": OnlyWhenCaps = true; kept.Add("-OnlyWhenCaps"); break;
                case "onlyonchange": OnlyOnChange = true; kept.Add("-OnlyOnChange"); break;
                case "showms":       ShowMs = int.Parse(next); kept.Add("-ShowMs"); kept.Add(next); i++; break;
                case "switcher":     Switcher = true; kept.Add("-Switcher"); break;
                case "glass":        Glass = true; kept.Add("-Glass"); break;

                case "install":      Install = true; break;
                case "uninstall":    Uninstall = true; break;
                case "noprompt":     NoPrompt = true; break;
                case "log":          LogPath = next; i++; break;
            }
        }

        PassThrough = string.Join(" ", kept.ToArray());

        // While hidden, a tick is three same-process calls, so it can be far
        // more frequent than the old default - and the panel has to appear the
        // moment the layout changes, not up to 120 ms later.
        if (OnlyOnChange && !intervalSet) Interval = 15;
    }

    static bool intervalSet;
}

static class Detector
{
    static readonly string[] TextTypes = { "Edit", "Document", "ComboBox" };

    // the focused element is expensive to look up, so it is cached per focus
    static AutomationElement focusEl;
    static string focusElType = "";
    static bool? focusIsText;
    static IntPtr lastFg = IntPtr.Zero;
    static int focusStamp;

    static bool IsTextElement(AutomationElement el)
    {
        try
        {
            string type = el.Current.ControlType.ProgrammaticName.Replace("ControlType.", "");
            if (type == "Button" || type == "Hyperlink" || type == "MenuItem") return false;
            foreach (string t in TextTypes) if (t == type) return true;
            foreach (AutomationPattern p in el.GetSupportedPatterns())
            {
                if (p.ProgrammaticName.Contains("TextPattern") || p.ProgrammaticName.Contains("ValuePattern"))
                    return true;
            }
        }
        catch { }
        return false;
    }

    static bool TryTextPatternCaret(AutomationElement el, out Rect rect)
    {
        rect = Rect.Empty;
        try
        {
            object patternObj;
            if (!el.TryGetCurrentPattern(TextPattern.Pattern, out patternObj)) return false;
            TextPattern tp = (TextPattern)patternObj;
            TextPatternRange[] sel = tp.GetSelection();
            if (sel == null || sel.Length == 0) return false;

            TextPatternRange range = sel[0].Clone();
            Rect[] rects = range.GetBoundingRectangles();
            if (rects.Length == 0)
            {
                range.ExpandToEnclosingUnit(TextUnit.Character);
                rects = range.GetBoundingRectangles();
            }
            if (rects.Length == 0) return false;

            Rect r = rects[0];
            if (r.Height <= 0 || r.Height > 200) return false;
            rect = r;
            return true;
        }
        catch { return false; }
    }

    // Two-letter codes for every layout installed in the system, in the order
    // Windows keeps them. Read once at startup - installing a layout mid-run is
    // rare enough not to poll for.
    public static string[] GetLayouts()
    {
        try
        {
            int n = Native.GetKeyboardLayoutList(0, null);
            if (n <= 0) return new string[0];
            IntPtr[] list = new IntPtr[n];
            Native.GetKeyboardLayoutList(n, list);

            System.Collections.Generic.List<string> outp = new System.Collections.Generic.List<string>();
            foreach (IntPtr hkl in list)
            {
                string code;
                try { code = CultureInfo.GetCultureInfo((int)(hkl.ToInt64() & 0xFFFF)).TwoLetterISOLanguageName.ToUpperInvariant(); }
                catch { continue; }
                if (!outp.Contains(code)) outp.Add(code);
            }
            return outp.ToArray();
        }
        catch { return new string[0]; }
    }

    // Layout and Caps Lock only: three cheap same-process calls, no UI
    // Automation. Safe to run on the dispatcher every tick.
    public static void ReadQuick(out string layout, out bool caps)
    {
        IntPtr ignored;
        ReadQuick(out layout, out caps, out ignored);
    }

    public static void ReadQuick(out string layout, out bool caps, out IntPtr foreground)
    {
        layout = "??";
        caps = (Native.GetKeyState(Native.VK_CAPITAL) & 1) == 1;

        IntPtr fg = Native.GetForegroundWindow();
        foreground = fg;
        if (fg == IntPtr.Zero) return;
        int procId;
        int tid = Native.GetWindowThreadProcessId(fg, out procId);
        if (tid == 0) return;

        IntPtr hkl = Native.GetKeyboardLayout(tid);
        int langId = (int)(hkl.ToInt64() & 0xFFFF);
        try { layout = CultureInfo.GetCultureInfo(langId).TwoLetterISOLanguageName.ToUpperInvariant(); }
        catch { layout = "??"; }
    }

    public static InputState Read()
    {
        InputState s = new InputState();

        IntPtr fg = Native.GetForegroundWindow();
        if (fg == IntPtr.Zero) return s;

        int procId;
        int tid = Native.GetWindowThreadProcessId(fg, out procId);
        if (tid == 0) return s;

        IntPtr hkl = Native.GetKeyboardLayout(tid);
        int langId = (int)(hkl.ToInt64() & 0xFFFF);
        try { s.Layout = CultureInfo.GetCultureInfo(langId).TwoLetterISOLanguageName.ToUpperInvariant(); }
        catch { s.Layout = "??"; }

        s.Caps = (Native.GetKeyState(Native.VK_CAPITAL) & 1) == 1;

        Native.GUITHREADINFO gti = new Native.GUITHREADINFO();
        gti.cbSize = Marshal.SizeOf(typeof(Native.GUITHREADINFO));
        if (Native.GetGUIThreadInfo(tid, ref gti))
        {
            Native.RECT r = gti.rcCaret;
            if (gti.hwndCaret != IntPtr.Zero && (r.Bottom - r.Top) > 0)
            {
                Native.POINT pt = new Native.POINT();
                pt.X = r.Left; pt.Y = r.Top;
                if (Native.ClientToScreen(gti.hwndCaret, ref pt))
                {
                    s.HasCaret = true;
                    s.X = pt.X;
                    s.CaretT = pt.Y;
                    s.CaretH = r.Bottom - r.Top;
                    s.Y = pt.Y + s.CaretH;
                }
            }
        }

        bool needUia = !s.HasCaret || Options.Anchor == "Field";
        if (!needUia) return s;

        try
        {
            int now = Environment.TickCount;
            if (fg != lastFg || (now - focusStamp) > 800 || focusEl == null)
            {
                focusEl = AutomationElement.FocusedElement;
                lastFg = fg;
                focusStamp = now;
                focusIsText = null;
            }

            AutomationElement el = focusEl;
            if (el != null)
            {
                if (!focusIsText.HasValue)
                {
                    focusElType = el.Current.ControlType.ProgrammaticName.Replace("ControlType.", "");
                    focusIsText = IsTextElement(el);
                }

                if (focusIsText.Value && (!s.HasCaret || Options.Anchor == "Field"))
                {
                    Rect fr = el.Current.BoundingRectangle;
                    if (!fr.IsEmpty && fr.Width > 0 && fr.Height > 0 && fr.Height <= 200)
                    {
                        s.HasField = true;
                        s.FieldL = (int)fr.Left;
                        s.FieldR = (int)fr.Right;
                        s.FieldT = (int)fr.Top;
                        s.FieldH = (int)fr.Height;
                    }
                }

                if (!s.HasCaret)
                {
                    Rect cr;
                    if (TryTextPatternCaret(el, out cr))
                    {
                        s.HasCaret = true;
                        s.X = (int)cr.Left;
                        s.CaretT = (int)cr.Top;
                        s.CaretH = (int)cr.Height;
                        s.Y = (int)cr.Bottom;
                    }
                }
            }
        }
        catch { focusEl = null; }

        return s;
    }
}

class Badge
{
    public Window Win;
    Border pill;
    TextBlock label;
    System.Windows.Shapes.Path glyph;

    readonly Brush inkLight = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F5F5F7"));
    readonly Brush inkDark  = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#281C00"));
    readonly Brush pillDark = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#EE28282B"));
    readonly Brush pillCaps = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F5FFB340"));

    string lastText = null;
    bool lastCaps;
    double lastX = double.NaN, lastY = double.NaN;
    bool shown;

    public double Margin { get; private set; }

    public Badge()
    {
        Margin = Options.Glass ? 0 : 8;

        glyph = new System.Windows.Shapes.Path();
        glyph.Data = Geometry.Parse("M 5,0 L 10,5.5 L 7.4,5.5 L 7.4,9 L 2.6,9 L 2.6,5.5 L 0,5.5 Z M 0,11 L 10,11 L 10,13.5 L 0,13.5 Z");
        glyph.Fill = inkLight;
        glyph.Stretch = Stretch.Uniform;
        glyph.Width = 11;
        glyph.Height = 12;
        glyph.Margin = new Thickness(0, 0, 6, 0);
        glyph.VerticalAlignment = VerticalAlignment.Center;
        glyph.Visibility = Visibility.Collapsed;

        label = new TextBlock();
        label.Text = "EN";
        label.Foreground = inkLight;
        label.FontFamily = new FontFamily("Segoe UI Variable Small, Segoe UI");
        label.FontWeight = FontWeights.SemiBold;
        label.FontSize = 12.5;
        label.VerticalAlignment = VerticalAlignment.Center;

        StackPanel row = new StackPanel();
        row.Orientation = Orientation.Horizontal;
        row.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(glyph);
        row.Children.Add(label);

        pill = new Border();
        pill.Child = Options.Switcher ? (UIElement)BuildSwitcher() : row;
        if (Options.Glass)
        {
            pill.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#33000000"));
            pill.Padding = new Thickness(11, 5, 13, 6);
        }
        else
        {
            pill.Background = pillDark;
            pill.CornerRadius = new CornerRadius(Options.Switcher ? 12 : 11);
            pill.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#26FFFFFF"));
            pill.BorderThickness = new Thickness(1);
            // in switcher mode the cells carry their own spacing
            pill.Padding = Options.Switcher ? new Thickness(4) : new Thickness(9, 3, 11, 4);
            pill.Margin = new Thickness(8);
            // The shadow is a software blur recomputed every frame on a layered
            // window, which makes the sliding selection stutter. Skip it in
            // switcher mode; the border already separates the panel from what
            // is behind it.
            if (!Options.Switcher)
            {
                DropShadowEffect fx = new DropShadowEffect();
                fx.BlurRadius = 12; fx.ShadowDepth = 1.5; fx.Direction = 270; fx.Opacity = 0.5;
                fx.Color = Colors.Black;
                pill.Effect = fx;
            }
        }

        Win = new Window();
        Win.WindowStyle = WindowStyle.None;
        Win.AllowsTransparency = !Options.Glass;
        Win.Background = Brushes.Transparent;
        Win.ShowInTaskbar = false;
        Win.Topmost = true;
        Win.ResizeMode = ResizeMode.NoResize;
        Win.SizeToContent = SizeToContent.WidthAndHeight;
        Win.ShowActivated = false;
        Win.Focusable = false;
        Win.IsHitTestVisible = false;
        Win.Left = -2000;
        Win.Top = -2000;
        Win.Content = pill;

        Win.Show();
        IntPtr h = new WindowInteropHelper(Win).Handle;
        Native.MakePassive(h);
        if (Options.Glass)
        {
            HwndSource src = HwndSource.FromHwnd(h);
            if (src != null && src.CompositionTarget != null)
                src.CompositionTarget.BackgroundColor = Colors.Transparent;
            Native.EnableGlass(h);
        }
        // Keep the window alive and just fade it: creating and destroying a
        // layered window on every switch is the slowest step in the path.
        // Glass mode keeps Show/Hide - forcing opacity on a non-layered window
        // would fight the DWM backdrop.
        if (Options.Glass) Win.Hide(); else Win.Opacity = 0;
    }

    // --- switcher mode ---------------------------------------------------
    // A row of fixed-width cells, one per installed layout, with a rounded
    // selection that slides between them. Fixed cells keep the geometry
    // trivial and match how the macOS HUD lays its sources out.

    const double CellW = 46, CellH = 28;
    Canvas track;
    Border sel;
    TranslateTransform slide;
    string[] layouts = new string[0];
    TextBlock[] cells = new TextBlock[0];
    int selIndex = -1;

    readonly Brush accent   = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0A84FF")); // macOS system blue, dark appearance
    readonly Brush inkIdle  = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#98989D")); // secondary label

    Canvas BuildSwitcher()
    {
        layouts = Detector.GetLayouts();
        if (layouts.Length == 0) layouts = new string[] { "EN" };

        track = new Canvas();
        track.Width = CellW * layouts.Length;
        track.Height = CellH;

        sel = new Border();
        sel.Width = CellW - 8;
        sel.Height = CellH - 6;
        sel.CornerRadius = new CornerRadius(7);
        sel.Background = accent;
        Canvas.SetTop(sel, 3);
        Canvas.SetLeft(sel, 4);
        sel.Opacity = 0;                       // nothing selected until the first update
        // Slide via a render transform, not Canvas.Left: moving the attached
        // property re-runs measure and arrange on every frame, and this window
        // is layered (AllowsTransparency), so all of that is done in software.
        slide = new TranslateTransform();
        sel.RenderTransform = slide;
        track.Children.Add(sel);

        cells = new TextBlock[layouts.Length];
        for (int i = 0; i < layouts.Length; i++)
        {
            TextBlock t = new TextBlock();
            t.Text = layouts[i];
            t.Foreground = inkIdle;
            t.FontFamily = new FontFamily("Segoe UI Variable Small, Segoe UI");
            t.FontWeight = FontWeights.SemiBold;
            t.FontSize = 12.5;
            t.Width = CellW;
            t.Height = CellH;
            t.TextAlignment = TextAlignment.Center;
            t.Padding = new Thickness(0, 6, 0, 0);
            Canvas.SetLeft(t, i * CellW);
            Canvas.SetTop(t, 0);
            track.Children.Add(t);
            cells[i] = t;
        }
        return track;
    }

    void SelectCell(string text, bool caps)
    {
        int idx = -1;
        for (int i = 0; i < layouts.Length; i++) if (layouts[i] == text) { idx = i; break; }
        if (idx < 0) return;

        sel.Background = caps ? pillCaps : accent;
        for (int i = 0; i < cells.Length; i++)
            cells[i].Foreground = (i == idx) ? (caps ? inkDark : inkLight) : inkIdle;

        double target = idx * CellW;
        if (selIndex < 0)
        {
            // first paint: appear in place rather than sliding in from the edge
            slide.BeginAnimation(TranslateTransform.XProperty, null);
            slide.X = target;
            sel.Opacity = 1;
        }
        else if (idx != selIndex)
        {
            DoubleAnimation a = new DoubleAnimation(target, new Duration(TimeSpan.FromMilliseconds(170)));
            a.EasingFunction = new CubicEase();   // EaseOut by default
            slide.BeginAnimation(TranslateTransform.XProperty, a);
        }
        selIndex = idx;
    }

    public void SetContent(string text, bool caps)
    {
        if (text == lastText && caps == lastCaps) return;
        lastText = text;
        lastCaps = caps;

        // No UpdateLayout here: the switcher's size never changes, and forcing a
        // synchronous layout pass on a layered window is exactly what we are
        // trying to avoid.
        if (Options.Switcher) { SelectCell(text, caps); return; }

        label.Text = text;
        if (Options.Glass)
        {
            label.Foreground = caps ? pillCaps : inkLight;
            glyph.Fill = caps ? pillCaps : inkLight;
            glyph.Visibility = caps ? Visibility.Visible : Visibility.Collapsed;
        }
        else if (caps)
        {
            pill.Background = pillCaps;
            label.Foreground = inkDark;
            glyph.Fill = inkDark;
            glyph.Visibility = Visibility.Visible;
        }
        else
        {
            pill.Background = pillDark;
            label.Foreground = inkLight;
            glyph.Visibility = Visibility.Collapsed;
        }
        Win.UpdateLayout();
    }

    public void ShowAt(double x, double y)
    {
        if (x != lastX || y != lastY)
        {
            Win.Left = x;
            Win.Top = y;
            lastX = x; lastY = y;
        }
        if (!shown)
        {
            if (Options.Glass) Win.Show(); else Win.Opacity = 1;
            shown = true;
        }
    }

    public void HideBadge()
    {
        if (shown)
        {
            if (Options.Glass) Win.Hide(); else Win.Opacity = 0;
            shown = false;
        }
    }
}

// Tray icon drawn at run time rather than shipped as a .ico: it has to carry
// the current layout anyway, the way the macOS menu bar shows the active input
// source, so it cannot be a static image. Types are fully qualified because
// System.Drawing and System.Windows.Media collide on Brush, Font and Color.
static class TrayArt
{
    [DllImport("user32.dll", SetLastError = true)]
    static extern bool DestroyIcon(IntPtr hIcon);

    public static System.Drawing.Icon Make(string text, bool caps)
    {
        const int Size = 32;
        using (System.Drawing.Bitmap bmp = new System.Drawing.Bitmap(Size, Size))
        using (System.Drawing.Graphics g = System.Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
            g.Clear(System.Drawing.Color.Transparent);

            System.Drawing.Color ink = caps
                ? System.Drawing.Color.FromArgb(255, 245, 179, 64)   // amber, matches the caps pill
                : System.Drawing.Color.White;

            using (System.Drawing.Brush b = new System.Drawing.SolidBrush(ink))
            // GenericTypographic, not the default: the default format pads a
            // string by roughly a sixth of an em on each side, which on a 32 px
            // tile costs several points of type size for nothing.
            using (System.Drawing.StringFormat sf = (System.Drawing.StringFormat)System.Drawing.StringFormat.GenericTypographic.Clone())
            {
                sf.Alignment = System.Drawing.StringAlignment.Center;
                sf.LineAlignment = System.Drawing.StringAlignment.Center;
                sf.FormatFlags = System.Drawing.StringFormatFlags.NoWrap;

                // Fill the tile: start large and step down until it fits, rather
                // than hard-coding a size that only suits two-letter codes.
                System.Drawing.Font f = null;
                try
                {
                    for (float px = 30f; px >= 10f; px -= 1f)
                    {
                        if (f != null) f.Dispose();
                        f = new System.Drawing.Font("Segoe UI", px, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Pixel);
                        // Measure unconstrained: passing a width here clamps the
                        // result to it, so the text always looks like it fits
                        // and the loop never shrinks anything.
                        System.Drawing.SizeF m = g.MeasureString(text, f, System.Drawing.PointF.Empty, sf);
                        if (m.Width <= Size - 1 && m.Height <= Size - 1) break;
                    }
                    g.DrawString(text, f, b, new System.Drawing.RectangleF(0, 0, Size, Size), sf);
                }
                finally { if (f != null) f.Dispose(); }
            }

            // FromHandle does not own the handle, so clone and free the original.
            IntPtr h = bmp.GetHicon();
            try { return (System.Drawing.Icon)System.Drawing.Icon.FromHandle(h).Clone(); }
            finally { DestroyIcon(h); }
        }
    }
}

static class Log
{
    static readonly object gate = new object();

    public static void Write(string format, params object[] args)
    {
        if (string.IsNullOrEmpty(Options.LogPath)) return;
        try
        {
            lock (gate)
            {
                File.AppendAllText(Options.LogPath,
                    DateTime.Now.ToString("HH:mm:ss.fff") + "  " + string.Format(format, args) +
                    Environment.NewLine);
            }
        }
        catch { }
    }
}

// One file is the whole product: it installs and removes itself, so nobody has
// to keep a PowerShell script next to it or fight the execution policy.
static class Installer
{
    public const string AppName  = "CaretLangIndicator";
    public const string LinkName = "Caret Language Indicator.lnk";

    public static string InstallDir
    {
        get
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName);
        }
    }

    public static string InstalledExe { get { return Path.Combine(InstallDir, AppName + ".exe"); } }

    public static string ShortcutPath
    {
        get
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), LinkName);
        }
    }

    public static string CurrentExe
    {
        get { return System.Reflection.Assembly.GetExecutingAssembly().Location; }
    }

    public static bool RunningFromInstallDir
    {
        get
        {
            try
            {
                return string.Equals(Path.GetFullPath(CurrentExe), Path.GetFullPath(InstalledExe),
                                     StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }
    }

    public static bool IsInstalled { get { return File.Exists(InstalledExe); } }

    // WScript.Shell through late binding: no COM reference to add, no extra
    // assembly to ship
    static void CreateShortcut(string linkPath, string target, string arguments, string workDir)
    {
        Type shellType = Type.GetTypeFromProgID("WScript.Shell");
        object shell = Activator.CreateInstance(shellType);
        try
        {
            object link = shellType.InvokeMember("CreateShortcut",
                System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { linkPath });
            Type linkType = link.GetType();
            linkType.InvokeMember("TargetPath", System.Reflection.BindingFlags.SetProperty, null, link, new object[] { target });
            linkType.InvokeMember("Arguments", System.Reflection.BindingFlags.SetProperty, null, link, new object[] { arguments ?? "" });
            linkType.InvokeMember("WorkingDirectory", System.Reflection.BindingFlags.SetProperty, null, link, new object[] { workDir });
            linkType.InvokeMember("Description", System.Reflection.BindingFlags.SetProperty, null, link,
                new object[] { "Keyboard layout and Caps Lock badge next to the caret" });
            linkType.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, link, null);
        }
        finally
        {
            if (shell != null) Marshal.ReleaseComObject(shell);
        }
    }

    static void StopOtherInstances()
    {
        int me = System.Diagnostics.Process.GetCurrentProcess().Id;
        foreach (var p in System.Diagnostics.Process.GetProcessesByName(AppName))
        {
            if (p.Id == me) continue;
            try { p.Kill(); p.WaitForExit(5000); } catch { }
        }
    }

    public static void Install(string passThroughArgs, bool autostart)
    {
        StopOtherInstances();
        Directory.CreateDirectory(InstallDir);

        if (!RunningFromInstallDir)
            File.Copy(CurrentExe, InstalledExe, true);

        if (autostart)
            CreateShortcut(ShortcutPath, InstalledExe, passThroughArgs, InstallDir);

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = InstalledExe,
            Arguments = passThroughArgs ?? "",
            WorkingDirectory = InstallDir,
            UseShellExecute = false
        });
    }

    public static void Uninstall()
    {
        StopOtherInstances();
        try { if (File.Exists(ShortcutPath)) File.Delete(ShortcutPath); } catch { }

        // cannot delete the exe while it is the one running - hand that to cmd
        if (RunningFromInstallDir)
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c timeout /t 2 /nobreak >nul & rmdir /s /q \"" + InstallDir + "\"",
                CreateNoWindow = true,
                UseShellExecute = false
            });
        }
        else
        {
            try { if (Directory.Exists(InstallDir)) Directory.Delete(InstallDir, true); } catch { }
        }
    }
}

static class Program
{
    // 0 = idle, 1 = a detection pass is in flight.
    // UI Automation calls are cross-process and are served by the target
    // application's UI thread, so a busy Chromium window can stall them for
    // tens of milliseconds. They therefore run on a pool thread (which is MTA,
    // the mode UIA clients are supposed to use) and never on the dispatcher —
    // otherwise the badge cannot repaint while a call is outstanding, which is
    // exactly the lag you see while typing fast.
    static int busy;

    // -OnlyOnChange bookkeeping
    static string lastLayout;
    static bool lastCaps;
    // Windows keeps the input language per thread, so the layout reported for
    // the foreground window changes when you merely move to another window -
    // switching virtual desktops does it too. Remembering which window the
    // last reading came from is what tells a real switch from that.
    static IntPtr lastForeground;
    static int showUntil;
    static double lastShownX = double.NaN, lastShownY;

    // tray state
    static System.Drawing.Icon trayIcon;
    static string trayLayout;
    static bool trayCaps;
    static Forms.ToolStripMenuItem layoutItem;

    // --- optional: a macOS modifier layout built on PowerToys ----------------
    // Not part of the indicator's own job, but the two belong to the same
    // keyboard setup, and this is the tray icon already sitting there. If
    // PowerToys is absent the menu entry simply stays hidden.

    static string PowerToysSettings()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            @"Microsoft\PowerToys\settings.json");
    }

    static bool PowerToysPresent() { return File.Exists(PowerToysSettings()); }

    static bool IsLayoutOn()
    {
        try
        {
            string s = File.ReadAllText(PowerToysSettings());
            Match m = Regex.Match(s, "\"Keyboard Manager\"\\s*:\\s*(true|false)");
            return m.Success && m.Groups[1].Value == "true";
        }
        catch { return false; }
    }

    // Asked once, at install time. The indicator itself has no dependencies -
    // only the layout switch needs PowerToys - so this informs rather than
    // blocks, but it makes you tick the box so the requirement is not a
    // surprise found later in a tray menu that does nothing.
    static bool ConfirmPowerToysDependency()
    {
        if (PowerToysPresent()) return true;

        Forms.Form f = new Forms.Form();
        f.Text = "Caret Language Indicator";
        f.FormBorderStyle = Forms.FormBorderStyle.FixedDialog;
        f.StartPosition = Forms.FormStartPosition.CenterScreen;
        f.MinimizeBox = false;
        f.MaximizeBox = false;
        f.ShowInTaskbar = false;
        f.ClientSize = new System.Drawing.Size(500, 214);

        Forms.Label msg = new Forms.Label();
        msg.Text =
            "PowerToys was not found on this computer.\r\n\r\n" +
            "The indicator itself does not need it. The macOS keyboard layout does: " +
            "PowerToys Keyboard Manager is what reorders the bottom-row modifiers and " +
            "provides the text navigation, and the tray switch simply turns it on and off.\r\n\r\n" +
            "Without PowerToys the indicator still shows the layout, but the switch will " +
            "not appear in the tray menu.";
        msg.SetBounds(16, 14, 468, 118);
        f.Controls.Add(msg);

        Forms.CheckBox cb = new Forms.CheckBox();
        cb.Text = "I understand, and will install PowerToys myself";
        cb.SetBounds(16, 138, 360, 22);
        f.Controls.Add(cb);

        Forms.Button get = new Forms.Button();
        get.Text = "Get PowerToys";
        get.SetBounds(16, 170, 120, 28);
        get.Click += delegate
        {
            try { Process.Start("https://github.com/microsoft/PowerToys/releases/latest"); }
            catch { }
        };
        f.Controls.Add(get);

        Forms.Button ok = new Forms.Button();
        ok.Text = "Continue";
        ok.SetBounds(292, 170, 90, 28);
        ok.Enabled = false;
        ok.DialogResult = Forms.DialogResult.OK;
        f.Controls.Add(ok);

        Forms.Button cancel = new Forms.Button();
        cancel.Text = "Cancel";
        cancel.SetBounds(392, 170, 90, 28);
        cancel.DialogResult = Forms.DialogResult.Cancel;
        f.Controls.Add(cancel);

        cb.CheckedChanged += delegate { ok.Enabled = cb.Checked; };
        f.AcceptButton = ok;
        f.CancelButton = cancel;

        return f.ShowDialog() == Forms.DialogResult.OK;
    }

    static void ToggleLayout()
    {
        string path = PowerToysSettings();
        try
        {
            string s = File.ReadAllText(path);
            string want = IsLayoutOn() ? "false" : "true";
            string n = Regex.Replace(s, "(\"Keyboard Manager\"\\s*:\\s*)(true|false)", "${1}" + want);
            if (n == s) throw new Exception("could not find the Keyboard Manager flag");
            File.WriteAllText(path, n, new System.Text.UTF8Encoding(false));
            Log.Write("layout toggled to {0}", want);
        }
        catch (Exception ex)
        {
            Forms.MessageBox.Show("Could not change the layout:\r\n" + ex.Message,
                "Caret Language Indicator", Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Warning);
            return;
        }

        // PowerToys reads the flag at startup, so it has to come back round.
        try
        {
            string exe = null;
            foreach (Process p in Process.GetProcessesByName("PowerToys"))
            {
                try { exe = p.MainModule.FileName; break; } catch { }
            }
            foreach (Process p in Process.GetProcesses())
            {
                if (p.ProcessName.StartsWith("PowerToys", StringComparison.OrdinalIgnoreCase))
                    try { p.Kill(); } catch { }
            }
            if (exe != null) { Thread.Sleep(1500); Process.Start(exe); }
        }
        catch { }
    }

    [STAThread]
    static void Main(string[] args)
    {
        Options.Parse(args);

        if (Options.Uninstall)
        {
            Installer.Uninstall();
            Forms.MessageBox.Show("Caret Language Indicator has been removed.", "Caret Language Indicator",
                Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Information);
            return;
        }

        if (Options.Install)
        {
            Installer.Install(Options.PassThrough, true);
            return;
        }

        // Double-clicked from a Downloads folder: offer to install rather than
        // silently running once and disappearing after the next reboot.
        if (!Options.NoPrompt && !Installer.RunningFromInstallDir && !Installer.IsInstalled)
        {
            var answer = Forms.MessageBox.Show(
                "Install Caret Language Indicator for your user account and start it with Windows?\r\n\r\n" +
                "It will be copied to your local app data folder. No administrator rights are needed, " +
                "and you can remove it later by running this file with -Uninstall.",
                "Caret Language Indicator",
                Forms.MessageBoxButtons.YesNoCancel, Forms.MessageBoxIcon.Question);

            if (answer == Forms.DialogResult.Cancel) return;
            if (answer == Forms.DialogResult.Yes)
            {
                if (!ConfirmPowerToysDependency()) return;
                Installer.Install(Options.PassThrough, true);
                return;
            }
            // "No" simply runs it from here, without installing
        }

        bool created;
        using (new Mutex(true, "Local\\CaretLangIndicator", out created))
        {
            if (!created) return;

            Log.Write("start: OnlyOnChange={0} Switcher={1} ShowMs={2} Interval={3} Anchor={4} exe={5}",
                      Options.OnlyOnChange, Options.Switcher, Options.ShowMs, Options.Interval,
                      Options.Anchor, Installer.CurrentExe);

            Badge badge = new Badge();
            Forms.Screen screen = Forms.Screen.PrimaryScreen;
            System.Drawing.Rectangle work = screen.WorkingArea;
            Dispatcher ui = Dispatcher.CurrentDispatcher;

            Forms.NotifyIcon tray = new Forms.NotifyIcon();
            trayIcon = TrayArt.Make("--", false);
            tray.Icon = trayIcon;
            tray.Text = "Caret language indicator";
            Forms.ContextMenuStrip menu = new Forms.ContextMenuStrip();
            if (PowerToysPresent())
            {
                layoutItem = new Forms.ToolStripMenuItem("macOS keyboard layout");
                layoutItem.Click += delegate { ToggleLayout(); };
                menu.Items.Add(layoutItem);
                menu.Items.Add(new Forms.ToolStripSeparator());
            }
            menu.Items.Add("Exit").Click += delegate
            {
                tray.Visible = false;
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            };

            // A tray menu closes the instant it opens unless the process owns
            // the foreground window, and this one deliberately has none: the
            // badge is WS_EX_NOACTIVATE so it never steals focus while you
            // type. So give the menu a hidden window to be activated on its
            // behalf, and show it by hand rather than via ContextMenuStrip.
            Forms.Form menuHost = new Forms.Form();
            menuHost.ShowInTaskbar = false;
            menuHost.FormBorderStyle = Forms.FormBorderStyle.None;
            menuHost.Opacity = 0;
            menuHost.Size = new System.Drawing.Size(1, 1);
            menuHost.StartPosition = Forms.FormStartPosition.Manual;
            menuHost.Location = new System.Drawing.Point(-32000, -32000);
            IntPtr hostHandle = menuHost.Handle;      // forces creation

            tray.MouseUp += delegate(object sender, Forms.MouseEventArgs e)
            {
                if (e.Button != Forms.MouseButtons.Right) return;
                // Read the state as the menu opens rather than caching it:
                // PowerToys' own UI can change it behind our back.
                if (layoutItem != null) layoutItem.Checked = IsLayoutOn();
                Native.SetForegroundWindow(hostHandle);
                menu.Show(Forms.Cursor.Position);
                menu.Focus();
            };
            tray.Visible = true;

            // DispatcherTimer defaults to Background priority, which other work
            // on the queue can starve - a poll meant to run every 15 ms then
            // fires late and the panel appears late with it.
            DispatcherTimer timer = new DispatcherTimer(DispatcherPriority.Normal);
            timer.Interval = TimeSpan.FromMilliseconds(Options.Interval);
            timer.Tick += delegate
            {
                // Cheap pass first — this is all that runs while the badge is
                // hidden, so idle cost is three same-process calls per tick.
                string layout; bool caps; IntPtr foreground;
                Detector.ReadQuick(out layout, out caps, out foreground);

                // Holding the switch modifier brings up the Windows input
                // switcher flyout, which takes the foreground. Its thread has no
                // layout we can map, and treating that as a change would both
                // restart the timer and leave the highlight stale.
                if (layout == "??") return;

                // The tray always reflects the current state, in every mode -
                // this is the persistent half, the panel is the transient one.
                if (layout != trayLayout || caps != trayCaps)
                {
                    trayLayout = layout;
                    trayCaps = caps;
                    System.Drawing.Icon old = trayIcon;
                    trayIcon = TrayArt.Make(layout, caps);
                    tray.Icon = trayIcon;
                    tray.Text = caps ? layout + " - Caps Lock" : layout;
                    if (old != null) old.Dispose();
                }

                // A different layout under a different window is not a switch:
                // it is the same two layouts sitting where they always were,
                // seen from the other side. Caps Lock is global, so a change
                // there still counts even when the window moved.
                bool windowMoved   = foreground != lastForeground;
                bool layoutChanged = lastLayout != null && layout != lastLayout && !windowMoved;
                bool capsChanged   = lastLayout != null && caps != lastCaps;
                bool changed = layoutChanged || capsChanged;

                if (layout != lastLayout || caps != lastCaps || windowMoved)
                {
                    Log.Write("layout {0}->{1} caps={2} windowMoved={3} => changed={4}",
                              lastLayout ?? "-", layout, caps, windowMoved, changed);
                }

                lastForeground = foreground;
                lastLayout = layout;
                lastCaps = caps;

                if (Options.OnlyOnChange)
                {
                    if (changed)
                    {
                        showUntil = Environment.TickCount + Options.ShowMs;
                        Log.Write("SHOW for {0} ms", Options.ShowMs);
                    }
                    if (unchecked(Environment.TickCount - showUntil) > 0)
                    {
                        badge.HideBadge();
                        return;
                    }
                    // Position once, when it appears; for the rest of the window
                    // leave it where it is rather than chasing the caret.
                    if (!changed) return;
                }

                // Skip the tick outright if the previous pass has not returned,
                // instead of queueing work behind a window that is already slow.
                if (Interlocked.CompareExchange(ref busy, 1, 0) != 0) return;

                ThreadPool.QueueUserWorkItem(delegate
                {
                    InputState s;
                    try { s = Detector.Read(); }
                    catch { s = new InputState(); }
                    finally { Interlocked.Exchange(ref busy, 0); }

                    // Send, not Render: Render sits below Normal on the queue,
                    // so the result waited behind whatever else was pending.
                    ui.BeginInvoke(DispatcherPriority.Send, (Action)delegate
                    {
                        Render(badge, s, work);
                    });
                });
            };
            timer.Start();

            Dispatcher.Run();

            tray.Visible = false;
            tray.Dispose();
        }
    }

    static void Render(Badge badge, InputState s, System.Drawing.Rectangle work)
    {
        {
            {
                badge.SetContent(s.Layout, s.Caps);

                double w = badge.Win.ActualWidth;
                double h = badge.Win.ActualHeight;
                if (w <= 0) w = 60;
                if (h <= 0) h = 40;
                double m = badge.Margin;
                double pillH = h - 2 * m;

                bool show = !(Options.OnlyWhenCaps && !s.Caps);
                bool useCaret = s.HasCaret && (Options.Anchor == "Caret" || !s.HasField);
                bool useField = s.HasField && Options.Anchor != "Corner" && !useCaret;

                double x = 0, y = 0;

                if (show && Options.Anchor == "Corner")
                {
                    x = work.Right - w + m - 12;
                    y = work.Bottom - h + m - 12;
                }
                else if (show && useField)
                {
                    if (Options.Side == "Right") x = s.FieldR - Options.FieldGap - w + m;
                    else
                    {
                        x = s.FieldL - Options.FieldGap - w + m;
                        if (x < work.Left) x = s.FieldR - Options.FieldGap - w + m;
                    }
                    y = (s.FieldH <= 120)
                        ? s.FieldT + (s.FieldH - pillH) / 2 - m
                        : s.FieldT - m;
                }
                else if (show && useCaret)
                {
                    x = s.X + Options.OffsetX - m;
                    y = (Options.VAlign == "Center" && s.CaretH > 0)
                        ? s.CaretT + (s.CaretH - pillH) / 2 - m
                        : s.Y + Options.OffsetY - m;
                }
                else if (show && Options.Fallback == "Mouse")
                {
                    System.Drawing.Point p = Forms.Cursor.Position;
                    x = p.X + 14 - m;
                    y = p.Y + 14 - m;
                }
                else if (show && Options.Fallback == "Corner")
                {
                    x = work.Right - w + m - 12;
                    y = work.Bottom - h + m - 12;
                }
                else show = false;

                if (show)
                {
                    if (x + w > work.Right) x = work.Right - w;
                    if (y + h > work.Bottom) y = work.Bottom - h;
                    if (x < work.Left) x = work.Left;
                    if (y < work.Top) y = work.Top;
                    lastShownX = x; lastShownY = y;
                    badge.ShowAt(x, y);
                }
                else if (Options.OnlyOnChange && !double.IsNaN(lastShownX)
                         && unchecked(Environment.TickCount - showUntil) <= 0)
                {
                    // No anchor this pass - typically the input switcher flyout
                    // holding the foreground while the modifier is down. Stay
                    // where we were instead of blinking out mid-switch.
                    badge.ShowAt(lastShownX, lastShownY);
                }
                else badge.HideBadge();
            }
        }
    }
}
