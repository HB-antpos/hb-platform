import {
  buildAssignContainerItems,
  findInvalidAssignContainerItems,
  stripAssignContainerItemsForRequest,
  summarizeAssignProductsResult,
} from './utils'
import type { ProductImportItem } from './types'
import { readFileSync } from 'node:fs'

function assertDeepEqual(actual: unknown, expected: unknown, label: string) {
  const actualJson = JSON.stringify(actual)
  const expectedJson = JSON.stringify(expected)

  if (actualJson !== expectedJson) {
    throw new Error(`${label}. Expected: ${expectedJson}, received: ${actualJson}`)
  }
}

function createProduct(overrides: Partial<ProductImportItem>): ProductImportItem {
  return {
    id: 'row-1',
    selected: true,
    imageUrl: '',
    customImage: false,
    imageLoadStatus: 'success',
    newProduct: {
      quantity: 1,
      productCode: 'HB001',
      productName: '测试商品',
    },
    status: 'unchanged',
    isDuplicate: false,
    calculated: { totalProducts: 1, totalVolume: 0 },
    ...overrides,
  }
}

const pageSource = readFileSync('src/pages/DomesticPurchase/ProductImport/index.tsx', 'utf8')
const pageStyleSource = readFileSync('src/pages/DomesticPurchase/ProductImport/productImport.css', 'utf8')
const cellSource = readFileSync('src/pages/DomesticPurchase/ProductImport/ImportCell.tsx', 'utf8')
const zhLocaleSource = readFileSync('src/i18n/locales/zh.json', 'utf8')
const enLocaleSource = readFileSync('src/i18n/locales/en.json', 'utf8')

assertDeepEqual(
  [
    pageSource.includes('const loadContainers = useCallback(async () => {'),
    // 原：onDropdownVisibleChange（antd 5.29 已弃用并在开发环境告警）；现用等价的 onOpenChange，行为不变
    pageSource.includes('onOpenChange={(open) => {'),
    pageSource.includes('if (open) void loadContainers()'),
  ],
  [true, true, true],
  '商品导入货柜下拉打开时应自动刷新货柜列表',
)

assertDeepEqual(
  [
    pageSource.includes('const productImportTableRef = useRef<HTMLDivElement | null>(null)'),
    pageSource.includes('const [tableScrollY, setTableScrollY] = useState(500)'),
    pageSource.includes('window.addEventListener(\'resize\', updateTableScrollY)'),
    pageSource.includes('window.removeEventListener(\'resize\', updateTableScrollY)'),
    pageSource.includes('className="product-import-table"'),
    pageSource.includes('const PRODUCT_IMPORT_TABLE_SCROLL_X = 968'),
    // 旧的「检测前 1280 / 检测后 2500」两套宽度已随 24 列并入 13 列而取消，不应再出现
    !pageSource.includes('PRODUCT_IMPORT_BASE_TABLE_SCROLL_X') && !pageSource.includes('PRODUCT_IMPORT_DETECTED_TABLE_SCROLL_X'),
    pageSource.includes('scroll={{ x: PRODUCT_IMPORT_TABLE_SCROLL_X, y: tableScrollY }}'),
    pageSource.includes('\'--product-import-table-body-height\': `${tableScrollY}px`'),
    pageStyleSource.includes('.product-import-table .ant-table-body'),
    pageStyleSource.includes('height: var(--product-import-table-body-height) !important;'),
    pageSource.includes('rowSelection={{ selectedRowKeys: state.selectedIds'),
    pageSource.includes('const handlePaste = useCallback((e: ClipboardEvent) => {'),
    pageSource.includes('const resolveColumnKeyFromTd = useCallback((td: HTMLTableCellElement): string | null => {'),
  ],
  [true, true, true, true, true, true, true, true, true, true, true, true, true, true],
  '商品导入表格应使用视口剩余高度和当前列宽填满页面并保留粘贴和选择交互',
)

// 新增：表格最小宽度必须能在 1280 视口 + 展开侧栏（内容区约 1000px）下不出现横向滚动条
const tableScrollXMatch = /const PRODUCT_IMPORT_TABLE_SCROLL_X = (\d+)/.exec(pageSource)
if (!tableScrollXMatch || Number(tableScrollXMatch[1]) > 1000) {
  throw new Error('商品导入表格最小宽度应不超过 1000px，才能在 1280 视口下不横向滚动')
}

