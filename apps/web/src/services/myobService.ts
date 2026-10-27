import type { ApiResponse } from '../types/api'
import type {
  MyobBindingCandidates,
  MyobBindingCounts,
  MyobBindingRow,
  MyobBindingSnapshot,
  MyobBindingStatus,
  MyobConfirmBindingItem,
  MyobConfirmBindingsResult,
  MyobConfirmFailure,
  MyobConnection,
  MyobCompanyOption,
  MyobConnectionStatus,
  MyobEmployeeCard,
  MyobIgnoreEmployeeRequest,
  MyobLink,
  MyobMatchBasis,
  MyobMatchCandidate,
  MyobOnlyInMyob,
  MyobOnlyInOurs,
  MyobPayrollBindingState,
  MyobPayrollCompanyOption,
  MyobPayrollEmployerDetail,
  MyobPayrollEmployerHistory,
  MyobPayrollEmployerList,
  MyobPayrollEmployerQuery,
  MyobPayrollEmployerRow,
  MyobSavePayrollEmployersRequest,
  MyobSignalResult,
  MyobStoreMappingChange,
  MyobStoreMappingItem,
  MyobStoreMappings,
  MyobTypeIssue,
} from '../types/myob'
import request, { unwrapApiData } from '../utils/request'

// 路由与后端 MYOB 控制器一致；连接 / 映射接口需 Myob.Connection.Manage，员工绑定接口需 Myob.Employee.Bind，发薪公司接口需 Attendance.Payroll.Edit。
const MYOB_BASE = '/api/react/v1/myob'

function text(value: unknown) {
  return typeof value === 'string' ? value : ''
}

function nullableText(value: unknown) {
  return typeof value === 'string' && value.trim() ? value : null
}

function record(value: unknown): Record<string, unknown> {
  return value && typeof value === 'object' ? (value as Record<string, unknown>) : {}
}

/** 状态值只认三种；未知值按「需重新授权」处理（偏保守：不会把未知状态当成可用连接）。 */
export function normalizeMyobStatus(value: unknown): MyobConnectionStatus {
  if (value === 'Active' || value === 'NeedsReauth' || value === 'Disconnected') {
    return value
  }
  return 'NeedsReauth'
}

export function normalizeMyobConnection(raw: unknown): MyobConnection | null {
  const item = record(raw)
  const connectionGuid = text(item.connectionGuid)
  if (!connectionGuid) {
    return null
  }
  return {
    connectionGuid,
    businessId: text(item.businessId),
    businessName: text(item.businessName),
    abn: nullableText(item.abn),
    status: normalizeMyobStatus(item.status),
    authorizedByUserGuid: nullableText(item.authorizedByUserGuid),
    authorizedByName: nullableText(item.authorizedByName),
    authorizedAt: nullableText(item.authorizedAt),
    lastRefreshedAt: nullableText(item.lastRefreshedAt),
    lastError: nullableText(item.lastError),
    storeCodes: Array.isArray(item.storeCodes)
      ? item.storeCodes.filter((code): code is string => typeof code === 'string' && code !== '')
      : [],
  }
}

export function normalizeMyobConnections(payload: unknown): MyobConnection[] {
  if (!Array.isArray(payload)) {
    return []
  }
  return payload.map(normalizeMyobConnection).filter((item): item is MyobConnection => item !== null)
}

function normalizeStoreItem(raw: unknown): MyobStoreMappingItem | null {
  const item = record(raw)
  const storeCode = text(item.storeCode)
  if (!storeCode) {
    return null
  }
  return {
    storeCode,
    storeName: text(item.storeName),
    storeAbn: nullableText(item.storeAbn),
    // 后端没给 isActive 时按启用处理，避免整张表被默认隐藏
    isActive: item.isActive !== false,
    connectionGuid: nullableText(item.connectionGuid),
    suggestedConnectionGuid: nullableText(item.suggestedConnectionGuid),
    mappedAt: nullableText(item.mappedAt),
  }
}

export function normalizeMyobStoreMappings(payload: unknown): MyobStoreMappings {
  const data = record(payload)
  return {
    stores: Array.isArray(data.stores)
      ? data.stores.map(normalizeStoreItem).filter((item): item is MyobStoreMappingItem => item !== null)
      : [],
    connections: normalizeMyobConnections(data.connections),
  }
}

/** 单条连接写接口（改 ABN / 验证 / 断开）统一返回更新后的连接；返回空时抛错，避免页面拿到 null 渲染崩溃。 */
function requireConnection(payload: unknown): MyobConnection {
  const connection = normalizeMyobConnection(payload)
  if (!connection) {
    throw new Error('MYOB response missing connection')
  }
  return connection
}

