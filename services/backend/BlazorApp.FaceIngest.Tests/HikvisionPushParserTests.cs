using System.Globalization;
using System.Text;
using System.Text.Json;
using BlazorApp.FaceIngest.Tests.Infrastructure;
using Xunit;

namespace BlazorApp.FaceIngest.Tests;

/// <summary>宽容解析器：调研报告第 2 节列出的每类不规范报文各一条，外加随机截断模糊测试。</summary>
public sealed class HikvisionPushParserTests
{
    [Fact]
    public void 官方结构人脸抓拍_字段齐全_人脸与背景图归类_contentID对不上按顺序配对()
    {
        var parsed = HikvisionPushParser.Parse(HikvisionSamples.OfficialFaceCaptureMultipart(), HikvisionSamples.MultipartContentType);

        Assert.Equal(CaptureParseStatus.Parsed, parsed.Status);
        Assert.Equal("json", parsed.EventFormat);
        Assert.Equal("faceCapture", parsed.EventType);
        Assert.Equal("active", parsed.EventState);
        Assert.Equal(new DateTimeOffset(2026, 10, 12, 12, 0, 0, TimeSpan.FromHours(11)), parsed.CameraEventTime);
        Assert.Equal(TimeSpan.FromHours(11), parsed.CameraEventTime!.Value.Offset);
        Assert.Equal("00:00:5e:00:53:01", parsed.MacAddress);
        Assert.Equal(1, parsed.Channel);
        Assert.False(parsed.IsRetransmission);
        Assert.Equal(1, parsed.FaceCount);

        var face = Assert.Single(parsed.Faces);
        Assert.Equal("5288", face.CameraFaceId);
        Assert.Equal("faceImage", face.ContentId);
        Assert.Equal(550.521m, face.RectX);
        Assert.Equal(619.444m, face.RectY);
        Assert.Equal(16.667m, face.RectW);
        Assert.Equal(36.111m, face.RectH);
        Assert.False(face.RectNormalized);
        Assert.Equal(35m, face.FaceScore);
        using (var attributes = JsonDocument.Parse(face.AttributesJson!))
        {
            Assert.Equal("female", attributes.RootElement.GetProperty("gender").GetProperty("value").GetString());
            Assert.Equal(30, attributes.RootElement.GetProperty("age").GetProperty("value").GetInt32());
            Assert.True(attributes.RootElement.TryGetProperty("mask", out _));
        }

        // 数据最小化：比对结果 / 证件号 / 体温不进 AttributesJson。
        Assert.DoesNotContain("identify", face.AttributesJson, StringComparison.Ordinal);
        Assert.DoesNotContain("certificateNumber", face.AttributesJson, StringComparison.Ordinal);
        Assert.DoesNotContain("Temperature", face.AttributesJson, StringComparison.Ordinal);

        Assert.Equal(2, parsed.Images.Count);
        var faceImage = parsed.Images[0];
        Assert.Equal(1, faceImage.PartIndex);
        Assert.Equal(CaptureImageKind.Face, faceImage.Kind);
        Assert.Equal(0, faceImage.FaceIndex);
        Assert.Equal(HikvisionSamples.FaceJpeg, faceImage.Data.ToArray());
        Assert.Equal((120, 160), (faceImage.Width, faceImage.Height));
        var background = parsed.Images[1];
        Assert.Equal(2, background.PartIndex);
        Assert.Equal(CaptureImageKind.Background, background.Kind);
        Assert.Null(background.FaceIndex);
        Assert.Equal(HikvisionSamples.BackgroundJpeg, background.Data.ToArray());
        Assert.Equal((1920, 1080), (background.Width, background.Height));

        // 官方样例 facePicture ↔ contentID faceImage 对不上：按顺序配对并记一次 note；Content-Length 与扫描一致，不记。
        Assert.Contains("paired face images with faces[] by order", parsed.Note, StringComparison.Ordinal);
        Assert.DoesNotContain("Content-Length", parsed.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void JSON重复键取第一个出现的值_不因重复键失败()
    {
        var json = HikvisionSamples.OfficialFaceCaptureJson.Replace(
            "\"eventType\": \"faceCapture\",",
            "\"eventType\": \"faceCapture\",\n\"eventType\": \"VMD\",",
            StringComparison.Ordinal);
        var parsed = HikvisionPushParser.Parse(HikvisionSamples.OfficialFaceCaptureMultipart(json: json), HikvisionSamples.MultipartContentType);

        Assert.Equal(CaptureParseStatus.Parsed, parsed.Status);
        Assert.Equal("faceCapture", parsed.EventType);
        Assert.Single(parsed.Faces);
    }

    [Fact]
    public void XML移动侦测_非人脸事件_仍解析出事件字段()
    {
        var parsed = HikvisionPushParser.Parse(HikvisionSamples.XmlVmdMultipart(), HikvisionSamples.VmdContentType);

        Assert.Equal(CaptureParseStatus.NotFaceEvent, parsed.Status);
        Assert.Equal("xml", parsed.EventFormat);
        Assert.Equal("VMD", parsed.EventType);
        Assert.Equal("active", parsed.EventState);
        Assert.Equal("00:00:5e:00:53:02", parsed.MacAddress);
        Assert.Equal(2, parsed.Channel);
        Assert.Equal(new DateTimeOffset(2026, 10, 12, 12, 0, 5, TimeSpan.FromHours(11)), parsed.CameraEventTime);
        Assert.Equal(0, parsed.FaceCount);
        Assert.Empty(parsed.Images);
        Assert.Null(parsed.Note);
    }

    [Fact]
    public void 整个body是裸XML_当作只有事件体()
    {
        var parsed = HikvisionPushParser.Parse(Encoding.UTF8.GetBytes(HikvisionSamples.BareVideoLossXml), "application/xml; charset=\"UTF-8\"");

        Assert.Equal(CaptureParseStatus.NotFaceEvent, parsed.Status);
        Assert.Equal("videoloss", parsed.EventType);
        Assert.Equal("inactive", parsed.EventState);
        Assert.Equal(1, parsed.Channel);
        Assert.True(parsed.IsRetransmission);
        Assert.Equal(TimeSpan.Zero, parsed.CameraEventTime!.Value.Offset);
        Assert.Empty(parsed.Images);
    }

    [Fact]
    public void XML未转义的and先修复再解析_合法实体不受影响()
    {
        var parsed = HikvisionPushParser.Parse(Encoding.UTF8.GetBytes(HikvisionSamples.UnescapedAmpersandXml), "text/xml");

        Assert.Equal("facedetection", parsed.EventType);
        // facedetection 属于人脸类事件；XML 里没有 faces[] 也没有图片。
        Assert.Equal(CaptureParseStatus.Parsed, parsed.Status);
        Assert.Contains("unescaped '&'; repaired", parsed.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void Part头Content_Length与实际不符_以边界扫描为准并记note()
    {
        var json = Encoding.UTF8.GetBytes(HikvisionSamples.OfficialFaceCaptureJson);
        var body = HikvisionSamples.BuildMultipart(
            "boundary",
            "\r\n",
            new HikvisionSamples.SamplePart(
                "Content-Disposition: form-data; name=\"faceCapture\"\nContent-Type: application/json\nContent-Length: 12",
                json),
            new HikvisionSamples.SamplePart(
                "Content-Disposition: form-data; name=\"facePicture\"\nContent-Type: image/jpeg\nContent-Length: 999999",
                HikvisionSamples.FaceJpeg));

        var parsed = HikvisionPushParser.Parse(body, HikvisionSamples.MultipartContentType);

        Assert.Equal(CaptureParseStatus.Parsed, parsed.Status);
        Assert.Equal(HikvisionSamples.FaceJpeg, Assert.Single(parsed.Images).Data.ToArray());
        Assert.Contains("part #0 Content-Length 12 differs from boundary scan", parsed.Note, StringComparison.Ordinal);
        Assert.Contains("part #1 Content-Length 999999 differs from boundary scan", parsed.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void 裸LF换行也接受()
    {
        var parsed = HikvisionPushParser.Parse(HikvisionSamples.OfficialFaceCaptureMultipart(newline: "\n"), HikvisionSamples.MultipartContentType);

        Assert.Equal(CaptureParseStatus.Parsed, parsed.Status);
        Assert.Equal(2, parsed.Images.Count);
        Assert.Equal(HikvisionSamples.FaceJpeg, parsed.Images[0].Data.ToArray());
        Assert.Equal(HikvisionSamples.BackgroundJpeg, parsed.Images[1].Data.ToArray());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("multipart/form-data")]
    public void 没有Content_Type或没有boundary_从正文首行推断边界(string? contentType)
    {
        var parsed = HikvisionPushParser.Parse(HikvisionSamples.OfficialFaceCaptureMultipart(), contentType);

        Assert.Equal(CaptureParseStatus.Parsed, parsed.Status);
        Assert.Equal(2, parsed.Images.Count);
        Assert.Contains("inferred from first line", parsed.Note, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("multipart/form-data; BOUNDARY=\"boundary\"")]
    [InlineData("Multipart/Form-Data;;boundary=boundary;")]
    [InlineData("multipart/form-data; charset=utf-8; boundary = \"boundary\" ")]
    public void boundary参数大小写_引号_多余分号都能识别(string contentType)
    {
        var parsed = HikvisionPushParser.Parse(HikvisionSamples.OfficialFaceCaptureMultipart(), contentType);

        Assert.Equal(CaptureParseStatus.Parsed, parsed.Status);
        Assert.Equal(2, parsed.Images.Count);
        Assert.DoesNotContain("inferred", parsed.Note ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void contentID与part名对不上的两张人脸图_按出现顺序与faces配对()
    {
        var parsed = HikvisionPushParser.Parse(HikvisionSamples.TwoFacesContentIdMismatchMultipart(), HikvisionSamples.MultipartContentType);

        Assert.Equal(CaptureParseStatus.Parsed, parsed.Status);
        Assert.Equal(2, parsed.FaceCount);
        Assert.Equal(3, parsed.Channel);
        Assert.Equal(2, parsed.Images.Count);
        Assert.All(parsed.Images, image => Assert.Equal(CaptureImageKind.Face, image.Kind));
        Assert.Equal([0, 1], parsed.Images.Select(image => image.FaceIndex!.Value).ToArray());
        Assert.Equal("image/pjpeg", parsed.Images[1].ContentType);
        Assert.Equal((90, 110), (parsed.Images[1].Width, parsed.Images[1].Height));
        Assert.True(parsed.Faces[0].RectNormalized);
        Assert.Equal(0.4m, parsed.Faces[0].RectX);
        Assert.Equal(91.5m, parsed.Faces[0].FaceScore);
        Assert.Equal("A2", parsed.Faces[1].CameraFaceId);
        Assert.Equal(1, CountOccurrences(parsed.Note!, "paired face images with faces[] by order"));
    }

    [Fact]
    public void contentID能对上时按名字配对()
    {
        var json = HikvisionSamples.OfficialFaceCaptureJson.Replace("\"contentID\": \"faceImage\"", "\"contentID\": \"facePicture\"", StringComparison.Ordinal);

        var parsed = HikvisionPushParser.Parse(HikvisionSamples.OfficialFaceCaptureMultipart(json: json), HikvisionSamples.MultipartContentType);

        Assert.Equal(CaptureParseStatus.Parsed, parsed.Status);
        Assert.Equal(0, parsed.Images[0].FaceIndex);
        Assert.DoesNotContain("paired", parsed.Note ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void 无Content_Type的part以FFD8FF开头_识别为图片()
    {
        var body = HikvisionSamples.BuildMultipart(
            "boundary",
            "\r\n",
            new HikvisionSamples.SamplePart("Content-Disposition: form-data; name=\"faceCapture\"", Encoding.UTF8.GetBytes(HikvisionSamples.OfficialFaceCaptureJson)),
            new HikvisionSamples.SamplePart("Content-Disposition: form-data;", HikvisionSamples.FaceJpeg));

        var parsed = HikvisionPushParser.Parse(body, HikvisionSamples.MultipartContentType);

        var image = Assert.Single(parsed.Images);
        Assert.Equal(CaptureImageKind.Other, image.Kind);
        Assert.Equal(HikvisionSamples.FaceJpeg, image.Data.ToArray());
        Assert.Equal("faceCapture", parsed.EventType);
        // faces[] 有 1 条却没有人脸类图片：记部分解析（多半是图片上传方式不是 binary）。
        Assert.Equal(CaptureParseStatus.PartiallyParsed, parsed.Status);
        Assert.Contains("no face image part", parsed.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void P0不规范样本_也能解析()
    {
        var parsed = HikvisionPushParser.Parse(HikvisionSamples.IrregularFaceCaptureMultipart(), HikvisionSamples.MultipartContentType);

        Assert.Equal(CaptureParseStatus.Parsed, parsed.Status);
        Assert.Equal("faceCapture", parsed.EventType);
        var image = Assert.Single(parsed.Images);
        Assert.Equal(CaptureImageKind.Face, image.Kind);
        Assert.Equal(0, image.FaceIndex);
    }

    [Fact]
    public void 报文在图片中间被截断_部分解析并记note()
    {
        var full = HikvisionSamples.OfficialFaceCaptureMultipart();
        var cut = full.AsSpan(0, full.Length - HikvisionSamples.BackgroundJpeg.Length / 2 - 20).ToArray();

        var parsed = HikvisionPushParser.Parse(cut, HikvisionSamples.MultipartContentType);

        Assert.Equal(CaptureParseStatus.PartiallyParsed, parsed.Status);
        Assert.Contains("closing boundary missing", parsed.Note, StringComparison.Ordinal);
        Assert.Equal(HikvisionSamples.FaceJpeg, parsed.Images[0].Data.ToArray());
    }

    [Theory]
    [InlineData("")]
    [InlineData("hello world")]
    [InlineData("{ not json")]
    [InlineData("<EventNotificationAlert><eventType>")]
    public void 空body_乱码_坏JSON_坏XML_都是Unparsed且不抛异常(string text)
    {
        var parsed = HikvisionPushParser.Parse(Encoding.UTF8.GetBytes(text), "application/octet-stream");

        Assert.Equal(CaptureParseStatus.Unparsed, parsed.Status);
        Assert.False(string.IsNullOrEmpty(parsed.Note));
        Assert.True(parsed.Note!.Length <= 500);
    }

    [Fact]
    public void dateTime没有时区偏移_置空并记note()
    {
        var json = HikvisionSamples.OfficialFaceCaptureJson.Replace("2026-10-12T12:00:00+11:00", "2026-10-12T12:00:00", StringComparison.Ordinal);

        var parsed = HikvisionPushParser.Parse(HikvisionSamples.OfficialFaceCaptureMultipart(json: json), HikvisionSamples.MultipartContentType);

        Assert.Null(parsed.CameraEventTime);
        Assert.Contains("dateTime has no UTC offset", parsed.Note, StringComparison.Ordinal);
        Assert.Equal(CaptureParseStatus.Parsed, parsed.Status);
    }

    [Fact]
    public void XML里的DTD被拒绝_不做实体展开()
    {
        const string xxe = """
            <?xml version="1.0"?>
            <!DOCTYPE foo [ <!ENTITY xxe SYSTEM "file:///etc/passwd"> ]>
            <EventNotificationAlert><eventType>&xxe;</eventType></EventNotificationAlert>
            """;

        var parsed = HikvisionPushParser.Parse(Encoding.UTF8.GetBytes(xxe), "application/xml");

        Assert.Equal(CaptureParseStatus.Unparsed, parsed.Status);
        Assert.Null(parsed.EventType);
    }

    [Fact]
    public void 随机截断官方样例200次_从不抛异常_状态与note合法()
    {
        var full = HikvisionSamples.OfficialFaceCaptureMultipart();
        var random = new Random(20261012);
        for (var i = 0; i < 200; i++)
        {
            var length = random.Next(0, full.Length + 1);
            var body = full.AsSpan(0, length).ToArray();
            // 一半样本再随机翻转几个字节。
            if (i % 2 == 1 && body.Length > 0)
            {
                for (var flip = 0; flip < 3; flip++)
                {
                    body[random.Next(body.Length)] ^= (byte)random.Next(1, 256);
                }
            }

            var parsed = HikvisionPushParser.Parse(body, i % 3 == 0 ? null : HikvisionSamples.MultipartContentType);

            Assert.True(Enum.IsDefined(parsed.Status), string.Create(CultureInfo.InvariantCulture, $"iteration {i}"));
            Assert.True(parsed.Note is null || parsed.Note.Length <= 500);
            Assert.All(parsed.Images, image => Assert.True(image.Data.Length > 0));
        }
    }

    [Fact]
    public void JPEG宽高读取_非JPEG返回null()
    {
        Assert.Equal((640, 480), JpegDimensions.TryRead(HikvisionSamples.FakeJpeg(640, 480, 7)));
        Assert.Null(JpegDimensions.TryRead([0xFF, 0xD8, 0xFF]));
        Assert.Null(JpegDimensions.TryRead(Encoding.ASCII.GetBytes("not a jpeg")));
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        for (var index = text.IndexOf(value, StringComparison.Ordinal); index >= 0; index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
