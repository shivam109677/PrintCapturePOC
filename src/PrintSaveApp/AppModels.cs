using System.Text.Json;

namespace PrintSaveApp;

public sealed record PrinterSettings(string PrinterUri, string DisplayName);

public sealed class SettingsStore
{
    private readonly string _path;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public SettingsStore()
    {
        var root = Path.Combine(PlatformPaths.UserHome(), ".printcapturepoc");
        Directory.CreateDirectory(root);
        _path = Path.Combine(root, "settings.json");
    }

    public PrinterSettings? Load()
    {
        if (!File.Exists(_path)) return null;
        try { return JsonSerializer.Deserialize<PrinterSettings>(File.ReadAllText(_path), _json); }
        catch { return null; }
    }

    public void Save(PrinterSettings settings)
    {
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings, _json));
        File.Move(temporary, _path, true);
    }
}

public static class PlatformPaths
{
    public static string UserHome()
    {
        var path = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(path)) path = Environment.GetEnvironmentVariable("HOME");
        if (string.IsNullOrWhiteSpace(path)) path = Environment.GetEnvironmentVariable("USERPROFILE");
        return string.IsNullOrWhiteSpace(path) ? AppContext.BaseDirectory : path;
    }
}

public sealed class ArchivedJob
{
    public int SchemaVersion { get; init; } = 1;
    public string Platform { get; init; } = Environment.OSVersion.Platform.ToString();
    public int? JobId { get; set; }
    public string? JobUri { get; set; }
    public required string DocumentName { get; init; }
    public required string ArchivedFile { get; set; }
    public long Bytes { get; set; }
    public required string Sha256 { get; set; }
    public string UserName { get; init; } = Environment.UserName;
    public string ComputerName { get; init; } = Environment.MachineName;
    public required string PrinterUri { get; init; }
    public required string PrinterName { get; init; }
    public int Copies { get; init; }
    public required string ColorMode { get; init; }
    public DateTimeOffset SubmittedAt { get; init; } = DateTimeOffset.Now;
    public DateTimeOffset LastUpdatedAt { get; set; } = DateTimeOffset.Now;
    public string Status { get; set; } = "Archiving";
    public int? StatusCode { get; set; }
    public List<string> StatusReasons { get; set; } = [];
    public string? Error { get; set; }
    public required string LocalDirectory { get; set; }
}
