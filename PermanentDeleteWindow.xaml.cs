using System.Collections.Generic;
using System.Linq;
using System.Windows;
using DevClean.Models;

namespace DevClean;

public partial class PermanentDeleteWindow : Window
{
    private readonly List<CandidateViewModel> _candidates;

    public bool Confirmed { get; private set; }

    public PermanentDeleteWindow(
        IEnumerable<CandidateViewModel> candidates)
    {
        InitializeComponent();

        _candidates = candidates.ToList();

        LoadSummary();
        LoadFileList();
    }

    private void LoadSummary()
    {
        long totalSize =
            _candidates.Sum(x => x.Size);

        int safe =
            _candidates.Count(
                x => x.Analysis.Level == SafetyLevel.Safe);

        int review =
            _candidates.Count(
                x => x.Analysis.Level == SafetyLevel.Review);

        int caution =
            _candidates.Count(
                x => x.Analysis.Level == SafetyLevel.Caution);

        FileCountText.Text =
            _candidates.Count.ToString("N0");

        TotalSizeText.Text =
            FormatSize(totalSize);

        SafetySummaryText.Text =
            $"🟢 {safe:N0} Safe   " +
            $"🟡 {review:N0} Review   " +
            $"🟠 {caution:N0} Caution";

        ListCountText.Text =
            $"{_candidates.Count:N0} file(s)";

        DeleteButton.Content =
            $"Permanently Delete {_candidates.Count:N0}";
    }

    private void LoadFileList()
    {
        FileList.ItemsSource =
            _candidates
                .OrderByDescending(x => x.Size)
                .ToList();
    }

    private void CancelButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        Confirmed = false;
        DialogResult = false;
    }

    private void DeleteButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        long totalSize =
            _candidates.Sum(x => x.Size);

        MessageBoxResult result =
            MessageBox.Show(
                $"You are about to permanently delete " +
                $"{_candidates.Count:N0} file(s).\n\n" +
                $"Total size: {FormatSize(totalSize)}\n\n" +
                "These files will NOT be sent to quarantine.\n" +
                "They cannot be restored after deletion.\n\n" +
                "Are you absolutely sure?",
                "Final Confirmation",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        Confirmed = true;
        DialogResult = true;
    }

    private static string FormatSize(long bytes)
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
}