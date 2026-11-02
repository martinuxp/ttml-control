using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace TtmlControl.Core.Ttml;

public static partial class TtmlParser
{
    private const string BackgroundRole = "x-bg";

    public static TtmlDocument ParseFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Parse(stream);
    }

    public static TtmlDocument Parse(Stream stream)
    {
        var root = LoadRoot(stream);

        var head = Children(root, "head").FirstOrDefault();
        var meta = head is null
            ? []
            : Descendants(head, "meta")
                .Select(e => (Key: Attribute(e, "key"), Value: Attribute(e, "value")))
                .Where(m => !string.IsNullOrWhiteSpace(m.Key) && !string.IsNullOrWhiteSpace(m.Value))
                .Select(m => (Key: m.Key!, Value: m.Value!.Trim()))
                .ToList();

        var body = Children(root, "body").FirstOrDefault();
        var paragraphs = body is null ? [] : Descendants(body, "p").ToList();
        var lines = paragraphs.Select(ToLine).ToList();

        return new TtmlDocument(
            Title: PickTitle(MetaValues(meta, "musicName")),
            Artists: MetaValues(meta, "artists").Distinct().ToList(),
            Album: MetaValues(meta, "album").LastOrDefault(),
            Isrc: MetaValues(meta, "isrc").LastOrDefault(),
            CoverArtUrl: MetaValues(meta, "cover_art").LastOrDefault(),
            Language: MetaValues(meta, "language").LastOrDefault() ?? Attribute(root, "lang"),
            Songwriters: head is null
                ? []
                : Descendants(head, "songwriter").Select(e => e.Value.Trim()).Where(s => s.Length > 0).ToList(),
            Timing: DetectTiming(root, paragraphs),
            Duration: ParseTime(Attribute(body, "dur")) ?? lines.Max(l => l.End),
            AgentCount: head is null ? 0 : Descendants(head, "agent").Count(),
            HasBackgroundVocals: lines.Any(l => l.BackgroundText.Length > 0),
            HasTransliterations: head is not null && Descendants(head, "transliterations").Any(HasText),
            HasTranslations: head is not null && Descendants(head, "translations").Any(HasText),
            Lines: lines,
            LyricsFingerprint: Fingerprint(lines));
    }

    public static TimeSpan? ParseTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = value.Trim();
        if (text.EndsWith('s') && !text.EndsWith("ms", StringComparison.Ordinal))
        {
            text = text[..^1];
        }

        var seconds = 0d;
        foreach (var part in text.Split(':'))
        {
            if (!double.TryParse(part, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number))
            {
                return null;
            }

            seconds = seconds * 60 + number;
        }

        return TimeSpan.FromSeconds(seconds);
    }

    private static XElement LoadRoot(Stream stream)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
        };

        XDocument document;
        try
        {
            using var reader = XmlReader.Create(stream, settings);
            // Whitespace between spans separates the words of a line.
            document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        }
        catch (XmlException ex)
        {
            throw new TtmlParseException($"XML inválido: {ex.Message}", ex);
        }

        var root = document.Root;
        if (root is null || root.Name.LocalName != "tt")
        {
            throw new TtmlParseException("El elemento raíz no es <tt>.");
        }

        return root;
    }

    private static TtmlLine ToLine(XElement paragraph)
    {
        var main = new StringBuilder();
        var background = new StringBuilder();
        CollectText(paragraph, main, background, inBackground: false);

        return new TtmlLine(
            Key: Attribute(paragraph, "key"),
            Begin: ParseTime(Attribute(paragraph, "begin")),
            End: ParseTime(Attribute(paragraph, "end")),
            Agent: Attribute(paragraph, "agent"),
            Text: CollapseWhitespace(main.ToString()),
            BackgroundText: CollapseWhitespace(background.ToString()),
            TimingSignature: BuildTimingSignature(paragraph));
    }

    // Parsed values, so "00:05.673" and "5.673" count as the same time. Covers every word,
    // because a retimed word can leave the line's own begin and end untouched.
    private static string BuildTimingSignature(XElement paragraph)
    {
        var times = paragraph.DescendantsAndSelf().SelectMany(e => new[]
        {
            ParseTime(Attribute(e, "begin"))?.TotalMilliseconds,
            ParseTime(Attribute(e, "end"))?.TotalMilliseconds,
        });
        return string.Join(',', times.Where(t => t is not null).Select(t => Math.Round(t!.Value).ToString(CultureInfo.InvariantCulture)));
    }

    private static void CollectText(XElement element, StringBuilder main, StringBuilder background, bool inBackground)
    {
        foreach (var node in element.Nodes())
        {
            switch (node)
            {
                case XText text:
                    (inBackground ? background : main).Append(text.Value);
                    break;
                case XElement child:
                    var role = Attribute(child, "role");
                    if (role is null)
                    {
                        CollectText(child, main, background, inBackground);
                    }
                    else if (role == BackgroundRole)
                    {
                        CollectText(child, main, background, inBackground: true);
                    }

                    // Other roles (translation, romanization) are not part of the lyrics.
                    break;
            }
        }
    }

    private static TtmlTiming DetectTiming(XElement root, List<XElement> paragraphs)
    {
        var declared = Attribute(root, "timing");
        if (string.Equals(declared, "Word", StringComparison.OrdinalIgnoreCase))
        {
            return TtmlTiming.Word;
        }

        if (string.Equals(declared, "Line", StringComparison.OrdinalIgnoreCase))
        {
            return TtmlTiming.Line;
        }

        if (paragraphs.Count == 0)
        {
            return TtmlTiming.Unknown;
        }

        return paragraphs.Any(p => Children(p, "span").Any(s => Attribute(s, "begin") is not null))
            ? TtmlTiming.Word
            : TtmlTiming.Line;
    }

    private static string? PickTitle(List<string> candidates)
    {
        // Some files carry stray numeric musicName entries before the real title.
        return candidates.LastOrDefault(c => !c.All(char.IsDigit)) ?? candidates.LastOrDefault();
    }

    private static List<string> MetaValues(List<(string Key, string Value)> meta, string key)
    {
        return meta.Where(m => m.Key == key).Select(m => m.Value).ToList();
    }

    private static string Fingerprint(List<TtmlLine> lines)
    {
        var normalized = lines.Select(l => NormalizeLyrics(l.Text)).Where(l => l.Length > 0);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', normalized)));
        return Convert.ToHexStringLower(hash);
    }

    private static string NormalizeLyrics(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text.Normalize(NormalizationForm.FormKC).ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c) || CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.NonSpacingMark)
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    private static string CollapseWhitespace(string text) => Whitespace().Replace(text, " ").Trim();

    private static bool HasText(XElement element) => !string.IsNullOrWhiteSpace(element.Value);

    // Elements and attributes match by local name: real files mix default namespaces.
    private static IEnumerable<XElement> Children(XElement element, string localName) =>
        element.Elements().Where(e => e.Name.LocalName == localName);

    private static IEnumerable<XElement> Descendants(XElement element, string localName) =>
        element.Descendants().Where(e => e.Name.LocalName == localName);

    private static string? Attribute(XElement? element, string localName) =>
        element?.Attributes().FirstOrDefault(a => !a.IsNamespaceDeclaration && a.Name.LocalName == localName)?.Value;

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
