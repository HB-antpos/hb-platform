import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { applySetCodeColumnPaste } from '../../ProductManagement/setCodeColumnPaste'

const invoicePageSource = readFileSync('src/pages/PosAdmin/LocalSupplierInvoices/InvoiceEdit/index.tsx', 'utf8')
const modalSource = readFileSync('src/pages/PosAdmin/LocalSupplierInvoices/InvoiceEdit/ProductSetCodeMaintenanceModal.tsx', 'utf8')

const requireInvoiceSource = (snippet: string, message: string) => {
  assert.equal(invoicePageSource.includes(snippet), true, message)
}
const requireModalSource = (snippet: string, message: string) => {
  assert.equal(modalSource.includes(snippet), true, message)
}

requireInvoiceSource("import ProductSetCodeMaintenanceModal from './ProductSetCodeMaintenanceModal'", '进货单页应接入独立多码/套装弹窗')
requireInvoiceSource('setSetCodeMaintenanceTarget({ ...record, productCode: maintenanceProductCode })', '商品行操作应使用去除空格后的商品号打开维护弹窗')
requireInvoiceSource('const maintenanceProductCode = record.productCode?.trim()', '入口应拒绝仅包含空格的商品号')
requireInvoiceSource('disabled: !maintenanceProductCode', '未检测或未匹配商品的明细不得打开维护弹窗（行尾菜单项禁用）')
requireInvoiceSource('productCode={setCodeMaintenanceTarget?.productCode}', '弹窗必须使用当前行实际 productCode')
requireInvoiceSource('storeCode={setCodeMaintenanceTarget?.storeCode?.trim() || invoice?.storeCode?.trim()}', '弹窗保存必须限定当前进货单分店')

requireModalSource('for (let page = 1; page <= MAX_PAGE_COUNT; page += 1)', '弹窗应加载全部条码分页')
requireModalSource('seenPageSignatures.has(signature)', '分页未推进时应停止，避免无限请求')
requireModalSource('rowsById.size !== expectedTotal', '分页完成后应核对唯一行数与服务端总数')
requireModalSource("handlePaste(event, 'setBarcode', rowId)", '条码单元格应支持从当前行粘贴 Excel 列')
requireModalSource("handlePaste(event, 'setRetailPrice', rowId)", '套装零售价单元格应支持从当前行粘贴 Excel 列')
requireModalSource("mode === 2 && field === 'setRetailPrice'", '多条码商品应保持主价格语义，只开放条码粘贴')
requireModalSource('validateSetCodeDrafts(validationRows, validationEdits)', '保存前应按商品类型校验空条码、空价格与重复条码')
requireModalSource('saveInFlightRef.current', '保存应使用同步锁防止重复提交')
requireModalSource('saveStoreProductSetCodeSnapshot({', '弹窗应通过单事务快照端点保存全部增删改')
requireModalSource('expectedProductType: baselineProductType', '保存必须提交加载时商品类型用于并发校验')
requireModalSource('expectedItems: baselineRows.map', '保存必须提交加载时完整条码快照用于并发校验')
requireModalSource('row.sourceSetType', '修复套装历史多条码时，并发基线必须保留每行原始类型')
requireModalSource('productType: mode', '目标快照必须明确保存当前套装/多码类型')
requireModalSource('await loadLatestData(false)', '保存成功或中断后应回读服务器状态')
requireModalSource('saveSetCodeMaintenanceUnconfirmed', '异常且回读无法确认时不得断言事务未提交')
requireModalSource('const [loadError, setLoadError] = useState<string | null>(null)', '加载失败必须保留明确的持久错误状态')
requireModalSource("action={<Button size=\"small\" onClick={() => void loadLatestData()}>{t('common.retry', '重试')}</Button>}", '加载失败必须提供显式重试入口')
requireModalSource('saveSetCodeMaintenanceUnconfirmedReloadFailed', '保存结果未确认且回读失败时不得声称已重新加载')
requireModalSource('const isChangingNormalProduct = product != null', '尚未加载商品或加载失败时不得显示普通商品切换提示')
requireModalSource('!canSwitchMode', '存在服务端条码时不得直接切换类型')
requireModalSource('setIntegrityError', '商品类型与条码类型不一致时应停止编辑')
requireModalSource('resolveProductSetCodeMaintenanceLoadState({', '加载后应通过统一状态解析器决定阻断或套装修复')
requireModalSource('setCodeRepairMultiCodeHint', '套装修复历史多条码时应明确提示保存后的转换结果')
requireModalSource('scroll={{ x: 780, y: 420 }}', '窄视口下表格应允许横向和纵向滚动')
requireModalSource('const MAX_PASTE_ROWS = PAGE_SIZE * MAX_PAGE_COUNT', '弹窗粘贴上限必须与已加载分页容量一致')
requireModalSource('maxRows: MAX_PASTE_ROWS', '弹窗必须将自身分页容量传给列粘贴助手')
requireModalSource("result.error === 'too_many_rows'", '超过弹窗容量时必须显式阻止本次粘贴')
requireModalSource('posAdmin.products.pasteTooManyRows', '超过弹窗容量时必须复用产品页既有双语提示')
requireModalSource("aria-label={t('common.copyValue', 'Copy {{value}}', { value })}", '仅图标复制按钮必须提供本地化可访问名称')
requireModalSource("successMessage: t('common.copySuccess', '复制成功')", '复制成功提示必须本地化并由复制工具统一处理')
requireModalSource("failureMessage: t('common.copyFailed', '复制失败')", '复制失败提示必须本地化并由复制工具统一处理')

