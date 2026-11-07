import type { ApiResponse, PagedResult } from '../types/api'
import type {
  StaffNotificationBatch,
  StaffNotificationBatchDetail,
  StaffNotificationBatchQuery,
  StaffNotificationPreflight,
  StaffNotificationPreflightRequest,
  StaffNotificationPreview,
  StaffNotificationPreviewRequest,
  StaffNotificationSendRequest,
  StaffNotificationSendResult,
  StaffNotificationSmsSender,
  StaffNotificationTemplate,
  StaffNotificationTemplateSave,
} from '../types/staffNotification'
import request, { unwrapApiData, unwrapPagedResult } from '../utils/request'

/**
 * 员工通知接口。后端契约：
 * - 发送 / 预检 / 预览 / 查记录需要 Users.ResetPassword；模板新建、修改、恢复默认仅管理员。
 * - 业务失败走 ApiResponse.success=false（HTTP 可能仍是 200），unwrapApiData 会转成 RequestError，
 *   errorCode 与 message 都在 error.payload 里（TEMPLATE_INVALID / CONFIRM_REQUIRED，见 types/staffNotification）。
 */
const BASE = '/api/staff-notifications'

const encode = (value: string) => encodeURIComponent(value)

// ===== 模板 =====

export async function getStaffNotificationTemplates(): Promise<StaffNotificationTemplate[]> {
  const response = await request.get<ApiResponse<StaffNotificationTemplate[]>>(`${BASE}/templates`, { cache: 'no-store' })
  return unwrapApiData(response) ?? []
}

export async function saveStaffNotificationTemplate(
  key: string,
  payload: StaffNotificationTemplateSave,
): Promise<StaffNotificationTemplate> {
  const response = await request.put<ApiResponse<StaffNotificationTemplate>>(`${BASE}/templates/${encode(key)}`, toTemplateSaveBody(payload))
  return unwrapApiData(response)
}

export async function createStaffNotificationTemplate(
  payload: StaffNotificationTemplateSave,
): Promise<StaffNotificationTemplate> {
  const response = await request.post<ApiResponse<StaffNotificationTemplate>>(`${BASE}/templates`, toTemplateSaveBody(payload))
  return unwrapApiData(response)
}

/** 恢复系统模板的默认文案（仅系统模板）。 */
export async function resetStaffNotificationTemplate(key: string): Promise<StaffNotificationTemplate> {
  const response = await request.post<ApiResponse<StaffNotificationTemplate>>(`${BASE}/templates/${encode(key)}/reset`)
  return unwrapApiData(response)
}

/** 只发契约里的五个字段，不把模板上的 key / kind / isCustomized 等只读字段回传。 */
function toTemplateSaveBody(payload: StaffNotificationTemplateSave): StaffNotificationTemplateSave {
  return {
    name: payload.name,
    isEnabled: payload.isEnabled,
    emailSubject: payload.emailSubject,
    emailBody: payload.emailBody,
    smsBody: payload.smsBody,
  }
}

// ===== 预检 / 预览 =====

export async function preflightStaffNotification(
  payload: StaffNotificationPreflightRequest,
  signal?: AbortSignal,
): Promise<StaffNotificationPreflight> {
  const response = await request.post<ApiResponse<StaffNotificationPreflight>>(
    `${BASE}/preflight`,
    {
      templateKey: payload.templateKey,
      userGuids: payload.userGuids,
      sendEmail: payload.sendEmail,
      sendSms: payload.sendSms,
    },
    { signal },
  )
  const data = unwrapApiData(response)
  // 缺字段时给安全默认值：没有短信可用信息就当不可用，不会误显示「短信服务已开启」。
  return {
    smsAvailable: data?.smsAvailable === true,
    smsSenders: normalizeSmsSenders(data?.smsSenders),
    smsUsedLast24h: Number(data?.smsUsedLast24h ?? 0),
    smsDailyLimit: Number(data?.smsDailyLimit ?? 0),
    smsUnitCostAud: Number(data?.smsUnitCostAud ?? 0),
    recipients: data?.recipients ?? [],
  }
}

/** 发件名列表缺省（旧后端 / 短信不可用）按空数组；丢掉没有名字的项，避免下拉里出现空选项。 */
function normalizeSmsSenders(value: unknown): StaffNotificationSmsSender[] {
  if (!Array.isArray(value)) return []
  return value
    .filter((item): item is StaffNotificationSmsSender => typeof item?.id === 'string' && item.id.trim() !== '')
    .map((item) => ({ id: item.id, isDefault: item.isDefault === true }))
}

export async function previewStaffNotification(
  payload: StaffNotificationPreviewRequest,
  signal?: AbortSignal,
): Promise<StaffNotificationPreview> {
  const response = await request.post<ApiResponse<StaffNotificationPreview>>(
    `${BASE}/preview`,
    { templateKey: payload.templateKey, userGuid: payload.userGuid },
    { signal },
  )
  return unwrapApiData(response)
}

// ===== 发送 / 批次 =====

export async function sendStaffNotification(payload: StaffNotificationSendRequest): Promise<StaffNotificationSendResult> {
  const response = await request.post<ApiResponse<StaffNotificationSendResult>>(`${BASE}/send`, {
    templateKey: payload.templateKey,
    userGuids: payload.userGuids,
    sendEmail: payload.sendEmail,
    sendSms: payload.sendSms,
    // 空串 / 未选不传：服务端按「不传 = 默认发件名」处理。
    ...(payload.smsSenderId ? { smsSenderId: payload.smsSenderId } : {}),
    confirmReissue: payload.confirmReissue,
  })
  const data = unwrapApiData(response)
  return { ...data, rejected: data?.rejected ?? [] }
}

export async function getStaffNotificationBatches(query: StaffNotificationBatchQuery): Promise<PagedResult<StaffNotificationBatch>> {
  const response = await request.get<ApiResponse<PagedResult<StaffNotificationBatch>>>(`${BASE}/batches`, {
    // 空串 / undefined 由请求层丢弃，不会拼进查询串。
    params: { ...query },
    cache: 'no-store',
  })
  return unwrapPagedResult(response)
}

/** 记录页展开行、发送结果弹窗、轮询共用。 */
export async function getStaffNotificationBatch(id: string, signal?: AbortSignal): Promise<StaffNotificationBatchDetail> {
  const response = await request.get<ApiResponse<StaffNotificationBatchDetail>>(`${BASE}/batches/${encode(id)}`, {
    cache: 'no-store',
    signal,
  })
  return normalizeBatchDetail(unwrapApiData(response))
}

/** 只重发失败项；返回后批次变回 queued，调用方继续轮询。 */
export async function retryStaffNotificationBatch(id: string): Promise<StaffNotificationBatchDetail> {
  const response = await request.post<ApiResponse<StaffNotificationBatchDetail>>(`${BASE}/batches/${encode(id)}/retry`)
  return normalizeBatchDetail(unwrapApiData(response))
}

function normalizeBatchDetail(detail: StaffNotificationBatchDetail): StaffNotificationBatchDetail {
  return { ...detail, recipients: detail?.recipients ?? [] }
}
