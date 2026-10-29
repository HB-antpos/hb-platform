// MYOB 对接（阶段 0：连接）接口类型。契约以后端 services/backend/BlazorApp.Api/Services/Myob/MyobDtos.cs 为准，
// 全部接口走 /api/react/v1/myob，返回 ApiResponse<T>，权限 Myob.Connection.Manage。

/** 连接状态：Active 可用；NeedsReauth token 失效需重新授权；Disconnected 已手动断开。 */
export type MyobConnectionStatus = 'Active' | 'NeedsReauth' | 'Disconnected'

export interface MyobConnection {
  connectionGuid: string
  /** MYOB 公司文件 ID（API 路径里的 cf_uri）。 */
  businessId: string
  businessName: string
  /** 只含数字（11 位）；未知为 null。 */
  abn: string | null
  status: MyobConnectionStatus
  authorizedByUserGuid: string | null
  authorizedByName: string | null
  /** 后端 UTC 时间字符串（可能不带 Z）。 */
  authorizedAt: string | null
  lastRefreshedAt: string | null
  lastError: string | null
  /** 映射到本连接的分店编号。 */
  storeCodes: string[]
}

export interface MyobStoreMappingItem {
  storeCode: string
  storeName: string
  storeAbn: string | null
  isActive: boolean
  /** 当前映射的连接；未映射为 null。 */
  connectionGuid: string | null
  /** 未映射时按 ABN 一致建议的 Active 连接；已映射时恒为 null。 */
  suggestedConnectionGuid: string | null
  mappedAt: string | null
}

export interface MyobStoreMappings {
  stores: MyobStoreMappingItem[]
  /** 可选作映射目标的连接（仅 Active）。 */
  connections: MyobConnection[]
}

/** PUT /store-mappings 的一行；connectionGuid 为 null 表示移除该分店映射。 */
export interface MyobStoreMappingChange {
  storeCode: string
  connectionGuid: string | null
}

// ───────────────────────── 阶段 1：员工绑定 + 发薪公司 ─────────────────────────
// 契约以后端 services/backend/BlazorApp.Api/Services/Myob/MyobEmployeeDtos.cs 为准。
// 员工绑定接口权限 Myob.Employee.Bind；发薪公司接口权限 Attendance.Payroll.Edit。

/** 绑定状态：Bound 已绑定；Suggested 高分建议；NeedsReview 需人工确认；Conflict 最佳候选卡已被别人绑定；Unmatched 没有相近候选。 */
export type MyobBindingStatus = 'Bound' | 'Suggested' | 'NeedsReview' | 'Conflict' | 'Unmatched'

/** 单项匹配信号：match 一致 / mismatch 不一致 / unknown 任一侧没有数据无法比对。 */
export type MyobSignalResult = 'match' | 'mismatch' | 'unknown'

/** 我方用工类型；资料没填为 null，未知取值原样保留（只用于展示）。 */
export type MyobEmployeeType = 'FullTime' | 'PartTime' | 'Temporary'

export interface MyobEmployeeCard {
  uid: string
  displayId: string | null
  name: string
  email: string | null
  /** 手机号只给末 4 位掩码，如「•••• 4821」。 */
  phoneMasked: string | null
  /** yyyy-MM-dd；没有为 null。 */
  dateOfBirth: string | null
  isActive: boolean
  /** MYOB EmploymentStatus：FullTime / PartTime / Casual / Other。 */
  employmentStatus: string | null
  hasAnnualEntitlement: boolean
  hasSickEntitlement: boolean
}

export interface MyobMatchBasis {
  email: MyobSignalResult
  birthday: MyobSignalResult
  name: MyobSignalResult
  phone: MyobSignalResult
}

export interface MyobMatchCandidate {
  card: MyobEmployeeCard
  /** 0–100。 */
  score: number
  basis: MyobMatchBasis
  /** 给人看的差异说明（后端中文）。 */
  notes: string[]
  /** 这张卡已绑定给谁；非空表示不可选。 */
  boundToUserGuid: string | null
  boundToUserName: string | null
}

