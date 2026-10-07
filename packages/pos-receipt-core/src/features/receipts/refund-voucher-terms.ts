import type { ReceiptTermsBlock } from "./receipt-document";

/**
 * 退款代金券券面底部的「券使用说明」文案，与 WPF 退款券券面的使用说明逐字一致。
 *
 * - 业主只审定过这份英文稿：无论小票 locale 是 en 还是 zh-CN 都原样打印，不做中文翻译。
 * - 手持与 iPad 的退款券渲染器（退货首次打印、取消分期后打印共用）必须引用这同一份常量，不要各自抄写。
 * - 条款句子较长，不在此手工拆行：由退款券渲染器按自己的纸宽（58mm=32 / 80mm=48 字符）在单词边界自动换行。
 * - 文案只含可打印 ASCII，因此不会带入可注入 ESC/POS 指令的控制字符。
 */
export const REFUND_VOUCHER_TERMS: ReceiptTermsBlock = Object.freeze({
  title: "VOUCHER TERMS",
  lines: Object.freeze([
    "Use at the issuing store only.",
    "Pay with it at checkout by scanning the barcode or QR code.",
    "Can be used across several purchases until the balance is $0.00.",
    "Not redeemable for cash.",
  ]),
});
