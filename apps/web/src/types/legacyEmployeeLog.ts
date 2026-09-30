/** 老系统（旧版收银 POSM.EmployeeLogs）操作日志；时间均为门店本地墙钟时间字符串，不带时区。 */
export interface LegacyEmployeeLogItem {
  id: string
  employeeId?: string | null
  employeeName?: string | null
  operation?: string | null
  operationDetail?: string | null
  operationTime: string
  deviceCode?: string | null
  storeCode?: string | null
  lastUploadTime: string
}

export interface LegacyEmployeeLogOperationCount {
  operation?: string | null
  count: number
}

export interface LegacyEmployeeLogEmployeeOption {
  employeeId?: string | null
  employeeName?: string | null
  count: number
}

export interface LegacyEmployeeLogDeviceOption {
  deviceCode?: string | null
  count: number
}

export interface LegacyEmployeeLogListResult {
  items: LegacyEmployeeLogItem[]
  total: number
  pageNumber: number
  pageSize: number
  operationCounts: LegacyEmployeeLogOperationCount[]
  employees: LegacyEmployeeLogEmployeeOption[]
  devices: LegacyEmployeeLogDeviceOption[]
}

export interface LegacyEmployeeLogContext {
  target: LegacyEmployeeLogItem
  windowMinutes: number
  neighbors: LegacyEmployeeLogItem[]
  truncated: boolean
}

export interface LegacyEmployeeLogQueryParams {
  storeCode: string
  /** 墙钟时间 YYYY-MM-DDTHH:mm:ss，半开区间 [from, to) */
  from: string
  to: string
  deviceCode?: string
  employeeIds?: string[]
  operations?: string[]
  keyword?: string
  pageNumber: number
  pageSize: number
  sortOrder: 'asc' | 'desc'
}