export interface MyobLink {
  linkGuid: string
  myobEmployeeUid: string
  myobName: string | null
  matchMethod: string
  matchScore: number | null
  boundByName: string | null
  /** 后端 UTC 时间字符串（可能不带 Z）。 */
  boundAt: string | null
  /** MYOB 里找不到这张卡了。 */
  cardMissing: boolean
}

export interface MyobBindingRow {
  userGuid: string
  userName: string
  email: string | null
  employeeType: string | null
  storeLabels: string[]
  isCrossCompany: boolean
  status: MyobBindingStatus
  link: MyobLink | null
  candidate: MyobMatchCandidate | null
}

export interface MyobOnlyInOurs {
  userGuid: string
  userName: string
  email: string | null
  employeeType: string | null
  storeLabels: string[]
  joinedAt: string | null
  /** 「复制资料」要放进剪贴板的文本。 */
  copyText: string
}

export interface MyobOnlyInMyob {
  card: MyobEmployeeCard
  ignored: boolean
}

export type MyobTypeIssueCode = 'MISSING_LEAVE_ENTITLEMENT' | 'TEMPORARY_HAS_LEAVE_ENTITLEMENT'

export interface MyobTypeIssue {
  userGuid: string
  userName: string
  storeLabels: string[]
  ourType: string
  myobStatus: string | null
  hasAnnualEntitlement: boolean
  hasSickEntitlement: boolean
  /** 已知取值见 MyobTypeIssueCode；未知码原样保留。 */
  issueCode: string
}

export interface MyobBindingCounts {
  all: number
  bound: number
  suggested: number
  /** NeedsReview + Conflict。 */
  needsReview: number
  unmatched: number
  onlyInOurs: number
  onlyInMyob: number
  typeIssues: number
  /** 可批量确认的高分建议条数。 */
  batchConfirmable: number
}

export interface MyobBindingSnapshot {
  connectionGuid: string
  connectionName: string
  /** MYOB 员工列表最近一次拉取时间（UTC，可能不带 Z）。 */
  fetchedAt: string | null
  counts: MyobBindingCounts
  /** 已按「高分建议 → 需确认 / 冲突 → 未匹配 → 已绑定」排序，前端保持顺序。 */
  rows: MyobBindingRow[]
  onlyInOurs: MyobOnlyInOurs[]
  onlyInMyob: MyobOnlyInMyob[]
  typeIssues: MyobTypeIssue[]
}

export interface MyobBindingCandidates {
  userGuid: string
  userName: string
  email: string | null
  phoneMasked: string | null
  birthday: string | null
  employeeType: string | null
  storeLabels: string[]
  candidates: MyobMatchCandidate[]
}

export interface MyobConfirmBindingItem {
  userGuid: string
  myobEmployeeUid: string
  /** true = 人工在抽屉里选的；false = 确认高分建议（服务端会重新打分）。 */
  manual: boolean
}

export interface MyobConfirmFailure {
  userGuid: string
  /** MyobBindErrorCodes。 */
  code: string
  message: string
}

export interface MyobConfirmBindingsResult {
  confirmed: number
  failed: MyobConfirmFailure[]
}

export interface MyobIgnoreEmployeeRequest {
  connectionGuid: string
  myobEmployeeUid: string
  /** true 忽略；false 取消忽略。 */
  ignored: boolean
}

// ───────────── 发薪公司 ─────────────

export interface MyobPayrollCompanyOption {
  connectionGuid: string
  name: string
  /** 仅 GET /myob/companies 返回；发薪公司接口里的公司选项没有。 */
  abn?: string | null
}

/**
 * GET /myob/companies 返回的公司选项（只含 Active 连接，只读）。
 * 员工绑定 / 发薪公司页签用它代替 /myob/connections：后者只认 Myob.Connection.Manage，只有绑定或发薪权限的人会 403。
 */
