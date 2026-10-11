// SimplePdf.cs — a small PDF writer for reports (text, lines, shaded boxes, tables).
// Uses the standard Helvetica fonts every PDF reader has, so nothing needs to be installed
// on the server and no extra package is needed. Pages are A4 (portrait or landscape).
// Coordinates are in points (1/72 inch) from the TOP-left corner of the page.

using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace AMS_Storage_and_Report_System.Services;

public sealed class SimplePdf
{
    public const float A4Width = 595.28f, A4Height = 841.89f;

    private readonly List<StringBuilder> _pages = [];
    private StringBuilder? _page;
    public float PageWidth { get; }
    public float PageHeight { get; }

    public SimplePdf(bool landscape = false)
    {
        PageWidth = landscape ? A4Height : A4Width;
        PageHeight = landscape ? A4Width : A4Height;
    }

    public int PageCount => _pages.Count;

    public void NewPage()
    {
        _page = new StringBuilder();
        _pages.Add(_page);
    }

    private StringBuilder P => _page ?? throw new InvalidOperationException("Call NewPage() first.");
    private static string N(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);
    private float Y(float top) => PageHeight - top;

    // ── Drawing ─────────────────────────────────────────────────────────
    public enum Align { Left, Center, Right }

    /// Draws one line of text; `top` is the baseline position from the top of the page.
    public void Text(float x, float top, string text, float size = 10, bool bold = false, Align align = Align.Left,
                     (float R, float G, float B)? color = null)
    {
        if (string.IsNullOrEmpty(text)) return;
        var w = Measure(text, size, bold);
        var left = align switch { Align.Center => x - w / 2, Align.Right => x - w, _ => x };
        var (r, g, b) = color ?? (0, 0, 0);
        P.Append($"BT /{(bold ? "F2" : "F1")} {N(size)} Tf {N(r)} {N(g)} {N(b)} rg {N(left)} {N(Y(top))} Td (");
        P.Append(Escape(text));
        P.Append(") Tj ET\n");
    }

    public void Line(float x1, float top1, float x2, float top2, float width = .6f, float gray = 0)
    {
        P.Append($"{N(width)} w {N(gray)} G {N(x1)} {N(Y(top1))} m {N(x2)} {N(Y(top2))} l S\n");
    }

    /// A filled rectangle (colour 0–1 each) and/or its border.
    public void Box(float x, float top, float w, float h, (float R, float G, float B)? fill = null, float border = 0)
    {
        if (fill is var (r, g, b)) P.Append($"{N(r)} {N(g)} {N(b)} rg {N(x)} {N(Y(top + h))} {N(w)} {N(h)} re f\n");
        if (border > 0) P.Append($"{N(border)} w 0 G {N(x)} {N(Y(top + h))} {N(w)} {N(h)} re S\n");
    }

    /// Splits text into lines that fit `width`; words longer than a line are broken.
    public static List<string> Wrap(string text, float width, float size, bool bold = false)
    {
        var lines = new List<string>();
        foreach (var para in (text ?? "").Replace("\r", "").Split('\n'))
        {
            var current = "";
            foreach (var word in para.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = current.Length == 0 ? word : current + " " + word;
                if (Measure(candidate, size, bold) <= width) { current = candidate; continue; }
                if (current.Length > 0) lines.Add(current);
                current = word;
                while (Measure(current, size, bold) > width && current.Length > 1)
                {
                    var cut = current.Length - 1;
                    while (cut > 1 && Measure(current[..cut], size, bold) > width) cut--;
                    lines.Add(current[..cut]);
                    current = current[cut..];
                }
            }
            lines.Add(current);
        }
        return lines;
    }

    // ── Output ──────────────────────────────────────────────────────────
    public byte[] Save(string title = "", string author = "AMS StockWatch")
    {
        if (_pages.Count == 0) NewPage();
        var objects = new List<byte[]>();
        int Add(byte[] body) { objects.Add(body); return objects.Count; }
        byte[] A(string s) => Encoding.ASCII.GetBytes(s);

        // 1 catalog, 2 pages, 3 font, 4 bold font, 5 info — fixed numbers; pages follow
        objects.Add([]); objects.Add([]);
        Add(A("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>"));
        Add(A("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>"));
        Add(Info(title, author));

        var kids = new List<int>();
        foreach (var page in _pages)
        {
            var raw = Win1252(page.ToString());
            byte[] compressed;
            using (var ms = new MemoryStream())
            {
                using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(raw);
                compressed = ms.ToArray();
            }
            var stream = Concat(A($"<< /Length {compressed.Length} /Filter /FlateDecode >>\nstream\n"), compressed, A("\nendstream"));
            var contentId = Add(stream);
            kids.Add(Add(A($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {N(PageWidth)} {N(PageHeight)}] " +
                           $"/Resources << /Font << /F1 3 0 R /F2 4 0 R >> >> /Contents {contentId} 0 R >>")));
        }
        objects[0] = A("<< /Type /Catalog /Pages 2 0 R >>");
        objects[1] = A($"<< /Type /Pages /Kids [{string.Join(" ", kids.Select(k => $"{k} 0 R"))}] /Count {kids.Count} >>");

