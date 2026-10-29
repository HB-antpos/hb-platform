using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Hbpos.Client.Wpf.Localization;
using Hbpos.Client.Wpf.Services;
using Hbpos.Client.Wpf.ViewModels;
using Hbpos.Contracts.Cashiers;

namespace Hbpos.Client.Tests;

/// <summary>
/// 收银员个人配色：员工偏好 → 本机上次配色 → 默认蓝色；只有服务端确认「从未选过」才弹首次选择窗，
/// 离线 / 紧急授权 / 旧版服务端 / 换人都不弹窗，旧员工的配色也不会盖到新会话上。
/// </summary>
public sealed class CashierColorThemePreferenceTests
{
    private const string UserGuid = "User-Guid-1";

    [Fact]
    public async Task Online_login_with_a_saved_preference_switches_to_it_and_caches_it_locally()
    {
        var fixture = new CashierColorThemeFixture(terminalTheme: PosColorTheme.Paper);
        fixture.Api.FetchResult = new CashierColorThemeFetchResult(CashierColorThemeFetchStatus.Found, PosColorTheme.Graphite);
        var session = fixture.Login();

        var outcome = await fixture.Service.ApplyForCashierAsync(session);

        Assert.Equal(CashierColorThemeOutcome.Applied, outcome);
        Assert.Equal(PosColorTheme.Graphite, fixture.Themes.Current);
        // 本机「上次使用的配色」跟着换成这位员工的选择，下次登录前的登录界面就是它。
        Assert.Equal("Graphite", fixture.Settings[ColorThemeService.SettingKey]);
        Assert.Equal("Graphite", fixture.Settings[CashierColorThemePreferenceService.UserSettingKey(UserGuid)]);
        Assert.Equal("0", fixture.Settings[CashierColorThemePreferenceService.PendingUploadKey(UserGuid)]);
        Assert.Equal([UserGuid], fixture.Api.Fetches);
        Assert.Empty(fixture.Api.Saves);
    }

    [Fact]
    public async Task Online_login_without_any_preference_asks_the_cashier_and_keeps_the_terminal_theme()
    {
        var fixture = new CashierColorThemeFixture(terminalTheme: PosColorTheme.Celadon);
        fixture.Api.FetchResult = new CashierColorThemeFetchResult(CashierColorThemeFetchStatus.NotSet);

        var outcome = await fixture.Service.ApplyForCashierAsync(fixture.Login());

        Assert.Equal(CashierColorThemeOutcome.NeedsChoice, outcome);
        Assert.Equal(PosColorTheme.Celadon, fixture.Themes.Current);
        Assert.False(fixture.Settings.ContainsKey(CashierColorThemePreferenceService.UserSettingKey(UserGuid)));
    }

    [Fact]
    public async Task Server_without_a_preference_but_local_cache_uses_the_cache_and_uploads_it()
    {
        var fixture = new CashierColorThemeFixture();
        fixture.Settings[CashierColorThemePreferenceService.UserSettingKey(UserGuid)] = "Elder";
        fixture.Api.FetchResult = new CashierColorThemeFetchResult(CashierColorThemeFetchStatus.NotSet);

        var outcome = await fixture.Service.ApplyForCashierAsync(fixture.Login());

        Assert.Equal(CashierColorThemeOutcome.Applied, outcome);
        Assert.Equal(PosColorTheme.Elder, fixture.Themes.Current);
        Assert.Equal([(UserGuid, PosColorTheme.Elder)], fixture.Api.Saves);
    }

    [Theory]
    [InlineData(null, CashierColorThemeOutcome.KeptCurrent, PosColorTheme.Paper)]
    [InlineData("Graphite", CashierColorThemeOutcome.Applied, PosColorTheme.Graphite)]
    public async Task Unavailable_server_falls_back_to_the_local_cache_then_the_terminal_theme_without_asking(
        string? cached,
        CashierColorThemeOutcome expectedOutcome,
        PosColorTheme expectedTheme)
    {
        var fixture = new CashierColorThemeFixture(terminalTheme: PosColorTheme.Paper);
        if (cached is not null)
        {
            fixture.Settings[CashierColorThemePreferenceService.UserSettingKey(UserGuid)] = cached;
        }

        fixture.Api.FetchResult = CashierColorThemeFetchResult.Unavailable;

        var outcome = await fixture.Service.ApplyForCashierAsync(fixture.Login());

        Assert.Equal(expectedOutcome, outcome);
        Assert.Equal(expectedTheme, fixture.Themes.Current);
    }

