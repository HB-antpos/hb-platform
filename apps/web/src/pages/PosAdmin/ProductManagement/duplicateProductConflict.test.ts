import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import path from 'node:path'

import {
  BARCODE_EXISTS_ERROR_CODE,
  ITEM_NUMBER_EXISTS_ERROR_CODE,
  formatExistingProductLine,
  readProductCreateConflict,
} from './duplicateProductConflict'

// 请求层把 4xx 响应体放在 error.payload，这里用同形状的对象模拟（不依赖具体异常类）。
function requestError(payload: unknown, status = 409) {
  return Object.assign(new Error('Conflict'), { status, payload })
}

// ---- BARCODE_EXISTS：带已有商品清单 ----
const barcodeConflict = readProductCreateConflict(
  requestError({
    success: false,
    message: '条码 9310000000017 已属于商品 货号 62800 · Soft toy（供应商 250），请选用已有商品，不能重复新建',
    errorCode: 'BARCODE_EXISTS',
    existingProducts: [
      { productCode: 'p1', itemNumber: '62800', productName: 'Soft toy', localSupplierCode: '250', source: 'MainBarcode' },
      { productCode: 'p2', itemNumber: null, productName: ' ', localSupplierCode: '248', source: 'StoreMultiCode' },
      { itemNumber: '没有商品编码的项会被丢弃' },
      'not-an-object',
    ],
  }),
)
assert.ok(barcodeConflict, '条码冲突必须被识别')
assert.equal(barcodeConflict.errorCode, BARCODE_EXISTS_ERROR_CODE)
assert.match(barcodeConflict.message ?? '', /请选用已有商品/)
assert.deepEqual(barcodeConflict.existingProducts, [
  { productCode: 'p1', itemNumber: '62800', productName: 'Soft toy', localSupplierCode: '250', source: 'MainBarcode' },
  { productCode: 'p2', itemNumber: undefined, productName: undefined, localSupplierCode: '248', source: 'StoreMultiCode' },
])

// 清单缺失（异常响应）也要识别为条码冲突，只是列表为空，页面退回使用后端 message。
const noList = readProductCreateConflict(requestError({ success: false, errorCode: 'BARCODE_EXISTS' }))
assert.equal(noList?.errorCode, BARCODE_EXISTS_ERROR_CODE)
assert.deepEqual(noList?.existingProducts, [])

// 大写开头的字段名也接受（与其他接口的兼容读取口径一致）。
const pascal = readProductCreateConflict(
  requestError({ ErrorCode: ' BARCODE_EXISTS ', ExistingProducts: [{ ProductCode: 'p3', ItemNumber: '1', ProductName: 'N', LocalSupplierCode: 'S' }] }),
)
assert.deepEqual(pascal?.existingProducts, [
  { productCode: 'p3', itemNumber: '1', productName: 'N', localSupplierCode: 'S', source: undefined },
])

// ---- ITEM_NUMBER_EXISTS：原有逻辑，没有清单 ----
const itemConflict = readProductCreateConflict(
  requestError({ success: false, message: '该供应商下货号已存在，请更换货号', errorCode: 'ITEM_NUMBER_EXISTS' }),
)
assert.equal(itemConflict?.errorCode, ITEM_NUMBER_EXISTS_ERROR_CODE)
assert.deepEqual(itemConflict?.existingProducts, [])

// ---- 其他失败不当作查重冲突，由页面走通用提示 ----
assert.equal(readProductCreateConflict(requestError({ success: false, message: '服务器内部错误' }, 500)), null)
assert.equal(readProductCreateConflict(requestError({ errorCode: 'OTHER_CODE' })), null)
assert.equal(readProductCreateConflict(requestError('text body')), null)
assert.equal(readProductCreateConflict(new Error('plain')), null)
assert.equal(readProductCreateConflict(null), null)
assert.equal(readProductCreateConflict(undefined), null)
assert.equal(readProductCreateConflict('BARCODE_EXISTS'), null)

// ---- 弹窗里的一行：货号 · 名称 · 供应商 ----
assert.equal(
  formatExistingProductLine(
    { productCode: 'p1', itemNumber: '62800', productName: 'Soft toy', localSupplierCode: '250' },
    { supplier: '供应商' },
  ),
  '62800 · Soft toy · 供应商 250',
)
assert.equal(
  formatExistingProductLine({ productCode: 'p2' }, { supplier: 'Supplier' }),
  '-- · -- · Supplier --',
  '缺失的部分用 -- 占位，不能出现 undefined',
)

// ---- 页面契约：创建失败时先识别条码冲突并弹窗，不再落到「创建商品失败」，也没有「仍然创建」入口 ----
// 测试在 apps/web 目录下运行（与同目录其他 uiContract 测试一致）。
const pageSource = readFileSync(path.resolve(process.cwd(), 'src/pages/PosAdmin/ProductManagement/index.tsx'), 'utf8')
const createSaveSource = pageSource.match(/const handleCreateSave = async \(\) => \{[\s\S]*?\n  \}\n\n  const handleBatchEnable/)?.[0] ?? ''
assert.ok(createSaveSource, '必须能定位创建商品的提交函数')
assert.match(createSaveSource, /readProductCreateConflict\(error\)/, '创建失败要先解析查重冲突')
assert.match(createSaveSource, /BARCODE_EXISTS_ERROR_CODE/, '必须有条码已存在分支')
assert.match(createSaveSource, /Modal\.warning\(/, '条码已存在要用弹窗列出已有商品')
assert.doesNotMatch(createSaveSource, /仍然创建|forceCreate|ignoreDuplicate/, '不提供「仍然创建」')
assert.ok(
  createSaveSource.indexOf('BARCODE_EXISTS_ERROR_CODE') < createSaveSource.indexOf('posAdmin.products.createProductFailed'),
  '条码冲突分支必须在通用失败提示之前',
)

console.log('duplicateProductConflict tests passed')
