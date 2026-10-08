/**
 * 节日贺卡的填报开放窗口：节日当天起 4 周内（当天 + 之后 28 天，共 29 天）开放对应节日，其余时间只读。
 * 填报年份由开放窗口决定（圣诞节窗口跨年时，1 月填的仍是上一年）。
 *
 * 以服务端 overview 返回的窗口为准；还没选供应商（拿不到 overview）时，用本文件按手机本地日期
 * 推算一份同口径的窗口做预览，规则与后端 SeasonalCardHolidayCalendar 保持一致。
 */
import type {
  SeasonalCardOverview,
  SeasonalCardType,
} from "@/modules/seasonal-cards/types";

/** 节日当天之后还开放的天数（4 周）。 */
export const SEASONAL_CARD_OPEN_DAYS_AFTER_HOLIDAY = 28;

const CARD_TYPES: SeasonalCardType[] = [1, 2, 3, 4, 5];
const EN_MONTHS = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];

export interface SeasonalCardHolidayWindow {
  cardType: SeasonalCardType;
  /** 今天是否在开放窗口内。 */
  isOpen: boolean;
  /** 开放时为本次填报归属年份；未开放时为下一次节日的年份；未知为 null。 */
  seasonYear: number | null;
  /** yyyy-MM-dd；未知为空字符串（旧后端）。 */
  holidayDate: string;
  opensOn: string;
  /** 开放末日（含当天）。 */
  closesOn: string;
}

function pad(value: number) {
  return String(value).padStart(2, "0");
}

/** 只按年月日计算，避免时区与夏令时影响。 */
function toUtcDay(year: number, month: number, day: number) {
  return Date.UTC(year, month - 1, day);
}

function formatUtcDay(time: number) {
  const date = new Date(time);
  return `${date.getUTCFullYear()}-${pad(date.getUTCMonth() + 1)}-${pad(date.getUTCDate())}`;
}

function addDays(time: number, days: number) {
  return time + days * 24 * 60 * 60 * 1000;
}

/** yyyy-MM-dd → UTC 零点时间戳；格式不对返回 null。 */
export function parseIsoDay(value: string | null | undefined) {
  const match = /^(\d{4})-(\d{2})-(\d{2})/.exec(value ?? "");
  if (!match) {
    return null;
  }
  return toUtcDay(Number(match[1]), Number(match[2]), Number(match[3]));
}

