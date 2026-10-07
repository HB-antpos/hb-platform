// 退款代金券「VOUCHER TERMS」与进行中分期小票「INSTALLMENT TERMS」的收银端内置默认文案。
// 仅用于分店表单 / 批量修改里的输入提示：总部看到「留空时收银端会印什么」，不参与任何保存、下发或比较逻辑
// （未定制 = 字段为空 = 收银端自己回落到默认文案，Web 不会把默认文案写进分店资料）。
// 必须与收银端现有默认文案逐字一致：packages/pos-receipt-core 的 refund-voucher-terms.ts / installment-receipt-terms.ts
// 与 WPF 对应实现；receiptTermsDefaults.test.ts 会核对 pos-receipt-core 两份源码，防止悄悄漂移。

/** 标题（VOUCHER TERMS / INSTALLMENT TERMS）由收银端固定打印，字段只是标题下面的正文。 */
export const DEFAULT_VOUCHER_TERMS_LINES: readonly string[] = Object.freeze([
  'Use at the issuing store only.',
  'Pay with it at checkout by scanning the barcode or QR code.',
  'Can be used across several purchases until the balance is $0.00.',
  'Not redeemable for cash.',
])

export const DEFAULT_INSTALLMENT_TERMS_LINES: readonly string[] = Object.freeze([
  'Order total: $50.00 minimum.',
  'First payment: $20.00 minimum.',
  'Each later payment: $5.00 minimum, or the remaining balance if it is lower.',
])

/** 一行一条，用换行拼成可直接放进 textarea placeholder 的文本。 */
export const DEFAULT_VOUCHER_TERMS_TEXT = DEFAULT_VOUCHER_TERMS_LINES.join('\n')
export const DEFAULT_INSTALLMENT_TERMS_TEXT = DEFAULT_INSTALLMENT_TERMS_LINES.join('\n')

/** 两个字段的长度上限（UTF-16 码元，JS `.length` 与后端 .NET `string.Length` 口径一致）；与 HBweb 下发前校验、收银端本机校验同值。 */
export const RECEIPT_TERMS_MAX_LENGTH = 600
