import type { BranchHourlyRevenueRow } from "./api";
import { CUTOFF_REFERENCE_UTC_OFFSET_MINUTES, parseUtcTimestamp } from "./hourly-cumulative";

/**
 * 门店时区缺失或无法换算时的偏移：沿用原来的固定 UTC+10（门店最西且无夏令时），
 * 这样旧后端不返回时区时行为不变，而且截止整点只会偏早、不会把半截小时拿去比较。
 */
export const FALLBACK_UTC_OFFSET_MINUTES = CUTOFF_REFERENCE_UTC_OFFSET_MINUTES;

const MAX_UTC_OFFSET_MINUTES = 14 * 60;
const LAST_HOUR = 23;

/**
 * 截止整点所用的时钟。小时桶是各店本地墙钟：参考时钟之外的门店要平移到参考时钟上，
 * 同一截止整点才对每家店都表示「本店已结束的整点」。
 */
export interface CutoffClock {
  /** 参考时钟相对 UTC 的偏移（分钟），截止整点与「实时」时刻都按它显示。 */
  referenceOffsetMinutes: number;
  /** 各店在所选日期的 UTC 偏移（分钟），键为去空格大写的分店代码。 */
  offsetMinutesByBranch: ReadonlyMap<string, number>;
}

interface StoreTimeZoneOption {
  value: string;
  timeZoneId?: string | null;
}

function normalizeBranchCode(branchCode: string) {
  return branchCode.trim().toUpperCase();
}

function pad(value: number) {
  return String(value).padStart(2, "0");
}

/** 门店选项里的 IANA 时区整理成按分店代码查找的表；没有时区的门店不进表，按回退偏移处理。 */
export function buildStoreTimeZoneMap(options: readonly StoreTimeZoneOption[]) {
  const map = new Map<string, string>();
  for (const option of options) {
    const branchCode = normalizeBranchCode(option.value);
    const timeZone = option.timeZoneId?.trim();
    if (branchCode && timeZone) map.set(branchCode, timeZone);
  }
  return map;
}

/** 某一时刻在指定 IANA 时区的 UTC 偏移（分钟）；时区无效或引擎不支持时返回 null。 */
export function getTimeZoneOffsetMinutes(timeZone: string, timestamp: number): number | null {
  if (!Number.isFinite(timestamp)) return null;
  try {
    const parts = new Intl.DateTimeFormat("en-US", {
      timeZone,
      hourCycle: "h23",
      year: "numeric",
      month: "2-digit",
      day: "2-digit",
      hour: "2-digit",
      minute: "2-digit",
      second: "2-digit",
    }).formatToParts(new Date(timestamp));
    const value = (type: Intl.DateTimeFormatPartTypes) => Number(parts.find((part) => part.type === type)?.value);
    // 部分引擎把零点输出为 24，按 0 点处理。
    const wallClock = Date.UTC(
      value("year"),
      value("month") - 1,
      value("day"),
      value("hour") % 24,
      value("minute"),
      value("second"),
    );
    const offset = Math.round((wallClock - Math.floor(timestamp / 1000) * 1000) / 60_000);
    return Number.isFinite(offset) && Math.abs(offset) <= MAX_UTC_OFFSET_MINUTES ? offset : null;
  } catch {
    return null;
  }
}

/** 取所选日期悉尼/布里斯班中午的时刻换算偏移：夏令时在凌晨 2–3 点切换，营业时段内偏移一致。 */
export function getCutoffClockAnchorUtc(date: string) {
  return Date.parse(`${date}T02:00:00Z`);
}

/**
 * 解析截止时钟：参考时钟取 referenceBranchCodes 里 UTC 偏移最大（最东）的门店。
 * 选中单店时参考就是该店本地时间；全部分店在夏令时期间按悉尼时间，布里斯班店平移一小时。
 */
