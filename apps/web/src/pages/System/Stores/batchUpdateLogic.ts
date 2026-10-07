import type {
  BatchUpdateStoresRequest,
  StoreBatchUpdateField,
} from '../../../types/store'

export interface BatchUpdateStoreFormValues {
  applyTimeZoneId?: boolean
  timeZoneId?: string
  applyAbn?: boolean
  abn?: string
  applyBrandName?: boolean
  brandName?: string
  applyIsActive?: boolean
  isActive?: boolean
  applyReturnPolicy?: boolean
  returnPolicy?: string
  applyVoucherTerms?: boolean
  voucherTerms?: string
  applyInstallmentTerms?: boolean
  installmentTerms?: string
}

export type StoreSelectionScopeAction =
  | 'query'
  | 'filter'
  | 'sort'
  | 'paginate'
  | 'refresh'

export class BatchUpdateRequestError extends Error {
  constructor(public readonly code: string) {
    super(code)
    this.name = 'BatchUpdateRequestError'
  }
}

function trimToNullable(value?: string) {
  const normalized = value?.trim()
  return normalized ? normalized : null
}

export function buildBatchUpdateStoresRequest(
  storeGuids: string[],
  values: BatchUpdateStoreFormValues,
): BatchUpdateStoresRequest {
  if (
    storeGuids.length < 1
    || storeGuids.length > 100
    || new Set(storeGuids).size !== storeGuids.length
  ) {
    throw new BatchUpdateRequestError('INVALID_TARGETS')
  }

  const fields: StoreBatchUpdateField[] = []
  const request: BatchUpdateStoresRequest = {
    storeGuids: [...storeGuids],
    fields,
  }

  if (values.applyTimeZoneId) {
    const timeZoneId = values.timeZoneId?.trim()
    if (!timeZoneId) {
      throw new BatchUpdateRequestError('TIME_ZONE_REQUIRED')
    }
    fields.push('timeZoneId')
    request.timeZoneId = timeZoneId
  }

  if (values.applyAbn) {
    fields.push('abn')
    request.abn = trimToNullable(values.abn)
  }

  if (values.applyBrandName) {
    fields.push('brandName')
    request.brandName = trimToNullable(values.brandName)
  }

  if (values.applyIsActive) {
    if (typeof values.isActive !== 'boolean') {
      throw new BatchUpdateRequestError('IS_ACTIVE_REQUIRED')
    }
    fields.push('isActive')
    request.isActive = values.isActive
  }

  if (values.applyReturnPolicy) {
    fields.push('returnPolicy')
    request.returnPolicy = trimToNullable(values.returnPolicy)
  }

  // 代金券使用说明 / 分期条款：与退换货政策同口径（trim，空白变 null）。null＝清除定制，下发后收银端回到内置默认文案。
  if (values.applyVoucherTerms) {
    fields.push('voucherTerms')
    request.voucherTerms = trimToNullable(values.voucherTerms)
  }

  if (values.applyInstallmentTerms) {
    fields.push('installmentTerms')
    request.installmentTerms = trimToNullable(values.installmentTerms)
  }

  if (fields.length === 0) {
    throw new BatchUpdateRequestError('NO_FIELDS_SELECTED')
  }

  return request
}

export function shouldClearStoreSelection(action: StoreSelectionScopeAction) {
  return action === 'query' || action === 'filter'
}
