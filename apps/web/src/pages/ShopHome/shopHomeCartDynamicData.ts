import type { StoreOrderDynamicData } from '../../types/storeOrder'

export interface ShopHomeDynamicDataRequestIdentityInput {
  active: boolean
  storeCode: string | null | undefined
  productCodes: string[]
}

export function buildShopHomeDynamicDataRequestIdentity({
  active,
  storeCode,
  productCodes,
}: ShopHomeDynamicDataRequestIdentityInput): string | null {
  if (!active || !storeCode || productCodes.length === 0) {
    return null
  }

  return JSON.stringify([storeCode, ...productCodes])
}

export function readShopHomeDynamicDataRequestProductCodes(identity: string | null): string[] {
  if (!identity) {
    return []
  }

  const parsedIdentity = JSON.parse(identity) as unknown
  if (!Array.isArray(parsedIdentity)) {
    return []
  }

  return parsedIdentity.slice(1).filter((code): code is string => typeof code === 'string')
}

export interface ShopHomeDynamicDataStoreScopeToken {
  storeCode: string
  generation: number
}

export interface ShopHomeDynamicDataStoreScopeCoordinator {
  activate: (storeCode: string | null) => void
  deactivate: (storeCode: string | null) => void
  capture: (storeCode: string | null) => ShopHomeDynamicDataStoreScopeToken | null
  isCurrent: (token: ShopHomeDynamicDataStoreScopeToken) => boolean
}

export function createShopHomeDynamicDataStoreScopeCoordinator(): ShopHomeDynamicDataStoreScopeCoordinator {
  let activeStoreCode: string | null = null
  let generation = 0

  return {
    activate(storeCode) {
      if (activeStoreCode === storeCode) {
        return
      }

      activeStoreCode = storeCode
      // 仅在已提交的门店切换时推进 generation，S1 -> S2 -> S1 的旧请求必须失效。
      generation += 1
    },
    deactivate(storeCode) {
      if (activeStoreCode !== storeCode) {
        return
      }

      activeStoreCode = null
      generation += 1
    },
    capture(storeCode) {
      if (!storeCode || activeStoreCode !== storeCode) {
        return null
      }

      return { storeCode, generation }
    },
    isCurrent(token) {
      return activeStoreCode === token.storeCode && generation === token.generation
    },
  }
}

export interface ShopHomeDynamicDataRequestToken {
  identity: string
  version: number
}

export interface ShopHomeDynamicDataRequestCoordinator {
  activate: (identity: string | null) => void
  deactivate: (identity: string | null) => void
  begin: (identity: string | null) => ShopHomeDynamicDataRequestToken | null
  invalidate: (token: ShopHomeDynamicDataRequestToken | null) => void
  isCurrent: (token: ShopHomeDynamicDataRequestToken) => boolean
}

export function createShopHomeDynamicDataRequestCoordinator(): ShopHomeDynamicDataRequestCoordinator {
  let activeIdentity: string | null = null
  let activeToken: ShopHomeDynamicDataRequestToken | null = null
  let version = 0

  const isCurrent = (token: ShopHomeDynamicDataRequestToken) =>
    activeIdentity === token.identity &&
    activeToken?.identity === token.identity &&
    activeToken.version === token.version

  return {
    activate(identity) {
      if (activeIdentity === identity) {
        return
      }

      activeIdentity = identity
      activeToken = null
      version += 1
    },
    deactivate(identity) {
      if (activeIdentity !== identity) {
        return
      }

      // layout cleanup 只失效它自己提交的身份，不得破坏后续已激活身份。
      activeIdentity = null
      activeToken = null
      version += 1
    },
    begin(identity) {
      if (!identity || activeIdentity !== identity) {
        return null
      }

      // 每次真实请求分配单调 version，identity 发生 ABA 时旧 token 仍会失效。
      version += 1
      const token = { identity, version }
      activeToken = token
      return token
    },
    invalidate(token) {
      if (!token || !isCurrent(token)) {
        return
      }

      activeToken = null
      version += 1
    },
    isCurrent,
  }
}

export interface ShopHomeSalesSummaryRequestToken {
  identity: string
  version: number
}

export interface ShopHomeSalesSummaryRequestCoordinator {
  activate: (identity: string | null) => void
  deactivate: (identity: string | null) => void
  begin: (identity: string | null) => ShopHomeSalesSummaryRequestToken | null
  isCurrent: (token: ShopHomeSalesSummaryRequestToken) => boolean
}

export function createShopHomeSalesSummaryRequestCoordinator(): ShopHomeSalesSummaryRequestCoordinator {
  let activeIdentity: string | null = null
  let activeToken: ShopHomeSalesSummaryRequestToken | null = null
  let version = 0

  const isCurrent = (token: ShopHomeSalesSummaryRequestToken) =>
    activeIdentity === token.identity &&
    activeToken?.identity === token.identity &&
    activeToken.version === token.version

  return {
    activate(identity) {
      if (activeIdentity === identity) {
        return
      }

      activeIdentity = identity
      activeToken = null
      version += 1
    },
    deactivate(identity) {
      if (activeIdentity !== identity) {
        return
      }

      activeIdentity = null
      activeToken = null
      version += 1
    },
    begin(identity) {
      if (!identity || activeIdentity !== identity) {
        return null
      }

      // Sales 独立版本号覆盖切店、分页、筛选及 ABA 回到相同 identity 的旧响应。
      version += 1
      const token = { identity, version }
      activeToken = token
      return token
    },
    isCurrent,
  }
}

