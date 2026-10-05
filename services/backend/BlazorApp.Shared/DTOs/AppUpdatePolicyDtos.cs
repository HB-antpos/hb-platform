using System.Text.Json.Serialization;

namespace BlazorApp.Shared.DTOs;

public static class AppUpdateApps
{
    public const string MobileIos = "mobile-ios";
    public const string MobileAndroid = "mobile-android";
    public const string PosIpad = "pos-ipad";
    public const string PosHandheld = "pos-handheld";
}

public static class AppUpdateStates
{
    public const string None = "none";
    public const string Optional = "optional";
    public const string Required = "required";
}

public static class AppUpdatePolicyErrorCodes
{
    public const string VersionRequired = "APP_UPDATE_POLICY_VERSION_REQUIRED";
    public const string VersionConflict = "APP_UPDATE_POLICY_VERSION_CONFLICT";
}

public static class PosHandheldUpdateLanes
{
    public const string AndroidNative = "android-native";
    public const string IosNative = "ios-native";
    public const string AndroidOta = "android-ota";
    public const string IosOta = "ios-ota";

    public static readonly string[] All =
        [AndroidNative, IosNative, AndroidOta, IosOta];
}

public static class PosHandheldUpdatePolicyErrorCodes
{
    public const string LaneInvalid = "POS_HANDHELD_UPDATE_LANE_INVALID";
    public const string CandidateRequired = "POS_HANDHELD_UPDATE_CANDIDATE_REQUIRED";
    public const string CandidateInvalid = "POS_HANDHELD_UPDATE_CANDIDATE_INVALID";
    public const string CandidateFingerprintMismatch =
        "POS_HANDHELD_UPDATE_CANDIDATE_FINGERPRINT_MISMATCH";
    public const string OtaCandidateNotChannelHead =
        "POS_HANDHELD_OTA_CANDIDATE_NOT_CHANNEL_HEAD";
    public const string NativeMinimumInvalid = "POS_HANDHELD_NATIVE_MINIMUM_INVALID";
    public const string OtaMinimumNotAllowed = "POS_HANDHELD_OTA_MINIMUM_NOT_ALLOWED";
    public const string ReleaseMessageInvalid = "POS_HANDHELD_RELEASE_MESSAGE_INVALID";
}

public static class AppUpdateTargetScopes
{
    public const string All = "all";
    public const string Stores = "stores";
}

public sealed class IosAppStoreReleaseCreateRequest
{
    public string App { get; set; } = string.Empty;
    public string AppStoreId { get; set; } = string.Empty;
    public string BuildNumber { get; set; } = string.Empty;
    public string Storefront { get; set; } = "au";
}

public sealed class IosAppStoreReleaseQuery
{
    public string? App { get; set; }
    public string? Storefront { get; set; } = "au";
}

