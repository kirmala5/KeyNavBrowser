namespace KeyNavBrowser.Models;

/// <summary>Одна запись в истории посещений.</summary>
public sealed class HistoryEntry
{
    public string Url { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public DateTimeOffset VisitedAt { get; set; } = DateTimeOffset.Now;

    public int VisitCount { get; set; } = 1;

    public string DisplayTitle =>
        string.IsNullOrWhiteSpace(Title) ? Url : Title;
}

/// <summary>Одна закладка.</summary>
public sealed class BookmarkItem
{
    public string Url { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    public string DisplayTitle =>
        string.IsNullOrWhiteSpace(Title) ? Url : Title;
}