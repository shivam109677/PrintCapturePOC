using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace PrintCapture;

public sealed class MultipartJobUploader(HttpClient client, Uri endpoint)
{
    public async Task UploadAsync(CaptureRecord record, CancellationToken cancellationToken)
    {
        record.Metadata.Upload.Enabled = true;
        record.Metadata.Upload.Attempted = true;
        try
        {
            using var form = new MultipartFormDataContent();
            var json = JsonSerializer.Serialize(record.Metadata, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            form.Add(new StringContent(json, Encoding.UTF8, "application/json"), "metadata");

            foreach (var captured in record.Metadata.CapturedFiles)
            {
                var path = Path.Combine(record.Directory, captured.FileName);
                if (!File.Exists(path)) continue;
                var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var content = new StreamContent(stream);
                content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                form.Add(content, "files", captured.FileName);
            }

            using var response = await client.PostAsync(endpoint, form, cancellationToken);
            record.Metadata.Upload.HttpStatus = (int)response.StatusCode;
            record.Metadata.Upload.Succeeded = response.IsSuccessStatusCode;
            if (!response.IsSuccessStatusCode)
                record.Metadata.Upload.Error = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            record.Metadata.Upload.Succeeded = false;
            record.Metadata.Upload.Error = ex.Message;
        }
    }
}
