import assert from "node:assert/strict";
import {
  applyEdit,
  buildBaseStates,
  buildPatch,
  computeImpact,
  countStoreFilters,
  describeEffective,
  deviceStatusMeta,
  diffStates,
  filterDefinitionsForClient,
  filterStoreSummaries,
  isSettingPendingSync,
  listOverrideEntries,
  previewEffective,
  resolveDisplayedState,
  resolveGlobalParent,
  sortDefinitions,
  sortDevices,
  summarizeStoreApply,
  toTriState,
  triStateToValue,
  type TriState,
} from "./logic";
import type { PosSettingDefinition, PosSettingDeviceStatus, PosSettingStoreSummary } from "./types";

const def = (key: string, clientKinds: string[], defaultWhenUnset: boolean | null = null, isNew = false): PosSettingDefinition => ({
  key,
  clientKinds,
  defaultWhenUnset,
  isNew,
});
const DEFS = [
  def("returns.rescan", ["wpf"]),
  def("cashMovement.enabled", ["wpf", "ipad", "handheld"], false, true),
  def("payment.voucher", ["wpf", "ipad"]),
];

const device = (overrides: Partial<PosSettingDeviceStatus>): PosSettingDeviceStatus => ({
  deviceCode: "D1",
  storeCode: "S1",
  clientKind: "wpf",
  appVersion: "1.0.120",
  status: "applied",
  appliedAtUtc: null,
  hasDeviceOverride: false,
  offlineOverrideCount: 0,
  lastOfflineOverrideAtUtc: null,
  ...overrides,
});

// ───────── 三态值互转 ─────────
assert.equal(toTriState(true), "on");
assert.equal(toTriState(false), "off");
assert.equal(toTriState(null), "inherit");
assert.equal(toTriState(undefined), "inherit");
assert.equal(triStateToValue("on"), true);
assert.equal(triStateToValue("off"), false);
assert.equal(triStateToValue("inherit"), null);

// ───────── 目录：新增项置顶，其余保持顺序；不改入参 ─────────
{
  const original = [...DEFS];
  assert.deepEqual(sortDefinitions(DEFS).map((d) => d.key), ["cashMovement.enabled", "returns.rescan", "payment.voucher"]);
  assert.deepEqual(DEFS, original, "排序不能改入参");
  assert.deepEqual(filterDefinitionsForClient(DEFS, "ipad").map((d) => d.key), ["cashMovement.enabled", "payment.voucher"]);
  assert.deepEqual(filterDefinitionsForClient(DEFS, "handheld").map((d) => d.key), ["cashMovement.enabled"]);
  assert.deepEqual(filterDefinitionsForClient(DEFS, "other"), [], "未知端一个都不适用");
}

// ───────── 草稿：只记录与已保存值不同的项；改回原值即撤销 ─────────
{
  const base = buildBaseStates(DEFS, { "returns.rescan": true, "unknown.key": true });
  assert.deepEqual(base, { "returns.rescan": "on", "cashMovement.enabled": "inherit", "payment.voucher": "inherit" });

  let edits: Record<string, TriState> = {};
  edits = applyEdit(edits, base, "returns.rescan", "off");
  assert.deepEqual(edits, { "returns.rescan": "off" });
  assert.equal(resolveDisplayedState("returns.rescan", base, edits), "off");
  assert.equal(resolveDisplayedState("payment.voucher", base, edits), "inherit");
  const before = edits;
  edits = applyEdit(edits, base, "returns.rescan", "on");
  assert.deepEqual(edits, {}, "改回已保存的值就不再算变更");
  assert.deepEqual(before, { "returns.rescan": "off" }, "applyEdit 不改入参");
  assert.deepEqual(applyEdit({}, base, "payment.voucher", "inherit"), {}, "选原来的值不产生草稿");
}

