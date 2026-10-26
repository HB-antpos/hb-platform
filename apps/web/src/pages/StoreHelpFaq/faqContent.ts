export type StoreHelpFaqLanguage = 'en' | 'zh'
export type StoreHelpFaqTab = 'pos' | 'mobile'

type Localized<T> = Record<StoreHelpFaqLanguage, T>

export type StoreHelpFaqRelated = {
  title: string
  body: string
}

/**
 * 文本里的界面元素记号：`[类型:English|中文]`，渲染时按当前语言显示，中文页附英文小字。
 * 类型：k 按钮、p 主按钮、t 开关/勾选、d 弹窗/区域标题、i 输入框、g 手势、tab 标签页。
 * 步骤以 `§` 开头时表示小标题，不参与编号。
 */
export type StoreHelpFaqQuestion = {
  id: string
  title: Localized<string>
  where: Localized<string>
  steps: Localized<string[]>
  tips?: Localized<string[]>
  rules?: Localized<string[]>
  permissions?: string[]
  related?: Localized<StoreHelpFaqRelated[]>
}

export type StoreHelpFaqGroup = {
  id: string
  title: Localized<string>
  questions: StoreHelpFaqQuestion[]
}

/**
 * 问答内容（分组、问题、步骤）已落库，由 `GET /api/support/faq` 提供；
 * 接口不可用时回退到 `faqSnapshot.json`，加载逻辑见 `faqData.ts`。这里只保留类型与页面外壳文案。
 */

export const STORE_HELP_FAQ_PATH = '/support/faq'

export const STORE_HELP_FAQ_COPY = {
  en: {
    documentTitle: 'POS & app how-to guide',
    brandSub: 'Help & FAQ',
    eyebrow: 'HB STORE HELP CENTRE',
    title: 'POS & app how-to guide',
    summary: 'Find your question, then follow the steps and tap the buttons shown.',
    tabs: { pos: 'Windows POS', mobile: 'Mobile app' },
    tabListLabel: 'Device',
    searchLabel: 'Search questions',
    searchPlaceholder: 'Search, e.g. “refund”, “discount”',
    clearSearch: 'Clear search',
    noResultsTitle: 'No matching questions',
    noResultsBody: 'Try another word, or switch to the other tab.',
    loadingLabel: 'Loading questions',
    loadFailedTitle: 'Could not load the questions',
    loadFailedBody: 'Check your network connection and try again.',
    retry: 'Try again',
    tocTitle: 'On this page',
    helpTitle: 'Still stuck?',
    helpBody: 'Ask your store manager or HQ IT. Tell them which screen you were on and which button you tapped.',
    where: 'Where',
    permission: 'Permission',
    posMapTitle: 'Sale screen at a glance',
    posMapBody: 'Area numbers used below refer to this map. Red outlines are the most-used buttons.',
    mobileMapTitle: 'App at a glance',
    mobileMapBody: 'Five bottom tabs, plus tool tiles grouped on Home.',
    posNote:
      'If you lack a permission, [d:Approval required|需要操作授权] appears. Ask a colleague who has it to scan their staff barcode. Use [k:中 / EN] at the top right to switch language.',
    mobileNote: 'If a tile or button is missing, your account probably lacks that permission. Ask your store manager to grant it under Staff.',
    footer: 'HOT BARGAIN INTERNATIONAL PTY LTD',
  },
  zh: {
    documentTitle: '收银与 App 使用问题解答',
    brandSub: '使用问题解答',
    eyebrow: 'HB 门店帮助中心',
    title: '收银与 App 使用问题解答',
    summary: '按问题找到对应的按钮，跟着步骤点即可。按钮旁的灰色小字是英文界面上的名字。',
    tabs: { pos: 'Windows 收银端', mobile: '移动端 App' },
    tabListLabel: '设备',
    searchLabel: '搜索问题',
    searchPlaceholder: '搜索问题，如「退款」「打折」',
    clearSearch: '清除搜索',
    noResultsTitle: '没有找到相关问题',
    noResultsBody: '换个关键词试试，或切换到另一个标签。',
    loadingLabel: '问题加载中',
    loadFailedTitle: '问题加载失败',
    loadFailedBody: '请检查网络后重试。',
    retry: '重试',
    tocTitle: '本页问题',
    helpTitle: '没找到答案？',
    helpBody: '请联系店长或总部 IT，并说明在哪个页面、点了哪个按钮。',
    where: '入口',
    permission: '所需权限',
    posMapTitle: '收银主页速览',
    posMapBody: '下文提到的区域编号对应这里；红框是最常用的按钮。',
    mobileMapTitle: 'App 速览',
    mobileMapBody: '底部 5 个标签 + 工作台按分组排列的功能磁贴。',
    posNote:
      '没有权限时会弹出 [d:Approval required|需要操作授权]，请有权限的同事扫他的员工码即可继续。界面右上角 [k:中 / EN] 可切换中英文。',
    mobileNote: '看不到某个磁贴或按钮，通常是账号没有对应权限，请联系店长在「人员管理」里开通。',
    footer: 'HOT BARGAIN INTERNATIONAL PTY LTD',
  },
} satisfies Localized<Record<string, unknown>>

