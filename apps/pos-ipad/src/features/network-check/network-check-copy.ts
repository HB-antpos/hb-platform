export const networkCheckEnglishCopy = {
  "eyebrow": "NETWORK CHECK",
  "title": "Network check",
  "subtitle":
    "Tests each server entry, the public internet and this iPad. Read-only: changes nothing.",
  "action.back": "Back",
  "action.start": "Start check",
  "action.restart": "Check again",
  "action.stop": "Stop",
  "action.share": "Share report",
  "share.failed": "Could not open sharing. The report is shown below.",
  "phase.idle": "Not started",
  "phase.basics": "Checking connections…",
  "phase.stability": "Checking stability…",
  "phase.burst": "Checking parallel requests…",
  "phase.done": "Finished in {{seconds}} s",
  "phase.cancelled": "Stopped",
  "summary.server": "Server",
  "summary.activeEntry": "App currently uses",
  "summary.loadingServer": "Reading server address…",
  "activeEntry.edge": "EdgeOne entry",
  "activeEntry.direct": "Direct entry (fallback in use)",
  "activeEntry.configured": "Configured address",
  "entry.edge": "EdgeOne",
  "entry.direct": "Direct",
  "entry.configured": "Configured address",
  "entry.edgeRole": "Preferred",
  "entry.directRole": "Fallback",
  "entry.configuredRole": "Only entry",
  "section.device": "This iPad and the internet",
  "section.entries": "Server entries",
  "section.report": "Report",
  "row.local": "Network",
  "row.reference": "Public internet",
  "row.publicIp": "Public IP",
  "row.clock": "Clock",
  "row.dns": "Public DNS",
  "row.first": "First request",
  "row.sequential": "One at a time",
  "row.burst": "All at once",
  "status.ok": "OK",
  "status.slow": "Slow",
  "status.failed": "Failed",
  "status.skipped": "Skipped",
  "status.pending": "Waiting",
  "detail.notChecked": "Not checked",
  "detail.dnsFailed": "Public DNS returned no address",
  "detail.dnsAddresses": "{{addresses}}",
  "detail.online": "Server reports online",
  "detail.sequential":
    "{{succeeded}}/{{attempts}} ok · avg {{avg}} · slowest {{max}} · jitter {{jitter}}",
  "detail.burst": "{{succeeded}}/{{attempts}} ok · slowest {{max}}",
  "detail.burstSkipped": "Skipped: no request got through one at a time",
  "detail.local": "{{type}} · iPad IP {{ip}}",
  "detail.localOffline": "{{type}} · not connected",
  "detail.localUnknown": "Network state unavailable",
  "detail.clockUnknown": "Server time unavailable",
  "detail.clockOff": "This iPad is {{offset}} off server time",
  "detail.clockOk": "Within {{seconds}} s of server time",
  "detail.publicIp": "{{ip}}",
  "detail.publicIpFailed": "Could not fetch",
  "detail.publicIpUnknown": "The server could not tell",
  "detail.publicIpNotApplicable": "Only checked for the HB server",
  "error.timeout": "No reply in time",
  "error.network": "Could not connect (DNS, connection or secure connection failed)",
  "error.edge": "EdgeOne error HTTP {{status}}: the edge could not reach the server",
  "error.http": "Server answered HTTP {{status}}",
  "error.notPosApi": "The reply did not come from the POS server",
  "verdict.idle.title": "Ready to check",
  "verdict.idle.advice": "The check takes about 10 to 30 seconds.",
  "verdict.running.title": "Checking…",
  "verdict.running.advice": "Keep this page open until the check finishes.",
  "verdict.cancelled.title": "Check stopped",
  "verdict.cancelled.advice": "Results measured so far are kept below.",
  "verdict.noLocalNetwork.title": "This iPad is not connected to a network",
  "verdict.noLocalNetwork.advice":
    "Check Wi-Fi in iPad Settings, or move closer to the router and check again.",
  "verdict.internetUnreachable.title": "No internet access",
  "verdict.internetUnreachable.advice":
    "The iPad is connected, but neither the server nor the public internet answers. Restart the router or check the store's internet service.",
  "verdict.notPosApi.title": "The reply is not from the POS server",
  "verdict.notPosApi.advice":
    "The network may need a sign-in page (guest Wi-Fi) or is intercepting requests. Switch to the store's own Wi-Fi.",
  "verdict.serverError.title": "The server answered with an error",
  "verdict.serverError.advice":
    "The network works; the server has a problem. Send this report to the administrator.",
  "verdict.serverUnreachable.title": "Cannot reach the HB server",
  "verdict.serverUnreachable.advice":
    "The public internet works, but no server entry answers. The server may be down, or the store's network or firewall blocks it. Send this report to the administrator.",
  "verdict.edgeDown.title": "EdgeOne entry is down; direct works",
  "verdict.edgeDown.advice":
    "The app switches to the direct entry automatically, so checkout keeps working but may be slower. Send this report to the administrator.",
  "verdict.directDown.title": "Direct entry is down; EdgeOne works",
  "verdict.directDown.advice":
    "Checkout is not affected because the app uses EdgeOne first, but there is no fallback if EdgeOne fails too.",
  "verdict.clockSkew.title": "The iPad clock is wrong",
  "verdict.clockSkew.advice":
    "Turn on Set Automatically in iPad Settings > General > Date & Time.",
  "verdict.linkUnstable.title": "This iPad's connection is unstable",
  "verdict.linkUnstable.advice":
    "Both the server and the public internet lose requests, so the problem is the Wi-Fi or the store's internet line. Move closer to the router, restart it, or use a wired network.",
  "verdict.entryLossy.title": "{{entry}} loses requests",
  "verdict.entryLossy.advice":
    "The public internet is stable, so requests are lost on the way to the server. Send this report to the administrator.",
  "verdict.burstLimited.title": "Parallel requests to {{entry}} fail",
  "verdict.burstLimited.advice":
    "One request at a time works, but several at once fail. The router or firewall may limit new connections.",
  "verdict.internetSlow.title": "The internet connection is slow",
  "verdict.internetSlow.advice":
    "Both the server and the public internet are slow. Check whether other devices use a lot of bandwidth, or restart the router.",
  "verdict.entrySlow.title": "{{entry}} is slow",
  "verdict.entrySlow.advice":
    "The public internet is fast, but requests to this entry are slow. Send this report to the administrator.",
  "verdict.referenceBlocked.title": "Server OK; public test address blocked",
  "verdict.referenceBlocked.advice":
    "The store network blocks 1.1.1.1. This does not affect checkout.",
  "verdict.allGood.title": "The network is working normally",
  "verdict.allGood.advice":
    "The server and the public internet respond normally.",
  "report.header": "HB POS network report",
  "report.time": "Time: {{value}}",
  "report.duration": "Duration: {{value}}",
  "report.device": "Device: {{value}}",
  "report.app": "App: {{value}}",
  "report.server": "Server: {{value}}",
  "report.activeEntry": "App currently uses: {{value}}",
  "report.verdict": "Finding: {{value}}",
  "report.section": "[{{name}}]",
  "report.row": "{{name}}: {{status}}{{ms}} · {{detail}}",
  "report.eoLogUuid": "EdgeOne log ID: {{value}}",
  "report.requestId": "Request ID: {{value}}",
  "report.stopped": "(Stopped before finishing)",
} as const;

