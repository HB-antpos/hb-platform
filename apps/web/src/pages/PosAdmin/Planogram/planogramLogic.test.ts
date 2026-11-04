import type { HookModelDto, ProductSpecListItemDto, ShelfModelDto } from '../../../types/planogram'
import { autoHoleGrid } from './engine/holeGrid'
import {
  createOpenKeyAllocator,
  defaultHoleSpanX,
  defaultHookFormValues,
  defaultShelfFormValues,
  effectivePage,
  extractFieldErrors,
  formatDimensions,
  formatNumber,
  formatUpdatedAt,
  hookCopyFormValues,
  hookFormToInput,
  hookToFormValues,
  hookToggleInput,
  listHookQty,
  parseIsoAsUtc,
  parsePlanogramSearch,
  PLANOGRAM_DESIGNER_PATTERN,
  planogramDesignerPath,
  planogramErrorText,
  resolvePlanogramError,
  serializePlanogramQuery,
  shelfCopyFormValues,
  shelfFormToInput,
  shelfToFormValues,
  shelfToggleInput,
  specFormToDto,
  specTableHooks,
  specToFormValues,
} from './planogramLogic'

// 页面逻辑层：地址栏、错误解析、时间 / 数字展示、表单 ↔ 请求体转换、挂数列表展示。

function assertEqual<T>(actual: T, expected: T, message: string) {
  if (actual !== expected) {
    throw new Error(`${message}. Expected: ${String(expected)}, received: ${String(actual)}`)
  }
}

function assert(condition: unknown, message: string): asserts condition {
  if (!condition) throw new Error(message)
}

function assertDeepEqual(actual: unknown, expected: unknown, message: string) {
  const a = JSON.stringify(actual)
  const e = JSON.stringify(expected)
  if (a !== e) throw new Error(`${message}. Expected: ${e}, received: ${a}`)
}

// ---------- 地址栏 ----------
assertDeepEqual(parsePlanogramSearch(''), { tab: 'plans', view: 'shelf' }, '默认：陈列方案（批 2 起排在最前）/ 货架型号')
assertDeepEqual(parsePlanogramSearch('?tab=specs'), { tab: 'specs', view: 'shelf' }, '商品陈列参数页签带 tab=specs')
assertDeepEqual(parsePlanogramSearch('?tab=models&view=hook'), { tab: 'models', view: 'hook' }, '型号页签的挂钩子页')
assertDeepEqual(parsePlanogramSearch('tab=models'), { tab: 'models', view: 'shelf' }, '不带问号也能解析')
assertDeepEqual(parsePlanogramSearch('?tab=evil&view=<x>'), { tab: 'plans', view: 'shelf' }, '未知值回到默认')
assertEqual(serializePlanogramQuery({ tab: 'plans', view: 'shelf' }), '', '默认值不写进地址栏')
assertEqual(serializePlanogramQuery({ tab: 'plans', view: 'hook' }), '', '陈列方案页签不带 view')
assertEqual(serializePlanogramQuery({ tab: 'specs', view: 'hook' }), 'tab=specs', '商品参数页签不带 view')
assertEqual(serializePlanogramQuery({ tab: 'models', view: 'shelf' }), 'tab=models', '货架型号子页是默认，不带 view')
assertEqual(serializePlanogramQuery({ tab: 'models', view: 'hook' }), 'tab=models&view=hook', '挂钩型号')
for (const query of [{ tab: 'models', view: 'hook' }, { tab: 'models', view: 'shelf' }, { tab: 'specs', view: 'shelf' }, { tab: 'plans', view: 'shelf' }] as const) {
  assertDeepEqual(parsePlanogramSearch(serializePlanogramQuery(query)), query, '序列化再解析应还原')
}

