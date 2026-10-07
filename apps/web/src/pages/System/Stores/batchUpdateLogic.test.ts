import {
  buildBatchUpdateStoresRequest,
  shouldClearStoreSelection,
} from './batchUpdateLogic'

function assertDeepEqual(actual: unknown, expected: unknown, label: string) {
  const actualJson = JSON.stringify(actual)
  const expectedJson = JSON.stringify(expected)
  if (actualJson !== expectedJson) {
    throw new Error(`${label}. Expected: ${expectedJson}, received: ${actualJson}`)
  }
}

function assertThrows(action: () => unknown, expectedCode: string, label: string) {
  try {
    action()
  } catch (error) {
    if (
      error instanceof Error
      && 'code' in error
      && String(error.code) === expectedCode
    ) {
      return
    }
    throw error
  }

  throw new Error(`${label}. Expected error code: ${expectedCode}`)
}

const request = buildBatchUpdateStoresRequest(
  ['store-1', 'store-2'],
  {
    applyTimeZoneId: true,
    timeZoneId: '  Australia/Sydney  ',
    applyAbn: true,
    abn: '   ',
    applyBrandName: true,
    brandName: '  Hot Bargain  ',
    applyIsActive: true,
    isActive: false,
    applyReturnPolicy: true,
    returnPolicy: '\n  ',
    applyVoucherTerms: true,
    voucherTerms: '  Use at the issuing store only.\nNot redeemable for cash.  ',
    applyInstallmentTerms: true,
    installmentTerms: ' \t\n ',
  },
)

assertDeepEqual(
  request,
  {
    storeGuids: ['store-1', 'store-2'],
    fields: ['timeZoneId', 'abn', 'brandName', 'isActive', 'returnPolicy', 'voucherTerms', 'installmentTerms'],
    timeZoneId: 'Australia/Sydney',
    abn: null,
    brandName: 'Hot Bargain',
    isActive: false,
    returnPolicy: null,
    voucherTerms: 'Use at the issuing store only.\nNot redeemable for cash.',
    installmentTerms: null,
  },
  '批量请求应裁剪文本（多行内部换行保留）、把空白转成 null，并保留显式 false；七个字段的顺序固定',
)

assertDeepEqual(
  buildBatchUpdateStoresRequest(
    ['store-3'],
    {
      applyAbn: true,
      abn: '12 345 678 901',
      timeZoneId: 'Australia/Perth',
      brandName: 'Ignored Brand',
      isActive: false,
      returnPolicy: 'Ignored policy',
      voucherTerms: 'Ignored voucher terms',
      installmentTerms: 'Ignored installment terms',
    },
  ),
  {
    storeGuids: ['store-3'],
    fields: ['abn'],
    abn: '12 345 678 901',
  },
  '未勾选字段不应进入请求体',
)

// 只勾选其中一个新字段：另一个即使有值也不进入请求体；勾选后留空＝清除定制（null）。
assertDeepEqual(
  buildBatchUpdateStoresRequest(
    ['store-4'],
    { applyVoucherTerms: true, voucherTerms: '', installmentTerms: 'Not selected' },
  ),
  { storeGuids: ['store-4'], fields: ['voucherTerms'], voucherTerms: null },
  '勾选代金券使用说明并留空应发送 null（清除定制），且不带上未勾选的分期条款',
)
assertDeepEqual(
  buildBatchUpdateStoresRequest(
    ['store-5'],
    { applyInstallmentTerms: true, installmentTerms: '  Order total: $50.00 minimum.  ', voucherTerms: 'Not selected' },
  ),
  { storeGuids: ['store-5'], fields: ['installmentTerms'], installmentTerms: 'Order total: $50.00 minimum.' },
  '勾选分期条款应裁剪首尾空白，且不带上未勾选的代金券使用说明',
)

assertThrows(
  () => buildBatchUpdateStoresRequest(['store-1'], {}),
  'NO_FIELDS_SELECTED',
  '未选择字段时应阻止提交',
)
assertThrows(
  () => buildBatchUpdateStoresRequest(['store-1'], { applyTimeZoneId: true, timeZoneId: '  ' }),
  'TIME_ZONE_REQUIRED',
  '勾选时区后空白值应阻止提交',
)
assertThrows(
  () => buildBatchUpdateStoresRequest([], { applyAbn: true, abn: '' }),
  'INVALID_TARGETS',
  '没有目标分店时应阻止提交',
)
assertDeepEqual(
  buildBatchUpdateStoresRequest(
    ['store-A', 'store-a'],
    { applyAbn: true, abn: '12 345 678 901' },
  ),
  {
    storeGuids: ['store-A', 'store-a'],
    fields: ['abn'],
    abn: '12 345 678 901',
  },
  '不透明分店标识仅在完全相同时才应视为重复',
)
assertThrows(
  () => buildBatchUpdateStoresRequest(
    ['store-1', 'store-1'],
    { applyAbn: true, abn: '' },
  ),
  'INVALID_TARGETS',
  '完全相同的分店标识仍应阻止提交',
)
assertThrows(
  () => buildBatchUpdateStoresRequest(['store-1'], { applyIsActive: true }),
  'IS_ACTIVE_REQUIRED',
  '勾选收银状态后必须保留明确布尔值',
)

assertDeepEqual(
  {
    query: shouldClearStoreSelection('query'),
    filter: shouldClearStoreSelection('filter'),
    sort: shouldClearStoreSelection('sort'),
    paginate: shouldClearStoreSelection('paginate'),
    refresh: shouldClearStoreSelection('refresh'),
  },
  {
    query: true,
    filter: true,
    sort: false,
    paginate: false,
    refresh: false,
  },
  '仅搜索和筛选范围变化时应清空跨页选择',
)

console.log('batchUpdateLogic.test: ok')
