import type { PromotionScopeType, PromotionStoreItemDto } from '../../../types/promotion'

type PromotionStoreScopeSource = {
  stores?: PromotionStoreItemDto[] | null
  scopeType?: PromotionScopeType | null
}

export function getPromotionStoreCodes(stores?: PromotionStoreItemDto[] | null) {
  return (stores ?? [])
    .map((store) => store.storeCode?.trim())
    .filter((storeCode): storeCode is string => !!storeCode)
}

/**
 * 是否未绑定任何分店。
 * 统一口径：分店必选；未绑定分店的促销收银端不计算（不再视为全部分店），后端新建 / 编辑也会拒绝空分店。
 */
export function isPromotionWithoutStores(source: PromotionStoreScopeSource) {
  return getPromotionStoreCodes(source.stores).length === 0
}

/** 编辑时回填原始分店；旧的未绑定促销回填为空，由表单必填校验要求补选。 */
export function getPromotionEditorStoreCodes(source: PromotionStoreScopeSource) {
  return getPromotionStoreCodes(source.stores)
}