export async function getMyobConnections(): Promise<MyobConnection[]> {
  const response = await request.get<ApiResponse<unknown>>(`${MYOB_BASE}/connections`)
  return normalizeMyobConnections(unwrapApiData(response))
}

/** 取 MYOB 授权页地址；前端拿到后整页跳转（window.location.href）。 */
export async function createMyobAuthorizeUrl(): Promise<string> {
  const response = await request.post<ApiResponse<unknown>>(`${MYOB_BASE}/connections/authorize-url`)
  const url = text(record(unwrapApiData(response)).url)
  if (!url) {
    throw new Error('MYOB authorize url is empty')
  }
  return url
}

/** 更新 ABN；空串清除。调用方应先用 normalizeAbnInput 去空格。 */
export async function updateMyobConnectionAbn(connectionGuid: string, abn: string): Promise<MyobConnection> {
  const response = await request.put<ApiResponse<unknown>>(
    `${MYOB_BASE}/connections/${encodeURIComponent(connectionGuid)}/abn`,
    { abn },
  )
  return requireConnection(unwrapApiData(response))
}

/** 验证 token，并从 MYOB 刷新公司名与 ABN。 */
export async function verifyMyobConnection(connectionGuid: string): Promise<MyobConnection> {
  const response = await request.post<ApiResponse<unknown>>(
    `${MYOB_BASE}/connections/${encodeURIComponent(connectionGuid)}/verify`,
  )
  return requireConnection(unwrapApiData(response))
}

/** 断开：清除 token，并移除该连接下所有分店映射。 */
export async function disconnectMyobConnection(connectionGuid: string): Promise<MyobConnection> {
  const response = await request.post<ApiResponse<unknown>>(
    `${MYOB_BASE}/connections/${encodeURIComponent(connectionGuid)}/disconnect`,
  )
  return requireConnection(unwrapApiData(response))
}

export async function getMyobStoreMappings(): Promise<MyobStoreMappings> {
  const response = await request.get<ApiResponse<unknown>>(`${MYOB_BASE}/store-mappings`)
  return normalizeMyobStoreMappings(unwrapApiData(response))
}

/** 只提交变化的行；返回保存后的完整映射表。 */
export async function updateMyobStoreMappings(items: MyobStoreMappingChange[]): Promise<MyobStoreMappings> {
  const response = await request.put<ApiResponse<unknown>>(`${MYOB_BASE}/store-mappings`, { items })
  return normalizeMyobStoreMappings(unwrapApiData(response))
}

// ───────────────────────── 阶段 1：员工绑定 ─────────────────────────
// normalize 的原则：后端缺字段 / null / 类型不对时给安全默认，页面不能因此崩溃。

function bool(value: unknown, fallback = false) {
  return typeof value === 'boolean' ? value : fallback
}

function int(value: unknown, fallback = 0) {
  return typeof value === 'number' && Number.isFinite(value) ? Math.trunc(value) : fallback
}

function nullableInt(value: unknown): number | null {
  return typeof value === 'number' && Number.isFinite(value) ? Math.trunc(value) : null
}

function textList(value: unknown): string[] {
  return Array.isArray(value) ? value.filter((item): item is string => typeof item === 'string' && item !== '') : []
}

function list<T>(value: unknown, mapper: (raw: unknown) => T | null): T[] {
  return Array.isArray(value) ? value.map(mapper).filter((item): item is T => item !== null) : []
}

const BINDING_STATUSES: readonly MyobBindingStatus[] = ['Bound', 'Suggested', 'NeedsReview', 'Conflict', 'Unmatched']

/** 状态值未知时按「未匹配」处理：没有绑定也没有候选，是最保守（不会误显示「确认绑定」）的呈现。 */
export function normalizeMyobBindingStatus(value: unknown): MyobBindingStatus {
  return BINDING_STATUSES.find((status) => status === value) ?? 'Unmatched'
}

export function normalizeMyobSignal(value: unknown): MyobSignalResult {
  return value === 'match' || value === 'mismatch' ? value : 'unknown'
}

function normalizeCard(raw: unknown): MyobEmployeeCard {
  const item = record(raw)
  return {
    uid: text(item.uid),
    displayId: nullableText(item.displayId),
    name: text(item.name),
    email: nullableText(item.email),
    phoneMasked: nullableText(item.phoneMasked),
    dateOfBirth: nullableText(item.dateOfBirth),
    isActive: bool(item.isActive, true),
    employmentStatus: nullableText(item.employmentStatus),
    hasAnnualEntitlement: bool(item.hasAnnualEntitlement),
    hasSickEntitlement: bool(item.hasSickEntitlement),
  }
}