// ---------- 错误解析 ----------
function requestError(status: number, payload: unknown) {
  return Object.assign(new Error('x'), { name: 'RequestError', status, payload })
}
const inUse = resolvePlanogramError(requestError(409, { errorCode: 'PLANOGRAM_MODEL_IN_USE', message: '被引用' }))
assertEqual(inUse.kind === 'error' && inUse.key, 'planogram.errors.modelInUse', 'MODEL_IN_USE → 提示改为停用')
const dup = resolvePlanogramError(requestError(400, { code: 'PLANOGRAM_CODE_DUPLICATE' }))
assertEqual(dup.kind === 'error' && dup.key, 'planogram.errors.codeDuplicate', 'code 字段也认')
assertEqual(resolvePlanogramError(requestError(400, { errorCode: 'PLANOGRAM_VALIDATION' })).kind === 'error', true, 'VALIDATION')
const notFound = resolvePlanogramError(requestError(404, { errorCode: 'PLANOGRAM_NOT_FOUND' }))
assertEqual(notFound.kind === 'error' && notFound.key, 'planogram.errors.notFound', 'NOT_FOUND')
const forbidden = resolvePlanogramError(requestError(403, {}))
assertEqual(forbidden.kind === 'error' && forbidden.key, 'planogram.errors.forbidden', '403 → 无权限')
const unknown = resolvePlanogramError(requestError(500, { errorCode: 'SOMETHING_ELSE' }))
assertEqual(unknown.kind === 'error' && unknown.key, 'planogram.errors.unknown', '未知错误码 → 通用')
assertEqual(resolvePlanogramError(new TypeError('Failed to fetch')).kind === 'error' && (resolvePlanogramError(new TypeError('x')) as { key: string }).key, 'planogram.errors.network', '断网')
assertEqual(resolvePlanogramError(Object.assign(new Error('a'), { name: 'AbortError' })).kind, 'abort', '取消请求不提示')
assertEqual(resolvePlanogramError('boom').kind === 'error', true, '字符串异常也兜底')

if (inUse.kind === 'error') {
  assertEqual(planogramErrorText(inUse, 'zh-CN', () => 'EN'), '被引用', '中文界面优先用服务端中文 message')
  assertEqual(planogramErrorText(inUse, 'en', () => 'EN'), 'EN', '英文界面用错误码对应文案')
}
if (unknown.kind === 'error') {
  assertEqual(planogramErrorText(unknown, 'zh', (key) => key), 'planogram.errors.unknown', '没有服务端 message 时用文案键')
}

// details 的三种写法
assertDeepEqual(extractFieldErrors([{ field: 'holePitchXMm', message: 'a' }, { name: 'code', error: 'b' }, 'c', 5, { message: ' ' }]),
  [{ field: 'holePitchXMm', message: 'a' }, { field: 'code', message: 'b' }, { field: null, message: 'c' }], '对象数组 / 字符串数组')
assertDeepEqual(extractFieldErrors({ code: '必填', holeCols: ['至少 1'], x: 1 }),
  [{ field: 'code', message: '必填' }, { field: 'holeCols', message: '至少 1' }], '字典')
assertDeepEqual(extractFieldErrors(null), [], 'null')
assertDeepEqual(extractFieldErrors('x'), [], '字符串不是字段列表')

// ---------- 时间 ----------
assertEqual(parseIsoAsUtc('2026-11-04T09:40:00Z')?.toISOString(), '2026-11-04T09:40:00.000Z', 'Z 结尾')
assertEqual(parseIsoAsUtc('2026-11-04T09:40:00+11:00')?.toISOString(), '2026-11-03T22:40:00.000Z', '带偏移')
assertEqual(parseIsoAsUtc('2026-11-04T09:40:00')?.toISOString(), '2026-11-04T09:40:00.000Z', '不带时区按 UTC 解析')
assertEqual(parseIsoAsUtc('2026-11-04 09:40:00')?.toISOString(), '2026-11-04T09:40:00.000Z', '空格分隔也行')
assertEqual(parseIsoAsUtc('not a date'), null, '非法')
assertEqual(parseIsoAsUtc('  '), null, '空串')
// 悉尼 11 月是夏令时（UTC+11）：UTC 09:40 = 悉尼 20:40
assertEqual(formatUpdatedAt('2026-11-04T09:40:00Z'), '11-04 20:40', '列表：MM-DD HH:mm（悉尼时间）')
assertEqual(formatUpdatedAt('2026-11-04T09:40:00Z', true), '2026-11-04 20:40', '抽屉：带年')
assertEqual(formatUpdatedAt('2026-11-04T14:30:00Z'), '11-05 01:30', '跨天按悉尼日期')
assertEqual(formatUpdatedAt('2026-07-01T00:05:00Z'), '07-01 10:05', '冬令时 UTC+10')
assertEqual(formatUpdatedAt(null), '—', '缺失显示破折号')
assertEqual(formatUpdatedAt('garbage'), '—', '解析失败显示破折号')

