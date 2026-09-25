using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace PrintSaveApp;

/// <summary>Wraps a JPEG image in a one-page PDF without decoding or changing its source bytes.</summary>
internal static class JpegPdfConverter
{
    private const double PageWidth = 612;
    private const double PageHeight = 792;

    public static async Task ConvertAsync(string jpegPath, string pdfPath, CancellationToken cancellationToken)
    {
        var jpeg = await File.ReadAllBytesAsync(jpegPath, cancellationToken);
        var (width, height, components) = ReadDimensions(jpeg);
        var colorSpace = components switch
        {
            1 => "/DeviceGray",
            3 => "/DeviceRGB",
            4 => "/DeviceCMYK /Decode [1 0 1 0 1 0 1 0]",
            _ => throw new InvalidDataException($"Unsupported JPEG color components: {components}")
        };

        var scale = Math.Min(PageWidth / width, PageHeight / height);
        var drawWidth = width * scale;
        var drawHeight = height * scale;
        var x = (PageWidth - drawWidth) / 2;
        var y = (PageHeight - drawHeight) / 2;
        var content = Encoding.ASCII.GetBytes(string.Format(CultureInfo.InvariantCulture,
            "q {0:0.###} 0 0 {1:0.###} {2:0.###} {3:0.###} cm /Im0 Do Q\n",
            drawWidth, drawHeight, x, y));

        using var output = new MemoryStream();
        WriteAscii(output, "%PDF-1.4\n");
        var offsets = new long[6];
        WriteObject(1, "<< /Type /Catalog /Pages 2 0 R >>");
        WriteObject(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        WriteObject(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /XObject << /Im0 5 0 R >> >> /Contents 4 0 R >>");
        offsets[4] = output.Position;
        WriteAscii(output, $"4 0 obj\n<< /Length {content.Length} >>\nstream\n");
        output.Write(content);
        WriteAscii(output, "endstream\nendobj\n");
        offsets[5] = output.Position;
        WriteAscii(output, $"5 0 obj\n<< /Type /XObject /Subtype /Image /Width {width} /Height {height} /ColorSpace {colorSpace} /BitsPerComponent 8 /Filter /DCTDecode /Length {jpeg.Length} >>\nstream\n");
        output.Write(jpeg);
        WriteAscii(output, "\nendstream\nendobj\n");

        var xref = output.Position;
        WriteAscii(output, "xref\n0 6\n0000000000 65535 f \n");
        for (var i = 1; i <= 5; i++) WriteAscii(output, $"{offsets[i]:D10} 00000 n \n");
        WriteAscii(output, $"trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        await File.WriteAllBytesAsync(pdfPath, output.ToArray(), cancellationToken);

        void WriteObject(int id, string body)
        {
            offsets[id] = output.Position;
            WriteAscii(output, $"{id} 0 obj\n{body}\nendobj\n");
        }
    }

    private static (int Width, int Height, int Components) ReadDimensions(ReadOnlySpan<byte> jpeg)
    {
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8)
            throw new InvalidDataException("The selected file is not a valid JPEG image.");

        var offset = 2;
        while (offset < jpeg.Length)
        {
            while (offset < jpeg.Length && jpeg[offset] == 0xFF) offset++;
            if (offset >= jpeg.Length) break;
            var marker = jpeg[offset++];
            if (marker is 0xD9 or 0xDA) break;
            if (marker is 0xD8 or 0x01 or >= 0xD0 and <= 0xD7) continue;
            if (offset + 2 > jpeg.Length) break;
            var length = BinaryPrimitives.ReadUInt16BigEndian(jpeg.Slice(offset, 2));
            if (length < 2 || offset + length > jpeg.Length) break;

            if (marker is 0xC0 or 0xC1 or 0xC2 or 0xC3 or 0xC5 or 0xC6 or 0xC7 or 0xC9 or 0xCA or 0xCB or 0xCD or 0xCE or 0xCF)
            {
                if (length < 8) break;
                var height = BinaryPrimitives.ReadUInt16BigEndian(jpeg.Slice(offset + 3, 2));
                var width = BinaryPrimitives.ReadUInt16BigEndian(jpeg.Slice(offset + 5, 2));
                var components = jpeg[offset + 7];
                if (width == 0 || height == 0) break;
                return (width, height, components);
            }
            offset += length;
        }
        throw new InvalidDataException("Could not read image dimensions from the selected JPEG.");
    }

    private static void WriteAscii(Stream stream, string value) => stream.Write(Encoding.ASCII.GetBytes(value));
}
