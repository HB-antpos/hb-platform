import {
  alignDomesticProductCode,
  previewAlignDomesticProductCode,
  assignContainerDetailCategoryByScope,
  batchUpdateDetails,
  deleteContainerDetailsByScope,
  getContainerDetailEditingPresence,
  heartbeatContainerDetailEditingPresence,
  leaveContainerDetailEditingPresence,
  previewContainerDetailAction,
  createContainer,
  getComingSoonContainerProducts,
  getComingSoonContainerSummaries,
  getContainerDomesticSetCodes,
  queryContainerProducts,
  recalculateContainerCostsByScope,
  setContainerDetailStatusByScope,
  syncContainersFromHq,
  translateHqProductNamesByContainerNumber,
  updateContainerDomesticSetCodePrices,
  updateContainer,
} from './containerService'
import type { UpdateContainerDetailRequest, UpdateContainerRequest } from '../types/container'

function assertEqual<T>(actual: T, expected: T, label: string) {
  if (actual !== expected) {
    throw new Error(`${label}. Expected: ${String(expected)}, received: ${String(actual)}`)
  }
}

function assertDeepEqual(actual: unknown, expected: unknown, label: string) {
  const actualJson = JSON.stringify(actual)
  const expectedJson = JSON.stringify(expected)

  if (actualJson !== expectedJson) {
    throw new Error(`${label}. Expected: ${expectedJson}, received: ${actualJson}`)
  }
}

async function assertRejects(execute: () => Promise<unknown>, expectedMessage: string, label: string) {
  try {
    await execute()
  } catch (error) {
    const actualMessage = error instanceof Error ? error.message : String(error)
    assertEqual(actualMessage, expectedMessage, label)
    return
  }

  throw new Error(`${label}. Expected promise to reject`)
}

const originalFetch = globalThis.fetch
let capturedUrl = ''
let capturedInit: RequestInit | undefined
const capturedUrls: string[] = []

globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
  capturedUrl = String(input)
  capturedInit = init

  return new Response(JSON.stringify({
    success: true,
    data: {
      TotalCandidates: 1,
      TotalTranslated: 1,
      TotalSkipped: 0,
      TotalFailed: 0,
      Samples: { 自动脱毛梳: 'Pet Grooming Comb' },
    },
  }), {
    status: 200,
    headers: { 'Content-Type': 'application/json' },
  })
}) as typeof fetch

