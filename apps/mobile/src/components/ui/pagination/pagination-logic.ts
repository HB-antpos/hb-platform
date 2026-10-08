/**
 * 分页公共纯函数：不依赖 React / 原生模块，可直接用 tsx 跑单测。
 * 页码一律从 1 开始；pageSize 是普通 number，可选项由各页面自己决定（如 50/100/200/500）。
 */

/** 总页数：至少 1 页（0 条数据也显示「1 / 1」，避免出现 0 页）；非法每页条数按 1 页处理。 */
export function getPageCount(total: number, pageSize: number): number {
  if (!Number.isFinite(total) || !Number.isFinite(pageSize) || pageSize <= 0) return 1;
  return Math.max(1, Math.ceil(Math.max(0, total) / pageSize));
}

/** 把页码夹回 [1, pageCount]；NaN / 小数 / 越界（刷新后条数变少、换店）都回到有效页。 */
export function clampPage(page: number, pageCount: number): number {
  const safeCount = Math.max(1, Math.floor(pageCount) || 1);
  return Math.min(Math.max(1, Math.floor(page) || 1), safeCount);
}

export interface PageRange {
  /** 本页第一条的序号（1 起）；没有数据时为 0 */
  from: number;
  /** 本页最后一条的序号（含）；没有数据时为 0 */
  to: number;
}

/** 本页显示的区间「from–to」：末页按实际剩余条数收尾，页码越界时先夹回有效页。 */
export function getPageRange(page: number, pageSize: number, total: number): PageRange {
  if (!Number.isFinite(total) || total <= 0 || !Number.isFinite(pageSize) || pageSize <= 0) return { from: 0, to: 0 };
  const current = clampPage(page, getPageCount(total, pageSize));
  const from = (current - 1) * pageSize + 1;
  return { from, to: Math.min(total, current * pageSize) };
}

/**
 * 每页条数只认 options 里的值：本地记住的值可能来自旧版本或被篡改（字符串、20、NaN），
 * 不在可选项里一律回到 fallback，保证界面上一定有一个被选中的选项。
 */
export function normalizePageSize<T extends number>(value: unknown, options: readonly T[], fallback: T): T {
  const parsed = typeof value === "string" ? Number(value) : value;
  return options.find((option) => option === parsed) ?? fallback;
}

/** 跳页输入框：只接受 1..pageCount 的整数，其余返回 null（确认按钮置灰 / 提示页码无效）。 */
export function parsePageInput(text: string, pageCount: number): number | null {
  const trimmed = text.trim();
  if (!/^\d+$/.test(trimmed)) return null;
  const page = Number(trimmed);
  return page >= 1 && page <= pageCount ? page : null;
}

/** 页数不多时跳页面板给出页码格子；页数很多时只保留输入框，避免面板过长。 */
export const PAGE_GRID_MAX_PAGES = 40;

export function getPageGridNumbers(pageCount: number): number[] {
  if (pageCount <= 1 || pageCount > PAGE_GRID_MAX_PAGES) return [];
  return Array.from({ length: pageCount }, (_, index) => index + 1);
}
