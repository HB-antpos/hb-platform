// 订单表信息表单（导入第 3 步、详情页编辑抽屉共用）的值类型与 表单值 ⇄ 接口字段 转换。
import dayjs, { type Dayjs } from 'dayjs'

import type {
  SupplierPreorderExportQtyUnit,
  SupplierPreorderFormDetail,
  SupplierPreorderFormHeaderFields,
  SupplierPreorderHeaderInfo,
  SupplierPreorderHolidayType,
} from '../../../types/supplierPreorder'

import {
  CONTAINER_PRESET_CBM,
  buildDefaultFormTitle,
  getDefaultHistoryRange,
  normalizeDateOnly,
  resolveContainerPreset,
  sydneyDateEndToUtcIso,
  utcIsoToSydneyDate,
  type ContainerPreset,
} from './supplierPreorderAdmin.logic'

export interface FormHeaderValues {
  holidayType: SupplierPreorderHolidayType
  seasonYear: number
  title: string
  quoteNo?: string
  offerDate?: Dayjs | null
  /** 只选日期，提交时按悉尼时间当天 23:59:59 换算成 UTC */
  deadlineDate?: Dayjs | null
  containerPreset: ContainerPreset
  containerType?: string
  containerCapacityCbm?: number | null
  priceTerms?: string
  historyRange?: [Dayjs, Dayjs]
  exportQtyUnit: SupplierPreorderExportQtyUnit
  notes?: string
}

function toDayjs(value?: string | null): Dayjs | null {
  const date = normalizeDateOnly(value)
  return date ? dayjs(date) : null
}

function toHistoryRange(from?: string | null, to?: string | null): [Dayjs, Dayjs] | undefined {
  const start = toDayjs(from)
  const end = toDayjs(to)
  return start && end ? [start, end] : undefined
}

function trimOrNull(value?: string | null): string | null {
  const text = value?.trim()
  return text ? text : null
}

/** 导入时：从原表表头自动带出（可改）；缺的按今年圣诞兜底。 */
export function buildHeaderValuesFromParse(
  headerInfo: SupplierPreorderHeaderInfo,
  supplierName: string | null | undefined,
  exportQtyUnit: SupplierPreorderExportQtyUnit,
  nowYear: number,
): FormHeaderValues {
  const holidayType = headerInfo.holidayType ?? 'Christmas'
  const seasonYear = headerInfo.seasonYear ?? nowYear
  const containerPreset = resolveContainerPreset(headerInfo.containerType)
  const history = getDefaultHistoryRange(holidayType, seasonYear)
  return {
    holidayType,
    seasonYear,
    title: buildDefaultFormTitle(supplierName, holidayType, seasonYear),
    quoteNo: headerInfo.quoteNo ?? undefined,
    offerDate: toDayjs(headerInfo.offerDate),
    deadlineDate: toDayjs(headerInfo.deadlineDate),
    containerPreset,
    containerType: headerInfo.containerType ?? (containerPreset === 'none' ? undefined : containerPreset),
    containerCapacityCbm: containerPreset === '40GP' || containerPreset === '20GP' ? CONTAINER_PRESET_CBM[containerPreset] : null,
    priceTerms: headerInfo.priceTerms ?? undefined,
    historyRange: history ? toHistoryRange(history.from, history.to) : undefined,
    exportQtyUnit,
    notes: undefined,
  }
}

/** 编辑时：从订单表详情回填。 */
export function buildHeaderValuesFromDetail(detail: SupplierPreorderFormDetail): FormHeaderValues {
  const { summary } = detail
  return {
    holidayType: summary.holidayType,
    seasonYear: summary.seasonYear,
    title: summary.title,
    quoteNo: summary.quoteNo ?? undefined,
    offerDate: toDayjs(detail.offerDate),
    deadlineDate: toDayjs(utcIsoToSydneyDate(summary.deadlineAtUtc)),
    containerPreset: resolveContainerPreset(detail.containerType),
    containerType: detail.containerType ?? undefined,
    containerCapacityCbm: detail.containerCapacityCbm ?? null,
    priceTerms: detail.priceTerms ?? undefined,
    historyRange: toHistoryRange(detail.historyFrom, detail.historyTo),
    exportQtyUnit: summary.exportQtyUnit,
    notes: detail.notes ?? undefined,
  }
}

export function toHeaderPayload(values: FormHeaderValues): SupplierPreorderFormHeaderFields {
  const deadlineDate = values.deadlineDate ? values.deadlineDate.format('YYYY-MM-DD') : null
  const hasContainer = values.containerPreset !== 'none'
  return {
    holidayType: values.holidayType,
    seasonYear: values.seasonYear,
    title: values.title.trim(),
    quoteNo: trimOrNull(values.quoteNo),
    offerDate: values.offerDate ? values.offerDate.format('YYYY-MM-DD') : null,
    deadlineAtUtc: deadlineDate ? sydneyDateEndToUtcIso(deadlineDate) : null,
    containerType: hasContainer ? trimOrNull(values.containerType) : null,
    containerCapacityCbm: hasContainer ? values.containerCapacityCbm ?? null : null,
    priceTerms: trimOrNull(values.priceTerms),
    historyFrom: values.historyRange?.[0]?.format('YYYY-MM-DD') ?? null,
    historyTo: values.historyRange?.[1]?.format('YYYY-MM-DD') ?? null,
    exportQtyUnit: values.exportQtyUnit,
    notes: trimOrNull(values.notes),
  }
}