export type MyobCompanyOption = MyobPayrollCompanyOption & { abn: string | null }

/** 列表一行：员工 × 上班分店。 */
export interface MyobPayrollEmployerRow {
  userGuid: string
  userName: string
  storeCode: string
  storeName: string
  defaultConnectionGuid: string | null
  defaultConnectionName: string | null
  effectiveConnectionGuid: string | null
  effectiveConnectionName: string | null
  /** 是否由「改由其他公司发」覆盖。 */
  isOverride: boolean
  /** 覆盖的生效日期 yyyy-MM-dd；非覆盖为 null。 */
  effectiveFrom: string | null
  isCrossCompany: boolean
}

export interface MyobPayrollEmployerList {
  rows: MyobPayrollEmployerRow[]
  companies: MyobPayrollCompanyOption[]
}

export interface MyobPayrollBindingState {
  connectionGuid: string
  bound: boolean
}

export interface MyobPayrollEmployerHistory {
  storeCode: string
  storeName: string
  /** 覆盖到的公司；null 表示「改回按分店」。 */
  connectionGuid: string | null
  connectionName: string | null
  effectiveFrom: string
  createdByName: string | null
  createdAt: string | null
}

export interface MyobPayrollEmployerDetail {
  userGuid: string
  userName: string
  rows: MyobPayrollEmployerRow[]
  companies: MyobPayrollCompanyOption[]
  bindingStates: MyobPayrollBindingState[]
  /** 可选生效日期（工资周期起始日，升序，第一项为当前期）。 */
  allowedEffectiveFrom: string[]
  /** 变更历史，最新在前。 */
  history: MyobPayrollEmployerHistory[]
}

export interface MyobSavePayrollEmployerItem {
  storeCode: string
  /** null = 按分店所属公司（默认）。 */
  connectionGuid: string | null
}

export interface MyobSavePayrollEmployersRequest {
  effectiveFrom: string
  items: MyobSavePayrollEmployerItem[]
}

export interface MyobPayrollEmployerQuery {
  keyword?: string
  storeCode?: string
  crossCompanyOnly?: boolean
}

// ───────────────────────── 阶段 2：工资类别映射 + 只读推送预览 ─────────────────────────
// 契约以后端 services/backend/BlazorApp.Api/Services/Myob/MyobTimesheetDtos.cs 为准。
// 工资类别映射接口权限 Myob.Connection.Manage；推送预览接口权限 Attendance.Payroll.Edit；日期一律 yyyy-MM-dd 字符串。

/**
 * 我方转账工时对应的 MYOB 工资类别键：5 项必填 + 2 项可选。
 * saturday / sunday 是全职、兼职共用的周末类别；saturdayCasual / sundayCasual 是临时工（Casual）专用的周末类别（可选）。
 */
export type MyobPayCategoryKey =
  | 'weekday'
  | 'saturday'
  | 'sunday'
  | 'saturdayCasual'
  | 'sundayCasual'
  | 'personalLeave'
  | 'holidayLeave'

/** MYOB 里的一个工资类别（Payroll/PayrollCategory/Wage）。 */
export interface MyobWageCategory {
  uid: string
  name: string
  /** Hourly / Salary；读不到为 null。 */
  wageType: string | null
}

/** 映射：每项是 MYOB 工资类别的 UID，null = 未设置。同一结构用于「当前映射」「建议」与保存请求。 */
export interface MyobPayCategoryMapping {
  weekdayUid: string | null
  /** 全职 / 兼职的周六。 */
  saturdayUid: string | null
  /** 全职 / 兼职的周日。 */
  sundayUid: string | null
  /** 临时工的周六（可选）。 */
  saturdayCasualUid: string | null
  /** 临时工的周日（可选）。 */
  sundayCasualUid: string | null
  personalLeaveUid: string | null
  holidayLeaveUid: string | null
}

