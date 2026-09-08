// Caret language indicator - shows the keyboard layout and Caps Lock state
// next to the text caret. Same behaviour as caret-lang-indicator.ps1, compiled
// so it does not carry a PowerShell host around.
//
// Build with the csc.exe that ships with Windows - see build.ps1.

using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
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
    [DllImport("user32.dll")] public static extern int GetWindowThreadProcessId(IntPtr hWnd, out int pid);
    [DllImport("user32.dll")] public static extern IntPtr GetKeyboardLayout(int idThread);
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

    public static void Parse(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i].TrimStart('-', '/').ToLowerInvariant();
            string next = (i + 1 < args.Length) ? args[i + 1] : null;
            switch (a)
            {
                case "anchor":       Anchor = next; i++; break;
                case "valign":       VAlign = next; i++; break;
                case "side":         Side = next; i++; break;
                case "fallback":     Fallback = next; i++; break;
                case "offsetx":      OffsetX = int.Parse(next); i++; break;
                case "offsety":      OffsetY = int.Parse(next); i++; break;
                case "fieldgap":     FieldGap = int.Parse(next); i++; break;
                case "interval":     Interval = int.Parse(next); i++; break;
                case "onlywhencaps": OnlyWhenCaps = true; break;
                case "glass":        Glass = true; break;
            }
        }
    }
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
        pill.Child = row;
        if (Options.Glass)
        {
            pill.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#33000000"));
            pill.Padding = new Thickness(11, 5, 13, 6);
        }
        else
        {
            pill.Background = pillDark;
            pill.CornerRadius = new CornerRadius(11);
            pill.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#26FFFFFF"));
            pill.BorderThickness = new Thickness(1);
            pill.Padding = new Thickness(9, 3, 11, 4);
            pill.Margin = new Thickness(8);
            DropShadowEffect fx = new DropShadowEffect();
            fx.BlurRadius = 12; fx.ShadowDepth = 1.5; fx.Direction = 270; fx.Opacity = 0.5;
            fx.Color = Colors.Black;
            pill.Effect = fx;
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
        Win.Hide();
    }

    public void SetContent(string text, bool caps)
    {
        if (text == lastText && caps == lastCaps) return;
        lastText = text;
        lastCaps = caps;

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
        if (!shown) { Win.Show(); shown = true; }
    }

    public void HideBadge()
    {
        if (shown) { Win.Hide(); shown = false; }
    }
}

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        Options.Parse(args);

        bool created;
        using (new Mutex(true, "Local\\CaretLangIndicator", out created))
        {
            if (!created) return;

            Badge badge = new Badge();
            Forms.Screen screen = Forms.Screen.PrimaryScreen;
            System.Drawing.Rectangle work = screen.WorkingArea;

            Forms.NotifyIcon tray = new Forms.NotifyIcon();
            tray.Icon = System.Drawing.SystemIcons.Information;
            tray.Text = "Caret language indicator";
            Forms.ContextMenuStrip menu = new Forms.ContextMenuStrip();
            menu.Items.Add("Exit").Click += delegate
            {
                tray.Visible = false;
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            };
            tray.ContextMenuStrip = menu;
            tray.Visible = true;

            DispatcherTimer timer = new DispatcherTimer();
            timer.Interval = TimeSpan.FromMilliseconds(Options.Interval);
            timer.Tick += delegate
            {
                InputState s = Detector.Read();
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
                    badge.ShowAt(x, y);
                }
                else badge.HideBadge();
            };
            timer.Start();

            Dispatcher.Run();

            tray.Visible = false;
            tray.Dispose();
        }
    }
}
