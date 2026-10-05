using BlazorApp.Shared.Models;
using SqlSugar;

namespace BlazorApp.Shared.Models.HBweb;

/// <summary>
/// 经 Apple Lookup 验证后的 iOS App Store 发布事实。发布事实只追加、不覆盖。
/// </summary>
[SugarTable("IosAppStoreRelease")]
public sealed class IosAppStoreRelease : BaseEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = false)]
    public Guid Id { get; set; } = Guid.NewGuid();

    [SugarColumn(Length = 40, IsNullable = false)]
    public string App { get; set; } = string.Empty;

    [SugarColumn(Length = 32, IsNullable = false)]
    public string AppStoreId { get; set; } = string.Empty;

    [SugarColumn(Length = 200, IsNullable = false)]
    public string BundleIdentifier { get; set; } = string.Empty;

    [SugarColumn(Length = 64, IsNullable = false)]
    public string Version { get; set; } = string.Empty;

    /// <summary>
    /// Apple Lookup 不返回 CFBundleVersion；此值只保存管理员提交的审计事实。
    /// </summary>
    [SugarColumn(Length = 64, IsNullable = false)]
    public string BuildNumber { get; set; } = string.Empty;

    [SugarColumn(Length = 8, IsNullable = false)]
    public string Storefront { get; set; } = "au";

    [SugarColumn(Length = 2048, IsNullable = false)]
    public string AppStoreUrl { get; set; } = string.Empty;

    [SugarColumn(IsNullable = false)]
    public DateTime AppleVerifiedAtUtc { get; set; }
}

/// <summary>
/// Mobile iOS 全局原生升级策略。PolicyKey 数据库唯一，服务只更新这一行。
/// </summary>
[SugarTable("MobileIosNativeUpdatePolicy")]
public sealed class MobileIosNativeUpdatePolicy : BaseEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = false)]
    public Guid Id { get; set; } = Guid.NewGuid();

    [SugarColumn(Length = 40, IsNullable = false)]
    public string PolicyKey { get; set; } = "mobile-ios";

    [SugarColumn(IsNullable = true)]
    public Guid? ReleaseId { get; set; }

    [SugarColumn(Length = 64, IsNullable = true)]
    public string? MinimumSupportedVersion { get; set; }

    [SugarColumn(IsNullable = true)]
    public int? MinimumSupportedBuildNumber { get; set; }

    [SugarColumn(Length = 1000, IsNullable = true)]
    public string? ReleaseMessage { get; set; }

    [SugarColumn(IsNullable = false)]
    public bool Enabled { get; set; }

    [SugarColumn(IsNullable = false)]
    public long PolicyVersion { get; set; }
}

/// <summary>
/// Mobile 安卓原生「最低支持构建号」强制更新策略。PolicyKey 数据库唯一，全局只有一行。
/// 表由版本化迁移 20261005.002 创建，实体不进 SqlSugarContext 的自动建表清单。
/// </summary>
[SugarTable("MobileAndroidNativeUpdatePolicy")]
public sealed class MobileAndroidNativeUpdatePolicy : BaseEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = false)]
    public Guid Id { get; set; } = Guid.NewGuid();

    [SugarColumn(Length = 40, IsNullable = false)]
    public string PolicyKey { get; set; } = "mobile-android";

    [SugarColumn(IsNullable = false)]
    public bool Enabled { get; set; }

    /// <summary>
    /// 最低支持的安卓 versionCode；低于它的设备在有可安装公开包时被整页拦截。
    /// </summary>
    [SugarColumn(IsNullable = true)]
    public int? MinimumSupportedBuildNumber { get; set; }

    [SugarColumn(Length = 1000, IsNullable = true)]
    public string? ReleaseMessage { get; set; }

    [SugarColumn(IsNullable = false)]
    public long PolicyVersion { get; set; }
}

/// <summary>
/// iPad 原生 App Store 升级策略。
/// </summary>
[SugarTable("PosIpadNativeUpdatePolicy")]
public sealed class PosIpadNativeUpdatePolicy : BaseEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = false)]
    public Guid Id { get; set; } = Guid.NewGuid();

    [SugarColumn(Length = 40, IsNullable = false)]
    public string PolicyKey { get; set; } = "pos-ipad-native";

    [SugarColumn(IsNullable = true)]
    public Guid? ReleaseId { get; set; }

    [SugarColumn(Length = 64, IsNullable = true)]
    public string? MinimumSupportedVersion { get; set; }

    [SugarColumn(IsNullable = true)]
    public int? MinimumSupportedBuildNumber { get; set; }

    [SugarColumn(Length = 1000, IsNullable = true)]
    public string? ReleaseMessage { get; set; }

    [SugarColumn(Length = 16, IsNullable = false)]
    public string TargetScope { get; set; } = "all";

    [SugarColumn(IsNullable = false)]
    public bool Enabled { get; set; }

    [SugarColumn(IsNullable = false)]
    public long PolicyVersion { get; set; }
}

[SugarTable("PosIpadNativeUpdatePolicyTarget")]
public sealed class PosIpadNativeUpdatePolicyTarget : BaseEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = false)]
    public Guid Id { get; set; } = Guid.NewGuid();

    [SugarColumn(IsNullable = false)]
    public Guid PolicyId { get; set; }

    [SugarColumn(Length = 100, IsNullable = false)]
    public string StoreGuid { get; set; } = string.Empty;
}