// ---------- 数字 ----------
assertEqual(formatNumber(25), '25', '整数')
assertEqual(formatNumber(25.4), '25.4', '小数')
assertEqual(formatNumber(25.40001), '25.4', '最多两位小数')
assertEqual(formatNumber(Number.NaN), '—', 'NaN')
assertEqual(formatNumber(null), '—', 'null')
assertEqual(formatDimensions([900, 1200]), '900 × 1200 mm', '尺寸')
assertEqual(formatDimensions([110, 155, 12]), '110 × 155 × 12 mm', '三维')
assertEqual(formatDimensions([52, 26], ''), '52 × 26 ', '无单位')
assertEqual(formatDimensions([900, null]), '—', '缺一项整体显示破折号')

// ---------- 货架表单 ----------
const defaults = defaultShelfFormValues()
const defaultGrid = autoHoleGrid({
  bayWidthMm: defaults.bayWidthMm as number, totalHeightMm: defaults.totalHeightMm as number,
  baseTopHeightMm: defaults.baseTopHeightMm as number, uprightWidthMm: defaults.uprightWidthMm as number,
  holePitchXMm: defaults.holePitchXMm as number, holePitchYMm: defaults.holePitchYMm as number,
  firstHoleLeftMm: defaults.firstHoleLeftMm as number, firstHoleTopMm: defaults.firstHoleTopMm as number,
  holeCols: 0, holeRows: 0,
})
assertEqual(defaults.holeCols, defaultGrid.cols, '新建默认孔列数等于按默认尺寸的换算值（防止默认值与公式漂移）')
assertEqual(defaults.holeRows, defaultGrid.rows, '新建默认孔行数等于换算值')
assertEqual(defaults.holePitchXMm, 25, '孔距默认 25（决策 #1）')
assertEqual(defaults.firstHoleLeftMm, 25, '首孔边距默认 25')
assertEqual(defaults.rowNumberingFrom, 1, '行号默认自板顶')
assertEqual(defaults.isActive, true, '默认启用')

const shelf: ShelfModelDto = {
  guid: 'g1', code: 'G-S-PEG-90x30x120', name: '单面洞洞板 900×1200 · 白', sideType: 1, color: '白', source: 'Shop Fittings Store G',
  bayWidthMm: 900, totalHeightMm: 1200, baseDepthMm: 300, baseTopHeightMm: 150, uprightWidthMm: 30,
  holePitchXMm: 25.4, holePitchYMm: 25.4, holeDiameterMm: 6, firstHoleLeftMm: 25.4, firstHoleTopMm: 25.4,
  holeCols: 34, holeRows: 41, rowNumberingFrom: 2, isActive: true, referencedPlanCount: 6, updatedAt: '2026-11-04T00:00:00Z', updatedBy: 'admin',
}
const shelfForm = shelfToFormValues(shelf)
assertEqual(shelfForm.holePitchXMm, 25.4, '小数孔距带入表单')
assertEqual(shelfForm.holeCols, 34, '已存孔数带入表单')
const shelfInput = shelfFormToInput({ ...shelfForm, name: '  新名称  ', code: ' CODE ', color: '  ', source: '' })
assertEqual(shelfInput.name, '新名称', '名称去首尾空格')
assertEqual(shelfInput.code, 'CODE', '编码去首尾空格')
assertEqual(shelfInput.color, null, '空颜色提交 null')
assertEqual(shelfInput.source, null, '空来源提交 null')
assertEqual(shelfInput.holePitchXMm, 25.4, '孔距小数原样提交')
// 外形尺寸后端是 int 列：提交前取整；孔距 / 孔径 / 首孔边距是 decimal，保持小数
const rounded = shelfFormToInput({ ...shelfForm, bayWidthMm: 900.4, totalHeightMm: 1199.6, baseDepthMm: 299.5, baseTopHeightMm: 150.2, uprightWidthMm: 29.7, holeDiameterMm: 6.35, firstHoleLeftMm: 12.7, firstHoleTopMm: 12.7 })
assertEqual(rounded.bayWidthMm, 900, '节宽取整')
assertEqual(rounded.totalHeightMm, 1200, '总高取整')
assertEqual(rounded.baseDepthMm, 300, '底板深取整')
assertEqual(rounded.baseTopHeightMm, 150, '底板面离地高取整')
assertEqual(rounded.uprightWidthMm, 30, '立柱宽取整')
assertEqual(rounded.holeDiameterMm, 6.35, '孔径保持小数')
assertEqual(rounded.firstHoleLeftMm, 12.7, '首孔边距保持小数')
assertEqual(shelfInput.holeCols, 34, '手填孔数以手填为准')
assertEqual('guid' in shelfInput || 'referencedPlanCount' in shelfInput || 'updatedAt' in shelfInput, false, '只读字段不提交')
const noCounts = shelfFormToInput({ ...defaults, name: 'a', code: 'b', holeCols: null, holeRows: null })
assertEqual(noCounts.holeCols, defaultGrid.cols, '孔数没填按换算值提交（列）')
assertEqual(noCounts.holeRows, defaultGrid.rows, '孔数没填按换算值提交（行）')
assertEqual(shelfFormToInput({ ...defaults, name: 'a', code: 'b', holeCols: 0 }).holeCols, defaultGrid.cols, '孔数 0 也按换算值')

