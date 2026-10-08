using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Hbpos.Client.Wpf.Services;

/// <summary>
/// 把动态 GIF 逐帧合成到同一张 WriteableBitmap 上。
/// WPF 的 Image/BitmapImage 只显示 GIF 第一帧，客显广告位的 GIF 动图因此不会动。
/// 这里按需解码当前帧（不预先展开所有帧），内存只占一到两张画布大小。
/// </summary>
internal sealed class AnimatedGifRenderer
{
    // 超大画布直接放弃动画、退回静态图，避免 4K 以上动图吃掉大量内存。
    private const long MaxCanvasPixels = 4096L * 4096L;
    private const int BytesPerPixel = 4;
    // 和浏览器一致：帧延时小于 20ms（含 0）按 100ms 处理，否则这类 GIF 会被播得飞快。
    private static readonly TimeSpan MinimumFrameDelay = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan DefaultFrameDelay = TimeSpan.FromMilliseconds(100);

    private readonly GifBitmapDecoder _decoder;
    private readonly GifFrameInfo[] _frames;
    private readonly byte[] _canvas;
    private readonly int _stride;
    private byte[]? _restoreSnapshot;
    private int _composedFrameIndex = -1;

    private AnimatedGifRenderer(GifBitmapDecoder decoder, GifFrameInfo[] frames, int width, int height)
    {
        _decoder = decoder;
        _frames = frames;
        Width = width;
        Height = height;
        _stride = width * BytesPerPixel;
        _canvas = new byte[_stride * height];
        Bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
        LoopDuration = TimeSpan.FromTicks(frames.Sum(frame => frame.Delay.Ticks));
    }

    public WriteableBitmap Bitmap { get; }

    public int Width { get; }

    public int Height { get; }

    public int FrameCount => _frames.Length;

    public TimeSpan LoopDuration { get; }

    public TimeSpan GetFrameDelay(int frameIndex) => _frames[frameIndex].Delay;

    /// <summary>
    /// 只有多帧 GIF 才返回渲染器；单帧 GIF、非 GIF 或画布过大都返回 null，调用方继续按静态图片显示。
    /// </summary>
    public static AnimatedGifRenderer? TryCreate(byte[] data)
    {
        if (!IsGif(data))
        {
            return null;
        }

        var decoder = new GifBitmapDecoder(
            new MemoryStream(data, writable: false),
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnDemand);
        if (decoder.Frames.Count < 2)
        {
            return null;
        }

        var frames = new GifFrameInfo[decoder.Frames.Count];
        var maxRight = 0;
        var maxBottom = 0;
        for (var index = 0; index < frames.Length; index++)
        {
            var frame = decoder.Frames[index];
            var metadata = frame.Metadata as BitmapMetadata;
            var left = ReadUInt16(metadata, "/imgdesc/Left");
            var top = ReadUInt16(metadata, "/imgdesc/Top");
            frames[index] = new GifFrameInfo(
                left,
                top,
                frame.PixelWidth,
                frame.PixelHeight,
                ResolveFrameDelay(ReadUInt16(metadata, "/grctlext/Delay")),
                (GifFrameDisposal)ReadByte(metadata, "/grctlext/Disposal"));
            maxRight = Math.Max(maxRight, left + frame.PixelWidth);
            maxBottom = Math.Max(maxBottom, top + frame.PixelHeight);
        }

        var (width, height) = ResolveCanvasSize(decoder, maxRight, maxBottom);
        if (width <= 0 || height <= 0 || (long)width * height > MaxCanvasPixels)
        {
            return null;
        }

        return new AnimatedGifRenderer(decoder, frames, width, height);
    }

    internal static bool IsGif(ReadOnlySpan<byte> data)
    {
        return data.Length >= 6
            && data[0] == (byte)'G'
            && data[1] == (byte)'I'
            && data[2] == (byte)'F'
            && data[3] == (byte)'8'
            && (data[4] == (byte)'7' || data[4] == (byte)'9')
            && data[5] == (byte)'a';
    }

    internal static TimeSpan ResolveFrameDelay(int delayCentiseconds)
    {
        var delay = TimeSpan.FromMilliseconds(delayCentiseconds * 10d);
        return delay < MinimumFrameDelay ? DefaultFrameDelay : delay;
    }

