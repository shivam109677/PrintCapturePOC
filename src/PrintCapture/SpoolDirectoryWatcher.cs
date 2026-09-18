using Microsoft.Win32;
using System.Collections.Concurrent;

namespace PrintCapture;

public sealed class SpoolDirectoryWatcher : IDisposable
{
    private readonly JobCaptureStore _store;
    private readonly FileSystemWatcher _watcher;
    private readonly ConcurrentDictionary<string, byte> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationToken _cancellationToken;

    public SpoolDirectoryWatcher(JobCaptureStore store, string? overrideDirectory, CancellationToken cancellationToken)
    {
        _store = store;
        _cancellationToken = cancellationToken;
        DirectoryPath = overrideDirectory ?? ResolveDefaultDirectory();
        _watcher = new FileSystemWatcher(DirectoryPath)
        {
            Filter = "*.*",
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite,
            InternalBufferSize = 64 * 1024
        };
        _watcher.Created += OnChanged;
        _watcher.Changed += OnChanged;
        _watcher.Renamed += (_, e) => Queue(e.FullPath);
        _watcher.Error += (_, e) => Console.Error.WriteLine($"[SPOOL WATCH WARNING] {e.GetException().Message}");
    }

    public string DirectoryPath { get; }

    public void Start()
    {
        _watcher.EnableRaisingEvents = true;
        foreach (var path in Directory.EnumerateFiles(DirectoryPath)) Queue(path);
    }

    private void OnChanged(object sender, FileSystemEventArgs e) => Queue(e.FullPath);

    private void Queue(string path)
    {
        var extension = Path.GetExtension(path);
        if (!extension.Equals(".SPL", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".SHD", StringComparison.OrdinalIgnoreCase)) return;
        if (!int.TryParse(Path.GetFileNameWithoutExtension(path), out var jobId)) return;
        if (!_pending.TryAdd(path, 0)) return;

        _ = Task.Run(async () =>
        {
            try { await _store.CopySpoolFileAsync(jobId, path, _cancellationToken); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Console.Error.WriteLine($"[CAPTURE WARNING] {Path.GetFileName(path)}: {ex.Message}"); }
            finally
            {
                _pending.TryRemove(path, out _);
                if (File.Exists(path) && !_cancellationToken.IsCancellationRequested &&
                    int.TryParse(Path.GetFileNameWithoutExtension(path), out var id))
                {
                    var sourceLength = new FileInfo(path).Length;
                    var destinationName = Path.GetExtension(path).Equals(".SPL", StringComparison.OrdinalIgnoreCase)
                        ? "spool_file.spl" : "shadow_file.shd";
                    var capturedLength = _store.TryGet(id, out var record) &&
                                         File.Exists(Path.Combine(record.Directory, destinationName))
                        ? new FileInfo(Path.Combine(record.Directory, destinationName)).Length : -1;
                    if (sourceLength > capturedLength)
                    {
                        await Task.Delay(75);
                        Queue(path);
                    }
                }
            }
        }, _cancellationToken);
    }

    private static string ResolveDefaultDirectory()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Print\Printers");
        var configured = key?.GetValue("DefaultSpoolDirectory") as string;
        configured = Environment.ExpandEnvironmentVariables(configured ?? @"%SystemRoot%\System32\spool\PRINTERS");
        if (!Directory.Exists(configured)) throw new DirectoryNotFoundException($"Spool directory not found: {configured}");
        return configured;
    }

    public void Dispose() => _watcher.Dispose();
}
