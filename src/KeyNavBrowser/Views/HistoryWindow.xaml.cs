using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using KeyNavBrowser.Models;
using KeyNavBrowser.Services;

namespace KeyNavBrowser.Views;

/// <summary>Просмотр истории посещений (п. 3.1 ТЗ, № 5).</summary>
public partial class HistoryWindow : Window
{
    private readonly HistoryService _history = App.History;
    private readonly Action<string> _openUrl;
    private readonly ICollectionView _view;

    public HistoryWindow(Action<string> openUrl)
    {
        InitializeComponent();

        _openUrl = openUrl;
        _view = CollectionViewSource.GetDefaultView(_history.Entries);
        _view.Filter = FilterEntry;

        EntriesList.ItemsSource = _view;
        UpdateCount();
    }

    private bool FilterEntry(object item)
    {
        if (item is not HistoryEntry entry)
            return false;

        if (string.IsNullOrWhiteSpace(SearchBox.Text))
            return true;

        string needle = SearchBox.Text.Trim();
        return entry.Url.Contains(needle, StringComparison.OrdinalIgnoreCase)
               || entry.DisplayTitle.Contains(needle, StringComparison.OrdinalIgnoreCase);
    }

    private void OnSearchKeyUp(object sender, KeyEventArgs e)
    {
        _view.Refresh();
        UpdateCount();
    }

    private void UpdateCount() =>
        CountText.Text = $"Записей: {_history.Entries.Count}" +
                         (_view.Cast<object>().Any() ? $" · показано: {_view.Cast<object>().Count()}" : string.Empty);

    private HistoryEntry? Selected =>
        EntriesList.SelectedItem as HistoryEntry;

    private void OnOpenClick(object sender, RoutedEventArgs e)
    {
        if (Selected is { } entry)
        {
            _openUrl(entry.Url);
            Close();
        }
    }

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (Selected is { } entry)
        {
            _history.Remove(entry);
            UpdateCount();
        }
    }

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(
                "Удалить все записи истории?",
                "История",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) == MessageBoxResult.Yes)
        {
            _history.Clear();
            UpdateCount();
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}