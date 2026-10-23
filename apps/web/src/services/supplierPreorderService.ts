import type { PagedResult } from '../types/api'
import type {
  SupplierPreorderAuthLog,
  SupplierPreorderAuthLogQuery,
  SupplierPreorderColumnMapping,
  SupplierPreorderCopyStoresResult,
  SupplierPreorderCreateFormPayload,
  SupplierPreorderFormDetail,
  SupplierPreorderFormItem,
  SupplierPreorderFormItemQuery,
  SupplierPreorderFormQuery,
  SupplierPreorderFormStatus,
  SupplierPreorderFormStoreStatus,
  SupplierPreorderFormSummary,
  SupplierPreorderImportRefCandidatesPayload,
  SupplierPreorderItemHistoryDetail,
  SupplierPreorderRefCandidates,
  SupplierPreorderSetRefPayload,
  SupplierPreorderMatrix,
  SupplierPreorderMatrixQuery,
  SupplierPreorderOrderersOverview,
  SupplierPreorderOrderer,
  SupplierPreorderOrderingContext,
  SupplierPreorderOrderingForm,
  SupplierPreorderOrderingItem,
  SupplierPreorderOrderingItemQuery,
  SupplierPreorderOrderingSummary,
  SupplierPreorderParseResult,
  SupplierPreorderParsedRow,
  SupplierPreorderPreviewResult,
  SupplierPreorderSaveLineResult,
  SupplierPreorderSaveOrdererPayload,
  SupplierPreorderStoreRef,
  SupplierPreorderSupplierOption,
  SupplierPreorderUpdateFormPayload,
  SupplierPreorderUserCandidate,
} from '../types/supplierPreorder'
import request, { unwrapApiData, unwrapPagedResult } from '../utils/request'

import { parseContentDispositionFileName } from './linklySettlementService'

const ADMIN_BASE = '/api/react/v1/supplier-preorders/admin'
const ORDERING_BASE = '/api/react/v1/supplier-preorders/ordering'

type Paged<T> = { items: T[]; total: number; page: number; pageSize: number }

// ---------- 导入 ----------

/** 上传原表（.xlsx / .xlsm）解析；文件会在服务器暂存，供「填回原表」导出 */
export async function parseSupplierPreorderFile(file: File, localSupplierCode?: string): Promise<SupplierPreorderParseResult> {
  const form = new FormData()
  form.append('file', file)
  if (localSupplierCode) form.append('localSupplierCode', localSupplierCode)
  return unwrapApiData(await request.post(`${ADMIN_BASE}/import/parse-file`, form))
}

/** 粘贴内容解析：rows 为按行、按制表符拆好的二维数组 */
export async function parseSupplierPreorderPasted(
  rows: (string | null)[][],
  localSupplierCode?: string,
): Promise<SupplierPreorderParseResult> {
  return unwrapApiData(await request.post(`${ADMIN_BASE}/import/parse-pasted`, { rows, localSupplierCode }))
}

export async function previewSupplierPreorderImport(
  localSupplierCode: string,
  mapping: SupplierPreorderColumnMapping,
  rows: SupplierPreorderParsedRow[],
  signal?: AbortSignal,
): Promise<SupplierPreorderPreviewResult> {
  return unwrapApiData(await request.post(`${ADMIN_BASE}/import/preview`, { localSupplierCode, mapping, rows }, { signal }))
}

export async function createSupplierPreorderForm(payload: SupplierPreorderCreateFormPayload): Promise<SupplierPreorderFormDetail> {
  return unwrapApiData(await request.post(`${ADMIN_BASE}/forms`, payload))
}

export async function getSupplierPreorderSuppliers(): Promise<SupplierPreorderSupplierOption[]> {
  return unwrapApiData(await request.get(`${ADMIN_BASE}/suppliers`))
}

export async function getSupplierPreorderActiveStores(): Promise<SupplierPreorderStoreRef[]> {
  return unwrapApiData(await request.get(`${ADMIN_BASE}/stores`))
}

// ---------- 订单表（管理） ----------

export async function getSupplierPreorderForms(query: SupplierPreorderFormQuery): Promise<Paged<SupplierPreorderFormSummary>> {
  return unwrapPagedResult(await request.get<PagedResult<SupplierPreorderFormSummary>>(`${ADMIN_BASE}/forms`, { params: { ...query } }))
}

export async function getSupplierPreorderForm(formGuid: string, signal?: AbortSignal): Promise<SupplierPreorderFormDetail> {
  return unwrapApiData(await request.get(`${ADMIN_BASE}/forms/${formGuid}`, { signal }))
}

export async function updateSupplierPreorderForm(formGuid: string, payload: SupplierPreorderUpdateFormPayload): Promise<SupplierPreorderFormDetail> {
  return unwrapApiData(await request.put(`${ADMIN_BASE}/forms/${formGuid}`, payload))
}