function normalizeBasis(raw: unknown): MyobMatchBasis {
  const item = record(raw)
  return {
    email: normalizeMyobSignal(item.email),
    birthday: normalizeMyobSignal(item.birthday),
    name: normalizeMyobSignal(item.name),
    phone: normalizeMyobSignal(item.phone),
  }
}

export function normalizeMyobCandidate(raw: unknown): MyobMatchCandidate | null {
  if (!raw || typeof raw !== 'object') {
    return null
  }
  const item = record(raw)
  const card = normalizeCard(item.card)
  // 没有卡 uid 的候选无法绑定，丢弃
  if (!card.uid) {
    return null
  }
  return {
    card,
    score: int(item.score),
    basis: normalizeBasis(item.basis),
    notes: textList(item.notes),
    boundToUserGuid: nullableText(item.boundToUserGuid),
    boundToUserName: nullableText(item.boundToUserName),
  }
}

function normalizeLink(raw: unknown): MyobLink | null {
  if (!raw || typeof raw !== 'object') {
    return null
  }
  const item = record(raw)
  return {
    linkGuid: text(item.linkGuid),
    myobEmployeeUid: text(item.myobEmployeeUid),
    myobName: nullableText(item.myobName),
    matchMethod: text(item.matchMethod),
    matchScore: nullableInt(item.matchScore),
    boundByName: nullableText(item.boundByName),
    boundAt: nullableText(item.boundAt),
    cardMissing: bool(item.cardMissing),
  }
}

function normalizeBindingRow(raw: unknown): MyobBindingRow | null {
  const item = record(raw)
  const userGuid = text(item.userGuid)
  if (!userGuid) {
    return null
  }
  const link = normalizeLink(item.link)
  const candidate = normalizeMyobCandidate(item.candidate)
  let status = normalizeMyobBindingStatus(item.status)
  // 自洽兜底：声称已绑定却没有绑定记录、或声称有候选却没有候选，降级为「未匹配」，避免操作按钮拿到 null 崩溃
  if (status === 'Bound' && !link) {
    status = 'Unmatched'
  }
  if ((status === 'Suggested' || status === 'NeedsReview' || status === 'Conflict') && !candidate) {
    status = 'Unmatched'
  }
  return {
    userGuid,
    userName: text(item.userName),
    email: nullableText(item.email),
    employeeType: nullableText(item.employeeType),
    storeLabels: textList(item.storeLabels),
    isCrossCompany: bool(item.isCrossCompany),
    status,
    link,
    candidate,
  }
}

function normalizeOnlyInOurs(raw: unknown): MyobOnlyInOurs | null {
  const item = record(raw)
  const userGuid = text(item.userGuid)
  if (!userGuid) {
    return null
  }
  return {
    userGuid,
    userName: text(item.userName),
    email: nullableText(item.email),
    employeeType: nullableText(item.employeeType),
    storeLabels: textList(item.storeLabels),
    joinedAt: nullableText(item.joinedAt),
    copyText: text(item.copyText),
  }
}

function normalizeOnlyInMyob(raw: unknown): MyobOnlyInMyob | null {
  const item = record(raw)
  const card = normalizeCard(item.card)
  if (!card.uid) {
    return null
  }
  return { card, ignored: bool(item.ignored) }
}

function normalizeTypeIssue(raw: unknown): MyobTypeIssue | null {
  const item = record(raw)
  const userGuid = text(item.userGuid)
  if (!userGuid) {
    return null
  }
  return {
    userGuid,
    userName: text(item.userName),
    storeLabels: textList(item.storeLabels),
    ourType: text(item.ourType),
    myobStatus: nullableText(item.myobStatus),
    hasAnnualEntitlement: bool(item.hasAnnualEntitlement),
    hasSickEntitlement: bool(item.hasSickEntitlement),
    issueCode: text(item.issueCode),
  }
}

function normalizeCounts(raw: unknown): MyobBindingCounts {
  const item = record(raw)
  return {
    all: int(item.all),
    bound: int(item.bound),
    suggested: int(item.suggested),
    needsReview: int(item.needsReview),
    unmatched: int(item.unmatched),
    onlyInOurs: int(item.onlyInOurs),
    onlyInMyob: int(item.onlyInMyob),
    typeIssues: int(item.typeIssues),
    batchConfirmable: int(item.batchConfirmable),
  }
}