/** 手机本地日期，yyyy-MM-dd。 */
export function formatLocalIsoDay(date = new Date()) {
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}`;
}

function nthSunday(year: number, month: number, nth: number) {
  const first = toUtcDay(year, month, 1);
  const offset = (7 - new Date(first).getUTCDay()) % 7;
  return addDays(first, offset + (nth - 1) * 7);
}

/** 公历复活节周日（Anonymous Gregorian 算法，与后端一致）。 */
function easterSunday(year: number) {
  const a = year % 19;
  const b = Math.floor(year / 100);
  const c = year % 100;
  const d = Math.floor(b / 4);
  const e = b % 4;
  const f = Math.floor((b + 8) / 25);
  const g = Math.floor((b - f + 1) / 3);
  const h = (19 * a + b - d - g + 15) % 30;
  const i = Math.floor(c / 4);
  const k = c % 4;
  const l = (32 + 2 * e + 2 * i - h - k) % 7;
  const m = Math.floor((a + 11 * h + 22 * l) / 451);
  const month = Math.floor((h + l - 7 * m + 114) / 31);
  const day = ((h + l - 7 * m + 114) % 31) + 1;
  return toUtcDay(year, month, day);
}

/** 澳洲习惯的节日日期：圣诞 12/25、情人节 2/14、母亲节 5 月第二个周日、父亲节 9 月第一个周日、复活节周日。 */
export function getSeasonalCardHolidayDate(cardType: SeasonalCardType, year: number) {
  switch (cardType) {
    case 1:
      return toUtcDay(year, 12, 25);
    case 2:
      return toUtcDay(year, 2, 14);
    case 3:
      return nthSunday(year, 5, 2);
    case 4:
      return easterSunday(year);
    case 5:
      return nthSunday(year, 9, 1);
    default:
      return toUtcDay(year, 1, 1);
  }
}

function buildWindow(
  cardType: SeasonalCardType,
  holiday: number,
  isOpen: boolean
): SeasonalCardHolidayWindow {
  return {
    cardType,
    isOpen,
    seasonYear: new Date(holiday).getUTCFullYear(),
    holidayDate: formatUtcDay(holiday),
    opensOn: formatUtcDay(holiday),
    closesOn: formatUtcDay(addDays(holiday, SEASONAL_CARD_OPEN_DAYS_AFTER_HOLIDAY)),
  };
}

/** 按某天（yyyy-MM-dd）推算一个节日的开放情况；只有圣诞节会跨年，所以只看今年和去年两次节日。 */
export function computeSeasonalCardHolidayWindow(
  cardType: SeasonalCardType,
  today: string
): SeasonalCardHolidayWindow {
  const todayTime = parseIsoDay(today) ?? parseIsoDay(formatLocalIsoDay())!;
  const year = new Date(todayTime).getUTCFullYear();
  for (const candidateYear of [year, year - 1]) {
    const holiday = getSeasonalCardHolidayDate(cardType, candidateYear);
    if (
      todayTime >= holiday &&
      todayTime <= addDays(holiday, SEASONAL_CARD_OPEN_DAYS_AFTER_HOLIDAY)
    ) {
      return buildWindow(cardType, holiday, true);
    }
  }
  let next = getSeasonalCardHolidayDate(cardType, year);
  if (next < todayTime) {
    next = getSeasonalCardHolidayDate(cardType, year + 1);
  }
  return buildWindow(cardType, next, false);
}

export function computeSeasonalCardHolidayWindows(today: string) {
  return CARD_TYPES.map((cardType) => computeSeasonalCardHolidayWindow(cardType, today));
}

/**
 * 页面使用的窗口：有 overview 用服务端口径（字段缺失按开放兜底，兼容旧后端）；
 * 没有 overview（还没选供应商）时按本地日期推算。
 */
export function resolveSeasonalCardHolidayWindows(
  overview: SeasonalCardOverview | null,
  localToday: string
): SeasonalCardHolidayWindow[] {
  if (!overview) {
    return computeSeasonalCardHolidayWindows(localToday);
  }
  return CARD_TYPES.map((cardType) => {
    const holiday = overview.holidays.find((item) => item.cardType === cardType);
    return {
      cardType,
      isOpen: holiday?.isOpen ?? true,
      seasonYear: holiday?.seasonYear ?? overview.seasonYear ?? null,
      holidayDate: holiday?.holidayDate ?? "",
      opensOn: holiday?.opensOn ?? "",
      closesOn: holiday?.closesOn ?? "",
    };
  });
}

/**
 * 默认选中的节日：之前选过且仍开放就保持；否则选开放中且最快截止的（closesOn 最近，未知的排最后）；
 * 没有开放的节日返回 null。
 */
export function pickSeasonalCardDefaultHoliday(
  windows: SeasonalCardHolidayWindow[],
  previous: SeasonalCardType | null
): SeasonalCardType | null {
  const open = windows.filter((item) => item.isOpen);
  if (previous != null && open.some((item) => item.cardType === previous)) {
    return previous;
  }
  const sorted = open.slice().sort((left, right) => {
    const leftClose = parseIsoDay(left.closesOn) ?? Number.MAX_SAFE_INTEGER;
    const rightClose = parseIsoDay(right.closesOn) ?? Number.MAX_SAFE_INTEGER;
    return leftClose - rightClose || left.cardType - right.cardType;
  });
  return sorted[0]?.cardType ?? null;
}

/** 没有开放节日时提示「下一个」：未开放节日中 opensOn 最早的。 */
export function findNextSeasonalCardOpening(windows: SeasonalCardHolidayWindow[]) {
  const candidates = windows
    .filter((item) => !item.isOpen && parseIsoDay(item.opensOn) != null)
    .sort(
      (left, right) =>
        (parseIsoDay(left.opensOn) ?? 0) - (parseIsoDay(right.opensOn) ?? 0) ||
        left.cardType - right.cardType
    );
  return candidates[0] ?? null;
}

/** 短日期：中文 12/25（月/日），英文 25 Dec（避免澳洲用户把 M/D 当成 D/M）。 */
export function formatSeasonalCardShortDay(value: string, language: string) {
  const match = /^(\d{4})-(\d{2})-(\d{2})/.exec(value);
  if (!match) {
    return "";
  }
  const month = Number(match[2]);
  const day = Number(match[3]);
  return language.toLowerCase().startsWith("zh")
    ? `${month}/${day}`
    : `${day} ${EN_MONTHS[month - 1] ?? ""}`.trim();
}
