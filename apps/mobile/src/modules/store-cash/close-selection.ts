// 日结存档多选的纯逻辑。
// 默认规则：同设备同营业日取 savedAt 最新的一份（服务端落实，archives[].included 标记当前纳入的）。
// 店长可手选多份求和（必须填原因），也可「恢复默认」。
import { CASH_NOTE_MAX_LENGTH, CASH_REASON_MIN_LENGTH } from "./constants";
import { sumAmounts } from "./money";
import type { CashCloseArchive, CashCloseSelectionRequest, CashDailyDevice } from "./types";

/** 服务端当前纳入现金池的存档编号。 */
export function getIncludedCloseIds(device: Pick<CashDailyDevice, "archives">): string[] {
  return device.archives.filter((archive) => archive.included).map((archive) => archive.closeId);
}

/** 勾选 / 取消勾选一份存档，保持其余顺序不变。 */
export function toggleCloseId(selected: readonly string[], closeId: string): string[] {
  return selected.includes(closeId)
    ? selected.filter((id) => id !== closeId)
    : [...selected, closeId];
}

/** 与集合顺序无关的相等判断，用来判断「是否改动了默认/现有选择」。 */
export function isSameSelection(a: readonly string[], b: readonly string[]): boolean {
  if (a.length !== b.length) return false;
  const set = new Set(a);
  return b.every((id) => set.has(id)) && set.size === a.length;
}

/** 刷新后某些存档可能已不存在：只保留仍存在的已选项。 */
export function pruneSelection(selected: readonly string[], archives: readonly CashCloseArchive[]): string[] {
  const existing = new Set(archives.map((archive) => archive.closeId));
  return selected.filter((id) => existing.has(id));
}

/** 已选存档的实点现金合计（按分累加）。 */
export function sumSelectedCountedCash(
  archives: readonly CashCloseArchive[],
  selected: readonly string[],
): number {
  const chosen = new Set(selected);
  return sumAmounts(archives.filter((archive) => chosen.has(archive.closeId)).map((archive) => archive.countedCash));
}

/**
 * 已选存档的统计区间是否有重叠（可能重复计入）。
 * 区间按 [from, to) 比较：首尾恰好相接不算重叠；无法解析时间的存档忽略。
 */
export function hasOverlappingPeriods(
  archives: readonly CashCloseArchive[],
  selected: readonly string[],
): boolean {
  const chosen = new Set(selected);
  const periods = archives
    .filter((archive) => chosen.has(archive.closeId))
    .map((archive) => ({ from: Date.parse(archive.periodFromUtc), to: Date.parse(archive.periodToUtc) }))
    .filter((period) => Number.isFinite(period.from) && Number.isFinite(period.to))
    .sort((a, b) => a.from - b.from || a.to - b.to);
  let latestEnd = Number.NEGATIVE_INFINITY;
  for (const period of periods) {
    if (period.from < latestEnd) return true;
    latestEnd = Math.max(latestEnd, period.to);
  }
  return false;
}

export interface DeviceSelectionNotices {
  /** 手选之后又出现了更新的存档，需要人确认。 */
  stale: boolean;
  /** 当前所选存档的统计区间重叠，可能重复计入。 */
  overlap: boolean;
}

export function resolveDeviceNotices(device: CashDailyDevice): DeviceSelectionNotices {
  return { stale: device.selectionStale, overlap: device.selectionOverlapWarning };
}

export type CloseSelectionIssue = "noneSelected" | "reasonRequired" | "reasonTooLong" | "unchanged";

/** 校验手选提交：至少选一份、必填原因、选择必须与当前不同。 */
export function validateManualSelection(options: {
  device: CashDailyDevice;
  selected: readonly string[];
  reason: string;
}): CloseSelectionIssue[] {
  const issues: CloseSelectionIssue[] = [];
  if (options.selected.length === 0) issues.push("noneSelected");
  const reason = options.reason.trim();
  // 与服务端一致：手选原因至少两个字（按字符计，中文一个字算一个）。
  if (Array.from(reason).length < CASH_REASON_MIN_LENGTH) issues.push("reasonRequired");
  if (reason.length > CASH_NOTE_MAX_LENGTH) issues.push("reasonTooLong");
  if (
    options.selected.length > 0 &&
    options.device.selectionMode === "Manual" &&
    isSameSelection(options.selected, getIncludedCloseIds(options.device)) &&
    !options.device.selectionStale
  ) {
    // 已经是这批手选且没有新存档，重复提交没有意义
    issues.push("unchanged");
  }
  return issues;
}

export function buildManualSelectionRequest(options: {
  storeCode: string;
  businessDate: string;
  deviceCode: string;
  selected: readonly string[];
  reason: string;
}): CashCloseSelectionRequest | null {
  const reason = options.reason.trim();
  if (options.selected.length === 0 || Array.from(reason).length < CASH_REASON_MIN_LENGTH) return null;
  return {
    storeCode: options.storeCode,
    businessDate: options.businessDate,
    deviceCode: options.deviceCode,
    mode: "Manual",
    closeIds: [...options.selected],
    reason,
  };
}

/** 恢复默认：不带存档编号与原因，由服务端按默认规则重算。 */
export function buildDefaultSelectionRequest(options: {
  storeCode: string;
  businessDate: string;
  deviceCode: string;
}): CashCloseSelectionRequest {
  return {
    storeCode: options.storeCode,
    businessDate: options.businessDate,
    deviceCode: options.deviceCode,
    mode: "Default",
    closeIds: [],
  };
}

/** 该设备是否有多份存档（只有一份时没有可选项，隐藏选择入口）。 */
export function hasSelectableArchives(device: Pick<CashDailyDevice, "archives">): boolean {
  return device.archives.length > 1;
}
