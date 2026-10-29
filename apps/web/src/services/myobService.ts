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
  MyobCompanyShare,
  MyobDayGridRow,
  MyobPayCategories,
  MyobPayCategoryKey,
  MyobPayCategoryMapping,
  MyobPayMode,
  MyobPreviewBlocker,
  MyobPreviewDetail,
  MyobPreviewHint,
  MyobPreviewPeriodOption,
  MyobPreviewReadiness,
  MyobPreviewPushState,
  MyobPreviewRow,
  MyobPushBatchDetail,
  MyobPushBatchItem,
  MyobPushBatchSummary,
  MyobPushRequest,
  MyobPushResult,
  MyobPushState,
  MyobPushStatus,
  MyobPushUserResult,
  MyobUndoResult,
  MyobPreviewStatus,
  MyobPreviewTotals,
  MyobPushHours,
  MyobTimesheetPreview,
  MyobTimesheetState,
  MyobUnlockedStore,
  MyobWageCategory,
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
  MyobSyncApplyRequest,
  MyobSyncApplyResult,
  MyobSyncBatch,
  MyobSyncBatchStatus,
  MyobSyncChange,
  MyobSyncEmployee,
  MyobSyncFailure,
  MyobSyncField,
  MyobSyncFieldStat,
  MyobSyncFieldStats,
  MyobSyncMode,
  MyobSyncPreview,
  MyobSyncPreviewRequest,
  MyobSyncScopeRequest,
  MyobSyncUndoResult,
  MyobSyncWritten,
  MyobTypeIssue,
} from '../types/myob'
import request, { unwrapApiData } from '../utils/request'

// 路由与后端 MYOB 控制器一致；连接 / 映射 / 工资类别接口需 Myob.Connection.Manage，员工绑定接口需 Myob.Employee.Bind，发薪公司与推送预览接口需 Attendance.Payroll.Edit。
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

// ───────────────────────── 阶段 2：工资类别映射 ─────────────────────────

/** 十进制小时：非有限数（缺字段 / 字符串 / NaN）一律按 0，页面不会因此出现 NaN。 */
function num(value: unknown, fallback = 0) {
  return typeof value === 'number' && Number.isFinite(value) ? value : fallback
}

/** 类别键与映射字段的对应；顺序即页面行的顺序（临时工周末与公共假期四项可选，不计入「已映射」必填项数）。 */
const PAY_CATEGORY_FIELDS: ReadonlyArray<readonly [MyobPayCategoryKey, keyof MyobPayCategoryMapping]> = [
  ['weekday', 'weekdayUid'],
  ['saturday', 'saturdayUid'],
  ['sunday', 'sundayUid'],
  ['saturdayCasual', 'saturdayCasualUid'],
  ['sundayCasual', 'sundayCasualUid'],
  ['publicHoliday', 'publicHolidayUid'],
  ['publicHolidayCasual', 'publicHolidayCasualUid'],
  ['personalLeave', 'personalLeaveUid'],
  ['holidayLeave', 'holidayLeaveUid'],
]

/** 可选类别键（不计入「已映射」必填项数），与后端 MyobPayCategoryKeys.Required 之外的键一致。 */
const OPTIONAL_PAY_CATEGORY_KEYS: ReadonlySet<MyobPayCategoryKey> = new Set([
  'saturdayCasual',
  'sundayCasual',
  'publicHoliday',
  'publicHolidayCasual',
])

export function normalizeMyobPayCategoryMapping(raw: unknown): MyobPayCategoryMapping {
  const item = record(raw)
  return {
    weekdayUid: nullableText(item.weekdayUid),
    saturdayUid: nullableText(item.saturdayUid),
    sundayUid: nullableText(item.sundayUid),
    saturdayCasualUid: nullableText(item.saturdayCasualUid),
    sundayCasualUid: nullableText(item.sundayCasualUid),
    publicHolidayUid: nullableText(item.publicHolidayUid),
    publicHolidayCasualUid: nullableText(item.publicHolidayCasualUid),
    personalLeaveUid: nullableText(item.personalLeaveUid),
    holidayLeaveUid: nullableText(item.holidayLeaveUid),
  }
}

function normalizeWageCategory(raw: unknown): MyobWageCategory | null {
  const item = record(raw)
  const uid = text(item.uid)
  // 没有 uid 的类别无法被选中保存，丢弃；名称为空时退回 uid，避免下拉里出现空白项
  return uid ? { uid, name: text(item.name).trim() || uid, wageType: nullableText(item.wageType) } : null
}

