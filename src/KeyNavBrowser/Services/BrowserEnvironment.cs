using Microsoft.Web.WebView2.Core;

namespace KeyNavBrowser.Services;

/// <summary>
/// Единая среда WebView2 для всех вкладок: общий профиль на диске и, как следствие,
/// общий процесс браузера, кэш и авторизация.
/// </summary>
public static class BrowserEnvironment
{
    private static Task<CoreWebView2Environment>? _shared;

    /// <summary>
    /// Общая среда создаётся один раз на всё приложение. Задача кэшируется
    /// до успешного завершения: если создание провалилось, кэш сбрасывается,
    /// чтобы следующая попытка могла повторить инициализацию.
    /// </summary>
    public static Task<CoreWebView2Environment> SharedAsync()
    {
        if (_shared is not null)
            return _shared;

        Task<CoreWebView2Environment> task = CreateAsync();
        _shared = task;
        return task;
    }

    private static async Task<CoreWebView2Environment> CreateAsync()
    {
        string profile = string.Empty;

        try
        {
            AppPaths.EnsureRootExists();
            profile = AppPaths.WebView2Profile;

            return await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: profile);
        }
        catch (Exception ex)
        {
            _shared = null;

            throw new InvalidOperationException(
                "Не удалось создать среду WebView2" +
                (string.IsNullOrEmpty(profile) ? string.Empty : $" (каталог профиля: {profile})") +
                ".\n\nПроверьте, что установлен Microsoft Edge WebView2 Runtime, " +
                "и что на папку профиля есть права на чтение и запись.",
                ex);
        }
    }

    /// <summary>Сбрасывает общую среду — используется при неудачной инициализации.</summary>
    public static void Reset() => _shared = null;
}

/// <summary>
/// Действия горячих клавиш, которые обрабатываются главным окном.
/// Вкладка сообщает о них из CoreWebView2Controller.AcceleratorKeyPressed,
/// когда фокус находится внутри веб-страницы.
/// </summary>
public enum HotkeyAction
{
    NewTab,
    CloseTab,
    FocusAddressBar,
    Reload,
    HardReload,
    NextTab,
    PreviousTab,
    ToggleBookmark,
    ShowHistory,
    ShowBookmarks,
    ShowSettings,
    ToggleFind,
    ToggleFullScreen,
}