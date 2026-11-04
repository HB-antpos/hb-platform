import type { HookModelDto, PlanogramDetailDto, PlanogramItemDto, PlanogramSnapshotDto, ProductSpecListItemDto, ShelfModelDto } from '../../../../types/planogram'

/**
 * PDF 测试夹具：取自设计稿 05-pdf.html / plano.js（2 节 900×1200 单面洞洞板、双钩带价签 200、12 个商品、51 个挂钩）。
 * 仅供测试使用（不被生产代码引用）。设计稿的行距是在「间距 10 mm」规则之前画的，所以夹具默认间距 0，保证没有冲突；
 * 需要冲突的用例自己再加一条重叠的明细。
 */

export const FIXTURE_SHELF: ShelfModelDto = {
  guid: 'shelf-1', code: 'G-S-PEG-90x30x120', name: '单面洞洞板 900×1200 · 白', sideType: 1, color: '白', source: null,
  bayWidthMm: 900, totalHeightMm: 1200, baseDepthMm: 300, baseTopHeightMm: 150, uprightWidthMm: 30,
  holePitchXMm: 25, holePitchYMm: 25, holeDiameterMm: 6, firstHoleLeftMm: 25, firstHoleTopMm: 25, holeCols: 34, holeRows: 41,
  rowNumberingFrom: 1, isActive: true, referencedPlanCount: 1,
}

export const FIXTURE_HOOK: HookModelDto = {
  guid: 'hook-1', code: 'HK-D-PT-200', name: '双钩带价签 200', color: null, prongCount: 2, hasPriceTagArm: true, armLengthMm: 200, dropMm: 50,
  wireDiameterMm: 5, tipReserveMm: 20, tagWidthMm: 52, tagHeightMm: 26, tagBottomAboveArmMm: 10, holeSpanX: 2, holeSpanY: 1, loadKg: 3,
  isActive: true, referencedPlanCount: 1,
}

interface FixtureProduct { code: string; zh: string; en: string | null; w: number; h: number; t: number; q: number; price: number }

// q = 设计稿里的每钩挂数；这里用 hookQtyOverrides 把它固定下来（不依赖「臂长 ÷ 厚度」的建议值）
export const FIXTURE_PRODUCTS: Record<string, FixtureProduct> = {
  p1: { code: 'XD0001201', zh: '胶棒 2 支装', en: 'Glue Stick 2pk', w: 70, h: 150, t: 25, q: 7, price: 2.5 },
  p2: { code: 'XD0001202', zh: '圆珠笔 10 支', en: 'Ballpoint Pen 10pk', w: 90, h: 185, t: 20, q: 9, price: 3.5 },
  p3: { code: 'XD0001203', zh: '剪刀 170mm', en: 'Scissors 170mm', w: 95, h: 215, t: 15, q: 12, price: 4 },
  p4: { code: 'XD0001204', zh: '荧光笔 6 色', en: 'Highlighter 6pk', w: 130, h: 165, t: 18, q: 10, price: 5 },
  p5: { code: 'XD0001205', zh: '便利贴 3 本', en: 'Sticky Notes 3pk', w: 80, h: 105, t: 30, q: 6, price: 2 },
  p6: { code: 'XD0001206', zh: '修正带 2 个', en: 'Correction Tape 2pk', w: 75, h: 150, t: 25, q: 7, price: 3 },
  p7: { code: 'XD0001207', zh: '橡皮 3 个装', en: 'Eraser 3pk', w: 65, h: 120, t: 22, q: 8, price: 1.5 },
  p8: { code: 'XD0001208', zh: '马克笔 4 色', en: 'Marker 4pk', w: 115, h: 190, t: 20, q: 9, price: 4.5 },
  p9: { code: 'XD0001209', zh: '尺子 30cm', en: 'Ruler 30cm', w: 45, h: 330, t: 5, q: 20, price: 1.5 },
  p10: { code: 'XD0001210', zh: '胶带 2 卷', en: 'Tape 2pk', w: 100, h: 150, t: 55, q: 3, price: 3.5 },
  p11: { code: 'XD0001211', zh: '笔记本 A6', en: 'Notebook A6', w: 110, h: 155, t: 12, q: 15, price: 2.5 },
  p12: { code: 'XD0001212', zh: '订书机', en: 'Stapler', w: 90, h: 200, t: 40, q: 4, price: 6 },
}

