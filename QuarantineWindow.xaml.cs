using System.Collections.ObjectModel;
using System.Windows;
using DevClean.Cleanup;
using DevClean.Models;

namespace DevClean;

public partial class QuarantineWindow : Window
{
    private readonly QuarantineService _service;

    private readonly ObservableCollection<QuarantineViewModel>
        _items = [];

    public QuarantineWindow(string driveRoot)
    {
        InitializeComponent();

        _service =
            new QuarantineService(driveRoot);

        QuarantineList.ItemsSource =
            _items;

        Loaded += async (_, _) =>
        {
            await LoadItemsAsync();
        };
    }

    private async Task LoadItemsAsync()
    {
        try
        {
            List<QuarantineItem> items =
                await _service.GetItemsAsync();

            _items.Clear();

            foreach (QuarantineItem item in items)
            {
                _items.Add(
                    new QuarantineViewModel(item));
            }

            UpdateStatus();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                ex.Message,
                "DevClean",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async void RefreshButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        await LoadItemsAsync();
    }

    private async void RestoreButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        List<QuarantineViewModel> selected =
            _items
                .Where(x => x.IsSelected)
                .ToList();

        if (selected.Count == 0)
        {
            MessageBox.Show(
                "Select at least one quarantined item.",
                "DevClean");

            return;
        }

        MessageBoxResult confirmation =
            MessageBox.Show(
                $"Restore {selected.Count:N0} selected item(s)?",
                "Confirm Restore",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        int success = 0;
        int failed = 0;

        foreach (QuarantineViewModel item in selected)
        {
            CleanupResult result =
                await _service.RestoreAsync(
                    item.Item.Id);

            if (result.Success)
            {
                success++;
                _items.Remove(item);
            }
            else
            {
                failed++;
            }
        }

        UpdateStatus();

        MessageBox.Show(
            $"Restore finished.\n\n" +
            $"Restored: {success:N0}\n" +
            $"Failed: {failed:N0}",
            "DevClean");
    }

    private async void DeleteButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        List<QuarantineViewModel> selected =
            _items
                .Where(x => x.IsSelected)
                .ToList();

        if (selected.Count == 0)
        {
            MessageBox.Show(
                "Select at least one quarantined item.",
                "DevClean");

            return;
        }

        long totalSize =
            selected.Sum(
                x => x.Item.Size);

        MessageBoxResult confirmation =
            MessageBox.Show(
                $"PERMANENTLY DELETE {selected.Count:N0} item(s)?\n\n" +
                $"Space: {FormatSize(totalSize)}\n\n" +
                "This action CANNOT be undone.",
                "Permanent Delete",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        int success = 0;
        int failed = 0;

        foreach (QuarantineViewModel item in selected)
        {
            CleanupResult result =
                await _service.PermanentlyDeleteAsync(
                    item.Item.Id);

            if (result.Success)
            {
                success++;
                _items.Remove(item);
            }
            else
            {
                failed++;
            }
        }

        UpdateStatus();

        MessageBox.Show(
            $"Permanent deletion finished.\n\n" +
            $"Deleted: {success:N0}\n" +
            $"Failed: {failed:N0}",
            "DevClean");
    }

    private void CloseButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        Close();
    }

    private void UpdateStatus()
    {
        long totalSize =
            _items.Sum(
                x => x.Item.Size);

        StatusText.Text =
            $"{_items.Count:N0} quarantined items • " +
            $"{FormatSize(totalSize)}";
    }

    private static string FormatSize(
        long bytes)
    {
        string[] units =
        [
            "B",
            "KB",
            "MB",
            "GB",
            "TB"
        ];

        double size = bytes;
        int unit = 0;

        while (size >= 1024 &&
               unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return $"{size:0.0} {units[unit]}";
    }

    private class QuarantineViewModel
    {
        public QuarantineItem Item { get; }

        public bool IsSelected { get; set; }

        public string FileName =>
            Item.FileName;

        public string TargetType =>
            Item.IsFolder ? "FOLDER" : "FILE";

        public string OriginalPath =>
            Item.OriginalPath;

        public string SizeDisplay =>
            FormatSize(Item.Size);

        public string QuarantinedAtDisplay =>
            Item.QuarantinedAt.ToString(
                "yyyy-MM-dd HH:mm");

        public string Reason =>
            Item.Reason;

        public QuarantineViewModel(
            QuarantineItem item)
        {
            Item = item;
        }
    }
}