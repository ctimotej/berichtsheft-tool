using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Berichtsheft.Desktop;

/// <summary>Light/dark colors for one mode. Light uses the Windows system colors, so it looks exactly as before.</summary>
internal sealed record Palette(
    bool Dark, Color Back, Color Surface, Color Text, Color Hint, Color Error, Color Border,
    Color Header, Color Selection, Color SelectionText, Color Button, Color ButtonHover, Color ButtonPressed,
    Color DropBack, Color DropHover, Color DropBorder, Color DropBorderHover)
{
    public static readonly Palette Light = new(false,
        SystemColors.Control, SystemColors.Window, SystemColors.ControlText, SystemColors.GrayText, Color.Firebrick, SystemColors.ControlDark,
        SystemColors.Control, SystemColors.Highlight, SystemColors.HighlightText, SystemColors.Control, SystemColors.Control, SystemColors.Control,
        SystemColors.ControlLightLight, Color.AliceBlue, SystemColors.ControlDark, SystemColors.Highlight);

    public static readonly Palette DarkMode = new(true,
        Color.FromArgb(32, 32, 32), Color.FromArgb(43, 43, 43), Color.FromArgb(240, 240, 240), Color.FromArgb(160, 160, 160),
        Color.FromArgb(255, 123, 114), Color.FromArgb(70, 70, 70),
        Color.FromArgb(45, 45, 45), Color.FromArgb(0, 95, 184), Color.White, Color.FromArgb(55, 55, 55),
        Color.FromArgb(68, 68, 68), Color.FromArgb(80, 80, 80),
        Color.FromArgb(38, 38, 38), Color.FromArgb(28, 52, 82), Color.FromArgb(110, 110, 110), Color.FromArgb(76, 160, 255));
}

/// <summary>Follows the Windows setting "Standard-App-Modus" (Einstellungen > Personalisierung > Farben): dark windows
/// when apps should be dark, also when it is switched while the program runs. High contrast always uses the system colors.
/// Every window calls <see cref="Attach"/> (via Ui.Dialog or MainForm); controls added later are themed as they arrive.
/// Colors that mean something (hints, errors) are read from <see cref="Current"/> instead of SystemColors/Color.Firebrick.</summary>
internal static class Theme
{
    public static Palette Current { get; private set; } = Palette.Light;
    public static bool IsDark => Current.Dark;
    public static Color Text => Current.Text;
    public static Color Hint => Current.Hint;
    public static Color Error => Current.Error;

    /// <summary>Raised on the UI thread after the mode changed; for custom painted controls.</summary>
    public static event Action? Changed;

    private static bool _started;
    private static readonly ConditionalWeakTable<Control, object> Hooked = new();
    private static readonly ConditionalWeakTable<ToolStrip, object> StripModes = new();
    private static readonly ToolStripRenderer DarkRenderer = new DarkStripRenderer();

