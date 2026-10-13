using System.Reflection;
using System.Security.Claims;
using BlazorApp.Api.Controllers.React;
using BlazorApp.Api.Features.FaceMatch;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>
/// 人脸比对控制器：每个动作对应的权限策略（元数据）、任一权限类动作的内联判定、响应头、业务错误映射、
/// multipart 解析与大小限制、操作人解析。服务层用 Moq 替身，不连数据库。
/// </summary>
public sealed class FaceMatchControllerTests
{
    private readonly Mock<IFaceStoreProfileService> _profiles = new(MockBehavior.Strict);
    private readonly Mock<IFaceStaffService> _staff = new(MockBehavior.Strict);
    private readonly Mock<IFaceTrafficService> _traffic = new(MockBehavior.Strict);
    private readonly Mock<IFaceUnwelcomeService> _unwelcome = new(MockBehavior.Strict);
    private readonly Mock<IAuthorizationService> _authorization = new();

    private static readonly string[] AllCodes =
    [
        Permissions.FaceMatch.Manage, Permissions.FaceMatch.Traffic.View, Permissions.FaceMatch.Unwelcome.Manage,
        Permissions.FaceMatch.Unwelcome.Approve, Permissions.FaceMatch.Unwelcome.Review,
    ];

    // ---------- 元数据 ----------

    [Fact]
    public void 控制器要求登录_路由固定_响应带no_store()
    {
        var type = typeof(FaceMatchController);

        Assert.Equal("api/react/v1/face-match", type.GetCustomAttribute<RouteAttribute>()!.Template);
        Assert.NotNull(type.GetCustomAttribute<AuthorizeAttribute>());
        Assert.NotNull(type.GetCustomAttribute<FaceCameraNoStoreAttribute>());
        Assert.NotNull(type.GetCustomAttribute<ApiControllerAttribute>());
    }

    public static IEnumerable<object[]> ActionPolicies() =>
    [
        [nameof(FaceMatchController.ListProfiles), Permissions.FaceMatch.Manage],
        [nameof(FaceMatchController.GetProfile), Permissions.FaceMatch.Manage],
        [nameof(FaceMatchController.CheckProfile), Permissions.FaceMatch.Manage],
        [nameof(FaceMatchController.UpdateProfile), Permissions.FaceMatch.Manage],
        [nameof(FaceMatchController.ListStaff), Permissions.FaceMatch.Manage],
        [nameof(FaceMatchController.ListConsents), Permissions.FaceMatch.Manage],
        [nameof(FaceMatchController.CreateConsent), Permissions.FaceMatch.Manage],
        [nameof(FaceMatchController.WithdrawConsent), Permissions.FaceMatch.Manage],
        [nameof(FaceMatchController.ListEnrollments), Permissions.FaceMatch.Manage],
        [nameof(FaceMatchController.CreateEnrollment), Permissions.FaceMatch.Manage],
        [nameof(FaceMatchController.WithdrawEnrollment), Permissions.FaceMatch.Manage],
        [nameof(FaceMatchController.Audit), Permissions.FaceMatch.Manage],
        [nameof(FaceMatchController.Traffic), Permissions.FaceMatch.Traffic.View],
        [nameof(FaceMatchController.CreateEntry), Permissions.FaceMatch.Unwelcome.Manage],
        [nameof(FaceMatchController.WithdrawEntry), Permissions.FaceMatch.Unwelcome.Manage],
        [nameof(FaceMatchController.SetDisputeNote), Permissions.FaceMatch.Unwelcome.Manage],
        [nameof(FaceMatchController.ApproveEntry), Permissions.FaceMatch.Unwelcome.Approve],
        [nameof(FaceMatchController.ListTasks), Permissions.FaceMatch.Unwelcome.Review],
        [nameof(FaceMatchController.ReviewTask), Permissions.FaceMatch.Unwelcome.Review],
        [nameof(FaceMatchController.TaskImage), Permissions.FaceMatch.Unwelcome.Review],
        [nameof(FaceMatchController.ReferencePhoto), Permissions.FaceMatch.Unwelcome.Review],
    ];

    [Theory]
    [MemberData(nameof(ActionPolicies))]
    public void 每个动作只认对应的单一权限码(string action, string policy)
    {
        var method = typeof(FaceMatchController).GetMethod(action)!;

        var attribute = Assert.Single(method.GetCustomAttributes<AuthorizeAttribute>());
        Assert.Equal(policy, attribute.Policy);
    }

