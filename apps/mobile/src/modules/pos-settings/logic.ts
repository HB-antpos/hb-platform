// 收银端设置的纯逻辑：三态值、生效值预览、变更差异、状态标签、列表筛选。不依赖 React / expo，方便 tsx 单测。
import type {
  PosSettingDefinition,
  PosSettingDeviceStatus,
  PosSettingDeviceStatusKind,
  PosSettingEffective,
  PosSettingScope,
  PosSettingStoreSummary,
  PosSettingsPatch,
} from "./types";

// ───────────────────────── 三态值 ─────────────────────────

/**
 * 三态：inherit（该层没设置，跟随上一层；全局层叫「未设置」= 不下发）/ on / off。
 * 三个层级统一使用，只是 inherit 的文案不同（未设置 / 跟随全局 / 跟随门店）。
 */
export type TriState = "inherit" | "on" | "off";

export const TRI_STATES: readonly TriState[] = ["inherit", "on", "off"];

export function toTriState(value: boolean | null | undefined): TriState {
  if (value === true) return "on";
  if (value === false) return "off";
  return "inherit";
}

export function triStateToValue(state: TriState): boolean | null {
  if (state === "on") return true;
  if (state === "off") return false;
  return null;
}

/** 把某层已设置的值（字典里没出现 = inherit）展开成每个设置项的三态。 */
export function buildBaseStates(
  definitions: readonly PosSettingDefinition[],
  values: Readonly<Record<string, boolean>>,
): Record<string, TriState> {
  const states: Record<string, TriState> = {};
  for (const definition of definitions) {
    states[definition.key] = toTriState(values[definition.key]);
  }
  return states;
}

/** 当前显示的三态：用户改过的取草稿，没改的取已保存值。 */
export function resolveDisplayedState(
  key: string,
  base: Readonly<Record<string, TriState>>,
  edits: Readonly<Record<string, TriState>>,
): TriState {
  return edits[key] ?? base[key] ?? "inherit";
}

/**
 * 记录一次编辑。改回已保存的值就把这一项从草稿里去掉，这样「改了又改回来」不算变更。
 * 返回新对象，不改入参。
 */
export function applyEdit(
  edits: Readonly<Record<string, TriState>>,
  base: Readonly<Record<string, TriState>>,
  key: string,
  next: TriState,
): Record<string, TriState> {
  const copy = { ...edits };
  if ((base[key] ?? "inherit") === next) {
    delete copy[key];
  } else {
    copy[key] = next;
  }
  return copy;
}

// ───────────────────────── 生效值 ─────────────────────────

/** 上一层（全局层之上）的生效值：全局已设置取全局；否则有服务端缺省值取缺省；否则沿用本机。 */
export function resolveGlobalParent(
  definition: PosSettingDefinition,
  globalValues: Readonly<Record<string, boolean>>,
): PosSettingEffective {
  const globalValue = globalValues[definition.key];
  if (typeof globalValue === "boolean") return { value: globalValue, source: "global" };
  if (definition.defaultWhenUnset !== null) return { value: definition.defaultWhenUnset, source: "default" };
  return { value: null, source: "none" };
}

/**
 * 该层在草稿状态下的生效值预览：自己设置了（on / off）就取自己；inherit 取上一层的生效值（parent）。
 * 门店层的 parent 来自 resolveGlobalParent，设备层的 parent 是本店层的生效值（服务端 stores/{code} 的 effective）。
 */
export function previewEffective(
  scope: PosSettingScope,
  state: TriState,
  parent: PosSettingEffective,
): PosSettingEffective {
  if (state === "on") return { value: true, source: scope };
  if (state === "off") return { value: false, source: scope };
  return parent;
}

export type EffectiveValueKind = "on" | "off" | "local";

/** 生效值的展示拆分：值（开 / 关 / 沿用本机）与来源；沿用本机没有来源。 */
export function describeEffective(effective: PosSettingEffective): {
  value: EffectiveValueKind;
  source: PosSettingEffective["source"] | null;
} {
  if (effective.value === null) return { value: "local", source: null };
  return { value: effective.value ? "on" : "off", source: effective.source };
}

