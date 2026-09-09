using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.IO;
namespace DevClean.Models;

public class CandidateViewModel : INotifyPropertyChanged
{
    private bool _isSelected;

    public CleanupCandidate Candidate { get; }

    public SafetyAnalysis Analysis { get; }

    public CandidateViewModel(
        CleanupCandidate candidate,
        SafetyAnalysis analysis)
    {
        Candidate = candidate;
        Analysis = analysis;
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
                return;

            _isSelected = value;
            OnPropertyChanged();
        }
    }

    public string FileName =>
        Candidate.File.Name;

    public string Path =>
        Candidate.TargetPath;

    public long Size =>
        Candidate.File.Size;

    public string SizeDisplay =>
        FormatSize(Size);

    public string Category =>
        Candidate.Category.ToString();

    public string Location =>
        Candidate.Location.ToString();

    public string TargetType =>
        Candidate.IsFolder ? "FOLDER" : "FILE";

    public string AgeDisplay =>
        FormatAge(
            DateTime.Now - Candidate.File.LastModified);

    public DateTime LastModified =>
        Candidate.File.LastModified;

    public string LastModifiedDisplay =>
        Candidate.File.LastModified.ToString(
            "yyyy-MM-dd HH:mm");

    public string Safety =>
        Analysis.Level switch
        {
            SafetyLevel.Safe => "SAFE",
            SafetyLevel.Review => "REVIEW",
            SafetyLevel.Caution => "CAUTION",
            SafetyLevel.DoNotDelete => "DO NOT DELETE",
            _ => "UNKNOWN"
        };

    public string SafetyIcon =>
        Analysis.Level switch
        {
            SafetyLevel.Safe => "🟢",
            SafetyLevel.Review => "🟡",
            SafetyLevel.Caution => "🟠",
            SafetyLevel.DoNotDelete => "🔴",
            _ => "⚪"
        };

    public string Reason =>
        Analysis.Reason;

    public string Explanation =>
        Analysis.Explanation;

    public int Confidence =>
        Analysis.Confidence;

    public event PropertyChangedEventHandler? PropertyChanged;

    public void OpenLocation()
    {
        try
        {
            string targetPath =
                Candidate.TargetPath;

            if (Candidate.IsFolder)
            {
                if (!Directory.Exists(targetPath))
                {
                    return;
                }

                Process.Start(
                    new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments =
                            $"\"{targetPath}\"",
                        UseShellExecute = true
                    });

                return;
            }

            if (!File.Exists(targetPath))
            {
                return;
            }

            Process.Start(
                new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments =
                        $"/select,\"{targetPath}\"",
                    UseShellExecute = true
                });
        }
        catch
        {
            // Explorer could not be opened.
        }
    }

    private static string FormatAge(
        TimeSpan age)
    {
        if (age.TotalDays < 1)
        {
            return "Less than 1 day old";
        }

        if (age.TotalDays < 30)
        {
            return $"{(int)age.TotalDays} days old";
        }

        if (age.TotalDays < 365)
        {
            int months =
                Math.Max(
                    1,
                    (int)(age.TotalDays / 30));

            return $"{months} months old";
        }

        int years =
            (int)(age.TotalDays / 365);

        int remainingMonths =
            (int)((age.TotalDays % 365) / 30);

        if (remainingMonths == 0)
        {
            return $"{years} year{(years == 1 ? "" : "s")} old";
        }

        return
            $"{years}y {remainingMonths}m old";
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

    private void OnPropertyChanged(
        [CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(propertyName));
    }
}