const copied = shelfCopyFormValues(shelf, ' 副本')
assertEqual(copied.name, '单面洞洞板 900×1200 · 白 副本', '复制：名称加副本后缀')
assertEqual(copied.code, '', '复制：编码留空，避免与原型号重复')
assertEqual(copied.bayWidthMm, 900, '复制：尺寸照搬')
assertEqual(copied.isActive, true, '复制出来的型号默认启用')
assertEqual(shelfCopyFormValues({ ...shelf, isActive: false }, '').isActive, true, '复制停用型号也启用')

const toggled = shelfToggleInput(shelf)
assertEqual(toggled.isActive, false, '停用：翻转 isActive')
assertEqual(toggled.code, shelf.code, '启停整体提交，其他字段不变')
assertEqual(toggled.holePitchXMm, 25.4, '启停不丢小数孔距')
assertEqual('guid' in toggled, false, '启停请求体不带只读字段')
assertEqual(shelfToggleInput({ ...shelf, isActive: false }).isActive, true, '启用')

// ---------- 挂钩表单 ----------
const hookDefaults = defaultHookFormValues()
assertEqual(hookDefaults.hasPriceTagArm, true, '默认有价签臂')
assertEqual(hookDefaults.tagBottomAboveArmMm, 10, '价签架底边默认高于挂货臂 10')
assertEqual(hookDefaults.tagWidthMm, 52, '价签架默认 52')
assertEqual(hookDefaults.holeSpanX, 2, '双钩占 2 孔')
assertEqual(defaultHoleSpanX(1), 1, '单钩占 1 孔')
assertEqual(defaultHoleSpanX(2), 2, '双钩占 2 孔')

const hook: HookModelDto = {
  guid: 'h1', code: 'HK-D-PT-200', name: '双钩带价签 200 · 黑', color: '黑', prongCount: 2, hasPriceTagArm: true,
  armLengthMm: 200, dropMm: 50, wireDiameterMm: 5, tipReserveMm: 20, tagWidthMm: 52, tagHeightMm: 26, tagBottomAboveArmMm: 10,
  holeSpanX: 2, holeSpanY: 1, loadKg: 3, isActive: true, referencedPlanCount: 2,
}
const hookInput = hookFormToInput(hookToFormValues(hook))
assertEqual(hookInput.tagWidthMm, 52, '有价签臂：价签尺寸原样提交')
assertEqual(hookInput.loadKg, 3, '承重')
const noTag = hookFormToInput({ ...hookToFormValues(hook), hasPriceTagArm: false })
assertEqual(noTag.tagWidthMm, 0, '无价签臂：价签宽提交 0')
assertEqual(noTag.tagHeightMm, 0, '无价签臂：价签高提交 0')
assertEqual(noTag.tagBottomAboveArmMm, 0, '无价签臂：价签底边高提交 0')
assertEqual(hookFormToInput({ ...hookToFormValues(hook), loadKg: null }).loadKg, null, '承重空 → null')
assertEqual(hookFormToInput({ ...hookToFormValues(hook), loadKg: 0 }).loadKg, null, '承重 0 → null')
assertEqual(hookFormToInput({ ...hookToFormValues(hook), loadKg: 3.5 }).loadKg, 3.5, '小数承重')
// 后端存了 0 的无价签臂型号：回填表单时给默认价签尺寸，切到「有」时直接可用
const backFilled = hookToFormValues({ ...hook, hasPriceTagArm: false, tagWidthMm: 0, tagHeightMm: 0, tagBottomAboveArmMm: 0 })
assertEqual(backFilled.tagWidthMm, 52, '回填默认价签宽')
assertEqual(backFilled.tagBottomAboveArmMm, 10, '回填默认底边高')
const hookCopy = hookCopyFormValues(hook, ' (copy)')
assertEqual(hookCopy.name, '双钩带价签 200 · 黑 (copy)', '复制挂钩名称')
assertEqual(hookCopy.code, '', '复制挂钩编码留空')
assertEqual(hookToggleInput(hook).isActive, false, '挂钩停用')
assertEqual('referencedPlanCount' in hookToggleInput(hook), false, '挂钩启停不带只读字段')

