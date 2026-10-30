import {
  appVersionKey,
  countAppVersionStates,
  buildRegisteredVersionSegments,
  compareVersions,
  formatAppVersion,
  getAppVersionBadge,
  getRegisteredVersionBadge,
  getRelativeAge,
  groupAppVersionDistribution,
  normalizeVersion,
  parseUtcTimestamp,
} from "./version-state";

function assertEqual(actual: unknown, expected: unknown, label: string) {
  const a = JSON.stringify(actual);
  const e = JSON.stringify(expected);
  if (a !== e) {
    throw new Error(`${label}: expected ${e}, got ${a}`);
  }
}

// 版本规范化：与后端同一规则。
assertEqual(normalizeVersion("1.0.96"), "1.0.96", "plain");
assertEqual(normalizeVersion("v1.0.96+abc"), "1.0.96", "prefix and suffix are ignored");
assertEqual(normalizeVersion("1.0.96.3"), "1.0.96.3", "keeps the fourth segment");
assertEqual(normalizeVersion("1.0"), null, "two segments are not a version");
assertEqual(normalizeVersion(""), null, "blank");
assertEqual(normalizeVersion(undefined), null, "undefined");

// 比较按数值，不按字符串。
assertEqual(compareVersions("1.0.10", "1.0.9"), 1, "numeric compare");
assertEqual(compareVersions("1.0.95", "1.0.96"), -1, "lower");
assertEqual(compareVersions("1.0.96", "1.0.96.0"), 0, "missing fourth segment is zero");
assertEqual(compareVersions("abc", "1.0.96"), null, "unparsable");

// 设备注册徽标。
assertEqual(getRegisteredVersionBadge({ versionState: undefined }), null, "no state, no badge");
assertEqual(getRegisteredVersionBadge({ versionState: "latest" })?.tone, "ok", "latest tone");
assertEqual(
  getRegisteredVersionBadge({ versionState: "behind", targetAppVersion: "1.0.96" }),
  {
    tone: "warn",
    labelKey: "versionState.behind",
    labelParams: { target: "1.0.96" },
    hintKey: "versionState.behindHint",
  },
  "behind carries the target",
);
assertEqual(getRegisteredVersionBadge({ versionState: "notTargeted", targetAppVersion: "1.0.96" })?.tone, "muted", "not targeted is muted");
assertEqual(getRegisteredVersionBadge({ versionState: "ahead", targetAppVersion: "1.0.96" })?.tone, "info", "ahead is informational");
assertEqual(getRegisteredVersionBadge({ versionState: "unreported" })?.labelKey, "versionState.unreported", "unreported label");

// App 徽标与版本展示。
assertEqual(formatAppVersion("1.0.12", "76"), "1.0.12 (76)", "version and build");
assertEqual(formatAppVersion("1.0.12", undefined), "1.0.12", "version only");
assertEqual(formatAppVersion(undefined, undefined), null, "none");
assertEqual(
  getAppVersionBadge({ versionState: "behind", latestAppVersion: "1.0.11", latestAppBuildVersion: "64" })?.hintParams,
  { latest: "1.0.11 (64)" },
  "behind hints the latest build",
);
assertEqual(getAppVersionBadge({ versionState: undefined }), null, "no app badge without state");

// 设备注册分布条：每个版本相对目标版本着色。
assertEqual(
  buildRegisteredVersionSegments({
    targetVersion: "1.0.96",
    total: 4,
    items: [
      { appVersion: "1.0.97", total: 1 },
      { appVersion: "1.0.96", total: 1 },
      { appVersion: "1.0.95", total: 1 },
      { appVersion: null, total: 1 },
    ],
    states: { latest: 1, behind: 1, ahead: 1, notTargeted: 0, unreported: 1, notApplicable: 0 },
  }).map((segment) => segment.tone),
  ["info", "ok", "warn", "muted"],
  "segment tones",
);
assertEqual(
  buildRegisteredVersionSegments({
    total: 1,
    items: [{ appVersion: "1.0.95", total: 1 }],
    states: { latest: 0, behind: 0, ahead: 0, notTargeted: 0, unreported: 0, notApplicable: 1 },
  }).map((segment) => segment.tone),
  ["muted"],
  "no policy target means no comparison",
);
assertEqual(buildRegisteredVersionSegments(undefined), [], "no summary");

