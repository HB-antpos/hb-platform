using System.ComponentModel.DataAnnotations;
using BlazorApp.Api.Data;
using BlazorApp.Api.Interfaces;
using BlazorApp.Api.Interfaces.React;
using BlazorApp.Api.Models.Myob;
using BlazorApp.Api.Services.Sms;
using BlazorApp.Api.Utils;
using BlazorApp.Shared.DTOs;
using BlazorApp.Shared.Models;
using SqlSugar;

namespace BlazorApp.Api.Services.Myob;

public interface IMyobEmployeeCreateService
{
    /// <summary>按 MYOB 员工卡预填我方员工资料（只读，不写库）。</summary>
    Task<ApiResponse<MyobCreateDraftDto>> GetDraftAsync(
        string connectionGuid, string myobEmployeeUid, CancellationToken cancellationToken);

    /// <summary>人工核对后建我方账号，并把它绑定到这张 MYOB 员工卡。</summary>
    Task<ApiResponse<MyobCreateFromMyobResult>> CreateAsync(
        MyobCreateFromMyobRequest request, string actorUserGuid, string actorName, CancellationToken cancellationToken);
}

/// <summary>
/// 「MYOB 有、我方没有」的我方建档：先给草稿（MYOB 资料预填 + 冲突提醒），人工改完确认后再建档。
/// 建账号复用 <see cref="IStoreUserReactService.CreateAsync"/>（手机端「新建员工」同一套规则），
/// 绑定复用 <see cref="IMyobEmployeeBindingService.ConfirmAsync"/>（人工绑定）。只读 MYOB，不往 MYOB 写任何东西。
/// </summary>
public sealed class MyobEmployeeCreateService : IMyobEmployeeCreateService
{
    private static readonly TimeZoneInfo? Sydney = FindSydney();

    private readonly ISqlSugarClient _db;
    private readonly IMyobEmployeeDirectory _directory;
    private readonly ICurrentUserManageableStoreScopeService _scope;
    private readonly IStoreUserReactService _storeUsers;
    private readonly IMyobEmployeeBindingService _binding;
    private readonly TimeProvider _time;

    public MyobEmployeeCreateService(
        SqlSugarContext context,
        IMyobEmployeeDirectory directory,
        ICurrentUserManageableStoreScopeService scope,
        IStoreUserReactService storeUsers,
        IMyobEmployeeBindingService binding,
        TimeProvider? timeProvider = null)
        : this(context.Db, directory, scope, storeUsers, binding, timeProvider)
    {
    }

    internal MyobEmployeeCreateService(
        ISqlSugarClient db,
        IMyobEmployeeDirectory directory,
        ICurrentUserManageableStoreScopeService scope,
        IStoreUserReactService storeUsers,
        IMyobEmployeeBindingService binding,
        TimeProvider? timeProvider = null)
    {
        _db = db;
        _directory = directory;
        _scope = scope;
        _storeUsers = storeUsers;
        _binding = binding;
        _time = timeProvider ?? TimeProvider.System;
    }

    // ───────────────────────── 草稿 ─────────────────────────

    public async Task<ApiResponse<MyobCreateDraftDto>> GetDraftAsync(
        string connectionGuid,
        string myobEmployeeUid,
        CancellationToken cancellationToken)
    {
        var scope = await _scope.GetScopeAsync();
        if (!scope.IsAllowed)
        {
            return ApiResponse<MyobCreateDraftDto>.Error(
                string.IsNullOrWhiteSpace(scope.Message) ? "没有权限新建员工" : scope.Message, MyobCreateErrorCodes.Forbidden);
        }

        var loaded = await LoadUnboundCardAsync(connectionGuid, myobEmployeeUid, cancellationToken);
        if (loaded.Error is not null)
        {
            return ApiResponse<MyobCreateDraftDto>.Error(loaded.Error.Value.Message, loaded.Error.Value.Code);
        }

        var connection = loaded.Connection!;
        var card = loaded.Card!;
        var today = SydneyToday();
        var email = card.Match.Emails.Select(item => item.Trim()).FirstOrDefault(item => item.Length > 0);
        var myobPhone = card.Match.Phones.Select(item => item.Trim()).FirstOrDefault(item => item.Length > 0);
        var birthday = card.Match.DateOfBirth?.Date;
        int? age = birthday is null ? null : NewStaffProfileRules.AgeOn(birthday.Value, today);
        var employmentType = MapEmploymentStatus(card.Dto.EmploymentStatus);

        var stores = await LoadStoreOptionsAsync(connection.ConnectionGuid, scope);
        var mappedStores = stores.Where(store => store.MappedToThisCompany).ToList();

        var draft = new MyobCreateDraftDto
        {
            ConnectionGuid = connection.ConnectionGuid,
            ConnectionName = connection.BusinessName,
            Card = card.Dto,
            GivenName = card.Match.FirstName.Trim(),
            FamilyName = card.Match.LastName.Trim(),
            Email = email,
            MyobPhone = myobPhone,
            Phone = NormalizePhone(myobPhone),
            Birthday = birthday?.ToString("yyyy-MM-dd"),
            Age = age,
            IsMinor = age is < 18,
            EmploymentType = employmentType,
            Stores = stores,
            SuggestedStoreCode = mappedStores.Count == 1 ? mappedStores[0].StoreCode : null,
        };
        draft.Username = await SuggestUsernameAsync(UsernameBase(draft.GivenName, draft.FamilyName, email));
        draft.Warnings = await BuildWarningsAsync(draft, card);
        return ApiResponse<MyobCreateDraftDto>.OK(draft);
    }