    [Fact]
    public void 任一权限类动作不挂单一策略_由动作内按权限判定_且每个公开动作都在清单里()
    {
        var anyOf = new[] { nameof(FaceMatchController.Config), nameof(FaceMatchController.ListEntries), nameof(FaceMatchController.Stats) };
        foreach (var name in anyOf)
        {
            Assert.Empty(typeof(FaceMatchController).GetMethod(name)!.GetCustomAttributes<AuthorizeAttribute>());
        }

        var listed = ActionPolicies().Select(row => (string)row[0]).Concat(anyOf).ToHashSet();
        var actions = typeof(FaceMatchController).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(m => m.Name)
            .ToHashSet();
        Assert.Subset(listed, actions);   // 新增动作必须登记权限并更新本清单
        Assert.Equal(listed.Count, actions.Count);
    }

    [Fact]
    public void 五个权限码互相独立且与设计一致()
    {
        Assert.Equal(
            ["FaceMatch.Manage", "FaceMatch.Traffic.View", "FaceMatch.Unwelcome.Manage", "FaceMatch.Unwelcome.Approve", "FaceMatch.Unwelcome.Review"],
            AllCodes);
        Assert.Equal(AllCodes.Length, AllCodes.Distinct().Count());
    }

    // ---------- 任一权限判定 ----------

    [Fact]
    public async Task 配置接口_五个权限一个都没有返回403_有一个就返回权限标志()
    {
        var none = CreateController();
        var forbidden = await none.Config(default);
        Assert.Equal(403, Assert.IsType<ObjectResult>(forbidden).StatusCode);
        Assert.Equal("FACE_MATCH_FORBIDDEN", ErrorCode(forbidden));

        FaceMatchPermissionFlagsDto? received = null;
        _profiles.Setup(p => p.GetConfigAsync(It.IsAny<FaceMatchPermissionFlagsDto>(), It.IsAny<CancellationToken>()))
            .Callback<FaceMatchPermissionFlagsDto, CancellationToken>((flags, _) => received = flags)
            .ReturnsAsync(new FaceMatchConfigDto { Enabled = true });
        var reviewer = CreateController(Permissions.FaceMatch.Unwelcome.Review);

        var ok = await reviewer.Config(default);

        Assert.Equal(200, Assert.IsType<OkObjectResult>(ok).StatusCode);
        Assert.Equal(new FaceMatchPermissionFlagsDto { UnwelcomeReview = true }, received);
    }

