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

const POS_GROUPS: StoreHelpFaqGroup[] = [
  {
    id: 'sale',
    title: { en: 'Selling', zh: '收银操作' },
    questions: [
      {
        id: 'remove',
        title: { zh: '如何删除购物车里的商品？', en: 'How do I remove an item from the cart?' },
        where: { zh: '收银主页 · 区域 ① 购物车', en: 'Sale screen · area ① cart' },
        steps: {
          zh: ['在左侧购物车里找到要删除的商品行。', '在这一行上 [g:Swipe left|向左滑]，行尾会露出红色按钮。', '点 [k:Remove|移除]，商品即从购物车删除。'],
          en: ['Find the line in the cart on the left.', '[g:Swipe left|向左滑] on that line to reveal a red button.', 'Tap [k:Remove|移除]. The item is removed from the cart.'],
        },
        tips: {
          zh: ['行上的 [k:−] 最少只能减到 1，要删除整行请用「移除」。', '目前没有「清空整单」按钮，需要逐行移除。'],
          en: ['The [k:−] button stops at 1. Use Remove to delete the whole line.', 'There is no “clear cart” button. Remove lines one by one.'],
        },
        permissions: ['Sales.RemoveLine'],
      },
      {
        id: 'line-discount',
        title: { zh: '如何给单个商品打折或改价？', en: 'How do I discount or reprice one item?' },
        where: { zh: '区域 ② 操作区', en: 'Area ② actions' },
        steps: {
          zh: [
            '在购物车里点选要打折的商品行（该行高亮）。',
            '用中间数字键输入数值，例如 15。',
            '点 [k:Discount %|折扣%] 打 15% 折扣，或点 [k:Discount Amount|折扣金额] 直接减 $15。',
            '常用折扣不用输数，直接点 [k:10%] 至 [k:50%]。',
            '要直接改售价：输入新价格后点 [k:Price|价格]。',
          ],
          en: [
            'Tap the cart line you want to discount (it highlights).',
            'Type a value on the keypad, e.g. 15.',
            'Tap [k:Discount %|折扣%] for 15% off, or [k:Discount Amount|折扣金额] for $15 off.',
            'For a common discount, skip the keypad and tap [k:10%] to [k:50%].',
            'To set a new price, type it, then tap [k:Price|价格].',
          ],
        },
        permissions: ['Sales.LineManualDiscount', 'Sales.LineQuickDiscount…', 'Sales.ChangePrice'],
      },
      {
        id: 'order-discount',
        title: { zh: '如何给整单打折？', en: 'How do I discount the whole order?' },
        where: { zh: '区域 ② 操作区', en: 'Area ② actions' },
        steps: {
          zh: [
            '先把所有商品加入购物车。',
            '点 [t:Whole Order|整单操作]，按钮亮起表示下一次折扣作用于整单。',
            '输入数值后点 [k:Discount %|折扣%] 或 [k:Discount Amount|折扣金额]；也可以直接点 [k:10%] 至 [k:50%]。',
            '在购物车底部核对折扣和合计。',
          ],
          en: [
            'Add every item to the cart first.',
            'Tap [t:Whole Order|整单操作]. When it is lit, the next discount applies to the whole order.',
            'Type a value, then tap [k:Discount %|折扣%] or [k:Discount Amount|折扣金额]. Or tap [k:10%] to [k:50%].',
            'Check the discount and total at the bottom of the cart.',
          ],
        },
        tips: {
          zh: ['「整单操作」只管下一次折扣，用完会自动关闭；要再打一次需重新点亮。'],
          en: ['Whole Order only covers the next discount, then switches off. Turn it on again for another one.'],
        },
        permissions: ['Sales.OrderManualDiscount', 'Sales.OrderQuickDiscount…'],
      },
    ],
  },
  {
    id: 'held',
    title: { en: 'Held sales & orders', zh: '挂单与订单' },
    questions: [
      {
        id: 'hold-recall',
        title: { zh: '如何挂单和取回挂单？', en: 'How do I hold a sale and recall it?' },
        where: { zh: '区域 ③ 功能砖 [k:Hold Order|挂单] / [k:Recall Order|取单]', en: 'Area ③ tiles [k:Hold Order|挂单] / [k:Recall Order|取单]' },
        steps: {
          zh: [
            '购物车里有商品时点 [k:Hold Order|挂单]，订单立即保存并清空购物车（不用填名称，状态栏会显示挂单编号）。',
            '要接着结账时点 [k:Recall Order|取单]，默认打开 [tab:Held|共享挂单] 标签下 [tab:Local|本机] 今天的挂单。',
            '在要继续的那一行点取回图标 [k:Recall Suspended|取回挂单]，商品回到购物车。',
            '照常付款即可。',
          ],
          en: [
            'With items in the cart, tap [k:Hold Order|挂单]. The sale is saved and the cart is cleared (no name needed; the status bar shows the held number).',
            'To finish it, tap [k:Recall Order|取单]. It opens [tab:Held|共享挂单] → [tab:Local|本机] for today.',
            'Tap the recall icon [k:Recall Suspended|取回挂单] on that line. The items return to the cart.',
            'Take payment as usual.',
          ],
        },
        tips: {
          zh: ['「取单」右上角的数字是今天还挂着的单数。', '不要的挂单可在该行「更多操作」里点 [k:Delete|删除]。'],
          en: ['The number on Recall Order is how many sales are still held today.', 'To discard a held sale, use [k:Delete|删除] in that line’s more-actions menu.'],
        },
        permissions: ['Sales.HoldOrder', 'Sales.RecallOrder', 'History.Recall'],
      },
      {
        id: 'held-other-till',
        title: { zh: '商品太多，如何挂单后到另一台收银机付款？', en: 'How do I hold a big sale and take payment on another till?' },
        where: { zh: '[k:Recall Order|取单] → [tab:Held|共享挂单]', en: '[k:Recall Order|取单] → [tab:Held|共享挂单]' },
        steps: {
          zh: [
            '§在录商品的收银机（A 机）',
            '点 [k:Hold Order|挂单]。',
            '点 [k:Recall Order|取单] → [tab:Held|共享挂单] → [tab:Local|本机]，在刚挂的那一行「更多操作」里点 [k:Share|共享]，等状态变成 [tab:Shared|已共享]。',
            '§在付款的收银机（B 机）',
            '点 [k:Recall Order|取单] → [tab:Held|共享挂单] → [tab:Other|非本机]。',
            '在这一行点 [k:Recall Suspended|取回挂单]，购物车恢复后照常付款。',
          ],
          en: [
            '§On the till where the items were scanned (till A)',
            'Tap [k:Hold Order|挂单].',
            'Tap [k:Recall Order|取单] → [tab:Held|共享挂单] → [tab:Local|本机]. In that line’s more-actions menu, tap [k:Share|共享] and wait until it shows [tab:Shared|已共享].',
            '§On the till that takes payment (till B)',
            'Tap [k:Recall Order|取单] → [tab:Held|共享挂单] → [tab:Other|非本机].',
            'Tap [k:Recall Suspended|取回挂单] on that line, then take payment as usual.',
          ],
        },
        tips: {
          zh: [
            '挂单默认只保存在本机，必须先点「共享」，其他收银机才看得到。',
            '只能在同一家分店的收银机之间共享，两台都要联网。',
            '已被其他收银机取走的挂单会显示「已被其他终端取单」。',
          ],
          en: [
            'Held sales stay on the till by default. Share it first, or other tills cannot see it.',
            'Sharing only works between tills in the same store, and both must be online.',
            'A held sale already recalled on another till is marked as taken by another terminal.',
          ],
        },
        permissions: ['Sales.HoldOrder', 'History.Recall'],
      },
      {
        id: 'order-history',
        title: { zh: '如何查找历史订单、补打小票？', en: 'How do I find a past order or reprint a receipt?' },
        where: { zh: '[k:Recall Order|取单] → [tab:Local|本地] / [tab:Online|在线]', en: '[k:Recall Order|取单] → [tab:Local|本地] / [tab:Online|在线]' },
        steps: {
          zh: [
            '点 [k:Recall Order|取单]，切到 [tab:Local|本地] 或 [tab:Online|在线] 标签。',
            '选日期：[k:Today|今天]、[k:Yesterday|昨天] 或 [k:Last 7 days|近 7 天]。',
            '在 [d:ORDER LOOKUP|订单查询] 搜索框输入订单号、商品货号或条码；也可以直接扫小票或商品条码，会自动搜索。',
            '在结果行点 [k:View receipt|查看小票] 或 [k:Order details|订单明细]；要补打，在小票预览里点 [k:Reprint|重打]。',
          ],
          en: [
            'Tap [k:Recall Order|取单] and open the [tab:Local|本地] or [tab:Online|在线] tab.',
            'Pick a date: [k:Today|今天], [k:Yesterday|昨天] or [k:Last 7 days|近 7 天].',
            'In [d:ORDER LOOKUP|订单查询], type the order number, item number or barcode — or just scan the receipt or item barcode to search.',
            'On the result, tap [k:View receipt|查看小票] or [k:Order details|订单明细]. To reprint, tap [k:Reprint|重打] in the receipt preview.',
          ],
        },
        tips: {
          zh: ['历史订单页不能直接退款，退款请用功能砖 [k:Refund|退款]（见「有小票，如何退款？」）。'],
          en: ['You cannot refund from order history. Use the [k:Refund|退款] tile instead (see “How do I refund with a receipt?”).'],
        },
        permissions: ['Sales.RecallOrder'],
      },
    ],
  },
  {
    id: 'pay',
    title: { en: 'Payments & refunds', zh: '付款与退款' },
    questions: [
      {
        id: 'voucher',
        title: { zh: '如何使用代金券付款？', en: 'How do I pay with a voucher?' },
        where: { zh: '区域 ① [p:Pay Now|立即付款] → 付款页', en: 'Area ① [p:Pay Now|立即付款] → payment screen' },
        steps: {
          zh: [
            '商品加好后点 [p:Pay Now|立即付款]，进入付款页。',
            '点付款方式 [k:Voucher|代金券]。',
            '在弹窗 [d:Voucher payment|代金券支付] 里扫描代金券条码，或输入 [i:Voucher code|代金券码]。',
            '点 [k:Add voucher|添加代金券]，代金券金额会从应付里扣除。',
            '还有未付金额就继续选现金或刷卡补足，最后点 [p:Complete Payment|完成支付]。',
          ],
          en: [
            'When the cart is ready, tap [p:Pay Now|立即付款].',
            'Tap the [k:Voucher|代金券] payment method.',
            'In [d:Voucher payment|代金券支付], scan the voucher or type the [i:Voucher code|代金券码].',
            'Tap [k:Add voucher|添加代金券]. The voucher amount comes off the balance.',
            'Pay any balance by cash or card, then tap [p:Complete Payment|完成支付].',
          ],
        },
        tips: {
          zh: ['付款页没有「代金券」按钮，说明本店设置未开启，请联系管理员。'],
          en: ['No Voucher button? It is turned off in store settings. Ask an administrator.'],
        },
        permissions: ['Payment.TakeVoucher'],
      },
      {
        id: 'card-failure',
        title: { zh: '刷卡失败或异常怎么处理？', en: 'What do I do when a card payment fails or gets stuck?' },
        where: { zh: '付款页刷卡弹窗 · 功能砖 [k:Card transaction recovery|卡交易异常中心]', en: 'Card pop-up on the payment screen · [k:Card transaction recovery|卡交易异常中心] tile' },
        steps: {
          zh: [
            '§被拒、余额不足或顾客取消',
            '弹出 [d:Card Payment Not Approved|银行卡支付未通过]，原因写在正文里（例如余额不足）；点 [k:OK|知道了]。',
            '这笔刷卡没有入账，订单仍停在付款页、未付。请顾客重刷、换卡，或改用其他付款方式。',
            '§刷卡机连不上、离线或超时',
            '弹出 [d:Card Terminal Connection Failed|刷卡机连接失败]、[d:Card Terminal Offline|刷卡机已离线] 或 [d:Card Transaction Timed Out|刷卡交易超时] 时，点 [k:OK|知道了]，检查刷卡机电源和网络后再刷；若询问 [d:Use Backup Card Payment?|是否使用备用刷卡方式？]，可点 [k:Use backup mode|使用备用方式]。',
            '一直连不上：[k:Settings|设置] → [k:Payment terminal|支付终端]，点 [k:Test Linkly|测试 Linkly] 或 [k:Log on to bank|登录银行网络] 自检。急着收款可临时打开 [t:Use manual card payment|使用手动刷卡]，在独立刷卡机上扣款成功后，在收银端点 [p:Confirm paid|已确认刷卡成功]。',
            '§结果未知（不确定顾客有没有被扣款）',
            '弹出 [d:Previous Card Transaction Needs Recovery|上一笔刷卡需要恢复] 时，不要马上再刷一次。可以点 [p:Move to recovery center and start a new sale|移至异常中心并开始新单] 先接待下一位顾客，或点 [k:Open card transaction recovery|打开卡交易异常中心] 马上处理。',
            '在 [k:Card transaction recovery|卡交易异常中心] 的 [tab:Pending|待处理] 里找到这一笔，点 [k:Check status now|立即检查]，向刷卡机查询结果；已批准的会自动补成订单。',
            '仍查不到时，由主管点 [k:Manual verification|人工核对]，对照刷卡机小票或银行流水选择 [k:Confirm paid|确认已付款]、[k:Confirm not paid|确认未付款]（订单恢复，可重新收款）或 [k:Continue waiting|继续等待]。',
          ],
          en: [
            '§Declined, insufficient funds or cancelled by the customer',
            '[d:Card Payment Not Approved|银行卡支付未通过] appears with the reason (for example insufficient funds). Tap [k:OK|知道了].',
            'Nothing was charged and the order stays unpaid on the payment screen. Ask the customer to try again or use another card, or take another payment method.',
            '§Terminal not connected, offline or timed out',
            'For [d:Card Terminal Connection Failed|刷卡机连接失败], [d:Card Terminal Offline|刷卡机已离线] or [d:Card Transaction Timed Out|刷卡交易超时], tap [k:OK|知道了], check the terminal’s power and network, then try again. If asked [d:Use Backup Card Payment?|是否使用备用刷卡方式？], you can tap [k:Use backup mode|使用备用方式].',
            'Still no connection: [k:Settings|设置] → [k:Payment terminal|支付终端], then [k:Test Linkly|测试 Linkly] or [k:Log on to bank|登录银行网络]. To keep trading, temporarily turn on [t:Use manual card payment|使用手动刷卡]: charge on the standalone terminal, then tap [p:Confirm paid|已确认刷卡成功] on the till.',
            '§Result unknown (not sure whether the customer was charged)',
            'When [d:Previous Card Transaction Needs Recovery|上一笔刷卡需要恢复] appears, do not charge again straight away. Tap [p:Move to recovery center and start a new sale|移至异常中心并开始新单] to serve the next customer, or [k:Open card transaction recovery|打开卡交易异常中心] to deal with it now.',
            'In [k:Card transaction recovery|卡交易异常中心], find it under [tab:Pending|待处理] and tap [k:Check status now|立即检查] to ask the terminal. Approved payments are turned into orders automatically.',
            'If there is still no answer, a supervisor taps [k:Manual verification|人工核对] and, using the terminal receipt or bank statement, chooses [k:Confirm paid|确认已付款], [k:Confirm not paid|确认未付款] (the order reopens for payment) or [k:Continue waiting|继续等待].',
          ],
        },
        tips: {
          zh: [
            '未处理完的刷卡不会挡住下一单刷卡，但处理前请先确认顾客有没有被扣款，避免重复收款。',
            '提示上一笔已批准（顾客已被扣款）但本机没有对应订单时：先给顾客退款或另行处理，在异常中心填写退款参考号，再点 [k:Refunded or handled elsewhere, close|已退款或已另行处理，关闭]。',
            '日结时 Linkly 结算失败：在 [tab:Linkly Settlement|Linkly 结算] 点 [k:Settle & Print|结算并打印] 重试；提示结算结果未知时不要再次提交，点 [k:Query result from Linkly|向 Linkly 查询结果]。',
          ],
          en: [
            'An unresolved card payment does not block the next card sale, but check whether the customer was charged first so they are not charged twice.',
            'If it says the previous payment was approved (customer charged) but there is no order on this till: refund or otherwise settle with the customer, enter the refund reference in the recovery center, then tap [k:Refunded or handled elsewhere, close|已退款或已另行处理，关闭].',
            'If the Linkly settlement fails at daily close, retry with [k:Settle & Print|结算并打印] on the [tab:Linkly Settlement|Linkly 结算] tab. If the result is unknown, do not resubmit — tap [k:Query result from Linkly|向 Linkly 查询结果].',
          ],
        },
        permissions: ['Payment.View', 'Payment.Confirm'],
      },
      {
        id: 'refund',
        title: { zh: '有小票，如何退款？', en: 'How do I refund with a receipt?' },
        where: { zh: '区域 ③ 功能砖 [k:Refund|退款]', en: 'Area ③ tile [k:Refund|退款]' },
        steps: {
          zh: [
            '点右侧功能砖 [k:Refund|退款]。',
            '扫小票上的条码，或输入订单号后点 [k:Search|搜索]。',
            '在 [d:Original Receipt|原订单商品] 里，点要退的商品行右侧的 [k:→]。',
            '点 [k:Add to Cart|加入购物车]，系统自动回到收银页。',
            '点 [p:Pay Now|立即付款]，选择 [k:Refund Cash|退现]、[k:Refund Card|退卡] 或 [k:Refund Voucher|退代金券]。',
            '点 [p:Complete Refund|完成退款]。',
          ],
          en: [
            'Tap the [k:Refund|退款] tile on the right.',
            'Scan the receipt barcode, or type the order number and tap [k:Search|搜索].',
            'In [d:Original Receipt|原订单商品], tap [k:→] on each line to return.',
            'Tap [k:Add to Cart|加入购物车]. You return to the sale screen.',
            'Tap [p:Pay Now|立即付款] and choose [k:Refund Cash|退现], [k:Refund Card|退卡] or [k:Refund Voucher|退代金券].',
            'Tap [p:Complete Refund|完成退款].',
          ],
        },
        tips: {
          zh: ['如果这台收银机开启了「退货操作需要再次扫码」，点退款后要先扫员工码（见「设置」分组）。'],
          en: ['If “Require another scan to start a return” is on for this till, you scan a staff barcode first (see Settings).'],
        },
        permissions: ['Returns.View', 'Returns.Confirm'],
      },
      {
        id: 'no-receipt-refund',
        title: { zh: '没有小票，如何退款？', en: 'How do I refund without a receipt?' },
        where: { zh: '区域 ③ 功能砖 [k:Refund|退款]', en: 'Area ③ tile [k:Refund|退款]' },
        steps: {
          zh: [
            '点功能砖 [k:Refund|退款]。',
            '勾选 [t:No-receipt return|无小票退货]。',
            '商品有条码：直接扫条码加入。',
            '商品没有条码：点 [k:No-barcode Item|无码商品]，在弹窗 [d:No-barcode return item|无码退货商品] 填 [i:Item name|商品名称] 和 [i:Retail price|零售价]，点 [k:Add|加入]。',
            '点 [k:Add to Cart|加入购物车]，再按「有小票，如何退款？」第 5–6 步选退款方式并完成退款。',
          ],
          en: [
            'Tap the [k:Refund|退款] tile.',
            'Tick [t:No-receipt return|无小票退货].',
            'Item has a barcode: scan it.',
            'No barcode: tap [k:No-barcode Item|无码商品]. In [d:No-barcode return item|无码退货商品], enter the [i:Item name|商品名称] and [i:Retail price|零售价], then tap [k:Add|加入].',
            'Tap [k:Add to Cart|加入购物车], then follow steps 5–6 of “How do I refund with a receipt?”.',
          ],
        },
        permissions: ['Returns.AddNoReceiptItem'],
      },
      {
        id: 'lay-by-create',
        title: { zh: '如何创建分期（Lay-by）？', en: 'How do I create a lay-by?' },
        where: { zh: '区域 ① [p:Pay Now|立即付款] → 付款页', en: 'Area ① [p:Pay Now|立即付款] → payment screen' },
        steps: {
          zh: [
            '把商品加入购物车，点 [p:Pay Now|立即付款]。',
            '打开 [t:Lay-by|分期付款] 开关。',
            '在 [d:Customer information|顾客信息] 点 [k:Edit|编辑]，填顾客姓名和电话（必填），点 [k:Save customer|保存顾客信息]。',
            '选一种付款方式，输入首付金额。',
            '点 [p:Complete Payment|完成支付]，分期订单创建完成。',
          ],
          en: [
            'Add items to the cart and tap [p:Pay Now|立即付款].',
            'Turn on [t:Lay-by|分期付款].',
            'Under [d:Customer information|顾客信息], tap [k:Edit|编辑]. Enter the customer name and phone (both required), then tap [k:Save customer|保存顾客信息].',
            'Choose one payment method and enter the deposit.',
            'Tap [p:Complete Payment|完成支付]. The lay-by is created.',
          ],
        },
        // 与 PaymentViewModel 的 MinimumInstallmentTotalAmount / MinimumInstallmentFirstPaymentAmount 保持一致
        rules: {
          zh: ['必须联网', '订单合计至少 $50', '首付至少 $20', '首付只能用一种付款方式'],
          en: ['Must be online', 'Order total at least $50', 'Deposit at least $20', 'Deposit uses one payment method'],
        },
      },
      {
        id: 'lay-by-continue',
        title: { zh: '如何继续支付（分期续付）？', en: 'How do I continue paying a lay-by?' },
        where: { zh: '区域 ③ 功能砖 [k:Recall Order|取单]', en: 'Area ③ tile [k:Recall Order|取单]' },
        steps: {
          zh: [
            '点功能砖 [k:Recall Order|取单]。',
            '切到 [tab:Lay-bys|分期订单] 标签，找到顾客的订单。',
            '点这一行的日历图标 [k:Continue Payment|继续支付]；也可以先点 [k:Order details|订单明细]，再点 [k:Continue Payment|继续支付]。',
            '进入付款页，分期开关已自动锁定为开，输入本次付款金额。',
            '点 [p:Complete Payment|完成支付]。',
          ],
          en: [
            'Tap the [k:Recall Order|取单] tile.',
            'Open the [tab:Lay-bys|分期订单] tab and find the order.',
            'Tap the calendar icon [k:Continue Payment|继续支付] on that line, or open [k:Order details|订单明细] and tap [k:Continue Payment|继续支付].',
            'The payment screen opens with Lay-by locked on. Enter this payment amount.',
            'Tap [p:Complete Payment|完成支付].',
          ],
        },
        related: {
          zh: [
            { title: '挂单后要接着结账？', body: '[k:Recall Order|取单] → [tab:Held|共享挂单] → [k:Recall Suspended|取回挂单]，回到收银页再付款。' },
            { title: '刷卡中途断开？', body: '点功能砖 [k:Card transaction recovery|卡交易异常中心] 查看并处理这笔刷卡。' },
          ],
          en: [
            { title: 'Finish a held sale?', body: '[k:Recall Order|取单] → [tab:Held|共享挂单] → [k:Recall Suspended|取回挂单], then pay on the sale screen.' },
            { title: 'Card payment interrupted?', body: 'Tap the [k:Card transaction recovery|卡交易异常中心] tile to check and resolve it.' },
          ],
        },
      },
    ],
  },
  {
    id: 'settings',
    title: { en: 'Settings', zh: '设置' },
    questions: [
      {
        id: 'color-theme',
        title: { zh: '如何切换界面主题（配色 / 长者模式）？', en: 'How do I change the colour theme or use Easy View?' },
        where: { zh: '窗口顶部标题栏 · 调色板图标', en: 'Window title bar · palette icon' },
        steps: {
          zh: [
            '点窗口顶部标题栏的调色板图标 [k:Color theme|界面配色]。',
            '选择一个配色：[k:Blue|蓝色]（默认）、[k:Classic|经典]、[k:Paper|暖纸]、[k:Graphite|石墨夜]、[k:Celadon|青瓷] 或 [k:Easy View|长者模式]。',
            '点选后立即生效，这台收银机会记住，下次打开沿用。',
          ],
          en: [
            'Click the palette icon [k:Color theme|界面配色] in the window title bar.',
            'Pick one: [k:Blue|蓝色] (default), [k:Classic|经典], [k:Paper|暖纸], [k:Graphite|石墨夜], [k:Celadon|青瓷] or [k:Easy View|长者模式].',
            'It applies straight away and this till remembers it next time.',
          ],
        },
        tips: {
          zh: ['「长者模式」只放大字号，按钮名称和位置不变。'],
          en: ['Easy View only enlarges text. Button names and positions stay the same.'],
        },
      },
      {
        id: 'receipt-profile',
        title: { zh: '如何修改小票上的门店信息和条款？', en: 'How do I change the store details and terms on receipts?' },
        where: { zh: 'Web 后台 · [k:System|系统管理] → [k:Store Management|分店管理]', en: 'Web admin · [k:System|系统管理] → [k:Store Management|分店管理]' },
        steps: {
          zh: [
            '§在 Web 后台修改并下发（总部统一管理）',
            '用电脑登录 Web 后台，打开 [k:System|系统管理] → [k:Store Management|分店管理]。',
            '在门店那一行点 [k:Edit|编辑]，修改分店名称、地址、联系电话、ABN、退换货政策、代金券使用说明、分期条款，点 [p:Save|保存]。',
            '点行尾 [k:More actions|更多操作] → [k:Publish receipt profile|下发小票资料]；多家门店可勾选后批量下发。',
            '在弹窗点 [p:Publish (N)|确认下发（N 家）]，收银端约 1 分钟内自动更新。',
            '§在收银端查看或马上同步',
            '点 [k:Settings|设置] → [k:Receipt printer|小票打印机]，在 [d:Store details|门店信息] 和 [d:Receipt terms|小票条款] 里查看。',
            '想马上生效，点 [k:Sync now|立即同步]。',
          ],
          en: [
            '§Edit and publish in Web admin (managed by head office)',
            'On a computer, open Web admin → [k:System|系统管理] → [k:Store Management|分店管理].',
            'Click [k:Edit|编辑] on the store, update name, address, phone, ABN, return policy, voucher terms and lay-by terms, then click [p:Save|保存].',
            'Click [k:More actions|更多操作] → [k:Publish receipt profile|下发小票资料]. Tick several stores to publish in bulk.',
            'Click [p:Publish (N)|确认下发（N 家）]. Tills update within about a minute.',
            '§Check on the till or sync now',
            'Tap [k:Settings|设置] → [k:Receipt printer|小票打印机] and look at [d:Store details|门店信息] and [d:Receipt terms|小票条款].',
            'To apply it immediately, tap [k:Sync now|立即同步].',
          ],
        },
        tips: {
          zh: ['总部下发过的字段在收银端是只读的；从未下发过的门店，可以直接在收银端修改。'],
          en: ['Fields published by head office are read-only on the till. Stores that have never been published can edit them on the till.'],
        },
        permissions: ['Stores.Edit', 'Settings.ReceiptPrinter'],
      },
      {
        id: 'bank-receipt',
        title: { zh: '是否打印银行小票，在哪里设置？', en: 'How do I turn the bank (EFTPOS) receipt on or off?' },
        where: { zh: '[k:Settings|设置] → [k:Receipt printer|小票打印机]', en: '[k:Settings|设置] → [k:Receipt printer|小票打印机]' },
        steps: {
          zh: [
            '点 [k:Settings|设置] → [k:Receipt printer|小票打印机] → [tab:Printer & options|打印机与选项]。',
            '在 [d:Print options|打印选项] 里打开或关闭 [t:Print bank receipt on customer receipt|客人小票附带银行收据]，改动自动保存。',
            '关闭后，客人小票上只打印卡类型、卡号后四位和金额。',
          ],
          en: [
            'Tap [k:Settings|设置] → [k:Receipt printer|小票打印机] → [tab:Printer & options|打印机与选项].',
            'Under [d:Print options|打印选项], turn [t:Print bank receipt on customer receipt|客人小票附带银行收据] on or off. It saves automatically.',
            'When off, the customer receipt shows only the card type, last four digits and amount.',
          ],
        },
        tips: {
          zh: ['默认开启，只对这台收银机生效，不随总部下发。'],
          en: ['On by default and set per till; head office publishing does not change it.'],
        },
        permissions: ['Settings.ReceiptPrinter'],
      },
      {
        id: 'manual-card',
        title: { zh: '如何切换到手动刷卡（独立刷卡机）？', en: 'How do I switch to manual card payments (standalone EFTPOS)?' },
        where: { zh: '[k:Settings|设置] → [k:Payment terminal|支付终端]', en: '[k:Settings|设置] → [k:Payment terminal|支付终端]' },
        steps: {
          zh: [
            '点功能砖 [k:Settings|设置]，左侧选 [k:Payment terminal|支付终端]。',
            '在 [d:Payment methods|付款方式] 打开 [t:Use manual card payment|使用手动刷卡]，改动立即保存。',
            '之后付款页的刷卡按钮变成 [k:Manual card|手动刷卡]：先在独立刷卡机上扣款成功，再在收银端确认。',
            '要恢复联机刷卡，关闭同一个开关即可。',
          ],
          en: [
            'Tap the [k:Settings|设置] tile, then choose [k:Payment terminal|支付终端].',
            'Under [d:Payment methods|付款方式], turn on [t:Use manual card payment|使用手动刷卡]. It saves immediately.',
            'The card button on the payment screen becomes [k:Manual card|手动刷卡]: take the payment on the standalone terminal first, then confirm on the till.',
            'To go back to the integrated terminal, turn the same switch off.',
          ],
        },
        tips: {
          zh: ['默认关闭。付款页上没有临时切换按钮，需要在设置里开关。'],
          en: ['Off by default. There is no switch on the payment screen; change it in Settings.'],
        },
        permissions: ['Settings.PaymentTerminal'],
      },
      {
        id: 'no-sale-drawer-scan',
        title: { zh: '无交易开钱箱，怎么设置成需要扫码？', en: 'How do I require a scan to open the drawer without a sale?' },
        where: { zh: '[k:Settings|设置] → [k:Receipt printer|小票打印机]', en: '[k:Settings|设置] → [k:Receipt printer|小票打印机]' },
        steps: {
          zh: [
            '点 [k:Settings|设置] → [k:Receipt printer|小票打印机]，找到 [d:Confirm with another scan|再次扫码确认]。',
            '打开 [t:Require another scan to open the cash drawer without a sale|无交易开钱箱需要再次扫码]，打开即生效。',
            '之后每次点 [k:Open Drawer|开钱箱] 都要扫一次员工码，扫码人需有开钱箱权限。',
          ],
          en: [
            'Tap [k:Settings|设置] → [k:Receipt printer|小票打印机] and find [d:Confirm with another scan|再次扫码确认].',
            'Turn on [t:Require another scan to open the cash drawer without a sale|无交易开钱箱需要再次扫码]. It applies immediately.',
            'From then on, [k:Open Drawer|开钱箱] asks for a staff barcode scan from someone allowed to open the drawer.',
          ],
        },
        tips: {
          zh: ['默认关闭，只对这台收银机生效。收现金后自动弹开钱箱不受影响。'],
          en: ['Off by default and set per till. The drawer still opens automatically after a cash sale.'],
        },
        permissions: ['Settings.ReceiptPrinter', 'CashDrawer.Open'],
      },
      {
        id: 'return-scan',
        title: { zh: '退货是否需要二次扫码，在哪里开启？', en: 'How do I require a second scan before a return?' },
        where: { zh: '[k:Settings|设置] → [k:Receipt printer|小票打印机]', en: '[k:Settings|设置] → [k:Receipt printer|小票打印机]' },
        steps: {
          zh: [
            '点 [k:Settings|设置] → [k:Receipt printer|小票打印机]，找到 [d:Confirm with another scan|再次扫码确认]。',
            '打开 [t:Require another scan to start a return|退货操作需要再次扫码]，打开即生效。',
            '之后点 [k:Refund|退款] 时先扫员工码（扫码人需有退货权限）；进入退货页后不再重复扫码。',
          ],
          en: [
            'Tap [k:Settings|设置] → [k:Receipt printer|小票打印机] and find [d:Confirm with another scan|再次扫码确认].',
            'Turn on [t:Require another scan to start a return|退货操作需要再次扫码]. It applies immediately.',
            'Tapping [k:Refund|退款] now asks for a staff barcode scan from someone with returns access. No further scans inside the return screen.',
          ],
        },
        tips: {
          zh: ['默认关闭，只对这台收银机生效。'],
          en: ['Off by default and set per till.'],
        },
        permissions: ['Settings.ReceiptPrinter', 'Returns.View'],
      },
    ],
  },
  {
    id: 'devices',
    title: { en: 'Devices & updates', zh: '设备与升级' },
    questions: [
      {
        id: 'card-terminal-pairing',
        title: { zh: '如何配对 ANZ-Linkly 或 Square 刷卡机？', en: 'How do I pair an ANZ-Linkly or Square card terminal?' },
        where: { zh: '[k:Settings|设置] → [k:Payment terminal|支付终端]', en: '[k:Settings|设置] → [k:Payment terminal|支付终端]' },
        steps: {
          zh: [
            '§ANZ-Linkly 刷卡机',
            '点 [k:Settings|设置] → [k:Payment terminal|支付终端]，找到 [d:ANZ Linkly payment terminal|ANZ Linkly 付款终端]。',
            '在 [d:Connection mode|连接模式] 选一种：[k:Local IP / Port|本地 IP / 端口]、[k:Cloud direct sync|Cloud 直连同步] 或 [k:Cloud backend async|Cloud 后端异步]；刷卡机上怎么操作可点 [k:View setup steps|查看设置说明]。',
            '本地 IP：点 [k:Use recommended|使用推荐地址] 或手填 IP 和端口 → [k:Test Linkly|测试 Linkly] → [k:Log on to bank|登录银行网络]。',
            'Cloud 直连：填 [i:Cloud API Username|Cloud API 账号] 和 [i:Cloud API Password|Cloud API 密码] → [k:Save Test Account|保存测试账号] → 填 [i:Pair Code|配对码] → [k:Pair Cloud|配对 Cloud]。',
            'Cloud 后端异步：在 [d:Lines|线路管理] 对线路点 [k:Re-pair|重新配对]，输入刷卡机上的 6 位配对码 → [k:Next|下一步] → [k:Confirm pairing|确认配对] → [k:Test connection|检测连接] → [k:Use this line|使用此线路]。',
            '最后点 [p:Enable Linkly|启用 Linkly]。',
            '§Square 刷卡机',
            '在 [d:Square payment terminal|Square 付款终端] 点 [k:Load Locations|加载门店位置] 选门店位置，再点 [k:Load Devices|加载设备]。',
            '在 [d:Device Codes|设备码管理] 填 [i:New Device Code Name|新设备码名称] → [k:Create Code|创建设备码]，把设备码输入到 Square 刷卡机上登录。',
            '点 [k:Refresh Status|刷新状态]，在 [i:Device|设备] 下拉框选中这台刷卡机 → [p:Save Square|保存 Square]。',
          ],
          en: [
            '§ANZ-Linkly terminal',
            'Tap [k:Settings|设置] → [k:Payment terminal|支付终端] and find [d:ANZ Linkly payment terminal|ANZ Linkly 付款终端].',
            'Under [d:Connection mode|连接模式], choose [k:Local IP / Port|本地 IP / 端口], [k:Cloud direct sync|Cloud 直连同步] or [k:Cloud backend async|Cloud 后端异步]. For the terminal-side steps, tap [k:View setup steps|查看设置说明].',
            'Local IP: tap [k:Use recommended|使用推荐地址] or enter the IP and port → [k:Test Linkly|测试 Linkly] → [k:Log on to bank|登录银行网络].',
            'Cloud direct: enter [i:Cloud API Username|Cloud API 账号] and [i:Cloud API Password|Cloud API 密码] → [k:Save Test Account|保存测试账号] → enter the [i:Pair Code|配对码] → [k:Pair Cloud|配对 Cloud].',
            'Cloud backend: under [d:Lines|线路管理], tap [k:Re-pair|重新配对] on a line, enter the 6-digit code from the terminal → [k:Next|下一步] → [k:Confirm pairing|确认配对] → [k:Test connection|检测连接] → [k:Use this line|使用此线路].',
            'Finally tap [p:Enable Linkly|启用 Linkly].',
            '§Square terminal',
            'Under [d:Square payment terminal|Square 付款终端], tap [k:Load Locations|加载门店位置] and pick the location, then [k:Load Devices|加载设备].',
            'Under [d:Device Codes|设备码管理], enter a [i:New Device Code Name|新设备码名称] → [k:Create Code|创建设备码], then sign in on the Square terminal with that code.',
            'Tap [k:Refresh Status|刷新状态], choose the terminal in [i:Device|设备] → [p:Save Square|保存 Square].',
          ],
        },
        tips: {
          zh: ['Cloud 后端异步模式的账号密码由总部在 Web 后台统一管理，门店不用填。', 'Square 设备码只支持正式（Production）环境。'],
          en: ['In Cloud backend mode, head office manages the account in Web admin; stores do not enter it.', 'Square device codes only work in the Production environment.'],
        },
        permissions: ['Settings.PaymentTerminal'],
      },
      {
        id: 'customer-display',
        title: { zh: '客显（顾客屏）怎么开启、调整大小、主副屏对调？', en: 'How do I open, resize or swap the customer display?' },
        where: { zh: '窗口顶部标题栏 · 显示器图标 [k:Customer display window|客显窗口]', en: 'Window title bar · monitor icon [k:Customer display window|客显窗口]' },
        steps: {
          zh: [
            '先接好第二块屏幕（顾客屏）。',
            '点主窗口标题栏的显示器图标 [k:Customer display window|客显窗口]：每点一次按「关闭 → 普通窗口 → 全屏 → 关闭」循环，下次开机会记住上次的模式。',
            '调整大小：在普通窗口模式下拖动客显窗口边缘，内容会等比缩放，尺寸会被记住；双击客显窗口的标题栏可切换全屏。',
            '主副屏对调：点客显窗口标题栏的 ⇄ 图标 [k:Swap screens with the customer display|主窗口与客显互换屏幕]（全屏时在右上角）。',
          ],
          en: [
            'Connect the second (customer) screen first.',
            'Click the monitor icon [k:Customer display window|客显窗口] in the main title bar. Each click cycles off → window → full screen → off, and the last mode is remembered.',
            'Resize: in window mode, drag the customer display’s edges. Content scales to fit and the size is remembered. Double-click its title bar for full screen.',
            'Swap screens: click the ⇄ icon [k:Swap screens with the customer display|主窗口与客显互换屏幕] in the customer display title bar (top right in full screen).',
          ],
        },
        tips: {
          zh: ['没接第二块屏时会提示未检测到第二个屏幕。', '没有单独的字号设置，大小靠拖动窗口调整；设置页里也没有客显选项。'],
          en: ['Without a second screen you will be told none was detected.', 'There is no font-size setting — resize the window instead. Settings has no customer display section.'],
        },
        permissions: ['CustomerDisplay.Manage'],
      },
      {
        id: 'app-update',
        title: { zh: '收银端如何升级到新版本？', en: 'How do I update the POS to a new version?' },
        where: { zh: '右下角状态栏 · [d:Update ready|更新已准备好]', en: 'Bottom-right status bar · [d:Update ready|更新已准备好]' },
        steps: {
          zh: [
            '收银端会在启动时和运行中（约每小时）自动检查新版本；下载完成后右下角出现 [d:Update ready|更新已准备好] 或 [d:Update required|必须更新]。',
            '先结束当前交易：清空购物车，确认没有正在刷卡或已收款未完成的订单。',
            '点 [p:Install update|安装更新]；可选更新会弹出 [d:Software update|软件更新] 窗口，可看 [d:What\'s new|更新内容]，点 [p:Restart and install|重启并安装]（或 [k:Install later|稍后安装]）。',
            '收银端自动关闭并安装，完成后自动重新打开，期间不要关机或断电；Windows 弹出授权窗口时选「是」。',
            '想手动检查：[k:Settings|设置] → [k:Data maintenance|数据维护] → [d:Software update|软件更新] → [k:Check update|检查更新]。',
          ],
          en: [
            'The POS checks for updates at startup and about hourly. When a download is ready, [d:Update ready|更新已准备好] or [d:Update required|必须更新] appears at the bottom right.',
            'Finish the current sale first: clear the cart and make sure no card payment is in progress.',
            'Tap [p:Install update|安装更新]. For an optional update, the [d:Software update|软件更新] window shows [d:What\'s new|更新内容]; tap [p:Restart and install|重启并安装] (or [k:Install later|稍后安装]).',
            'The POS closes, installs and reopens by itself. Do not switch off the PC; if Windows asks for permission, choose Yes.',
            'To check manually: [k:Settings|设置] → [k:Data maintenance|数据维护] → [d:Software update|软件更新] → [k:Check update|检查更新].',
          ],
        },
        tips: {
          zh: [
            '当前版本号显示在右下角状态栏 [d:Current version|当前版本]。',
            '晚上收银机空闲时，也会自动安装已下载的更新。',
            '更新失败时版本不变、收银数据不受影响，可在更新窗口里点「重试更新」。',
            '推送哪个版本、哪些分店或机器先升级，由总部在 Web 后台的版本发布中心控制。',
          ],
          en: [
            'The current version is shown at the bottom right as [d:Current version|当前版本].',
            'Downloaded updates also install automatically when the till is idle overnight.',
            'If an update fails, the version and sales data stay as they were; use “Retry update” in the update window.',
            'Head office controls which version goes to which stores or tills in the Web admin Release Center.',
          ],
        },
      },
    ],
  },
]

