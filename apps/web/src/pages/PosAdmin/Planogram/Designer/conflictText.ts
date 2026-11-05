import type { TFunction } from 'i18next'
import { formatNumber } from '../planogramLogic'
import type { ConflictDetail } from '../engine/geometry'

/**
 * 冲突代码 → 界面文案。键必须以带引号的字面量出现（pageMessagesContract 按字面量静态核对缺失键），
 * 文案只引用「方案间距 / 孔阵尺寸」这类界面本来就知道的数字，所以服务端返回的冲突（没有 detail）也能完整显示。
 */
const CONFLICT_KEYS: Record<string, string> = {
  BAY_OUT_OF_RANGE: 'planogram.designer.conflict.BAY_OUT_OF_RANGE',
  PRODUCT_SPEC_MISSING: 'planogram.designer.conflict.PRODUCT_SPEC_MISSING',
  HOOK_MODEL_MISSING: 'planogram.designer.conflict.HOOK_MODEL_MISSING',
  HOLE_OUT_OF_RANGE: 'planogram.designer.conflict.HOLE_OUT_OF_RANGE',
  HOOK_HOLE_CLASH: 'planogram.designer.conflict.HOOK_HOLE_CLASH',
  OUT_OF_BOARD: 'planogram.designer.conflict.OUT_OF_BOARD',
  EDGE_GAP: 'planogram.designer.conflict.EDGE_GAP',
  OVERLAP: 'planogram.designer.conflict.OVERLAP',
  OVERLOAD: 'planogram.designer.conflict.OVERLOAD',
}

/** 承重警告带具体数字（本地复算有 detail）；服务端返回的没有时用不带数字的通用文案。 */
const OVERLOAD_DETAIL_KEY = 'planogram.designer.conflict.OVERLOAD_DETAIL'

export interface ConflictTextEnv {
  bayCount: number
  gapXMm: number
  gapYMm: number
  holeCols: number
  holeRows: number
}

export function conflictCodeKnown(code: string): boolean {
  return code in CONFLICT_KEYS
}

export function conflictText(
  t: TFunction,
  conflict: { code: string; detail?: ConflictDetail },
  env: ConflictTextEnv,
): string {
  const detail = conflict.detail
  if (conflict.code === 'OVERLOAD' && detail?.qty !== undefined && detail.totalKg !== undefined && detail.limitKg !== undefined) {
    return t(OVERLOAD_DETAIL_KEY, { qty: detail.qty, total: formatNumber(detail.totalKg), limit: formatNumber(detail.limitKg) })
  }
  const key = CONFLICT_KEYS[conflict.code] ?? 'planogram.designer.conflict.UNKNOWN'
  return t(key, {
    bayCount: env.bayCount,
    gapX: formatNumber(env.gapXMm),
    gapY: formatNumber(env.gapYMm),
    cols: env.holeCols,
    rows: env.holeRows,
  })
}
