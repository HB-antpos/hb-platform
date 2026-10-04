using System.Globalization;
using iTextSharp.text;
using iTextSharp.text.pdf;

namespace BlazorApp.Api.Services;

/// <summary>把官方 CE1 字段与独立签署证据合成不可编辑的留档原件；NSW 使用公司同意书。</summary>
public static partial class EmployeeMinorComplianceDocumentBuilder
{
    public const string QldTemplateSource = "https://www.oir.qld.gov.au/system/files/2023-02/6457-parents-consent-form-for-school-aged-or-young-child.pdf";
    public const string QldTemplateSha256 = "c0577c3fea1c8cd219c4361a53f0a57268f48c8b2b924b473efa9c61f9b43841";

    public sealed record Field(string Label, string? Value);
    public sealed record Section(string Title, IReadOnlyList<Field> Fields);
    public sealed record Input(
        string FormType, int ProfileId, int Version, string SignedName,
        DateTime SignedAtUtc, string SignatureData, string ConsentScope,
        IReadOnlyDictionary<string, string> Ce1Fields, IReadOnlyList<Section> Sections);

    public static byte[] Build(Input input)
    {
        var signature = DecodeSignature(input.SignatureData);
        var appendix = BuildAppendix(input, signature);
        if (input.FormType != "QLD_CE1") return appendix;

        using var template = typeof(EmployeeMinorComplianceDocumentBuilder).Assembly
            .GetManifestResourceStream("BlazorApp.Api.Assets.minor-employment-ce1-v4.pdf")
            ?? throw new InvalidOperationException("官方 CE1 v4 模板资源不存在");
        using var reader = new PdfReader(template);
        using var completed = new MemoryStream();
        using (var stamper = new PdfStamper(reader, completed))
        {
            stamper.Writer.CloseStream = false;
            var font = BaseFont.CreateFont(BaseFont.HELVETICA, BaseFont.WINANSI, BaseFont.NOT_EMBEDDED);
            var cjkFont = BaseFont.CreateFont("STSong-Light", "UniGB-UCS2-H", BaseFont.NOT_EMBEDDED);
            stamper.AcroFields.AddSubstitutionFont(cjkFont);
            foreach (var field in input.Ce1Fields)
            {
                if (!stamper.AcroFields.Fields.ContainsKey(field.Key))
                    throw new InvalidOperationException($"官方 CE1 字段不存在: {field.Key}");
                stamper.AcroFields.SetFieldProperty(field.Key, "textfont", font, null);
                stamper.AcroFields.SetFieldProperty(field.Key, "textsize", 8f, null);
                // 长内容在签署附页完整保留，避免在官方小单元格中截断。
                var value = field.Value.Length > 85 ? "See signed details appendix" : field.Value;
                if (!stamper.AcroFields.SetField(field.Key, value))
                    throw new InvalidOperationException($"官方 CE1 字段填写失败: {field.Key}");
            }
            var image = Image.GetInstance(signature);
            image.ScaleToFit(210, 35);
            image.SetAbsolutePosition(85, 537);
            stamper.GetOverContent(4).AddImage(image);
            for (var page = 1; page <= reader.NumberOfPages; page++)
            {
                ColumnText.ShowTextAligned(stamper.GetOverContent(page), Element.ALIGN_RIGHT,
                    new Phrase($"HB record {input.ProfileId} / v{input.Version} / signed details attached", new Font(font, 6)),
                    555, page == 1 ? 120 : 12, 0);
            }
            // 本流程归档的版本不可再编辑；原始空白模板仍随应用保留。
            stamper.FormFlattening = true;
        }

        using var output = new MemoryStream();
        using (var document = new Document())
        {
            var copy = new PdfCopy(document, output) { CloseStream = false };
            document.Open();
            using var filledReader = new PdfReader(completed.ToArray());
            using var appendixReader = new PdfReader(appendix);
            for (var page = 1; page <= filledReader.NumberOfPages; page++) copy.AddPage(copy.GetImportedPage(filledReader, page));
            for (var page = 1; page <= appendixReader.NumberOfPages; page++) copy.AddPage(copy.GetImportedPage(appendixReader, page));
        }
        return output.ToArray();
    }

