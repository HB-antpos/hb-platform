using System.Text.RegularExpressions;
using BlazorApp.Shared.DTOs;

namespace BlazorApp.Api.Services
{
    /// <summary>设备版本状态的取值，前后端共用同一套字符串。</summary>
    public static class DeviceVersionStateNames
    {
        /// <summary>已是策略版本（或该系统最新原生包）。</summary>
        public const string Latest = "latest";

        /// <summary>低于目标版本，待升级。</summary>
        public const string Behind = "behind";

        /// <summary>高于策略目标版本（策略被回退到旧版本时出现）。</summary>
        public const string Ahead = "ahead";

        /// <summary>策略只放量到指定分店 / 设备，这台不在范围内，不会被推送。</summary>
        public const string NotTargeted = "notTargeted";

        /// <summary>没有上报版本（旧版客户端或版本无法识别）。</summary>
        public const string Unreported = "unreported";
    }

    /// <summary>
    /// WPF 更新策略的目标版本与放量范围快照，用于判断某台设备「会不会被推到哪个版本」。
    /// 范围口径与 <c>WpfAppReleaseService.DoesPolicyTargetMatchAsync</c> 一致：
    /// all 全部设备；stores 按设备当前分店代码；devices 按设备注册 ID。
    /// </summary>
    public sealed class WpfPolicyTargetSnapshot
    {
        public WpfPolicyTargetSnapshot(
            string targetVersion,
            string scope,
            IEnumerable<string>? storeCodes = null,
            IEnumerable<int>? deviceIds = null
        )
        {
            TargetVersion = targetVersion;
            Scope = scope;
            StoreCodes = new HashSet<string>(
                storeCodes ?? Array.Empty<string>(),
                StringComparer.OrdinalIgnoreCase
            );
            DeviceIds = new HashSet<int>(deviceIds ?? Array.Empty<int>());
        }

        public string TargetVersion { get; }

        public string Scope { get; }

        public IReadOnlySet<string> StoreCodes { get; }

        public IReadOnlySet<int> DeviceIds { get; }

        public bool Targets(int deviceId, string? storeCode)
        {
            return Scope switch
            {
                "devices" => DeviceIds.Contains(deviceId),
                "stores" => !string.IsNullOrWhiteSpace(storeCode) && StoreCodes.Contains(storeCode.Trim()),
                _ => true,
            };
        }
    }

    /// <summary>
    /// 设备注册列表的版本判断：规范化版本号、比较、给设备打状态、按版本 / 状态筛选、生成汇总。
    /// 全部是不依赖数据库的纯函数。
    /// </summary>
    public static class DeviceVersionStateResolver
    {
        // 只取开头的 major.minor.patch[.build]，忽略「+提交号」「-beta」之类后缀，
        // 这样同一版本的不同构建后缀会归到同一个版本下。
        private static readonly Regex VersionPrefix = new(
            @"^\s*[vV]?(?<major>\d+)\.(?<minor>\d+)\.(?<patch>\d+)(?:\.(?<build>\d+))?",
            RegexOptions.Compiled | RegexOptions.CultureInvariant
        );

        /// <summary>规范化为 1.0.96（有第四段时为 1.0.96.3）；无法识别返回 null。</summary>
        public static string? NormalizeVersion(string? value)
        {
            return TryParse(value, out var normalized, out _) ? normalized : null;
        }

        public static bool TryParse(string? value, out string normalized, out long[] parts)
        {
            normalized = string.Empty;
            parts = Array.Empty<long>();
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            var match = VersionPrefix.Match(value);
            if (!match.Success)
            {
                return false;
            }

            var segments = new List<long>();
            foreach (var name in new[] { "major", "minor", "patch", "build" })
            {
                var group = match.Groups[name];
                if (!group.Success)
                {
                    continue;
                }

                if (!long.TryParse(group.Value, out var number))
                {
                    return false;
                }

                segments.Add(number);
            }

            parts = segments.ToArray();
            normalized = string.Join('.', parts);
            return true;
        }

        /// <summary>按数值比较两个版本；缺少第四段视为 0。任一无法识别时返回 null。</summary>
        public static int? Compare(string? left, string? right)
        {
            if (!TryParse(left, out _, out var leftParts) || !TryParse(right, out _, out var rightParts))
            {
                return null;
            }

            for (var index = 0; index < Math.Max(leftParts.Length, rightParts.Length); index++)
            {
                var leftValue = index < leftParts.Length ? leftParts[index] : 0;
                var rightValue = index < rightParts.Length ? rightParts[index] : 0;
                if (leftValue != rightValue)
                {
                    return leftValue.CompareTo(rightValue);
                }
            }

            return 0;
        }

