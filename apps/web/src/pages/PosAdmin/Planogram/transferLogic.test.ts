import assert from 'node:assert/strict'
import {
  exportJsonFileName,
  groupUnresolved,
  isDuplicatePlanName,
  isSupportedFormatVersion,
  MAX_IMPORT_BYTES,
  parseImportFile,
  stringifyExportFile,
  suggestImportName,
} from './transferLogic'
import { extractConflictDetails, extractImportUnresolved, resolvePlanogramError } from './planogramLogic'

// 方案 JSON 导入 / 导出的纯逻辑：文件解析与大小限制、文件名、重名、缺失编码分组、错误 details 解析。

const valid = {
  formatVersion: 1,
  exportedAt: '2026-11-04T01:00:00Z',
  plan: { name: '文具挂板 A1', notes: '开学季', bayCount: 2, gapXMm: 10, gapYMm: 10 },
  shelfModelCode: 'G-S-PEG-90x30x120',
  items: [{ bayIndex: 1, productCode: 'XD0001203', hookModelCode: 'HK-D-PT-200', holeCol: 2, holeRow: 3, perHookQty: 12 }, { bayIndex: 1, productCode: 'XD0001203', hookModelCode: 'HK-D-PT-200', holeCol: 7, holeRow: 3 }],
}
const text = JSON.stringify(valid)

// 1) 正常文件
const ok = parseImportFile(text, text.length)
assert.ok(ok.ok)
if (ok.ok) {
  assert.equal(ok.file.plan.name, '文具挂板 A1')
  assert.equal(ok.file.items.length, 2)
  assert.equal(ok.file.items[0].perHookQty, 12)
  assert.equal('perHookQty' in ok.file.items[1], false, '没填每钩挂数的明细不带这个字段（保持文件干净）')
  assert.equal(isSupportedFormatVersion(ok.file), true)
}
assert.ok(parseImportFile(`﻿${text}`, text.length + 3).ok, 'Windows 记事本保存的 BOM 不影响解析')

// 2) 失败原因要明确
assert.deepEqual(parseImportFile(text, MAX_IMPORT_BYTES + 1), { ok: false, reason: 'tooLarge' }, '大于 5 MB')
assert.deepEqual(parseImportFile(text, MAX_IMPORT_BYTES).ok, true, '恰好 5 MB 允许')
assert.deepEqual(parseImportFile('   \n', 4), { ok: false, reason: 'empty' })
assert.deepEqual(parseImportFile('', 0), { ok: false, reason: 'empty' })
assert.deepEqual(parseImportFile('{not json', 9), { ok: false, reason: 'notJson' })
assert.deepEqual(parseImportFile('<html></html>', 13), { ok: false, reason: 'notJson' }, '非 JSON')
assert.deepEqual(parseImportFile('[1,2,3]', 7), { ok: false, reason: 'notPlanogram' }, '数组不是导出文件')
assert.deepEqual(parseImportFile('"hello"', 7), { ok: false, reason: 'notPlanogram' })
assert.deepEqual(parseImportFile('null', 4), { ok: false, reason: 'notPlanogram' })
assert.deepEqual(parseImportFile(JSON.stringify({ ...valid, plan: undefined }), 100), { ok: false, reason: 'notPlanogram' }, '缺方案头')
assert.deepEqual(parseImportFile(JSON.stringify({ ...valid, shelfModelCode: undefined }), 100), { ok: false, reason: 'notPlanogram' }, '缺货架编码')
assert.deepEqual(parseImportFile(JSON.stringify({ ...valid, items: 'x' }), 100), { ok: false, reason: 'notPlanogram' }, '明细不是数组')
// 格式版本不在这里拒绝（后端校验），但能提前判断
const future = parseImportFile(JSON.stringify({ ...valid, formatVersion: 2 }), 100)
assert.ok(future.ok && !isSupportedFormatVersion(future.file), '不认识的格式版本：解析通过，弹窗里提示并禁用导入')
const noVersion = parseImportFile(JSON.stringify({ ...valid, formatVersion: undefined }), 100)
assert.ok(noVersion.ok && noVersion.file.formatVersion === 0 && !isSupportedFormatVersion(noVersion.file), '缺 formatVersion 按 0（不认识）处理，不悄悄当成 1')

// 3) 导出文件名与内容
assert.equal(exportJsonFileName('文具挂板 A1'), '文具挂板_A1.planogram.json')
assert.equal(exportJsonFileName('a/b\\c:d'), 'a_b_c_d.planogram.json')
assert.equal(exportJsonFileName('  '), 'planogram.planogram.json', '名称为空回退 planogram')
const stringified = stringifyExportFile(ok.ok ? ok.file : (undefined as never))
assert.ok(stringified.endsWith('\n') && stringified.includes('\n  "plan"'), '缩进 2 个空格、以换行结尾')
const roundTrip = parseImportFile(stringified, stringified.length)
assert.ok(roundTrip.ok && JSON.stringify(roundTrip.file) === JSON.stringify(ok.ok ? ok.file : null), '导出再导入内容不变（往返）')

