// 员工端「整店整周排班」的纯逻辑：接口数据规整、同名区分、按天分组、人 × 周表格、时间轴、与我同班的重叠时长。
// 后端只返回「姓名 + 已发布班次的日期与起止时间」（同事的工时、请假、备注、打卡状态一律不下发），这里也不派生这些信息。
import { assignStaffColors, FALLBACK_STAFF_COLOR, type StaffColor } from "./schedule-board";
import { normalizeClockTime, shiftMinutesRange } from "./schedule-grid";

/** 一个已发布的班：谁、哪天、几点到几点。 */
export interface StoreWeekShift {
  userGuid: string;
  /** 已区分过同名的显示名（见 uniqueDisplayNames）。 */
  name: string;
  isMine: boolean;
  /** YYYY-MM-DD */
  workDate: string;
  /** HH:mm；结束早于开始表示跨午夜。 */
  startTime: string;
  endTime: string;
}

export interface StoreWeekStore {
  storeCode: string;
  storeName?: string;
  /** 这一周是否已有已发布的班；false 对应「店长还没发布」。 */
  published: boolean;
  shifts: StoreWeekShift[];
}

export interface StoreWeek {
  weekStartDate: string;
  stores: StoreWeekStore[];
}

type JsonRecord = Record<string, unknown>;

function asRecord(value: unknown): JsonRecord {
  return value && typeof value === "object" && !Array.isArray(value) ? (value as JsonRecord) : {};
}

function text(value: unknown): string | undefined {
  if (typeof value !== "string") return undefined;
  const trimmed = value.trim();
  return trimmed === "" ? undefined : trimmed;
}

const collator = new Intl.Collator(undefined, { sensitivity: "base", numeric: true });

/**
 * 一批同事的显示名：短名撞名（忽略大小写，例如两个 Sam）时，优先换成全名；全名也一样或没有时在后面加 2、3 区分。
 * 不撞名的人保持短名不变，保证「同一个人同一周」在任何视图里都是同一个名字。
 */
export function uniqueDisplayNames(
  people: readonly { userGuid: string; displayName: string; fullName?: string }[],
): Map<string, string> {
  const byName = new Map<string, string[]>();
  const info = new Map<string, { displayName: string; fullName?: string }>();
  people.forEach((person) => {
    if (info.has(person.userGuid)) return;
    info.set(person.userGuid, person);
    const key = person.displayName.toLowerCase();
    byName.set(key, [...(byName.get(key) ?? []), person.userGuid]);
  });
  const result = new Map<string, string>();
  const taken = new Set<string>();
  // 先登记所有不撞名的人，再处理撞名组，避免新生成的名字和后面才遍历到的人撞上。
  byName.forEach((guids) => {
    if (guids.length !== 1) return;
    result.set(guids[0], info.get(guids[0])!.displayName);
    taken.add(info.get(guids[0])!.displayName.toLowerCase());
  });
  byName.forEach((guids) => {
    if (guids.length === 1) return;
    // 撞名：全名能区分就用全名，否则按 userGuid 顺序加序号（稳定，跨设备一致）。
    [...guids].sort().forEach((guid, index) => {
      const person = info.get(guid)!;
      const full = person.fullName && person.fullName.toLowerCase() !== person.displayName.toLowerCase() ? person.fullName : undefined;
      let name = full ?? `${person.displayName} ${index + 1}`;
      while (taken.has(name.toLowerCase())) name = `${name}'`;
      taken.add(name.toLowerCase());
      result.set(guid, name);
    });
  });
  return result;
}

/** 规整 my/store-week 响应；字段缺失一律当没有，不抛错。 */
export function normalizeStoreWeek(raw: unknown, fallbackWeekStart: string): StoreWeek {
  const data = asRecord(raw);
  const weekStartDate = text(data.weekStartDate)?.slice(0, 10) ?? fallbackWeekStart;
  const stores = (Array.isArray(data.stores) ? data.stores : []).map((item): StoreWeekStore => {
    const store = asRecord(item);
    const rows = (Array.isArray(store.shifts) ? store.shifts : []).map(asRecord).filter((row) => text(row.userGuid));
    const names = uniqueDisplayNames(
      rows.map((row) => ({
        userGuid: text(row.userGuid)!,
        displayName: text(row.displayName) ?? text(row.fullName) ?? text(row.userGuid)!,
        fullName: text(row.fullName),
      })),
    );
    const shifts = rows
      .map((row): StoreWeekShift => ({
        userGuid: text(row.userGuid)!,
        name: names.get(text(row.userGuid)!) ?? "",
        isMine: row.isMine === true,
        workDate: (text(row.workDate) ?? "").slice(0, 10),
        startTime: normalizeClockTime(text(row.startTime) ?? ""),
        endTime: normalizeClockTime(text(row.endTime) ?? ""),
      }))
      // 日期或时间解析不出来的脏数据丢掉，免得界面出现「--」班次。
      .filter((shift) => shift.workDate !== "" && shift.startTime !== "" && shift.endTime !== "");
    return {
      storeCode: text(store.storeCode) ?? "",
      storeName: text(store.storeName),
      published: store.published === true,
      shifts,
    };
  });
  return { weekStartDate, stores: stores.filter((store) => store.storeCode !== "") };
}

