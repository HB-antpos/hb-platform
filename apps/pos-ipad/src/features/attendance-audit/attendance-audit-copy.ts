import type {
  AttendanceAuditPresenterState,
} from "./attendance-audit-presenter";
import type { OperationAuditUploadState } from "@hb/pos-domain/features/attendance-audit/operation-audit-presenter";

export const attendanceAuditEnglishCopy = {
  "header.eyebrow": "Store security",
  "header.title": "Attendance & audit",
  "header.subtitle": "Short-lived QR codes and this device's audit trail. Keys, tokens, and payment references stay hidden.",
  "action.back": "Back to sales",
  "qr.kicker": "Attendance",
  "panel.title": "Staff clock-in",
  "panel.hint": "Open the staff app → Clock in, then scan this code. Same code for starting and finishing a shift.",
  "panel.countdownHint": "The number in the middle is the seconds left; a new code appears automatically (one every 15 sec).",
  "panel.online": "Verified online",
  "panel.offline": "Signed offline",
  "panel.device": "POS device {{device}}",
  "panel.rotate": "New code every 15 sec",
  "panel.collapseNote": "Once the first item is scanned, this code moves to \"Clock in\" in the left rail. Tap it any time to enlarge.",
  "panel.loginHint": "No cashier sign-in needed. Open the staff app → Clock in and scan the code below. Same code for starting and finishing a shift.",
  "panel.railLabel": "Clock in",
  "panel.railHint": "Shows the staff clock-in QR code",
  "panel.close": "Close (or tap outside)",
  "panel.closeShort": "Close",
  "panel.seconds": "{{seconds}} seconds left",
  "panel.state.preparing.title": "Preparing the code…",
  "panel.state.preparing.body": "This only takes a moment.",
  "panel.state.enable-online.title": "Connect to enable",
  "panel.state.enable-online.body": "First use (or a new device key) must be registered online. The code appears by itself once connected.",
  "panel.state.clock-rollback.title": "Clock changed — re-sync needed",
  "panel.state.clock-rollback.body": "This iPad's clock was set back, so the code is locked. It unlocks after the next online time sync.",
  "panel.state.setup-failed.title": "Setup failed — retrying",
  "panel.state.setup-failed.body": "Check the network connection. It retries automatically every 15 seconds.",
  "qr.title": "Attendance QR",
  "qr.online": "Verified online",
  "qr.offline": "Signed offline",
  "qr.clockRollback.title": "Clock rollback",
  "qr.clockRollback.body": "The QR code is locked. Re-sync trusted time online before using it again.",
  "qr.imageLabel": "Attendance QR code",
  "qr.preparing": "Preparing secure QR…",
  "qr.unavailable": "QR unavailable",
  "qr.placeholder": "Online registration is required for first use or re-keying.",
  "qr.remaining": "{{seconds}} sec",
  "qr.remainingLabel": "Current QR validity",
  "context.store": "Store",
  "context.device": "Device",
  "action.refreshQr": "Secure refresh",
  "audit.kicker": "Operation audit",
  "audit.title": "Audit trail",
  "audit.permission.title": "Audit trail protected",
  "audit.permission.body": "The current cashier does not have Permissions.PosTerminal.Audit.View. Attendance QR remains available; sign in again after supervisor approval.",
  "audit.resultCount": "{{count}} records",
  "audit.resultCountOne": "{{count}} record",
  "audit.source.local": "Local",
  "audit.source.remote": "Remote",
  "audit.upload.all": "All",
  "audit.upload.pending": "Pending",
  "audit.upload.uploaded": "Uploaded",
  "audit.upload.rejected": "Rejected",
  "audit.searchLabel": "Search operation audit",
  "audit.searchPlaceholder": "Receipt, order, operation",
  "audit.action.search": "Search",
  "audit.loading": "Loading audit records…",
  "audit.records": "Records",
  "audit.row.event": "Event",
  "audit.empty.title": "No audit records",
  "audit.empty.body": "Change source or filters, then search again.",
  "audit.details": "Details",
  "audit.detail.empty.title": "Select a record",
  "audit.detail.empty.body": "Only validated and redacted fields are shown.",
  "audit.detail.operation": "Operation",
  "audit.detail.outcome": "Outcome",
  "audit.detail.time": "Time",
  "audit.detail.cashier": "Cashier",
  "audit.detail.receipt": "Receipt",
  "audit.detail.order": "Order",
  "audit.detail.amount": "Amount",
  "audit.detail.quantity": "Qty",
  "audit.detail.correlation": "Correlation",
  "audit.detail.safeMessage": "Safe message",
  "audit.detail.itemChanges": "Item changes",
  "unavailable.title": "Secure services unavailable",
  "unavailable.body": "The native composition root has not supplied secure key, trusted-time, or audit storage services. No insecure fallback is used.",
  "qrStatus.clock-rollback": "Trusted time locked",
  "qrStatus.enable-online": "Online setup required",
  "qrStatus.offline-signed": "Signed locally with the registered device key",
  "qrStatus.online-verified": "Device identity and trusted time verified online",
  "qrStatus.setup-failed": "Secure QR setup failed; check connectivity and retry",
  "auditStatus.details-failed": "Details failed; no partial result is shown.",
  "auditStatus.details-unavailable": "Record is unavailable.",
  "auditStatus.list-failed": "Audit load failed; no partial list is shown.",
  "auditStatus.online-required": "Remote audit requires connectivity; local audit remains available.",
  "auditStatus.permission-required": "Audit.View permission is required.",
} as const;