// 4) 重名
assert.equal(isDuplicatePlanName(' 文具挂板 a1 ', ['文具挂板 A1', '其它']), true, '忽略首尾空格与大小写')
assert.equal(isDuplicatePlanName('文具挂板 A2', ['文具挂板 A1']), false)
assert.equal(isDuplicatePlanName('   ', ['  ']), false, '空名不算重名（由必填校验管）')
assert.equal(suggestImportName('文具挂板 A1', ['文具挂板 A1'], ' (导入)'), '文具挂板 A1 (导入)')
assert.equal(suggestImportName('文具挂板 A1', ['文具挂板 A1', '文具挂板 A1 (导入)'], ' (导入)'), '文具挂板 A1 (导入)2', '建议名也重名就加序号')
assert.equal(suggestImportName('x'.repeat(100), [], ' (导入)'), 'x'.repeat(95) + ' (导入)', '原名已经 100 字：截原名再接后缀，后缀不会被截没')
const long = suggestImportName('x'.repeat(100), ['x'.repeat(95) + ' (导入)'], ' (导入)')
assert.ok(long.length <= 100 && long.endsWith('(导入)2'), '截断原名后再加序号仍不超长')

// 5) 缺失编码分组
assert.deepEqual(
  groupUnresolved([
    { kind: 'product', code: 'XD2' }, { kind: 'shelf', code: 'S1' }, { kind: 'product', code: 'XD1' }, { kind: 'hook', code: 'H1' }, { kind: 'product', code: 'XD1' },
  ]),
  { shelf: ['S1'], hook: ['H1'], product: ['XD1', 'XD2'] },
  '按种类分组、组内排序去重',
)
assert.deepEqual(groupUnresolved([]), { shelf: [], hook: [], product: [] })

// 6) 错误 details 解析
assert.deepEqual(extractImportUnresolved([{ kind: 'shelf', code: 'S1' }, { kind: 'bogus', code: 'X' }, { kind: 'hook' }, 'x', { kind: 'product', code: 'P1' }, { kind: 'product', code: 'P1' }]), [
  { kind: 'shelf', code: 'S1' },
  { kind: 'product', code: 'P1' },
], '去掉未知种类 / 缺编码 / 重复项')
assert.deepEqual(extractImportUnresolved(null), [])
assert.deepEqual(extractImportUnresolved({ kind: 'shelf', code: 'x' }), [], 'details 不是数组')
assert.deepEqual(extractConflictDetails([
  { itemGuid: 'a', otherItemGuid: 'b', code: 'OVERLAP', severity: 'error', message: '重叠' },
  { itemGuid: 'c', code: 'OVERLOAD', severity: 'warn', message: '' },
  { code: 'X' },
  'oops',
]), [
  { itemGuid: 'a', otherItemGuid: 'b', code: 'OVERLAP', severity: 'error', message: '重叠' },
  { itemGuid: 'c', otherItemGuid: null, code: 'OVERLOAD', severity: 'warn', message: '' },
], '冲突 details：形状不对的丢掉、缺 severity 按 error')
const resolved = resolvePlanogramError({ status: 400, payload: { errorCode: 'PLANOGRAM_HAS_CONFLICTS', message: '方案还有 2 条冲突', details: [{ itemGuid: 'a', code: 'OVERLAP', severity: 'error', message: '重叠' }] } })
assert.ok(resolved.kind === 'error' && resolved.code === 'PLANOGRAM_HAS_CONFLICTS' && resolved.key === 'planogram.errors.hasConflicts')
assert.ok(resolved.kind === 'error' && extractConflictDetails(resolved.details).length === 1, '错误里原样带回 details')
for (const [code, key] of [
  ['PLANOGRAM_EMPTY', 'planogram.errors.empty'],
  ['PLANOGRAM_ALREADY_PUBLISHED', 'planogram.errors.alreadyPublished'],
  ['PLANOGRAM_IMPORT_UNRESOLVED', 'planogram.errors.importUnresolved'],
  ['PLANOGRAM_SNAPSHOT_INVALID', 'planogram.errors.snapshotInvalid'],
]) {
  const r = resolvePlanogramError({ status: 400, payload: { errorCode: code, message: 'm' } })
  assert.ok(r.kind === 'error' && r.key === key, `${code} → ${key}`)
}

console.log('transferLogic.test: ok')