/**
 * 选出要显示的分店：指定了分店就用它（不在列表里返回 undefined）；
 * 没指定时优先选「我有班」的店，其次第一家。
 */
export function pickStoreWeek(week: StoreWeek | undefined, storeCode?: string): StoreWeekStore | undefined {
  if (!week || week.stores.length === 0) return undefined;
  if (storeCode) return week.stores.find((store) => store.storeCode.toLowerCase() === storeCode.toLowerCase());
  return week.stores.find((store) => store.shifts.some((shift) => shift.isMine)) ?? week.stores[0];
}

// ---------- 时间格式 ----------

/** 「09:00–17:00」。 */
export function formatShiftRange(shift: Pick<StoreWeekShift, "startTime" | "endTime">): string {
  return `${shift.startTime}–${shift.endTime}`;
}

function shortClock(time: string): string {
  const [hour, minute] = time.split(":");
  const h = String(Number(hour));
  return minute === "00" ? h : `${h}:${minute}`;
}

/** 表格格子里的短写法：9–17、9–15:30。 */
export function shortShiftRange(shift: Pick<StoreWeekShift, "startTime" | "endTime">): string {
  return `${shortClock(shift.startTime)}–${shortClock(shift.endTime)}`;
}

// ---------- 按天分组 ----------

const minutesOfStart = (shift: Pick<StoreWeekShift, "startTime" | "endTime">) => shiftMinutesRange(shift)?.start ?? 0;

/** 同一天：开始时间 → 姓名 → 结束时间。 */
export function compareDayShifts(left: StoreWeekShift, right: StoreWeekShift): number {
  return (
    minutesOfStart(left) - minutesOfStart(right) ||
    collator.compare(left.name, right.name) ||
    left.endTime.localeCompare(right.endTime)
  );
}

export interface StoreWeekDay {
  date: string;
  shifts: StoreWeekShift[];
  /** 当天在岗人数（同一人多个班只算 1 人）。 */
  people: number;
  /** 当天最早开始 ~ 最晚结束（HH:mm）；没人上班时为空。 */
  span?: { start: string; end: string };
}

export function groupStoreWeekDays(weekDates: readonly string[], shifts: readonly StoreWeekShift[]): StoreWeekDay[] {
  return weekDates.map((date) => {
    const dayShifts = shifts.filter((shift) => shift.workDate === date).sort(compareDayShifts);
    const ranges = dayShifts.map((shift) => ({ shift, range: shiftMinutesRange(shift) })).filter((item) => item.range);
    const first = ranges.reduce<(typeof ranges)[number] | undefined>(
      (best, item) => (!best || item.range!.start < best.range!.start ? item : best),
      undefined,
    );
    const last = ranges.reduce<(typeof ranges)[number] | undefined>(
      (best, item) => (!best || item.range!.end > best.range!.end ? item : best),
      undefined,
    );
    return {
      date,
      shifts: dayShifts,
      people: new Set(dayShifts.map((shift) => shift.userGuid)).size,
      span: first && last ? { start: first.shift.startTime, end: last.shift.endTime } : undefined,
    };
  });
}

// ---------- 人 × 周表格 ----------

export interface StoreWeekGridRow {
  userGuid: string;
  name: string;
  isMine: boolean;
  /** 周一到周日，每格是当天这个人的班（没班为空数组；一天多段会有多个）。 */
  cells: StoreWeekShift[][];
  shiftCount: number;
}

/** 我的一行置顶，其余按姓名排序。 */
export function buildStoreWeekGrid(weekDates: readonly string[], shifts: readonly StoreWeekShift[]): StoreWeekGridRow[] {
  const rows = new Map<string, StoreWeekGridRow>();
  shifts.forEach((shift) => {
    // 不在本周的脏数据直接跳过，免得表格里出现一整行空白的人。
    const dayIndex = weekDates.indexOf(shift.workDate);
    if (dayIndex < 0) return;
    let row = rows.get(shift.userGuid);
    if (!row) {
      row = { userGuid: shift.userGuid, name: shift.name, isMine: false, cells: weekDates.map(() => []), shiftCount: 0 };
      rows.set(shift.userGuid, row);
    }
    row.cells[dayIndex].push(shift);
    row.shiftCount += 1;
    row.isMine = row.isMine || shift.isMine;
  });
  const result = Array.from(rows.values());
  result.forEach((row) => row.cells.forEach((cell) => cell.sort(compareDayShifts)));
  return result.sort((left, right) => Number(right.isMine) - Number(left.isMine) || collator.compare(left.name, right.name));
}

/** 表格格子文字：没班为空；一天多段写第一段加「+」。 */
export function gridCellLabel(cell: readonly StoreWeekShift[]): string {
  if (cell.length === 0) return "";
  return `${shortShiftRange(cell[0])}${cell.length > 1 ? "+" : ""}`;
}

