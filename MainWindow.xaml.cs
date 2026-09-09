using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using DevClean.AI;
using DevClean.Candidates;
using DevClean.Cleanup;
using DevClean.Models;
using DevClean.Scanner;

namespace DevClean;

public partial class MainWindow : Window
{
    private readonly DiskScanner _scanner;
    private readonly LocalSafetyAnalyzer _analyzer;
    private readonly CandidateDetector _candidateDetector;

    private readonly ObservableCollection<CandidateViewModel>
        _allCandidates = [];

    private readonly ObservableCollection<CandidateViewModel>
        _visibleCandidates = [];

    private CancellationTokenSource? _cancellationSource;

    public MainWindow()
{
    InitializeComponent();

    var workArea = SystemParameters.WorkArea;

    Left = workArea.Left +
           (workArea.Width - Width) / 2;

    Top = workArea.Top +
          (workArea.Height - Height) / 2;

    _scanner = new DiskScanner();
    _analyzer = new LocalSafetyAnalyzer();
    _candidateDetector = new CandidateDetector();
            new CandidateDetector();

        LoadDrives();

        CandidateList.ItemsSource =
            _visibleCandidates;

        CategoryFilter.Items.Add(
            "All categories");

        CategoryFilter.SelectedIndex = 0;

        SafetyFilter.Items.Add(
            "All safety levels");

        SafetyFilter.Items.Add("Safe");
        SafetyFilter.Items.Add("Review");
        SafetyFilter.Items.Add("Caution");
        SafetyFilter.Items.Add("Do not delete");

        SafetyFilter.SelectedIndex = 0;
    }

    private void LoadDrives()
    {
        DriveInfo[] drives =
            DriveInfo.GetDrives()
                .Where(d =>
                    d.IsReady &&
                    d.DriveType == DriveType.Fixed)
                .ToArray();

        DriveComboBox.Items.Clear();

        foreach (DriveInfo drive in drives)
        {
            DriveComboBox.Items.Add(
                new DriveItem
                {
                    Drive = drive,
                    Display =
                        $"{drive.Name} " +
                        $"({FormatSize(drive.AvailableFreeSpace)} free)"
                });
        }

        if (drives.Length > 0)
        {
            DriveComboBox.SelectedIndex = 0;
        }
    }