/// <summary>
/// iPad EAS OTA 发布事实。登记不代表激活。
/// </summary>
[SugarTable("PosIpadOtaRelease")]
public sealed class PosIpadOtaRelease : BaseEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = false)]
    public Guid Id { get; set; } = Guid.NewGuid();

    [SugarColumn(Length = 32, IsNullable = false)]
    public string Environment { get; set; } = "production";

    [SugarColumn(Length = 120, IsNullable = false)]
    public string UpdateGroupId { get; set; } = string.Empty;

    [SugarColumn(Length = 120, IsNullable = false)]
    public string IosUpdateId { get; set; } = string.Empty;

    [SugarColumn(Length = 120, IsNullable = false)]
    public string Channel { get; set; } = string.Empty;

    [SugarColumn(Length = 120, IsNullable = false)]
    public string RuntimeVersion { get; set; } = string.Empty;

    [SugarColumn(Length = 120, IsNullable = true)]
    public string? GitCommitHash { get; set; }

    [SugarColumn(Length = 2048, IsNullable = true)]
    public string? DashboardUrl { get; set; }

    [SugarColumn(IsNullable = false)]
    public DateTime PublishedAtUtc { get; set; }

    [SugarColumn(IsNullable = false)]
    public bool IsRollback { get; set; }

    [SugarColumn(IsNullable = true)]
    public Guid? RollbackOfReleaseId { get; set; }
}

/// <summary>
/// iPad OTA 投放策略。历史行保留，生产环境最多一个 Enabled 行。
/// </summary>
[SugarTable("PosIpadOtaRollout")]
public sealed class PosIpadOtaRollout : BaseEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = false)]
    public Guid Id { get; set; } = Guid.NewGuid();

    [SugarColumn(Length = 32, IsNullable = false)]
    public string Environment { get; set; } = "production";

    [SugarColumn(IsNullable = false)]
    public Guid ReleaseId { get; set; }

    [SugarColumn(IsNullable = false)]
    public bool ForceUpdate { get; set; }

    [SugarColumn(Length = 16, IsNullable = false)]
    public string TargetScope { get; set; } = "all";

    [SugarColumn(Length = 1000, IsNullable = true)]
    public string? ReleaseMessage { get; set; }

    [SugarColumn(IsNullable = false)]
    public bool Enabled { get; set; }

    [SugarColumn(IsNullable = false)]
    public long PolicyVersion { get; set; }
}

[SugarTable("PosIpadOtaRolloutTarget")]
public sealed class PosIpadOtaRolloutTarget : BaseEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = false)]
    public Guid Id { get; set; } = Guid.NewGuid();

    [SugarColumn(IsNullable = false)]
    public Guid RolloutId { get; set; }

    [SugarColumn(Length = 100, IsNullable = false)]
    public string StoreGuid { get; set; } = string.Empty;
}

/// <summary>
/// 手持 POS 单条发布 lane 的当前策略。四条 lane 各自只有一行，缺行代表继续使用旧配置。
/// </summary>
[SugarTable("PosHandheldUpdatePolicy")]
public sealed class PosHandheldUpdatePolicy : BaseEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = false)]
    public Guid Id { get; set; } = Guid.NewGuid();

    [SugarColumn(Length = 32, IsNullable = false)]
    public string Lane { get; set; } = string.Empty;

    [SugarColumn(IsNullable = false)]
    public bool Enabled { get; set; }

    [SugarColumn(IsNullable = false)]
    public bool Required { get; set; }

    [SugarColumn(IsNullable = true)]
    public Guid? CandidateId { get; set; }

    /// <summary>
    /// 激活时对候选不可变身份生成的 SHA-256；决策时重新计算，防止 webhook 更新原行后静默漂移。
    /// </summary>
    [SugarColumn(Length = 64, IsNullable = true)]
    public string? CandidateFingerprint { get; set; }

    [SugarColumn(Length = 64, IsNullable = true)]
    public string? MinimumSupportedVersion { get; set; }

    [SugarColumn(IsNullable = true)]
    public int? MinimumSupportedBuildNumber { get; set; }

    [SugarColumn(Length = 1000, IsNullable = true)]
    public string? ReleaseMessage { get; set; }

    [SugarColumn(IsNullable = false)]
    public long PolicyVersion { get; set; }
}

/// <summary>
/// 手持 POS 策略追加式审计。每次真实变化写入完整快照，禁止业务代码更新或删除。
/// </summary>
[SugarTable("PosHandheldUpdatePolicyRevision")]
public sealed class PosHandheldUpdatePolicyRevision : BaseEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = false)]
    public Guid Id { get; set; } = Guid.NewGuid();

    [SugarColumn(IsNullable = false)]
    public Guid PolicyId { get; set; }

    [SugarColumn(Length = 32, IsNullable = false)]
    public string Lane { get; set; } = string.Empty;

    [SugarColumn(IsNullable = false)]
    public long PolicyVersion { get; set; }

    [SugarColumn(Length = 16, IsNullable = false)]
    public string Action { get; set; } = "save";

    [SugarColumn(Length = 8000, IsNullable = false)]
    public string SnapshotJson { get; set; } = string.Empty;
}
