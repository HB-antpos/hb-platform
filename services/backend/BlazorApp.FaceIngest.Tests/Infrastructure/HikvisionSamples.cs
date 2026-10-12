using System.Globalization;
using System.Text;

namespace BlazorApp.FaceIngest.Tests.Infrastructure;

/// <summary>
/// 仿照海康人脸抓拍推送的「不规范 multipart」样本（见调研报告第 2 节）：
/// boundary 是字面量 <c>boundary</c>；part 头带 Content-Length；Content-Disposition 末尾多一个分号；
/// JSON 里有重复键与中文；图片段含 \0、CRLF、非 UTF-8 字节，甚至一段看起来像 boundary 的字节。
/// 服务端必须逐字节原样落盘，不能被任何 multipart 解析改写。
/// </summary>
internal static class HikvisionSamples
{
    public const string MultipartContentType = "multipart/form-data; boundary=boundary";

    public static byte[] IrregularFaceCaptureMultipart()
    {
        var json = Encoding.UTF8.GetBytes(
            "{\"ipAddress\":\"192.168.1.64\",\"portNo\":80,\"protocol\":\"HTTP\",\"macAddress\":\"44:19:b6:00:00:01\","
            + "\"channelID\":1,\"dateTime\":\"2026-10-12T12:00:00+11:00\",\"activePostCount\":1,"
            + "\"eventType\":\"faceCapture\",\"eventState\":\"active\",\"eventDescription\":\"faceCapture\","
            + "\"faceCapture\":[{\"targetAttrs\":{\"contentID\":\"backgroundImage\",\"contentID\":\"backgroundImage\","
            + "\"deviceName\":\"门店入口\"},\"faces\":[{\"faceId\":1,\"contentID\":\"faceImage\",\"faceScore\":87}]}]}");

        byte[] picture =
        [
            0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01,
            0x00, 0x00, 0x0D, 0x0A, 0x0D, 0x0A, 0xC3, 0x28, 0xFE, 0xFF, 0x80, 0x81,
            .. Encoding.ASCII.GetBytes("--boundary"),
            0x0D, 0x0A, 0x00, 0xA0, 0xA1, 0xED, 0xA0, 0x80, 0xF8, 0x88, 0x80, 0x80,
            0xFF, 0xD9,
        ];

        using var stream = new MemoryStream();
        WriteAscii(stream, "--boundary\r\n");
        WriteAscii(stream, "Content-Disposition: form-data; name=\"faceCapture\";\r\n");
        WriteAscii(stream, "Content-Type: application/json; charset=\"UTF-8\"\r\n");
        WriteAscii(stream, string.Create(CultureInfo.InvariantCulture, $"Content-Length: {json.Length}\r\n\r\n"));
        stream.Write(json);
        WriteAscii(stream, "\r\n--boundary\r\n");
        WriteAscii(stream, "Content-Disposition: form-data; name=\"facePicture\"; filename=\"facePicture.jpg\";\r\n");
        WriteAscii(stream, "Content-Type: image/jpeg\r\n");
        WriteAscii(stream, string.Create(CultureInfo.InvariantCulture, $"Content-Length: {picture.Length}\r\n\r\n"));
        stream.Write(picture);
        WriteAscii(stream, "\r\n--boundary--\r\n");
        return stream.ToArray();
    }

    private static void WriteAscii(Stream stream, string text) => stream.Write(Encoding.ASCII.GetBytes(text));

    // ======================================================================== P1 解析器样本（全部假数据）

