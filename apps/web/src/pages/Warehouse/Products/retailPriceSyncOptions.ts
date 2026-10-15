// 仓库改零售价时的两个开关：同步分店零售价 / 生成价格任务（默认都勾选，仅组件内 state，不持久化）。
// 只有本次保存确实会改零售价时才展示开关、也才把字段带进请求；其它保存（改供货状态、进货价等）保持原请求体。
import type { WarehouseRetailPriceSyncOptions } from '../../../services/warehouseProductService'

export interface RetailPriceSyncChoice {
  syncStoreRetailPrices: boolean
  generateTasks: boolean
}

/** 每次打开编辑 / 批量修改 / 行内改价确认时都回到这组默认值（都勾选）。 */
export const DEFAULT_RETAIL_PRICE_SYNC_CHOICE: Readonly<RetailPriceSyncChoice> = Object.freeze({
  syncStoreRetailPrices: true,
  generateTasks: true,
})

/**
 * 表单里的零售价是否相对原值发生了变化：未填（null / undefined）视为没改，
 * 填了与原值相同的数也不算改。原值为空时与 null 比较，保证「从无到有」算改。
 */
export function isRetailPriceChanged(next: unknown, original: number | null | undefined): next is number {
  return typeof next === 'number' && next !== (original ?? null)
}

/** 批量修改表单里填了零售价（与提交时「只传填写字段」的过滤口径一致）才会改零售价。 */
export function isBatchRetailPriceFilled(value: unknown): value is number {
  return value !== undefined && value !== null
}

/**
 * 把勾选状态转成要随请求发送的字段：本次没有改零售价返回空对象（请求体不带这两个字段），
 * 改了则两个字段都按勾选状态显式发送（不勾 = false，不依赖后端默认值）。
 */
export function buildRetailPriceSyncFields(
  choice: RetailPriceSyncChoice,
  retailPriceChanged: boolean,
): WarehouseRetailPriceSyncOptions {
  if (!retailPriceChanged) {
    return {}
  }
  return {
    syncStoreRetailPrices: choice.syncStoreRetailPrices,
    generateTasks: choice.generateTasks,
  }
}