    [Theory]
    [InlineData(new string[0], false, false)]
    [InlineData(new[] { Permissions.FaceMatch.Unwelcome.Review }, false, false)]
    [InlineData(new[] { Permissions.FaceMatch.Unwelcome.Manage }, true, false)]
    [InlineData(new[] { Permissions.FaceMatch.Unwelcome.Approve }, true, true)]
    [InlineData(new[] { Permissions.FaceMatch.Unwelcome.Manage, Permissions.FaceMatch.Unwelcome.Approve }, true, true)]
    public async Task 条目列表_Manage或Approve任一可进_canApprove跟随Approve权限(string[] granted, bool allowed, bool canApprove)
    {
        bool? receivedCanApprove = null;
        _unwelcome.Setup(u => u.ListEntriesAsync(null, null, 1, 20, It.IsAny<FaceMatchActor>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback<string?, string?, int, int, FaceMatchActor, bool, CancellationToken>((_, _, _, _, _, approve, _) => receivedCanApprove = approve)
            .ReturnsAsync(new FaceMatchPageDto<FaceUnwelcomeEntryDto>());

        var result = await CreateController(granted).ListEntries(null, null, 1, 20, default);

        Assert.Equal(allowed ? 200 : 403, StatusOf(result));
        Assert.Equal(allowed ? canApprove : null, receivedCanApprove);
    }

    [Theory]
    [InlineData(new string[0], false)]
    [InlineData(new[] { Permissions.FaceMatch.Traffic.View }, false)]
    [InlineData(new[] { Permissions.FaceMatch.Manage }, false)]
    [InlineData(new[] { Permissions.FaceMatch.Unwelcome.Review }, true)]
    [InlineData(new[] { Permissions.FaceMatch.Unwelcome.Approve }, true)]
    [InlineData(new[] { Permissions.FaceMatch.Unwelcome.Manage }, true)]
    public async Task 误报统计_Manage_Approve_Review任一名单权限可看_档案和客流权限不能看(string[] granted, bool allowed)
    {
        _unwelcome.Setup(u => u.GetStatsAsync(It.IsAny<FaceMatchActor>(), It.IsAny<CancellationToken>())).ReturnsAsync(new FaceUnwelcomeStatsDto());

        var result = await CreateController(granted).Stats(default);

        Assert.Equal(allowed ? 200 : 403, StatusOf(result));
    }

    // ---------- 错误映射与响应头 ----------

    [Fact]
    public async Task 业务错误转成对应状态码与错误码_闸门缺项通过details返回()
    {
        var report = new FaceGateReportDto { CanSave = false, Facets = [new FaceGateFacetDto { Facet = "match", Requested = true, Missing = ["piaReference"] }] };
        _profiles.Setup(p => p.UpdateAsync("1013", It.IsAny<FaceStoreProfileUpdateRequest>(), It.IsAny<FaceMatchActor>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new FaceMatchException(409, "FACE_MATCH_GATE_NOT_MET", "启用前置条件不满足。", report));

        var result = await CreateController().UpdateProfile("1013", null, default);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(409, objectResult.StatusCode);
        var envelope = Assert.IsType<ApiResponse<FaceStoreProfileDto>>(objectResult.Value);
        Assert.False(envelope.Success);
        Assert.Equal("FACE_MATCH_GATE_NOT_MET", envelope.ErrorCode);
        Assert.Same(report, envelope.Details);
    }

    [Fact]
    public async Task 命中图接口_只返回图片字节_带no_store_nosniff_inline_错误是JSON信封不带图片头()
    {
        _unwelcome.Setup(u => u.GetTaskImageAsync("t1", It.IsAny<FaceMatchActor>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FaceMatchImageContent(FaceImageTestData.Jpeg, "image/jpeg"));
        _unwelcome.Setup(u => u.GetTaskImageAsync("gone", It.IsAny<FaceMatchActor>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new FaceMatchException(404, "FACE_MATCH_IMAGE_GONE", "图片不存在或已被清理。"));
        var controller = CreateController();

        var ok = await controller.TaskImage("t1", default);
        var file = Assert.IsType<FileContentResult>(ok);
        Assert.Equal("image/jpeg", file.ContentType);
        Assert.Equal(FaceImageTestData.Jpeg, file.FileContents);
        var headers = controller.Response.Headers;
        Assert.Equal("no-store", headers.CacheControl.ToString());
        Assert.Equal("no-cache", headers.Pragma.ToString());
        Assert.Equal("nosniff", headers["X-Content-Type-Options"].ToString());
        Assert.Equal("inline", headers.ContentDisposition.ToString());

        var gone = CreateController();
        var error = await gone.TaskImage("gone", default);
        Assert.Equal(404, StatusOf(error));
        Assert.Equal("FACE_MATCH_IMAGE_GONE", ErrorCode(error));
        Assert.False(gone.Response.Headers.ContainsKey("X-Content-Type-Options"));
    }

    [Fact]
    public async Task 参考照片接口_透传任务编号与下标()
    {
        _unwelcome.Setup(u => u.GetReferencePhotoAsync("t1", 2, It.IsAny<FaceMatchActor>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FaceMatchImageContent(FaceImageTestData.Png, "image/png"));

        var result = await CreateController().ReferencePhoto("t1", 2, default);

        Assert.Equal("image/png", Assert.IsType<FileContentResult>(result).ContentType);
    }

    // ---------- 操作人 ----------

    [Fact]
    public async Task 操作人_管理员角色标记IsAdmin_账号取userId声明_普通账号不是管理员()
    {
        FaceMatchActor? actor = null;
        _unwelcome.Setup(u => u.ApproveEntryAsync("e1", It.IsAny<FaceMatchActor>(), It.IsAny<CancellationToken>()))
            .Callback<string, FaceMatchActor, CancellationToken>((_, a, _) => actor = a)
            .ReturnsAsync(new FaceUnwelcomeEntryDto());

        await CreateController(roles: ["管理员"], userId: "u-77").ApproveEntry("e1", default);
        Assert.Equal(new FaceMatchActor("u-77", "tester", IsAdmin: true), actor);

        await CreateController(roles: ["StoreManager"], userId: "u-78").ApproveEntry("e1", default);
        Assert.Equal(new FaceMatchActor("u-78", "tester", IsAdmin: false), actor);

        await CreateController(userId: null).ApproveEntry("e1", default);
        Assert.Equal("unknown", actor!.Id);
    }

    // ---------- multipart ----------

    [Fact]
    public async Task 登记接口_解析表单字段与多个文件_交给服务层()
    {
        FaceEnrollmentCreateRequest? request = null;
        IReadOnlyList<FaceMatchUpload>? photos = null;
        _staff.Setup(s => s.CreateEnrollmentAsync(It.IsAny<FaceEnrollmentCreateRequest>(), It.IsAny<IReadOnlyList<FaceMatchUpload>>(), It.IsAny<FaceMatchActor>(), It.IsAny<CancellationToken>()))
            .Callback<FaceEnrollmentCreateRequest, IReadOnlyList<FaceMatchUpload>, FaceMatchActor, CancellationToken>((r, p, _, _) => { request = r; photos = p; })
            .ReturnsAsync(new FaceEnrollmentDto { EnrollmentId = "en1" });
        var controller = CreateController();
        SetForm(controller, new() { ["userGuid"] = "u-1", ["consentId"] = "c-1" },
            Files(("photos", "a.jpg", FaceImageTestData.Jpeg), ("photos", "b.png", FaceImageTestData.Png), ("photos", "c.jpg", FaceImageTestData.Jpeg)));

        var result = await controller.CreateEnrollment(default);

        Assert.Equal(200, StatusOf(result));
        Assert.Equal("u-1", request!.UserGuid);
        Assert.Equal("c-1", request.ConsentId);
        Assert.Equal(3, photos!.Count);
        Assert.Equal(FaceImageTestData.Png, photos[1].Bytes);
    }

    [Fact]
    public async Task 同意接口_解析时间与单个扫描件()
    {
        FaceConsentCreateRequest? request = null;
        FaceMatchUpload? scan = null;
        _staff.Setup(s => s.CreateConsentAsync(It.IsAny<FaceConsentCreateRequest>(), It.IsAny<FaceMatchUpload?>(), It.IsAny<FaceMatchActor>(), It.IsAny<CancellationToken>()))
            .Callback<FaceConsentCreateRequest, FaceMatchUpload?, FaceMatchActor, CancellationToken>((r, s, _, _) => { request = r; scan = s; })
            .ReturnsAsync(new FaceConsentDto { ConsentId = "c1" });
        var controller = CreateController();
        SetForm(controller,
            new() { ["userGuid"] = "u-1", ["consentVersion"] = "v1.0", ["signedAtUtc"] = "2026-10-13T01:02:03Z", ["method"] = "paper", ["nswNoticeDate"] = "2026-09-01" },
            Files(("scan", "scan.jpg", FaceImageTestData.Jpeg)));

        await controller.CreateConsent(default);

        Assert.Equal(new DateTime(2026, 10, 13, 1, 2, 3, DateTimeKind.Utc), request!.SignedAtUtc);
        Assert.Equal(DateTimeKind.Utc, request.SignedAtUtc!.Value.Kind);
        Assert.Equal("2026-09-01", request.NswNoticeDate);
        Assert.Equal(FaceImageTestData.Jpeg, scan!.Bytes);
    }

    [Fact]
    public async Task 名单提交接口_门店范围按逗号拆分_类别与到期时间解析()
    {
        FaceUnwelcomeEntryCreateRequest? request = null;
        _unwelcome.Setup(u => u.CreateEntryAsync(It.IsAny<FaceUnwelcomeEntryCreateRequest>(), It.IsAny<IReadOnlyList<FaceMatchUpload>>(), It.IsAny<FaceMatchActor>(), It.IsAny<CancellationToken>()))
            .Callback<FaceUnwelcomeEntryCreateRequest, IReadOnlyList<FaceMatchUpload>, FaceMatchActor, CancellationToken>((r, _, _, _) => request = r)
            .ReturnsAsync(new FaceUnwelcomeEntryDto());
        var controller = CreateController();
        SetForm(controller,
            new() { ["incidentCategory"] = "2", ["evidenceRef"] = "E-1", ["storeScope"] = "1013, 2001 ,", ["expiresAtUtc"] = "2027-01-01T00:00:00Z" },
            Files(("photos", "a.jpg", FaceImageTestData.Jpeg)));

        await controller.CreateEntry(default);

        Assert.Equal(2, request!.IncidentCategory);
        Assert.Equal(["1013", "2001"], request.StoreScope);
        Assert.Equal(new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc), request.ExpiresAtUtc);
    }

    [Fact]
    public async Task 上传校验_非multipart_文件过多_文件过大_数字或时间非法_都是400()
    {
        var notForm = CreateController();
        Assert.Equal(400, StatusOf(await notForm.CreateEnrollment(default)));

        var many = CreateController();
        SetForm(many, new(), Files(Enumerable.Range(0, 7).Select(i => ("photos", $"{i}.jpg", FaceImageTestData.Jpeg)).ToArray()));
        Assert.Equal(400, StatusOf(await many.CreateEnrollment(default)));

        var big = CreateController();
        SetForm(big, new(), Files(("photos", "big.jpg", new byte[FaceMatchRules.MaxImageBytes + 1])));
        Assert.Equal(400, StatusOf(await big.CreateEnrollment(default)));

        var badNumber = CreateController();
        SetForm(badNumber, new() { ["incidentCategory"] = "abc" }, Files());
        Assert.Equal(400, StatusOf(await badNumber.CreateEntry(default)));

        var badTime = CreateController();
        SetForm(badTime, new() { ["signedAtUtc"] = "yesterday-ish" }, Files());
        Assert.Equal(400, StatusOf(await badTime.CreateConsent(default)));
    }

    [Fact]
    public void 上传类动作限制请求体大小()
    {
        foreach (var name in new[] { nameof(FaceMatchController.CreateConsent), nameof(FaceMatchController.CreateEnrollment), nameof(FaceMatchController.CreateEntry) })
        {
            var limit = typeof(FaceMatchController).GetMethod(name)!.GetCustomAttribute<RequestSizeLimitAttribute>();
            Assert.NotNull(limit);
        }
    }

    // ---------- 辅助 ----------

    private FaceMatchController CreateController(params string[] granted) => CreateController(granted, roles: [], userId: "u-1");

    private FaceMatchController CreateController(string[]? granted = null, string[]? roles = null, string? userId = "u-1")
    {
        var grantedSet = (granted ?? []).ToHashSet();
        _authorization.Setup(a => a.AuthorizeAsync(It.IsAny<ClaimsPrincipal>(), It.IsAny<object?>(), It.IsAny<string>()))
            .ReturnsAsync((ClaimsPrincipal _, object? _, string policy) =>
                grantedSet.Contains(policy) ? AuthorizationResult.Success() : AuthorizationResult.Failed());

        var claims = new List<Claim> { new(ClaimTypes.Name, "tester") };
        if (userId != null) claims.Add(new Claim("userId", userId));
        claims.AddRange((roles ?? []).Select(role => new Claim(ClaimTypes.Role, role)));
        var controller = new FaceMatchController(_profiles.Object, _staff.Object, _traffic.Object, _unwelcome.Object, _authorization.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) },
            },
        };
        return controller;
    }

    private static void SetForm(FaceMatchController controller, Dictionary<string, string> fields, FormFileCollection files)
    {
        var request = controller.ControllerContext.HttpContext.Request;
        request.ContentType = "multipart/form-data; boundary=test-boundary";
        request.Form = new FormCollection(fields.ToDictionary(p => p.Key, p => new Microsoft.Extensions.Primitives.StringValues(p.Value)), files);
    }

    private static FormFileCollection Files(params (string Field, string Name, byte[] Bytes)[] items)
    {
        var files = new FormFileCollection();
        foreach (var (field, name, bytes) in items)
        {
            files.Add(new FormFile(new MemoryStream(bytes), 0, bytes.Length, field, name)
            {
                Headers = new HeaderDictionary(),
                ContentType = "application/octet-stream",
            });
        }

        return files;
    }

    private static int StatusOf(IActionResult result) => result switch
    {
        ObjectResult objectResult => objectResult.StatusCode ?? 200,
        _ => 200,
    };

    private static string? ErrorCode(IActionResult result) =>
        Assert.IsType<ObjectResult>(result).Value switch
        {
            ApiResponse<object> envelope => envelope.ErrorCode,
            { } other => (string?)other.GetType().GetProperty("ErrorCode")!.GetValue(other),
            _ => null,
        };
}
