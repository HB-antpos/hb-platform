import { ProductType, type DomesticProductItem } from '../../../types/domesticProduct'
import {
  DEFAULT_SORT_FIELD,
  DEFAULT_SORT_ORDER,
  LIST_COLUMN_WIDTHS,
  LIST_TABLE_MIN_WIDTH,
  PRODUCT_COLUMN_MIN_WIDTH,
  SUPPLIER_COLUMN_MIN_WIDTH,
  ROW_CLICK_IGNORE_SELECTOR,
  SORT_FIELD_BY_COLUMN,
  applyFormValuesToItem,
  buildCreatePayload,
  buildLetterTileDataUri,
  buildSupplierOptions,
  buildUpdatePayload,
  countChangedFields,
  describeSupplier,
  emptyProductFormValues,
  filterSupplierOption,
  formatFullTimestamp,
  formatMoney,
  formatPrice,
  formatUpdatedParts,
  formatVolume,
  productThumbColor,
  productThumbLetter,
  productToFormValues,
  resolveExportPageSize,
  resolveTableChange,
  shouldIgnoreRowClick,
  sortOrderForColumn,
  statusFromSegment,
  statusToSegment,
  typeFromSegment,
  typeToSegment,
  type ProductFormValues,
} from './domesticProductsLogic'

function assert(condition: unknown, message: string): asserts condition {
  if (!condition) {
    throw new Error(message)
  }
}

function assertEqual<T>(actual: T, expected: T, message: string) {
  if (actual !== expected) {
    throw new Error(`${message}: expected ${String(expected)}, got ${String(actual)}`)
  }
}

function assertDeepEqual(actual: unknown, expected: unknown, message: string) {
  const actualJson = JSON.stringify(actual)
  const expectedJson = JSON.stringify(expected)
  if (actualJson !== expectedJson) {
    throw new Error(`${message}: expected ${expectedJson}, got ${actualJson}`)
  }
}

// ---------------------------------------------------------------------------
// 排序键映射：只给后端 ApplyAgGridSorts 认识的列开排序
// ---------------------------------------------------------------------------

// 后端 DomesticProductReactService.ApplyAgGridSorts 认识的 colId（其余一律回落成 UpdatedAt desc）。
const backendSortColumns = new Set([
  'supplierCode',
  'supplierName',
  'name',
  'nameEn',
  'itemNumber',
  'barcode',
  'productType',
  'domesticPrice',
  'labelPrice',
  'importPrice',
  'packingQty',
  'volume',
  'middlePackQty',
  'createdAt',
  'updatedAt',
])
for (const [column, field] of Object.entries(SORT_FIELD_BY_COLUMN)) {
  assert(backendSortColumns.has(field), `列 ${column} 的排序键 ${field} 后端不认识，点列头会静默失效`)
}
assert(backendSortColumns.has(DEFAULT_SORT_FIELD), '默认排序键必须是后端认识的列')
assertEqual(DEFAULT_SORT_FIELD, 'updatedAt', '默认按更新时间排序，与列表展示的「更新」列一致')
assertEqual(DEFAULT_SORT_ORDER, 'descend', '默认降序')

assertEqual(sortOrderForColumn('updated', 'updatedAt', 'descend'), 'descend', '当前排序列显示箭头')
assertEqual(sortOrderForColumn('product', 'updatedAt', 'descend'), null, '其它列不显示箭头')
assertEqual(sortOrderForColumn('supplier', 'supplierName', 'ascend'), 'ascend', '供应商列对应 supplierName')
assertEqual(sortOrderForColumn('itemNumber', undefined, undefined), null, '没有排序字段时所有列都不显示箭头')

// ---------------------------------------------------------------------------
// Table.onChange → 查询覆盖项
// ---------------------------------------------------------------------------

const paginateResult = resolveTableChange('paginate', { current: 3, pageSize: 50 }, { field: 'name', order: 'ascend' }, 50)
assertDeepEqual(paginateResult, { page: 3, pageSize: 50 }, '翻页只带页码与条数，不带排序，沿用当前排序')
assert(!('sortField' in paginateResult), '翻页结果里不能出现 sortField 键，否则会把当前排序覆盖成 undefined')

assertDeepEqual(
  resolveTableChange('paginate', { current: 4, pageSize: 100 }, undefined, 50),
  { page: 1, pageSize: 100 },
  '改每页条数回到第一页，不信任 antd 给的 current',
)

