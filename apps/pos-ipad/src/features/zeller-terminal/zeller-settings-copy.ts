export const zellerSettingsEnglishCopy = {
  "tab.environmentNote": "Zeller has a live environment only, no sandbox",
  "card.eyebrow": "CARD TERMINAL",
  "card.intro":
    "This iPad connects to the Zeller terminal directly. Pairing is stored on this iPad only.",
  "pill.loading": "Loading Zeller…",
  "pill.loadFailed": "Zeller could not be loaded",
  "pill.checking": "Checking pairing…",
  "pill.pairing": "Pairing",
  "pill.unpaired": "Not paired yet",
  "pill.paired": "Paired",
  "pill.offline": "Cannot reach Zeller",
  "copy.loadFailed": "The Zeller component failed to load: {{reason}}",
  "copy.checking": "Asking Zeller whether this iPad is paired…",
  "copy.pairing": "Sign in and choose the terminal in the Zeller window that opened.",
  "copy.paired": "Paired with a Zeller terminal. Not enabled yet; card payments still use {{provider}}.",
  "copy.pairedNoProvider": "Paired with a Zeller terminal. Not enabled yet.",
  "copy.active": "Card payments on this iPad go through the Zeller terminal.",
  "copy.offline":
    "The pairing status could not be read this time. Check the network and try again. The enabled card terminal has not changed.",
  "row.pairing": "Pairing",
  "row.lastCheck": "Last check",
  "row.activeProvider": "This iPad uses",
  "value.unpaired": "Not paired",
  "value.paired": "Paired",
  "value.pairingInProgress": "Pairing…",
  "value.checking": "Checking…",
  "value.unknown": "Unknown",
  "value.unknownWasPaired": "Unknown (last: paired)",
  "value.neverChecked": "Not checked yet",
  "value.checkedAt": "Today {{time}}",
  "value.checkFailedAt": "Today {{time}}, failed ({{reason}})",
  "value.noProvider": "None selected",
  "action.check": "Check pairing",
  "action.pair": "Pair terminal",
  "action.repair": "Pair again",
  "action.retry": "Try again",
  "action.networkCheck": "Network check",
  "action.enable": "Enable Zeller",
  "enable.lockedTitle": "Enable Zeller",
  "enable.lockedHint": "Available after pairing succeeds.",
  "enable.readyTitle": "Switch this iPad's card payments to Zeller",
  "enable.readyHint": "The {{provider}} settings are kept, so you can switch back at any time.",
  "enable.readyHintNoProvider": "Square and Linkly settings are kept, so you can switch back at any time.",
  "enable.notReadyHint":
    "Zeller payments are not wired into checkout on this iPad yet. You can finish pairing now; enabling comes later.",
  "enable.activeTitle": "Zeller is enabled",
  "enable.activeHint": "To use another terminal, enable it on the Square or Linkly tab.",
  "offline.title": "Card payments may be unavailable for now",
  "offline.hint": "If you need to take payment urgently, turn on “Manual card” under payment methods above.",
  "steps.eyebrow": "PAIRING STEPS",
  "steps.title": "First-time setup",
  "steps.one": "Sign the terminal in to the Zeller account and make sure it is online.",
  "steps.two":
    "Tap “Pair terminal”, sign in to the same account in the Zeller window, and choose the terminal for this iPad.",
  "steps.three":
    "Once it shows “Paired”, tap “Enable Zeller”. Each iPad is paired once; restarts and updates keep the pairing.",
  "footer.note":
    "Pair first, then enable. Square and Linkly settings stay as they are when you switch back. Keep all terminals of one store under the same Zeller Site, otherwise a refund from another terminal may not find the original payment.",
  "hideUi.title": "Hide the Zeller window when taking payment",
  "hideUi.hint":
    "Card payments run on the terminal without covering this screen. Follow the prompts on the terminal and use “Cancel payment” on the payment page to cancel. Refunds and pairing still open the Zeller window.",
} as const;

export type ZellerSettingsCopyKey = keyof typeof zellerSettingsEnglishCopy;

