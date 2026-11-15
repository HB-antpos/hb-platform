import type { PackCartonSummary, PackVoidBlankResult } from '../../types/packing'

import {
  BLANK_BATCH_DEFAULT,
  BLANK_BATCH_MAX,
  BLANK_QUICK_COUNTS,
  BLANK_UNACTIVATED_LIMIT,
  PRINT_CHUNK_MAX_PAGES,
  blankBatchPosition,
  blankPrintState,
  blankRequestSignature,
  cartonsToReportPrinted,
  chunkArray,
  chunkPrintQueue,
  clampBlankCount,
  clampCopies,
  countPrintPages,
  filterReprintable,
  filterUnprinted,
  formatLabelSeq,
  generateRequestGuid,
  isUnactivatedBlank,
  remainingBlankQuota,
  resolveBlankLimitError,
  resolveClientRequestId,
  sampleBlankCartonNo,
  shortBatchCode,
  summarizeSelection,
  summarizeVoidResult,
  uniqueCartons,
  voidResultParts,
} from './blankCartons'

function assertEqual<T>(actual: T, expected: T, message: string) {
  if (JSON.stringify(actual) !== JSON.stringify(expected)) {
    throw new Error(`${message}。Expected: ${JSON.stringify(expected)}, received: ${JSON.stringify(actual)}`)
  }
}

function carton(overrides: Partial<PackCartonSummary> = {}): PackCartonSummary {
  return {
    cartonGuid: 'c1',
    cartonNo: 'BX2611150001',
    status: 1,
    ownerCode: '1008',
    ownerName: 'Sunnybank',
    originCode: '1008',
    originName: 'Sunnybank',
    destination: { destinationType: 0 },
    skuCount: 0,
    pieceCount: 0,
    createdByName: 'Sean',
    createdAtUtc: '2026-11-15T01:40:00Z',
    updatedAtUtc: '2026-11-15T01:40:00Z',
    ...overrides,
  }
}

const blank = (index: number, overrides: Partial<PackCartonSummary> = {}) =>
  carton({ cartonGuid: `b${index}`, cartonNo: `BX26111500${String(index).padStart(2, '0')}`, isBlank: true, ...overrides })

// ---- 数量上限与快捷值 ----
assertEqual(BLANK_BATCH_MAX, 100, '一次最多 100 张')
assertEqual([...BLANK_QUICK_COUNTS], [10, 20, 50, 100], '快捷值 10/20/50/100')
assertEqual(BLANK_QUICK_COUNTS.every((value) => value >= 1 && value <= BLANK_BATCH_MAX), true, '快捷值都在上限内')
assertEqual(clampBlankCount(0), 1, '小于 1 夹到 1')
assertEqual(clampBlankCount(-5), 1, '负数夹到 1')
assertEqual(clampBlankCount(101), 100, '大于 100 夹到 100')
assertEqual(clampBlankCount(37.9), 37, '小数取整')
assertEqual(clampBlankCount('50'), 50, '字符串数字')
assertEqual(clampBlankCount(null), BLANK_BATCH_DEFAULT, '空值回到默认')
assertEqual(clampBlankCount(undefined), BLANK_BATCH_DEFAULT, 'undefined 回到默认')
assertEqual(clampBlankCount(Number.NaN), BLANK_BATCH_DEFAULT, 'NaN 回到默认')
assertEqual(clampCopies(0), 1, '份数最少 1')
assertEqual(clampCopies(9), 5, '份数最多 5')
assertEqual(clampCopies(null), 1, '份数空值为 1')

// ---- 空箱标判断：旧后端缺字段按普通箱 ----
assertEqual(isUnactivatedBlank(carton()), false, '缺 isBlank 不是空箱标')
assertEqual(isUnactivatedBlank(blank(1)), true, '未启用的空箱标')
assertEqual(isUnactivatedBlank(blank(1, { activatedAtUtc: '2026-11-15T02:00:00Z' })), false, '启用后是普通箱')
assertEqual(isUnactivatedBlank(blank(1, { status: 9 })), false, '作废的不算')
assertEqual(blankPrintState(carton()), { kind: 'unprinted' }, '缺 labelPrintCount 视为没打印')
assertEqual(blankPrintState(blank(1, { labelPrintCount: 0 })), { kind: 'unprinted' }, '0 次没打印')
assertEqual(blankPrintState(blank(1, { labelPrintCount: 3 })), { kind: 'printed', count: 3 }, '已打印 3 次')

