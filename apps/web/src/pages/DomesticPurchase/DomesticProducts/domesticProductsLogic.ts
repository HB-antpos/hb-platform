// 国内商品页的纯逻辑：列表查询参数、排序映射、表单取值与提交载荷、展示格式化。
// 全部写成不依赖 React / antd 的纯函数，便于在 Node 下直接做单元测试。

import { ProductType } from '../../../types/domesticProduct'
import { allocateCappedColumns, type CappedColumn } from '../tableWidthLogic'
import type {
  CreateDomesticProductPayload,
  DomesticProductItem,
  SupplierOption,
  UpdateDomesticProductPayload,
} from '../../../types/domesticProduct'

// ---------------------------------------------------------------------------
// 列表分页与排序
// ---------------------------------------------------------------------------

/** 设计稿分页条为「50 条/页」；列表不再写死高度，整页滚动，条数可以放宽。 */
export const DEFAULT_PAGE_SIZE = 50
export const PAGE_SIZE_OPTIONS = [20, 50, 100]

/**
 * 默认按「更新」降序：列表里展示的就是更新日期，排序与所见一致；
 * 同时与后端未传排序时的默认（UpdatedAt desc）保持一致。
 */
export const DEFAULT_SORT_FIELD = 'updatedAt'
export const DEFAULT_SORT_ORDER: SortOrderValue = 'descend'

export type SortOrderValue = 'ascend' | 'descend'

/**
 * 可排序列 → 后端 ApplyAgGridSorts 认识的 colId。
 * 只给后端真正支持的列开排序：商品名称 / 货号 / 供应商名称 / 更新时间；
 * 其它列（价格、类型等）后端虽然也认，但设计稿不开放，避免列头一片排序箭头。
 */
export const SORT_FIELD_BY_COLUMN = {
  product: 'name',
  itemNumber: 'itemNumber',
  supplier: 'supplierName',
  updated: 'updatedAt',
} as const

export type SortableColumnKey = keyof typeof SORT_FIELD_BY_COLUMN

/** 列受控的 sortOrder：只有当前排序字段所在的列显示箭头，其余列为 null。 */
export function sortOrderForColumn(
  column: SortableColumnKey,
  sortField: string | undefined,
  sortOrder: SortOrderValue | undefined,
): SortOrderValue | null {
  return sortField === SORT_FIELD_BY_COLUMN[column] ? sortOrder ?? null : null
}

/**
 * 列表列宽（px）。真实可用宽度 = 视口 − 侧栏 248 − 内容区左右 padding 32：1280 视口约 983，1440 视口约 1143。
 * 做法是「固定列 + 两个有上下限的弹性列（商品、供应商）」：
 * - 固定列单元格左右 padding 各 10px；货号（10 位等宽字符 + 复制按钮）与条码（13 位等宽字符 + 复制按钮）必须完整显示
 *   （等宽字符约 7.7px：货号 77px、条码 101px，再加复制按钮 22px + 间距 2px + padding 20px，即货号列 ≥ 122、条码列 ≥ 145）；
 * - 序号跨页连续编号，2 万多件商品时到 5 位数，按 5 位预留（12px 等宽数字约 33px + padding 20px）；
 * - action 是「操作」列在表格最窄时的宽度（「编辑 ⋯」按钮组实测 66px + padding 20px，至少 86）；
 *   操作列自身不设宽度，吸收两个弹性列封顶之后多出来的所有宽度（按钮靠右）。
 */
export const LIST_COLUMN_WIDTHS = {
  selection: 40,
  serial: 56,
  itemNumber: 124,
  barcode: 148,
  type: 60,
  price: 84,
  status: 60,
  updated: 76,
  action: 86,
} as const

/**
 * 商品列（缩略图 + 名称 + 英文名）与供应商列（名称 + 编码两行）的宽度范围与分配权重：
 * - 最窄值保证 1280 视口不出现横向滚动（Windows 经典滚动条再占约 15px，所以总最小宽度要 ≤ 985）；
 * - 最宽值：商品列 340（缩略图 + 约 15 个汉字），供应商列 200（约 12 个汉字），再宽只是空白——
 *   大屏上多出来的宽度交给「操作」列，不再把这两列拉到上千像素；
 * - 笔记本宽度下按 0.62 / 0.38 分剩余空间，商品列分得更多。
 */