export interface MyobPayCategories {
  connectionGuid: string
  connectionName: string
  /** MYOB 工资类别最近一次读取时间（UTC，可能不带 Z）。 */
  fetchedAt: string | null
  /** MYOB 里可选的工资类别（后端已按名称排序）。 */
  categories: MyobWageCategory[]
  mapping: MyobPayCategoryMapping
  /** 按名称相近给出的建议（只对尚未设置的项给出）。 */
  suggestions: MyobPayCategoryMapping
  /** 已设置的必填项数（0–5；临时工的可选两项不计入）。 */
  mappedCount: number
  /** 5 项必填都已设置，且都还在 MYOB 的类别里。 */
  complete: boolean
  /** 已保存的映射里 MYOB 现在已找不到的类别键，需要重新选择。 */
  missingKeys: MyobPayCategoryKey[]
  updatedAt: string | null
}

/** 预览行状态：Ready 可推送；NoTransfer 本公司没有转账工时；Blocked 有拦截项。 */
export type MyobPreviewStatus = 'Ready' | 'NoTransfer' | 'Blocked'

/** 员工发放方式：全转账 / 全现金 / 转账 + 现金。 */
export type MyobPayMode = 'Transfer' | 'Cash' | 'Mixed'

/** MYOB 对该员工该周期 Timesheet 的现状（只有 checkMyob=true 才会读）。 */
export type MyobTimesheetState = 'Unknown' | 'Clear' | 'HasEntries' | 'Processed' | 'CheckFailed'

export interface MyobPreviewPeriodOption {
  /** 周期起始日 yyyy-MM-dd。 */
  start: string
  end: string
}

export interface MyobUnlockedStore {
  storeCode: string
  storeName: string
  /** 本公司员工里在这家店有工时的人数。 */
  employeeCount: number
}

export interface MyobPreviewReadiness {
  /** 已提交锁定的分店（有本公司员工工时的分店）。 */
  lockedStores: string[]
  unlockedStores: MyobUnlockedStore[]
  categoryMappedCount: number
  categoryComplete: boolean
  /** 是否读过 MYOB 的 Timesheet 状态（checkMyob=true）。 */
  myobChecked: boolean
  /** MYOB 里已发薪的员工数（仅 myobChecked 时有意义）。 */
  myobProcessedCount: number
  /** 读 MYOB 失败的员工数（仅 myobChecked 时有意义）。 */
  myobCheckFailedCount: number
}

export interface MyobPreviewHint {
  /** MyobHintCodes：BIRTHDAY_REVIEW / BELOW_MINIMUM_RATE / PUBLIC_HOLIDAY_RATE_MISMATCH / ANNUAL_CASH_OUT_NOT_PUSHED；未知码原样保留。 */
  code: string
  userGuid: string
  userName: string
  /** 给人看的中文说明（含日期 / 工时等细节）。 */
  message: string
}

export interface MyobPreviewBlocker {
  /** MyobBlockerCodes：NOT_BOUND / STORE_NOT_LOCKED / STORE_NOT_MAPPED / CATEGORY_NOT_MAPPED / NO_RATE / NO_STORE_ACCESS / MYOB_PROCESSED；未知码原样保留。 */
  code: string
  message: string
  /** 相关分店或类别键；没有为 null。 */
  ref: string | null
}

/** 要推送的转账工时，按 5 个 MYOB 类别汇总（小时）。 */
export interface MyobPushHours {
  weekday: number
  saturday: number
  sunday: number
  personalLeave: number
  holidayLeave: number
}

/** 日网格里的一行：key 是类别键或工时种类键；hours 与 days 一一对应。 */
export interface MyobDayGridRow {
  key: string
  /** 后端可选给出的显示名（如 MYOB 类别名）；契约里目前没有，没给为 null。 */
  label: string | null
  hours: number[]
  total: number
}

/** 跨公司时各公司分到的份额。 */
export interface MyobCompanyShare {
  connectionGuid: string
  connectionName: string
  actualHours: number
  allocatedTransferHours: number
}

