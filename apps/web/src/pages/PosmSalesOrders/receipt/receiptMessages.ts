/**
 * 在线小票的页面级文案：随订单页代码块懒注册，不进入首屏 i18n 包（首屏体积预算已接近上限）。
 * 键都在 posmReceipt 下；t() 调用处同时带中文默认值，注册失败时仍能显示。
 */
export const posmReceiptMessages = {
  zh: {
    posmReceipt: {
      tabReceipt: '小票',
      tabItems: '商品明细',
      loadFailed: '小票加载失败',
      retry: '重试',
      unsupported: '该订单状态暂不支持在线小票',
      openQr: '在线二维码',
      print: '打印小票',
      popoverTitle: '在线小票',
      popoverHint: '客人扫码即可查看，无需登录',
      creating: '正在生成链接…',
      createFailed: '生成链接失败，请重试',
      copyLink: '复制链接',
      copied: '链接已复制',
      copyFailed: '复制失败，请手动选择链接',
      downloadQr: '下载二维码',
      regenerate: '重新生成',
      regenerateConfirm: '重新生成后旧链接立即失效，确定吗？',
      regenerated: '已重新生成，旧链接已失效',
      meta: '有效至 {{date}} · 已打开 {{count}} 次',
    },
  },
  en: {
    posmReceipt: {
      tabReceipt: 'Receipt',
      tabItems: 'Items',
      loadFailed: 'Failed to load the receipt',
      retry: 'Retry',
      unsupported: 'Online receipts are not available for this order status',
      openQr: 'Online QR',
      print: 'Print receipt',
      popoverTitle: 'Online receipt',
      popoverHint: 'Customers scan to view it — no login needed',
      creating: 'Creating link…',
      createFailed: 'Could not create the link. Please retry.',
      copyLink: 'Copy link',
      copied: 'Link copied',
      copyFailed: 'Copy failed. Please select the link manually.',
      downloadQr: 'Download QR',
      regenerate: 'Regenerate',
      regenerateConfirm: 'The old link stops working immediately. Continue?',
      regenerated: 'Regenerated. The old link no longer works.',
      meta: 'Valid until {{date}} · opened {{count}} times',
    },
  },
}
