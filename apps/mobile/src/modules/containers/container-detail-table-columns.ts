import {
  getDetailGuid,
  getDetailItemNumber,
  getDetailMatchType,
  getDetailProductName,
  getDetailRealtimeImportPrice,
  getDetailVisibleOemPrice,
  hasDetailProductCodeConflict,
} from "./query";
import type { ContainerDetail, ContainerDetailQueryMatchType, ContainerMain } from "./types";

/**
 * 货柜明细表格的列定义、尺寸与取值格式化（纯函数，无 React 依赖，便于单测）。
 *
 * 表格结构：左侧固定列（勾选 + 货号 + 名称）+ 右侧横向滚动列。
 * 行高固定，这样 FlatList 可以用 getItemLayout 直接算出偏移，
 * 任意下标的 scrollToIndex（定位到该行）都是精确的，也不需要运行时测量。
 */

/** 数据行固定高度（含底部分隔线）。 */
export const CONTAINER_DETAIL_ROW_HEIGHT = 52;
/** 表头固定高度。 */
export const CONTAINER_DETAIL_HEADER_HEIGHT = 40;
/** 左侧固定列宽：勾选框 44 + 货号/名称 84。 */
export const CONTAINER_DETAIL_FIXED_COLUMN_WIDTH = 128;
/** 固定列里勾选框触控区宽度（满足 44px 触控目标）。 */
export const CONTAINER_DETAIL_CHECKBOX_WIDTH = 44;

export type ContainerDetailScrollColumnKey =
  | "status"
  | "containerQuantity"
  | "middlePack"
  | "domesticPrice"
  | "warehouseImportPrice"
  | "importPrice"
  | "retailPrice";

export interface ContainerDetailScrollColumn {
  key: ContainerDetailScrollColumnKey;
  width: number;
  align: "left" | "right";
}

/** 右侧滚动列，顺序即界面顺序；表头文案由 i18n 键 `table.columns.<key>` 提供。 */
export const CONTAINER_DETAIL_SCROLL_COLUMNS: readonly ContainerDetailScrollColumn[] = [
  { key: "status", width: 96, align: "left" },
  { key: "containerQuantity", width: 76, align: "right" },
  { key: "middlePack", width: 64, align: "right" },
  { key: "domesticPrice", width: 80, align: "right" },
  { key: "warehouseImportPrice", width: 92, align: "right" },
  { key: "importPrice", width: 80, align: "right" },
  { key: "retailPrice", width: 88, align: "right" },
];

export const CONTAINER_DETAIL_SCROLL_COLUMNS_WIDTH = CONTAINER_DETAIL_SCROLL_COLUMNS.reduce(
  (sum, column) => sum + column.width,
  0,
);

/** 表格内容总宽 = 固定列 + 滚动列。 */
export const CONTAINER_DETAIL_TABLE_WIDTH = CONTAINER_DETAIL_FIXED_COLUMN_WIDTH + CONTAINER_DETAIL_SCROLL_COLUMNS_WIDTH;

/** FlatList.getItemLayout：行高固定，offset 直接乘下标。 */
export function getContainerDetailRowLayout(_data: unknown, index: number) {
  return { length: CONTAINER_DETAIL_ROW_HEIGHT, offset: CONTAINER_DETAIL_ROW_HEIGHT * index, index };
}

/** 稳定的行 key：优先明细 GUID，其次数据库 id，最后用下标兜底（保证不同行不撞 key）。 */
export function getContainerDetailRowKey(detail: ContainerDetail, index: number) {
  const hguid = getDetailGuid(detail).trim();
  if (hguid) return hguid;
  const id = detail.id ?? detail.ID;
  return id != null ? `id:${id}` : `index:${index}`;
}

function isFiniteNumber(value: unknown): value is number {
  return typeof value === "number" && Number.isFinite(value);
}

/** 金额/小数：缺失显示 "--"，其余固定小数位（默认 2 位）。 */
export function formatContainerDetailNumber(value: number | null | undefined, digits = 2) {
  return isFiniteNumber(value) ? value.toFixed(digits) : "--";
}

/** 数量：整数，千分位逗号，缺失显示 "--"。 */
export function formatContainerDetailQuantity(value: number | null | undefined) {
  return isFiniteNumber(value) ? Math.round(value).toLocaleString("en-US") : "--";
}

export interface ContainerDetailRowCells {
  hguid: string;
  itemNumber: string;
  productName: string;
  /** 本柜新品（建档后仍为 true），仅用于展示；创建新商品的资格以 detail.是否新商品 为准。 */
  isNewProduct: boolean;
  isActive: boolean;
  hasConflict: boolean;
  matchType: ContainerDetailQueryMatchType;
  /** 状态列是否需要显示「编码冲突/候选需确认」提示图标 */
  needsAttention: boolean;
  containerQuantity: string;
  middlePack: string;
  domesticPrice: string;
  warehouseImportPrice: string;
  importPrice: string;
  retailPrice: string;
  /** 零售价为 0 / 非正数（新商品缺零售价同理）：红色 + 警示图标 */
  retailPriceMissing: boolean;
}

