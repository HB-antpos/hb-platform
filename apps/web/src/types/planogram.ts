/**
 * 商品陈列图（Planogram）批 1 接口类型：货架型号、挂钩型号、商品陈列参数。
 * 依据 docs/plans/2026-11-04-planogram-shelf-display-designer.md 附录 A（前缀 api/react/v1/planogram，JSON camelCase，
 * 时间为 ISO 字符串）。字段名 / 错误码一律照附录 A，后端同步对照开发，不要自行改名。
 */

/** 货架类型：1 单面 / 2 双面。 */
export type PlanogramSideType = 1 | 2
/** 行号起算：1 自板顶 / 2 自底板（只影响显示编号，内部仍存自顶行号）。 */
export type PlanogramRowNumberingFrom = 1 | 2
/** 钩型：1 单钩 / 2 双钩。 */
export type PlanogramProngCount = 1 | 2
/** 吊孔类型：0 无（只能放层板，二期）/ 1 圆孔 / 2 欧式挂孔。 */
export type PlanogramHangHoleType = 0 | 1 | 2
/** 尺寸来源：1 人工量取 / 2 供应商资料 / 3 同套装主档。 */
export type PlanogramSpecSource = 1 | 2 | 3

/** 附录 A 的错误码。失败响应的 message 是可直接展示的中文，details 给字段列表（PLANOGRAM_VALIDATION）。 */
export const PLANOGRAM_ERROR_CODES = [
  'PLANOGRAM_VALIDATION',
  'PLANOGRAM_NOT_FOUND',
  'PLANOGRAM_CODE_DUPLICATE',
  'PLANOGRAM_MODEL_IN_USE',
  // 批 2（附录 B.2）：商品参数被方案引用 / 货架型号被锁定 / 保存并发冲突 / 已归档不可保存 / 不可删除
  'PLANOGRAM_SPEC_IN_USE',
  'PLANOGRAM_MODEL_LOCKED',
  'PLANOGRAM_STALE',
  'PLANOGRAM_ARCHIVED',
  'PLANOGRAM_NOT_DELETABLE',
  // 批 3（附录 C）：发布 / 导入
  'PLANOGRAM_HAS_CONFLICTS',
  'PLANOGRAM_EMPTY',
  'PLANOGRAM_ALREADY_PUBLISHED',
  'PLANOGRAM_IMPORT_UNRESOLVED',
  // 附录 C 之外、后端另加：历史版本的发布快照被改坏，读不出来
  'PLANOGRAM_SNAPSHOT_INVALID',
] as const
export type PlanogramErrorCode = (typeof PLANOGRAM_ERROR_CODES)[number]

/** 货架型号（洞洞板孔阵）。尺寸单位 mm；孔距 / 孔径 / 首孔边距允许小数（25.4 等英寸换算值）。 */
export interface ShelfModelDto {
  guid: string
  code: string
  name: string
  sideType: PlanogramSideType
  color?: string | null
  source?: string | null
  bayWidthMm: number
  totalHeightMm: number
  baseDepthMm: number
  baseTopHeightMm: number
  uprightWidthMm: number
  holePitchXMm: number
  holePitchYMm: number
  holeDiameterMm: number
  firstHoleLeftMm: number
  firstHoleTopMm: number
  holeCols: number
  holeRows: number
  rowNumberingFrom: PlanogramRowNumberingFrom
  isActive: boolean
  /** 只读；批 1 恒为 0。 */
  referencedPlanCount: number
  updatedAt?: string | null
  updatedBy?: string | null
}

/** 新建 / 保存货架型号的请求体：只读字段不提交；孔数缺省或为 0 时后端按板面尺寸自动换算。 */
export type ShelfModelInput = Omit<ShelfModelDto, 'guid' | 'referencedPlanCount' | 'updatedAt' | 'updatedBy'>

/** 挂钩型号（双钩 / 单钩、带价签臂）。 */
export interface HookModelDto {
  guid: string
  code: string
  name: string
  color?: string | null
  prongCount: PlanogramProngCount
  hasPriceTagArm: boolean
  armLengthMm: number
  /** 钩高 H：挂货臂相对挂孔下落的距离（上下臂间距）。 */
  dropMm: number
  wireDiameterMm: number
  /** 臂端不挂货的预留。 */
  tipReserveMm: number
  /** 价签架宽 / 高；没有价签臂时可为 0。 */
  tagWidthMm: number
  tagHeightMm: number
  /** 价签架底边高于挂货臂的距离，默认 10。 */
  tagBottomAboveArmMm: number
  holeSpanX: number
  holeSpanY: number
  loadKg?: number | null
  isActive: boolean
  referencedPlanCount: number
  updatedAt?: string | null
  updatedBy?: string | null
}

export type HookModelInput = Omit<HookModelDto, 'guid' | 'referencedPlanCount' | 'updatedAt' | 'updatedBy'>

