using System.Text;
using TtmlControl.Core.Ttml;

namespace TtmlControl.Tests;

public class TtmlDiffTests
{
    private static TtmlDocument Doc(string paragraphs, string head = "") =>
        TtmlParser.Parse(new MemoryStream(Encoding.UTF8.GetBytes(
            $"""<tt xmlns:ttm="http://www.w3.org/ns/ttml#metadata" xmlns:amll="http://www.example.com/ns/amll"><head><metadata>{head}</metadata></head><body><div>{paragraphs}</div></body></tt>""")));

    private static string Line(string text, string begin = "1.000", string end = "2.000", string agent = "v1") =>
        $"""<p begin="{begin}" end="{end}" ttm:agent="{agent}"><span begin="{begin}" end="{end}">{text}</span></p>""";

    private static LineChangeKind[] Kinds(TtmlDiff diff) => diff.Lines.Select(l => l.Kind).ToArray();

    [Fact]
    public void Identical_documents_only_have_unchanged_lines()
    {
        var diff = TtmlDiff.Compare(Doc(Line("a") + Line("b")), Doc(Line("a") + Line("b")));

        Assert.Equal([LineChangeKind.Unchanged, LineChangeKind.Unchanged], Kinds(diff));
        Assert.Empty(diff.Metadata);
    }

    [Fact]
    public void Same_time_in_another_format_is_not_a_change()
    {
        var diff = TtmlDiff.Compare(Doc(Line("a", "1:05.500", "1:06.000")), Doc(Line("a", "00:65.500", "66.000")));

        Assert.Equal([LineChangeKind.Unchanged], Kinds(diff));
    }

    [Fact]
    public void Retimed_line_is_a_timing_change()
    {
        var diff = TtmlDiff.Compare(Doc(Line("a")), Doc(Line("a", begin: "1.100")));

        Assert.Equal([LineChangeKind.TimingChanged], Kinds(diff));
        Assert.Equal(1, diff.TimingChanges);
        Assert.Equal(0, diff.LyricChanges);
    }

    [Fact]
    public void Retimed_word_inside_an_unchanged_line_is_a_timing_change()
    {
        const string before = """<p begin="1" end="3"><span begin="1" end="2">a</span> <span begin="2" end="3">b</span></p>""";
        const string after = """<p begin="1" end="3"><span begin="1" end="1.5">a</span> <span begin="1.5" end="3">b</span></p>""";

        Assert.Equal([LineChangeKind.TimingChanged], Kinds(TtmlDiff.Compare(Doc(before), Doc(after))));
    }

    [Fact]
    public void Edited_word_pairs_old_and_new_line()
    {
        var diff = TtmlDiff.Compare(Doc(Line("one") + Line("two") + Line("three")), Doc(Line("one") + Line("TWO") + Line("three")));

        Assert.Equal([LineChangeKind.Unchanged, LineChangeKind.TextChanged, LineChangeKind.Unchanged], Kinds(diff));
        Assert.Equal("two", diff.Lines[1].Old!.Text);
        Assert.Equal("TWO", diff.Lines[1].New!.Text);
    }

    [Fact]
    public void Added_and_removed_lines_keep_later_lines_aligned()
    {
        var added = TtmlDiff.Compare(Doc(Line("a") + Line("c")), Doc(Line("a") + Line("b") + Line("c")));
        var removed = TtmlDiff.Compare(Doc(Line("a") + Line("b") + Line("c")), Doc(Line("a") + Line("c")));

        Assert.Equal([LineChangeKind.Unchanged, LineChangeKind.Added, LineChangeKind.Unchanged], Kinds(added));
        Assert.Equal([LineChangeKind.Unchanged, LineChangeKind.Removed, LineChangeKind.Unchanged], Kinds(removed));
    }

    [Fact]
    public void Repeated_chorus_aligns_when_a_verse_is_inserted()
    {
        var before = Line("chorus") + Line("end");
        var after = Line("verse") + Line("chorus") + Line("end");

        Assert.Equal([LineChangeKind.Added, LineChangeKind.Unchanged, LineChangeKind.Unchanged], Kinds(TtmlDiff.Compare(Doc(before), Doc(after))));
    }

    [Fact]
    public void Background_vocals_count_as_lyrics()
    {
        const string before = """<p begin="1" end="2"><span begin="1" end="2">a</span><span ttm:role="x-bg" begin="1" end="2"><span begin="1" end="2">(b)</span></span></p>""";
        const string after = """<p begin="1" end="2"><span begin="1" end="2">a</span><span ttm:role="x-bg" begin="1" end="2"><span begin="1" end="2">(c)</span></span></p>""";

        Assert.Equal([LineChangeKind.TextChanged], Kinds(TtmlDiff.Compare(Doc(before), Doc(after))));
    }

    [Fact]
    public void Voice_change_is_reported_separately()
    {
        var diff = TtmlDiff.Compare(Doc(Line("a")), Doc(Line("a", agent: "v2")));

        Assert.Equal([LineChangeKind.VoiceChanged], Kinds(diff));
    }

    [Fact]
    public void Metadata_changes_are_listed()
    {
        var diff = TtmlDiff.Compare(
            Doc(Line("a"), """<amll:meta key="album" value="Old" />"""),
            Doc(Line("a"), """<amll:meta key="album" value="New" />"""));

        var change = Assert.Single(diff.Metadata);
        Assert.Equal(("Álbum", "Old", "New"), (change.Field, change.Old, change.New));
    }

    [Fact]
    public void Empty_sides_are_all_additions_or_removals()
    {
        Assert.Equal([LineChangeKind.Added], Kinds(TtmlDiff.Compare(Doc(""), Doc(Line("a")))));
        Assert.Equal([LineChangeKind.Removed], Kinds(TtmlDiff.Compare(Doc(Line("a")), Doc(""))));
    }
}
