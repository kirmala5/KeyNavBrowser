using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using KeyNavBrowser.Services;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace KeyNavBrowser.Models;

/// <summary>
/// Одна вкладка браузера: обёртка над WebView2 вместе с состоянием навигации,
/// подключением клавиатурной навигации и записью в историю.
/// </summary>
public sealed class TabModel : INotifyPropertyChanged, IDisposable
{
    // Виртуальные коды клавиш из п. 4 ТЗ.
    private const uint VK_W = 0x57;
    private const uint VK_A = 0x41;
    private const uint VK_S = 0x53;
    private const uint VK_D = 0x44;
    private const uint VK_Q = 0x51;
    private const uint VK_E = 0x45;
    private const uint VK_SPACE = 0x20;
    private const uint VK_RETURN = 0x0D;
    private const uint VK_ESCAPE = 0x1B;

    // Клавиши 1–9 для перехода по номеру ссылки (VK_1..VK_9).
    private const uint VK_1 = 0x31;
    private const uint VK_NUMPAD_1 = 0x61;

    // Коды клавиш-модификаторов (CoreWebView2AcceleratorKeyPressedEventArgs
    // не отдаёт состояние модификаторов напрямую, поэтому отслеживаем сами).
    private const uint VK_SHIFT = 0x10;
    private const uint VK_CONTROL = 0x11;
    private const uint VK_MENU = 0x12;
    private const uint VK_LWIN = 0x5B;
    private const uint VK_RWIN = 0x5C;
    private const uint VK_F5 = 0x74;
    private const uint VK_T = 0x54;
    private const uint VK_W_GLOBAL = 0x57;   // совпадает с VK_W, используется в сочетаниях
    private const uint VK_L = 0x4C;
    private const uint VK_B = 0x42;
    private const uint VK_H = 0x48;
    private const uint VK_O = 0x4F;
    private const uint VK_R = 0x52;
    private const uint VK_OEM_PLUS = 0xBB;
    private const uint VK_OEM_MINUS = 0xBD;

    private readonly SettingsService _settings;
    private readonly HistoryService _history;
    private readonly BookmarkService _bookmarks;

    private string _url = string.Empty;
    private string _title = "Новая вкладка";
    private string _statusText = string.Empty;
    private bool _canGoBack;
    private bool _canGoForward;
    private bool _isLoading;
    private bool _isActive;
    private bool _isBookmarked;
    private bool _keyboardNavReady;
    private double _progress;

    /// <summary>Кэшированное состояние «фокус в поле ввода», полученное от скрипта.</summary>
    private bool _pageHasEditableFocus;

    private ModifierKeys _modifiers = ModifierKeys.None;
    private string? _pendingUrl;
    private CoreWebView2Controller? _controller;
    private bool _disposed;

    public TabModel(SettingsService settings, HistoryService history, BookmarkService bookmarks)
    {
        _settings = settings;
        _history = history;
        _bookmarks = bookmarks;

        View = new WebView2
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
    }

    /// <summary>Элемент WebView2, который добавляется в область содержимого окна.</summary>
    public WebView2 View { get; }

    public string Url
    {
        get => _url;
        private set => Set(ref _url, value);
    }

