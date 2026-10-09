import type { OperationAuthorizationFailureReason } from "./operation-authorization-service";

export type OperationAuthorizationLocale = "en" | "zh";

export type OperationAuthorizationCopyKey =
  | "eyebrow"
  | "title"
  | "description"
  | "requestedAction"
  | "paymentRecoveryPaid"
  | "paymentRecoveryUnpaid"
  | "paymentRecoveryUncertain"
  | "paymentTenderVoidUnreleased"
  | "returnResolveUnknownRefund"
  | "inputLabel"
  | "inputHint"
  | "keyboard"
  | "privacy"
  | "cancel"
  | "submit"
  | "verifying"
  | "barcodeRequired"
  | "authenticationFailed"
  | "permissionDenied"
  | "identityMismatch"
  | "ticketInvalid"
  | "validationFailed";

const COPY: Readonly<
  Record<OperationAuthorizationLocale, Readonly<Record<OperationAuthorizationCopyKey, string>>>
> = {
  en: {
    eyebrow: "SUPERVISOR APPROVAL",
    title: "Approval required",
    description:
      "Scan an authorized supervisor barcode to continue this operation.",
    requestedAction: "Requested action: {{action}}",
    paymentRecoveryPaid: "Manually confirm card payment received",
    paymentRecoveryUnpaid: "Manually confirm no card payment",
    paymentRecoveryUncertain: "Keep card payment awaiting verification",
    paymentTenderVoidUnreleased: "Void blocked gift card tender",
    returnResolveUnknownRefund: "Resolve an unknown card refund",
    inputLabel: "Supervisor barcode",
    inputHint: "Scan supervisor barcode",
    keyboard: "Keyboard",
    privacy:
      "The barcode is masked, used only for this approval, and cleared immediately after submission.",
    cancel: "Cancel",
    submit: "Verify supervisor",
    verifying: "Verifying…",
    barcodeRequired: "Scan a supervisor barcode to continue.",
    authenticationFailed:
      "The barcode was not accepted. Check it and scan again.",
    permissionDenied:
      "This supervisor cannot approve the requested operation.",
    identityMismatch:
      "The supervisor must be authorized for this store and iPad.",
    ticketInvalid:
      "The supervisor authorization is not currently valid. Scan another authorized barcode.",
    validationFailed:
      "Approval could not be verified safely. Please scan again or cancel.",
  },
  zh: {
    eyebrow: "主管授权",
    title: "此操作需要主管批准",
    description: "请扫描具有相应权限的主管条码后继续。",
    requestedAction: "申请操作：{{action}}",
    paymentRecoveryPaid: "人工确认刷卡已收款",
    paymentRecoveryUnpaid: "人工确认刷卡未扣款",
    paymentRecoveryUncertain: "保留刷卡待核实状态",
    paymentTenderVoidUnreleased: "作废已阻断的礼品卡付款",
    returnResolveUnknownRefund: "主管结案：退款结果未知",
    inputLabel: "主管条码",
    inputHint: "扫描主管条码",
    keyboard: "键盘",
    privacy: "条码会被遮罩，仅用于本次授权，并在提交后立即清除。",
    cancel: "取消",
    submit: "核验主管",
    verifying: "正在核验…",
    barcodeRequired: "请扫描主管条码后继续。",
    authenticationFailed: "条码未获接受，请检查后重新扫描。",
    permissionDenied: "该主管没有批准此操作的权限。",
    identityMismatch: "主管必须已获当前门店和这台 iPad 的授权。",
    ticketInvalid: "主管授权当前无效，请扫描其他已授权条码。",
    validationFailed: "无法安全核验此次授权，请重新扫描或取消。",
  },
};

export function resolveOperationAuthorizationLocale(
  locale: string | null | undefined,
): OperationAuthorizationLocale {
  return locale?.toLowerCase().startsWith("zh") ? "zh" : "en";
}

export function operationAuthorizationText(
  locale: OperationAuthorizationLocale,
  key: OperationAuthorizationCopyKey,
  values?: Readonly<Record<string, string | number>>,
): string {
  const template = COPY[locale][key];
  if (!values) return template;
  return Object.entries(values).reduce(
    (text, [name, value]) => text.replaceAll(`{{${name}}}`, String(value)),
    template,
  );
}

export function operationAuthorizationActionCopyKey(
  action: string,
): OperationAuthorizationCopyKey | null {
  switch (action) {
    case "payment-recovery-paid":
      return "paymentRecoveryPaid";
    case "payment-recovery-unpaid":
      return "paymentRecoveryUnpaid";
    case "payment-recovery-uncertain":
      return "paymentRecoveryUncertain";
    case "payment-tender-void-unreleased":
      return "paymentTenderVoidUnreleased";
    case "resolve-unknown-refund":
      return "returnResolveUnknownRefund";
    default:
      return null;
  }
}

export function operationAuthorizationFailureCopyKey(
  reason: OperationAuthorizationFailureReason,
): OperationAuthorizationCopyKey {
  switch (reason) {
    case "AUTHENTICATION_FAILED":
      return "authenticationFailed";
    case "PERMISSION_DENIED":
    case "EMERGENCY_OVERRIDE_DENIED":
      return "permissionDenied";
    case "STORE_OR_DEVICE_MISMATCH":
    case "AUTHORIZER_IDENTITY_INVALID":
      return "identityMismatch";
    case "AUTHORIZATION_TICKET_INVALID":
      return "ticketInvalid";
    case "AUTHORIZATION_VALIDATION_FAILED":
    case "NO_ACTIVE_CASHIER":
    case "ACTION_ID_CONFLICT":
    case "ANOTHER_AUTHORIZATION_PENDING":
    case "CANCELLED":
    case "REVOKED":
      return "validationFailed";
  }
}