export function normalizeMyobBindingSnapshot(payload: unknown): MyobBindingSnapshot {
  const data = record(payload)
  return {
    connectionGuid: text(data.connectionGuid),
    connectionName: text(data.connectionName),
    fetchedAt: nullableText(data.fetchedAt),
    counts: normalizeCounts(data.counts),
    rows: list(data.rows, normalizeBindingRow),
    onlyInOurs: list(data.onlyInOurs, normalizeOnlyInOurs),
    onlyInMyob: list(data.onlyInMyob, normalizeOnlyInMyob),
    typeIssues: list(data.typeIssues, normalizeTypeIssue),
  }
}

export function normalizeMyobBindingCandidates(payload: unknown): MyobBindingCandidates {
  const data = record(payload)
  return {
    userGuid: text(data.userGuid),
    userName: text(data.userName),
    email: nullableText(data.email),
    phoneMasked: nullableText(data.phoneMasked),
    birthday: nullableText(data.birthday),
    employeeType: nullableText(data.employeeType),
    storeLabels: textList(data.storeLabels),
    candidates: list(data.candidates, normalizeMyobCandidate),
  }
}

function normalizeConfirmFailure(raw: unknown): MyobConfirmFailure | null {
  const item = record(raw)
  const failure = { userGuid: text(item.userGuid), code: text(item.code), message: text(item.message) }
  // 空对象 / null 不是有效失败项，丢弃（否则界面会出现一行空白的失败原因）
  return failure.userGuid || failure.code || failure.message ? failure : null
}

export function normalizeMyobConfirmResult(payload: unknown): MyobConfirmBindingsResult {
  const data = record(payload)
  return { confirmed: int(data.confirmed), failed: list(data.failed, normalizeConfirmFailure) }
}

/** 取某个公司文件的完整绑定视图；refresh=true 时后端重新拉取 MYOB 员工列表（否则用缓存）。 */
export async function getMyobBindingSnapshot(connectionGuid: string, refresh = false): Promise<MyobBindingSnapshot> {
  const response = await request.get<ApiResponse<unknown>>(`${MYOB_BASE}/employee-bindings`, {
    params: { connectionGuid, refresh: refresh ? true : undefined },
  })
  return normalizeMyobBindingSnapshot(unwrapApiData(response))
}

/** 为某员工手动选卡：按得分降序最多 30 条，keyword 命中姓名 / 邮箱 / 手机末 4 位。 */
export async function getMyobBindingCandidates(
  connectionGuid: string,
  userGuid: string,
  keyword?: string,
): Promise<MyobBindingCandidates> {
  const response = await request.get<ApiResponse<unknown>>(`${MYOB_BASE}/employee-bindings/candidates`, {
    params: { connectionGuid, userGuid, keyword: keyword?.trim() || undefined },
  })
  return normalizeMyobBindingCandidates(unwrapApiData(response))
}

/** 确认绑定（可批量）；部分失败时 failed 非空，调用方需逐条展示失败原因。 */
export async function confirmMyobBindings(
  connectionGuid: string,
  items: MyobConfirmBindingItem[],
): Promise<MyobConfirmBindingsResult> {
  const response = await request.post<ApiResponse<unknown>>(`${MYOB_BASE}/employee-bindings/confirm`, {
    connectionGuid,
    items,
  })
  return normalizeMyobConfirmResult(unwrapApiData(response))
}

export async function unbindMyobEmployee(linkGuid: string): Promise<void> {
  const response = await request.post<ApiResponse<unknown>>(
    `${MYOB_BASE}/employee-bindings/${encodeURIComponent(linkGuid)}/unbind`,
  )
  unwrapApiData(response)
}

/** 忽略 / 取消忽略「MYOB 有、我方没有」的人。 */
export async function ignoreMyobEmployee(payload: MyobIgnoreEmployeeRequest): Promise<void> {
  const response = await request.post<ApiResponse<unknown>>(`${MYOB_BASE}/employee-bindings/ignore`, payload)
  unwrapApiData(response)
}

// ───────────────────────── 阶段 1：发薪公司 ─────────────────────────

function normalizeCompanyOption(raw: unknown): MyobPayrollCompanyOption | null {
  const item = record(raw)
  const connectionGuid = text(item.connectionGuid)
  return connectionGuid ? { connectionGuid, name: text(item.name) } : null
}

