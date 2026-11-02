using System.Reflection;
using BlazorApp.Api.Controllers.React;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Api.Models.Myob;
using BlazorApp.Api.Services.Myob;
using BlazorApp.Shared.Constants;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SqlSugar;
using Xunit;

namespace BlazorApp.Api.Tests;

/// <summary>
/// MYOB「MYOB 有、我方没有」的我方建档：草稿预填（字段对应 / 冲突提醒 / 分店范围）、建档（复用门店员工建档 + 人工绑定）、
/// 纯函数与控制器权限。SQLite 临时库 + 假 MYOB 名册 + 假门店员工服务（真实建档逻辑在 StoreUserReactService 的测试里覆盖）。
/// </summary>
public sealed class MyobEmployeeCreateTests : IDisposable
{
    // 悉尼 2026-11-02 11:00（夏令时 UTC+11）。
    private static readonly DateTime Now = new(2026, 11, 2, 0, 0, 0, DateTimeKind.Utc);
    private const string Orion = "conn-orion";
    private const string Inv = "conn-inv";

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"hb-myob-create-{Guid.NewGuid():N}.db");
    private readonly SqlSugarClient _db;
    private readonly FakeDirectory _directory = new();
    private readonly FakeScope _scope = new();
    private readonly FakeTime _time = new(Now);
    private readonly FakeStoreUsers _storeUsers;

    public MyobEmployeeCreateTests()
    {
        _db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = $"Data Source={_dbPath}",
            DbType = DbType.Sqlite,
            IsAutoCloseConnection = true,
            InitKeyType = InitKeyType.Attribute,
        });
        _db.CodeFirst.InitTables(
            typeof(MyobConnection), typeof(MyobStoreMapping), typeof(EmployeeMyobLink), typeof(EmployeePayrollEmployer),
            typeof(MyobEmployeeIgnore), typeof(Store), typeof(User), typeof(UserStore), typeof(EmployeeProfile),
            typeof(EmployeeMinorCompliance), typeof(AttendanceSettings));
        _storeUsers = new FakeStoreUsers(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        SqliteTempFileCleanup.DeleteIfExists(_dbPath);
    }

    private MyobEmployeeCreateService Service() =>
        new(_db, _directory, _scope, _storeUsers, new MyobEmployeeBindingService(_db, _directory, _time), _time);

    // ───────────────────────── 种子数据 ─────────────────────────

    private async Task SeedAsync()
    {
        foreach (var (guid, name) in new[] { (Orion, "HOT BARGAIN ORION PTY LTD"), (Inv, "Hot Bargain Investment Pty Ltd") })
        {
            await _db.Insertable(new MyobConnection
            {
                ConnectionGuid = guid,
                BusinessId = $"biz-{guid}",
                BusinessName = name,
                Status = MyobConnectionStatuses.Active,
                AuthorizedAt = Now,
                CreatedAt = Now,
            }).ExecuteCommandAsync();
        }

        await SeedStoreAsync("1013", "Orion", Orion);
        await SeedStoreAsync("1014", "Kawana", Inv);
        await SeedStoreAsync("1042", "TestStore", null);

        var connection = await _db.Queryable<MyobConnection>().FirstAsync(item => item.ConnectionGuid == Orion);
        _directory.Set(Orion, MyobEmployeeDirectory.Build(
            connection,
            [
                new MyobEmployeeRecord("c-erin", "E001", "ERIN", "CHUNG", null, true, true, ["erinchung268@gmail.com"], ["+61412345678"]),
                new MyobEmployeeRecord("c-kyle", null, "KYLE", "BUETA-MACKENZIE", null, true, true, ["kylebueta05@gmail.com"], ["0400 111 222"]),
                new MyobEmployeeRecord("c-gone", null, "Old", "Timer", null, true, false, [], []),
            ],
            [
                new MyobEmployeePayrollRecord("c-erin", new DateTime(2010, 2, 23), "Casual", []),
                new MyobEmployeePayrollRecord("c-kyle", new DateTime(2004, 12, 5), "FullTime", []),
                new MyobEmployeePayrollRecord("c-gone", null, "Other", []),
            ],
            Now));
    }

    private async Task SeedStoreAsync(string code, string name, string? connectionGuid)
    {
        await _db.Insertable(new Store { StoreGUID = $"g-{code}", StoreCode = code, StoreName = name, IsActive = true }).ExecuteCommandAsync();
        if (connectionGuid is not null)
        {
            await _db.Insertable(new MyobStoreMapping { StoreCode = code, ConnectionGuid = connectionGuid, MappedAt = Now }).ExecuteCommandAsync();
        }
    }

    private async Task SeedUserAsync(string guid, string username, string email, string? phone = null, bool deleted = false)
    {
        await _db.Insertable(new User
        {
            UserGUID = guid,
            Username = username,
            Email = email,
            FullName = $"Full {username}",
            IsActive = true,
            IsDeleted = deleted,
            CreatedAt = Now.AddDays(-10),
        }).ExecuteCommandAsync();
        if (phone is not null)
        {
            await _db.Insertable(new EmployeeProfile { UserGUID = guid, Phone = phone }).ExecuteCommandAsync();
        }
    }

    private static MyobCreateFromMyobRequest KyleRequest() => new()
    {
        ConnectionGuid = Orion,
        MyobEmployeeUid = "c-kyle",
        Username = "kylebuetamackenzie",
        GivenName = "Kyle",
        FamilyName = "Bueta-Mackenzie",
        Email = "kylebueta05@gmail.com",
        Phone = "0400 111 222",
        Birthday = new DateTime(2004, 12, 5),
        StoreCode = "1013",
        EmploymentType = "FullTime",
        SendPasswordSetupEmail = true,
    };

    // ───────────────────────── 纯函数 ─────────────────────────

    [Fact]
    public void UsernameBase_uses_letters_and_digits_of_given_and_family_name()
    {
        Assert.Equal("erinchung", MyobEmployeeCreateService.UsernameBase("ERIN", "CHUNG", null));
        Assert.Equal("kylebuetamackenzie", MyobEmployeeCreateService.UsernameBase("KYLE", "BUETA-MACKENZIE", null));
        // 名字全是中文等非 ASCII 时退到邮箱 @ 前面的部分，再不行用 staff。
        Assert.Equal("tiffanysong7034", MyobEmployeeCreateService.UsernameBase("张", "三", "Tiffanysong7034@gmail.com"));
        Assert.Equal("staff", MyobEmployeeCreateService.UsernameBase("张", "三", null));
        Assert.Equal(40, MyobEmployeeCreateService.UsernameBase(new string('a', 30), new string('b', 30), null).Length);
    }

    [Fact]
    public void NormalizePhone_formats_australian_mobile_and_keeps_others()
    {
        Assert.Equal("0412 345 678", MyobEmployeeCreateService.NormalizePhone("+61412345678"));
        Assert.Equal("0412 345 678", MyobEmployeeCreateService.NormalizePhone("61412345678"));
        Assert.Equal("0412 345 678", MyobEmployeeCreateService.NormalizePhone("0412-345-678"));
        Assert.Equal("(02) 9876 5432", MyobEmployeeCreateService.NormalizePhone(" (02) 9876 5432 "));
        Assert.Null(MyobEmployeeCreateService.NormalizePhone("  "));
        Assert.Equal(MyobEmployeeCreateService.PhoneKey("0412 345 678"), MyobEmployeeCreateService.PhoneKey("+61 412 345 678"));
    }

    [Fact]
    public void Employment_status_maps_like_profile_sync_and_request_type_is_strict()
    {
        Assert.Equal("FullTime", MyobEmployeeCreateService.MapEmploymentStatus("FullTime"));
        Assert.Equal("PartTime", MyobEmployeeCreateService.MapEmploymentStatus("part-time"));
        Assert.Equal("Temporary", MyobEmployeeCreateService.MapEmploymentStatus("Casual"));
        Assert.Null(MyobEmployeeCreateService.MapEmploymentStatus("Other"));
        Assert.Null(MyobEmployeeCreateService.MapEmploymentStatus(null));

        Assert.Equal("fulltime", MyobEmployeeCreateService.ParseRequestedEmploymentType("FullTime"));
        Assert.Equal("temporary", MyobEmployeeCreateService.ParseRequestedEmploymentType(" Temporary "));
        // 建员工服务遇到认不出的值会默认成临时工，这里必须拒绝而不是放过去。
        Assert.Null(MyobEmployeeCreateService.ParseRequestedEmploymentType("Casual"));
        Assert.Null(MyobEmployeeCreateService.ParseRequestedEmploymentType(""));
    }

    // ───────────────────────── 草稿 ─────────────────────────

    [Fact]
    public async Task Draft_prefills_fields_from_myob_card()
    {
        await SeedAsync();

        var result = await Service().GetDraftAsync(Orion, "c-erin", CancellationToken.None);

        Assert.True(result.Success, result.Message);
        var draft = result.Data!;
        Assert.Equal("HOT BARGAIN ORION PTY LTD", draft.ConnectionName);
        Assert.Equal("ERIN", draft.GivenName);
        Assert.Equal("CHUNG", draft.FamilyName);
        Assert.Equal("erinchung268@gmail.com", draft.Email);
        Assert.Equal("+61412345678", draft.MyobPhone);
        Assert.Equal("0412 345 678", draft.Phone);
        Assert.Equal("2010-02-23", draft.Birthday);
        Assert.Equal(16, draft.Age);
        Assert.True(draft.IsMinor);
        Assert.Equal("Temporary", draft.EmploymentType);
        Assert.Equal("erinchung", draft.Username);
        // 只有 1013 映射到 ORION：预选它，并排在最前面。
        Assert.Equal("1013", draft.SuggestedStoreCode);
        Assert.Equal(["1013", "1014", "1042"], draft.Stores.Select(item => item.StoreCode));
        Assert.True(draft.Stores[0].MappedToThisCompany);
        Assert.Empty(draft.Warnings);
    }

    [Fact]
    public async Task Draft_suggests_next_free_username_and_flags_email_and_phone_conflicts()
    {
        await SeedAsync();
        await SeedUserAsync("u-other", "erinchung", "someone@example.com");
        await SeedUserAsync("u-erin", "erin", "ErinChung268@Gmail.com ", phone: "0412 345 678");

        var draft = (await Service().GetDraftAsync(Orion, "c-erin", CancellationToken.None)).Data!;

        Assert.Equal("erinchung2", draft.Username);
        var email = Assert.Single(draft.Warnings, item => item.Code == MyobCreateWarningCodes.EmailInUse);
        Assert.Equal("u-erin", email.UserGuid);
        Assert.False(email.UserDeleted);
        var phone = Assert.Single(draft.Warnings, item => item.Code == MyobCreateWarningCodes.PhoneInUse);
        Assert.Equal("u-erin", phone.UserGuid);
    }

    [Fact]
    public async Task Draft_marks_email_taken_by_deleted_account()
    {
        await SeedAsync();
        await SeedUserAsync("u-deleted", "olderin", "erinchung268@gmail.com", deleted: true);

        var draft = (await Service().GetDraftAsync(Orion, "c-erin", CancellationToken.None)).Data!;

        var email = Assert.Single(draft.Warnings, item => item.Code == MyobCreateWarningCodes.EmailInUse);
        Assert.True(email.UserDeleted);
    }

    [Fact]
    public async Task Draft_flags_inactive_card_and_missing_fields()
    {
        await SeedAsync();

        var draft = (await Service().GetDraftAsync(Orion, "c-gone", CancellationToken.None)).Data!;

        Assert.Null(draft.EmploymentType);
        Assert.False(draft.IsMinor);
        Assert.Contains(draft.Warnings, item => item.Code == MyobCreateWarningCodes.CardInactive);
        var missing = Assert.Single(draft.Warnings, item => item.Code == MyobCreateWarningCodes.MissingFields);
        Assert.Contains("邮箱", missing.Message);
        Assert.Contains("用工类型", missing.Message);
    }

    [Fact]
    public async Task Draft_lists_only_stores_in_manager_scope()
    {
        await SeedAsync();
        _scope.Scope = new CurrentUserManageableStoreScope { IsAllowed = true, IsAdmin = false, StoreCodes = ["1042"] };

        var draft = (await Service().GetDraftAsync(Orion, "c-kyle", CancellationToken.None)).Data!;

        Assert.Equal(["1042"], draft.Stores.Select(item => item.StoreCode));
        Assert.Null(draft.SuggestedStoreCode);
        Assert.Contains(draft.Warnings, item => item.Code == MyobCreateWarningCodes.NoMappedStore);
    }

    [Fact]
    public async Task Draft_rejects_bound_card_missing_card_inactive_connection_and_denied_scope()
    {
        await SeedAsync();
        await _db.Insertable(new EmployeeMyobLink
        {
            LinkGuid = "link-kyle", UserGuid = "u-kyle", ConnectionGuid = Orion, MyobEmployeeUid = "c-kyle",
            MyobName = "KYLE", MatchMethod = MyobMatchMethods.Manual, MatchScore = 90, BoundAt = Now,
        }).ExecuteCommandAsync();

        Assert.Equal(MyobBindErrorCodes.CardAlreadyBound, (await Service().GetDraftAsync(Orion, "c-kyle", CancellationToken.None)).ErrorCode);
        Assert.Equal(MyobBindErrorCodes.CardNotFound, (await Service().GetDraftAsync(Orion, "c-nobody", CancellationToken.None)).ErrorCode);
        Assert.Equal(MyobErrorCodes.ConnectionNotActive, (await Service().GetDraftAsync("conn-x", "c-erin", CancellationToken.None)).ErrorCode);
        Assert.Equal(MyobCreateErrorCodes.InvalidRequest, (await Service().GetDraftAsync(Orion, " ", CancellationToken.None)).ErrorCode);

        _scope.Scope = new CurrentUserManageableStoreScope { IsAllowed = false, Message = "无权限" };
        Assert.Equal(MyobCreateErrorCodes.Forbidden, (await Service().GetDraftAsync(Orion, "c-erin", CancellationToken.None)).ErrorCode);
    }

    // ───────────────────────── 建档 ─────────────────────────

    [Fact]
    public async Task Create_reuses_store_staff_creation_and_binds_card()
    {
        await SeedAsync();

        var result = await Service().CreateAsync(KyleRequest(), "actor-1", "admin", CancellationToken.None);

        Assert.True(result.Success, result.Message);
        Assert.True(result.Data!.Bound);
        Assert.Null(result.Data.BindError);
        Assert.Equal("k***@gmail.com", result.Data.PasswordSetupEmailTo);

        var dto = Assert.Single(_storeUsers.Calls);
        Assert.Equal("kylebuetamackenzie", dto.Username);
        Assert.Equal("1013", dto.StoreCode);
        Assert.Equal("fulltime", dto.EmploymentType);
        Assert.True(dto.RequireNewStaffProfile);
        Assert.True(dto.RequirePasswordChange);
        Assert.True(dto.SendPasswordSetupEmail);
        Assert.True(dto.LoginEnabled);
        Assert.Null(dto.Password);
        Assert.Equal("raw", dto.PasswordFormat);
        Assert.Equal("admin", _storeUsers.LastCreatedBy);

        var link = await _db.Queryable<EmployeeMyobLink>().SingleAsync(item => item.MyobEmployeeUid == "c-kyle");
        Assert.Equal(result.Data.UserGuid, link.UserGuid);
        Assert.Equal(MyobMatchMethods.Manual, link.MatchMethod);
        Assert.Equal("actor-1", link.BoundByUserGuid);
    }

    [Fact]
    public async Task Create_with_manual_password_passes_it_through()
    {
        await SeedAsync();
        var request = KyleRequest();
        request.SendPasswordSetupEmail = false;
        request.Password = "Init#2026";

        var result = await Service().CreateAsync(request, "actor-1", "admin", CancellationToken.None);

        Assert.True(result.Success, result.Message);
        var dto = Assert.Single(_storeUsers.Calls);
        Assert.False(dto.SendPasswordSetupEmail);
        Assert.Equal("Init#2026", dto.Password);
        Assert.Null(result.Data!.PasswordSetupEmailTo);
    }

    [Fact]
    public async Task Create_rejects_before_creating_when_card_bound_type_invalid_or_fields_invalid()
    {
        await SeedAsync();

        var badType = KyleRequest();
        badType.EmploymentType = "Casual";
        Assert.Equal(MyobCreateErrorCodes.InvalidEmploymentType, (await Service().CreateAsync(badType, "a", "admin", CancellationToken.None)).ErrorCode);

        var shortName = KyleRequest();
        shortName.Username = "ab";
        Assert.Equal("VALIDATION_ERROR", (await Service().CreateAsync(shortName, "a", "admin", CancellationToken.None)).ErrorCode);

        var shortPassword = KyleRequest();
        shortPassword.SendPasswordSetupEmail = false;
        shortPassword.Password = "123";
        Assert.Equal("VALIDATION_ERROR", (await Service().CreateAsync(shortPassword, "a", "admin", CancellationToken.None)).ErrorCode);

        await _db.Insertable(new EmployeeMyobLink
        {
            LinkGuid = "link-kyle", UserGuid = "u-kyle", ConnectionGuid = Orion, MyobEmployeeUid = "c-kyle",
            MyobName = "KYLE", MatchMethod = MyobMatchMethods.Manual, MatchScore = 90, BoundAt = Now,
        }).ExecuteCommandAsync();
        Assert.Equal(MyobBindErrorCodes.CardAlreadyBound, (await Service().CreateAsync(KyleRequest(), "a", "admin", CancellationToken.None)).ErrorCode);

        Assert.Empty(_storeUsers.Calls);
    }

    [Fact]
    public async Task Create_passes_through_store_staff_creation_errors()
    {
        await SeedAsync();
        _storeUsers.Failure = ApiResponse<StoreUserDetailDto>.Error("邮箱已被使用", "EMAIL_ALREADY_EXISTS");

        var result = await Service().CreateAsync(KyleRequest(), "a", "admin", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("EMAIL_ALREADY_EXISTS", result.ErrorCode);
        Assert.Equal("邮箱已被使用", result.Message);
        Assert.False(await _db.Queryable<EmployeeMyobLink>().AnyAsync());
    }

    [Fact]
    public async Task Create_keeps_account_and_reports_when_binding_fails()
    {
        await SeedAsync();
        // 建好的账号没有分店：绑定服务的在职名单里找不到他，绑定失败但账号保留。
        _storeUsers.SkipStore = true;

        var result = await Service().CreateAsync(KyleRequest(), "a", "admin", CancellationToken.None);

        Assert.True(result.Success, result.Message);
        Assert.False(result.Data!.Bound);
        Assert.False(string.IsNullOrWhiteSpace(result.Data.BindError));
        Assert.Contains("绑定失败", result.Message);
        Assert.True(await _db.Queryable<User>().AnyAsync(item => item.UserGUID == result.Data.UserGuid));
        Assert.False(await _db.Queryable<EmployeeMyobLink>().AnyAsync());
    }

    // ───────────────────────── 控制器 ─────────────────────────

    [Fact]
    public void Controller_requires_both_binding_and_store_staff_creation_permissions()
    {
        var type = typeof(MyobEmployeeCreateController);
        Assert.Equal("api/react/v1/myob/employee-create", type.GetCustomAttribute<RouteAttribute>()!.Template);
        var policies = type.GetCustomAttributes<AuthorizeAttribute>().Select(item => item.Policy).ToList();
        Assert.Contains(Permissions.Myob.Employee.Bind, policies);
        Assert.Contains(Permissions.Users.CreateStoreStaff, policies);
        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            Assert.Null(method.GetCustomAttribute<AllowAnonymousAttribute>());
        }
    }

    // ───────────────────────── 测试替身 ─────────────────────────

    private sealed class FakeDirectory : IMyobEmployeeDirectory
    {
        private readonly Dictionary<string, MyobEmployeeDirectoryData> _data = new(StringComparer.OrdinalIgnoreCase);

        public void Set(string connectionGuid, MyobEmployeeDirectoryData data) => _data[connectionGuid] = data;

        public Task<MyobEmployeeDirectoryData> GetAsync(string connectionGuid, bool refresh, CancellationToken cancellationToken) =>
            Task.FromResult(_data[connectionGuid]);
    }

    private sealed class FakeScope : ICurrentUserManageableStoreScopeService
    {
        public CurrentUserManageableStoreScope Scope { get; set; } = new() { IsAllowed = true, IsAdmin = true };

        public Task<CurrentUserManageableStoreScope> GetScopeAsync() => Task.FromResult(Scope);
        public Task<IReadOnlyList<string>> GetAccessibleStoreCodesAsync() => throw new NotSupportedException();
        public Task<bool> CanAccessStoreCodeAsync(string storeCode) => throw new NotSupportedException();
        public Task<bool> CanAccessOrderAsync(string orderGuid) => throw new NotSupportedException();
        public Task<bool> CanManageStoreAsync(string storeGuid) => throw new NotSupportedException();
        public Task<bool> CanManageUserAsync(string userGuid) => throw new NotSupportedException();
    }

    /// <summary>只记录收到的建档请求，并写入最少的账号 / 分店 / 资料行，让绑定服务能找到这名在职员工。</summary>
    private sealed class FakeStoreUsers(SqlSugarClient db) : IStoreUserReactService
    {
        public List<CreateStoreUserDto> Calls { get; } = [];

        public string? LastCreatedBy { get; private set; }

        public ApiResponse<StoreUserDetailDto>? Failure { get; set; }

        public bool SkipStore { get; set; }

        public async Task<ApiResponse<StoreUserDetailDto>> CreateAsync(CreateStoreUserDto dto, string createdBy)
        {
            Calls.Add(dto);
            LastCreatedBy = createdBy;
            if (Failure is not null)
            {
                return Failure;
            }

            var userGuid = Guid.NewGuid().ToString();
            await db.Insertable(new User
            {
                UserGUID = userGuid,
                Username = dto.Username,
                Email = dto.Email ?? string.Empty,
                GivenName = dto.GivenName,
                FamilyName = dto.FamilyName,
                FullName = $"{dto.GivenName} {dto.FamilyName}",
                IsActive = true,
                CreatedAt = Now,
            }).ExecuteCommandAsync();
            await db.Insertable(new EmployeeProfile { UserGUID = userGuid, Phone = dto.Phone, Birthday = dto.Birthday }).ExecuteCommandAsync();
            if (!SkipStore)
            {
                await db.Insertable(new UserStore { UserStoreGUID = Guid.NewGuid().ToString(), UserGUID = userGuid, StoreGUID = $"g-{dto.StoreCode}" })
                    .ExecuteCommandAsync();
            }

            return ApiResponse<StoreUserDetailDto>.OK(new StoreUserDetailDto
            {
                UserGuid = userGuid,
                Username = dto.Username,
                StoreCode = dto.StoreCode,
                PasswordSetupEmail = dto.SendPasswordSetupEmail == true ? new PasswordSetupEmailResultDto { MaskedEmail = "k***@gmail.com" } : null,
            });
        }

        public Task<GridResponseDto<StoreUserListDto>> GetGridDataAsync(StoreUserGridRequestDto request) => throw new NotSupportedException();
        public Task<ApiResponse<StoreUserDetailDto>> GetByUserGuidAsync(string userGuid, string? storeCode) => throw new NotSupportedException();
        public Task<ApiResponse<StoreUserDetailDto>> UpdateAsync(string userGuid, UpdateStoreUserDto dto, string updatedBy) => throw new NotSupportedException();
        public Task<ApiResponse<bool>> UpdateStatusAsync(string userGuid, UpdateStoreUserStatusDto dto, string updatedBy) => throw new NotSupportedException();
        public Task<ApiResponse<bool>> UpdatePasswordAsync(string userGuid, UpdateStoreUserPasswordDto dto, string updatedBy) => throw new NotSupportedException();
        public Task<ApiResponse<PasswordSetupEmailResultDto>> SendPasswordSetupEmailAsync(string userGuid, SendStoreUserPasswordSetupEmailDto dto, string requestedBy) =>
            throw new NotSupportedException();
    }

    private sealed class FakeTime(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }
}
