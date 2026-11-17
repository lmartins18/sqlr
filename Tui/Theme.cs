using Sqlr.Config;
using TAttr = Terminal.Gui.Attribute;
using Terminal.Gui;

namespace Sqlr.Tui;

/// <summary>
/// Colour schemes for every view, built from the active <see cref="Palette"/>. F9 picks a palette
/// (previewed live, saved to ~/.sqlr/theme). All panels use rounded borders.
/// </summary>
public static class Theme
{
    public enum Msg { Info, Rows, Error, Head }

    private static Schemes _s = new(Palettes.Classic);

    public static Palette Current { get; private set; } = Palettes.Classic;
    public static Palette Default => Palettes.Classic;
    public static IReadOnlyList<Palette> All => Palettes.All;

    public static Palette? Find(string? name) =>
        All.FirstOrDefault(p => p.Name.Equals(name?.Trim(), StringComparison.OrdinalIgnoreCase));

    // ── Colour schemes ─────────────────────────────────────────────────────

    public static ColorScheme Base       => _s.Base;
    public static ColorScheme TitleBar   => _s.TitleBar;
    public static ColorScheme StatusBar  => _s.StatusBar;
    public static ColorScheme Input      => _s.Input;
    public static ColorScheme Popup      => _s.Popup;
    public static ColorScheme Picker     => _s.Picker;

    // Zebra rows: index 0, 2, 4… on the background, the others on the stripe colour
    public static ColorScheme OddRow     => _s.OddRow;
    public static ColorScheme EvenRow    => _s.EvenRow;

    /// NULL cells — dimmed so they stand apart from real values
    public static ColorScheme NullCell(bool evenRow) => evenRow ? _s.NullOnBg : _s.NullOnStripe;

    public static ColorScheme StatusOk    => _s.StatusOk;
    public static ColorScheme StatusError => _s.StatusError;
    public static ColorScheme StatusBusy  => _s.StatusBusy;

    public static TAttr Message(Msg kind) => new(kind switch
    {
        Msg.Rows  => Current.Dim,
        Msg.Error => Current.Err,
        Msg.Head  => Current.Accent,
        _         => Current.Fg
    }, Current.Bg);

    public static void SetRounded(View v) =>
        v.BorderStyle = LineStyle.Rounded;

    // ── Switching ──────────────────────────────────────────────────────────

    /// <summary>Switches palette and recolours the given open views and everything inside them.</summary>
    public static void Apply(Palette palette, params View?[] roots)
    {
        var old = _s;
        Select(palette);

        // Views hold the scheme instance they were given; swap each old instance for its new twin.
        var map = new Dictionary<ColorScheme, ColorScheme>(ReferenceEqualityComparer.Instance);
        foreach (var (from, to) in old.All.Zip(_s.All)) map[from] = to;

        foreach (var root in roots.Distinct())
            if (root is not null) Recolor(root, map);
    }

    private static void Recolor(View v, Dictionary<ColorScheme, ColorScheme> map)
    {
        if (v.ColorScheme is { } cs && map.TryGetValue(cs, out var next))
            v.ColorScheme = next;
        v.SetNeedsDraw();
        foreach (var sub in v.Subviews) Recolor(sub, map);
    }

    private static void Select(Palette palette)
    {
        Current = palette;
        _s      = new Schemes(palette);
    }

    /// <summary>Loads the theme saved by the F9 picker (call before the first screen is built).</summary>
    public static void LoadSaved()
    {
        try
        {
            if (File.Exists(SqlrPaths.ThemeFile) && Find(File.ReadAllText(SqlrPaths.ThemeFile)) is { } p)
                Select(p);
        }
        catch (Exception ex)
        {
            SqlrPaths.LogError(ex);
        }
    }

    public static void Save(Palette palette) =>
        SqlrPaths.WriteAllTextAtomic(SqlrPaths.ThemeFile, palette.Name + "\n");

    // ── Scheme set for one palette ─────────────────────────────────────────

    private sealed class Schemes
    {
        public readonly ColorScheme Base, TitleBar, StatusBar, Input, Popup, Picker, OddRow, EvenRow,
                                    NullOnBg, NullOnStripe, StatusOk, StatusError, StatusBusy;

        public Schemes(Palette p)
        {
            Base      = S(A(p.Fg, p.Bg), A(p.Fg, p.Sel), A(p.Accent, p.Bg), A(p.Accent, p.Sel), A(p.Dim, p.Bg));
            TitleBar  = S(A(p.TitleFg, p.TitleBg));
            StatusBar = S(A(p.Accent, p.Bg), A(p.Accent, p.Bg), A(p.Gold, p.Bg), A(p.Gold, p.Bg), A(p.Dim, p.Bg));
            Input     = S(A(p.Fg, p.Bg), A(p.Fg, p.Bg), A(p.Border, p.Bg), A(p.Accent, p.Bg), A(p.Dim, p.Bg));
            Popup     = S(A(p.Fg, p.Float), A(p.HiFg, p.Accent), A(p.Accent, p.Float), A(p.HiFg, p.Accent), A(p.Dim, p.Float));
            Picker    = Row(p, p.Bg);
            OddRow    = Row(p, p.Bg);
            EvenRow   = Row(p, p.Stripe);
            NullOnBg     = Null(p, p.Bg);
            NullOnStripe = Null(p, p.Stripe);
            StatusOk    = Solid(p, p.Ok);
            StatusError = Solid(p, p.Err);
            StatusBusy  = Solid(p, p.Gold);
        }

        public ColorScheme[] All =>
            [Base, TitleBar, StatusBar, Input, Popup, Picker, OddRow, EvenRow, NullOnBg, NullOnStripe, StatusOk, StatusError, StatusBusy];

        private static TAttr A(Color fg, Color bg) => new(fg, bg);

        private static ColorScheme S(TAttr all) => S(all, all, all, all, all);

        private static ColorScheme S(TAttr normal, TAttr focus, TAttr hotNormal, TAttr hotFocus, TAttr disabled) => new()
        {
            Normal = normal, Focus = focus, HotNormal = hotNormal, HotFocus = hotFocus, Disabled = disabled
        };

        private static ColorScheme Row(Palette p, Color bg) =>
            S(A(p.Fg, bg), A(p.HiFg, p.Hi), A(p.Gold, bg), A(p.Gold, p.Hi), A(p.Dim, bg));

        private static ColorScheme Null(Palette p, Color bg) =>
            S(A(p.Dim, bg), A(p.HiFg, p.Hi), A(p.Dim, bg), A(p.HiFg, p.Hi), A(p.Dim, p.Bg));

        private static ColorScheme Solid(Palette p, Color fg) =>
            S(A(fg, p.Bg), A(fg, p.Bg), A(p.Gold, p.Bg), A(p.Gold, p.Bg), A(p.Dim, p.Bg));
    }
}