assertDeepEqual(
  [
    pageSource.includes('searchText: [s.supplierCode, s.supplierName, s.shopNumber]'),
    pageSource.includes('String(option?.searchText || option?.label || option?.value || \'\').toLowerCase()'),
    pageSource.includes('label: `${s.supplierCode} - ${s.supplierName}${s.shopNumber ? ` - ${s.shopNumber}` : \'\'}'),
  ],
  [true, true, true],
  '商品导入供应商搜索应覆盖编码、名称、店号和完整展示文本',
)

const deleteAllStart = pageSource.indexOf('const deleteAllRows = useCallback(() => {')
const deleteAllSource = pageSource.slice(deleteAllStart, pageSource.indexOf('const updateProduct', deleteAllStart))
const resetViewStart = pageSource.indexOf('const resetDetectionView = useCallback(() => {')
const resetViewSource = pageSource.slice(resetViewStart, pageSource.indexOf('useEffect(', resetViewStart))

assertDeepEqual(
  [
    deleteAllStart >= 0 && resetViewStart >= 0,
    pageSource.includes('title: t(\'productImport.deleteAllConfirmTitle\''),
    pageSource.includes('content: t(\'productImport.deleteAllConfirmContent\''),
    deleteAllSource.includes('products: []'),
    deleteAllSource.includes('selectedIds: []'),
    // 原：statistics: calculateStatistics([], []) 写在这里；现在统计与步骤都由行数据派生，行清空即归零，只需清检测结果
    deleteAllSource.includes('resetDetectionView()'),
    resetViewSource.includes('setShowStatistics(false)'),
    resetViewSource.includes('setDetectedRowIds(null)'),
    resetViewSource.includes('setDuplicateGroups([])'),
    // 原：disabled={state.products.length === 0}；现在检测 / 入库 / 发送等请求进行中也要禁用，防止并发删除
    pageSource.includes('disabled={state.products.length === 0 || busy}'),
    pageSource.includes('productImport.deleteAll'),
    zhLocaleSource.includes('"deleteAll": "删除全部"'),
    zhLocaleSource.includes('"deleteAllConfirmTitle": "删除所有表格行"'),
    enLocaleSource.includes('"deleteAll": "Delete All"'),
    enLocaleSource.includes('"deleteAllConfirmTitle": "Delete all table rows"'),
  ],
  [true, true, true, true, true, true, true, true, true, true, true, true, true, true, true],
  '商品导入应提供删除全部表格行按钮，二次确认后清空行、选中和检测结果状态'
)

assertDeepEqual(
  [
    pageSource.includes("import { isValidEAN13 } from '../../../utils/barcode'"),
    pageSource.includes('const isNonEan13Barcode = Boolean(barcode && !isValidEAN13(barcode))'),
    // 原：status={isNonEan13Barcode ? 'warning' : undefined} 直接写在页面里；现在输入框在 ImportCell 里，由 warning prop 驱动（错误优先于警告）
    cellSource.includes("status={invalidMessage ? 'error' : warning ? 'warning' : undefined}"),
    pageSource.includes("warning: isNonEan13Barcode ? t('productImport.notEan13Barcode', '不是 EAN13 条码') : undefined"),
    pageSource.includes("t('productImport.notEan13Barcode', '不是 EAN13 条码')"),
  ],
  [true, true, true, true, true],
  '商品导入条码列应标明非 EAN13 条码，但不阻断检测和保存流程',
)

assertDeepEqual(
  buildAssignContainerItems([
    createProduct({
      newProduct: {
        quantity: 17,
        productCode: 'JM-018',
        productName: '1pc Baby Muffin',
        domesticPrice: 11.6,
        oemPrice: 6.99,
        casePackQuantity: 48,
        volume: 0.118,
      },
      matchedProduct: { productCode: 'P-JM-018' },
    }),
  ], '补价格'),
  [
    {
      hbProductNo: 'JM-018',
      productCode: 'P-JM-018',
      quantity: 17,
      packingQuantity: 48,
      unitVolume: 0.118,
      domesticPrice: 11.6,
      oemPrice: 6.99,
      notes: '补价格',
    },
  ],
  '发送货柜本地校验对象应保留导入行国内价格和零售价',
)

