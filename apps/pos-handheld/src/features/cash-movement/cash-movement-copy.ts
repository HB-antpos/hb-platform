/** 现金存入 / 取出（钱箱）弹窗文案：中英文成对，键与插值参数必须一致（test:locale-parity 核对）。 */
export const cashMovementEnglishCopy = {
  eyebrow: "CASH DRAWER",
  title: "Cash in / out",
  subtitle: "Record cash put into or taken out of the drawer.",
  "action.close": "Close",
  "action.refresh": "Refresh",
  "action.working": "Working…",
  "action.clear": "Clear",
  "action.backspace": "Delete",
  "action.reprint": "Reprint",
  "action.void": "Void",
  "action.confirmIn": "Confirm cash in {{amount}}",
  "action.confirmOut": "Confirm cash out {{amount}}",
  "tab.in": "Cash in",
  "tab.out": "Cash out",
  "tab.records": "Today",
  "form.reason": "Reason",
  "form.amount": "Amount",
  "form.quickAmounts": "Quick amounts",
  "form.note": "Note",
  "form.noteOptional": "Note (optional)",
  "form.noteRequiredLabel": "Note (required)",
  "form.notePlaceholder": "Up to 200 characters",
  "form.noteCounter": "{{count}} / {{max}}",
  "reason.ChangeTopUp": "Change top-up",
  "reason.OtherIn": "Other cash in",
  "reason.SafeDrop": "Safe drop",
  "reason.PaidOut": "Paid out",
  "reasonHint.ChangeTopUp": "Take change from the safe and put it in the drawer.",
  "reasonHint.OtherIn": "A note is required.",
  "reasonHint.SafeDrop":
    "The cash stays in the store and joins the cash pool after daily close.",
  "reasonHint.PaidOut":
    "A back-office cash expense is created automatically. A note is required.",
  "records.title": "Today's records",
  "records.summary": "Cash in {{in}} · Cash out {{out}}",
  "records.loading": "Loading today's records…",
  "records.empty": "No cash in or out recorded today.",
  "records.rejectedBanner":
    "{{count}} cash movements were rejected by the server. Daily close still includes them; contact the back office.",
  "record.voided": "Voided",
  "record.voids": "Voids {{number}}",
  "record.cashier": "Cashier: {{value}}",
  "record.cashierAuthorized": "Cashier: {{cashier}} / {{authorizer}}",
  "record.note": "Note: {{value}}",
  "direction.in": "Cash in",
  "direction.out": "Cash out",
  "direction.void": "Void",
  "upload.pending": "Pending upload",
  "upload.uploading": "Uploading",
  "upload.synced": "Uploaded",
  "upload.rejected": "Rejected",
  "void.title": "Void {{number}}",
  "void.hint": "Enter why this record is being voided (at least 2 characters).",
  "void.notePlaceholder": "Void reason",
  "void.confirm": "Confirm void",
  "void.cancel": "Cancel",
  "voidDenied.IS_VOID_RECORD": "A void record cannot be voided.",
  "voidDenied.NOT_TODAY": "Only today's records can be voided.",
  "voidDenied.ALREADY_VOIDED": "This record is already voided.",
  "voidDenied.DAILY_CLOSE_SAVED":
    "A daily close was saved after this record, so it can no longer be voided.",
  "status.reason-required": "Select a reason.",
  "status.amount-invalid": "Enter an amount greater than 0 (up to 2 decimals).",
  "status.amount-too-large": "The amount exceeds the limit.",
  "status.note-required": "This reason needs a note of at least 2 characters.",
  "status.note-too-long": "The note is too long (200 characters at most).",
  "status.void-note-required": "Enter a void reason of at least 2 characters.",
  "status.saved": "Saved.",
  "status.saved-drawer-failed":
    "Saved, but the cash drawer did not open. Open it with the Open drawer button.",
  "status.saved-print-failed":
    "Saved, but the slip did not print. Reprint it from Today.",
  "status.saved-drawer-print-failed":
    "Saved, but the drawer did not open and the slip did not print. Reprint it from Today.",
  "status.save-failed": "Could not save. Nothing was recorded; try again.",
  "status.authorization-denied":
    "Supervisor approval was not granted. Nothing was recorded.",
  "status.authorization-cancelled": "Approval cancelled. Nothing was recorded.",
  "status.void-saved": "Voided.",
  "status.void-saved-print-failed":
    "Voided, but the slip did not print. Reprint it from Today.",
  "status.void-saved-drawer-failed":
    "Voided, but the cash drawer did not open. Open it with the Open drawer button.",
  "status.void-saved-drawer-print-failed":
    "Voided, but the drawer did not open and the slip did not print. Reprint it from Today.",
  "status.void-failed": "Could not void. Nothing was changed; try again.",
  "status.void-not-allowed": "This record can no longer be voided.",
  "status.reprint-printed": "Slip sent to the printer.",
  "status.reprint-failed": "The slip could not be printed.",
  "status.load-failed": "Today's records could not be loaded. Try again.",
} as const;

export type CashMovementCopyKey = keyof typeof cashMovementEnglishCopy;