export function normalizeMyobPayCategories(payload: unknown): MyobPayCategories {
  const data = record(payload)
  const mapping = normalizeMyobPayCategoryMapping(data.mapping)
  const categories = list(data.categories, normalizeWageCategory)
  const knownKeys = new Set<string>(PAY_CATEGORY_FIELDS.map(([key]) => key))
  const mappedCount =
    typeof data.mappedCount === 'number'
      ? Math.min(5, Math.max(0, int(data.mappedCount)))
      : PAY_CATEGORY_FIELDS.filter(([key, field]) => !OPTIONAL_PAY_CATEGORY_KEYS.has(key) && mapping[field] !== null).length
  return {
    connectionGuid: text(data.connectionGuid),
    connectionName: text(data.connectionName),
    fetchedAt: nullableText(data.fetchedAt),
    categories,
    mapping,
    suggestions: normalizeMyobPayCategoryMapping(data.suggestions),
    mappedCount,
    complete: bool(data.complete),
    missingKeys: textList(data.missingKeys).filter((key): key is MyobPayCategoryKey => knownKeys.has(key)),
    updatedAt: nullableText(data.updatedAt),
  }
}

/** 读取某公司文件的 MYOB 工资类别 + 已保存映射；refresh=true 时后端重新从 MYOB 拉取（否则用缓存）。权限 Myob.Connection.Manage。 */
export async function getMyobPayCategories(connectionGuid: string, refresh = false): Promise<MyobPayCategories> {
  const response = await request.get<ApiResponse<unknown>>(`${MYOB_BASE}/pay-categories`, {
    params: { connectionGuid, refresh: refresh ? true : undefined },
  })
  return normalizeMyobPayCategories(unwrapApiData(response))
}

/** 保存 5 项映射（调用方须保证 5 项都已选）；返回保存后的完整视图。 */
export async function saveMyobPayCategoryMapping(
  connectionGuid: string,
  mapping: MyobPayCategoryMapping,
): Promise<MyobPayCategories> {
  const response = await request.put<ApiResponse<unknown>>(
    `${MYOB_BASE}/pay-categories/${encodeURIComponent(connectionGuid)}`,
    mapping,
  )
  return normalizeMyobPayCategories(unwrapApiData(response))
}

// ───────────────────────── 阶段 2：推送预览（只读） ─────────────────────────

const PREVIEW_STATUSES: readonly MyobPreviewStatus[] = ['Ready', 'NoTransfer', 'Blocked']
const PAY_MODES: readonly MyobPayMode[] = ['Transfer', 'Cash', 'Mixed']
const TIMESHEET_STATES: readonly MyobTimesheetState[] = ['Unknown', 'Clear', 'HasEntries', 'Processed', 'CheckFailed']

/** 未知的行状态按「被拦截」处理：宁可多拦一个，也不会把不认识的状态显示成「可推送」。 */
export function normalizeMyobPreviewStatus(value: unknown): MyobPreviewStatus {
  return PREVIEW_STATUSES.find((status) => status === value) ?? 'Blocked'
}

function normalizeMyobTimesheetState(value: unknown): MyobTimesheetState {
  return TIMESHEET_STATES.find((state) => state === value) ?? 'Unknown'
}

function normalizePayMode(value: unknown): MyobPayMode | null {
  return PAY_MODES.find((mode) => mode === value) ?? null
}

function normalizePeriod(raw: unknown): MyobPreviewPeriodOption | null {
  const item = record(raw)
  const start = text(item.start)
  return start ? { start, end: text(item.end) } : null
}

function normalizeUnlockedStore(raw: unknown): MyobUnlockedStore | null {
  const item = record(raw)
  const storeCode = text(item.storeCode)
  return storeCode ? { storeCode, storeName: text(item.storeName), employeeCount: int(item.employeeCount) } : null
}

function normalizeReadiness(raw: unknown): MyobPreviewReadiness {
  const item = record(raw)
  return {
    lockedStores: textList(item.lockedStores),
    unlockedStores: list(item.unlockedStores, normalizeUnlockedStore),
    categoryMappedCount: Math.min(5, Math.max(0, int(item.categoryMappedCount))),
    categoryComplete: bool(item.categoryComplete),
    myobChecked: bool(item.myobChecked),
    myobProcessedCount: int(item.myobProcessedCount),
    myobCheckFailedCount: int(item.myobCheckFailedCount),
  }
}