/** 商品陈列参数（按 ProductCode 一对一）。 */
export interface ProductDisplaySpecDto {
  productCode: string
  widthMm: number
  heightMm: number
  /** 厚 T：挂钩方向。 */
  depthMm: number
  /** 吊孔距顶边，默认 10。 */
  hangHoleOffsetMm: number
  hangHoleType: PlanogramHangHoleType
  /** 单件重（克），可空。 */
  unitWeightG?: number | null
  defaultHookGuid?: string | null
  /** 按挂钩型号手填覆盖每钩挂数：{ hookGuid: 件数 }，值 ≥ 1，hookGuid 必须存在。 */
  hookQtyOverrides: Record<string, number>
  source: PlanogramSpecSource
  notes?: string | null
}

/** 商品陈列参数列表行（联 Product）。 */
export interface ProductSpecListItemDto {
  productCode: string
  itemNumber?: string | null
  barcode?: string | null
  productName: string
  englishName?: string | null
  productImage?: string | null
  retailPrice: number
  categoryName?: string | null
  hasSpec: boolean
  referencedPlanCount: number
  spec?: ProductDisplaySpecDto | null
  updatedAt?: string | null
  updatedBy?: string | null
}

/** 状态筛选：all 全部 / set 已设参数 / unset 未设参数。 */
export type ProductSpecStatusFilter = 'all' | 'set' | 'unset'

export interface ModelListQuery {
  keyword?: string
  /** 缺省 = 全部；true 只看启用，false 只看停用。 */
  isActive?: boolean
  page: number
  pageSize: number
}

export interface ProductSpecListQuery {
  keyword?: string
  status: ProductSpecStatusFilter
  page: number
  pageSize: number
}

/** 归一化后的分页结果（服务层保证 items 一定是数组）。 */
export interface PlanogramPaged<T> {
  items: T[]
  total: number
  page: number
  pageSize: number
}

// ---------------------------------------------------------------------------
// 批 2：陈列方案（附录 B.2）。字段名 / 路径 / 错误码与后端 PlanogramDtos.cs 一一对应。
// ---------------------------------------------------------------------------

/** 方案状态：0 草稿 / 1 已发布 / 2 已归档。 */
export type PlanogramStatus = 0 | 1 | 2
export const PLANOGRAM_STATUS_DRAFT: PlanogramStatus = 0
export const PLANOGRAM_STATUS_PUBLISHED: PlanogramStatus = 1
export const PLANOGRAM_STATUS_ARCHIVED: PlanogramStatus = 2

/** 方案列表行；同时是详情里的 header。 */
export interface PlanogramListItemDto {
  guid: string
  name: string
  notes?: string | null
  shelfModelGuid: string
  shelfModelName: string
  bayCount: number
  gapXMm: number
  gapYMm: number
  status: PlanogramStatus
  version: number
  publishedVersion?: number | null
  publishedAt?: string | null
  itemCount: number
  skuCount: number
  unitCount: number
  fillPercent: number
  conflictCount: number
  /** 有未发布的改动：status == 0 且 publishedVersion 不为空（已发布方案被保存后回到草稿）。字段缺省按 false。 */
  hasUnpublishedChanges: boolean
  updatedAt?: string | null
  updatedBy?: string | null
}

/** 方案明细（一钩一行）。face 恒 0、itemType 恒 1（二期才放开）；perHookQty 为 null = 按规则（规格覆盖 > 建议值）。 */
export interface PlanogramItemDto {
  guid: string
  bayIndex: number
  face: 0
  itemType: 1
  productCode: string
  hookModelGuid: string
  holeCol: number
  holeRow: number
  perHookQty: number | null
  sortOrder: number
  /** 只读：保存时的商品尺寸快照与当前参数不一致（「参数已变」）；请求里被忽略。 */
  specChanged?: boolean
}

/** 附录 B.1 的冲突代码（前后端一致）。 */
export const PLANOGRAM_CONFLICT_CODES = [
  'BAY_OUT_OF_RANGE',
  'PRODUCT_SPEC_MISSING',
  'HOOK_MODEL_MISSING',
  'HOLE_OUT_OF_RANGE',
  'HOOK_HOLE_CLASH',
  'OUT_OF_BOARD',
  'EDGE_GAP',
  'OVERLAP',
  'OVERLOAD',
] as const
export type PlanogramConflictCode = (typeof PLANOGRAM_CONFLICT_CODES)[number]
export type PlanogramConflictSeverity = 'error' | 'warn'

/** 一条冲突；成对冲突（HOOK_HOLE_CLASH / OVERLAP）每个条目各报一条，otherItemGuid 指向对方。 */
export interface ConflictDto {
  itemGuid: string
  otherItemGuid?: string | null
  code: string
  severity: PlanogramConflictSeverity
  message: string
}

/** 方案统计（口径见附录 B.1 第 6 条）。 */
export interface PlanogramStatsDto {
  itemCount: number
  skuCount: number
  unitCount: number
  retailValue: number
  fillPercent: number
  conflictCount: number
}