    private async Task<List<MyobCreateWarningDto>> BuildWarningsAsync(MyobCreateDraftDto draft, MyobDirectoryCard card)
    {
        var warnings = new List<MyobCreateWarningDto>();

        if (!card.Dto.IsActive)
        {
            warnings.Add(new MyobCreateWarningDto
            {
                Code = MyobCreateWarningCodes.CardInactive,
                Message = "MYOB 里这张员工卡已停用，可能已经离职，确认要建档再继续",
            });
        }

        // 邮箱在我方唯一（含已删除账号）：占用了就建不成，多半其实是同一个人，提示改为直接绑定。
        if (!string.IsNullOrWhiteSpace(draft.Email))
        {
            var emailKey = draft.Email.Trim().ToLowerInvariant();
            var owner = await _db.Queryable<User>()
                .Where(user => user.Email.Trim().ToLower() == emailKey)
                .Select(user => new { user.UserGUID, user.Username, user.FullName, user.IsDeleted })
                .FirstAsync();
            if (owner is not null)
            {
                var ownerName = string.IsNullOrWhiteSpace(owner.FullName) ? owner.Username : owner.FullName!;
                warnings.Add(new MyobCreateWarningDto
                {
                    Code = MyobCreateWarningCodes.EmailInUse,
                    Message = owner.IsDeleted
                        ? $"这个邮箱被已删除的账号 {ownerName} 占用，需要换一个邮箱才能建档"
                        : $"这个邮箱已属于我方账号 {ownerName}，多半是同一个人，建议直接把这张卡绑定给他/她",
                    UserGuid = owner.UserGUID,
                    UserName = ownerName,
                    UserDeleted = owner.IsDeleted,
                });
            }
        }

        if (!string.IsNullOrWhiteSpace(draft.MyobPhone))
        {
            var phoneOwner = await FindPhoneOwnerAsync(draft.MyobPhone);
            if (phoneOwner is not null)
            {
                warnings.Add(new MyobCreateWarningDto
                {
                    Code = MyobCreateWarningCodes.PhoneInUse,
                    Message = $"这个电话已出现在我方员工 {phoneOwner.Value.Name} 的资料里，请确认不是同一个人",
                    UserGuid = phoneOwner.Value.UserGuid,
                    UserName = phoneOwner.Value.Name,
                });
            }
        }

        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(draft.Email)) missing.Add("邮箱");
        if (string.IsNullOrWhiteSpace(draft.Phone)) missing.Add("电话");
        if (draft.Birthday is null) missing.Add("生日");
        if (draft.EmploymentType is null) missing.Add("用工类型");
        if (missing.Count > 0)
        {
            warnings.Add(new MyobCreateWarningDto
            {
                Code = MyobCreateWarningCodes.MissingFields,
                Message = $"MYOB 里没有{string.Join("、", missing)}，需要人工补上",
            });
        }

        if (!draft.Stores.Any(store => store.MappedToThisCompany))
        {
            warnings.Add(new MyobCreateWarningDto
            {
                Code = MyobCreateWarningCodes.NoMappedStore,
                Message = "你能选的分店里没有映射到这家公司的；建档后这名员工不会出现在这家公司的名单里，需另在「发薪公司」里指定",
            });
        }

