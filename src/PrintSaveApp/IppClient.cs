using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace PrintSaveApp;

public sealed class IppClient(HttpClient httpClient)
{
    private int _requestId;

    public async Task<IppPrinterStatus> GetPrinterStatusAsync(string printerUri, CancellationToken cancellationToken)
    {
        var request = new IppRequest(0x000B, NextId())
            .Operation("attributes-charset", 0x47, "utf-8")
            .Operation("attributes-natural-language", 0x48, "en")
            .Operation("printer-uri", 0x45, printerUri)
            .Operation("requested-attributes", 0x44, "printer-name")
            .Additional(0x44, "printer-state")
            .Additional(0x44, "printer-state-reasons")
            .Additional(0x44, "printer-is-accepting-jobs")
            .Additional(0x44, "document-format-supported")
            .Additional(0x44, "media-default")
            .End();
        var response = await SendAsync(printerUri, request.Bytes, null, cancellationToken);
        EnsureSuccess(response);
        return new IppPrinterStatus(
            response.String("printer-name") ?? "IPP Printer",
            response.Integer("printer-state") ?? 0,
            response.Boolean("printer-is-accepting-jobs") ?? false,
            response.Strings("printer-state-reasons"),
            response.Strings("document-format-supported"),
            response.String("media-default"));
    }

    public async Task<IppPrintResult> PrintAsync(string printerUri, string path, string documentName,
        string mimeType, int copies, string colorMode, CancellationToken cancellationToken)
    {
        var request = new IppRequest(0x0002, NextId())
            .Operation("attributes-charset", 0x47, "utf-8")
            .Operation("attributes-natural-language", 0x48, "en")
            .Operation("printer-uri", 0x45, printerUri)
            .Operation("requesting-user-name", 0x42, Environment.UserName)
            .Operation("job-name", 0x42, documentName)
            .Operation("document-format", 0x49, mimeType)
            .Group(0x02)
            .Integer("copies", copies);
        if (colorMode != "auto") request.Value("print-color-mode", 0x44, colorMode);
        request.End();

        var response = await SendAsync(printerUri, request.Bytes, path, cancellationToken);
        EnsureSuccess(response);
        return new IppPrintResult(response.Integer("job-id") ?? 0, response.String("job-uri"), response.StatusCode);
    }

    public async Task<IppJobStatus> GetJobStatusAsync(string printerUri, int jobId, string? jobUri,
        CancellationToken cancellationToken)
    {
        var request = new IppRequest(0x0009, NextId())
            .Operation("attributes-charset", 0x47, "utf-8")
            .Operation("attributes-natural-language", 0x48, "en");
        if (!string.IsNullOrWhiteSpace(jobUri)) request.Operation("job-uri", 0x45, jobUri);
        else request.Operation("printer-uri", 0x45, printerUri).Integer("job-id", jobId);
        request.Operation("requested-attributes", 0x44, "job-state")
            .Additional(0x44, "job-state-reasons")
            .Additional(0x44, "job-media-sheets")
            .Additional(0x44, "job-media-sheets-completed")
            .End();
        var response = await SendAsync(printerUri, request.Bytes, null, cancellationToken);
        EnsureSuccess(response);
        return new IppJobStatus(
            response.Integer("job-state") ?? 0,
            response.Strings("job-state-reasons"),
            response.Integer("job-media-sheets"),
            response.Integer("job-media-sheets-completed"));
    }

    private int NextId() => Interlocked.Increment(ref _requestId);

    private async Task<IppResponse> SendAsync(string ippUri, byte[] requestBytes, string? documentPath,
        CancellationToken cancellationToken)
    {
        using HttpContent content = documentPath is null
            ? new ByteArrayContent(requestBytes)
            : new IppDocumentContent(requestBytes, documentPath);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/ipp");
        using var message = new HttpRequestMessage(HttpMethod.Post, ToHttpUri(ippUri)) { Content = content };
        using var result = await httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        result.EnsureSuccessStatusCode();
        var bytes = await result.Content.ReadAsByteArrayAsync(cancellationToken);
        return IppResponse.Parse(bytes);
    }

    private static Uri ToHttpUri(string value)
    {
        var source = new Uri(value, UriKind.Absolute);
        if (source.Scheme is not ("ipp" or "ipps")) throw new ArgumentException("Printer URI must use ipp:// or ipps://");
        var builder = new UriBuilder(source)
        {
            Scheme = source.Scheme == "ipps" ? Uri.UriSchemeHttps : Uri.UriSchemeHttp,
            Port = source.IsDefaultPort ? 631 : source.Port
        };
        return builder.Uri;
    }

    private static void EnsureSuccess(IppResponse response)
    {
        if (response.StatusCode > 0x00FF)
            throw new InvalidOperationException($"IPP request failed with status 0x{response.StatusCode:X4}");
    }