// ───────────────────────── 变更差异 ─────────────────────────

export interface PosSettingChange {
  key: string;
  from: TriState;
  to: TriState;
}

/** 草稿与已保存值的差异，按目录顺序输出；只统计目录里存在的项。 */
export function diffStates(
  definitions: readonly PosSettingDefinition[],
  base: Readonly<Record<string, TriState>>,
  edits: Readonly<Record<string, TriState>>,
): PosSettingChange[] {
  const changes: PosSettingChange[] = [];
  for (const definition of definitions) {
    const from = base[definition.key] ?? "inherit";
    const to = edits[definition.key] ?? from;
    if (from !== to) changes.push({ key: definition.key, from, to });
  }
  return changes;
}

/** 保存请求体：只带变更项，inherit → null（清除），on / off → true / false。 */
export function buildPatch(changes: readonly PosSettingChange[]): PosSettingsPatch {
  const patch: PosSettingsPatch = {};
  for (const change of changes) {
    patch[change.key] = triStateToValue(change.to);
  }
  return patch;
}

// ───────────────────────── 目录与适用端 ─────────────────────────

/** 新增项置顶（「新」标记），其余保持目录顺序；不改入参。 */
export function sortDefinitions(definitions: readonly PosSettingDefinition[]): PosSettingDefinition[] {
  return definitions
    .map((definition, index) => ({ definition, index }))
    .sort((a, b) => Number(b.definition.isNew) - Number(a.definition.isNew) || a.index - b.index)
    .map((entry) => entry.definition);
}

export function supportsClientKind(definition: PosSettingDefinition, clientKind: string): boolean {
  return definition.clientKinds.includes(clientKind);
}

/** 设备页只显示该设备端适用的项。 */
export function filterDefinitionsForClient(
  definitions: readonly PosSettingDefinition[],
  clientKind: string,
): PosSettingDefinition[] {
  return definitions.filter((definition) => supportsClientKind(definition, clientKind));
}

// ───────────────────────── 设备状态 ─────────────────────────

export type StatusTone = "success" | "warning" | "neutral";

export interface DeviceStatusMeta {
  tone: StatusTone;
  /** 文案键（posSettings 命名空间下 deviceStatus.*）。 */
  labelKey: "applied" | "pending" | "never";
  /** never 的补充说明：旧版本不支持，或还没联过网。 */
  hint: boolean;
}

/** applied = 已应用最新（绿）、pending = 待同步（橙）、never = 尚未同步（灰，带说明）。 */
export function deviceStatusMeta(status: PosSettingDeviceStatusKind): DeviceStatusMeta {
  switch (status) {
    case "applied":
      return { tone: "success", labelKey: "applied", hint: false };
    case "pending":
      return { tone: "warning", labelKey: "pending", hint: false };
    default:
      return { tone: "neutral", labelKey: "never", hint: true };
  }
}

/**
 * 设备页某项「待同步」：应生效值（服务端合并结果）与设备回报不同。
 * - 应生效为「沿用本机」（null）不比较；
 * - 设备回报里有这一项：值不同即待同步；
 * - 设备回报里没有这一项：只有设备整体状态为 pending 才算待同步
 *   （never 是还没回执，applied 是旧版本不回报该项，这两种都不在这里再标一次）。
 */
export function isSettingPendingSync(
  key: string,
  effective: PosSettingEffective | undefined,
  reported: Readonly<Record<string, boolean>>,
  deviceStatus: PosSettingDeviceStatusKind,
): boolean {
  if (!effective || effective.value === null) return false;
  if (key in reported) return reported[key] !== effective.value;
  return deviceStatus === "pending";
}

export function sortDevices(devices: readonly PosSettingDeviceStatus[]): PosSettingDeviceStatus[] {
  return [...devices].sort((a, b) => a.deviceCode.localeCompare(b.deviceCode, undefined, { numeric: true }));
}

// ───────────────────────── 门店列表 ─────────────────────────