export type AttendanceAuditCopyKey = keyof typeof attendanceAuditEnglishCopy;

const attendanceAuditChineseCopy = {
  "header.eyebrow": "门店安全",
  "header.title": "考勤与审计",
  "header.subtitle": "显示短时二维码和本设备操作轨迹；密钥、token 和支付引用不会显示。",
  "action.back": "返回销售",
  "qr.kicker": "考勤",
  "panel.title": "员工打卡",
  "panel.hint": "打开员工 App → 打卡，对准这个码扫一下。上班、下班都扫这一个。",
  "panel.countdownHint": "码中间是剩余秒数，到时自动换新码（每 15 秒一张）。",
  "panel.online": "在线验证",
  "panel.offline": "离线签名",
  "panel.device": "POS 设备 {{device}}",
  "panel.rotate": "每 15 秒换新码",
  "panel.collapseNote": "扫入第一件商品后，这个码收进左侧栏「考勤」，随时点开放大。",
  "panel.loginHint": "不用登录收银。打开员工 App → 打卡，对准下面的码扫一下，上班、下班都扫这一个。",
  "panel.railLabel": "考勤",
  "panel.railHint": "显示员工打卡二维码",
  "panel.close": "关闭（点空白处也可关闭）",
  "panel.closeShort": "关闭",
  "panel.seconds": "剩余 {{seconds}} 秒",
  "panel.state.preparing.title": "正在准备考勤码…",
  "panel.state.preparing.body": "稍等片刻即可显示。",
  "panel.state.enable-online.title": "请先联网启用",
  "panel.state.enable-online.body": "首次启用或安全身份失效时必须联网登记，联网后自动出码。",
  "panel.state.clock-rollback.title": "时钟变化，需要校时",
  "panel.state.clock-rollback.body": "本机时间被调回过，码已锁定；下次联网校时后自动恢复。",
  "panel.state.setup-failed.title": "启用失败，正在自动重试",
  "panel.state.setup-failed.body": "请检查网络，每 15 秒自动重试一次。",
  "qr.title": "考勤二维码",
  "qr.online": "已在线验证",
  "qr.offline": "已离线签发",
  "qr.clockRollback.title": "时钟回拨",
  "qr.clockRollback.body": "二维码已锁定。请在线重新同步可信时间后再使用。",
  "qr.imageLabel": "考勤二维码",
  "qr.preparing": "正在建立安全二维码…",
  "qr.unavailable": "二维码暂不可用",
  "qr.placeholder": "首次启用或安全身份失效时必须联网登记。",
  "qr.remaining": "{{seconds}} 秒",
  "qr.remainingLabel": "当前二维码剩余有效期",
  "context.store": "门店",
  "context.device": "设备",
  "action.refreshQr": "安全刷新",
  "audit.kicker": "操作审计",
  "audit.title": "操作审计",
  "audit.permission.title": "审计记录受保护",
  "audit.permission.body": "当前收银员没有 Permissions.PosTerminal.Audit.View。考勤二维码仍可正常使用；请由主管授权后重新登录。",
  "audit.resultCount": "{{count}} 项",
  "audit.resultCountOne": "{{count}} 项",
  "audit.source.local": "本机",
  "audit.source.remote": "远程",
  "audit.upload.all": "全部",
  "audit.upload.pending": "待传",
  "audit.upload.uploaded": "已传",
  "audit.upload.rejected": "拒绝",
  "audit.searchLabel": "搜索操作审计",
  "audit.searchPlaceholder": "小票、订单、操作",
  "audit.action.search": "查询",
  "audit.loading": "正在读取审计记录…",
  "audit.records": "记录",
  "audit.row.event": "事件",
  "audit.empty.title": "暂无记录",
  "audit.empty.body": "更改来源或筛选后重新查询。",
  "audit.details": "详情",
  "audit.detail.empty.title": "选择一条记录",
  "audit.detail.empty.body": "这里只显示已校验并脱敏的字段。",
  "audit.detail.operation": "操作",
  "audit.detail.outcome": "结果",
  "audit.detail.time": "时间",
  "audit.detail.cashier": "收银员",
  "audit.detail.receipt": "小票",
  "audit.detail.order": "订单",
  "audit.detail.amount": "金额",
  "audit.detail.quantity": "数量",
  "audit.detail.correlation": "关联",
  "audit.detail.safeMessage": "安全消息",
  "audit.detail.itemChanges": "商品变化",
  "unavailable.title": "安全服务尚未接线",
  "unavailable.body": "考勤密钥、可信时间或审计仓储未由原生组合根提供。页面保持关闭，不会用临时存储降级。",
  "qrStatus.clock-rollback": "可信时间已锁定",
  "qrStatus.enable-online": "首次启用需联网",
  "qrStatus.offline-signed": "使用已登记本机密钥离线签发",
  "qrStatus.online-verified": "设备身份与可信时间已在线验证",
  "qrStatus.setup-failed": "安全二维码建立失败，请检查网络后重试",
  "auditStatus.details-failed": "详情读取失败；未显示部分结果。",
  "auditStatus.details-unavailable": "记录已不存在或不可访问。",
  "auditStatus.list-failed": "审计读取失败；未显示可能误导的部分列表。",
  "auditStatus.online-required": "远程审计必须联网；本机审计仍可读取。",
  "auditStatus.permission-required": "当前收银员无审计查看权限。",
} as const satisfies Record<AttendanceAuditCopyKey, string>;