    [Theory]
    [InlineData(null, CashierColorThemeOutcome.KeptCurrent, PosColorTheme.Blue)]
    [InlineData("Celadon", CashierColorThemeOutcome.Applied, PosColorTheme.Celadon)]
    public async Task Offline_login_uses_only_the_local_cache_and_never_calls_the_server(
        string? cached,
        CashierColorThemeOutcome expectedOutcome,
        PosColorTheme expectedTheme)
    {
        var fixture = new CashierColorThemeFixture();
        if (cached is not null)
        {
            fixture.Settings[CashierColorThemePreferenceService.UserSettingKey(UserGuid)] = cached;
        }

        var outcome = await fixture.Service.ApplyForCashierAsync(fixture.Login(Session() with { IsOfflineCached = true }));

        Assert.Equal(expectedOutcome, outcome);
        Assert.Equal(expectedTheme, fixture.Themes.Current);
        Assert.Empty(fixture.Api.Fetches);
        Assert.Empty(fixture.Api.Saves);
    }

    [Fact]
    public async Task Emergency_login_keeps_the_terminal_theme_and_touches_no_preference()
    {
        var fixture = new CashierColorThemeFixture(terminalTheme: PosColorTheme.Paper);
        var session = fixture.Login(Session() with { IsEmergencyOverride = true });

        var outcome = await fixture.Service.ApplyForCashierAsync(session);
        await fixture.Service.SaveChoiceAsync(session, PosColorTheme.Graphite);

        Assert.Equal(CashierColorThemeOutcome.KeptCurrent, outcome);
        Assert.Empty(fixture.Api.Fetches);
        Assert.Empty(fixture.Api.Saves);
        Assert.DoesNotContain(fixture.Settings.Keys, key => key.StartsWith("Shell:ColorTheme:User:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Choice_made_offline_is_uploaded_on_the_next_online_login_and_wins_over_the_server()
    {
        var fixture = new CashierColorThemeFixture();
        var offline = fixture.Login(Session() with { IsOfflineCached = true });
        await fixture.Service.SaveChoiceAsync(offline, PosColorTheme.Paper);
        Assert.Equal("1", fixture.Settings[CashierColorThemePreferenceService.PendingUploadKey(UserGuid)]);
        Assert.Empty(fixture.Api.Saves);

        fixture.Api.FetchResult = new CashierColorThemeFetchResult(CashierColorThemeFetchStatus.Found, PosColorTheme.Graphite);
        var outcome = await fixture.Service.ApplyForCashierAsync(fixture.Login());

        Assert.Equal(CashierColorThemeOutcome.Applied, outcome);
        Assert.Equal(PosColorTheme.Paper, fixture.Themes.Current);
        Assert.Equal([(UserGuid, PosColorTheme.Paper)], fixture.Api.Saves);
        Assert.Empty(fixture.Api.Fetches);
        Assert.Equal("0", fixture.Settings[CashierColorThemePreferenceService.PendingUploadKey(UserGuid)]);
    }

    [Fact]
    public async Task Failed_upload_keeps_the_pending_flag()
    {
        var fixture = new CashierColorThemeFixture();
        fixture.Api.SaveSucceeds = false;

        await fixture.Service.SaveChoiceAsync(fixture.Login(), PosColorTheme.Celadon);

        Assert.Equal(PosColorTheme.Celadon, fixture.Themes.Current);
        Assert.Equal([(UserGuid, PosColorTheme.Celadon)], fixture.Api.Saves);
        Assert.Equal("1", fixture.Settings[CashierColorThemePreferenceService.PendingUploadKey(UserGuid)]);
    }

    [Fact]
    public async Task Preference_that_arrives_after_the_cashier_changed_is_not_applied()
    {
        var fixture = new CashierColorThemeFixture(terminalTheme: PosColorTheme.Paper);
        var first = fixture.Login();
        fixture.Api.FetchResult = new CashierColorThemeFetchResult(CashierColorThemeFetchStatus.Found, PosColorTheme.Graphite);
        fixture.Api.BeforeFetchReturns = () => fixture.Login(Session() with { UserGuid = "someone-else" });

        var outcome = await fixture.Service.ApplyForCashierAsync(first);

        Assert.Equal(CashierColorThemeOutcome.KeptCurrent, outcome);
        Assert.Equal(PosColorTheme.Paper, fixture.Themes.Current);
    }

    [Fact]
    public async Task Header_menu_saves_for_the_logged_in_cashier_and_only_locally_when_nobody_is_logged_in()
    {
        var fixture = new CashierColorThemeFixture();

        await fixture.Service.SelectForCurrentCashierAsync(PosColorTheme.Celadon);
        Assert.Equal("Celadon", fixture.Settings[ColorThemeService.SettingKey]);
        Assert.Empty(fixture.Api.Saves);

        fixture.Login();
        var switcher = new ColorThemeSwitcherViewModel(fixture.Themes, Localization(), fixture.Service);
        await switcher.SelectCommand.ExecuteAsync(PosColorTheme.Elder);

        Assert.Equal(PosColorTheme.Elder, fixture.Themes.Current);
        Assert.Equal([(UserGuid, PosColorTheme.Elder)], fixture.Api.Saves);
        Assert.Equal("Elder", fixture.Settings[CashierColorThemePreferenceService.UserSettingKey(UserGuid)]);
    }

    [Fact]
    public async Task Onboarding_opens_on_the_terminal_theme_previews_without_saving_and_saves_on_confirm()
    {
        var fixture = new CashierColorThemeFixture(terminalTheme: PosColorTheme.Paper);
        fixture.Api.FetchResult = new CashierColorThemeFetchResult(CashierColorThemeFetchStatus.NotSet);
        var onboarding = fixture.Onboarding();

        await onboarding.HandleCashierLoginAsync(fixture.Login());

        Assert.True(onboarding.IsOpen);
        Assert.Equal("欢迎，Alice", onboarding.Title);
        Assert.Equal("A", onboarding.AvatarText);
        Assert.Equal([PosColorTheme.Paper], onboarding.Options.Where(option => option.IsSelected).Select(option => option.Theme));
        Assert.Equal("使用「暖纸」开始收银", onboarding.ConfirmText);

        onboarding.PreviewCommand.Execute(PosColorTheme.Graphite);
        Assert.Equal(PosColorTheme.Graphite, fixture.Themes.Current);
        Assert.Equal("Paper", fixture.Settings[ColorThemeService.SettingKey]);
        Assert.Empty(fixture.Api.Saves);
        Assert.Equal("使用「石墨夜」开始收银", onboarding.ConfirmText);

        await onboarding.ConfirmCommand.ExecuteAsync(null);

        Assert.False(onboarding.IsOpen);
        Assert.Equal("Graphite", fixture.Settings[ColorThemeService.SettingKey]);
        Assert.Equal([(UserGuid, PosColorTheme.Graphite)], fixture.Api.Saves);
    }

    [Fact]
    public async Task Next_cashier_login_withdraws_an_unconfirmed_choice_and_restores_the_theme()
    {
        var fixture = new CashierColorThemeFixture(terminalTheme: PosColorTheme.Paper);
        fixture.Api.FetchResult = new CashierColorThemeFetchResult(CashierColorThemeFetchStatus.NotSet);
        var onboarding = fixture.Onboarding();
        await onboarding.HandleCashierLoginAsync(fixture.Login());
        onboarding.PreviewCommand.Execute(PosColorTheme.Elder);

        fixture.Api.FetchResult = new CashierColorThemeFetchResult(CashierColorThemeFetchStatus.Found, PosColorTheme.Celadon);
        await onboarding.HandleCashierLoginAsync(fixture.Login(Session() with { UserGuid = "user-2", CashierName = "Bob" }));

        Assert.False(onboarding.IsOpen);
        Assert.Equal(PosColorTheme.Celadon, fixture.Themes.Current);
        Assert.Empty(fixture.Api.Saves);
    }

    [Fact]
    public async Task Confirm_after_the_cashier_changed_saves_nothing_and_restores_the_theme()
    {
        var fixture = new CashierColorThemeFixture(terminalTheme: PosColorTheme.Paper);
        fixture.Api.FetchResult = new CashierColorThemeFetchResult(CashierColorThemeFetchStatus.NotSet);
        var onboarding = fixture.Onboarding();
        await onboarding.HandleCashierLoginAsync(fixture.Login());
        onboarding.PreviewCommand.Execute(PosColorTheme.Elder);
        fixture.Sessions.Clear();

        await onboarding.ConfirmCommand.ExecuteAsync(null);

        Assert.False(onboarding.IsOpen);
        Assert.Equal(PosColorTheme.Paper, fixture.Themes.Current);
        Assert.Empty(fixture.Api.Saves);
        Assert.Equal("Paper", fixture.Settings[ColorThemeService.SettingKey]);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, """{"success":true,"data":{"userGuid":"user-guid-1","colorTheme":"Graphite"}}""", CashierColorThemeFetchStatus.Found)]
    [InlineData(HttpStatusCode.OK, """{"success":true,"data":{"userGuid":"user-guid-1","colorTheme":null}}""", CashierColorThemeFetchStatus.NotSet)]
    [InlineData(HttpStatusCode.OK, """{"success":true,"data":{"userGuid":"user-guid-1","colorTheme":"Neon"}}""", CashierColorThemeFetchStatus.NotSet)]
    [InlineData(HttpStatusCode.OK, """{"success":true,"data":{"userGuid":"someone-else","colorTheme":"Graphite"}}""", CashierColorThemeFetchStatus.Unavailable)]
    [InlineData(HttpStatusCode.OK, """{"portal":"wifi-login"}""", CashierColorThemeFetchStatus.Unavailable)]
    [InlineData(HttpStatusCode.NotFound, "", CashierColorThemeFetchStatus.Unavailable)]
    [InlineData(HttpStatusCode.ServiceUnavailable, """{"success":false,"errorCode":"CASHIER_PREFERENCES_UNAVAILABLE"}""", CashierColorThemeFetchStatus.Unavailable)]
    public async Task Api_client_only_reports_not_set_when_the_server_confirms_it_for_the_same_user(
        HttpStatusCode status,
        string body,
        CashierColorThemeFetchStatus expected)
    {
        var handler = new CashierColorThemeStubHandler(_ => new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        var client = new CashierPreferenceApiClient(new HttpClient(handler) { BaseAddress = new Uri("http://pos.test/") });

        var result = await client.GetColorThemeAsync("USER-GUID-1");

        Assert.Equal(expected, result.Status);
        Assert.Equal("/api/v1/cashiers/me/preferences/color-theme", Assert.Single(handler.Requests).RequestUri!.AbsolutePath);
        if (expected == CashierColorThemeFetchStatus.Found)
        {
            Assert.Equal(PosColorTheme.Graphite, result.Theme);
        }
    }

    [Fact]
    public async Task Api_client_saves_the_user_and_theme_name_in_the_body()
    {
        string? sentBody = null;
        var handler = new CashierColorThemeStubHandler(request =>
        {
            sentBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"success":true,"data":{"userGuid":"user-guid-1","colorTheme":"Elder"}}""", Encoding.UTF8, "application/json")
            };
        });
        var client = new CashierPreferenceApiClient(new HttpClient(handler) { BaseAddress = new Uri("http://pos.test/") });

        var saved = await client.SaveColorThemeAsync("user-guid-1", PosColorTheme.Elder);

        Assert.True(saved);
        Assert.Equal(HttpMethod.Put, Assert.Single(handler.Requests).Method);
        using var json = JsonDocument.Parse(sentBody!);
        Assert.Equal("user-guid-1", json.RootElement.GetProperty("userGuid").GetString());
        Assert.Equal("Elder", json.RootElement.GetProperty("colorTheme").GetString());
    }

    [Fact]
    public void Client_theme_names_match_the_server_allow_list()
    {
        Assert.Equal(Enum.GetNames<PosColorTheme>().Order(), CashierColorThemes.All.Order());
    }

    [Theory]
    [InlineData("graphite", true, PosColorTheme.Graphite)]
    [InlineData(" Elder ", true, PosColorTheme.Elder)]
    [InlineData("1", false, PosColorTheme.Blue)]
    [InlineData("", false, PosColorTheme.Blue)]
    [InlineData(null, false, PosColorTheme.Blue)]
    [InlineData("Neon", false, PosColorTheme.Blue)]
    public void Preference_parsing_accepts_only_theme_names(string? value, bool expected, PosColorTheme expectedTheme)
    {
        Assert.Equal(expected, ColorThemeService.TryParse(value, out var theme));
        Assert.Equal(expectedTheme, theme);
    }

    internal static CashierSessionDto Session() => new(
        "C001",
        UserGuid,
        "Alice",
        "S01",
        "POS-01",
        ["Cashier"],
        [],
        ["S01"],
        IsSuperAdmin: false,
        IsOfflineCached: false,
        IsEmergencyOverride: false,
        AuthorizationToken: "ticket-token",
        AuthorizationExpiresAtUtc: DateTimeOffset.UtcNow.AddHours(1));

    internal static LocalizationService Localization()
    {
        var localization = new LocalizationService();
        localization.SetCulture("zh-CN");
        return localization;
    }
}

internal sealed class CashierColorThemeFixture
{
    public CashierColorThemeFixture(PosColorTheme? terminalTheme = null, IColorThemeApplier? applier = null)
    {
        if (terminalTheme is not null)
        {
            Settings[ColorThemeService.SettingKey] = terminalTheme.Value.ToString();
        }

        Themes = new ColorThemeService(Settings, applier ?? new CashierColorThemeNullApplier());
        Themes.InitializeAsync().GetAwaiter().GetResult();
        Service = new CashierColorThemePreferenceService(Themes, Settings, Sessions, Api);
    }

    public CashierColorThemeMemorySettings Settings { get; } = new();

    public CashierSessionContext Sessions { get; } = new();

    public CashierColorThemeFakePreferenceApi Api { get; } = new();

    public ColorThemeService Themes { get; }

    public CashierColorThemePreferenceService Service { get; }

    public CashierSessionDto Login(CashierSessionDto? session = null)
    {
        session ??= CashierColorThemePreferenceTests.Session();
        Sessions.SetCurrent(session);
        return session;
    }

    public ColorThemeOnboardingViewModel Onboarding() => new(Service, Themes, Sessions, CashierColorThemePreferenceTests.Localization());
}

internal sealed class CashierColorThemeFakePreferenceApi : ICashierPreferenceApiClient
{
    public CashierColorThemeFetchResult FetchResult { get; set; } = CashierColorThemeFetchResult.Unavailable;

    public bool SaveSucceeds { get; set; } = true;

    public Action? BeforeFetchReturns { get; set; }

    public List<string> Fetches { get; } = [];

    public List<(string UserGuid, PosColorTheme Theme)> Saves { get; } = [];

    public Task<CashierColorThemeFetchResult> GetColorThemeAsync(string expectedUserGuid, CancellationToken cancellationToken = default)
    {
        Fetches.Add(expectedUserGuid);
        BeforeFetchReturns?.Invoke();
        return Task.FromResult(FetchResult);
    }

    public Task<bool> SaveColorThemeAsync(string userGuid, PosColorTheme theme, CancellationToken cancellationToken = default)
    {
        Saves.Add((userGuid, theme));
        return Task.FromResult(SaveSucceeds);
    }
}

internal sealed class CashierColorThemeNullApplier : IColorThemeApplier
{
    public void Apply(PosColorTheme theme)
    {
    }
}

internal sealed class CashierColorThemeStubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.FromResult(respond(request));
    }
}

internal sealed class CashierColorThemeMemorySettings : Dictionary<string, string>, ILocalAppSettingsRepository
{
    public Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default) =>
        Task.FromResult(TryGetValue(key, out var value) ? value : null);

    public Task SetValueAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        this[key] = value;
        return Task.CompletedTask;
    }

    public Task SetValuesAsync(IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken = default)
    {
        foreach (var (key, value) in values)
        {
            this[key] = value;
        }

        return Task.CompletedTask;
    }

    public Task DeleteValueAsync(string key, CancellationToken cancellationToken = default)
    {
        Remove(key);
        return Task.CompletedTask;
    }
}
