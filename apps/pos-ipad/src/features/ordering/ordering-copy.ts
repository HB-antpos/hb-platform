/**
 * 首页两个入口、订货登录、订货工作台的中英文文案（跟页面代码放一起，不进全局文案包）。
 */
export const orderingEnglishCopy = {
  "entry.title.unregistered": "What will this iPad be used for?",
  "entry.subtitle.unregistered": "Checkout needs this iPad to be registered first. For ordering only, sign in with a staff account.",
  "entry.title.registered": "What would you like to do?",
  "entry.subtitle.registered": "This iPad is a registered checkout. Ordering uses a staff account and does not affect checkout.",
  "entry.pos.title": "Checkout",
  "entry.pos.desc.unregistered": "Register this iPad as a store checkout: sales, payments, returns, daily close.",
  "entry.pos.desc.registered": "Sales, payments, returns, daily close.",
  "entry.pos.action.unregistered": "Register with a device code",
  "entry.pos.action.registered": "Cashier sign-in",
  "entry.order.title": "Ordering",
  "entry.order.desc": "Warehouse ordering and Australian supplier ordering. Sign in with your HB staff account; no device registration needed.",
  "entry.order.action": "Staff sign-in",
  "entry.networkCheck": "Network check",
  "entry.back": "Back to home",
  "login.back": "Back",
  "login.title": "Ordering sign-in",
  "login.subtitle": "Use your hotbargain.vip staff account",
  "login.username": "Account",
  "login.password": "Password",
  "login.showPassword": "Show password",
  "login.hidePassword": "Hide password",
  "login.submit": "Sign in",
  "login.submitting": "Signing in…",
  "login.note": "Your password is not saved. You are signed out after 15 minutes without activity or when you tap Sign out. Only ordering is available on this iPad.",
  "login.error.required": "Enter your account and password.",
  "login.error.invalid": "Wrong account or password.",
  "login.error.notAllowed": "This account cannot use ordering. Ask your manager to grant ordering access.",
  "login.error.network": "Cannot reach hotbargain.vip. Check the network and try again.",
  "login.error.server": "Sign-in failed. Try again later.",
  "workbench.title": "Ordering",
  "workbench.tab.shop": "Warehouse ordering",
  "workbench.tab.assistant": "AU supplier ordering",
  "workbench.signOut": "Sign out",
  "workbench.idle": "Signs out in {{time}}",
  "workbench.store": "Store",
  "workbench.storeLoading": "Loading stores…",
  "workbench.storePick": "Choose a store",
  "workbench.storePickerTitle": "Choose a store for the ordering assistant",
  "workbench.storePickerClose": "Close",
  "workbench.storesFailed": "Could not load stores.",
  "workbench.storesEmpty": "This account has no stores for the ordering assistant.",
  "workbench.retry": "Try again",
  "workbench.shopBlocked": "Only warehouse ordering pages can be opened here.",
  "workbench.sessionEnded": "Your sign-in ended. Please sign in again.",
} as const;

export type OrderingCopyKey = keyof typeof orderingEnglishCopy;

export const orderingChineseCopy = {
  "entry.title.unregistered": "这台 iPad 要用来做什么？",
  "entry.subtitle.unregistered": "收银需要先注册设备；只订货的话，用员工账号登录就能用。",
  "entry.title.registered": "选择要做的事",
  "entry.subtitle.registered": "本机已注册为收银机；订货用员工账号登录，两边互不影响。",
  "entry.pos.title": "收银",
  "entry.pos.desc.unregistered": "注册成分店收银机：开单、收款、退货、日结。",
  "entry.pos.desc.registered": "开单、收款、退货、日结。",
  "entry.pos.action.unregistered": "用设备注册码注册",
  "entry.pos.action.registered": "收银员登录",
  "entry.order.title": "订货",
  "entry.order.desc": "仓库订货 + 澳洲供应商订货，用 HB 员工账号登录，不用注册设备。",
  "entry.order.action": "员工账号登录",
  "entry.networkCheck": "网络检测",
  "entry.back": "返回首页",
  "login.back": "返回",
  "login.title": "订货登录",
  "login.subtitle": "使用 hotbargain.vip 的员工账号",
  "login.username": "账号",
  "login.password": "密码",
  "login.showPassword": "显示密码",
  "login.hidePassword": "隐藏密码",
  "login.submit": "登录",
  "login.submitting": "正在登录…",
  "login.note": "不保存密码。闲置 15 分钟或点「退出」会清除登录；这台 iPad 上只能用订货功能。",
  "login.error.required": "请输入账号和密码。",
  "login.error.invalid": "账号或密码不对。",
  "login.error.notAllowed": "这个账号没有订货权限，请联系店长开通。",
  "login.error.network": "连不上 hotbargain.vip，请检查网络后重试。",
  "login.error.server": "登录失败，请稍后再试。",
  "workbench.title": "订货",
  "workbench.tab.shop": "仓库订货",
  "workbench.tab.assistant": "澳洲供应商订货",
  "workbench.signOut": "退出",
  "workbench.idle": "{{time}} 后自动退出",
  "workbench.store": "分店",
  "workbench.storeLoading": "正在加载分店…",
  "workbench.storePick": "选择分店",
  "workbench.storePickerTitle": "选择订货助手的分店",
  "workbench.storePickerClose": "关闭",
  "workbench.storesFailed": "分店加载失败。",
  "workbench.storesEmpty": "这个账号没有可用于订货助手的分店。",
  "workbench.retry": "重试",
  "workbench.shopBlocked": "这里只能打开仓库订货的页面。",
  "workbench.sessionEnded": "登录已结束，请重新登录。",
} as const satisfies Record<OrderingCopyKey, string>;

export type OrderingLocale = "zh" | "en";

export function resolveOrderingLocale(language?: string | null): OrderingLocale {
  return language?.toLowerCase().startsWith("zh") ? "zh" : "en";
}

export function orderingText(
  locale: OrderingLocale,
  key: OrderingCopyKey,
  values?: Readonly<Record<string, string | number>>,
): string {
  const template: string = (locale === "zh" ? orderingChineseCopy : orderingEnglishCopy)[key];
  if (!values) return template;
  return template.replace(/\{\{(\w+)\}\}/g, (placeholder, name: string) => {
    const value = values[name];
    return value === undefined ? placeholder : String(value);
  });
}