assertDeepEqual(
  resolveTableChange('sort', { current: 5, pageSize: 50 }, { field: 'itemNumber', order: 'ascend' }, 50),
  { page: 1, pageSize: 50, sortField: 'itemNumber', sortOrder: 'ascend' },
  '排序一律回第一页（antd 排序时会带着旧页码）',
)
assertDeepEqual(
  resolveTableChange('sort', { current: 2 }, [{ field: 'supplierName', order: 'descend' }], 50),
  { page: 1, pageSize: 50, sortField: 'supplierName', sortOrder: 'descend' },
  '多列 sorter 数组取第一项，缺省 pageSize 沿用当前',
)
assertDeepEqual(
  resolveTableChange('sort', { current: 2, pageSize: 50 }, { field: 'name', order: null }, 50),
  { page: 1, pageSize: 50, sortField: DEFAULT_SORT_FIELD, sortOrder: DEFAULT_SORT_ORDER },
  '排序被取消时回落默认排序，不向后端发无排序请求',
)
assertDeepEqual(
  resolveTableChange('sort', { current: 2, pageSize: 50 }, { field: ['a', 'b'], order: 'ascend' }, 50),
  { page: 1, pageSize: 50, sortField: DEFAULT_SORT_FIELD, sortOrder: DEFAULT_SORT_ORDER },
  '非字符串 field 视为无效排序',
)

// ---------------------------------------------------------------------------
// 分段控件取值互转：「普通」是 0、「停用」是 false，都不能被当成未设置
// ---------------------------------------------------------------------------

assertEqual(typeFromSegment('all'), undefined, '全部不带 productType')
assertEqual(typeFromSegment('normal'), ProductType.NORMAL, '普通对应 0')
assertEqual(typeFromSegment('normal'), 0, '普通对应 0（不得被当成未设置）')
assertEqual(typeFromSegment('set'), ProductType.SET, '套装对应 1')
assertEqual(typeFromSegment('multi'), ProductType.MULTICODE, '多码对应 2')
assertEqual(typeToSegment(undefined), 'all', '未设置对应全部')
assertEqual(typeToSegment(ProductType.NORMAL), 'normal', '0 对应普通而不是全部')
assertEqual(typeToSegment(ProductType.SET), 'set', '1 对应套装')
assertEqual(typeToSegment(ProductType.MULTICODE), 'multi', '2 对应多码')
assertEqual(statusFromSegment('all'), undefined, '全部不带 isActive')
assertEqual(statusFromSegment('active'), true, '启用对应 true')
assertEqual(statusFromSegment('inactive'), false, '停用对应 false，不得被当成未设置')
assertEqual(statusToSegment(undefined), 'all', '未设置对应全部')
assertEqual(statusToSegment(true), 'active', 'true 对应启用')
assertEqual(statusToSegment(false), 'inactive', 'false 对应停用')

// ---------------------------------------------------------------------------
// 列宽：1280 视口可用宽度 = 1280 − 侧栏 248 − 内容区 padding 32 = 1000
// ---------------------------------------------------------------------------

const fixedWidthSum = Object.values(LIST_COLUMN_WIDTHS).reduce((sum, width) => sum + width, 0)
assertEqual(
  LIST_TABLE_MIN_WIDTH,
  fixedWidthSum + SUPPLIER_COLUMN_MIN_WIDTH + PRODUCT_COLUMN_MIN_WIDTH,
  '表格最小宽度 = 固定列合计 + 供应商列最小宽度 + 商品列最小宽度',
)
// 1280 视口可用 1000px；Windows 经典滚动条再占 15px，所以按 985 把关。
assert(LIST_TABLE_MIN_WIDTH <= 985, `表格最小宽度 ${LIST_TABLE_MIN_WIDTH} 超过 1280 视口扣掉滚动条后的 985px，会出现横向滚动`)
// 1280 视口表格可用约 983：固定列 + 供应商列最小宽度之后，剩给商品列的不能少于它的最小宽度。
assertEqual(983 - fixedWidthSum - SUPPLIER_COLUMN_MIN_WIDTH >= PRODUCT_COLUMN_MIN_WIDTH, true, '1280 视口下商品列实际宽度不少于其最小宽度')
// 货号 / 条码要完整显示：等宽字符 + 复制图标 + 单元格左右 padding。
assert(LIST_COLUMN_WIDTHS.itemNumber >= 124, '货号列至少 124px 才能完整显示 10 位货号和复制图标（等宽 77px + 按钮 24 + padding 20）')
assert(LIST_COLUMN_WIDTHS.barcode >= 148, '条码列至少 148px 才能完整显示 13 位条码和复制图标（等宽 101px + 按钮 24 + padding 20）')
// 操作列：按钮组实测 66px + 单元格左右 padding 20px，小于 86 会溢出并出现横向滚动条。
assert(LIST_COLUMN_WIDTHS.action >= 86, '操作列至少 86px 才能容纳「编辑 ⋯」按钮组')
assert(PRODUCT_COLUMN_MIN_WIDTH >= 180, '商品列至少要容纳缩略图 + 约 8 个汉字')

