namespace KeyNavBrowser.Services;

/// <summary>
/// Пути хранения пользовательских данных браузера.
///
/// Каталог профиля WebView2 (cookies, кэш, локальное хранилище) требует прав
/// на запись. Если %APPDATA% недоступен — например, профиль Windows загружен
/// по сети или каталог заблокирован политикой, — Chromium не запустится вовсе.
/// Поэтому путь проверяется реальной записью файла, а при неудаче используется
/// временный каталог, и <see cref="ProfileFallbackReason"/> сообщает об этом
/// пользователю.
/// </summary>
public static class AppPaths
{
    private static readonly Lazy<string> RootDirectory = new(ComputeRoot, isThreadSafe: true);
    private static readonly Lazy<string> ProfileDirectory = new(ComputeProfile, isThreadSafe: true);

    private static string ComputeRoot()
    {
        string root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "KeyNavBrowser");

        if (TryEnsureDirectory(root))
            return root;

        // Даже временная папка может оказаться недоступной (жёсткие ограничения
        // политик). Тогда возвращаем путь «как есть»: операции с файлами сообщат
        // об ошибке сами при обращении.
        string fallback = Path.Combine(Path.GetTempPath(), "KeyNavBrowser");
        TryEnsureDirectory(fallback);
        return fallback;
    }

    private static string ComputeProfile()
    {
        string preferred = Path.Combine(Root, "WebView2Profile");

        if (TryEnsureDirectory(preferred))
            return preferred;

        // Основной каталог недоступен для записи — пробуем одноимённый каталог
        // во временной папке. Кэш переживёт перезапуск только до её очистки,
        // но браузер продолжит работать.
        string fallback = Path.Combine(Path.GetTempPath(), "KeyNavBrowser", "WebView2Profile");

        if (TryEnsureDirectory(fallback))
        {
            ProfileFallbackReason =
                $"Каталог «{preferred}» недоступен для записи. " +
                $"Профиль и кэш размещены во временной папке: {fallback}\n" +
                "Настройки, история и закладки сохраняются как обычно. " +
                "После очистки системной временной папки потребуется повторный вход на сайты.";
            return fallback;
        }

        throw new InvalidOperationException(
            "Не удалось создать каталог профиля браузера ни в профиле пользователя, " +
            $"ни во временной папке. Проверьте права доступа к папке «{Root}».");
    }

    /// <summary>
    /// Создаёт каталог и проверяет, что в него действительно можно записать.
    /// Проверка нужна, потому что каталог может существовать, но быть read-only.
    /// </summary>
    private static bool TryEnsureDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        string probe = Path.Combine(path, $".write-test-{Environment.ProcessId}.tmp");

        try
        {
            Directory.CreateDirectory(path);
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return true;
        }
        catch
        {
            // Любая ошибка доступа ( UnauthorizedAccessException, IOException,
            // а также, например, превышение длины пути ) означает одно и то же:
            // каталог непригоден, вызывающий код перейдёт к запасному пути.
            return false;
        }
        finally
        {
            try
            {
                if (File.Exists(probe))
                    File.Delete(probe);
            }
            catch
            {
                // Временный файл мог остаться из-за антивируса — не критично.
            }
        }
    }

    /// <summary>%APPDATA%\KeyNavBrowser — общая папка для настроек, истории и закладок.</summary>
    public static string Root => RootDirectory.Value;

    /// <summary>Каталог профиля WebView2 (общий для всех обычных вкладок).</summary>
    /// <exception cref="InvalidOperationException">
    /// Каталог не удалось создать ни в профиле пользователя, ни во временной папке.
    /// </exception>
    public static string WebView2Profile => ProfileDirectory.Value;

    /// <summary>
    /// Непустая строка, если профиль пришлось перенести во временную папку.
    /// Показывается пользователю один раз при запуске.
    /// </summary>
    public static string? ProfileFallbackReason { get; private set; }

    public static void EnsureRootExists() => _ = RootDirectory.Value;
}