export interface RunShopHomeDynamicDataRequestOptions<TResult> {
  coordinator: ShopHomeDynamicDataRequestCoordinator
  token: ShopHomeDynamicDataRequestToken | null
  productCodes: string[]
  request: (productCodes: string[]) => Promise<TResult>
  onSuccess: (result: TResult) => void
  onError: (error: unknown) => void
}

export async function runShopHomeDynamicDataRequest<TResult>({
  coordinator,
  token,
  productCodes,
  request,
  onSuccess,
  onError,
}: RunShopHomeDynamicDataRequestOptions<TResult>): Promise<void> {
  if (!token || productCodes.length === 0) {
    return
  }

  try {
    const result = await request(productCodes)
    // identity 与 version 必须同时匹配，防止分页/切店 ABA 后旧 Sales 数据回填。
    if (coordinator.isCurrent(token)) {
      onSuccess(result)
    }
  } catch (error) {
    // 旧批次的错误同样不能清空当前页已经加载的动态数据。
    if (coordinator.isCurrent(token)) {
      onError(error)
    }
  }
}

export interface RunShopHomeSalesSummaryRequestOptions<TResult> {
  coordinator: ShopHomeSalesSummaryRequestCoordinator
  token: ShopHomeSalesSummaryRequestToken | null
  productCodes: string[]
  request: (productCodes: string[]) => Promise<TResult>
  onSuccess: (result: TResult) => void
  onError: (error: unknown) => void
}

export async function runShopHomeSalesSummaryRequest<TResult>({
  coordinator,
  token,
  productCodes,
  request,
  onSuccess,
  onError,
}: RunShopHomeSalesSummaryRequestOptions<TResult>): Promise<void> {
  if (!token || productCodes.length === 0) {
    return
  }

  try {
    const result = await request(productCodes)
    if (coordinator.isCurrent(token)) {
      onSuccess(result)
    }
  } catch (error) {
    // 旧批次失败不能清掉基础动态数据或已完成的优先 Sales。
    if (coordinator.isCurrent(token)) {
      onError(error)
    }
  }
}

export interface RunShopHomeStoreScopedDynamicDataRequestOptions<TResult> {
  coordinator: ShopHomeDynamicDataStoreScopeCoordinator
  token: ShopHomeDynamicDataStoreScopeToken | null
  productCodes: string[]
  request: (productCodes: string[]) => Promise<TResult>
  onSuccess: (result: TResult) => void
}

export async function runShopHomeStoreScopedDynamicDataRequest<TResult>({
  coordinator,
  token,
  productCodes,
  request,
  onSuccess,
}: RunShopHomeStoreScopedDynamicDataRequestOptions<TResult>): Promise<void> {
  if (!token || productCodes.length === 0) {
    return
  }

  const result = await request(productCodes)
  // 局部刷新返回前同时校验门店与 generation，避免切店 ABA 时旧 Sales 回填。
  if (coordinator.isCurrent(token)) {
    onSuccess(result)
  }
}

export function mergeShopHomeBaseDynamicDataMap(
  previousMap: Record<string, StoreOrderDynamicData>,
  nextBaseMap: Record<string, StoreOrderDynamicData>,
): Record<string, StoreOrderDynamicData> {
  const nextMap = { ...previousMap }
  Object.entries(nextBaseMap).forEach(([productCode, nextBaseData]) => {
    const previousData = previousMap[productCode]
    // 局部刷新只更新购物车与最近订单等基础字段；已经异步加载的 Sales 与来货日期不得被 undefined 覆盖。
    nextMap[productCode] =
      previousData?.salesQuantitySinceLastArrival === undefined
        ? nextBaseData
        : {
            ...nextBaseData,
            salesQuantitySinceLastArrival: previousData.salesQuantitySinceLastArrival,
            lastArrivalDate: previousData.lastArrivalDate,
          }
  })

  return nextMap
}

export interface MergeShopHomeCartDynamicDataInput {
  dynamicData?: StoreOrderDynamicData
  productCode: string
  cartQuantity?: number
}

export function mergeShopHomeCartDynamicData({
  dynamicData,
  productCode,
  cartQuantity,
}: MergeShopHomeCartDynamicDataInput): StoreOrderDynamicData {
  const nextCartQuantity = cartQuantity ?? dynamicData?.cartQuantity ?? 0
  if (dynamicData && dynamicData.productCode === productCode && dynamicData.cartQuantity === nextCartQuantity) {
    // 未改变内容时保留原引用，React.memo 才能跳过 Sales 分批期间未变化的商品卡。
    return dynamicData
  }

  return {
    ...(dynamicData ?? { productCode, cartQuantity: 0 }),
    productCode: dynamicData?.productCode ?? productCode,
    // full cart 的明细数量是当前真值；summary-only 则继续沿用动态接口数量。
    cartQuantity: nextCartQuantity,
  }
}