// App 版本分布按系统分组：iOS、Android 在前；无版本行不进选项；每组第一项是该系统最新。
const groups = groupAppVersionDistribution({
  total: 6,
  items: [
    { deviceSystem: "Android", appVersion: "1.0.12", appBuildVersion: "76", total: 18, online: 10 },
    { deviceSystem: "Android", appVersion: "1.0.11", appBuildVersion: "74", total: 5, online: 1 },
    { deviceSystem: "Android", total: 2, online: 0 },
    { deviceSystem: "iOS", appVersion: "1.0.11", appBuildVersion: "64", total: 7, online: 4 },
    { appVersion: "9.9.9", total: 1, online: 0 },
    { deviceSystem: "Windows", appVersion: "2.0.0", appBuildVersion: "1", total: 1, online: 1 },
  ],
});
assertEqual(groups.map((group) => group.deviceSystem), ["iOS", "Android", "Windows", ""], "system order");
assertEqual(groups[1].options.map((option) => [option.appVersion, option.isLatest]), [["1.0.12", true], ["1.0.11", false]], "latest flag");
assertEqual(groups[1].options[0].key, appVersionKey({ deviceSystem: "Android", appVersion: "1.0.12", appBuildVersion: "76" }), "stable key");
assertEqual(groupAppVersionDistribution(null), [], "no distribution");

// 状态预设数量：每个系统第一个有版本的行是最新，其余落后，无版本归未上报。
const stateDistribution = {
  total: 29,
  items: [
    { deviceSystem: "Android", appVersion: "1.0.12", appBuildVersion: "76", total: 18, online: 10 },
    { deviceSystem: "Android", appVersion: "1.0.11", appBuildVersion: "74", total: 5, online: 1 },
    { deviceSystem: "Android", total: 2, online: 0 },
    { deviceSystem: "iOS", appVersion: "1.0.11", appBuildVersion: "64", total: 3, online: 2 },
    { deviceSystem: "iOS", appVersion: "1.0.10", appBuildVersion: "62", total: 1, online: 0 },
  ],
};
assertEqual(countAppVersionStates(stateDistribution), { latest: 21, behind: 6, unreported: 2 }, "all systems");
assertEqual(countAppVersionStates(stateDistribution, "Android"), { latest: 18, behind: 5, unreported: 2 }, "android only");
assertEqual(countAppVersionStates(stateDistribution, "ios"), { latest: 3, behind: 1, unreported: 0 }, "system match is case-insensitive");
assertEqual(countAppVersionStates(null), { latest: 0, behind: 0, unreported: 0 }, "no distribution");

// 相对时间。
const now = new Date("2026-10-30T04:00:00Z");
assertEqual(getRelativeAge("2026-10-30T03:59:30Z", now), { unit: "justNow" }, "within a minute");
assertEqual(getRelativeAge("2026-10-30T03:57:00Z", now), { unit: "minutes", count: 3 }, "minutes");
assertEqual(getRelativeAge("2026-10-30T01:10:00Z", now), { unit: "hours", count: 2 }, "hours");
assertEqual(getRelativeAge("2026-10-27T04:00:00Z", now), { unit: "days", count: 3 }, "days");
assertEqual(getRelativeAge("2026-10-30T05:00:00Z", now), null, "future");
assertEqual(getRelativeAge("not a date", now), null, "invalid");
assertEqual(getRelativeAge(null, now), null, "empty");

// 无时区后缀的时间按 UTC 处理。
assertEqual(parseUtcTimestamp("2026-10-30T03:57:00"), "2026-10-30T03:57:00Z", "adds Z");
assertEqual(parseUtcTimestamp("2026-10-30T03:57:00Z"), "2026-10-30T03:57:00Z", "keeps Z");
assertEqual(parseUtcTimestamp("2026-10-30T03:57:00+10:00"), "2026-10-30T03:57:00+10:00", "keeps offset");
assertEqual(parseUtcTimestamp(undefined), null, "empty timestamp");

console.log("version-state ok");