function normalizePayrollRow(raw: unknown): MyobPayrollEmployerRow | null {
  const item = record(raw)
  const userGuid = text(item.userGuid)
  if (!userGuid) {
    return null
  }
  return {
    userGuid,
    userName: text(item.userName),
    storeCode: text(item.storeCode),
    storeName: text(item.storeName),
    defaultConnectionGuid: nullableText(item.defaultConnectionGuid),
    defaultConnectionName: nullableText(item.defaultConnectionName),
    effectiveConnectionGuid: nullableText(item.effectiveConnectionGuid),
    effectiveConnectionName: nullableText(item.effectiveConnectionName),
    isOverride: bool(item.isOverride),
    effectiveFrom: nullableText(item.effectiveFrom),
    isCrossCompany: bool(item.isCrossCompany),
  }
}

function normalizeBindingState(raw: unknown): MyobPayrollBindingState | null {
  const item = record(raw)
  const connectionGuid = text(item.connectionGuid)
  return connectionGuid ? { connectionGuid, bound: bool(item.bound) } : null
}

function normalizeHistory(raw: unknown): MyobPayrollEmployerHistory | null {
  const item = record(raw)
  if (!text(item.effectiveFrom)) {
    return null
  }
  return {
    storeCode: text(item.storeCode),
    storeName: text(item.storeName),
    connectionGuid: nullableText(item.connectionGuid),
    connectionName: nullableText(item.connectionName),
    effectiveFrom: text(item.effectiveFrom),
    createdByName: nullableText(item.createdByName),
    createdAt: nullableText(item.createdAt),
  }
}

export function normalizeMyobPayrollEmployerList(payload: unknown): MyobPayrollEmployerList {
  const data = record(payload)
  return {
    rows: list(data.rows, normalizePayrollRow),
    companies: list(data.companies, normalizeCompanyOption),
  }
}

export function normalizeMyobPayrollEmployerDetail(payload: unknown): MyobPayrollEmployerDetail {
  const data = record(payload)
  return {
    userGuid: text(data.userGuid),
    userName: text(data.userName),
    rows: list(data.rows, normalizePayrollRow),
    companies: list(data.companies, normalizeCompanyOption),
    bindingStates: list(data.bindingStates, normalizeBindingState),
    allowedEffectiveFrom: textList(data.allowedEffectiveFrom),
    history: list(data.history, normalizeHistory),
  }
}

export async function getMyobPayrollEmployers(query: MyobPayrollEmployerQuery = {}): Promise<MyobPayrollEmployerList> {
  const response = await request.get<ApiResponse<unknown>>(`${MYOB_BASE}/payroll-employers`, {
    params: {
      keyword: query.keyword?.trim() || undefined,
      storeCode: query.storeCode || undefined,
      crossCompanyOnly: query.crossCompanyOnly ? true : undefined,
    },
  })
  return normalizeMyobPayrollEmployerList(unwrapApiData(response))
}

export async function getMyobPayrollEmployerDetail(userGuid: string): Promise<MyobPayrollEmployerDetail> {
  const response = await request.get<ApiResponse<unknown>>(
    `${MYOB_BASE}/payroll-employers/${encodeURIComponent(userGuid)}`,
  )
  return normalizeMyobPayrollEmployerDetail(unwrapApiData(response))
}

/** 保存员工的发薪公司覆盖；返回保存后的详情。 */
export async function saveMyobPayrollEmployers(
  userGuid: string,
  payload: MyobSavePayrollEmployersRequest,
): Promise<MyobPayrollEmployerDetail> {
  const response = await request.put<ApiResponse<unknown>>(
    `${MYOB_BASE}/payroll-employers/${encodeURIComponent(userGuid)}`,
    payload,
  )
  return normalizeMyobPayrollEmployerDetail(unwrapApiData(response))
}

// ───────────────────────── 公司选项（只读） ─────────────────────────

export function normalizeMyobCompanies(payload: unknown): MyobCompanyOption[] {
  return list(payload, (raw) => {
    const item = record(raw)
    const connectionGuid = text(item.connectionGuid)
    // 没有 connectionGuid 的行无法选中，丢弃；ABN 只保留数字，空串 / 非字符串按未填写
    return connectionGuid
      ? { connectionGuid, name: text(item.name), abn: nullableText(item.abn)?.replace(/\s+/g, '') ?? null }
      : null
  })
}

/**
 * 可选的公司文件（只含已连接 Active 的）。权限：Myob.Connection.Manage / Myob.Employee.Bind / Attendance.Payroll.Edit 任一。
 * 员工绑定页签的公司选择条用它，不再调需要连接管理权限的 /connections。
 */
export async function getMyobCompanies(): Promise<MyobCompanyOption[]> {
  const response = await request.get<ApiResponse<unknown>>(`${MYOB_BASE}/companies`)
  return normalizeMyobCompanies(unwrapApiData(response))
}
