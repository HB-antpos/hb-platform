import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import { batchExecuteActions } from '../../../../services/localSupplierInvoiceService'
import { DetailAction } from '../../../../types/localSupplierInvoice'
import { RequestError } from '../../../../utils/request'
import {
  buildBatchExecuteConfirmText,
  buildBatchExecuteSnapshot,
  getBatchExecuteErrorFeedback,
  getNewProductWithAdditionalBarcodesRows,
  constrainSelectedRowKeysToVisibleDetails,
  countSelectedBatchExecuteActions,
  pickPurchasePriceDirectionGuids,
  splitPurchasePriceDirectionGuids,
} from './batchExecuteConfirm'

function assert(condition: unknown, message: string): asserts condition {
  if (!condition) {
    throw new Error(message)
  }
}

function assertEqual<T>(actual: T, expected: T, message: string) {
  if (actual !== expected) {
    throw new Error(`${message}。Expected: ${String(expected)}, received: ${String(actual)}`)
  }
}

function assertDeepEqual(actual: unknown, expected: unknown, message: string) {
  const actualJson = JSON.stringify(actual)
  const expectedJson = JSON.stringify(expected)

  if (actualJson !== expectedJson) {
    throw new Error(`${message}。Expected: ${expectedJson}, received: ${actualJson}`)
  }
}

async function runTest(name: string, execute: () => void | Promise<void>): Promise<string | null> {
  try {
    await execute()
    console.log(`ok - ${name}`)
    return null
  } catch (error) {
    const reason = error instanceof Error ? error.message : String(error)
    console.error(`not ok - ${name}`)
    console.error(reason)
    return `${name}: ${reason}`
  }
}