// ───────── 变更差异与请求体（全局 / 门店 / 设备同一套） ─────────
{
  const base = buildBaseStates(DEFS, { "returns.rescan": true, "payment.voucher": false });
  const edits: Record<string, TriState> = {
    "returns.rescan": "inherit", // 清除
    "cashMovement.enabled": "on", // 新设置
    "payment.voucher": "on", // 关 → 开
  };
  const changes = diffStates(DEFS, base, edits);
  // 按目录顺序输出
  assert.deepEqual(changes, [
    { key: "returns.rescan", from: "on", to: "inherit" },
    { key: "cashMovement.enabled", from: "inherit", to: "on" },
    { key: "payment.voucher", from: "off", to: "on" },
  ]);
  assert.deepEqual(buildPatch(changes), { "returns.rescan": null, "cashMovement.enabled": true, "payment.voucher": true });
  assert.deepEqual(diffStates(DEFS, base, {}), [], "没改动没有差异");
  assert.deepEqual(buildPatch([]), {});
  assert.deepEqual(
    diffStates(DEFS, base, { "ghost.key": "on" }),
    [],
    "目录里没有的键不进变更（避免把未知键发给服务端）",
  );
}

// ───────── 生效值：全局之上取缺省，门店 / 设备 inherit 取上一层 ─────────
{
  const cash = DEFS[1];
  const rescan = DEFS[0];
  assert.deepEqual(resolveGlobalParent(rescan, { "returns.rescan": true }), { value: true, source: "global" });
  assert.deepEqual(resolveGlobalParent(rescan, { "returns.rescan": false }), { value: false, source: "global" });
  assert.deepEqual(resolveGlobalParent(cash, {}), { value: false, source: "default" }, "新功能全局未设置按缺省关");
  assert.deepEqual(resolveGlobalParent(rescan, {}), { value: null, source: "none" }, "无缺省 = 沿用本机");

  const parent = { value: false, source: "global" } as const;
  assert.deepEqual(previewEffective("store", "inherit", parent), parent);
  assert.deepEqual(previewEffective("store", "on", parent), { value: true, source: "store" });
  assert.deepEqual(previewEffective("device", "off", { value: true, source: "store" }), { value: false, source: "device" });
  assert.deepEqual(previewEffective("global", "on", { value: null, source: "none" }), { value: true, source: "global" });

  assert.deepEqual(describeEffective({ value: true, source: "store" }), { value: "on", source: "store" });
  assert.deepEqual(describeEffective({ value: false, source: "default" }), { value: "off", source: "default" });
  assert.deepEqual(describeEffective({ value: null, source: "none" }), { value: "local", source: null });
}

// ───────── 设备状态标签：applied 绿 / pending 橙 / never 灰（带说明） ─────────
assert.deepEqual(deviceStatusMeta("applied"), { tone: "success", labelKey: "applied", hint: false });
assert.deepEqual(deviceStatusMeta("pending"), { tone: "warning", labelKey: "pending", hint: false });
assert.deepEqual(deviceStatusMeta("never"), { tone: "neutral", labelKey: "never", hint: true });

// ───────── 设备页「待同步」：应生效 vs 设备回报 ─────────
{
  const on = { value: true, source: "store" } as const;
  assert.equal(isSettingPendingSync("a", on, { a: true }, "applied"), false, "一致");
  assert.equal(isSettingPendingSync("a", on, { a: false }, "applied"), true, "回报与应生效不同");
  assert.equal(isSettingPendingSync("a", { value: null, source: "none" }, { a: false }, "applied"), false, "沿用本机不比较");
  assert.equal(isSettingPendingSync("a", undefined, { a: false }, "pending"), false, "没有应生效值不比较");
  assert.equal(isSettingPendingSync("a", on, {}, "pending"), true, "待同步设备没回报这一项 = 待同步");
  assert.equal(isSettingPendingSync("a", on, {}, "never"), false, "从未回执的设备只在状态上标「尚未同步」");
  assert.equal(isSettingPendingSync("a", on, {}, "applied"), false, "已应用最新但旧版本不回报这项，不重复标记");
}