export const zellerSettingsChineseCopy = {
  "tab.environmentNote": "Zeller 只有正式环境，没有测试环境",
  "card.eyebrow": "刷卡终端",
  "card.intro": "由这台 iPad 直连 Zeller 刷卡机，配对信息只保存在这台 iPad 上。",
  "pill.loading": "正在加载 Zeller…",
  "pill.loadFailed": "Zeller 加载失败",
  "pill.checking": "正在检查配对…",
  "pill.pairing": "正在配对",
  "pill.unpaired": "还没有配对",
  "pill.paired": "已配对",
  "pill.offline": "连不上 Zeller",
  "copy.loadFailed": "Zeller 组件加载失败：{{reason}}",
  "copy.checking": "正在向 Zeller 查询这台 iPad 的配对状态…",
  "copy.pairing": "请在弹出的 Zeller 窗口里登录并选择刷卡机。",
  "copy.paired": "已和 Zeller 刷卡机配对。还没有启用，刷卡仍走 {{provider}}。",
  "copy.pairedNoProvider": "已和 Zeller 刷卡机配对。还没有启用。",
  "copy.active": "这台 iPad 的刷卡走 Zeller 刷卡机。",
  "copy.offline": "这次没有查到配对状态，请检查网络后重试。已启用的刷卡通道没有变。",
  "row.pairing": "配对状态",
  "row.lastCheck": "上次检查",
  "row.activeProvider": "这台 iPad 现在用",
  "value.unpaired": "未配对",
  "value.paired": "已配对",
  "value.pairingInProgress": "配对中…",
  "value.checking": "检查中…",
  "value.unknown": "未知",
  "value.unknownWasPaired": "未知（上次：已配对）",
  "value.neverChecked": "还没有检查",
  "value.checkedAt": "今天 {{time}}",
  "value.checkFailedAt": "今天 {{time}} 失败（{{reason}}）",
  "value.noProvider": "未选择",
  "action.check": "检查配对",
  "action.pair": "配对刷卡机",
  "action.repair": "重新配对",
  "action.retry": "重试",
  "action.networkCheck": "网络检测",
  "action.enable": "启用 Zeller",
  "enable.lockedTitle": "启用 Zeller",
  "enable.lockedHint": "配对成功后才能启用。",
  "enable.readyTitle": "把这台 iPad 的刷卡切到 Zeller",
  "enable.readyHint": "{{provider}} 的设置会保留，随时可以切回去。",
  "enable.readyHintNoProvider": "Square 和 Linkly 的设置会保留，随时可以切回去。",
  "enable.notReadyHint":
    "Zeller 收款还没有接进这台 iPad 的收银流程。现在可以先完成配对，启用要等后续版本。",
  "enable.activeTitle": "已启用 Zeller",
  "enable.activeHint": "要换回别的刷卡机，到 Square 或 Linkly 页签里启用即可。",
  "offline.title": "刷卡可能暂时用不了",
  "offline.hint": "着急收款时，可在上方「收银支付方式」里打开「手动刷卡」。",
  "steps.eyebrow": "配对步骤",
  "steps.title": "第一次使用",
  "steps.one": "刷卡机登录 Zeller 账号，并确认已联网。",
  "steps.two":
    "点「配对刷卡机」，在弹出的 Zeller 窗口里登录同一个账号，选择这台 iPad 对应的刷卡机。",
  "steps.three":
    "显示「已配对」后，点「启用 Zeller」。每台 iPad 只需配对一次，重启和更新都不用重配。",
  "footer.note":
    "先配对，再启用。切回 Square 或 Linkly 时，它们的设置都还在。同一家店的刷卡机请放在同一个 Zeller Site 下，否则跨刷卡机退款可能找不到原交易。",
  "hideUi.title": "收款时不弹出 Zeller 窗口",
  "hideUi.hint":
    "刷卡扣款时 iPad 上不再盖一层 Zeller 窗口，请看刷卡机屏幕上的提示操作；要取消请点付款页的「取消支付」。退款和配对仍会弹出 Zeller 窗口。",
} as const satisfies Record<ZellerSettingsCopyKey, string>;

const zellerSettingsCopy = {
  en: zellerSettingsEnglishCopy,
  zh: zellerSettingsChineseCopy,
} as const;

export type ZellerSettingsLocale = keyof typeof zellerSettingsCopy;

export function zellerSettingsText(
  locale: ZellerSettingsLocale,
  key: ZellerSettingsCopyKey,
  values?: Readonly<Record<string, string | number>>,
): string {
  const template: string = zellerSettingsCopy[locale][key];
  if (!values) return template;
  return template.replace(/\{\{(\w+)\}\}/g, (placeholder, name: string) => {
    const value = values[name];
    return value === undefined ? placeholder : String(value);
  });
}
