namespace PrintCapture;

public sealed class PrintJobMetadata
{
    public int SchemaVersion { get; init; } = 1;
    public int JobId { get; init; }
    public string? DocumentName { get; set; }
    public string? UserName { get; set; }
    public string? ComputerName { get; set; }
    public string? PrinterName { get; set; }
    public int? TotalPages { get; set; }
    public int? PagesPrinted { get; set; }
    public int? Copies { get; set; }
    public DateTimeOffset? SubmittedAt { get; set; }
    public DateTimeOffset FirstObservedAt { get; init; } = DateTimeOffset.Now;
    public DateTimeOffset LastObservedAt { get; set; } = DateTimeOffset.Now;
    public string? Status { get; set; }
    public uint StatusCode { get; set; }
    public string? SpoolDataType { get; set; }
    public string? PrintProcessor { get; set; }
    public string? DriverName { get; set; }
    public string? ApplicationProcess { get; set; }
    public string ApplicationProcessNote { get; set; } =
        "The Win32 spooler job APIs do not expose the originating process. This field is null unless a future collector supplies it.";
    public List<CapturedFile> CapturedFiles { get; init; } = [];
    public List<string> CaptureWarnings { get; init; } = [];
    public UploadState Upload { get; init; } = new();
}

public sealed class CapturedFile
{
    public required string FileName { get; init; }
    public required string Kind { get; init; }
    public long Bytes { get; set; }
    public DateTimeOffset LastCopiedAt { get; set; }
    public string? DetectedFormat { get; set; }
    public string? Sha256 { get; set; }
}

public sealed class UploadState
{
    public bool Enabled { get; set; }
    public bool Attempted { get; set; }
    public bool Succeeded { get; set; }
    public int? HttpStatus { get; set; }
    public string? Error { get; set; }
}