assertDeepEqual(
  stripAssignContainerItemsForRequest(buildAssignContainerItems([
    createProduct({
      newProduct: {
        quantity: 17,
        productCode: 'JM-018',
        productName: '1pc Baby Muffin',
        domesticPrice: 11.6,
        oemPrice: 6.99,
        casePackQuantity: 48,
        volume: 0.118,
      },
      matchedProduct: { productCode: 'P-JM-018' },
    }),
  ], '补价格')),
  [
    {
      hbProductNo: 'JM-018',
      productCode: 'P-JM-018',
      quantity: 17,
      packingQuantity: 48,
      unitVolume: 0.118,
      domesticPrice: 11.6,
      oemPrice: 6.99,
      notes: '补价格',
    },
  ],
  '发送 assign-products 前应保留后端 DTO 支持的业务字段',
)

assertDeepEqual(
  buildAssignContainerItems([
    createProduct({
      newProduct: {
        quantity: 2,
        productCode: 'HB002',
        productName: '旧价兜底商品',
      },
      matchedProduct: {
        productCode: 'P-HB002',
        domesticPrice: 3.2,
        oemPrice: 1.8,
        packingQuantity: 24,
        unitVolume: 0.086,
      },
    }),
  ], ''),
  [
    {
      hbProductNo: 'HB002',
      productCode: 'P-HB002',
      quantity: 2,
      packingQuantity: 24,
      unitVolume: 0.086,
      domesticPrice: 3.2,
      oemPrice: 1.8,
      notes: '',
    },
  ],
  '发送货柜业务字段缺失时应使用匹配商品字段兜底',
)

assertDeepEqual(
  stripAssignContainerItemsForRequest(buildAssignContainerItems([
    createProduct({
      newProduct: {
        quantity: 2,
        productCode: 'HB002',
        productName: '旧装箱数兜底商品',
      },
      matchedProduct: {
        productCode: 'P-HB002',
        domesticPrice: 3.2,
        packingQuantity: 24,
        unitVolume: 0.086,
      },
    }),
  ], '旧字段兜底')),
  [
    {
      hbProductNo: 'HB002',
      productCode: 'P-HB002',
      quantity: 2,
      packingQuantity: 24,
      unitVolume: 0.086,
      domesticPrice: 3.2,
      oemPrice: undefined,
      notes: '旧字段兜底',
    },
  ],
  '发送 assign-products 请求体应保留旧商品托底后的装箱数，供后端计算装柜数量',
)

assertDeepEqual(
  buildAssignContainerItems([
    createProduct({
      newProduct: {
        quantity: 5,
        productCode: 'HB004',
        productName: '零装箱数托底商品',
        domesticPrice: 0,
        oemPrice: 0,
        casePackQuantity: 0,
        volume: 0,
      },
      matchedProduct: {
        productCode: 'P-HB004',
        domesticPrice: 9.9,
        oemPrice: 8.8,
        packingQuantity: 36,
        unitVolume: 0.125,
      },
    }),
  ], undefined),
  [
    {
      hbProductNo: 'HB004',
      productCode: 'P-HB004',
      quantity: 5,
      packingQuantity: 36,
      unitVolume: 0.125,
      domesticPrice: 0,
      oemPrice: 0,
      notes: undefined,
    },
  ],
  '发送货柜装箱数和体积为 0 时应托底，价格为 0 时应保留当前值',
)

assertDeepEqual(
  buildAssignContainerItems([
    createProduct({
      newProduct: {
        quantity: 3,
        productCode: 'HB003',
        productName: '无价格商品',
      },
      matchedProduct: { productCode: 'P-HB003' },
    }),
  ], undefined),
  [
    {
      hbProductNo: 'HB003',
      productCode: 'P-HB003',
      quantity: 3,
      packingQuantity: undefined,
      unitVolume: undefined,
      domesticPrice: undefined,
      oemPrice: undefined,
      notes: undefined,
    },
  ],
  '发送货柜不应把缺失价格转成 0',
)

assertDeepEqual(
  findInvalidAssignContainerItems(buildAssignContainerItems([
    createProduct({
      newProduct: {
        quantity: 0,
        productCode: 'HB005',
        productName: '业务字段缺失商品',
        domesticPrice: 0,
        casePackQuantity: 0,
        volume: 0,
      },
      matchedProduct: {
        productCode: 'P-HB005',
      },
    }),
  ])),
  [
    {
      hbProductNo: 'HB005',
      productCode: 'P-HB005',
      fields: ['件数', '国内价格', '装箱数', '体积'],
      reasons: [],
    },
  ],
  '发送货柜应拦截件数、国内价格、装箱数、体积为空或为 0 的商品',
)

