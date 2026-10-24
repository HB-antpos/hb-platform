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
            '仍查不到时，由主管处理：选中这一笔，对照刷卡机小票、顾客的支付记录或银行流水，点 [k:Confirm paid|确认已付款]、[k:Confirm not paid|确认未付款]（订单恢复，可重新收款）或 [k:Continue waiting|继续等待]，会弹出一个确认窗口。',
            '窗口里不需要填证据，只有选填的 [k:Supervisor note|主管备注]；可以点预设一键填入。已付款：[k:Terminal shows payment successful|刷卡机显示支付成功]、[k:Customer showed payment record|顾客提供支付记录]；未付款：[k:Terminal shows failed|刷卡机显示失败]、[k:Terminal offline|刷卡机离线]、[k:Terminal stuck|刷卡机卡住了]。已付款还可以填 [k:Payment reference|付款参考号]（选填）。',
            '点窗口里的确认按钮后，由有权限的主管扫码授权，结果以主管的人工确认为准，系统不会再向刷卡机或银行重新核实；点 [k:Cancel|取消] 可以放弃。如果提示没有保存，原因会显示在窗口里，填写的内容会保留。',
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
            'If there is still no answer, a supervisor selects the transaction and, using the terminal receipt, the customer’s payment record or the bank statement, taps [k:Confirm paid|确认已付款], [k:Confirm not paid|确认未付款] (the order reopens for payment) or [k:Continue waiting|继续等待]. A confirmation window opens.',
            'No evidence is needed in that window — only an optional [k:Supervisor note|主管备注]. Tap a preset to fill it in. Paid: [k:Terminal shows payment successful|刷卡机显示支付成功], [k:Customer showed payment record|顾客提供支付记录]. Not paid: [k:Terminal shows failed|刷卡机显示失败], [k:Terminal offline|刷卡机离线], [k:Terminal stuck|刷卡机卡住了]. For paid you may also enter a [k:Payment reference|付款参考号] (optional).',
            'After the confirm button, an authorised supervisor scans their barcode. The supervisor’s manual confirmation is final — the system no longer re-checks with the terminal or bank. Tap [k:Cancel|取消] to back out. If it says the decision was not saved, the reason shows in the window and what you typed is kept.',
          ],
        },
        tips: {
          zh: [
            '未处理完的刷卡不会挡住下一单刷卡，但处理前请先确认顾客有没有被扣款，避免重复收款。人工确认后系统不会再向刷卡机核实，所以点 [k:Confirm paid|确认已付款] 或 [k:Confirm not paid|确认未付款] 前，务必先看刷卡机小票或顾客的支付记录。',
            '提示上一笔已批准（顾客已被扣款）但本机没有对应订单时：先给顾客退款或另行处理，再点 [k:Refunded or handled elsewhere, close|已退款或已另行处理，关闭]，在窗口的 [k:Supervisor note|主管备注] 里写明怎么退的（必填，例如退款参考号）。',
            '日结时 Linkly 结算失败：在 [tab:Linkly Settlement|Linkly 结算] 点 [k:Settle & Print|结算并打印] 重试；提示结算结果未知时不要再次提交，点 [k:Query result from Linkly|向 Linkly 查询结果]。',
          ],
          en: [
            'An unresolved card payment does not block the next card sale, but check whether the customer was charged first so they are not charged twice. The system does not re-check with the terminal after a manual confirmation, so look at the terminal receipt or the customer’s payment record before tapping [k:Confirm paid|确认已付款] or [k:Confirm not paid|确认未付款].',
            'If it says the previous payment was approved (customer charged) but there is no order on this till: refund or otherwise settle with the customer, then tap [k:Refunded or handled elsewhere, close|已退款或已另行处理，关闭] and write how it was settled in the [k:Supervisor note|主管备注] (required, for example the refund reference).',
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
            '打开右上角的 [t:No-receipt return|无小票退货] 开关。',
            '商品有条码：直接扫条码加入。',
            '商品没有条码：点 [k:No-barcode Item|无码商品]，在弹窗 [d:No-barcode return item|无码退货商品] 填 [i:Item name|商品名称] 和 [i:Retail price|零售价]，点 [k:Add|加入]。',
            '点 [k:Add to Cart|加入购物车]，再按「有小票，如何退款？」第 5–6 步选退款方式并完成退款。',
          ],
          en: [
            'Tap the [k:Refund|退款] tile.',
            'Turn on the [t:No-receipt return|无小票退货] switch at the top right.',
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
    id: 'close',
    title: { en: 'Daily close', zh: '日结' },
    questions: [
      {
        // 按钮名对照 WPF Strings.resx / Strings.zh-CN.resx 的 dailyClose.* 同一 key；
        // 自动 Linkly 结算条件见 LinklySettlementService.ShouldAutoSettleAfterDailyCloseAsync（今天、集成 Linkly、未开手动刷卡、当天无成功/待定结算）。
        id: 'pos-daily-close',
        title: { zh: '如何日结（清点现金、Linkly 结算、打印日结单）？', en: 'How do I do the daily close (cash count, Linkly settlement, printout)?' },
        where: { zh: '区域 ③ 功能砖 [k:Daily Close|日结]', en: 'Area ③ tile [k:Daily Close|日结]' },
        steps: {
          zh: [
            '§清点现金并保存',
            '先结束当前交易，点右侧功能砖 [k:Daily Close|日结]。',
            '核对顶部 [i:Business Date|营业日期]（点 [k:Today|今天] 切回今天），点 [p:New Daily Close|创建新日结]；上次没做完时按钮显示为 [p:Continue Daily Close|继续日结]。',
            '在 [d:Cash Count|现金盘点] 里逐个点选纸币、硬币面额，用数字键输入钱箱里的数量，点 [k:Apply · Next|应用 · 下一项] 跳到下一个面额。',
            '在右侧 [d:Cash Reconciliation|现金对账] 核对 [d:Expected Cash|应有现金]、[d:Cash Counted|现金实盘] 和 [d:Over / Short|溢长 / 短款]（显示短款、长款或平账）；[d:System Totals|系统汇总] 里可看销售、退款和订单数。',
            '点 [p:Save & Finalize|保存并完成]，日结保存并自动打印日结单。',
            '§Linkly 刷卡结算',
            '按钮下方显示 [d:Also sends Linkly settlement|同时发送 Linkly 结算] 时，保存后会自动向刷卡机结算、打印银行结算单，并停在 [tab:Linkly Settlement|Linkly 结算] 页签显示结果。',
            '没发出或失败（例如刷卡机离线）：检查刷卡机和 Linkly 程序后，在 [tab:Linkly Settlement|Linkly 结算] 点 [k:Settle & Print|结算并打印]，在 [d:Confirm Linkly settlement|确认刷卡机日结] 里再点 [k:Settle & Print|结算并打印] 重试。',
            '§重打日结单或银行结算单',
            '在 [tab:Daily Close History|日结历史] 选好营业日期，点选一条记录，再点 [k:Reprint Daily Close|重打印日结单]。',
            '银行结算单：在 [tab:Linkly Settlement|Linkly 结算] 选中记录，点 [k:Reprint Selected Settlement|重打所选结算单]。',
          ],
          en: [
            '§Count the cash and save',
            'Finish the current sale, then tap the [k:Daily Close|日结] tile on the right.',
            'Check the [i:Business Date|营业日期] at the top (tap [k:Today|今天] to go back to today), then tap [p:New Daily Close|创建新日结]. If one was left unfinished, the button shows [p:Continue Daily Close|继续日结].',
            'In [d:Cash Count|现金盘点], tap each note and coin denomination, type how many are in the till, then tap [k:Apply · Next|应用 · 下一项] to move to the next one.',
            'On the right, check [d:Expected Cash|应有现金], [d:Cash Counted|现金实盘] and [d:Over / Short|溢长 / 短款] under [d:Cash Reconciliation|现金对账] (shown as Short, Over or Balanced). [d:System Totals|系统汇总] shows sales, refunds and order count.',
            'Tap [p:Save & Finalize|保存并完成]. The daily close is saved and printed.',
            '§Linkly (EFTPOS) settlement',
            'If [d:Also sends Linkly settlement|同时发送 Linkly 结算] shows under the button, saving also settles the card terminal and prints the bank settlement receipt, then stays on the [tab:Linkly Settlement|Linkly 结算] tab to show the result.',
            'Not sent or failed (e.g. terminal offline): check the terminal and the Linkly program, then on [tab:Linkly Settlement|Linkly 结算] tap [k:Settle & Print|结算并打印] and confirm with [k:Settle & Print|结算并打印] in [d:Confirm Linkly settlement|确认刷卡机日结].',
            '§Reprint the daily close or bank settlement',
            'On [tab:Daily Close History|日结历史], pick the business date, select a record and tap [k:Reprint Daily Close|重打印日结单].',
            'Bank settlement receipt: on [tab:Linkly Settlement|Linkly 结算], select the record and tap [k:Reprint Selected Settlement|重打所选结算单].',
          ],
        },
        tips: {
          zh: [
            '清点到一半可点 [k:Return to History|返回历史] 保留草稿，之后点「继续日结」接着做；[k:Discard Draft|放弃草稿] 会清空已输入的数量，无法撤销。',
            '一天可以做多次日结；当天 Linkly 已结算成功后，再次日结不会重复结算。补做以前日期的日结不会发送结算，[k:Settle & Print|结算并打印] 只能结算今天。',
            '只有这台收银机接的是 Linkly 刷卡机、且没开「使用手动刷卡」时，收银端才会发送 Linkly 结算。',
            '提示结算结果未知时不要再次提交：有 [k:Query result from Linkly|向 Linkly 查询结果] 按钮就先点它；仍查不到，由主管对照刷卡机在 [d:Supervisor resolution|主管人工处理] 选 [k:Confirmed succeeded|确认结算成功]、[k:Confirmed failed|确认结算失败] 或 [k:Confirmed not submitted|确认未提交结算]，再点 [k:Confirm decision|确认结论]（只记录结论，不会再次结算）。',
            '日结后如弹出 [d:Restart this POS?|建议重启这台收银机]，可点 [k:Restart now|立即重启]，或点 [k:Later|稍后] 今天不再提醒。',
            '日结记录会上传总部：Web 后台 [k:POS Admin|收银管理] → [k:Daily Closes|日结记录]，或 App 工作台 [k:Daily closes|日结记录] 都能查看。',
          ],
          en: [
            'Halfway through counting? Tap [k:Return to History|返回历史] to keep the draft, then use Continue Daily Close later. [k:Discard Draft|放弃草稿] clears the counts and cannot be undone.',
            'You can close more than once a day; once today’s Linkly settlement has succeeded it is not sent again. A daily close for an earlier date never settles, and [k:Settle & Print|结算并打印] only works for today.',
            'The POS only sends a Linkly settlement when this till uses a Linkly terminal and “Use manual card payment” is off.',
            'If the settlement result is unknown, do not resubmit. Tap [k:Query result from Linkly|向 Linkly 查询结果] if it is shown; if there is still no answer, a supervisor checks the terminal and picks [k:Confirmed succeeded|确认结算成功], [k:Confirmed failed|确认结算失败] or [k:Confirmed not submitted|确认未提交结算] under [d:Supervisor resolution|主管人工处理], then [k:Confirm decision|确认结论] (this only records the outcome; nothing is settled again).',
            'If [d:Restart this POS?|建议重启这台收银机] appears after the close, tap [k:Restart now|立即重启], or [k:Later|稍后] to skip it for today.',
            'Daily closes are uploaded to head office. View them in Web admin under [k:POS Admin|收银管理] → [k:Daily Closes|日结记录], or on the app’s Home under [k:Daily closes|日结记录].',
          ],
        },
        permissions: ['DailyClose.View', 'DailyClose.Save', 'DailyClose.Reprint'],
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
      {
        id: 'scanner-double-scan',
        title: {
          zh: '扫一次商品却加购了两次（或快速连扫被漏掉），怎么调扫码枪灵敏度？',
          en: 'One scan adds the same item twice (or a quick re-scan is missed) — how do I adjust scanner sensitivity?',
        },
        where: {
          zh: '[k:Settings|设置] → [k:Data maintenance|数据维护] → [d:Scanner sensitivity|扫码枪灵敏度]',
          en: '[k:Settings|设置] → [k:Data maintenance|数据维护] → [d:Scanner sensitivity|扫码枪灵敏度]',
        },
        steps: {
          zh: [
            '点 [k:Settings|设置] → [k:Data maintenance|数据维护]，找到 [d:Scanner sensitivity|扫码枪灵敏度]。',
            '拖动滑块，或点 [k:−] / [k:+]，每次调整 100 毫秒；范围 100～1000 毫秒，默认 600 毫秒。改完自动保存，马上生效，不用重启。',
            '同一个商品总是被加购两次：把数值调大一档，再扫一次试试。',
            '正常快速连扫两个相同商品却只进了一个：把数值调小一档。',
          ],
          en: [
            'Tap [k:Settings|设置] → [k:Data maintenance|数据维护] and find [d:Scanner sensitivity|扫码枪灵敏度].',
            'Drag the slider, or tap [k:−] / [k:+]. It moves in steps of 100 ms, from 100 to 1000 ms; the default is 600 ms. Changes save automatically and apply at once, no restart needed.',
            'If the same item keeps being added twice, raise the value one step and scan again.',
            'If scanning two identical items quickly only adds one, lower the value one step.',
          ],
        },
        tips: {
          zh: [
            '含义：同一个条码在这段时间内被扫码枪读到第二次，会当成扫码枪重复读码而忽略；扫不同的商品不受影响。',
            '只对这台收银机生效。数值越大越不容易重复加购，但相同商品要隔得更久才能再扫一次。',
          ],
          en: [
            'How it works: if the scanner reads the same barcode again within this time, the second read is treated as a repeat and ignored. Different items are never affected.',
            'Set per till. A larger value avoids double adds but makes you wait longer before re-scanning the same item.',
          ],
        },
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
        // 门店运维经验（非代码可查）：Windows 托盘的绿色 S、C 两个图标代表刷卡机连接正常；缺了查底座蓝牙与 USB 插口；
        // 都在说明刷卡机死机，Func + Clear 同按重启 ANZ 刷卡机；还不行提供设备号请求远程协助。
        id: 'anz-terminal-unresponsive',
        title: {
          zh: 'ANZ 刷卡机没反应（Linkly 弹窗或收银端刷卡操作无响应）怎么办？',
          en: 'The ANZ terminal is not responding (Linkly pop-up or card actions on the POS do nothing). What do I do?',
        },
        where: { zh: 'Windows 任务栏右下角 · 显示隐藏的图标 [k:^]', en: 'Windows taskbar, bottom right · show hidden icons [k:^]' },
        steps: {
          zh: [
            '§先看右下角的小图标',
            '点 Windows 任务栏右下角的 [k:^]（显示隐藏的图标），展开小图标面板。',
            '找有没有两个绿色小图标：一个是绿底的 S，一个是绿底的 C（不是棕色底的那个 C）。',
            '§绿色 S 和 C 不全：硬件连接有问题',
            '看刷卡机屏幕上的蓝牙是否已和底座正常连接。',
            '检查底座的 USB 线插口有没有松动，或者是否被换插到收银机的其他 USB 口。',
            '§绿色 S 和 C 都在：刷卡机死机了',
            '在刷卡机上同时按住 [k:Func] 键和 [k:Clear] 键，重启刷卡机。',
            '刷卡机重新启动、回到待机画面后，再在收银端重试刚才的刷卡或操作。',
            '§还是不行',
            '提供收银页面底部状态栏 [d:Terminal|终端] 后面的设备号，联系总部 IT 请求远程协助（见「需要远程协助时，如何找到收银机的设备号？」）。',
          ],
          en: [
            '§First check the small icons at the bottom right',
            'Click [k:^] (show hidden icons) at the bottom right of the Windows taskbar to open the icon panel.',
            'Look for two green icons: a green S and a green C (not the brown C).',
            '§Green S or C missing: hardware connection problem',
            'On the terminal screen, check that Bluetooth is connected to the base.',
            'Check that the base’s USB plug is not loose and has not been moved to a different USB port on the till.',
            '§Both green S and C are there: the terminal has frozen',
            'On the terminal, press and hold the [k:Func] and [k:Clear] keys together to restart it.',
            'Once it has restarted and is back on its idle screen, retry the card payment or action on the POS.',
            '§Still not working',
            'Contact HQ IT for remote help and give them the terminal number after [d:Terminal|终端] in the status bar at the bottom of the sale screen (see “Need remote help? How do I find the till’s terminal number?”).',
          ],
        },
        tips: {
          zh: [
            '如果不确定顾客有没有被扣款，重启后不要马上重刷，先按「刷卡失败或异常怎么处理？」到 [k:Card transaction recovery|卡交易异常中心] 核对。',
            '这个检查只适用于连接 ANZ-Linkly 刷卡机的收银机。',
          ],
          en: [
            'If you are not sure whether the customer was charged, do not charge again straight after the restart. Check it in [k:Card transaction recovery|卡交易异常中心] first (see “What do I do when a card payment fails or gets stuck?”).',
            'This check only applies to tills connected to an ANZ-Linkly terminal.',
          ],
        },
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
        // 界面名称对照 apps/web 的 posAdmin.advertisements.*（广告管理页）；播放规则对照 AdvertisementPlaybackService 与收银端客显轮播。
        id: 'advertisement-manage',
        title: { zh: '如何在 Web 后台添加、修改客显广告？', en: 'How do I add or edit customer display ads in Web admin?' },
        where: { zh: 'Web 后台 · [k:POS Admin|收银管理] → [k:Advertisements|广告管理]', en: 'Web admin · [k:POS Admin|收银管理] → [k:Advertisements|广告管理]' },
        steps: {
          zh: [
            '§添加新广告',
            '用电脑登录 Web 后台，打开 [k:POS Admin|收银管理] → [k:Advertisements|广告管理]，点右上角 [p:New Advertisement|新建广告]。',
            '填写 [i:Title|广告标题]，[i:Media Type|媒体类型] 选图片或视频。',
            '在 [d:Media Asset|媒体素材] 区域点 [k:Upload|上传] 选择文件。图片最大 20MB，视频最大 200MB；上传后会自动读取素材尺寸，视频还会自动截一帧当封面。',
            '确认 [i:Layout|版式]：横版在空闲全屏时播放，竖版在收银时右侧广告位播放，通用两处都播。系统会按素材尺寸自动选好，弹窗里的 [d:Customer display preview|客显效果预览] 可以直接看到留边情况。',
            '设置 [i:Start Time|开始时间] 和 [i:End Time|结束时间]（只在这段时间内播放）。',
            '在 [i:Store Scope|分店范围] 选择要播放的分店，至少选一个；可先按品牌筛选再全选。',
            '[i:Sort Order|排序] 数字越小越靠前播放，默认 0；确认 [t:Enabled|启用状态] 已打开，点 [p:OK|确定] 保存。',
            '§修改、停用或删除',
            '在列表里点 [k:Edit|编辑] 修改内容；直接拨动列表里的 [t:Enabled|启用状态] 开关可以马上启用 / 停用；点 [k:Delete|删除] 并确认可删除这条广告。',
            '点眼睛图标 [k:Preview|预览] 可以预览素材，不影响线上播放。',
          ],
          en: [
            '§Add a new ad',
            'On a computer, sign in to Web admin, open [k:POS Admin|收银管理] → [k:Advertisements|广告管理] and click [p:New Advertisement|新建广告] at the top right.',
            'Fill in [i:Title|广告标题] and choose an image or a video as [i:Media Type|媒体类型].',
            'In [d:Media Asset|媒体素材], click [k:Upload|上传] and pick the file. Images can be up to 20MB and videos up to 200MB. The size is read automatically, and a still frame is captured as the cover for videos.',
            'Check [i:Layout|版式]: Landscape plays full screen when idle, Portrait plays in the right-hand ad panel during checkout, and Any plays in both. It is chosen from the media size automatically, and [d:Customer display preview|客显效果预览] in the window shows any borders.',
            'Set [i:Start Time|开始时间] and [i:End Time|结束时间]; the ad only plays between them.',
            'Pick the stores in [i:Store Scope|分店范围] (at least one). You can filter by brand and then select all.',
            'A smaller [i:Sort Order|排序] number plays earlier (default 0). Make sure [t:Enabled|启用状态] is on, then click [p:OK|确定] to save.',
            '§Edit, disable or delete',
            'Click [k:Edit|编辑] in the list to change an ad. Flip the [t:Enabled|启用状态] switch in the list to turn it on or off straight away. Click [k:Delete|删除] and confirm to remove it.',
            'The eye icon [k:Preview|预览] previews the media without affecting what is playing.',
          ],
        },
        tips: {
          zh: [
            '保存后收银端约每 5 分钟刷新一次广告，不用重启收银机。',
            '图片每张停留约 8～10 秒（动图会播完整个循环，最长 30 秒），视频播完再切下一条。',
            '素材比例和版式不符时只会提示、不会拦截保存，但播放时会留黑边；建议横版用 1366×768（16:9），竖版用 772×870。',
            '没有「广告管理」菜单或按钮是灰的，说明账号缺少权限，请联系总部。',
          ],
          en: [
            'Tills refresh ads about every 5 minutes after you save; no restart is needed.',
            'Each image stays about 8–10 seconds (animated images play one full loop, up to 30 seconds); a video plays to the end before the next ad.',
            'A media size that does not match the layout only shows a warning and can still be saved, but it plays with borders. Use 1366×768 (16:9) for Landscape and 772×870 for Portrait.',
            'If the Advertisements menu is missing or the buttons are greyed out, your account lacks the permission. Ask head office.',
          ],
        },
        permissions: ['Advertisements.View', 'Advertisements.Edit'],
      },
      {
        id: 'advertisement-not-playing',
        title: { zh: '客显广告不播放，或播放的和设置的不一样，怎么排查？', en: 'The customer display ads are not playing, or differ from what I set. How do I check?' },
        where: { zh: 'Web 后台 · [k:POS Admin|收银管理] → [k:Advertisements|广告管理]', en: 'Web admin · [k:POS Admin|收银管理] → [k:Advertisements|广告管理]' },
        steps: {
          zh: [
            '在列表里找到这条广告，确认 [t:Enabled|启用状态] 是打开的。',
            '看 [i:Effective Range|有效期]：当前时间必须在开始和结束时间之间，过期或还没到开始时间都不会播放。',
            '看 [i:Store Scope|分店范围] 是否包含这家店；没勾这家店就不会在它的客显上播放。',
            '看 [i:Layout|版式]：空闲全屏只播横版和通用，收银右侧只播竖版和通用。想两处都播就改成「通用」。',
            '等待约 5 分钟让收银端刷新；仍不对时，把店名、广告标题和现象告诉总部 IT。',
          ],
          en: [
            'Find the ad in the list and make sure its [t:Enabled|启用状态] switch is on.',
            'Check [i:Effective Range|有效期]: the current time must be between the start and end time. Expired ads and ads not yet started do not play.',
            'Check [i:Store Scope|分店范围] includes this store. An ad that does not include it will not play on its customer display.',
            'Check [i:Layout|版式]: idle full screen only plays Landscape and Any, and the checkout right panel only plays Portrait and Any. Choose Any to play in both.',
            'Wait about 5 minutes for the till to refresh. If it is still wrong, tell head office IT the store, the ad title and what you see.',
          ],
        },
        tips: {
          zh: [
            '某个位置没有匹配版式的广告时，会退回播放全部已启用且有效的广告，所以可能看到「不该出现」的版式。',
            '同一排序数字的广告，新建的排在前面；收银端一次最多取排序靠前的 20 条。',
            '收银端本身怎么开启 / 调整客显窗口，见上一条「客显（顾客屏）怎么开启……」。',
          ],
          en: [
            'When a slot has no ad of the matching layout, it falls back to all enabled ads in their validity period, so you may see a layout you did not expect there.',
            'Ads with the same sort number play newest first. A till takes at most the first 20 ads by sort order.',
            'To open or resize the customer display window itself, see “How do I open, resize or swap the customer display?” above.',
          ],
        },
        permissions: ['Advertisements.View'],
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
      {
        // 状态栏文案对照 WPF Strings.resx 的 shell.footer.terminalInfo（Terminal: {0} / 终端：{0}）与 shell.footer.currentVersion；
        // 远程维护入口对照 SettingsStrings.resx 的 settings.remoteMaintenance.*。
        id: 'remote-assist',
        title: { zh: '需要远程协助时，如何找到收银机的设备号？', en: 'Need remote help? How do I find the till’s terminal number?' },
        where: { zh: '收银页面底部状态栏 · [d:Terminal|终端]', en: 'Status bar at the bottom of the sale screen · [d:Terminal|终端]' },
        steps: {
          zh: [
            '看收银页面最下方的状态栏，找到 [d:Terminal|终端] 后面的设备号，例如 POS_1022_0841。',
            '联系总部 IT 时，把这个设备号完整告诉对方（字母、数字和下划线都要报全），最好连同旁边的 [d:Current version|当前版本] 一起提供。',
            '保持收银机开机并联网，等待 IT 远程连接。',
          ],
          en: [
            'Look at the status bar along the bottom of the sale screen and find the number after [d:Terminal|终端], e.g. POS_1022_0841.',
            'When you contact HQ IT, give them the full terminal number (letters, digits and underscores), ideally with the [d:Current version|当前版本] shown next to it.',
            'Keep the till switched on and online while IT connects remotely.',
          ],
        },
        tips: {
          zh: [
            '也可以把状态栏这一行拍照或截图发给 IT，注意拍全设备号。',
            '远程维护在 [k:Settings|设置] → [k:Remote maintenance|远程维护] 里查看 [d:Local service status|本机服务状态]：未配置时点 [k:Install remote maintenance|安装远程维护]（Windows 弹出授权窗口时选「是」），服务停止时点 [k:Check and restore|检查并恢复]。',
            '配置好之后，收银员退出登录也不影响 IT 远程维护。',
          ],
          en: [
            'You can also send IT a photo or screenshot of the status bar. Make sure the full terminal number is visible.',
            'Remote maintenance lives in [k:Settings|设置] → [k:Remote maintenance|远程维护], which shows the [d:Local service status|本机服务状态]. If it is not set up, tap [k:Install remote maintenance|安装远程维护] (choose Yes when Windows asks for permission); if the service has stopped, tap [k:Check and restore|检查并恢复].',
            'Once set up, IT can still connect after the cashier signs out.',
          ],
        },
      },
      {
        // 链路依据：移动端改价/改名/建商品直接写 StoreRetailPrice / Product（无下发步骤）；POS API 扫码查询直读库
        // （CatalogService.LookupSellableItemDirectAsync），WPF 加购后后台复核并刷新该行（PosCartService.UpdateLineFromRemote，手工价不覆盖）；
        // 整机目录只在启动时自动同步一次，手动走「数据维护」；服务端目录缓存 20 分钟（CatalogIndexCache）。
        id: 'pda-product-sync',
        title: {
          zh: 'PDA 上改了商品（价格、名称、新建），收银端怎么同步？',
          en: 'I updated a product on the PDA (price, name or new item). How does the POS pick it up?',
        },
        where: {
          zh: 'PDA [k:Products|商品维护] / [k:Prices & labels|价格和标签] → 收银端扫码或 [k:Data Download|数据下载]',
          en: 'PDA [k:Products|商品维护] / [k:Prices & labels|价格和标签] → scan on the POS or [k:Data Download|数据下载]',
        },
        steps: {
          zh: [
            '§在 PDA 上更新商品',
            '改价：在 [k:Products|商品维护] 找到商品，改好分店价后点 [k:Save|保存]（多项时 [k:Save all|保存全部]）或 [k:Save & print|保存并打印]，提示 [d:Product information saved.|商品信息已保存] 即已写入系统。',
            '改名：点 [k:Edit product name|修改商品名称] → [k:Save name|保存名称]；新建：点 [k:Create product|创建商品]，提示 [d:Product created.|商品已创建]。',
            '价格任务：在 [k:Prices & labels|价格和标签] 点 [k:Update only|仅更新] 或 [p:Update & print|更新并打印] 后新价才生效。',
            '§收银端拿到新数据',
            '收银机联网时直接扫这个商品即可：加入购物车后会自动向服务器取最新价格和名称，不用做别的操作。',
            'PDA 新建的商品收银端本地还没有：联网扫码时显示 [d:Looking up item...|正在查询商品...]，查到后直接加入购物车。',
            '改价前已经扫进购物车的商品不会自动变价：先删掉这一行（[g:Swipe left|向左滑] → [k:Remove|移除]），再重新扫码。',
            '想让整台收银机的本地商品数据一次更新：[k:Settings|设置] → [k:Data maintenance|数据维护] → [k:Data Download|数据下载]。',
          ],
          en: [
            '§Update the product on the PDA',
            'Price: find the product in [k:Products|商品维护], change the store price, then tap [k:Save|保存] ([k:Save all|保存全部] for several changes) or [k:Save & print|保存并打印]. [d:Product information saved.|商品信息已保存] means it is in the system.',
            'Name: tap [k:Edit product name|修改商品名称] → [k:Save name|保存名称]. New item: tap [k:Create product|创建商品] until you see [d:Product created.|商品已创建].',
            'Price tasks: the new price only takes effect after [k:Update only|仅更新] or [p:Update & print|更新并打印] in [k:Prices & labels|价格和标签].',
            '§Get the new data on the POS',
            'With the till online, just scan the item. After it is added to the cart, the POS fetches the latest price and name from the server automatically.',
            'A new item from the PDA is not on the till yet: when you scan it online, [d:Looking up item...|正在查询商品...] shows and the item is added once found.',
            'Items scanned before the price change do not reprice by themselves: remove the line ([g:Swipe left|向左滑] → [k:Remove|移除]) and scan it again.',
            'To refresh the till’s whole local product list: [k:Settings|设置] → [k:Data maintenance|数据维护] → [k:Data Download|数据下载].',
          ],
        },
        tips: {
          zh: [
            'PDA 保存后不用另外「下发」；价格任务没处理、或选了 [k:Keep store price|保持本店价]，收银价不会变。',
            '收银机断网时只能用本地已有的数据，本地没有的新商品会提示 [d:No local item found. Checkout can continue offline.|未在本地找到商品，可继续离线结账。]，联网后再扫。',
            '刚改完就点「数据下载」可能还拿到旧数据（服务器商品目录约 20 分钟刷新一次），稍后再试；联网扫码不受影响。',
            '价格不刷新、搜不到商品等本地数据明显不对时，用 [k:Data Reset|数据重置] 重新建立本地商品目录。',
            '收银机每次启动时也会自动同步一次商品数据；购物车里手工改过价的行，扫码刷新不会覆盖你改的价格。',
          ],
          en: [
            'Nothing needs to be published after saving on the PDA. Price tasks left undone, or set to [k:Keep store price|保持本店价], do not change the POS price.',
            'Offline, the till only has its local data; a new item it does not have shows [d:No local item found. Checkout can continue offline.|未在本地找到商品，可继续离线结账。]. Scan it again once online.',
            'Data Download right after a change may still bring old data (the server’s product catalogue refreshes about every 20 minutes); try again later. Scanning online is not affected.',
            'If local data is clearly wrong (prices not refreshing, items not found), use [k:Data Reset|数据重置] to rebuild the local product catalogue.',
            'The till also syncs products automatically each time it starts. A cart line whose price you changed by hand keeps your price when it is refreshed.',
          ],
        },
        permissions: ['Settings.CatalogDownload', 'Settings.CatalogReset'],
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
        id: 'revenue-report',
        title: { zh: '如何使用报表里的营业额报告？', en: 'How do I use the Revenue Report?' },
        where: { zh: '底部 [tab:Reports|报表] · [tab:Revenue Report|营业额报告]', en: 'Bottom [tab:Reports|报表] tab · [tab:Revenue Report|营业额报告]' },
        steps: {
          zh: [
            '§进入与选范围',
            '点底部 [tab:Reports|报表]，页面默认就是 [tab:Revenue Report|营业额报告]。',
            '切换 [k:Day|日]、[k:Week|周]、[k:Month|月]；点 [k:All stores|全部分店] 选一家分店，不选就是全部分店。',
            '点 [i:Date|日期] 选日期；也可用 [k:Previous|上一期]、[k:Next|下一期] 前后翻，或点快捷 [k:Today|今日]、[k:Yesterday|昨日]（周、月模式是 [k:This week|本周]、[k:Last week|上周]、[k:This month|本月]、[k:Last month|上月]）。',
            '§看今天卖了多少（日模式）',
            '[d:Cumulative revenue|累计营业额] 卡片显示截至目前的累计金额、增长率，以及比去年同一时刻多或少多少。',
            '点下方 [d:Cumulative change by hour|逐小时累计涨跌] 里的某个整点，卡片和下面的排行就只算到那个整点，对比去年同一时刻。',
            '§看各分店',
            '[d:Store ranking|分店排行] 按营业额从高到低列出每家分店的 [d:Revenue|营业额]、[d:Txns|客单数]、[d:AOV|客单价]，每项下面小字是去年同期；点放大镜可按店名或编号搜索。',
            '点任一分店：日模式打开 [d:Hourly revenue|分时营业额]，周、月模式打开 [d:Daily revenue|每日营业额]，右上角 × 关闭。',
            '分时营业额里可在 [k:Cumulative|累计] 和 [k:Per hour|逐小时] 之间切换。',
            '页面最底部的 [d:All-store summary|全店汇总] 是所选分店合计的营业额、客单数、客单价，含 [d:Current|本期]、[d:LY|同期]、[d:Growth|增长率]。',
          ],
          en: [
            '§Open it and set the range',
            'Tap the [tab:Reports|报表] tab. It opens on [tab:Revenue Report|营业额报告].',
            'Switch [k:Day|日], [k:Week|周] or [k:Month|月]. Tap [k:All stores|全部分店] to pick one store; leave it to see all stores.',
            'Tap [i:Date|日期] to choose a date, or use [k:Previous|上一期] and [k:Next|下一期]. The shortcuts are [k:Today|今日] and [k:Yesterday|昨日] (in Week and Month: [k:This week|本周], [k:Last week|上周], [k:This month|本月], [k:Last month|上月]).',
            '§Today so far (Day view)',
            'The [d:Cumulative revenue|累计营业额] card shows the total so far, the growth rate, and how far ahead or behind you are compared with the same time last year.',
            'Tap an hour in [d:Cumulative change by hour|逐小时累计涨跌] and the card and ranking below count only up to that hour, compared with the same hour last year.',
            '§Store by store',
            '[d:Store ranking|分店排行] lists stores by revenue, highest first, with [d:Revenue|营业额], [d:Txns|客单数] and [d:AOV|客单价]. The small figure under each is last year. Tap the magnifier to search a store name or code.',
            'Tap a store: in Day view you get [d:Hourly revenue|分时营业额]; in Week and Month you get [d:Daily revenue|每日营业额]. Close it with × at the top right.',
            'In hourly revenue you can switch between [k:Cumulative|累计] and [k:Per hour|逐小时].',
            'At the very bottom, [d:All-store summary|全店汇总] totals the chosen stores’ revenue, transactions and AOV as [d:Current|本期], [d:LY|同期] and [d:Growth|增长率].',
          ],
        },
        tips: {
          zh: [
            '同期是去年同一星期几（日）、去年同一周（周）、去年同月（月）；没有环比。当天、当周、当月只统计到今天。',
            '增长率 = (本期 − 同期) ÷ 同期。同期为 0 而本期有销售显示「新增」；两边都为 0 显示「--」。',
            '顶部「最近统计」是后台最后一次汇总的时间，数据不是实时的；点右上角 [k:Refresh|刷新] 或下拉刷新。显示「统计补算中」时稍等，会自动重试；去年数据还在补算时，同期和增长率会暂时显示「—」，稍后刷新即可。',
            '营业额已扣除退款；客单数只数已支付和分期付款的订单。',
            '店长管理的分店有超过 7 天的价格任务时，进入报表会被带到 [k:Prices & labels|价格和标签] 的超期列表，处理完自动解锁，再点 [k:Open reports|去看报表]。',
          ],
          en: [
            'The comparison is the same weekday last year (Day), the same week last year (Week) or the same month last year (Month). There is no period-on-period view. The current day, week or month counts up to today only.',
            'Growth = (current − LY) ÷ LY. If LY is 0 and you have sales it shows “New”; if both are 0 it shows “--”.',
            'The “Last updated” line at the top is when the back office last finished its calculation, so figures are not live. Tap [k:Refresh|刷新] at the top right or pull down. If it says statistics are being refreshed, wait; it retries by itself. While last year’s figures are still updating, LY and growth show “—”; refresh later.',
            'Revenue is net of refunds; transactions count paid and lay-by orders only.',
            'If a store manager’s store has price tasks overdue by 7 days, opening Reports takes you to the overdue list in [k:Prices & labels|价格和标签]. It unlocks when they are done; then tap [k:Open reports|去看报表].',
          ],
        },
        permissions: ['Reports.ProductMovement.View'],
      },
      {
        id: 'product-report',
        title: { zh: '如何使用报表里的商品报告？', en: 'How do I use the Product Report?' },
        where: { zh: '底部 [tab:Reports|报表] · [tab:Product Report|商品报告]', en: 'Bottom [tab:Reports|报表] tab · [tab:Product Report|商品报告]' },
        steps: {
          zh: [
            '§进入与选范围',
            '点底部 [tab:Reports|报表]，在顶部切到 [tab:Product Report|商品报告]。',
            '先选 [tab:Australian suppliers|澳洲供应商] 或 [tab:China suppliers|中国供应商]。',
            '点 [k:All stores|全部分店] 选一家分店；日期点快捷 [k:Today|今天]、[k:Yesterday|昨天]、[k:This week|本周]、[k:Last week|上周]、[k:This month|本月]、[k:Last month|上月]，或在 [i:Start date|开始日期]、[i:End date|结束日期] 里按 YYYY-MM-DD 手输。',
            '§看供应商和商品',
            '[d:Suppliers|供应商] 表默认按金额从高到低排；点表头可按金额、数量、均价排序，再点同一列切换升降序。',
            '点供应商名称，下面的 [d:Product details|商品明细] 只显示这个供应商的商品，再点一次取消。',
            '点供应商的金额，打开 [d:Supplier by store|供应商分店数据]，看这个供应商在各分店的数据。',
            '点商品明细里的一行，打开 [d:Product by store|商品分店数据]，看这个商品在各分店的数量、金额和增长率。',
            '在商品明细里输入 [i:Item no. or barcode|货号或条码]，点 [k:Search|查询] 只看一个商品；点 [k:Clear search|清除查询] 恢复。',
            '表格太长时点 [k:Previous|上一页]、[k:Next|下一页] 翻页，也可以改 [d:Per page|每页] 条数。表格左滑还能看均价、毛利和增长率。',
            '§仅中国供应商',
            '[d:China revenue|中国货营业额] 卡片显示中国货营业额、占总营业额的比例和中国货毛利率，并与同期对比。',
            '[d:China share by store|分店中国货占比] 表按「中国货金额 ÷ 分店营业额」列出每家分店，默认显示前 8 家，点 [k:Show all N stores|展开全部 N 家] 看全部。',
            '点其中一家分店，供应商表和商品明细会只显示这家分店；再点一次取消。',
          ],
          en: [
            '§Open it and set the range',
            'Tap the [tab:Reports|报表] tab, then switch to [tab:Product Report|商品报告] at the top.',
            'Choose [tab:Australian suppliers|澳洲供应商] or [tab:China suppliers|中国供应商] first.',
            'Tap [k:All stores|全部分店] to pick one store. For dates, tap [k:Today|今天], [k:Yesterday|昨天], [k:This week|本周], [k:Last week|上周], [k:This month|本月] or [k:Last month|上月], or type [i:Start date|开始日期] and [i:End date|结束日期] as YYYY-MM-DD.',
            '§Read suppliers and products',
            'The [d:Suppliers|供应商] table is sorted by amount, highest first. Tap a column header to sort by amount, quantity or average price; tap it again to flip the order.',
            'Tap a supplier name and [d:Product details|商品明细] shows only that supplier’s products. Tap it again to clear.',
            'Tap a supplier’s amount to open [d:Supplier by store|供应商分店数据] and see that supplier in each store.',
            'Tap a row in product details to open [d:Product by store|商品分店数据]: quantity, amount and growth in each store.',
            'In product details type an [i:Item no. or barcode|货号或条码] and tap [k:Search|查询] to see one product; tap [k:Clear search|清除查询] to go back.',
            'For long tables use [k:Previous|上一页] and [k:Next|下一页], or change [d:Per page|每页]. Swipe a table left to see average price, margin and growth.',
            '§China suppliers only',
            'The [d:China revenue|中国货营业额] card shows China revenue, its share of total revenue and China gross margin, each compared with the same period last year.',
            'The [d:China share by store|分店中国货占比] table lists each store as China sales ÷ store revenue. It shows the top 8; tap [k:Show all N stores|展开全部 N 家] for the rest.',
            'Tap a store there and the supplier table and product details show only that store. Tap it again to clear.',
          ],
        },
        tips: {
          zh: [
            '改日期或点快捷日期会清掉已选的供应商，已选的分店会保留。',
            '顶部「最近统计」是后台最后一次汇总的时间，数据不是实时的；点右上角 [k:Refresh|刷新] 或下拉刷新。显示「统计补算中」时稍等，会自动重试。',
            '店长只能看到自己关联的分店；管理员和仓库管理可以看全部启用收银的分店。',
          ],
          en: [
            'Changing the dates, or tapping a date shortcut, clears the chosen supplier. The chosen store stays.',
            'The “Last updated” line at the top is when the back office last finished its calculation, so figures are not live. Tap [k:Refresh|刷新] at the top right or pull down. If it says statistics are being refreshed, wait; it retries by itself.',
            'Store managers only see their own stores. Admins and warehouse managers see every cashier-enabled store.',
          ],
        },
        permissions: ['Reports.ProductMovement.View'],
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
            '在商品页底部点 [k:Regular|普通]、[k:Small|小标签]、[k:Discount|折扣] 或 [k:Large|大标签] 打印标签；左边的打印机图标用颜色表示连接状态。',
            '改份数：点左边的打印机图标打开 [d:Print settings|打印设置]，调 [i:Print quantity|打印数量]；连打印机：[k:Printer settings|打印机设置] → [k:Scan for printers|扫描打印机] → [k:Connect|连接]。',
          ],
          en: [
            'In the Product purchasing group on Home, tap [k:Local supplier invoices|供应商发票].',
            'Search with [i:Search invoice no.|搜索发票号], or filter by [tab:Not received|未入库], [tab:Partially received|部分入库] or [tab:Received|已入库]. Store, supplier and dates are under [k:More filters|更多筛选].',
            'Tap an invoice to open [d:Invoice details|发票明细]: last cost, cost, quantity and subtotal per line. Use [tab:Price up|涨价] or [tab:Price down|减价] to see only price changes.',
            '§Print product labels',
            'Tap [k:Edit product|商品编辑] on a line. The product page opens with that item.',
            'At the bottom, tap [k:Reg|普通], [k:Small|小标签], [k:Disc|折扣] or [k:Large|大标签] to print. The printer icon on the left shows the connection status by colour.',
            'To change copies, tap the printer icon on the left to open [d:Print settings|打印设置] and set [i:Print quantity|打印数量]. To connect a printer: [k:Printer settings|打印机设置] → [k:Scan for printers|扫描打印机] → [k:Connect|连接].',
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