export const PRODUCT_COLUMN: CappedColumn = { min: 148, max: 340, weight: 0.62 }
export const SUPPLIER_COLUMN: CappedColumn = { min: 100, max: 200, weight: 0.38 }

const LIST_FIXED_WIDTH = Object.values(LIST_COLUMN_WIDTHS).reduce((sum, width) => sum + width, 0)

/** 表格最窄宽度 = 固定列合计 + 两个弹性列的最窄宽度（1280 视口下也不出现横向滚动的下限）。 */
export const LIST_TABLE_MIN_WIDTH = LIST_FIXED_WIDTH + PRODUCT_COLUMN.min + SUPPLIER_COLUMN.min

export interface ListTableLayout {
  productWidth: number
  supplierWidth: number
  /** 传给 antd `scroll.x` 的表格宽度；容器比它宽时，多出来的部分由「操作」列吸收。 */
  tableWidth: number
}

/** 由表格可用宽度算出商品列、供应商列的宽度与 scroll.x。 */
export function resolveListTableLayout(containerWidth: number): ListTableLayout {
  const [productWidth, supplierWidth] = allocateCappedColumns(containerWidth - LIST_FIXED_WIDTH, [PRODUCT_COLUMN, SUPPLIER_COLUMN])
  return { productWidth, supplierWidth, tableWidth: LIST_FIXED_WIDTH + productWidth + supplierWidth }
}

interface TablePaginationLike {
  current?: number
  pageSize?: number
}

interface TableSorterLike {
  field?: unknown
  order?: 'ascend' | 'descend' | null
}

export interface TableChangeResult {
  page: number
  pageSize: number
  /** 仅排序变化时才带；翻页时不带，沿用当前排序。 */
  sortField?: string
  sortOrder?: SortOrderValue
}

/**
 * 把 antd Table.onChange 的参数翻译成列表查询覆盖项：
 * - 翻页：保留目标页，排序不变；
 * - 改每页条数：回到第一页（antd 对条数变化给的 current 不一定是 1，不能直接信）；
 * - 排序：一律回第一页（antd 在排序时不会重置 current，会把旧页码带过来）。
 * 列头只开放升/降序两态（sortDirections），所以排序事件里 order 理论上不会为空；
 * 防御性地回落到默认排序，避免给后端发出「无排序」的请求。
 */
export function resolveTableChange(
  action: 'paginate' | 'sort' | 'filter',
  pagination: TablePaginationLike,
  sorter: TableSorterLike | TableSorterLike[] | undefined,
  currentPageSize: number,
): TableChangeResult {
  const nextPageSize = pagination.pageSize ?? currentPageSize

  if (action === 'sort') {
    const first = Array.isArray(sorter) ? sorter[0] : sorter
    const field = typeof first?.field === 'string' ? first.field : undefined
    const order = first?.order === 'ascend' || first?.order === 'descend' ? first.order : undefined
    return {
      page: 1,
      pageSize: nextPageSize,
      sortField: field && order ? field : DEFAULT_SORT_FIELD,
      sortOrder: field && order ? order : DEFAULT_SORT_ORDER,
    }
  }

  if (nextPageSize !== currentPageSize) {
    return { page: 1, pageSize: nextPageSize }
  }
  return { page: pagination.current ?? 1, pageSize: nextPageSize }
}

// ---------------------------------------------------------------------------
// 筛选：分段控件取值互转 / 供应商下拉
// ---------------------------------------------------------------------------

export type TypeSegment = 'all' | 'normal' | 'set' | 'multi'
export type StatusSegment = 'all' | 'active' | 'inactive'

export function typeToSegment(type: ProductType | undefined): TypeSegment {
  switch (type) {
    case ProductType.NORMAL:
      return 'normal'
    case ProductType.SET:
      return 'set'
    case ProductType.MULTICODE:
      return 'multi'
    default:
      return 'all'
  }
}

/** 「全部」对应 undefined（不带筛选）；NORMAL 的值是 0，不能用真假判断。 */
export function typeFromSegment(segment: TypeSegment): ProductType | undefined {
  switch (segment) {
    case 'normal':
      return ProductType.NORMAL
    case 'set':
      return ProductType.SET
    case 'multi':
      return ProductType.MULTICODE
    default:
      return undefined
  }
}

