/** 商品主档中文名称的最大长度，与后端 Product / DomesticProduct 列长度及服务端校验一致。 */
export const PRODUCT_NAME_MAX_LENGTH = 200;

/** 与后端 `char.IsControl` 同口径：C0 控制字符（含换行、制表）、DEL 与 C1 控制字符。 */
const CONTROL_CHARACTER_PATTERN = /[\u0000-\u001F\u007F-\u009F]/;

export type ProductNameDraftIssue = "empty" | "tooLong" | "control" | "unchanged";

export interface ProductNameDraftState {
  /** 去掉首尾空白后将提交的名称。 */
  normalized: string;
  canSave: boolean;
  issue: ProductNameDraftIssue | null;
}

/**
 * 判断草稿能否保存。服务端会再校验一遍；这里提前拦住，避免来回一趟才报错。
 * 与当前名称相同（去首尾空白后）视为「没有变化」：不发请求，也就不会触发全局改动与 HQ 同步。
 */
export function evaluateProductNameDraft(draft: string, currentName: string | null | undefined): ProductNameDraftState {
  const normalized = draft.trim();
  if (normalized.length === 0) {
    return { normalized, canSave: false, issue: "empty" };
  }
  if (normalized.length > PRODUCT_NAME_MAX_LENGTH) {
    return { normalized, canSave: false, issue: "tooLong" };
  }
  if (CONTROL_CHARACTER_PATTERN.test(normalized)) {
    return { normalized, canSave: false, issue: "control" };
  }
  if (normalized === (currentName ?? "").trim()) {
    return { normalized, canSave: false, issue: "unchanged" };
  }
  return { normalized, canSave: true, issue: null };
}

const TIMEZONE_DESIGNATOR_PATTERN = /(Z|[+-]\d{2}:?\d{2})$/i;

/**
 * 修改记录的时间是真正的 UTC 时刻（不同于订单的门店墙钟时间），按设备本地时区显示为 DD/MM/YYYY HH:mm。
 * 后端从库里读出的 UTC 时间可能没有 Z 后缀，不补上会被 Date 当成本地时间，导致整体平移时区差。
 */
export function formatUtcInstantAsLocal(value: string | null | undefined): string {
  if (!value) return "—";
  const hasZone = TIMEZONE_DESIGNATOR_PATTERN.test(value);
  const parsed = new Date(hasZone ? value : `${value.replace(" ", "T")}Z`);
  if (Number.isNaN(parsed.getTime())) return value;
  const pad = (n: number) => String(n).padStart(2, "0");
  return `${pad(parsed.getDate())}/${pad(parsed.getMonth() + 1)}/${parsed.getFullYear()} ${pad(parsed.getHours())}:${pad(parsed.getMinutes())}`;
}
