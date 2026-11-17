using Terminal.Gui;

namespace Sqlr.Tui;

/// <summary>The colours a theme is built from. <see cref="Theme"/> derives every view's scheme from these.</summary>
public sealed record Palette(string Name, string Description)
{
    public required Color Bg      { get; init; }
    public required Color Stripe  { get; init; } // zebra rows in the results grid
    public required Color Float   { get; init; } // completion popup
    public required Color Sel     { get; init; } // focused button / view
    public required Color Hi      { get; init; } // selected list row / grid cell
    public required Color HiFg    { get; init; }
    public required Color Fg      { get; init; }
    public required Color Dim     { get; init; } // NULLs, rows-affected lines, disabled
    public required Color Border  { get; init; }
    public required Color Accent  { get; init; } // focused borders, status text
    public required Color Gold    { get; init; } // hotkeys, busy status
    public required Color TitleFg { get; init; }
    public required Color TitleBg { get; init; }
    public required Color Ok      { get; init; }
    public required Color Err     { get; init; }

    // SQL syntax
    public required Color Keyword  { get; init; }
    public required Color Ddl      { get; init; }
    public required Color Function { get; init; }
    public required Color String   { get; init; }
    public required Color Comment  { get; init; }
    public required Color Number   { get; init; }
    public required Color Operator { get; init; }
    public required Color Variable { get; init; }
}

internal static class Palettes
{
    private static Color H(string hex) => new(hex);

    /// The original look: the terminal's 16 ANSI colours.
    public static readonly Palette Classic = new("classic", "the original — black, cyan & gold")
    {
        Bg = Color.Black, Stripe = Color.Blue, Float = Color.Black, Sel = Color.Blue,
        Hi = Color.Cyan, HiFg = Color.Black, Fg = Color.White, Dim = Color.Gray,
        Border = Color.Cyan, Accent = Color.BrightCyan, Gold = Color.Yellow,
        TitleFg = Color.Black, TitleBg = Color.Cyan, Ok = Color.BrightGreen, Err = Color.BrightRed,
        Keyword = Color.Yellow, Ddl = Color.BrightMagenta, Function = Color.BrightCyan, String = Color.BrightGreen,
        Comment = Color.Gray, Number = Color.Magenta, Operator = Color.Cyan, Variable = Color.BrightBlue
    };

