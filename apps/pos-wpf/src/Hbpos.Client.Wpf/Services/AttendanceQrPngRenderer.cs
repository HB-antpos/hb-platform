using QRCoder;

namespace Hbpos.Client.Wpf.Services;

internal sealed record AttendanceQrPng(byte[] PngBytes, int ModuleCount, int PixelsPerModule, int PixelSize);

internal static class AttendanceQrPngRenderer
{
    internal const int ContainerPixels = 260;

    public static AttendanceQrPng Render(string token, int containerPixels = ContainerPixels)
    {
        using var data = QRCodeGenerator.GenerateQrCode(token, QRCodeGenerator.ECCLevel.L);
        // QRCoder 的矩阵已包含 quiet zone；只允许整数倍模块，避免扫码边缘被插值模糊。
        var moduleCount = data.ModuleMatrix.Count;
        var pixelsPerModule = containerPixels / moduleCount;
        if (pixelsPerModule < 2)
        {
            throw new InvalidOperationException($"二维码超出 {containerPixels}px 扫码预算：{moduleCount} modules。");
        }

        var bytes = new PngByteQRCode(data).GetGraphic(pixelsPerModule, drawQuietZones: true);
        return new AttendanceQrPng(bytes, moduleCount, pixelsPerModule, moduleCount * pixelsPerModule);
    }
}