export interface MyobPreviewDetail {
  /** 周期内每一天 yyyy-MM-dd（7 或 14 个）。 */
  days: string[]
  actual: MyobDayGridRow[]
  pushed: MyobDayGridRow[]
  cash: MyobDayGridRow[]
  shares: MyobCompanyShare[]
  /** 年假折现里占用了转账额度、但不推送的小时。 */
  annualCashOutTransferHours: number
  checkOk: boolean
  checkMessage: string
}

export interface MyobPreviewRow {
  userGuid: string
  userName: string
  payMode: MyobPayMode | null
  fixedTransferHoursPerWeek: number
  isCrossCompany: boolean
  allocatedTransferHours: number
  actualHours: number
  /** 有工时的分店，如「1013 Orion」。 */
  storeLabels: string[]
  status: MyobPreviewStatus
  pushed: MyobPushHours
  pushedTotal: number
  /** 本期年假折现工时（不推送，财务手工处理）。 */
  cashOutHours: number
  blockers: MyobPreviewBlocker[]
  hintCodes: string[]
  /** 已绑定的 MYOB 员工卡姓名；未绑定为 null。 */
  myobName: string | null
  myobState: MyobTimesheetState
  /** 推送状态（后端 MyobPreviewPushStateDto）：未推送 / 已推送 / 需重新推送，及最近一次记录。 */
  push: MyobPreviewPushState
  detail: MyobPreviewDetail
}

export interface MyobPreviewTotals {
  readyCount: number
  readyHours: number
  blockedCount: number
  noTransferCount: number
}

export interface MyobTimesheetPreview {
  connectionGuid: string
  connectionName: string
  periodStart: string
  periodEnd: string
  weeks: number
  /** 可选的工资周期：最近 6 期，新的在前。 */
  periods: MyobPreviewPeriodOption[]
  readiness: MyobPreviewReadiness
  hints: MyobPreviewHint[]
  /** 后端已按「可推送 → 被拦截 → 不推送」排序，同档按姓名。 */
  rows: MyobPreviewRow[]
  totals: MyobPreviewTotals
  /** 推送功能是否已开放：总闸 Myob__TimesheetPushEnabled 开 + 该公司文件在灰度白名单里（权限另由 canPushMyobTimesheet 决定）。 */
  pushEnabled: boolean
}

// ───────────────────────── Timesheet 推送与撤销 ─────────────────────────
// 契约以后端 services/backend/BlazorApp.Api/Services/Myob/MyobTimesheetPushDtos.cs 为准。
// 路由 /api/react/v1/myob/timesheet/{push, batches, batches/{id}, batches/{id}/undo}，权限 Myob.Timesheet.Push；会写 MYOB 生产数据。

/** 预览行的推送状态。 */
export type MyobPushState = 'NotPushed' | 'Pushed' | 'NeedsRepush' | 'ProcessedSkipped' | 'Failed' | 'Mismatch'

export interface MyobPreviewPushState {
  state: MyobPushState
  needsRepush: boolean
  /** 最近一条记录的状态（MyobPushStatus）；没有记录为 null。 */
  lastStatus: string | null
  lastBatchGuid: string | null
  lastPushedAt: string | null
}

/**
 * 推送 / 撤销结果里单个员工的状态。Blocked 只在返回里出现（被拦截项挡住，没落库）；
 * Undo* 三个是撤销被拒绝的原因。后端给了不认识的状态一律按 Failed 处理，不把未知值显示成成功。
 */
export type MyobPushStatus =
  | 'Pushed'
  | 'UpToDate'
  | 'SkippedProcessed'
  | 'SkippedHasEntries'
  | 'Blocked'
  | 'Failed'
  | 'Mismatch'
  | 'Undone'
  | 'UndoConflict'
  | 'UndoBlockedProcessed'
  | 'UndoBlockedNewer'
  | 'Running'

