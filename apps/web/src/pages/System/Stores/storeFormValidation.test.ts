import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'

function assertIncludes(source: string, expected: string, label: string) {
  if (!source.includes(expected)) {
    throw new Error(`${label}. Missing: ${expected}`)
  }
}

function assertNotIncludes(source: string, unexpected: string, label: string) {
  if (source.includes(unexpected)) {
    throw new Error(`${label}. Unexpected: ${unexpected}`)
  }
}

function assertOccurrenceAtLeast(source: string, expected: string, count: number, label: string) {
  const actual = source.split(expected).length - 1
  if (actual < count) {
    throw new Error(`${label}. Expected at least ${count}, actual ${actual}. Missing: ${expected}`)
  }
}

function assertOccurrenceExactly(source: string, expected: string, count: number, label: string) {
  const actual = source.split(expected).length - 1
  if (actual !== count) {
    throw new Error(`${label}. Expected exactly ${count}, actual ${actual}. Value: ${expected}`)
  }
}

const pageSource = readFileSync(resolve('src/pages/System/Stores/index.tsx'), 'utf8')
// 新建与编辑弹窗共用 StoreFormFields：字段定义与前端校验集中在这里，页面只负责渲染两次。
const formFieldsSource = readFileSync(resolve('src/pages/System/Stores/StoreFormFields.tsx'), 'utf8')
const detailPageSource = readFileSync(resolve('src/pages/System/Stores/Detail.tsx'), 'utf8')
const timeZoneOptionsSource = readFileSync(resolve('src/pages/System/Stores/timeZoneOptions.ts'), 'utf8')
const storeTypesSource = readFileSync(resolve('src/types/store.ts'), 'utf8')
const zhSource = readFileSync(resolve('src/i18n/locales/zh.json'), 'utf8')
const enSource = readFileSync(resolve('src/i18n/locales/en.json'), 'utf8')

assertOccurrenceAtLeast(
  pageSource,
  '<StoreFormFields',
  2,
  '创建和编辑分店弹窗都必须使用共用表单，保证两处字段与校验一致',
)
assertIncludes(
  formFieldsSource,
  "max: 20",
  '分店编辑表单应在前端限制联系电话最大长度，避免提交后才收到 400',
)
assertIncludes(
  formFieldsSource,
  "t('system.stores.contactPhoneMaxLength'",
  '联系电话长度校验应使用分店模块自己的友好提示文案',
)
assertIncludes(
  zhSource,
  '"contactPhoneMaxLength": "联系电话不能超过 20 个字符"',
  '中文文案应明确说明联系电话最大长度',
)
assertIncludes(
  enSource,
  '"contactPhoneMaxLength": "Contact phone cannot exceed 20 characters"',
  '英文文案应明确说明联系电话最大长度',
)
assertIncludes(
  pageSource,
  'record.abn',
  '分店列表应显示 ABN（并入「品牌 / ABN」列第二行），方便列表直接核对商业号码',
)
assertIncludes(
  pageSource,
  "t('system.stores.brandAbn')",
  '品牌与 ABN 合并列应使用分店模块统一文案',
)
assertIncludes(
  formFieldsSource,
  'name="abn"',
  '创建和编辑分店表单应提供 ABN 输入项',
)
assertIncludes(
  pageSource,
  "t('system.stores.abn')",
  'ABN 展示和表单标签应使用分店模块统一文案',
)
assertIncludes(
  pageSource,
  'detailStore.abn',
  '列表页内详情弹窗应展示 ABN 字段',
)
assertIncludes(
  detailPageSource,
  'store.abn',
  '独立分店详情页应展示 ABN 字段',
)
assertIncludes(
  detailPageSource,
  "t('system.stores.abn')",
  '独立分店详情页 ABN 标签应使用分店模块统一文案',
)
assertIncludes(
  zhSource,
  '"abn": "ABN"',
  '中文文案应包含 ABN 标签',
)
assertIncludes(
  zhSource,
  '"abnMaxLength": "ABN 不能超过 20 个字符"',
  '中文文案应明确说明 ABN 最大长度',
)
assertIncludes(
  enSource,
  '"abn": "ABN"',
  '英文文案应包含 ABN 标签',
)
assertIncludes(
  enSource,
  '"abnMaxLength": "ABN cannot exceed 20 characters"',
  '英文文案应明确说明 ABN 最大长度',
)

