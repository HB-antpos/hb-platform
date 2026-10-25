using System.Windows.Input;
using Hbpos.Client.Wpf;
using Hbpos.Client.Wpf.Services;

namespace Hbpos.Client.Tests;

public sealed class KeyboardScannerFallbackBufferTests
{
    [Fact]
    public void Process_CompletesFastScannerInputOnEnter()
    {
        var buffer = new KeyboardScannerFallbackBuffer();
        var now = DateTimeOffset.UtcNow;

        Assert.Null(buffer.Process(Key.D9, now));
        Assert.Null(buffer.Process(Key.D3, now.AddMilliseconds(10)));
        Assert.Null(buffer.Process(Key.D0, now.AddMilliseconds(20)));
        Assert.Null(buffer.Process(Key.D1, now.AddMilliseconds(30)));
        Assert.Null(buffer.Process(Key.D0, now.AddMilliseconds(40)));
        var barcode = buffer.Process(Key.Enter, now.AddMilliseconds(50));

        Assert.Equal("93010", barcode);
    }

    [Fact]
    public void Process_DropsInputShorterThanTheSharedMinimumLength()
    {
        // 与 Raw Input 路径共用 RawScannerInputProcessor.DefaultMinBarcodeLength（5）：4 位及以下视为误触。
        Assert.Equal(5, RawScannerInputProcessor.DefaultMinBarcodeLength);
        var buffer = new KeyboardScannerFallbackBuffer();
        var now = DateTimeOffset.UtcNow;

        Assert.Null(buffer.Process(Key.D9, now));
        Assert.Null(buffer.Process(Key.D3, now.AddMilliseconds(10)));
        Assert.Null(buffer.Process(Key.D0, now.AddMilliseconds(20)));
        Assert.Null(buffer.Process(Key.D1, now.AddMilliseconds(30)));
        var barcode = buffer.Process(Key.Enter, now.AddMilliseconds(40));

        Assert.Null(barcode);
    }

    [Fact]
    public void Process_DropsSlowInputBeforeEnter()
    {
        var buffer = new KeyboardScannerFallbackBuffer();
        var now = DateTimeOffset.UtcNow;

        Assert.Null(buffer.Process(Key.D9, now));
        Assert.Null(buffer.Process(Key.D3, now.AddMilliseconds(150)));
        var barcode = buffer.Process(Key.Enter, now.AddMilliseconds(160));

        Assert.Null(barcode);
    }

    [Fact]
    public void Process_UsesImeProcessedKeyForScannerCharacters()
    {
        var buffer = new KeyboardScannerFallbackBuffer();
        var now = DateTimeOffset.UtcNow;

        Assert.Null(buffer.Process(Key.ImeProcessed, now, Key.A));
        Assert.Null(buffer.Process(Key.ImeProcessed, now.AddMilliseconds(10), Key.B));
        Assert.Null(buffer.Process(Key.ImeProcessed, now.AddMilliseconds(20), Key.C));
        Assert.Null(buffer.Process(Key.ImeProcessed, now.AddMilliseconds(30), Key.D));
        Assert.Null(buffer.Process(Key.ImeProcessed, now.AddMilliseconds(40), Key.E));
        var barcode = buffer.Process(Key.Enter, now.AddMilliseconds(50));

        Assert.Equal("ABCDE", barcode);
    }

    [Fact]
    public void DuplicateGuard_suppresses_same_barcode_from_different_input_sources()
    {
        var guard = new ScannerInputDuplicateGuard();
        var now = DateTimeOffset.UtcNow;

        Assert.True(guard.TryAccept("ABC123", "raw", now));
        Assert.False(guard.TryAccept("ABC123", "keyboard-fallback", now.AddMilliseconds(10)));
        Assert.False(guard.TryAccept("ABC123", "keyboard-fallback", now.AddMilliseconds(180)));
    }

    [Theory]
    [InlineData(240)]
    [InlineData(277)]
    [InlineData(534)]
    [InlineData(1400)]
    public void DuplicateGuard_pairs_late_keyboard_copy_with_the_raw_delivery_once(int keyboardLagMs)
    {
        // 回归：POS_1004_0613 的键盘拷贝晚到 240～277ms，固定 200ms 窗口漏掉，同一次扫码加购两次。
        var guard = new ScannerInputDuplicateGuard();
        var now = DateTimeOffset.UtcNow;

        Assert.True(guard.TryAccept("9351972009975", "raw", now));
        Assert.False(guard.TryAccept("9351972009975", "keyboard-fallback", now.AddMilliseconds(keyboardLagMs)));
        // 额度只抵消一次：同条码再来一份键盘提交（没有对应的 Raw 投递）是真实扫码，必须放行。
        Assert.True(guard.TryAccept("9351972009975", "keyboard-fallback", now.AddMilliseconds(keyboardLagMs + 10)));
    }

