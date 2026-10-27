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