  private async void ScanButton_Click(
    object sender,
    RoutedEventArgs e)
{
    if (DriveComboBox.SelectedItem
        is not DriveItem selected)
    {
        MessageBox.Show(
            "Please select a drive.",
            "DevClean");

        return;
    }

    _cancellationSource?.Cancel();

    _cancellationSource =
        new CancellationTokenSource();

    ScanButton.IsEnabled = false;
    QuarantineButton.IsEnabled = false;

    StatusText.Text = "Scanning...";
    ProgressText.Text =
        "Scanning every file on the drive...";

    ScanProgressBar.IsIndeterminate = true;

    _allCandidates.Clear();
    _visibleCandidates.Clear();

    FilesScannedText.Text = "0";
    CandidatesText.Text = "0";

    try
    {
        List<FileItem> files =
            await _scanner.ScanFilesAsync(
                selected.Drive.RootDirectory.FullName,
                _cancellationSource.Token);

        FilesScannedText.Text =
            files.Count.ToString("N0");

        ProgressText.Text =
            "Building file inventory...";

        // -------------------------------------------------
        // EVERY FILE
        // -------------------------------------------------

        List<CleanupCandidate> fileCandidates =
            _candidateDetector.FindCandidates(
                files);

        // -------------------------------------------------
        // SMART FOLDER TARGETS
        // -------------------------------------------------

        List<CleanupCandidate> folderCandidates =
            _candidateDetector.FindFolderCandidates(
                files);

        // -------------------------------------------------
        // COMBINE
        // -------------------------------------------------

        List<CleanupCandidate> candidates =
            fileCandidates
                .Concat(folderCandidates)
                .OrderByDescending(
                    x => x.PriorityScore)
                .ThenByDescending(
                    x => x.Size)
                .ToList();

        CandidatesText.Text =
            candidates.Count.ToString("N0");

        ProgressText.Text =
            $"Analyzing {candidates.Count:N0} items...";

        // -------------------------------------------------
        // IMPORTANT:
        //
        // No artificial 1,000 item limit.
        // Every file gets analyzed.
        // -------------------------------------------------

        foreach (CleanupCandidate candidate
                 in candidates)
        {
            _cancellationSource.Token
                .ThrowIfCancellationRequested();

            SafetyAnalysis analysis =
                await _analyzer.AnalyzeAsync(
                    candidate.File,
                    _cancellationSource.Token);

            // Never allow a dangerous folder to become
            // a cleanup target.
            if (candidate.IsFolder &&
                analysis.Level ==
                SafetyLevel.DoNotDelete)
            {
                continue;
            }

            _allCandidates.Add(
                new CandidateViewModel(
                    candidate,
                    analysis));
        }

        PopulateCategoryFilter();

        ApplyFilters();

        StatusText.Text = "Ready";

        ProgressText.Text =
            $"Scan complete • " +
            $"{files.Count:N0} files scanned • " +
            $"{_allCandidates.Count:N0} results";

        ScanProgressBar.IsIndeterminate =
            false;

        ScanProgressBar.Value = 100;
    }
    catch (OperationCanceledException)
    {
        StatusText.Text = "Cancelled";

        ProgressText.Text =
            "Scan cancelled.";

        ScanProgressBar.IsIndeterminate =
            false;
    }
    catch (Exception ex)
    {
        StatusText.Text = "Error";

        MessageBox.Show(
            ex.Message,
            "DevClean scan error",
            MessageBoxButton.OK,
            MessageBoxImage.Error);

        ScanProgressBar.IsIndeterminate =
            false;
    }
    finally
    {
        ScanButton.IsEnabled = true;
        QuarantineButton.IsEnabled = true;
    }
}
    private void PopulateCategoryFilter()
    {
        string? current =
            CategoryFilter.SelectedItem?.ToString();

        CategoryFilter.Items.Clear();

        CategoryFilter.Items.Add(
            "All categories");

        foreach (string category in
                 _allCandidates
                     .Select(x => x.Category)
                     .Distinct()
                     .OrderBy(x => x))
        {
            CategoryFilter.Items.Add(category);
        }

        int index = 0;

        if (!string.IsNullOrWhiteSpace(current))
        {
            for (int i = 0;
                 i < CategoryFilter.Items.Count;
                 i++)
            {
                if (CategoryFilter.Items[i]?.ToString()
                    == current)
                {
                    index = i;
                    break;
                }
            }
        }

        CategoryFilter.SelectedIndex = index;
    }

    private void ApplyFilters()
    {
        _visibleCandidates.Clear();

        string category =
            CategoryFilter.SelectedItem?.ToString()
            ?? "All categories";

        string safety =
            SafetyFilter.SelectedItem?.ToString()
            ?? "All safety levels";

        string type =
            (TypeFilter.SelectedItem as ComboBoxItem)
                ?.Content?.ToString()
            ?? "All";

        IEnumerable<CandidateViewModel> query =
            _allCandidates;

        if (category != "All categories")
        {
            query =
                query.Where(
                    x => x.Category == category);
        }

        if (type == "Files")
        {
            query =
                query.Where(
                    x => !x.Candidate.IsFolder);
        }
        else if (type == "Folders")
        {
            query =
                query.Where(
                    x => x.Candidate.IsFolder);
        }

        if (safety != "All safety levels")
        {
            SafetyLevel selectedLevel =
                safety switch
                {
                    "Safe" => SafetyLevel.Safe,
                    "Review" => SafetyLevel.Review,
                    "Caution" => SafetyLevel.Caution,
                    "Do not delete" =>
                        SafetyLevel.DoNotDelete,
                    _ => SafetyLevel.Review
                };

            query =
                query.Where(
                    x => x.Analysis.Level ==
                         selectedLevel);
        }

        foreach (CandidateViewModel candidate
                 in query
                     .OrderByDescending(
                         x => x.Size))
        {
            _visibleCandidates.Add(
                candidate);
        }

        ResultCountText.Text =
            $"{_visibleCandidates.Count:N0} results";
    }