const attendanceAuditCopy = {
  en: attendanceAuditEnglishCopy,
  zh: attendanceAuditChineseCopy,
} as const;

export type AttendanceAuditLocale = keyof typeof attendanceAuditCopy;

export function resolveAttendanceAuditLocale(language?: string): AttendanceAuditLocale {
  return language?.toLowerCase().startsWith("zh") ? "zh" : "en";
}

export function attendanceAuditText(
  locale: AttendanceAuditLocale,
  key: AttendanceAuditCopyKey,
  values?: Readonly<Record<string, string | number>>,
): string {
  const template = attendanceAuditCopy[locale][key];
  if (!values) return template;
  return template.replace(/\{\{(\w+)\}\}/g, (placeholder, name: string) => {
    const value = values[name];
    return value === undefined ? placeholder : String(value);
  });
}

export function attendanceAuditQrStatusCopyKey(
  statusCode: AttendanceAuditPresenterState["qr"]["statusCode"],
): AttendanceAuditCopyKey {
  return `qrStatus.${statusCode}`;
}

export function attendanceAuditStatusCopyKey(
  statusCode: NonNullable<AttendanceAuditPresenterState["audit"]["statusCode"]>,
): AttendanceAuditCopyKey {
  return `auditStatus.${statusCode}`;
}

export function attendanceAuditUploadStateCopyKey(
  state: OperationAuditUploadState,
): AttendanceAuditCopyKey {
  return `audit.upload.${state}`;
}