function normalizeHint(raw: unknown): MyobPreviewHint | null {
  const item = record(raw)
  const message = text(item.message).trim()
  // 没有说明文字的提示没有信息量，丢弃
  return message ? { code: text(item.code), userGuid: text(item.userGuid), userName: text(item.userName), message } : null
}

function normalizeBlocker(raw: unknown): MyobPreviewBlocker | null {
  const item = record(raw)
  const code = text(item.code)
  const message = text(item.message)
  return code || message ? { code, message, ref: nullableText(item.ref) } : null
}

function normalizePushHours(raw: unknown): MyobPushHours {
  const item = record(raw)
  return {
    weekday: num(item.weekday),
    saturday: num(item.saturday),
    sunday: num(item.sunday),
    publicHoliday: num(item.publicHoliday),
    personalLeave: num(item.personalLeave),
    holidayLeave: num(item.holidayLeave),
  }
}

/** 日网格行：hours 与 days 对齐（缺的补 0、多的截断），保证渲染时每个单元格都有数。 */
function normalizeGridRow(raw: unknown, dayCount: number): MyobDayGridRow | null {
  const item = record(raw)
  const key = text(item.key)
  if (!key) {
    return null
  }
  const source = Array.isArray(item.hours) ? item.hours : []
  const hours = Array.from({ length: dayCount }, (_, index) => num(source[index]))
  return {
    key,
    label: nullableText(item.label),
    hours,
    total: typeof item.total === 'number' && Number.isFinite(item.total) ? item.total : hours.reduce((sum, value) => sum + value, 0),
  }
}

function normalizeShare(raw: unknown): MyobCompanyShare | null {
  const item = record(raw)
  const connectionGuid = text(item.connectionGuid)
  return connectionGuid
    ? {
        connectionGuid,
        connectionName: text(item.connectionName),
        actualHours: num(item.actualHours),
        allocatedTransferHours: num(item.allocatedTransferHours),
      }
    : null
}

function normalizeDetail(raw: unknown): MyobPreviewDetail {
  const item = record(raw)
  const days = textList(item.days)
  return {
    days,
    actual: list(item.actual, (row) => normalizeGridRow(row, days.length)),
    pushed: list(item.pushed, (row) => normalizeGridRow(row, days.length)),
    cash: list(item.cash, (row) => normalizeGridRow(row, days.length)),
    shares: list(item.shares, normalizeShare),
    annualCashOutTransferHours: num(item.annualCashOutTransferHours),
    // 后端没给 checkOk 时按「未通过」处理：校验条不会在缺数据时显示成绿色通过
    checkOk: bool(item.checkOk),
    checkMessage: text(item.checkMessage),
  }
}

const PUSH_STATES: readonly MyobPushState[] = ['NotPushed', 'Pushed', 'NeedsRepush', 'ProcessedSkipped', 'Failed', 'Mismatch']

const PUSH_STATUSES: readonly MyobPushStatus[] = [
  'Pushed',
  'UpToDate',
  'SkippedProcessed',
  'SkippedHasEntries',
  'Blocked',
  'Failed',
  'Mismatch',
  'Undone',
  'UndoConflict',
  'UndoBlockedProcessed',
  'UndoBlockedNewer',
  'Running',
]

/** 不认识的推送状态按失败处理：宁可让人多看一眼，也不把未知值显示成「已推送」。 */
export function normalizeMyobPushStatus(value: unknown): MyobPushStatus {
  return PUSH_STATUSES.find((status) => status === value) ?? 'Failed'
}

function normalizePreviewPushState(raw: unknown): MyobPreviewPushState {
  const item = record(raw)
  const needsRepush = bool(item.needsRepush)
  const state = PUSH_STATES.find((value) => value === item.state) ?? 'NotPushed'
  return {
    // needsRepush 为真时一定显示「需重新推送」，不管 state 写了什么（避免旧状态让人以为 MYOB 是最新的）
    state: needsRepush ? 'NeedsRepush' : state,
    needsRepush,
    lastStatus: nullableText(item.lastStatus),
    lastBatchGuid: nullableText(item.lastBatchGuid),
    lastPushedAt: nullableText(item.lastPushedAt),
  }
}