    /// <summary>官方人脸抓拍 JSON 的结构（调研报告第 2、3 节字段），内容为假数据。faceRect 用像素口径。</summary>
    public const string OfficialFaceCaptureJson = """
        {
        "ipAddress": "192.0.2.64",
        "portNo": 80,
        "protocol": "HTTP",
        "macAddress": "00:00:5e:00:53:01",
        "channelID": 1,
        "dateTime": "2026-10-12T12:00:00+11:00",
        "activePostCount": 1,
        "eventType": "faceCapture",
        "eventState": "active",
        "eventDescription": "faceCapture",
        "channelName": "Camera 01",
        "isDataRetransmission": false,
        "faceCapture": [{
        "targetAttrs": {
        "deviceChannel": 1,
        "deviceName": "FAKE CAMERA",
        "faceTime": "2026-10-12T11:59:58+11:00",
        "contentID": "backgroundImage",
        "contentID": "thermalPicture"
        },
        "faces": [{
        "faceId": 5288,
        "faceRect": { "height": 36.111, "width": 16.667, "x": 550.521, "y": 619.444 },
        "mask": { "value": "no" },
        "age": { "value": 30, "ageDeviation": 5 },
        "gender": { "value": "female" },
        "glass": { "value": "no" },
        "contentID": "faceImage",
        "stayDuration": 0,
        "faceScore": 35,
        "captureEndMark": "false",
        "currTemperature": 35.9,
        "identify": [{ "candidate": [{ "reserve_field": { "certificateNumber": "000000000000" } }] }]
        }],
        "uid": "20261012120000000000000000000000000000000000000000000000000000ab"
        }]
        }
        """;