export type NetworkCheckCopyKey = keyof typeof networkCheckEnglishCopy;

export const networkCheckChineseCopy = {
  "eyebrow": "网络检测",
  "title": "网络检测",
  "subtitle":
    "分别测试每条服务器入口、公共互联网和本机网络。只读检测，不修改任何设置。",
  "action.back": "返回",
  "action.start": "开始检测",
  "action.restart": "重新检测",
  "action.stop": "停止",
  "action.share": "分享报告",
  "share.failed": "无法打开分享，报告全文显示在下方。",
  "phase.idle": "尚未开始",
  "phase.basics": "正在检测连接…",
  "phase.stability": "正在检测稳定性…",
  "phase.burst": "正在检测同时请求…",
  "phase.done": "已完成，用时 {{seconds}} 秒",
  "phase.cancelled": "已停止",
  "summary.server": "服务器",
  "summary.activeEntry": "当前使用的线路",
  "summary.loadingServer": "正在读取服务器地址…",
  "activeEntry.edge": "走 EdgeOne 加速入口",
  "activeEntry.direct": "直连入口（正在备用）",
  "activeEntry.configured": "当前配置地址",
  "entry.edge": "EdgeOne",
  "entry.direct": "直连",
  "entry.configured": "当前地址",
  "entry.edgeRole": "首选",
  "entry.directRole": "备用",
  "entry.configuredRole": "唯一入口",
  "section.device": "本机与互联网",
  "section.entries": "服务器入口",
  "section.report": "报告",
  "row.local": "本机网络",
  "row.reference": "公共互联网",
  "row.publicIp": "公网地址",
  "row.clock": "时钟",
  "row.dns": "公共域名解析",
  "row.first": "首次请求",
  "row.sequential": "逐个请求",
  "row.burst": "同时请求",
  "status.ok": "正常",
  "status.slow": "偏慢",
  "status.failed": "失败",
  "status.skipped": "跳过",
  "status.pending": "等待",
  "detail.notChecked": "未检测",
  "detail.dnsFailed": "公共 DNS 没有返回地址",
  "detail.dnsAddresses": "{{addresses}}",
  "detail.online": "服务器在线",
  "detail.sequential":
    "成功 {{succeeded}}/{{attempts}} · 平均 {{avg}} · 最慢 {{max}} · 抖动 {{jitter}}",
  "detail.burst": "成功 {{succeeded}}/{{attempts}} · 最慢 {{max}}",
  "detail.burstSkipped": "跳过：逐个请求全部失败",
  "detail.local": "{{type}} · 本机地址 {{ip}}",
  "detail.localOffline": "{{type}} · 未连接",
  "detail.localUnknown": "读不到网络状态",
  "detail.clockUnknown": "没拿到服务器时间",
  "detail.clockOff": "本机时间与服务器相差 {{offset}}",
  "detail.clockOk": "与服务器相差 {{seconds}} 秒",
  "detail.publicIp": "{{ip}}",
  "detail.publicIpFailed": "没有取到",
  "detail.publicIpUnknown": "服务器未识别",
  "detail.publicIpNotApplicable": "只在连接 HB 服务器时检测",
  "error.timeout": "超时没有回应",
  "error.network": "连不上（域名解析、建立连接或安全连接失败）",
  "error.edge": "EdgeOne 报错 HTTP {{status}}：边缘节点连不上服务器",
  "error.http": "服务器返回 HTTP 错误 {{status}}",
  "error.notPosApi": "返回的不是 POS 服务器的内容",
  "verdict.idle.title": "可以开始检测",
  "verdict.idle.advice": "检测大约需要 10 到 30 秒。",
  "verdict.running.title": "正在检测…",
  "verdict.running.advice": "检测完成前请停留在本页。",
  "verdict.cancelled.title": "检测已停止",
  "verdict.cancelled.advice": "已测到的结果保留在下方。",
  "verdict.noLocalNetwork.title": "这台 iPad 没有连上网络",
  "verdict.noLocalNetwork.advice":
    "请在 iPad 设置里检查 Wi-Fi，或靠近路由器后重新检测。",
  "verdict.internetUnreachable.title": "上不了外网",
  "verdict.internetUnreachable.advice":
    "iPad 已连上网络，但服务器和公共互联网都没有回应。请重启路由器，或检查门店宽带。",
  "verdict.notPosApi.title": "返回的不是 POS 服务器的内容",
  "verdict.notPosApi.advice":
    "可能是需要网页登录的访客 Wi-Fi，或网络在拦截请求。请换到门店自己的 Wi-Fi。",
  "verdict.serverError.title": "服务器返回错误",
  "verdict.serverError.advice":
    "网络是通的，是服务器出了问题。请把这份报告发给管理员。",
  "verdict.serverUnreachable.title": "连不上 HB 服务器",
  "verdict.serverUnreachable.advice":
    "公共互联网正常，但所有服务器入口都没有回应：服务器可能故障，或门店网络 / 防火墙挡住了它。请把这份报告发给管理员。",
  "verdict.edgeDown.title": "加速入口 EdgeOne 不通，直连正常",
  "verdict.edgeDown.advice":
    "本机会自动改走直连，收银可以继续，但可能变慢。请把这份报告发给管理员。",
  "verdict.directDown.title": "直连入口不通，EdgeOne 正常",
  "verdict.directDown.advice":
    "本机优先走 EdgeOne，收银不受影响；但 EdgeOne 再出问题时就没有备用线路了。",
  "verdict.clockSkew.title": "iPad 时间不对",
  "verdict.clockSkew.advice":
    "请在 iPad「设置 > 通用 > 日期与时间」里打开「自动设置」。",
  "verdict.linkUnstable.title": "本机网络不稳定",
  "verdict.linkUnstable.advice":
    "服务器和公共互联网都有请求失败，问题在 Wi-Fi 或门店宽带。请靠近路由器、重启路由器，或改用有线网络。",
  "verdict.entryLossy.title": "{{entry}} 有请求失败",
  "verdict.entryLossy.advice":
    "公共互联网是稳定的，请求是在去服务器的路上丢的。请把这份报告发给管理员。",
  "verdict.burstLimited.title": "{{entry}} 同时请求会失败",
  "verdict.burstLimited.advice":
    "逐个请求正常，同时发多个就失败：路由器或防火墙可能限制了新连接数。",
  "verdict.internetSlow.title": "外网整体偏慢",
  "verdict.internetSlow.advice":
    "服务器和公共互联网都慢。请看看是否有其他设备占用大量带宽，或重启路由器。",
  "verdict.entrySlow.title": "{{entry}} 偏慢",
  "verdict.entrySlow.advice":
    "公共互联网很快，但到这条入口的请求慢。请把这份报告发给管理员。",
  "verdict.referenceBlocked.title": "服务器正常，公共测试地址被屏蔽",
  "verdict.referenceBlocked.advice":
    "门店网络屏蔽了 1.1.1.1，不影响收银。",
  "verdict.allGood.title": "网络正常",
  "verdict.allGood.advice": "服务器和公共互联网都响应正常。",
  "report.header": "HB POS 网络检测报告",
  "report.time": "时间：{{value}}",
  "report.duration": "用时：{{value}}",
  "report.device": "设备：{{value}}",
  "report.app": "App：{{value}}",
  "report.server": "服务器：{{value}}",
  "report.activeEntry": "当前使用的线路：{{value}}",
  "report.verdict": "结论：{{value}}",
  "report.section": "【{{name}}】",
  "report.row": "{{name}}：{{status}}{{ms}} · {{detail}}",
  "report.eoLogUuid": "加速入口 EdgeOne 日志编号：{{value}}",
  "report.requestId": "请求编号：{{value}}",
  "report.stopped": "（检测中途停止）",
} as const satisfies Record<NetworkCheckCopyKey, string>;

const networkCheckCopy = {
  en: networkCheckEnglishCopy,
  zh: networkCheckChineseCopy,
} as const;

export type NetworkCheckLocale = keyof typeof networkCheckCopy;

export function resolveNetworkCheckLocale(language?: string): NetworkCheckLocale {
  return language?.toLowerCase().startsWith("zh") ? "zh" : "en";
}

export function isNetworkCheckCopyKey(key: string): key is NetworkCheckCopyKey {
  return Object.prototype.hasOwnProperty.call(networkCheckEnglishCopy, key);
}

export function networkCheckText(
  locale: NetworkCheckLocale,
  key: NetworkCheckCopyKey,
  values?: Readonly<Record<string, string | number>>,
): string {
  const template: string = networkCheckCopy[locale][key];
  if (!values) return template;
  return template.replace(/\{\{(\w+)\}\}/g, (placeholder, name: string) => {
    const value = values[name];
    return value === undefined ? placeholder : String(value);
  });
}
