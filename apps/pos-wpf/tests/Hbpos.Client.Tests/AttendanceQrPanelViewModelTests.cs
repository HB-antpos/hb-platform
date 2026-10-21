using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Net;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BlazorApp.Shared.Security;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Models;
using Hbpos.Client.Wpf.Services;
using Hbpos.Client.Wpf.ViewModels;
using Hbpos.Contracts.Attendance;
using Microsoft.Data.Sqlite;
using ZXing;
using ZXing.Common;

namespace Hbpos.Client.Tests;

[Collection(ConsoleLogGlobalStateTestCollection.Name)]
public sealed class AttendanceQrPanelViewModelTests
{
    [Fact]
    public async Task First_online_start_registers_aes256_key_and_issues_strict_15_second_token()
    {
        var fixture = new Fixture(online: true);
        using var viewModel = fixture.CreateViewModel();

        await viewModel.RefreshAsync();

        var typicalRendered = AttendanceQrPngRenderer.Render(viewModel.QrToken!);
        Assert.StartsWith("HBATE1.", viewModel.QrToken, StringComparison.Ordinal);
        Assert.True(typicalRendered.PixelSize <= AttendanceQrPngRenderer.ContainerPixels);

        var request = Assert.Single(fixture.Api.Registrations);
        Assert.Equal("A256GCM", request.Algorithm);
        Assert.Equal(43, request.KeyMaterial.Length);
        var key = DecodeBase64Url(request.KeyMaterial);
        Assert.Equal(32, key.Length);
        Assert.Contains(fixture.Settings.Values.Keys, key => key == "attendance.qr.identity.v2");
        Assert.DoesNotContain(fixture.Settings.Values.Keys, key => key == "attendance.qr.identity.v1");
        Assert.All(fixture.Settings.Values.Values, value => Assert.StartsWith("protected:", value));
        Assert.NotNull(viewModel.QrImage);
        Assert.True(AttendanceQrTokenCodec.TryDecrypt(
            viewModel.QrToken,
            new Dictionary<string, byte[]> { [request.Kid] = key },
            fixture.ServerUtc.UtcDateTime,
            out var payload,
            out _,
            out _));
        Assert.Equal(fixture.ServerUtc.UtcDateTime, payload!.IssuedAtUtc);
        Assert.NotEqual(fixture.Time.GetUtcNow().UtcDateTime, payload.IssuedAtUtc);
        Assert.Equal(TimeSpan.FromSeconds(15), payload!.ExpiresAtUtc - payload.IssuedAtUtc);
    }

    [Fact]
    public async Task Qr_rotates_at_ten_seconds_and_again_at_the_next_expiry_boundary()
    {
        var fixture = new Fixture(online: true);
        using var viewModel = fixture.CreateViewModel();
        await viewModel.RefreshAsync();
        var firstToken = viewModel.QrToken;

        fixture.Time.Advance(TimeSpan.FromSeconds(9));
        await viewModel.TickAsync();
        Assert.Equal(firstToken, viewModel.QrToken);
        Assert.Equal(6, viewModel.SecondsRemaining);

        fixture.Time.Advance(TimeSpan.FromSeconds(1));
        await viewModel.TickAsync();
        var secondToken = viewModel.QrToken;
        Assert.NotEqual(firstToken, secondToken);
        Assert.Equal(15, viewModel.SecondsRemaining);

        fixture.Time.Advance(TimeSpan.FromSeconds(15));
        await viewModel.TickAsync();
        Assert.NotEqual(secondToken, viewModel.QrToken);
        Assert.Equal(15, viewModel.SecondsRemaining);
    }

    [Fact]
    public async Task The_default_refresh_slows_to_five_minutes_when_healthy_and_retries_quickly_otherwise()
    {
        var fixture = new Fixture(online: true);
        using var viewModel = fixture.CreateViewModel();

        // 还没成功启用过（没有身份 / 可信时间）：按异常处理，15 秒重试。
        Assert.Equal(TimeSpan.FromSeconds(15), await viewModel.GetNextRefreshIntervalAsync());

        await viewModel.RefreshAsync();
        Assert.Equal(TimeSpan.FromMinutes(5), await viewModel.GetNextRefreshIntervalAsync());

        // 离线：快速重试，网络恢复后尽快回到在线校验状态。
        fixture.Connectivity.IsOnline = false;
        await viewModel.RefreshAsync();
        Assert.Equal(TimeSpan.FromSeconds(15), await viewModel.GetNextRefreshIntervalAsync());

        fixture.Connectivity.IsOnline = true;
        await viewModel.RefreshAsync();
        Assert.Equal(TimeSpan.FromMinutes(5), await viewModel.GetNextRefreshIntervalAsync());
    }

