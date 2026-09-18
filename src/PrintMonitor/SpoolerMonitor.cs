using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using PrintCapture;

namespace PrintMonitor;

internal sealed class SpoolerMonitor(JobCaptureStore store, TimeSpan fallbackPoll, MultipartJobUploader? uploader)
{
    private readonly ConcurrentDictionary<int, int> _missingScans = new();
    private readonly HashSet<int> _announced = [];
    private readonly HashSet<int> _uploaded = [];

    internal async Task RunAsync(CancellationToken cancellationToken)
    {
        IntPtr server = IntPtr.Zero;
        IntPtr change = NativeMethods.InvalidHandleValue;
        try
        {
            if (NativeMethods.OpenPrinter(null, out server, IntPtr.Zero))
            {
                change = NativeMethods.FindFirstPrinterChangeNotification(server,
                    NativeMethods.PRINTER_CHANGE_JOB, 0, IntPtr.Zero);
                if (change == NativeMethods.InvalidHandleValue)
                    Console.Error.WriteLine($"[WARNING] Spooler notifications unavailable ({Marshal.GetLastWin32Error()}); polling remains active.");
            }
            else Console.Error.WriteLine($"[WARNING] Could not open local print server ({Marshal.GetLastWin32Error()}); polling remains active.");

            await ScanAsync(cancellationToken);
            while (!cancellationToken.IsCancellationRequested)
            {
                if (change != NativeMethods.InvalidHandleValue)
                {
                    var wait = NativeMethods.WaitForSingleObject(change, checked((uint)fallbackPoll.TotalMilliseconds));
                    if (wait == NativeMethods.WAIT_OBJECT_0 &&
                        !NativeMethods.FindNextPrinterChangeNotification(change, out _, IntPtr.Zero, IntPtr.Zero))
                        Console.Error.WriteLine($"[WARNING] FindNextPrinterChangeNotification failed ({Marshal.GetLastWin32Error()}).");
                }
                else await Task.Delay(fallbackPoll, cancellationToken);

                await ScanAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally
        {
            if (change != NativeMethods.InvalidHandleValue) NativeMethods.FindClosePrinterChangeNotification(change);
            if (server != IntPtr.Zero) NativeMethods.ClosePrinter(server);
        }
    }

    private async Task ScanAsync(CancellationToken cancellationToken)
    {
        var current = PrinterJobReader.Snapshot(message => Console.Error.WriteLine($"[QUEUE WARNING] {message}"));
        var seen = current.Select(x => x.JobId).ToHashSet();
        foreach (var job in current)
        {
            _missingScans[job.JobId] = 0;
            await store.UpdateMetadataAsync(job, cancellationToken);
            if (_announced.Add(job.JobId)) PrintDetected(job, store.GetOrCreate(job.JobId).Directory);
        }

        foreach (var record in store.Records)
        {
            var id = record.Metadata.JobId;
            if (seen.Contains(id)) continue;
            var missing = _missingScans.AddOrUpdate(id, 1, (_, count) => count + 1);
            if (missing < 2) continue;
            await store.MarkRemovedAsync(id, cancellationToken);
            if (uploader is not null && _uploaded.Add(id))
            {
                await uploader.UploadAsync(record, cancellationToken);
                await store.PersistAsync(record, cancellationToken);
                Console.WriteLine(record.Metadata.Upload.Succeeded
                    ? $"[UPLOAD OK] Job {id}"
                    : $"[UPLOAD FAILED] Job {id}: {record.Metadata.Upload.Error ?? $"HTTP {record.Metadata.Upload.HttpStatus}"}");
            }
        }
    }

    private static void PrintDetected(PrintJobMetadata job, string directory)
    {
        Console.WriteLine("[PRINT DETECTED]");
        Console.WriteLine($"Job ID: {job.JobId}");
        Console.WriteLine($"User: {job.UserName ?? "(unavailable)"}");
        Console.WriteLine($"Computer: {job.ComputerName ?? "(unavailable)"}");
        Console.WriteLine($"Document: {job.DocumentName ?? "(unavailable)"}");
        Console.WriteLine($"Printer: {job.PrinterName ?? "(unavailable)"}");
        Console.WriteLine($"Pages: {job.TotalPages?.ToString() ?? "unknown while spooling"}");
        Console.WriteLine($"Copies: {job.Copies?.ToString() ?? "unavailable"}");
        Console.WriteLine($"Time: {job.SubmittedAt?.ToString("yyyy-MM-dd HH:mm:ss zzz") ?? DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz")}");
        Console.WriteLine($"Status: {job.Status}");
        Console.WriteLine($"Spool Format: {job.SpoolDataType ?? "unknown"}");
        Console.WriteLine($"Captured Folder: {directory}");
        Console.WriteLine();
    }
}
