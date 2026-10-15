import { readFileSync } from 'node:fs'
import path from 'node:path'
import {
  DEFAULT_RETAIL_PRICE_SYNC_CHOICE,
  buildRetailPriceSyncFields,
  isBatchRetailPriceFilled,
  isRetailPriceChanged,
} from './retailPriceSyncOptions'

function assert(condition: unknown, message: string): asserts condition {
  if (!condition) throw new Error(message)
}

function assertDeepEqual(actual: unknown, expected: unknown, message: string) {
  const actualJson = JSON.stringify(actual)
  const expectedJson = JSON.stringify(expected)
  if (actualJson !== expectedJson) {
    throw new Error(`${message}。Expected: ${expectedJson}, received: ${actualJson}`)
  }
}

function extractSection(source: string, startText: string, endText: string) {
  const startIndex = source.indexOf(startText)
  assert(startIndex >= 0, `未找到代码片段：${startText}`)
  const endIndex = source.indexOf(endText, startIndex)
  assert(endIndex >= 0, `未找到结束片段：${endText}`)
  return source.slice(startIndex, endIndex)
}

const read = (relativePath: string) => readFileSync(path.resolve(process.cwd(), relativePath), 'utf8')
const pageSource = read('src/pages/Warehouse/Products/index.tsx')
const fieldSource = read('src/pages/Warehouse/Products/RetailPriceSyncOptionsField.tsx')
const containerDetailSource = read('src/pages/Warehouse/ContainerDetail/index.tsx')
const zhMessages = JSON.parse(read('src/pages/Warehouse/Products/warehouseProductsMessages.zh.json'))
const enMessages = JSON.parse(read('src/pages/Warehouse/Products/warehouseProductsMessages.en.json'))

// 1. 纯逻辑：默认两个都勾选；只有真的改了零售价才带字段
assertDeepEqual(DEFAULT_RETAIL_PRICE_SYNC_CHOICE, { syncStoreRetailPrices: true, generateTasks: true }, '两个勾选项默认都应勾选')
assert(isRetailPriceChanged(9.9, 8.8) && isRetailPriceChanged(9.9, null) && isRetailPriceChanged(9.9, undefined), '零售价与原值不同（含原值为空）应算改了零售价')
assert(!isRetailPriceChanged(8.8, 8.8), '零售价与原值相同不算改了零售价')
assert(!isRetailPriceChanged(undefined, 8.8) && !isRetailPriceChanged(null, 8.8), '零售价留空不算改了零售价')
assert(isBatchRetailPriceFilled(0) && isBatchRetailPriceFilled(5), '批量修改填了零售价（含 0）应算改零售价')
assert(!isBatchRetailPriceFilled(undefined) && !isBatchRetailPriceFilled(null), '批量修改留空零售价不算改零售价')
assertDeepEqual(
  buildRetailPriceSyncFields({ syncStoreRetailPrices: false, generateTasks: true }, true),
  { syncStoreRetailPrices: false, generateTasks: true },
  '改了零售价时应按勾选状态显式发送两个字段（不勾 = false）',
)
assertDeepEqual(
  buildRetailPriceSyncFields({ syncStoreRetailPrices: false, generateTasks: false }, false),
  {},
  '没改零售价时不应携带这两个字段',
)

// 2. 文案：随页面懒注册的页面级消息，中英文都有，且中文与产品约定一致
const zhSync = zhMessages.warehouseUi.products.retailPriceSync
const enSync = enMessages.warehouseUi.products.retailPriceSync
assert(zhSync.syncStoreRetailPrices === '同步分店零售价', '同步分店零售价中文标题')
assert(zhSync.syncStoreRetailPricesHint === '勾选后直接把各分店零售价改成新价并通知换标签；不勾选则不动分店价，由分店确认改价', '同步分店零售价中文说明')
assert(zhSync.generateTasks === '生成价格任务', '生成价格任务中文标题')
assert(zhSync.generateTasksHint === '勾选后给分店生成价格更新任务；不勾选则不通知分店（同步分店价时会立即同步总部 HQ）', '生成价格任务中文说明')
for (const key of Object.keys(zhSync)) {
  assert(typeof enSync[key] === 'string' && enSync[key].length > 0, `retailPriceSync.${key} 缺少英文文案`)
}
for (const key of ['syncStoreRetailPrices', 'syncStoreRetailPricesHint', 'generateTasks', 'generateTasksHint']) {
  assert(fieldSource.includes(`warehouseUi.products.retailPriceSync.${key}`), `勾选组件应使用页面级文案 ${key}`)
}
assert(
  fieldSource.includes('<Checkbox checked={value.syncStoreRetailPrices}') && fieldSource.includes('<Checkbox checked={value.generateTasks}'),
  '勾选组件应是两个受控 Checkbox',
)

