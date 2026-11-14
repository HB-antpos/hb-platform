import type { WarehousePackProgress, WarehousePackProgressLine } from '../../../../types/packing'
import { PACK_CARTON_STATUS } from '../../../../types/packing'

// 订单详情「装箱情况」与「完成出库提醒」的纯逻辑：不依赖 React / DOM，便于单测。

type PackLineQuantities = Pick<WarehousePackProgressLine, 'pickedPieces' | 'packedPieces'>

/** 差 = 已拣 - 已装（与后端对账口径一致）：正数 = 拣了没装进箱，负数 = 箱里装多了。 */
export function packLineDiff(line: PackLineQuantities): number {
  return (line.pickedPieces ?? 0) - (line.packedPieces ?? 0)
}

export function hasPackLineMismatch(line: PackLineQuantities): boolean {
  return packLineDiff(line) !== 0
}

/** 差值显示：正数带 +，0 显示 0。 */
export function formatPackDiff(diff: number): string {
  return diff > 0 ? `+${diff}` : String(diff)
}

/** 件数显示：订货件数后端是 decimal，整数不带小数，否则最多两位。 */
export function formatPackPieces(value: number | null | undefined): string {
  const pieces = Number(value ?? 0)
  return pieces.toLocaleString('en-US', { maximumFractionDigits: 2 })
}

/** 商品对照表排序：有差异的排在前面，其余保持后端给的顺序。 */
export function sortPackLines(lines: WarehousePackProgressLine[]): WarehousePackProgressLine[] {
  return [...lines]
    .map((line, index) => ({ line, index }))
    .sort((a, b) => Number(hasPackLineMismatch(b.line)) - Number(hasPackLineMismatch(a.line)) || a.index - b.index)
    .map((entry) => entry.line)
}

/** 订单没有任何箱 = 没用装箱功能，整个「装箱情况」区和出库提醒都不出现。 */
export function shouldShowPackProgress(progress: Pick<WarehousePackProgress, 'cartons'> | null | undefined): boolean {
  return (progress?.cartons?.length ?? 0) > 0
}

export type PackCartonStatusKind = 'packing' | 'sealed' | 'transit' | 'unknown'

/** 箱状态：1 装箱中 / 2 已封箱 / 3 在途（作废的箱后端不返回）。 */
export function packCartonStatusKind(status: number): PackCartonStatusKind {
  switch (status) {
    case PACK_CARTON_STATUS.Packing:
      return 'packing'
    case PACK_CARTON_STATUS.Sealed:
      return 'sealed'
    case PACK_CARTON_STATUS.InTransit:
      return 'transit'
    default:
      return 'unknown'
  }
}

export interface PackProgressSummary {
  orderedPieces: number
  pickedPieces: number
  packedPieces: number
  cartonCount: number
  packingCartonCount: number
  sealedCartonCount: number
  transitCartonCount: number
  /** 装箱中且没有任何商品的空箱（含在 packingCartonCount 里）。 */
  emptyCartonCount: number
  /** 拣货数与装箱数对不上的商品数。 */
  mismatchLineCount: number
  /** 已装箱件数与已拣件数不一致：摘要里的「已装箱」用警示色。 */
  packedDiffersFromPicked: boolean
}

/** 三格摘要（已拣件数 / 已装箱件数 / 箱数）与差异计数；全部由后端返回的商品行与箱列表汇总。 */
export function summarizePackProgress(progress: Pick<WarehousePackProgress, 'lines' | 'cartons'>): PackProgressSummary {
  const lines = progress.lines ?? []
  const cartons = progress.cartons ?? []
  const sum = (pick: (line: WarehousePackProgressLine) => number) => lines.reduce((total, line) => total + Number(pick(line) ?? 0), 0)
  const pickedPieces = sum((line) => line.pickedPieces)
  const packedPieces = sum((line) => line.packedPieces)
  const countStatus = (status: number) => cartons.filter((carton) => carton.status === status).length
  return {
    orderedPieces: sum((line) => line.orderedPieces),
    pickedPieces,
    packedPieces,
    cartonCount: cartons.length,
    packingCartonCount: countStatus(PACK_CARTON_STATUS.Packing),
    sealedCartonCount: countStatus(PACK_CARTON_STATUS.Sealed),
    transitCartonCount: countStatus(PACK_CARTON_STATUS.InTransit),
    emptyCartonCount: cartons.filter((carton) => carton.status === PACK_CARTON_STATUS.Packing && carton.pieceCount === 0).length,
    mismatchLineCount: lines.filter(hasPackLineMismatch).length,
    packedDiffersFromPicked: pickedPieces !== packedPieces,
  }
}

export interface PackCompletionWarning {
  /** 还没封箱的箱数（装箱中，含空箱）。 */
  openCartonCount: number
  /** 其中没有任何商品的空箱数。 */
  emptyCartonCount: number
  /** 拣货数与装箱数对不上的商品数。 */
  mismatchLineCount: number
}

/**
 * 完成出库前的装箱提醒（只提醒、不拦截）：订单用了装箱（至少有一只箱）且「有未封箱」或「没对上账」才提醒。
 * 订单一只箱都没有时后端的 reconciled 恒为 false（拣了货就算没对上），但那是没用装箱功能，不能打扰主管。
 */
export function resolvePackCompletionWarning(progress: WarehousePackProgress | null | undefined): PackCompletionWarning | null {
  if (!progress || !shouldShowPackProgress(progress)) {
    return null
  }
  if (!(progress.openCartonCount > 0) && progress.reconciled) {
    return null
  }
  const summary = summarizePackProgress(progress)
  return {
    openCartonCount: progress.openCartonCount,
    emptyCartonCount: progress.emptyCartonCount,
    mismatchLineCount: summary.mismatchLineCount,
  }
}
