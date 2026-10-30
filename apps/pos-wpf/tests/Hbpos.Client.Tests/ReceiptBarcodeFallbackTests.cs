using Hbpos.Client.Wpf.Services;

namespace Hbpos.Client.Tests;

public sealed class ReceiptBarcodeFallbackTests
{
    [Fact]
    public void Native_success_stops_the_chain()
    {
        var calls = new List<string>();

        var outcome = ReceiptBarcodeFallback.Print(
            () => Record(calls, "native", true),
            () => Record(calls, "bitmap", true),
            () => Record(calls, "text", true),
            degraded => calls.Add($"degraded:{degraded}"));

        Assert.Equal(ReceiptBarcodeOutcome.Native, outcome);
        Assert.Equal(["native"], calls);
    }

    [Fact]
    public void Native_failure_tries_bitmap_then_prints_the_readable_number_after_it()
    {
        var calls = new List<string>();

        var outcome = ReceiptBarcodeFallback.Print(
            () => Record(calls, "native", false),
            () => Record(calls, "bitmap", true),
            () => Record(calls, "text", true),
            degraded => calls.Add($"degraded:{degraded}"));

        Assert.Equal(ReceiptBarcodeOutcome.Bitmap, outcome);
        // 位图没有 HRI，必须在位图之后补印一行可读单号。
        Assert.Equal(["native", "bitmap", "text", "degraded:Bitmap"], calls);
    }

    [Fact]
    public void Bitmap_unavailable_falls_back_to_text_only_and_the_receipt_continues()
    {
        var calls = new List<string>();

        var outcome = ReceiptBarcodeFallback.Print(
            () => Record(calls, "native", false),
            () => Record(calls, "bitmap", false),
            () => Record(calls, "text", true),
            degraded => calls.Add($"degraded:{degraded}"));

        Assert.Equal(ReceiptBarcodeOutcome.TextOnly, outcome);
        Assert.Equal(["native", "bitmap", "text", "degraded:TextOnly"], calls);
    }

    [Fact]
    public void Bitmap_exception_is_treated_as_unavailable()
    {
        var calls = new List<string>();

        var outcome = ReceiptBarcodeFallback.Print(
            () => Record(calls, "native", false),
            () =>
            {
                calls.Add("bitmap");
                throw new InvalidOperationException("sdk exploded");
            },
            () => Record(calls, "text", true));

        Assert.Equal(ReceiptBarcodeOutcome.TextOnly, outcome);
        Assert.Equal(["native", "bitmap", "text"], calls);
    }

    [Fact]
    public void Bitmap_cancellation_is_not_swallowed()
    {
        Assert.Throws<OperationCanceledException>(() => ReceiptBarcodeFallback.Print(
            () => false,
            () => throw new OperationCanceledException(),
            () => true));
    }

    [Fact]
    public void Text_failure_after_bitmap_is_a_failure_and_is_not_reported_as_degraded()
    {
        var degraded = new List<ReceiptBarcodeOutcome>();

        var outcome = ReceiptBarcodeFallback.Print(() => false, () => true, () => false, degraded.Add);

        Assert.Equal(ReceiptBarcodeOutcome.Failed, outcome);
        Assert.Empty(degraded);
    }

    [Fact]
    public void Text_failure_when_everything_else_failed_is_a_failure()
    {
        var degraded = new List<ReceiptBarcodeOutcome>();

        var outcome = ReceiptBarcodeFallback.Print(() => false, () => false, () => false, degraded.Add);

        Assert.Equal(ReceiptBarcodeOutcome.Failed, outcome);
        Assert.Empty(degraded);
    }

    private static bool Record(List<string> calls, string name, bool result)
    {
        calls.Add(name);
        return result;
    }
}
