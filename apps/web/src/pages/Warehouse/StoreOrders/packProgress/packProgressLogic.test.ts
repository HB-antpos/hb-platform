import type { WarehousePackProgress, WarehousePackProgressCarton, WarehousePackProgressLine } from '../../../../types/packing'
import { normalizeWarehousePackProgress } from '../../../../services/warehousePackProgressService'

import {
  formatPackDiff,
  formatPackPieces,
  hasPackLineMismatch,
  packCartonStatusKind,
  packLineDiff,
  resolvePackCompletionWarning,
  shouldShowPackProgress,
  sortPackLines,
  summarizePackProgress,
} from './packProgressLogic'

function assertDeepEqual(actual: unknown, expected: unknown, label: string) {
  const actualText = JSON.stringify(actual)
  const expectedText = JSON.stringify(expected)
  if (actualText !== expectedText) {
    throw new Error(`${label}。Expected: ${expectedText}, received: ${actualText}`)
  }
}

function assertEqual<T>(actual: T, expected: T, label: string) {
  if (actual !== expected) {
    throw new Error(`${label}。Expected: ${String(expected)}, received: ${String(actual)}`)
  }
}

function line(code: string, picked: number, packed: number, overrides: Partial<WarehousePackProgressLine> = {}): WarehousePackProgressLine {
  return { productCode: code, orderedPieces: picked, pickedPieces: picked, packedPieces: packed, ...overrides }
}

function carton(no: string, status: number, pieceCount = 12, overrides: Partial<WarehousePackProgressCarton> = {}): WarehousePackProgressCarton {
  return { cartonGuid: `g-${no}`, cartonNo: no, status, skuCount: 1, pieceCount, createdByName: 'Amy', isMine: false, ...overrides }
}

function progress(overrides: Partial<WarehousePackProgress> = {}): WarehousePackProgress {
  return {
    orderGuid: 'o-1',
    flowStatus: 2,
    lines: [],
    cartons: [],
    openCartonCount: 0,
    emptyCartonCount: 0,
    sealedCartonCount: 0,
    reconciled: false,
    ...overrides,
  }
}

// ---- 差异计算：差 = 已拣 - 已装 ----
assertEqual(packLineDiff(line('A', 10, 10)), 0, '拣=装，差为 0')
assertEqual(packLineDiff(line('A', 10, 7)), 3, '拣了没装进箱：差为正')
assertEqual(packLineDiff(line('A', 5, 8)), -3, '箱里装多了：差为负')
assertEqual(hasPackLineMismatch(line('A', 10, 10)), false, '差为 0 不高亮')
assertEqual(hasPackLineMismatch(line('A', 10, 9)), true, '差不为 0 高亮')
assertEqual(formatPackDiff(3), '+3', '正数带加号')
assertEqual(formatPackDiff(-2), '-2', '负数带减号')
assertEqual(formatPackDiff(0), '0', '零不带符号')
assertEqual(formatPackPieces(12), '12', '整数不带小数')
assertEqual(formatPackPieces(12.5), '12.5', '订货件数是 decimal，保留小数')
assertEqual(formatPackPieces(1234), '1,234', '千分位')
assertEqual(formatPackPieces(undefined), '0', '缺失按 0')

// 有差异的商品排在前面，其余保持原顺序。
assertDeepEqual(
  sortPackLines([line('A', 5, 5), line('B', 5, 4), line('C', 5, 5), line('D', 2, 6)]).map((entry) => entry.productCode),
  ['B', 'D', 'A', 'C'],
  '差异商品在前、其余保持原顺序',
)

// ---- 装箱状态标签 ----
assertEqual(packCartonStatusKind(1), 'packing', '1 装箱中')
assertEqual(packCartonStatusKind(2), 'sealed', '2 已封')
assertEqual(packCartonStatusKind(3), 'transit', '3 在途')
assertEqual(packCartonStatusKind(9), 'unknown', '作废（后端不返回）按未知')
assertEqual(packCartonStatusKind(0), 'unknown', '其他值按未知')

// ---- 没有任何箱：整块不显示 ----
assertEqual(shouldShowPackProgress(null), false, '没读到数据不显示')
assertEqual(shouldShowPackProgress(progress()), false, '订单没有箱（没用装箱功能）不显示')
assertEqual(shouldShowPackProgress(progress({ cartons: [carton('BX1', 1)] })), true, '有箱就显示')

