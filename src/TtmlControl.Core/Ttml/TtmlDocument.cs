namespace TtmlControl.Core.Ttml;

public enum TtmlTiming
{
    Unknown,
    Line,
    Word,
}

public sealed record TtmlLine(
    string? Key,
    TimeSpan? Begin,
    TimeSpan? End,
    string? Agent,
    string Text,
    string BackgroundText,
    string TimingSignature);

public sealed record TtmlDocument(
    string? Title,
    IReadOnlyList<string> Artists,
    string? Album,
    string? Isrc,
    string? CoverArtUrl,
    string? Language,
    IReadOnlyList<string> Songwriters,
    TtmlTiming Timing,
    TimeSpan? Duration,
    int AgentCount,
    bool HasBackgroundVocals,
    bool HasTransliterations,
    bool HasTranslations,
    IReadOnlyList<TtmlLine> Lines,
    string LyricsFingerprint);

public sealed class TtmlParseException(string message, Exception? innerException = null)
    : Exception(message, innerException);