// ---------------------------------------------------------------------------
// 供应商下拉
// ---------------------------------------------------------------------------

const suppliers = [
  { code: 'HB012', name: '义乌市嘉悦日用品商行', shopNumber: 'A12-018' },
  { code: 'HB017', name: '义乌市恒美塑料制品厂' },
]
const supplierOptions = buildSupplierOptions(suppliers)
assertEqual(supplierOptions[0].label, 'HB012 - 义乌市嘉悦日用品商行 - A12-018', '下拉文案：编码 - 名称 - 店铺号')
assertEqual(supplierOptions[1].label, 'HB017 - 义乌市恒美塑料制品厂', '没有店铺号时不带尾巴')
assertEqual(supplierOptions[0].shortLabel, '义乌市嘉悦日用品商行', '选中后输入框只显示名称')
assert(filterSupplierOption('a12', supplierOptions[0]), '可按店铺号搜索（不区分大小写）')
assert(filterSupplierOption(' 恒美 ', supplierOptions[1]), '可按名称搜索，忽略首尾空白')
assert(!filterSupplierOption('zzz', supplierOptions[0]), '不匹配时过滤掉')
assertEqual(describeSupplier(suppliers, 'HB017'), '义乌市恒美塑料制品厂', '已生效标签显示供应商名称')
assertEqual(describeSupplier(suppliers, 'HB999'), 'HB999', '供应商选项未加载时退回编码')

// ---------------------------------------------------------------------------
// 展示格式化
// ---------------------------------------------------------------------------

assertEqual(formatPrice(18.5), '18.50', '价格保留两位小数')
assertEqual(formatPrice(undefined), '--', '缺失价格显示占位')
assertEqual(formatPrice(null), '--', 'null 价格显示占位')
assertEqual(formatPrice(0), '0.00', '0 是有效价格，不能显示成占位')
assertEqual(formatMoney('¥', 18.5), '¥ 18.50', '带币种符号')
assertEqual(formatMoney('$', undefined), '--', '缺失时不带币种符号')
assertEqual(formatVolume(0.0021), '0.0021', '体积保留有效小数')
assertEqual(formatVolume(0.1), '0.1', '去掉多余的尾随 0')
assertEqual(formatVolume(2), '2', '整数不带小数点')
assertEqual(formatVolume(undefined), '--', '缺失体积显示占位')

assertDeepEqual(formatUpdatedParts('2026-10-04T14:20:11.123', 2026), { date: '10-04', time: '14:20' }, '当年省略年份')
assertDeepEqual(formatUpdatedParts('2025-12-30T09:05:00', 2026), { date: '2025-12-30', time: '09:05' }, '跨年保留年份避免歧义')
assertDeepEqual(formatUpdatedParts('2026-10-04 14:20:11', 2026), { date: '10-04', time: '14:20' }, '空格分隔的时间同样处理')
assertDeepEqual(formatUpdatedParts(undefined, 2026), { date: '--', time: '' }, '缺失更新时间显示占位')
assertEqual(formatFullTimestamp('2026-10-04T14:20:11.123'), '2026-10-04 14:20', '悬浮提示用完整时间，只整理文本不做时区换算')
assertEqual(formatFullTimestamp(undefined), '--', '缺失时显示占位')

// ---------------------------------------------------------------------------
// 缩略图占位
// ---------------------------------------------------------------------------