export function statusToSegment(isActive: boolean | undefined): StatusSegment {
  return isActive === undefined ? 'all' : isActive ? 'active' : 'inactive'
}

/** 「停用」是 false 而不是未设置，不能被当成「全部」。 */
export function statusFromSegment(segment: StatusSegment): boolean | undefined {
  return segment === 'all' ? undefined : segment === 'active'
}

export interface SupplierSelectOption {
  value: string
  /** 下拉里的完整描述：编码 - 名称 - 店铺号。 */
  label: string
  /** 选中后输入框里只显示名称，避免长文案把前缀文字挤掉。 */
  shortLabel: string
  searchText: string
}

export function buildSupplierOptions(suppliers: SupplierOption[]): SupplierSelectOption[] {
  return suppliers.map((item) => ({
    value: item.code,
    label: `${item.code} - ${item.name}${item.shopNumber ? ` - ${item.shopNumber}` : ''}`,
    shortLabel: item.name || item.code,
    searchText: `${item.code} ${item.name} ${item.shopNumber ?? ''}`.toLowerCase(),
  }))
}

export function filterSupplierOption(input: string, option?: { searchText?: unknown }) {
  return String(option?.searchText ?? '').includes(input.trim().toLowerCase())
}

/** 已生效条件标签里的供应商文字：优先名称；选项还没加载回来时退回编码。 */
export function describeSupplier(suppliers: SupplierOption[], code: string) {
  return suppliers.find((item) => item.code === code)?.name || code
}

// ---------------------------------------------------------------------------
// 展示格式化
// ---------------------------------------------------------------------------

export function formatPrice(value?: number | null) {
  if (value === undefined || value === null || Number.isNaN(value)) {
    return '--'
  }
  return value.toFixed(2)
}

/** 带币种符号的价格，如 `¥ 18.50`；缺失时只显示占位，不带符号。 */
export function formatMoney(symbol: string, value?: number | null) {
  const text = formatPrice(value)
  return text === '--' ? text : `${symbol} ${text}`
}

/** 单件体积（m³）：后端是 decimal，去掉多余的尾随 0，缺失显示占位。 */
export function formatVolume(value?: number | null) {
  if (value === undefined || value === null || Number.isNaN(value)) {
    return '--'
  }
  return String(Number(value.toFixed(4)))
}

/**
 * 列表「更新」列的两行：日期与时间。
 * 后端时间是不带时区的墙钟文本，只做文本层面整理、不经时区换算（与分店页一致）；
 * 当年的日期省略年份（MM-DD），跨年的带上年份避免歧义。
 */
export function formatUpdatedParts(value: string | undefined, currentYear: number) {
  if (!value) {
    return { date: '--', time: '' }
  }
  const text = value.replace('T', ' ')
  const year = text.slice(0, 4)
  const date = year === String(currentYear) ? text.slice(5, 10) : text.slice(0, 10)
  return { date: date || '--', time: text.slice(11, 16) }
}

/** 悬浮提示和抽屉里使用的完整时间：`2026-10-04 14:20`。 */
export function formatFullTimestamp(value?: string) {
  return value ? value.replace('T', ' ').slice(0, 16) : '--'
}

// ---------------------------------------------------------------------------
// 缩略图占位：图片缺失/加载失败时用「首字 + 稳定底色」，与设计稿一致
// ---------------------------------------------------------------------------

const THUMB_COLORS = ['#5b8def', '#8e6bd8', '#2fa58f', '#e0922f', '#d9546a', '#4c9ad9']

/** 取名称的第一个可见字符（按码点取，避免把 emoji / 生僻字劈成半个代理对）。 */
export function productThumbLetter(name?: string) {
  const first = Array.from((name ?? '').trim())[0]
  return first ? first.toUpperCase() : '?'
}

/** 同一个种子（货号）永远得到同一种颜色，翻页、刷新后不跳色。 */
export function productThumbColor(seed?: string) {
  const text = seed ?? ''
  let hash = 0
  for (let index = 0; index < text.length; index += 1) {
    hash = (hash * 31 + text.charCodeAt(index)) >>> 0
  }
  return THUMB_COLORS[hash % THUMB_COLORS.length]
}

function escapeXml(text: string) {
  return text.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;')
}