        return warnings;
    }

    private async Task<(string UserGuid, string Name)?> FindPhoneOwnerAsync(string phone)
    {
        var target = PhoneKey(phone);
        if (target.Length < 8)
        {
            return null;
        }

        // 员工资料里的电话写法五花八门（空格、+61、04…），库里没法直接等值比较：取出有电话的在职资料在内存里按规范化后的号码比。
        var rows = await _db.Queryable<EmployeeProfile>()
            .InnerJoin<User>((profile, user) => profile.UserGUID == user.UserGUID)
            .Where((profile, user) => !profile.IsDeleted && !user.IsDeleted && profile.Phone != null && profile.Phone != "")
            .Select((profile, user) => new { user.UserGUID, user.Username, user.FullName, profile.Phone })
            .ToListAsync();
        var hit = rows.FirstOrDefault(row => PhoneKey(row.Phone) == target);
        return hit is null
            ? null
            : (hit.UserGUID, string.IsNullOrWhiteSpace(hit.FullName) ? hit.Username : hit.FullName!);
    }

    private async Task<List<MyobCreateStoreOptionDto>> LoadStoreOptionsAsync(string connectionGuid, CurrentUserManageableStoreScope scope)
    {
        var mapped = (await _db.Queryable<MyobStoreMapping>()
                .Where(item => item.ConnectionGuid == connectionGuid)
                .Select(item => item.StoreCode)
                .ToListAsync())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var stores = await _db.Queryable<Store>()
            .Where(store => !store.IsDeleted && store.IsActive)
            .Select(store => new { store.StoreCode, store.StoreName })
            .ToListAsync();

        // 只列当前账号能为其建员工的分店（与建档时 StoreUserReactService 的分店范围检查同一口径）；本公司的分店排前面。
        return stores
            .Where(store => !string.IsNullOrWhiteSpace(store.StoreCode) && scope.CanAccessStoreCode(store.StoreCode))
            .Select(store => new MyobCreateStoreOptionDto
            {
                StoreCode = store.StoreCode,
                StoreName = store.StoreName,
                MappedToThisCompany = mapped.Contains(store.StoreCode),
            })
            .OrderByDescending(store => store.MappedToThisCompany)
            .ThenBy(store => store.StoreCode, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<string> SuggestUsernameAsync(string baseName)
    {
        for (var suffix = 1; suffix < 100; suffix++)
        {
            var candidate = suffix == 1 ? baseName : $"{baseName}{suffix}";
            var conflict = await UserIdentityUniqueness.FindConflictAsync(_db, candidate, null);
            if (conflict == UserIdentityConflict.None)
            {
                return candidate;
            }
        }

        // 极端情况（同名 99 个）：给个带随机尾巴的，人工可以再改。
        return $"{baseName}{Guid.NewGuid().ToString("N")[..4]}";
    }

    // ───────────────────────── 建档 ─────────────────────────

    public async Task<ApiResponse<MyobCreateFromMyobResult>> CreateAsync(
        MyobCreateFromMyobRequest request,
        string actorUserGuid,
        string actorName,
        CancellationToken cancellationToken)
    {
        var loaded = await LoadUnboundCardAsync(request.ConnectionGuid, request.MyobEmployeeUid, cancellationToken);
        if (loaded.Error is not null)
        {
            // 卡不存在 / 已被绑定：先拦住，免得建出一个绑不上的账号。
            return ApiResponse<MyobCreateFromMyobResult>.Error(loaded.Error.Value.Message, loaded.Error.Value.Code);
        }

        var employmentType = ParseRequestedEmploymentType(request.EmploymentType);
        if (employmentType is null)
        {
            return ApiResponse<MyobCreateFromMyobResult>.Error(
                "请选择用工类型（全职 / 兼职 / 临时工）", MyobCreateErrorCodes.InvalidEmploymentType);
        }

        var dto = new CreateStoreUserDto
        {
            Username = request.Username?.Trim() ?? string.Empty,
            Email = string.IsNullOrWhiteSpace(request.Email) ? null : request.Email.Trim(),
            GivenName = request.GivenName,
            MiddleName = request.MiddleName,
            FamilyName = request.FamilyName,
            Nickname = request.Nickname,
            Phone = request.Phone,
            Birthday = request.Birthday?.Date,
            Guardian = request.Guardian,
            StoreCode = request.StoreCode?.Trim() ?? string.Empty,
            Status = 1,
            EmploymentType = employmentType,
            LoginEnabled = true,
            // 两种给密码的方式员工首次登录都必须先改成自己的密码。
            RequirePasswordChange = true,
            SendPasswordSetupEmail = request.SendPasswordSetupEmail,
            Password = request.SendPasswordSetupEmail ? null : request.Password,
            // 前端提交的是明文（HTTPS），必须显式标 raw，否则会被当成旧客户端的 sha256 摘要。
            PasswordFormat = PasswordHasher.PasswordFormatRaw,
            // 与「新建员工」入口一致：生日、邮箱、电话必填，未满 18 岁要监护人。
            RequireNewStaffProfile = true,
        };

        // 控制器的模型校验只覆盖本请求，这里补上与手机端接口相同的字段校验（用户名长度、邮箱格式、密码长度等）。
        var validation = new List<ValidationResult>();
        if (!Validator.TryValidateObject(dto, new ValidationContext(dto), validation, validateAllProperties: true))
        {
            return ApiResponse<MyobCreateFromMyobResult>.Error(
                validation[0].ErrorMessage ?? "请求参数验证失败", "VALIDATION_ERROR");
        }

        var created = await _storeUsers.CreateAsync(dto, string.IsNullOrWhiteSpace(actorName) ? "system" : actorName);
        if (!created.Success || created.Data is null)
        {
            return ApiResponse<MyobCreateFromMyobResult>.Error(
                string.IsNullOrWhiteSpace(created.Message) ? "建档失败" : created.Message, created.ErrorCode);
        }

        var result = new MyobCreateFromMyobResult
        {
            UserGuid = created.Data.UserGuid,
            Username = created.Data.Username,
            StoreCode = created.Data.StoreCode,
            PasswordSetupEmailTo = created.Data.PasswordSetupEmail?.MaskedEmail,
            PasswordSetupEmailError = created.Data.PasswordSetupEmailError,
        };

        // 账号已提交；绑定失败（并发里卡刚被别人绑走等）不回滚建号，明确告诉前端，到绑定列表手动处理。
        var bind = await _binding.ConfirmAsync(
            new MyobConfirmBindingsRequest
            {
                ConnectionGuid = loaded.Connection!.ConnectionGuid,
                Items = [new MyobConfirmBindingItem { UserGuid = result.UserGuid, MyobEmployeeUid = loaded.Card!.Dto.Uid, Manual = true }],
            },
            actorUserGuid,
            cancellationToken);
        if (!bind.Success)
        {
            result.BindError = string.IsNullOrWhiteSpace(bind.Message) ? "绑定失败" : bind.Message;
        }
        else if (bind.Data is { Failed.Count: > 0 } bindResult)
        {
            result.BindError = bindResult.Failed[0].Message;
        }

        result.Bound = result.BindError is null;
        return ApiResponse<MyobCreateFromMyobResult>.OK(
            result,
            result.Bound ? "已建档并绑定 MYOB 员工卡" : $"账号已建好，但绑定失败：{result.BindError}");
    }

    // ───────────────────────── 公共 ─────────────────────────

    private readonly record struct LoadError(string Message, string Code);

    private sealed record LoadedCard(MyobConnection? Connection, MyobDirectoryCard? Card, LoadError? Error);

    /// <summary>取公司文件与员工卡，并确认这张卡还没绑定给任何人。</summary>
    private async Task<LoadedCard> LoadUnboundCardAsync(string? connectionGuid, string? myobEmployeeUid, CancellationToken cancellationToken)
    {
        var guid = connectionGuid?.Trim() ?? string.Empty;
        var uid = myobEmployeeUid?.Trim() ?? string.Empty;
        if (guid.Length == 0 || uid.Length == 0)
        {
            return new LoadedCard(null, null, new LoadError("缺少公司文件或员工卡", MyobCreateErrorCodes.InvalidRequest));
        }

        var connection = await _db.Queryable<MyobConnection>()
            .FirstAsync(item => item.ConnectionGuid == guid && item.Status == MyobConnectionStatuses.Active);
        if (connection is null)
        {
            return new LoadedCard(null, null, new LoadError("MYOB 连接不存在或未启用", MyobErrorCodes.ConnectionNotActive));
        }

        MyobEmployeeDirectoryData directory;
        try
        {
            directory = await _directory.GetAsync(guid, false, cancellationToken);
        }
        catch (MyobApiException ex)
        {
            return new LoadedCard(connection, null, new LoadError(ex.Message, ex.Code));
        }

        var card = directory.Cards.FirstOrDefault(item => string.Equals(item.Dto.Uid, uid, StringComparison.OrdinalIgnoreCase));
        if (card is null)
        {
            return new LoadedCard(connection, null, new LoadError("MYOB 里找不到这张员工卡，请刷新后重试", MyobBindErrorCodes.CardNotFound));
        }

        var link = await _db.Queryable<EmployeeMyobLink>()
            .FirstAsync(item => item.ConnectionGuid == guid && item.MyobEmployeeUid == card.Dto.Uid && item.UnboundAt == null);
        if (link is not null)
        {
            return new LoadedCard(connection, card, new LoadError("这张员工卡已经绑定给我方员工了，请刷新列表", MyobBindErrorCodes.CardAlreadyBound));
        }

        return new LoadedCard(connection, card, null);
    }

    private DateTime SydneyToday()
    {
        var utcNow = _time.GetUtcNow().UtcDateTime;
        return Sydney is null ? utcNow.Date : TimeZoneInfo.ConvertTimeFromUtc(utcNow, Sydney).Date;
    }

    private static TimeZoneInfo? FindSydney()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Australia/Sydney"); }
        catch (TimeZoneNotFoundException) { return null; }
    }

    /// <summary>
    /// MYOB EmploymentStatus → 我方用工类型（口径同资料同步 MyobEmployeeSyncRules.EmploymentTypeDiff）：
    /// FullTime → FullTime，PartTime → PartTime，Casual → Temporary；Other / 空对应不上，返回 null 让人工选。
    /// </summary>
    internal static string? MapEmploymentStatus(string? status) =>
        status?.Trim().ToLowerInvariant() switch
        {
            "fulltime" or "full time" or "full-time" => nameof(EmployeeType.FullTime),
            "parttime" or "part time" or "part-time" => nameof(EmployeeType.PartTime),
            "casual" => nameof(EmployeeType.Temporary),
            _ => null,
        };

    /// <summary>
    /// 建档请求里的用工类型必须明确是三者之一：建员工服务遇到认不出的值会默认成临时工，这里不能让它静默兜底。
    /// 返回值是建员工服务认得的写法。
    /// </summary>
    internal static string? ParseRequestedEmploymentType(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "fulltime" => "fulltime",
            "parttime" => "parttime",
            "temporary" => "temporary",
            _ => null,
        };

    /// <summary>澳洲手机统一写成「0412 345 678」；不是澳洲手机（座机、海外号）就原样去空白返回。</summary>
    internal static string? NormalizePhone(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone))
        {
            return null;
        }

        if (!AustralianPhoneNumber.TryNormalize(phone, out var e164))
        {
            return phone.Trim();
        }

        var national = "0" + e164[3..];
        return $"{national[..4]} {national[4..7]} {national[7..]}";
    }

    /// <summary>比较两个电话是否同一号码用的键：澳洲手机用 E.164，其余只留数字。</summary>
    internal static string PhoneKey(string? phone) =>
        AustralianPhoneNumber.TryNormalize(phone, out var e164)
            ? e164
            : new string((phone ?? string.Empty).Where(char.IsAsciiDigit).ToArray());

    /// <summary>
    /// 建议用户名：名 + 姓（只留小写字母和数字，如 Erin Chung → erinchung）；太短再用邮箱 @ 前面的部分；
    /// 都不够 3 个字符时用 staff。最长 40 个字符，给查重时追加的数字留位置（用户名上限 50）。
    /// </summary>
    internal static string UsernameBase(string? givenName, string? familyName, string? email)
    {
        static string Clean(string? value) =>
            new((value ?? string.Empty).ToLowerInvariant().Where(c => c is >= 'a' and <= 'z' or >= '0' and <= '9').ToArray());

        static string Cap(string value) => value.Length > 40 ? value[..40] : value;

        var name = Clean(givenName) + Clean(familyName);
        if (name.Length >= 3)
        {
            return Cap(name);
        }

        var local = Clean(email?.Split('@')[0]);
        return local.Length >= 3 ? Cap(local) : "staff";
    }
}
