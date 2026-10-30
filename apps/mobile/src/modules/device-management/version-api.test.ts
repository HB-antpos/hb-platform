import {
  buildAppDeviceStatusListParams,
  buildListParams,
  joinVersionList,
  normalizeAppDeviceStatusListResponse,
  normalizeAppVersionDistribution,
  normalizeDeviceManagementListResponse,
  normalizeDeviceVersionSummary,
} from "./api";

function assertEqual(actual: unknown, expected: unknown, label: string) {
  const a = JSON.stringify(actual);
  const e = JSON.stringify(expected);
  if (a !== e) {
    throw new Error(`${label}: expected ${e}, got ${a}`);
  }
}

// 设备注册：版本多选合并成逗号串，去空白、去重、保持顺序。
assertEqual(joinVersionList([" 1.0.96 ", "1.0.95", "1.0.96", ""]), "1.0.96,1.0.95", "joins unique versions");
assertEqual(joinVersionList([]), undefined, "empty list is omitted");
assertEqual(joinVersionList(null), undefined, "null list is omitted");

const versioned = buildListParams({ appVersions: ["1.0.96"], versionState: "behind", keyword: " pos " });
assertEqual(versioned.appVersions, "1.0.96", "versions are sent");
assertEqual(versioned.versionState, "behind", "state is sent");
assertEqual(versioned.keyword, "pos", "other params are unchanged");

// all 只存在于前端界面，不能传给后端的状态枚举。
const unfiltered = buildListParams({ appVersions: [], versionState: "all" });
assertEqual(unfiltered.appVersions, undefined, "no versions, no param");
assertEqual(unfiltered.versionState, undefined, "all is not sent");

// App 使用情况：版本、构建号与状态。
const appParams = buildAppDeviceStatusListParams({
  appVersion: " 1.0.12 ",
  appBuildVersion: " 76 ",
  versionState: "behind",
  deviceSystem: "Android",
  onlineState: "all",
});
assertEqual(appParams.appVersion, "1.0.12", "trims app version");
assertEqual(appParams.appBuildVersion, "76", "trims build");
assertEqual(appParams.versionState, "behind", "app state is sent");
assertEqual(appParams.onlineState, undefined, "all online state is not sent");
// 构建号单独存在没有意义（没有对应的版本行），不传。
assertEqual(buildAppDeviceStatusListParams({ appBuildVersion: "76" }).appBuildVersion, undefined, "build without version is dropped");
assertEqual(buildAppDeviceStatusListParams({ versionState: "all" }).versionState, undefined, "all app state is not sent");

// 设备注册列表归一化：带出目标版本和版本状态，未知状态丢弃。
const registered = normalizeDeviceManagementListResponse({
  data: {
    devices: [
      { id: 1, hardwareId: "HW-1", status: 1, appVersion: "1.0.95", targetAppVersion: "1.0.96", versionState: "behind" },
      { id: 2, hardwareId: "HW-2", status: 1, TargetAppVersion: "1.0.96", VersionState: "NotTargeted" },
      { id: 3, hardwareId: "HW-3", status: 1, versionState: "from-the-future" },
      { id: 4, hardwareId: "HW-4", status: 1 },
    ],
  },
});
assertEqual(registered.devices.map((device) => device.versionState), ["behind", "notTargeted", undefined, undefined], "version states");
assertEqual(registered.devices[0].targetAppVersion, "1.0.96", "target version");
assertEqual(registered.devices[1].targetAppVersion, "1.0.96", "PascalCase keys are accepted");

// 版本汇总归一化。
const summary = normalizeDeviceVersionSummary({
  success: true,
  data: {
    targetVersion: "1.0.96",
    targetScope: "stores",
    total: 4,
    items: [
      { appVersion: "1.0.96", total: 2 },
      { appVersion: null, total: 1 },
      "garbage",
    ],
    states: { latest: 2, behind: 1, ahead: 0, notTargeted: 0, unreported: 1, notApplicable: 0 },
  },
});
assertEqual(summary.targetVersion, "1.0.96", "summary target");
assertEqual(summary.items, [{ appVersion: "1.0.96", total: 2 }, { appVersion: null, total: 1 }], "summary items drop garbage and keep null version");
assertEqual(summary.states.latest, 2, "summary latest");
assertEqual(summary.states.unreported, 1, "summary unreported");
const emptySummary = normalizeDeviceVersionSummary(undefined);
assertEqual(emptySummary.total, 0, "empty summary total");
assertEqual(emptySummary.items, [], "empty summary items");
assertEqual(emptySummary.states.notApplicable, 0, "empty summary states");

// App 设备归一化：最新版本和状态。
const apps = normalizeAppDeviceStatusListResponse({
  data: {
    items: [
      {
        id: "11111111-1111-1111-1111-111111111111",
        hardwareId: "hw-1",
        isOnline: true,
        appVersion: "1.0.10",
        appBuildVersion: "62",
        latestAppVersion: "1.0.11",
        latestAppBuildVersion: "64",
        versionState: "behind",
      },
      { id: "22222222-2222-2222-2222-222222222222", hardwareId: "hw-2", versionState: "weird" },
    ],
    total: 2,
    page: 1,
    pageSize: 20,
  },
});
assertEqual(apps.devices[0].versionState, "behind", "app state");
assertEqual(apps.devices[0].latestAppBuildVersion, "64", "latest build");
assertEqual(apps.devices[1].versionState, undefined, "unknown app state dropped");

// 版本分布归一化。
const distribution = normalizeAppVersionDistribution({
  success: true,
  data: {
    total: 3,
    items: [
      { deviceSystem: "Android", appVersion: "1.0.12", appBuildVersion: "76", total: 2, online: 1, ota: 1, embedded: 1 },
      { deviceSystem: "iOS", total: 1, online: 0 },
    ],
  },
});
assertEqual(distribution.total, 3, "distribution total");
assertEqual(distribution.items[0], { deviceSystem: "Android", appVersion: "1.0.12", appBuildVersion: "76", total: 2, online: 1 }, "distribution row");
assertEqual(distribution.items[1].appVersion, undefined, "device without version");

console.log("version-api ok");