    public static void RunCodecSelfTest()
    {
        byte[] response =
        [
            0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01,
            0x02,
            0x21, 0x00, 0x06, (byte)'j', (byte)'o', (byte)'b', (byte)'-', (byte)'i', (byte)'d',
            0x00, 0x04, 0x00, 0x00, 0x00, 0x2A,
            0x03
        ];
        var parsed = IppResponse.Parse(response);
        if (parsed.StatusCode != 0 || parsed.Integer("job-id") != 42)
            throw new InvalidOperationException("IPP codec self-test failed");
    }
}

internal sealed class IppRequest(ushort operation, int requestId)
{
    private readonly MemoryStream _stream = Create(operation, requestId);
    public byte[] Bytes => _stream.ToArray();

    public IppRequest Operation(string name, byte tag, string value) => GroupIfNeeded().Value(name, tag, value);
    public IppRequest Additional(byte tag, string value) => Value(string.Empty, tag, value);
    public IppRequest Group(byte tag) { _stream.WriteByte(tag); return this; }
    public IppRequest End() { _stream.WriteByte(0x03); return this; }
    public IppRequest Integer(string name, int value)
    {
        WriteAttributeHeader(0x21, name, 4);
        Span<byte> bytes = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(bytes, value); _stream.Write(bytes);
        return this;
    }
    public IppRequest Value(string name, byte tag, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value); WriteAttributeHeader(tag, name, bytes.Length); _stream.Write(bytes); return this;
    }
    private IppRequest GroupIfNeeded() { if (_stream.Length == 8) _stream.WriteByte(0x01); return this; }
    private void WriteAttributeHeader(byte tag, string name, int valueLength)
    {
        var nameBytes = Encoding.UTF8.GetBytes(name); _stream.WriteByte(tag); WriteU16(nameBytes.Length); _stream.Write(nameBytes); WriteU16(valueLength);
    }
    private void WriteU16(int value) { Span<byte> bytes = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(bytes, checked((ushort)value)); _stream.Write(bytes); }
    private static MemoryStream Create(ushort operation, int requestId)
    {
        var stream = new MemoryStream(); stream.WriteByte(0x02); stream.WriteByte(0x00);
        Span<byte> bytes = stackalloc byte[4]; BinaryPrimitives.WriteUInt16BigEndian(bytes[..2], operation); stream.Write(bytes[..2]);
        BinaryPrimitives.WriteInt32BigEndian(bytes, requestId); stream.Write(bytes); return stream;
    }
}

internal sealed class IppResponse(ushort StatusCode, Dictionary<string, List<object>> Attributes)
{
    public ushort StatusCode { get; } = StatusCode;
    public int? Integer(string name) => Attributes.TryGetValue(name, out var values) && values.OfType<int>().FirstOrDefault() is var value && values.Any(v => v is int) ? value : null;
    public bool? Boolean(string name) => Attributes.TryGetValue(name, out var values) && values.OfType<bool>().FirstOrDefault() is var value && values.Any(v => v is bool) ? value : null;
    public string? String(string name) => Attributes.TryGetValue(name, out var values) ? values.OfType<string>().FirstOrDefault() : null;
    public List<string> Strings(string name) => Attributes.TryGetValue(name, out var values) ? values.OfType<string>().ToList() : [];

    public static IppResponse Parse(byte[] bytes)
    {
        if (bytes.Length < 8) throw new InvalidDataException("Short IPP response");
        var status = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(2, 2));
        var attributes = new Dictionary<string, List<object>>(StringComparer.OrdinalIgnoreCase);
        var offset = 8; string? currentName = null;
        while (offset < bytes.Length)
        {
            var tag = bytes[offset++];
            if (tag == 0x03) break;
            if (tag <= 0x0F) { currentName = null; continue; }
            if (offset + 2 > bytes.Length) break;
            var nameLength = ReadU16();
            if (offset + nameLength + 2 > bytes.Length) break;
            if (nameLength > 0) { currentName = Encoding.UTF8.GetString(bytes, offset, nameLength); offset += nameLength; }
            var valueLength = ReadU16();
            if (offset + valueLength > bytes.Length) break;
            if (currentName is not null)
            {
                object value = tag switch
                {
                    0x21 or 0x23 when valueLength == 4 => BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(offset, 4)),
                    0x22 when valueLength == 1 => bytes[offset] != 0,
                    _ => Encoding.UTF8.GetString(bytes, offset, valueLength)
                };
                if (!attributes.TryGetValue(currentName, out var list)) attributes[currentName] = list = [];
                list.Add(value);
            }
            offset += valueLength;
        }
        return new IppResponse(status, attributes);
        int ReadU16() { var value = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset, 2)); offset += 2; return value; }
    }
}

internal sealed class IppDocumentContent(byte[] header, string path) : HttpContent
{
    protected override bool TryComputeLength(out long length) { length = header.LongLength + new FileInfo(path).Length; return true; }
    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
    {
        await stream.WriteAsync(header);
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await input.CopyToAsync(stream);
    }
}

public sealed record IppPrinterStatus(string Name, int State, bool AcceptingJobs, List<string> Reasons,
    List<string> Formats, string? MediaDefault);
public sealed record IppPrintResult(int JobId, string? JobUri, ushort StatusCode);
public sealed record IppJobStatus(int State, List<string> Reasons, int? TotalPages, int? PagesCompleted);