// ---- 批内位置 ----
assertEqual(blankBatchPosition(carton()), null, '没有批次信息')
assertEqual(blankBatchPosition({ batchGuid: 'abcd1234-0000', batchSeq: 12, batchCount: 20 }), { batch: 'ABCD', seq: 12, count: 20 }, '批内位置')
assertEqual(blankBatchPosition({ batchGuid: 'abcd1234-0000', batchSeq: null, batchCount: 20 }), null, '缺序号不显示')
assertEqual(shortBatchCode('1a2b-3c4d'), '1A2B', '批次短码去掉连字符取前 4 位大写')
assertEqual(formatLabelSeq(1, 20), 'LABEL 01 / 20', '序号补两位')
assertEqual(formatLabelSeq(12, 100), 'LABEL 12 / 100', '三位总数')

// ---- 打印队列分页 ----
assertEqual(chunkArray([1, 2, 3, 4, 5], 2), [[1, 2], [3, 4], [5]], '按大小分块')
assertEqual(chunkArray([], 3), [], '空数组没有块')
assertEqual(countPrintPages(20, 1), 20, '一份一页')
assertEqual(countPrintPages(20, 3), 60, '箱数 × 份数')
const hundred = Array.from({ length: 100 }, (_, index) => blank(index + 1))
assertEqual(chunkPrintQueue(hundred, 1).length, 1, '100 页一次打完')
assertEqual(PRINT_CHUNK_MAX_PAGES, 100, '每次最多 100 页')
const threeCopies = chunkPrintQueue(hundred, 3)
assertEqual(threeCopies.map((chunk) => chunk.length), [33, 33, 33, 1], '每只箱 3 份时每次 33 只箱（99 页），同一只箱的份数不拆开')
assertEqual(threeCopies.every((chunk) => chunk.length * 3 <= PRINT_CHUNK_MAX_PAGES), true, '每次页数不超过上限')
assertEqual(chunkPrintQueue(hundred, 5, 3).map((chunk) => chunk.length).slice(0, 2), [1, 1], '上限小于一只箱的份数时每次至少一只箱')
assertEqual(chunkPrintQueue(hundred, 1, 30).map((chunk) => chunk.length), [30, 30, 30, 10], '自定义页数上限')
assertEqual(uniqueCartons([blank(1), blank(1), blank(2)]).map((item) => item.cartonGuid), ['b1', 'b2'], '去重保持顺序')

// ---- 批次里的「补打未打印的 / 全部重打」 ----
const batchCartons = [
  blank(1, { labelPrintCount: 1 }),
  blank(2),
  blank(3, { labelPrintCount: 0 }),
  blank(4, { status: 9 }),
  blank(5, { activatedAtUtc: '2026-11-15T02:00:00Z' }),
  blank(6, { labelPrintCount: 2 }),
]
assertEqual(filterUnprinted(batchCartons).map((item) => item.cartonGuid), ['b2', 'b3'], '补打未打印：没打印过的未启用空箱标')
assertEqual(filterReprintable(batchCartons).map((item) => item.cartonGuid), ['b1', 'b2', 'b3', 'b6'], '全部重打：未启用的空箱标（不含作废 / 已启用）')
assertEqual(
  cartonsToReportPrinted([blank(1), carton({ cartonGuid: 'n1' }), blank(2, { activatedAtUtc: '2026-11-15T02:00:00Z' })]),
  ['b1', 'b2'],
  '只上报 isBlank 的箱',
)

