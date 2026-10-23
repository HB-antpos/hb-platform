import assert from 'node:assert/strict'

import type { SupplierPreorderFormDetail } from '../../../types/supplierPreorder'

import { buildHeaderValuesFromDetail, buildHeaderValuesFromParse, toHeaderPayload } from './formHeaderValues'

// 从原表表头带出 → 提交：截止日期按悉尼 23:59:59 换算、40GP 预设带出柜型与容量、圣诞默认历史季
const fromParse = buildHeaderValuesFromParse(
  { quoteNo: 'QT0013074', offerDate: '2026-02-05T00:00:00', deadlineDate: '2026-03-06', containerType: '40GP', seasonYear: 2026, holidayType: 'Christmas' },
  'Uncle Bills (KFC)',
  'Cartons',
  2030,
)
assert.equal(fromParse.title, 'Uncle Bills (KFC) · Christmas 2026')
assert.equal(fromParse.containerPreset, '40GP')
assert.deepEqual(toHeaderPayload({ ...fromParse, notes: '  ', priceTerms: ' FOB Ningbo ' }), {
  holidayType: 'Christmas',
  seasonYear: 2026,
  title: 'Uncle Bills (KFC) · Christmas 2026',
  quoteNo: 'QT0013074',
  offerDate: '2026-02-05',
  deadlineAtUtc: '2026-03-06T12:59:59.000Z',
  containerType: '40GP',
  containerCapacityCbm: 67,
  priceTerms: 'FOB Ningbo',
  historyFrom: '2025-09-01',
  historyTo: '2026-01-18',
  exportQtyUnit: 'Cartons',
  notes: null,
})

// 表头什么都没有：按今年圣诞兜底，柜型为空时不提交容量
const empty = buildHeaderValuesFromParse({}, null, 'Pieces', 2027)
assert.equal(empty.holidayType, 'Christmas')
assert.equal(empty.seasonYear, 2027)
assert.equal(empty.containerPreset, 'none')
const emptyPayload = toHeaderPayload({ ...empty, containerCapacityCbm: 50 })
assert.equal(emptyPayload.containerType, null)
assert.equal(emptyPayload.containerCapacityCbm, null)
assert.equal(emptyPayload.deadlineAtUtc, null)
assert.equal(emptyPayload.exportQtyUnit, 'Pieces')

// 编辑回填：UTC 截止 → 悉尼日期；万圣节无历史季时为空
const detail = {
  summary: {
    formGuid: 'f',
    localSupplierCode: '240',
    supplierName: 'Dats',
    holidayType: 'Halloween',
    seasonYear: 2026,
    title: 'Dats 自定义标题',
    quoteNo: null,
    status: 'Open',
    deadlineAtUtc: '2026-06-30T13:59:59Z',
    itemCount: 1,
    enabledStoreCount: 0,
    submittedStoreCount: 0,
    draftStoreCount: 0,
    totalCartons: 0,
    totalAmount: 0,
    totalCbm: 0,
    hasSourceFile: true,
    exportQtyUnit: 'Pieces',
    createdAtUtc: '2026-02-01T00:00:00Z',
  },
  containerType: '1 pallet',
  containerCapacityCbm: 2,
  historyFrom: null,
  historyTo: null,
  matchedCount: 0,
  newCount: 0,
  warningCount: 0,
  categories: [],
  history: { status: 'NoWindow', keyDates: [] },
  ownHistoryCount: 0,
  refAutoCount: 0,
  refManualCount: 0,
  needRefCount: 0,
} as SupplierPreorderFormDetail
const fromDetail = buildHeaderValuesFromDetail(detail)
assert.equal(fromDetail.deadlineDate?.format('YYYY-MM-DD'), '2026-06-30')
assert.equal(fromDetail.containerPreset, 'custom')
assert.equal(fromDetail.historyRange, undefined)
const detailPayload = toHeaderPayload(fromDetail)
assert.equal(detailPayload.deadlineAtUtc, '2026-06-30T13:59:59.000Z')
assert.equal(detailPayload.containerType, '1 pallet')
assert.equal(detailPayload.containerCapacityCbm, 2)
assert.equal(detailPayload.historyFrom, null)
assert.equal(detailPayload.title, 'Dats 自定义标题')