    public string Title
    {
        get => _title;
        private set => Set(ref _title, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    public bool CanGoBack
    {
        get => _canGoBack;
        private set => Set(ref _canGoBack, value);
    }

    public bool CanGoForward
    {
        get => _canGoForward;
        private set => Set(ref _canGoForward, value);
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set => Set(ref _isLoading, value);
    }

    public bool IsActive
    {
        get => _isActive;
        set => Set(ref _isActive, value);
    }

    public bool IsBookmarked
    {
        get => _isBookmarked;
        private set => Set(ref _isBookmarked, value);
    }

    /// <summary>0–100; значение 0 во время загрузки означает неопределённый прогресс.</summary>
    public double Progress
    {
        get => _progress;
        private set => Set(ref _progress, value);
    }

    public double ZoomFactor
    {
        get => View.ZoomFactor;
        set
        {
            View.ZoomFactor = Math.Clamp(value, 0.25, 2.0);
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Признак того, что клавиатурная навигация активна и скрипт установлен на странице.
    /// </summary>
    public bool IsKeyboardNavigationActive => _keyboardNavReady && _settings.Current.KeyboardNavigationEnabled;

    // ---------------- события для главного окна ----------------

    /// <summary>Изменилось состояние навигации (кнопки «Назад»/«Вперёд», заголовок, прогресс).</summary>
    public event EventHandler? NavigationStateChanged;

    /// <summary>Страница попросила открыть ссылку в новой вкладке (target="_blank").</summary>
    public event EventHandler<string>? OpenUrlInNewTabRequested;

    public event EventHandler<HotkeyAction>? HotkeyRequested;

    public event PropertyChangedEventHandler? PropertyChanged;

    // ---------------- жизненный цикл ----------------

    /// <summary>
    /// Инициализирует WebView2. Вызывается после того, как элемент добавлен в дерево WPF.
    /// </summary>
    public async Task InitializeAsync(string startUrl)
    {
        var environment = await BrowserEnvironment.SharedAsync();
        await View.EnsureCoreWebView2Async(environment);

        var core = View.CoreWebView2
            ?? throw new InvalidOperationException("WebView2 не удалось инициализировать.");

        core.Settings.AreDefaultContextMenusEnabled = true;
        core.Settings.AreDevToolsEnabled = true;
        core.Settings.IsWebMessageEnabled = true;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;

        core.NavigationStarting += OnNavigationStarting;
        core.NavigationCompleted += OnNavigationCompleted;
        core.DocumentTitleChanged += OnDocumentTitleChanged;
        core.ContentLoading += OnContentLoading;
        core.SourceChanged += OnSourceChanged;
        core.NewWindowRequested += OnNewWindowRequested;
        core.DownloadStarting += OnDownloadStarting;
        core.WebMessageReceived += OnWebMessageReceived;
        core.ProcessFailed += OnProcessFailed;

        View.ZoomFactorChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(ZoomFactor));
            RaiseNavigationStateChanged();
        };

        AttachController();

        _isBookmarked = !string.IsNullOrEmpty(Url) && _bookmarks.Contains(Url);

        string target = _pendingUrl ?? startUrl;
        _pendingUrl = null;

        if (!string.IsNullOrWhiteSpace(target))
            Navigate(target);
    }

    /// <summary>
    /// Подписка на CoreWebView2Controller.AcceleratorKeyPressed — механизм, указанный
    /// в п. 5 ТЗ. Через него обрабатываются горячие клавиши браузера, когда фокус
    /// находится внутри страницы, а также клавиши навигации в качестве резерва,
    /// если внедрённый скрипт ещё не готов.
    /// </summary>
    private void AttachController()
    {
        _controller = WebView2ControllerAccessor.TryGetController(View);

        if (_controller is null)
            return;

        _controller.AcceleratorKeyPressed += OnAcceleratorKeyPressed;
    }

    // ---------------- навигация (п. 3.1 ТЗ, № 3) ----------------

    /// <summary>
    /// Переход по строке, введённой пользователем: URL — как URL, иначе — поисковый запрос.
    /// </summary>
    public void NavigateInput(string input)
    {
        string url = UrlHelper.Resolve(input, _settings.Current);
        Navigate(url);
    }

    public void Navigate(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            // Не похоже на URL — считаем поисковым запросом.
            NavigateInput(url);
            return;
        }

        if (!UrlHelper.IsSupported(uri.AbsoluteUri))
        {
            string scheme = UrlHelper.GetScheme(uri.AbsoluteUri);
            ShowInternalPage(
                "Неподдерживаемый адрес",
                string.IsNullOrEmpty(scheme)
                    ? $"Не удалось разобрать адрес:\n\n{uri.OriginalString}"
                    : $"Схема «{scheme}:» не поддерживается браузером.\n\n{uri.AbsoluteUri}");

            return;
        }

        if (View.CoreWebView2 is null)
        {
            // WebView2 ещё инициализируется — переход выполнит InitializeAsync.
            _pendingUrl = uri.AbsoluteUri;
            return;
        }

        try
        {
            View.Source = uri;
        }
        catch (Exception ex)
        {
            ShowInternalPage("Не удалось открыть адрес", ex.Message);
        }
    }

    public void GoBack()
    {
        if (View.CoreWebView2?.CanGoBack == true)
            View.CoreWebView2.GoBack();
    }

    public void GoForward()
    {
        if (View.CoreWebView2?.CanGoForward == true)
            View.CoreWebView2.GoForward();
    }

    public void Reload(bool hard = false)
    {
        var core = View.CoreWebView2;
        if (core is null)
            return;

        string current = core.Source ?? Url;
        if (string.IsNullOrWhiteSpace(current))
            return;

        if (hard)
            core.Navigate(current);
        else
            core.Reload();
    }

    public void Stop() => View.CoreWebView2?.Stop();

    public void GoHome() => Navigate(_settings.Current.HomePage);

    // ---------------- обработчики WebView2 ----------------

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        IsLoading = true;
        Progress = 0;
        Url = e.Uri ?? string.Empty;
        _pageHasEditableFocus = false;
        _keyboardNavReady = false;
        StatusText = "Загрузка…";

        if (!e.IsRedirected)
            _recordVisit = true;

        RaiseNavigationStateChanged();
    }