// 3. 编辑弹窗：只在零售价相对原值变化时展示；保存时同口径决定是否带字段；每次打开重置为默认
const formModalSection = extractSection(pageSource, 'function ProductFormModal(', 'function SetItemsModal(')
assert(
  formModalSection.includes('const retailPriceWillChange = Boolean(editingItem) && isRetailPriceChanged(watchedRetailPrice, originalRetailPrice)') &&
    formModalSection.includes('{retailPriceWillChange ? (') &&
    formModalSection.includes('<RetailPriceSyncOptionsField value={retailPriceSync} onChange={onRetailPriceSyncChange}'),
  '编辑弹窗应只在零售价发生变化时展示两个勾选项',
)
assert(
  formModalSection.includes('!pricePreview.retailPriceChanged || retailPreviewMatchesChoice'),
  '取消任一勾选后，「自动下发并通知换标签」的零售价预告不应再显示',
)
const openEditSection = extractSection(pageSource, 'const handleOpenEdit = (', 'const handleCloseModal = () => {')
assert(
  openEditSection.includes('setEditRetailPriceSync({ ...DEFAULT_RETAIL_PRICE_SYNC_CHOICE })'),
  '每次打开编辑弹窗都应把两个勾选项重置为默认勾选',
)
const saveSection = extractSection(pageSource, 'const handleSave = async (', 'const getInlineCellKey = (')
assert(
  saveSection.includes('const retailPriceChanged = isRetailPriceChanged(values.oemPrice, editingItem.labelPrice)') &&
    saveSection.includes('...buildRetailPriceSyncFields(editRetailPriceSync, retailPriceChanged)'),
  '编辑保存应只在改了零售价时随 full-update 带上两个开关',
)

// 4. 行内改价：零售价先确认再 PATCH，其它字段保持原有一步保存且不带开关
const inlineSection = extractSection(pageSource, 'const handleInlineSave = async (', 'const inlineRetailPriceConfirmRecord')
assert(
  inlineSection.includes("if (cell.field === 'labelPrice') {") &&
    inlineSection.includes('setInlineRetailPriceConfirm({ open: true, cell, value, sync: { ...DEFAULT_RETAIL_PRICE_SYNC_CHOICE } })'),
  '行内改零售价应先弹出带两个勾选项的确认框',
)
assert(
  inlineSection.indexOf("if (cell.field === 'labelPrice') {") < inlineSection.indexOf('await commitInlineSave(cell, value, payload)'),
  '零售价确认必须发生在直接保存之前',
)
assert(
  inlineSection.includes('oemPrice: pending.value,') && inlineSection.includes('...buildRetailPriceSyncFields(pending.sync, true)'),
  '确认保存后的零售价 PATCH 应携带两个开关',
)
const directPayloadSection = extractSection(inlineSection, 'const payload: PatchWarehouseProductPayload', 'await commitInlineSave(cell, value, payload)')
assert(
  !directPayloadSection.includes('oemPrice') && !directPayloadSection.includes('syncStoreRetailPrices') && !directPayloadSection.includes('generateTasks'),
  '非零售价的行内修改不应带零售价字段与同步开关',
)
assert(
  inlineSection.includes('if (!pending?.open) return;') &&
    inlineSection.includes('inlineSaveLockRef.current = null;'),
  '确认框应防重复提交，并在取消时释放行内保存锁',
)

// 5. 批量修改：只在填了零售价时展示，并随后台任务请求带上开关
assert(
  pageSource.includes("const batchRetailPriceFilled = isBatchRetailPriceFilled(Form.useWatch('oemPrice', batchEditForm))"),
  '批量修改弹窗应监听零售价是否填写',
)
const batchModalSection = extractSection(pageSource, "t('warehouse.batchEditTitle'", '</Form>')
assert(
  batchModalSection.includes('{batchRetailPriceFilled ? (') &&
    batchModalSection.includes('<RetailPriceSyncOptionsField value={batchRetailPriceSync} onChange={setBatchRetailPriceSync}'),
  '批量修改弹窗应只在填了零售价时展示两个勾选项',
)
const openBatchEditSection = extractSection(pageSource, 'const openBatchEdit = () => {', 'const handleCategoryMutationCommitted')
assert(
  openBatchEditSection.includes('setBatchRetailPriceSync({ ...DEFAULT_RETAIL_PRICE_SYNC_CHOICE })'),
  '每次打开批量修改都应把两个勾选项重置为默认勾选',
)
const batchSaveSection = extractSection(pageSource, 'const handleBatchEditSave = async (', 'const handleToggleSingleActive = async (')
assert(
  batchSaveSection.includes('...buildRetailPriceSyncFields(batchRetailPriceSync, isBatchRetailPriceFilled(values.oemPrice))'),
  '批量修改提交时应只在填了零售价时带上两个开关',
)

// 6. 货柜明细页没有合适的零售价确认位置：两处批量调用显式固定「不同步分店零售价、仍生成任务」
const containerUpdateSection = extractSection(containerDetailSource, 'const updateExistingPurchase = async () => {', 'const updateWarehouseImportPrice = async () => {')
assert(
  containerUpdateSection.includes('syncStoreRetailPrices: false,') && containerUpdateSection.includes('generateTasks: true,'),
  '货柜明细「更新已有商品」的批量修改应显式传 syncStoreRetailPrices: false, generateTasks: true',
)
const containerImportSection = extractSection(containerDetailSource, 'const updateWarehouseImportPrice = async () => {', '删除明细后后端已按剩余明细重算')
assert(
  containerImportSection.includes('syncStoreRetailPrices: false, generateTasks: true'),
  '货柜明细「更新仓库进货价」的批量修改应显式传 syncStoreRetailPrices: false, generateTasks: true',
)

console.log('WarehouseProducts.retailPriceSync.uiContract.test: ok')
