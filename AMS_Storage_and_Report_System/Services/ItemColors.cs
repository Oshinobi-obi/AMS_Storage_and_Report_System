// This is my helper for item colors (database update 020). The same code is in e-PINS and e-SRS.
//   supply_items.color_options  "Black|#1f2937;Blue|#1d4ed8"   the colors AMS offers
//   cart_items.colors           "Black=5;Blue=3"                how many of each the office wants

using System.Text.RegularExpressions;

namespace AMS_Storage_and_Report_System.Services;

public sealed record ItemColor(string Name, string Hex);

public static partial class ItemColors
{
    public const int MaxColors = 12, MaxNameLength = 30;

    /// Common colors AMS can add with one click.
    public static readonly IReadOnlyList<ItemColor> Presets =
    [
        new("Black", "#1f2937"), new("Blue", "#1d4ed8"), new("Red", "#dc2626"), new("Green", "#16a34a"),
        new("Yellow", "#facc15"), new("Orange", "#f97316"), new("Pink", "#ec4899"), new("Violet", "#7c3aed"),
        new("Brown", "#92400e"), new("Gray", "#6b7280"), new("White", "#ffffff"), new("Silver", "#c0c4cc"),
        new("Gold", "#d4a017"), new("Light blue", "#7dd3fc"), new("Dark blue", "#1e3a8a"), new("Maroon", "#7f1d1d"),
    ];

    public static List<ItemColor> Parse(string? options)
    {
        var list = new List<ItemColor>();
        foreach (var part in (options ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var bits = part.Split('|');
            var name = Clean(bits[0]);
            if (name.Length == 0 || list.Any(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) continue;
            list.Add(new ItemColor(name, bits.Length > 1 && HexRx().IsMatch(bits[1]) ? bits[1].ToLowerInvariant() : "#9ca3af"));
        }
        return list;
    }

    public static string? Format(IEnumerable<ItemColor> colors)
    {
        var text = string.Join(";", colors.Take(MaxColors).Select(c => $"{Clean(c.Name)}|{(HexRx().IsMatch(c.Hex) ? c.Hex.ToLowerInvariant() : "#9ca3af")}"));
        return text.Length == 0 ? null : text.Length <= 500 ? text : text[..text.LastIndexOf(';', 499)];
    }

    /// "Black=5;Blue=3" becomes [(Black, 5), (Blue, 3)]
    public static List<(string Name, int Qty)> ParseQty(string? colors)
    {
        var list = new List<(string Name, int Qty)>();
        foreach (var part in (colors ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var i = part.LastIndexOf('=');
            if (i <= 0 || !int.TryParse(part[(i + 1)..], out var qty) || qty <= 0) continue;
            var name = Clean(part[..i]);
            var at = list.FindIndex(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (at >= 0) list[at] = (list[at].Name, list[at].Qty + qty); else list.Add((name, qty));
        }
        return list;
    }

    public static string? FormatQty(IEnumerable<(string Name, int Qty)> colors)
    {
        var text = string.Join(";", colors.Where(c => c.Qty > 0).Select(c => $"{Clean(c.Name)}={c.Qty}"));
        return text.Length == 0 ? null : text;
    }

    /// "Black 5, Blue 3" for the RIS line.
    public static string Text(IEnumerable<(string Name, int Qty)> colors) =>
        string.Join(", ", colors.Where(c => c.Qty > 0).Select(c => $"{c.Name} {c.Qty}"));

    /// Light colors (White, Yellow...) need a border so they show on white.
    public static bool IsLight(string hex)
    {
        if (!HexRx().IsMatch(hex)) return false;
        var r = Convert.ToInt32(hex[1..3], 16); var g = Convert.ToInt32(hex[3..5], 16); var b = Convert.ToInt32(hex[5..7], 16);
        return 0.299 * r + 0.587 * g + 0.114 * b > 200;
    }

    /// No separators, single spaces, 30 characters max.
    public static string Clean(string? name)
    {
        var s = string.Join(' ', (name ?? "").Replace(";", " ").Replace("|", " ").Replace("=", " ")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return s.Length <= MaxNameLength ? s : s[..MaxNameLength].TrimEnd();
    }

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex HexRx();
}