    private bool _recordVisit;

    private void OnContentLoading(object? sender, CoreWebView2ContentLoadingEventArgs e)
    {
        Progress = 0;
        IsBookmarked = !string.IsNullOrEmpty(Url) && _bookmarks.Contains(Url);
    }

    private void OnSourceChanged(object? sender, CoreWebView2SourceChangedEventArgs e)
    {
        if (View.CoreWebView2 is { } core)
        {
            CanGoBack = core.CanGoBack;
            CanGoForward = core.CanGoForward;
        }

        RaiseNavigationStateChanged();
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        IsLoading = false;
        Progress = 100;

        var core = View.CoreWebView2;
        if (core is not null)
        {
            CanGoBack = core.CanGoBack;
            CanGoForward = core.CanGoForward;
            Url = core.Source ?? Url;

            if (string.IsNullOrWhiteSpace(core.DocumentTitle))
                Title = UrlHelper.GetHost(Url) ?? "Новая вкладка";

            if (e.IsSuccess && _recordVisit)
                _history.Record(Url, core.DocumentTitle);

            _recordVisit = false;
        }

        IsBookmarked = !string.IsNullOrEmpty(Url) && _bookmarks.Contains(Url);
        StatusText = DescribeKeyboardHelp();

        // Внедряем обработчик клавиатурной навигации на каждой загруженной странице.
        _ = InjectKeyboardNavigationAsync();

        RaiseNavigationStateChanged();
    }

    private void OnDocumentTitleChanged(object? sender, object e)
    {
        string title = View.CoreWebView2?.DocumentTitle ?? string.Empty;

        if (string.IsNullOrWhiteSpace(title))
            title = UrlHelper.GetHost(Url) ?? Url;

        Title = title;
        RaiseNavigationStateChanged();
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        /* Сюда попадают и ссылки с target="_blank", и вызовы window.open().
           e.Handled ставим всегда: иначе Chromium откроет отдельное окно,
           а требование — открывать всё в текущем окне. */
        e.Handled = true;

        if (string.IsNullOrWhiteSpace(e.Uri))
            return;

        if (e.Uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
            return;

        if (_settings.Current.TargetBlankInNewTab)
            OpenUrlInNewTabRequested?.Invoke(this, e.Uri);
        else
            Navigate(e.Uri);
    }

    private void OnDownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
    {
        var operation = e.DownloadOperation;
        if (operation is null)
            return;

        string fileName = GetFileNameFromUri(operation.Uri);

        if (string.IsNullOrWhiteSpace(e.ResultFilePath))
            e.ResultFilePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Downloads",
                fileName);

        Progress = 0;
        IsLoading = true;
        StatusText = $"Загрузка файла: {fileName}";

        operation.StateChanged += (_, _) => ReportDownloadState(operation, fileName);
    }

    private void ReportDownloadState(CoreWebView2DownloadOperation operation, string fileName)
    {
        void Report()
        {
            ulong? total = operation.TotalBytesToReceive;

            if (total is > 0)
                Progress = Math.Clamp(100.0 * operation.BytesReceived / total.Value, 0, 100);

            switch (operation.State)
            {
                case CoreWebView2DownloadState.InProgress:
                    StatusText = total is > 0
                        ? $"Загрузка {fileName}: {FormatBytes(operation.BytesReceived)} из {FormatBytes(total.Value)}"
                        : $"Загрузка {fileName}: {FormatBytes(operation.BytesReceived)}";
                    break;

                case CoreWebView2DownloadState.Completed:
                    IsLoading = false;
                    Progress = 100;
                    StatusText = $"Файл сохранён: {fileName}";
                    break;

                case CoreWebView2DownloadState.Interrupted:
                    IsLoading = false;
                    Progress = 0;
                    StatusText = $"Загрузка прервана: {fileName}";
                    break;
            }
        }

        _ = View.Dispatcher.BeginInvoke(Report);
    }

