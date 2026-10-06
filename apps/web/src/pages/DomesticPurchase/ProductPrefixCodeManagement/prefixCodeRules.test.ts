import assert from 'node:assert/strict'
import {
  PREFIX_CODE_MAX_LENGTH,
  PREFIX_SORT_ORDER_MAX,
  buildPrefixPayload,
  buildStatusTogglePayload,
  getPrefixApiErrorCode,
  isFormValidationError,
  normalizePrefixCode,
  normalizeSortOrder,
  validatePrefixCode,
} from './prefixCodeRules'

// ---- 前缀整理：保存自动转大写 + 去首尾空白 ----
assert.equal(normalizePrefixCode('bx2'), 'BX2', '小写应转为大写')
assert.equal(normalizePrefixCode('  Wjx '), 'WJX', '首尾空白应被去掉')
assert.equal(normalizePrefixCode(undefined), '', '非字符串视为空')
assert.equal(normalizePrefixCode(null), '', 'null 视为空')

// ---- 前缀校验：仅字母数字、长度 ≤ 10 ----
assert.equal(validatePrefixCode(''), 'required', '空值必填')
assert.equal(validatePrefixCode('   '), 'required', '纯空白等同于空')
assert.equal(validatePrefixCode('bx'), null, '小写字母合法（保存时会转大写）')
assert.equal(validatePrefixCode(' BX2 '), null, '首尾空白不应让合法前缀校验失败')
assert.equal(validatePrefixCode('B-X'), 'pattern', '连字符不合法')
assert.equal(validatePrefixCode('B X'), 'pattern', '中间空格不合法')
assert.equal(validatePrefixCode('中文'), 'pattern', '中文不合法')
assert.equal(validatePrefixCode('A'.repeat(PREFIX_CODE_MAX_LENGTH)), null, '恰好 10 位合法')
assert.equal(validatePrefixCode('A'.repeat(PREFIX_CODE_MAX_LENGTH + 1)), 'tooLong', '11 位超长')
assert.equal(validatePrefixCode('A-'.repeat(8)), 'pattern', '同时含非法字符和超长时先报字符问题')

// ---- 排序号：非负整数，空值保持为空（不补 0） ----
assert.equal(normalizeSortOrder(10), 10)
assert.equal(normalizeSortOrder(0), 0, '0 是合法排序号，不能被当成空')
assert.equal(normalizeSortOrder(3.9), 3, '小数取整')
assert.equal(normalizeSortOrder(-1), undefined, '负数视为未填')
assert.equal(normalizeSortOrder(null), undefined, 'InputNumber 清空后是 null')
assert.equal(normalizeSortOrder(undefined), undefined)
assert.equal(normalizeSortOrder(Number.NaN), undefined)
assert.equal(normalizeSortOrder(PREFIX_SORT_ORDER_MAX + 100), PREFIX_SORT_ORDER_MAX, '超过 int 上限时截到上限')

// ---- 请求体：新增带供应商，编辑不带；排序随表单提交 ----
assert.deepEqual(
  buildPrefixPayload({ supplierCode: 'S1', prefixName: ' bx2 ', prefixDescription: ' 保温杯 ', sortOrder: 10, isActive: false }, 'S1'),
  { supplierCode: 'S1', prefixName: 'BX2', prefixDescription: '保温杯', isActive: false, sortOrder: 10 },
  '新增：前缀大写、说明去空白、排序与状态原样提交',
)
assert.deepEqual(
  buildPrefixPayload({ prefixName: 'jj', prefixDescription: '   ', sortOrder: null }),
  { prefixName: 'JJ', prefixDescription: undefined, isActive: true, sortOrder: undefined },
  '编辑：不带供应商；说明清空后为 undefined；排序清空保持为空而不是 0；isActive 缺省为启用',
)
assert.equal('supplierCode' in buildPrefixPayload({ prefixName: 'A' }), false, '没有供应商时不应出现 supplierCode 键')

// ---- 列表内切换状态：原样带回其余字段，排序为空时保持为空 ----
assert.deepEqual(
  buildStatusTogglePayload({ prefixName: 'BX', prefixDescription: '保温杯', sortOrder: 10 }, false),
  { prefixName: 'BX', prefixDescription: '保温杯', isActive: false, sortOrder: 10 },
  '切换状态只改 isActive',
)
const toggled = buildStatusTogglePayload({ prefixName: 'BX', prefixDescription: undefined, sortOrder: undefined }, true)
assert.equal(toggled.sortOrder, undefined, '后端排序为空的前缀切换状态后不能被写成 0')
assert.equal(toggled.isActive, true)

// ---- 错误识别 ----
assert.equal(getPrefixApiErrorCode({ payload: { errorCode: 'PREFIX_NAME_EXISTS' } }), 'PREFIX_NAME_EXISTS')
assert.equal(getPrefixApiErrorCode({ payload: { success: false } }), undefined)
assert.equal(getPrefixApiErrorCode({ payload: { errorCode: 5 } }), undefined, '非字符串错误码忽略')
assert.equal(getPrefixApiErrorCode(new Error('x')), undefined)
assert.equal(getPrefixApiErrorCode(null), undefined)
assert.equal(isFormValidationError({ errorFields: [] }), true)
assert.equal(isFormValidationError(new Error('x')), false)
assert.equal(isFormValidationError(undefined), false)

console.log('prefixCodeRules.test.ts: ok')