        using var outp = new MemoryStream();
        void W(byte[] b) => outp.Write(b);
        W(A("%PDF-1.4\n"));
        W([(byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n']);   // marks the file as binary
        var offsets = new List<long>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(outp.Position);
            W(A($"{i + 1} 0 obj\n")); W(objects[i]); W(A("\nendobj\n"));
        }
        var xref = outp.Position;
        var sb = new StringBuilder($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append(o.ToString("0000000000")).Append(" 00000 n \n");
        sb.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R /Info 5 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        W(A(sb.ToString()));
        return outp.ToArray();
    }

    private static byte[] Info(string title, string author)
    {
        var now = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
        var s = $"<< /Title ({Escape(title)}) /Author ({Escape(author)}) /Producer (AMS StockWatch) /CreationDate (D:{now}Z) >>";
        return Win1252(s);
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var all = new byte[parts.Sum(p => p.Length)];
        var at = 0;
        foreach (var p in parts) { Buffer.BlockCopy(p, 0, all, at, p.Length); at += p.Length; }
        return all;
    }

    // ── Text encoding and widths (Helvetica, WinAnsi) ───────────────────
    private static readonly Dictionary<char, byte> Cp1252Extra = new()
    {
        ['€'] = 0x80, ['‚'] = 0x82, ['„'] = 0x84, ['…'] = 0x85, ['†'] = 0x86, ['‡'] = 0x87, ['‰'] = 0x89,
        ['‹'] = 0x8B, ['‘'] = 0x91, ['’'] = 0x92, ['“'] = 0x93, ['”'] = 0x94, ['•'] = 0x95, ['–'] = 0x96,
        ['—'] = 0x97, ['™'] = 0x99, ['›'] = 0x9B,
    };

    private static byte[] Win1252(string s)
    {
        var bytes = new byte[s.Length];
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            bytes[i] = c < 0x80 || (c >= 0xA0 && c <= 0xFF) ? (byte)c : Cp1252Extra.TryGetValue(c, out var b) ? b : (byte)'?';
        }
        return bytes;
    }

    /// Characters Helvetica can't show are replaced (₱ → PHP).
    private static string Clean(string s) => s.Replace("₱", "PHP ").Replace("\t", " ");

    private static string Escape(string s)
    {
        var sb = new StringBuilder();
        foreach (var c in Clean(s))
        {
            if (c is '(' or ')' or '\\') sb.Append('\\');
            sb.Append(c < ' ' ? ' ' : c);
        }
        return sb.ToString();
    }

    // Advance widths (1/1000 em) for ASCII 32–126
    private static readonly short[] Regular =
    [
        278,278,355,556,556,889,667,191,333,333,389,584,278,333,278,278,556,556,556,556,556,556,556,556,556,556,
        278,278,584,584,584,556,1015,667,667,722,722,667,611,778,722,278,500,667,556,833,722,778,667,778,722,667,
        611,722,667,944,667,667,611,278,278,278,469,556,333,556,556,500,556,556,278,556,556,222,222,500,222,833,
        556,556,556,556,333,500,278,556,500,722,500,500,500,334,260,334,584,
    ];
    private static readonly short[] Bold =
    [
        278,333,474,556,556,889,722,238,333,333,389,584,278,333,278,278,556,556,556,556,556,556,556,556,556,556,
        333,333,584,584,584,611,975,722,722,722,722,667,611,778,722,278,556,722,611,833,722,778,667,778,722,667,
        611,722,667,944,667,667,611,333,278,333,584,556,333,556,611,556,611,556,333,611,611,278,278,556,278,889,
        611,611,611,611,389,556,333,611,556,778,556,556,500,389,280,389,584,
    ];

    public static float Measure(string text, float size, bool bold = false)
    {
        var table = bold ? Bold : Regular;
        var units = 0;
        foreach (var c in Clean(text ?? "")) units += c >= 32 && c <= 126 ? table[c - 32] : 556;
        return units * size / 1000f;
    }
}