assertEqual(productThumbLetter('不锈钢保温杯'), '不', '取第一个汉字')
assertEqual(productThumbLetter('  stainless'), 'S', '跳过首部空白并转大写')
assertEqual(productThumbLetter('😀 smile'), '😀', '按码点取字，不劈开代理对')
assertEqual(productThumbLetter(''), '?', '没有名称时用问号')
assertEqual(productThumbLetter(undefined), '?', '名称缺失时用问号')
assertEqual(productThumbColor('HB012-8001'), productThumbColor('HB012-8001'), '同一种子永远同色')
assert(/^#[0-9a-f]{6}$/i.test(productThumbColor('whatever')), '底色是合法的十六进制色')
assert(/^#[0-9a-f]{6}$/i.test(productThumbColor(undefined)), '种子缺失也能给出底色')
const tileUri = buildLetterTileDataUri('<', '#5b8def')
assert(tileUri.startsWith('data:image/svg+xml;charset=utf-8,'), '占位图是内联 SVG data URI')
assert(decodeURIComponent(tileUri).includes('&lt;'), '特殊字符 < 必须被转义成 &lt;，否则 SVG 无法渲染')

// ---------------------------------------------------------------------------
// 行点击：行内自带交互的元素不触发「开详情」
// ---------------------------------------------------------------------------

const insideRow = { contains: () => true }
const outsideRow = { contains: () => false }
assert(shouldIgnoreRowClick(null, insideRow), '没有目标元素时忽略')
assert(shouldIgnoreRowClick({ closest: () => ({}) }, insideRow), '点在按钮 / 勾选框等交互元素内时忽略')
assert(!shouldIgnoreRowClick({ closest: () => null }, insideRow), '点在普通单元格上才开详情')
assert(shouldIgnoreRowClick({ closest: () => null }, outsideRow), 'Portal（下拉菜单 / 图片预览）里的点击虽冒泡到行，但 DOM 不在行内，必须忽略')
for (const required of ['button', '.ant-checkbox-wrapper', '.ant-table-selection-column', '.ant-dropdown', '.ant-image']) {
  assert(ROW_CLICK_IGNORE_SELECTOR.includes(required), `行点击忽略选择器必须包含 ${required}`)
}

// ---------------------------------------------------------------------------
// 编辑 / 新建表单
// ---------------------------------------------------------------------------

const sampleItem: DomesticProductItem = {
  id: 'P1',
  rowNumber: 1,
  supplierCode: 'HB012',
  supplierName: '义乌市嘉悦日用品商行',
  name: '不锈钢保温杯 500ml 黑色',
  nameEn: 'Stainless Steel Tumbler 500ml Black',
  itemNumber: 'HB012-8001',
  barcode: '6901234567892',
  specs: '500ml · 黑色',
  productImage: 'https://cdn.example.com/p/8001.jpg',
  productType: ProductType.NORMAL,
  domesticPrice: 18.5,
  labelPrice: 6.99,
  importPrice: 4.2,
  packingQty: 48,
  volume: 0.0016,
  middlePackQty: 12,
  packingSize: '30x20x10',
  material: '304 不锈钢',
  remark: '旧备注',
  isActive: true,
  createdAt: '2026-08-21T10:00:00',
  updatedAt: '2026-10-04T14:20:11',
  updatedBy: 'Amy',
}

const formValues = productToFormValues(sampleItem)
assertEqual(formValues.productName, sampleItem.name, '表单载入商品名称')
assertEqual(formValues.oemPrice, 6.99, '零售价来自 labelPrice')
assertEqual(formValues.packingQuantity, 48, '装箱数来自 packingQty')
assertEqual(formValues.isActive, true, '状态原样载入')
assert(!('barcode' in formValues) && !('hbProductNo' in formValues), '编辑表单不载入条码 / 货号（后端不保存，只读展示在抽屉）')

assertEqual(productToFormValues({ ...sampleItem, packingQty: 0, middlePackQty: 0 }).packingQuantity, undefined, '历史数据里的 0 装箱数按未填处理，避免卡住保存')
assertEqual(productToFormValues({ ...sampleItem, packingQty: 0, middlePackQty: 0 }).middlePackQuantity, undefined, '历史数据里的 0 中包数量按未填处理')
assertEqual(productToFormValues({ ...sampleItem, packingQty: 1 }).packingQuantity, 1, '最小值 1 是合法的')

// 更新载荷：只带后端 UpdateDomesticProductDto 保存的 12 个字段
const updatePayload = buildUpdatePayload({
  ...formValues,
  productName: '  新名称  ',
  englishProductName: '   ',
  productSpecification: '',
  domesticPrice: null,
  productImage: ' https://cdn.example.com/p/new.jpg ',
  isActive: false,
})
assertDeepEqual(
  Object.keys(updatePayload).sort(),
  [
    'domesticPrice',
    'englishProductName',
    'importPrice',
    'isActive',
    'middlePackQuantity',
    'oemPrice',
    'packingQuantity',
    'productImage',
    'productName',
    'productSpecification',
    'productType',
    'unitVolume',
  ],
  '更新载荷恰好是后端保存的 12 个字段',
)
for (const removed of ['barcode', 'packingSize', 'material', 'remarks', 'hbProductNo', 'supplierCode']) {
  assert(!(removed in updatePayload), `更新载荷不能再带后端不保存的字段 ${removed}`)
}
assertEqual(updatePayload.productName, '新名称', '商品名称去掉首尾空白')
assertEqual(updatePayload.englishProductName, undefined, '全空白的可选文本按未填处理')
assertEqual(updatePayload.productSpecification, undefined, '空串规格按未填处理')
assertEqual(updatePayload.domesticPrice, undefined, '清空的价格（null）按未填处理')
assertEqual(updatePayload.productImage, 'https://cdn.example.com/p/new.jpg', '图片链接去掉首尾空白')
assertEqual(updatePayload.isActive, false, '停用必须原样提交：后端 Update 的 IsActive 缺省为 true，漏带会把停用商品重新启用')
assertEqual(buildUpdatePayload({ ...formValues, domesticPrice: 0 }).domesticPrice, 0, '价格 0 是有效值，不能被当成未填')

const createPayload = buildCreatePayload({
  ...emptyProductFormValues(),
  supplierCode: 'HB012',
  productName: ' 新商品 ',
  hbProductNo: '   ',
  barcode: '',
  oemPrice: 1.99,
})
assertEqual(createPayload.supplierCode, 'HB012', '新建带供应商编码')
assertEqual(createPayload.productName, '新商品', '新建商品名称去首尾空白')
assertEqual(createPayload.hbProductNo, undefined, '货号留空交给后端分配，空白不能传过去')
assertEqual(createPayload.barcode, undefined, '条码留空交给后端分配，空串不能传过去')
assertEqual(createPayload.productType, ProductType.NORMAL, '新建默认普通商品')
assertEqual(createPayload.isActive, true, '新建默认启用')
assertEqual(createPayload.oemPrice, 1.99, '新建带价格')
assertEqual(buildCreatePayload({ ...emptyProductFormValues(), supplierCode: 'HB012', productName: 'x', barcode: '690123' }).barcode, '690123', '新建时填写的条码照常提交')

// 已修改项数
const initialForEdit: Partial<ProductFormValues> = { ...formValues }
assertEqual(countChangedFields(initialForEdit, { ...formValues }), 0, '没改动为 0')
assertEqual(countChangedFields(initialForEdit, { ...formValues, productName: `  ${formValues.productName}  ` }), 0, '只多了首尾空白不算修改')
assertEqual(countChangedFields({ ...formValues, englishProductName: undefined }, { ...formValues, englishProductName: '' }), 0, '未填与空串等价')
assertEqual(countChangedFields({ ...formValues, domesticPrice: undefined }, { ...formValues, domesticPrice: null }), 0, 'undefined 与 null 等价')
assertEqual(countChangedFields(initialForEdit, { ...formValues, domesticPrice: 19 }), 1, '改一个价格算 1 项')
assertEqual(countChangedFields(initialForEdit, { ...formValues, domesticPrice: 19, isActive: false, productType: ProductType.SET }), 3, '多个字段分别计数')
assertEqual(countChangedFields(initialForEdit, { ...formValues, domesticPrice: 0 }), 1, '改成 0 算修改')
assertEqual(countChangedFields({ ...formValues, domesticPrice: undefined }, { ...formValues, domesticPrice: 0 }), 1, '从空改成 0 算修改')
assertEqual(countChangedFields(initialForEdit, { ...formValues, supplierCode: 'OTHER' }), 0, '供应商不在编辑字段里，不参与统计')

// 保存后合并进抽屉里的商品
const patchedItem = applyFormValuesToItem(sampleItem, { ...formValues, productName: '新名称', domesticPrice: 20, isActive: false })
assertEqual(patchedItem.name, '新名称', '合并名称')
assertEqual(patchedItem.domesticPrice, 20, '合并国内价')
assertEqual(patchedItem.isActive, false, '合并状态')
assertEqual(patchedItem.itemNumber, sampleItem.itemNumber, '不动货号')
assertEqual(patchedItem.barcode, sampleItem.barcode, '不动条码')
assertEqual(patchedItem.supplierName, sampleItem.supplierName, '不动供应商')
assertEqual(patchedItem.updatedBy, 'Amy', '不动更新人（以服务端刷新为准）')

// 导出：total 只当 pageSize 上限，绝不改写
assertEqual(resolveExportPageSize(12431), 12431, '按列表 total 一次取回，不做前端修正')
assertEqual(resolveExportPageSize(0), 1, 'total 为 0 时仍给合法 pageSize')

console.log('domesticProductsLogic.test: ok')
