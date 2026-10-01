namespace BlazorApp.Api.Features.LegacyEmployeeLogs.Risk;

/// <summary>
/// 异常规则扫描配置（配置节 LegacyEmployeeLogRisk）。阈值默认值按 2026-10-01 生产抽样校准：
/// 1003/1005/1008 三店 7 天内各规则命中 22 / 10 / 5 / 11 / 0 / 0 次，折算 25 店约每天 50 条。
/// 新收银操作审计的扫描共用同一开关、节奏与阈值（规则编号相同，核查口径一致），
/// 只多一个大额折扣金额下限，见 <see cref="BigDiscountMinAmount"/>。
/// </summary>
public sealed class LegacyEmployeeLogRiskOptions
{
    public const string SectionName = "LegacyEmployeeLogRisk";

    /// <summary>总开关；默认关闭，迁移执行并观察后再打开。</summary>
    public bool Enabled { get; set; }

    /// <summary>新收银操作审计的扫描子开关（总开关打开时才生效）；新收银风险表未迁移时自动跳过。</summary>
    public bool PosAuditEnabled { get; set; } = true;

    /// <summary>常规扫描间隔与回看时长：日志由收银机批量上传，通常滞后十几到几十分钟。</summary>
    public int QuickIntervalMinutes { get; set; } = 15;

    public int QuickLookbackHours { get; set; } = 6;

    /// <summary>深度扫描：每个 UTC 日在该小时之后跑一次（默认 17 点 = 悉尼凌晨 3–4 点），按天回看，兜住晚到很久的日志与阈值调整。</summary>
    public int DeepScanUtcHour { get; set; } = 17;

    public int DeepLookbackDays { get; set; } = 7;

    /// <summary>开钱箱前后多少秒内同设备有「结账」就视为交易中的开钱箱。</summary>
    public int DrawerCheckoutWindowSeconds { get; set; } = 120;

    public int DeleteAfterCheckoutWindowSeconds { get; set; } = 120;

    public decimal DeleteAfterCheckoutMinAmount { get; set; } = 10m;

    /// <summary>单品或整单折扣达到该百分比（减免比例）即命中。</summary>
    public decimal BigDiscountMinPercent { get; set; } = 50m;

    /// <summary>改价降幅达到该比例（0.5 = 新价不高于原价一半）即命中。</summary>
    public decimal BigPriceCutMinRatio { get; set; } = 0.5m;

    /// <summary>
    /// 仅新收银：折扣 / 降价让利金额下限。新收银有 50% 快捷折扣按钮，1013 一周 70 次单品折扣里 56 次达到 50%，
    /// 其中让利 ≥ 10 元的 13 次；只按比例判定会每天七八条，核查不过来。
    /// </summary>
    public decimal BigDiscountMinAmount { get; set; } = 10m;

    public int BurstDeleteWindowMinutes { get; set; } = 10;

    public int BurstDeleteMinCount { get; set; } = 8;

    public int RepeatReprintMinCount { get; set; } = 3;

    /// <summary>营业时间（门店墙钟），之外的操作按非营业时间处理。</summary>
    public TimeSpan BusinessOpen { get; set; } = new(7, 0, 0);

    public TimeSpan BusinessClose { get; set; } = new(22, 0, 0);
}
