namespace Hbpos.Contracts.PosSettings;

/// <summary>
/// 收银端设置下发的接口数据结构。设置项键见 BlazorApp.Shared.Constants.PosSettingKeys（键一经发布不能改名）；
/// 新增字段只能追加在末尾并带默认值，保证新旧客户端与服务端互相兼容。
/// </summary>
public sealed record PosSettingValueDto(bool Value, string Source);

/// <summary>
/// 同步响应。Changed 为 false 表示客户端手里的指纹已是最新，不带 Settings；
/// 为 true 时 Settings 是该设备（按所属门店、设备编号合并三层后）适用的全部「已管理」项。
/// Settings 里没出现的键 = 三层都没设置，客户端沿用本机值。
/// </summary>
public sealed record PosSettingsSyncDto(
    bool Changed,
    string Hash,
    IReadOnlyDictionary<string, PosSettingValueDto>? Settings);

/// <summary>
/// 回执：客户端应用完设置后，回传已应用的指纹和「实际生效值」（含本机离线临时修改后的值），
/// 以及累计离线临时修改次数。后台据此显示设备是否已用上最新设置、以及设备当前到底开着哪些开关。
/// </summary>
public sealed record PosSettingsAckRequest(
    string Hash,
    IReadOnlyDictionary<string, bool>? Applied,
    string? AppVersion,
    int OfflineOverrideCount = 0);

public sealed record PosSettingsAckResultDto(string AppliedHash);