// ---------- 商品陈列参数 ----------
const hookB: HookModelDto = { ...hook, guid: 'h2', name: '双钩 250', armLengthMm: 250 }
const hookOff: HookModelDto = { ...hook, guid: 'h3', name: '停用钩', isActive: false }
const catalog = [hook, hookB, hookOff]
assertEqual(specToFormValues(null).hangHoleOffsetMm, 10, '新补充参数：吊孔偏移默认 10')
assertEqual(specToFormValues(undefined).source, 1, '新补充参数：来源默认人工量取')
assertEqual(specToFormValues(null).widthMm, null, '新补充参数：宽高厚留空')

const previous = {
  productCode: 'XD1', widthMm: 110, heightMm: 155, depthMm: 12, hangHoleOffsetMm: 10, hangHoleType: 2 as const, unitWeightG: 95,
  defaultHookGuid: 'h1', hookQtyOverrides: { h2: 16 }, source: 1 as const, notes: '原备注',
}
const specForm = specToFormValues(previous)
assertEqual(specForm.widthMm, 110, '已有参数带入表单')
assertEqual(specForm.overrides.h2, 16, '覆盖带入表单')
const dto = specFormToDto('XD1', specForm, catalog, previous)
assertEqual(dto.notes, '原备注', '备注界面上没有输入框，保存时原样带回')
assertDeepEqual(dto.hookQtyOverrides, { h2: 16 }, '有效覆盖保留')
assertEqual(dto.defaultHookGuid, 'h1', '默认挂钩保留')

const messy = specFormToDto('XD1', {
  ...specForm,
  unitWeightG: 0,
  defaultHookGuid: 'gone',
  overrides: { h1: null, h2: 0, h3: 2.5, gone: 7, h2x: 3 },
}, catalog, previous)
assertDeepEqual(messy.hookQtyOverrides, {}, '空 / 0 / 小数 / 已不存在的型号的覆盖都剔除（否则整次保存被后端拒绝）')
assertEqual(messy.defaultHookGuid, null, '默认挂钩指向不存在的型号 → 清空')
assertEqual(messy.unitWeightG, null, '单件重 0 → null')
assertEqual(specFormToDto('XD1', specForm, catalog, null).notes, null, '没有旧参数时备注为 null')
assertDeepEqual(specFormToDto('XD1', { ...specForm, overrides: { h1: 5, h3: 9 } }, catalog, previous).hookQtyOverrides, { h1: 5, h3: 9 }, '停用的型号只要还存在，覆盖照常保留')

const listItem = (spec: ProductSpecListItemDto['spec']): ProductSpecListItemDto => ({
  productCode: 'XD1', productName: 'x', retailPrice: 1, hasSpec: Boolean(spec), referencedPlanCount: 0, spec,
})
const byGuid = new Map(catalog.map((item) => [item.guid, item]))
// 设计图：A6 笔记本厚 12，默认挂钩 200 → ⌊180 ÷ 12⌋ = 15 自动
const auto = listHookQty(listItem({ ...previous, defaultHookGuid: 'h1', hookQtyOverrides: {} }), byGuid)
assertEqual(auto?.result.effective, 15, '列表：默认挂钩自动换算 15')
assertEqual(auto?.result.source, 'auto', '列表：自动')
const manual = listHookQty(listItem({ ...previous, defaultHookGuid: 'h1', hookQtyOverrides: { h1: 3 } }), byGuid)
assertEqual(manual?.result.effective, 3, '列表：手填覆盖优先')
assertEqual(manual?.result.auto, 15, '列表：保留自动值用于对照')
assertEqual(listHookQty(listItem({ ...previous, defaultHookGuid: null }), byGuid), null, '没有默认挂钩 → null（显示破折号）')
assertEqual(listHookQty(listItem({ ...previous, defaultHookGuid: 'gone' }), byGuid), null, '默认挂钩已不存在 → null')
assertEqual(listHookQty(listItem(null), byGuid), null, '未设参数 → null')

