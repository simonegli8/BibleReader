using System;
using System.Linq;
using System.Collections.Generic;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;

namespace BibleReader.Library.Downloads;

/// <summary>
/// A cover for a book that has none (<see cref="InstallableBook.Image"/> is null): an SVG, as a <c>data:</c> address, with
/// the name of the book over a background that fits its epoch, an ornament as the border and the origin at the bottom.
/// The same book always gets the same cover: the epoch comes from the year in its name (a book without a year counts as 2000), the preset of the epoch from a stable hash of the name.
/// </summary>
public static class CoverFallback
{
    /// <summary>The font of the text: the one of the app, which <see cref="BibleReader.Library.UI.OnlineBibleItem"/> gives to the SVG renderer (it is not looked up in the system), and a serif if it is missing.</summary>
    public const string FontFamily = "'Bible Roman', Georgia, 'Times New Roman', serif";

    /// <summary>The colours of a preset: the background from top to bottom, the ornament and the text.</summary>
    record Preset(string Top, string Bottom, string Accent, string Text);

    // The presets of every epoch; the ornament (see Ornament) is chosen separately, so a preset has several looks.
    static readonly Preset[] Ancient =   // until 999: parchment and ink
    {
        new("#dcc590", "#b8955a", "#5b3a14", "#3b2410"),
        new("#cdb987", "#a58b57", "#4a2d12", "#33200e"),
        new("#e3d3a6", "#c0a66f", "#6a4318", "#402812"),
    };
    static readonly Preset[] Medieval =  // 1000-1499: manuscript colours and gold
    {
        new("#7a1f2b", "#4a0f19", "#e0b84d", "#f5e6c4"),
        new("#1f3a6b", "#122344", "#e0b84d", "#f5e6c4"),
        new("#3d2a5c", "#241638", "#e0b84d", "#f5e6c4"),
    };
    static readonly Preset[] Reformation = // 1500-1699: leather bindings
    {
        new("#1f4a3a", "#10291f", "#d4b25c", "#f1e7cd"),
        new("#5a3820", "#33200f", "#d4b25c", "#f1e7cd"),
        new("#6b2a1a", "#3e1409", "#d4b25c", "#f1e7cd"),
    };
    static readonly Preset[] Classic =   // 1700-1899: cloth and gilt
    {
        new("#1b2a49", "#0e1628", "#c9a95a", "#efe6cf"),
        new("#5c1f2e", "#36111b", "#c9a95a", "#efe6cf"),
        new("#2f3a33", "#1a211d", "#c9a95a", "#efe6cf"),
    };
    static readonly Preset[] Modern =    // from 1900: plain and clear
    {
        new("#2f3e46", "#1b252a", "#cad2c5", "#f4f6f3"),
        new("#1f5f6b", "#103840", "#d9ece8", "#f4f6f3"),
        new("#e9e2d0", "#cfc4a8", "#7a5c2e", "#2b2b2b"),
    };

    /// <summary>The cover of the book as an address <c>data:image/svg+xml;base64,...</c>.</summary>
    public static string Create(InstallableBook book)
    {
        var name = (book.Name ?? book.EnglishName ?? "").Trim();
        var hash = Hash(name);

        // a book without a year in its name is taken as contemporary: most of the free translations are
        var year = Year(book);
        var presets = (year ?? 2000) switch
        {
            < 1000 => Ancient,
            < 1500 => Medieval,
            < 1700 => Reformation,
            < 1900 => Classic,
            _ => Modern,
        };
        var preset = presets[hash % (uint)presets.Length];
        var ornament = (int)((hash >> 20) % 3);

        var svg = Svg(name, book.Origin, year, preset, ornament, hash);
        return "data:image/svg+xml;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(svg));
    }