    public static readonly IReadOnlyList<Palette> All =
    [
        Classic,

        new("horizon", "warm cranberry, lavender & turquoise")
        {
            Bg = H("#1C1E26"), Stripe = H("#232530"), Float = H("#232530"), Sel = H("#343647"),
            Hi = H("#E95678"), HiFg = H("#1C1E26"), Fg = H("#BBBBBB"), Dim = H("#6C6F93"),
            Border = H("#6C6F93"), Accent = H("#E95678"), Gold = H("#FAB795"),
            TitleFg = H("#1C1E26"), TitleBg = H("#E95678"), Ok = H("#29D398"), Err = H("#F43E5C"),
            Keyword = H("#B877DB"), Ddl = H("#E95678"), Function = H("#25B0BC"), String = H("#E4A88A"),
            Comment = H("#6C6D71"), Number = H("#F09483"), Operator = H("#BBBBBB"), Variable = H("#D55070")
        },

        new("tokyodark", "deep ink with coral, green & gold")
        {
            Bg = H("#11121D"), Stripe = H("#171826"), Float = H("#1A1B2A"), Sel = H("#212234"),
            Hi = H("#7199EE"), HiFg = H("#11121D"), Fg = H("#A0A8CD"), Dim = H("#5A6080"),
            Border = H("#4A5057"), Accent = H("#7199EE"), Gold = H("#D7A65F"),
            TitleFg = H("#11121D"), TitleBg = H("#EE6D85"), Ok = H("#95C561"), Err = H("#FE6D85"),
            Keyword = H("#EE6D85"), Ddl = H("#7199EE"), Function = H("#95C561"), String = H("#D7A65F"),
            Comment = H("#4A5057"), Number = H("#A485DD"), Operator = H("#EE6D85"), Variable = H("#F6955B")
        },

        new("pastel-inu", "plum & candy pink with teal")
        {
            Bg = H("#24273A"), Stripe = H("#2B2B40"), Float = H("#3F2131"), Sel = H("#643257"),
            Hi = H("#FC50B4"), HiFg = H("#24273A"), Fg = H("#EB87B6"), Dim = H("#8D5A7C"),
            Border = H("#8A3868"), Accent = H("#FC50B4"), Gold = H("#FDFD96"),
            TitleFg = H("#F2D5CF"), TitleBg = H("#8A3868"), Ok = H("#4FC8B7"), Err = H("#FF004C"),
            Keyword = H("#F9E2AF"), Ddl = H("#D179EE"), Function = H("#7AC3FF"), String = H("#4FC8B7"),
            Comment = H("#699DAD"), Number = H("#4FC8B7"), Operator = H("#7AC3FF"), Variable = H("#FC50B4")
        },

        new("tokyo-night", "navy with electric blue & purple")
        {
            Bg = H("#1A1B26"), Stripe = H("#1F2133"), Float = H("#16161E"), Sel = H("#283457"),
            Hi = H("#7AA2F7"), HiFg = H("#1A1B26"), Fg = H("#C0CAF5"), Dim = H("#565F89"),
            Border = H("#3B4261"), Accent = H("#7AA2F7"), Gold = H("#E0AF68"),
            TitleFg = H("#1A1B26"), TitleBg = H("#BB9AF7"), Ok = H("#9ECE6A"), Err = H("#F7768E"),
            Keyword = H("#BB9AF7"), Ddl = H("#2AC3DE"), Function = H("#7AA2F7"), String = H("#9ECE6A"),
            Comment = H("#565F89"), Number = H("#FF9E64"), Operator = H("#89DDFF"), Variable = H("#E0AF68")
        },

        new("catppuccin", "soft pastels on mocha")
        {
            Bg = H("#1E1E2E"), Stripe = H("#242436"), Float = H("#181825"), Sel = H("#313244"),
            Hi = H("#CBA6F7"), HiFg = H("#1E1E2E"), Fg = H("#CDD6F4"), Dim = H("#7F849C"),
            Border = H("#45475A"), Accent = H("#89B4FA"), Gold = H("#F9E2AF"),
            TitleFg = H("#11111B"), TitleBg = H("#CBA6F7"), Ok = H("#A6E3A1"), Err = H("#F38BA8"),
            Keyword = H("#CBA6F7"), Ddl = H("#F9E2AF"), Function = H("#89B4FA"), String = H("#A6E3A1"),
            Comment = H("#7F849C"), Number = H("#FAB387"), Operator = H("#89DCEB"), Variable = H("#F5C2E7")
        },

        new("rose-pine", "muted rose, gold & foam")
        {
            Bg = H("#191724"), Stripe = H("#1F1D2E"), Float = H("#1F1D2E"), Sel = H("#403D52"),
            Hi = H("#EBBCBA"), HiFg = H("#191724"), Fg = H("#E0DEF4"), Dim = H("#6E6A86"),
            Border = H("#524F67"), Accent = H("#C4A7E7"), Gold = H("#F6C177"),
            TitleFg = H("#191724"), TitleBg = H("#EBBCBA"), Ok = H("#9CCFD8"), Err = H("#EB6F92"),
            Keyword = H("#31748F"), Ddl = H("#9CCFD8"), Function = H("#EBBCBA"), String = H("#F6C177"),
            Comment = H("#6E6A86"), Number = H("#EB6F92"), Operator = H("#908CAA"), Variable = H("#C4A7E7")
        },

        new("kanagawa", "ink-wash blues & autumn tones")
        {
            Bg = H("#1F1F28"), Stripe = H("#252530"), Float = H("#16161D"), Sel = H("#223249"),
            Hi = H("#7E9CD8"), HiFg = H("#1F1F28"), Fg = H("#DCD7BA"), Dim = H("#727169"),
            Border = H("#54546D"), Accent = H("#7E9CD8"), Gold = H("#E6C384"),
            TitleFg = H("#1F1F28"), TitleBg = H("#957FB8"), Ok = H("#98BB6C"), Err = H("#FF5D62"),
            Keyword = H("#957FB8"), Ddl = H("#7AA89F"), Function = H("#7E9CD8"), String = H("#98BB6C"),
            Comment = H("#727169"), Number = H("#D27E99"), Operator = H("#C0A36E"), Variable = H("#E6C384")
        },

        new("dracula", "purple, pink & neon green")
        {
            Bg = H("#282A36"), Stripe = H("#2E303E"), Float = H("#21222C"), Sel = H("#44475A"),
            Hi = H("#BD93F9"), HiFg = H("#282A36"), Fg = H("#F8F8F2"), Dim = H("#6272A4"),
            Border = H("#6272A4"), Accent = H("#FF79C6"), Gold = H("#F1FA8C"),
            TitleFg = H("#282A36"), TitleBg = H("#BD93F9"), Ok = H("#50FA7B"), Err = H("#FF5555"),
            Keyword = H("#FF79C6"), Ddl = H("#8BE9FD"), Function = H("#50FA7B"), String = H("#F1FA8C"),
            Comment = H("#6272A4"), Number = H("#BD93F9"), Operator = H("#FF79C6"), Variable = H("#FFB86C")
        },

        new("nord", "cool arctic blues")
        {
            Bg = H("#2E3440"), Stripe = H("#333A47"), Float = H("#3B4252"), Sel = H("#434C5E"),
            Hi = H("#88C0D0"), HiFg = H("#2E3440"), Fg = H("#D8DEE9"), Dim = H("#616E88"),
            Border = H("#4C566A"), Accent = H("#88C0D0"), Gold = H("#EBCB8B"),
            TitleFg = H("#ECEFF4"), TitleBg = H("#5E81AC"), Ok = H("#A3BE8C"), Err = H("#BF616A"),
            Keyword = H("#81A1C1"), Ddl = H("#8FBCBB"), Function = H("#88C0D0"), String = H("#A3BE8C"),
            Comment = H("#616E88"), Number = H("#B48EAD"), Operator = H("#81A1C1"), Variable = H("#D08770")
        },

        new("gruvbox", "retro warm earth tones")
        {
            Bg = H("#282828"), Stripe = H("#32302F"), Float = H("#1D2021"), Sel = H("#504945"),
            Hi = H("#FABD2F"), HiFg = H("#282828"), Fg = H("#EBDBB2"), Dim = H("#928374"),
            Border = H("#665C54"), Accent = H("#8EC07C"), Gold = H("#FABD2F"),
            TitleFg = H("#282828"), TitleBg = H("#FE8019"), Ok = H("#B8BB26"), Err = H("#FB4934"),
            Keyword = H("#FB4934"), Ddl = H("#FABD2F"), Function = H("#8EC07C"), String = H("#B8BB26"),
            Comment = H("#928374"), Number = H("#D3869B"), Operator = H("#FE8019"), Variable = H("#83A598")
        },

        new("synthwave", "80s neon — hot pink & cyan")
        {
            Bg = H("#262335"), Stripe = H("#2C2740"), Float = H("#241B2F"), Sel = H("#463465"),
            Hi = H("#FF7EDB"), HiFg = H("#262335"), Fg = H("#EBE4F2"), Dim = H("#848BBD"),
            Border = H("#495495"), Accent = H("#36F9F6"), Gold = H("#FEDE5D"),
            TitleFg = H("#241B2F"), TitleBg = H("#FF7EDB"), Ok = H("#72F1B8"), Err = H("#FE4450"),
            Keyword = H("#FEDE5D"), Ddl = H("#FE4450"), Function = H("#36F9F6"), String = H("#FF8B39"),
            Comment = H("#848BBD"), Number = H("#F97E72"), Operator = H("#FF7EDB"), Variable = H("#72F1B8")
        },
    ];
}