// ---- 多选：作废结果提示 ----
const selected = [blank(1), blank(2), carton({ cartonGuid: 'n1', status: 2 }), blank(3, { activatedAtUtc: '2026-11-15T02:00:00Z' })]
const selection = summarizeSelection(selected)
assertEqual(selection.blankGuids, ['b1', 'b2'], '只有未启用的空箱标可作废')
assertEqual(selection.otherCount, 2, '其余 2 只不是空箱标')
const voidResponses: PackVoidBlankResult[] = [
  { voided: ['b1'], skipped: [{ cartonGuid: 'b2', reason: 'NOT_BLANK' }] },
  { voided: [], skipped: [{ cartonGuid: 'x', reason: 'NOT_FOUND' }] },
]
const summary = summarizeVoidResult(selection.otherCount, voidResponses)
assertEqual(summary, { voided: 1, notBlank: 3, notFound: 1 }, '汇总：预先排除的 + 后端 NOT_BLANK 都算「不是空箱标」')
assertEqual(
  voidResultParts(summary),
  [
    { key: 'voided', count: 1 },
    { key: 'skippedNotBlank', count: 3 },
    { key: 'skippedNotFound', count: 1 },
  ],
  '提示由「已作废 / 不是空箱标已跳过 / 找不到已跳过」按序组成',
)
assertEqual(voidResultParts({ voided: 5, notBlank: 0, notFound: 0 }), [{ key: 'voided', count: 5 }], '数量为 0 的项不提示')
assertEqual(summarizeVoidResult(0, []), { voided: 0, notBlank: 0, notFound: 0 }, '没有响应')

// ---- BLANK_LIMIT_EXCEEDED 提示 ----
assertEqual(resolveBlankLimitError({ status: 409, errorCode: 'BLANK_LIMIT_EXCEEDED', data: { existing: 290, limit: 300 } }), { existing: 290, limit: 300 }, '取后端的已有数和上限')
assertEqual(resolveBlankLimitError({ status: 409, errorCode: 'BLANK_LIMIT_EXCEEDED', data: null }), { existing: 0, limit: BLANK_UNACTIVATED_LIMIT }, '没带数据时上限兜底 300')
assertEqual(resolveBlankLimitError({ status: 409, errorCode: 'BLANK_LIMIT_EXCEEDED', data: { existing: 12 } }), { existing: 12, limit: 300 }, '只带已有数')
assertEqual(resolveBlankLimitError({ status: 400, errorCode: 'QUANTITY_OUT_OF_RANGE' }), null, '其他错误不是已满')
assertEqual(resolveBlankLimitError(null), null, '空')
assertEqual(remainingBlankQuota(290), 10, '剩余额度')
assertEqual(remainingBlankQuota(400), 0, '超限为 0')
assertEqual(remainingBlankQuota(undefined), 300, '缺失按 0 张算')

// ---- clientRequestId：失败重试复用，参数变了换新 ----
let counter = 0
const generate = () => `guid-${++counter}`
const sigA = blankRequestSignature({ ownerCode: '1008', count: 20 })
const first = resolveClientRequestId(null, sigA, generate)
assertEqual(first.id, 'guid-1', '首次生成')
assertEqual(resolveClientRequestId(first, sigA, generate).id, 'guid-1', '参数不变（失败重试）复用同一个 id')
assertEqual(counter, 1, '复用时不再生成')
const sigB = blankRequestSignature({ ownerCode: '1008', count: 50 })
assertEqual(resolveClientRequestId(first, sigB, generate).id, 'guid-2', '张数变了换新 id，避免拿到上一批')
assertEqual(sigA === blankRequestSignature({ ownerCode: '1008', count: 20, destinationType: 2, destinationCode: '1009' }), false, '去向变了签名变')
assertEqual(/^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/.test(generateRequestGuid()), true, '生成的是 v4 GUID')

// ---- 预览示例箱号 ----
assertEqual(sampleBlankCartonNo(new Date(2026, 10, 15, 9, 0, 0)), 'BX2611150001', '示例箱号：BX + yymmdd + 0001')

console.log('blankCartons.test: ok')