async function main() {
  const failures: string[] = []

  const countFailure = await runTest('确认统计应按选中明细计算新建商品数量', () => {
    const result = countSelectedBatchExecuteActions(
      ['d1', 'd2', 'd3'],
      [
        { detailGUID: 'd1', activityType: DetailAction.CreateProduct },
        { detailGUID: 'd2', activityType: DetailAction.UpdatePurchasePrice },
        { detailGUID: 'd3', activityType: DetailAction.WaitForOperation },
        { detailGUID: 'd4', activityType: DetailAction.CreateProduct },
      ],
      { d2: DetailAction.CreateProduct },
    )

    assertEqual(result.selectedCount, 3, '应统计选中条数')
    assertEqual(result.createProductCount, 2, 'rowActions 应覆盖明细原始操作类型')
    assertEqual(result.createProductWithAdditionalBarcodesCount, 0, '无副码时不应计入副码新商品数量')
  })
  if (countFailure) failures.push(countFailure)

  const additionalBarcodeRowsFailure = await runTest('有副码新商品应生成类型选择行', () => {
    const rows = getNewProductWithAdditionalBarcodesRows(
      ['d1', 'd2', 'd3'],
      [
        {
          detailGUID: 'd1',
          activityType: DetailAction.CreateProduct,
          additionalBarcodes: ['191554882690', '191554882669'],
          itemNumber: '88842',
          barcode: '191554882676',
          productName: 'Men Travel Perfume Assorted 35mL',
        },
        {
          detailGUID: 'd2',
          activityType: DetailAction.CreateProduct,
          additionalBarcodes: [],
          itemNumber: '15142',
          barcode: '752527840019',
          productName: 'Women Perfume',
        },
        {
          detailGUID: 'd3',
          activityType: DetailAction.UpdatePurchasePrice,
          additionalBarcodes: ['副码不应计入'],
          itemNumber: 'OLD',
          barcode: 'OLD-BARCODE',
          productName: 'Old Product',
        },
      ],
      {},
    )

    assertDeepEqual(
      rows,
      [
        {
          detailGuid: 'd1',
          itemNumber: '88842',
          barcode: '191554882676',
          productName: 'Men Travel Perfume Assorted 35mL',
          additionalBarcodeCount: 2,
        },
      ],
      '只有 CreateProduct 且带副码的明细需要进入类型选择',
    )
  })
  if (additionalBarcodeRowsFailure) failures.push(additionalBarcodeRowsFailure)

  const snapshotFailure = await runTest('确认时应冻结 batch execute 快照并保留当前 action 与 activityType', () => {
    const selectedRowKeys = ['d1', 'd2']
    const details = [
      { detailGUID: 'd1', activityType: DetailAction.CreateProduct },
      { detailGUID: 'd2', activityType: DetailAction.UpdatePurchasePrice },
      { detailGUID: 'd3', activityType: DetailAction.WaitForOperation },
    ]
    const rowActions = {
      d2: DetailAction.CreateProduct,
      d3: DetailAction.None,
    }

    const snapshot = buildBatchExecuteSnapshot({
      selectedRowKeys,
      details,
      rowActions,
      confirmedAt: '2026-06-02T09:30:00.000Z',
    })

    assertEqual(snapshot.selectedCount, 2, '快照应保留确认当刻选中条数')
    assertEqual(snapshot.confirmedCreateProductCount, 2, '快照应保留确认当刻新建商品数量')
    assertDeepEqual(snapshot.newProductProductTypeSelections, [], '无副码新商品时选择契约为空数组')
    assertDeepEqual(
      snapshot.detailGuids,
      ['d1', 'd2'],
      '快照应复制确认当刻的明细主键',
    )
    assertDeepEqual(
      snapshot.expectedActions,
      [
        { detailGuid: 'd1', action: DetailAction.CreateProduct, activityType: DetailAction.CreateProduct },
        { detailGuid: 'd2', action: DetailAction.CreateProduct, activityType: DetailAction.UpdatePurchasePrice },
      ],
      '快照应同时携带当前 action 与原始 activityType',
    )
    assertEqual(snapshot.confirmedAt, '2026-06-02T09:30:00.000Z', '快照应保留确认时间')

    selectedRowKeys.push('d3')
    details[0].activityType = DetailAction.None
    rowActions.d2 = DetailAction.UpdateItemNumber

    assertDeepEqual(
      snapshot.detailGuids,
      ['d1', 'd2'],
      '确认后外部选择变化不应污染已生成的 payload 快照',
    )
    assertDeepEqual(
      snapshot.expectedActions,
      [
        { detailGuid: 'd1', action: DetailAction.CreateProduct, activityType: DetailAction.CreateProduct },
        { detailGuid: 'd2', action: DetailAction.CreateProduct, activityType: DetailAction.UpdatePurchasePrice },
      ],
      '确认后行操作变化不应污染已生成的 expectedActions 快照',
    )
  })
  if (snapshotFailure) failures.push(snapshotFailure)

  const productTypeSelectionSnapshotFailure = await runTest('确认快照应携带新商品副码类型选择', () => {
    const snapshot = buildBatchExecuteSnapshot({
      selectedRowKeys: ['d1'],
      details: [
        { detailGUID: 'd1', activityType: DetailAction.CreateProduct },
      ],
      rowActions: {},
      newProductProductTypeSelections: [
        { detailGuid: 'd1', productType: 2 },
      ],
      confirmedAt: '2026-06-02T10:00:00.000Z',
    })

    assertDeepEqual(
      snapshot.newProductProductTypeSelections,
      [
        { detailGuid: 'd1', productType: 2 },
      ],
      '新商品副码类型选择应进入批量执行 payload 快照',
    )
  })
  if (productTypeSelectionSnapshotFailure) failures.push(productTypeSelectionSnapshotFailure)

  const updateItemNumberSnapshotFailure = await runTest('确认快照应正向携带更新货号操作', () => {
    const snapshot = buildBatchExecuteSnapshot({
      selectedRowKeys: ['d1'],
      details: [
        { detailGUID: 'd1', activityType: DetailAction.UpdateItemNumber },
      ],
      rowActions: {
        d1: DetailAction.UpdateItemNumber,
      },
      confirmedAt: '2026-06-02T09:45:00.000Z',
    })

    assertDeepEqual(
      snapshot.expectedActions,
      [
        { detailGuid: 'd1', action: DetailAction.UpdateItemNumber, activityType: DetailAction.UpdateItemNumber },
      ],
      '更新货号应作为当前确认动作进入 expectedActions',
    )
    assertEqual(snapshot.confirmedCreateProductCount, 0, '更新货号不应计入新建商品数量')
  })
  if (updateItemNumberSnapshotFailure) failures.push(updateItemNumberSnapshotFailure)

  const textFailure = await runTest('确认文案应包含执行数量和新建商品风险提示', () => {
    const text = buildBatchExecuteConfirmText({
      selectedCount: 3,
      createProductCount: 2,
      labels: {
        title: '确认执行批量操作？',
        content: '将对 {{count}} 条明细执行已设置的操作。',
        createProductNotice: '其中 {{count}} 条会新建商品，请确认货号、条码和名称无误。',
        okText: '确认执行',
        cancelText: '取消',
      },
    })

    assertEqual(text.title, '确认执行批量操作？', '应返回确认标题')
    assert(text.content.includes('3 条明细'), '应在正文中展示选中条数')
    assert(text.content.includes('2 条会新建商品'), '应在正文中展示新建商品风险提示')
    assertEqual(text.okText, '确认执行', '应返回确认按钮文案')
    assertEqual(text.cancelText, '取消', '应返回取消按钮文案')
  })
  if (textFailure) failures.push(textFailure)

  const servicePayloadFailure = await runTest('batchExecuteActions 应发送确认快照契约字段', async () => {
    const originalFetch = globalThis.fetch
    let capturedUrl = ''
    let capturedInit: RequestInit | undefined

    globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
      capturedUrl = String(input)
      capturedInit = init

      return new Response(JSON.stringify({
        success: true,
        data: {
          createdProducts: 1,
          updatedPurchasePrices: 0,
          updatedItemNumbers: 0,
          addedMultiCodes: 0,
          skipped: 0,
          failed: 0,
          errors: [],
        },
      }), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      })
    }) as typeof fetch

    try {
      await batchExecuteActions({
        invoiceGuid: 'invoice-1',
        detailGuids: ['d1', 'd2'],
        expectedActions: [
          { detailGuid: 'd1', action: DetailAction.CreateProduct, activityType: DetailAction.CreateProduct },
          { detailGuid: 'd2', action: DetailAction.UpdatePurchasePrice, activityType: DetailAction.UpdatePurchasePrice },
        ],
        confirmedCreateProductCount: 1,
        newProductProductTypeSelections: [
          { detailGuid: 'd1', productType: 2 },
        ],
        confirmedAt: '2026-06-02T09:30:00.000Z',
      })
    } finally {
      globalThis.fetch = originalFetch
    }

    assertEqual(
      capturedUrl,
      '/api/react/v1/local-supplier-invoices/invoice-1/details/batch-execute',
      '批量执行应调用固定接口',
    )
    assertEqual(capturedInit?.method, 'POST', '批量执行应使用 POST')
    assertDeepEqual(
      JSON.parse(String(capturedInit?.body)),
      {
        detailGuids: ['d1', 'd2'],
        expectedActions: [
          { detailGuid: 'd1', action: DetailAction.CreateProduct, activityType: DetailAction.CreateProduct },
          { detailGuid: 'd2', action: DetailAction.UpdatePurchasePrice, activityType: DetailAction.UpdatePurchasePrice },
        ],
        confirmedCreateProductCount: 1,
        confirmedAt: '2026-06-02T09:30:00.000Z',
        newProductProductTypeSelections: [
          { detailGuid: 'd1', productType: 2 },
        ],
      },
      '批量执行应发送确认当刻的 expectedActions 与 confirmedCreateProductCount',
    )
  })
  if (servicePayloadFailure) failures.push(servicePayloadFailure)

  const updateItemNumberServicePayloadFailure = await runTest('batchExecuteActions 应原样发送更新货号确认动作', async () => {
    const originalFetch = globalThis.fetch
    let capturedInit: RequestInit | undefined

    globalThis.fetch = (async (_input: RequestInfo | URL, init?: RequestInit) => {
      capturedInit = init

      return new Response(JSON.stringify({
        success: true,
        data: {
          createdProducts: 0,
          updatedPurchasePrices: 0,
          updatedItemNumbers: 1,
          addedMultiCodes: 0,
          skipped: 0,
          failed: 0,
          errors: [],
        },
      }), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      })
    }) as typeof fetch

    try {
      await batchExecuteActions({
        invoiceGuid: 'invoice-1',
        detailGuids: ['d1'],
        expectedActions: [
          { detailGuid: 'd1', action: DetailAction.UpdateItemNumber, activityType: DetailAction.UpdateItemNumber },
        ],
        confirmedCreateProductCount: 0,
        confirmedAt: '2026-06-02T09:45:00.000Z',
      })
    } finally {
      globalThis.fetch = originalFetch
    }

    assertDeepEqual(
      JSON.parse(String(capturedInit?.body)),
      {
        detailGuids: ['d1'],
        expectedActions: [
          { detailGuid: 'd1', action: DetailAction.UpdateItemNumber, activityType: DetailAction.UpdateItemNumber },
        ],
        confirmedCreateProductCount: 0,
        confirmedAt: '2026-06-02T09:45:00.000Z',
        newProductProductTypeSelections: [],
      },
      '批量执行服务应原样发送更新货号动作',
    )
  })
  if (updateItemNumberServicePayloadFailure) failures.push(updateItemNumberServicePayloadFailure)

  const serviceBusinessFailure = await runTest('batchExecuteActions 遇到 success=false 应保留后端业务消息与 payload', async () => {
    const originalFetch = globalThis.fetch

    globalThis.fetch = (async () => new Response(JSON.stringify({
      success: false,
      message: '批量执行校验失败',
      data: {
        createdProducts: 0,
        updatedPurchasePrices: 0,
        updatedItemNumbers: 0,
        addedMultiCodes: 0,
        skipped: 0,
        failed: 1,
        errors: ['d1: 条码重复'],
      },
    }), {
      status: 200,
      headers: { 'Content-Type': 'application/json' },
    })) as typeof fetch

    try {
      await batchExecuteActions({
        invoiceGuid: 'invoice-1',
        detailGuids: ['d1'],
        expectedActions: [
          { detailGuid: 'd1', action: DetailAction.CreateProduct, activityType: DetailAction.CreateProduct },
        ],
        confirmedCreateProductCount: 1,
        confirmedAt: '2026-06-02T09:30:00.000Z',
      })
      throw new Error('预期 batchExecuteActions 抛出业务失败')
    } catch (error) {
      assert(error instanceof RequestError, '业务失败应抛出 RequestError')
      assertEqual(error.message, '批量执行校验失败', '业务失败应优先保留后端 message')
      assertDeepEqual(
        error.payload,
        {
          success: false,
          message: '批量执行校验失败',
          data: {
            createdProducts: 0,
            updatedPurchasePrices: 0,
            updatedItemNumbers: 0,
            addedMultiCodes: 0,
            skipped: 0,
            failed: 1,
            errors: ['d1: 条码重复'],
          },
        },
        '业务失败应保留完整 payload 供前端继续展示明细',
      )
    } finally {
      globalThis.fetch = originalFetch
    }
  })
  if (serviceBusinessFailure) failures.push(serviceBusinessFailure)

  const errorFeedbackFailure = await runTest('错误反馈应优先展示后端 message 并保留结构化明细', () => {
    const feedback = getBatchExecuteErrorFeedback(
      new RequestError('批量执行校验失败', 200, {
        data: {
          createdProducts: 0,
          updatedPurchasePrices: 0,
          updatedItemNumbers: 0,
          addedMultiCodes: 0,
          skipped: 0,
          failed: 1,
          errors: ['d1: 条码重复'],
        },
      }),
      '批量执行操作失败',
    )

    assertEqual(feedback.message, '批量执行校验失败', '应优先展示后端 message')
    assertDeepEqual(feedback.details, ['d1: 条码重复'], '有结构化 details 时应继续保留明细')
    assertEqual(feedback.failure?.failed, 1, '应保留失败统计供结果弹窗使用')
  })
  if (errorFeedbackFailure) failures.push(errorFeedbackFailure)

  const visibleSelectionFailure = await runTest('筛选变化后应只保留当前可见明细的选中项', () => {
    const result = constrainSelectedRowKeysToVisibleDetails(
      ['d1', 'd2', 'hidden'],
      [
        { detailGUID: 'd1' },
        { detailGUID: 'd2' },
      ],
    )

    assertEqual(result.length, 2, '应移除不可见选中项')
    assertEqual(String(result[0]), 'd1', '应保留第一个可见选中项')
    assertEqual(String(result[1]), 'd2', '应保留第二个可见选中项')
  })
  if (visibleSelectionFailure) failures.push(visibleSelectionFailure)

  const priceDirectionFailure = await runTest('执行操作按进货价涨跌分组，涨价降价可分开执行且保持原顺序', () => {
    const details = [
      { detailGUID: 'up-1', activityType: DetailAction.UpdatePurchasePrice, purchasePrice: 3, lastPurchasePrice: 2.5 },
      { detailGUID: 'item-1', activityType: DetailAction.UpdateItemNumber, purchasePrice: 9, lastPurchasePrice: 1 },
      { detailGUID: 'down-1', activityType: DetailAction.UpdatePurchasePrice, purchasePrice: 2, lastPurchasePrice: 2.5 },
      { detailGUID: 'flat-1', activityType: DetailAction.UpdatePurchasePrice, purchasePrice: 2.5, lastPurchasePrice: 2.5 },
      { detailGUID: 'no-last', activityType: DetailAction.UpdatePurchasePrice, purchasePrice: 2, lastPurchasePrice: 0 },
      { detailGUID: 'up-2', activityType: DetailAction.WaitForOperation, purchasePrice: 5, lastPurchasePrice: 4 },
    ]
    // up-2 在页面里刚改成「更新进货价」还没保存，以 rowActions 为准
    const rowActions = { 'up-2': DetailAction.UpdatePurchasePrice }
    const guids = details.map((item) => item.detailGUID)
    const split = splitPurchasePriceDirectionGuids(guids, details, rowActions)
    assertDeepEqual(split.upGuids, ['up-1', 'up-2'], '涨价只算「更新进货价」且高于上次进货价的行')
    assertDeepEqual(split.downGuids, ['down-1'], '降价只算「更新进货价」且低于上次进货价的行')
    assertDeepEqual(split.otherGuids, ['item-1', 'flat-1', 'no-last'], '改货号、价格未变、没有上次进货价都归其他')

    assertDeepEqual(
      pickPurchasePriceDirectionGuids(guids, split, { includeUp: true, includeDown: true }),
      guids,
      '默认全选时与原行为一致',
    )
    assertDeepEqual(
      pickPurchasePriceDirectionGuids(guids, split, { includeUp: false, includeDown: true }),
      ['item-1', 'down-1', 'flat-1', 'no-last'],
      '只执行降价时排除涨价行并保持顺序',
    )
    assertDeepEqual(
      pickPurchasePriceDirectionGuids(guids, split, { includeUp: true, includeDown: false }),
      ['up-1', 'item-1', 'flat-1', 'no-last', 'up-2'],
      '只执行涨价时排除降价行',
    )
    assertDeepEqual(
      pickPurchasePriceDirectionGuids(['up-1', 'down-1'], splitPurchasePriceDirectionGuids(['up-1', 'down-1'], details, {}), { includeUp: false, includeDown: false }),
      [],
      '两类都取消且没有其他行时为空，页面据此提示并保持确认框打开',
    )
  })
  if (priceDirectionFailure) failures.push(priceDirectionFailure)

  const i18nFailure = await runTest('中英文 locale 应补齐批量执行确认框文案 key', () => {
    const zh = JSON.parse(readFileSync(resolve(process.cwd(), 'src/i18n/locales/zh.json'), 'utf8'))
    const en = JSON.parse(readFileSync(resolve(process.cwd(), 'src/i18n/locales/en.json'), 'utf8'))
    const requiredKeys = [
      'batchExecuteConfirmTitle',
      'batchExecuteConfirmContent',
      'batchExecuteCreateProductNotice',
      'batchExecuteConfirmOk',
    ]

    requiredKeys.forEach((key) => {
      assert(
        typeof zh?.posAdmin?.invoiceDetail?.[key] === 'string' && zh.posAdmin.invoiceDetail[key].length > 0,
        `中文 locale 缺少 ${key}`,
      )
      assert(
        typeof en?.posAdmin?.invoiceDetail?.[key] === 'string' && en.posAdmin.invoiceDetail[key].length > 0,
        `英文 locale 缺少 ${key}`,
      )
    })
  })
  if (i18nFailure) failures.push(i18nFailure)

  if (failures.length) {
    throw new Error(failures.join('\n'))
  }
}

main().catch((error) => {
  console.error(error)
  process.exitCode = 1
})
