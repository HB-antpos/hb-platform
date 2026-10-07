import type { ReceiptTermsBlock } from "./receipt-document";

/**
 * 分期小票底部的「分期条款」文案，与 WPF 分期小票的条款块逐字一致。
 *
 * - 业主只审定过这份英文稿：无论小票 locale 是 en 还是 zh-CN 都原样打印，不做中文翻译。
 * - 只有「进行中」的分期补打才会传入（见两端 installment-receipt-reprint-service）；
 *   手持与 iPad 必须引用这同一份常量，不要各自抄写。
 * - 第三条较长，不在此手工拆行：由 buildSaleReceiptDocument 按纸宽（58mm=32 / 80mm=42 字符）自动换行。
 * - 这是「未定制」时的内置默认文案，必须保持逐字不变。总部在 Web 分店管理下发了自定义正文时，
 *   由分期补打服务经 resolveReceiptTermsBlock（receipt-terms-text.ts）换成「本块标题 + 自定义正文」。
 */
export const INSTALLMENT_RECEIPT_TERMS: ReceiptTermsBlock = Object.freeze({
  title: "INSTALLMENT TERMS",
  lines: Object.freeze([
    "Order total: $50.00 minimum.",
    "First payment: $20.00 minimum.",
    "Each later payment: $5.00 minimum, or the remaining balance if it is lower.",
  ]),
});
