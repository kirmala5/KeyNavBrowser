using System.Windows;
using System.Windows.Controls;
using KeyNavBrowser.Models;
using KeyNavBrowser.Services;

namespace KeyNavBrowser.Views;

/// <summary>Панель настроек (п. 3.2 ТЗ и п. 4.3 ТЗ — включение клавиатурной навигации).</summary>
public partial class SettingsWindow : Window
{
    private readonly SettingsService _settings = App.Settings;
    private bool _loading = true;

    public SettingsWindow()
    {
        InitializeComponent();

        SearchEngineBox.ItemsSource = SearchEngines.All;
        ThemeBox.ItemsSource = ThemeNames.All;
        ContentModeBox.ItemsSource = ContentModes.ContentModeItem.All;

        LoadFromSettings();
        _loading = false;
    }

    private void LoadFromSettings()
    {
        var current = _settings.Current;

        HomePageBox.Text = current.HomePage;
        SearchEngineBox.SelectedItem = current.SearchEngine;
        ThemeBox.SelectedItem = current.Theme;
        KeyboardNavCheck.IsChecked = current.KeyboardNavigationEnabled;
        TargetBlankCheck.IsChecked = current.TargetBlankInNewTab;
        LinkNumbersCheck.IsChecked = current.ShowLinkNumbers;
        ContentModeBox.SelectedItem =
            ContentModes.ContentModeItem.All.FirstOrDefault(i => i.Value == current.ContentMode);

        ScrollStepSlider.Value = Math.Clamp(current.ScrollStep, 50, 100);
        UpdateScrollStepLabel();
        UpdateContentModeHint();
    }

    private void UpdateScrollStepLabel() =>
        ScrollStepValue.Text = $"{ScrollStepSlider.Value:0} пикселей";

    private void UpdateContentModeHint()
    {
        if (ContentModeBox.SelectedItem is ContentModes.ContentModeItem item)
            ContentModeHint.Text = item.Description;
        else
            ContentModeHint.Text = string.Empty;
    }

    // ---- предварительный просмотр настроек до нажатия «Сохранить» ----

    /// <summary>
    /// Режим отображения применяется сразу: страницу не нужно перезагружать,
    /// скрипт переключает CSS на лету. Поэтому здесь, в отличие от остальных
    /// настроек, изменение сразу уходит в <see cref="SettingsService"/>.
    /// </summary>
    private void OnContentModePreview(object sender, SelectionChangedEventArgs e)
    {
        UpdateContentModeHint();

        if (_loading || ContentModeBox.SelectedItem is not ContentModes.ContentModeItem item)
            return;

        _settings.Update(s => s.ContentMode = item.Value);
    }

    private void OnLinkNumbersChanged(object sender, RoutedEventArgs e)
    {
        if (!_loading)
            _settings.Update(s => s.ShowLinkNumbers = LinkNumbersCheck.IsChecked == true);
    }

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ThemeBox.SelectedItem is not string theme)
            return;

        ThemeManager.Apply(theme);
    }

    private void OnKeyboardNavChanged(object sender, RoutedEventArgs e)
    {
        if (!_loading)
            _settings.Update(s => s.KeyboardNavigationEnabled = KeyboardNavCheck.IsChecked == true);
    }

    private void OnTargetBlankChanged(object sender, RoutedEventArgs e)
    {
        if (!_loading)
            _settings.Update(s => s.TargetBlankInNewTab = TargetBlankCheck.IsChecked == true);
    }

    private void OnScrollStepChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading)
            return;

        UpdateScrollStepLabel();
        _settings.Update(s => s.ScrollStep = (int)e.NewValue);
    }

    // ---- кнопки ----

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        _settings.Update(s =>
        {
            s.HomePage = HomePageBox.Text;
            s.SearchEngine = SearchEngineBox.SelectedItem as string ?? SearchEngines.Yandex;
            s.Theme = ThemeBox.SelectedItem as string ?? ThemeNames.Light;
            s.KeyboardNavigationEnabled = KeyboardNavCheck.IsChecked == true;
            s.TargetBlankInNewTab = TargetBlankCheck.IsChecked == true;
            s.ShowLinkNumbers = LinkNumbersCheck.IsChecked == true;
            s.ScrollStep = (int)ScrollStepSlider.Value;

            if (ContentModeBox.SelectedItem is ContentModes.ContentModeItem mode)
                s.ContentMode = mode.Value;
        });

        DialogResult = true;
    }

    private void OnResetClick(object sender, RoutedEventArgs e)
    {
        var defaults = new BrowserSettings();

        HomePageBox.Text = defaults.HomePage;
        SearchEngineBox.SelectedItem = defaults.SearchEngine;
        ThemeBox.SelectedItem = defaults.Theme;
        KeyboardNavCheck.IsChecked = defaults.KeyboardNavigationEnabled;
        TargetBlankCheck.IsChecked = defaults.TargetBlankInNewTab;
        LinkNumbersCheck.IsChecked = defaults.ShowLinkNumbers;
        ScrollStepSlider.Value = defaults.ScrollStep;
        ContentModeBox.SelectedItem =
            ContentModes.ContentModeItem.All.FirstOrDefault(i => i.Value == defaults.ContentMode);
        UpdateScrollStepLabel();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        // Возвращаем сохранённые значения и тему, если пользователь «примерял» тему.
        ThemeManager.Apply(_settings.Current.Theme);
        LoadFromSettings();
        DialogResult = false;
    }
}