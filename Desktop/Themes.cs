using System.Drawing;

namespace WyreMonitor.Desktop;

/// <summary>
/// The page's board themes and alert colours, for what the app draws itself: the window
/// background and title bar, the small window and the number on the tray icon. Same values as
/// the theme tokens in wwwroot/styles.css: change both together.
/// </summary>
internal static class Themes
{
    /// <param name="Page">Page background (solder mask).</param>
    /// <param name="Board">The board, a step lighter: the small window's and the icon's surface.</param>
    /// <param name="Edge">Outline of controls on the page.</param>
    /// <param name="Text">Main text on the page.</param>
    /// <param name="TextDim">Secondary text on the page.</param>
    /// <param name="Hover">Surface under the pointer.</param>
    /// <param name="Light">A light theme: Windows draws a light title bar.</param>
    public sealed record Theme(Color Page, Color Board, Color Edge, Color Text, Color TextDim, Color Hover, bool Light);

    private static Color C(string hex) => ColorTranslator.FromHtml(hex);

    public static readonly Theme Green = new(C("#0c2a25"), C("#123a32"), C("#4f9a86"), C("#f0f1e4"), C("#a9c4b9"), C("#123a32"), false);
    public static readonly Theme Black = new(C("#101112"), C("#18191b"), C("#73787f"), C("#ececea"), C("#a4a8ad"), C("#1f2023"), false);
    public static readonly Theme Blue = new(C("#0a1a33"), C("#0f2548"), C("#5583c6"), C("#eef3fb"), C("#a9bddb"), C("#1b2433"), false);
    public static readonly Theme White = new(C("#e4e8e1"), C("#f7f8f4"), C("#76847d"), C("#17201c"), C("#4f5d56"), C("#e4e8e1"), true);

    public static Theme For(string? name) => name switch
    {
        "black" => Black,
        "blue" => Blue,
        "white" => White,
        _ => Green,
    };

    /// <summary>
    /// Warning and critical colours. For colour blindness: orange and purple (Okabe-Ito),
    /// which stay apart with red-green deficiencies. On a light surface, darker shades keep
    /// their contrast against it.
    /// </summary>
    public static (Color Warn, Color Crit) Alerts(string? palette, bool light) => (palette == "cb", light) switch
    {
        (true, false) => (C("#e69f00"), C("#cc79a7")),
        (true, true) => (C("#7a5200"), C("#933469")),
        (false, true) => (C("#a04400"), C("#a3341a")),
        _ => (C("#ff8a3d"), C("#ff5c33")),
    };
}