assertIncludes(
  timeZoneOptionsSource,
  "value: 'Australia/Brisbane', label: 'Australia/Brisbane (Queensland)'",
  '时区选项应包含 Queensland 的 Australia/Brisbane',
)
assertIncludes(
  timeZoneOptionsSource,
  "value: 'Australia/Sydney', label: 'Australia/Sydney (New South Wales)'",
  '时区选项应包含 New South Wales 的 Australia/Sydney',
)
assertIncludes(
  timeZoneOptionsSource,
  "value: 'Australia/Melbourne', label: 'Australia/Melbourne (Victoria)'",
  '时区选项应包含 Victoria 的 Australia/Melbourne',
)
assertOccurrenceExactly(
  timeZoneOptionsSource,
  "value: 'Australia/",
  3,
  '共享分店时区选项只能包含三个已批准的 IANA 时区',
)
assertOccurrenceExactly(
  storeTypesSource,
  'timeZoneId?: string',
  5,
  '列表查询、StoreDto、CreateStoreDto、UpdateStoreDto 和批量请求都应声明可选时区字段',
)
assertIncludes(
  formFieldsSource,
  'name="timeZoneId"',
  '创建和编辑分店表单应提供时区选择项',
)
assertIncludes(
  formFieldsSource,
  "t('system.stores.timeZoneRequired')",
  '分店时区必填应使用模块自己的提示文案',
)
assertIncludes(
  pageSource,
  'timeZoneId: detail.timeZoneId',
  '编辑分店时应回填服务端返回的时区，缺失时不得猜测默认值',
)
assertIncludes(
  pageSource,
  "dataIndex: 'timeZoneId'",
  '分店列表应显示时区列',
)
assertOccurrenceExactly(
  pageSource,
  "fixed: 'left'",
  2,
  '分店名称和分店编码应固定在表格左侧（序号列已移除）',
)
assertIncludes(
  pageSource,
  "fixed: 'right'",
  '操作列应固定在表格右侧',
)
assertIncludes(
  pageSource,
  'fixed: true',
  '批量勾选列应与左侧关键列一起固定',
)
assertIncludes(
  pageSource,
  'timeZoneFilterOptions',
  '分店列表应构造受控时区筛选选项',
)
assertIncludes(
  pageSource,
  'value={timeZoneFilter}',
  '时区筛选应是绑定单个值的下拉',
)
assertNotIncludes(
  pageSource,
  'mode="multiple"',
  '时区筛选应限制为单选',
)
assertIncludes(
  pageSource,
  'UNSET_STORE_TIME_ZONE_FILTER',
  '时区筛选应包含未设置分店的保留标识',
)
assertIncludes(
  pageSource,
  'timeZoneId: query.timeZone',
  '时区筛选应作为服务端分页查询参数提交',
)
assertIncludes(
  pageSource,
  'detailStore.timeZoneId',
  '列表页内详情弹窗应展示时区字段',
)
assertIncludes(
  detailPageSource,
  'store.timeZoneId',
  '独立分店详情页应展示时区字段',
)
assertIncludes(
  zhSource,
  '"timeZone": "时区"',
  '中文文案应包含时区标签',
)
assertIncludes(
  zhSource,
  '"timeZoneRequired": "请选择时区"',
  '中文文案应包含时区必填提示',
)
assertIncludes(
  enSource,
  '"timeZone": "Time Zone"',
  '英文文案应包含时区标签',
)
assertIncludes(
  enSource,
  '"timeZoneRequired": "Please select a time zone"',
  '英文文案应包含时区必填提示',
)
assertIncludes(
  zhSource,
  '"timeZoneUnset": "未设置"',
  '中文文案应包含未设置时区筛选项',
)
assertIncludes(
  enSource,
  '"timeZoneUnset": "Not set"',
  '英文文案应包含未设置时区筛选项',
)

