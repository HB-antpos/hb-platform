using Hbpos.Client.Wpf.Services;

namespace Hbpos.Client.Tests;

public sealed class RawScannerInputProcessorTests
{
    [Theory]
    // 默认最小长度 5：短于 5 位（含 3~4 位内部短码）一律视为误触丢弃，恰好 5 位及以上正常识别。
    [InlineData("L303", false)]
    [InlineData("1234", false)]
    [InlineData("12345", true)]
    [InlineData("5550001234567", true)]
    public void Default_processor_requires_at_least_five_characters_for_enter_and_timeout_completion(string barcode, bool accepted)
    {
        Assert.Equal(5, RawScannerInputProcessor.DefaultMinBarcodeLength);

        foreach (var completeByTimeout in new[] { false, true })
        {
            var processor = new RawScannerInputProcessor();
            var now = DateTimeOffset.UtcNow;
            foreach (var character in barcode)
            {
                Assert.Null(processor.ProcessCharacter("scanner-device", character, now, boundDevicePath: null));
                now = now.AddMilliseconds(10);
            }

            RawScannerInputResult? result;
            if (completeByTimeout)
            {
                result = processor
                    .FlushExpired(now + RawScannerInputProcessor.DefaultScanTimeout, boundDevicePath: null)
                    .SingleOrDefault();
            }
            else
            {
                result = processor.ProcessEnter("scanner-device", now, boundDevicePath: null);
            }

            Assert.Equal(accepted, result is not null);
            if (accepted)
            {
                Assert.Equal(barcode, result!.Barcode);
            }
        }
    }

    [Fact]
    public void Unbound_scanner_learns_from_enter_completed_scan()
    {
        var processor = new RawScannerInputProcessor(TimeSpan.FromMilliseconds(120), minBarcodeLength: 3);
        var now = DateTimeOffset.UtcNow;

        foreach (var character in "930101")
        {
            Assert.Null(processor.ProcessCharacter("scanner-device", character, now, boundDevicePath: null));
            now = now.AddMilliseconds(10);
        }

        var result = processor.ProcessEnter("scanner-device", now, boundDevicePath: null);

        Assert.NotNull(result);
        Assert.Equal("930101", result.Barcode);
        Assert.Equal("scanner-device", result.DevicePath);
        Assert.Equal(RawScannerCompletionKind.Enter, result.CompletionKind);
    }

    [Fact]
    public void Bound_scanner_ignores_other_keyboard_devices_even_when_input_is_fast()
    {
        var processor = new RawScannerInputProcessor(TimeSpan.FromMilliseconds(120), minBarcodeLength: 3);
        var now = DateTimeOffset.UtcNow;

        foreach (var character in "930101")
        {
            Assert.Null(processor.ProcessCharacter("ordinary-keyboard", character, now, boundDevicePath: "scanner-device"));
            now = now.AddMilliseconds(5);
        }

        Assert.Null(processor.ProcessEnter("ordinary-keyboard", now, boundDevicePath: "scanner-device"));
        Assert.Empty(processor.FlushExpired(now.AddMilliseconds(200), boundDevicePath: "scanner-device"));
    }

    [Fact]
    public void Scanner_without_enter_suffix_completes_after_silent_timeout()
    {
        var processor = new RawScannerInputProcessor(TimeSpan.FromMilliseconds(120), minBarcodeLength: 3);
        var now = DateTimeOffset.UtcNow;

        foreach (var character in "ABC123")
        {
            Assert.Null(processor.ProcessCharacter("scanner-device", character, now, boundDevicePath: "scanner-device"));
            now = now.AddMilliseconds(8);
        }

        Assert.Empty(processor.FlushExpired(now.AddMilliseconds(80), boundDevicePath: "scanner-device"));
        var result = Assert.Single(processor.FlushExpired(now.AddMilliseconds(130), boundDevicePath: "scanner-device"));

        Assert.Equal("ABC123", result.Barcode);
        Assert.Equal("scanner-device", result.DevicePath);
        Assert.Equal(RawScannerCompletionKind.Timeout, result.CompletionKind);
    }

    [Fact]
    public void Slow_input_is_not_treated_as_one_scan()
    {
        var processor = new RawScannerInputProcessor(TimeSpan.FromMilliseconds(120), minBarcodeLength: 3);
        var now = DateTimeOffset.UtcNow;

        Assert.Null(processor.ProcessCharacter("scanner-device", '9', now, boundDevicePath: "scanner-device"));
        Assert.Null(processor.ProcessCharacter("scanner-device", '3', now.AddMilliseconds(150), boundDevicePath: "scanner-device"));
        var result = processor.ProcessEnter("scanner-device", now.AddMilliseconds(160), boundDevicePath: "scanner-device");

        Assert.Null(result);
    }

    [Fact]
    public void Short_scan_is_discarded()
    {
        var processor = new RawScannerInputProcessor(TimeSpan.FromMilliseconds(120), minBarcodeLength: 3);
        var now = DateTimeOffset.UtcNow;

        Assert.Null(processor.ProcessCharacter("scanner-device", 'A', now, boundDevicePath: "scanner-device"));
        Assert.Null(processor.ProcessCharacter("scanner-device", 'B', now.AddMilliseconds(10), boundDevicePath: "scanner-device"));

        Assert.Null(processor.ProcessEnter("scanner-device", now.AddMilliseconds(20), boundDevicePath: "scanner-device"));
    }
}
