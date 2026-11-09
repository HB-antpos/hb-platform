using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Models;
using BlazorApp.Api.Services;
using BlazorApp.Api.Services.Background;
using BlazorApp.Api.Services.Sms;
using BlazorApp.Api.Services.StaffNotifications;
using BlazorApp.Api.Utils;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>
/// 员工通知的服务层与后台发送：用真实的 PasswordResetService / 模板服务 / 发送器，只把邮件、短信和链接换成假的，
/// 这样「写密码 → 发信 → 失败回滚 → 吊销旧登录」整条通道都是真代码在跑。
/// </summary>
public sealed class StaffNotificationServiceTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db");
    private readonly SqliteConnection _connection;
    private readonly SqlSugarClient _db;
    private readonly FakeScope _scope = new();
    private readonly FakeEmailSender _email = new();
    private readonly FakeSms _sms = new();
    private readonly CountingSignal _signal = new();
    private readonly SqlSugarContext _context;
    private User _admin = null!;

    public StaffNotificationServiceTests()
    {
        _connection = new SqliteConnection($"Data Source={_dbPath}");
        _connection.Open();
        _db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = _connection.ConnectionString,
            DbType = DbType.Sqlite,
            IsAutoCloseConnection = false,
            InitKeyType = InitKeyType.Attribute,
        });
        _db.CodeFirst.InitTables(
            typeof(User), typeof(Role), typeof(UserRole), typeof(Store), typeof(UserStore), typeof(EmployeeProfile),
            typeof(UserLoginRestriction), typeof(RefreshToken), typeof(UserPasswordChangeRequirement), typeof(UserPasswordResetCode),
            typeof(SysRolePermission), typeof(SysUserPermission), // 仓库经理判断「受保护目标」要查角色 / 用户权限
            typeof(SmsSendLog), typeof(StaffNotificationTemplate), typeof(StaffNotificationBatch), typeof(StaffNotificationRecipient)
        );
        _context = CreateContext(_db);
        _admin = SeedAdminAsync().GetAwaiter().GetResult();
        _scope.UserGuid = _admin.UserGUID;
    }

    // ───────────── 预检 ─────────────

    [Fact]
    public async Task 预检_按状态分成可发送_需确认_不可发送_并给出渠道与原因()
    {
        var ready = await SeedUserAsync("mia", "mia@example.com", phone: "0491 570 156", store: "Sunnybank");
        var loggedIn = await SeedUserAsync("emma", "emma@example.com", lastLoginAt: DateTime.UtcNow.AddDays(-3));
        var suspicious = await SeedUserAsync("olivia", "olivia@123.com");
        var noEmail = await SeedUserAsync("ava", "ava@s001.store.local");
        var inactive = await SeedUserAsync("ethan", "ethan@example.com", isActive: false);
        var restricted = await SeedUserAsync("frank", "frank@example.com", loginRestricted: true);
        var landline = await SeedUserAsync("liam", "liam@example.com", phone: "07 5550 1234");
        var noPhone = await SeedUserAsync("sophie", "sophie@example.com", phone: null);
        var missingGuid = Guid.NewGuid().ToString();

        var result = await CreateService().PreflightAsync(new StaffNotificationPreflightRequestDto
        {
            TemplateKey = StaffNotificationTemplateDefaults.AccountActivatedKey,
            UserGuids = new List<string>
            {
                ready.UserGUID, loggedIn.UserGUID, suspicious.UserGUID, noEmail.UserGUID, inactive.UserGUID,
                restricted.UserGUID, landline.UserGUID, noPhone.UserGUID, _admin.UserGUID, missingGuid, ready.UserGUID,
            },
            SendEmail = true,
            SendSms = true,
        });

        Assert.True(result.Success);
        var data = result.Data!;
        Assert.True(data.SmsAvailable);
        Assert.Equal(300, data.SmsDailyLimit);
        Assert.Equal(0.0792m, data.SmsUnitCostAud);
        Assert.Equal(10, data.Recipients.Count); // 重复的 mia 只算一次，顺序与请求一致
        var byGuid = data.Recipients.ToDictionary(item => item.UserGuid);

        var mia = byGuid[ready.UserGUID];
        Assert.Equal("ready", mia.Status);
        Assert.Empty(mia.Reasons);
        Assert.True(mia.WillSendEmail);
        Assert.True(mia.WillSendSms);
        Assert.Equal(1, mia.SmsSegments);
        Assert.Equal("Sunnybank", mia.StoreName);
        Assert.Equal("mia@example.com", mia.Email);
        Assert.Equal("0491 570 156", mia.Phone);

        Assert.Equal("confirm", byGuid[loggedIn.UserGUID].Status);
        Assert.Contains("already_logged_in", byGuid[loggedIn.UserGUID].Reasons);
        Assert.True(byGuid[loggedIn.UserGUID].HasLoggedIn);

        Assert.Equal("confirm", byGuid[suspicious.UserGUID].Status);
        Assert.Contains("suspicious_email", byGuid[suspicious.UserGUID].Reasons);

        Assert.Equal("blocked", byGuid[noEmail.UserGUID].Status);
        Assert.Contains("no_email", byGuid[noEmail.UserGUID].Reasons);
        Assert.Equal("blocked", byGuid[inactive.UserGUID].Status);
        Assert.Contains("account_inactive", byGuid[inactive.UserGUID].Reasons);
        Assert.Equal("blocked", byGuid[restricted.UserGUID].Status);
        Assert.Contains("login_disabled", byGuid[restricted.UserGUID].Reasons);
        Assert.Equal("blocked", byGuid[_admin.UserGUID].Status);
        Assert.Contains("self", byGuid[_admin.UserGUID].Reasons);
        Assert.Equal("blocked", byGuid[missingGuid].Status);
        Assert.Contains("not_found", byGuid[missingGuid].Reasons);

        // 固定电话 / 没手机号：仍可发（邮件），只是短信跳过并说明原因。
        Assert.Equal("ready", byGuid[landline.UserGUID].Status);
        Assert.False(byGuid[landline.UserGUID].WillSendSms);
        Assert.Equal("not_mobile", byGuid[landline.UserGUID].SmsSkipReason);
        Assert.Equal("ready", byGuid[noPhone.UserGUID].Status);
        Assert.Equal("no_phone", byGuid[noPhone.UserGUID].SmsSkipReason);
    }

    [Fact]
    public async Task 预检_短信服务没开_短信都跳过但不影响邮件()
    {
        var mia = await SeedUserAsync("mia", "mia@example.com", phone: "0491 570 156");
        _sms.Available = false;

        var result = await CreateService().PreflightAsync(Preflight(mia.UserGUID));

        var item = Assert.Single(result.Data!.Recipients);
        Assert.False(result.Data.SmsAvailable);
        Assert.Equal("ready", item.Status);
        Assert.True(item.WillSendEmail);
        Assert.False(item.WillSendSms);
        Assert.Equal("sms_unavailable", item.SmsSkipReason);
    }

    [Fact]
    public async Task 预检_返回可选发件名_默认发件名排第一_短信服务没开时没有选项()
    {
        var mia = await SeedUserAsync("mia", "mia@example.com", phone: "0491 570 156");

        var on = await CreateService().PreflightAsync(Preflight(mia.UserGUID));
        Assert.Collection(
            on.Data!.SmsSenders,
            first => { Assert.Equal("HotBargain", first.Id); Assert.True(first.IsDefault); },
            second => { Assert.Equal("ANTPOS", second.Id); Assert.False(second.IsDefault); }
        );

        _sms.Available = false;
        var off = await CreateService().PreflightAsync(Preflight(mia.UserGUID));
        Assert.Empty(off.Data!.SmsSenders);
    }

    [Fact]
    public async Task 预检_自建模板只发短信_没有手机号的人没有任何可发渠道_被拦下()
    {
        var withPhone = await SeedUserAsync("mia", "mia@example.com", phone: "0491 570 156");
        var withoutPhone = await SeedUserAsync("sophie", "sophie@example.com", phone: null);
        var template = await CreateGeneralTemplateAsync("SMS only", "Hi {{name}}, roster is out: {{app_link}}");

        var result = await CreateService().PreflightAsync(new StaffNotificationPreflightRequestDto
        {
            TemplateKey = template.Key,
            UserGuids = new List<string> { withPhone.UserGUID, withoutPhone.UserGUID },
            SendEmail = false,
            SendSms = true,
        });

        var byGuid = result.Data!.Recipients.ToDictionary(item => item.UserGuid);
        Assert.Equal("ready", byGuid[withPhone.UserGUID].Status);
        Assert.False(byGuid[withPhone.UserGUID].WillSendEmail);
        Assert.True(byGuid[withPhone.UserGUID].WillSendSms);
        Assert.Equal("blocked", byGuid[withoutPhone.UserGUID].Status);
        Assert.Contains("no_channel", byGuid[withoutPhone.UserGUID].Reasons);
    }

    [Fact]
    public async Task 预检_模板不存在或已停用_参数不合法_都有明确错误()
    {
        var mia = await SeedUserAsync("mia", "mia@example.com");
        var service = CreateService();

        Assert.Equal(StaffNotificationTemplateService.TemplateNotFoundCode,
            (await service.PreflightAsync(new StaffNotificationPreflightRequestDto { TemplateKey = "nope", UserGuids = { mia.UserGUID } })).ErrorCode);
        Assert.Equal("VALIDATION_ERROR",
            (await service.PreflightAsync(new StaffNotificationPreflightRequestDto { TemplateKey = StaffNotificationTemplateDefaults.AccountActivatedKey })).ErrorCode);
        var tooMany = Enumerable.Range(0, 101).Select(_ => Guid.NewGuid().ToString()).ToList();
        Assert.Equal("VALIDATION_ERROR",
            (await service.PreflightAsync(new StaffNotificationPreflightRequestDto { TemplateKey = StaffNotificationTemplateDefaults.AccountActivatedKey, UserGuids = tooMany })).ErrorCode);

        var disabled = await CreateGeneralTemplateAsync("Disabled one", "Hi {{name}}", enabled: false);
        Assert.Equal(StaffNotificationService.TemplateDisabledCode,
            (await service.PreflightAsync(new StaffNotificationPreflightRequestDto { TemplateKey = disabled.Key, UserGuids = { mia.UserGUID } })).ErrorCode);
    }

    [Fact]
    public async Task 预检_没有管理权限的账号_被拒绝()
    {
        var mia = await SeedUserAsync("mia", "mia@example.com");
        var nobody = await SeedUserAsync("nobody", "nobody@example.com");
        _scope.UserGuid = nobody.UserGUID; // 没有任何管理类角色

        var result = await CreateService().PreflightAsync(Preflight(mia.UserGUID));

        Assert.False(result.Success);
        Assert.Equal(StaffNotificationService.ForbiddenCode, result.ErrorCode);
    }

    // ───────────── 预览 ─────────────

    [Fact]
    public async Task 预览_密码是圆点_邮件主题与短信按这个员工渲染()
    {
        var mia = await SeedUserAsync("mia", "mia@example.com", fullName: "Mia Chen", phone: "0491 570 156");

        var result = await CreateService().PreviewAsync(new StaffNotificationPreviewRequestDto
        {
            TemplateKey = StaffNotificationTemplateDefaults.AccountActivatedKey,
            UserGuid = mia.UserGUID,
        });

        Assert.True(result.Success);
        var preview = result.Data!;
        Assert.Equal("Your Hot Bargain staff app account is ready", preview.EmailSubject);
        Assert.Contains("Hi Mia Chen,", preview.EmailHtml);
        Assert.Contains("mia@example.com", preview.EmailHtml);
        Assert.Contains("••••", preview.EmailHtml);
        // 移动端没有 WebView，预览用纯文本版：内容与 HTML 一致，密码同样是圆点，不带任何标签。
        Assert.Contains("Hi Mia Chen,", preview.EmailText);
        Assert.Contains("••••", preview.EmailText);
        Assert.DoesNotContain("<", preview.EmailText);
        Assert.DoesNotContain("hotbargain.vip", preview.SmsText); // 短信里不放链接（ClickSend 拒收该域名）
        Assert.Equal(125, preview.SmsLength);
        Assert.Equal(1, preview.SmsSegments);
        Assert.True(preview.SmsValid);
        // 预览不改密码、不发信、不写记录。
        Assert.Empty(_email.Rendered);
        Assert.False(await _db.Queryable<StaffNotificationBatch>().AnyAsync());
    }

    [Fact]
    public async Task 预览_员工不存在_不泄露任何资料()
    {
        var result = await CreateService().PreviewAsync(new StaffNotificationPreviewRequestDto
        {
            TemplateKey = StaffNotificationTemplateDefaults.AccountActivatedKey,
            UserGuid = Guid.NewGuid().ToString(),
        });

        Assert.False(result.Success);
        Assert.Equal(StaffNotificationService.ForbiddenCode, result.ErrorCode);
    }

    // ───────────── 发送（入队） ─────────────

    [Fact]
    public async Task 发送_选中有登录过的员工但没确认_整体拒绝_不建任何记录()
    {
        var ready = await SeedUserAsync("mia", "mia@example.com");
        var loggedIn = await SeedUserAsync("emma", "emma@example.com", lastLoginAt: DateTime.UtcNow.AddDays(-1));

        var result = await CreateService().SendAsync(SendRequest(ready.UserGUID, loggedIn.UserGUID), "admin");

        Assert.False(result.Success);
        Assert.Equal(StaffNotificationService.ConfirmRequiredCode, result.ErrorCode);
        Assert.False(await _db.Queryable<StaffNotificationBatch>().AnyAsync());
        Assert.Equal(0, _signal.Count);
    }

    [Fact]
    public async Task 发送_确认后入队_被拦下的人进rejected_状态与说明写进每一行()
    {
        var ready = await SeedUserAsync("mia", "mia@example.com", fullName: "Mia Chen", phone: "0491 570 156", store: "Sunnybank");
        var landline = await SeedUserAsync("liam", "liam@example.com", phone: "07 5550 1234");
        var loggedIn = await SeedUserAsync("emma", "emma@example.com", lastLoginAt: DateTime.UtcNow.AddDays(-1), phone: "0491 570 158");
        var inactive = await SeedUserAsync("ethan", "ethan@example.com", isActive: false);

        var request = SendRequest(ready.UserGUID, landline.UserGUID, loggedIn.UserGUID, inactive.UserGUID);
        request.ConfirmReissue = true;
        var result = await CreateService().SendAsync(request, "admin");

        Assert.True(result.Success);
        Assert.Equal("queued", result.Data!.Status);
        Assert.Equal(3, result.Data.TotalCount);
        var rejected = Assert.Single(result.Data.Rejected);
        Assert.Equal(inactive.UserGUID, rejected.UserGuid);
        Assert.Equal("account_inactive", rejected.Reason);
        Assert.Equal(1, _signal.Count);

        var batch = await _db.Queryable<StaffNotificationBatch>().SingleAsync();
        Assert.Equal(StaffNotificationBatchStatuses.Queued, batch.Status);
        Assert.Equal("admin", batch.CreatedBy);
        Assert.Equal(_admin.UserGUID, batch.CreatedByUserGuid);
        Assert.Equal("账号已开通", batch.TemplateName);
        var rows = (await _db.Queryable<StaffNotificationRecipient>().OrderBy(item => item.SortOrder).ToListAsync());
        Assert.Equal(3, rows.Count);

        var mia = rows[0];
        Assert.Equal("Mia Chen", mia.DisplayName);
        Assert.Equal("Sunnybank", mia.StoreName);
        Assert.Equal("mi***@example.com", mia.EmailMask);
        Assert.Equal("+614*****156", mia.PhoneMask);
        Assert.Equal(StaffNotificationChannelStatuses.Pending, mia.EmailStatus);
        Assert.Equal(StaffNotificationChannelStatuses.Pending, mia.SmsStatus);
        Assert.Equal(string.Empty, mia.Note);

        var liam = rows[1];
        Assert.Equal(StaffNotificationChannelStatuses.Pending, liam.EmailStatus);
        Assert.Equal(StaffNotificationChannelStatuses.Skipped, liam.SmsStatus);
        Assert.Contains("固定电话", liam.Note);
        Assert.Equal("***234", liam.PhoneMask);
    }

    [Fact]
    public async Task 发送_同一个人已经在排队的批次里_第二次被拒绝_避免重复重置密码()
    {
        var mia = await SeedUserAsync("mia", "mia@example.com");
        var service = CreateService();

        var first = await service.SendAsync(SendRequest(mia.UserGUID), "admin");
        var second = await service.SendAsync(SendRequest(mia.UserGUID), "admin");

        Assert.True(first.Success);
        Assert.False(second.Success);
        Assert.Equal(StaffNotificationService.NoRecipientsCode, second.ErrorCode);
        var rejected = Assert.Single(Assert.IsType<List<StaffNotificationRejectedDto>>(second.Details));
        Assert.Equal("in_progress", rejected.Reason);
        Assert.Single(await _db.Queryable<StaffNotificationBatch>().ToListAsync());
    }

    [Fact]
    public async Task 发送_系统模板必须发邮件_即使请求把邮件关了也以服务端为准()
    {
        var mia = await SeedUserAsync("mia", "mia@example.com");
        var request = SendRequest(mia.UserGUID);
        request.SendEmail = false;
        request.SendSms = false;

        var result = await CreateService().SendAsync(request, "admin");

        Assert.True(result.Success);
        var batch = await _db.Queryable<StaffNotificationBatch>().SingleAsync();
        Assert.True(batch.SendEmail);
        Assert.False(batch.SendSms);
    }

    [Fact]
    public async Task 发送_自建模板两个渠道都没选_被拒绝()
    {
        var mia = await SeedUserAsync("mia", "mia@example.com");
        var template = await CreateGeneralTemplateAsync("Notice", "Hi {{name}}");

        var result = await CreateService().SendAsync(new StaffNotificationSendRequestDto
        {
            TemplateKey = template.Key,
            UserGuids = { mia.UserGUID },
            SendEmail = false,
            SendSms = false,
        }, "admin");

        Assert.False(result.Success);
        Assert.Equal("VALIDATION_ERROR", result.ErrorCode);
    }

    // ───────────── 后台发送：账号已开通 ─────────────

    [Fact]
    public async Task 后台发送_账号已开通_密码走邮件_短信不含密码_全部Sent()
    {
        var mia = await SeedUserAsync("mia", "mia@example.com", fullName: "Mia Chen", phone: "0491 570 156", store: "Sunnybank");
        var oldHash = await PasswordHashOfAsync(mia.UserGUID);
        await SeedRefreshTokenAsync(mia.UserGUID);
        var sent = await CreateService().SendAsync(SendRequest(mia.UserGUID), "admin");

        await RunDispatcherAsync();

        var newHash = await PasswordHashOfAsync(mia.UserGUID);
        Assert.NotEqual(oldHash, newHash);
        var mail = Assert.Single(_email.Rendered);
        Assert.Equal("mia@example.com", mail.To);
        Assert.Equal("Your Hot Bargain staff app account is ready", mail.Subject);
        var password = Regex.Match(mail.Text, @"Temporary password: (\S+)").Groups[1].Value;
        Assert.Matches("^[A-Za-z2-9]{4}$", password);
        // 密码哈希带随机盐，不能比较两次哈希相等；用校验方法确认「邮件里的密码就是新密码」，并且旧密码已失效。
        Assert.True(PasswordHasher.VerifyPassword(password, newHash, PasswordHasher.PasswordFormatRaw, out _));
        Assert.False(PasswordHasher.VerifyPassword("Secret123", newHash, PasswordHasher.PasswordFormatRaw, out _));
        Assert.Contains($">{password}<", mail.Html);
        Assert.Contains("Hi Mia Chen,", mail.Text);

        // 与新建员工同一条通道：打上首次改密标记（72 小时失效的临时密码口径），旧登录被吊销。
        var requirement = await UserPasswordChangeRequirements.GetAsync(_db, mia.UserGUID);
        Assert.Equal(UserPasswordChangeRequirement.ReasonCreatedEmailed, requirement!.Reason);
        Assert.Equal("admin", requirement.RequiredBy);
        Assert.True((await _db.Queryable<RefreshToken>().SingleAsync()).IsRevoked);

        var smsRequest = Assert.Single(_sms.Sent);
        Assert.Equal(SmsPurposes.StaffNotification, smsRequest.Purpose);
        Assert.Equal("+61491570156", smsRequest.ToPhone); // 发给服务商的是规范化后的 E.164
        Assert.Equal(sent.Data!.BatchId, smsRequest.RelatedKey);
        Assert.DoesNotContain("hotbargain.vip", smsRequest.Body); // 默认模板的短信不带链接
        Assert.DoesNotContain(password, smsRequest.Body);

        var detail = (await CreateService().GetBatchAsync(sent.Data.BatchId)).Data!;
        Assert.Equal("completed", detail.Status);
        Assert.Equal("all_sent", detail.Result);
        Assert.Equal(1, detail.EmailSent);
        Assert.Equal(1, detail.SmsSent);
        Assert.Equal(1, detail.SmsSegments);
        Assert.Equal(0.0792m, detail.SmsCostAud);
        Assert.False(detail.CanRetry);
        var row = Assert.Single(detail.Recipients);
        Assert.Equal("sent", row.EmailStatus);
        Assert.Equal("sent", row.SmsStatus);
        Assert.Equal(string.Empty, row.Note);
        // 邮箱 / 手机号明文与随机密码都不在数据库里。
        var stored = await _db.Queryable<StaffNotificationRecipient>().SingleAsync();
        Assert.DoesNotContain("mia@example.com", stored.EmailMask);
        Assert.DoesNotContain(password, stored.Note + stored.EmailError + stored.SmsError);
    }

    [Fact]
    public async Task 后台发送_邮件失败_密码恢复原样_短信也不发_note说明密码没有改动()
    {
        var mia = await SeedUserAsync("mia", "mia@example.com", phone: "0491 570 156");
        var oldHash = await PasswordHashOfAsync(mia.UserGUID);
        _email.FailFor.Add("mia@example.com");
        var sent = await CreateService().SendAsync(SendRequest(mia.UserGUID), "admin");

        await RunDispatcherAsync();

        Assert.Equal(oldHash, await PasswordHashOfAsync(mia.UserGUID)); // 发信失败必须恢复原密码
        Assert.Null(await UserPasswordChangeRequirements.GetAsync(_db, mia.UserGUID));
        Assert.Empty(_sms.Sent); // 短信写着「密码已发到邮箱」，邮件没发出就不能发
        var detail = (await CreateService().GetBatchAsync(sent.Data!.BatchId)).Data!;
        Assert.Equal("completed", detail.Status);
        Assert.Equal("all_failed", detail.Result);
        Assert.True(detail.CanRetry);
        var row = Assert.Single(detail.Recipients);
        Assert.Equal("failed", row.EmailStatus);
        Assert.Equal("skipped", row.SmsStatus);
        Assert.Contains("密码没有改动", row.Note);
        Assert.Contains("短信也不发", row.Note);
        Assert.Equal(1, detail.SmsRequested); // 被扣住的短信仍算「原本要发」，与预检的条数一致
        Assert.Equal(0, detail.SmsFailed);
    }

    [Fact]
    public async Task 重试_邮件失败后恢复_邮件重发_被扣住的短信一起补发()
    {
        var mia = await SeedUserAsync("mia", "mia@example.com", phone: "0491 570 156");
        _email.FailFor.Add("mia@example.com");
        var service = CreateService();
        var sent = await service.SendAsync(SendRequest(mia.UserGUID), "admin");
        await RunDispatcherAsync();

        _email.FailFor.Clear();
        var retry = await service.RetryAsync(sent.Data!.BatchId, "admin");

        Assert.True(retry.Success);
        Assert.Equal("queued", retry.Data!.Status);
        Assert.Equal("pending", retry.Data.Recipients.Single().EmailStatus);
        Assert.Equal("pending", retry.Data.Recipients.Single().SmsStatus);
        Assert.Equal(2, _signal.Count);

        await RunDispatcherAsync();

        var detail = (await service.GetBatchAsync(sent.Data.BatchId)).Data!;
        Assert.Equal("all_sent", detail.Result);
        Assert.Equal("sent", detail.Recipients.Single().EmailStatus);
        Assert.Equal("sent", detail.Recipients.Single().SmsStatus);
        Assert.Single(_email.Rendered);
        Assert.Single(_sms.Sent);
    }

    [Fact]
    public async Task 重试_只有短信失败_只补短信_不重新生成密码()
    {
        var mia = await SeedUserAsync("mia", "mia@example.com", phone: "0491 570 156");
        _sms.Handler = _ => ApiResponse<bool>.Error("该手机号短信发送过于频繁，请稍后再试", SmsErrorCodes.PhoneRateLimited);
        var service = CreateService();
        var sent = await service.SendAsync(SendRequest(mia.UserGUID), "admin");
        await RunDispatcherAsync();

        var afterFirstRun = await PasswordHashOfAsync(mia.UserGUID);
        var first = (await service.GetBatchAsync(sent.Data!.BatchId)).Data!;
        Assert.Equal("partial_failed", first.Result);
        Assert.Equal("sent", first.Recipients.Single().EmailStatus);
        Assert.Equal("failed", first.Recipients.Single().SmsStatus);
        Assert.Contains("过于频繁", first.Recipients.Single().Note);
        Assert.True(first.CanRetry);

        _sms.Handler = null;
        var retry = await service.RetryAsync(sent.Data.BatchId, "admin");
        Assert.True(retry.Success);
        Assert.Equal("sent", retry.Data!.Recipients.Single().EmailStatus); // 邮件已经成功，不动
        await RunDispatcherAsync();

        Assert.Single(_email.Rendered); // 没有再发第二封邮件
        Assert.Equal(afterFirstRun, await PasswordHashOfAsync(mia.UserGUID)); // 密码没有被重新生成
        Assert.Equal("all_sent", (await service.GetBatchAsync(sent.Data.BatchId)).Data!.Result);
    }

    [Fact]
    public async Task 发送_不指定发件名_用默认发件名_批次记录它_后台发给服务商时带上它()
    {
        var mia = await SeedUserAsync("mia", "mia@example.com", phone: "0491 570 156");
        var service = CreateService();

        var sent = await service.SendAsync(SendRequest(mia.UserGUID), "admin");
        Assert.True(sent.Success, sent.Message);
        Assert.Equal("HotBargain", (await _db.Queryable<StaffNotificationBatch>().SingleAsync()).SmsSenderId);

        await RunDispatcherAsync();

        Assert.Equal("HotBargain", Assert.Single(_sms.Sent).SenderId);
        Assert.Equal("HotBargain", (await service.GetBatchAsync(sent.Data!.BatchId)).Data!.SmsSenderId);
    }

    [Fact]
    public async Task 发送_指定ANTPOS_批次记录它_后台发给服务商时用ANTPOS_批次列表也带出来()
    {
        var mia = await SeedUserAsync("mia", "mia@example.com", phone: "0491 570 156");
        var service = CreateService();
        var request = SendRequest(mia.UserGUID);
        request.SmsSenderId = "antpos"; // 忽略大小写，落库用配置里的规范写法

        var sent = await service.SendAsync(request, "admin");
        Assert.True(sent.Success, sent.Message);
        await RunDispatcherAsync();

        Assert.Equal("ANTPOS", (await _db.Queryable<StaffNotificationBatch>().SingleAsync()).SmsSenderId);
        Assert.Equal("ANTPOS", Assert.Single(_sms.Sent).SenderId);
        var listed = (await service.ListBatchesAsync(new StaffNotificationBatchQueryDto())).Data!;
        Assert.Equal("ANTPOS", Assert.Single(listed.Items).SmsSenderId);
    }

    [Fact]
    public async Task 发送_指定不在白名单的发件名_整体拒绝_不建批次_不重置密码()
    {
        var mia = await SeedUserAsync("mia", "mia@example.com", phone: "0491 570 156");
        var oldHash = await PasswordHashOfAsync(mia.UserGUID);
        var request = SendRequest(mia.UserGUID);
        request.SmsSenderId = "SomeoneElse";

        var result = await CreateService().SendAsync(request, "admin");

        Assert.False(result.Success);
        Assert.Equal("VALIDATION_ERROR", result.ErrorCode);
        Assert.Empty(await _db.Queryable<StaffNotificationBatch>().ToListAsync());
        Assert.Equal(oldHash, await PasswordHashOfAsync(mia.UserGUID));
    }

    [Fact]
    public async Task 发送_只发邮件时_忽略发件名字段_批次不记录发件名()
    {
        var mia = await SeedUserAsync("mia", "mia@example.com", phone: "0491 570 156");
        var request = SendRequest(mia.UserGUID);
        request.SendSms = false;
        request.SmsSenderId = "ANTPOS";

        var sent = await CreateService().SendAsync(request, "admin");

        Assert.True(sent.Success, sent.Message);
        Assert.Null((await _db.Queryable<StaffNotificationBatch>().SingleAsync()).SmsSenderId);
        Assert.Null((await CreateService().GetBatchAsync(sent.Data!.BatchId)).Data!.SmsSenderId);
    }

    [Fact]
    public async Task 短信服务没开时_指定的发件名被忽略_邮件照发_批次不记录发件名()
    {
        var mia = await SeedUserAsync("mia", "mia@example.com", phone: "0491 570 156");
        _sms.Available = false;
        var request = SendRequest(mia.UserGUID);
        request.SmsSenderId = "SomeoneElse";

        var sent = await CreateService().SendAsync(request, "admin");

        Assert.True(sent.Success, sent.Message);
        Assert.Null((await _db.Queryable<StaffNotificationBatch>().SingleAsync()).SmsSenderId);
    }

    [Fact]
    public async Task 重试_沿用批次当初选的发件名_不会悄悄换回默认发件名()
    {
        var mia = await SeedUserAsync("mia", "mia@example.com", phone: "0491 570 156");
        _sms.Handler = _ => ApiResponse<bool>.Error("短信发送失败，请稍后重试", SmsErrorCodes.SendFailed);
        var service = CreateService();
        var request = SendRequest(mia.UserGUID);
        request.SmsSenderId = "ANTPOS";
        var sent = await service.SendAsync(request, "admin");
        await RunDispatcherAsync();
        Assert.Empty(_sms.Sent); // 第一次全部失败

        _sms.Handler = null;
        var retry = await service.RetryAsync(sent.Data!.BatchId, "admin");
        Assert.True(retry.Success, retry.Message);
        await RunDispatcherAsync();

        Assert.Equal("ANTPOS", Assert.Single(_sms.Sent).SenderId);
        Assert.Equal("ANTPOS", (await service.GetBatchAsync(sent.Data.BatchId)).Data!.SmsSenderId);
    }

    [Fact]
    public async Task 重试_批次还在发送中_或没有失败项_都被拒绝()
    {
        var mia = await SeedUserAsync("mia", "mia@example.com");
        var service = CreateService();
        var sent = await service.SendAsync(SendRequest(mia.UserGUID), "admin");

        var running = await service.RetryAsync(sent.Data!.BatchId, "admin"); // 还是 queued
        Assert.Equal("BATCH_IN_PROGRESS", running.ErrorCode);

        await RunDispatcherAsync();
        var nothing = await service.RetryAsync(sent.Data.BatchId, "admin");
        Assert.Equal("NOTHING_TO_RETRY", nothing.ErrorCode);
    }

    [Fact]
    public async Task 后台发送_排队之后账号被停用_不再发信也不动密码()
    {
        var mia = await SeedUserAsync("mia", "mia@example.com", phone: "0491 570 156");
        var oldHash = await PasswordHashOfAsync(mia.UserGUID);
        var sent = await CreateService().SendAsync(SendRequest(mia.UserGUID), "admin");
        await _db.Updateable<User>().SetColumns(item => item.IsActive == false).Where(item => item.UserGUID == mia.UserGUID).ExecuteCommandAsync();

        await RunDispatcherAsync();

        Assert.Empty(_email.Rendered);
        Assert.Empty(_sms.Sent);
        Assert.Equal(oldHash, await PasswordHashOfAsync(mia.UserGUID));
        var row = (await CreateService().GetBatchAsync(sent.Data!.BatchId)).Data!.Recipients.Single();
        Assert.Equal("failed", row.EmailStatus);
        Assert.Equal("failed", row.SmsStatus);
        Assert.Contains("停用", row.Note);
    }

    // ───────────── 后台发送：自建模板 ─────────────

    [Fact]
    public async Task 自建模板_邮件不带密码_不改账号_也不打首次改密标记()
    {
        var mia = await SeedUserAsync("mia", "mia@example.com", fullName: "Mia Chen", phone: "0491 570 156");
        var oldHash = await PasswordHashOfAsync(mia.UserGUID);
        var template = await CreateGeneralTemplateAsync("Roster notice", "Hi {{name}}, your roster is out: {{app_link}}");
        var service = CreateService();
        var sent = await service.SendAsync(new StaffNotificationSendRequestDto
        {
            TemplateKey = template.Key,
            UserGuids = { mia.UserGUID },
            SendEmail = true,
            SendSms = true,
        }, "admin");
        Assert.True(sent.Success);

        await RunDispatcherAsync();

        var mail = Assert.Single(_email.Rendered);
        Assert.DoesNotContain("Temporary password", mail.Text);
        Assert.Equal("Roster update", mail.Subject);
        Assert.Contains("Hi Mia Chen,", mail.Text);
        Assert.Equal(oldHash, await PasswordHashOfAsync(mia.UserGUID));
        Assert.Null(await UserPasswordChangeRequirements.GetAsync(_db, mia.UserGUID));
        Assert.Equal("Hi Mia Chen, your roster is out: https://hotbargain.vip/app", Assert.Single(_sms.Sent).Body);
        Assert.Equal("all_sent", (await service.GetBatchAsync(sent.Data!.BatchId)).Data!.Result);
    }

    [Fact]
    public async Task 自建模板_邮件失败不影响短信_两个渠道互相独立()
    {
        var mia = await SeedUserAsync("mia", "mia@example.com", phone: "0491 570 156");
        var template = await CreateGeneralTemplateAsync("Roster notice", "Hi {{name}}, your roster is out");
        _email.FailFor.Add("mia@example.com");
        var service = CreateService();
        var sent = await service.SendAsync(new StaffNotificationSendRequestDto
        {
            TemplateKey = template.Key,
            UserGuids = { mia.UserGUID },
            SendEmail = true,
            SendSms = true,
        }, "admin");

        await RunDispatcherAsync();

        var detail = (await service.GetBatchAsync(sent.Data!.BatchId)).Data!;
        Assert.Equal("failed", detail.Recipients.Single().EmailStatus);
        Assert.Equal("sent", detail.Recipients.Single().SmsStatus); // 普通通知没有「密码在邮件里」这个前提，短信照发
        Assert.Equal("partial_failed", detail.Result);
        Assert.DoesNotContain("密码没有改动", detail.Recipients.Single().Note); // 没发密码，就不必提密码
        Assert.Single(_sms.Sent);
    }

    [Fact]
    public async Task 自建模板_已登录的员工不需要确认_也不会被踢下线()
    {
        var emma = await SeedUserAsync("emma", "emma@example.com", lastLoginAt: DateTime.UtcNow.AddDays(-1));
        await SeedRefreshTokenAsync(emma.UserGUID);
        var template = await CreateGeneralTemplateAsync("Roster notice", "Hi {{name}}");

        var preflight = await CreateService().PreflightAsync(new StaffNotificationPreflightRequestDto
        {
            TemplateKey = template.Key,
            UserGuids = { emma.UserGUID },
            SendEmail = true,
        });
        Assert.Equal("ready", preflight.Data!.Recipients.Single().Status);

        var sent = await CreateService().SendAsync(new StaffNotificationSendRequestDto { TemplateKey = template.Key, UserGuids = { emma.UserGUID }, SendEmail = true }, "admin");
        Assert.True(sent.Success);
        await RunDispatcherAsync();

        Assert.False((await _db.Queryable<RefreshToken>().SingleAsync()).IsRevoked);
    }

    // ───────────── 启动恢复与后台任务 ─────────────

    [Fact]
    public async Task 启动恢复_上次正在发送的行标成失败_运行中的批次放回队列()
    {
        var batch = new StaffNotificationBatch
        {
            TemplateKey = StaffNotificationTemplateDefaults.AccountActivatedKey,
            TemplateName = "账号已开通",
            CreatedBy = "admin",
            CreatedByUserGuid = _admin.UserGUID,
            SendEmail = true,
            Status = StaffNotificationBatchStatuses.Running,
            TotalCount = 2,
        };
        await _db.Insertable(batch).ExecuteCommandAsync();
        var interrupted = new StaffNotificationRecipient { BatchId = batch.Id, UserGuid = "u1", DisplayName = "A", EmailStatus = StaffNotificationChannelStatuses.Sending, SmsStatus = StaffNotificationChannelStatuses.Pending };
        var untouched = new StaffNotificationRecipient { BatchId = batch.Id, UserGuid = "u2", DisplayName = "B", EmailStatus = StaffNotificationChannelStatuses.Pending, SortOrder = 1 };
        await _db.Insertable(new[] { interrupted, untouched }).ExecuteCommandAsync();

        await CreateDispatcher().RecoverAsync(CancellationToken.None);

        var first = await _db.Queryable<StaffNotificationRecipient>().FirstAsync(item => item.Id == interrupted.Id);
        Assert.Equal(StaffNotificationChannelStatuses.Failed, first.EmailStatus);
        Assert.Equal("INTERRUPTED", first.EmailError);
        Assert.Equal(StaffNotificationChannelStatuses.Pending, first.SmsStatus);
        Assert.Contains("结果未知", first.Note);
        var second = await _db.Queryable<StaffNotificationRecipient>().FirstAsync(item => item.Id == untouched.Id);
        Assert.Equal(StaffNotificationChannelStatuses.Pending, second.EmailStatus); // 没领取过的行不动，之后继续发
        Assert.Equal(StaffNotificationBatchStatuses.Queued, (await _db.Queryable<StaffNotificationBatch>().SingleAsync()).Status);
    }

    [Fact]
    public async Task 后台任务_节拍处理排队批次_首个节拍先做启动恢复()
    {
        var mia = await SeedUserAsync("mia", "mia@example.com", phone: "0491 570 156");
        var sent = await CreateService().SendAsync(SendRequest(mia.UserGUID), "admin");
        var provider = new ServiceCollection().AddScoped(_ => CreateDispatcher()).BuildServiceProvider();
        var worker = new StaffNotificationBackgroundService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new StaffNotificationSignal(),
            Options.Create(new ScheduledTaskOptions { Enabled = true }),
            NullLogger<StaffNotificationBackgroundService>.Instance
        );

        var wait = await worker.TickAsync(CancellationToken.None);

        Assert.Equal(worker.IdleWait, wait);
        Assert.Equal("completed", (await CreateService().GetBatchAsync(sent.Data!.BatchId)).Data!.Status);
    }

    [Fact]
    public async Task 后台任务_节拍里出错_不抛出_按30秒退避()
    {
        var provider = new ServiceCollection()
            .AddScoped<StaffNotificationDispatcher>(_ => throw new InvalidOperationException("表不存在"))
            .BuildServiceProvider();
        var worker = new StaffNotificationBackgroundService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new StaffNotificationSignal(),
            Options.Create(new ScheduledTaskOptions { Enabled = true }),
            NullLogger<StaffNotificationBackgroundService>.Instance
        );

        var wait = await worker.TickAsync(CancellationToken.None);

        Assert.Equal(StaffNotificationBackgroundService.FailureBackoff, wait);
    }

    [Fact]
    public async Task 后台任务_ScheduledTasks关闭的进程_不处理批次()
    {
        var mia = await SeedUserAsync("mia", "mia@example.com");
        await CreateService().SendAsync(SendRequest(mia.UserGUID), "admin");
        var provider = new ServiceCollection().AddScoped(_ => CreateDispatcher()).BuildServiceProvider();
        var worker = new StaffNotificationBackgroundService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new StaffNotificationSignal(),
            Options.Create(new ScheduledTaskOptions { Enabled = false }),
            NullLogger<StaffNotificationBackgroundService>.Instance
        ) { StartupDelay = TimeSpan.Zero };

        await worker.StartAsync(CancellationToken.None);
        await Task.Delay(150);
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(StaffNotificationBatchStatuses.Queued, (await _db.Queryable<StaffNotificationBatch>().SingleAsync()).Status);
        Assert.Empty(_email.Rendered);
    }

    [Fact]
    public async Task 后台任务_收到信号立刻处理_不用等轮询()
    {
        var mia = await SeedUserAsync("mia", "mia@example.com");
        var signal = new StaffNotificationSignal();
        var provider = new ServiceCollection().AddScoped(_ => CreateDispatcher()).BuildServiceProvider();
        var worker = new StaffNotificationBackgroundService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            signal,
            Options.Create(new ScheduledTaskOptions { Enabled = true }),
            NullLogger<StaffNotificationBackgroundService>.Instance
        ) { StartupDelay = TimeSpan.Zero, IdleWait = TimeSpan.FromMinutes(10) };

        await worker.StartAsync(CancellationToken.None);
        await Task.Delay(300); // 第一个节拍：空队列，之后睡 10 分钟
        var sent = await CreateService().SendAsync(SendRequest(mia.UserGUID), "admin");
        signal.Notify();

        // 后台线程在用 _db 的连接写库：轮询改用独立连接，避免两个线程抢同一个 SQLite 连接。
        using var pollConnection = new SqliteConnection($"Data Source={_dbPath}");
        using var pollDb = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = pollConnection.ConnectionString,
            DbType = DbType.Sqlite,
            IsAutoCloseConnection = true,
            InitKeyType = InitKeyType.Attribute,
        });
        var batchId = sent.Data!.BatchId;
        var completed = false;
        for (var i = 0; i < 50 && !completed; i++)
        {
            await Task.Delay(100);
            completed = (await pollDb.Queryable<StaffNotificationBatch>().FirstAsync(item => item.Id == batchId))?.Status == StaffNotificationBatchStatuses.Completed;
        }

        await worker.StopAsync(CancellationToken.None);
        Assert.True(completed, "收到信号后应立刻处理完排队批次");
    }

    // ───────────── 批次记录：汇总、可见范围、筛选 ─────────────

    [Fact]
    public void 汇总_邮件4比5_短信1比3_与设计图一致_被扣住的短信计入原本要发的条数()
    {
        var batch = new StaffNotificationBatch { Status = StaffNotificationBatchStatuses.Completed, TotalCount = 5 };
        StaffNotificationRecipient Row(string email, string sms, string? smsError = null, int segments = 0) => new()
        {
            EmailStatus = email, SmsStatus = sms, SmsError = smsError, SmsSegments = segments,
        };
        var rows = new[]
        {
            Row("sent", "sent", segments: 1),                                                   // Mia
            Row("sent", "failed"),                                                              // Jack：短信限流
            Row("sent", "skipped"),                                                             // Sophie：没手机号
            Row("sent", "skipped"),                                                             // Liam：固定电话
            Row("failed", "skipped", StaffNotificationService.SmsHeldByEmailFailure),           // Emma：邮件失败，短信被扣住
        };

        var summary = StaffNotificationService.Summarize(batch, rows);

        Assert.Equal(5, summary.EmailRequested);
        Assert.Equal(4, summary.EmailSent);
        Assert.Equal(1, summary.EmailFailed);
        Assert.Equal(3, summary.SmsRequested);
        Assert.Equal(1, summary.SmsSent);
        Assert.Equal(1, summary.SmsFailed);
        Assert.Equal("partial_failed", summary.Result);
        Assert.True(summary.CanRetry);
        Assert.Equal(1, summary.SmsSegments);
    }

    [Fact]
    public void 汇总_没结束是running_全部成功是all_sent_有失败但没有成功是all_failed()
    {
        var sentRow = new[] { new StaffNotificationRecipient { EmailStatus = "sent", SmsStatus = "none" } };
        var failedRow = new[] { new StaffNotificationRecipient { EmailStatus = "failed", SmsStatus = "none" } };

        Assert.Equal("running", StaffNotificationService.Summarize(new StaffNotificationBatch { Status = "running" }, sentRow).Result);
        Assert.Equal("running", StaffNotificationService.Summarize(new StaffNotificationBatch { Status = "queued" }, sentRow).Result);
        Assert.Equal("all_sent", StaffNotificationService.Summarize(new StaffNotificationBatch { Status = "completed" }, sentRow).Result);
        Assert.Equal("all_failed", StaffNotificationService.Summarize(new StaffNotificationBatch { Status = "completed" }, failedRow).Result);
        Assert.False(StaffNotificationService.Summarize(new StaffNotificationBatch { Status = "running" }, failedRow).CanRetry); // 没结束不能重试
    }

    [Fact]
    public async Task 记录_管理员看全部_非管理员只能看到自己发出的批次()
    {
        var manager = await SeedStoreManagerAsync();
        var adminBatch = await InsertBatchAsync(_admin.UserGUID, "admin", DateTime.UtcNow);
        var managerBatch = await InsertBatchAsync(manager.UserGUID, "manager", DateTime.UtcNow);

        Assert.Equal(2, (await CreateService().ListBatchesAsync(new StaffNotificationBatchQueryDto())).Data!.Total);

        _scope.UserGuid = manager.UserGUID;
        var list = (await CreateService().ListBatchesAsync(new StaffNotificationBatchQueryDto())).Data!;
        Assert.Equal(1, list.Total);
        Assert.Equal(managerBatch, list.Items!.Single().Id);
        Assert.Equal(StaffNotificationService.BatchNotFoundCode, (await CreateService().GetBatchAsync(adminBatch)).ErrorCode);
        Assert.Equal(StaffNotificationService.BatchNotFoundCode, (await CreateService().RetryAsync(adminBatch, "manager")).ErrorCode);
        Assert.True((await CreateService().GetBatchAsync(managerBatch)).Success);
    }

    [Fact]
    public async Task 预检_店长只能发给本店店员_外店和非店员角色都被拦下且不泄露资料()
    {
        var manager = await SeedStoreManagerAsync();
        var staffRole = new Role { RoleGUID = Guid.NewGuid().ToString(), RoleName = "StoreStaff", IsActive = true };
        await _db.Insertable(staffRole).ExecuteCommandAsync();

        var ownStaff = await SeedUserAsync("own.staff", "own.staff@example.com", store: "Sunnybank");
        var otherStoreStaff = await SeedUserAsync("other.staff", "other.staff@example.com", store: "Chermside");
        await _db.Insertable(new[]
        {
            new UserRole { UserGUID = ownStaff.UserGUID, RoleGUID = staffRole.RoleGUID },
            new UserRole { UserGUID = otherStoreStaff.UserGUID, RoleGUID = staffRole.RoleGUID },
        }).ExecuteCommandAsync();
        // 挂在本店、但不是店员角色的账号：现有重置入口管不到，这里也不能管。
        var ownNonStaff = await SeedUserAsync("own.other", "own.other@example.com", store: "Sunnybank");

        _scope.UserGuid = manager.UserGUID;
        var result = await CreateService().PreflightAsync(new StaffNotificationPreflightRequestDto
        {
            TemplateKey = StaffNotificationTemplateDefaults.AccountActivatedKey,
            UserGuids = new List<string> { ownStaff.UserGUID, otherStoreStaff.UserGUID, ownNonStaff.UserGUID, manager.UserGUID },
            SendEmail = true,
        });

        var rows = result.Data!.Recipients.ToDictionary(item => item.UserGuid);
        Assert.Equal("ready", rows[ownStaff.UserGUID].Status);
        Assert.Equal("own.staff@example.com", rows[ownStaff.UserGUID].Email);
        foreach (var forbidden in new[] { otherStoreStaff, ownNonStaff })
        {
            var row = rows[forbidden.UserGUID];
            Assert.Equal("blocked", row.Status);
            Assert.Contains("forbidden", row.Reasons);
            Assert.True(string.IsNullOrEmpty(row.Email));
            Assert.True(string.IsNullOrEmpty(row.LoginName));
        }
        Assert.Contains("self", rows[manager.UserGUID].Reasons);
    }

    [Fact]
    public async Task 发送_店长给本店店员发送_批次只有操作人自己看得到()
    {
        var manager = await SeedStoreManagerAsync();
        var staffRole = new Role { RoleGUID = Guid.NewGuid().ToString(), RoleName = "StoreStaff", IsActive = true };
        await _db.Insertable(staffRole).ExecuteCommandAsync();
        var staff = await SeedUserAsync("own.staff", "own.staff@example.com", store: "Sunnybank");
        await _db.Insertable(new UserRole { UserGUID = staff.UserGUID, RoleGUID = staffRole.RoleGUID }).ExecuteCommandAsync();

        _scope.UserGuid = manager.UserGUID;
        var sent = await CreateService().SendAsync(new StaffNotificationSendRequestDto
        {
            TemplateKey = StaffNotificationTemplateDefaults.AccountActivatedKey,
            UserGuids = new List<string> { staff.UserGUID },
            SendEmail = true,
        }, "manager");

        Assert.True(sent.Success, sent.Message);
        Assert.Equal(1, sent.Data!.TotalCount);
        Assert.Equal(1, (await CreateService().ListBatchesAsync(new StaffNotificationBatchQueryDto())).Data!.Total);
    }

    [Fact]
    public async Task 记录_按模板渠道关键词日期结果筛选_并分页()
    {
        var sydneyNow = DateTime.UtcNow;
        var today = await InsertBatchAsync(_admin.UserGUID, "admin", sydneyNow, recipientName: "Mia Chen", emailStatus: "sent", smsStatus: "failed", sendSms: true);
        var old = await InsertBatchAsync(_admin.UserGUID, "emma.t", sydneyNow.AddDays(-10), recipientName: "Noah Lee", emailStatus: "sent", smsStatus: "none", sendSms: false);
        var general = await InsertBatchAsync(_admin.UserGUID, "admin", sydneyNow.AddDays(-1), recipientName: "Zoe Martin", emailStatus: "sent", smsStatus: "none", sendSms: false, templateKey: "custom-1");
        var service = CreateService();

        Assert.Equal(3, (await service.ListBatchesAsync(new StaffNotificationBatchQueryDto())).Data!.Total);
        Assert.Equal(new[] { general }, (await service.ListBatchesAsync(new StaffNotificationBatchQueryDto { TemplateKey = "custom-1" })).Data!.Items!.Select(item => item.Id));
        Assert.Equal(new[] { today }, (await service.ListBatchesAsync(new StaffNotificationBatchQueryDto { Channel = "sms" })).Data!.Items!.Select(item => item.Id));
        Assert.Equal(new[] { old }, (await service.ListBatchesAsync(new StaffNotificationBatchQueryDto { Keyword = "emma" })).Data!.Items!.Select(item => item.Id)); // 匹配操作人
        Assert.Equal(new[] { general }, (await service.ListBatchesAsync(new StaffNotificationBatchQueryDto { Keyword = "Zoe" })).Data!.Items!.Select(item => item.Id)); // 匹配收件员工
        Assert.Equal(new[] { today }, (await service.ListBatchesAsync(new StaffNotificationBatchQueryDto { Result = "partial_failed" })).Data!.Items!.Select(item => item.Id));
        Assert.Equal(2, (await service.ListBatchesAsync(new StaffNotificationBatchQueryDto { Result = "all_sent" })).Data!.Total);

        var sydneyToday = TimeZoneInfo.ConvertTimeFromUtc(sydneyNow, TimeZoneInfo.FindSystemTimeZoneById("Australia/Sydney")).ToString("yyyy-MM-dd");
        var fromToday = (await service.ListBatchesAsync(new StaffNotificationBatchQueryDto { From = sydneyToday })).Data!;
        Assert.Equal(new[] { today }, fromToday.Items!.Select(item => item.Id));

        var page = (await service.ListBatchesAsync(new StaffNotificationBatchQueryDto { Page = 2, PageSize = 2 })).Data!;
        Assert.Equal(3, page.Total);
        Assert.Single(page.Items!);
        Assert.Equal(old, page.Items!.Single().Id); // 按创建时间倒序，最老的在第二页
    }

    // ───────────── 模板 ─────────────

    [Fact]
    public async Task 模板_系统模板排第一_改文案后标记已自定义_恢复默认后回到出厂状态()
    {
        var service = CreateService();
        var listed = (await service.ListTemplatesAsync()).Data!;
        var system = listed.Single();
        Assert.Equal(StaffNotificationTemplateDefaults.AccountActivatedKey, system.Key);
        Assert.True(system.IssuesPassword);
        Assert.True(system.RequiresEmail);
        Assert.False(system.IsCustomized);

        var save = new StaffNotificationTemplateSaveDto
        {
            Name = "Renamed by admin", // 系统模板名称固定，传了也不改
            IsEnabled = true,
            EmailSubject = "Welcome to Hot Bargain",
            EmailBody = system.EmailBody.Replace("Hi {{name}},", "Hello {{name}},"),
            SmsBody = system.SmsBody,
        };
        var updated = await service.UpdateTemplateAsync(system.Key, save, "admin");
        Assert.True(updated.Success);
        Assert.Equal("账号已开通", updated.Data!.Name);
        Assert.Equal("Welcome to Hot Bargain", updated.Data.EmailSubject);
        Assert.True(updated.Data.IsCustomized);
        Assert.Contains("Hello {{name}},", (await service.ListTemplatesAsync()).Data!.Single().EmailBody);

        var reset = await service.ResetTemplateAsync(system.Key, "admin");
        Assert.True(reset.Success);
        Assert.False(reset.Data!.IsCustomized);
        Assert.Equal("Your Hot Bargain staff app account is ready", reset.Data.EmailSubject);
        Assert.False(await _db.Queryable<StaffNotificationTemplate>().AnyAsync());
    }

    [Fact]
    public async Task 模板_校验失败时message是拼好的中文原因_不落库()
    {
        var service = CreateService();
        var system = (await service.ListTemplatesAsync()).Data!.Single();

        var result = await service.UpdateTemplateAsync(system.Key, new StaffNotificationTemplateSaveDto
        {
            Name = "x",
            EmailSubject = "你好",
            EmailBody = "No box here",
            SmsBody = "Don’t",
        }, "admin");

        Assert.False(result.Success);
        Assert.Equal(StaffNotificationTemplateService.TemplateInvalidCode, result.ErrorCode);
        Assert.Contains("login_box", result.Message);
        Assert.Contains("中文", result.Message);
        Assert.Contains("；", result.Message);
        Assert.False(await _db.Queryable<StaffNotificationTemplate>().AnyAsync());
    }

    [Fact]
    public async Task 模板_自建_重名不允许_可以改名_不能恢复默认()
    {
        var service = CreateService();
        var created = await CreateGeneralTemplateAsync("Roster notice", "Hi {{name}}");

        var duplicate = await service.CreateTemplateAsync(NewTemplate("roster notice"), "admin"); // 大小写不敏感
        Assert.Equal(StaffNotificationTemplateService.TemplateNameDuplicateCode, duplicate.ErrorCode);
        var clashWithSystem = await service.CreateTemplateAsync(NewTemplate("账号已开通"), "admin");
        Assert.Equal(StaffNotificationTemplateService.TemplateNameDuplicateCode, clashWithSystem.ErrorCode);

        var renamed = await service.UpdateTemplateAsync(created.Key, NewTemplate("Roster update"), "admin");
        Assert.True(renamed.Success);
        Assert.Equal("Roster update", renamed.Data!.Name);
        Assert.Equal("general", renamed.Data.Kind);
        Assert.False(renamed.Data.IssuesPassword);

        Assert.False((await service.ResetTemplateAsync(created.Key, "admin")).Success);
        Assert.Equal(StaffNotificationTemplateService.TemplateNotFoundCode, (await service.UpdateTemplateAsync("nope", NewTemplate("x"), "admin")).ErrorCode);
    }

    // ───────────── 公开基址与链接 ─────────────

    [Theory]
    [InlineData("https://hotbargain.vip", "https://hotbargain.vip")]
    [InlineData("https://hotbargain.vip/", "https://hotbargain.vip")]
    [InlineData("https://staging.example.com:8443", "https://staging.example.com:8443")]
    [InlineData("http://hotbargain.vip", "https://hotbargain.vip")]               // 只收 https
    [InlineData("https://hotbargain.vip/app", "https://hotbargain.vip")]          // 带路径回退
    [InlineData("https://evil.com?x=1", "https://hotbargain.vip")]
    [InlineData("https://user:pw@hotbargain.vip", "https://hotbargain.vip")]
    [InlineData("", "https://hotbargain.vip")]
    [InlineData("not a url", "https://hotbargain.vip")]
    public void 公开基址_必须是纯https域名_否则回退默认值(string configured, string expected)
    {
        Assert.Equal(expected, new StaffNotificationOptions { PublicBaseUrl = configured }.ResolveBaseUrl());
    }

    [Theory]
    [InlineData("https://apps.apple.com/au/app/hbgroup/id6786073002?uo=4", true)]
    [InlineData("https://itunes.apple.com/au/app/id1", true)]
    [InlineData("http://apps.apple.com/au/app/id1", false)]
    [InlineData("https://apps.apple.com.evil.com/x", false)]
    [InlineData("https://evil.com/apps.apple.com", false)]
    [InlineData("", false)]
    public void iOS商店链接_只放行https的apple域名(string url, bool expected)
    {
        Assert.Equal(expected, StaffNotificationLinkProvider.IsTrustedStoreUrl(url));
    }

    // ───────────── 独立审查补充：越权、并发、重试保护、异常恢复 ─────────────

    [Fact]
    public async Task 预检_仓库经理对管理员_只返回被拦下_不泄露登录名邮箱手机号和登录时间()
    {
        var warehouseManager = await SeedWarehouseManagerAsync();
        var boss = await SeedAdminUserAsync("boss", "boss@hotbargain.com.au", phone: "0491 570 999", lastLoginAt: DateTime.UtcNow.AddHours(-1));
        var staff = await SeedUserAsync("mia", "mia@example.com", phone: "0491 570 156");
        _scope.UserGuid = warehouseManager.UserGUID;

        var result = await CreateService().PreflightAsync(Preflight(boss.UserGUID, staff.UserGUID));

        Assert.True(result.Success, result.Message);
        var protectedRow = result.Data!.Recipients[0];
        Assert.Equal(boss.UserGUID, protectedRow.UserGuid);
        Assert.Equal("blocked", protectedRow.Status);
        Assert.Equal(new[] { "forbidden" }, protectedRow.Reasons.ToArray());
        // 越权目标的任何资料都不能出现在预检里（仓库经理本来就看不到管理员账号）。
        Assert.Equal(string.Empty, protectedRow.DisplayName);
        Assert.Equal(string.Empty, protectedRow.LoginName);
        Assert.Null(protectedRow.Email);
        Assert.Null(protectedRow.Phone);
        Assert.Null(protectedRow.StoreName);
        Assert.Null(protectedRow.LastLoginAtUtc);
        Assert.False(protectedRow.HasLoggedIn);
        Assert.False(protectedRow.WillSendEmail);
        Assert.False(protectedRow.WillSendSms);
        Assert.Equal("ready", result.Data.Recipients[1].Status); // 普通员工仍然可发

        var preview = await CreateService().PreviewAsync(new StaffNotificationPreviewRequestDto
        {
            TemplateKey = StaffNotificationTemplateDefaults.AccountActivatedKey,
            UserGuid = boss.UserGUID,
        });
        Assert.Equal(StaffNotificationService.ForbiddenCode, preview.ErrorCode);

        var send = await CreateService().SendAsync(SendRequest(boss.UserGUID, staff.UserGUID), "warehouse");
        Assert.True(send.Success, send.Message);
        Assert.Equal(boss.UserGUID, Assert.Single(send.Data!.Rejected).UserGuid);
        Assert.Equal("forbidden", send.Data.Rejected.Single().Reason);
        Assert.Equal(1, send.Data.TotalCount);
    }

    [Fact]
    public async Task 记录_只有管理员看全部_仓库经理也只能看到自己发出的批次()
    {
        var warehouseManager = await SeedWarehouseManagerAsync();
        var adminBatch = await InsertBatchAsync(_admin.UserGUID, "admin", DateTime.UtcNow, recipientName: "Mia Chen");
        var ownBatch = await InsertBatchAsync(warehouseManager.UserGUID, "warehouse", DateTime.UtcNow);

        _scope.UserGuid = warehouseManager.UserGUID;
        var list = (await CreateService().ListBatchesAsync(new StaffNotificationBatchQueryDto())).Data!;

        Assert.Equal(ownBatch, Assert.Single(list.Items!).Id);
        Assert.Equal(StaffNotificationService.BatchNotFoundCode, (await CreateService().GetBatchAsync(adminBatch)).ErrorCode);
    }

    [Fact]
    public async Task 发送_并发两次点击_只会建一个批次_另一次被拒绝_不会重复重置密码()
    {
        var mia = await SeedUserAsync("mia", "mia@example.com", phone: "0491 570 156");
        // 第二个请求用独立连接：模拟两个并发的 HTTP 请求各自拿到自己的数据库连接。
        using var db2 = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = $"Data Source={_dbPath}",
            DbType = DbType.Sqlite,
            IsAutoCloseConnection = true,
            InitKeyType = InitKeyType.Attribute,
        });
        var context2 = CreateContext(db2);
        var second = new StaffNotificationService(
            context2, _scope, new StaffNotificationTemplateService(context2), new FakeLinks(), _sms,
            SmsConfigured(), _signal, NullLogger<StaffNotificationService>.Instance
        );

        var results = await Task.WhenAll(
            Task.Run(() => CreateService().SendAsync(SendRequest(mia.UserGUID), "admin")),
            Task.Run(() => second.SendAsync(SendRequest(mia.UserGUID), "admin"))
        );

        Assert.Equal(1, results.Count(item => item.Success));
        Assert.Equal(StaffNotificationService.NoRecipientsCode, results.Single(item => !item.Success).ErrorCode);
        Assert.Single(await _db.Queryable<StaffNotificationBatch>().ToListAsync());
        Assert.Single(await _db.Queryable<StaffNotificationRecipient>().ToListAsync());
    }

    [Fact]
    public async Task 重试_员工在这批发送之后已经登录过_不自动重试_说明原因_要求重新发送确认()
    {
        var mia = await SeedUserAsync("mia", "mia@example.com", phone: "0491 570 156");
        _email.FailFor.Add("mia@example.com");
        var service = CreateService();
        var sent = await service.SendAsync(SendRequest(mia.UserGUID), "admin");
        await RunDispatcherAsync();
        _email.FailFor.Clear();
        // 例如「结果未知」的那封其实已送达，员工用它登录了：这时重试会把他踢下线，必须走新发送的二次确认。
        await _db.Updateable<User>().SetColumns(item => item.LastLoginAt == DateTime.UtcNow.AddMinutes(1)).Where(item => item.UserGUID == mia.UserGUID).ExecuteCommandAsync();
        var oldHash = await PasswordHashOfAsync(mia.UserGUID);

        var retry = await service.RetryAsync(sent.Data!.BatchId, "admin");

        Assert.False(retry.Success);
        Assert.Equal("NOTHING_TO_RETRY", retry.ErrorCode);
        var detail = (await service.GetBatchAsync(sent.Data.BatchId)).Data!;
        Assert.Equal("completed", detail.Status);
        Assert.Equal("failed", detail.Recipients.Single().EmailStatus);
        Assert.Contains("已经登录过", detail.Recipients.Single().Note);
        await RunDispatcherAsync();
        Assert.Empty(_email.Rendered);
        Assert.Equal(oldHash, await PasswordHashOfAsync(mia.UserGUID));
    }

    [Fact]
    public async Task 重试_管理员重试自己那一行_被拒绝_不会把自己踢下线()
    {
        var batchId = await InsertBatchAsync(_admin.UserGUID, "admin", DateTime.UtcNow.AddMinutes(-5), recipientName: "Admin", emailStatus: "failed");
        var row = await _db.Queryable<StaffNotificationRecipient>().SingleAsync(item => item.BatchId == batchId);
        row.UserGuid = _admin.UserGUID;
        await _db.Updateable(row).ExecuteCommandAsync();
        var oldHash = await PasswordHashOfAsync(_admin.UserGUID);

        var retry = await CreateService().RetryAsync(batchId, "admin");

        Assert.Equal("NOTHING_TO_RETRY", retry.ErrorCode);
        var after = await _db.Queryable<StaffNotificationRecipient>().SingleAsync(item => item.BatchId == batchId);
        Assert.Equal("failed", after.EmailStatus);
        Assert.Contains("不能给自己", after.Note);
        Assert.Equal(oldHash, await PasswordHashOfAsync(_admin.UserGUID));
    }

    [Fact]
    public async Task 重试_这批之后员工已被另一批重新发放过密码_不重试_避免先到的密码被作废()
    {
        var mia = await SeedUserAsync("mia", "mia@example.com", phone: "0491 570 156");
        _email.FailFor.Add("mia@example.com");
        var service = CreateService();
        var batchA = await service.SendAsync(SendRequest(mia.UserGUID), "admin");
        await RunDispatcherAsync();
        _email.FailFor.Clear();
        await Task.Delay(30); // 让批次 B 的创建时间晚于 A
        var batchB = await service.SendAsync(SendRequest(mia.UserGUID), "admin");
        await RunDispatcherAsync();
        var hashAfterB = await PasswordHashOfAsync(mia.UserGUID);
        var mailsAfterB = _email.Rendered.Count;

        var retry = await service.RetryAsync(batchA.Data!.BatchId, "admin");

        Assert.Equal("NOTHING_TO_RETRY", retry.ErrorCode);
        Assert.Contains("重新发放过", (await service.GetBatchAsync(batchA.Data.BatchId)).Data!.Recipients.Single().Note);
        await RunDispatcherAsync();
        Assert.Equal(mailsAfterB, _email.Rendered.Count);
        Assert.Equal(hashAfterB, await PasswordHashOfAsync(mia.UserGUID));
        Assert.True(batchB.Success);
    }

    [Fact]
    public async Task 重试_员工已在另一个进行中的批次里_不重试()
    {
        var mia = await SeedUserAsync("mia", "mia@example.com");
        _email.FailFor.Add("mia@example.com");
        var service = CreateService();
        var batchA = await service.SendAsync(SendRequest(mia.UserGUID), "admin");
        await RunDispatcherAsync();
        _email.FailFor.Clear();
        var batchB = await service.SendAsync(SendRequest(mia.UserGUID), "admin"); // 排队中，还没处理
        Assert.True(batchB.Success);

        var retry = await service.RetryAsync(batchA.Data!.BatchId, "admin");

        Assert.Equal("NOTHING_TO_RETRY", retry.ErrorCode);
        Assert.Contains("进行中的批次", (await service.GetBatchAsync(batchA.Data.BatchId)).Data!.Recipients.Single().Note);
    }

    [Fact]
    public async Task 重试_只补短信不动密码_不受登录过的限制()
    {
        var mia = await SeedUserAsync("mia", "mia@example.com", phone: "0491 570 156");
        _sms.Handler = _ => ApiResponse<bool>.Error("该手机号短信发送过于频繁，请稍后再试", SmsErrorCodes.PhoneRateLimited);
        var service = CreateService();
        var sent = await service.SendAsync(SendRequest(mia.UserGUID), "admin");
        await RunDispatcherAsync();
        await _db.Updateable<User>().SetColumns(item => item.LastLoginAt == DateTime.UtcNow.AddMinutes(1)).Where(item => item.UserGUID == mia.UserGUID).ExecuteCommandAsync();
        _sms.Handler = null;

        var retry = await service.RetryAsync(sent.Data!.BatchId, "admin");

        Assert.True(retry.Success, retry.Message); // 密码不会变，员工登录与否都无所谓
        await RunDispatcherAsync();
        Assert.Equal("all_sent", (await service.GetBatchAsync(sent.Data.BatchId)).Data!.Result);
    }

    [Fact]
    public async Task 后台任务_领取批次后抛异常_下个节拍重新恢复_批次不会永远停在running()
    {
        var mia = await SeedUserAsync("mia", "mia@example.com", phone: "0491 570 156");
        var sent = await CreateService().SendAsync(SendRequest(mia.UserGUID), "admin");
        var links = new FlakyLinks { Fail = true };
        var provider = new ServiceCollection().AddScoped(_ => CreateDispatcher(links)).BuildServiceProvider();
        var worker = new StaffNotificationBackgroundService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new StaffNotificationSignal(),
            Options.Create(new ScheduledTaskOptions { Enabled = true }),
            NullLogger<StaffNotificationBackgroundService>.Instance
        );

        // 第一个节拍：批次已被领取（running），随后读链接时抛异常。
        Assert.Equal(StaffNotificationBackgroundService.FailureBackoff, await worker.TickAsync(CancellationToken.None));
        Assert.Equal(StaffNotificationBatchStatuses.Running, (await _db.Queryable<StaffNotificationBatch>().SingleAsync()).Status);
        Assert.Empty(_email.Rendered);

        // 故障恢复后的下一个节拍必须重新做启动恢复（running → queued），然后把批次发完。
        links.Fail = false;
        await worker.TickAsync(CancellationToken.None);

        Assert.Equal("completed", (await CreateService().GetBatchAsync(sent.Data!.BatchId)).Data!.Status);
        Assert.Single(_email.Rendered);
    }

    [Fact]
    public void 随机密码对象的ToString不带密码_以后被用来打日志也不会泄露()
    {
        var text = new GeneratedPasswordMail("a@example.com", "A", "a", "s3cretPw").ToString();

        Assert.DoesNotContain("s3cretPw", text);
    }

    // ───────────── 夹具 ─────────────

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        SqliteTempFileCleanup.DeleteIfExists(_dbPath);
    }

    private static StaffNotificationPreflightRequestDto Preflight(params string[] guids) => new()
    {
        TemplateKey = StaffNotificationTemplateDefaults.AccountActivatedKey,
        UserGuids = guids.ToList(),
        SendEmail = true,
        SendSms = true,
    };

    private static StaffNotificationSendRequestDto SendRequest(params string[] guids) => new()
    {
        TemplateKey = StaffNotificationTemplateDefaults.AccountActivatedKey,
        UserGuids = guids.ToList(),
        SendEmail = true,
        SendSms = true,
    };

    private static StaffNotificationTemplateSaveDto NewTemplate(string name, string? smsBody = null) => new()
    {
        Name = name,
        IsEnabled = true,
        EmailSubject = "Roster update",
        EmailBody = "Hi {{name}},\n\nYour roster has been updated.",
        SmsBody = smsBody ?? string.Empty,
    };

    private async Task<StaffNotificationTemplateDto> CreateGeneralTemplateAsync(string name, string smsBody, bool enabled = true)
    {
        var dto = NewTemplate(name, smsBody);
        dto.IsEnabled = enabled;
        var result = await CreateService().CreateTemplateAsync(dto, "admin");
        Assert.True(result.Success, result.Message);
        return result.Data!;
    }

    /// <summary>与生产一致：短信可用意味着配置了默认发件名；这里另外开放 ANTPOS 作为可选发件名。</summary>
    private static IOptions<SmsOptions> SmsConfigured() =>
        Options.Create(new SmsOptions { From = "HotBargain", Senders = "ANTPOS", GlobalDayLimit = 300 });

    private IStaffNotificationService CreateService() => new StaffNotificationService(
        _context,
        _scope,
        new StaffNotificationTemplateService(_context),
        new FakeLinks(),
        _sms,
        SmsConfigured(),
        _signal,
        NullLogger<StaffNotificationService>.Instance
    );

    private StaffNotificationDispatcher CreateDispatcher(IStaffNotificationLinkProvider? links = null) => new(
        _context,
        new PasswordResetService(_context, _email, NullLogger<PasswordResetService>.Instance),
        _email,
        _sms,
        new StaffNotificationTemplateService(_context),
        links ?? new FakeLinks(),
        NullLogger<StaffNotificationDispatcher>.Instance
    );

    private Task<int> RunDispatcherAsync() => CreateDispatcher().RunPendingAsync(CancellationToken.None);

    private async Task<string> PasswordHashOfAsync(string userGuid) =>
        (await _db.Queryable<User>().FirstAsync(item => item.UserGUID == userGuid)).PasswordHash;

    private async Task<User> SeedAdminAsync()
    {
        var role = new Role { RoleGUID = Guid.NewGuid().ToString(), RoleName = "Admin", IsActive = true };
        await _db.Insertable(role).ExecuteCommandAsync();
        var admin = await SeedUserAsync("admin", "admin@hotbargain.com.au", fullName: "Admin");
        await _db.Insertable(new UserRole { UserGUID = admin.UserGUID, RoleGUID = role.RoleGUID }).ExecuteCommandAsync();
        return admin;
    }

    private async Task<User> SeedWarehouseManagerAsync()
    {
        var role = new Role { RoleGUID = Guid.NewGuid().ToString(), RoleName = "WarehouseManager", IsActive = true };
        await _db.Insertable(role).ExecuteCommandAsync();
        var manager = await SeedUserAsync("warehouse", "warehouse@hotbargain.com.au");
        await _db.Insertable(new UserRole { UserGUID = manager.UserGUID, RoleGUID = role.RoleGUID }).ExecuteCommandAsync();
        return manager;
    }

    /// <summary>再造一个管理员账号（复用已有的 Admin 角色）。</summary>
    private async Task<User> SeedAdminUserAsync(string username, string email, string? phone = null, DateTime? lastLoginAt = null)
    {
        var adminRole = await _db.Queryable<Role>().FirstAsync(item => item.RoleName == "Admin");
        var user = await SeedUserAsync(username, email, phone: phone, lastLoginAt: lastLoginAt);
        await _db.Insertable(new UserRole { UserGUID = user.UserGUID, RoleGUID = adminRole.RoleGUID }).ExecuteCommandAsync();
        return user;
    }

    private async Task<User> SeedStoreManagerAsync()
    {
        var role = new Role { RoleGUID = Guid.NewGuid().ToString(), RoleName = "StoreManager", IsActive = true };
        await _db.Insertable(role).ExecuteCommandAsync();
        var manager = await SeedUserAsync("manager", "manager@hotbargain.com.au", store: "Sunnybank");
        await _db.Insertable(new UserRole { UserGUID = manager.UserGUID, RoleGUID = role.RoleGUID }).ExecuteCommandAsync();
        return manager;
    }

    private async Task<User> SeedUserAsync(
        string username,
        string email,
        string? fullName = null,
        bool isActive = true,
        DateTime? lastLoginAt = null,
        string? phone = null,
        string? store = null,
        bool loginRestricted = false
    )
    {
        var user = new User
        {
            UserGUID = Guid.NewGuid().ToString(),
            Username = username,
            Email = email,
            PasswordHash = PasswordHasher.HashPassword("Secret123"),
            FullName = fullName ?? username,
            IsActive = isActive,
            IsDeleted = false,
            LastLoginAt = lastLoginAt,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        await _db.Insertable(user).ExecuteCommandAsync();

        if (phone is not null)
        {
            await _db.Insertable(new EmployeeProfile { UserGUID = user.UserGUID, Phone = phone }).ExecuteCommandAsync();
        }

        if (store is not null)
        {
            var existing = await _db.Queryable<Store>().FirstAsync(item => item.StoreName == store);
            if (existing is null)
            {
                existing = new Store { StoreGUID = Guid.NewGuid().ToString(), StoreName = store, StoreCode = "S" + Guid.NewGuid().ToString("N")[..4], IsActive = true };
                await _db.Insertable(existing).ExecuteCommandAsync();
            }

            await _db.Insertable(new UserStore { UserGUID = user.UserGUID, StoreGUID = existing.StoreGUID, IsPrimary = true }).ExecuteCommandAsync();
        }

        if (loginRestricted)
        {
            await _db.Insertable(new UserLoginRestriction { UserGUID = user.UserGUID, RestrictedAtUtc = DateTime.UtcNow }).ExecuteCommandAsync();
        }

        return user;
    }

    private async Task SeedRefreshTokenAsync(string userGuid)
    {
        await _db.Insertable(new RefreshToken
        {
            RefreshTokenGUID = Guid.NewGuid().ToString(),
            UserGUID = userGuid,
            Token = "token-" + Guid.NewGuid().ToString("N"),
            ExpiresAt = DateTime.UtcNow.AddDays(1),
            IsRevoked = false,
            IsDeleted = false,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        }).ExecuteCommandAsync();
    }

    /// <summary>直接插一个已完成的批次（含一个收件人），用来测记录页的可见范围与筛选。</summary>
    private async Task<string> InsertBatchAsync(
        string creatorGuid,
        string creatorName,
        DateTime createdAtUtc,
        string recipientName = "Someone",
        string emailStatus = "sent",
        string smsStatus = "none",
        bool sendSms = false,
        string templateKey = StaffNotificationTemplateDefaults.AccountActivatedKey
    )
    {
        var batch = new StaffNotificationBatch
        {
            TemplateKey = templateKey,
            TemplateName = templateKey == StaffNotificationTemplateDefaults.AccountActivatedKey ? "账号已开通" : "Custom",
            CreatedBy = creatorName,
            CreatedByUserGuid = creatorGuid,
            SendEmail = true,
            SendSms = sendSms,
            Status = StaffNotificationBatchStatuses.Completed,
            TotalCount = 1,
            CreatedAtUtc = createdAtUtc,
            FinishedAtUtc = createdAtUtc,
        };
        await _db.Insertable(batch).ExecuteCommandAsync();
        await _db.Insertable(new StaffNotificationRecipient
        {
            BatchId = batch.Id, UserGuid = Guid.NewGuid().ToString(), DisplayName = recipientName,
            EmailStatus = emailStatus, SmsStatus = smsStatus,
        }).ExecuteCommandAsync();
        return batch.Id;
    }

    private static SqlSugarContext CreateContext(ISqlSugarClient db)
    {
        var context = (SqlSugarContext)RuntimeHelpers.GetUninitializedObject(typeof(SqlSugarContext));
        typeof(SqlSugarContext).GetField("_db", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(context, db);
        return context;
    }

    private sealed class FakeScope : ICurrentUserManageableStoreScopeService
    {
        public string UserGuid { get; set; } = string.Empty;

        public Task<CurrentUserManageableStoreScope> GetScopeAsync() => Task.FromResult(new CurrentUserManageableStoreScope
        {
            IsAllowed = true,
            IsAuthenticated = true,
            UserGuid = UserGuid,
            ActorLabel = "test",
        });

        public Task<IReadOnlyList<string>> GetAccessibleStoreCodesAsync() => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        public Task<bool> CanAccessStoreCodeAsync(string storeCode) => Task.FromResult(true);
        public Task<bool> CanAccessOrderAsync(string orderGuid) => Task.FromResult(true);
        public Task<bool> CanManageStoreAsync(string storeGuid) => Task.FromResult(true);
        public Task<bool> CanManageUserAsync(string userGuid) => Task.FromResult(true);
    }

    private sealed class FakeEmailSender : IAccountPasswordEmailSender
    {
        public List<(string To, string Subject, string Text, string Html)> Rendered { get; } = new();
        public HashSet<string> FailFor { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Task<ApiResponse<bool>> SendRenderedAsync(string toEmail, string subject, string text, string html, string kind, CancellationToken cancellationToken = default)
        {
            if (FailFor.Contains(toEmail))
            {
                return Task.FromResult(ApiResponse<bool>.Error("邮件发送失败，请稍后重试", AccountPasswordEmailSender.AccountEmailSendFailedCode));
            }

            Rendered.Add((toEmail, subject, text, html));
            return Task.FromResult(ApiResponse<bool>.OK(true));
        }

        public Task<ApiResponse<bool>> SendPasswordCodeAsync(string toEmail, string displayName, string loginName, string code, DateTime expiresAtUtc, bool invite, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ApiResponse<bool>> SendInitialPasswordAsync(string toEmail, string displayName, string loginName, string initialPassword, bool isReset = false, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ApiResponse<bool>> SendEmailChangeCodeAsync(string toEmail, string displayName, string loginName, string code, DateTime expiresAtUtc, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeSms : ISmsSender
    {
        public bool Available { get; set; } = true;
        public List<SmsRequest> Sent { get; } = new();
        public Func<SmsRequest, ApiResponse<bool>>? Handler { get; set; }
        public bool IsAvailable => Available;

        public Task<ApiResponse<bool>> SendAsync(SmsRequest request, CancellationToken cancellationToken = default)
        {
            var result = Handler?.Invoke(request) ?? ApiResponse<bool>.OK(true);
            if (result.Success) Sent.Add(request);
            return Task.FromResult(result);
        }
    }

    private sealed class FakeLinks : IStaffNotificationLinkProvider
    {
        public Task<StaffNotificationLinks> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult(new StaffNotificationLinks(
            "https://hotbargain.vip/app",
            "https://apps.apple.com/au/app/hbgroup/id6786073002",
            "https://hotbargain.vip/api/mobile-app-builds/android-latest/download?profile=production"
        ));
    }

    private sealed class FlakyLinks : IStaffNotificationLinkProvider
    {
        public bool Fail { get; set; }

        public Task<StaffNotificationLinks> GetAsync(CancellationToken cancellationToken = default) =>
            Fail
                ? throw new InvalidOperationException("模拟读取链接时数据库超时")
                : new FakeLinks().GetAsync(cancellationToken);
    }

    private sealed class CountingSignal : IStaffNotificationSignal
    {
        public int Count { get; private set; }
        public void Notify() => Count++;
    }
}

/// <summary>员工通知控制器的授权元数据：发送 = 重置密码权限，模板写操作 = 仅管理员。</summary>
public sealed class StaffNotificationsControllerMetadataTests
{
    private static readonly Type ControllerType = typeof(BlazorApp.Api.Controllers.StaffNotificationsController);

    private static System.Collections.Generic.IEnumerable<MethodInfo> Actions() =>
        ControllerType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Where(method => method.GetCustomAttributes<Microsoft.AspNetCore.Mvc.Routing.HttpMethodAttribute>().Any());

    [Fact]
    public void 控制器要求登录_每个接口都有重置密码策略_发送类接口店长也能进()
    {
        Assert.Contains(ControllerType.GetCustomAttributes<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>(), _ => true);
        var actions = Actions().ToList();
        Assert.Equal(10, actions.Count); // 新增接口时要同步确认它的授权，不能漏

        foreach (var action in actions)
        {
            var policies = action.GetCustomAttributes<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>().Select(item => item.Policy).ToList();
            var adminOnly = action.GetCustomAttributes<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>().Any(item => item.Roles == "Admin");
            // 模板写操作仍只给管理员；其余接口用店长持有的店员码，全局码经别名同样满足。
            var expected = adminOnly
                ? BlazorApp.Shared.Constants.Permissions.Users.ResetPassword
                : BlazorApp.Shared.Constants.Permissions.Users.ResetStoreStaffPassword;
            Assert.True(policies.Contains(expected), $"{action.Name} 缺少 {expected} 策略");
        }
    }

    [Fact]
    public void 全局重置密码权限经别名满足店员码_Web后台入口不受影响()
    {
        var aliases = BlazorApp.Shared.Constants.Permissions.GetPermissionAliases();

        Assert.Contains(
            BlazorApp.Shared.Constants.Permissions.Users.ResetPassword,
            aliases[BlazorApp.Shared.Constants.Permissions.Users.ResetStoreStaffPassword]
        );
    }

    [Theory]
    [InlineData("CreateTemplate", true)]
    [InlineData("UpdateTemplate", true)]
    [InlineData("ResetTemplate", true)]
    [InlineData("ListTemplates", false)]
    [InlineData("Preflight", false)]
    [InlineData("Preview", false)]
    [InlineData("Send", false)]
    [InlineData("ListBatches", false)]
    [InlineData("GetBatch", false)]
    [InlineData("RetryBatch", false)]
    public void 模板写操作只有管理员能做_其余接口不要求管理员角色(string actionName, bool adminOnly)
    {
        var action = ControllerType.GetMethod(actionName)!;
        var roles = action.GetCustomAttributes<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>().Select(item => item.Roles).Where(item => item is not null).ToList();

        Assert.Equal(adminOnly, roles.Contains("Admin"));
    }

    [Fact]
    public void 没有任何接口允许匿名访问()
    {
        Assert.Empty(ControllerType.GetCustomAttributes<Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute>());
        Assert.DoesNotContain(Actions(), action => action.GetCustomAttributes<Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute>().Any());
    }
}
