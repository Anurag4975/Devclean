using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace DevClean;

public partial class App : Application
{
    private static string LogFile =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DevClean", "crash.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Crash-proofing: never let an unhandled exception silently close the app.
        // This fixes "app closes by itself" on permanent delete and any other unhandled error.
        DispatcherUnhandledException += (s, args) =>
        {
            ShowDetailed("DevClean — error recovered", args.Exception, recovered: true);
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                ShowDetailed("DevClean — fatal error", ex, recovered: false);
        };

        TaskScheduler.UnobservedTaskException += (s, args) => args.SetObserved();
    }

    private static void ShowDetailed(string title, Exception ex, bool recovered)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Something went wrong:");
        sb.AppendLine();
        Exception? cur = ex;
        int depth = 0;
        while (cur != null)
        {
            sb.AppendLine((depth == 0 ? "" : "Inner: ") + cur.GetType().Name + ": " + cur.Message);
            sb.AppendLine(cur.StackTrace);
            sb.AppendLine();
            cur = cur.InnerException;
            depth++;
        }
        if (recovered) sb.AppendLine("DevClean will continue running.");

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogFile)!);
            File.AppendAllText(LogFile, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {title}\n{sb}\n---\n");
        }
        catch { /* logging must never itself crash the app */ }

        MessageBox.Show(sb.ToString(), title, MessageBoxButton.OK,
            recovered ? MessageBoxImage.Warning : MessageBoxImage.Error);
    }
}