    private static string GetFileNameFromUri(string? uri)
    {
        if (Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
        {
            string candidate = Path.GetFileName(parsed.LocalPath);
            if (!string.IsNullOrWhiteSpace(candidate))
                return Uri.EscapeDataString(candidate);
        }

        return "download";
    }

    private static string FormatBytes(double bytes)
    {
        string[] units = { "Б", "КБ", "МБ", "ГБ" };
        double value = bytes;
        int unit = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.#} {units[unit]}";
    }

    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        IsLoading = false;
        Progress = 0;
        StatusText = "Процесс браузера завершён аварийно.";
        RaiseNavigationStateChanged();
    }

    // ---------------- мост скрипт <-> C# ----------------

    private async Task InjectKeyboardNavigationAsync()
    {
        if (View.CoreWebView2 is null)
            return;

        await RunJavaScriptAsync(KeyboardNavigationScript.Build(_settings.Current));

        _keyboardNavReady = true;
        _pageHasEditableFocus = false;
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var document = JsonDocument.Parse(e.TryGetWebMessageAsString());
            var root = document.RootElement;

            if (!root.TryGetProperty("type", out var typeElement))
                return;

            switch (typeElement.GetString())
            {
                case "focus":
                    // П. 4.3: пока фокус в поле ввода, W/A/S/D/Q/E должны печатать символы.
                    _pageHasEditableFocus =
                        root.TryGetProperty("editable", out var editable) && editable.GetBoolean();
                    break;

                case "ready":
                    _keyboardNavReady = true;
                    StatusText = DescribeKeyboardHelp();
                    break;

                case "activate":
                    HandleActivateMessage(root);
                    break;
            }
        }
        catch
        {
            // Сообщения, не являющиеся JSON объектом нашего скрипта, игнорируем.
        }
    }

    private void HandleActivateMessage(JsonElement root)
    {
        if (!root.TryGetProperty("url", out var urlElement))
            return;

        string url = urlElement.GetString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(url))
            return;

        bool newTab = root.TryGetProperty("newTab", out var flag) && flag.GetBoolean();

        // По ТЗ Enter открывает ссылку в текущей вкладке; target="_blank" переносим в новую.
        if (newTab && _settings.Current.TargetBlankInNewTab)
            OpenUrlInNewTabRequested?.Invoke(this, url);
        else
            Navigate(url);
    }

    // ---------------- горячие клавиши ----------------

    /// <summary>
    /// Событие CoreWebView2Controller.AcceleratorKeyPressed: срабатывает, когда фокус
    /// находится внутри веб-страницы. Здесь обрабатываются горячие клавиши браузера;
    /// клавиши навигации обрабатывает внедрённый в страницу скрипт (он же следит за
    /// тем, чтобы фокус находился в поле ввода), а этот обработчик используется как
    /// резерв, если скрипт ещё не внедрён.
    /// </summary>
    private void OnAcceleratorKeyPressed(object? sender, CoreWebView2AcceleratorKeyPressedEventArgs e)
    {
        bool keyDown = e.KeyEventKind == CoreWebView2KeyEventKind.KeyDown
                       || e.KeyEventKind == CoreWebView2KeyEventKind.SystemKeyDown;

        if (!keyDown)
            return;

        uint key = e.VirtualKey;
        UpdateModifierState(key, down: true);

        if (IsControlDown && !IsShiftDown)
        {
            switch (key)
            {
                case VK_T:
                    Raise(HotkeyAction.NewTab);
                    e.Handled = true;
                    return;
                case VK_W_GLOBAL:
                    Raise(HotkeyAction.CloseTab);
                    e.Handled = true;
                    return;
                case VK_L:
                    Raise(HotkeyAction.FocusAddressBar);
                    e.Handled = true;
                    return;
                case VK_B:
                    Raise(HotkeyAction.ToggleBookmark);
                    e.Handled = true;
                    return;
                case VK_H:
                    Raise(HotkeyAction.ShowHistory);
                    e.Handled = true;
                    return;
                case VK_O:
                    Raise(HotkeyAction.ShowBookmarks);
                    e.Handled = true;
                    return;
                case VK_R:
                    Raise(IsShiftDown ? HotkeyAction.HardReload : HotkeyAction.Reload);
                    e.Handled = true;
                    return;
                case VK_OEM_MINUS:
                    ZoomOut();
                    e.Handled = true;
                    return;
            }

            if (key is VK_OEM_PLUS or 0x6B /* VK_ADD */)
            {
                ZoomIn();
                e.Handled = true;
                return;
            }

            return;
        }

        if (key == VK_F5)
        {
            Raise(IsShiftDown ? HotkeyAction.HardReload : HotkeyAction.Reload);
            e.Handled = true;
            return;
        }

        if (!IsShiftDown && key == VK_OEM_PLUS)
        {
            ZoomIn();
            e.Handled = true;
            return;
        }

        if (!IsAltDown && !IsShiftDown && !IsControlDown)
            HandleNavigationKey(key, e);
    }

    /// <summary>
    /// Резервная обработка клавиш раздела 4 ТЗ. Основную работу выполняет скрипт
    /// внутри страницы; сюда попадают нажатия только когда скрипт ещё не готов,
    /// поэтому двойного срабатывания не происходит.
    /// </summary>
    private void HandleNavigationKey(uint key, CoreWebView2AcceleratorKeyPressedEventArgs e)
    {
        if (!_settings.Current.KeyboardNavigationEnabled)
            return;

        if (_pageHasEditableFocus)
            return;

        // Скрипт внутри страницы — основной обработчик клавиш навигации.
        if (_keyboardNavReady)
            return;

        switch (key)
        {
            case VK_W: ScrollUp(); e.Handled = true; break;
            case VK_S: ScrollDown(); e.Handled = true; break;
            case VK_A: ScrollLeft(); e.Handled = true; break;
            case VK_D: ScrollRight(); e.Handled = true; break;
            case VK_SPACE: PageDownScreen(); e.Handled = true; break;
            case VK_Q: PreviousLink(); e.Handled = true; break;
            case VK_E: NextLink(); e.Handled = true; break;
            case VK_ESCAPE: ClearLinkHighlight(); e.Handled = true; break;
            case VK_RETURN: ActivateLink(); break;

            // 1–9 — переход по ссылке с соответствующим номером.
            case >= VK_1 and <= VK_1 + 8:
                GoToLinkNumber((int)(key - VK_1) + 1); e.Handled = true; break;
            case >= VK_NUMPAD_1 and <= VK_NUMPAD_1 + 8:
                GoToLinkNumber((int)(key - VK_NUMPAD_1) + 1); e.Handled = true; break;
        }
    }

    private void Raise(HotkeyAction action) => HotkeyRequested?.Invoke(this, action);

    private bool IsControlDown => _modifiers.HasFlag(ModifierKeys.Control);
    private bool IsShiftDown => _modifiers.HasFlag(ModifierKeys.Shift);
    private bool IsAltDown => _modifiers.HasFlag(ModifierKeys.Alt);

    private void UpdateModifierState(uint key, bool down)
    {
        ModifierKeys flag;

        switch (key)
        {
            case VK_SHIFT: flag = ModifierKeys.Shift; break;
            case VK_CONTROL: flag = ModifierKeys.Control; break;
            case VK_MENU: flag = ModifierKeys.Alt; break;
            case VK_LWIN:
            case VK_RWIN: flag = ModifierKeys.Windows; break;
            default: return;
        }

        _modifiers = down ? _modifiers | flag : _modifiers & ~flag;
    }

    // ---------------- команды клавиатурной навигации (раздел 4 ТЗ) ----------------

    public void ScrollUp()
    {
        InvokeNav("__keynav.scrollStep()");
        SetStatus("W — прокрутка вверх");
    }

    public void ScrollDown()
    {
        InvokeNav("__keynav.scrollStepDown()");
        SetStatus("S — прокрутка вниз");
    }

    public void ScrollLeft()
    {
        InvokeNav("__keynav.scrollLeft()");
        SetStatus("A — прокрутка влево");
    }

    public void ScrollRight()
    {
        InvokeNav("__keynav.scrollRight()");
        SetStatus("D — прокрутка вправо");
    }

    public void PageDownScreen()
    {
        InvokeNav("__keynav.pageDown()");
        SetStatus("ПРОБЕЛ — экран вниз");
    }

    public void PreviousLink()
    {
        InvokeNav("__keynav.prev()");
        SetStatus("Q — предыдущая ссылка");
    }

    public void NextLink()
    {
        InvokeNav("__keynav.next()");
        SetStatus("E — следующая ссылка");
    }

    public void ActivateLink() => InvokeNav("__keynav.activate()");

    /// <summary>
    /// Переход по ссылке с указанным номером (1–9) — номера соответствуют подписям,
    /// которые скрипт рисует поверх ссылок (п. 5 ТЗ, «1–9 — переход по ссылке
    /// с соответствующим номером»).
    /// </summary>
    public void GoToLinkNumber(int number)
    {
        if (number is < 1 or > 9)
            return;

        InvokeNav($"__keynav.goto({number})");
        SetStatus($"{number} — переход по ссылке № {number}");
    }

    public void ClearLinkHighlight()
    {
        InvokeNav("__keynav.clear()");
        SetStatus("ESC — выделение снято");
    }

    private void InvokeNav(string script)
    {
        if (!_keyboardNavReady)
            return;

        _ = RunJavaScriptAsync($"if (window.__keynav) window.{script}");
    }

    // ---------------- масштаб ----------------

    public void ZoomIn() => SetZoom(ZoomFactor + 0.1);

    public void ZoomOut() => SetZoom(ZoomFactor - 0.1);

    public void ResetZoom() => SetZoom(1.0);

    private void SetZoom(double value)
    {
        ZoomFactor = value;
        SetStatus($"Масштаб: {ZoomFactor * 100:0}%");
    }

    /// <summary>Переприменяет настройки после изменения панели настроек.</summary>
    public async Task ApplySettingsAsync()
    {
        if (View.CoreWebView2 is null)
            return;

        if (!_settings.Current.KeyboardNavigationEnabled)
            await RunJavaScriptAsync("if (window.__keynav) window.__keynav.clear();");

        await InjectKeyboardNavigationAsync();

        _pageHasEditableFocus = false;
        StatusText = DescribeKeyboardHelp();

        OnPropertyChanged(nameof(IsKeyboardNavigationActive));
        RaiseNavigationStateChanged();
    }

    // ---------------- вспомогательное ----------------

    public async Task RunJavaScriptAsync(string script)
    {
        if (View.CoreWebView2 is null)
            return;

        try
        {
            await View.CoreWebView2.ExecuteScriptAsync(script);
        }
        catch
        {
            // Скрипт во время смены страницы недоступен — это штатная ситуация.
        }
    }

    private void SetStatus(string text) => StatusText = text;

    private string DescribeKeyboardHelp()
    {
        if (!_settings.Current.KeyboardNavigationEnabled)
            return "Клавиатурная навигация отключена (Настройки) · " + (UrlHelper.GetHost(Url) ?? string.Empty);

        return "W/S/A/D — прокрутка · ПРОБЕЛ — экран · Q/E — ссылки · 1–9 — по номеру"
             + " · ENTER — открыть · ESC — сброс · Ctrl+L — адрес";
    }

    /// <summary>Показывает локальную страницу с сообщением об ошибке.</summary>
    public void ShowInternalPage(string title, string message)
    {
        // Стили заданы инлайном: в «сыром» интерполированном литерале нельзя экранировать
        // фигурные скобки удвоением, поэтому блок <style> здесь не используется.
        string html = $"""
        <!DOCTYPE html>
        <html lang="ru">
        <head>
          <meta charset="utf-8">
          <title>{Escape(title)}</title>
        </head>
        <body style="font:15px/1.6 'Segoe UI',system-ui,sans-serif;margin:0;padding:48px;background:#1b1b1f;color:#ececec">
          <h1 style="font-size:20px;margin:0 0 12px;color:#ff8a00">{Escape(title)}</h1>
          <p style="margin:0;color:#a8a8b0;white-space:pre-wrap">{Escape(message)}</p>
        </body>
        </html>
        """;

        View.CoreWebView2?.NavigateToString(html);
        Title = title;
        IsLoading = false;
    }

    private static string Escape(string text) => System.Net.WebUtility.HtmlEncode(text);

    public void ToggleBookmark()
    {
        if (string.IsNullOrWhiteSpace(Url))
            return;

        bool added = _bookmarks.Toggle(Url, Title);
        IsBookmarked = added;
        StatusText = added ? "Закладка добавлена" : "Закладка удалена";
        RaiseNavigationStateChanged();
    }

    private void RaiseNavigationStateChanged() => NavigationStateChanged?.Invoke(this, EventArgs.Empty);

    private void Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;

        field = value;
        OnPropertyChanged(propertyName);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        if (_controller is not null)
            _controller.AcceleratorKeyPressed -= OnAcceleratorKeyPressed;

        View.CoreWebView2InitializationCompleted -= (_, _) => { };
        View.Dispose();
    }
}