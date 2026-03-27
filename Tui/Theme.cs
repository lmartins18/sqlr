using TAttr = Terminal.Gui.Attribute;
using Terminal.Gui;

namespace Sqlr.Tui;

/// <summary>
/// btop-inspired dark theme.
/// Deep navy/slate background, cyan/teal accents, amber highlights.
/// All panels use rounded borders.
/// </summary>
public static class Theme
{
    // ── Raw colours ────────────────────────────────────────────────────────
    // Terminal.Gui's 16-colour palette mapped to the closest btop equivalents:
    //   Black      ≈ #0f1117  (deep background)
    //   DarkGray   ≈ #1e2433  (alternate row / panel bg)
    //   Blue       ≈ #1a3a5c  (selection bg)
    //   Cyan       ≈ #00aabb  (borders / labels)
    //   BrightCyan ≈ #00e5ff  (focused borders / active title)
    //   Yellow     ≈ #ffb000  (header / hotkey gold)
    //   Gray       ≈ #4a5568  (dim text)
    //   White      ≈ #e2e8f0  (normal text)
    //   BrightGreen≈ #00e676  (success / OK)
    //   BrightRed  ≈ #ff5252  (error)
    //   BrightMagenta ≈ #ff80ab (highlight accent)

    // ── Colour schemes ─────────────────────────────────────────────────────

    /// Default scheme for most panels
    public static ColorScheme Base => new()
    {
        Normal    = new TAttr(Color.White,        Color.Black),
        Focus     = new TAttr(Color.White,        Color.Blue),
        HotNormal = new TAttr(Color.BrightCyan,   Color.Black),
        HotFocus  = new TAttr(Color.BrightCyan,   Color.Blue),
        Disabled  = new TAttr(Color.Gray,         Color.Black)
    };

    /// Title bar — full-width header strip
    public static ColorScheme TitleBar => new()
    {
        Normal    = new TAttr(Color.Black,        Color.Cyan),
        Focus     = new TAttr(Color.Black,        Color.Cyan),
        HotNormal = new TAttr(Color.Black,        Color.Cyan),
        HotFocus  = new TAttr(Color.Black,        Color.Cyan),
        Disabled  = new TAttr(Color.Black,        Color.Cyan)
    };

    /// Status bar at the very bottom
    public static ColorScheme StatusBar => new()
    {
        Normal    = new TAttr(Color.BrightCyan,   Color.Black),
        Focus     = new TAttr(Color.BrightCyan,   Color.Black),
        HotNormal = new TAttr(Color.Yellow,       Color.Black),
        HotFocus  = new TAttr(Color.Yellow,       Color.Black),
        Disabled  = new TAttr(Color.Gray,         Color.Black)
    };

    /// SQL input panel — cyan border when focused
    public static ColorScheme Input => new()
    {
        Normal    = new TAttr(Color.White,        Color.Black),
        Focus     = new TAttr(Color.White,        Color.Black),
        HotNormal = new TAttr(Color.Cyan,         Color.Black),
        HotFocus  = new TAttr(Color.BrightCyan,   Color.Black),
        Disabled  = new TAttr(Color.Gray,         Color.Black)
    };

    /// Completion popup
    public static ColorScheme Popup => new()
    {
        Normal    = new TAttr(Color.White,        Color.Black),
        Focus     = new TAttr(Color.Black,        Color.BrightCyan),
        HotNormal = new TAttr(Color.BrightCyan,   Color.Black),
        HotFocus  = new TAttr(Color.Black,        Color.BrightCyan),
        Disabled  = new TAttr(Color.Gray,         Color.Black)
    };

    /// Connection picker list
    public static ColorScheme Picker => new()
    {
        Normal    = new TAttr(Color.White,        Color.Black),
        Focus     = new TAttr(Color.Black,        Color.Cyan),
        HotNormal = new TAttr(Color.Yellow,       Color.Black),
        HotFocus  = new TAttr(Color.Yellow,       Color.Cyan),
        Disabled  = new TAttr(Color.Gray,         Color.Black)
    };

    // ── TableView row colours ──────────────────────────────────────────────

    public static ColorScheme OddRow => new()
    {
        Normal    = new TAttr(Color.White,        Color.Black),
        Focus     = new TAttr(Color.Black,        Color.Cyan),
        HotNormal = new TAttr(Color.Yellow,       Color.Black),
        HotFocus  = new TAttr(Color.Yellow,       Color.Cyan),
        Disabled  = new TAttr(Color.Gray,         Color.Black)
    };

    public static ColorScheme EvenRow => new()
    {
        Normal    = new TAttr(Color.White,        Color.Blue),
        Focus     = new TAttr(Color.Black,        Color.Cyan),
        HotNormal = new TAttr(Color.Yellow,       Color.Blue),
        HotFocus  = new TAttr(Color.Yellow,       Color.Cyan),
        Disabled  = new TAttr(Color.Gray,         Color.Blue)
    };

    // ── Helper: apply rounded border to any View ───────────────────────────
    public static void SetRounded(View v) =>
        v.BorderStyle = LineStyle.Rounded;
}