/** 方案详情：设计器一次拿齐。header 是嵌套对象，rowVersion 在顶层。 */
export interface PlanogramDetailDto {
  header: PlanogramListItemDto
  /** 乐观并发令牌：保存时原样带回；不一致返回 PLANOGRAM_STALE。 */
  rowVersion: string
  items: PlanogramItemDto[]
  shelfModel: ShelfModelDto
  /** 全部启用的挂钩型号 + 本方案引用到的停用型号。 */
  hookModels: HookModelDto[]
  /** 仅方案内商品的参数行（含零售价、是否已设参数）。 */
  productSpecs: ProductSpecListItemDto[]
  conflicts: ConflictDto[]
  stats: PlanogramStatsDto
}

/** 列表筛选：不绑定分店，只有货架型号 / 状态 / 关键字（匹配方案名称与明细商品货号、名称）。 */
export interface PlanogramListQuery {
  shelfModelGuid?: string
  status?: PlanogramStatus
  keyword?: string
  page: number
  pageSize: number
}

export interface PlanogramCreateInput {
  name: string
  notes?: string | null
  shelfModelGuid: string
  /** 1–12。 */
  bayCount: number
  /** 缺省 10。 */
  gapXMm?: number
  gapYMm?: number
}

/** PUT / validate 请求里的明细：specChanged 不提交。 */
export type PlanogramItemInput = Omit<PlanogramItemDto, 'specChanged'>

/** 整体保存（PUT）请求体；校验（POST validate）用同一结构但不带 rowVersion。 */
export interface PlanogramSaveInput {
  name: string
  notes?: string | null
  shelfModelGuid: string
  bayCount: number
  gapXMm: number
  gapYMm: number
  items: PlanogramItemInput[]
  rowVersion: string
}
export type PlanogramValidateInput = Omit<PlanogramSaveInput, 'rowVersion'>

export interface PlanogramValidateResult {
  conflicts: ConflictDto[]
  stats: PlanogramStatsDto
}

// ---------------------------------------------------------------------------
// 批 3：发布、版本快照、JSON 导入导出（附录 C）
// ---------------------------------------------------------------------------

/** 发布请求体：rowVersion 取自读取方案时返回的值，与保存同一套乐观并发。 */
export interface PlanogramPublishInput {
  rowVersion: string
}

/** 版本列表的一行（按版本倒序）；统计取自发布时固化的快照。 */
export interface PlanogramVersionSummaryDto {
  version: number
  publishedAt: string | null
  publishedBy: string | null
  itemCount: number
  skuCount: number
  unitCount: number
}

/** 快照里的方案头。 */
export interface PlanogramSnapshotPlanDto {
  guid: string
  name: string
  notes?: string | null
  bayCount: number
  gapXMm: number
  gapYMm: number
}

/** 快照里的商品：发布时刻的名称 / 英文名 / 图片 / 零售价与陈列参数，之后主档再改也不影响历史版本。 */
export interface PlanogramSnapshotProductDto {
  productCode: string
  itemNumber: string
  barcode: string
  productName: string
  englishName?: string | null
  productImage?: string | null
  retailPrice: number
  spec: ProductDisplaySpecDto
}

/**
 * 发布快照（PlanogramSnapshotDto）：按当时数据重出 PDF / 只读预览用。
 * 「当前已保存版本」的 PDF 也先组装成同一形状（见 export/pdfLogic.ts 的 snapshotFromDetail），导出代码只认这一种数据来源。
 */
export interface PlanogramSnapshotDto {
  version: number
  publishedAt: string | null
  publishedBy: string | null
  plan: PlanogramSnapshotPlanDto
  shelfModel: ShelfModelDto
  /** 仅方案用到的挂钩型号。 */
  hookModels: HookModelDto[]
  products: PlanogramSnapshotProductDto[]
  items: PlanogramItemDto[]
  stats: PlanogramStatsDto
}

/** 方案导出文件的明细：用型号编码和商品货号，不带 guid。 */
export interface PlanogramExportItemDto {
  bayIndex: number
  productCode: string
  hookModelCode: string
  holeCol: number
  holeRow: number
  perHookQty?: number | null
}

/** 方案导出文件（JSON 对象，前端存为 <方案名>.planogram.json）。 */
export interface PlanogramExportDto {
  formatVersion: number
  exportedAt?: string | null
  plan: { name: string; notes?: string | null; bayCount: number; gapXMm: number; gapYMm: number }
  shelfModelCode: string
  items: PlanogramExportItemDto[]
}

/** 导入请求体 = 导出文件内容，另可带 name 覆盖方案名称。 */
export type PlanogramImportInput = PlanogramExportDto & { name?: string }

/** PLANOGRAM_IMPORT_UNRESOLVED 的 details 一项：解析不到的货架 / 挂钩型号编码或商品货号。 */
export interface PlanogramImportUnresolvedItem {
  kind: 'shelf' | 'hook' | 'product'
  code: string
}

export const PLANOGRAM_EXPORT_FORMAT_VERSION = 1
