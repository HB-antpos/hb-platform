/**
 * 购物车提交前后的暂停供货汇总：纯函数，供抽屉的确认弹窗与结果弹窗共用。
 * 规则与后端一致：仓库已下架（isActive === false）的行提交时保留在购物车；
 * 其中供货说明为「不再供应」的行单独列出，提示店员删除。
 */
export interface CartSubmitLineLike {
  productCode: string
  itemNumber?: string | null
  isActive?: boolean
  supplyPlan?: string | null
  quantity?: number
  importAmount?: number
}

export interface CartSubmitSummary {
  /** 会进单的行数 */
  submittableCount: number
  /** 会进单的件数（只累计可提交行） */
  submittableQuantity: number
  /** 会进单的进货金额（只累计可提交行） */
  submittableImportAmount: number
  /** 暂停供货、提交时保留在购物车的货号（不含不再供应） */
  pausedLabels: string[]
  /** 已不再供应、需要店员删除的货号 */
  discontinuedLabels: string[]
}

const DISCONTINUED = 'Discontinued'

/** 提示里用货号，没有货号时退回商品编码。 */
export function getCartSubmitLabel(line: CartSubmitLineLike): string {
  const itemNumber = line.itemNumber?.trim()
  return itemNumber || line.productCode
}

function isKeptLine(line: CartSubmitLineLike): boolean {
  // 旧后端不返回 isActive 时视为可提交，与现有 UI 的 `=== false` 判定一致。
  return line.isActive === false
}

function isDiscontinued(line: CartSubmitLineLike): boolean {
  return line.supplyPlan === DISCONTINUED
}

/** 按当前购物车数据预估提交结果，用于提交前确认。 */
export function summarizeCartForSubmit(items: readonly CartSubmitLineLike[]): CartSubmitSummary {
  const summary: CartSubmitSummary = {
    submittableCount: 0,
    submittableQuantity: 0,
    submittableImportAmount: 0,
    pausedLabels: [],
    discontinuedLabels: [],
  }
  for (const line of items) {
    if (!isKeptLine(line)) {
      summary.submittableCount += 1
      summary.submittableQuantity += Number(line.quantity ?? 0) || 0
      summary.submittableImportAmount += Number(line.importAmount ?? 0) || 0
      continue
    }
    if (isDiscontinued(line)) {
      summary.discontinuedLabels.push(getCartSubmitLabel(line))
    } else {
      summary.pausedLabels.push(getCartSubmitLabel(line))
    }
  }
  return summary
}

/** 把后端返回的保留行归一化成同样的结构；这些行全部是保留行，只按供货计划分组。 */
export function summarizeKeptLines(
  lines: readonly CartSubmitLineLike[],
  submittedLineCount = 0,
): CartSubmitSummary {
  const summary: CartSubmitSummary = {
    submittableCount: submittedLineCount,
    submittableQuantity: 0,
    submittableImportAmount: 0,
    pausedLabels: [],
    discontinuedLabels: [],
  }
  for (const line of lines) {
    if (isDiscontinued(line)) {
      summary.discontinuedLabels.push(getCartSubmitLabel(line))
    } else {
      summary.pausedLabels.push(getCartSubmitLabel(line))
    }
  }
  return summary
}

/** 提示里最多列出前 max 个货号，其余折叠成「等 N 个」。 */
export function splitLabelsForDisplay(labels: readonly string[], max = 10): { shown: string[]; more: number } {
  if (labels.length <= max) {
    return { shown: [...labels], more: 0 }
  }
  return { shown: labels.slice(0, max), more: labels.length - max }
}