assertIncludes(
  formFieldsSource,
  'name="returnPolicy"',
  '创建和编辑分店表单都应提供退换货政策文本域',
)
assertIncludes(
  formFieldsSource,
  "t('system.stores.returnPolicy'",
  '退换货政策表单标签应使用分店模块统一文案',
)
assertIncludes(
  formFieldsSource,
  "t('system.stores.returnPolicyMaxLength'",
  '退换货政策长度校验应使用分店模块自己的友好提示文案',
)
assertIncludes(
  pageSource,
  'returnPolicy: detail.returnPolicy',
  '编辑分店时应回填服务端返回的退换货政策',
)
assertIncludes(
  pageSource,
  'detailStore.returnPolicy',
  '列表页内详情弹窗应展示退换货政策字段',
)
assertIncludes(
  pageSource,
  "whiteSpace: 'pre-wrap'",
  '列表页内详情弹窗应保留退换货政策换行',
)
assertIncludes(
  detailPageSource,
  'store.returnPolicy',
  '独立分店详情页应展示退换货政策字段',
)
assertIncludes(
  detailPageSource,
  "whiteSpace: 'pre-wrap'",
  '独立分店详情页应保留退换货政策换行',
)
assertIncludes(
  detailPageSource,
  "t('system.stores.returnPolicy'",
  '独立分店详情页退换货政策标签应使用分店模块统一文案',
)
assertOccurrenceExactly(
  storeTypesSource,
  'returnPolicy?: string',
  4,
  'StoreDto、CreateStoreDto、UpdateStoreDto 和批量请求都应声明可选退换货政策字段',
)
assertIncludes(
  zhSource,
  '"returnPolicy": "退换货政策"',
  '中文文案应包含退换货政策标签',
)
assertIncludes(
  zhSource,
  '"returnPolicyMaxLength": "退换货政策不能超过 500 个字符"',
  '中文文案应明确说明退换货政策最大长度',
)
assertIncludes(
  enSource,
  '"returnPolicy": "Return Policy"',
  '英文文案应包含退换货政策标签',
)
assertIncludes(
  enSource,
  '"returnPolicyMaxLength": "Return policy cannot exceed 500 characters"',
  '英文文案应明确说明退换货政策最大长度',
)

// 代金券使用说明 / 分期条款（总部按门店维护，点「下发」后到三端收银）。
// 文案放页面级消息文件，不进全局语言包（首屏 gzip 预算很紧）。
const zhPageMessages = readFileSync(resolve('src/pages/System/Stores/storesMessages.zh.json'), 'utf8')
const enPageMessages = readFileSync(resolve('src/pages/System/Stores/storesMessages.en.json'), 'utf8')
const termsDefaultsSource = readFileSync(resolve('src/pages/System/Stores/receiptTermsDefaults.ts'), 'utf8')
for (const [field, label] of [['voucherTerms', '代金券使用说明'], ['installmentTerms', '分期条款']] as const) {
  assertIncludes(
    formFieldsSource,
    `name="${field}"`,
    `创建和编辑分店表单都应提供 ${label} 文本域`,
  )
  assertIncludes(
    formFieldsSource,
    `<CountedLabel name="${field}"`,
    `${label} 应带字数计数，超限即时变红`,
  )
  assertIncludes(
    formFieldsSource,
    `t('system.stores.${field}MaxLength')`,
    `${label} 长度校验应使用页面级友好提示文案`,
  )
  assertIncludes(
    pageSource,
    `${field}: detail.${field}`,
    `编辑分店时应回填服务端返回的 ${label}`,
  )
  assertIncludes(
    pageSource,
    `detailStore.${field}`,
    `列表页内详情抽屉应展示 ${label}`,
  )
  assertIncludes(
    pageSource,
    `apply${field[0].toUpperCase()}${field.slice(1)}`,
    `批量弹窗应为 ${label} 提供独立修改开关`,
  )
  assertIncludes(
    detailPageSource,
    `store.${field}`,
    `独立分店详情页应展示 ${label}`,
  )
  assertOccurrenceExactly(
    storeTypesSource,
    `${field}?: string`,
    4,
    `StoreDto、CreateStoreDto、UpdateStoreDto 和批量请求都应声明可选的 ${field}`,
  )
  // 全局语言包里不得出现（页面级消息文件才是它们的家）；页面级中英文都要有，且上限写明 600。
  assertNotIncludes(zhSource, `"${field}"`, `${field} 文案不应进全局中文语言包`)
  assertNotIncludes(enSource, `"${field}"`, `${field} 文案不应进全局英文语言包`)
  assertIncludes(zhPageMessages, `"${field}":`, `${field} 应有页面级中文文案`)
  assertIncludes(enPageMessages, `"${field}":`, `${field} 应有页面级英文文案`)
}
assertIncludes(zhPageMessages, '"voucherTermsMaxLength": "代金券使用说明不能超过 600 个字符"', '中文文案应明确说明代金券使用说明最大长度')
assertIncludes(zhPageMessages, '"installmentTermsMaxLength": "分期条款不能超过 600 个字符"', '中文文案应明确说明分期条款最大长度')
assertIncludes(enPageMessages, '"voucherTermsMaxLength": "Voucher terms cannot exceed 600 characters"', '英文文案应明确说明代金券使用说明最大长度')
assertIncludes(enPageMessages, '"installmentTermsMaxLength": "Installment terms cannot exceed 600 characters"', '英文文案应明确说明分期条款最大长度')
// 表单里的 placeholder 展示收银端内置默认文案，批量弹窗同理；长度上限与后端 / 收银端同值，只在常量文件里定义一次。
assertIncludes(formFieldsSource, 'placeholder={DEFAULT_VOUCHER_TERMS_TEXT}', '代金券使用说明输入框应以默认文案作为占位提示')
assertIncludes(formFieldsSource, 'placeholder={DEFAULT_INSTALLMENT_TERMS_TEXT}', '分期条款输入框应以默认文案作为占位提示')
assertIncludes(pageSource, 'placeholder={DEFAULT_VOUCHER_TERMS_TEXT}', '批量修改的代金券使用说明应以默认文案作为占位提示')
assertIncludes(pageSource, 'placeholder={DEFAULT_INSTALLMENT_TERMS_TEXT}', '批量修改的分期条款应以默认文案作为占位提示')
assertIncludes(termsDefaultsSource, 'RECEIPT_TERMS_MAX_LENGTH = 600', '两个字段的长度上限应为 600')
assertNotIncludes(formFieldsSource, 'max: 600', '长度上限必须引用 RECEIPT_TERMS_MAX_LENGTH，不要在表单里再写一份字面量')