/** antd Image 的 fallback 只接受地址：用内联 SVG data URI 画出首字方块。 */
export function buildLetterTileDataUri(letter: string, color: string) {
  const svg =
    `<svg xmlns="http://www.w3.org/2000/svg" width="80" height="80" viewBox="0 0 80 80">` +
    `<rect width="80" height="80" fill="${color}"/>` +
    `<text x="40" y="41" text-anchor="middle" dominant-baseline="central" fill="#fff" ` +
    `font-size="32" font-weight="600" font-family="-apple-system,BlinkMacSystemFont,'PingFang SC','Segoe UI',sans-serif">` +
    `${escapeXml(letter)}</text></svg>`
  return `data:image/svg+xml;charset=utf-8,${encodeURIComponent(svg)}`
}

// ---------------------------------------------------------------------------
// 行点击：整行开详情，但行内自带交互的元素不能触发
// ---------------------------------------------------------------------------

/** 行内这些元素自己处理点击（复制、编辑、更多、勾选、缩略图预览），不应再触发「开详情」。 */
export const ROW_CLICK_IGNORE_SELECTOR =
  'button, a, input, .ant-checkbox-wrapper, .ant-table-selection-column, .ant-dropdown, .ant-image, .ant-image-preview-root, .ant-popover'

interface ClosestLike {
  closest?: (selector: string) => unknown
}

/**
 * 是否应忽略这次行点击。
 * React 合成事件会沿组件树冒泡：弹出的下拉菜单 / 图片预览渲染在 body 的 Portal 里，
 * DOM 上不在本行内，却会冒泡到行的 onClick；用 contains 先排除这类「树内、DOM 外」的事件。
 */
export function shouldIgnoreRowClick(
  target: ClosestLike | null,
  row: { contains?: (node: never) => boolean },
) {
  if (!target) {
    return true
  }
  if (row.contains && !row.contains(target as never)) {
    return true
  }
  return Boolean(target.closest?.(ROW_CLICK_IGNORE_SELECTOR))
}

// ---------------------------------------------------------------------------
// 编辑 / 新建表单
// ---------------------------------------------------------------------------

/** 后端 Create/Update DTO 的长度上限（BlazorApp.Shared/DTOs/DomesticProductDtos.cs），前端提前拦住 400。 */
export const FIELD_MAX_LENGTH = {
  productName: 200,
  englishProductName: 500,
  hbProductNo: 50,
  barcode: 50,
  productSpecification: 100,
  productImage: 500,
} as const

export interface ProductFormValues {
  /** 仅新建时填写：编辑不允许换供应商（Update DTO 没有该字段）。 */
  supplierCode?: string
  productName: string
  englishProductName?: string
  /** 仅新建：不填由后端生成。编辑时不进表单（后端不保存）。 */
  hbProductNo?: string
  /** 仅新建：不填由后端生成。编辑时只读展示在详情抽屉。 */
  barcode?: string
  productSpecification?: string
  productType: ProductType
  productImage?: string
  domesticPrice?: number | null
  oemPrice?: number | null
  importPrice?: number | null
  packingQuantity?: number | null
  unitVolume?: number | null
  middlePackQuantity?: number | null
  isActive: boolean
}

/** 编辑表单里参与「已修改 N 项」统计的字段（不含新建专用的供应商 / 货号 / 条码）。 */
export const EDITABLE_FIELDS = [
  'productName',
  'englishProductName',
  'productType',
  'productSpecification',
  'productImage',
  'domesticPrice',
  'oemPrice',
  'importPrice',
  'packingQuantity',
  'unitVolume',
  'middlePackQuantity',
  'isActive',
] as const satisfies ReadonlyArray<keyof ProductFormValues>

export function emptyProductFormValues(): ProductFormValues {
  return { productName: '', productType: ProductType.NORMAL, isActive: true }
}

/** 装箱数 / 中包数量后端要求 ≥ 1：历史数据里的 0 在表单里按「未填」处理，否则会卡住其它字段的保存。 */
function positiveIntegerOrUndefined(value?: number) {
  return value !== undefined && value >= 1 ? value : undefined
}