export async function setSupplierPreorderFormStatus(formGuid: string, status: SupplierPreorderFormStatus): Promise<SupplierPreorderFormDetail> {
  return unwrapApiData(await request.post(`${ADMIN_BASE}/forms/${formGuid}/status`, { status }))
}

/** 只能删除没有任何分店下单的订单表 */
export async function deleteSupplierPreorderForm(formGuid: string): Promise<void> {
  unwrapApiData(await request.delete(`${ADMIN_BASE}/forms/${formGuid}`))
}

export async function getSupplierPreorderFormItems(
  formGuid: string,
  query: SupplierPreorderFormItemQuery,
  signal?: AbortSignal,
): Promise<Paged<SupplierPreorderFormItem>> {
  return unwrapPagedResult(await request.get<PagedResult<SupplierPreorderFormItem>>(`${ADMIN_BASE}/forms/${formGuid}/items`, { params: { ...query }, signal }))
}

export async function getSupplierPreorderFormStores(formGuid: string, signal?: AbortSignal): Promise<SupplierPreorderFormStoreStatus[]> {
  return unwrapApiData(await request.get(`${ADMIN_BASE}/forms/${formGuid}/stores`, { signal }))
}

/** 整体覆盖这张订单表开启的分店 */
export async function updateSupplierPreorderFormStores(formGuid: string, storeGuids: string[]): Promise<SupplierPreorderFormStoreStatus[]> {
  return unwrapApiData(await request.put(`${ADMIN_BASE}/forms/${formGuid}/stores`, { storeGuids }))
}

/** 从同供应商同节日的上一张订单表复制开启的分店 */
export async function copySupplierPreorderFormStoresFromPrevious(formGuid: string): Promise<SupplierPreorderCopyStoresResult> {
  return unwrapApiData(await request.post(`${ADMIN_BASE}/forms/${formGuid}/stores/copy-from-previous`))
}

export async function getSupplierPreorderMatrix(
  formGuid: string,
  query: SupplierPreorderMatrixQuery,
  signal?: AbortSignal,
): Promise<SupplierPreorderMatrix> {
  return unwrapApiData(await request.get(`${ADMIN_BASE}/forms/${formGuid}/matrix`, { params: { ...query }, signal }))
}

async function downloadFile(path: string, fallbackName: string): Promise<void> {
  const baseUrl = (import.meta.env.VITE_API_BASE_URL || '').trim()
  const response = await fetch(`${baseUrl}${path}`, { credentials: 'include' })
  if (!response.ok) {
    // 后端业务错误是 JSON（ApiResponse），尽量把中文原因带出来
    let message = `导出失败 (${response.status})`
    try {
      const payload = await response.json()
      if (payload?.message) message = payload.message
    } catch {
      // 非 JSON 响应，沿用状态码提示
    }
    throw new Error(message)
  }
  // ASP.NET 会同时给 filename（ASCII 兜底）和 filename*（UTF-8），必须优先取后者，否则中文名变成下划线
  const fileName = parseContentDispositionFileName(response.headers.get('content-disposition'), fallbackName)
  const blob = await response.blob()
  const url = URL.createObjectURL(blob)
  const anchor = document.createElement('a')
  anchor.href = url
  anchor.download = fileName
  anchor.click()
  URL.revokeObjectURL(url)
}

/** format = original 填回供应商原表；generic 通用格式 */
export function downloadSupplierPreorderExport(formGuid: string, format: 'original' | 'generic'): Promise<void> {
  return downloadFile(`${ADMIN_BASE}/forms/${formGuid}/export?format=${format}`, `supplier-preorder-${format}.xlsx`)
}

// ---------- 下单人员授权 ----------

export async function getSupplierPreorderOrderers(signal?: AbortSignal): Promise<SupplierPreorderOrderersOverview> {
  return unwrapApiData(await request.get(`${ADMIN_BASE}/orderers`, { signal }))
}

export async function saveSupplierPreorderOrderer(userGuid: string, payload: SupplierPreorderSaveOrdererPayload): Promise<SupplierPreorderOrderer> {
  return unwrapApiData(await request.put(`${ADMIN_BASE}/orderers/${userGuid}`, payload))
}

export async function removeSupplierPreorderOrderer(userGuid: string): Promise<void> {
  unwrapApiData(await request.delete(`${ADMIN_BASE}/orderers/${userGuid}`))
}

export async function searchSupplierPreorderUserCandidates(keyword: string, signal?: AbortSignal): Promise<SupplierPreorderUserCandidate[]> {
  return unwrapApiData(await request.get(`${ADMIN_BASE}/orderers/candidates`, { params: { keyword }, signal }))
}

export async function getSupplierPreorderAuthLogs(query: SupplierPreorderAuthLogQuery, signal?: AbortSignal): Promise<Paged<SupplierPreorderAuthLog>> {
  return unwrapPagedResult(await request.get<PagedResult<SupplierPreorderAuthLog>>(`${ADMIN_BASE}/auth-logs`, { params: { ...query }, signal }))
}