const cashMovementChineseCopy = {
  eyebrow: "钱箱",
  title: "现金存入 / 取出",
  subtitle: "记录放进钱箱或从钱箱拿走的现金。",
  "action.close": "关闭",
  "action.refresh": "刷新",
  "action.working": "处理中…",
  "action.clear": "清除",
  "action.backspace": "删除",
  "action.reprint": "补打",
  "action.void": "冲正",
  "action.confirmIn": "确认存入 {{amount}}",
  "action.confirmOut": "确认取出 {{amount}}",
  "tab.in": "存入",
  "tab.out": "取出",
  "tab.records": "今日记录",
  "form.reason": "原因",
  "form.amount": "金额",
  "form.quickAmounts": "快捷金额",
  "form.note": "备注",
  "form.noteOptional": "备注（选填）",
  "form.noteRequiredLabel": "备注（必填）",
  "form.notePlaceholder": "最多 200 字",
  "form.noteCounter": "{{count}} / {{max}}",
  "reason.ChangeTopUp": "补零钱",
  "reason.OtherIn": "其他存入",
  "reason.SafeDrop": "交店长 / 入保险柜",
  "reason.PaidOut": "小额现金支出",
  "reasonHint.ChangeTopUp": "从保险柜拿零钱放进钱箱。",
  "reasonHint.OtherIn": "必须写备注。",
  "reasonHint.SafeDrop": "钱还在店里，日结后入现金池。",
  "reasonHint.PaidOut": "会自动生成后台「现金支出」，必须写备注。",
  "records.title": "今日记录",
  "records.summary": "存入 {{in}} · 取出 {{out}}",
  "records.loading": "正在读取今日记录…",
  "records.empty": "今天还没有现金存入或取出记录。",
  "records.rejectedBanner":
    "有 {{count}} 笔现金进出被服务端拒绝；日结仍包含它们，请联系后台。",
  "record.voided": "已冲正",
  "record.voids": "冲正 {{number}}",
  "record.cashier": "收银员：{{value}}",
  "record.cashierAuthorized": "收银员：{{cashier}} / {{authorizer}}",
  "record.note": "备注：{{value}}",
  "direction.in": "存入",
  "direction.out": "取出",
  "direction.void": "冲正",
  "upload.pending": "待上传",
  "upload.uploading": "上传中",
  "upload.synced": "已上传",
  "upload.rejected": "被拒",
  "void.title": "冲正 {{number}}",
  "void.hint": "请写明冲正原因，至少 2 个字。",
  "void.notePlaceholder": "冲正原因",
  "void.confirm": "确认冲正",
  "void.cancel": "取消",
  "voidDenied.IS_VOID_RECORD": "冲正记录不能再冲正。",
  "voidDenied.NOT_TODAY": "只能冲正今天的记录。",
  "voidDenied.ALREADY_VOIDED": "这条记录已冲正。",
  "voidDenied.DAILY_CLOSE_SAVED": "这条记录之后已保存日结，不能再冲正。",
  "status.reason-required": "请选择原因。",
  "status.amount-invalid": "请输入大于 0 的金额（最多两位小数）。",
  "status.amount-too-large": "金额超出上限。",
  "status.note-required": "这个原因必须写备注，至少 2 个字。",
  "status.note-too-long": "备注过长（最多 200 字）。",
  "status.void-note-required": "请写冲正原因，至少 2 个字。",
  "status.saved": "已保存。",
  "status.saved-drawer-failed": "已保存，但钱箱没有打开，可点「开箱」手动打开。",
  "status.saved-print-failed": "已保存，但凭条没有打印出来，可在今日记录里补打。",
  "status.saved-drawer-print-failed":
    "已保存，但钱箱没有打开、凭条也没有打印，可在今日记录里补打。",
  "status.save-failed": "保存失败，没有记录任何内容，请重试。",
  "status.authorization-denied": "没有获得主管授权，没有记录任何内容。",
  "status.authorization-cancelled": "授权已取消，没有记录任何内容。",
  "status.void-saved": "已冲正。",
  "status.void-saved-print-failed": "已冲正，但凭条没有打印出来，可在今日记录里补打。",
  "status.void-saved-drawer-failed": "已冲正，但钱箱没有打开，可点「开箱」手动打开。",
  "status.void-saved-drawer-print-failed":
    "已冲正，但钱箱没有打开、凭条也没有打印，可在今日记录里补打。",
  "status.void-failed": "冲正失败，没有改动任何内容，请重试。",
  "status.void-not-allowed": "这条记录已不能再冲正。",
  "status.reprint-printed": "凭条已发送到打印机。",
  "status.reprint-failed": "凭条打印失败。",
  "status.load-failed": "今日记录读取失败，请重试。",
} as const satisfies Record<CashMovementCopyKey, string>;

const cashMovementCopy = {
  en: cashMovementEnglishCopy,
  zh: cashMovementChineseCopy,
} as const;

export type CashMovementLocale = keyof typeof cashMovementCopy;

export function resolveCashMovementLocale(language?: string): CashMovementLocale {
  return language?.toLowerCase().startsWith("zh") ? "zh" : "en";
}

export function cashMovementText(
  locale: CashMovementLocale,
  key: CashMovementCopyKey,
  values?: Readonly<Record<string, string | number>>,
): string {
  const template: string = cashMovementCopy[locale][key];
  if (!values) return template;
  return template.replace(/\{\{(\w+)\}\}/g, (placeholder, name: string) => {
    const value = values[name];
    return value === undefined ? placeholder : String(value);
  });
}