// ---- 摘要 ----
const summary = summarizePackProgress(
  progress({
    lines: [line('A', 10, 10), line('B', 6, 4, { orderedPieces: 8 }), line('C', 0, 3)],
    cartons: [carton('BX1', 1, 0), carton('BX2', 1, 5), carton('BX3', 2), carton('BX4', 2), carton('BX5', 3)],
  }),
)
assertDeepEqual(
  summary,
  {
    orderedPieces: 18,
    pickedPieces: 16,
    packedPieces: 17,
    cartonCount: 5,
    packingCartonCount: 2,
    sealedCartonCount: 2,
    transitCartonCount: 1,
    emptyCartonCount: 1,
    mismatchLineCount: 2,
    packedDiffersFromPicked: true,
  },
  '摘要按商品行与箱列表汇总',
)
assertEqual(
  summarizePackProgress(progress({ lines: [line('A', 4, 4)], cartons: [carton('BX1', 2)] })).packedDiffersFromPicked,
  false,
  '已装 = 已拣时不用警示色',
)

// ---- 完成出库提醒的判定：只提醒、不拦截 ----
assertEqual(resolvePackCompletionWarning(null), null, '没读到数据（失败）不提醒')
assertEqual(
  resolvePackCompletionWarning(progress({ lines: [line('A', 5, 0)], reconciled: false })),
  null,
  '订单没有任何箱时 reconciled 恒为 false，但没用装箱功能不能提醒',
)
assertEqual(
  resolvePackCompletionWarning(progress({ lines: [line('A', 5, 5)], cartons: [carton('BX1', 2)], sealedCartonCount: 1, reconciled: true })),
  null,
  '全部封箱且对上账不提醒',
)
assertDeepEqual(
  resolvePackCompletionWarning(
    progress({
      lines: [line('A', 5, 5)],
      cartons: [carton('BX1', 1, 5), carton('BX2', 1, 0), carton('BX3', 2)],
      openCartonCount: 2,
      emptyCartonCount: 1,
      sealedCartonCount: 1,
      reconciled: false,
    }),
  ),
  { openCartonCount: 2, emptyCartonCount: 1, mismatchLineCount: 0 },
  '有未封箱（含空箱）就提醒，并带出空箱数',
)
assertDeepEqual(
  resolvePackCompletionWarning(
    progress({
      lines: [line('A', 5, 4), line('B', 3, 3), line('C', 2, 5)],
      cartons: [carton('BX1', 2)],
      sealedCartonCount: 1,
      reconciled: false,
    }),
  ),
  { openCartonCount: 0, emptyCartonCount: 0, mismatchLineCount: 2 },
  '全部封箱但拣装对不上：提醒差异商品数',
)
assertDeepEqual(
  resolvePackCompletionWarning(progress({ lines: [line('A', 5, 5)], cartons: [carton('BX1', 1, 5)], openCartonCount: 1, reconciled: false })),
  { openCartonCount: 1, emptyCartonCount: 0, mismatchLineCount: 0 },
  '只有未封箱、商品对得上：仍提醒',
)
assertEqual(
  resolvePackCompletionWarning(
    progress({ lines: [line('A', 5, 5)], cartons: [carton('BX1', 1, 0)], openCartonCount: 1, emptyCartonCount: 1, reconciled: true }),
  )?.openCartonCount,
  1,
  '只有空箱（对账通过）但 openCartonCount>0 也按规格提醒',
)

// ---- 后端 null 字段不输出：缺失的列表 / 计数补默认值 ----
const normalized = normalizeWarehousePackProgress({ orderGuid: 'o-9', flowStatus: 2 } as WarehousePackProgress)
assertDeepEqual(
  [normalized.lines, normalized.cartons, normalized.openCartonCount, normalized.emptyCartonCount, normalized.sealedCartonCount, normalized.reconciled],
  [[], [], 0, 0, 0, false],
  '缺失字段补默认值',
)
assertEqual(normalized.orderNo, undefined, '可空字段保持缺失，不补 null')
assertEqual(
  normalizeWarehousePackProgress({ ...progress(), reconciled: true }).reconciled,
  true,
  'reconciled 为 true 时保留',
)

// 新后端装箱中的箱带 createdAtUtc / items，旧后端没有：两种都能读，汇总不受影响，字段原样保留（界面暂不展示）。
const withNewFields = normalizeWarehousePackProgress(
  progress({
    cartons: [
      carton('BX1', 1, 5, { createdAtUtc: '2026-11-14T01:00:00Z', items: [{ productCode: 'A', quantity: 5 }] }),
      carton('BX2', 2, 7),
    ],
  }),
)
assertDeepEqual(withNewFields.cartons[0].items, [{ productCode: 'A', quantity: 5 }], '装箱中的箱的 items 原样保留')
assertEqual(withNewFields.cartons[1].createdAtUtc, undefined, '旧后端没有 createdAtUtc 时保持缺失')
assertEqual(summarizePackProgress(withNewFields).cartonCount, 2, '新字段不影响汇总')

console.log('packProgressLogic.test: ok')
