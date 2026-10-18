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
    // 在 tiles 里需要红框强调的下标：退款、取单、卡交易异常中心、设置
    highlightedTiles: [1, 3, 8, 10],
  },
  mobile: {
    home: { en: 'Home', zh: '工作台' },
    groups: [
      { title: { en: 'Product purchasing', zh: '商品进货' }, tiles: [{ en: 'HB new arrivals', zh: 'HB新品', hl: true }, { en: 'Local supplier invoices', zh: '澳洲进货' }, { en: 'China purchasing', zh: '中国采购' }] },
      { title: { en: 'Product sales', zh: '商品销售' }, tiles: [{ en: 'Product activity', zh: '商品进销查询', hl: true }, { en: 'Sales Records', zh: '销售订单', hl: true }, { en: 'Lay-bys', zh: '分期订单' }] },
      { title: { en: 'Operations & reports', zh: '运营与报表' }, tiles: [{ en: 'Reports', zh: '报表中心', hl: true }, { en: 'Daily closes', zh: '日结记录', hl: true }, { en: 'Employee operation logs', zh: '员工操作日志', hl: true }] },
      { title: { en: 'Staff & admin', zh: '人员与管理' }, tiles: [{ en: 'Staff', zh: '人员管理', hl: true }, { en: 'Install app', zh: 'App 安装', hl: true }, { en: 'Attendance', zh: '考勤' }] },
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
    title: { en: 'Products', zh: '商品' },
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
    ],
  },
  {
    id: 'app',
    title: { en: 'App download', zh: 'App 下载' },
    questions: [
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
