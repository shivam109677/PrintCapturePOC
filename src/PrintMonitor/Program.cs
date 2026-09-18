using System.Security.Principal;
using PrintCapture;
using PrintMonitor;

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("PrintMonitor uses the Windows Print Spooler and must run on Windows.");
    return 2;
}

var options = Options.Parse(args);
Directory.CreateDirectory(options.OutputDirectory);
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };

var elevated = new WindowsPrincipal(WindowsIdentity.GetCurrent())
    .IsInRole(WindowsBuiltInRole.Administrator);
Console.WriteLine("PrintCapturePOC - passive Windows spooler monitor");
Console.WriteLine($"Capture output: {Path.GetFullPath(options.OutputDirectory)}");
Console.WriteLine($"Administrator: {elevated}");
if (!elevated)
    Console.WriteLine("[WARNING] Metadata monitoring may work, but Windows normally blocks access to the spool directory. Run elevated to test payload capture.");

var store = new JobCaptureStore(options.OutputDirectory);
SpoolDirectoryWatcher? watcher = null;
if (!options.DisableSpoolCapture)
{
    try
    {
        watcher = new SpoolDirectoryWatcher(store, options.SpoolDirectory, cancellation.Token);
        watcher.Start();
        Console.WriteLine($"Spool directory: {watcher.DirectoryPath}");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[WARNING] Spool payload capture disabled: {ex.Message}");
    }
}

using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
var uploader = options.UploadUrl is null ? null : new MultipartJobUploader(httpClient, options.UploadUrl);
if (uploader is not null) Console.WriteLine($"Upload endpoint: {options.UploadUrl}");
Console.WriteLine("Monitoring. Press Ctrl+C to stop. Printing is never paused or modified.\n");

try
{
    await new SpoolerMonitor(store, TimeSpan.FromMilliseconds(options.PollMilliseconds), uploader)
        .RunAsync(cancellation.Token);
}
finally { watcher?.Dispose(); }
return 0;

internal sealed record Options(string OutputDirectory, Uri? UploadUrl, int PollMilliseconds,
    bool DisableSpoolCapture, string? SpoolDirectory)
{
    internal static Options Parse(string[] args)
    {
        var output = Path.Combine(AppContext.BaseDirectory, "captured_jobs");
        Uri? upload = null;
        var poll = 500;
        var disableSpool = false;
        string? spoolDirectory = null;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--output" when i + 1 < args.Length: output = args[++i]; break;
                case "--upload-url" when i + 1 < args.Length: upload = new Uri(args[++i], UriKind.Absolute); break;
                case "--poll-ms" when i + 1 < args.Length: poll = int.Parse(args[++i]); break;
                case "--spool-directory" when i + 1 < args.Length: spoolDirectory = args[++i]; break;
                case "--no-spool-capture": disableSpool = true; break;
                case "--help": PrintHelp(); Environment.Exit(0); break;
                default: throw new ArgumentException($"Unknown or incomplete argument: {args[i]}");
            }
        }
        if (poll is < 100 or > 60_000) throw new ArgumentOutOfRangeException(nameof(poll), "--poll-ms must be 100..60000");
        return new Options(output, upload, poll, disableSpool, spoolDirectory);
    }

    private static void PrintHelp() => Console.WriteLine("""
        Usage: PrintMonitor [options]
          --output PATH             Capture root (default: captured_jobs beside executable)
          --upload-url URL          Optional multipart POST endpoint
          --poll-ms N               Fallback queue scan interval, 100..60000 (default: 500)
          --spool-directory PATH    Override Windows spool folder discovery
          --no-spool-capture        Capture metadata only
        """);
}