/**
 * 零售价是否「缺失」：
 * - 数值存在但 <= 0，一定缺失；
 * - 取不到数值时，只有新商品才算缺失（创建新商品会把它写入主表），已有商品取不到只显示 "--"。
 */
export function isRetailPriceMissing(price: number | null | undefined, isNewProduct: boolean) {
  if (isFiniteNumber(price)) return price <= 0;
  return isNewProduct;
}

/** 把一条明细转成表格一行所需的全部展示值，渲染层不再做取值/格式化。 */
export function buildContainerDetailRowCells(detail: ContainerDetail): ContainerDetailRowCells {
  const isNewProduct = Boolean(detail.isContainerNewProduct ?? detail.是否新商品);
  const hasConflict = hasDetailProductCodeConflict(detail);
  const matchType = getDetailMatchType(detail);
  const retailPrice = getDetailVisibleOemPrice(detail);
  return {
    hguid: getDetailGuid(detail).trim(),
    itemNumber: getDetailItemNumber(detail) || detail.商品编码 || "",
    productName: getDetailProductName(detail),
    isNewProduct,
    isActive: detail.warehouseIsActive !== false,
    hasConflict,
    matchType,
    needsAttention: hasConflict || matchType === "supplierItem",
    containerQuantity: formatContainerDetailQuantity(detail.装柜数量),
    middlePack: formatContainerDetailQuantity(detail.中包数),
    domesticPrice: formatContainerDetailNumber(detail.国内价格),
    warehouseImportPrice: formatContainerDetailNumber(getDetailRealtimeImportPrice(detail)),
    importPrice: formatContainerDetailNumber(detail.进口价格),
    retailPrice: formatContainerDetailNumber(retailPrice),
    retailPriceMissing: isRetailPriceMissing(retailPrice, Boolean(detail.是否新商品)),
  };
}

/** 套装子商品不允许对齐国内编码（与旧版卡片一致）。 */
export function isSetChildDetail(detail: ContainerDetail) {
  return detail.商品类型 === "套装子商品" || detail.商品信息?.商品类型 === "套装子商品";
}

export function getDetailRemark(detail: ContainerDetail) {
  return detail.备注?.trim() ?? "";
}

/** 明细的创建资格：未建档的新商品（建档后 是否新商品 变 false，不能再次创建）。 */
export function isCreatableNewProduct(detail: ContainerDetail) {
  return Boolean(detail.是否新商品);
}

// ---------------------------------------------------------------------------
// 货柜头部（避免界面层直接访问中文字段名）
// ---------------------------------------------------------------------------

export interface ContainerHeaderInfo {
  containerNumber: string;
  status?: number;
  estimatedArrival: string;
  actualArrival: string;
}

function dateOnly(value?: string) {
  return value ? value.slice(0, 10) : "";
}

export function buildContainerHeaderInfo(container: ContainerMain | null | undefined): ContainerHeaderInfo {
  return {
    containerNumber: container?.货柜编号?.trim() ?? "",
    status: container?.状态,
    estimatedArrival: dateOnly(container?.预计到岸日期),
    actualArrival: dateOnly(container?.实际到货日期),
  };
}

/** 货柜状态值 -> i18n 键后缀；未知状态返回 null，界面显示「状态 N」。 */
export function getContainerStatusKey(status?: number): "0" | "1" | "2" | "7" | null {
  return status === 0 || status === 1 || status === 2 || status === 7 ? (String(status) as "0" | "1" | "2" | "7") : null;
}

/** 金额概览：保留两位小数并加千分位，缺失显示 "--"。 */
export function formatContainerDetailAmount(value: number | null | undefined) {
  return isFiniteNumber(value)
    ? value.toLocaleString("en-US", { minimumFractionDigits: 2, maximumFractionDigits: 2 })
    : "--";
}

// ---------------------------------------------------------------------------
// 后端字段名（协议键，不是界面文案）
// ---------------------------------------------------------------------------

/** 后端校验错误里英文名称字段的键。 */
export const CONTAINER_DETAIL_ENGLISH_NAME_FIELD = "英文名称";
/** 后端用 "*" 表示「整行」级别的校验错误。 */
export const CONTAINER_DETAIL_WHOLE_ROW_FIELD = "*";

const EDIT_FIELD_LABEL_KEYS: Readonly<Record<string, string>> = {
  商品名称: "productName",
  英文名称: "englishName",
  国内价格: "domesticPrice",
  进口价格: "importPrice",
  贴牌价格: "oemPrice",
  调整浮率: "floatRate",
  装柜数量: "containerQuantity",
  中包数: "middlePackQuantity",
  IsActive: "isActive",
};

/** 冲突/校验面板里字段名的 i18n 键后缀（edit.fields.<key>）；不认识的字段返回 null，界面直接显示后端原名。 */
export function getContainerDetailFieldLabelKey(field: string): string | null {
  return EDIT_FIELD_LABEL_KEYS[field] ?? null;
}
