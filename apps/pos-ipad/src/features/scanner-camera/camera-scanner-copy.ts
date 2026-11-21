import type { ScannerCaptureContext } from "@/core/peripherals/scanner";

export const cameraScannerEnglishCopy = {
  "header.eyebrow": "Camera scan",
  "description": "Keep the barcode inside the frame. A successful scan returns to the current task immediately.",
  "preview.label": "Camera barcode preview",
  "action.closeLabel": "Close camera scanner",
  "action.cancel": "Cancel",
  "inline.header.eyebrow": "Continuous scan",
  "inline.action.closeLabel": "Close continuous camera scanner",
  "inline.status.ready": "Ready to scan",
  "inline.status.verifying": "Verifying barcode",
  "inline.status.submitted": "Barcode submitted",
  "inline.status.failed": "Unable to submit",
  "permission.denied.title": "Camera permission denied",
  "permission.denied.body": "Allow Camera in iPad Settings, then try again. Manual entry is not used as a substitute for scanning.",
  "unavailable.title": "Camera unavailable",
  "unavailable.body": "This device or Development Build has no camera scanning capability, so no barcode will be delivered.",
  "permission.required.title": "Camera permission required",
  "permission.required.body": "Camera access is used only while scanning.",
  "action.allowCamera": "Allow camera",
  "checking": "Checking camera",
  "starting": "Starting camera",
  "context.cashier-login": "Cashier sign-in",
  "context.dialog": "Dialog scan",
  "context.device-activation": "Device registration code",
  "context.scanner-test": "Scanner test",
  "context.emergency-qr": "Emergency QR",
  "context.product": "Product barcode",
  "context.product-search": "Product search",
  "context.supervisor-authorization": "Supervisor authorization",
} as const;

export type CameraScannerCopyKey = keyof typeof cameraScannerEnglishCopy;

const cameraScannerChineseCopy = {
  "header.eyebrow": "相机扫码",
  "description": "请将条码置于取景框内；成功后会立即返回当前操作。",
  "preview.label": "相机条码取景",
  "action.closeLabel": "关闭相机扫码",
  "action.cancel": "取消",
  "inline.header.eyebrow": "连续扫码",
  "inline.action.closeLabel": "关闭连续相机扫码",
  "inline.status.ready": "准备扫码",
  "inline.status.verifying": "正在核验",
  "inline.status.submitted": "条码已提交",
  "inline.status.failed": "未能提交",
  "permission.denied.title": "相机权限已被拒绝",
  "permission.denied.body": "请在 iPad 设置中允许相机后重试；不会使用手动输入替代扫码。",
  "unavailable.title": "相机不可用",
  "unavailable.body": "此设备或 Development Build 未提供相机扫码能力；为防止误入账，本次不会交付任何条码。",
  "permission.required.title": "需要相机权限",
  "permission.required.body": "相机只在本次扫码期间使用。",
  "action.allowCamera": "允许相机",
  "checking": "正在检查相机",
  "starting": "正在启动相机",
  "context.cashier-login": "收银员登录",
  "context.dialog": "对话框扫码",
  "context.device-activation": "设备注册码",
  "context.scanner-test": "扫码测试",
  "context.emergency-qr": "紧急二维码",
  "context.product": "商品条码",
  "context.product-search": "商品搜索",
  "context.supervisor-authorization": "主管授权",
} as const satisfies Record<CameraScannerCopyKey, string>;

const cameraScannerCopy = {
  en: cameraScannerEnglishCopy,
  zh: cameraScannerChineseCopy,
} as const;

export type CameraScannerLocale = keyof typeof cameraScannerCopy;

export function resolveCameraScannerLocale(language?: string): CameraScannerLocale {
  return language?.toLowerCase().startsWith("zh") ? "zh" : "en";
}

export function cameraScannerText(
  locale: CameraScannerLocale,
  key: CameraScannerCopyKey,
): string {
  return cameraScannerCopy[locale][key];
}

export function cameraScannerContextCopyKey(
  context: ScannerCaptureContext,
): CameraScannerCopyKey {
  return `context.${context}`;
}