    /// <summary>The SVG source of a data address made by <see cref="Create"/>; null if it is not one.</summary>
    public static string? FromDataUri(string? uri)
    {
        const string prefix = "data:image/svg+xml;base64,";
        if (uri == null || !uri.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        return Encoding.UTF8.GetString(Convert.FromBase64String(uri.Substring(prefix.Length)));
    }

    /// <summary>A hash of the text that is the same in every run and on every machine (string.GetHashCode is not).</summary>
    static uint Hash(string text)
    {
        uint h = 2166136261;
        foreach (var b in Encoding.UTF8.GetBytes(text)) h = (h ^ b) * 16777619;
        return h;
    }

    static int? Year(InstallableBook book)
    {
        int? year = null;
        foreach (var text in new[] { book.Name, book.EnglishName })
        {
            var m = Regex.Match(text ?? "", "(?<![0-9])[0-9]{4}(?![0-9])");
            if (m.Success) year = Math.Max(year ?? 0, int.Parse(m.Value));
        }
        return year;
    }

    /// <summary>
    /// The photos of leather (jpg, about 300 x 420) that are the background of the covers of the books from 1000 to 1899: the files
    /// <c>Assets/Leather/leather1.jpg</c>, <c>leather2.jpg</c>, ... of the app, as far as they are numbered without a gap, unless set here.
    /// Without any photo the covers have a plain colour. The same book always gets the same one (a hash of its name).
    /// </summary>
    public static IReadOnlyList<byte[]> Leathers
    {
        get
        {
            if (_leathers == null)
            {
                var list = new List<byte[]>();
                for (int i = 1; ; i++)
                {
                    try
                    {
                        using var asset = Avalonia.Platform.AssetLoader.Open(new Uri($"avares://BibleReader.Library/Assets/Leather/leather{i}.jpg"));
                        using var data = new System.IO.MemoryStream();
                        asset.CopyTo(data);
                        list.Add(data.ToArray());
                    }
                    catch (Exception) { break; } // no more photos
                }
                _leathers = list;
            }
            return _leathers;
        }
        set => _leathers = value;
    }
    static IReadOnlyList<byte[]>? _leathers;

    static string F(double d) => d.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// One corner of the frame, drawn at the origin and pointing right and down; the others are this one mirrored.
    /// 0: two scrolls that curl into each other, 1: quarter arcs with a fleuron, 2: a rosette.
    /// </summary>
    static string Corner(int ornament, Preset p, string ground)
    {
        var a = p.Accent;
        switch (ornament)
        {
            case 0:
                return $"<g fill=\"none\" stroke=\"{a}\" stroke-width=\"1.5\" stroke-linecap=\"round\">" +
                       "<path d=\"M0,0 C0,18 6,29 18,29 C26,29 29,22 25,18 C22,15 17,18 19,21\"/>" +
                       "<path d=\"M0,0 C18,0 29,6 29,18 C29,26 22,29 18,25 C15,22 18,17 21,19\"/>" +
                       "<path d=\"M0,12 C5,12 9,8 9,4\" stroke-width=\"1\"/>" +
                       $"</g><circle r=\"3.4\" fill=\"{a}\"/><circle cx=\"21\" cy=\"21\" r=\"1.6\" fill=\"{a}\"/>";
            case 1:
                return $"<g fill=\"none\" stroke=\"{a}\" stroke-width=\"1.3\">" +
                       "<path d=\"M0,30 A30,30 0 0 0 30,0\"/><path d=\"M0,22 A22,22 0 0 0 22,0\" stroke-width=\"0.8\"/>" +
                       "<path d=\"M0,14 A14,14 0 0 0 14,0\" stroke-width=\"0.8\"/></g>" +
                       // a fleuron: three leaves on the diagonal
                       $"<g fill=\"{a}\"><ellipse cx=\"8\" cy=\"8\" rx=\"5.5\" ry=\"2.2\" transform=\"rotate(45 8 8)\"/>" +
                       "<ellipse cx=\"5\" cy=\"12\" rx=\"3.5\" ry=\"1.6\" transform=\"rotate(110 5 12)\"/>" +
                       "<ellipse cx=\"12\" cy=\"5\" rx=\"3.5\" ry=\"1.6\" transform=\"rotate(-20 12 5)\"/>" +
                       "<circle r=\"3\"/></g>";
            default:
                var petals = new StringBuilder();
                for (int k = 0; k < 8; k++)
                    petals.Append($"<ellipse cx=\"0\" cy=\"-8\" rx=\"2.8\" ry=\"6\" transform=\"rotate({k * 45})\"/>");
                return $"<g transform=\"translate(10 10)\"><g fill=\"{a}\" fill-opacity=\"0.9\">{petals}</g>" +
                       $"<circle r=\"4.2\" fill=\"{ground}\" stroke=\"{a}\" stroke-width=\"1.2\"/><circle r=\"1.5\" fill=\"{a}\"/></g>" +
                       $"<path d=\"M24,10 L40,10 M10,24 L10,40\" stroke=\"{a}\" stroke-width=\"1\"/>";
        }
    }

    static string Svg(string name, string? origin, int? year, Preset p, int ornament, uint hash)
    {
        // the books from 1000 to 1899 are bound in leather, if there are photos of one; the parchment and the modern ones are plain
        var leathers = year is >= 1000 and < 1900 ? Leathers : null;
        var leather = leathers is { Count: > 0 } ? leathers[(int)((hash >> 8) % (uint)leathers.Count)] : null;
        var ground = p.Top;
        var a = p.Accent;

        var sb = new StringBuilder();
        sb.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:xlink=\"http://www.w3.org/1999/xlink\" width=\"200\" height=\"280\" viewBox=\"0 0 200 280\">");
        sb.Append($"<defs><linearGradient id=\"bg\" x1=\"0\" y1=\"0\" x2=\"0\" y2=\"1\"><stop offset=\"0\" stop-color=\"{p.Top}\"/><stop offset=\"1\" stop-color=\"{p.Bottom}\"/></linearGradient>" +
                  "<radialGradient id=\"vig\" cx=\"0.5\" cy=\"0.5\" r=\"0.75\"><stop offset=\"0.55\" stop-color=\"#000\" stop-opacity=\"0\"/><stop offset=\"1\" stop-color=\"#000\" stop-opacity=\"0.5\"/></radialGradient></defs>");
        if (leather != null)
        {
            // the photo, coloured by the preset, and darker at the edges like an old binding
            sb.Append($"<image x=\"0\" y=\"0\" width=\"200\" height=\"280\" preserveAspectRatio=\"xMidYMid slice\" xlink:href=\"data:image/jpeg;base64,{Convert.ToBase64String(leather)}\"/>");
            sb.Append($"<rect width=\"200\" height=\"280\" fill=\"{p.Top}\" fill-opacity=\"0.45\"/>");
            sb.Append("<rect width=\"200\" height=\"280\" fill=\"url(#vig)\"/>");
        }
        else sb.Append("<rect width=\"200\" height=\"280\" fill=\"url(#bg)\"/>");

        // the frame: a heavy rule and a fine one, like the gilt tooling of a binding
        sb.Append($"<rect x=\"7\" y=\"7\" width=\"186\" height=\"266\" fill=\"none\" stroke=\"{a}\" stroke-width=\"2.6\"/>");
        sb.Append($"<rect x=\"11.5\" y=\"11.5\" width=\"177\" height=\"257\" fill=\"none\" stroke=\"{a}\" stroke-width=\"0.8\"/>");
        sb.Append($"<rect x=\"15\" y=\"15\" width=\"170\" height=\"250\" fill=\"none\" stroke=\"{a}\" stroke-width=\"1.1\"/>");
        foreach (var (x, y, sx, sy) in new[] { (15, 15, 1, 1), (185, 15, -1, 1), (15, 265, 1, -1), (185, 265, -1, -1) })
            sb.Append($"<g transform=\"translate({x} {y}) scale({sx} {sy})\">{Corner(ornament, p, ground)}</g>");

        // a small lozenge in the middle of the top and of the bottom rule
        foreach (var y in new[] { 15, 265 })
            sb.Append($"<polygon points=\"100,{y - 5} 106,{y} 100,{y + 5} 94,{y}\" fill=\"{a}\"/><circle cx=\"88\" cy=\"{y}\" r=\"1.6\" fill=\"{a}\"/><circle cx=\"112\" cy=\"{y}\" r=\"1.6\" fill=\"{a}\"/>");

        if (year != null)
            sb.Append($"<text x=\"100\" y=\"42\" text-anchor=\"middle\" font-family=\"{FontFamily}\" font-size=\"12\" letter-spacing=\"3\" fill=\"{a}\">{year}</text>");

        // the name, in the middle, wrapped; on leather on a label (the title piece of a binding)
        var total = Em(name);
        var size = total <= 9 ? 26 : total <= 18 ? 21 : 17;
        // only an ordinary word is made to fit by a smaller font; Chinese and the like (and any run of more than 16 letters) is broken instead
        var longest = name.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length <= 16 && !w.EnumerateRunes().Any(r => IsWide(r.Value))).Select(Em).DefaultIfEmpty(1).Max();
        size = Math.Max(Math.Min(size, (int)(124 / longest)), 11); // the longest word fits the label, if that can be done in a readable size
        var lines = Wrap(name, 124.0 / size);
        var lineHeight = size * 1.25;
        var y0 = 128 - (lines.Count - 1) * lineHeight / 2 + size * 0.35;
        if (leather != null)
        {
            var h = lines.Count * lineHeight + 22;
            var top = 128 - h / 2;
            sb.Append($"<rect x=\"26\" y=\"{F(top)}\" width=\"148\" height=\"{F(h)}\" fill=\"{p.Bottom}\" fill-opacity=\"0.85\" stroke=\"{a}\" stroke-width=\"1.6\"/>");
            sb.Append($"<rect x=\"30\" y=\"{F(top + 4)}\" width=\"140\" height=\"{F(h - 8)}\" fill=\"none\" stroke=\"{a}\" stroke-width=\"0.7\"/>");
        }
        sb.Append($"<text text-anchor=\"middle\" font-family=\"{FontFamily}\" font-size=\"{size}\" font-weight=\"bold\" fill=\"{p.Text}\">");
        for (int i = 0; i < lines.Count; i++)
            sb.Append($"<tspan x=\"100\" y=\"{F(y0 + i * lineHeight)}\">{SecurityElement.Escape(lines[i])}</tspan>");
        sb.Append("</text>");

