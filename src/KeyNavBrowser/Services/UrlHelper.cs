using System.Text.RegularExpressions;
using KeyNavBrowser.Models;

namespace KeyNavBrowser.Services;

/// <summary>
/// Разбор строки, введённой в адресную строку (п. 3.1 ТЗ, № 1):
/// если это URL — переходим по нему, иначе выполняем поиск через выбранный поисковик.
/// </summary>
public static partial class UrlHelper
{
    /// <summary>
    /// Схемы, которые WebView2 действительно открывает. Остальные (javascript:,
    /// magnet:, tel:, ftp: и т.п.) приводят к пустой странице с ошибкой Chromium,
    /// поэтому такие адреса отсекаются заранее — см. <see cref="IsSupported"/>.
    /// </summary>
    private static readonly HashSet<string> SupportedSchemes = new(StringComparer.OrdinalIgnoreCase)
    {
        Uri.UriSchemeHttp,
        Uri.UriSchemeHttps,
        Uri.UriSchemeFile,
    };

    [GeneratedRegex(@"^[a-zA-Z][a-zA-Z0-9+.\-]*://", RegexOptions.CultureInvariant)]
    private static partial Regex SchemeRegex();

    // «example.com», «ru.wikipedia.org/wiki/Клавиатура», «localhost:8080/x» и т.п.
    [GeneratedRegex(@"^(?:localhost|[\w\-]+(?:\.[\w\-]+)+)(?::\d+)?(?:[/?#].*)?$",
                     RegexOptions.CultureInvariant)]
    private static partial Regex HostLikeRegex();

    /// <summary>
    /// Превращает пользовательский ввод в абсолютный URL либо в URL поискового запроса.
    /// </summary>
    public static string Resolve(string input, BrowserSettings settings)
    {
        string text = (input ?? string.Empty).Trim();
        if (text.Length == 0)
            return settings.HomePage;

        // Явная схема — считаем URL как есть.
        if (SchemeRegex().IsMatch(text))
        {
            if (Uri.TryCreate(text, UriKind.Absolute, out var explicitUri))
                return explicitUri.AbsoluteUri;
        }

        // Похоже на домен без схемы — добавляем https://
        if (HostLikeRegex().IsMatch(text))
        {
            string candidate = "https://" + text;
            if (Uri.TryCreate(candidate, UriKind.Absolute, out var hostUri))
                return hostUri.AbsoluteUri;
        }

        // Иначе — поисковый запрос.
        return SearchEngines.BuildSearchUrl(settings.SearchEngine, text);
    }

    /// <summary>Приводит ссылку с страницы к абсолютному виду (учитывая адрес текущего документа).</summary>
    public static string? ToAbsolute(string href, string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(href))
            return null;

        href = href.Trim();

        if (Uri.TryCreate(href, UriKind.Absolute, out var absolute))
            return absolute.AbsoluteUri;

        if (string.IsNullOrWhiteSpace(baseUrl))
            return null;

        if (Uri.TryCreate(new Uri(baseUrl), href, out var combined))
            return combined.AbsoluteUri;

        return null;
    }

    /// <summary>Короткое имя хоста для отображения в подсказках и истории.</summary>
    public static string? GetHost(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : null;

    /// <summary>
    /// Проверяет, что адрес вообще можно открыть: это корректный абсолютный URI
    /// со схемой http, https или file. Всё прочее — опечатка или неподдерживаемая
    /// схема, и вместо пустой страницы Chromium показывается понятное сообщение.
    /// </summary>
    public static bool IsSupported(string? url) =>
        !string.IsNullOrWhiteSpace(url)
        && Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && SupportedSchemes.Contains(uri.Scheme);

    /// <summary>Схема адреса для сообщения об ошибке (пустая строка, если схемы нет).</summary>
    public static string GetScheme(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Scheme : string.Empty;
}