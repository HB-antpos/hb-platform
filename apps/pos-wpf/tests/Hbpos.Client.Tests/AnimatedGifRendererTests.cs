using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Hbpos.Client.Wpf.Services;
using Hbpos.Client.Wpf.Views.Screens;

namespace Hbpos.Client.Tests;

public sealed class AnimatedGifRendererTests
{
    private static readonly Color Transparent = Color.FromArgb(0, 0, 0, 0);

    [Fact]
    public void TryCreate_returns_null_for_non_gif_and_single_frame_gif()
    {
        Assert.Null(AnimatedGifRenderer.TryCreate([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]));
        Assert.Null(AnimatedGifRenderer.TryCreate(EncodeGif(CreateFrame(4, 4, (_, _) => Colors.Red))));
    }

    [Fact]
    public void RenderFrame_advances_through_frames_and_restarts_from_first_frame()
    {
        var data = EncodeGif(
            CreateFrame(4, 4, (_, _) => Colors.Red),
            CreateFrame(4, 4, (_, _) => Colors.Blue));

        var renderer = AnimatedGifRenderer.TryCreate(data);

        Assert.NotNull(renderer);
        Assert.Equal(2, renderer.FrameCount);
        Assert.Equal(4, renderer.Width);
        Assert.Equal(4, renderer.Height);

        renderer.RenderFrame(0);
        Assert.Equal(Colors.Red, ReadPixel(renderer.Bitmap, 1, 1));

        renderer.RenderFrame(1);
        Assert.Equal(Colors.Blue, ReadPixel(renderer.Bitmap, 1, 1));

        renderer.RenderFrame(0);
        Assert.Equal(Colors.Red, ReadPixel(renderer.Bitmap, 1, 1));
    }

    [Fact]
    public void RenderFrame_keeps_previous_pixels_under_transparent_pixels()
    {
        // 第二帧只画左半边，右半边透明：右半边应保留第一帧的画面（差分帧）。
        var data = EncodeGif(
            CreateFrame(4, 4, (_, _) => Colors.Red),
            CreateFrame(4, 4, (x, _) => x < 2 ? Colors.Blue : Transparent));

        var renderer = AnimatedGifRenderer.TryCreate(data);
        Assert.NotNull(renderer);

        renderer.RenderFrame(0);
        renderer.RenderFrame(1);

        Assert.Equal(Colors.Blue, ReadPixel(renderer.Bitmap, 0, 0));
        Assert.Equal(Colors.Red, ReadPixel(renderer.Bitmap, 3, 3));
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(1, 100)]
    [InlineData(2, 20)]
    [InlineData(5, 50)]
    [InlineData(150, 1500)]
    public void ResolveFrameDelay_treats_too_short_delays_as_100ms_like_browsers(int centiseconds, int expectedMilliseconds)
    {
        Assert.Equal(TimeSpan.FromMilliseconds(expectedMilliseconds), AnimatedGifRenderer.ResolveFrameDelay(centiseconds));
    }

    [Theory]
    // 一轮很短：保持图片默认的 8 秒
    [InlineData(1.2, 8)]
    // 一轮超过 8 秒：至少播完一轮
    [InlineData(12, 12)]
    // 超长动图：和视频一样最多 30 秒
    [InlineData(45, 30)]
    public void CustomerDisplayView_shows_animated_image_for_at_least_one_loop_up_to_30_seconds(
        double loopSeconds,
        double expectedSeconds)
    {
        Assert.Equal(
            TimeSpan.FromSeconds(expectedSeconds),
            CustomerDisplayView.ResolveImageDisplayDuration(TimeSpan.FromSeconds(loopSeconds)));
    }

    private static BitmapSource CreateFrame(int width, int height, Func<int, int, Color> colorAt)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var color = colorAt(x, y);
                var offset = ((y * width) + x) * 4;
                pixels[offset] = color.B;
                pixels[offset + 1] = color.G;
                pixels[offset + 2] = color.R;
                pixels[offset + 3] = color.A;
            }
        }

        return BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
    }

    private static byte[] EncodeGif(params BitmapSource[] frames)
    {
        var encoder = new GifBitmapEncoder();
        foreach (var frame in frames)
        {
            encoder.Frames.Add(BitmapFrame.Create(frame));
        }

        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static Color ReadPixel(BitmapSource bitmap, int x, int y)
    {
        var pixel = new byte[4];
        bitmap.CopyPixels(new System.Windows.Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return Color.FromArgb(pixel[3], pixel[2], pixel[1], pixel[0]);
    }
}
