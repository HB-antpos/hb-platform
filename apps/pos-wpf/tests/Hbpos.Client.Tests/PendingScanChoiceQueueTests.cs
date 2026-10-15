using Hbpos.Client.Wpf.Services;
using Hbpos.Contracts.Catalog;
using Microsoft.Extensions.Time.Testing;

namespace Hbpos.Client.Tests;

/// <summary>待选队列的纯逻辑：先进先出、互不覆盖、互不合并、上限、事件。</summary>
public sealed class PendingScanChoiceQueueTests
{
    [Fact]
    public void Entries_come_out_first_in_first_out_and_never_overwrite_each_other()
    {
        var queue = new PendingScanChoiceQueue();

        var first = queue.TryEnqueue("930001", "raw", "raw-1", "S001", [Item("A1"), Item("A2")])!;
        var second = queue.TryEnqueue("930002", "keyboard-fallback", "keyboard-fallback-2", "S001", [Item("B1"), Item("B2")])!;

        Assert.Equal(2, queue.Count);
        Assert.Equal(first.Id, queue.Current!.Id);
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal([first.Id, second.Id], queue.Snapshot().Select(entry => entry.Id));

        Assert.True(queue.Remove(first.Id));
        Assert.Equal(second.Id, queue.Current!.Id);
        Assert.Equal(1, queue.Count);
    }

    [Fact]
    public void Same_barcode_scanned_twice_is_two_entries_and_the_second_is_marked()
    {
        var queue = new PendingScanChoiceQueue();

        var first = queue.TryEnqueue("930001", "raw", "raw-1", "S001", [Item("A1"), Item("A2")])!;
        var second = queue.TryEnqueue(" 930001 ", "raw", "raw-2", "S001", [Item("A1"), Item("A2")])!;
        var third = queue.TryEnqueue("930002", "raw", "raw-3", "S001", [Item("B1"), Item("B2")])!;

        // 同一条码下的两件实物可能是不同商品，所以绝不合并；只是给第二个打上「与上一项同一条码」的标记。
        Assert.Equal(3, queue.Count);
        Assert.False(first.SameBarcodeAsPrevious);
        Assert.True(second.SameBarcodeAsPrevious);
        Assert.False(third.SameBarcodeAsPrevious);
    }

    [Fact]
    public void Queue_rejects_new_entries_when_full_instead_of_dropping_old_ones()
    {
        var queue = new PendingScanChoiceQueue();
        for (var index = 0; index < PendingScanChoiceQueue.Capacity; index++)
        {
            Assert.NotNull(queue.TryEnqueue($"93{index:0000}", "raw", $"raw-{index}", "S001", [Item("A1"), Item("A2")]));
        }

        var rejected = queue.TryEnqueue("939999", "raw", "raw-x", "S001", [Item("A1"), Item("A2")]);

        Assert.Null(rejected);
        Assert.Equal(PendingScanChoiceQueue.Capacity, queue.Count);
        Assert.Equal("930000", queue.Current!.Barcode);
    }

    [Fact]
    public void Remove_unknown_id_is_a_no_op_and_does_not_raise_changed()
    {
        var queue = new PendingScanChoiceQueue();
        var entry = queue.TryEnqueue("930001", "raw", "raw-1", "S001", [Item("A1")])!;
        var changes = 0;
        queue.Changed += (_, _) => changes++;

        Assert.False(queue.Remove(entry.Id + 100));
        Assert.Equal(0, changes);
        Assert.True(queue.Remove(entry.Id));
        Assert.False(queue.Remove(entry.Id));
        Assert.Equal(1, changes);
    }

    [Fact]
    public void Changed_fires_for_enqueue_remove_and_clear_but_not_for_an_empty_clear()
    {
        var queue = new PendingScanChoiceQueue();
        var changes = 0;
        queue.Changed += (_, _) => changes++;

        Assert.Empty(queue.Clear());
        Assert.Equal(0, changes);

        var first = queue.TryEnqueue("930001", "raw", "raw-1", "S001", [Item("A1")])!;
        queue.TryEnqueue("930002", "raw", "raw-2", "S001", [Item("B1")]);
        Assert.Equal(2, changes);

        queue.Remove(first.Id);
        Assert.Equal(3, changes);

        var cleared = queue.Clear();
        Assert.Single(cleared);
        Assert.Equal(4, changes);
        Assert.Equal(0, queue.Count);
        Assert.Null(queue.Current);
    }

    [Fact]
    public void Entry_records_scan_time_and_shows_only_the_barcode_tail()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 15, 14, 32, 8, TimeSpan.FromHours(11)));
        var queue = new PendingScanChoiceQueue(time);

        var entry = queue.TryEnqueue("6405090401470", "raw", "raw-9", "S001", [Item("A1"), Item("A2")])!;

        Assert.Equal(time.GetLocalNow(), entry.CreatedAt);
        Assert.Equal("1470", entry.BarcodeTail);
        Assert.Equal("raw-9", entry.TraceId);
        Assert.Equal("S001", entry.StoreCode);
    }

    [Fact]
    public void Snapshot_is_a_copy_so_later_changes_do_not_leak_into_it()
    {
        var queue = new PendingScanChoiceQueue();
        var first = queue.TryEnqueue("930001", "raw", "raw-1", "S001", [Item("A1")])!;
        var snapshot = queue.Snapshot();

        queue.Remove(first.Id);

        Assert.Single(snapshot);
        Assert.Equal(0, queue.Count);
    }

    private static SellableItemDto Item(string productCode)
    {
        return new SellableItemDto(
            StoreCode: "S001",
            ProductCode: productCode,
            ReferenceCode: null,
            DisplayName: productCode,
            LookupCode: "930000",
            ItemNumber: productCode,
            Barcode: "930000",
            RetailPrice: 1m,
            PriceSource: PriceSourceKind.StoreRetailPrice,
            PriceSourceLabel: "store",
            QuantityFactor: 1m,
            UpdatedAt: DateTimeOffset.UtcNow);
    }
}
