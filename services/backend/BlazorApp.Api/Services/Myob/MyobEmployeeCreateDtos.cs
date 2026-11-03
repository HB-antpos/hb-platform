using BlazorApp.Shared.DTOs;

namespace BlazorApp.Api.Services.Myob;

// 「对方没有 → MYOB 有、我方没有」的「我方建档」：按 MYOB 员工卡预填我方员工资料，人工核对后一次建账号并绑定这张卡。
// 全局 JSON 为 camelCase；全部返回 ApiResponse<T>。
//
// 路由与权限（两项都要：Myob.Employee.Bind 且 Users.CreateStoreStaff，后者由 Users.Create 隐含）：
//   GET  /api/react/v1/myob/employee-create/draft?connectionGuid=&myobEmployeeUid=   → MyobCreateDraftDto（只读，不写库）
//   POST /api/react/v1/myob/employee-create        Body MyobCreateFromMyobRequest      → MyobCreateFromMyobResult
//
// 建账号复用手机端「新建员工」同一套服务（StoreUserReactService.CreateAsync）：固定 StoreStaff 角色、按分店范围校验、
// 新员工必填规则（未满 16 岁要监护人）、未成年合规草稿、首次登录强制改密 / 邮件发初始密码，都与那边一致。

/// <summary>草稿里可选的分店（只含当前账号能为其建员工的分店）。</summary>
public sealed class MyobCreateStoreOptionDto
{
    public string StoreCode { get; set; } = string.Empty;

    public string StoreName { get; set; } = string.Empty;

    /// <summary>该分店映射到当前公司文件（默认由这家公司发薪）。</summary>
    public bool MappedToThisCompany { get; set; }
}

/// <summary>草稿里的提醒：不挡住建档，但要人工留意。</summary>
public sealed class MyobCreateWarningDto
{
    /// <summary>MyobCreateWarningCodes。</summary>
    public string Code { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;

    /// <summary>邮箱 / 电话已被我方账号占用时：占用者（便于改为直接绑定该账号）。</summary>
    public string? UserGuid { get; set; }

    public string? UserName { get; set; }

    /// <summary>占用者账号是否已删除（已删除账号不能用来绑定，只能换邮箱）。</summary>
    public bool UserDeleted { get; set; }
}

public static class MyobCreateWarningCodes
{
    /// <summary>MYOB 邮箱已被我方账号占用：直接建会因邮箱重复失败，多半其实是同一个人。</summary>
    public const string EmailInUse = "EMAIL_IN_USE";

    /// <summary>MYOB 电话已出现在我方某个员工资料里。</summary>
    public const string PhoneInUse = "PHONE_IN_USE";

    /// <summary>MYOB 里这张卡已停用（可能已离职）。</summary>
    public const string CardInactive = "CARD_INACTIVE";

    /// <summary>当前账号能选的分店里没有映射到这家公司的。</summary>
    public const string NoMappedStore = "NO_MAPPED_STORE";

    /// <summary>MYOB 没有邮箱 / 电话 / 生日 / 用工类型，需要人工补。</summary>
    public const string MissingFields = "MISSING_FIELDS";
}

public static class MyobCreateErrorCodes
{
    public const string InvalidRequest = "MYOB_CREATE_INVALID_REQUEST";
    public const string InvalidEmploymentType = "MYOB_CREATE_INVALID_EMPLOYMENT_TYPE";
    public const string Forbidden = "MYOB_CREATE_FORBIDDEN";
}

/// <summary>按一张 MYOB 员工卡预填的我方员工资料草稿。</summary>
public sealed class MyobCreateDraftDto
{
    public string ConnectionGuid { get; set; } = string.Empty;

    public string ConnectionName { get; set; } = string.Empty;

    public MyobEmployeeCardDto Card { get; set; } = new();

    /// <summary>建议用户名（已查重）。</summary>
    public string Username { get; set; } = string.Empty;

    public string GivenName { get; set; } = string.Empty;

    public string FamilyName { get; set; } = string.Empty;

    public string? Email { get; set; }

    /// <summary>MYOB 里的原始电话（完整号码，只在本接口返回）。</summary>
    public string? MyobPhone { get; set; }

    /// <summary>规范化后的电话：澳洲手机写成「0412 345 678」，规范不了的原样。</summary>
    public string? Phone { get; set; }

    /// <summary>yyyy-MM-dd；MYOB 没有为 null。</summary>
    public string? Birthday { get; set; }

    /// <summary>按悉尼今天算的周岁；没有生日为 null。</summary>
    public int? Age { get; set; }

    /// <summary>未满 16 岁：建档必须填监护人四项（MYOB 没有，只能人工补）。</summary>
    public bool IsMinor { get; set; }

    /// <summary>按 MYOB EmploymentStatus 对应的我方用工类型：FullTime / PartTime / Temporary；对应不上为 null（必须人工选）。</summary>
    public string? EmploymentType { get; set; }

    public List<MyobCreateStoreOptionDto> Stores { get; set; } = [];

    /// <summary>能选的分店里恰好只有一家映射到这家公司时预选它，否则为 null（必须人工选）。</summary>
    public string? SuggestedStoreCode { get; set; }

    public List<MyobCreateWarningDto> Warnings { get; set; } = [];
}

/// <summary>人工核对后的建档请求。</summary>
public sealed class MyobCreateFromMyobRequest
{
    public string ConnectionGuid { get; set; } = string.Empty;

    public string MyobEmployeeUid { get; set; } = string.Empty;

    public string Username { get; set; } = string.Empty;

    public string? GivenName { get; set; }

    public string? MiddleName { get; set; }

    public string? FamilyName { get; set; }

    public string? Nickname { get; set; }

    public string? Email { get; set; }

    public string? Phone { get; set; }

    public DateTime? Birthday { get; set; }

    /// <summary>只在生日未满 16 岁时提交。</summary>
    public NewStaffGuardianDto? Guardian { get; set; }

    public string StoreCode { get; set; } = string.Empty;

    /// <summary>FullTime / PartTime / Temporary，必须明确选一个。</summary>
    public string EmploymentType { get; set; } = string.Empty;

    /// <summary>true = 系统生成初始密码发到员工邮箱（推荐）；false = 使用 Password。两种方式员工首次登录都要先改密。</summary>
    public bool SendPasswordSetupEmail { get; set; } = true;

    public string? Password { get; set; }
}

public sealed class MyobCreateFromMyobResult
{
    public string UserGuid { get; set; } = string.Empty;

    public string Username { get; set; } = string.Empty;

    public string StoreCode { get; set; } = string.Empty;

    /// <summary>是否已绑定到这张 MYOB 员工卡；false 时账号已建好，需要到绑定列表手动绑定。</summary>
    public bool Bound { get; set; }

    public string? BindError { get; set; }

    /// <summary>初始密码邮件已发往的邮箱（脱敏）。</summary>
    public string? PasswordSetupEmailTo { get; set; }

    /// <summary>账号已建好但初始密码邮件没发出去的原因（可在人员管理里重发）。</summary>
    public string? PasswordSetupEmailError { get; set; }
}