    /// <summary>一张最小的「JPEG」：SOI + APP0 + SOF0（给出宽高）+ SOS + 若干字节 + EOI，用于宽高读取与原样存图断言。</summary>
    public static byte[] FakeJpeg(int width, int height, byte seed)
    {
        using var stream = new MemoryStream();
        stream.Write([0xFF, 0xD8]);
        stream.Write([0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00]);
        stream.Write([0xFF, 0xC0, 0x00, 0x11, 0x08, (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, 0x03,
            0x01, 0x22, 0x00, 0x02, 0x11, 0x01, 0x03, 0x11, 0x01]);
        stream.Write([0xFF, 0xDA, 0x00, 0x0C, 0x03, 0x01, 0x00, 0x02, 0x11, 0x03, 0x11, 0x00, 0x3F, 0x00]);
        for (var i = 0; i < 64; i++)
        {
            stream.WriteByte((byte)(seed + i));
        }

        // 中文注释：故意放一段不在行首的 "--boundary" 与 CRLF，验证不会被误切。
        stream.Write(Encoding.ASCII.GetBytes("x--boundary\r\n"));
        stream.Write([0xFF, 0xD9]);
        return stream.ToArray();
    }

    /// <summary>构造 multipart 的一段。</summary>
    internal sealed record SamplePart(string Headers, byte[] Content);

    /// <summary>按给定换行（CRLF / LF）与边界拼 multipart。headers 里用 \n 分隔多行，拼接时换成指定换行。</summary>
    public static byte[] BuildMultipart(string boundary, string newline, params SamplePart[] parts)
    {
        using var stream = new MemoryStream();
        foreach (var part in parts)
        {
            WriteAscii(stream, "--" + boundary + newline);
            foreach (var line in part.Headers.Split('\n'))
            {
                WriteAscii(stream, line + newline);
            }

            WriteAscii(stream, newline);
            stream.Write(part.Content);
            WriteAscii(stream, newline);
        }

        WriteAscii(stream, "--" + boundary + "--" + newline);
        return stream.ToArray();
    }

    /// <summary>官方结构的人脸抓拍：JSON + 人脸小图（facePicture，filename 后多分号）+ 背景图，part 头带正确的 Content-Length。</summary>
    public static byte[] OfficialFaceCaptureMultipart(string newline = "\r\n", string? json = null)
    {
        var jsonBytes = Encoding.UTF8.GetBytes(json ?? OfficialFaceCaptureJson);
        var face = FaceJpeg;
        var background = BackgroundJpeg;
        return BuildMultipart(
            "boundary",
            newline,
            new SamplePart(
                "Content-Disposition: form-data; name=\"faceCapture\"\nContent-Type: application/json; charset=\"UTF-8\"\n"
                + string.Create(CultureInfo.InvariantCulture, $"Content-Length: {jsonBytes.Length}"),
                jsonBytes),
            new SamplePart(
                "Content-Disposition: form-data; name=\"facePicture\"; filename=\"facePicture.jpg\";\nContent-Type: image/jpeg\n"
                + string.Create(CultureInfo.InvariantCulture, $"Content-Length: {face.Length}"),
                face),
            new SamplePart(
                "Content-Disposition: form-data; name=\"backgroundPicture\"; filename=\"background.jpg\";\nContent-Type: image/jpeg",
                background));
    }

    /// <summary>人脸小图（120×160）。</summary>
    public static byte[] FaceJpeg { get; } = FakeJpeg(120, 160, 0x10);

    /// <summary>背景图（1920×1080）。</summary>
    public static byte[] BackgroundJpeg { get; } = FakeJpeg(1920, 1080, 0x80);

    /// <summary>XML 移动侦测（VMD），multipart 里一段 MoveDetection.xml，命名空间 isapi.org/ver20。</summary>
    public static byte[] XmlVmdMultipart()
        => BuildMultipart(
            "---------------------------7e13971310878",
            "\r\n",
            new SamplePart(
                "Content-Disposition: form-data; name=\"MoveDetection.xml\"; filename=\"MoveDetection.xml\"\nContent-Type: application/xml; charset=\"UTF-8\"",
                Encoding.UTF8.GetBytes(VmdXml)));

    public const string VmdContentType = "multipart/form-data; boundary=---------------------------7e13971310878";

    public const string VmdXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <EventNotificationAlert version="2.0" xmlns="http://www.isapi.org/ver20/XMLSchema">
        <ipAddress>192.0.2.65</ipAddress>
        <macAddress>00:00:5e:00:53:02</macAddress>
        <channelID>2</channelID>
        <dateTime>2026-10-12T12:00:05+11:00</dateTime>
        <activePostCount>3</activePostCount>
        <eventType>VMD</eventType>
        <eventState>active</eventState>
        <eventDescription>Motion alarm</eventDescription>
        </EventNotificationAlert>
        """;

    /// <summary>老机型整包裸 XML（不是 multipart），命名空间 hikvision.com/ver20。</summary>
    public const string BareVideoLossXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <EventNotificationAlert version="2.0" xmlns="http://www.hikvision.com/ver20/XMLSchema">
        <ipAddress>192.0.2.66</ipAddress>
        <dynChannelID>1</dynChannelID>
        <dateTime>2026-10-12T01:00:00Z</dateTime>
        <eventType>videoloss</eventType>
        <eventState>inactive</eventState>
        <isDataRetransmission>true</isDataRetransmission>
        </EventNotificationAlert>
        """;

    /// <summary>XML 里有未转义的 &amp;（设备名），以及合法实体 &amp;amp; 不应被二次转义。</summary>
    public const string UnescapedAmpersandXml = """
        <EventNotificationAlert xmlns="http://www.isapi.org/ver20/XMLSchema">
        <channelName>Front & Back &amp; Side</channelName>
        <dateTime>2026-10-12T12:00:00+11:00</dateTime>
        <eventType>facedetection</eventType>
        <eventState>active</eventState>
        </EventNotificationAlert>
        """;

    /// <summary>contentID 与 part 名对不上的两张人脸图（两个 faces[]，归一化 faceRect）。</summary>
    public static byte[] TwoFacesContentIdMismatchMultipart()
    {
        var json = Encoding.UTF8.GetBytes("""
            {"eventType":"faceCapture","eventState":"active","dateTime":"2026-10-12T12:00:00+11:00","channelID":"3",
             "faceCapture":[{"faces":[
               {"faceId":"A1","contentID":"faceImage","faceScore":91.5,"faceRect":{"x":0.4,"y":0.577,"width":0.093,"height":0.165}},
               {"faceId":"A2","contentID":"faceImage","faceScore":88,"faceRect":{"x":0.1,"y":0.2,"width":0.05,"height":0.08}}]}]}
            """);
        return BuildMultipart(
            "boundary",
            "\r\n",
            new SamplePart("Content-Disposition: form-data; name=\"faceCapture\"\nContent-Type: application/json", json),
            new SamplePart("Content-Disposition: form-data; name=\"facePicture1\"; filename=\"facePicture1.jpg\"\nContent-Type: image/jpeg", FakeJpeg(100, 100, 1)),
            new SamplePart("Content-Disposition: form-data; name=\"facePicture2\"; filename=\"facePicture2.jpg\"\nContent-Type: image/pjpeg", FakeJpeg(90, 110, 2)));
    }
}