// ───────── 门店列表：应用情况 / 筛选 / 搜索 ─────────
{
  const store = (storeCode: string, storeName: string, extra: Partial<PosSettingStoreSummary> = {}): PosSettingStoreSummary => ({
    storeCode,
    storeName,
    overrides: {},
    deviceOverrideCount: 0,
    deviceTotal: 0,
    deviceApplied: 0,
    ...extra,
  });
  const stores = [
    store("ORI", "Orion", { overrides: { "cashMovement.enabled": true }, deviceTotal: 3, deviceApplied: 3 }),
    store("BT1", "Blacktown", { deviceTotal: 2, deviceApplied: 1 }),
    store("LVP", "Liverpool", { deviceTotal: 4, deviceApplied: 4, deviceOverrideCount: 1 }),
    store("EMP", "Empty"),
  ];
  assert.deepEqual(countStoreFilters(stores), { all: 4, overrides: 2, unsynced: 1 });
  assert.deepEqual(filterStoreSummaries(stores, "", "all").map((s) => s.storeCode), ["BT1", "EMP", "LVP", "ORI"], "按名称排序");
  assert.deepEqual(filterStoreSummaries(stores, "", "overrides").map((s) => s.storeCode), ["LVP", "ORI"], "设备单独覆盖也算有覆盖");
  assert.deepEqual(filterStoreSummaries(stores, "", "unsynced").map((s) => s.storeCode), ["BT1"]);
  assert.deepEqual(filterStoreSummaries(stores, "  ori ", "all").map((s) => s.storeCode), ["ORI"], "按代码搜索，忽略大小写与空白");
  assert.deepEqual(filterStoreSummaries(stores, "town", "all").map((s) => s.storeCode), ["BT1"], "按名称搜索");
  assert.deepEqual(filterStoreSummaries(stores, "town", "overrides"), [], "搜索与筛选叠加");

  assert.deepEqual(summarizeStoreApply(stores[0]), { kind: "applied", applied: 3, total: 3 });
  assert.deepEqual(summarizeStoreApply(stores[1]), { kind: "pending", applied: 1, total: 2 });
  assert.deepEqual(summarizeStoreApply(stores[3]), { kind: "none", applied: 0, total: 0 });
  assert.deepEqual(
    summarizeStoreApply(store("X", "X", { deviceTotal: 2, deviceApplied: 5 })),
    { kind: "applied", applied: 2, total: 2 },
    "已应用数超过总数时按总数封顶",
  );

  assert.deepEqual(
    listOverrideEntries(DEFS, { "payment.voucher": false, "returns.rescan": true, "zzz.unknown": true }),
    [
      { key: "returns.rescan", value: true },
      { key: "payment.voucher", value: false },
      { key: "zzz.unknown", value: true },
    ],
    "覆盖项按目录顺序，未知键放最后",
  );
}

// ───────── 保存确认：影响设备数与「尚未同步」提示 ─────────
{
  const devices = [
    device({ deviceCode: "ORI-10", clientKind: "wpf" }),
    device({ deviceCode: "ORI-02", clientKind: "ipad" }),
    device({ deviceCode: "ORI-03", clientKind: "wpf", status: "never", appVersion: "1.0.113" }),
    device({ deviceCode: "ORI-04", clientKind: "handheld", status: "never" }),
  ];
  // 只改 returns.rescan（仅 WPF 适用）：2 台 WPF 受影响，其中 ORI-03 尚未同步
  const impact = computeImpact(devices, DEFS, [{ key: "returns.rescan", from: "inherit", to: "on" }]);
  assert.equal(impact.affectedCount, 2);
  assert.deepEqual(impact.notSynced.map((d) => d.deviceCode), ["ORI-03"]);
  // 改全端适用的项：4 台都受影响
  const all = computeImpact(devices, DEFS, [{ key: "cashMovement.enabled", from: "inherit", to: "on" }]);
  assert.equal(all.affectedCount, 4);
  assert.deepEqual(all.notSynced.map((d) => d.deviceCode), ["ORI-03", "ORI-04"]);
  assert.equal(computeImpact(devices, DEFS, []).affectedCount, 0);
  assert.deepEqual(sortDevices(devices).map((d) => d.deviceCode), ["ORI-02", "ORI-03", "ORI-04", "ORI-10"], "设备编号按数字序");
}

console.log("logic.test.ts: ok");
