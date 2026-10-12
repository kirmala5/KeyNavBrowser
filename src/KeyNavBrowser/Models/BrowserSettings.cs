namespace KeyNavBrowser.Models;

/// <summary>
/// Тема оформления интерфейса (п. 3.2 ТЗ — «Настройки: … тема»).
/// </summary>
public static class ThemeNames
{
    public const string Light = "Light";
    public const string Dark = "Dark";
    public const string System = "System";

    public static IReadOnlyList<string> All { get; } = new[] { Light, Dark, System };
}

/// <summary>
/// Доступные поисковые системы (используются адресной строкой, когда ввод не является URL).
/// </summary>
public static class SearchEngines
{
    public const string Yandex = "Yandex";
    public const string Google = "Google";
    public const string Bing = "Bing";
    public const string DuckDuckGo = "DuckDuckGo";

    private static readonly Dictionary<string, string> Templates = new(StringComparer.OrdinalIgnoreCase)
    {
        [Yandex] = "https://yandex.ru/search/?text={0}",
        [Google] = "https://www.google.com/search?q={0}",
        [Bing] = "https://www.bing.com/search?q={0}",
        [DuckDuckGo] = "https://duckduckgo.com/?q={0}",
    };

    public static IReadOnlyList<string> All { get; } = new[] { Yandex, Google, Bing, DuckDuckGo };

    public static string BuildSearchUrl(string engine, string query)
    {
        if (!Templates.TryGetValue(engine, out var template))
            template = Templates[Yandex];

        return string.Format(template, Uri.EscapeDataString(query));
    }
}

/// <summary>
/// Режимы отображения контента (ТЗ: «Реализовать три режима отображения контента»).
///
/// Сами CSS-правила скрытия живут рядом с остальной разметкой во внедряемом скрипте
/// (<see cref="Services.KeyboardNavigationScript"/>); здесь только название режима
/// и текст для интерфейса.
/// </summary>
public static class ContentModes
{
    /// <summary>Скрыты изображения, видео, iframe, canvas и svg.</summary>
    public const string TextOnly = "TextOnly";

    /// <summary>Скрыты только видео и iframe.</summary>
    public const string TextAndImages = "TextAndImages";

    /// <summary>Скрыты изображения, видео остаётся доступным.</summary>
    public const string TextAndVideo = "TextAndVideo";

    public static IReadOnlyList<string> All { get; } = new[] { TextOnly, TextAndImages, TextAndVideo };

    public static bool IsValid(string? mode) =>
        mode is TextOnly or TextAndImages or TextAndVideo;

    /// <summary>Название режима для ComboBox и подсказок.</summary>
    public static string DisplayName(string? mode) => mode switch
    {
        TextOnly => "Только текст",
        TextAndImages => "Текст + изображения",
        TextAndVideo => "Текст + видео",
        _ => "Текст + изображения",
    };

    /// <summary>Что именно скрывает режим — для всплывающей подсказки.</summary>
    public static string Describe(string? mode) => mode switch
    {
        TextOnly => "Скрыты изображения, видео, iframe, canvas и svg",
        TextAndImages => "Скрыты только видео и iframe",
        TextAndVideo => "Скрыты изображения, видео остаётся",
        _ => string.Empty,
    };

    /// <summary>
    /// Элемент выпадающего списка режимов: значение для настроек и название для UI.
    /// ComboBox привязывается через DisplayMemberPath/SelectedValuePath, поэтому
    /// нужны именно два свойства, а не «голое» имя режима.
    /// </summary>
    public sealed record ContentModeItem(string Value)
    {
        public string Display => ContentModes.DisplayName(Value);

        public string Description => ContentModes.Describe(Value);

        /// <summary>
        /// WPF берёт имя элемента списка из ToString(), поэтому без этого
        /// переопределения ComboBox показывал бы «ContentModeItem { Value = … }»
        /// — и в подсказке, и в Accessibility Insights.
        /// </summary>
        public override string ToString() => Display;

        public static IReadOnlyList<ContentModeItem> All { get; } =
            ContentModes.All.Select(value => new ContentModeItem(value)).ToArray();
    }
}

/// <summary>
/// Пользовательские настройки браузера. Сохраняются в JSON рядом с остальными данными.
/// </summary>
public sealed class BrowserSettings
{
    /// <summary>Домашняя страница, открываемая кнопкой «Домой» и при запуске.</summary>
    public string HomePage { get; set; } = "https://ru.wikipedia.org/wiki/Клавиатура";

    /// <summary>Поисковая система по умолчанию для адресной строки.</summary>
    public string SearchEngine { get; set; } = SearchEngines.Yandex;

    /// <summary>Тема оформления: см. <see cref="ThemeNames"/>.</summary>
    public string Theme { get; set; } = ThemeNames.Light;

    /// <summary>
    /// Пункт 4.3 ТЗ: «Пользователь может включить/выключить клавиатурную навигацию
    /// в настройках (по умолчанию включена)».
    /// </summary>
    public bool KeyboardNavigationEnabled { get; set; } = true;

    /// <summary>
    /// Пункт 4.3 ТЗ: шаг прокрутки W/S/A/D в пикселях (допустимый диапазон 50–100).
    /// </summary>
    public int ScrollStep { get; set; } = 80;

    /// <summary>Сколько минут не обращаться к диску при записи истории.</summary>
    public int HistoryMaxEntries { get; set; } = 3000;

    /// <summary>
    /// Открывать ссылки с target="_blank" (в том числе из window.open) в новой вкладке.
    /// По умолчанию false — по ТЗ все ссылки открываются в текущем окне.
    /// </summary>
    public bool TargetBlankInNewTab { get; set; }

    /// <summary>
    /// Режим отображения контента (ТЗ: три режима). По умолчанию — «Текст + изображения»:
    /// это наименее ограничивающий из трёх режимов, он скрывает только видео и iframe.
    /// </summary>
    public string ContentMode { get; set; } = ContentModes.TextAndImages;

    /// <summary>
    /// Показывать порядковый номер поверх каждой видимой ссылки (ТЗ: «Реализовать
    /// нумерацию ссылок»). Номера 1–9 соответствуют клавишам быстрого перехода.
    /// </summary>
    public bool ShowLinkNumbers { get; set; } = true;

    /// <summary>Приводит модель к корректным значениям (например, после правки JSON вручную).</summary>
    public void Normalize()
    {
        HomePage = string.IsNullOrWhiteSpace(HomePage) ? "https://example.com" : HomePage.Trim();
        SearchEngine = SearchEngines.All.Contains(SearchEngine) ? SearchEngine : SearchEngines.Yandex;
        Theme = ThemeNames.All.Contains(Theme) ? Theme : ThemeNames.Light;
        ScrollStep = Math.Clamp(ScrollStep, 50, 100);
        HistoryMaxEntries = Math.Clamp(HistoryMaxEntries, 100, 50_000);
        ContentMode = ContentModes.IsValid(ContentMode) ? ContentMode : ContentModes.TextAndImages;
    }

    public BrowserSettings Clone() => (BrowserSettings)MemberwiseClone();
}