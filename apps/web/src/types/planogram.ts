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
  // 附录 D（11-04 追加）：商品陈列参数批量修改
  'PLANOGRAM_BATCH_TOO_MANY',
  'PLANOGRAM_BATCH_EMPTY',
  // 真正执行时的并发冲突（整批已回滚）：提示重新预览
  'PLANOGRAM_BATCH_CONFLICT',
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
  /** 商品分类（POS ProductCategory）叶子名，注意不是仓库分类。 */
  categoryName?: string | null
  /** 商品等级 A～D（商品等级表），未分级为 null；只读。 */
  grade?: PlanogramGradeLetter | null
  /** 仓库分类完整路径，分隔符「 > 」（如「手工 > 纸品 > 皱纹纸」），没有仓库分类为 null；按原样显示。 */
  warehouseCategoryPath?: string | null
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

/**
 * 商品等级过滤取值：A / B / C / D 与 none（未分级）。等级定义与颜色复用 types/productGrade.ts 的 PRODUCT_GRADE_CONFIG，
 * 这里只固定「请求里传什么字符串」，多选取并集。
 */
export const PLANOGRAM_GRADE_NONE = 'none'
export const PLANOGRAM_GRADE_VALUES = ['A', 'B', 'C', 'D', PLANOGRAM_GRADE_NONE] as const
export type PlanogramProductGrade = (typeof PLANOGRAM_GRADE_VALUES)[number]
/** 列表行上的等级字母（行上没有 none：未分级就是 null）。 */
export type PlanogramGradeLetter = Exclude<PlanogramProductGrade, typeof PLANOGRAM_GRADE_NONE>

/**
 * 附录 D.1 的商品范围过滤条件（商品陈列参数列表、设计器商品库、批量修改「按当前筛选」共用）。
 * 都是可选条件；数组在查询串里用重复键（warehouseCategoryGuids=a&warehouseCategoryGuids=b）。
 * - warehouseCategoryGuids：仓库分类，服务端按分类树展开含全部子分类；
 * - localSupplierCode：单个本地供应商；
 * - supplierCategoryGuids：本地供应商分类（含子分类）；supplierCategoryUnassignedOnly：只看「未归类」；
 * - grades：商品等级（11-04 追加，协调人口头补充，附录 D 稍后补）。
 */
export interface ProductSpecScopeFilter {
  warehouseCategoryGuids?: string[]
  localSupplierCode?: string
  supplierCategoryGuids?: string[]
  supplierCategoryUnassignedOnly?: boolean
  grades?: PlanogramProductGrade[]
}

export interface ProductSpecListQuery extends ProductSpecScopeFilter {
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

// ---------------------------------------------------------------------------
// 附录 D.2：商品陈列参数批量修改 POST /product-specs/batch（权限 Planogram.Edit）
// ---------------------------------------------------------------------------

/** 批量目标里的「按当前筛选」：服务端自己解析出全部匹配商品（> 2000 返回 PLANOGRAM_BATCH_TOO_MANY）。 */
export interface ProductSpecBatchFilter {
  keyword: string
  status: ProductSpecStatusFilter
  warehouseCategoryGuids: string[]
  localSupplierCode: string | null
  supplierCategoryGuids: string[]
  supplierCategoryUnassignedOnly: boolean
  grades: PlanogramProductGrade[]
}

/** 二选一：勾选的货号（≤ 2000），或按当前筛选。 */
export type ProductSpecBatchTarget =
  | { productCodes: string[] }
  | { filter: ProductSpecBatchFilter }

/**
 * 只含被勾选字段的补丁：键存在才修改，缺省 = 不动；
 * 可空字段（unitWeightG / defaultHookGuid / notes）传 null = 清空；非空字段传 null 后端按校验错误处理，所以类型上不允许。
 */
export interface ProductSpecBatchPatch {
  widthMm?: number
  heightMm?: number
  depthMm?: number
  hangHoleOffsetMm?: number
  hangHoleType?: PlanogramHangHoleType
  unitWeightG?: number | null
  defaultHookGuid?: string | null
  source?: PlanogramSpecSource
  notes?: string | null
}

/** 每钩挂数覆盖：merge 只覆盖给出的挂钩；replace 整体替换（values 为空对象 = 清空全部覆盖）。缺省整个字段 = 不动。 */
export interface ProductSpecBatchHookQty {
  mode: 'merge' | 'replace'
  values: Record<string, number>
}

export interface ProductSpecBatchRequest {
  target: ProductSpecBatchTarget
  patch: ProductSpecBatchPatch
  hookQtyOverrides?: ProductSpecBatchHookQty
  /** 目标里「未设参数」的商品是否创建（创建要求补丁同时含宽、高、厚）。 */
  createMissing: boolean
  /** true = 完整校验与计数但不写库（预览）。 */
  dryRun: boolean
}

/** 跳过原因（后端枚举字符串）；界面遇到不认识的原因原样显示。 */
export const PLANOGRAM_BATCH_SKIP_REASONS = ['NO_SPEC', 'MISSING_DIMENSIONS', 'OFFSET_EXCEEDS_HEIGHT', 'PRODUCT_NOT_FOUND', 'OVERRIDES_TOO_LONG'] as const
export type PlanogramBatchSkipReason = (typeof PLANOGRAM_BATCH_SKIP_REASONS)[number]

export interface ProductSpecBatchSkippedItem {
  productCode: string
  itemNumber?: string | null
  productName?: string | null
  reason: string
}

/**
 * dryRun 时 created / updated / unchanged 恒为 0，计划数在 will*；真正执行时 will* 恒为 0，结果在 created / updated / unchanged。
 * skippedItems 最多 200 条，skipped 才是真实总数。
 */
/** 改前改后明细里的键：同 patch 白名单，挂数覆盖用对象键 hookQtyOverrides。 */
export interface ProductSpecBatchChangeValues {
  widthMm?: number
  heightMm?: number
  depthMm?: number
  hangHoleOffsetMm?: number
  hangHoleType?: PlanogramHangHoleType
  unitWeightG?: number | null
  defaultHookGuid?: string | null
  source?: PlanogramSpecSource
  notes?: string | null
  /** 整份覆盖（{ hookGuid: 件数 }，键排序）；没有覆盖时为 {}。 */
  hookQtyOverrides?: Record<string, number>
}

/**
 * 改前改后明细的一行（不变与跳过的不进表）：
 * - update：before / after 只含本次实际改变的字段，被清空的可空字段在 after 里显式为 null；
 * - create：before 恒为 null，after 给宽高厚、吊孔偏移 / 类型 / 来源（含缺省值）与其他已设置的字段。
 */
export interface ProductSpecBatchChangedItem {
  productCode: string
  itemNumber?: string | null
  productName?: string | null
  action: 'create' | 'update'
  before: ProductSpecBatchChangeValues | null
  after: ProductSpecBatchChangeValues
}

export interface ProductSpecBatchResultDto {
  dryRun: boolean
  matched: number
  created: number
  updated: number
  unchanged: number
  willCreate: number
  willUpdate: number
  willStayUnchanged: number
  skipped: number
  skippedItems: ProductSpecBatchSkippedItem[]
  /** 将创建 + 将更新（执行时为已创建 + 已更新）的真实总数。 */
  changedTotal: number
  /** 改前改后明细：按货号排序取前 50 条，dryRun 与真正执行都返回。 */
  changedItems: ProductSpecBatchChangedItem[]
}
