import {
  checkHookLoad,
  formatKgFromGrams,
  formatLimitKg,
  normalizeQtyOverride,
  resolveHookQty,
  suggestHookQty,
  tagTopBelowHoleMm,
} from './hookQty'

// 每钩挂数换算 / 手填覆盖 / 承重核对：数字全部取自设计图 03-product-spec 与 02-hook-model 的样例。

function assertEqual<T>(actual: T, expected: T, message: string) {
  if (actual !== expected) {
    throw new Error(`${message}. Expected: ${String(expected)}, received: ${String(actual)}`)
  }
}

// ---------- 自动换算 ⌊(臂长 − 臂端预留) ÷ 厚⌋ ----------
const reserve = 20
assertEqual(suggestHookQty({ armLengthMm: 200, tipReserveMm: reserve }, 20), 9, '圆珠笔：⌊180 ÷ 20⌋ = 9（挂钩型号设计图的公式示例）')
assertEqual(suggestHookQty({ armLengthMm: 150, tipReserveMm: reserve }, 12), 10, '笔记本 150 臂：⌊130 ÷ 12⌋ = 10')
assertEqual(suggestHookQty({ armLengthMm: 200, tipReserveMm: reserve }, 12), 15, '笔记本 200 臂：⌊180 ÷ 12⌋ = 15')
assertEqual(suggestHookQty({ armLengthMm: 250, tipReserveMm: reserve }, 12), 19, '笔记本 250 臂：⌊230 ÷ 12⌋ = 19')
assertEqual(suggestHookQty({ armLengthMm: 300, tipReserveMm: reserve }, 12), 23, '笔记本 300 臂：⌊280 ÷ 12⌋ = 23')
assertEqual(suggestHookQty({ armLengthMm: 200, tipReserveMm: reserve }, 5), 36, '尺子：⌊180 ÷ 5⌋ = 36（设计图「自动 36」）')
assertEqual(suggestHookQty({ armLengthMm: 200, tipReserveMm: reserve }, 55), 3, '胶带：⌊180 ÷ 55⌋ = 3')
assertEqual(suggestHookQty({ armLengthMm: 200, tipReserveMm: reserve }, 0.6), 300, '小数厚度恰好整除时不被浮点误差少算一件')
assertEqual(suggestHookQty({ armLengthMm: 200, tipReserveMm: reserve }, 12.5), 14, '小数厚度：⌊180 ÷ 12.5⌋ = 14')

// ---------- 不合法输入：返回 0，不抛异常、不给负数 / NaN ----------
assertEqual(suggestHookQty({ armLengthMm: 200, tipReserveMm: reserve }, 0), 0, '厚度 0 不能换算')
assertEqual(suggestHookQty({ armLengthMm: 200, tipReserveMm: reserve }, -3), 0, '厚度为负不能换算')
assertEqual(suggestHookQty({ armLengthMm: 200, tipReserveMm: reserve }, Number.NaN), 0, '厚度 NaN 不能换算')
assertEqual(suggestHookQty({ armLengthMm: 20, tipReserveMm: 20 }, 10), 0, '臂长等于预留：没有可挂长度')
assertEqual(suggestHookQty({ armLengthMm: 10, tipReserveMm: 20 }, 10), 0, '臂长小于预留：没有可挂长度')
assertEqual(suggestHookQty({ armLengthMm: 200, tipReserveMm: reserve }, 500), 0, '厚度大于可用臂长：一件也挂不下')

// ---------- 手填覆盖：整数 ≥ 1 才算，覆盖值优先 ----------
assertEqual(normalizeQtyOverride(16), 16, '整数 16 有效')
assertEqual(normalizeQtyOverride(1), 1, '1 有效（下限）')
assertEqual(normalizeQtyOverride(0), null, '0 无效')
assertEqual(normalizeQtyOverride(-2), null, '负数无效')
assertEqual(normalizeQtyOverride(2.5), null, '小数无效')
assertEqual(normalizeQtyOverride(undefined), null, 'undefined 无效')
assertEqual(normalizeQtyOverride(null), null, 'null 无效')
assertEqual(normalizeQtyOverride(Number.NaN), null, 'NaN 无效')
assertEqual(normalizeQtyOverride('16'), null, '字符串不当数字')

const hook250 = { guid: 'hook-250', armLengthMm: 250, tipReserveMm: reserve }
const hook200 = { guid: 'hook-200', armLengthMm: 200, tipReserveMm: reserve }
const overrides = { 'hook-250': 16, 'hook-bad': 0, 'hook-fraction': 2.5 }