// ---------- 颜色 ----------

/** 一人一色：与店长看板同一套哈希（同一个人两端同色）。 */
export function staffColorMap(shifts: readonly StoreWeekShift[]): Map<string, StaffColor> {
  return assignStaffColors(shifts.map((shift) => ({ userGuid: shift.userGuid, name: shift.name })));
}

export function colorOf(colors: Map<string, StaffColor>, userGuid: string): StaffColor {
  return colors.get(userGuid) ?? FALLBACK_STAFF_COLOR;
}

/** 图例顺序：我在最前，其余按姓名。 */
export function legendPeople(shifts: readonly StoreWeekShift[]): { userGuid: string; name: string; isMine: boolean }[] {
  const people = new Map<string, { userGuid: string; name: string; isMine: boolean }>();
  shifts.forEach((shift) => {
    const existing = people.get(shift.userGuid);
    people.set(shift.userGuid, { userGuid: shift.userGuid, name: shift.name, isMine: Boolean(existing?.isMine) || shift.isMine });
  });
  return Array.from(people.values()).sort((left, right) => Number(right.isMine) - Number(left.isMine) || collator.compare(left.name, right.name));
}

// ---------- 时间轴与同班重叠 ----------

export interface DayAxis {
  /** 分钟（整点）。 */
  start: number;
  end: number;
}

/** 当天时间轴：覆盖所有班次，向外取整点，至少 8 小时，最长不超过 24 小时。 */
export function buildDayAxis(shifts: readonly Pick<StoreWeekShift, "startTime" | "endTime">[]): DayAxis {
  const ranges = shifts.map(shiftMinutesRange).filter((range): range is { start: number; end: number } => Boolean(range));
  if (ranges.length === 0) return { start: 8 * 60, end: 18 * 60 };
  let start = Math.floor(Math.min(...ranges.map((range) => range.start)) / 60) * 60;
  let end = Math.ceil(Math.max(...ranges.map((range) => range.end)) / 60) * 60;
  if (end - start < 8 * 60) end = start + 8 * 60;
  if (end - start > 24 * 60) end = start + 24 * 60;
  return { start, end };
}

/** 班次条在轴上的位置（0~100%）。 */
export function trackPosition(shift: Pick<StoreWeekShift, "startTime" | "endTime">, axis: DayAxis): { left: number; width: number } {
  const range = shiftMinutesRange(shift);
  if (!range) return { left: 0, width: 0 };
  const span = axis.end - axis.start;
  const left = Math.min(100, Math.max(0, ((range.start - axis.start) / span) * 100));
  const right = Math.min(100, Math.max(0, ((range.end - axis.start) / span) * 100));
  return { left, width: Math.max(1, right - left) };
}

/** 两个班同时在岗的分钟数。 */
export function overlapMinutes(
  left: Pick<StoreWeekShift, "startTime" | "endTime">,
  right: Pick<StoreWeekShift, "startTime" | "endTime">,
): number {
  const a = shiftMinutesRange(left);
  const b = shiftMinutesRange(right);
  if (!a || !b) return 0;
  return Math.max(0, Math.min(a.end, b.end) - Math.max(a.start, b.start));
}

/** 某个同事和我（当天可能有多段）同时在岗的时间段（分钟，自当天 0 点起）；我自己、我当天没班时为空。 */
export function overlapRangesWithMine(
  shift: StoreWeekShift,
  myShifts: readonly StoreWeekShift[],
): { start: number; end: number }[] {
  if (shift.isMine) return [];
  const range = shiftMinutesRange(shift);
  if (!range) return [];
  return myShifts
    .map(shiftMinutesRange)
    .filter((mine): mine is { start: number; end: number } => Boolean(mine))
    .map((mine) => ({ start: Math.max(range.start, mine.start), end: Math.min(range.end, mine.end) }))
    .filter((item) => item.end > item.start);
}

/** 某个同事和我（当天可能有多段）同时在岗的总分钟数；我当天没班时为 0。 */
export function overlapWithMine(shift: StoreWeekShift, myShifts: readonly StoreWeekShift[]): number {
  return overlapRangesWithMine(shift, myShifts).reduce((sum, item) => sum + (item.end - item.start), 0);
}

/** 轴上的刻度（每 3 小时一个），标签为 HH。 */
export function axisTicks(axis: DayAxis): { minutes: number; label: string; percent: number }[] {
  const ticks: { minutes: number; label: string; percent: number }[] = [];
  for (let minutes = axis.start; minutes <= axis.end; minutes += 180) {
    const hour = Math.floor((minutes % 1440) / 60);
    ticks.push({ minutes, label: String(hour).padStart(2, "0"), percent: ((minutes - axis.start) / (axis.end - axis.start)) * 100 });
  }
  return ticks;
}

/** 整店概况：已发布班次数与人数。 */
export function summarizeStoreWeek(shifts: readonly StoreWeekShift[]): { shiftCount: number; peopleCount: number } {
  return { shiftCount: shifts.length, peopleCount: new Set(shifts.map((shift) => shift.userGuid)).size };
}