export interface MyobPushRequest {
  connectionGuid: string
  /** 工资周期起始日 yyyy-MM-dd。 */
  periodStart: string
  /** 本块 1–10 人；客户端只传「推谁」，工时由服务端重新规划。 */
  userGuids: string[]
  /** 第二块起带上第一块返回的批次号。 */
  batchGuid?: string
  /** 用户在确认弹窗里按人勾选「覆盖」的员工（MYOB 里已有未发薪内容的人）。 */
  overwriteUserGuids: string[]
  /** 已知晓的提示项码；员工命中的提示必须全部被覆盖。 */
  acknowledgedHintCodes: string[]
}

export interface MyobPushUserResult {
  userGuid: string
  userName: string
  status: MyobPushStatus
  /** 拦截 / 失败 / 跳过的原因码；没有为 null。 */
  code: string | null
  /** 后端给的说明（不含 MYOB 响应体）；没有为 null。 */
  message: string | null
  pushedHours: number
}

export interface MyobPushResult {
  batchGuid: string
  connectionGuid: string
  periodStart: string
  results: MyobPushUserResult[]
}

export interface MyobPushBatchSummary {
  batchGuid: string
  startedAt: string
  actorUserGuid: string | null
  actorName: string | null
  total: number
  pushed: number
  upToDate: number
  skipped: number
  failed: number
  mismatch: number
  undone: number
  /** 本批真正写过 MYOB、且还没撤销的员工数。 */
  undoable: number
}

export interface MyobPushBatchItem {
  userGuid: string
  userName: string
  status: MyobPushStatus
  errorCode: string | null
  errorStatus: number | null
  pushedHours: number
  startedAt: string
  finishedAt: string | null
  undoneAt: string | null
}

export interface MyobPushBatchDetail {
  summary: MyobPushBatchSummary
  connectionGuid: string
  periodStart: string
  items: MyobPushBatchItem[]
}

export interface MyobUndoResult {
  batchGuid: string
  results: MyobPushUserResult[]
}

// ───────────────────────── 从 MYOB 同步资料到我方 ─────────────────────────
// 契约以后端 services/backend/BlazorApp.Api/Services/Myob/MyobEmployeeSyncDtos.cs 为准（权限 Myob.Employee.Bind + EmployeeProfiles.Edit）。
// 只读 MYOB、只写我方员工资料；必须先预览再写入，写入前留快照，7 天内可整批撤销。

/** 可同步的字段键；birthday / bank / super 为敏感字段。 */
export type MyobSyncField = 'name' | 'phone' | 'email' | 'birthday' | 'gender' | 'address' | 'employmentType' | 'bank' | 'super'

/** FillEmpty 只补空；Overwrite 以 MYOB 为准（有差异的也默认勾上）。 */
export type MyobSyncMode = 'FillEmpty' | 'Overwrite'

/** Fill 我方为空补上；Overwrite 我方已有值且不同。 */
export type MyobSyncChangeKind = 'Fill' | 'Overwrite'

/** 范围：userGuids 为空 / 缺省 = 该公司文件下全部已绑定员工。 */
export interface MyobSyncScopeRequest {
  connectionGuid: string
  userGuids?: string[]
  /** true = 忽略 MYOB 缓存重新拉取。 */
  refresh?: boolean
}

export interface MyobSyncFieldStat {
  field: MyobSyncField
  sensitive: boolean
  defaultSelected: boolean
  myobHasValue: number
  /** 我方为空、MYOB 有值（可补空）的员工数。 */
  fillable: number
  /** 我方与 MYOB 都有值且不同的员工数。 */
  differs: number
}

export interface MyobSyncFieldStats {
  connectionGuid: string
  fetchedAt: string
  boundCount: number
  missingCardCount: number
  fields: MyobSyncFieldStat[]
  /** 当前用户有 EmployeeProfiles.Edit；false 时前端不提供同步入口。 */
  canWrite: boolean
  /** MYOB 数据没拉到的字段键（如付款详情接口没权限）：按「MYOB 无值」统计，前端灰显、不可选。 */
  unavailableFields: MyobSyncField[]
}