// ---------- 下单 ----------

export async function getSupplierPreorderOrderingForms(signal?: AbortSignal): Promise<SupplierPreorderOrderingForm[]> {
  return unwrapApiData(await request.get(`${ORDERING_BASE}/forms`, { signal }))
}

export async function getSupplierPreorderOrderingContext(formGuid: string, signal?: AbortSignal): Promise<SupplierPreorderOrderingContext> {
  return unwrapApiData(await request.get(`${ORDERING_BASE}/forms/${formGuid}/context`, { signal }))
}

export async function getSupplierPreorderOrderingSummary(
  formGuid: string,
  storeGuid: string,
  signal?: AbortSignal,
): Promise<SupplierPreorderOrderingSummary> {
  return unwrapApiData(await request.get(`${ORDERING_BASE}/forms/${formGuid}/summary`, { params: { storeGuid }, signal }))
}

export async function getSupplierPreorderOrderingItems(
  formGuid: string,
  query: SupplierPreorderOrderingItemQuery,
  signal?: AbortSignal,
): Promise<Paged<SupplierPreorderOrderingItem>> {
  return unwrapPagedResult(await request.get<PagedResult<SupplierPreorderOrderingItem>>(`${ORDERING_BASE}/forms/${formGuid}/items`, { params: { ...query }, signal }))
}

/** 保存某商品的本店箱数（0 = 删除该行）；返回最新汇总 */
export async function saveSupplierPreorderLine(
  formGuid: string,
  storeGuid: string,
  itemGuid: string,
  cartons: number,
): Promise<SupplierPreorderSaveLineResult> {
  return unwrapApiData(await request.put(`${ORDERING_BASE}/forms/${formGuid}/stores/${storeGuid}/lines/${itemGuid}`, { cartons }))
}

export async function submitSupplierPreorderStoreOrder(formGuid: string, storeGuid: string): Promise<SupplierPreorderOrderingSummary> {
  return unwrapApiData(await request.post(`${ORDERING_BASE}/forms/${formGuid}/stores/${storeGuid}/submit`))
}

export function downloadSupplierPreorderStoreExport(formGuid: string, storeGuid: string): Promise<void> {
  return downloadFile(`${ORDERING_BASE}/forms/${formGuid}/stores/${storeGuid}/export`, 'supplier-preorder-store.xlsx')
}

// ---------- 去年历史与参考商品 ----------

/** 管理端：把转存失败满 3 次的新品图片清零，后台重新下载 */
export async function retrySupplierPreorderFailedImages(formGuid: string): Promise<SupplierPreorderFormDetail> {
  return unwrapApiData(await request.post(`${ADMIN_BASE}/forms/${formGuid}/images/retry-failed`))
}

/** 管理端：重新计算这张订单表的去年历史（排队，后台算） */
export async function recomputeSupplierPreorderHistory(formGuid: string): Promise<SupplierPreorderFormDetail> {
  return unwrapApiData(await request.post(`${ADMIN_BASE}/forms/${formGuid}/history/recompute`))
}

/** 管理端：某款商品的参考商品候选；不带关键词时返回系统推荐前 3，带关键词时按货号/条码/名称搜索 */
export async function getSupplierPreorderRefCandidates(
  formGuid: string,
  itemGuid: string,
  keyword?: string,
  signal?: AbortSignal,
): Promise<SupplierPreorderRefCandidates> {
  return unwrapApiData(
    await request.get(`${ADMIN_BASE}/forms/${formGuid}/items/${itemGuid}/ref-candidates`, { params: { keyword: keyword || undefined }, signal }),
  )
}

/** 管理端：指定 / 清除 / 交回系统推荐 参考商品，返回更新后的商品行 */
export async function setSupplierPreorderItemRef(
  formGuid: string,
  itemGuid: string,
  payload: SupplierPreorderSetRefPayload,
): Promise<SupplierPreorderFormItem> {
  return unwrapApiData(await request.put(`${ADMIN_BASE}/forms/${formGuid}/items/${itemGuid}/ref`, payload))
}

/** 导入校对页：订单表还没建，按将要建的供应商与历史窗口找参考商品候选 */
export async function getSupplierPreorderImportRefCandidates(
  payload: SupplierPreorderImportRefCandidatesPayload,
  signal?: AbortSignal,
): Promise<SupplierPreorderRefCandidates> {
  return unwrapApiData(await request.post(`${ADMIN_BASE}/import/ref-candidates`, payload, { signal }))
}

/** 下单端：展开行的本店大图、小结与各分店去年对比 */
export async function getSupplierPreorderItemHistory(
  formGuid: string,
  itemGuid: string,
  storeGuid: string,
  signal?: AbortSignal,
): Promise<SupplierPreorderItemHistoryDetail> {
  return unwrapApiData(await request.get(`${ORDERING_BASE}/forms/${formGuid}/items/${itemGuid}/history`, { params: { storeGuid }, signal }))
}