export function productToFormValues(item: DomesticProductItem): ProductFormValues {
  return {
    supplierCode: item.supplierCode,
    productName: item.name,
    englishProductName: item.nameEn,
    productSpecification: item.specs,
    productType: item.productType,
    productImage: item.productImage,
    domesticPrice: item.domesticPrice,
    oemPrice: item.labelPrice,
    importPrice: item.importPrice,
    packingQuantity: positiveIntegerOrUndefined(item.packingQty),
    unitVolume: item.volume,
    middlePackQuantity: positiveIntegerOrUndefined(item.middlePackQty),
    isActive: item.isActive,
  }
}

/** 空白字符串与未填等价：避免「原来为空、点进去又退出」被算成已修改。 */
function normalizeFieldValue(value: unknown) {
  if (typeof value === 'string') {
    const trimmed = value.trim()
    return trimmed === '' ? undefined : trimmed
  }
  return value === null ? undefined : value
}

/** 与初始值相比，有多少个字段被改动（底栏「已修改 N 项」）。 */
export function countChangedFields(initial: Partial<ProductFormValues>, current: Partial<ProductFormValues>) {
  return EDITABLE_FIELDS.filter(
    (field) => normalizeFieldValue(initial[field]) !== normalizeFieldValue(current[field]),
  ).length
}

function optionalText(value?: string) {
  const trimmed = value?.trim()
  return trimmed ? trimmed : undefined
}

function optionalNumber(value?: number | null) {
  return value === null || value === undefined || Number.isNaN(value) ? undefined : value
}

/**
 * 更新载荷只带后端 UpdateDomesticProductDto 真正保存的 12 个字段：
 * 条码、包装尺寸、材质、备注后端不保存，不再从界面提交，避免给人「改了」的错觉。
 * 注意后端用 AutoMapper 无条件覆盖：缺省字段会被置空，所以编辑表单必须把这 12 个字段都带上。
 */
export function buildUpdatePayload(values: ProductFormValues): UpdateDomesticProductPayload {
  return {
    productName: values.productName.trim(),
    englishProductName: optionalText(values.englishProductName),
    productSpecification: optionalText(values.productSpecification),
    productType: values.productType,
    domesticPrice: optionalNumber(values.domesticPrice),
    oemPrice: optionalNumber(values.oemPrice),
    importPrice: optionalNumber(values.importPrice),
    packingQuantity: optionalNumber(values.packingQuantity),
    unitVolume: optionalNumber(values.unitVolume),
    middlePackQuantity: optionalNumber(values.middlePackQuantity),
    productImage: optionalText(values.productImage),
    isActive: values.isActive,
  }
}

export function buildCreatePayload(values: ProductFormValues): CreateDomesticProductPayload {
  return {
    ...buildUpdatePayload(values),
    supplierCode: values.supplierCode ?? '',
    productName: values.productName.trim(),
    // 货号 / 条码留空由后端 ItemBarcodeService 分配，空串不能传过去。
    hbProductNo: optionalText(values.hbProductNo),
    barcode: optionalText(values.barcode),
    productType: values.productType,
    isActive: values.isActive,
  }
}

/** 保存成功后立即把改动合并进详情抽屉里的商品，等列表刷新回来再以服务端数据为准。 */
export function applyFormValuesToItem(item: DomesticProductItem, values: ProductFormValues): DomesticProductItem {
  const payload = buildUpdatePayload(values)
  return {
    ...item,
    name: payload.productName ?? item.name,
    nameEn: payload.englishProductName,
    specs: payload.productSpecification,
    productType: payload.productType ?? item.productType,
    domesticPrice: payload.domesticPrice,
    labelPrice: payload.oemPrice,
    importPrice: payload.importPrice,
    packingQty: payload.packingQuantity,
    volume: payload.unitVolume,
    middlePackQty: payload.middlePackQuantity,
    productImage: payload.productImage,
    isActive: payload.isActive ?? item.isActive,
  }
}

// ---------------------------------------------------------------------------
// 导出
// ---------------------------------------------------------------------------

/**
 * 「导出筛选结果」一次性取回的条数。
 * 注意后端 total 不含供应商/类型/状态筛选（后端缺陷，另行修复），筛选后偏大；
 * 这里只把它当作 pageSize 的上限使用——多取不会多出数据，不会影响导出正确性，
 * 所以前端不对 total 做任何「修正」。
 */
export function resolveExportPageSize(total: number) {
  return Math.max(total, 1)
}