export interface MyobSyncPreviewRequest {
  connectionGuid: string
  userGuids?: string[]
  refresh?: boolean
  fields: MyobSyncField[]
  mode: MyobSyncMode
}

export interface MyobSyncChange {
  field: MyobSyncField
  sensitive: boolean
  /** 我方现值展示文本；空为 null。银行账号只给末 4 位。 */
  ourDisplay: string | null
  myobDisplay: string | null
  kind: MyobSyncChangeKind
  defaultChecked: boolean
  note: string | null
  /** 规范化哈希；写入时原样带回，服务端据此发现「预览后被改过」。 */
  ourHash: string
  myobHash: string
}

export interface MyobSyncEmployee {
  userGuid: string
  userName: string
  storeLabels: string[]
  employeeType: string | null
  /** 有待审敏感资料申请；写入敏感字段会使其作废。 */
  hasPendingSensitiveRequest: boolean
  /** 预览时的敏感资料版本号；apply 原样带回。 */
  sensitiveRevision: number
  changes: MyobSyncChange[]
}

export interface MyobSyncPreviewSummary {
  employeeCount: number
  changeCount: number
  defaultCheckedCount: number
  overwriteCount: number
  sensitiveCount: number
  pendingSensitiveRequestCount: number
  skippedNoDiff: number
  skippedMissingCard: number
}

export interface MyobSyncPreview {
  connectionGuid: string
  fetchedAt: string
  mode: MyobSyncMode
  fields: MyobSyncField[]
  summary: MyobSyncPreviewSummary
  /** 同 MyobSyncFieldStats.unavailableFields。 */
  unavailableFields: MyobSyncField[]
  employees: MyobSyncEmployee[]
}

export interface MyobSyncApplyChangeItem {
  field: MyobSyncField
  ourHash: string
  myobHash: string
}

export interface MyobSyncApplyEmployeeItem {
  userGuid: string
  expectedSensitiveRevision: number
  changes: MyobSyncApplyChangeItem[]
}

export interface MyobSyncApplyRequest {
  connectionGuid: string
  items: MyobSyncApplyEmployeeItem[]
  /** 员工有待审敏感申请时，写入敏感字段会使其作废；用户确认后传 true。 */
  confirmSupersedePendingSensitiveRequests: boolean
}

export interface MyobSyncWritten {
  userGuid: string
  userName: string
  fields: MyobSyncField[]
}

export interface MyobSyncFailure {
  userGuid: string
  userName: string
  /** 该员工没有写入的字段。 */
  fields: MyobSyncField[]
  /** MYOB_SYNC_* 错误码。 */
  code: string
  message: string
}

export interface MyobSyncApplyResult {
  /** 没有任何写入成功时为 null（不留批次，也不可撤销）。 */
  batchGuid: string | null
  writtenEmployeeCount: number
  writtenChangeCount: number
  writtenSensitiveCount: number
  written: MyobSyncWritten[]
  failed: MyobSyncFailure[]
  /** 撤销截止时间（UTC）= 写入时间 + 7 天。 */
  undoDeadline: string | null
}

export type MyobSyncBatchStatus = 'Active' | 'Undone' | 'Expired'

export interface MyobSyncBatch {
  batchGuid: string
  createdAt: string
  actorName: string | null
  employeeCount: number
  changeCount: number
  sensitiveCount: number
  status: MyobSyncBatchStatus
  canUndo: boolean
  undoDeadline: string
  undoneAt: string | null
  undoneByName: string | null
}

export interface MyobSyncUndoResult {
  restoredEmployeeCount: number
  restoredChangeCount: number
  /** 写入之后又被人改过、因此没有还原的字段。 */
  skippedChanged: MyobSyncWritten[]
}
