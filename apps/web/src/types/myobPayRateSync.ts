// 「从 MYOB 同步转账时薪」前端类型，对应后端 MyobPayRateSyncDtos.cs（api/react/v1/myob/pay-rate-sync）。
// 隐私：响应里只有我方员工名与时薪数，没有 MYOB 员工 UID 与年薪。

/** 一行的类别：Create 新建 / Fill 补空 / Differ 与 MYOB 不同 / Same 平日已一致 / Skip 不参与。 */
export type MyobPayRateSyncKind = 'Create' | 'Fill' | 'Differ' | 'Same' | 'Skip'

/** 可以单独勾选的价格格（apply 请求里每个员工勾了哪几格）。 */
export type MyobPayRateSyncCellKey = 'weekday' | 'saturday' | 'sunday' | 'publicHoliday'

/** Skip 原因与「无法推算」原因（同一套码，前端按码出文案；未识别的码按 unknown 兜底）。 */
export type MyobPayRateSyncReason =
  | 'MYOB_RATE_NOT_HOURLY'
  | 'MYOB_EMPLOYEE_TERMINATED'
  | 'MYOB_CARD_MISSING'
  | 'PAY_MODE_CASH'
  | 'CATEGORY_NOT_MAPPED'
  | 'CATEGORY_NOT_FOUND'
  | 'CATEGORY_NOT_ON_CARD'
  | 'CATEGORY_NOT_RATE_MULTIPLE'

export interface MyobPayRateSyncCell {
  ourRate: number | null
  /** 平日格 = MYOB 基础时薪取 2 位；周末 / 公假格 = 基础价 × 映射类别倍数取 2 位；无法推算为 null。 */
  suggested: number | null
  /** 用到的倍数（平日格为 null）。 */
  multiplier: number | null
  /** 有建议值且与我方现值不同：这一格才有东西可写。 */
  canApply: boolean
  /** 无法推算的原因码；能推算时为 null。 */
  unavailableReason: string | null
  defaultSelected: boolean
}

export interface MyobPayRateSyncRow {
  userGuid: string
  userName: string
  employmentType: string | null
  kind: MyobPayRateSyncKind
  skipReason: string | null
  payMode: string | null
  /** MYOB 基础时薪原值（可能有 4 位小数）。 */
  myobHourlyRate: number | null
  /** 原值取整到 2 位后与原值不同。 */
  rounded: boolean
  weekday: MyobPayRateSyncCell
  saturday: MyobPayRateSyncCell
  sunday: MyobPayRateSyncCell
  publicHoliday: MyobPayRateSyncCell
  /** 覆盖「我方现值 + MYOB 值 + 三个建议值」；apply 时原样带回，后端据此拒绝过期预览。 */
  hash: string
}

export interface MyobPayRateSyncPreview {
  connectionGuid: string
  connectionName: string
  storeCode: string
  /** 默认生效日期 yyyy-MM-dd（下一个未锁定周期起始日）。 */
  effectiveFrom: string
  fetchedAt: string
  /** 该分店里没有绑定这个 MYOB 公司文件的员工数（不出现在 rows 里）。 */
  notBoundCount: number
  rows: MyobPayRateSyncRow[]
}

export interface MyobPayRateSyncApplyItem {
  userGuid: string
  hash: string
  cells: MyobPayRateSyncCellKey[]
}

export interface MyobPayRateSyncApplyPayload {
  connectionGuid: string
  storeCode: string
  effectiveFrom: string
  items: MyobPayRateSyncApplyItem[]
}

export interface MyobPayRateSyncWritten {
  userGuid: string
  userName: string
  rateGuid: string
  cells: MyobPayRateSyncCellKey[]
}

export interface MyobPayRateSyncFailure {
  userGuid: string
  userName: string
  code: string
  message: string
}

export interface MyobPayRateSyncApplyResult {
  effectiveFrom: string
  written: MyobPayRateSyncWritten[]
  failed: MyobPayRateSyncFailure[]
}
