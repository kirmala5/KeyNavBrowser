using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using KeyNavBrowser.Models;
using KeyNavBrowser.Services;
using KeyNavBrowser.Views;

namespace KeyNavBrowser;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly SettingsService _settings = App.Settings;
    private readonly HistoryService _history = App.History;
    private readonly BookmarkService _bookmarks = App.Bookmarks;

    private TabModel? _activeTab;
    private bool _suppressAddressSync;

    public ObservableCollection<TabModel> Tabs { get; } = new();

    public TabModel? ActiveTab
    {
        get => _activeTab;
        private set
        {
            if (ReferenceEquals(_activeTab, value))
                return;

            _activeTab = value;
            OnPropertyChanged();
            UpdateWindowChrome();
        }
    }

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;

        ContentModeBox.ItemsSource = ContentModes.ContentModeItem.All;
        ContentModeBox.DisplayMemberPath = nameof(ContentModes.ContentModeItem.Display);
        ContentModeBox.SelectedValuePath = nameof(ContentModes.ContentModeItem.Value);

        _settings.Changed += OnSettingsChanged;
    }

    // ==================== режим отображения контента (ТЗ: три режима) ====================

    /// <summary>
    /// Обновляет подпись режима. Отдельно от <see cref="UpdateWindowChrome"/>, потому
    /// что выбор режима меняет настройки, а та в ответ перестраивает подпись.
    /// </summary>
    private void SyncContentModeBox()
    {
        string mode = _settings.Current.ContentMode;

        // Сравнивать нужно по значению элемента, а не самим объектом:
        // Items.Contains("TextAndImages") всегда вернул бы false.
        var item = ContentModes.ContentModeItem.All
            .FirstOrDefault(i => string.Equals(i.Value, mode, StringComparison.Ordinal));

        if (item is null)
            return;

        if (!ReferenceEquals(ContentModeBox.SelectedItem, item))
            ContentModeBox.SelectedItem = item;

        ContentModeBox.ToolTip = ContentModes.Describe(mode);
    }

    private void OnContentModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ContentModeBox.SelectedItem is not ContentModes.ContentModeItem item)
            return;

        // Смена режима меняет настройки, а та вызывает SyncContentModeBox,
        // которая снова выставит SelectedItem. Проверка по значению замыкает
        // это цепочку и исключает рекурсию.
        if (string.Equals(item.Value, _settings.Current.ContentMode, StringComparison.Ordinal))
            return;

        _settings.Update(s => s.ContentMode = item.Value);
    }

    // ==================== запуск ====================

    private async void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        EmptyPlaceholder.Visibility = Visibility.Visible;
        UpdateWindowChrome();

        await CreateTabAsync(_settings.Current.HomePage);
    }

    // ==================== вкладки (п. 3.1 ТЗ, № 4) ====================

    private async Task<TabModel> CreateTabAsync(string? url = null)
    {
        var tab = new TabModel(_settings, _history, _bookmarks);

        Tabs.Add(tab);
        WebViewHost.Children.Add(tab.View);
        tab.View.Visibility = Visibility.Collapsed;

        tab.NavigationStateChanged += OnTabNavigationStateChanged;
        tab.HotkeyRequested += OnTabHotkeyRequested;
        tab.OpenUrlInNewTabRequested += OnOpenUrlInNewTab;
        tab.PropertyChanged += OnTabPropertyChanged;

        SelectTab(tab);

        try
        {
            await tab.InitializeAsync(url ?? _settings.Current.HomePage);
        }
        catch (Exception ex)
        {
            tab.ShowInternalPage(
                "Не удалось запустить WebView2",
                ex.Message + "\n\nПроверьте, что установлен Microsoft Edge WebView2 Runtime.");
        }

        UpdateWindowChrome();
        return tab;
    }

    private void CloseTab(TabModel tab)
    {
        int index = Tabs.IndexOf(tab);

        tab.NavigationStateChanged -= OnTabNavigationStateChanged;
        tab.HotkeyRequested -= OnTabHotkeyRequested;
        tab.OpenUrlInNewTabRequested -= OnOpenUrlInNewTab;
        tab.PropertyChanged -= OnTabPropertyChanged;

        WebViewHost.Children.Remove(tab.View);
        Tabs.Remove(tab);
        tab.Dispose();

        if (Tabs.Count == 0)
        {
            ActiveTab = null;
            EmptyPlaceholder.Visibility = Visibility.Visible;
            UpdateWindowChrome();
            return;
        }

        if (ReferenceEquals(ActiveTab, tab))
            SelectTab(Tabs[Math.Clamp(index, 0, Tabs.Count - 1)]);

        UpdateWindowChrome();
    }

    private void CloseActiveTab()
    {
        if (ActiveTab is { } tab)
            CloseTab(tab);
        else
            _ = CreateTabAsync(_settings.Current.HomePage);
    }

    private void SelectTab(TabModel tab)
    {
        foreach (var item in Tabs)
        {
            bool isActive = ReferenceEquals(item, tab);
            item.IsActive = isActive;
            item.View.Visibility = isActive ? Visibility.Visible : Visibility.Collapsed;
        }

        ActiveTab = tab;
        EmptyPlaceholder.Visibility = Tabs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (tab.View.IsVisible)
            FocusPage();
    }

    private void CycleTab(int delta)
    {
        if (Tabs.Count == 0)
            return;

        int current = ActiveTab is null ? -1 : Tabs.IndexOf(ActiveTab);
        int next = ((current + delta) % Tabs.Count + Tabs.Count) % Tabs.Count;
        SelectTab(Tabs[next]);
    }

    /// <summary>Возвращает клавиатурный фокус веб-странице — иначе W/S/A/D/Q/E не доходят до неё.</summary>
    private void FocusPage()
    {
        var tab = ActiveTab;
        if (tab is null)
            return;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!tab.View.IsVisible)
                return;

            tab.View.Focus();
            Keyboard.Focus(tab.View);
        }), System.Windows.Threading.DispatcherPriority.Input);
    }

    // ==================== обработчики вкладок ====================

    private void OnTabNavigationStateChanged(object? sender, EventArgs e) => UpdateWindowChrome();

    private void OnTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (ReferenceEquals(sender, ActiveTab))
            UpdateWindowChrome();
    }

    private async void OnOpenUrlInNewTab(object? sender, string url) => await CreateTabAsync(url);

    private void OnTabHotkeyRequested(object? sender, HotkeyAction action) => HandleHotkey(action);

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        UpdateWindowChrome();

        foreach (var tab in Tabs.ToList())
            _ = tab.ApplySettingsAsync();
    }

    private void UpdateWindowChrome()
    {
        var tab = ActiveTab;

        Title = tab is null
            ? "KeyNav Browser"
            : string.IsNullOrWhiteSpace(tab.Title) ? "KeyNav Browser" : $"{tab.Title} — KeyNav Browser";

        BackButton.IsEnabled = tab?.CanGoBack ?? false;
        ForwardButton.IsEnabled = tab?.CanGoForward ?? false;
        ReloadButton.IsEnabled = tab is not null;

        if (!_suppressAddressSync)
            AddressBox.Text = tab?.Url ?? string.Empty;

        ZoomText.Text = tab is null ? string.Empty : $"Масштаб {tab.ZoomFactor * 100:0}%";

        BookmarkButton.Content = tab?.IsBookmarked == true ? "★" : "☆";
        BookmarkButton.ToolTip = tab?.IsBookmarked == true
            ? "Удалить закладку (Ctrl+B)"
            : "Добавить закладку (Ctrl+B)";

        KeyboardNavIndicator.Text = _settings.Current.KeyboardNavigationEnabled
            ? "Клавиатурная навигация: ВКЛ"
            : "Клавиатурная навигация: ВЫКЛ";

        KeyboardNavIndicator.Foreground = _settings.Current.KeyboardNavigationEnabled
            ? (Brush)FindResource("AccentBrush")
            : (Brush)FindResource("TextSecondaryBrush");

        SyncContentModeBox();
    }

    // ==================== панель навигации (п. 3.1 ТЗ, № 3) ====================

    private void OnBackClick(object sender, RoutedEventArgs e) => ActiveTab?.GoBack();

    private void OnForwardClick(object sender, RoutedEventArgs e) => ActiveTab?.GoForward();

    private void OnReloadClick(object sender, RoutedEventArgs e) => ActiveTab?.Reload();

    private void OnHomeClick(object sender, RoutedEventArgs e) => ActiveTab?.GoHome();

    private void OnZoomInClick(object sender, RoutedEventArgs e)
    {
        ActiveTab?.ZoomIn();
        UpdateWindowChrome();
    }

    private void OnZoomOutClick(object sender, RoutedEventArgs e)
    {
        ActiveTab?.ZoomOut();
        UpdateWindowChrome();
    }

    // ==================== адресная строка (п. 3.1 ТЗ, № 1) ====================

    private void OnGoClick(object sender, RoutedEventArgs e) => NavigateFromAddressBox();

    private void OnAddressBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            NavigateFromAddressBox();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            AddressBox.Text = ActiveTab?.Url ?? string.Empty;
            FocusPage();
            e.Handled = true;
        }
    }

    private void OnAddressBoxGotFocus(object sender, RoutedEventArgs e)
    {
        _suppressAddressSync = true;
        AddressBox.SelectAll();
    }

    private void OnAddressBoxLostFocus(object sender, RoutedEventArgs e)
    {
        _suppressAddressSync = false;
        AddressBox.Text = ActiveTab?.Url ?? string.Empty;
    }

    private void NavigateFromAddressBox()
    {
        var tab = ActiveTab;
        if (tab is null)
            return;

        string input = AddressBox.Text;
        tab.NavigateInput(input);

        AddressBox.Text = tab.Url;
        FocusPage();
    }

    private void FocusAddressBar()
    {
        AddressBox.Focus();
        AddressBox.SelectAll();
    }

    // ==================== закладки (п. 3.1 ТЗ, № 6) ====================

    private void OnBookmarkClick(object sender, RoutedEventArgs e) => ActiveTab?.ToggleBookmark();

    // ==================== диалоговые окна ====================

    private void OnHistoryClick(object sender, RoutedEventArgs e) => ShowHistoryWindow();

    private void OnBookmarksClick(object sender, RoutedEventArgs e) => ShowBookmarksWindow();

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow { Owner = this };
        dialog.ShowDialog();
        UpdateWindowChrome();
    }

    private void ShowHistoryWindow()
    {
        var window = new HistoryWindow(url => ActiveTab?.NavigateInput(url));
        window.Owner = this;
        window.ShowDialog();
    }

    private void ShowBookmarksWindow()
    {
        var window = new BookmarksWindow(url => ActiveTab?.NavigateInput(url));
        window.Owner = this;
        window.ShowDialog();
    }

    // ==================== обработчики полосы вкладок ====================

    private void OnNewTabClick(object sender, RoutedEventArgs e) => _ = CreateTabAsync(_settings.Current.HomePage);

    private void OnTabHeaderClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TabModel tab })
            SelectTab(tab);
    }

    private void OnTabCloseClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TabModel tab })
            CloseTab(tab);
    }

    // ==================== горячие клавиши ====================

    /// <summary>
    /// Срабатывает, когда фокус находится на элементах WPF (адресная строка, кнопки).
    /// Если фокус внутри WebView2, те же комбинации обрабатываются в
    /// CoreWebView2Controller.AcceleratorKeyPressed.
    /// </summary>
    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        bool alt = (Keyboard.Modifiers & ModifierKeys.Alt) != 0;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

        if (ctrl)
        {
            switch (e.Key)
            {
                case Key.T: _ = CreateTabAsync(_settings.Current.HomePage); break;
                case Key.W: CloseActiveTab(); break;
                case Key.L: FocusAddressBar(); break;
                case Key.B: ActiveTab?.ToggleBookmark(); break;
                case Key.H: ShowHistoryWindow(); break;
                case Key.O: ShowBookmarksWindow(); break;
                case Key.R: ActiveTab?.Reload(shift); break;
                case Key.Add:
                case Key.OemPlus: ActiveTab?.ZoomIn(); break;
                case Key.Subtract:
                case Key.OemMinus: ActiveTab?.ZoomOut(); break;
                case Key.D0:
                case Key.NumPad0: ActiveTab?.ResetZoom(); break;
                case Key.Tab: CycleTab(shift ? -1 : 1); break;
                case Key.PageDown: CycleTab(1); break;
                case Key.PageUp: CycleTab(-1); break;
                default: return;
            }

            UpdateWindowChrome();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F5)
        {
            ActiveTab?.Reload(shift);
            UpdateWindowChrome();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F11)
        {
            ToggleFullScreen();
            e.Handled = true;
            return;
        }

        if (alt && e.Key == Key.Left)
        {
            ActiveTab?.GoBack();
            e.Handled = true;
            return;
        }

        if (alt && e.Key == Key.Right)
        {
            ActiveTab?.GoForward();
            e.Handled = true;
            return;
        }

        // --- Клавиши клавиатурной навигации (раздел 4 ТЗ) ---
        if (!_settings.Current.KeyboardNavigationEnabled || alt)
            return;

        // Пока фокус в текстовом поле, буквы должны вводиться как обычный текст.
        // TextBoxBase живёт в пространстве имён Primitives и покрывает TextBox и RichTextBox.
        if (Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase or PasswordBox)
            return;

        var tab = ActiveTab;
        if (tab is null)
            return;

        switch (e.Key)
        {
            case Key.W: tab.ScrollUp(); break;
            case Key.S: tab.ScrollDown(); break;
            case Key.A: tab.ScrollLeft(); break;
            case Key.D: tab.ScrollRight(); break;
            case Key.Space: tab.PageDownScreen(); break;
            case Key.Q: tab.PreviousLink(); break;
            case Key.E: tab.NextLink(); break;
            case Key.Enter: tab.ActivateLink(); break;
            case Key.Escape: tab.ClearLinkHighlight(); break;

            // 1–9 — переход по ссылке с соответствующим номером. Нумерация,
            // нарисованная поверх ссылок, соответствует этим же номерам.
            case >= Key.D1 and <= Key.D9:
                tab.GoToLinkNumber(e.Key - Key.D0); break;
            case >= Key.NumPad1 and <= Key.NumPad9:
                tab.GoToLinkNumber(e.Key - Key.NumPad0); break;

            default: return;
        }

        UpdateWindowChrome();
        e.Handled = true;
    }

    private void HandleHotkey(HotkeyAction action)
    {
        switch (action)
        {
            case HotkeyAction.NewTab: _ = CreateTabAsync(_settings.Current.HomePage); break;
            case HotkeyAction.CloseTab: CloseActiveTab(); break;
            case HotkeyAction.FocusAddressBar: FocusAddressBar(); break;
            case HotkeyAction.Reload: ActiveTab?.Reload(); break;
            case HotkeyAction.HardReload: ActiveTab?.Reload(true); break;
            case HotkeyAction.NextTab: CycleTab(1); break;
            case HotkeyAction.PreviousTab: CycleTab(-1); break;
            case HotkeyAction.ToggleBookmark: ActiveTab?.ToggleBookmark(); break;
            case HotkeyAction.ShowHistory: ShowHistoryWindow(); break;
            case HotkeyAction.ShowBookmarks: ShowBookmarksWindow(); break;
            case HotkeyAction.ShowSettings: OnSettingsClick(this, new RoutedEventArgs()); break;
        }

        UpdateWindowChrome();
    }

    private void ToggleFullScreen()
    {
        if (WindowState == WindowState.Maximized)
        {
            WindowState = WindowState.Normal;
            ResizeMode = ResizeMode.CanResize;
            Topmost = false;
        }
        else
        {
            WindowState = WindowState.Maximized;
            ResizeMode = ResizeMode.NoResize;
            Topmost = true;
        }
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        _settings.Save();
        _history.Flush();
        _bookmarks.Save();

        foreach (var tab in Tabs.ToList())
            tab.Dispose();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}