export function normalizeMyobPreviewRow(raw: unknown): MyobPreviewRow | null {
  const item = record(raw)
  const userGuid = text(item.userGuid)
  if (!userGuid) {
    return null
  }
  const blockers = list(item.blockers, normalizeBlocker)
  // 自洽兜底：有拦截项就一定是「被拦截」，不管 status 写了什么（推送时服务端也会拒绝）
  const status = blockers.length > 0 ? 'Blocked' : normalizeMyobPreviewStatus(item.status)
  return {
    userGuid,
    userName: text(item.userName),
    payMode: normalizePayMode(item.payMode),
    fixedTransferHoursPerWeek: num(item.fixedTransferHoursPerWeek),
    isCrossCompany: bool(item.isCrossCompany),
    allocatedTransferHours: num(item.allocatedTransferHours),
    actualHours: num(item.actualHours),
    storeLabels: textList(item.storeLabels),
    status,
    pushed: normalizePushHours(item.pushed),
    pushedTotal: num(item.pushedTotal),
    cashOutHours: num(item.cashOutHours),
    blockers,
    hintCodes: textList(item.hintCodes),
    myobName: nullableText(item.myobName),
    myobState: normalizeMyobTimesheetState(item.myobState),
    push: normalizePreviewPushState(item.push),
    detail: normalizeDetail(item.detail),
  }
}

/** 后端没给 totals 时，由行数据推出（页面底部汇总不会空白）。 */
function summarizePreviewRows(rows: MyobPreviewRow[]): MyobPreviewTotals {
  const ready = rows.filter((row) => row.status === 'Ready')
  return {
    readyCount: ready.length,
    readyHours: ready.reduce((sum, row) => sum + row.pushedTotal, 0),
    blockedCount: rows.filter((row) => row.status === 'Blocked').length,
    noTransferCount: rows.filter((row) => row.status === 'NoTransfer').length,
  }
}

export function normalizeMyobTimesheetPreview(payload: unknown): MyobTimesheetPreview {
  const data = record(payload)
  const rows = list(data.rows, normalizeMyobPreviewRow)
  const rawTotals = record(data.totals)
  const totals: MyobPreviewTotals =
    Object.keys(rawTotals).length > 0
      ? {
          readyCount: int(rawTotals.readyCount),
          readyHours: num(rawTotals.readyHours),
          blockedCount: int(rawTotals.blockedCount),
          noTransferCount: int(rawTotals.noTransferCount),
        }
      : summarizePreviewRows(rows)
  return {
    connectionGuid: text(data.connectionGuid),
    connectionName: text(data.connectionName),
    periodStart: text(data.periodStart),
    periodEnd: text(data.periodEnd),
    weeks: Math.max(1, int(data.weeks, 1)),
    periods: list(data.periods, normalizePeriod),
    readiness: normalizeReadiness(data.readiness),
    hints: list(data.hints, normalizeHint),
    rows,
    totals,
    pushEnabled: bool(data.pushEnabled),
  }
}

export interface MyobTimesheetPreviewQuery {
  /** 周期起始日 yyyy-MM-dd；省略 = 后端取「上一个已结束的工资周期」。 */
  periodStart?: string | null
  /** true 才会逐个员工去 MYOB 读 Timesheet 状态（慢，会产生 MYOB 读请求）。 */
  checkMyob?: boolean
}

/** 只读推送预览：不会往 MYOB 写任何东西。权限 Attendance.Payroll.Edit。 */
export async function getMyobTimesheetPreview(
  connectionGuid: string,
  query: MyobTimesheetPreviewQuery = {},
): Promise<MyobTimesheetPreview> {
  const response = await request.get<ApiResponse<unknown>>(`${MYOB_BASE}/timesheet/preview`, {
    params: {
      connectionGuid,
      periodStart: query.periodStart || undefined,
      checkMyob: query.checkMyob ? true : undefined,
    },
  })
  return normalizeMyobTimesheetPreview(unwrapApiData(response))
}

// ───────────────────────── 从 MYOB 同步资料到我方 ─────────────────────────
// 契约：MyobEmployeeSyncDtos.cs。权限：Myob.Employee.Bind，服务端另要求 EmployeeProfiles.Edit（field-stats.canWrite 反映）。