    /// <summary>Reads the Windows setting once and starts listening for changes. Call before the first window is created.</summary>
    public static void Initialize()
    {
        if (_started) return;
        _started = true;
        Current = SystemWantsDark() ? Palette.DarkMode : Palette.Light;
        ApplyStripRenderer();
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.Accessibility or UserPreferenceCategory.VisualStyle)
                Refresh();
        };
    }

    /// <summary>True when Windows apps should be dark ("AppsUseLightTheme" = 0) and high contrast is off.</summary>
    public static bool SystemWantsDark()
    {
        if (SystemInformation.HighContrast) return false;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch (Exception) { return false; } // no access / older Windows: light, as before
    }

    /// <summary>Re-reads the Windows setting and re-themes all open windows when it changed.</summary>
    private static void Refresh()
    {
        var wanted = SystemWantsDark() ? Palette.DarkMode : Palette.Light;
        if (wanted == Current) return;
        var forms = Application.OpenForms.Cast<Form>().ToList();
        if (forms.Count > 0 && forms[0].InvokeRequired) { forms[0].BeginInvoke(Refresh); return; }
        Current = wanted;
        ApplyStripRenderer();
        foreach (var f in forms)
        {
            ApplyTree(f);
            ApplyTitleBar(f);
            f.Invalidate(true);
        }
        Changed?.Invoke();
    }

    /// <summary>Themes the window now and everything that is added to it later.</summary>
    public static void Attach(Form f)
    {
        Initialize();
        f.HandleCreated += (_, _) => ApplyTitleBar(f);
        f.Load += (_, _) => ApplyTree(f);
        ApplyTree(f);
    }

    /// <summary>Themes a control and all its children (e.g. a context menu that is not part of a window).</summary>
    public static void ApplyTree(Control root)
    {
        Apply(root);
        foreach (Control c in root.Controls) ApplyTree(c);
        if (!Hooked.TryGetValue(root, out _))
        {
            Hooked.Add(root, new object());
            root.ControlAdded += (_, e) => { if (e.Control != null) ApplyTree(e.Control); };
        }
    }

    private static void Apply(Control c)
    {
        var p = Current;
        var dark = p.Dark;
        switch (c)
        {
            case Form f:
                f.BackColor = dark ? p.Back : Color.Empty;
                f.ForeColor = dark ? p.Text : Color.Empty;
                break;
            case DataGridView g:
                StyleGrid(g, p);
                NativeDark(g, dark);
                break;
            case Button b:
                b.FlatStyle = dark ? FlatStyle.Flat : FlatStyle.Standard;
                b.BackColor = dark ? p.Button : Color.Empty;
                b.ForeColor = dark ? p.Text : Color.Empty;
                b.FlatAppearance.BorderColor = dark ? p.Border : Color.Empty;
                b.FlatAppearance.MouseOverBackColor = dark ? p.ButtonHover : Color.Empty;
                b.FlatAppearance.MouseDownBackColor = dark ? p.ButtonPressed : Color.Empty;
                if (!dark) b.UseVisualStyleBackColor = true;
                break;
            case ComboBox cb:
                cb.FlatStyle = dark ? FlatStyle.Flat : FlatStyle.Standard; // a themed DropDownList ignores BackColor
                SetInput(cb, p);
                NativeDark(cb, dark, "DarkMode_CFD");
                break;
            case TextBoxBase or ListBox or NumericUpDown or TreeView or ListView:
                SetInput(c, p);
                NativeDark(c, dark);
                break;
            case ScrollBar:
                NativeDark(c, dark);
                break;
            case ToolStrip strip:
                // StatusStrip uses the system renderer by default: remember the original mode for the way back
                if (dark && !StripModes.TryGetValue(strip, out _)) StripModes.Add(strip, strip.RenderMode);
                if (dark) strip.RenderMode = ToolStripRenderMode.ManagerRenderMode;
                else if (StripModes.TryGetValue(strip, out var mode)) strip.RenderMode = (ToolStripRenderMode)mode;
                strip.ForeColor = dark ? p.Text : Color.Empty;
                break;
            case DateTimePicker:
                break; // drawn by Windows without color support: stays light
            case Label or CheckBox or RadioButton or GroupBox:
                RemapRole(c, p);
                break;
            case ScrollableControl s when s.AutoScroll:
                NativeDark(s, dark);
                break;
        }
    }

    private static void SetInput(Control c, Palette p)
    {
        c.BackColor = p.Dark ? p.Surface : Color.Empty;
        c.ForeColor = p.Dark ? p.Text : Color.Empty;
    }

    /// <summary>Hint and error texts keep their meaning in the other mode.</summary>
    private static void RemapRole(Control c, Palette p)
    {
        if (Role(c.ForeColor) is { } role) c.ForeColor = role(p);
    }

    private static Func<Palette, Color>? Role(Color color)
    {
        if (color.IsEmpty) return null;
        var argb = color.ToArgb();
        if (argb == Palette.Light.Hint.ToArgb() || argb == Palette.DarkMode.Hint.ToArgb()) return x => x.Hint;
        if (argb == Palette.Light.Error.ToArgb() || argb == Palette.DarkMode.Error.ToArgb()) return x => x.Error;
        if (argb == Palette.Light.Text.ToArgb() || argb == Palette.DarkMode.Text.ToArgb()) return x => x.Text;
        return null;
    }

    private static void StyleGrid(DataGridView g, Palette p)
    {
        var dark = p.Dark;
        g.EnableHeadersVisualStyles = !dark;  // header colors are only used without visual styles
        g.BackgroundColor = p.Surface;
        g.GridColor = dark ? p.Border : SystemColors.ControlDark;
        g.BorderStyle = dark ? BorderStyle.FixedSingle : BorderStyle.Fixed3D;
        g.ColumnHeadersBorderStyle = dark ? DataGridViewHeaderBorderStyle.Single : DataGridViewHeaderBorderStyle.Raised;
        var cell = g.DefaultCellStyle;
        cell.BackColor = p.Surface; cell.ForeColor = p.Text;
        cell.SelectionBackColor = p.Selection; cell.SelectionForeColor = p.SelectionText;
        var head = g.ColumnHeadersDefaultCellStyle;
        head.BackColor = p.Header; head.ForeColor = p.Text;
        head.SelectionBackColor = p.Header; head.SelectionForeColor = p.Text;
        foreach (DataGridViewRow r in g.Rows)
        {
            if (r.HasDefaultCellStyle && Role(r.DefaultCellStyle.ForeColor) is { } role) r.DefaultCellStyle.ForeColor = role(p);
        }
    }

    private static void ApplyStripRenderer()
    {
        if (Current.Dark) ToolStripManager.Renderer = DarkRenderer;
        else ToolStripManager.RenderMode = ToolStripManagerRenderMode.Professional;
    }

    // ------------------------------------------------------------ native parts: title bar, scroll bars, drop-down lists
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] private static extern int SetWindowTheme(IntPtr hwnd, string? subApp, string? idList);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    private const int DwmUseImmersiveDarkMode = 20, DwmUseImmersiveDarkModeOld = 19; // Windows 10 20H1+ / 1809–1909
    private const uint SwpFrameChanged = 0x20, SwpNoMove = 0x2, SwpNoSize = 0x1, SwpNoZOrder = 0x4, SwpNoActivate = 0x10;

    private static void ApplyTitleBar(Form f)
    {
        if (!f.IsHandleCreated) return;
        try
        {
            var on = Current.Dark ? 1 : 0;
            if (DwmSetWindowAttribute(f.Handle, DwmUseImmersiveDarkMode, ref on, sizeof(int)) != 0)
                DwmSetWindowAttribute(f.Handle, DwmUseImmersiveDarkModeOld, ref on, sizeof(int));
            if (f.Visible) SetWindowPos(f.Handle, IntPtr.Zero, 0, 0, 0, 0, SwpFrameChanged | SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate);
        }
        catch (Exception) { } // not available (older Windows): normal title bar
    }

    /// <summary>Dark scroll bars etc. for a native control; null theme = back to the default.</summary>
    private static void NativeDark(Control c, bool dark, string darkTheme = "DarkMode_Explorer")
    {
        if (!c.IsHandleCreated)
        {
            if (!dark) return;
            void OnCreated(object? s, EventArgs e) { c.HandleCreated -= OnCreated; NativeDark(c, Current.Dark, darkTheme); }
            c.HandleCreated += OnCreated;
            return;
        }
        try { SetWindowTheme(c.Handle, dark ? darkTheme : null, null); }
        catch (Exception) { }
    }

    // ------------------------------------------------------------ menus, status bar, context menus
    private sealed class DarkStripRenderer : ToolStripProfessionalRenderer
    {
        public DarkStripRenderer() : base(new DarkColors()) { RoundedEdges = false; }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Palette.DarkMode.Text : Palette.DarkMode.Hint;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = Palette.DarkMode.Text;
            base.OnRenderArrow(e);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            // the default check mark is black: draw a light one
            var r = e.ImageRectangle;
            using (var b = new SolidBrush(Palette.DarkMode.Selection)) e.Graphics.FillRectangle(b, r);
            using var pen = new Pen(Palette.DarkMode.Text, 1.6f);
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            e.Graphics.DrawLines(pen, new[]
            {
                new PointF(r.Left + r.Width * 0.22f, r.Top + r.Height * 0.52f),
                new PointF(r.Left + r.Width * 0.42f, r.Top + r.Height * 0.72f),
                new PointF(r.Left + r.Width * 0.78f, r.Top + r.Height * 0.30f),
            });
        }
    }

    private sealed class DarkColors : ProfessionalColorTable
    {
        private static readonly Palette P = Palette.DarkMode;
        private static readonly Color Hover = Color.FromArgb(62, 62, 62), Menu = Color.FromArgb(43, 43, 43);
        public DarkColors() { UseSystemColors = false; }
        public override Color MenuStripGradientBegin => P.Back;
        public override Color MenuStripGradientEnd => P.Back;
        public override Color StatusStripGradientBegin => P.Back;
        public override Color StatusStripGradientEnd => P.Back;
        public override Color ToolStripGradientBegin => P.Back;
        public override Color ToolStripGradientMiddle => P.Back;
        public override Color ToolStripGradientEnd => P.Back;
        public override Color ToolStripBorder => P.Back;
        public override Color ToolStripDropDownBackground => Menu;
        public override Color ImageMarginGradientBegin => Menu;
        public override Color ImageMarginGradientMiddle => Menu;
        public override Color ImageMarginGradientEnd => Menu;
        public override Color MenuBorder => P.Border;
        public override Color MenuItemBorder => Hover;
        public override Color MenuItemSelected => Hover;
        public override Color MenuItemSelectedGradientBegin => Hover;
        public override Color MenuItemSelectedGradientEnd => Hover;
        public override Color MenuItemPressedGradientBegin => Menu;
        public override Color MenuItemPressedGradientMiddle => Menu;
        public override Color MenuItemPressedGradientEnd => Menu;
        public override Color SeparatorDark => P.Border;
        public override Color SeparatorLight => P.Border;
        public override Color CheckBackground => P.Selection;
        public override Color CheckSelectedBackground => P.Selection;
        public override Color CheckPressedBackground => P.Selection;
        public override Color ButtonSelectedBorder => Hover;
        public override Color ButtonSelectedHighlight => Hover;
        public override Color ButtonSelectedGradientBegin => Hover;
        public override Color ButtonSelectedGradientEnd => Hover;
        public override Color GripDark => P.Border;
        public override Color GripLight => P.Back;
    }
}
