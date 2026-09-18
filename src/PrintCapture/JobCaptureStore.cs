using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;

namespace PrintCapture;

public sealed class JobCaptureStore
{
    private readonly string _root;
    private readonly ConcurrentDictionary<int, CaptureRecord> _records = new();
    private readonly ConcurrentDictionary<int, SemaphoreSlim> _locks = new();
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public JobCaptureStore(string root)
    {
        _root = Path.GetFullPath(root);
        Directory.CreateDirectory(_root);
    }

    public string Root => _root;

    public CaptureRecord GetOrCreate(int jobId)
    {
        return _records.GetOrAdd(jobId, id =>
        {
            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
            var directory = Path.Combine(_root, $"job_{id:D5}_{stamp}");
            Directory.CreateDirectory(directory);
            return new CaptureRecord(directory, new PrintJobMetadata { JobId = id });
        });
    }

    public bool TryGet(int jobId, out CaptureRecord record) => _records.TryGetValue(jobId, out record!);

    public IEnumerable<CaptureRecord> Records => _records.Values;

    public async Task UpdateMetadataAsync(PrintJobMetadata update, CancellationToken cancellationToken)
    {
        var gate = _locks.GetOrAdd(update.JobId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var record = GetOrCreate(update.JobId);
            Merge(record.Metadata, update);
            await WriteMetadataCoreAsync(record, cancellationToken);
        }
        finally { gate.Release(); }
    }

    public async Task<bool> CopySpoolFileAsync(int jobId, string sourcePath, CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(sourcePath).ToUpperInvariant();
        if (extension is not (".SPL" or ".SHD")) return false;

        var gate = _locks.GetOrAdd(jobId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var record = GetOrCreate(jobId);
            var destinationName = extension == ".SPL" ? "spool_file.spl" : "shadow_file.shd";
            var destination = Path.Combine(record.Directory, destinationName);
            var temporary = destination + ".copying";

            Exception? lastError = null;
            for (var attempt = 0; attempt < 12 && !cancellationToken.IsCancellationRequested; attempt++)
            {
                try
                {
                    await using (var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read,
                                     FileShare.ReadWrite | FileShare.Delete, 128 * 1024,
                                     FileOptions.Asynchronous | FileOptions.SequentialScan))
                    await using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write,
                                     FileShare.Read, 128 * 1024, FileOptions.Asynchronous))
                    {
                        await input.CopyToAsync(output, cancellationToken);
                    }

                    var newLength = new FileInfo(temporary).Length;
                    var oldLength = File.Exists(destination) ? new FileInfo(destination).Length : -1;
                    if (newLength >= oldLength)
                        File.Move(temporary, destination, true);
                    else
                        File.Delete(temporary);

                    if (File.Exists(destination))
                    {
                        var captured = record.Metadata.CapturedFiles.FirstOrDefault(x => x.FileName == destinationName);
                        if (captured is null)
                        {
                            captured = new CapturedFile { FileName = destinationName, Kind = extension[1..] };
                            record.Metadata.CapturedFiles.Add(captured);
                        }
                        captured.Bytes = new FileInfo(destination).Length;
                        captured.LastCopiedAt = DateTimeOffset.Now;
                        captured.DetectedFormat = extension == ".SPL" ? DetectSpoolFormat(destination, record.Metadata.SpoolDataType) : "Windows shadow job metadata (undocumented)";
                        captured.Sha256 = await HashAsync(destination, cancellationToken);
                        await WriteMetadataCoreAsync(record, cancellationToken);
                    }
                    return true;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    lastError = ex;
                    await Task.Delay(25 * (attempt + 1), cancellationToken);
                }
            }

            var warning = $"Could not copy {Path.GetFileName(sourcePath)}: {lastError?.Message}";
            if (!record.Metadata.CaptureWarnings.Contains(warning)) record.Metadata.CaptureWarnings.Add(warning);
            await WriteMetadataCoreAsync(record, cancellationToken);
            return false;
        }
        finally { gate.Release(); }
    }

    public async Task MarkRemovedAsync(int jobId, CancellationToken cancellationToken)
    {
        if (!TryGet(jobId, out var record)) return;
        var gate = _locks.GetOrAdd(jobId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            record.Metadata.Status = record.Metadata.Status is "Deleted" or "Error"
                ? record.Metadata.Status
                : "CompletedOrRemovedFromQueue";
            record.Metadata.LastObservedAt = DateTimeOffset.Now;
            await WriteMetadataCoreAsync(record, cancellationToken);
        }
        finally { gate.Release(); }
    }

    public async Task PersistAsync(CaptureRecord record, CancellationToken cancellationToken)
    {
        var gate = _locks.GetOrAdd(record.Metadata.JobId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try { await WriteMetadataCoreAsync(record, cancellationToken); }
        finally { gate.Release(); }
    }

    private async Task WriteMetadataCoreAsync(CaptureRecord record, CancellationToken cancellationToken)
    {
        var target = Path.Combine(record.Directory, "metadata.json");
        var temporary = target + ".tmp";
        await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.Read))
            await JsonSerializer.SerializeAsync(stream, record.Metadata, _json, cancellationToken);
        File.Move(temporary, target, true);
    }

    private static void Merge(PrintJobMetadata target, PrintJobMetadata source)
    {
        target.DocumentName = source.DocumentName ?? target.DocumentName;
        target.UserName = source.UserName ?? target.UserName;
        target.ComputerName = source.ComputerName ?? target.ComputerName;
        target.PrinterName = source.PrinterName ?? target.PrinterName;
        target.TotalPages = source.TotalPages ?? target.TotalPages;
        target.PagesPrinted = source.PagesPrinted ?? target.PagesPrinted;
        target.Copies = source.Copies ?? target.Copies;
        target.SubmittedAt = source.SubmittedAt ?? target.SubmittedAt;
        target.LastObservedAt = DateTimeOffset.Now;
        target.Status = source.Status ?? target.Status;
        target.StatusCode = source.StatusCode;
        target.SpoolDataType = source.SpoolDataType ?? target.SpoolDataType;
        target.PrintProcessor = source.PrintProcessor ?? target.PrintProcessor;
        target.DriverName = source.DriverName ?? target.DriverName;
    }

    private static string DetectSpoolFormat(string path, string? spoolDataType)
    {
        Span<byte> bytes = stackalloc byte[8];
        using var stream = File.OpenRead(path);
        var count = stream.Read(bytes);
        if (count >= 4 && bytes[0] == 0x50 && bytes[1] == 0x4B && bytes[2] == 0x03 && bytes[3] == 0x04)
            return "ZIP package (possibly XPS/OXPS; validate with an XPS-aware tool)";
        if (count >= 4 && bytes[0] == 0x25 && bytes[1] == 0x50 && bytes[2] == 0x44 && bytes[3] == 0x46)
            return "PDF";
        if (count >= 4 && bytes[0] == 0x25 && bytes[1] == 0x21 && bytes[2] == 0x50 && bytes[3] == 0x53)
            return "PostScript";
        if (count >= 2 && bytes[0] == 0x1B && bytes[1] == 0x45)
            return "Likely PCL";
        return string.IsNullOrWhiteSpace(spoolDataType) ? "Unknown/device-specific" : spoolDataType;
    }

    private static async Task<string> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

public sealed record CaptureRecord(string Directory, PrintJobMetadata Metadata);