function syncField(value: unknown): MyobSyncField | null {
  // 字段键原样保留（未知键界面按原文显示），只丢弃空值
  return typeof value === 'string' && value !== '' ? (value as MyobSyncField) : null
}

function syncFieldList(value: unknown): MyobSyncField[] {
  return list(value, syncField)
}

function normalizeSyncMode(value: unknown): MyobSyncMode {
  return value === 'Overwrite' ? 'Overwrite' : 'FillEmpty'
}

function normalizeSyncFieldStat(raw: unknown): MyobSyncFieldStat | null {
  const item = record(raw)
  const field = syncField(item.field)
  if (!field) {
    return null
  }
  return {
    field,
    sensitive: bool(item.sensitive),
    defaultSelected: bool(item.defaultSelected),
    myobHasValue: int(item.myobHasValue),
    fillable: int(item.fillable),
    differs: int(item.differs),
  }
}

export function normalizeMyobSyncFieldStats(payload: unknown): MyobSyncFieldStats {
  const data = record(payload)
  return {
    connectionGuid: text(data.connectionGuid),
    fetchedAt: text(data.fetchedAt),
    boundCount: int(data.boundCount),
    missingCardCount: int(data.missingCardCount),
    fields: list(data.fields, normalizeSyncFieldStat),
    // 缺字段按没权限处理：宁可隐藏入口，也不让没有权限的人走到写入那一步
    canWrite: bool(data.canWrite, false),
    unavailableFields: syncFieldList(data.unavailableFields),
  }
}

function normalizeSyncChange(raw: unknown): MyobSyncChange | null {
  const item = record(raw)
  const field = syncField(item.field)
  if (!field) {
    return null
  }
  return {
    field,
    sensitive: bool(item.sensitive),
    ourDisplay: nullableText(item.ourDisplay),
    myobDisplay: nullableText(item.myobDisplay),
    kind: item.kind === 'Overwrite' ? 'Overwrite' : 'Fill',
    defaultChecked: bool(item.defaultChecked),
    note: nullableText(item.note),
    ourHash: text(item.ourHash),
    myobHash: text(item.myobHash),
  }
}

function normalizeSyncEmployee(raw: unknown): MyobSyncEmployee | null {
  const item = record(raw)
  const userGuid = text(item.userGuid)
  if (!userGuid) {
    return null
  }
  return {
    userGuid,
    userName: text(item.userName),
    storeLabels: textList(item.storeLabels),
    employeeType: nullableText(item.employeeType),
    hasPendingSensitiveRequest: bool(item.hasPendingSensitiveRequest),
    sensitiveRevision: int(item.sensitiveRevision),
    changes: list(item.changes, normalizeSyncChange),
  }
}

export function normalizeMyobSyncPreview(payload: unknown): MyobSyncPreview {
  const data = record(payload)
  const summary = record(data.summary)
  return {
    connectionGuid: text(data.connectionGuid),
    fetchedAt: text(data.fetchedAt),
    mode: normalizeSyncMode(data.mode),
    fields: syncFieldList(data.fields),
    summary: {
      employeeCount: int(summary.employeeCount),
      changeCount: int(summary.changeCount),
      defaultCheckedCount: int(summary.defaultCheckedCount),
      overwriteCount: int(summary.overwriteCount),
      sensitiveCount: int(summary.sensitiveCount),
      pendingSensitiveRequestCount: int(summary.pendingSensitiveRequestCount),
      skippedNoDiff: int(summary.skippedNoDiff),
      skippedMissingCard: int(summary.skippedMissingCard),
    },
    unavailableFields: syncFieldList(data.unavailableFields),
    employees: list(data.employees, normalizeSyncEmployee),
  }
}

function normalizeSyncWritten(raw: unknown): MyobSyncWritten | null {
  const item = record(raw)
  const userGuid = text(item.userGuid)
  return userGuid ? { userGuid, userName: text(item.userName), fields: syncFieldList(item.fields) } : null
}

function normalizeSyncFailure(raw: unknown): MyobSyncFailure | null {
  const item = record(raw)
  const userGuid = text(item.userGuid)
  return userGuid
    ? { userGuid, userName: text(item.userName), fields: syncFieldList(item.fields), code: text(item.code), message: text(item.message) }
    : null
}