export function resolveCutoffClock({
  branchCodes,
  referenceBranchCodes,
  timeZoneByBranch,
  atUtc,
}: {
  /** 报表范围内的全部门店，都需要知道各自的偏移。 */
  branchCodes: readonly string[];
  /** 决定参考时钟的门店：选中单店时只有该店，否则等于报表范围。 */
  referenceBranchCodes: readonly string[];
  timeZoneByBranch: ReadonlyMap<string, string>;
  atUtc: number;
}): CutoffClock {
  const offsetByTimeZone = new Map<string, number>();
  const offsetMinutesByBranch = new Map<string, number>();
  const resolveOffset = (branchCode: string) => {
    const code = normalizeBranchCode(branchCode);
    const cached = offsetMinutesByBranch.get(code);
    if (cached !== undefined) return cached;
    const timeZone = timeZoneByBranch.get(code);
    let offset = FALLBACK_UTC_OFFSET_MINUTES;
    if (timeZone) {
      if (!offsetByTimeZone.has(timeZone)) {
        offsetByTimeZone.set(timeZone, getTimeZoneOffsetMinutes(timeZone, atUtc) ?? FALLBACK_UTC_OFFSET_MINUTES);
      }
      offset = offsetByTimeZone.get(timeZone) ?? FALLBACK_UTC_OFFSET_MINUTES;
    }
    if (code) offsetMinutesByBranch.set(code, offset);
    return offset;
  };
  branchCodes.forEach(resolveOffset);
  const referenceOffsets = referenceBranchCodes.map(resolveOffset);
  return {
    referenceOffsetMinutes: referenceOffsets.length > 0 ? Math.max(...referenceOffsets) : FALLBACK_UTC_OFFSET_MINUTES,
    offsetMinutesByBranch,
  };
}

export function getBranchOffsetMinutes(clock: CutoffClock, branchCode: string) {
  return clock.offsetMinutesByBranch.get(normalizeBranchCode(branchCode)) ?? FALLBACK_UTC_OFFSET_MINUTES;
}

/**
 * 门店本地小时要加上的平移量。时差不是整小时（如半小时时区）时向上取整：
 * 平移后同一截止整点只会让该店少算、不会多算进行中的小时。
 */
export function getBranchShiftHours(clock: CutoffClock, branchCode: string) {
  const shift = Math.ceil((clock.referenceOffsetMinutes - getBranchOffsetMinutes(clock, branchCode)) / 60);
  return shift === 0 ? 0 : shift;
}

/**
 * 把逐店小时行平移到参考时钟。超出 0–23 的小时并入首尾小时：
 * 23 点平移到 24 点并入 23 点后，只有整天口径会计入，与平移前一致。
 * 所有门店都不需要平移时原样返回，保持引用稳定。
 */
export function shiftBranchHourlyRows(
  rows: readonly BranchHourlyRevenueRow[],
  clock: CutoffClock,
): readonly BranchHourlyRevenueRow[] {
  let shifted = false;
  const result = rows.map((row) => {
    const shift = getBranchShiftHours(clock, row.branchCode);
    if (shift === 0) return row;
    shifted = true;
    return { ...row, hour: Math.max(0, Math.min(LAST_HOUR, Math.trunc(row.hour) + shift)) };
  });
  return shifted ? result : rows;
}

/** 参考时钟上的整点换成门店本地整点；24（整天）保持不变。 */
export function toBranchClockHour(hour: number, shiftHours: number) {
  if (hour >= 24) return hour;
  return Math.max(0, Math.min(24, hour - shiftHours));
}

/** 统计完成时刻在指定偏移下的「时:分」。 */
export function formatClockTimeAtOffset(value: string | null | undefined, offsetMinutes: number) {
  const timestamp = parseUtcTimestamp(value);
  if (timestamp === null) return null;
  const date = new Date(timestamp + offsetMinutes * 60_000);
  return `${pad(date.getUTCHours())}:${pad(date.getUTCMinutes())}`;
}
