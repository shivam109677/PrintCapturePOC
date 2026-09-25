using System.Diagnostics;

var options = LauncherOptions.Parse(args);
var repositoryRoot = FindRepositoryRoot();
var platform = options.PlatformOverride ?? DetectPlatform();
var launch = GetLaunch(platform, repositoryRoot, options.ForwardedArguments);

if (options.DryRun)
{
    Console.WriteLine($"Detected platform: {platform}");
    Console.WriteLine($"Monitor: {launch.Description}");
    Console.WriteLine($"Command: {launch.FileName} {string.Join(' ', launch.Arguments)}");
    return 0;
}

Console.WriteLine($"PrintCapturePOC selected {launch.Description} for {platform}.");
if (OperatingSystem.IsWindows() && options.PlatformOverride is null)
    Console.WriteLine("For spool-file capture, run this terminal as Administrator.");
if (OperatingSystem.IsLinux() && options.PlatformOverride is null && Environment.UserName != "root")
    Console.WriteLine("Linux spool capture needs sudo; enter your password if prompted.");

var start = new ProcessStartInfo(launch.FileName)
{
    WorkingDirectory = repositoryRoot,
    UseShellExecute = false
};
foreach (var argument in launch.Arguments) start.ArgumentList.Add(argument);
using var process = Process.Start(start);
if (process is null)
{
    Console.Error.WriteLine($"Could not start {launch.FileName}.");
    return 1;
}
await process.WaitForExitAsync();
return process.ExitCode;

static string DetectPlatform() => OperatingSystem.IsWindows() ? "windows"
    : OperatingSystem.IsLinux() ? "linux"
    : "unsupported";

static LaunchPlan GetLaunch(string platform, string root, string[] forwarded)
{
    var arguments = new List<string>();
    if (platform == "windows")
    {
        arguments.AddRange(["run", "--project", Path.Combine(root, "src", "PrintMonitor", "PrintMonitor.csproj"), "--"]);
        arguments.AddRange(["--output", Path.Combine(root, "captured_jobs")]);
        arguments.AddRange(forwarded);
        return new("dotnet", arguments, "Windows Print Spooler monitor");
    }

    if (platform == "linux")
    {
        var pythonArguments = new List<string>
        {
            "python3", Path.Combine(root, "linux", "monitor_cups.py"),
            "--output", Path.Combine(root, "captured_jobs_linux")
        };
        pythonArguments.AddRange(forwarded);
        if (Environment.UserName == "root")
            return new(pythonArguments[0], pythonArguments.Skip(1).ToArray(), "Linux CUPS monitor");
        return new("sudo", pythonArguments, "Linux CUPS monitor");
    }

    throw new PlatformNotSupportedException("Passive print monitoring is supported on Windows and Linux. The shared Print & Save app is available separately on macOS.");
}

static string FindRepositoryRoot()
{
    foreach (var origin in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
    {
        var current = new DirectoryInfo(origin);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "PrintCapturePOC.sln"))) return current.FullName;
            current = current.Parent;
        }
    }
    throw new DirectoryNotFoundException("Could not locate PrintCapturePOC.sln. Run the launcher from the repository checkout.");
}

internal sealed record LaunchPlan(string FileName, IReadOnlyList<string> Arguments, string Description);

internal sealed record LauncherOptions(bool DryRun, string? PlatformOverride, string[] ForwardedArguments)
{
    internal static LauncherOptions Parse(string[] args)
    {
        var dryRun = false;
        string? platform = null;
        var forwarded = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--dry-run": dryRun = true; break;
                case "--platform" when i + 1 < args.Length:
                    platform = args[++i].ToLowerInvariant();
                    if (platform is not ("windows" or "linux")) throw new ArgumentException("--platform must be windows or linux.");
                    break;
                case "--": forwarded.AddRange(args.Skip(i + 1)); i = args.Length; break;
                default: forwarded.Add(args[i]); break;
            }
        }
        if (platform is not null && !dryRun)
            throw new ArgumentException("--platform is only allowed with --dry-run.");
        return new(dryRun, platform, forwarded.ToArray());
    }
}
