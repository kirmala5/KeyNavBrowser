using KeyNavBrowser.Models;

namespace KeyNavBrowser.Services;

/// <summary>Хранилище настроек браузера (п. 3.2 ТЗ).</summary>
public sealed class SettingsService
{
    private readonly JsonStore<BrowserSettings> _store = new("settings.json", () => new BrowserSettings());

    private BrowserSettings _current = new();

    public BrowserSettings Current => _current;

    public void Load()
    {
        _current = _store.Load();
        _current.Normalize();
    }

    public void Save()
    {
        _current.Normalize();
        _store.Save(_current);
    }

    /// <summary>Применяет новые настройки, сохраняет их и переуведомляет подписчиков.</summary>
    public void Update(Action<BrowserSettings> mutate)
    {
        var draft = _current.Clone();
        mutate(draft);
        draft.Normalize();
        _current = draft;

        Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? Changed;
}

/// <summary>
/// Меняет объединённый словарь ресурсов приложения на нужную тему.
/// Все элементы интерфейса используют DynamicResource, поэтому переключение
/// происходит без пересоздания окна.
/// </summary>
public static class ThemeManager
{
    public static void Apply(string themeName)
    {
        string resolved = Resolve(themeName);
        var uri = new Uri($"Themes/{resolved}.xaml", UriKind.Relative);

        var app = Application.Current;
        if (app is null)
            return;

        var merged = new ResourceDictionary { Source = uri };

        // Заменяем только словарь темы, сохраняя пользовательские ресурсы.
        var dictionaries = app.Resources.MergedDictionaries;
        var existing = dictionaries.FirstOrDefault(d =>
            d.Source?.OriginalString.Contains("Themes/", StringComparison.OrdinalIgnoreCase) == true);

        if (existing is not null)
        {
            int index = dictionaries.IndexOf(existing);
            dictionaries[index] = merged;
        }
        else
        {
            dictionaries.Add(merged);
        }
    }

    public static string Resolve(string themeName)
    {
        if (string.Equals(themeName, ThemeNames.System, StringComparison.OrdinalIgnoreCase))
        {
            return IsSystemDark() ? ThemeNames.Dark : ThemeNames.Light;
        }

        return string.Equals(themeName, ThemeNames.Dark, StringComparison.OrdinalIgnoreCase)
            ? ThemeNames.Dark
            : ThemeNames.Light;
    }

    public static bool IsSystemDark()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");

            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch
        {
            return false;
        }
    }
}