export type StoreListFilter = "all" | "overrides" | "unsynced";

/** 本店有覆盖：门店层设置过项，或有设备单独覆盖。 */
export function storeHasOverrides(summary: PosSettingStoreSummary): boolean {
  return Object.keys(summary.overrides).length > 0 || summary.deviceOverrideCount > 0;
}

/** 本店有设备还没应用最新设置。 */
export function storeHasUnsynced(summary: PosSettingStoreSummary): boolean {
  return summary.deviceApplied < summary.deviceTotal;
}

export interface StoreApplySummary {
  /** none = 本店没有设备；applied = 全部已应用；pending = 还有设备没应用。 */
  kind: "none" | "applied" | "pending";
  applied: number;
  total: number;
}

export function summarizeStoreApply(summary: PosSettingStoreSummary): StoreApplySummary {
  const total = Math.max(0, summary.deviceTotal);
  const applied = Math.min(Math.max(0, summary.deviceApplied), total);
  if (total === 0) return { kind: "none", applied, total };
  return { kind: applied >= total ? "applied" : "pending", applied, total };
}

export function countStoreFilters(summaries: readonly PosSettingStoreSummary[]): Record<StoreListFilter, number> {
  return {
    all: summaries.length,
    overrides: summaries.filter(storeHasOverrides).length,
    unsynced: summaries.filter(storeHasUnsynced).length,
  };
}

/** 搜索门店名称或代码（忽略大小写与首尾空白）+ 筛选；结果按门店名排序。 */
export function filterStoreSummaries(
  summaries: readonly PosSettingStoreSummary[],
  query: string,
  filter: StoreListFilter,
): PosSettingStoreSummary[] {
  const needle = query.trim().toLowerCase();
  return summaries
    .filter((summary) => {
      if (filter === "overrides" && !storeHasOverrides(summary)) return false;
      if (filter === "unsynced" && !storeHasUnsynced(summary)) return false;
      if (!needle) return true;
      return (
        summary.storeName.toLowerCase().includes(needle)
        || summary.storeCode.toLowerCase().includes(needle)
      );
    })
    .sort((a, b) => (a.storeName || a.storeCode).localeCompare(b.storeName || b.storeCode, undefined, { sensitivity: "base" }));
}

/** 门店行上的覆盖项标签：按目录顺序，只含目录里认识的项；未知键放最后，界面回退显示键名。 */
export function listOverrideEntries(
  definitions: readonly PosSettingDefinition[],
  overrides: Readonly<Record<string, boolean>>,
): { key: string; value: boolean }[] {
  const known = definitions
    .filter((definition) => typeof overrides[definition.key] === "boolean")
    .map((definition) => ({ key: definition.key, value: overrides[definition.key] }));
  const knownKeys = new Set(known.map((entry) => entry.key));
  const unknown = Object.keys(overrides)
    .filter((key) => !knownKeys.has(key))
    .sort()
    .map((key) => ({ key, value: overrides[key] }));
  return [...known, ...unknown];
}

// ───────────────────────── 保存确认 ─────────────────────────

export interface ConfirmImpact {
  /** 受影响设备数：本店（或本设备）里适用于任一变更项的设备。 */
  affectedCount: number;
  /** 受影响设备里「尚未同步」的（旧版本不支持，或还没联过网）：升级 / 联网后才会生效。 */
  notSynced: PosSettingDeviceStatus[];
}

/** 门店 / 设备层保存：受影响设备 = 设备端适用于任一变更项的设备。 */
export function computeImpact(
  devices: readonly PosSettingDeviceStatus[],
  definitions: readonly PosSettingDefinition[],
  changes: readonly PosSettingChange[],
): ConfirmImpact {
  const changedDefinitions = definitions.filter((definition) =>
    changes.some((change) => change.key === definition.key),
  );
  const affected = devices.filter((device) =>
    changedDefinitions.some((definition) => supportsClientKind(definition, device.clientKind)),
  );
  return {
    affectedCount: affected.length,
    notSynced: sortDevices(affected.filter((device) => device.status === "never")),
  };
}
