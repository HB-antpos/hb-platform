import assert from 'node:assert/strict'
import { readFileSync, readdirSync } from 'node:fs'
import { resolve } from 'node:path'

const pageDir = 'src/pages/PosAdmin/ProductManagement'
const read = (path: string) => readFileSync(resolve(process.cwd(), path), 'utf8')
const pageSource = read(`${pageDir}/index.tsx`)

const sliceBetween = (source: string, start: string, end: string) => {
  const startIndex = source.indexOf(start)
  assert.notEqual(startIndex, -1, `应找到：${start}`)
  const endIndex = source.indexOf(end, startIndex + start.length)
  assert.notEqual(endIndex, -1, `应找到：${end}`)
  return source.slice(startIndex, endIndex)
}

// 文案随页面懒注册，主包零改动
assert.ok(pageSource.includes('registerPageMessages({ zh: messagesZh, en: messagesEn })'), '商品管理页必须懒注册本页文案')
assert.ok(pageSource.includes("import messagesZh from './messages.zh.json'") && pageSource.includes("import messagesEn from './messages.en.json'"))
for (const language of ['zh', 'en'] as const) {
  const main = JSON.parse(read(`src/i18n/locales/${language}.json`)) as { posAdmin?: { products?: Record<string, unknown> } }
  assert.equal(main.posAdmin?.products?.supplierCategory, undefined, `首屏 ${language}.json 不应包含供应商分类文案`)
}

// zh/en 键集合一致
function collectKeys(value: unknown, prefix = ''): string[] {
  if (typeof value !== 'object' || value === null) return [prefix]
  return Object.entries(value).flatMap(([key, child]) => collectKeys(child, prefix ? `${prefix}.${key}` : key))
}
const zh = JSON.parse(read(`${pageDir}/messages.zh.json`)) as { posAdmin: { products: { supplierCategory: Record<string, string> } } }
const en = JSON.parse(read(`${pageDir}/messages.en.json`)) as { posAdmin: { products: { supplierCategory: Record<string, string> } } }
assert.deepEqual(Object.keys(zh), ['posAdmin'], '页面文案只放 posAdmin 命名空间')
assert.deepEqual(Object.keys(zh.posAdmin.products), ['supplierCategory'], '页面文案只新增 posAdmin.products.supplierCategory')
const zhKeys = collectKeys(zh).sort()
const enKeys = collectKeys(en).sort()
assert.ok(zhKeys.length > 30, '应有足够的文案键')
assert.deepEqual(zhKeys, enKeys, 'zh/en 文案键必须一致')

// 源码引用的键必须全部存在
const componentSources = readdirSync(resolve(process.cwd(), pageDir))
  .filter((file) => /^SupplierCategory.*\.tsx$/.test(file))
  .map((file) => read(`${pageDir}/${file}`))
const usedKeys = new Set<string>()
for (const match of pageSource.matchAll(/'posAdmin\.products\.supplierCategory\.([A-Za-z]+)'/g)) usedKeys.add(match[1])
for (const source of componentSources) {
  assert.ok(source.includes("const I18N = 'posAdmin.products.supplierCategory'") || !source.includes('${I18N}'), '组件文案前缀应统一')
  for (const match of source.matchAll(/`\$\{I18N\}\.([A-Za-z]+)`/g)) usedKeys.add(match[1])
}
assert.ok(usedKeys.size > 30, '应能从源码中提取到文案键')
for (const key of usedKeys) {
  assert.ok(typeof zh.posAdmin.products.supplierCategory[key] === 'string', `缺少文案 posAdmin.products.supplierCategory.${key}`)
}

// 列在澳洲供应商右侧、国内供应商左侧，不支持排序
const columnsSource = sliceBetween(pageSource, 'const columns: ColumnsType<ProductRow> = [', '\n  ]\n\n  return (')
const supplierColumnIndex = columnsSource.indexOf("key: 'localSupplierCode'")
const supplierCategoryColumnIndex = columnsSource.indexOf("key: 'supplierCategory'")
const domesticColumnIndex = columnsSource.indexOf("key: 'domesticSupplierCode'")
assert.ok(supplierColumnIndex >= 0 && supplierColumnIndex < supplierCategoryColumnIndex && supplierCategoryColumnIndex < domesticColumnIndex, '供应商分类列应位于澳洲供应商与国内供应商之间')
const supplierCategoryColumnSource = columnsSource.slice(supplierCategoryColumnIndex, domesticColumnIndex)
assert.ok(supplierCategoryColumnSource.includes('sorter: false'), '供应商分类列首版不支持排序')
assert.ok(supplierCategoryColumnSource.includes('<SupplierCategoryCell'), '列渲染复用 SupplierCategoryCell')