    public static byte[] DecodeSignature(string data)
    {
        const string prefix = "data:image/png;base64,";
        if (!data.StartsWith(prefix, StringComparison.Ordinal) || data.Length > 400_000)
            throw new ArgumentException("签名必须是大小不超过 300 KB 的 PNG 图片", nameof(data));
        byte[] bytes;
        try { bytes = Convert.FromBase64String(data[prefix.Length..]); }
        catch (FormatException ex) { throw new ArgumentException("签名图片格式无效", nameof(data), ex); }
        if (bytes.Length < 100 || bytes.Length > 300_000 || !bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            throw new ArgumentException("签名图片格式无效", nameof(data));
        // 先检查 PNG IHDR，防止解码超大图片耗尽内存。
        var width = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16, 4));
        var height = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20, 4));
        if (width is < 50 or > 2000 || height is < 20 or > 1000)
            throw new ArgumentException("签名图片尺寸无效", nameof(data));
        using var decoded = SixLabors.ImageSharp.Image.Load<SixLabors.ImageSharp.PixelFormats.Rgba32>(bytes);
        var ink = 0;
        for (var y = 0; y < decoded.Height; y++)
        for (var x = 0; x < decoded.Width; x++)
        {
            var pixel = decoded[x, y];
            if (pixel.A > 50 && (pixel.R < 220 || pixel.G < 220 || pixel.B < 220)) ink++;
        }
        if (ink < 25) throw new ArgumentException("请手写签名后提交", nameof(data));
        return bytes;
    }

    private static byte[] BuildAppendix(Input input, byte[] signature)
    {
        using var stream = new MemoryStream();
        using (var document = new Document(PageSize.A4, 40, 40, 40, 40))
        {
            var writer = PdfWriter.GetInstance(document, stream);
            writer.CloseStream = false;
            document.Open();
            var latin = BaseFont.CreateFont(BaseFont.HELVETICA, BaseFont.WINANSI, BaseFont.NOT_EMBEDDED);
            var cjk = BaseFont.CreateFont("STSong-Light", "UniGB-UCS2-H", BaseFont.NOT_EMBEDDED);
            var bodyFont = new Font(latin, 9);
            var selector = new FontSelector();
            selector.AddFont(bodyFont);
            selector.AddFont(new Font(cjk, 9));
            document.Add(new Paragraph(input.FormType == "QLD_CE1" ? "CE1 - Signed details appendix" : "NSW - Parent / guardian company consent", new Font(latin, 16, Font.BOLD)) { SpacingAfter = 12 });
            document.Add(new Paragraph($"Record {input.ProfileId} | Version {input.Version} | Signed {input.SignedAtUtc.ToUniversalTime():yyyy-MM-dd HH:mm:ss} UTC", bodyFont) { SpacingAfter = 8 });
            document.Add(new Paragraph(input.FormType == "QLD_CE1"
                ? "The first four pages use Queensland CE1 version 4. This appendix records all details reviewed in the employer's online signing process. It does not claim government approval of electronic signing."
                : "This is the employer's safeguarding policy for ordinary retail employment. It is not a Queensland CE1 form or a statement that NSW law universally requires parental consent.", bodyFont) { SpacingAfter = 12 });
            foreach (var section in input.Sections)
            {
                document.Add(new Paragraph(section.Title, new Font(latin, 11, Font.BOLD)) { SpacingBefore = 10, SpacingAfter = 6 });
                var table = new PdfPTable(2) { WidthPercentage = 100, SplitLate = false, SpacingAfter = 8 };
                table.SetWidths(new float[] { 1.25f, 2.75f });
                foreach (var field in section.Fields)
                {
                    table.AddCell(new PdfPCell(selector.Process(field.Label)) { Padding = 5, BackgroundColor = new BaseColor(242, 245, 249) });
                    table.AddCell(new PdfPCell(selector.Process(string.IsNullOrWhiteSpace(field.Value) ? "Not provided" : field.Value)) { Padding = 5 });
                }
                document.Add(table);
            }
            document.Add(new Paragraph("Parent / guardian declaration", new Font(latin, 11, Font.BOLD)) { SpacingBefore = 10, SpacingAfter = 6 });
            document.Add(selector.Process(input.ConsentScope));
            document.Add(new Paragraph(selector.Process($"Signed by: {input.SignedName}")) { SpacingBefore = 8 });
            var image = Image.GetInstance(signature);
            image.ScaleToFit(250, 75);
            image.SpacingBefore = 8;
            document.Add(image);
            document.Add(new Paragraph(input.SignedAtUtc.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture), bodyFont));
        }
        return stream.ToArray();
    }
}