    [Fact]
    public async Task A_failed_setup_is_retried_every_fifteen_seconds_until_it_recovers()
    {
        var fixture = new Fixture(online: true);
        using var viewModel = fixture.CreateViewModel();
        fixture.Api.RegisterException = new HttpRequestException("registration failed");

        await viewModel.RefreshAsync();
        Assert.Equal(TimeSpan.FromSeconds(15), await viewModel.GetNextRefreshIntervalAsync());

        fixture.Api.RegisterException = null;
        await viewModel.RefreshAsync();
        Assert.Equal(TimeSpan.FromMinutes(5), await viewModel.GetNextRefreshIntervalAsync());
    }

    [Fact]
    public async Task An_explicit_refresh_interval_stays_fixed()
    {
        var fixture = new Fixture(online: true);
        using var viewModel = fixture.CreateViewModel(refreshInterval: TimeSpan.FromSeconds(30));

        await viewModel.RefreshAsync();

        Assert.Equal(TimeSpan.FromSeconds(30), await viewModel.GetNextRefreshIntervalAsync());
    }

    [Fact]
    public async Task Repeated_ticks_during_early_rotation_do_not_reissue_qr()
    {
        var fixture = new Fixture(online: true);
        using var viewModel = fixture.CreateViewModel();
        await viewModel.RefreshAsync();
        var tokenChanges = new List<string?>();
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(AttendanceQrPanelViewModel.QrToken))
            {
                tokenChanges.Add(viewModel.QrToken);
            }
        };

        fixture.Time.Advance(TimeSpan.FromSeconds(10));
        await Task.WhenAll(viewModel.TickAsync(), viewModel.TickAsync());

        Assert.Equal(1, tokenChanges.Count(token => token is null));
        Assert.Equal(1, tokenChanges.Count(token => token is not null));
        Assert.Equal(15, viewModel.SecondsRemaining);
    }

    [Fact]
    public async Task Online_to_offline_keeps_signing_and_rotates_after_expiry_with_clear_first()
    {
        var fixture = new Fixture(online: true);
        using var viewModel = fixture.CreateViewModel();
        await viewModel.RefreshAsync();
        var firstToken = viewModel.QrToken;
        var changes = new List<string?>();
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(AttendanceQrPanelViewModel.QrToken))
            {
                changes.Add(viewModel.QrToken);
            }
        };

        fixture.Connectivity.IsOnline = false;
        // 离线模式也在剩余 5 秒时换码；第 9 秒仍保留旧码，第 10 秒刷新。
        fixture.Time.Advance(TimeSpan.FromSeconds(9));
        await viewModel.RefreshAsync();
        Assert.Equal(firstToken, viewModel.QrToken);
        Assert.Equal(6, viewModel.SecondsRemaining);

        fixture.Time.Advance(TimeSpan.FromSeconds(1));
        await viewModel.RefreshAsync();

        Assert.NotEqual(firstToken, viewModel.QrToken);
        Assert.Contains(null, changes);
        Assert.Equal("Offline signed", viewModel.VerificationStatusText);
        Assert.Equal(15, viewModel.SecondsRemaining);
    }

    [Fact]
    public async Task First_offline_start_without_trusted_time_never_displays_a_qr()
    {
        var fixture = new Fixture(online: false);
        using var viewModel = fixture.CreateViewModel();

        await viewModel.RefreshAsync();

        Assert.Empty(fixture.Api.Registrations);
        Assert.Null(viewModel.QrToken);
        Assert.Null(viewModel.QrImage);
        Assert.Equal("Connect online once to enable offline attendance QR.", viewModel.MessageText);
    }

    [Fact]
    public async Task Online_setup_failure_stays_visible_during_ticks_and_recovers_on_next_refresh()
    {
        var fixture = new Fixture(online: true);
        fixture.Api.RegisterException = new HttpRequestException(
            "kid=sensitive KeyMaterial=sensitive ProtectedKey=sensitive",
            null,
            HttpStatusCode.InternalServerError);
        using var viewModel = fixture.CreateViewModel();

        await viewModel.RefreshAsync();

        Assert.Null(viewModel.QrToken);
        Assert.Null(viewModel.QrImage);
        Assert.Equal(0, viewModel.SecondsRemaining);
        Assert.Equal("Attendance QR setup failed. Retrying automatically.", viewModel.MessageText);

        await viewModel.TickAsync();

        Assert.Null(viewModel.QrToken);
        Assert.Equal(0, viewModel.SecondsRemaining);
        Assert.Equal("Attendance QR setup failed. Retrying automatically.", viewModel.MessageText);

        fixture.Api.RegisterException = null;
        await viewModel.RefreshAsync();

        Assert.NotNull(viewModel.QrToken);
        Assert.NotNull(viewModel.QrImage);
        Assert.Equal(15, viewModel.SecondsRemaining);
        Assert.Equal("Scan to clock in / out", viewModel.MessageText);
        Assert.Contains("attendance.qr.identity.v2", fixture.Settings.Values.Keys);
        Assert.Contains("attendance.qr.trusted-time.v1", fixture.Settings.Values.Keys);
    }

    [Fact]
    public async Task Dpapi_save_failure_clears_qr_and_recovers_on_next_refresh()
    {
        var fixture = new Fixture(online: true);
        fixture.Protector.ProtectException = new Win32Exception(5, "KeyMaterial=sensitive");
        using var viewModel = fixture.CreateViewModel();

        await viewModel.RefreshAsync();

        Assert.Null(viewModel.QrToken);
        Assert.Null(viewModel.QrImage);
        Assert.Equal(0, viewModel.SecondsRemaining);
        Assert.Equal("Attendance QR setup failed. Retrying automatically.", viewModel.MessageText);

        fixture.Protector.ProtectException = null;
        await viewModel.RefreshAsync();

        Assert.NotNull(viewModel.QrToken);
        Assert.Equal("Scan to clock in / out", viewModel.MessageText);
    }

    [Fact]
    public async Task Settings_read_failure_clears_existing_qr_and_recovers_on_next_refresh()
    {
        var fixture = new Fixture(online: true);
        using var viewModel = fixture.CreateViewModel();
        await viewModel.RefreshAsync();
        Assert.NotNull(viewModel.QrToken);
        fixture.Settings.GetException = new SqliteException("ProtectedKey=sensitive", 5);

        await viewModel.RefreshAsync();

        Assert.Null(viewModel.QrToken);
        Assert.Null(viewModel.QrImage);
        Assert.Equal(0, viewModel.SecondsRemaining);
        Assert.Equal("Attendance QR setup failed. Retrying automatically.", viewModel.MessageText);

        fixture.Settings.GetException = null;
        await viewModel.RefreshAsync();

        Assert.NotNull(viewModel.QrToken);
        Assert.Equal("Scan to clock in / out", viewModel.MessageText);
    }

    [Fact]
    public async Task Background_refresh_fallback_clears_qr_and_recovers_automatically()
    {
        var fixture = new Fixture(online: true);
        fixture.Settings.GetException = new NotSupportedException("request body=sensitive");
        using var viewModel = fixture.CreateViewModel(
            startTimer: true,
            tickInterval: TimeSpan.FromMilliseconds(10),
            refreshInterval: TimeSpan.FromMilliseconds(20));

        await WaitUntilAsync(
            () => viewModel.MessageText == "Attendance QR setup failed. Retrying automatically.",
            TimeSpan.FromSeconds(2));
        Assert.Null(viewModel.QrToken);
        Assert.Equal(0, viewModel.SecondsRemaining);

        fixture.Settings.GetException = null;
        await WaitUntilAsync(
            () => viewModel.QrToken is not null && viewModel.MessageText == "Scan to clock in / out",
            TimeSpan.FromSeconds(2));

        Assert.Equal("Scan to clock in / out", viewModel.MessageText);
    }

    [Fact]
    public async Task Failed_background_tick_cannot_clear_qr_committed_by_interleaved_refresh()
    {
        var fixture = new Fixture(online: true);
        using var viewModel = fixture.CreateViewModel(
            startTimer: true,
            tickInterval: TimeSpan.FromSeconds(1),
            refreshInterval: TimeSpan.FromHours(1));
        await WaitUntilAsync(
            () => viewModel.QrToken is not null && viewModel.MessageText == "Scan to clock in / out",
            TimeSpan.FromSeconds(2));
        fixture.Localization.ArmTickFailure();
        var qrCleared = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(AttendanceQrPanelViewModel.QrToken) && viewModel.QrToken is null)
            {
                qrCleared.TrySetResult();
            }
        };

        await fixture.Localization.TickCatchEntered.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
        var refresh = viewModel.RefreshAsync();
        await Task.WhenAny(refresh, Task.Delay(200));
        fixture.Localization.ReleaseTickCatch();

        await qrCleared.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
        await refresh.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);

        Assert.NotNull(viewModel.QrToken);
        Assert.Equal("Scan to clock in / out", viewModel.MessageText);
    }

    [Fact]
    public async Task Setup_failure_console_log_omits_exception_message_and_secrets()
    {
        const string secret = "secret-marker request-body KeyMaterial ProtectedKey";
        var fixture = new Fixture(online: true);
        fixture.Api.RegisterException = new HttpRequestException(secret, null, HttpStatusCode.InternalServerError);
        using var viewModel = fixture.CreateViewModel();
        var lines = new List<string>();
        void Capture(string line) => lines.Add(line);

        ConsoleLog.LineWritten += Capture;
        try
        {
            await viewModel.RefreshAsync();
        }
        finally
        {
            ConsoleLog.LineWritten -= Capture;
        }

        var setupLog = Assert.Single(lines.Where(line => line.Contains("phase=setup", StringComparison.Ordinal)));
        Assert.Contains("error=HttpRequestException status=500", setupLog, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-marker", setupLog, StringComparison.Ordinal);
        Assert.DoesNotContain("request-body", setupLog, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("KeyMaterial", setupLog, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ProtectedKey", setupLog, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Culture_refresh_observes_property_and_logging_exceptions_without_leaking_secrets()
    {
        const string secret = "culture-secret-marker request-body KeyMaterial ProtectedKey";
        var fixture = new Fixture(online: true);
        using var viewModel = fixture.CreateViewModel();
        await viewModel.RefreshAsync();
        var context = new CapturingSynchronizationContext();
        var previousContext = SynchronizationContext.Current;
        var lines = new List<string>();
        void Capture(string line) => lines.Add(line);
        void ThrowFromLog(string _) => throw new InvalidOperationException("logger-secret-marker");
        void ThrowFromProperty(object? _, PropertyChangedEventArgs args)
        {
            if (args.PropertyName == nameof(AttendanceQrPanelViewModel.VerificationStatusText))
            {
                throw new InvalidOperationException(secret);
            }
        }

        viewModel.PropertyChanged += ThrowFromProperty;
        ConsoleLog.LineWritten += Capture;
        ConsoleLog.LineWritten += ThrowFromLog;
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            fixture.Localization.SetCulture("zh-CN");
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
            ConsoleLog.LineWritten -= ThrowFromLog;
            ConsoleLog.LineWritten -= Capture;
            viewModel.PropertyChanged -= ThrowFromProperty;
        }

        Assert.Empty(context.Exceptions);
        var cultureLog = Assert.Single(lines.Where(line => line.Contains("phase=culture", StringComparison.Ordinal)));
        Assert.Contains("error=InvalidOperationException status=none", cultureLog, StringComparison.Ordinal);
        Assert.DoesNotContain("culture-secret-marker", cultureLog, StringComparison.Ordinal);
        Assert.DoesNotContain("request-body", cultureLog, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("KeyMaterial", cultureLog, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ProtectedKey", cultureLog, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Attendance_refresh_logs_exclude_exception_objects_and_sensitive_fields()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepoRoot(), "apps", "pos-wpf", "src", "Hbpos.Client.Wpf", "ViewModels", "AttendanceQrPanelViewModel.cs"));
        var safeLog = Assert.Single(source.Split('\n').Where(line => line.Contains("phase={phase}", StringComparison.Ordinal)));

        Assert.Contains("error={ex.GetType().Name} status={statusCode}", safeLog, StringComparison.Ordinal);
        Assert.Contains("MarkSetupFailedAsync(ex, cancellationToken, \"refresh\")", source, StringComparison.Ordinal);
        Assert.Contains("private async void OnCultureChanged", source, StringComparison.Ordinal);
        Assert.Contains("await _stateGate.WaitAsync(_stop.Token)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("exception: ex", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ex.Message", source, StringComparison.Ordinal);
        Assert.DoesNotContain("kid", safeLog, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("KeyMaterial", safeLog, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("request", safeLog, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ProtectedKey", safeLog, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Clock_rollback_immediately_clears_qr_and_requires_online_time_sync()
    {
        var fixture = new Fixture(online: true);
        using var viewModel = fixture.CreateViewModel();
        await viewModel.RefreshAsync();
        fixture.Connectivity.IsOnline = false;
        fixture.Time.Advance(TimeSpan.FromSeconds(-1));

        await viewModel.TickAsync();

        Assert.Null(viewModel.QrToken);
        Assert.Null(viewModel.QrImage);
        Assert.Contains("online", viewModel.MessageText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Clock_rollback_stays_latched_offline_until_online_time_sync_succeeds()
    {
        var fixture = new Fixture(online: true);
        using var viewModel = fixture.CreateViewModel();
        await viewModel.RefreshAsync();
        fixture.Connectivity.IsOnline = false;

        fixture.Time.Advance(TimeSpan.FromSeconds(-1));
        await viewModel.TickAsync();
        fixture.Time.Advance(TimeSpan.FromSeconds(1));
        await viewModel.RefreshAsync();

        Assert.Null(viewModel.QrToken);
        Assert.Null(viewModel.QrImage);

        fixture.Connectivity.IsOnline = true;
        await viewModel.RefreshAsync();

        Assert.NotNull(viewModel.QrToken);
        Assert.NotNull(viewModel.QrImage);
    }

    [Fact]
    public async Task Device_context_change_requires_online_registration_and_uses_new_kid()
    {
        var fixture = new Fixture(online: true);
        using var viewModel = fixture.CreateViewModel();
        await viewModel.RefreshAsync();
        var oldKid = fixture.Api.Registrations.Single().Kid;
        fixture.Device = fixture.Device with { StoreCode = "2002", StoreName = "Second Store" };
        fixture.Connectivity.IsOnline = false;

        await viewModel.RefreshAsync();

        Assert.Null(viewModel.QrToken);
        fixture.Connectivity.IsOnline = true;
        await viewModel.RefreshAsync();
        Assert.NotEqual(oldKid, fixture.Api.Registrations.Last().Kid);
        Assert.Equal("Second Store (2002)", viewModel.StoreText);
    }

    [Fact]
    public async Task Corrupted_protected_identity_stops_offline_qr_and_recovers_with_new_key_online()
    {
        var fixture = new Fixture(online: true);
        string oldKid;
        using (var first = fixture.CreateViewModel())
        {
            await first.RefreshAsync();
            oldKid = fixture.Api.Registrations.Single().Kid;
        }

        var identityKey = fixture.Settings.Values.Single(pair => pair.Value.Contains("keyMaterial", StringComparison.OrdinalIgnoreCase)).Key;
        fixture.Settings.Values[identityKey] = "protected:{broken-json";
        fixture.Connectivity.IsOnline = false;
        using var recovered = fixture.CreateViewModel();

        await recovered.RefreshAsync();
        Assert.Null(recovered.QrToken);

        fixture.Connectivity.IsOnline = true;
        await recovered.RefreshAsync();
        Assert.NotNull(recovered.QrToken);
        Assert.NotEqual(oldKid, fixture.Api.Registrations.Last().Kid);
    }

    [Fact]
    public async Task Reregistered_device_authorization_requires_a_new_attendance_key()
    {
        var fixture = new Fixture(online: true);
        using var viewModel = fixture.CreateViewModel();
        await viewModel.RefreshAsync();
        var oldKid = fixture.Api.Registrations.Single().Kid;

        fixture.Device = fixture.Device with { AuthorizationCode = "new-device-authorization" };
        await viewModel.RefreshAsync();

        Assert.NotEqual(oldKid, fixture.Api.Registrations.Last().Kid);
    }

    [Fact]
    public async Task Blocked_online_verification_never_delays_expired_qr_cleanup_and_rotation()
    {
        var fixture = new Fixture(online: true);
        using var viewModel = fixture.CreateViewModel();
        await viewModel.RefreshAsync();
        var firstToken = viewModel.QrToken;
        var imageChanges = new List<BitmapImage?>();
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(AttendanceQrPanelViewModel.QrImage))
            {
                imageChanges.Add(viewModel.QrImage);
            }
        };

        fixture.Api.BlockRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var refresh = viewModel.RefreshAsync();
        await fixture.Api.RefreshBlocked.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);
        try
        {
            fixture.Time.Advance(TimeSpan.FromSeconds(15));
            await viewModel.TickAsync().WaitAsync(AsyncTestWaitSupport.DefaultTimeout);

            Assert.NotEqual(firstToken, viewModel.QrToken);
            Assert.Contains(null, imageChanges);
        }
        finally
        {
            fixture.Api.BlockRefresh.SetResult();
            await refresh;
        }
    }

    [Fact]
    public async Task Every_online_refresh_authenticates_the_current_key_and_conflict_rotates_once()
    {
        var fixture = new Fixture(online: true);
        using var viewModel = fixture.CreateViewModel();
        await viewModel.RefreshAsync();
        var firstKid = fixture.Api.Registrations.Single().Kid;

        await viewModel.RefreshAsync();
        Assert.Equal(2, fixture.Api.Registrations.Count);
        Assert.Equal(firstKid, fixture.Api.Registrations[1].Kid);

        fixture.Api.ConflictNextRegistration = true;
        await viewModel.RefreshAsync();
        Assert.Equal(4, fixture.Api.Registrations.Count);
        Assert.NotEqual(firstKid, fixture.Api.Registrations[^1].Kid);
    }

    [Fact]
    public async Task Production_background_tick_rotates_expired_qr_while_refresh_put_is_blocked()
    {
        var fixture = new Fixture(online: true);
        using var viewModel = fixture.CreateViewModel(
            startTimer: true,
            tickInterval: TimeSpan.FromMilliseconds(10),
            refreshInterval: TimeSpan.FromMilliseconds(100));
        await WaitUntilAsync(() => viewModel.QrToken is not null, TimeSpan.FromSeconds(2));
        var firstToken = viewModel.QrToken;

        fixture.Api.BlockRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await WaitUntilAsync(() => fixture.Api.RegistrationCount >= 2, TimeSpan.FromSeconds(2));
        fixture.Time.Advance(TimeSpan.FromSeconds(15));

        await WaitUntilAsync(() => viewModel.QrToken is not null && viewModel.QrToken != firstToken, TimeSpan.FromSeconds(2));
        Assert.NotNull(viewModel.QrToken);
    }

    [Fact]
    public async Task Dispose_does_not_wait_for_a_non_cooperative_refresh_request()
    {
        var fixture = new Fixture(online: true);
        fixture.Api.BlockRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Api.IgnoreCancellationWhileBlocked = true;
        var viewModel = fixture.CreateViewModel(
            startTimer: true,
            tickInterval: TimeSpan.FromMilliseconds(10),
            refreshInterval: TimeSpan.FromHours(1));
        await fixture.Api.RefreshBlocked.Task.WaitAsync(AsyncTestWaitSupport.DefaultTimeout);

        var startedAt = Stopwatch.GetTimestamp();
        viewModel.Dispose();
        var elapsed = Stopwatch.GetElapsedTime(startedAt);
        fixture.Api.BlockRefresh.TrySetResult();

        Assert.True(elapsed < TimeSpan.FromMilliseconds(100), $"Dispose blocked for {elapsed.TotalMilliseconds:0}ms.");
    }

    [Fact]
    public async Task Offline_refresh_never_lowers_observed_clock_high_water_mark()
    {
        var fixture = new Fixture(online: true);
        using var viewModel = fixture.CreateViewModel();
        await viewModel.RefreshAsync();

        fixture.Time.Advance(TimeSpan.FromMinutes(10));
        await viewModel.TickAsync();
        fixture.Connectivity.IsOnline = false;
        await viewModel.RefreshAsync();

        fixture.Time.SetUtcNow(fixture.Time.GetUtcNow().AddMinutes(-5));
        await viewModel.TickAsync();

        Assert.Null(viewModel.QrToken);
        Assert.Null(viewModel.QrImage);
        Assert.Contains("Clock changed", viewModel.MessageText, StringComparison.Ordinal);
    }

    [Fact]
    public void Generated_png_uses_integer_modules_within_260_pixels_and_decodes_hbate1_token()
    {
        var key = RandomNumberGenerator.GetBytes(AttendanceQrTokenCodec.KeyLength);
        var token = AttendanceQrTokenCodec.Encrypt(
            new AttendanceQrTokenPayload
            {
                TokenId = Guid.NewGuid(),
                StoreCode = new string('S', 50),
                DeviceCode = new string('D', 50),
                IssuedAtUtc = new DateTime(2026, 7, 16, 1, 2, 3, DateTimeKind.Utc),
            },
            "12345678901234",
            key);

        var rendered = AttendanceQrPngRenderer.Render(token);

        Assert.StartsWith("HBATE1.", token, StringComparison.Ordinal);
        Assert.True(rendered.PixelsPerModule >= 2);
        Assert.True(rendered.PixelSize <= 260);
        Assert.Equal(0, rendered.PixelSize % rendered.PixelsPerModule);
        Assert.Equal(token, DecodeQr(rendered.PngBytes));
    }

    private static byte[] DecodeBase64Url(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - padded.Length % 4) % 4);
        return Convert.FromBase64String(padded);
    }

    private static string FindRepoRoot()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var current = new DirectoryInfo(start);
            while (current is not null)
            {
                if (Directory.Exists(Path.Combine(current.FullName, "apps", "pos-wpf")))
                {
                    return current.FullName;
                }

                current = current.Parent;
            }
        }

        throw new DirectoryNotFoundException("Unable to find repository root.");
    }

    private sealed class Fixture
    {
        public Fixture(bool online)
        {
            Connectivity.IsOnline = online;
            Api.ServerUtc = ServerUtc;
        }

        public DateTimeOffset ServerUtc { get; } = new(2026, 7, 16, 1, 2, 3, TimeSpan.Zero);

        public MutableTimeProvider Time { get; } = new(new DateTimeOffset(2026, 7, 16, 15, 2, 3, TimeSpan.FromHours(10)));

        public FakeConnectivity Connectivity { get; } = new();

        public FakeAttendanceApi Api { get; } = new();

        public MemorySettings Settings { get; } = new();

        public PrefixProtector Protector { get; } = new();

        public TestLocalization Localization { get; } = new();

        public LocalDeviceCache Device { get; set; } = new("POS-01", "1002", "Main Store", "HW-01", 1, true, null, DateTimeOffset.UtcNow, "auth");

        public AttendanceQrPanelViewModel CreateViewModel(
            bool startTimer = false,
            TimeSpan? tickInterval = null,
            TimeSpan? refreshInterval = null) => new(
            Api,
            Connectivity,
            new DeviceRepository(() => Device),
            new Fingerprint("HW-01"),
            Settings,
            Protector,
            Localization,
            Time,
            startTimer,
            tickInterval: tickInterval,
            refreshInterval: refreshInterval);
    }

    private sealed class FakeAttendanceApi : IAttendanceSigningKeyApiClient
    {
        private int _registrationCount;

        public DateTimeOffset ServerUtc { get; set; }

        public Exception? RegisterException { get; set; }

        public bool ConflictNextRegistration { get; set; }

        public TaskCompletionSource? BlockRefresh { get; set; }

        public TaskCompletionSource RefreshBlocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool IgnoreCancellationWhileBlocked { get; set; }

        public List<AttendanceSigningKeyRegistrationRequest> Registrations { get; } = [];

        public int RegistrationCount => Volatile.Read(ref _registrationCount);

        public async Task<AttendanceSigningKeyRegistrationResponse> RegisterAsync(
            AttendanceSigningKeyRegistrationRequest request,
            CancellationToken cancellationToken = default)
        {
            Registrations.Add(request);
            Interlocked.Increment(ref _registrationCount);
            if (BlockRefresh is not null)
            {
                RefreshBlocked.TrySetResult();
                if (IgnoreCancellationWhileBlocked)
                {
                    await BlockRefresh.Task;
                }
                else
                {
                    await BlockRefresh.Task.WaitAsync(cancellationToken);
                }
            }

            if (ConflictNextRegistration)
            {
                ConflictNextRegistration = false;
                throw new HttpRequestException("revoked", null, HttpStatusCode.Conflict);
            }

            if (RegisterException is not null)
            {
                throw RegisterException;
            }

            return new AttendanceSigningKeyRegistrationResponse(request.Kid, ServerUtc.UtcDateTime, ServerUtc.UtcDateTime);
        }

    }

    private sealed class FakeConnectivity : IConnectivityApiClient
    {
        public bool IsOnline { get; set; }

        public Task<bool> CheckOnlineAsync(CancellationToken cancellationToken = default) => Task.FromResult(IsOnline);
    }

    private sealed class DeviceRepository(Func<LocalDeviceCache> getDevice) : ILocalDeviceRepository
    {
        public Task<LocalDeviceCache?> GetLatestAsync(CancellationToken cancellationToken = default) => Task.FromResult<LocalDeviceCache?>(getDevice());

        public Task SaveAsync(Hbpos.Contracts.Devices.DeviceRegisterResponse response, string hardwareId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SaveAsync(Hbpos.Contracts.Devices.DeviceVerifyResponse response, string hardwareId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SaveAsync(Hbpos.Contracts.Devices.DeviceReregisterResponse response, string hardwareId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class Fingerprint(string hardwareId) : IDeviceFingerprintService
    {
        public string GetHardwareId() => hardwareId;
    }

    private sealed class MemorySettings : ILocalAppSettingsRepository
    {
        private Exception? _getException;

        public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);

        public Exception? GetException
        {
            get => Volatile.Read(ref _getException);
            set => Volatile.Write(ref _getException, value);
        }

        public Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default)
        {
            if (GetException is not null) throw GetException;
            return Task.FromResult(Values.GetValueOrDefault(key));
        }

        public Task SetValueAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            Values[key] = value;
            return Task.CompletedTask;
        }

        public Task SetValuesAsync(
            IReadOnlyDictionary<string, string> values,
            CancellationToken cancellationToken = default)
        {
            foreach (var (key, value) in values)
            {
                Values[key] = value;
            }

            return Task.CompletedTask;
        }

        public Task DeleteValueAsync(string key, CancellationToken cancellationToken = default)
        {
            Values.Remove(key);
            return Task.CompletedTask;
        }
    }

    private sealed class PrefixProtector : IDeviceAuthorizationProtector
    {
        public Exception? ProtectException { get; set; }

        public string? Protect(string? value)
        {
            if (ProtectException is not null) throw ProtectException;
            return value is null ? null : $"protected:{value}";
        }

        public string? Unprotect(string? protectedValue) => protectedValue?.StartsWith("protected:", StringComparison.Ordinal) == true
            ? protectedValue[10..]
            : null;
    }

    private sealed class TestLocalization : ILocalizationService
    {
        private readonly LocalizationService _inner = new();
        private int _failNextOnlineVerification;
        private int _blockEnableOnline;

        public TaskCompletionSource TickCatchEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private TaskCompletionSource TickCatchRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<CultureInfo> AvailableCultures => _inner.AvailableCultures;

        public CultureInfo CurrentCulture => _inner.CurrentCulture;

        public event EventHandler? CultureChanged
        {
            add => _inner.CultureChanged += value;
            remove => _inner.CultureChanged -= value;
        }

        public event PropertyChangedEventHandler? PropertyChanged
        {
            add => _inner.PropertyChanged += value;
            remove => _inner.PropertyChanged -= value;
        }

        public void ArmTickFailure()
        {
            Volatile.Write(ref _failNextOnlineVerification, 1);
            Volatile.Write(ref _blockEnableOnline, 1);
        }

        public void ReleaseTickCatch() => TickCatchRelease.TrySetResult();

        public void SetCulture(string cultureName) => _inner.SetCulture(cultureName);

        public void SetCulture(CultureInfo culture) => _inner.SetCulture(culture);

        public Task SetCultureAsync(string cultureName, CancellationToken cancellationToken = default) =>
            _inner.SetCultureAsync(cultureName, cancellationToken);

        public string T(string key)
        {
            if (key == "attendance.qr.onlineVerified" && Interlocked.Exchange(ref _failNextOnlineVerification, 0) == 1)
            {
                throw new InvalidOperationException("secret-marker-request-KeyMaterial-ProtectedKey");
            }

            if (key == "attendance.qr.enableOnline" && Volatile.Read(ref _blockEnableOnline) == 1)
            {
                TickCatchEntered.TrySetResult();
                TickCatchRelease.Task.GetAwaiter().GetResult();
                Volatile.Write(ref _blockEnableOnline, 0);
            }

            return _inner.T(key);
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan delta) => _utcNow += delta;

        public void SetUtcNow(DateTimeOffset value) => _utcNow = value;
    }

    private sealed class CapturingSynchronizationContext : SynchronizationContext
    {
        public List<Exception> Exceptions { get; } = [];

        public override void Post(SendOrPostCallback d, object? state)
        {
            try { d(state); }
            catch (Exception ex) { Exceptions.Add(ex); }
        }
    }

    private static string? DecodeQr(byte[] pngBytes)
    {
        using var stream = new MemoryStream(pngBytes);
        var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var bitmap = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgra32, null, 0);
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        var result = new BarcodeReaderGeneric
        {
            AutoRotate = false,
            Options = new DecodingOptions
            {
                PossibleFormats = [BarcodeFormat.QR_CODE],
                PureBarcode = true,
                TryHarder = true,
            },
        }.Decode(new RGBLuminanceSource(pixels, bitmap.PixelWidth, bitmap.PixelHeight, RGBLuminanceSource.BitmapFormat.BGRA32));
        return result?.Text;
    }
}