/** 设计稿的 51 个挂钩：[节(0 起), 商品, 列(0 起), 行(0 起)]；转成明细时列 / 行 +1、节 +1。 */
const DESIGN_ITEMS: Array<[number, string, number, number]> = [
  [0, 'p3', 1, 2], [0, 'p3', 6, 2], [0, 'p2', 11, 2], [0, 'p2', 15, 2], [0, 'p8', 20, 2], [0, 'p4', 26, 2], [0, 'p12', 31, 2],
  [0, 'p1', 1, 12], [0, 'p1', 5, 12], [0, 'p1', 9, 12], [0, 'p6', 13, 12], [0, 'p6', 17, 12], [0, 'p11', 22, 12], [0, 'p11', 27, 12], [0, 'p7', 31, 12],
  [0, 'p5', 1, 22], [0, 'p5', 5, 22], [0, 'p5', 9, 22], [0, 'p5', 13, 22], [0, 'p10', 17, 22], [0, 'p10', 22, 22], [0, 'p10', 27, 22], [0, 'p7', 31, 22],
  [0, 'p11', 1, 31], [0, 'p11', 6, 31], [0, 'p1', 10, 31], [0, 'p1', 14, 31], [0, 'p7', 18, 31], [0, 'p7', 21, 31], [0, 'p7', 24, 31], [0, 'p2', 28, 31],
  [1, 'p8', 1, 2], [1, 'p8', 6, 2], [1, 'p8', 11, 2], [1, 'p4', 17, 2], [1, 'p4', 23, 2], [1, 'p9', 30, 2],
  [1, 'p2', 1, 12], [1, 'p2', 5, 12], [1, 'p12', 9, 12], [1, 'p3', 14, 12], [1, 'p3', 19, 12], [1, 'p6', 23, 12],
  [1, 'p5', 1, 22], [1, 'p5', 5, 22], [1, 'p5', 9, 22], [1, 'p10', 13, 22], [1, 'p1', 17, 22], [1, 'p1', 21, 22], [1, 'p11', 25, 22],
  [1, 'p7', 1, 31], [1, 'p7', 4, 31],
]

export function fixtureProductRows(options: { withoutEnglish?: string[] } = {}): ProductSpecListItemDto[] {
  return Object.values(FIXTURE_PRODUCTS).map((product) => ({
    productCode: product.code,
    itemNumber: product.code,
    barcode: `93${product.code.slice(-8)}`,
    productName: product.zh,
    englishName: options.withoutEnglish?.includes(product.code) ? null : product.en,
    productImage: null,
    retailPrice: product.price,
    categoryName: '文具',
    hasSpec: true,
    referencedPlanCount: 1,
    spec: {
      productCode: product.code, widthMm: product.w, heightMm: product.h, depthMm: product.t, hangHoleOffsetMm: 10, hangHoleType: 2,
      unitWeightG: null, defaultHookGuid: null, hookQtyOverrides: { [FIXTURE_HOOK.guid]: product.q }, source: 1, notes: null,
    },
  }))
}

export function fixtureItems(count = DESIGN_ITEMS.length): PlanogramItemDto[] {
  return DESIGN_ITEMS.slice(0, count).map(([bay, pid, col, row], index) => ({
    guid: `item-${String(index + 1).padStart(2, '0')}`,
    bayIndex: bay + 1,
    face: 0,
    itemType: 1,
    productCode: FIXTURE_PRODUCTS[pid].code,
    hookModelGuid: FIXTURE_HOOK.guid,
    holeCol: col + 1,
    holeRow: row + 1,
    perHookQty: null,
    sortOrder: index,
  }))
}

export interface FixtureDetailOptions {
  items?: PlanogramItemDto[]
  status?: 0 | 1 | 2
  version?: number
  name?: string
  bayCount?: number
  withoutEnglish?: string[]
  gap?: number
  shelf?: Partial<ShelfModelDto>
}

/** 设计稿样例的方案详情（getPlan 的返回形状）。 */
export function fixtureDetail(options: FixtureDetailOptions = {}): PlanogramDetailDto {
  const items = options.items ?? fixtureItems()
  const status = options.status ?? 1
  const version = options.version ?? 3
  const gap = options.gap ?? 0
  return {
    header: {
      guid: 'plan-1', name: options.name ?? '文具挂板 A1', notes: null, shelfModelGuid: FIXTURE_SHELF.guid, shelfModelName: FIXTURE_SHELF.name,
      bayCount: options.bayCount ?? 2, gapXMm: gap, gapYMm: gap, status, version, publishedVersion: status === 1 ? version : null,
      publishedAt: status === 1 ? '2026-11-03T14:30:00Z' : null, itemCount: items.length, skuCount: 12, unitCount: 433, fillPercent: 39, conflictCount: 0,
      hasUnpublishedChanges: false, updatedAt: '2026-11-03T14:30:00Z', updatedBy: 'admin',
    },
    rowVersion: '2026-11-03T14:30:00.0000000Z',
    items,
    shelfModel: { ...FIXTURE_SHELF, ...options.shelf },
    hookModels: [FIXTURE_HOOK],
    productSpecs: fixtureProductRows({ withoutEnglish: options.withoutEnglish }),
    conflicts: [],
    stats: { itemCount: items.length, skuCount: 12, unitCount: 433, retailValue: 1291, fillPercent: 39, conflictCount: 0 },
  }
}

/** 同一份数据组装成发布快照（getPlanVersion 的返回形状）。 */
export function fixtureSnapshot(options: FixtureDetailOptions = {}): PlanogramSnapshotDto {
  const detail = fixtureDetail(options)
  return {
    version: detail.header.version,
    publishedAt: '2026-11-03T14:30:00Z',
    publishedBy: 'admin',
    plan: { guid: detail.header.guid, name: detail.header.name, notes: null, bayCount: detail.header.bayCount, gapXMm: detail.header.gapXMm, gapYMm: detail.header.gapYMm },
    shelfModel: detail.shelfModel,
    hookModels: detail.hookModels,
    products: detail.productSpecs.flatMap((row) =>
      row.spec
        ? [{ productCode: row.productCode, itemNumber: row.itemNumber ?? '', barcode: row.barcode ?? '', productName: row.productName, englishName: row.englishName, productImage: row.productImage, retailPrice: row.retailPrice, spec: row.spec }]
        : [],
    ),
    items: detail.items,
    stats: detail.stats,
  }
}
