using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;

namespace LumePDF;

public partial class App : Application
{
    MainWindow? _window;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) => LogCrash(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogCrash(e.ExceptionObject as Exception);
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Warm up PDFium on its worker thread while the window is being built.
        Pdf.PdfWorker.Start();

        _window = new MainWindow();
        _window.Activate();

        _ = OpenFromCommandLineAsync(_window, Environment.GetCommandLineArgs());
    }

    // LumePDF.exe [file.pdf] [--page N] [--search "text"]
    static async Task OpenFromCommandLineAsync(MainWindow window, string[] argv)
    {
        string? file = null, search = null;
        int page = 0;
        for (int i = 1; i < argv.Length; i++)
        {
            if (argv[i] == "--page" && i + 1 < argv.Length)
                int.TryParse(argv[++i], out page);
            else if (argv[i] == "--search" && i + 1 < argv.Length)
                search = argv[++i];
            else
                file ??= argv[i];
        }
        if (file == null)
            return;
        await window.OpenFileAsync(file, page: page - 1);
        if (!string.IsNullOrWhiteSpace(search))
            window.Search(search);
    }

    static void LogCrash(Exception? ex)
    {
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LumePDF");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "crash.log"), $"{DateTime.Now:O}\n{ex}\n\n");
        }
        catch
        {
            // Nothing sensible left to do.
        }
    }
}
