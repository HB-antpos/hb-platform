export const zellerProbeEnglishCopy = {
  "title": "Zeller terminal check",
  "subtitle": "Development tool. Every purchase here charges a real card.",
  "action.back": "Back",
  "action.share": "Share log",
  "action.initialise": "Check pairing",
  "action.setup": "Pair terminal",
  "action.purchase": "Charge {{amount}}",
  "action.cancel": "Cancel on terminal",
  "action.query": "Look up last charge",
  "action.refund": "Refund last charge",
  "action.clear": "Clear log",
  "section.sdk": "SDK",
  "section.pairing": "1. Pairing",
  "section.purchase": "2. Charge",
  "section.followUp": "3. Look up and refund",
  "section.log": "Log",
  "sdk.loading": "Loading the Zeller SDK…",
  "sdk.ready": "Loaded",
  "sdk.failed": "Could not load the Zeller SDK: {{reason}}",
  "sdk.unavailable": "This check is not available in production builds.",
  "amount.label": "Amount",
  "warning.realMoney":
    "Zeller has no sandbox. Use a small amount and refund it afterwards.",
  "hint.pairing":
    "Check pairing first. If it reports that setup is required, pair the terminal and sign in with the Zeller account.",
  "hint.followUp":
    "Look up uses the reference of the last charge. Refund is enabled once a charge was approved.",
  "state.busy": "Waiting for the terminal…",
  "state.idle": "Ready",
  "last.none": "No charge yet",
  "last.summary": "{{decision}} · {{reference}}",
  "log.empty": "Nothing yet. Start with “Check pairing”.",
  "share.failed": "Could not open sharing. Select the log text to copy it.",
} as const;

export type ZellerProbeCopyKey = keyof typeof zellerProbeEnglishCopy;

export const zellerProbeChineseCopy = {
  "title": "Zeller 刷卡机验证",
  "subtitle": "开发工具。这里的每一笔扣款都是真实扣款。",
  "action.back": "返回",
  "action.share": "分享日志",
  "action.initialise": "检查配对",
  "action.setup": "配对刷卡机",
  "action.purchase": "扣款 {{amount}}",
  "action.cancel": "在刷卡机上取消",
  "action.query": "查询上一笔",
  "action.refund": "退回上一笔",
  "action.clear": "清空日志",
  "section.sdk": "SDK",
  "section.pairing": "1. 配对",
  "section.purchase": "2. 扣款",
  "section.followUp": "3. 查询与退款",
  "section.log": "日志",
  "sdk.loading": "正在加载 Zeller SDK…",
  "sdk.ready": "已加载",
  "sdk.failed": "Zeller SDK 加载失败：{{reason}}",
  "sdk.unavailable": "正式版不提供这个验证页。",
  "amount.label": "金额",
  "warning.realMoney": "Zeller 没有测试环境。请用小金额，验证完退回。",
  "hint.pairing":
    "先点“检查配对”。如果提示需要设置，再点“配对刷卡机”，用 Zeller 账号登录。",
  "hint.followUp":
    "查询用的是上一笔扣款的 reference。扣款被批准后才能退款。",
  "state.busy": "等待刷卡机…",
  "state.idle": "就绪",
  "last.none": "还没有扣款",
  "last.summary": "{{decision}} · {{reference}}",
  "log.empty": "还没有记录。先点“检查配对”。",
  "share.failed": "打不开分享。可以长按选中日志文字后拷贝。",
} as const satisfies Record<ZellerProbeCopyKey, string>;

const zellerProbeCopy = {
  en: zellerProbeEnglishCopy,
  zh: zellerProbeChineseCopy,
} as const;

export type ZellerProbeLocale = keyof typeof zellerProbeCopy;

export function resolveZellerProbeLocale(language?: string): ZellerProbeLocale {
  return language?.toLowerCase().startsWith("zh") ? "zh" : "en";
}

export function zellerProbeText(
  locale: ZellerProbeLocale,
  key: ZellerProbeCopyKey,
  values?: Readonly<Record<string, string | number>>,
): string {
  const template: string = zellerProbeCopy[locale][key];
  if (!values) return template;
  return template.replace(/\{\{(\w+)\}\}/g, (placeholder, name: string) => {
    const value = values[name];
    return value === undefined ? placeholder : String(value);
  });
}