    /// <summary>
    /// 渲染指定帧。GIF 差分帧依赖上一帧画面，正常按 0、1、2… 顺序推进；
    /// 跳帧或回到第 0 帧时清空画布，从头重新合成到目标帧。
    /// </summary>
    public void RenderFrame(int frameIndex)
    {
        if (frameIndex != _composedFrameIndex + 1)
        {
            Array.Clear(_canvas);
            _restoreSnapshot = null;
            _composedFrameIndex = -1;
            for (var index = 0; index < frameIndex; index++)
            {
                ComposeFrame(index);
            }
        }

        ComposeFrame(frameIndex);
        Bitmap.WritePixels(new Int32Rect(0, 0, Width, Height), _canvas, _stride, 0);
    }

    private void ComposeFrame(int frameIndex)
    {
        if (frameIndex > 0)
        {
            ApplyDisposal(_frames[frameIndex - 1]);
        }

        var frame = _frames[frameIndex];
        if (frame.Disposal == GifFrameDisposal.RestorePrevious)
        {
            _restoreSnapshot ??= new byte[_canvas.Length];
            Buffer.BlockCopy(_canvas, 0, _restoreSnapshot, 0, _canvas.Length);
        }

        var source = new FormatConvertedBitmap(_decoder.Frames[frameIndex], PixelFormats.Bgra32, null, 0);
        var frameStride = frame.Width * BytesPerPixel;
        var framePixels = new byte[frameStride * frame.Height];
        source.CopyPixels(framePixels, frameStride, 0);

        var visibleWidth = Math.Min(frame.Width, Width - frame.Left);
        var visibleHeight = Math.Min(frame.Height, Height - frame.Top);
        for (var y = 0; y < visibleHeight; y++)
        {
            var sourceOffset = y * frameStride;
            var targetOffset = ((frame.Top + y) * _stride) + (frame.Left * BytesPerPixel);
            for (var x = 0; x < visibleWidth; x++)
            {
                var sourcePixel = sourceOffset + (x * BytesPerPixel);
                // GIF 只有全透明/不透明两种像素：透明像素保留下层画面，差分帧就靠这一点。
                if (framePixels[sourcePixel + 3] != 0)
                {
                    Buffer.BlockCopy(framePixels, sourcePixel, _canvas, targetOffset + (x * BytesPerPixel), BytesPerPixel);
                }
            }
        }

        _composedFrameIndex = frameIndex;
    }

    private void ApplyDisposal(GifFrameInfo previousFrame)
    {
        switch (previousFrame.Disposal)
        {
            case GifFrameDisposal.RestoreBackground:
                // 与主流浏览器一致，背景恢复为透明而不是逻辑屏幕背景色。
                var clearWidth = Math.Min(previousFrame.Width, Width - previousFrame.Left);
                var clearHeight = Math.Min(previousFrame.Height, Height - previousFrame.Top);
                for (var y = 0; y < clearHeight; y++)
                {
                    var offset = ((previousFrame.Top + y) * _stride) + (previousFrame.Left * BytesPerPixel);
                    Array.Clear(_canvas, offset, clearWidth * BytesPerPixel);
                }

                break;
            case GifFrameDisposal.RestorePrevious when _restoreSnapshot is not null:
                Buffer.BlockCopy(_restoreSnapshot, 0, _canvas, 0, _canvas.Length);
                break;
        }
    }

    private static (int Width, int Height) ResolveCanvasSize(GifBitmapDecoder decoder, int frameRight, int frameBottom)
    {
        int screenWidth;
        int screenHeight;
        try
        {
            screenWidth = ReadUInt16(decoder.Metadata, "/logscrdesc/Width");
            screenHeight = ReadUInt16(decoder.Metadata, "/logscrdesc/Height");
        }
        catch (NotSupportedException)
        {
            // 解码器不提供全局元数据时按帧范围推算。
            return (frameRight, frameBottom);
        }

        // 个别导出工具写的逻辑屏幕比帧还小，取两者较大值，避免帧被裁掉。
        return (Math.Max(screenWidth, frameRight), Math.Max(screenHeight, frameBottom));
    }

    private static int ReadUInt16(BitmapMetadata? metadata, string query)
    {
        return metadata?.GetQuery(query) is ushort value ? value : 0;
    }

    private static int ReadByte(BitmapMetadata? metadata, string query)
    {
        return metadata?.GetQuery(query) is byte value ? value : 0;
    }

    private enum GifFrameDisposal
    {
        Unspecified = 0,
        DoNotDispose = 1,
        RestoreBackground = 2,
        RestorePrevious = 3,
    }

    private readonly record struct GifFrameInfo(
        int Left,
        int Top,
        int Width,
        int Height,
        TimeSpan Delay,
        GifFrameDisposal Disposal);
}