    [Fact]
    public void DuplicateGuard_pairs_each_rapid_raw_scan_with_its_own_late_keyboard_copy()
    {
        var guard = new ScannerInputDuplicateGuard();
        var now = DateTimeOffset.UtcNow;

        // 连续快扫同一商品（间隔超过 600ms 的真实重扫），键盘拷贝都落后 ~300ms：第二次 Raw 先于第一份拷贝到达。
        Assert.True(guard.TryAccept("ABC123", "raw", now));
        Assert.True(guard.TryAccept("ABC123", "raw", now.AddMilliseconds(700)));
        Assert.False(guard.TryAccept("ABC123", "keyboard-fallback", now.AddMilliseconds(750)));
        Assert.False(guard.TryAccept("ABC123", "keyboard-fallback", now.AddMilliseconds(1000)));
        Assert.True(guard.TryAccept("ABC123", "keyboard-fallback", now.AddMilliseconds(1300)));
    }

    [Fact]
    public void DuplicateGuard_keeps_unrelated_and_expired_deliveries()
    {
        var guard = new ScannerInputDuplicateGuard();
        var now = DateTimeOffset.UtcNow;

        Assert.True(guard.TryAccept("ABC123", "raw", now));
        // 不同条码不受配对额度影响。
        Assert.True(guard.TryAccept("XYZ789", "keyboard-fallback", now.AddMilliseconds(300)));
        // 额度超过 1.5 秒没等到拷贝就作废，之后的键盘提交按真实扫码处理。
        Assert.True(guard.TryAccept("ABC123", "keyboard-fallback", now.AddMilliseconds(2000)));
    }

    [Fact]
    public void DuplicateGuard_suppressed_copy_does_not_turn_the_next_raw_scan_into_a_duplicate()
    {
        var guard = new ScannerInputDuplicateGuard();
        var now = DateTimeOffset.UtcNow;

        Assert.True(guard.TryAccept("ABC123", "raw", now));
        Assert.False(guard.TryAccept("ABC123", "keyboard-fallback", now.AddMilliseconds(100)));
        // 拷贝被抵消后，超过 Raw 重复窗口（默认 600ms）的下一次真实 Raw 扫码仍要接受。
        Assert.True(guard.TryAccept("ABC123", "raw", now.AddMilliseconds(700)));
    }

    [Theory]
    [InlineData(10)]
    [InlineData(250)]
    [InlineData(600)]
    public void DuplicateGuard_drops_same_barcode_raw_repeat_within_window(int repeatLagMs)
    {
        // 扫码枪对着条码多停一会儿会连读，生产日志里集中在 100～500ms：只放行第一次。
        var guard = new ScannerInputDuplicateGuard();
        var now = DateTimeOffset.UtcNow;

        Assert.True(guard.TryAccept("ABC123", "raw", now));
        Assert.False(guard.TryAccept("ABC123", "raw", now.AddMilliseconds(repeatLagMs)));
    }

    [Fact]
    public void DuplicateGuard_keeps_real_rescan_after_window_and_different_barcodes()
    {
        var guard = new ScannerInputDuplicateGuard();
        var now = DateTimeOffset.UtcNow;

        Assert.True(guard.TryAccept("ABC123", "raw", now));
        // 不同条码紧接着扫不受影响。
        Assert.True(guard.TryAccept("XYZ789", "raw", now.AddMilliseconds(100)));
        // 同条码超过 600ms 是真实重扫（人工最快也要 500ms 以上）。
        Assert.True(guard.TryAccept("ABC123", "raw", now.AddMilliseconds(700)));
    }

    [Fact]
    public void DuplicateGuard_collapses_a_held_over_burst_into_one_scan()
    {
        // 持续对着条码连读：每次间隔 250ms 都在窗口内，整串只放行第一次；窗口随最近一次读码顺延。
        var guard = new ScannerInputDuplicateGuard();
        var now = DateTimeOffset.UtcNow;

        Assert.True(guard.TryAccept("ABC123", "raw", now));
        Assert.False(guard.TryAccept("ABC123", "raw", now.AddMilliseconds(250)));
        Assert.False(guard.TryAccept("ABC123", "raw", now.AddMilliseconds(500)));
        Assert.False(guard.TryAccept("ABC123", "raw", now.AddMilliseconds(750)));
        Assert.True(guard.TryAccept("ABC123", "raw", now.AddMilliseconds(1400)));
    }