export function normalizeMyobSyncApplyResult(payload: unknown): MyobSyncApplyResult {
  const data = record(payload)
  return {
    batchGuid: nullableText(data.batchGuid),
    writtenEmployeeCount: int(data.writtenEmployeeCount),
    writtenChangeCount: int(data.writtenChangeCount),
    writtenSensitiveCount: int(data.writtenSensitiveCount),
    written: list(data.written, normalizeSyncWritten),
    failed: list(data.failed, normalizeSyncFailure),
    undoDeadline: nullableText(data.undoDeadline),
  }
}

const SYNC_BATCH_STATUSES: readonly MyobSyncBatchStatus[] = ['Active', 'Undone', 'Expired']

function normalizeSyncBatch(raw: unknown): MyobSyncBatch | null {
  const item = record(raw)
  const batchGuid = text(item.batchGuid)
  if (!batchGuid) {
    return null
  }
  const status = SYNC_BATCH_STATUSES.find((value) => value === item.status) ?? 'Expired'
  return {
    batchGuid,
    createdAt: text(item.createdAt),
    actorName: nullableText(item.actorName),
    employeeCount: int(item.employeeCount),
    changeCount: int(item.changeCount),
    sensitiveCount: int(item.sensitiveCount),
    status,
    // 状态不是可撤销（含未知状态）时，即使后端 canUndo 误为 true 也不显示撤销入口
    canUndo: bool(item.canUndo) && status === 'Active',
    undoDeadline: text(item.undoDeadline),
    undoneAt: nullableText(item.undoneAt),
    undoneByName: nullableText(item.undoneByName),
  }
}

export function normalizeMyobSyncBatches(payload: unknown): MyobSyncBatch[] {
  return list(payload, normalizeSyncBatch)
}

export function normalizeMyobSyncUndoResult(payload: unknown): MyobSyncUndoResult {
  const data = record(payload)
  return {
    restoredEmployeeCount: int(data.restoredEmployeeCount),
    restoredChangeCount: int(data.restoredChangeCount),
    skippedChanged: list(data.skippedChanged, normalizeSyncWritten),
  }
}

/** 第 1 步：范围内每个字段「补空 N / 不同 M」；userGuids 为空 = 全部已绑定员工。 */
export async function getMyobSyncFieldStats(payload: MyobSyncScopeRequest): Promise<MyobSyncFieldStats> {
  const response = await request.post<ApiResponse<unknown>>(`${MYOB_BASE}/employee-sync/field-stats`, payload)
  return normalizeMyobSyncFieldStats(unwrapApiData(response))
}

/** 第 2 步：逐员工逐字段的差异预览，不写入。 */
export async function previewMyobEmployeeSync(payload: MyobSyncPreviewRequest): Promise<MyobSyncPreview> {
  const response = await request.post<ApiResponse<unknown>>(`${MYOB_BASE}/employee-sync/preview`, payload)
  return normalizeMyobSyncPreview(unwrapApiData(response))
}

/** 确认写入（留快照）；部分员工失败时 failed 非空，调用方逐条展示。 */
export async function applyMyobEmployeeSync(payload: MyobSyncApplyRequest): Promise<MyobSyncApplyResult> {
  const response = await request.post<ApiResponse<unknown>>(`${MYOB_BASE}/employee-sync/apply`, payload)
  return normalizeMyobSyncApplyResult(unwrapApiData(response))
}

/** 同步记录（最近 30 条）。 */
export async function getMyobSyncBatches(connectionGuid: string): Promise<MyobSyncBatch[]> {
  const response = await request.get<ApiResponse<unknown>>(`${MYOB_BASE}/employee-sync/batches`, {
    params: { connectionGuid },
  })
  return normalizeMyobSyncBatches(unwrapApiData(response))
}

/** 7 天内整批撤销；只还原本次写入且之后没被改过的字段。 */
export async function undoMyobSyncBatch(batchGuid: string): Promise<MyobSyncUndoResult> {
  const response = await request.post<ApiResponse<unknown>>(
    `${MYOB_BASE}/employee-sync/batches/${encodeURIComponent(batchGuid)}/undo`,
  )
  return normalizeMyobSyncUndoResult(unwrapApiData(response))
}

// ───────────────────────── Timesheet 推送与撤销 ─────────────────────────
// 契约：MyobTimesheetPushDtos.cs。权限 Myob.Timesheet.Push；会写 MYOB 生产数据，总闸关闭时后端回 MYOB_PUSH_DISABLED。