/** 界面速览示意图的文案（只做示意，不代表每个账号都能看到全部磁贴）。 */
export const STORE_HELP_FAQ_MAPS = {
  pos: {
    zones: { en: ['Cart & pay', 'Keypad & actions', 'Function tiles'], zh: ['购物车与付款', '数字键与操作区', '功能砖'] },
    search: { en: 'Search / scan', zh: '搜索 / 扫码' },
    remove: { en: 'Remove', zh: '移除' },
    payNow: { en: 'Pay Now', zh: '立即付款' },
    actions: {
      en: ['Modify Qty', 'Price', 'Whole Order', 'Discount Amount', 'Discount %'],
      zh: ['修改数量', '价格', '整单操作', '折扣金额', '折扣%'],
    },
    tiles: {
      en: ['Special Items', 'Refund', 'Hold Order', 'Recall Order', 'Open Drawer', 'Last Receipt', 'Log Out', 'Daily Close', 'Card recovery', 'Attendance QR', 'Settings', 'Exit'],
      zh: ['特殊商品', '退款', '挂单', '取单', '开钱箱', '上张小票', '登出', '日结', '卡交易异常中心', '打卡', '设置', '退出软件'],
    },
    // 在 tiles 里需要红框强调的下标：退款、挂单、取单、卡交易异常中心、设置
    highlightedTiles: [1, 2, 3, 8, 10],
    // 窗口标题栏上的图标按钮：界面配色、客显、中英切换
    titleBar: {
      label: { en: 'Title bar', zh: '标题栏' },
      items: { en: ['Color theme', 'Customer display window', '中 / EN'], zh: ['界面配色', '客显窗口', '中 / EN'] },
    },
  },
  mobile: {
    home: { en: 'Home', zh: '工作台' },
    groups: [
      { title: { en: 'Product purchasing', zh: '商品进货' }, tiles: [{ en: 'HB new arrivals', zh: 'HB新品', hl: true }, { en: 'Local supplier invoices', zh: '供应商发票', hl: true }, { en: 'HB Orders', zh: 'HB订单', hl: true }] },
      { title: { en: 'Product sales', zh: '商品销售' }, tiles: [{ en: 'Product activity', zh: '商品进销查询', hl: true }, { en: 'Sales Records', zh: '销售订单', hl: true }, { en: 'Prices & labels', zh: '价格和标签', hl: true }, { en: 'Seasonal cards', zh: '节日卡', hl: true }] },
      { title: { en: 'Operations & reports', zh: '运营与报表' }, tiles: [{ en: 'Reports', zh: '报表中心', hl: true }, { en: 'Daily closes', zh: '日结记录', hl: true }, { en: 'Employee operation logs', zh: '员工操作日志', hl: true }] },
      { title: { en: 'Staff & admin', zh: '人员与管理' }, tiles: [{ en: 'Staff', zh: '人员管理', hl: true }, { en: 'Install app', zh: 'App 安装', hl: true }, { en: 'Help', zh: '使用帮助', hl: true }] },
    ],
    bottomTabs: { en: ['Home', 'Scan', 'Clock in', 'Reports', 'Me'], zh: ['工作台', '扫码查询', '打卡', '报表', '我的'] },
    legend: {
      en: [
        'Bottom tabs: Home, Scan, Clock in, Reports, Me (Clock in and Reports depend on permissions)',
        'Home groups: Product purchasing, Product sales, Warehouse & purchasing, Operations & reports, Staff & admin',
        'Red outlines mark tiles used on this page (illustrative; tiles depend on your permissions)',
      ],
      zh: [
        '底部标签：工作台、扫码查询、打卡、报表、我的（打卡、报表按权限显示）',
        '工作台分组：商品进货、商品销售、仓库与采购、运营与报表、人员与管理',
        '红框是本页用到的磁贴（示意，实际磁贴按权限显示）',
      ],
    },
  },
} as const