const modalPasteMaxRows = 100 * 100
const clipboardColumn = (count: number) => Array.from(
  { length: count },
  (_, index) => `SET-CODE-${index + 1}`,
).join('\n')

let acceptedCreateCalls = 0
const atModalPasteLimit = applySetCodeColumnPaste({
  rows: [],
  edits: {},
  field: 'setBarcode',
  clipboardText: clipboardColumn(modalPasteMaxRows),
  maxRows: modalPasteMaxRows,
  createRow: (index) => {
    acceptedCreateCalls += 1
    return { _rowId: `accepted-${index}` }
  },
})
assert.equal(atModalPasteLimit.error, undefined, '恰好 10,000 行必须允许粘贴')
assert.equal(atModalPasteLimit.rows.length, modalPasteMaxRows, '恰好 10,000 行必须完整写入')
assert.equal(acceptedCreateCalls, modalPasteMaxRows)

let rejectedCreateCalls = 0
const aboveModalPasteLimit = applySetCodeColumnPaste({
  rows: [],
  edits: {},
  field: 'setBarcode',
  clipboardText: clipboardColumn(modalPasteMaxRows + 1),
  maxRows: modalPasteMaxRows,
  createRow: (index) => {
    rejectedCreateCalls += 1
    return { _rowId: `rejected-${index}` }
  },
})
assert.equal(aboveModalPasteLimit.error, 'too_many_rows', '10,001 行必须整次拒绝')
assert.equal(aboveModalPasteLimit.rows.length, 0, '超过 10,000 行不得创建草稿')
assert.equal(rejectedCreateCalls, 0, '超过 10,000 行必须在创建草稿前拒绝')

assert.equal(modalSource.includes("from '../../../../services/multiCodeSetService'"), false, '弹窗不得使用固定创建多码类型的旧接口')
assert.equal(modalSource.includes('createStoreProductSetCode('), false, '弹窗不得串行新增导致部分保存')
assert.equal(modalSource.includes('updateStoreProductSetCode('), false, '弹窗不得串行更新导致部分保存')
assert.equal(modalSource.includes('deleteStoreProductSetCode('), false, '弹窗不得串行删除导致部分保存')
assert.equal(modalSource.includes('updateStoreProductType('), false, '弹窗不得先切换类型导致部分保存')
assert.equal(modalSource.includes('completedOperations'), false, '单事务保存失败不应再报告部分成功')
assert.equal(modalSource.includes('服务器未提交本次修改'), false, '网络中断时不能断言服务端未提交')
assert.equal(modalSource.includes("message.success(t('message.copySuccess'"), false, '复制按钮不得在复制失败时额外提示成功')
assert.equal(modalSource.includes('updateProduct('), false, '弹窗不得通过覆盖式商品更新切换类型')
assert.equal(/(?:window|document)\.addEventListener\(['\"]paste['\"]/.test(modalSource), false, '弹窗不得注册全局 paste 监听')