    [Fact]
    public void DuplicateGuard_follows_a_changing_window_provider()
    {
        // 设置页改了过滤窗口，下一次读码立即按新窗口判断。
        var windowMs = 200;
        var guard = new ScannerInputDuplicateGuard(() => TimeSpan.FromMilliseconds(windowMs));
        var now = DateTimeOffset.UtcNow;

        Assert.True(guard.TryAccept("ABC123", "raw", now));
        Assert.True(guard.TryAccept("ABC123", "raw", now.AddMilliseconds(350)));

        windowMs = 1000;
        Assert.False(guard.TryAccept("ABC123", "raw", now.AddMilliseconds(700)));
    }

    [Fact]
    public void DuplicateGuard_absorbs_keyboard_copy_of_a_dropped_raw_repeat()
    {
        // 连读的两次读码在键盘通道各有一份拷贝：两份都要被抵消，否则第二份会被当成真实键盘扫码加购第二次。
        var guard = new ScannerInputDuplicateGuard();
        var now = DateTimeOffset.UtcNow;

        Assert.True(guard.TryAccept("ABC123", "raw", now));
        Assert.False(guard.TryAccept("ABC123", "raw", now.AddMilliseconds(250)));
        Assert.False(guard.TryAccept("ABC123", "keyboard-fallback", now.AddMilliseconds(300)));
        Assert.False(guard.TryAccept("ABC123", "keyboard-fallback", now.AddMilliseconds(550)));
        // 额度用完后的键盘提交才是新的真实扫码。
        Assert.True(guard.TryAccept("ABC123", "keyboard-fallback", now.AddMilliseconds(1000)));
    }

