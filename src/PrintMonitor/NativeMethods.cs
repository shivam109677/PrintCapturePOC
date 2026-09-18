using System.ComponentModel;
using System.Runtime.InteropServices;

namespace PrintMonitor;

internal static class NativeMethods
{
    internal const uint PRINTER_ENUM_LOCAL = 0x2;
    internal const uint PRINTER_ENUM_CONNECTIONS = 0x4;
    internal const uint PRINTER_CHANGE_JOB = 0x0000FF00;
    internal const uint WAIT_OBJECT_0 = 0;
    internal const uint WAIT_TIMEOUT = 258;
    internal static readonly IntPtr InvalidHandleValue = new(-1);

    [DllImport("winspool.drv", EntryPoint = "OpenPrinterW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool OpenPrinter(string? printerName, out IntPtr printer, IntPtr defaults);

    [DllImport("winspool.drv", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ClosePrinter(IntPtr printer);

    [DllImport("winspool.drv", EntryPoint = "EnumPrintersW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumPrinters(uint flags, string? name, uint level, IntPtr buffer,
        uint bufferSize, out uint needed, out uint returned);

    [DllImport("winspool.drv", EntryPoint = "EnumJobsW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumJobs(IntPtr printer, uint firstJob, uint numberOfJobs, uint level,
        IntPtr buffer, uint bufferSize, out uint needed, out uint returned);

    [DllImport("winspool.drv", SetLastError = true)]
    internal static extern IntPtr FindFirstPrinterChangeNotification(IntPtr printer, uint filter,
        uint options, IntPtr notifyOptions);

    [DllImport("winspool.drv", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool FindNextPrinterChangeNotification(IntPtr change, out uint changeFlags,
        IntPtr notifyOptions, IntPtr notifyInfo);

    [DllImport("winspool.drv", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool FindClosePrinterChangeNotification(IntPtr change);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    internal static IReadOnlyList<string> EnumeratePrinterNames()
    {
        var flags = PRINTER_ENUM_LOCAL | PRINTER_ENUM_CONNECTIONS;
        EnumPrinters(flags, null, 4, IntPtr.Zero, 0, out var needed, out _);
        if (needed == 0) return [];
        var buffer = Marshal.AllocHGlobal(checked((int)needed));
        try
        {
            if (!EnumPrinters(flags, null, 4, buffer, needed, out _, out var returned))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "EnumPrinters failed");
            var size = Marshal.SizeOf<PRINTER_INFO_4>();
            var names = new List<string>(checked((int)returned));
            for (var i = 0; i < returned; i++)
            {
                var info = Marshal.PtrToStructure<PRINTER_INFO_4>(IntPtr.Add(buffer, checked((int)i * size)));
                var name = PtrToString(info.PrinterName);
                if (!string.IsNullOrWhiteSpace(name)) names.Add(name);
            }
            return names;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    internal static IReadOnlyList<NativeJob> EnumerateJobs(string printerName)
    {
        if (!OpenPrinter(printerName, out var printer, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"OpenPrinter failed for '{printerName}'");
        try
        {
            EnumJobs(printer, 0, uint.MaxValue, 2, IntPtr.Zero, 0, out var needed, out var returned);
            if (needed == 0) return [];
            var buffer = Marshal.AllocHGlobal(checked((int)needed));
            try
            {
                if (!EnumJobs(printer, 0, uint.MaxValue, 2, buffer, needed, out _, out returned))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), $"EnumJobs failed for '{printerName}'");
                var size = Marshal.SizeOf<JOB_INFO_2>();
                var jobs = new List<NativeJob>(checked((int)returned));
                for (var i = 0; i < returned; i++)
                {
                    var item = Marshal.PtrToStructure<JOB_INFO_2>(IntPtr.Add(buffer, checked((int)i * size)));
                    jobs.Add(new NativeJob(item.JobId, PtrToString(item.PrinterName), PtrToString(item.MachineName),
                        PtrToString(item.UserName), PtrToString(item.Document), PtrToString(item.DataType),
                        PtrToString(item.PrintProcessor), PtrToString(item.DriverName), PtrToString(item.StatusText),
                        item.Status, item.TotalPages, item.PagesPrinted, item.Submitted, ReadCopies(item.DevMode)));
                }
                return jobs;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        finally { ClosePrinter(printer); }
    }

    internal static string? PtrToString(IntPtr pointer) =>
        pointer == IntPtr.Zero ? null : Marshal.PtrToStringUni(pointer);

    private static int? ReadCopies(IntPtr devMode)
    {
        const uint dmCopies = 0x00000100;
        if (devMode == IntPtr.Zero) return null;
        try
        {
            // Unicode DEVMODEW: dmSize byte 68, dmFields byte 72, printer dmCopies byte 86.
            var size = unchecked((ushort)Marshal.ReadInt16(devMode, 68));
            var fields = unchecked((uint)Marshal.ReadInt32(devMode, 72));
            if (size < 88 || (fields & dmCopies) == 0) return null;
            var copies = Marshal.ReadInt16(devMode, 86);
            return copies > 0 ? copies : null;
        }
        catch { return null; }
    }

    internal sealed record NativeJob(uint JobId, string? PrinterName, string? MachineName,
        string? UserName, string? Document, string? DataType, string? PrintProcessor,
        string? DriverName, string? StatusText, uint Status, uint TotalPages, uint PagesPrinted,
        SYSTEMTIME Submitted, int? Copies);

    [StructLayout(LayoutKind.Sequential)]
    internal struct PRINTER_INFO_4
    {
        internal IntPtr PrinterName;
        internal IntPtr ServerName;
        internal uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct JOB_INFO_2
    {
        internal uint JobId;
        internal IntPtr PrinterName;
        internal IntPtr MachineName;
        internal IntPtr UserName;
        internal IntPtr Document;
        internal IntPtr NotifyName;
        internal IntPtr DataType;
        internal IntPtr PrintProcessor;
        internal IntPtr Parameters;
        internal IntPtr DriverName;
        internal IntPtr DevMode;
        internal IntPtr StatusText;
        internal IntPtr SecurityDescriptor;
        internal uint Status;
        internal uint Priority;
        internal uint Position;
        internal uint StartTime;
        internal uint UntilTime;
        internal uint TotalPages;
        internal uint Size;
        internal SYSTEMTIME Submitted;
        internal uint Time;
        internal uint PagesPrinted;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SYSTEMTIME
    {
        internal ushort Year;
        internal ushort Month;
        internal ushort DayOfWeek;
        internal ushort Day;
        internal ushort Hour;
        internal ushort Minute;
        internal ushort Second;
        internal ushort Milliseconds;
    }
}