assertIncludes(
  pageSource,
  'usePermission(P.Stores.Edit)',
  '只有拥有 Stores.Edit 权限的用户才能看到批量选择能力',
)
assertIncludes(
  pageSource,
  'preserveSelectedRowKeys: true',
  '批量选择应在翻页时保留',
)
assertIncludes(
  pageSource,
  "shouldClearStoreSelection('filter')",
  '品牌或状态筛选范围变化时应清空隐藏选择',
)
assertIncludes(
  pageSource,
  "t('system.stores.batchEdit')",
  '工具栏应提供批量修改入口',
)
assertIncludes(
  pageSource,
  'buildBatchUpdateStoresRequest(selectedStoreGuids',
  '批量弹窗提交应通过纯逻辑构造明确字段请求',
)
assertIncludes(
  pageSource,
  'applyTimeZoneId',
  '批量弹窗应为时区提供独立修改开关',
)
assertIncludes(
  pageSource,
  'applyAbn',
  '批量弹窗应为 ABN 提供独立修改开关',
)
assertIncludes(
  pageSource,
  'applyBrandName',
  '批量弹窗应为品牌提供独立修改开关',
)
assertIncludes(
  pageSource,
  'applyIsActive',
  '批量弹窗应把收银状态的修改开关与 Switch 分离',
)
assertIncludes(
  pageSource,
  'applyReturnPolicy',
  '批量弹窗应为退换货政策提供独立修改开关',
)
assertIncludes(
  zhSource,
  '"batchEdit": "批量修改"',
  '中文文案应包含批量修改入口',
)
assertIncludes(
  enSource,
  '"batchEdit": "Batch Edit"',
  '英文文案应包含批量修改入口',
)
assertIncludes(
  zhSource,
  '"batchCashRegisterDisableWarning"',
  '中文文案应包含批量停用收银警告',
)
assertIncludes(
  enSource,
  '"batchCashRegisterDisableWarning"',
  '英文文案应包含批量停用收银警告',
)

// 筛选/排序用「对象合并」传入查询：显式传 undefined 表示清除该筛选。
// 若退回「默认参数 = 当前筛选」的写法，undefined 会被默认值吞掉，清除品牌/时区/收银筛选就不会生效。
assertIncludes(
  pageSource,
  '...overrides',
  '列表查询应用对象合并，保证显式 undefined 能清除筛选',
)
assertNotIncludes(
  pageSource,
  'nextBrandFilter = brandFilter',
  '不得用默认参数承载筛选值，否则清除筛选会被默认值吞掉',
)

console.log('storeFormValidation.test: ok')
