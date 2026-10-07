import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import {
  DEFAULT_INSTALLMENT_TERMS_LINES,
  DEFAULT_INSTALLMENT_TERMS_TEXT,
  DEFAULT_VOUCHER_TERMS_LINES,
  DEFAULT_VOUCHER_TERMS_TEXT,
  RECEIPT_TERMS_MAX_LENGTH,
} from './receiptTermsDefaults'

// 分店表单 / 批量修改里作为占位提示展示的「收银端内置默认文案」。
// 业主审定过的英文稿，必须与收银端现有默认文案逐字一致（含标点与 $ 金额），这里同时锁住字面值与收银端源码。

assert.deepEqual(
  [...DEFAULT_VOUCHER_TERMS_LINES],
  [
    'Use at the issuing store only.',
    'Pay with it at checkout by scanning the barcode or QR code.',
    'Can be used across several purchases until the balance is $0.00.',
    'Not redeemable for cash.',
  ],
  '代金券默认文案 4 行',
)
assert.deepEqual(
  [...DEFAULT_INSTALLMENT_TERMS_LINES],
  [
    'Order total: $50.00 minimum.',
    'First payment: $20.00 minimum.',
    'Each later payment: $5.00 minimum, or the remaining balance if it is lower.',
  ],
  '分期条款默认文案 3 行',
)
assert.equal(DEFAULT_VOUCHER_TERMS_TEXT, DEFAULT_VOUCHER_TERMS_LINES.join('\n'), '一行一条，用换行拼接')
assert.equal(DEFAULT_INSTALLMENT_TERMS_TEXT, DEFAULT_INSTALLMENT_TERMS_LINES.join('\n'))
assert.ok(Object.isFrozen(DEFAULT_VOUCHER_TERMS_LINES) && Object.isFrozen(DEFAULT_INSTALLMENT_TERMS_LINES), '默认文案不可被改写')

// 默认文案本身必须能通过两个字段的限制：不超长，且只含可打印 ASCII（不会触发控制字符拦截）。
for (const text of [DEFAULT_VOUCHER_TERMS_TEXT, DEFAULT_INSTALLMENT_TERMS_TEXT]) {
  assert.ok(text.length <= RECEIPT_TERMS_MAX_LENGTH, '默认文案不应超过字段上限')
  assert.ok(/^[\x20-\x7e\n]+$/.test(text), '默认文案只含可打印 ASCII 与换行')
}

// ── 漂移守卫：对照收银端共用包里的内置默认文案（手持 / iPad 引用同一份常量，WPF 与之逐字一致）──
const readSource = (path: string) => {
  try {
    return readFileSync(resolve('..', '..', path), 'utf8')
  } catch {
    assert.fail(`找不到收银端默认文案源文件 ${path}：文件被移动或改名了，请同步更新本测试与 Web 的默认文案常量`)
  }
}
const voucherSource = readSource('packages/pos-receipt-core/src/features/receipts/refund-voucher-terms.ts')
const installmentSource = readSource('packages/pos-receipt-core/src/features/receipts/installment-receipt-terms.ts')
for (const line of DEFAULT_VOUCHER_TERMS_LINES) {
  assert.ok(voucherSource.includes(JSON.stringify(line)), `收银端退款代金券默认文案里找不到这一行（逐字比较）: ${line}`)
}
for (const line of DEFAULT_INSTALLMENT_TERMS_LINES) {
  assert.ok(installmentSource.includes(JSON.stringify(line)), `收银端分期条款默认文案里找不到这一行（逐字比较）: ${line}`)
}

// ── 长度上限与 HBweb 后端下发前校验同值（后端超限会在下发时整批拦截）──
const guardSource = readSource('services/backend/BlazorApp.Api/Services/StoreReceiptProfiles/StoreReceiptProfileGuard.cs')
assert.equal(RECEIPT_TERMS_MAX_LENGTH, 600)
assert.match(guardSource, /MaxVoucherTermsLength = 600;/, '后端代金券使用说明上限应与 Web 一致')
assert.match(guardSource, /MaxInstallmentTermsLength = 600;/, '后端分期条款上限应与 Web 一致')

console.log('receiptTermsDefaults.test: ok')