// 顶部筛选：级联框紧跟澳洲供应商 Select，并与之共享 supplierCode
const toolbarSource = sliceBetween(pageSource, '<div className="list-toolbar-filter-row">', '<MoreFiltersButton')
assert.ok(toolbarSource.indexOf('onChange={handleSupplierFilterChange}') < toolbarSource.indexOf('<SupplierCategoryCascader'), '级联框应在供应商 Select 之后')
assert.ok(toolbarSource.includes('value={supplierCategoryCascaderValue}') && toolbarSource.includes('onChange={handleSupplierCategoryFilterChange}'))
assert.ok(toolbarSource.includes('style={{ width: 220 }}'))
assert.ok(pageSource.includes('toSupplierCategoryQueryParams({ supplierCode, warehouseCategoryGuid, supplierCategoryGuid, supplierCategoryUnassignedOnly })'), '列表请求带上供应商分类筛选')
assert.ok(
  sliceBetween(pageSource, 'const handleSupplierFilterChange', '\n  }\n').includes('applySupplierSelectChange('),
  '换供应商经 applySupplierSelectChange 连带清理供应商分类',
)
assert.ok(
  sliceBetween(pageSource, 'const handleWarehouseCategoryFilterChange', '\n  }\n').includes('applyWarehouseCategoryFilterChange('),
  '仓库分类变更处理 200 下未归类的互斥',
)

// 重置、查询、移除标签都要处理供应商分类
const resetSource = sliceBetween(pageSource, 'const handleReset = () => {', '\n  }\n')
for (const setter of ['setSupplierCategoryGuidInput(undefined)', 'setSupplierCategoryGuid(undefined)', 'setSupplierCategoryUnassignedOnlyInput(false)', 'setSupplierCategoryUnassignedOnly(false)']) {
  assert.ok(resetSource.includes(setter), `重置应调用 ${setter}`)
}
const searchSource = sliceBetween(pageSource, 'const handleSearch = () => {', 'const handleReset = () => {')
assert.ok(searchSource.includes('setSupplierCategoryGuid(supplierCategoryGuidInput)') && searchSource.includes('setSupplierCategoryUnassignedOnly(supplierCategoryUnassignedOnlyInput)'))
const removeSource = sliceBetween(pageSource, 'const removeToolbarFilter = (key: ToolbarFilterKey) => {', 'const removeColumnFilter')
const supplierBranch = sliceBetween(removeSource, "key === 'supplierCode'", '} else if')
assert.ok(supplierBranch.includes('commitSupplierCategorySelection('), '移除供应商标签时连带清理供应商分类')
assert.ok(removeSource.includes("key === 'supplierCategory'") && removeSource.includes('clearSupplierCategoryFilter('), '供应商分类标签可单独移除')

// 编辑与批量编辑
assert.ok(pageSource.includes("Form.useWatch('localSupplierCode', editForm)") && pageSource.includes("Form.useWatch('localSupplierCode', batchEditForm)"))
const openEditSource = sliceBetween(pageSource, 'const openEdit = (record: PosProductDto) => {', 'const openStoreRecords')
assert.ok(openEditSource.includes('supplierCategoryGuid: record.supplierCategoryGuid'), '打开编辑时回填供应商分类')
const editSaveSource = sliceBetween(pageSource, 'const handleEditSave = async () => {', 'const handleBatchEnable')
assert.ok(editSaveSource.includes('resolveSupplierCategoryUpdate('), '单个保存走三态解析')
assert.ok(editSaveSource.includes('...supplierCategoryUpdate'), '三态结果并入更新请求')
assert.ok(editSaveSource.includes('SUPPLIER_CATEGORY_MISMATCH_ERROR_CODE'), '分类与供应商不一致时给出明确提示')
const batchSaveSource = sliceBetween(pageSource, 'const handleBatchEditSave = async () => {', '\n  const openSyncToStoreModal')
assert.ok(batchSaveSource.includes("batchSupplierCategoryScope.status === 'enabled'") && batchSaveSource.includes('resolveBatchSupplierCategoryUpdate('), '批量只在同一非 200 供应商时提交')

const editFormSource = sliceBetween(pageSource, '<Form form={editForm}', '</Form>')
assert.ok(editFormSource.includes('<Form.Item name="supplierCategoryGuid"'), '编辑表单有供应商分类字段')
assert.ok(editFormSource.includes('onValuesChange={handleEditFormValuesChange}'), '改供应商时清空供应商分类')
assert.equal(editFormSource.includes('disabled={false}'), false, '编辑表单不得以显式 false 绕过保存期间的 Form 禁用')
const batchFormSource = sliceBetween(pageSource, '<Form form={batchEditForm}', '</Form>')
assert.ok(batchFormSource.includes('<Form.Item name="supplierCategoryGuid"') && batchFormSource.includes('mode="batch"'))
assert.equal(batchFormSource.includes('disabled={false}'), false)
const formFieldSource = read(`${pageDir}/SupplierCategoryFormField.tsx`)
assert.equal(/disabled=\{false\}/.test(formFieldSource), false)
assert.ok(formFieldSource.includes('disabled={unavailable || undefined}'), '表单字段禁用写成 条件 || undefined')

// 工具菜单入口与管理弹窗：查看不加权限码，写操作按商品管理权限
const toolsSource = sliceBetween(pageSource, "label={t('common.listToolbar.tools', '工具')}", '/>\n')
const managementItem = sliceBetween(toolsSource, "key: 'supplierCategoryManagement'", '},')
assert.ok(!managementItem.includes('visible:'), '供应商分类管理入口沿用页面查看权限')
assert.ok(pageSource.includes('<SupplierCategoryManagerModal') && pageSource.includes('canManage={canManagePosProducts}'))
assert.equal(/Permissions?\.|P\.PosProducts/.test(pageSource.slice(pageSource.indexOf('<SupplierCategoryManagerModal'))), false, '不新增权限码')

console.log('supplierCategory.uiContract.test: ok')