function normalizePushUserResult(raw: unknown): MyobPushUserResult | null {
  const item = record(raw)
  const userGuid = text(item.userGuid)
  if (!userGuid) {
    return null
  }
  return {
    userGuid,
    userName: text(item.userName) || userGuid,
    status: normalizeMyobPushStatus(item.status),
    code: nullableText(item.code),
    message: nullableText(item.message),
    pushedHours: num(item.pushedHours),
  }
}

export function normalizeMyobPushResult(payload: unknown): MyobPushResult {
  const data = record(payload)
  return {
    batchGuid: text(data.batchGuid),
    connectionGuid: text(data.connectionGuid),
    periodStart: text(data.periodStart),
    results: list(data.results, normalizePushUserResult),
  }
}

export function normalizeMyobUndoResult(payload: unknown): MyobUndoResult {
  const data = record(payload)
  return { batchGuid: text(data.batchGuid), results: list(data.results, normalizePushUserResult) }
}

function normalizePushBatchSummary(raw: unknown): MyobPushBatchSummary | null {
  const item = record(raw)
  const batchGuid = text(item.batchGuid)
  if (!batchGuid) {
    return null
  }
  return {
    batchGuid,
    startedAt: text(item.startedAt),
    actorUserGuid: nullableText(item.actorUserGuid),
    actorName: nullableText(item.actorName),
    total: int(item.total),
    pushed: int(item.pushed),
    upToDate: int(item.upToDate),
    skipped: int(item.skipped),
    failed: int(item.failed),
    mismatch: int(item.mismatch),
    undone: int(item.undone),
    undoable: int(item.undoable),
  }
}

export function normalizeMyobPushBatches(payload: unknown): MyobPushBatchSummary[] {
  return list(payload, normalizePushBatchSummary)
}

function normalizePushBatchItem(raw: unknown): MyobPushBatchItem | null {
  const item = record(raw)
  const userGuid = text(item.userGuid)
  if (!userGuid) {
    return null
  }
  return {
    userGuid,
    userName: text(item.userName) || userGuid,
    status: normalizeMyobPushStatus(item.status),
    errorCode: nullableText(item.errorCode),
    errorStatus: typeof item.errorStatus === 'number' ? item.errorStatus : null,
    pushedHours: num(item.pushedHours),
    startedAt: text(item.startedAt),
    finishedAt: nullableText(item.finishedAt),
    undoneAt: nullableText(item.undoneAt),
  }
}

export function normalizeMyobPushBatchDetail(payload: unknown): MyobPushBatchDetail {
  const data = record(payload)
  return {
    summary: normalizePushBatchSummary(data.summary) ?? normalizePushBatchSummary({ batchGuid: 'unknown' })!,
    connectionGuid: text(data.connectionGuid),
    periodStart: text(data.periodStart),
    items: list(data.items, normalizePushBatchItem),
  }
}

/** 推送一块（≤10 人）。同一次「点推送」的多块用同一个 batchGuid。 */
export async function pushMyobTimesheet(body: MyobPushRequest): Promise<MyobPushResult> {
  const response = await request.post<ApiResponse<unknown>>(`${MYOB_BASE}/timesheet/push`, body)
  return normalizeMyobPushResult(unwrapApiData(response))
}

export async function getMyobPushBatches(connectionGuid: string, periodStart?: string | null): Promise<MyobPushBatchSummary[]> {
  const response = await request.get<ApiResponse<unknown>>(`${MYOB_BASE}/timesheet/batches`, {
    params: { connectionGuid, periodStart: periodStart || undefined },
  })
  return normalizeMyobPushBatches(unwrapApiData(response))
}

export async function getMyobPushBatch(batchGuid: string): Promise<MyobPushBatchDetail> {
  const response = await request.get<ApiResponse<unknown>>(`${MYOB_BASE}/timesheet/batches/${encodeURIComponent(batchGuid)}`)
  return normalizeMyobPushBatchDetail(unwrapApiData(response))
}

/** 撤销整个批次：把推送前的快照写回 MYOB；已发薪 / 被人改过 / 有更新批次的员工不动，逐个返回原因。 */
export async function undoMyobPushBatch(batchGuid: string): Promise<MyobUndoResult> {
  const response = await request.post<ApiResponse<unknown>>(`${MYOB_BASE}/timesheet/batches/${encodeURIComponent(batchGuid)}/undo`)
  return normalizeMyobUndoResult(unwrapApiData(response))
}
