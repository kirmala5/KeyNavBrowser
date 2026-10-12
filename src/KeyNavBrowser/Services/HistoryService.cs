using System.Collections.ObjectModel;
using KeyNavBrowser.Models;

namespace KeyNavBrowser.Services;

/// <summary>Хранилище истории посещённых страниц (п. 3.1 ТЗ, № 5).</summary>
public sealed class HistoryService
{
    private readonly JsonStore<List<HistoryEntry>> _store = new("history.json", () => new List<HistoryEntry>());
    private readonly object _gate = new();

    private List<HistoryEntry> _entries = new();
    private Timer? _saveTimer;

    public ObservableCollection<HistoryEntry> Entries { get; } = new();

    public void Load()
    {
        _entries = _store.Load();
        _entries.RemoveAll(e => string.IsNullOrWhiteSpace(e.Url));
        Entries.Clear();
        foreach (var entry in _entries.OrderByDescending(e => e.VisitedAt))
            Entries.Add(entry);
    }

    /// <summary>
    /// Регистрирует посещение страницы. Повторный переход на тот же URL
    /// не создаёт дубликат, а увеличивает счётчик и поднимает запись вверх списка.
    /// </summary>
    public void Record(string url, string title)
    {
        if (string.IsNullOrWhiteSpace(url))
            return;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return;

        if (uri.Scheme is not ("http" or "https" or "file"))
            return;

        string normalized = uri.AbsoluteUri;

        lock (_gate)
        {
            var existing = _entries.FirstOrDefault(e =>
                string.Equals(e.Url, normalized, StringComparison.OrdinalIgnoreCase));

            if (existing is not null)
            {
                existing.VisitCount++;
                existing.VisitedAt = DateTimeOffset.Now;
                if (!string.IsNullOrWhiteSpace(title))
                    existing.Title = title;
            }
            else
            {
                _entries.Add(new HistoryEntry
                {
                    Url = normalized,
                    Title = title ?? string.Empty,
                    VisitedAt = DateTimeOffset.Now,
                });
            }

            TrimToLimit();
            ScheduleSave();
        }

        RefreshView();
    }

    public void Remove(HistoryEntry entry)
    {
        lock (_gate)
        {
            _entries.Remove(entry);
            ScheduleSave();
        }

        Entries.Remove(entry);
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            ScheduleSave();
        }

        Entries.Clear();
    }

    public IEnumerable<HistoryEntry> Search(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Entries;

        string needle = query.Trim();
        return Entries.Where(e =>
            e.Url.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
            e.DisplayTitle.Contains(needle, StringComparison.OrdinalIgnoreCase));
    }

    private void TrimToLimit()
    {
        int limit = App.Settings?.Current.HistoryMaxEntries ?? 3000;
        if (_entries.Count <= limit)
            return;

        _entries = _entries
            .OrderByDescending(e => e.VisitedAt)
            .Take(limit)
            .ToList();
    }

    private void RefreshView()
    {
        var ordered = _entries.OrderByDescending(e => e.VisitedAt).ToList();

        Entries.Clear();
        foreach (var entry in ordered)
            Entries.Add(entry);
    }

    private void ScheduleSave()
    {
        _saveTimer?.Dispose();
        _saveTimer = new Timer(_ =>
        {
            Flush();
        }, null, TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan);
    }

    public void Flush()
    {
        _saveTimer?.Dispose();
        _saveTimer = null;

        lock (_gate)
            _store.Save(_entries.ToList());
    }
}

/// <summary>Хранилище закладок (п. 3.1 ТЗ, № 6).</summary>
public sealed class BookmarkService
{
    private readonly JsonStore<List<BookmarkItem>> _store = new("bookmarks.json", () => new List<BookmarkItem>());

    public ObservableCollection<BookmarkItem> Items { get; } = new();

    public void Load()
    {
        var loaded = _store.Load().Where(b => !string.IsNullOrWhiteSpace(b.Url)).ToList();
        Items.Clear();
        foreach (var item in loaded)
            Items.Add(item);
    }

    public bool Contains(string url) =>
        Items.Any(b => string.Equals(b.Url, url, StringComparison.OrdinalIgnoreCase));

    public bool Toggle(string url, string title)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;

        var existing = Items.FirstOrDefault(b =>
            string.Equals(b.Url, url, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            Items.Remove(existing);
            Save();
            return false;
        }

        Items.Insert(0, new BookmarkItem
        {
            Url = url,
            Title = string.IsNullOrWhiteSpace(title) ? url : title,
            CreatedAt = DateTimeOffset.Now,
        });

        Save();
        return true;
    }

    public void Remove(BookmarkItem item)
    {
        Items.Remove(item);
        Save();
    }

    public void Save() => _store.Save(Items.ToList());
}