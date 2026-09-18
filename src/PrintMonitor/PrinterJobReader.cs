using PrintCapture;

namespace PrintMonitor;

internal static class PrinterJobReader
{
    internal static IReadOnlyList<PrintJobMetadata> Snapshot(Action<string>? warning = null)
    {
        var result = new List<PrintJobMetadata>();
        IReadOnlyList<string> printers;
        try { printers = NativeMethods.EnumeratePrinterNames(); }
        catch (Exception ex)
        {
            warning?.Invoke(ex.Message);
            return result;
        }

        foreach (var printer in printers)
        {
            try
            {
                foreach (var native in NativeMethods.EnumerateJobs(printer))
                    result.Add(Convert(native, printer));
            }
            catch (Exception ex) { warning?.Invoke(ex.Message); }
        }
        return result;
    }

    private static PrintJobMetadata Convert(NativeMethods.NativeJob job, string fallbackPrinter)
    {
        return new PrintJobMetadata
        {
            JobId = checked((int)job.JobId),
            DocumentName = job.Document,
            UserName = job.UserName,
            ComputerName = NormalizeMachineName(job.MachineName),
            PrinterName = job.PrinterName ?? fallbackPrinter,
            TotalPages = ToNullable(job.TotalPages),
            PagesPrinted = checked((int)job.PagesPrinted),
            Copies = job.Copies,
            SubmittedAt = ToDateTime(job.Submitted),
            Status = FormatStatus(job.Status, job.StatusText),
            StatusCode = job.Status,
            SpoolDataType = job.DataType,
            PrintProcessor = job.PrintProcessor,
            DriverName = job.DriverName
        };
    }

    private static int? ToNullable(uint value) => value == 0 ? null : checked((int)value);

    private static DateTimeOffset? ToDateTime(NativeMethods.SYSTEMTIME value)
    {
        if (value.Year == 0 || value.Month == 0 || value.Day == 0) return null;
        try
        {
            var utc = new DateTime(value.Year, value.Month, value.Day, value.Hour, value.Minute,
                value.Second, value.Milliseconds, DateTimeKind.Utc);
            return new DateTimeOffset(utc).ToLocalTime();
        }
        catch { return null; }
    }

    private static string? NormalizeMachineName(string? value) => value?.TrimStart('\\');

    private static string FormatStatus(uint status, string? statusText)
    {
        if (!string.IsNullOrWhiteSpace(statusText)) return statusText;
        if (status == 0) return "Queued/Spooling";
        var names = new List<string>();
        Add(0x00000001, "Paused"); Add(0x00000002, "Error"); Add(0x00000004, "Deleting");
        Add(0x00000008, "Spooling"); Add(0x00000010, "Printing"); Add(0x00000020, "Offline");
        Add(0x00000040, "PaperOut"); Add(0x00000080, "Printed"); Add(0x00000100, "Deleted");
        Add(0x00000200, "Blocked"); Add(0x00000400, "UserIntervention"); Add(0x00000800, "Restarting");
        Add(0x00001000, "Complete"); Add(0x00002000, "Retained"); Add(0x00004000, "RenderingLocally");
        return names.Count == 0 ? $"Unknown (0x{status:X8})" : string.Join('|', names);

        void Add(uint flag, string name) { if ((status & flag) != 0) names.Add(name); }
    }
}
