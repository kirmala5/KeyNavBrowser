using System.Windows;
using KeyNavBrowser.Models;
using KeyNavBrowser.Services;

namespace KeyNavBrowser.Views;

/// <summary>Просмотр и удаление закладок (п. 3.1 ТЗ, № 6).</summary>
public partial class BookmarksWindow : Window
{
    private readonly BookmarkService _bookmarks = App.Bookmarks;
    private readonly Action<string> _openUrl;

    public BookmarksWindow(Action<string> openUrl)
    {
        InitializeComponent();

        _openUrl = openUrl;
        BookmarksList.ItemsSource = _bookmarks.Items;
        CountText.Text = $"Закладок: {_bookmarks.Items.Count}";
    }

    private BookmarkItem? Selected => BookmarksList.SelectedItem as BookmarkItem;

    private void OnOpenClick(object sender, RoutedEventArgs e)
    {
        if (Selected is { } item)
        {
            _openUrl(item.Url);
            Close();
        }
    }

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (Selected is { } item)
        {
            _bookmarks.Remove(item);
            CountText.Text = $"Закладок: {_bookmarks.Items.Count}";
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}