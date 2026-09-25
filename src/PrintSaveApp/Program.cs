using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;

namespace PrintSaveApp;

public static class Program
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly Dictionary<string, string> MimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = "application/pdf", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg",
        [".txt"] = "text/plain", [".ps"] = "application/postscript", [".pcl"] = "application/vnd.hp-pcl",
        [".doc"] = "application/msword", [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".xls"] = "application/vnd.ms-excel", [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        [".ppt"] = "application/vnd.ms-powerpoint", [".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation"
    };

    public static async Task Main(string[] args)
    {
        if (args.Contains("--self-test"))
        {
            IppClient.RunCodecSelfTest();
            Console.WriteLine("IPP codec self-test passed.");
            return;
        }

        using var probeHttp = CreateHttpClient();
        if (GetArgument(args, "--probe") is { } probeUri)
        {
            var status = await new IppClient(probeHttp).GetPrinterStatusAsync(probeUri, CancellationToken.None);
            Console.WriteLine(JsonSerializer.Serialize(status, Json));
            return;
        }

        var port = int.TryParse(GetArgument(args, "--port"), out var requestedPort) && requestedPort is > 0 and <= 65535 ? requestedPort : 8765;
        var localUrl = $"http://127.0.0.1:{port}";
        var publishedWebRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            WebRootPath = Directory.Exists(publishedWebRoot) ? publishedWebRoot : null
        });
        builder.WebHost.UseUrls(localUrl);
        builder.Services.Configure<FormOptions>(options => options.MultipartBodyLengthLimit = 256L * 1024 * 1024);
        builder.Services.AddSingleton(new SettingsStore());
        builder.Services.AddSingleton(CreateHttpClient());
        builder.Services.AddSingleton<IppClient>();
        var app = builder.Build();
        app.UseDefaultFiles();
        app.UseStaticFiles();

        app.MapGet("/api/settings", (SettingsStore store) =>
            Results.Ok(new { settings = store.Load(), archiveRoot = GetArchiveRoot() }));

        app.MapPost("/api/settings", async (PrinterSettings requested, SettingsStore store, IppClient ipp, CancellationToken ct) =>
        {
            if (!Uri.TryCreate(requested.PrinterUri, UriKind.Absolute, out var uri) || uri.Scheme is not ("ipp" or "ipps"))
                return Results.BadRequest(new { error = "Use a complete ipp:// or ipps:// printer URI." });
            try
            {
                var status = await ipp.GetPrinterStatusAsync(requested.PrinterUri, ct);
                var saved = requested with { DisplayName = string.IsNullOrWhiteSpace(requested.DisplayName) ? status.Name : requested.DisplayName.Trim() };
                store.Save(saved);
                return Results.Ok(new { settings = saved, printer = status });
            }
            catch (Exception ex) { return Results.BadRequest(new { error = SafeMessage(ex) }); }
        });

        app.MapGet("/api/status", async (SettingsStore store, IppClient ipp, CancellationToken ct) =>
        {
            var settings = store.Load();
            if (settings is null) return Results.Ok(new { configured = false });
            try
            {
                var status = await ipp.GetPrinterStatusAsync(settings.PrinterUri, ct);
                return Results.Ok(new { configured = true, online = true, settings, printer = status });
            }
            catch (Exception ex) { return Results.Ok(new { configured = true, online = false, settings, error = SafeMessage(ex) }); }
        });

        app.MapPost("/api/print", async (HttpRequest request, SettingsStore store, IppClient ipp, CancellationToken ct) =>
        {
            var settings = store.Load();
            if (settings is null) return Results.BadRequest(new { error = "Configure and test the printer first." });
            var form = await request.ReadFormAsync(ct);
            var upload = form.Files.GetFile("document");
            if (upload is null || upload.Length == 0) return Results.BadRequest(new { error = "Select a non-empty document." });
            var copies = int.TryParse(form["copies"], out var parsedCopies) ? Math.Clamp(parsedCopies, 1, 99) : 1;
            var color = form["color"].ToString() is "color" or "monochrome" ? form["color"].ToString() : "auto";
            var originalName = Path.GetFileName(upload.FileName);
            var extension = Path.GetExtension(originalName);
            if (!MimeTypes.TryGetValue(extension, out var mime))
                return Results.BadRequest(new { error = $"{extension} cannot be sent directly. Export it to PDF or JPEG first." });

            var root = GetArchiveRoot();
            Directory.CreateDirectory(root);
            var temporaryDirectory = Path.Combine(root, $"job_pending_{DateTimeOffset.Now:yyyyMMdd_HHmmss_fff}");
            Directory.CreateDirectory(temporaryDirectory);
            var archivedPath = Path.Combine(temporaryDirectory, "source_" + Sanitize(originalName));
            await using (var output = new FileStream(archivedPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 128 * 1024, true))
                await upload.CopyToAsync(output, ct);
            string hash;
            await using (var input = File.OpenRead(archivedPath))
                hash = Convert.ToHexString(await SHA256.HashDataAsync(input, ct)).ToLowerInvariant();
            var metadata = new ArchivedJob
            {
                DocumentName = originalName, ArchivedFile = archivedPath, Bytes = upload.Length, Sha256 = hash,
                PrinterUri = settings.PrinterUri, PrinterName = settings.DisplayName, Copies = copies,
                ColorMode = color, LocalDirectory = temporaryDirectory, SubmittedFormat = mime
            };
            await WriteMetadata(metadata, ct);
            string? convertedPath = null;
            try
            {
                IppPrintResult result;
                if (mime == "image/jpeg")
                {
                    var printer = await ipp.GetPrinterStatusAsync(settings.PrinterUri, ct);
                    if (printer.Formats.Contains("application/pdf", StringComparer.OrdinalIgnoreCase))
                    {
                        convertedPath = Path.Combine(temporaryDirectory, "print_image.pdf");
                        await JpegPdfConverter.ConvertAsync(archivedPath, convertedPath, printer.MediaDefault, ct);
                        metadata.SubmittedFormat = "application/pdf (image/jpeg fitted to page)";
                        result = await ipp.PrintAsync(settings.PrinterUri, convertedPath, originalName, "application/pdf", copies, color, ct);
                    }
                    else
                    {
                        result = await ipp.PrintAsync(settings.PrinterUri, archivedPath, originalName, mime, copies, color, ct);
                    }
                }
                else result = await ipp.PrintAsync(settings.PrinterUri, archivedPath, originalName, mime, copies, color, ct);
                if (convertedPath is not null)
                {
                    File.Delete(convertedPath);
                    convertedPath = null;
                }
                metadata.JobId = result.JobId;
                metadata.JobUri = result.JobUri;
                metadata.StatusCode = result.StatusCode;
                metadata.Status = "Submitted";
                var finalDirectory = UniqueJobDirectory(root, result.JobId);
                Directory.Move(temporaryDirectory, finalDirectory);
                metadata.LocalDirectory = finalDirectory;
                metadata.ArchivedFile = Path.Combine(finalDirectory, Path.GetFileName(archivedPath));
                metadata.LastUpdatedAt = DateTimeOffset.Now;
                await WriteMetadata(metadata, ct);
                Console.WriteLine($"[PRINT SAVED] Job {result.JobId}: {originalName} -> {finalDirectory}");
                return Results.Ok(metadata);
            }
            catch (Exception ex)
            {
                if (convertedPath is not null && File.Exists(convertedPath)) File.Delete(convertedPath);
                metadata.Status = "Capture saved; print submission failed";
                metadata.Error = SafeMessage(ex);
                metadata.LastUpdatedAt = DateTimeOffset.Now;
                await WriteMetadata(metadata, CancellationToken.None);
                return Results.BadRequest(metadata);
            }
        });

        app.MapGet("/api/jobs/{jobId:int}", async (int jobId, SettingsStore store, IppClient ipp, CancellationToken ct) =>
        {
            var settings = store.Load();
            if (settings is null) return Results.NotFound(new { error = "Printer is not configured." });
            try { return Results.Ok(await ipp.GetJobStatusAsync(settings.PrinterUri, jobId, null, ct)); }
            catch (Exception ex) { return Results.BadRequest(new { error = SafeMessage(ex) }); }
        });

        app.Lifetime.ApplicationStarted.Register(() =>
        {
            Console.WriteLine($"PrintCapturePOC is ready at {localUrl}");
            Console.WriteLine($"Captured originals and metadata are saved under: {GetArchiveRoot()}");
            if (!args.Contains("--no-browser"))
                try { Process.Start(new ProcessStartInfo(localUrl) { UseShellExecute = true }); } catch { }
        });
        await app.RunAsync();
    }

    private static HttpClient CreateHttpClient() => new() { Timeout = TimeSpan.FromMinutes(5) };
    private static string GetArchiveRoot()
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrWhiteSpace(documents))
            documents = Path.Combine(PlatformPaths.UserHome(), "Documents");
        return Path.Combine(documents, "PrintCapturePOC", "printed_jobs");
    }
    private static string? GetArgument(string[] args, string name) { var index = Array.IndexOf(args, name); return index >= 0 && index + 1 < args.Length ? args[index + 1] : null; }
    private static string Sanitize(string value) => string.Concat(value.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
    private static string UniqueJobDirectory(string root, int jobId)
    {
        var basis = Path.Combine(root, $"job_{jobId:D5}_{DateTimeOffset.Now:yyyyMMdd_HHmmss}");
        var candidate = basis; var suffix = 1;
        while (Directory.Exists(candidate)) candidate = basis + "_" + suffix++;
        return candidate;
    }
    private static async Task WriteMetadata(ArchivedJob job, CancellationToken ct)
    {
        Directory.CreateDirectory(job.LocalDirectory);
        await File.WriteAllTextAsync(Path.Combine(job.LocalDirectory, "metadata.json"), JsonSerializer.Serialize(job, Json), ct);
    }
    private static string SafeMessage(Exception ex) => ex.GetBaseException().Message;
}