    private void Filter_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (!IsInitialized)
        {
            return;
        }

        ApplyFilters();
    }

    private void SelectAllCheckBox_Checked(
        object sender,
        RoutedEventArgs e)
    {
        foreach (CandidateViewModel candidate
                 in _visibleCandidates)
        {
            if (candidate.Analysis.Level !=
                SafetyLevel.DoNotDelete)
            {
                candidate.IsSelected = true;
            }
        }

        UpdateSelectionDisplay();
    }

    private void SelectAllCheckBox_Unchecked(
        object sender,
        RoutedEventArgs e)
    {
        foreach (CandidateViewModel candidate
                 in _visibleCandidates)
        {
            candidate.IsSelected = false;
        }

        UpdateSelectionDisplay();
    }

    private void CandidateCheckBox_Changed(
        object sender,
        RoutedEventArgs e)
    {
        UpdateSelectionDisplay();
    }

    private void CandidateList_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (CandidateList.SelectedItem
            is CandidateViewModel candidate)
        {
            StatusText.Text =
                candidate.Reason;

            ProgressText.Text =
                $"{candidate.Explanation} " +
                $"Last modified: " +
                $"{candidate.LastModifiedDisplay} • " +
                $"{candidate.AgeDisplay}";
        }
    }

    private void OpenLocationButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (CandidateList.SelectedItem
            is CandidateViewModel candidate)
        {
            candidate.OpenLocation();
            return;
        }

        CandidateViewModel? selected =
            _allCandidates
                .FirstOrDefault(
                    x => x.IsSelected);

        if (selected != null)
        {
            selected.OpenLocation();
            return;
        }

        MessageBox.Show(
            "Select an item first.",
            "DevClean");
    }

    private async void QuarantineButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (DriveComboBox.SelectedItem
            is not DriveItem selected)
        {
            return;
        }

        List<CandidateViewModel>
            selectedCandidates =
                _allCandidates
                    .Where(x =>
                        x.IsSelected &&
                        x.Analysis.Level !=
                        SafetyLevel.DoNotDelete)
                    .ToList();

        if (selectedCandidates.Count == 0)
        {
            MessageBox.Show(
                "Select at least one file or folder first.",
                "DevClean");

            return;
        }

        long totalSize =
            selectedCandidates.Sum(
                x => x.Size);

        int folderCount =
            selectedCandidates.Count(
                x => x.Candidate.IsFolder);

        int fileCount =
            selectedCandidates.Count -
            folderCount;

        string targetDescription =
            folderCount > 0 && fileCount > 0
                ? $"{fileCount:N0} file(s) and " +
                  $"{folderCount:N0} folder(s)"
                : folderCount > 0
                    ? $"{folderCount:N0} folder(s)"
                    : $"{fileCount:N0} file(s)";

        MessageBoxResult confirmation =
            MessageBox.Show(
                $"Move {targetDescription} " +
                $"({FormatSize(totalSize)}) to quarantine?\n\n" +
                "Nothing will be permanently deleted.",
                "Confirm cleanup",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

        if (confirmation !=
            MessageBoxResult.Yes)
        {
            return;
        }

        var service =
            new QuarantineService(
                selected.Drive.RootDirectory.FullName);

        int success = 0;
        int failed = 0;

        foreach (CandidateViewModel item
                 in selectedCandidates)
        {
            CleanupResult result =
                await service.QuarantineAsync(
                    item.Candidate);

            if (result.Success)
            {
                success++;
            }
            else
            {
                failed++;
            }
        }

        foreach (CandidateViewModel item
                 in selectedCandidates)
        {
            _allCandidates.Remove(item);
        }

        ApplyFilters();
        UpdateSelectionDisplay();

        MessageBox.Show(
            $"Cleanup finished.\n\n" +
            $"Quarantined: {success:N0}\n" +
            $"Failed: {failed:N0}\n" +
            $"Space: {FormatSize(totalSize)}",
            "DevClean");
    }

    private void QuarantineManagerButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (DriveComboBox.SelectedItem
            is not DriveItem selected)
        {
            MessageBox.Show(
                "Please select a drive first.",
                "DevClean");

            return;
        }

        var window =
            new QuarantineWindow(
                selected.Drive.RootDirectory.FullName);

        window.Owner = this;

        window.ShowDialog();
    }

    private void UpdateSelectionDisplay()
    {
        long selectedSize =
            _allCandidates
                .Where(x => x.IsSelected)
                .Sum(x => x.Size);

        int selectedCount =
            _allCandidates.Count(
                x => x.IsSelected);

        SelectedText.Text =
            FormatSize(selectedSize);

        SelectedInfoText.Text =
            selectedCount == 0
                ? "Select files or folders to clean."
                : $"{selectedCount:N0} item(s) selected • " +
                  $"{FormatSize(selectedSize)}";
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

    private class DriveItem
    {
        public DriveInfo Drive { get; set; } = null!;

        public string Display { get; set; } = string.Empty;

        public override string ToString()
        {
            return Display;
        }
    }


    private void ExitButton_Click(
    object sender,
    RoutedEventArgs e)
{
    Close();
}
private async void PermanentDeleteButton_Click(
    object sender,
    RoutedEventArgs e)
{
    List<CandidateViewModel> selectedCandidates =
        _allCandidates
            .Where(x =>
                x.IsSelected &&
                x.Analysis.Level !=
                    SafetyLevel.DoNotDelete &&
                !x.Candidate.IsFolder)
            .ToList();

    if (selectedCandidates.Count == 0)
    {
        MessageBox.Show(
            "Select at least one file first.\n\n" +
            "Permanent deletion currently supports files only.",
            "DevClean",
            MessageBoxButton.OK,
            MessageBoxImage.Information);

        return;
    }

    var confirmationWindow =
        new PermanentDeleteWindow(
            selectedCandidates);

    confirmationWindow.Owner = this;

    bool? result =
        confirmationWindow.ShowDialog();

    if (result != true ||
        !confirmationWindow.Confirmed)
    {
        return;
    }

    PermanentDeleteButton.IsEnabled = false;
    QuarantineButton.IsEnabled = false;

    int success = 0;
    int failed = 0;
    long deletedBytes = 0;

    foreach (CandidateViewModel item
             in selectedCandidates)
    {
        try
        {
            string path = item.Path;

            if (!File.Exists(path))
            {
                failed++;
                continue;
            }

            long size = item.Size;

            await Task.Run(() =>
            {
                File.Delete(path);
            });

            if (!File.Exists(path))
            {
                success++;
                deletedBytes += size;
            }
            else
            {
                failed++;
            }
        }
        catch
        {
            failed++;
        }
    }

    foreach (CandidateViewModel item
             in selectedCandidates)
    {
        if (!File.Exists(item.Path))
        {
            _allCandidates.Remove(item);
        }
    }

    ApplyFilters();
    UpdateSelectionDisplay();

    PermanentDeleteButton.IsEnabled = true;
    QuarantineButton.IsEnabled = true;

    MessageBox.Show(
        $"Permanent deletion finished.\n\n" +
        $"Deleted: {success:N0}\n" +
        $"Failed: {failed:N0}\n" +
        $"Space freed: {FormatSize(deletedBytes)}",
        "DevClean",
        MessageBoxButton.OK,
        failed == 0
            ? MessageBoxImage.Information
            : MessageBoxImage.Warning);
}
}