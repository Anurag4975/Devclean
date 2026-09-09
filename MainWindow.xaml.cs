using System.Collections.ObjectModel;
using System.IO;
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

    private bool _updatingSelection;


    // =========================================================
    // CONSTRUCTOR
    // =========================================================

    public MainWindow()
    {
        InitializeComponent();

        var workArea = SystemParameters.WorkArea;

        Left =
            workArea.Left +
            (workArea.Width - Width) / 2;

        Top =
            workArea.Top +
            (workArea.Height - Height) / 2;

        _scanner = new DiskScanner();
        _analyzer = new LocalSafetyAnalyzer();
        _candidateDetector = new CandidateDetector();

        LoadDrives();

        // -----------------------------------------------------
        // CATEGORY FILTER
        // -----------------------------------------------------

        CategoryFilter.Items.Clear();

        CategoryFilter.Items.Add(
            "All categories");

        CategoryFilter.SelectedIndex = 0;


        // -----------------------------------------------------
        // SAFETY FILTER
        // -----------------------------------------------------

        SafetyFilter.Items.Clear();

        SafetyFilter.Items.Add(
            "All safety levels");

        SafetyFilter.Items.Add("Safe");
        SafetyFilter.Items.Add("Review");
        SafetyFilter.Items.Add("Caution");
        SafetyFilter.Items.Add("Do not delete");

        SafetyFilter.SelectedIndex = 0;


        // -----------------------------------------------------
        // INITIAL LIST SOURCES
        // -----------------------------------------------------

        CandidateList.ItemsSource =
            _visibleCandidates;

        FolderList.ItemsSource = null;

        FolderList.Visibility =
            Visibility.Collapsed;

        CandidateList.Visibility =
            Visibility.Visible;
    }


    // =========================================================
    // DRIVE LOADING
    // =========================================================

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


    // =========================================================
    // SCAN
    // =========================================================

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
        PermanentDeleteButton.IsEnabled = false;

        StatusText.Text =
            "Scanning...";

        ProgressText.Text =
            "Scanning every file on the drive...";

        ScanProgressBar.IsIndeterminate =
            true;

        _allCandidates.Clear();
        _visibleCandidates.Clear();

        FilesScannedText.Text = "0";
        CandidatesText.Text = "0";

        SelectedText.Text = "0 B";

        SelectedInfoText.Text =
            "Select files or folders to clean.";

        try
        {
            // -------------------------------------------------
            // SCAN FILES
            // -------------------------------------------------

            List<FileItem> files =
                await _scanner.ScanFilesAsync(
                    selected.Drive.RootDirectory.FullName,
                    _cancellationSource.Token);

            FilesScannedText.Text =
                files.Count.ToString("N0");


            // -------------------------------------------------
            // BUILD FILE CANDIDATES
            // -------------------------------------------------

            ProgressText.Text =
                "Building file inventory...";

            List<CleanupCandidate> fileCandidates =
                _candidateDetector.FindCandidates(
                    files);


            // -------------------------------------------------
            // BUILD FOLDER CANDIDATES
            // -------------------------------------------------

            ProgressText.Text =
                "Building folder inventory...";

            List<CleanupCandidate> folderCandidates =
                _candidateDetector.FindFolderCandidates(
                    files);


            // -------------------------------------------------
            // COMBINE FILES + FOLDERS
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
            // ANALYZE EVERY ITEM
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

                _allCandidates.Add(
                    new CandidateViewModel(
                        candidate,
                        analysis));
            }


            // -------------------------------------------------
            // CATEGORY FILTER
            // -------------------------------------------------

            PopulateCategoryFilter();


            // -------------------------------------------------
            // APPLY FILTERS
            // -------------------------------------------------

            ApplyFilters();


            // -------------------------------------------------
            // COMPLETE
            // -------------------------------------------------

            StatusText.Text =
                "Ready";

            ProgressText.Text =
                $"Scan complete • " +
                $"{files.Count:N0} files scanned • " +
                $"{_allCandidates.Count:N0} results";

            ScanProgressBar.IsIndeterminate =
                false;

            ScanProgressBar.Value =
                100;
        }
        catch (OperationCanceledException)
        {
            StatusText.Text =
                "Cancelled";

            ProgressText.Text =
                "Scan cancelled.";

            ScanProgressBar.IsIndeterminate =
                false;

            ScanProgressBar.Value =
                0;
        }
        catch (Exception ex)
        {
            StatusText.Text =
                "Error";

            ProgressText.Text =
                "Scan failed.";

            ScanProgressBar.IsIndeterminate =
                false;

            MessageBox.Show(
                ex.Message,
                "DevClean scan error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            ScanButton.IsEnabled = true;
            QuarantineButton.IsEnabled = true;
            PermanentDeleteButton.IsEnabled = true;
        }
    }


    // =========================================================
    // CATEGORY FILTER
    // =========================================================

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

        CategoryFilter.SelectedIndex =
            index;
    }


    // =========================================================
    // FILTERS
    // =========================================================

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


        // =====================================================
        // FOLDER MODE
        //
        // Folder inventory is intentionally simple.
        //
        // IMPORTANT:
        // Category and Safety filters are ignored here.
        //
        // This means:
        // - hidden folders remain visible
        // - protected folders remain visible
        // - empty folders remain visible
        // - folders are not filtered by cleanup category
        // - folders are not filtered by AI safety level
        // =====================================================

        if (type == "Folders")
        {
            query =
                query.Where(
                    x => x.Candidate.IsFolder);
        }
        else
        {
            // =================================================
            // FILE MODE
            // =================================================

            if (type == "Files")
            {
                query =
                    query.Where(
                        x => !x.Candidate.IsFolder);
            }


            // =================================================
            // CATEGORY
            // =================================================

            if (category != "All categories")
            {
                query =
                    query.Where(
                        x => x.Category == category);
            }


            // =================================================
            // SAFETY
            // =================================================

            if (safety != "All safety levels")
            {
                SafetyLevel selectedLevel =
                    safety switch
                    {
                        "Safe" =>
                            SafetyLevel.Safe,

                        "Review" =>
                            SafetyLevel.Review,

                        "Caution" =>
                            SafetyLevel.Caution,

                        "Do not delete" =>
                            SafetyLevel.DoNotDelete,

                        _ =>
                            SafetyLevel.Review
                    };

                query =
                    query.Where(
                        x =>
                            x.Analysis.Level ==
                            selectedLevel);
            }
        }


        // =====================================================
        // POPULATE VISIBLE RESULTS
        // =====================================================

        foreach (CandidateViewModel candidate
                 in query
                     .OrderByDescending(
                         x => x.Size)
                     .ThenBy(
                         x => x.Path))
        {
            _visibleCandidates.Add(
                candidate);
        }


        // =====================================================
        // SWITCH LISTS
        // =====================================================

        bool foldersOnly =
            type == "Folders";


        if (foldersOnly)
        {
            CandidateList.Visibility =
                Visibility.Collapsed;

            FolderList.Visibility =
                Visibility.Visible;

            CandidateList.ItemsSource =
                null;

            FolderList.ItemsSource =
                _visibleCandidates;
        }
        else
        {
            CandidateList.Visibility =
                Visibility.Visible;

            FolderList.Visibility =
                Visibility.Collapsed;

            FolderList.ItemsSource =
                null;

            CandidateList.ItemsSource =
                _visibleCandidates;
        }


        // =====================================================
        // RESULT COUNT
        // =====================================================

        ResultCountText.Text =
            $"{_visibleCandidates.Count:N0} results";
    }


    // =========================================================
    // FILTER EVENT
    // =========================================================

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


    // =========================================================
    // SELECT ALL
    // =========================================================

    private void SelectAllCheckBox_Checked(
        object sender,
        RoutedEventArgs e)
    {
        if (_updatingSelection)
        {
            return;
        }

        _updatingSelection = true;

        try
        {
            // -------------------------------------------------
            // CLEAR EXISTING SELECTION
            // -------------------------------------------------

            foreach (CandidateViewModel candidate
                     in _allCandidates)
            {
                candidate.IsSelected =
                    false;
            }


            // -------------------------------------------------
            // PROCESS SHORTEST PATHS FIRST
            //
            // This allows a parent folder to own its
            // descendants without double counting.
            // -------------------------------------------------

            List<CandidateViewModel> selectable =
                _visibleCandidates
                    .Where(
                        x =>
                            x.Analysis.Level !=
                            SafetyLevel.DoNotDelete)
                    .OrderBy(
                        x =>
                            NormalizePath(
                                x.Path).Length)
                    .ToList();

            List<string> selectedPaths = [];


            foreach (CandidateViewModel candidate
                     in selectable)
            {
                string path =
                    NormalizePath(
                        candidate.Path);

                bool alreadyCovered =
                    selectedPaths.Any(
                        parent =>
                            IsSameOrDescendantPath(
                                path,
                                parent));

                if (alreadyCovered)
                {
                    continue;
                }

                candidate.IsSelected =
                    true;

                selectedPaths.Add(
                    path);
            }
        }
        finally
        {
            _updatingSelection =
                false;
        }

        UpdateSelectionDisplay();
    }


    // =========================================================
    // UNSELECT ALL
    // =========================================================

    private void SelectAllCheckBox_Unchecked(
        object sender,
        RoutedEventArgs e)
    {
        if (_updatingSelection)
        {
            return;
        }

        _updatingSelection = true;

        try
        {
            foreach (CandidateViewModel candidate
                     in _allCandidates)
            {
                candidate.IsSelected =
                    false;
            }
        }
        finally
        {
            _updatingSelection =
                false;
        }

        UpdateSelectionDisplay();
    }


    // =========================================================
    // INDIVIDUAL CHECKBOX
    // =========================================================

    private void CandidateCheckBox_Changed(
        object sender,
        RoutedEventArgs e)
    {
        if (_updatingSelection)
        {
            return;
        }

        if (sender is not CheckBox checkBox)
        {
            return;
        }

        if (checkBox.DataContext
            is not CandidateViewModel candidate)
        {
            return;
        }


        // -----------------------------------------------------
        // NEVER ALLOW DO NOT DELETE
        // -----------------------------------------------------

        if (candidate.Analysis.Level ==
            SafetyLevel.DoNotDelete)
        {
            _updatingSelection = true;

            try
            {
                candidate.IsSelected =
                    false;

                checkBox.IsChecked =
                    false;
            }
            finally
            {
                _updatingSelection =
                    false;
            }

            UpdateSelectionDisplay();

            return;
        }


        // -----------------------------------------------------
        // SMART SELECTION
        // -----------------------------------------------------

        _updatingSelection = true;

        try
        {
            if (candidate.IsSelected)
            {
                ApplySmartSelection(
                    candidate);
            }
        }
        finally
        {
            _updatingSelection =
                false;
        }

        UpdateSelectionDisplay();
    }


    // =========================================================
    // SMART PARENT / CHILD SELECTION
    // =========================================================

    private void ApplySmartSelection(
        CandidateViewModel selected)
    {
        string selectedPath =
            NormalizePath(
                selected.Path);


        // =====================================================
        // FOLDER SELECTED
        //
        // Selecting a folder means selecting the entire
        // folder tree.
        //
        // Therefore remove selected descendants.
        // =====================================================

        if (selected.Candidate.IsFolder)
        {
            foreach (CandidateViewModel candidate
                     in _allCandidates)
            {
                if (ReferenceEquals(
                        candidate,
                        selected))
                {
                    continue;
                }

                if (!candidate.IsSelected)
                {
                    continue;
                }

                if (IsSameOrDescendantPath(
                        candidate.Path,
                        selectedPath))
                {
                    candidate.IsSelected =
                        false;
                }
            }

            return;
        }


        // =====================================================
        // FILE SELECTED
        //
        // A file cannot coexist with a selected parent folder.
        // =====================================================

        foreach (CandidateViewModel candidate
                 in _allCandidates)
        {
            if (!candidate.IsSelected)
            {
                continue;
            }

            if (!candidate.Candidate.IsFolder)
            {
                continue;
            }

            string folderPath =
                NormalizePath(
                    candidate.Path);

            if (IsSameOrDescendantPath(
                    selectedPath,
                    folderPath))
            {
                candidate.IsSelected =
                    false;
            }
        }
    }


    // =========================================================
    // PATH COMPARISON
    // =========================================================

    private static bool IsSameOrDescendantPath(
        string childPath,
        string parentPath)
    {
        string child =
            NormalizePath(
                childPath);

        string parent =
            NormalizePath(
                parentPath);

        if (string.Equals(
                child,
                parent,
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string parentWithSeparator =
            parent +
            Path.DirectorySeparatorChar;

        return child.StartsWith(
            parentWithSeparator,
            StringComparison.OrdinalIgnoreCase);
    }


    // =========================================================
    // NORMALIZE PATH
    // =========================================================

    private static string NormalizePath(
        string path)
    {
        try
        {
            return Path
                .GetFullPath(path)
                .TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return path.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
        }
    }


    // =========================================================
    // LIST SELECTION
    // =========================================================

    private void CandidateList_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        CandidateViewModel? candidate =
            null;

        if (sender is ListView listView &&
            listView.SelectedItem
                is CandidateViewModel selected)
        {
            candidate =
                selected;
        }

        if (candidate == null)
        {
            return;
        }


        // Folder candidates don't necessarily have
        // meaningful analysis reasons.
        if (candidate.Candidate.IsFolder)
        {
            StatusText.Text =
                "Folder";

            ProgressText.Text =
                $"{candidate.Path} • " +
                $"{candidate.SizeDisplay}";

            return;
        }


        // Normal file information.

        StatusText.Text =
            candidate.Reason;

        ProgressText.Text =
            $"{candidate.Explanation} " +
            $"Last modified: " +
            $"{candidate.LastModifiedDisplay} • " +
            $"{candidate.AgeDisplay}";
    }


    // =========================================================
    // OPEN LOCATION
    // =========================================================

    private void OpenLocationButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        // -----------------------------------------------------
        // NORMAL FILE LIST
        // -----------------------------------------------------

        if (CandidateList.SelectedItem
            is CandidateViewModel candidate)
        {
            candidate.OpenLocation();
            return;
        }


        // -----------------------------------------------------
        // FOLDER LIST
        // -----------------------------------------------------

        if (FolderList.SelectedItem
            is CandidateViewModel folder)
        {
            folder.OpenLocation();
            return;
        }


        // -----------------------------------------------------
        // FALLBACK TO SELECTED CHECKBOX ITEM
        // -----------------------------------------------------

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


    // =========================================================
    // QUARANTINE
    // =========================================================

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
                    .Where(
                        x =>
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
            folderCount > 0 &&
            fileCount > 0
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


        QuarantineButton.IsEnabled =
            false;

        ScanButton.IsEnabled =
            false;


        try
        {
            var service =
                new QuarantineService(
                    selected.Drive.RootDirectory.FullName);


            int success = 0;
            int failed = 0;

            List<CandidateViewModel>
                successfullyQuarantined = [];


            foreach (CandidateViewModel item
                     in selectedCandidates)
            {
                CleanupResult result =
                    await service.QuarantineAsync(
                        item.Candidate);


                if (result.Success)
                {
                    success++;

                    successfullyQuarantined.Add(
                        item);
                }
                else
                {
                    failed++;
                }
            }


            // -------------------------------------------------
            // IMPORTANT:
            //
            // Only remove items from the UI when quarantine
            // actually succeeded.
            // -------------------------------------------------

            foreach (CandidateViewModel item
                     in successfullyQuarantined)
            {
                _allCandidates.Remove(
                    item);
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
        finally
        {
            ScanButton.IsEnabled =
                true;

            QuarantineButton.IsEnabled =
                true;
        }
    }


    // =========================================================
    // QUARANTINE MANAGER
    // =========================================================

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

        window.Owner =
            this;

        window.ShowDialog();
    }


    // =========================================================
    // SELECTION DISPLAY
    // =========================================================

    private void UpdateSelectionDisplay()
    {
        long selectedSize =
            _allCandidates
                .Where(
                    x => x.IsSelected)
                .Sum(
                    x => x.Size);


        int selectedCount =
            _allCandidates.Count(
                x => x.IsSelected);


        SelectedText.Text =
            FormatSize(
                selectedSize);


        SelectedInfoText.Text =
            selectedCount == 0
                ? "Select files or folders to clean."
                : $"{selectedCount:N0} item(s) selected • " +
                  $"{FormatSize(selectedSize)}";
    }


    // =========================================================
    // SIZE FORMATTER
    // =========================================================

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


        double size =
            bytes;

        int unit =
            0;


        while (size >= 1024 &&
               unit < units.Length - 1)
        {
            size /=
                1024;

            unit++;
        }


        return
            $"{size:0.0} {units[unit]}";
    }


    // =========================================================
    // DRIVE ITEM
    // =========================================================

    private class DriveItem
    {
        public DriveInfo Drive { get; set; } =
            null!;

        public string Display { get; set; } =
            string.Empty;


        public override string ToString()
        {
            return Display;
        }
    }


    // =========================================================
    // EXIT
    // =========================================================

    private void ExitButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        Close();
    }


    // =========================================================
    // PERMANENT DELETE
    // =========================================================

    private async void PermanentDeleteButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        List<CandidateViewModel>
            selectedCandidates =
                _allCandidates
                    .Where(
                        x =>
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


        // -----------------------------------------------------
        // SHOW CONFIRMATION WINDOW
        // -----------------------------------------------------

        var confirmationWindow =
            new PermanentDeleteWindow(
                selectedCandidates);

        confirmationWindow.Owner =
            this;


        bool? result =
            confirmationWindow.ShowDialog();


        if (result != true ||
            !confirmationWindow.Confirmed)
        {
            return;
        }


        PermanentDeleteButton.IsEnabled =
            false;

        QuarantineButton.IsEnabled =
            false;

        ScanButton.IsEnabled =
            false;


        try
        {
            int success = 0;
            int failed = 0;

            long deletedBytes = 0;


            foreach (CandidateViewModel item
                     in selectedCandidates)
            {
                try
                {
                    string path =
                        item.Path;


                    if (!File.Exists(path))
                    {
                        failed++;

                        continue;
                    }


                    long size =
                        item.Size;


                    await Task.Run(
                        () =>
                        {
                            File.Delete(path);
                        });


                    if (!File.Exists(path))
                    {
                        success++;

                        deletedBytes +=
                            size;
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


            // -------------------------------------------------
            // REMOVE ONLY FILES THAT ARE ACTUALLY GONE
            // -------------------------------------------------

            foreach (CandidateViewModel item
                     in selectedCandidates)
            {
                if (!File.Exists(
                        item.Path))
                {
                    _allCandidates.Remove(
                        item);
                }
            }


            ApplyFilters();

            UpdateSelectionDisplay();


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
        finally
        {
            PermanentDeleteButton.IsEnabled =
                true;

            QuarantineButton.IsEnabled =
                true;

            ScanButton.IsEnabled =
                true;
        }
    }
}