assertDeepEqual(
  findInvalidAssignContainerItems(buildAssignContainerItems([
    createProduct({
      newProduct: {
        quantity: 4,
        productCode: 'HB006',
        productName: '旧字段托底通过商品',
      },
      matchedProduct: {
        productCode: 'P-HB006',
        domesticPrice: 6.6,
        packingQuantity: 12,
        unitVolume: 0.08,
      },
    }),
  ])),
  [],
  '发送货柜应允许业务字段通过旧商品托底后发送',
)

assertDeepEqual(
  findInvalidAssignContainerItems(buildAssignContainerItems([
    createProduct({
      newProduct: {
        quantity: 6,
        productCode: 'HB007',
        productName: '未匹配本地编码商品',
        domesticPrice: 6.8,
        casePackQuantity: 20,
        volume: 0.09,
      },
      matchedProduct: undefined,
    }),
  ])),
  [
    {
      hbProductNo: 'HB007',
      productCode: undefined,
      fields: ['本地商品编码'],
      reasons: ['未匹配本地商品编码'],
    },
  ],
  '发送货柜应拦截未匹配本地商品编码的商品',
)

assertDeepEqual(
  summarizeAssignProductsResult(
    {
      success: true,
      data: {
        created: 2,
        updated: 1,
        failed: [],
      },
    },
    buildAssignContainerItems([
      createProduct({
        newProduct: {
          quantity: 2,
          productCode: 'HB008',
          productName: '全成功商品',
          domesticPrice: 9.1,
          casePackQuantity: 18,
          volume: 0.05,
        },
        matchedProduct: {
          productCode: 'P-HB008',
        },
      }),
    ]),
  ),
  {
    status: 'success',
    success: true,
    message: undefined,
    created: 2,
    updated: 1,
    succeeded: 3,
    failedCount: 0,
    failed: [],
  },
  'assign-products 全成功时应归纳为 success',
)

assertDeepEqual(
  summarizeAssignProductsResult(
    {
      success: true,
      data: {
        created: 1,
        updated: 1,
        failed: [
          { productCode: 'P-HB009', error: '国内价格缺失' },
        ],
      },
    },
    buildAssignContainerItems([
      createProduct({
        id: 'row-9',
        newProduct: {
          quantity: 4,
          productCode: 'HB009',
          productName: '部分失败商品',
          domesticPrice: 10.5,
          casePackQuantity: 16,
          volume: 0.07,
        },
        matchedProduct: {
          productCode: 'P-HB009',
        },
      }),
    ]),
  ),
  {
    status: 'partial',
    success: true,
    message: undefined,
    created: 1,
    updated: 1,
    succeeded: 2,
    failedCount: 1,
    failed: [
      {
        hbProductNo: 'HB009',
        productCode: 'P-HB009',
        reason: '国内价格缺失',
      },
    ],
  },
  'assign-products 部分失败时应保留成功统计并带出失败明细',
)

assertDeepEqual(
  summarizeAssignProductsResult(
    {
      success: true,
      data: {
        created: 0,
        updated: 0,
        failed: [
          { productCode: 'P-HB010', error: '商品不存在' },
        ],
      },
    },
    buildAssignContainerItems([
      createProduct({
        id: 'row-10',
        newProduct: {
          quantity: 5,
          productCode: 'HB010',
          productName: '全失败商品',
          domesticPrice: 8.8,
          casePackQuantity: 30,
          volume: 0.06,
        },
        matchedProduct: {
          productCode: 'P-HB010',
        },
      }),
    ]),
  ),
  {
    status: 'failed',
    success: false,
    message: undefined,
    created: 0,
    updated: 0,
    succeeded: 0,
    failedCount: 1,
    failed: [
      {
        hbProductNo: 'HB010',
        productCode: 'P-HB010',
        reason: '商品不存在',
      },
    ],
  },
  'assign-products 全失败时不应被视为成功',
)

assertDeepEqual(
  summarizeAssignProductsResult(
    {
      success: false,
      message: '发送失败',
      data: {
        created: 0,
        updated: 0,
        failed: [],
      },
    },
    [],
  ),
  {
    status: 'apiError',
    success: false,
    message: '发送失败',
    created: 0,
    updated: 0,
    succeeded: 0,
    failedCount: 0,
    failed: [],
  },
  'assign-products 接口级失败时应归纳为 apiError',
)
