using System.Windows;
using System.Windows.Threading;
using KeyNavBrowser.Services;

namespace KeyNavBrowser;

public partial class App : Application
{
    public static SettingsService Settings { get; private set; } = null!;
    public static HistoryService History { get; private set; } = null!;
    public static BookmarkService Bookmarks { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Если профиль пришлось перенести во временную папку, сообщаем об этом
        // один раз при запуске — иначе браузер молча «забыл» бы авторизацию.
        if (AppPaths.ProfileFallbackReason is { Length: > 0 } warning)
        {
            MessageBox.Show(warning, "KeyNav Browser — профиль во временной папке",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        base.OnStartup(e);

        Settings = new SettingsService();
        Settings.Load();

        History = new HistoryService();
        History.Load();

        Bookmarks = new BookmarkService();
        Bookmarks.Load();

        ThemeManager.Apply(Settings.Current.Theme);

        // Окно создаём явно (StartupUri в App.xaml не используется), чтобы
        // сначала были готовы настройки, история и закладки.
        var window = new MainWindow();
        MainWindow = window;
        window.Show();

        DispatcherUnhandledException += OnDispatcherUnhandledException;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Settings?.Save();
        History?.Flush();
        Bookmarks?.Save();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            $"Непредвиденная ошибка:\n\n{e.Exception.Message}",
            "KeyNav Browser",
            MessageBoxButton.OK,
            MessageBoxImage.Error);

        e.Handled = true;
    }
}