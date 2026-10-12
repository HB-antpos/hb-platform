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
}
