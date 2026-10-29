import type { UserStoreDto } from './user'

export interface LoginRequest {
  username: string
  password: string
  passwordFormat?: 'raw' | 'clientSha256'
}

export interface SessionResponse {
  accessTokenExpiry: string
  refreshTokenExpiry: string
}

export interface CurrentUser {
  userGUID: string
  username: string
  email: string
  fullName?: string
  phone?: string
  permissions: string[]
  exactPermissions?: string[]
  roleNames: string[]
  storeNames: string[]
  stores?: UserStoreDto[]
}

export interface AccessControl {
  isAdmin: boolean
  isManager: boolean
  isUser: boolean
  isWarehouseStaff: boolean
  isWarehouseStaffOnly: boolean
  isWarehouseManager: boolean
  isStoreStaff: boolean
  isStoreManager: boolean
  isStoreLevelManager: boolean
  onlyOrder: boolean
  canReadOrder: boolean
  canWriteOrder: boolean
  canDeleteOrder: boolean
  canReadProduct: boolean
  canWriteProduct: boolean
  canDeleteProduct: boolean
  canReadUser: boolean
  canReadUserConsole: boolean
  canWriteUser: boolean
  canDeleteUser: boolean
  canReadRole: boolean
  canWriteRole: boolean
  canDeleteRole: boolean
  canReadStore: boolean
  canWriteStore: boolean
  canDeleteStore: boolean
  // 旧权限（保留兼容）
  canManageWarehouse: boolean
  canManageStore: boolean
  canViewReports: boolean
  canViewSalesIntelligence: boolean
  canViewSalesData: boolean
  canViewSalesDetail: boolean
  canViewCompactSalesBoard: boolean
  canViewMonthlyDailySalesDownload: boolean
  canViewProductMovementReport: boolean
  canViewBatchProductSalesAnalysis: boolean
  // 订货前台「货号销量」页面，独立于后台销售看板权限。
  canViewShopBatchProductSales: boolean
  canViewWarehouseProductFlowAnalysis: boolean
  canViewLocalProductSalesAnalysis: boolean
  canViewPurchaseAmountDashboard: boolean
  canViewLocalSupplierPurchaseSalesAnalysis: boolean
  // 「进货销量分析」合并页入口：批量货号销量或分店进货销量分析任一权限即可进入。
  canViewPurchaseSalesAnalysis: boolean
  canViewProductSalesAnalysis: boolean
  // 新细粒度权限
  canManageWarehouseProducts: boolean
  canManageWarehouseOrders: boolean
  canManageStoreOrderImportPriceVariance: boolean
  /** 分店订货单可录入退货（负数发货）；只认 Warehouse.StoreOrderReturn，无别名。 */
  canReturnStoreOrderGoods: boolean
  /** 供应商节日预订单管理（导入、开启分店、下单人员授权、导出）；只认 SupplierPreorder.Manage。 */
  canManageSupplierPreorders: boolean
  /** 预订单下单入口；SupplierPreorder.Order 或 Manage。能改哪家店由后端按下单人员授权判定。 */
  canOrderSupplierPreorders: boolean
  canManageWarehouseCategories: boolean
  canManageWarehouseLocations: boolean
  canViewContainers: boolean
  canCreateContainer: boolean
  canEditContainer: boolean
  canDeleteContainer: boolean
  /** 可点按钮查看完整货柜编号（默认只显示后 4 位） */
  canViewFullContainerNumber: boolean
  canManageStoreProducts: boolean
  canEditStoreProducts: boolean
  canCreateStoreProducts: boolean
  canManageStoreOps: boolean
  /** 分店现金管理页（/pos-admin/store-cash）：只认 Cash.Overview.View。 */
  canViewStoreCash: boolean
  canManageLocalPurchase: boolean
  canEditLocalPurchase: boolean
  canPushLocalPurchaseToHq: boolean
  canManagePricing: boolean
  canEditPricing: boolean
  canManagePromotions: boolean
  canEditPromotions: boolean
  canManageAdvertisements: boolean
  canEditAdvertisements: boolean
  canViewAustralianSuppliers: boolean
  canEditAustralianSuppliers: boolean
  canManageDomesticSuppliers: boolean
  canManageDomesticProducts: boolean
  canManageDomesticPrefixCodes: boolean
  canViewAttendanceSchedule: boolean
  canEditAttendanceSchedule: boolean
  canViewAttendanceAvailability: boolean
  canViewAttendancePunches: boolean
  canReviewAttendance: boolean
  canEditAttendanceHoliday: boolean
  canEditAttendanceSettings: boolean
  canReviewOwnAttendance: boolean
  canViewAttendancePayroll: boolean
  canViewAttendanceHours: boolean
  canAdjustAttendancePunches: boolean
  canEditAttendancePayroll: boolean
  canUnlockAttendancePayroll: boolean
  /** 员工个人信息维护页 / 用户管理「个人信息」页签：EmployeeProfiles.ViewManagedStore。 */
  canViewEmployeeProfiles: boolean
  /** 修改非敏感资料：EmployeeProfiles.EditManagedStore。 */
  canEditEmployeeProfiles: boolean
  canViewSystemLogs: boolean
  canViewPerformanceBaseline: boolean
  canViewOperationAudits: boolean
  canViewLegacyEmployeeLogs: boolean
  /** 员工操作日志合并页：老收银或新收银任一查看权限即可进入。 */
  canViewEmployeeOperationLogs: boolean
  canReviewLegacyEmployeeLogs: boolean
  /** 日结记录页：独立权限 DailyCloseRecords.View。 */
  canViewDailyCloseRecords: boolean
  /** 节日贺卡分店填报统计页：独立权限 SeasonalCards.Remaining.ViewAllStores。 */
  canViewSeasonalCardStats: boolean
  /** 人脸抓拍摄像机管理页：独立权限 FaceCapture.Cameras.Manage。 */
  canManageFaceCameras: boolean
  /** MYOB 对接页（连接公司文件、分店映射）：独立权限 Myob.Connection.Manage。 */
  canManageMyobConnections: boolean
  /** MYOB 对接页「员工绑定」页签：独立权限 Myob.Employee.Bind。 */
  canBindMyobEmployees: boolean
  /** MYOB 对接页入口：连接管理 / 员工绑定 / 编辑工资（发薪公司页签）三者任一；页内各页签再按各自权限显示。 */
  canAccessMyob: boolean
  /** 人脸比对页：FaceMatch.* 五个权限码任一（页签再按各自权限显示）。 */
  canAccessFaceMatch: boolean
  canManageScheduledTasks: boolean
  canManageSystemSettings: boolean
  canViewAppDownloads: boolean
  canManageAppDownloads: boolean
  canViewDeviceRegistration: boolean
  canManageDeviceRegistration: boolean
  canManageMobileDeviceActivationCodes: boolean
  canViewPosProducts: boolean
  canManagePosProducts: boolean
  canAccessAdminShell: boolean
  canAccessDashboard: boolean
  canAccessOrderFront: boolean
  hasPermission: (permission: string) => boolean
  hasRole: (role: string) => boolean
  onlyRole: (role: string) => boolean
  hasAnyRole: (roles: string[]) => boolean
  hasAllRoles: (roles: string[]) => boolean
  managedStoreCodes: () => string[] | null
  visibleStoreCodes: () => string[] | null
}

export interface NavigationMenuDto {
  path: string
  titleKey: string
  icon: string
  permission?: string
  children?: NavigationMenuDto[]
}