public sealed class IosAppStoreReleaseDto
{
    public Guid Id { get; set; }
    public string App { get; set; } = string.Empty;
    public string AppStoreId { get; set; } = string.Empty;
    public string BundleIdentifier { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string BuildNumber { get; set; } = string.Empty;
    public string Storefront { get; set; } = string.Empty;
    public string AppStoreUrl { get; set; } = string.Empty;
    public DateTime AppleVerifiedAtUtc { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
}

public class NativeUpdatePolicyRequest
{
    public long? ExpectedPolicyVersion { get; set; }
    public bool Enabled { get; set; }
    public Guid? ReleaseId { get; set; }
    public string? MinimumSupportedVersion { get; set; }
    public string? ReleaseMessage { get; set; }
}

public sealed class MobileIosNativeUpdatePolicyRequest : NativeUpdatePolicyRequest
{
    public int? MinimumSupportedBuildNumber { get; set; }
}

public sealed class PosIpadNativeUpdatePolicyRequest : NativeUpdatePolicyRequest
{
    public int? MinimumSupportedBuildNumber { get; set; }
    public string? TargetScope { get; set; } = AppUpdateTargetScopes.All;
    public List<string> TargetStoreGuids { get; set; } = new();
}

public sealed class NativeUpdatePolicyDto
{
    public Guid? Id { get; set; }
    public bool Enabled { get; set; }
    public long PolicyVersion { get; set; }
    public Guid? ReleaseId { get; set; }
    public string? LatestVersion { get; set; }
    public string? MinimumSupportedVersion { get; set; }
    public int? MinimumSupportedBuildNumber { get; set; }
    public string? AppStoreUrl { get; set; }
    public string? ReleaseMessage { get; set; }
    public string TargetScope { get; set; } = AppUpdateTargetScopes.All;
    public List<string> TargetStoreGuids { get; set; } = new();
    public DateTime? UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
}

/// <summary>
/// Mobile 安卓原生最低构建号策略保存错误码（版本冲突沿用 <see cref="AppUpdatePolicyErrorCodes"/>）。
/// </summary>
public static class MobileAndroidNativeUpdatePolicyErrorCodes
{
    public const string ReleaseMessageTooLong = "RELEASE_MESSAGE_TOO_LONG";
    public const string MinimumBuildRequired = "MINIMUM_BUILD_REQUIRED";
    public const string MinimumBuildInvalid = "MINIMUM_BUILD_INVALID";
    public const string MinimumBuildAboveLatest = "MINIMUM_BUILD_ABOVE_LATEST";
    public const string SaveFailed = "MOBILE_ANDROID_NATIVE_POLICY_SAVE_FAILED";
}

public sealed class MobileAndroidNativeUpdatePolicyRequest
{
    public long? ExpectedPolicyVersion { get; set; }
    public bool Enabled { get; set; }
    public int? MinimumSupportedBuildNumber { get; set; }
    public string? ReleaseMessage { get; set; }
}

/// <summary>
/// 管理端读取 / 保存后的安卓原生策略。可空字段显式输出 null，避免全局 WhenWritingNull 吞掉字段。
/// </summary>
public sealed class MobileAndroidNativeUpdatePolicyDto
{
    public bool Enabled { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public int? MinimumSupportedBuildNumber { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? ReleaseMessage { get; set; }

    public long PolicyVersion { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public DateTime? UpdatedAt { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? UpdatedBy { get; set; }

    /// <summary>
    /// 当前公开 production 安卓包（与匿名 android-latest 同一筛选）；没有时为 null。
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public MobileAndroidLatestBuildDto? LatestBuild { get; set; }
}

public sealed class MobileAndroidLatestBuildDto
{
    public string EasBuildId { get; set; } = string.Empty;

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? AppVersion { get; set; }

    /// <summary>安卓 versionCode（表里是字符串，这里已解析为正整数）。</summary>
    public int AppBuildVersion { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public DateTime? CompletedAt { get; set; }
}

/// <summary>
/// 匿名判定结果。state 只有 none / required；所有字段即使为 null 也必须输出。
/// </summary>
public sealed class MobileAndroidNativeUpdateDecisionDto
{
    public string State { get; set; } = AppUpdateStates.None;
    public string PolicyVersion { get; set; } = AppUpdateStates.None;

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public int? MinimumSupportedBuildNumber { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? LatestVersion { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public int? LatestBuildNumber { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? ReleaseMessage { get; set; }
}

public sealed class AppUpdateTargetStoreOptionDto
{
    public string StoreGuid { get; set; } = string.Empty;
    public string StoreCode { get; set; } = string.Empty;
    public string StoreName { get; set; } = string.Empty;
}

public sealed class NativeAppUpdateDecisionDto
{
    public string State { get; set; } = AppUpdateStates.None;
    public string PolicyVersion { get; set; } = AppUpdateStates.None;

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? LatestVersion { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? MinimumSupportedVersion { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? AppStoreUrl { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? ReleaseMessage { get; set; }
}

public sealed class PosIpadNativeDecisionRequest
{
    public string? StoreCode { get; set; }
    public string? Version { get; set; }
    public string? Build { get; set; }
}

public sealed class PosIpadOtaReleaseCreateRequest
{
    public string UpdateGroupId { get; set; } = string.Empty;
    public string IosUpdateId { get; set; } = string.Empty;
    public string Channel { get; set; } = string.Empty;
    public string RuntimeVersion { get; set; } = string.Empty;
    public string? GitCommitHash { get; set; }
    public string? DashboardUrl { get; set; }
    public DateTime? PublishedAtUtc { get; set; }
    public bool IsRollback { get; set; }
    public Guid? RollbackOfReleaseId { get; set; }
}

public sealed class PosIpadOtaChannelPreflightRequest
{
    public string Channel { get; set; } = string.Empty;
}

public sealed class PosIpadOtaChannelPreflightDto
{
    public string Channel { get; set; } = string.Empty;
    public bool Available { get; set; }
}

public sealed class PosIpadOtaReleaseDto
{
    public Guid Id { get; set; }
    public string Environment { get; set; } = string.Empty;
    public string UpdateGroupId { get; set; } = string.Empty;
    public string IosUpdateId { get; set; } = string.Empty;
    public string Channel { get; set; } = string.Empty;
    public string RuntimeVersion { get; set; } = string.Empty;
    public string? GitCommitHash { get; set; }
    public string? DashboardUrl { get; set; }
    public DateTime PublishedAtUtc { get; set; }
    public bool IsRollback { get; set; }
    public Guid? RollbackOfReleaseId { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
}

public sealed class PosIpadOtaRolloutRequest
{
    public long? ExpectedPolicyVersion { get; set; }
    public bool Enabled { get; set; }
    public Guid? ReleaseId { get; set; }
    public bool ForceUpdate { get; set; }
    public string? TargetScope { get; set; } = AppUpdateTargetScopes.All;
    public List<string> TargetStoreGuids { get; set; } = new();
    public string? ReleaseMessage { get; set; }
}

public sealed class PosIpadOtaRolloutDto
{
    public Guid? Id { get; set; }
    public bool Enabled { get; set; }
    public long PolicyVersion { get; set; }
    public Guid? ReleaseId { get; set; }
    public bool ForceUpdate { get; set; }
    public string TargetScope { get; set; } = AppUpdateTargetScopes.All;
    public List<string> TargetStoreGuids { get; set; } = new();
    public string? ReleaseMessage { get; set; }
    public PosIpadOtaReleaseDto? Release { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
}

public sealed class PosIpadOtaDecisionRequest
{
    public string? StoreCode { get; set; }
    public string? RuntimeVersion { get; set; }
    public string? CurrentUpdateId { get; set; }
    public string? CurrentUpdateGroupId { get; set; }
}

public sealed class PosIpadOtaDecisionDto
{
    public string State { get; set; } = AppUpdateStates.None;
    public string PolicyVersion { get; set; } = AppUpdateStates.None;

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? Channel { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? RuntimeVersion { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? IosUpdateId { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? UpdateGroupId { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? ReleaseMessage { get; set; }
}

public sealed class PosHandheldNativeDecisionRequest
{
    public string? StoreCode { get; set; }
    public string? Platform { get; set; }
    public string? Version { get; set; }
    public string? Build { get; set; }
}

public sealed class PosHandheldNativeDecisionDto
{
    public string State { get; set; } = AppUpdateStates.None;
    public string PolicyVersion { get; set; } = AppUpdateStates.None;
    public string Platform { get; set; } = string.Empty;
    public bool Required { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? LatestVersion { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? LatestBuild { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? MinimumSupportedVersion { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? Distribution { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? DownloadUrl { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public long? FileSize { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? Sha256 { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? PackageName { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? SigningCertificateSha256 { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? BundleIdentifier { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? AppStoreId { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? ReleaseMessage { get; set; }
}

public sealed class PosHandheldOtaDecisionRequest
{
    public string? StoreCode { get; set; }
    public string? Platform { get; set; }
    public string? RuntimeVersion { get; set; }
    public string? CurrentUpdateId { get; set; }
    public string? CurrentUpdateGroupId { get; set; }
}

public sealed class PosHandheldOtaDecisionDto
{
    public string State { get; set; } = AppUpdateStates.None;
    public string PolicyVersion { get; set; } = AppUpdateStates.None;
    public string AppKey { get; set; } = "pos-handheld";

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? ProjectName { get; set; }

    public string Platform { get; set; } = string.Empty;
    public bool Required { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? Channel { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? RuntimeVersion { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? UpdateId { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? UpdateGroupId { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? ReleaseMessage { get; set; }
}

public sealed class PosHandheldUpdatePolicyRequest
{
    public long? ExpectedPolicyVersion { get; set; }
    public bool Enabled { get; set; }
    public bool Required { get; set; }
    public Guid? CandidateId { get; set; }
    public string? MinimumSupportedVersion { get; set; }
    public int? MinimumSupportedBuildNumber { get; set; }
    public string? ReleaseMessage { get; set; }
}

public sealed class PosHandheldUpdatePolicyDto
{
    public Guid? Id { get; set; }
    public string Lane { get; set; } = string.Empty;
    public bool Managed { get; set; }
    public string Source { get; set; } = "legacy";
    public bool Enabled { get; set; }
    public bool Required { get; set; }
    public long PolicyVersion { get; set; }
    public Guid? CandidateId { get; set; }
    public bool CandidateValid { get; set; }
    public string? BlockedReason { get; set; }
    public PosHandheldUpdateCandidateDto? Candidate { get; set; }
    public string? MinimumSupportedVersion { get; set; }
    public int? MinimumSupportedBuildNumber { get; set; }
    public string? ReleaseMessage { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
}

public sealed class PosHandheldUpdateCandidateDto
{
    public Guid Id { get; set; }
    public string Lane { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string Platform { get; set; } = string.Empty;
    public string ProjectName { get; set; } = string.Empty;
    public string? Profile { get; set; }
    public string? Version { get; set; }
    public string? BuildNumber { get; set; }
    public string? RuntimeVersion { get; set; }
    public string? Channel { get; set; }
    public string? UpdateId { get; set; }
    public string? UpdateGroupId { get; set; }
    public string? ArtifactUrl { get; set; }
    public long? FileSize { get; set; }
    public string? Sha256 { get; set; }
    public string? Distribution { get; set; }
    public string? PackageName { get; set; }
    public string? SigningCertificateSha256 { get; set; }
    public string? AppStoreId { get; set; }
    public string? BundleIdentifier { get; set; }
    public string? ReleaseMessage { get; set; }
    public Guid? ReleaseBatchId { get; set; }
    public bool Legacy { get; set; }
    public string? FactFingerprint { get; set; }
    public string? GitCommitHash { get; set; }
    public string? DashboardUrl { get; set; }
    public bool IsRollback { get; set; }
    public Guid? RollbackOfReleaseId { get; set; }
    public string? RegistrationSource { get; set; }
    public string? RegisteredBy { get; set; }
    public DateTime PublishedAtUtc { get; set; }
    public bool IsCurrentHead { get; set; }
    public bool Activatable { get; set; }
    public string? BlockedReason { get; set; }
}

public sealed class PosHandheldUpdatePolicyRevisionDto
{
    public Guid Id { get; set; }
    public string Lane { get; set; } = string.Empty;
    public long PolicyVersion { get; set; }
    public string Action { get; set; } = "save";
    public PosHandheldUpdatePolicyDto Snapshot { get; set; } = new();
    public DateTime CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
}
