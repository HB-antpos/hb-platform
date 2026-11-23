import { readFileSync } from 'node:fs'
import path from 'node:path'

function assert(condition: boolean, message: string) {
  if (!condition) {
    throw new Error(message)
  }
}

const pageFile = path.resolve(process.cwd(), 'src/pages/Warehouse/StoreOrders/index.tsx')
const source = readFileSync(pageFile, 'utf8')
const storePickerStart = source.indexOf('function StorePickerModal')
const storePickerEnd = source.indexOf('function CopyOrderModal')
const storePickerSource = source.slice(storePickerStart, storePickerEnd)
const createStoreHandlerStart = storePickerSource.indexOf('const handleCreateStore = async () => {')
const createStoreCatchStart = storePickerSource.indexOf('} catch (error) {', createStoreHandlerStart)
const createStoreFinallyStart = storePickerSource.indexOf('} finally {', createStoreCatchStart)
const createStoreCatchSource = storePickerSource.slice(createStoreCatchStart, createStoreFinallyStart)

// 分店筛选已改为页面左侧的分店列表（单选为主，列表头部「多选」开关默认关闭）：选项先构建稳定排序结果，列表里名字在前、编码另行显示
assert(
  source.includes('const sidebarStoreOptions = useMemo('),
  '左侧分店列表选项应先构建稳定排序结果',
)
assert(
  source.includes("localeCompare(right.name || '', 'zh-Hans-CN', { numeric: true, sensitivity: 'base' })"),
  '左侧分店列表选项应按分店名称优先排序',
)
assert(
  source.includes('label: item.name || item.code'),
  '左侧分店列表 label 只放店名（编码由列表组件按 value 单独显示，搜索同时支持名称和编码）',
)
assert(
  source.includes('useStoreSidebar({') && source.includes('multi: { values: selectedStoreCodes, onChange: handleStoreCodesChange }'),
  '订货列表应接入共用的左侧分店列表，并带可开关的多选（多选状态仍是 selectedStoreCodes）',
)
assert(
  source.includes('<StoreSidebarLayout docked={storeSidebar.docked} sidebar={storeSidebar.sidebar}>') && source.includes('{storeSidebar.switcher}'),
  '订货列表应用左侧分店列表布局，收起时分店按钮放在工具栏里',
)
assert(
  !source.includes('wh-orders-store-select'),
  '工具栏里的分店多选下拉应已移除（分店改在左侧列表里选）',
)
// 未匹配分店弹窗里的「目标分店」下拉仍用关键字过滤函数
assert(source.includes('showSearch'), '未匹配分店弹窗的目标分店 Select 应支持输入搜索')
assert(
  source.includes('optionFilterProp="label"'),
  '未匹配分店弹窗的目标分店 Select 应基于 label 过滤',
)
assert(
  source.includes('filterOption={filterStoreOption}'),
  '未匹配分店弹窗的目标分店 Select 应使用关键字过滤函数',
)
assert(storePickerStart >= 0 && storePickerEnd > storePickerStart, '应能定位创建订单分店选择弹窗')
assert(
  storePickerSource.includes("cashRegisterFilter === 'all' ? {} : { isActive: cashRegisterFilter === 'enabled' }"),
  '创建订单分店弹窗应按收银系统状态过滤，默认不固定只查启用分店',
)
assert(
  !storePickerSource.includes('isActive: true'),
  '创建订单分店弹窗默认应显示所有分店，不能固定 isActive: true',
)
assert(
  storePickerSource.includes("title: t('common.index')") && storePickerSource.includes('render: (_value, _record, index) => index + 1'),
  '创建订单分店表格应显示当前列表行号',
)
assert(
  storePickerSource.includes("t('storeOrders.storeCashRegisterAll')") &&
    storePickerSource.includes("t('storeOrders.storeCashRegisterEnabled')") &&
    storePickerSource.includes("t('storeOrders.storeCashRegisterDisabled')"),
  '创建订单分店弹窗应提供全部/启用/未启用收银系统过滤按钮',
)
assert(
  source.includes("import { createStore, getNextStoreCode, getStores } from '../../../services/storeService'") &&
    storePickerSource.includes("initialValues={{ isActive: false }}") &&
    storePickerSource.includes("createStore({ ...values, isActive: values.isActive ?? false })"),
  '创建订单弹窗应支持新建分店且默认不启用收银系统',
)
assert(
  storePickerSource.includes('const nextCode = await getNextStoreCode()') &&
    storePickerSource.includes('createForm.setFieldsValue({ storeCode: nextCode })') &&
    storePickerSource.includes('void loadNextStoreCode()'),
  '打开创建订单分店表单时应从后端获取建议分店编码并填入 storeCode',
)
assert(
  storePickerSource.includes("t('storeOrders.regenerateStoreCode')") &&
    storePickerSource.includes('loading={storeCodeLoading}'),
  '创建订单分店表单应提供重新生成分店编码入口',
)
assert(
  storePickerSource.includes("getApiErrorCode(error) === 'DUPLICATE_STORE_CODE'") &&
    storePickerSource.includes("t('storeOrders.duplicateStoreCode')") &&
    createStoreCatchStart > createStoreHandlerStart &&
    !createStoreCatchSource.includes('setCreateOpen(false)'),
  '创建分店编码重复时应保留表单并展示专用错误提示',
)
assert(
  storePickerSource.includes('onSelect(created)'),
  '新建分店成功后应继续选中该分店并创建订单',
)

console.log('storeOrderFilter.logic.test: ok')