        /// <summary>WPF 更新策略只管 Windows POS；设备系统为空的老记录按 Windows 处理（与列表的 Windows 筛选一致）。</summary>
        public static bool IsWpfPosDevice(string? deviceType, string? deviceSystem)
        {
            if (!string.Equals(deviceType?.Trim(), "POS", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var system = deviceSystem?.Trim();
            return string.IsNullOrEmpty(system)
                || string.Equals(system, "Windows", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 计算单台设备的版本状态；不参与版本判断的设备（非 Windows POS）返回 null。
        /// 没有策略时只能区分「未上报」，其余返回 null。
        /// </summary>
        public static string? Resolve(
            WpfPolicyTargetSnapshot? snapshot,
            string? deviceType,
            string? deviceSystem,
            int deviceId,
            string? storeCode,
            string? appVersion
        )
        {
            if (!IsWpfPosDevice(deviceType, deviceSystem))
            {
                return null;
            }

            // 没有版本就谈不上比较，先于「是否在放量范围」判断，因为「未上报」对排查更有用。
            if (!TryParse(appVersion, out _, out _))
            {
                return DeviceVersionStateNames.Unreported;
            }

            if (snapshot == null || !TryParse(snapshot.TargetVersion, out _, out _))
            {
                return null;
            }

            if (!snapshot.Targets(deviceId, storeCode))
            {
                return DeviceVersionStateNames.NotTargeted;
            }

            var comparison = Compare(appVersion, snapshot.TargetVersion);
            return comparison switch
            {
                0 => DeviceVersionStateNames.Latest,
                < 0 => DeviceVersionStateNames.Behind,
                > 0 => DeviceVersionStateNames.Ahead,
                null => null,
            };
        }

        /// <summary>给列表设备补上目标版本和版本状态；设备的 AppVersion 同时规范化，保证前端分组口径一致。</summary>
        public static void Annotate(IEnumerable<DeviceListItemDto> devices, WpfPolicyTargetSnapshot? snapshot)
        {
            foreach (var device in devices)
            {
                var state = Resolve(
                    snapshot,
                    device.DeviceType,
                    device.DeviceSystem,
                    device.Id,
                    device.StoreCode,
                    device.AppVersion
                );
                device.VersionState = state;
                device.TargetAppVersion =
                    IsWpfPosDevice(device.DeviceType, device.DeviceSystem) ? snapshot?.TargetVersion : null;
            }
        }

        /// <summary>
        /// 解析 versionState 查询参数：空或 all 表示不筛选；未知取值返回 false，由调用方回 400。
        /// </summary>
        public static bool TryNormalizeState(string? raw, out string? state)
        {
            state = null;
            var value = raw?.Trim();
            if (string.IsNullOrEmpty(value) || string.Equals(value, "all", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            foreach (var known in new[]
            {
                DeviceVersionStateNames.Latest,
                DeviceVersionStateNames.Behind,
                DeviceVersionStateNames.Ahead,
                DeviceVersionStateNames.NotTargeted,
                DeviceVersionStateNames.Unreported,
            })
            {
                if (string.Equals(value, known, StringComparison.OrdinalIgnoreCase))
                {
                    state = known;
                    return true;
                }
            }

            return false;
        }

        /// <summary>逗号分隔的版本列表 → 规范化去重集合；无法识别的片段直接丢弃，最多取 30 个。</summary>
        public static HashSet<string> ParseVersionList(string? raw)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            if (string.IsNullOrWhiteSpace(raw))
            {
                return result;
            }

            foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var normalized = NormalizeVersion(part);
                if (normalized != null)
                {
                    result.Add(normalized);
                }

                if (result.Count >= 30)
                {
                    break;
                }
            }

            return result;
        }

        /// <summary>设备是否满足版本条件：版本集合与状态两项都给时取交集。</summary>
        public static bool Matches(DeviceListItemDto device, IReadOnlySet<string> versions, string? state)
        {
            if (state != null && !string.Equals(device.VersionState, state, StringComparison.Ordinal))
            {
                return false;
            }

            return versions.Count == 0 || (NormalizeVersion(device.AppVersion) is { } v && versions.Contains(v));
        }

        /// <summary>按版本分组并统计各状态设备数；已完成 <see cref="Annotate"/> 的列表才能传入。</summary>
        public static DeviceVersionSummaryDto BuildSummary(
            IReadOnlyCollection<DeviceListItemDto> devices,
            WpfPolicyTargetSnapshot? snapshot
        )
        {
            var summary = new DeviceVersionSummaryDto
            {
                TargetVersion = snapshot?.TargetVersion,
                TargetScope = snapshot?.Scope,
                Total = devices.Count,
            };

            foreach (var device in devices)
            {
                switch (device.VersionState)
                {
                    case DeviceVersionStateNames.Latest: summary.States.Latest++; break;
                    case DeviceVersionStateNames.Behind: summary.States.Behind++; break;
                    case DeviceVersionStateNames.Ahead: summary.States.Ahead++; break;
                    case DeviceVersionStateNames.NotTargeted: summary.States.NotTargeted++; break;
                    case DeviceVersionStateNames.Unreported: summary.States.Unreported++; break;
                    default: summary.States.NotApplicable++; break;
                }
            }

            // 只有 Windows POS 才会上报版本；其他设备没有版本不算「未上报」，不进版本分组，避免把无关设备混进来。
            var groups = devices
                .Where(device =>
                    NormalizeVersion(device.AppVersion) != null
                    || IsWpfPosDevice(device.DeviceType, device.DeviceSystem)
                )
                .GroupBy(device => NormalizeVersion(device.AppVersion))
                .Select(group => new DeviceVersionSummaryItemDto
                {
                    AppVersion = group.Key,
                    Total = group.Count(),
                })
                .ToList();
            groups.Sort(
                (left, right) =>
                    left.AppVersion == null || right.AppVersion == null
                        ? (left.AppVersion == null).CompareTo(right.AppVersion == null)
                        : -(Compare(left.AppVersion, right.AppVersion) ?? 0)
            );
            summary.Items = groups;
            return summary;
        }
    }
}