try {
  globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
    capturedUrl = String(input)
    capturedInit = init

    return new Response(JSON.stringify({ success: true }), {
      status: 200,
      headers: { 'Content-Type': 'application/json' },
    })
  }) as typeof fetch

  const updatePayload: UpdateContainerRequest = {
    实际到货日期: '2026-06-16',
    汇率: 4.5,
    运费: 1280,
    备注: '状态切换测试',
    状态: 1,
  }
  await updateContainer('OOCU5568972', updatePayload)

  assertEqual(
    capturedUrl,
    '/api/react/v1/containers/OOCU5568972',
    'updateContainer should keep the React container update URL unchanged',
  )
  assertEqual(capturedInit?.method, 'PUT', 'updateContainer should use PUT')
  assertDeepEqual(
    JSON.parse(String(capturedInit?.body)),
    updatePayload,
    'updateContainer should send container status with the header update payload',
  )

  globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
    capturedUrl = String(input)
    capturedInit = init

    return new Response(JSON.stringify({
      success: false,
      message: '货柜编号 CSNU6209359 在装柜日期 2026-05-29 已存在',
    }), {
      status: 200,
      headers: { 'Content-Type': 'application/json' },
    })
  }) as typeof fetch

  await assertRejects(
    () => createContainer({ 货柜编号: 'CSNU6209359', 装柜日期: '2026-05-29' }),
    '货柜编号 CSNU6209359 在装柜日期 2026-05-29 已存在',
    'createContainer 应透传后端货柜编号和装柜日期组合重复提示',
  )
  assertEqual(capturedUrl, '/api/react/v1/containers', 'createContainer 应调用 React 货柜创建接口')
  assertEqual(capturedInit?.method, 'POST', 'createContainer 应使用 POST')
  assertDeepEqual(
    JSON.parse(String(capturedInit?.body)),
    { 货柜编号: 'CSNU6209359', 装柜日期: '2026-05-29' },
    'createContainer 应继续发送货柜编号和装柜日期给后端组合判重',
  )

  globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
    capturedUrl = String(input)
    capturedInit = init

    return new Response(JSON.stringify({
      success: true,
      data: {
        totalUpdated: 1,
        totalRequested: 1,
        autoRepairedStoreGroupCount: 2,
        autoRepairedRelationCount: 3,
        validationErrors: [{
          hguid: 'D-CLEAR-EN',
          field: '英文名称',
          code: 'CONTAINS_CHINESE',
          message: '英文名称不能包含中文',
        }],
      },
    }), {
      status: 200,
      headers: { 'Content-Type': 'application/json' },
    })
  }) as typeof fetch

  const detailUpdates: UpdateContainerDetailRequest[] = [
    {
      hguid: 'D-CLEAR-EN',
      ClearEnglishName: true,
      中包数: 12,
      备注: '连续编辑后的备注',
      ProductCategoryGUID: 'CAT-TARGET',
      SkipRelatedProductSync: true,
      expectedServerFieldTokens: {
        '英文名称': 'baseline-english',
        '中包数': 'baseline-middle-pack',
        '备注': 'baseline-remark',
      },
      overrideAcknowledgements: {
        '备注': 'current-remark',
      },
    },
  ]
  const detailUpdateResult = await batchUpdateDetails('container-123', detailUpdates)

  assertEqual(
    capturedUrl,
    '/api/react/v1/containers/container-123/batch-update-details',
    'batchUpdateDetails 应将货柜 GUID 放入受限更新路由',
  )
  assertEqual(capturedInit?.method, 'POST', 'batchUpdateDetails should use POST')
  assertDeepEqual(
    JSON.parse(String(capturedInit?.body)),
    [{ HGUID: 'D-CLEAR-EN', ClearEnglishName: true, ProductCategoryGUID: 'CAT-TARGET', 中包数: 12, 备注: '连续编辑后的备注', SkipRelatedProductSync: true, ExpectedServerFieldTokens: { '英文名称': 'baseline-english', '中包数': 'baseline-middle-pack', '备注': 'baseline-remark' }, OverrideAcknowledgements: { '备注': 'current-remark' } }],
    'batchUpdateDetails 应发送字段令牌和备注等显式字段，货柜范围由路径约束',
  )
  assertDeepEqual(
    detailUpdateResult.validationErrors,
    [{
      hguid: 'D-CLEAR-EN',
      field: '英文名称',
      code: 'CONTAINS_CHINESE',
      message: '英文名称不能包含中文',
    }],
    'batchUpdateDetails should preserve structured validation errors for field-level retry',
  )
  assertDeepEqual(
    {
      autoRepairedStoreGroupCount: detailUpdateResult.autoRepairedStoreGroupCount,
      autoRepairedRelationCount: detailUpdateResult.autoRepairedRelationCount,
    },
    { autoRepairedStoreGroupCount: 2, autoRepairedRelationCount: 3 },
    'batchUpdateDetails should preserve optional set and multi-code auto-repair statistics',
  )

  globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
    capturedUrl = String(input)
    capturedInit = init
    return new Response(JSON.stringify({
      success: true,
      data: {
        totalUpdated: 0,
        totalRequested: 1,
        validationErrors: [],
        conflicts: [{
          hguid: 'D-CONFLICT',
          field: '进口价格',
          serverValue: 4.8,
          submittedValue: 4.2,
          currentServerFieldToken: 'current-import',
          code: 'CONCURRENT_FIELD_UPDATE',
        }],
      },
    }), { status: 200, headers: { 'Content-Type': 'application/json' } })
  }) as typeof fetch
  const conflictResult = await batchUpdateDetails('container-123', [{
    hguid: 'D-CONFLICT',
    进口价格: 4.2,
    expectedServerFieldTokens: { '进口价格': 'baseline-import' },
  }])
  assertDeepEqual(
    conflictResult.conflicts,
    [{
      hguid: 'D-CONFLICT',
      field: '进口价格',
      serverValue: 4.8,
      submittedValue: 4.2,
      currentServerFieldToken: 'current-import',
      code: 'CONCURRENT_FIELD_UPDATE',
    }],
    'batchUpdateDetails 应保留字段级并发冲突，不得当作网络失败或清除草稿',
  )

  globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
    capturedUrl = String(input)
    capturedInit = init
    return new Response(JSON.stringify({
      success: true,
      data: {
        previewToken: 'preview-token',
        affectedCount: 2,
        fieldSummary: ['进口价格', '调整浮率'],
        expiresAt: '2026-09-03T00:05:00Z',
      },
    }), { status: 200, headers: { 'Content-Type': 'application/json' } })
  }) as typeof fetch
  const preview = await previewContainerDetailAction('container-123', {
    operation: 'apply-prices',
    scope: { selectedHguids: ['D-1', 'D-2'] },
    parameters: { importPrice: 4.2 },
  })
  assertEqual(capturedUrl, '/api/react/v1/containers/container-123/actions/preview', '批量预览应使用货柜范围路由')
  assertDeepEqual(
    JSON.parse(String(capturedInit?.body)),
    { operation: 'apply-prices', scope: { selectedHguids: ['D-1', 'D-2'] }, parameters: { importPrice: 4.2 } },
    '批量预览必须发送操作、范围与参数，供服务端签名',
  )
  assertDeepEqual(preview, {
    previewToken: 'preview-token',
    affectedCount: 2,
    fieldSummary: ['进口价格', '调整浮率'],
    expiresAt: '2026-09-03T00:05:00Z',
  }, '批量预览应保留服务端范围摘要和短期令牌')

  globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
    capturedUrl = String(input)
    capturedInit = init
    return new Response(JSON.stringify({ success: true, data: { totalDeleted: 2, totalRequested: 2 } }), {
      status: 200,
      headers: { 'Content-Type': 'application/json' },
    })
  }) as typeof fetch
  const deleteResult = await deleteContainerDetailsByScope('container-123', { selectedHguids: ['D-1', 'D-2'] }, 'preview-delete')
  assertEqual(capturedUrl, '/api/react/v1/containers/container-123/actions/delete-details', '批量删除应使用货柜范围动作路由')
  assertDeepEqual(
    JSON.parse(String(capturedInit?.body)),
    { selectedHguids: ['D-1', 'D-2'], previewToken: 'preview-delete' },
    '批量删除必须携带必填 previewToken，并保留既有根级 scope 形状',
  )
  assertDeepEqual(deleteResult, { totalDeleted: 2, totalRequested: 2 }, '批量删除应只返回经过整数验证的统计结果')

  globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
    capturedUrl = String(input)
    capturedInit = init
    return new Response(JSON.stringify({ success: true, data: { viewers: [{ userGuid: 'U-2', userName: 'Alice', lastActiveAt: '2026-09-03T00:00:00Z' }], editors: [] } }), {
      status: 200,
      headers: { 'Content-Type': 'application/json' },
    })
  }) as typeof fetch
  const presence = await getContainerDetailEditingPresence('container-123')
  assertEqual(capturedUrl, '/api/react/v1/containers/container-123/editing-presence', '活动用户查询应绑定货柜范围')
  assertEqual(presence.viewers[0]?.userName, 'Alice', '活动用户查询应规范化查看者')
  await heartbeatContainerDetailEditingPresence('container-123', { clientSessionId: 'session-1', state: 'editing' })
  assertEqual(capturedUrl, '/api/react/v1/containers/container-123/editing-presence/heartbeat', '心跳应使用货柜范围路由')
  assertDeepEqual(JSON.parse(String(capturedInit?.body)), { clientSessionId: 'session-1', state: 'editing' }, '心跳应上报客户端会话和编辑状态')
  await leaveContainerDetailEditingPresence('container-123', { clientSessionId: 'session-1' })
  assertEqual(capturedUrl, '/api/react/v1/containers/container-123/editing-presence/leave', '离开应使用最佳努力货柜范围路由')

  globalThis.fetch = (async () => new Response(JSON.stringify({
    data: {
      totalUpdated: 1,
      totalRequested: 1,
      validationErrors: [],
    },
  }), {
    status: 200,
    headers: { 'Content-Type': 'application/json' },
  })) as typeof fetch
  await assertRejects(
    () => batchUpdateDetails('container-123', [{ hguid: 'D-CLEAR-EN', 进口价格: 4.2 }]),
    '批量更新货柜明细失败',
    '2xx 缺少 success 标记不得按保存成功处理或清除草稿',
  )

  globalThis.fetch = (async () => new Response(JSON.stringify({
    success: true,
    data: {
      totalUpdated: 1,
      totalRequested: 1,
      validationErrors: [{ hguid: 'OTHER-DETAIL', field: '进口价格', code: 'INVALID', message: '错误货柜明细' }],
    },
  }), {
    status: 200,
    headers: { 'Content-Type': 'application/json' },
  })) as typeof fetch
  await assertRejects(
    () => batchUpdateDetails('container-123', [{ hguid: 'D-CLEAR-EN', 进口价格: 4.2 }]),
    '批量更新货柜明细返回校验错误不完整，未保存草稿已保留',
    '2xx 包含不属于请求的 HGUID 时不得清除草稿',
  )

  globalThis.fetch = (async () => new Response(JSON.stringify({
    success: true,
    data: {
      totalUpdated: 0,
      totalRequested: 1,
      validationErrors: [{ hguid: 'D-CLEAR-EN', field: '贴牌价格', code: 'INVALID', message: '未提交字段' }],
    },
  }), {
    status: 200,
    headers: { 'Content-Type': 'application/json' },
  })) as typeof fetch
  await assertRejects(
    () => batchUpdateDetails('container-123', [{ hguid: 'D-CLEAR-EN', ClearEnglishName: true }]),
    '批量更新货柜明细返回校验错误不完整，未保存草稿已保留',
    '2xx 返回未提交字段的错误不得部分清除草稿',
  )

  globalThis.fetch = (async () => new Response(JSON.stringify({
    success: true,
    data: {
      totalUpdated: 0,
      totalRequested: 1,
      validationErrors: [
        { hguid: 'D-CLEAR-EN', field: '英文名称', code: 'A', message: '冲突一' },
        { hguid: 'D-CLEAR-EN', field: '英文名称', code: 'B', message: '冲突二' },
      ],
    },
  }), {
    status: 200,
    headers: { 'Content-Type': 'application/json' },
  })) as typeof fetch
  await assertRejects(
    () => batchUpdateDetails('container-123', [{ hguid: 'D-CLEAR-EN', ClearEnglishName: true }]),
    '批量更新货柜明细返回校验错误冲突，未保存草稿已保留',
    '同一字段出现冲突重复错误时必须 fail-closed',
  )

  globalThis.fetch = (async () => new Response(JSON.stringify({
    success: true,
    data: { totalUpdated: 0, totalRequested: 1, validationErrors: [] },
  }), { status: 200, headers: { 'Content-Type': 'application/json' } })) as typeof fetch
  await assertRejects(
    () => batchUpdateDetails('container-123', [{ hguid: 'D-EMPTY-SUCCESS', 进口价格: 4.2 }]),
    '批量更新货柜明细返回成功计数不一致，未保存草稿已保留',
    '2xx 空错误且 totalUpdated 为零不得清除实际提交字段的草稿',
  )

  globalThis.fetch = (async () => new Response(JSON.stringify({
    success: true,
    data: {
      totalUpdated: 1,
      totalRequested: 1,
      validationErrors: [{ hguid: 'D-PARTIAL', field: '英文名称', code: 'INVALID', message: '英文名失败' }],
    },
  }), { status: 200, headers: { 'Content-Type': 'application/json' } })) as typeof fetch
  await batchUpdateDetails('container-123', [{ hguid: 'D-PARTIAL', 进口价格: 4.2, ClearEnglishName: true }])

  await assertRejects(
    () => batchUpdateDetails('container-123', [{ hguid: 'D-CLIENT-ONLY', SkipRelatedProductSync: true }]),
    '批量更新货柜明细没有可保存字段，未保存草稿已保留',
    '仅客户端控制字段或 undefined 不得计入服务端成功行',
  )
  await assertRejects(
    () => batchUpdateDetails('container-123', [
      { hguid: 'D-DUPLICATE', 进口价格: 4.2 },
      { hguid: 'D-DUPLICATE', 贴牌价格: 8.8 },
    ]),
    '批量更新货柜明细包含重复 HGUID，未保存草稿已保留',
    '重复出站 HGUID 必须 fail-closed，不能让成功计数含糊',
  )

  globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
    capturedUrl = String(input)
    capturedInit = init

    return new Response(JSON.stringify({
      success: true,
      data: {
        oldProductCode: 'DOM-OLD',
        newProductCode: 'LOCAL-NEW',
        updatedDomesticProducts: 1,
        updatedContainerDetails: 2,
      },
    }), {
      status: 200,
      headers: { 'Content-Type': 'application/json' },
    })
  }) as typeof fetch

  const alignResult = await alignDomesticProductCode({
    detailHguid: 'D-ALIGN',
    expectedDomesticProductCode: 'DOM-OLD',
    targetProductCode: 'LOCAL-NEW',
    supplierCode: '200',
  })

  assertEqual(
    capturedUrl,
    '/api/react/v1/containers/details/align-domestic-product-code',
    'alignDomesticProductCode should call the manual alignment endpoint',
  )
  assertDeepEqual(
    JSON.parse(String(capturedInit?.body)),
    {
      DetailHguid: 'D-ALIGN',
      ExpectedDomesticProductCode: 'DOM-OLD',
      TargetProductCode: 'LOCAL-NEW',
      SupplierCode: '200',
    },
    'alignDomesticProductCode should send the confirmed old and target product codes',
  )
  assertDeepEqual(
    {
      oldProductCode: alignResult.oldProductCode,
      newProductCode: alignResult.newProductCode,
      updatedDomesticProducts: alignResult.updatedDomesticProducts,
      updatedContainerDetails: alignResult.updatedContainerDetails,
    },
    {
      oldProductCode: 'DOM-OLD',
      newProductCode: 'LOCAL-NEW',
      updatedDomesticProducts: 1,
      updatedContainerDetails: 2,
    },
    'alignDomesticProductCode should normalize response counts',
  )

  globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
    capturedUrl = String(input)
    capturedInit = init
    return new Response(JSON.stringify({
      success: true,
      data: {
        mode: 'Merge',
        oldProductCode: 'DOM-OLD',
        newProductCode: 'LOCAL-NEW',
        affectedContainerDetails: 3,
        affectedContainers: 2,
        fields: [{ field: 'Barcode', label: '条形码', existingValue: null, oldValue: '952', mergedValue: '952', filledFromOld: true }],
      },
    }), { status: 200, headers: { 'Content-Type': 'application/json' } })
  }) as typeof fetch

  const alignPreview = await previewAlignDomesticProductCode({
    detailHguid: 'D-ALIGN',
    expectedDomesticProductCode: 'DOM-OLD',
    targetProductCode: 'LOCAL-NEW',
    supplierCode: '200',
  })
  assertEqual(
    capturedUrl,
    '/api/react/v1/containers/details/align-domestic-product-code/preview',
    'previewAlignDomesticProductCode should call the preview endpoint',
  )
  assertDeepEqual(
    {
      mode: alignPreview.mode,
      affectedContainerDetails: alignPreview.affectedContainerDetails,
      affectedContainers: alignPreview.affectedContainers,
      firstField: alignPreview.fields[0],
    },
    {
      mode: 'Merge',
      affectedContainerDetails: 3,
      affectedContainers: 2,
      firstField: { field: 'Barcode', label: '条形码', existingValue: null, oldValue: '952', mergedValue: '952', filledFromOld: true },
    },
    'previewAlignDomesticProductCode should normalize merge preview fields',
  )

  await alignDomesticProductCode({
    detailHguid: 'D-ALIGN',
    expectedDomesticProductCode: 'DOM-OLD',
    targetProductCode: 'LOCAL-NEW',
    supplierCode: '200',
    mergeIntoExistingDomesticProduct: true,
  })
  assertEqual(
    JSON.parse(String(capturedInit?.body)).MergeIntoExistingDomesticProduct,
    true,
    'alignDomesticProductCode should send the merge flag only when merging',
  )

  const abortController = new AbortController()
  globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
    capturedUrl = String(input)
    capturedInit = init

    return new Response(JSON.stringify({
      success: true,
      data: {
        items: [{ id: 101, hguid: 'remote-101', 商品名称: '远程明细' }],
        itemsTotal: 12,
        pageNumber: 2,
        pageSize: 20,
        hasMore: true,
        totalComputed: false,
        statsComputed: false,
        tagStats: {
          all: 12,
          new: 3,
          existing: 9,
          noOemPrice: 1,
          abnormalImport: 2,
          active: 8,
          inactive: 4,
          normal: 9,
          set: 1,
          multi: 1,
          setChild: 1,
          productCodeMatched: 7,
          supplierItemMatched: 4,
          unmatched: 1,
        },
      },
    }), {
      status: 200,
      headers: { 'Content-Type': 'application/json' },
    })
  }) as typeof fetch

  const queryResult = await queryContainerProducts('GUID/需要编码', {
    pageNumber: 2,
    pageSize: 20,
    itemNumber: 'HB308',
    selectedTags: ['new', 'normal'],
    sortBy: 'itemNumber',
    sortOrder: 'ascend',
    includeTotal: false,
    includeStats: false,
    includeItems: false,
  }, abortController.signal)

  assertEqual(
    capturedUrl,
    '/api/react/v1/containers/GUID%2F%E9%9C%80%E8%A6%81%E7%BC%96%E7%A0%81/products/query',
    'queryContainerProducts 应调用按货柜 GUID 编码后的远程查询接口',
  )
  assertEqual(capturedInit?.method, 'POST', 'queryContainerProducts 应使用 POST')
  assertEqual(capturedInit?.signal, abortController.signal, 'queryContainerProducts 应透传 AbortSignal')
  assertDeepEqual(
    JSON.parse(String(capturedInit?.body)),
    {
      containerGuid: 'GUID/需要编码',
      pageNumber: 2,
      pageSize: 20,
      itemNumber: 'HB308',
      selectedTags: ['new', 'normal'],
      sortBy: 'itemNumber',
      sortOrder: 'ascend',
      includeTotal: false,
      includeStats: false,
      includeItems: false,
    },
    'queryContainerProducts 应发送远程查询 body 且保留 containerGuid',
  )
  assertDeepEqual(
    queryResult,
    {
      items: [{ id: 101, hguid: 'remote-101', 商品名称: '远程明细' }],
      itemsTotal: 12,
      pageNumber: 2,
      pageSize: 20,
      hasMore: true,
      totalComputed: false,
      statsComputed: false,
      tagStats: {
        all: 12,
        new: 3,
        existing: 9,
        noOemPrice: 1,
        abnormalImport: 2,
        active: 8,
        inactive: 4,
        normal: 9,
        set: 1,
        multi: 1,
        setChild: 1,
        productCodeMatched: 7,
        supplierItemMatched: 4,
        unmatched: 1,
      },
    },
    'queryContainerProducts 应返回 data 内的分页明细结果',
  )

  globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
    capturedUrl = String(input)
    capturedInit = init

    return new Response(JSON.stringify({
      success: true,
      data: { totalUpdated: 87, totalRequested: 87 },
    }), {
      status: 200,
      headers: { 'Content-Type': 'application/json' },
    })
  }) as typeof fetch

  const recalculateResult = await recalculateContainerCostsByScope('WHOLE-CONTAINER-GUID', {
    query: {
      containerGuid: 'WHOLE-CONTAINER-GUID',
      pageNumber: 1,
      pageSize: 50,
      selectedTags: [],
    },
  }, 'preview-recalculate')

  assertEqual(
    capturedUrl,
    '/api/react/v1/containers/WHOLE-CONTAINER-GUID/actions/recalculate-costs',
    'recalculateContainerCostsByScope 应调用货柜成本重算接口',
  )
  assertEqual(capturedInit?.method, 'POST', 'recalculateContainerCostsByScope 应使用 POST')
  assertDeepEqual(
    JSON.parse(String(capturedInit?.body)),
    {
      query: {
        containerGuid: 'WHOLE-CONTAINER-GUID',
        pageNumber: 1,
        pageSize: 50,
        selectedTags: [],
      },
      previewToken: 'preview-recalculate',
    },
    'recalculateContainerCostsByScope 应原样发送整柜 query scope',
  )
  assertDeepEqual(
    recalculateResult,
    { totalUpdated: 87, totalRequested: 87 },
    'recalculateContainerCostsByScope 应返回后端更新统计',
  )

  await setContainerDetailStatusByScope('WHOLE-CONTAINER-GUID', { selectedHguids: ['D-1'] }, false, 'preview-status')
  assertEqual(capturedUrl, '/api/react/v1/containers/WHOLE-CONTAINER-GUID/actions/set-status', '上下架必须使用货柜范围并发保护动作')
  assertDeepEqual(
    JSON.parse(String(capturedInit?.body)),
    { selectedHguids: ['D-1'], isActive: false, previewToken: 'preview-status' },
    '上下架执行必须保留 root-level scope 并携带必填 previewToken',
  )
  await assignContainerDetailCategoryByScope('WHOLE-CONTAINER-GUID', { selectedHguids: ['D-1'] }, 'CATEGORY-1', 'preview-category')
  assertEqual(capturedUrl, '/api/react/v1/containers/WHOLE-CONTAINER-GUID/actions/assign-category', '分类必须使用货柜范围并发保护动作')
  assertDeepEqual(
    JSON.parse(String(capturedInit?.body)),
    { selectedHguids: ['D-1'], categoryGuid: 'CATEGORY-1', previewToken: 'preview-category' },
    '分类执行必须保留 root-level scope 并携带必填 previewToken',
  )

  globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
    capturedUrl = String(input)
    capturedInit = init

    return new Response(JSON.stringify({
      success: true,
      data: {
        TotalCandidates: 1,
        TotalTranslated: 1,
        TotalSkipped: 0,
        TotalFailed: 0,
        Samples: { 自动脱毛梳: 'Pet Grooming Comb' },
      },
    }), {
      status: 200,
      headers: { 'Content-Type': 'application/json' },
    })
  }) as typeof fetch

  await translateHqProductNamesByContainerNumber('CSNU6601647')

  assertEqual(
    capturedUrl,
    '/api/react/v1/hq-products/translate-names/by-container-number',
    'HQ container translation should call the body-based endpoint',
  )
  assertEqual(capturedInit?.method, 'POST', 'HQ container translation should use POST')
  assertDeepEqual(
    JSON.parse(String(capturedInit?.body)),
    { ContainerNumbers: ['CSNU6601647'], OverwriteExisting: false },
    'HQ container translation should send container numbers in the request body',
  )

  globalThis.fetch = (async () => new Response(JSON.stringify({
    success: true,
    data: {
      totalCandidates: 9,
      totalTranslated: 7,
      totalSkipped: 1,
      totalFailed: 1,
      samples: { 大草莓: 'Large Strawberry' },
    },
  }), {
    status: 200,
    headers: { 'Content-Type': 'application/json' },
  })) as typeof fetch

  const camelCaseTranslation = await translateHqProductNamesByContainerNumber('CSGU7149907')
  assertDeepEqual(
    camelCaseTranslation,
    {
      TotalCandidates: 9,
      TotalTranslated: 7,
      TotalSkipped: 1,
      TotalFailed: 1,
      Samples: { 大草莓: 'Large Strawberry' },
    },
    'HQ container translation should normalize camelCase backend statistics',
  )

  globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
    capturedUrl = String(input)
    capturedInit = init

    return new Response(JSON.stringify({
      success: false,
      message: 'HQ 同步失败：业务错误',
      data: {
        isSuccess: false,
        message: '不应返回成功结果',
      },
    }), {
      status: 200,
      headers: { 'Content-Type': 'application/json' },
    })
  }) as typeof fetch

  await assertRejects(
    () => syncContainersFromHq('2026-05-01'),
    'HQ 同步失败：业务错误',
    'syncContainersFromHq should throw backend message when success is false',
  )
  assertEqual(capturedUrl, '/api/react/v1/containers/sync-from-hq', 'syncContainersFromHq should keep the sync URL unchanged')
  assertEqual(capturedInit?.method, 'POST', 'syncContainersFromHq should use POST')
  assertDeepEqual(
    JSON.parse(String(capturedInit?.body)),
    { startDate: '2026-05-01' },
    'syncContainersFromHq should keep the request body unchanged',
  )

  globalThis.fetch = (async () => new Response(JSON.stringify({
    success: false,
    message: 'HQ 同步失败：HTTP 错误',
  }), {
    status: 500,
    headers: { 'Content-Type': 'application/json' },
  })) as typeof fetch

  await assertRejects(
    () => syncContainersFromHq(),
    'HQ 同步失败：HTTP 错误',
    'syncContainersFromHq should throw backend message when HTTP status is not 2xx',
  )

  capturedUrls.length = 0
  globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
    capturedUrl = String(input)
    capturedInit = init
    capturedUrls.push(capturedUrl)

    return new Response(JSON.stringify({
      success: true,
      data: [
        { id: 1, hguid: 'ARRIVED-GUID', 货柜编号: 'ARRIVED-1', 实际到货日期: '2026-06-01' },
        { id: 2, hguid: 'UPCOMING-GUID', 货柜编号: 'UPCOMING-1', 预计到岸日期: '2026-06-16' },
      ],
    }), {
      status: 200,
      headers: { 'Content-Type': 'application/json' },
    })
  }) as typeof fetch

  const summaries = await getComingSoonContainerSummaries()

  assertEqual(summaries.length, 2, 'Coming Soon 摘要应返回货柜头列表')
  assertEqual(
    capturedUrls.filter((url) => url.includes('/products')).length,
    0,
    'Coming Soon 摘要首屏不应提前请求货柜商品明细',
  )
  assertEqual(
    capturedUrl,
    '/api/react/v1/containers/coming-soon/summaries',
    'Coming Soon 摘要应调用后端共享缓存专用接口',
  )
  assertEqual(capturedInit?.method, 'GET', 'Coming Soon 摘要应使用 GET 缓存接口')

  globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
    capturedUrl = String(input)
    capturedInit = init

    return new Response(JSON.stringify({
      success: true,
      data: [
        {
          id: 1,
          hguid: 'DETAIL-1',
          商品编码: 'P-3',
          商品信息: { 货号: 'HB10', 条形码: '9300000000100', 商品名称: 'Item 10', 零售价格: 1.99 },
          装柜数量: 10,
          // 本柜建档后 是否新商品 变 false，但仍是本柜新品。
          是否新商品: false,
          isContainerNewProduct: true,
        },
        {
          id: 2,
          hguid: 'DETAIL-2',
          商品编码: 'P-1',
          商品信息: { 货号: 'HB2', 商品名称: 'Item 2' },
          装柜数量: 20,
          是否新商品: true,
        },
        {
          id: 3,
          hguid: 'DETAIL-3',
          商品编码: 'P-2',
          商品信息: { 商品名称: 'No Item Number' },
          装柜数量: 30,
          是否新商品: false,
        },
      ],
    }), {
      status: 200,
      headers: { 'Content-Type': 'application/json' },
    })
  }) as typeof fetch

  const products = await getComingSoonContainerProducts('CONTAINER-GUID')

  assertEqual(
    capturedUrl,
    '/api/react/v1/containers/coming-soon/CONTAINER-GUID/products',
    'Coming Soon 单货柜商品应调用后端共享缓存专用接口',
  )
  assertEqual(capturedInit?.method, 'GET', 'Coming Soon 单货柜商品应使用 GET')
  assertDeepEqual(
    products.map((item) => item.itemNumber ?? ''),
    ['HB2', 'HB10', ''],
    'Coming Soon 单货柜商品应按货号自然排序，空货号排最后',
  )
  assertEqual(
    products.find((item) => item.itemNumber === 'HB10')?.barcode,
    '9300000000100',
    'Coming Soon 单货柜商品应映射商品条形码用于生成条码图',
  )
  assertEqual(
    products.find((item) => item.itemNumber === 'HB10')?.retailPrice,
    1.99,
    'Coming Soon 单货柜商品应映射商品建议零售价',
  )
  assertEqual(
    products.find((item) => item.itemNumber === 'HB10')?.isNewProduct,
    true,
    'Coming Soon 应按本柜新品标记新品，本柜建档后仍显示为新商品',
  )
  assertEqual(
    products.find((item) => item.hguid === 'DETAIL-3')?.isNewProduct,
    false,
    '后端未返回本柜新品字段时应回退到是否新商品',
  )

  const setCodeAbortController = new AbortController()
  globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
    capturedUrl = String(input)
    capturedInit = init

    return new Response(JSON.stringify({
      success: true,
      data: [
        {
          productCode: 'P/套装',
          setProductCode: 'SET-1',
          setItemNumber: 'HB137-480-01',
          barcode: '9525811370252',
          retailPrice: 11.47,
          purchasePrice: 3.04,
        },
      ],
    }), {
      status: 200,
      headers: { 'Content-Type': 'application/json' },
    })
  }) as typeof fetch

  const setCodes = await getContainerDomesticSetCodes('P/套装', setCodeAbortController.signal)

  assertEqual(
    capturedUrl,
    '/api/react/v1/containers/products/P%2F%E5%A5%97%E8%A3%85/domestic-set-codes',
    'getContainerDomesticSetCodes 应按商品编码编码后请求国内套装明细',
  )
  assertEqual(capturedInit?.method, 'GET', 'getContainerDomesticSetCodes 应使用 GET')
  assertEqual(capturedInit?.signal, setCodeAbortController.signal, 'getContainerDomesticSetCodes 应透传 AbortSignal')
  assertDeepEqual(
    setCodes,
    [{
      productCode: 'P/套装',
      setProductCode: 'SET-1',
      setItemNumber: 'HB137-480-01',
      barcode: '9525811370252',
      retailPrice: 11.47,
      purchasePrice: 3.04,
    }],
    'getContainerDomesticSetCodes 应返回 data 内国内套装明细',
  )

  globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
    capturedUrl = String(input)
    capturedInit = init

    return new Response(JSON.stringify({
      success: true,
      data: { updatedCount: 1 },
    }), {
      status: 200,
      headers: { 'Content-Type': 'application/json' },
    })
  }) as typeof fetch

  const updateSetCodeResult = await updateContainerDomesticSetCodePrices('P/套装', [
    { setProductCode: 'SET-1', retailPrice: 12.34, purchasePrice: 4.56 },
  ])

  assertEqual(
    capturedUrl,
    '/api/react/v1/containers/products/P%2F%E5%A5%97%E8%A3%85/domestic-set-codes/prices',
    'updateContainerDomesticSetCodePrices 应按商品编码编码后请求价格回写接口',
  )
  assertEqual(capturedInit?.method, 'PATCH', 'updateContainerDomesticSetCodePrices 应使用 PATCH')
  assertDeepEqual(
    JSON.parse(String(capturedInit?.body)),
    { items: [{ setProductCode: 'SET-1', retailPrice: 12.34, purchasePrice: 4.56 }] },
    'updateContainerDomesticSetCodePrices 应只发送套装编码和价格字段',
  )
  assertDeepEqual(updateSetCodeResult, { updatedCount: 1 }, 'updateContainerDomesticSetCodePrices 应返回更新数量')

  globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
    capturedUrl = String(input)
    capturedInit = init

    return new Response(JSON.stringify({
      success: false,
      message: '保存失败',
    }), {
      status: 200,
      headers: { 'Content-Type': 'application/json' },
    })
  }) as typeof fetch

  await assertRejects(
    () => updateContainerDomesticSetCodePrices('P-FAIL', [{ setProductCode: 'SET-FAIL', retailPrice: 1, purchasePrice: 2 }]),
    '保存失败',
    'updateContainerDomesticSetCodePrices 应透传后端失败消息',
  )
} finally {
  globalThis.fetch = originalFetch
}
