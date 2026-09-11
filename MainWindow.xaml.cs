using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using DevClean.AI;
using DevClean.Apps;
using DevClean.Candidates;
using DevClean.Cleanup;
using DevClean.Models;
using DevClean.Scanner;
using DevClean.Settings;
using DevClean.Smart;
using System.Windows.Media;
namespace DevClean;
using System.Threading;
using System.Threading.Tasks;

public partial class MainWindow : Window
{
    private readonly DiskScanner _scanner = new();
    private readonly LocalSafetyAnalyzer _analyzer = new();
    private readonly CandidateDetector _candidateDetector = new();
    private readonly ObservableCollection<CandidateViewModel> _allCandidates = [];
    private readonly ObservableCollection<CandidateViewModel> _visibleCandidates = [];
    private readonly ObservableCollection<AppRow> _apps = [];
    private readonly ObservableCollection<AiRow> _aiRows = [];
    private CancellationTokenSource? _cancellationSource;
    private bool _updatingSelection;
    private List<FileItem> _lastScanFiles = [];
    private List<AiRecommendedItem> _aiRecommended = [];

    private static string AppDataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DevClean");
    private static string ApiKeyFile => Path.Combine(AppDataDir, "mistral.key");

    public MainWindow()
    {
        InitializeComponent();
        LoadDrives();
        InitFilters();
        CandidateList.ItemsSource = _visibleCandidates;
        AppsListView.ItemsSource = _apps;
        AiResultsList.ItemsSource = _aiRows;
        LoadApiKey();
        LoadSettingsIntoUi();
        foreach (DriveInfo d in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed))
            FormatDriveCombo.Items.Add(d.Name);
        if (FormatDriveCombo.Items.Count > 0) FormatDriveCombo.SelectedIndex = 0;
    }

    // =========================================================
    // DRIVES / FREE SPACE
    // =========================================================
    private void LoadDrives()
    {
        DriveComboBox.Items.Clear();
        foreach (DriveInfo drive in DriveInfo.GetDrives()
            .Where(d => d.IsReady && d.DriveType == DriveType.Fixed))
        {
            DriveComboBox.Items.Add(new DriveItem { Drive = drive, Display = $"{drive.Name} ({FormatSize(drive.AvailableFreeSpace)} free)" });
        }
        if (DriveComboBox.Items.Count > 0) DriveComboBox.SelectedIndex = 0;
        UpdateFreeSpace();
    }

    private void UpdateFreeSpace()
    {
        if (DriveComboBox.SelectedItem is DriveItem di)
        {
            try { FreeSpaceText.Text = $"{FormatSize(di.Drive.AvailableFreeSpace)}"; }
            catch { FreeSpaceText.Text = "—"; }
        }
    }

    private void InitFilters()
    {
        CategoryFilter.Items.Clear(); CategoryFilter.Items.Add("All categories"); CategoryFilter.SelectedIndex = 0;
        SafetyFilter.Items.Clear();
        SafetyFilter.Items.Add("All"); SafetyFilter.Items.Add("Safe"); SafetyFilter.Items.Add("Review");
        SafetyFilter.Items.Add("Caution"); SafetyFilter.Items.Add("Do not delete");
        SafetyFilter.SelectedIndex = 0;
    }

    // =========================================================
    // SCAN (parallel + live progress)
    // =========================================================
    private async void ScanButton_Click(object sender, RoutedEventArgs e)
    {
        if (DriveComboBox.SelectedItem is not DriveItem selected) { MessageBox.Show("Select a drive."); return; }
        _cancellationSource?.Cancel();
        _cancellationSource = new CancellationTokenSource();
        SetUiEnabled(false);
        ScanProgressBar.IsIndeterminate = true;
        ProgressText.Text = "Scanning (parallel)…";
        _allCandidates.Clear(); _visibleCandidates.Clear();
        FilesScannedText.Text = "0"; CandidatesText.Text = "0"; SelectedText.Text = "0 B";

        try
{
    List<FileItem> files = await _scanner.ScanFilesAsync(
        selected.Drive.RootDirectory.FullName,
        count => Dispatcher.Invoke(() => ProgressText.Text = $"Scanning… {count:N0} files found…"),
        _cancellationSource.Token);
    _lastScanFiles = files;
    FilesScannedText.Text = files.Count.ToString("N0");

    ProgressText.Text = "Analyzing safety…";
    List<CleanupCandidate> candidates = _candidateDetector.FindCandidates(files)
        .Concat(_candidateDetector.FindFolderCandidates(files))
        .OrderByDescending(x => x.PriorityScore).ThenByDescending(x => x.Size).ToList();
    CandidatesText.Text = candidates.Count.ToString("N0");

    CancellationToken token = _cancellationSource.Token;
    var results = new SafetyAnalysis[candidates.Count];
    int processedCount = 0;

    await Task.Run(() =>
    {
        Parallel.For(0, candidates.Count,
            new ParallelOptions
            {
                CancellationToken = token,
                MaxDegreeOfParallelism = Environment.ProcessorCount
            },
            i =>
            {
                results[i] = _analyzer.AnalyzeAsync(candidates[i].File, token).GetAwaiter().GetResult();

                int done = Interlocked.Increment(ref processedCount);
                if (done % 250 == 0)
                    Dispatcher.Invoke(() => ProgressText.Text = $"Analyzing… {done:N0}/{candidates.Count:N0}");
            });
    }, token);

    for (int i = 0; i < candidates.Count; i++)
        _allCandidates.Add(new CandidateViewModel(candidates[i], results[i]));

    PopulateCategoryFilter();
    ApplyFilters();

    ScanProgressBar.IsIndeterminate = false; ScanProgressBar.Value = 100;
    ProgressText.Text = $"Done — {files.Count:N0} files, {_allCandidates.Count:N0} candidates. Press Smart Clean.";
    UpdateFreeSpace();

    if (SimpleModeCheck.IsChecked == true) ApplySmartSelection(false);
}
        catch (OperationCanceledException) { ProgressText.Text = "Cancelled."; }
        finally { SetUiEnabled(true); }
    }

    private void SetUiEnabled(bool on)
    {
        ScanButton.IsEnabled = on; SmartCleanButton.IsEnabled = on;
        QuarantineButton.IsEnabled = on; PermanentDeleteButton.IsEnabled = on;
        AiAnalyzeButton.IsEnabled = on;
    }

    // =========================================================
    // SMART CLEAN (one-click widget)
    // =========================================================
    private void SmartCleanButton_Click(object sender, RoutedEventArgs e) => ApplySmartSelection(true);

    private void ApplySmartSelection(bool showMessage)
    {
        if (_allCandidates.Count == 0) { if (showMessage) MessageBox.Show("Scan a drive first."); return; }
        SmartCleanPlan plan = SmartCleanAdvisor.Plan(_allCandidates.Select(c => (c.Candidate, c.Analysis)));
        _updatingSelection = true;
        try
        {
            foreach (var c in _allCandidates) c.IsSelected = false;
            var paths = new HashSet<string>(plan.SafeItems.Select(x => Norm(x.TargetPath)), StringComparer.OrdinalIgnoreCase);
            foreach (var c in _allCandidates) if (paths.Contains(Norm(c.Path))) c.IsSelected = true;
        }
        finally { _updatingSelection = false; }
        UpdateSelectionDisplay();
        ProgressText.Text = plan.Summary;
        if (showMessage) MessageBox.Show(plan.Summary + "\n\nPress 'Quarantine Selected' to move them (fully restorable).", "Smart Clean");
    }

    // =========================================================
    // FILTERS + SELECT ALL (safe-only)
    // =========================================================
    private void PopulateCategoryFilter()
    {
        string? cur = CategoryFilter.SelectedItem?.ToString();
        CategoryFilter.Items.Clear(); CategoryFilter.Items.Add("All categories");
        foreach (string cat in _allCandidates.Select(x => x.Category).Distinct().OrderBy(x => x)) CategoryFilter.Items.Add(cat);
        int idx = 0;
        if (cur is not null) for (int i = 0; i < CategoryFilter.Items.Count; i++)
                if (CategoryFilter.Items[i]?.ToString() == cur) { idx = i; break; }
        CategoryFilter.SelectedIndex = idx;
    }

    private void ApplyFilters()
    {
        _visibleCandidates.Clear();
        string cat = CategoryFilter.SelectedItem?.ToString() ?? "All categories";
        string saf = SafetyFilter.SelectedItem?.ToString() ?? "All";
        string type = (TypeFilter.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "All";
        IEnumerable<CandidateViewModel> q = _allCandidates;
        if (type == "Files") q = q.Where(x => !x.Candidate.IsFolder);
        else if (type == "Folders") q = q.Where(x => x.Candidate.IsFolder);
        if (cat != "All categories") q = q.Where(x => x.Category == cat);
        if (saf != "All")
        {
            SafetyLevel lvl = saf switch { "Safe" => SafetyLevel.Safe, "Review" => SafetyLevel.Review, "Caution" => SafetyLevel.Caution, _ => SafetyLevel.DoNotDelete };
            q = q.Where(x => x.Analysis.Level == lvl);
        }
        foreach (var c in q.OrderByDescending(x => x.Size)) _visibleCandidates.Add(c);
        ResultCountText.Text = $"{_visibleCandidates.Count:N0} results";
    }

    private void Filter_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (IsInitialized) ApplyFilters(); }

    private void SelectAllCheckBox_Checked(object sender, RoutedEventArgs e)
    {
        if (_updatingSelection) return;
        _updatingSelection = true;
        try
        {
            foreach (var c in _allCandidates) c.IsSelected = false;
            foreach (var c in _visibleCandidates
                .Where(x => x.Analysis.Level == SafetyLevel.Safe && x.Analysis.Confidence >= 80))
                c.IsSelected = true;
        }
        finally { _updatingSelection = false; }
        UpdateSelectionDisplay();
    }

    private void SelectAllCheckBox_Unchecked(object sender, RoutedEventArgs e)
    {
        if (_updatingSelection) return;
        _updatingSelection = true;
        try { foreach (var c in _allCandidates) c.IsSelected = false; }
        finally { _updatingSelection = false; }
        UpdateSelectionDisplay();
    }

    private void CandidateCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_updatingSelection) return;
        if (sender is CheckBox cb && cb.DataContext is CandidateViewModel c && c.Analysis.Level == SafetyLevel.DoNotDelete)
        {
            _updatingSelection = true;
            try { c.IsSelected = false; cb.IsChecked = false; }
            finally { _updatingSelection = false; }
        }
        UpdateSelectionDisplay();
    }

    // =========================================================
    // DETAILS PANEL (detailed reasoning)
    // =========================================================
    private void CandidateList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CandidateList.SelectedItem is CandidateViewModel c)
        {
            DetailReasonText.Text = $"{c.SafetyIcon} {c.Safety} ({c.Analysis.Confidence}% confidence) — {c.Reason}";
            DetailExplanationText.Text = c.Explanation;
            DetailEvidenceText.Text = c.Analysis.Evidence.Count > 0
                ? "Evidence: " + string.Join(" • ", c.Analysis.Evidence)
                : string.Empty;
        }
    }

    private void OpenLocationButton_Click(object sender, RoutedEventArgs e)
    {
        if (CandidateList.SelectedItem is CandidateViewModel c) c.OpenLocation();
        else MessageBox.Show("Select an item first.");
    }

    // =========================================================
    // QUARANTINE
    // =========================================================
    private async void QuarantineButton_Click(object sender, RoutedEventArgs e)
    {
        if (DriveComboBox.SelectedItem is not DriveItem sel) return;
        var selected = _allCandidates.Where(x => x.IsSelected && x.Analysis.Level != SafetyLevel.DoNotDelete).ToList();
        if (selected.Count == 0) { MessageBox.Show("Select items, or press Smart Clean."); return; }
        long total = selected.Sum(x => x.Size);
        if (MessageBox.Show($"Move {selected.Count:N0} items ({FormatSize(total)}) to quarantine?\nFully restorable.",
            "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        SetUiEnabled(false);
        try
        {
            var svc = new QuarantineService(sel.Drive.RootDirectory.FullName);
            int ok = 0, fail = 0;
            foreach (var item in selected)
                if ((await svc.QuarantineAsync(item.Candidate)).Success) { ok++; _allCandidates.Remove(item); }
                else fail++;
            ApplyFilters(); UpdateSelectionDisplay(); UpdateFreeSpace();
            MessageBox.Show($"Done. Quarantined: {ok:N0} • Failed: {fail:N0} • {FormatSize(total)}");
        }
        finally { SetUiEnabled(true); }
    }

    private void QuarantineManagerButton_Click(object sender, RoutedEventArgs e)
    {
        if (DriveComboBox.SelectedItem is not DriveItem sel) return;
        new QuarantineWindow(sel.Drive.RootDirectory.FullName) { Owner = this }.ShowDialog();
        UpdateFreeSpace();
    }

    // =========================================================
    // PERMANENT DELETE (crash-proof — everything wrapped)
    // =========================================================
    private async void PermanentDeleteButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var selected = _allCandidates
                .Where(x => x.IsSelected && x.Analysis.Level != SafetyLevel.DoNotDelete && !x.Candidate.IsFolder).ToList();
            if (selected.Count == 0) { MessageBox.Show("Select files first. Folders: use Quarantine then delete from Quarantine Manager."); return; }
            var confirm = new PermanentDeleteWindow(selected) { Owner = this };
            if (confirm.ShowDialog() != true || !confirm.Confirmed) return;

            SetUiEnabled(false);
            int ok = 0, fail = 0; long freed = 0;
            foreach (var item in selected)
            {
                try
                {
                    if (!File.Exists(item.Path)) { fail++; continue; }
                    long sz = item.Size;
                    await Task.Run(() => File.Delete(item.Path));
                    if (!File.Exists(item.Path)) { ok++; freed += sz; _allCandidates.Remove(item); } else fail++;
                }
                catch { fail++; }
            }
            ApplyFilters(); UpdateSelectionDisplay(); UpdateFreeSpace();
            MessageBox.Show($"Deleted: {ok:N0} • Failed: {fail:N0} • Freed: {FormatSize(freed)}", "Done",
                MessageBoxButton.OK, fail == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show("Error: " + ex.Message, "Permanent delete", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { SetUiEnabled(true); }
    }

    // =========================================================
    // APPS TAB
    // =========================================================
    private void RefreshAppsButton_Click(object sender, RoutedEventArgs e)
    {
        _apps.Clear();
        List<InstalledApplication> list = InstalledAppScanner.Scan();
        foreach (var a in list) _apps.Add(new AppRow(a));
        AppsStatusText.Text = $"{list.Count:N0} installed applications. Select one and press Uninstall to launch its own uninstaller.";
    }

    private void UninstallAppButton_Click(object sender, RoutedEventArgs e)
    {
        if (AppsListView.SelectedItem is not AppRow row) { MessageBox.Show("Select an app."); return; }
        if (MessageBox.Show($"Launch the uninstaller for '{row.App.Name}'?", "Uninstall", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        if (!InstalledAppScanner.Uninstall(row.App)) MessageBox.Show("Could not launch uninstaller.");
    }

    // =========================================================
    // AI TAB (Mistral)
    // =========================================================
    private void LoadApiKey() { try { if (File.Exists(ApiKeyFile)) ApiKeyBox.Password = File.ReadAllText(ApiKeyFile); } catch { } }

    private void SaveKeyButton_Click(object sender, RoutedEventArgs e)
    {
        try { Directory.CreateDirectory(AppDataDir); File.WriteAllText(ApiKeyFile, ApiKeyBox.Password); AiStatusText.Text = "API key saved."; }
        catch (Exception ex) { MessageBox.Show("Could not save key: " + ex.Message); }
    }

    private async void AiAnalyzeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_lastScanFiles.Count == 0) { AiStatusText.Text = "Scan a drive first (Clean tab)."; return; }
        string key = ApiKeyBox.Password;
        if (string.IsNullOrWhiteSpace(key)) { AiStatusText.Text = "Enter and save your Mistral API key first."; return; }
        AiAnalyzeButton.IsEnabled = false; AiStatusText.Text = "Sending manifest to Mistral… (top 1500 largest files)";
        try
        {
            AiCleanPlan plan = await MistralCleanAdvisor.AnalyzeAsync(_lastScanFiles, key);
            if (!plan.Success) { AiStatusText.Text = "AI error: " + plan.RawError; return; }
            _aiRecommended = plan.Recommended;
            _aiRows.Clear();
            foreach (var r in plan.Recommended) _aiRows.Add(new AiRow { Path = r.Path, Reason = r.Reason });
            AiStatusText.Text = $"AI analyzed {plan.FilesAnalyzed:N0} files, recommends {plan.Recommended.Count:N0} items to quarantine.";
            AiSummaryText.Text = "Review the list, then press 'Quarantine AI-selected' to move them (still fully restorable).";
        }
        finally { AiAnalyzeButton.IsEnabled = true; }
    }

    private void AiQuarantineButton_Click(object sender, RoutedEventArgs e)
    {
        if (_aiRecommended.Count == 0) { AiStatusText.Text = "Run AI analysis first."; return; }
        var paths = new HashSet<string>(_aiRecommended.Select(r => Norm(r.Path)), StringComparer.OrdinalIgnoreCase);
        _updatingSelection = true;
        try
        {
            foreach (var c in _allCandidates) c.IsSelected = paths.Contains(Norm(c.Path));
        }
        finally { _updatingSelection = false; }
        UpdateSelectionDisplay();
        MainTabs.SelectedIndex = 0; // switch to Clean tab
        MessageBox.Show($"{_allCandidates.Count(x => x.IsSelected):N0} AI-recommended items selected. Press 'Quarantine Selected' on the Clean tab.");
    }

    // =========================================================
    // SETTINGS TAB
    // =========================================================
    private void LoadSettingsIntoUi()
    {
        RetentionDaysBox.Text = AppSettings.Instance.QuarantineRetentionDays.ToString();
        AutoPurgeCheck.IsChecked = AppSettings.Instance.AutoPurgeExpiredQuarantine;
        SimpleModeCheck.IsChecked = AppSettings.Instance.SimpleMode;
    }

    private async void SaveSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (int.TryParse(RetentionDaysBox.Text, out int days) && days > 0)
            AppSettings.Instance.QuarantineRetentionDays = days;
        AppSettings.Instance.AutoPurgeExpiredQuarantine = AutoPurgeCheck.IsChecked == true;
        AppSettings.Instance.SimpleMode = SimpleModeCheck.IsChecked == true;
        AppSettings.Instance.Save();
        DriveItem? sel = DriveComboBox.SelectedItem as DriveItem;
        if (AppSettings.Instance.AutoPurgeExpiredQuarantine && sel is not null)
        {
            int purged = await new QuarantineService(sel.Drive.RootDirectory.FullName)
                .PurgeExpiredAsync(AppSettings.Instance.QuarantineRetentionDays);
            MessageBox.Show($"Settings saved. Purged {purged:N0} expired quarantine items.");
        }
        else MessageBox.Show("Settings saved.");
    }

    private void FormatButton_Click(object sender, RoutedEventArgs e)
    {
        if (FormatDriveCombo.SelectedItem is not string drive) return;
        string letter = drive.TrimEnd('\\', ':');
        if (MessageBox.Show(
            $"WARNING: Formatting {drive} will PERMANENTLY DESTROY ALL DATA on it.\n\n" +
            $"This cannot be undone. Windows will ask you to confirm. Continue?",
            "FORMAT DRIVE — DANGER", MessageBoxButton.YesNo, MessageBoxImage.Stop) != MessageBoxResult.Yes) return;
        try
        {
            Process.Start(new ProcessStartInfo("cmd.exe", $"/k format {letter}: /Q /X")
            { UseShellExecute = true, Verb = "runas" });
        }
        catch (Exception ex) { MessageBox.Show("Could not start format: " + ex.Message); }
    }

    // =========================================================
    // HELPERS
    // =========================================================
    private void UpdateSelectionDisplay()
    {
        long sz = _allCandidates.Where(x => x.IsSelected).Sum(x => x.Size);
        int n = _allCandidates.Count(x => x.IsSelected);
        SelectedText.Text = FormatSize(sz);
        DetailReasonText.Text = n == 0 ? "Select an item to see why." : $"{n:N0} selected • {FormatSize(sz)} — goes to quarantine, restorable.";
    }

    private static string Norm(string p)
    {
        try { return Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        catch { return p.TrimEnd('\\', '/'); }
    }

    private static string FormatSize(long bytes)
    {
        string[] u = ["B", "KB", "MB", "GB", "TB"];
        double s = bytes; int i = 0;
        while (s >= 1024 && i < u.Length - 1) { s /= 1024; i++; }
        return $"{s:0.0} {u[i]}";
    }

    private class DriveItem { public DriveInfo Drive { get; set; } = null!; public string Display { get; set; } = ""; public override string ToString() => Display; }

    private class AppRow
    {
        public InstalledApplication App { get; }
        public AppRow(InstalledApplication a) { App = a; }
        public string Name => App.Name;
        public string Publisher => App.Publisher;
        public string SizeDisplay => App.EstimatedSize > 0 ? FormatSize(App.EstimatedSize) : "—";
        public string InstallDateDisplay => App.InstallDate > DateTime.MinValue ? App.InstallDate.ToString("yyyy-MM-dd") : "—";
    }

    private class AiRow { public string Path { get; set; } = ""; public string Reason { get; set; } = ""; }

            // === THEME SWITCH ===
        private bool _isDarkMode;

        private void ThemeToggleBtn_Click(object sender, RoutedEventArgs e)
        {
            _isDarkMode = !_isDarkMode;
            ApplyTheme(_isDarkMode);
            ThemeToggleBtn.Content = _isDarkMode ? "☀️" : "🌙";
            ThemeBox.SelectedIndex = _isDarkMode ? 1 : 0;
        }

        private void ThemeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ThemeBox.SelectedItem is ComboBoxItem item && item.Content is string txt)
            {
                _isDarkMode = txt == "Dark";
                ApplyTheme(_isDarkMode);
                ThemeToggleBtn.Content = _isDarkMode ? "☀️" : "🌙";
            }
        }

        private void ApplyTheme(bool dark)
        {
            var dict = Application.Current.Resources.MergedDictionaries.FirstOrDefault();
            if (dict == null) return;

            if (dark)
            {
                dict["AppBackgroundBrush"] = new SolidColorBrush(Color.FromRgb(28, 28, 30));
                dict["CardBackgroundBrush"] = new SolidColorBrush(Color.FromRgb(44, 44, 46));
                dict["CardAltBrush"] = new SolidColorBrush(Color.FromRgb(58, 58, 60));
                dict["TextPrimaryBrush"] = new SolidColorBrush(Color.FromRgb(240, 240, 242));
                dict["TextSecondaryBrush"] = new SolidColorBrush(Color.FromRgb(170, 170, 178));
                dict["BorderBrush"] = new SolidColorBrush(Color.FromRgb(58, 58, 60));
                dict["TabActiveBrush"] = new SolidColorBrush(Color.FromRgb(20, 60, 100));
            }
            else
            {
                dict["AppBackgroundBrush"] = new SolidColorBrush(Color.FromRgb(240, 242, 245));
                dict["CardBackgroundBrush"] = new SolidColorBrush(Colors.White);
                dict["CardAltBrush"] = new SolidColorBrush(Color.FromRgb(248, 249, 250));
                dict["TextPrimaryBrush"] = new SolidColorBrush(Color.FromRgb(26, 26, 31));
                dict["TextSecondaryBrush"] = new SolidColorBrush(Color.FromRgb(90, 92, 102));
                dict["BorderBrush"] = new SolidColorBrush(Color.FromRgb(226, 229, 234));
                dict["TabActiveBrush"] = new SolidColorBrush(Color.FromRgb(232, 240, 254));
            }
        }

}