const MOBILE_GROUPS: StoreHelpFaqGroup[] = [
  {
    id: 'people',
    title: { en: 'Staff & permissions', zh: '员工与权限' },
    questions: [
      {
        id: 'create-staff',
        title: { zh: '如何创建员工账号？', en: 'How do I create a staff account?' },
        where: { zh: '工作台 · 人员与管理 · [k:Staff|人员管理]', en: 'Home · Staff & admin · [k:Staff|人员管理]' },
        steps: {
          zh: [
            '在 [tab:Home|工作台] 点 [k:Staff|人员管理]。',
            '顶部选择你管理的分店（选「全部分店」时不会出现新增按钮）。',
            '点右下角 [p:Add staff member|新增员工]。',
            '在 [d:Name and contact|姓名与联系] 填姓、名（必填）和电话；在 [d:Login details|登录信息] 填员工邮箱和账号（账号默认等于邮箱）。',
            '按需打开 [t:Enable now|立即启用]，点 [p:Create account|创建账号]。',
          ],
          en: [
            'On [tab:Home|工作台], tap [k:Staff|人员管理].',
            'Pick a store you manage at the top (Add is hidden when “All stores” is selected).',
            'Tap [p:Add staff member|新增员工] at the bottom right.',
            'Under [d:Name and contact|姓名与联系], enter first and last name (required) and phone. Under [d:Login details|登录信息], enter the staff email and username (defaults to the email).',
            'Turn on [t:Enable now|立即启用] if needed, then tap [p:Create account|创建账号].',
          ],
        },
        tips: {
          zh: ['新员工默认归属本店，身份为店员 · 临时工。', '账号创建后不能修改，提交前请核对。'],
          en: ['New staff belong to this store as Staff · Casual by default.', 'The username cannot be changed after creation. Check it first.'],
        },
        permissions: ['Users.CreateStoreStaff'],
      },
      {
        id: 'pos-permissions',
        title: { zh: '如何设置员工的 POS 收银权限？', en: 'How do I set a staff member’s POS permissions?' },
        where: { zh: '[k:Staff|人员管理] → 员工详情', en: '[k:Staff|人员管理] → staff details' },
        steps: {
          zh: [
            '在 [k:Staff|人员管理] 点员工所在行，进入 [d:Staff details|员工详情]。',
            '在 [d:Stores and access|分店与权限] 区点 [k:POS permissions|收银权限]（有多家分店时先选分店）。',
            '按分组勾选：销售、折扣、收款、退货、历史订单、日结、分期、钱箱与小票等；或点 [k:Apply|套用]，选 [k:Basic cashier|基础收银]、[k:Senior cashier|资深收银] 或 [k:Copy from colleague|从同事复制]。',
            '点 [k:Review and save|查看并保存] 核对改动，再点 [p:Confirm and save|确认保存]。',
          ],
          en: [
            'In [k:Staff|人员管理], tap the staff member to open [d:Staff details|员工详情].',
            'Under [d:Stores and access|分店与权限], tap [k:POS permissions|收银权限] (choose a store first if there are several).',
            'Tick by group: Sales, Discounts, Payment, Returns, Order history, Daily close, Lay-bys, Cash drawer & receipt. Or tap [k:Apply|套用] and pick [k:Basic cashier|基础收银], [k:Senior cashier|资深收银] or [k:Copy from colleague|从同事复制].',
            'Tap [k:Review and save|查看并保存], check the changes, then tap [p:Confirm and save|确认保存].',
          ],
        },
        tips: {
          zh: [
            '只对当前这家分店生效；改错了可点「恢复角色默认」。',
            '不能修改自己的权限。',
            'App 里能看到哪些功能是另一套设置：员工详情 → [k:Manage access|去授权] → 账号授权。',
          ],
          en: [
            'Changes apply to this store only. Use “Restore role default” to undo.',
            'You cannot change your own permissions.',
            'What the person can see in the app is set separately: staff details → [k:Manage access|去授权] → Account access.',
          ],
        },
        permissions: ['Users.ManagePosTerminalPermissions'],
      },
    ],
  },
  {
    id: 'data',
    title: { en: 'Sales & reports', zh: '销售与报表' },
    questions: [
      {
        id: 'product-sales',
        title: { zh: '如何查看商品销售数据？', en: 'How do I see product sales?' },
        where: { zh: '底部 [tab:Reports|报表] 或 工作台 [k:Product activity|商品进销查询]', en: 'Bottom [tab:Reports|报表] tab, or Home [k:Product activity|商品进销查询]' },
        steps: {
          zh: [
            '§看各供应商、各商品的销售汇总',
            '点底部 [tab:Reports|报表]，切到 [tab:Product Report|商品报告]。',
            '选分店和日期，或点快捷 [k:Today|今天]、[k:This week|本周]、[k:This month|本月]。',
            '输入货号或条码，点 [k:Search|查询] 只看这个商品。',
            '§查某一个商品在各店卖了多少',
            '工作台点 [k:Product activity|商品进销查询]，选分店后扫码或输入货号，点 [k:Search|查询]。',
            '在 [tab:Sales|销售明细] 标签看销量；点 [k:View all POS store sales|查看全部 POS 分店销售] 对比各店。',
          ],
          en: [
            '§Sales summary by supplier and product',
            'Tap the [tab:Reports|报表] tab, then open [tab:Product Report|商品报告].',
            'Choose store and dates, or tap [k:Today|今天], [k:This week|本周], [k:This month|本月].',
            'Type an item number or barcode and tap [k:Search|查询] to see one product.',
            '§How much one product sold in each store',
            'On Home, tap [k:Product activity|商品进销查询]. Choose a store, scan or type the item, and tap [k:Search|查询].',
            'Read the [tab:Sales|销售明细] tab. Tap [k:View all POS store sales|查看全部 POS 分店销售] to compare stores.',
          ],
        },
        permissions: ['Reports.ProductMovement.View', 'StoreProducts.View'],
      },
      {
        id: 'sales-orders',
        title: { zh: '如何查看 POS 销售订单和明细？', en: 'How do I see POS sales orders and details?' },
        where: { zh: '工作台 · 商品销售 · [k:Sales Records|销售订单]', en: 'Home · Product sales · [k:Sales Records|销售订单]' },
        steps: {
          zh: [
            '工作台「商品销售」分组点 [k:Sales Records|销售订单]，默认显示今天的订单。',
            '在搜索框输入订单号、货号、条码或商品名，或点 [k:Scan|扫码] 扫小票。',
            '点 [k:Filters|筛选]，选日期区间（最长 30 天）、分店、订单类型（已支付、已取消、已退款、分期等），点 [p:Apply filters|应用筛选]。',
            '点任一订单打开 [d:Order details|订单详情]：看商品明细（含折扣）和 [d:Payments|支付信息]，也可预览或下载发票。',
            '§看营业额汇总',
            '点底部 [tab:Reports|报表] → [tab:Revenue Report|营业额报告]，按日、周、月查看。',
          ],
          en: [
            'In the Product sales group on Home, tap [k:Sales Records|销售订单]. Today’s orders show by default.',
            'Search by order no., item no., barcode or product name, or tap [k:Scan|扫码] to scan a receipt.',
            'Tap [k:Filters|筛选] to set dates (up to 30 days), stores and order type (paid, cancelled, refunded, lay-by…), then [p:Apply filters|应用筛选].',
            'Tap an order to open [d:Order details|订单详情]: items (with discounts), [d:Payments|支付信息], and invoice preview or download.',
            '§Revenue summary',
            'Tap the [tab:Reports|报表] tab → [tab:Revenue Report|营业额报告] for daily, weekly or monthly figures.',
          ],
        },
        permissions: ['SalesOrders.View', 'Reports.ProductMovement.View'],
      },
      {
        id: 'daily-closes',
        title: { zh: '如何查看日结数据？', en: 'How do I see daily close records?' },
        where: { zh: '工作台 · 运营与报表 · [k:Daily closes|日结记录]', en: 'Home · Operations & reports · [k:Daily closes|日结记录]' },
        steps: {
          zh: [
            '工作台「运营与报表」分组点 [k:Daily closes|日结记录]。',
            '选时间：[tab:Today|今天]、[tab:Yesterday|昨天]、[tab:Last 7 days|近 7 天]、[tab:This month|本月] 或 [tab:Custom|自定义]。',
            '需要时点 [k:Filters|筛选]，按分店、来源（WPF / 手持 / iPad）、收银员筛选，点 [p:View results|查看结果]。',
            '列表顶部看 [d:Net difference (counted − expected)|净差额（实点 − 应有）]，每条标出短款、长款或已平。',
            '点一条进 [d:Daily close|日结详情]：现金差额、[d:Payment summary|支付方式汇总]（现金、刷卡、代金券）和 [d:Cash count|现金盘点明细]。',
          ],
          en: [
            'In the Operations & reports group on Home, tap [k:Daily closes|日结记录].',
            'Pick [tab:Today|今天], [tab:Yesterday|昨天], [tab:Last 7 days|近 7 天], [tab:This month|本月] or [tab:Custom|自定义].',
            'Tap [k:Filters|筛选] to narrow by store, source (WPF / handheld / iPad) or cashier, then [p:View results|查看结果].',
            'The top shows [d:Net difference (counted − expected)|净差额（实点 − 应有）]; each record is marked short, over or balanced.',
            'Tap a record for [d:Daily close|日结详情]: cash difference, [d:Payment summary|支付方式汇总] (cash, card, voucher) and [d:Cash count|现金盘点明细].',
          ],
        },
        tips: {
          zh: ['店长只能看到自己管理的分店。'],
          en: ['Store managers only see the stores they manage.'],
        },
        permissions: ['DailyCloseRecords.View'],
      },
      {
        id: 'operation-logs',
        title: { zh: '如何查看员工操作日志？', en: 'How do I see staff operation logs?' },
        where: { zh: '工作台 · 运营与报表 · [k:Employee operation logs|员工操作日志]', en: 'Home · Operations & reports · [k:Employee operation logs|员工操作日志]' },
        steps: {
          zh: [
            '工作台「运营与报表」分组点 [k:Employee operation logs|员工操作日志]。',
            '顶部选数据来源：[tab:Legacy POS|老收银] 或 [tab:New POS|新收银]。',
            '点 [k:Filters|筛选]，至少选一家分店，时间最长 31 天，还可按员工、设备、关键字筛选。',
            '切换视图 [tab:All|全部]、[tab:Risky|危险]、[tab:Exceptions|异常]、[tab:To review only|仅待核查]；或切到 [tab:By staff|按员工汇总] 看每人的异常数。',
            '点一条看 [d:Operation detail|操作详情] 和前后操作；有核查权限可点 [k:Mark normal|确认正常] 或 [k:Needs follow-up|有问题，需跟进]。',
          ],
          en: [
            'In the Operations & reports group on Home, tap [k:Employee operation logs|员工操作日志].',
            'Choose the source at the top: [tab:Legacy POS|老收银] or [tab:New POS|新收银].',
            'Tap [k:Filters|筛选]. Pick at least one store (up to 31 days), and optionally staff, device or keyword.',
            'Switch view: [tab:All|全部], [tab:Risky|危险], [tab:Exceptions|异常], [tab:To review only|仅待核查], or open [tab:By staff|按员工汇总] for exception counts per person.',
            'Tap a record for [d:Operation detail|操作详情] and nearby actions. Reviewers can tap [k:Mark normal|确认正常] or [k:Needs follow-up|有问题，需跟进].',
          ],
        },
        tips: {
          zh: [
            '记录的操作包括删除商品、改价、折扣、开钱箱、退款、作废销售、补打小票、主管授权、日结等。',
            '没有「按操作类型」筛选，可用「危险 / 异常」视图或关键字缩小范围。',
          ],
          en: [
            'Logged actions include removing items, price changes, discounts, opening the drawer, refunds, voids, reprints, supervisor approvals and daily close.',
            'There is no filter by action type. Use the Risky / Exceptions views or a keyword.',
          ],
        },
        permissions: ['LegacyEmployeeLogs.View', 'LegacyEmployeeLogs.Review'],
      },
    ],
  },
  {
    id: 'products',
    title: { en: 'Products & ordering', zh: '商品与订货' },
    questions: [
      {
        id: 'new-arrivals',
        title: { zh: '如何查看仓库新商品？', en: 'How do I see new warehouse arrivals?' },
        where: { zh: '工作台 · 商品进货 · [k:HB new arrivals|HB新品]', en: 'Home · Product purchasing · [k:HB new arrivals|HB新品]' },
        steps: {
          zh: [
            '工作台「商品进货」分组点 [k:HB new arrivals|HB新品]。',
            '确认 [d:Current store|当前门店]，需要时点 [k:Switch|切换]。',
            '选时间段：[tab:All|全部]、[tab:Past week|过去 1 周] 或 [tab:Next 3 weeks|未来 3 周]。',
            '点筛选，选商品类型（新商品 / 已有商品）、[k:At warehouse|已到仓] 或 [k:In transit|在途]、货柜，再点 [p:Show N products|显示 N 款商品]。',
          ],
          en: [
            'In the Product purchasing group on Home, tap [k:HB new arrivals|HB新品].',
            'Check [d:Current store|当前门店]. Tap [k:Switch|切换] to change it.',
            'Pick a period: [tab:All|全部], [tab:Past week|过去 1 周] or [tab:Next 3 weeks|未来 3 周].',
            'Open filters, choose product type (New / Existing), [k:At warehouse|已到仓] or [k:In transit|在途], and containers, then tap [p:Show N products|显示 N 款商品].',
          ],
        },
        tips: {
          zh: ['列表按到仓库日期排序：已到仓按实际日期，在途按预计日期。'],
          en: ['Sorted by warehouse date: actual date once arrived, estimated date while in transit.'],
        },
        permissions: ['Container.MobileNewProductsView'],
      },
      {
        id: 'supplier-invoices',
        title: { zh: '如何查看澳洲供应商发票，并打印商品标签？', en: 'How do I view Australian supplier invoices and print labels?' },
        where: { zh: '工作台 · 商品进货 · [k:Local supplier invoices|供应商发票]', en: 'Home · Product purchasing · [k:Local supplier invoices|供应商发票]' },
        steps: {
          zh: [
            '工作台「商品进货」分组点 [k:Local supplier invoices|供应商发票]。',
            '用 [i:Search invoice no.|搜索发票号] 查找，或按入库状态 [tab:Not received|未入库]、[tab:Partially received|部分入库]、[tab:Received|已入库] 筛选；分店、供应商、日期在 [k:More filters|更多筛选]。',
            '点一张发票进入 [d:Invoice details|发票明细]，查看每行的上次进货价、进货价、数量和小计；可用 [tab:Price up|涨价]、[tab:Price down|减价] 只看价格变动的商品。',
            '§打印商品标签',
            '在明细行点 [k:Edit product|商品编辑]，会跳到商品页并带出这个商品。',
            '在商品页底部点 [k:Regular|普通]、[k:Discount|折扣] 或 [k:Large|大标签] 打印标签。',
            '改份数：点打印机状态打开 [d:Print settings|打印设置]，调 [i:Print quantity|打印数量]；连打印机：[k:Printer settings|打印机设置] → [k:Scan for printers|扫描打印机] → [k:Connect|连接]。',
          ],
          en: [
            'In the Product purchasing group on Home, tap [k:Local supplier invoices|供应商发票].',
            'Search with [i:Search invoice no.|搜索发票号], or filter by [tab:Not received|未入库], [tab:Partially received|部分入库] or [tab:Received|已入库]. Store, supplier and dates are under [k:More filters|更多筛选].',
            'Tap an invoice to open [d:Invoice details|发票明细]: last cost, cost, quantity and subtotal per line. Use [tab:Price up|涨价] or [tab:Price down|减价] to see only price changes.',
            '§Print product labels',
            'Tap [k:Edit product|商品编辑] on a line. The product page opens with that item.',
            'At the bottom, tap [k:Regular|普通], [k:Discount|折扣] or [k:Large|大标签] to print.',
            'To change copies, tap the printer status to open [d:Print settings|打印设置] and set [i:Print quantity|打印数量]. To connect a printer: [k:Printer settings|打印机设置] → [k:Scan for printers|扫描打印机] → [k:Connect|连接].',
          ],
        },
        tips: {
          zh: ['发票页本身不能直接打标签，要通过「商品编辑」跳到商品页打印。'],
          en: ['Labels cannot be printed from the invoice itself — go through Edit product.'],
        },
        permissions: ['LocalPurchase.MobileView', 'StoreProducts.View'],
      },
      {
        id: 'hb-orders',
        title: { zh: '如何查看仓库订单（HB订单）？', en: 'How do I view warehouse orders (HB Orders)?' },
        where: { zh: '工作台 · 商品进货 · [k:HB Orders|HB订单]', en: 'Home · Product purchasing · [k:HB Orders|HB订单]' },
        steps: {
          zh: [
            '工作台「商品进货」分组点 [k:HB Orders|HB订单]，选择分店，默认显示近 6 个月的订单。',
            '按 [k:Status|状态] 筛选：[tab:In cart|购物车]、[tab:Submitted|已提交]、[tab:Picking|配货中]、[tab:Completed|已完成]。',
            '点 [k:View details|查看明细]，查看每个商品的订货数、发货数、货位、金额和体积。',
            '需要标签时在明细行点 [k:Print label|打印标签]（每次打一张普通标签）。',
          ],
          en: [
            'In the Product purchasing group on Home, tap [k:HB Orders|HB订单] and choose a store. The last 6 months show by default.',
            'Filter by [k:Status|状态]: [tab:In cart|购物车], [tab:Submitted|已提交], [tab:Picking|配货中] or [tab:Completed|已完成].',
            'Tap [k:View details|查看明细] to see ordered and shipped quantities, location, amount and volume per item.',
            'To print a label, tap [k:Print label|打印标签] on a line (one regular label each time).',
          ],
        },
        tips: {
          zh: ['仓库拣货员用的是「仓库与采购」分组的 [k:Order picking|订单拣货]：扫员工码 → 选订单 → [k:Start|开始拣货] → [k:Finish picking|完成拣货] → [k:Submit|确认完成]。'],
          en: ['Warehouse pickers use [k:Order picking|订单拣货] in Warehouse & purchasing: scan staff code → choose order → [k:Start|开始拣货] → [k:Finish picking|完成拣货] → [k:Submit|确认完成].'],
        },
        permissions: ['Orders.View'],
      },
    ],
  },
  {
    id: 'store-tasks',
    title: { en: 'Store tasks', zh: '门店任务' },
    questions: [
      {
        id: 'price-updates',
        title: { zh: '如何处理仓库商品价格更新提醒？', en: 'How do I handle price update reminders?' },
        where: { zh: '工作台 · 商品销售 · [k:Prices & labels|价格和标签]', en: 'Home · Product sales · [k:Prices & labels|价格和标签]' },
        steps: {
          zh: [
            '提醒来自工作台 [k:Prices & labels|价格和标签] 磁贴上的角标，以及每小时一次的提醒弹窗（点 [k:Go to tasks|去处理] 直接进入，或 [k:Later|稍后]）。',
            '在 [tab:To do|未完成] 标签下，可筛 [tab:Price change|需改价]、[tab:Relabel|待换标签]、[tab:Overdue|超期]。',
            '需改价的任务：点 [k:Update only|仅更新] 或 [p:Update & print|更新并打印]；不改价可在 [k:More|更多] 里选 [k:Keep store price|保持本店价]。',
            '待换标签的任务：换好后点 [k:Mark replaced|标记已换]，或点 [k:Print label|打印标签] 打新价签。',
            '任务多时点 [k:Select|批量处理]，全选后一次执行。',
          ],
          en: [
            'Reminders come from the badge on the [k:Prices & labels|价格和标签] tile and an hourly pop-up ([k:Go to tasks|去处理] opens the list; [k:Later|稍后] snoozes).',
            'On [tab:To do|未完成], filter by [tab:Price change|需改价], [tab:Relabel|待换标签] or [tab:Overdue|超期].',
            'Price change: tap [k:Update only|仅更新] or [p:Update & print|更新并打印]. To keep your price, use [k:More|更多] → [k:Keep store price|保持本店价].',
            'Relabel: after swapping the label tap [k:Mark replaced|标记已换], or [k:Print label|打印标签] for a new one.',
            'For many tasks, tap [k:Select|批量处理], select all and apply once.',
          ],
        },
        tips: {
          zh: [
            '任务超过 7 天未处理会锁住店长的报表中心，处理完自动解锁。',
            '有撤销权限的账号可在 [k:More|更多] → [k:Cancel pending (all stores)|撤销未完成（所有分店）] → [k:Cancel tasks|确认撤销]；撤销只取消提醒、不改价格，且无法恢复。',
            '管理员账号不会弹每小时提醒。',
          ],
          en: [
            'Tasks overdue by 7 days lock the store manager’s Reports until they are done.',
            'Accounts with cancel access can use [k:More|更多] → [k:Cancel pending (all stores)|撤销未完成（所有分店）] → [k:Cancel tasks|确认撤销]. This only cancels reminders, changes no prices and cannot be undone.',
            'Admin accounts do not get the hourly pop-up.',
          ],
        },
        permissions: ['StoreProducts.PriceUpdates'],
      },
      {
        id: 'seasonal-cards',
        title: { zh: '如何统计并提交节日卡片库存？', en: 'How do I count and submit seasonal card stock?' },
        where: { zh: '工作台 · 商品销售 · [k:Seasonal cards|节日卡]', en: 'Home · Product sales · [k:Seasonal cards|节日卡]' },
        steps: {
          zh: [
            '工作台「商品销售」分组点 [k:Seasonal cards|节日卡]。',
            '在 [tab:Submit|填报] 标签先选分店，再选 [i:Supplier|供应商]（必选）。',
            '按价格类型填剩余数量，空着按 0 计；[i:Other price|其他价格] 要填实际单价。',
            '点 [p:Submit|提交填报]。',
            '填错了可以再进来改：会带出上次的数量，改完点 [p:Overwrite|覆盖提交] → [k:Overwrite|确认覆盖]。',
            '在 [tab:History|历史记录] 按年度和节日查看，可切换 [tab:List|列表] 或 [tab:Table|表格]。',
          ],
          en: [
            'In the Product sales group on Home, tap [k:Seasonal cards|节日卡].',
            'On [tab:Submit|填报], choose the store, then a [i:Supplier|供应商] (required).',
            'Enter the remaining quantity for each price type; blanks count as 0. [i:Other price|其他价格] needs the actual unit price.',
            'Tap [p:Submit|提交填报].',
            'To correct it, come back: last quantities are filled in. Change them, then [p:Overwrite|覆盖提交] → [k:Overwrite|确认覆盖].',
            'Use [tab:History|历史记录] to view by year and holiday, as a [tab:List|列表] or [tab:Table|表格].',
          ],
        },
        tips: {
          zh: ['每个节日从当天起 29 天内可以填报，过了只能查看历史。', '覆盖提交后旧记录仍留在历史里，标为「已被更新」。'],
          en: ['Submissions are open for 29 days from each holiday; after that, history only.', 'Overwritten submissions stay in history, marked as updated.'],
        },
        permissions: ['SeasonalCards.Remaining.SubmitManagedStore'],
      },
    ],
  },
  {
    id: 'app',
    title: { en: 'Using the app', zh: 'App 使用' },
    questions: [
      {
        id: 'floating-button',
        title: { zh: '如何开启和使用语言悬浮按钮？', en: 'How do I turn on and use the floating language button?' },
        where: { zh: '底部 [tab:Me|我的] → [d:Preferences|偏好] → [t:Floating language button|语言悬浮按钮]', en: 'Bottom [tab:Me|我的] tab → [d:Preferences|偏好] → [t:Floating language button|语言悬浮按钮]' },
        steps: {
          zh: [
            '§开启',
            '点底部 [tab:Me|我的] 进入设置页，在 [d:Preferences|偏好] 里打开 [t:Floating language button|语言悬浮按钮]（新版本默认已开启）。',
            '开启后每一页都会出现一个「中 / EN」小圆按钮（登录页、键盘弹出时、全屏拣货页不显示）。',
            '§使用',
            '点一下：在中文和英文之间切换界面语言。',
            '按住（默认 2 秒）：打开 [d:Quick navigation|快速跳转]，可直接去 [k:Home|工作台]、[k:Settings|设置]、[d:Recent|最近访问] 的页面或其他有权限的功能。',
            '拖动：按住拖到屏幕任意位置，松手后自动靠边。',
            '隐藏：拖到屏幕底部的 [d:Drop here to hide|拖到这里隐藏] 区域松手；底部会出现提示，点 [k:Undo|撤销] 可以马上恢复。',
          ],
          en: [
            '§Turn it on',
            'Tap the bottom [tab:Me|我的] tab to open Settings, then turn on [t:Floating language button|语言悬浮按钮] under [d:Preferences|偏好] (on by default in newer versions).',
            'A small 中 / EN button then appears on every page (not on sign-in, while the keyboard is open, or on full-screen picking).',
            '§Use it',
            'Tap: switch the app between Chinese and English.',
            'Hold (2 s by default): open [d:Quick navigation|快速跳转] to jump to [k:Home|工作台], [k:Settings|设置], [d:Recent|最近访问] pages or any tool you can access.',
            'Drag: move it anywhere; it snaps to the nearest edge when released.',
            'Hide: drag it onto [d:Drop here to hide|拖到这里隐藏] at the bottom. A message appears; tap [k:Undo|撤销] to bring it back.',
          ],
        },
        tips: {
          zh: [
            '按住多久打开快速跳转，可在同一处的 [d:Hold time|按住时长] 滑轨调节（1–3 秒，默认 2 秒）。',
            '隐藏后要重新打开：[tab:Me|我的] → [d:Preferences|偏好] → 打开 [t:Floating language button|语言悬浮按钮]。',
            '这个设置保存在本机，退出登录不会重置。',
            '当前版本号显示在设置页底部（「版本 x.x.x」）。安卓低于 1.0.12 时，App 会提示安装新版本，按提示安装即可；iOS 的 App Store 版本要等 1.0.9 上架后，在 App Store 里更新。',
          ],
          en: [
            'Change how long to hold with the [d:Hold time|按住时长] slider in the same place (1–3 s, default 2 s).',
            'To show it again after hiding: [tab:Me|我的] → [d:Preferences|偏好] → turn on [t:Floating language button|语言悬浮按钮].',
            'The setting is saved on the phone and survives sign-out.',
            'Your version is shown at the bottom of Settings (“Version x.x.x”). Below 1.0.12 on Android, the app prompts you to install the new version — follow the prompt. On iOS, App Store users get it once version 1.0.9 is released there.',
          ],
        },
        // 依据线上 OTA 源核对：只有安卓 1.0.12 与 iOS 1.0.9 的源包含 floating-language 模块
        rules: {
          zh: ['安卓：App 1.0.12 及以上', 'iOS：App 1.0.9 及以上（目前在 TestFlight，App Store 待上架）'],
          en: ['Android: app 1.0.12 or later', 'iOS: app 1.0.9 or later (TestFlight now; App Store pending)'],
        },
      },
      {
        id: 'app-download',
        title: { zh: '如何获取最新 App 下载地址？', en: 'Where do I get the latest app download link?' },
        where: { zh: '工作台 · 人员与管理 · [k:Install app|App 安装]', en: 'Home · Staff & admin · [k:Install app|App 安装]' },
        steps: {
          zh: [
            '工作台「人员与管理」分组点 [k:Install app|App 安装]。',
            '选 [tab:iOS] 或 [tab:Android]，默认选中本机系统。',
            '让同事用手机相机扫页面上的二维码：iOS 打开 App Store，Android 下载安装包。',
            '也可以点 [k:Copy link|复制链接] 或 [k:Share|分享] 发给同事，或点 [k:Open on this device|在本机打开]。',
          ],
          en: [
            'In the Staff & admin group on Home, tap [k:Install app|App 安装].',
            'Choose [tab:iOS] or [tab:Android]; your own system is selected by default.',
            'Have your colleague scan the QR code with their phone camera: iOS opens the App Store, Android downloads the installer.',
            'Or tap [k:Copy link|复制链接] or [k:Share|分享] to send it, or [k:Open on this device|在本机打开].',
          ],
        },
        tips: {
          zh: [
            '页面会显示当前版本号和 Build，可用来核对是不是最新版。',
            'Android 首次安装需要允许「安装未知应用」。',
            '收银端 [k:Settings|设置] → [k:Staff app download|员工 App 下载] 也能扫码下载。',
          ],
          en: [
            'The page shows the current version and build, so you can check you are up to date.',
            'On Android, allow “Install unknown apps” the first time.',
            'On the till, [k:Settings|设置] → [k:Staff app download|员工 App 下载] shows the same QR code.',
          ],
        },
        permissions: ['System.ViewMobileAppInstallLinks'],
      },
    ],
  },
]

export const STORE_HELP_FAQ_GROUPS: Record<StoreHelpFaqTab, StoreHelpFaqGroup[]> = {
  pos: POS_GROUPS,
  mobile: MOBILE_GROUPS,
}
