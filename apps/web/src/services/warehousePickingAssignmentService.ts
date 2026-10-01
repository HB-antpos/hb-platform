import type { ApiResponse } from '../types/api'
import request, { RequestError, unwrapApiData } from '../utils/request'

/**
 * 仓库拣货派单接口（仓库经理用）：候选员工、分段预览、保存/撤销、批量平均分、分单打印数据。
 * 字段与后端 WarehousePickingDtos 一一对应（camelCase，空值字段会被省略）。
 */
const API_BASE = '/api/react/v1/warehouse-picking'

export interface PickerCandidate {
  pickerUserGuid: string
  pickerName: string
  roleLabel?: string | null
  activeOrderCount: number
}

/** 一段的输入：pickerUserGuid 为空表示打印分单后由员工扫码领取。 */
export interface AssignmentPickerInput {
  pickerUserGuid: string | null
  /** 全部为空时后端按品种数平均分。 */
  lineCount?: number | null
}

export interface AssignmentSegment {
  pickerUserGuid?: string | null
  pickerName?: string | null
  lineCount: number
  pieces: number
  firstLocation?: string | null
  lastLocation?: string | null
  unlocatedLineCount: number
  irregularLineCount: number
  detailGuids: string[]
}

export interface AssignmentPreview {
  lineCount: number
  pieces: number
  unlocatedLineCount: number
  irregularLineCount: number
  segments: AssignmentSegment[]
}

/** 一段的负责人；pickerUserGuid 为空表示待员工扫分单领取。 */
export interface Assignee {
  pickerUserGuid?: string | null
  pickerName?: string | null
  lineCount: number
  segmentNo: number
  slipCode?: string | null
  firstLocation?: string | null
  lastLocation?: string | null
  pieces?: number | null
  pickedPieces?: number | null
  completedLineCount?: number | null
  stockoutLineCount?: number | null
  lastActiveAtUtc?: string | null
}

export interface AssignmentSummary {
  orderGuid: string
  lineCount: number
  unassignedLineCount: number
  assignees: Assignee[]
  assignedByName?: string | null
  assignedAtUtc?: string | null
  lines: { detailGuid: string; segmentNo: number }[]
}

export interface BatchAssignItem {
  orderGuid: string
  orderNo?: string | null
  success: boolean
  errorCode?: string | null
  message?: string | null
}

export interface PickingSlipLine {
  detailGuid: string
  locationCode?: string | null
  zone?: string | null
  rowLabel?: string | null
  itemNumber?: string | null
  productName?: string | null
  barcode?: string | null
  orderedQuantity: number
  minOrderQuantity?: number | null
}

export interface PickingSlip {
  segmentNo: number
  segmentCount: number
  slipCode: string
  pickerUserGuid?: string | null
  pickerName?: string | null
  lineCount: number
  pieces: number
  firstLocation?: string | null
  lastLocation?: string | null
  otherPickerNames: string[]
  lines: PickingSlipLine[]
}

export interface PickingSlips {
  orderGuid: string
  orderNo?: string | null
  storeCode?: string | null
  storeName?: string | null
  orderDate?: string | null
  assignedByName?: string | null
  assignedAtUtc?: string | null
  slips: PickingSlip[]
}

type Envelope<T> = ApiResponse<T> | T

function orderPath(orderGuid: string, suffix = '') {
  return `${API_BASE}/orders/${encodeURIComponent(orderGuid)}/assignments${suffix}`
}

/** 后端业务错误码（如 ASSIGN_LINES_INVALID 表示预览后订单明细变了，需要重新预览）。 */
export function readAssignmentErrorCode(error: unknown): string | null {
  if (!(error instanceof RequestError)) return null
  const payload = error.payload as { errorCode?: unknown } | null | undefined
  return typeof payload?.errorCode === 'string' ? payload.errorCode : null
}

export async function listPickerCandidates(signal?: AbortSignal) {
  return unwrapApiData(await request<Envelope<PickerCandidate[]>>(`${API_BASE}/pickers`, { signal })) ?? []
}

export async function getAssignment(orderGuid: string, signal?: AbortSignal) {
  return unwrapApiData(await request<Envelope<AssignmentSummary>>(orderPath(orderGuid), { signal }))
}

export async function previewAssignment(orderGuid: string, pickers: AssignmentPickerInput[], signal?: AbortSignal) {
  return unwrapApiData(
    await request<Envelope<AssignmentPreview>>(orderPath(orderGuid, '/preview'), {
      method: 'POST',
      data: { pickers },
      signal,
    }),
  )
}

/** 保存的就是预览出来的逐行归属：每段一个员工与其明细 GUID 列表，按段号顺序。 */
export async function saveAssignment(orderGuid: string, segments: { pickerUserGuid: string | null; detailGuids: string[] }[]) {
  return unwrapApiData(
    await request<Envelope<AssignmentSummary>>(orderPath(orderGuid), {
      method: 'PUT',
      data: { assignments: segments },
    }),
  )
}

export async function clearAssignment(orderGuid: string) {
  return unwrapApiData(await request<Envelope<AssignmentSummary>>(orderPath(orderGuid), { method: 'DELETE' }))
}

/** 批量平均分：指定员工时按人数分段；不指定时按 segmentCount 分段、全部待扫码领取。 */
export async function assignOrdersEvenly(orderGuids: string[], pickerUserGuids: string[], segmentCount?: number) {
  const result = unwrapApiData(
    await request<Envelope<{ items: BatchAssignItem[] }>>(`${API_BASE}/assignments/batch`, {
      method: 'POST',
      data: { orderGuids, pickerUserGuids, segmentCount: pickerUserGuids.length > 0 ? null : segmentCount },
    }),
  )
  return result?.items ?? []
}

/** 订单列表“拣货分配”列：返回 { 订单GUID: 负责人列表 }，没有分配的订单不在结果里。 */
export async function listAssignmentSummaries(orderGuids: string[], signal?: AbortSignal) {
  if (orderGuids.length === 0) return {}
  return (
    unwrapApiData(
      await request<Envelope<Record<string, Assignee[]>>>(`${API_BASE}/assignments/summaries`, {
        method: 'POST',
        data: { orderGuids },
        signal,
      }),
    ) ?? {}
  )
}

/** 改某一段的负责人；pickerUserGuid 为空表示释放为待领取（不改版本，已打印的分单仍有效）。 */
export async function setSegmentPicker(orderGuid: string, segmentNo: number, pickerUserGuid: string | null) {
  return unwrapApiData(
    await request<Envelope<AssignmentSummary>>(orderPath(orderGuid, `/segments/${segmentNo}/picker`), {
      method: 'PUT',
      data: { pickerUserGuid },
    }),
  )
}

export async function getPickingSlips(orderGuid: string, segmentNo?: number | null, signal?: AbortSignal) {
  return unwrapApiData(
    await request<Envelope<PickingSlips>>(orderPath(orderGuid, '/slips'), {
      params: segmentNo ? { segmentNo } : undefined,
      signal,
    }),
  )
}
