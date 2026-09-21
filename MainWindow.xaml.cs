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
using Windows.Services.Store;   
namespace DevClean;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Security.Cryptography;
using System.Text;

[ComImport]
[Guid("3E68D4BD-7135-4D10-8018-9FB6D9F33FA1")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IInitializeWithWindow
{
    void Initialize(IntPtr hwnd);
}

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
#if STORE_BUILD
        // Store/MSIX apps run sandboxed and cannot elevate (Verb="runas" will simply fail
        // certification and/or fail at runtime), so this destructive feature is hidden here.
        // The Format button now opens Windows Disk Management, which is Store-safe, so the
        // group is still hidden in Store builds to keep the UI simple, but no longer because
        // the underlying code is dangerous.
        AdvancedFormatGroup.Visibility = Visibility.Collapsed;
#endif
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
            CancellationToken token = _cancellationSource.Token;

            // Everything below — detection, sorting, safety analysis, and VM
            // construction — runs on background threads. On C:\ this is tens of
            // thousands of candidates, and doing any of it on the UI thread is
            // what froze the window at "Analyzing safety…".
            var (candidates, vms) = await Task.Run(() =>
            {
                List<CleanupCandidate> cands = _candidateDetector.FindCandidates(files)
                    .Concat(_candidateDetector.FindFolderCandidates(files))
                    .AsParallel()
                    .WithCancellation(token)
                    .WithDegreeOfParallelism(Environment.ProcessorCount)
                    .OrderByDescending(x => x.PriorityScore)
                    .ThenByDescending(x => x.Size)
                    .ToList();

                var resultArr = new SafetyAnalysis[cands.Count];
                int processed = 0;

                Parallel.For(0, cands.Count,
                    new ParallelOptions
                    {
                        CancellationToken = token,
                        MaxDegreeOfParallelism = Environment.ProcessorCount
                    },
                    i =>
                    {
                        resultArr[i] = _analyzer.AnalyzeAsync(cands[i].File, token).GetAwaiter().GetResult();
                        int done = Interlocked.Increment(ref processed);
                        if (done % 250 == 0)
                            Dispatcher.Invoke(() => ProgressText.Text = $"Analyzing… {done:N0}/{cands.Count:N0}");
                    });

                var vmList = new List<CandidateViewModel>(cands.Count);
                for (int i = 0; i < cands.Count; i++)
                    vmList.Add(new CandidateViewModel(cands[i], resultArr[i]));

                return (cands, vmList);
            }, token);

            CandidatesText.Text = candidates.Count.ToString("N0");

            // Single bulk update — the Add loop below runs before the collection
            // is bound to the ListView, so no layout passes fire here.
            _allCandidates.Clear();
            foreach (var vm in vms) _allCandidates.Add(vm);

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
        // Materialize the sort before touching the bound collection, so the LINQ
        // work is done up-front rather than interleaved with per-item notifications.
        var sorted = q.OrderByDescending(x => x.Size).ToList();
        _visibleCandidates.Clear();
        foreach (var c in sorted) _visibleCandidates.Add(c);
        ResultCountText.Text = $"{_visibleCandidates.Count:N0} results";
    }

    private void Filter_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (IsInitialized) ApplyFilters(); }

    private void SelectAllHeaderCheckBox_Checked(object sender, RoutedEventArgs e)
    {
        if (_updatingSelection) return;
        _updatingSelection = true;
        try
        {
            foreach (var c in _visibleCandidates)
                if (c.Analysis.Level != SafetyLevel.DoNotDelete) c.IsSelected = true;
        }
        finally { _updatingSelection = false; }
        UpdateSelectionDisplay();
    }

    private void SelectAllHeaderCheckBox_Unchecked(object sender, RoutedEventArgs e)
    {
        if (_updatingSelection) return;
        _updatingSelection = true;
        try { foreach (var c in _visibleCandidates) c.IsSelected = false; }
        finally { _updatingSelection = false; }
        UpdateSelectionDisplay();
    }

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
            AiFileVerdictText.Text = string.Empty;
            AiFileConsequenceText.Text = string.Empty;
            AiFileHowToText.Text = string.Empty;
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
    private async void RefreshAppsButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshAppsButton.IsEnabled = false;
        AppsStatusText.Text = "Scanning installed applications…";
        try
        {
            List<InstalledApplication> list = await Task.Run(() => InstalledAppScanner.Scan());
            _apps.Clear();
            foreach (var a in list) _apps.Add(new AppRow(a));
            AppsStatusText.Text = $"{list.Count:N0} installed applications. Select one and press Uninstall to launch its own uninstaller.";
        }
        finally { RefreshAppsButton.IsEnabled = true; }
    }

    private async void UninstallAppButton_Click(object sender, RoutedEventArgs e)
    {
        if (AppsListView.SelectedItem is not AppRow row) { MessageBox.Show("Select an app."); return; }
        if (MessageBox.Show($"Launch the uninstaller for '{row.App.Name}'?", "Uninstall", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        UninstallAppButton.IsEnabled = false;
        try
        {
            bool started = await Task.Run(() => InstalledAppScanner.Uninstall(row.App));
            if (!started) MessageBox.Show("Could not launch uninstaller.");
        }
        finally { UninstallAppButton.IsEnabled = true; }
    }

    // =========================================================
    // AI TAB (Mistral)
    // =========================================================
    // API key is protected with Windows DPAPI, scoped to the current Windows user account.
    // This means the encrypted bytes on disk are meaningless if copied to another machine or
    // read by another user account — only the same Windows user profile can decrypt them.
    private void LoadApiKey()
    {
        try
        {
            if (!File.Exists(ApiKeyFile)) return;
            byte[] encrypted = File.ReadAllBytes(ApiKeyFile);
            byte[] plain = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            ApiKeyBox.Password = Encoding.UTF8.GetString(plain);
        }
        catch
        {
            // Corrupt, foreign, or unreadable key file — leave the box empty rather than crash.
        }
    }

    private void SaveKeyButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(AppDataDir);
            byte[] plain = Encoding.UTF8.GetBytes(ApiKeyBox.Password);
            byte[] encrypted = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(ApiKeyFile, encrypted);
            AiStatusText.Text = "API key saved (encrypted to your Windows account).";
        }
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

        // Restore the user's last theme choice.
        _isDarkMode = AppSettings.Instance.IsDarkMode;
        ApplyTheme(_isDarkMode);
        ThemeToggleBtn.Content = _isDarkMode ? "☀️" : "🌙";
        ThemeBox.SelectedIndex = _isDarkMode ? 1 : 0;
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

        // Microsoft Store apps cannot shell out to format.com with an elevation request —
        // that is a certification blocker. Instead we hand off to Windows' own Disk
        // Management, which handles the elevation prompt and confirmation itself and needs
        // no special capability here. This is safe for both the Store and sideload builds,
        // so no #if STORE_BUILD guard is needed.
        MessageBox.Show(
            $"For your safety, DevClean doesn't format drives directly.\n\n" +
            $"Opening Disk Management — right-click {drive} there and choose \"Format...\".",
            "Open Disk Management", MessageBoxButton.OK, MessageBoxImage.Information);
        try
        {
            Process.Start(new ProcessStartInfo("diskmgmt.msc") { UseShellExecute = true });
        }
        catch (Exception ex) { MessageBox.Show("Could not open Disk Management: " + ex.Message); }
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
        PersistThemeChoice();
    }

    private void ThemeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ThemeBox.SelectedItem is ComboBoxItem item && item.Content is string txt)
        {
            _isDarkMode = txt == "Dark";
            ApplyTheme(_isDarkMode);
            ThemeToggleBtn.Content = _isDarkMode ? "☀️" : "🌙";
            PersistThemeChoice();
        }
    }

    private void PersistThemeChoice()
    {
        AppSettings.Instance.IsDarkMode = _isDarkMode;
        AppSettings.Instance.Save();
    }

    // Theme.xaml's *first* merged dictionary is always the active color-token
    // dictionary (Theme/Colors.Light.xaml or Theme/Colors.Dark.xaml — see
    // Theme.xaml). Every brush every control template/style binds to via
    // DynamicResource lives there, so swapping that one dictionary in one shot
    // repaints the entire app — native WPF chrome included — instead of the
    // old approach of patching seven hardcoded brushes by hand.
    private void ApplyTheme(bool dark)
{
    var themeDictionary = Application.Current.Resources.MergedDictionaries.FirstOrDefault();
    if (themeDictionary == null || themeDictionary.MergedDictionaries.Count == 0) return;

    var colorsUri = new Uri(
        dark ? "pack://application:,,,/Theme/Colors.Dark.xaml" : "pack://application:,,,/Theme/Colors.Light.xaml",
        UriKind.Absolute);

    var freshColors = new ResourceDictionary { Source = colorsUri };
    var activeColors = themeDictionary.MergedDictionaries[0];
    foreach (var key in freshColors.Keys)
    {
        activeColors[key] = freshColors[key];
    }
}

    private async void CheckWithAiButton_Click(object sender, RoutedEventArgs e)
    {
        if (CandidateList.SelectedItem is not CandidateViewModel c) { MessageBox.Show("Select an item first."); return; }
        string key = ApiKeyBox.Password;
        if (string.IsNullOrWhiteSpace(key)) { AiFileVerdictText.Text = "Enter and save your API key on the AI tab first."; return; }

        CheckWithAiButton.IsEnabled = false;
        AiFileVerdictText.Text = "Asking AI…";
        AiFileConsequenceText.Text = string.Empty;
        AiFileHowToText.Text = string.Empty;
        try
        {
            AiFileVerdict verdict = await MistralCleanAdvisor.AnalyzeSingleFileAsync(c.Candidate.File, key);
            if (!verdict.Success)
            {
                AiFileVerdictText.Text = "AI check failed: " + verdict.RawError;
                return;
            }
            AiFileVerdictText.Text = verdict.IsSafe ? "✅ Safe to delete" : "⚠️ Review before deleting";
            AiFileConsequenceText.Text = "If deleted: " + verdict.Consequence;
            AiFileHowToText.Text = "Safe removal: " + verdict.SafeDeletionSteps;
        }
        finally { CheckWithAiButton.IsEnabled = true; }
    }
       private const string SupportDeveloperProductId = "devclean.supportdeveloper";

        private async void SupportDeveloperButton_Click(object sender, RoutedEventArgs e)
{
    SupportDeveloperButton.IsEnabled = false;
    try
    {
        StoreContext context = StoreContext.GetDefault();

        // Tell the Store API which window owns the purchase dialog —
        // required for classic WPF apps, not needed for WinUI/UWP.
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        ((IInitializeWithWindow)(object)context).Initialize(hwnd);

        var result = await context.RequestPurchaseAsync(SupportDeveloperProductId);

            switch (result.Status)
            {
                case StorePurchaseStatus.Succeeded:
                    MessageBox.Show("Thank you so much for supporting DevClean! 💙", "Thanks!");
                    break;
                case StorePurchaseStatus.AlreadyPurchased:
                    MessageBox.Show("You've already supported DevClean — thank you again!", "Thanks!");
                    break;
                case StorePurchaseStatus.NotPurchased:
                    break;
                case StorePurchaseStatus.NetworkError:
                    MessageBox.Show("Network error — please check your connection and try again.");
                    break;
                default:
                    MessageBox.Show("Something went wrong with the purchase. Please try again later.");
                    break;
            }
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Could not open the purchase dialog: " + ex.Message);
                }
                    finally { SupportDeveloperButton.IsEnabled = true; }
        }

        private void PrivacyLink_RequestNavigate(object sender,
    System.Windows.Navigation.RequestNavigateEventArgs e)
{
    Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
    e.Handled = true;
}
}