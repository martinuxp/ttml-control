using System.Text;
using TtmlControl.Core.Ttml;

namespace TtmlControl.Tests;

public class TtmlParserTests
{
    private const string Namespaces =
        """xmlns:ttm="http://www.w3.org/ns/ttml#metadata" xmlns:amll="http://www.example.com/ns/amll" xmlns:itunes="http://music.apple.com/lyric-ttml-internal" """;

    private static TtmlDocument ParseXml(string xml) =>
        TtmlParser.Parse(new MemoryStream(Encoding.UTF8.GetBytes(xml)));

    private static string Song(string defaultNamespace, string head, string paragraphs) =>
        $"""<tt {defaultNamespace} {Namespaces}><head><metadata>{head}</metadata></head><body dur="01:00.000"><div>{paragraphs}</div></body></tt>""";

    [Theory]
    [InlineData("")]
    [InlineData("""xmlns="http://www.w3.org/ns/ttml" """)]
    public void Reads_metadata_with_or_without_default_namespace(string defaultNamespace)
    {
        var doc = ParseXml(Song(
            defaultNamespace,
            """<amll:meta key="musicName" value="Song" /><amll:meta key="artists" value="A" /><amll:meta key="artists" value="B" /><amll:meta key="isrc" value="X1" />""",
            """<p begin="1.000" end="2.000" ttm:agent="v1" itunes:key="L1"><span begin="1.000" end="2.000">Hi</span></p>"""));

        Assert.Equal("Song", doc.Title);
        Assert.Equal(["A", "B"], doc.Artists);
        Assert.Equal("X1", doc.Isrc);
        Assert.Equal(TimeSpan.FromMinutes(1), doc.Duration);
        Assert.Equal("L1", doc.Lines.Single().Key);
    }

    [Fact]
    public void Ignores_stray_numeric_titles_unless_nothing_else_exists()
    {
        var mixed = ParseXml(Song("", """<amll:meta key="musicName" value="8" /><amll:meta key="musicName" value="Abyecta" />""", ""));
        var numeric = ParseXml(Song("", """<amll:meta key="musicName" value="1979" />""", ""));

        Assert.Equal("Abyecta", mixed.Title);
        Assert.Equal("1979", numeric.Title);
    }

    [Fact]
    public void Joins_syllables_and_separates_background_vocals()
    {
        var doc = ParseXml(Song(
            "",
            "",
            """<p begin="1" end="2"><span begin="1" end="1.2">an</span> <span begin="1.2" end="1.4">ho</span><span begin="1.4" end="1.6">nest</span><span ttm:role="x-bg" begin="1" end="2"><span begin="1" end="2">(echo)</span></span><span ttm:role="x-translation">traducción</span></p>"""));

        var line = doc.Lines.Single();
        Assert.Equal("an honest", line.Text);
        Assert.Equal("(echo)", line.BackgroundText);
        Assert.True(doc.HasBackgroundVocals);
    }

    [Fact]
    public void Fingerprint_ignores_timings_and_punctuation_but_not_words()
    {
        var original = ParseXml(Song("", "", """<p begin="1" end="2"><span begin="1" end="2">Hello,</span> <span begin="2" end="3">world</span></p>"""));
        var retimed = ParseXml(Song("", "", """<p begin="5" end="6"><span begin="5" end="6">hello</span> <span begin="6" end="7">WORLD!</span></p>"""));
        var edited = ParseXml(Song("", "", """<p begin="1" end="2"><span begin="1" end="2">hello</span> <span begin="2" end="3">there</span></p>"""));

        Assert.Equal(original.LyricsFingerprint, retimed.LyricsFingerprint);
        Assert.NotEqual(original.LyricsFingerprint, edited.LyricsFingerprint);
    }

    [Theory]
    [InlineData("""<tt itunes:timing="Word" xmlns:itunes="http://music.apple.com/lyric-ttml-internal"><body /></tt>""", TtmlTiming.Word)]
    [InlineData("""<tt><body><p begin="1" end="2"><span begin="1" end="2">a</span></p></body></tt>""", TtmlTiming.Word)]
    [InlineData("""<tt><body><p begin="1" end="2">a</p></body></tt>""", TtmlTiming.Line)]
    [InlineData("""<tt><body /></tt>""", TtmlTiming.Unknown)]
    public void Detects_timing(string xml, TtmlTiming expected)
    {
        Assert.Equal(expected, ParseXml(xml).Timing);
    }

    [Theory]
    [InlineData("12.499", 12.499)]
    [InlineData("2:51.742", 171.742)]
    [InlineData("02:33.184", 153.184)]
    [InlineData("1:00:00.5", 3600.5)]
    [InlineData("12.5s", 12.5)]
    public void Parses_time_formats(string value, double seconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(seconds), TtmlParser.ParseTime(value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("abc")]
    public void Invalid_time_is_null(string? value)
    {
        Assert.Null(TtmlParser.ParseTime(value));
    }

    [Theory]
    [InlineData("<tt><body>")]
    [InlineData("<html />")]
    [InlineData("""<!DOCTYPE tt [<!ENTITY x "y">]><tt />""")]
    public void Rejects_invalid_documents(string xml)
    {
        Assert.Throws<TtmlParseException>(() => ParseXml(xml));
    }

    [Theory]
    [InlineData(@"C:\x\femtanyl - CITY(4).ttml", "femtanyl - CITY", new[] { "femtanyl", "CITY" })]
    [InlineData(@"C:\x\Mal Comunicada - AKRIILA, Latin Mafia (2).ttml", "Mal Comunicada - AKRIILA, Latin Mafia", new[] { "Mal Comunicada", "AKRIILA, Latin Mafia" })]
    [InlineData(@"C:\x\NANA フリーク版.ttml", "NANA フリーク版", new[] { "NANA フリーク版" })]
    public void File_name_hints_drop_copy_suffix(string path, string cleanName, string[] parts)
    {
        var hints = FileNameHints.FromPath(path);

        Assert.Equal(cleanName, hints.CleanName);
        Assert.Equal(parts, hints.Parts);
    }
}
