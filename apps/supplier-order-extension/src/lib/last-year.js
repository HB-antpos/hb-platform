// 「去年同期」纯逻辑：窗口天数设置、后端字段归一化、取数、日期范围与星期几文案。
// 口径与订货页一致：门店当地今天往前推 364 天（与今天同星期几）起算，统计未来 30 / 60 天本店销量；
// 窗口不可用（分店未启用、去年此时没数据、旧后端）时全部为 null，界面据此隐藏，不显示成 0。
export const LAST_YEAR_DAYS_OPTIONS = [30, 60];
export const DEFAULT_LAST_YEAR_DAYS = 30;
export const LAST_YEAR_DAYS_STORAGE_KEY = 'lastYearWindowDays';

const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];
const WEEKDAYS = {
  zh: ['周日', '周一', '周二', '周三', '周四', '周五', '周六'],
  en: ['Sunday', 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday'],
};
const DATE_ONLY = /^(\d{4})-(\d{2})-(\d{2})/;

/** 本机存储值只认 30 / 60，其它值（缺失、被改坏、旧版本）一律回到默认 30 天。 */
export function normalizeLastYearDays(value) {
  return Number(value) === 60 ? 60 : DEFAULT_LAST_YEAR_DAYS;
}

function parseDateOnly(value) {
  const match = typeof value === 'string' ? DATE_ONLY.exec(value.trim()) : null;
  if (!match) return null;
  const [, year, month, day] = match;
  const time = Date.UTC(Number(year), Number(month) - 1, Number(day));
  return Number.isFinite(time) ? { time, year, month: Number(month), day: Number(day) } : null;
}

function finiteNumber(value) {
  return typeof value === 'number' && Number.isFinite(value) ? value : null;
}

/**
 * 把后端摘要项里的三个字段归一化。窗口起点非法或两个销量不是数字时整体视为不可用（返回 null），
 * 0 是有效值（去年有数据但没卖）。
 */
export function normalizeLastYear(raw) {
  if (!raw || typeof raw !== 'object') return null;
  const start = parseDateOnly(raw.lastYearWindowStartDate);
  const quantity30 = finiteNumber(raw.lastYearSalesQuantity30Days);
  const quantity60 = finiteNumber(raw.lastYearSalesQuantity60Days);
  if (!start || quantity30 === null || quantity60 === null) return null;
  return {
    windowStartDate: `${start.year}-${String(start.month).padStart(2, '0')}-${String(start.day).padStart(2, '0')}`,
    quantity30,
    quantity60,
  };
}

/** 取所选窗口的销量；lastYear 缺失时为 null。 */
export function pickLastYearQuantity(lastYear, days) {
  if (!lastYear) return null;
  return normalizeLastYearDays(days) === 60 ? lastYear.quantity60 : lastYear.quantity30;
}

function formatDay(time, withYear) {
  const date = new Date(time);
  const base = `${date.getUTCDate()} ${MONTHS[date.getUTCMonth()]}`;
  return withYear ? `${base} ${date.getUTCFullYear()}` : base;
}

/**
 * 窗口日期范围（含首尾，终点 = 起点 + 天数 − 1）：同一年只在终点写年份「9 Nov – 8 Dec 2025」，
 * 跨年两端都写「9 Nov 2025 – 7 Jan 2026」。只做 UTC 纯日期加法，不经过本地时区，避免夏令时差一天。
 */
export function formatLastYearRange(windowStartDate, days) {
  const start = parseDateOnly(windowStartDate);
  if (!start) return '';
  const endTime = start.time + (normalizeLastYearDays(days) - 1) * 24 * 60 * 60 * 1000;
  const sameYear = new Date(endTime).getUTCFullYear() === Number(start.year);
  return `${formatDay(start.time, !sameYear)} – ${formatDay(endTime, true)}`;
}

/** 起点是星期几（中文「周日」/ 英文「Sunday」）；与今天同一天，用于「按星期几对齐」的说明。 */
export function lastYearWeekday(windowStartDate, locale = 'zh') {
  const start = parseDateOnly(windowStartDate);
  if (!start) return '';
  const names = WEEKDAYS[locale] || WEEKDAYS.zh;
  return names[new Date(start.time).getUTCDay()];
}

/** 「27 Oct」：卡片里的短日期，只取 YYYY-MM-DD，不经过本地时区。 */
export function formatDayMonth(value) {
  const date = parseDateOnly(value);
  return date ? formatDay(date.time, false) : '';
}

/** 「27 Oct 2026」。 */
export function formatDayMonthYear(value) {
  const date = parseDateOnly(value);
  return date ? formatDay(date.time, true) : '';
}