    [Fact]
    public void DuplicateGuard_allows_repeated_keyboard_only_scans()
    {
        var now = DateTimeOffset.UtcNow;

        // 没有 Raw 通道的机器（如 POS_1013_0239）：键盘兜底连续扫同一商品必须逐次放行。
        var keyboardOnly = new ScannerInputDuplicateGuard();
        Assert.True(keyboardOnly.TryAccept("ABC123", "keyboard-fallback", now));
        Assert.True(keyboardOnly.TryAccept("ABC123", "keyboard-fallback", now.AddMilliseconds(300)));
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, false)]
    public void ShouldBlockKeyboardScannerFallback_blocks_only_visible_text_input_focus(
        bool isTextInputFocused,
        bool isFocusedElementVisible,
        bool expected)
    {
        Assert.Equal(
            expected,
            MainWindow.ShouldBlockKeyboardScannerFallback(isTextInputFocused, isFocusedElementVisible));
    }

    [Theory]
    [InlineData(true, false, false, false, true)]
    [InlineData(false, true, false, false, true)]
    [InlineData(false, false, false, false, false)]
    public void ShouldBlockKeyboardScannerFallback_blocks_when_global_overlay_is_active(
        bool isForceUpdateBlocking,
        bool isConfirmationDialogOpen,
        bool isTextInputFocused,
        bool isFocusedElementVisible,
        bool expected)
    {
        Assert.Equal(
            expected,
            MainWindow.ShouldBlockKeyboardScannerFallback(
                isForceUpdateBlocking,
                isConfirmationDialogOpen,
                isTextInputFocused,
                isFocusedElementVisible));
    }

    [Theory]
    [InlineData(true, false, 0x00FF, true)]
    [InlineData(false, true, 0x00FF, true)]
    [InlineData(true, true, 0x0100, false)]
    [InlineData(false, false, 0x00FF, false)]
    public void ShouldBlockRawScannerWindowMessage_blocks_when_global_overlay_is_active(
        bool isForceUpdateBlocking,
        bool isConfirmationDialogOpen,
        int messageId,
        bool expected)
    {
        Assert.Equal(
            expected,
            MainWindow.ShouldBlockRawScannerWindowMessage(isForceUpdateBlocking, isConfirmationDialogOpen, messageId));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void ProcessRawScannerWindowMessage_marks_handled_and_skips_dispatch_when_global_overlay_is_active(
        bool isForceUpdateBlocking,
        bool isConfirmationDialogOpen)
    {
        var handled = false;
        var dispatchCalled = false;

        var result = MainWindow.ProcessRawScannerWindowMessage(
            isForceUpdateBlocking,
            isConfirmationDialogOpen,
            messageId: 0x00FF,
            hwnd: IntPtr.Zero,
            wParam: IntPtr.Zero,
            lParam: IntPtr.Zero,
            processWindowMessage: (IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool nextHandled) =>
            {
                dispatchCalled = true;
                return new IntPtr(42);
            },
            handled: ref handled);

        Assert.Equal(IntPtr.Zero, result);
        Assert.True(handled);
        Assert.False(dispatchCalled);
    }

    [Fact]
    public void ProcessRawScannerWindowMessage_dispatches_non_blocked_messages_to_raw_scanner()
    {
        var handled = false;
        var dispatchCalled = false;

        var result = MainWindow.ProcessRawScannerWindowMessage(
            isForceUpdateBlocking: false,
            isConfirmationDialogOpen: false,
            messageId: 0x00FF,
            hwnd: IntPtr.Zero,
            wParam: IntPtr.Zero,
            lParam: IntPtr.Zero,
            processWindowMessage: (IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool nextHandled) =>
            {
                dispatchCalled = true;
                nextHandled = true;
                return new IntPtr(42);
            },
            handled: ref handled);

        Assert.Equal(new IntPtr(42), result);
        Assert.True(handled);
        Assert.True(dispatchCalled);
    }

    [Fact]
    public void Confirmation_dialog_opening_clears_keyboard_and_raw_scanner_pending_input()
    {
        var sourcePath = Path.Combine(
            FindRepoRoot(),
            "apps",
            "pos-wpf",
            "src",
            "Hbpos.Client.Wpf",
            "MainWindow.xaml.cs");
        var source = File.ReadAllText(sourcePath);
        var handlerStart = source.IndexOf(
            "private void ConfirmationDialogIsVisibleChanged",
            StringComparison.Ordinal);
        var handlerEnd = source.IndexOf(
            "private static bool IsTextInputElement",
            handlerStart,
            StringComparison.Ordinal);

        Assert.True(handlerStart >= 0);
        Assert.True(handlerEnd > handlerStart);
        var handlerSource = source[handlerStart..handlerEnd];
        Assert.Contains("_keyboardScannerFallback.Clear();", handlerSource, StringComparison.Ordinal);
        Assert.Contains("_rawScannerService.ClearPendingInput();", handlerSource, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, true, Key.Escape, true)]
    [InlineData(true, true, Key.Tab, true)]
    [InlineData(true, true, Key.Enter, true)]
    [InlineData(false, true, Key.Escape, false)]
    [InlineData(false, true, Key.Tab, false)]
    [InlineData(false, true, Key.Enter, false)]
    [InlineData(false, true, Key.A, true)]
    public void ShouldConsumeBlockedKeyboardInput_preserves_confirmation_keys_only(
        bool isForceUpdateBlocking,
        bool isConfirmationDialogOpen,
        Key key,
        bool expected)
    {
        Assert.Equal(
            expected,
            MainWindow.ShouldConsumeBlockedKeyboardInput(
                isForceUpdateBlocking,
                isConfirmationDialogOpen,
                key));
    }

    [Theory]
    [InlineData("", "1", "1")]
    [InlineData("12", "3", "123")]
    [InlineData("123", "Back", "12")]
    [InlineData("", "Back", "")]
    [InlineData("123", "Clear", "")]
    [InlineData("123", "A", "123")]
    [InlineData("123", null, "123")]
    public void ApplyCashierBarcodeKeyboardInput_updates_cashier_barcode_buffer(
        string current,
        string? key,
        string expected)
    {
        Assert.Equal(expected, MainWindow.ApplyCashierBarcodeKeyboardInput(current, key));
    }

    [Fact]
    public void VoucherEntryTextBox_disables_ime_for_scanner_input()
    {
        var xamlPath = Path.Combine(
            FindRepoRoot(),
            "apps",
            "pos-wpf",
            "src",
            "Hbpos.Client.Wpf",
            "Views",
            "Screens",
            "PaymentView.xaml");
        var xaml = File.ReadAllText(xamlPath);
        var textBoxStart = xaml.IndexOf("<TextBox x:Name=\"VoucherEntryTextBox\"", StringComparison.Ordinal);

        Assert.True(textBoxStart >= 0);
        var textBoxEnd = xaml.IndexOf("/>", textBoxStart, StringComparison.Ordinal);
        Assert.True(textBoxEnd > textBoxStart);
        var textBoxMarkup = xaml[textBoxStart..textBoxEnd];
        Assert.Contains("InputMethod.IsInputMethodEnabled=\"False\"", textBoxMarkup, StringComparison.Ordinal);
        Assert.Contains("InputMethod.PreferredImeState=\"Off\"", textBoxMarkup, StringComparison.Ordinal);
    }

    [Fact]
    public void OperationAuthorization_read_only_run_bindings_are_one_way()
    {
        var xamlPath = Path.Combine(
            FindRepoRoot(),
            "apps",
            "pos-wpf",
            "src",
            "Hbpos.Client.Wpf",
            "MainWindow.xaml");
        var xaml = File.ReadAllText(xamlPath);

        Assert.Contains(
            "<Run Text=\"{Binding OperationAuthorization.Screen, Mode=OneWay}\" />",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "<Run Text=\"{Binding OperationAuthorization.PermissionCode, Mode=OneWay}\" />",
            xaml,
            StringComparison.Ordinal);
    }

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, ".git")) ||
                File.Exists(Path.Combine(current.FullName, ".git")) ||
                File.Exists(Path.Combine(current.FullName, "hb-platform.sln")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Unable to find repository root.");
    }
}
