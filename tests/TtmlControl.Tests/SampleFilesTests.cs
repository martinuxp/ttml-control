using TtmlControl.Core.Ttml;

namespace TtmlControl.Tests;

/// <summary>Checks the parser against the real files in /samples, when they are present.</summary>
public class SampleFilesTests
{
    private static string? SamplesDirectory()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "samples");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static TtmlDocument? Load(string fileName)
    {
        var path = SamplesDirectory() is { } dir ? Path.Combine(dir, fileName) : null;
        return path is not null && File.Exists(path) ? TtmlParser.ParseFile(path) : null;
    }

    [Fact]
    public void Every_sample_yields_timed_lyrics_and_a_unique_fingerprint()
    {
        var dir = SamplesDirectory();
        if (dir is null)
        {
            return;
        }

        var docs = Directory.GetFiles(dir, "*.ttml").Select(TtmlParser.ParseFile).ToList();

        Assert.All(docs, doc =>
        {
            Assert.NotEmpty(doc.Lines);
            Assert.All(doc.Lines, line =>
            {
                Assert.NotEmpty(line.Text + line.BackgroundText);
                Assert.NotNull(line.Begin);
                Assert.NotNull(line.End);
            });
            Assert.NotNull(doc.Duration);
            Assert.Equal(TtmlTiming.Word, doc.Timing);
            Assert.NotEmpty(doc.Songwriters);
        });
        Assert.Equal(docs.Count, docs.Select(d => d.LyricsFingerprint).Distinct().Count());
    }

    [Fact]
    public void Picks_the_real_title_when_there_are_stray_entries()
    {
        if (Load("Inundaremos - Abyecta(1).ttml") is not { } doc)
        {
            return;
        }

        Assert.Equal("Abyecta", doc.Title);
        Assert.Equal(["Inundaremos"], doc.Artists);
        Assert.Equal("QM6P42635693", doc.Isrc);
        Assert.Equal(TimeSpan.FromSeconds(171.742), doc.Duration);
    }

    [Fact]
    public void Detects_transliterations()
    {
        if (Load("K_DA - MORE.ttml") is not { } doc)
        {
            return;
        }

        Assert.Equal("MORE", doc.Title);
        Assert.Equal(["K/DA"], doc.Artists);
        Assert.True(doc.HasTransliterations);
        Assert.False(doc.HasTranslations);
    }

    [Fact]
    public void Files_without_metadata_have_no_title_but_keep_songwriters()
    {
        if (Load("Compass - mili(1).ttml") is not { } doc)
        {
            return;
        }

        Assert.Null(doc.Title);
        Assert.Empty(doc.Artists);
        Assert.Equal(["Cassie Wei", "Yamato Kasai"], doc.Songwriters);
    }

    [Fact]
    public void Empty_translations_element_does_not_count()
    {
        if (Load("PUSH UR T3MPRR - femtanyl.ttml") is not { } doc)
        {
            return;
        }

        Assert.False(doc.HasTranslations);
        Assert.Equal("en", doc.Language);
    }
}