// 抽屉挂数表：启用的型号 + 本商品引用过的停用型号
assertDeepEqual(specTableHooks(catalog, null).map((item) => item.guid), ['h1', 'h2'], '默认只列启用的型号')
assertDeepEqual(specTableHooks(catalog, { defaultHookGuid: 'h3', hookQtyOverrides: {} }).map((item) => item.guid), ['h1', 'h2', 'h3'], '默认挂钩是停用型号时仍列出')
assertDeepEqual(specTableHooks(catalog, { defaultHookGuid: null, hookQtyOverrides: { h3: 4 } }).map((item) => item.guid), ['h1', 'h2', 'h3'], '有覆盖的停用型号仍列出')

// ---------- 抽屉打开编号：关闭动画期间重新打开，表单必须重建 ----------
const allocate = createOpenKeyAllocator()
const targetA = { mode: 'edit' }
const targetB = { mode: 'edit' }
const first = allocate(targetA)
assertEqual(allocate(targetA), first, '同一个 target 重复渲染得到同一个编号（渲染期间不能让表单重建）')
const second = allocate(targetB)
assertEqual(second !== first, true, '换了 target（再次打开）得到新编号 → 表单 key 变化 → 表单重建')
assertEqual(allocate(null), second, '关闭时保持上一个编号（关闭动画期间表单不重建）')
assertEqual(allocate(targetB), second, '关闭后同一 target 仍是同一编号')
const reopened = allocate({ ...targetA })
assertEqual(reopened !== first && reopened !== second, true, '同一行数据复制出的新对象也是一次新的打开')

// ---------- 分页：筛选条件变了，有效页码立刻回到第 1 页（不靠 effect，避免用旧页码 + 新条件多发一次请求） ----------
assertEqual(effectivePage({ page: 3, pageSize: 20, filterKey: 'a|all' }, 'a|all'), 3, '同一组条件下保持所翻到的页')
assertEqual(effectivePage({ page: 3, pageSize: 20, filterKey: 'a|all' }, 'ab|all'), 1, '关键字变了 → 第 1 页')
assertEqual(effectivePage({ page: 3, pageSize: 20, filterKey: 'a|all' }, 'a|set'), 1, '状态变了 → 第 1 页')
assertEqual(effectivePage({ page: 1, pageSize: 20, filterKey: 'a|all' }, 'zzz|all'), 1, '本来就在第 1 页')

// 设计器子页面路径（批 2）
assertEqual(planogramDesignerPath('abc-1'), '/pos-admin/planograms/abc-1/design', '设计器路径')
assertEqual(planogramDesignerPath('a/b c'), '/pos-admin/planograms/a%2Fb%20c/design', '路径里的特殊字符要编码')
assertEqual(PLANOGRAM_DESIGNER_PATTERN, '/pos-admin/planograms/:guid/design', '设计器路由模式')

// 批 2 新增的错误码都有专门文案键
for (const [code, key] of [
  ['PLANOGRAM_SPEC_IN_USE', 'planogram.errors.specInUse'],
  ['PLANOGRAM_MODEL_LOCKED', 'planogram.errors.modelLocked'],
  ['PLANOGRAM_STALE', 'planogram.errors.stale'],
  ['PLANOGRAM_ARCHIVED', 'planogram.errors.archived'],
  ['PLANOGRAM_NOT_DELETABLE', 'planogram.errors.notDeletable'],
] as const) {
  const resolved = resolvePlanogramError({ status: 400, payload: { errorCode: code, message: '服务端中文' } })
  assert(resolved.kind === 'error' && resolved.key === key && resolved.code === code, `${code} → ${key}`)
}

console.log('planogramLogic.test: ok')