        // a classical divider (a lozenge between two curls) and the origin at the bottom
        sb.Append($"<g fill=\"none\" stroke=\"{a}\" stroke-width=\"1\" stroke-linecap=\"round\">" +
                  "<path d=\"M40,224 L84,224 C89,224 90,218 86,217 C83,216 82,220 85,221\"/>" +
                  "<path d=\"M160,224 L116,224 C111,224 110,218 114,217 C117,216 118,220 115,221\"/></g>");
        sb.Append($"<polygon points=\"100,218.5 105,224 100,229.5 95,224\" fill=\"{a}\"/>");
        if (!string.IsNullOrWhiteSpace(origin))
            sb.Append($"<text x=\"100\" y=\"247\" text-anchor=\"middle\" font-family=\"{FontFamily}\" font-size=\"12\" font-style=\"italic\" fill=\"{p.Text}\">{SecurityElement.Escape(origin)}</text>");

        sb.Append("</svg>");
        return sb.ToString();
    }

    /// <summary>The width of a text in em: a Chinese, Japanese or Korean character is as wide as it is high, a Latin one is about 0.56.</summary>
    static double Em(string text)
    {
        double em = 0;
        foreach (var rune in text.EnumerateRunes()) em += IsWide(rune.Value) ? 1.0 : 0.56;
        return em;
    }

    const string NoLineStart = "，。、．！？；：）」』】》〉”’,.!?;:)]}";

    static bool IsWide(int c) =>
        c is >= 0x1100 and <= 0x11FF       // Hangul Jamo
        or >= 0x2E80 and <= 0x9FFF         // CJK radicals, kana, Bopomofo, CJK ideographs, ...
        or >= 0xAC00 and <= 0xD7FF         // Hangul
        or >= 0xF900 and <= 0xFAFF         // CJK compatibility ideographs
        or >= 0xFE30 and <= 0xFE6F
        or >= 0xFF00 and <= 0xFF60         // full-width forms
        or >= 0xFFE0 and <= 0xFFE6
        or >= 0x20000 and <= 0x3FFFF;      // CJK extensions

    /// <summary>
    /// The words in lines of at most <paramref name="maxEm"/> em (see <see cref="Em"/>), up to six lines. A word that is longer than a
    /// line, and so a text without spaces such as Chinese or Thai, is broken between its characters.
    /// </summary>
    static List<string> Wrap(string text, double maxEm)
    {
        var lines = new List<string>();
        var line = "";
        void Add(string piece)
        {
            var sep = line.Length == 0 ? "" : " ";
            if (line.Length > 0 && Em(line + sep + piece) > maxEm) { lines.Add(line); line = piece; }
            else line += sep + piece;
        }
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (Em(word) <= maxEm) { Add(word); continue; }
            // too long for a line: break it between characters (text elements, so that accents stay with their letter)
            var elements = System.Globalization.StringInfo.GetTextElementEnumerator(word);
            var piece = "";
            while (elements.MoveNext())
            {
                var c = (string)elements.Current;
                // punctuation that closes a phrase does not start a line in Chinese, Japanese or Korean: it stays on the line before
                if (piece.Length > 0 && Em(piece + c) > maxEm && !NoLineStart.Contains(c)) { Add(piece); lines.Add(line); line = ""; piece = ""; }
                piece += c;
            }
            if (piece.Length > 0) Add(piece);
        }
        if (line.Length > 0) lines.Add(line);
        if (lines.Count > 6) { lines = lines.GetRange(0, 6); lines[5] += "…"; }
        return lines;
    }
}