const overridden = resolveHookQty(hook250, 12, overrides)
assertEqual(overridden.auto, 19, '覆盖时仍保留自动换算值用于对照')
assertEqual(overridden.override, 16, '覆盖值')
assertEqual(overridden.effective, 16, '覆盖值优先生效')
assertEqual(overridden.source, 'override', '来源标记为手填')

const automatic = resolveHookQty(hook200, 12, overrides)
assertEqual(automatic.override, null, '没有覆盖的型号')
assertEqual(automatic.effective, 15, '没有覆盖时用自动换算值')
assertEqual(automatic.source, 'auto', '来源标记为自动')

assertEqual(resolveHookQty({ ...hook200, guid: 'hook-bad' }, 12, overrides).effective, 15, '0 的覆盖被忽略，回到自动换算')
assertEqual(resolveHookQty({ ...hook200, guid: 'hook-fraction' }, 12, overrides).source, 'auto', '小数覆盖被忽略')
assertEqual(resolveHookQty(hook200, 12, undefined).effective, 15, '覆盖表缺失（旧数据）按没有覆盖处理')
assertEqual(resolveHookQty(hook200, 12, null).effective, 15, '覆盖表为 null 按没有覆盖处理')
assertEqual(resolveHookQty(hook200, 0, undefined).effective, 0, '厚度没填：自动值为 0')
assertEqual(resolveHookQty(hook200, 0, { 'hook-200': 7 }).effective, 7, '厚度没填时手填覆盖仍可生效')

// ---------- 承重核对 ----------
const ok = checkHookLoad(10, 95, 3)
assertEqual(ok.status, 'ok', '0.95 kg / 3 kg 在承重内')
assertEqual(ok.totalG, 950, '总重按克')
assertEqual(ok.limitG, 3000, '承重按克')
assertEqual(formatKgFromGrams(950), '0.95', '设计图 0.95')
assertEqual(formatKgFromGrams(1425), '1.43', '设计图 1.43：15 × 95 g = 1425 g，半数进一')
assertEqual(formatKgFromGrams(1520), '1.52', '设计图 1.52')
assertEqual(formatKgFromGrams(2185), '2.19', '设计图 2.19')
assertEqual(formatLimitKg(3000), '3', '承重 3 kg 去尾零')
assertEqual(formatLimitKg(3500), '3.5', '承重 3.5 kg')
assertEqual(formatLimitKg(3150), '3.15', '承重 3.15 kg')

assertEqual(checkHookLoad(32, 100, 3).status, 'over', '3.2 kg > 3 kg 超重')
assertEqual(checkHookLoad(30, 100, 3).status, 'ok', '正好等于承重不算超重')
assertEqual(checkHookLoad(21, 150, 3.15).status, 'ok', '小数承重 3.15 kg：3150 g 正好等于，不被浮点误差判成超重')
assertEqual(checkHookLoad(21, 151, 3.15).status, 'over', '3171 g > 3150 g 超重')
assertEqual(checkHookLoad(10, null, 3).status, 'noWeight', '没填单件重：无法核对')
assertEqual(checkHookLoad(10, 0, 3).status, 'noWeight', '单件重 0 视为没填')
assertEqual(checkHookLoad(10, undefined, 3).totalG, null, '没填单件重时不给总重')
assertEqual(checkHookLoad(0, 95, 3).status, 'noWeight', '挂数为 0（臂太短）无法核对')
const noLimit = checkHookLoad(10, 95, null)
assertEqual(noLimit.status, 'noLimit', '挂钩型号没填承重：只显示总重')
assertEqual(noLimit.totalG, 950, '没有承重时仍给总重')
assertEqual(noLimit.limitG, null, '没有承重')
assertEqual(checkHookLoad(10, 95, 0).status, 'noLimit', '承重 0 视为没填')

// ---------- 价签架顶边距挂孔（设计图 50 − 10 − 26 = 14） ----------
assertEqual(tagTopBelowHoleMm({ dropMm: 50, tagBottomAboveArmMm: 10, tagHeightMm: 26 }), 14, '默认挂钩型号')
assertEqual(tagTopBelowHoleMm({ dropMm: 30, tagBottomAboveArmMm: 10, tagHeightMm: 26 }), -6, '负数表示价签架顶边在挂孔之上')

console.log('hookQty.test: ok')
