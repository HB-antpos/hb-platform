import { readFileSync } from 'node:fs'
import path from 'node:path'
import enGB from 'antd/es/date-picker/locale/en_GB'
import dayjsGenerateConfig from 'rc-picker/es/generate/dayjs'
import 'dayjs/locale/en-gb'

function assert(condition: unknown, message: string): asserts condition {
  if (!condition) throw new Error(message)
}

const pageSource = readFileSync(path.resolve(process.cwd(), 'src/pages/PosAdmin/ScheduleAttendance/index.tsx'), 'utf8')
const serviceSource = readFileSync(path.resolve(process.cwd(), 'src/services/scheduleAttendanceService.ts'), 'utf8')
const zhLocale = JSON.parse(readFileSync(path.resolve(process.cwd(), 'src/i18n/locales/zh.json'), 'utf8')).posAdmin.scheduleAttendance
const enLocale = JSON.parse(readFileSync(path.resolve(process.cwd(), 'src/i18n/locales/en.json'), 'utf8')).posAdmin.scheduleAttendance
const trajectoryMapSource = readFileSync(
  path.resolve(process.cwd(), 'src/pages/PosAdmin/ScheduleAttendance/AttendanceLocationTrajectoryMap.tsx'),
  'utf8',
)
const approvalColumnsStart = pageSource.indexOf('const approvalColumns:')
const holidayColumnsStart = pageSource.indexOf('const holidayColumns:', approvalColumnsStart)
assert(approvalColumnsStart >= 0 && holidayColumnsStart > approvalColumnsStart, '应能定位审核中心表格定义')
const approvalColumnsSource = pageSource.slice(approvalColumnsStart, holidayColumnsStart)
const recordColumnsStart = pageSource.indexOf('const recordColumns:')
const availabilityColumnsStart = pageSource.indexOf('const availabilityColumns:', recordColumnsStart)
assert(recordColumnsStart >= 0 && availabilityColumnsStart > recordColumnsStart, '应能定位考勤记录表格定义')
const recordColumnsSource = pageSource.slice(recordColumnsStart, availabilityColumnsStart)
const punchColumnsStart = pageSource.indexOf('const punchColumns:')
assert(punchColumnsStart >= 0 && approvalColumnsStart > punchColumnsStart, '应能定位打卡记录表格定义')
const punchColumnsSource = pageSource.slice(punchColumnsStart, approvalColumnsStart)
const adjustmentHandlersStart = pageSource.indexOf('const openPunchAdjustmentModal =')
const saveSettingsStart = pageSource.indexOf('const saveSettings =', adjustmentHandlersStart)
assert(adjustmentHandlersStart >= 0 && saveSettingsStart > adjustmentHandlersStart, '应能定位补卡业务处理逻辑')
const adjustmentHandlersSource = pageSource.slice(adjustmentHandlersStart, saveSettingsStart)
const adjustmentModalStart = pageSource.indexOf("title={t('posAdmin.scheduleAttendance.drawer.adjustPunch")
const reviewModalStart = pageSource.indexOf("title={reviewTarget?.sourceType", adjustmentModalStart)
assert(adjustmentModalStart >= 0 && reviewModalStart > adjustmentModalStart, '应能定位补卡修改弹窗')
const adjustmentModalSource = pageSource.slice(adjustmentModalStart, reviewModalStart)
const filterBarStart = pageSource.indexOf('const filterBar =')
const recordTabItemStart = pageSource.indexOf('const recordTabItem =', filterBarStart)
assert(filterBarStart >= 0 && recordTabItemStart > filterBarStart, '应能定位排班筛选栏定义')
const filterBarSource = pageSource.slice(filterBarStart, recordTabItemStart)
const sundayBoundary = dayjsGenerateConfig.getFixedDate('2026-08-02')
const mondayBoundary = dayjsGenerateConfig.getFixedDate('2026-08-03')

assert(dayjsGenerateConfig.locale.getWeekFirstDay(enGB.lang.locale) === 1, '周选择器使用的日历 locale 应以周一为第一天')
assert(dayjsGenerateConfig.locale.getWeekFirstDate(enGB.lang.locale, sundayBoundary).format('YYYY-MM-DD') === '2026-07-27', '周日所在周应从前一个周一开始')
assert(dayjsGenerateConfig.locale.getWeekFirstDate(enGB.lang.locale, mondayBoundary).format('YYYY-MM-DD') === '2026-08-03', '周一应成为所选周的起始日期')
assert(dayjsGenerateConfig.locale.getWeek(enGB.lang.locale, sundayBoundary) === 31, '周日仍应归入上一 ISO 周')
assert(dayjsGenerateConfig.locale.getWeek(enGB.lang.locale, mondayBoundary) === 32, '周一应开始新的 ISO 周')
assert(pageSource.includes("import 'dayjs/locale/en-gb'"), '排班页应注册周一开始的 Day.js locale')
assert(filterBarSource.includes('locale={enGB}'), '排班周选择器应显式使用周一开始的 locale')
assert(!filterBarSource.includes('formatBusinessDisplay'), '筛选器与日期输入控件不应追加星期显示')
assert(pageSource.includes("type TabKey = 'schedules' | 'records'"), '考勤页应有独立记录 tab，不改变排班 CRUD')
assert(pageSource.includes('buildAttendanceRecordSummary'), '记录表应复用首上班/最终下班与班段工时逻辑')
assert(pageSource.includes('candidateOvertimeMinutes'), '记录与审批 UI 应显示候选加班')
assert(pageSource.includes('approvedOvertimeMinutes'), '记录与审批 UI 应显示批准加班')
assert(pageSource.includes('const formatBusinessDisplayText = useCallback'), '业务日期应通过独立格式化入口插入本地化星期')
assert(pageSource.includes('const renderBusinessDisplay = useCallback'), '独立日期列应通过统一的两行渲染入口显示星期')
assert((pageSource.match(/formatBusinessDisplayText\(/g) ?? []).length >= 18, '详情、补卡、轨迹与地图中的业务日期时间应完整显示星期')
assert((pageSource.match(/renderBusinessDisplay\(/g) ?? []).length >= 6, '各考勤表格的独立业务日期列应显示星期')
assert(recordColumnsSource.includes('renderBusinessDisplay(formatDate(record.workDate))'), '考勤记录排班日期应显示星期')
assert((recordColumnsSource.match(/formatBusinessDisplayText\(/g) ?? []).length === 4, '考勤记录两端班段与首末打卡时间应显示星期')
assert((punchColumnsSource.match(/renderBusinessDisplay\(/g) ?? []).length >= 2, '打卡记录工作日期与本地打卡时间应显示星期')
assert(punchColumnsSource.includes('formatBusinessDisplayText(formatDateTime(record.punchTimeLocal))'), '打卡记录有效时间应显示星期')
assert(approvalColumnsSource.includes('renderBusinessDisplay(formatDate(value))'), '审核中心工作日期应显示星期')
assert(approvalColumnsSource.includes("dataIndex: 'reviewedAt', width: 170, render: formatDateTime"), '审核时间属于审计字段，不应附加星期')
assert(pageSource.includes("{formatDateTime(settings.updatedAt)}"), '最后更新时间属于审计字段，不应附加星期')
assert(pageSource.includes('formatBusinessDisplayText(formatDateTimeInTimeZone('), '门店时区时间应先转换再按展示日期附加星期')
assert(pageSource.includes('formatBusinessDisplayText(formatStoredLocalDateTime('), '存储的门店本地时间应按本地日期附加星期')
assert(pageSource.includes('formatBusinessDetailText(supplementalDetail)'), '审核补充明细中的日期范围应逐个显示星期')
assert(pageSource.includes('formatBusinessDetailText(record.detail)'), '未知审核类型的原始业务明细也应保留内容并增强日期显示')
assert(!recordColumnsSource.match(/[\u3400-\u9fff]/), '考勤记录表格不应残留用户可见的硬编码中文')
assert(!adjustmentHandlersSource.match(/[\u3400-\u9fff]/), '补卡业务提示不应依赖硬编码中文回退')
assert(!adjustmentModalSource.match(/[\u3400-\u9fff]/), '补卡修改弹窗不应残留用户可见的硬编码中文')
assert(!pageSource.includes("tabs.records', '考勤记录'"), '考勤记录页签名称应完全由 locale 提供')
assert(recordColumnsSource.includes("statusLabel('segment', segment.status)"), '班段状态应使用本地化状态文案')
assert(recordColumnsSource.includes("statusLabel('scheduleState', record.scheduleState)"), '考勤排班状态应使用本地化状态文案')
assert(recordColumnsSource.includes("statusLabel('overtimeApproval', record.overtimeApprovalStatus)"), '加班审批状态应使用本地化状态文案')
assert(pageSource.includes('isKnownAttendanceApprovalSourceType'), '已知审批来源必须使用本地化展示文案')
assert(pageSource.includes('getSupplementalAttendanceApprovalDetail'), 'Punch/Leave 必须保留 DTO 补充明细')
assert(pageSource.includes('supplementalDetail ?'), 'DTO 补充明细仅在非空且不重复时渲染')
assert(!pageSource.includes('<Typography.Text strong>{record.title}</Typography.Text>'), '已知审批不得直接展示后端标题')
assert(pageSource.includes('record.adjustment.effectivePunchTimeLocal ?'), '补卡仅在实际生效时间存在时展示该字段')
assert(pageSource.includes('validateOvertimeApproval'), '加班审批提交前应校验范围、15 分钟粒度与备注')
assert(pageSource.includes('approveOvertime'), '加班审批应使用明确的批准按钮语义')
assert(pageSource.includes('rejectOvertime'), '加班审批应使用明确的拒绝按钮语义')
assert(pageSource.includes('buildLocalPunchAdjustmentPreview'), '补卡保存前应展示客户端预览')
assert(pageSource.includes('if (!access.canViewAttendancePunches)'), '记录请求入口必须以 Punch.ViewManagedStore 做二次保护')
assert(pageSource.includes('access.canViewAttendancePunches ? {'), '无 Punch 查看权限时不得渲染考勤记录 tab')
assert(pageSource.includes("activeTab === 'records' && access.canViewAttendancePunches"), '记录 tab 的加载分支必须再次校验 Punch 查看权限')
assert(pageSource.includes('createMyAttendancePunchAdjustment'), '店长本人补卡应调用服务层集中入口')
assert(pageSource.match(/buildPunchAdjustmentPayload\(adjustmentTarget, values\)/g)?.length === 2, 'preview/create 必须复用同一个 payload builder')
assert(pageSource.includes("adjustmentMode: 'create' | 'replace'"), '补卡表单必须显式区分新增和纠正模式')
assert(pageSource.includes('resolvePunchAdjustmentOriginalGuid(values.adjustmentMode, values.originalPunchGuid)'), '新增模式必须清空 originalPunchGuid')
assert(pageSource.includes("getDefaultPunchAdjustmentMode(record, punchType)"), '漏最终下班应由统一规则默认进入新增模式')
assert(pageSource.includes('previewRequestIdRef'), 'preview 应使用递增请求 id 防止旧响应覆盖')
assert(pageSource.includes('previewPayloadSnapshot'), 'preview 与提交应绑定完整 payload snapshot')
assert(pageSource.includes('previewRevision'), '补卡 preview 与提交必须绑定后端 revision')
assert(pageSource.includes('previewRevisionMissing'), '缺少 preview revision 时必须显示明确错误')
assert(pageSource.includes('previewRevision: serverAdjustmentPreview.previewRevision'), '补卡提交必须原样回传当前 preview revision')
assert(pageSource.includes('getPunchAdjustmentPayloadSnapshot(payload)'), '提交前应重新计算当前 payload snapshot')
assert(pageSource.includes('hasMissingClockOut'), '记录 UI 应展示漏下班状态')
assert(pageSource.includes('supersedesPunchGuid'), '打卡审计应展示原始/有效关系')
assert(pageSource.match(/canAdjustOwnAttendanceRecord\(/g)?.length === 2, '补卡按钮和弹窗入口必须复用同一角色判断')
assert(pageSource.includes('serverAdjustmentPreview.existingSession?.workedMinutes'), '工时预览必须以服务端结果为权威')
assert(pageSource.includes('getProposedAdjustmentPunchStatus'), '异常状态必须从服务端 proposed session 精确匹配请求打卡')
assert(!pageSource.includes('adjustmentPreview.exceptions.map'), '补卡预览不得继续展示忽略 grace 的本地异常推断')
assert(pageSource.includes("window.open(externalMapUrl, '_blank', 'noopener,noreferrer')"), '地图只能由明确点击后在外部窗口打开')
assert(pageSource.includes('buildAttendanceLocationTrajectory'), '班中样本必须按有效班段生成时间轨迹')
assert(pageSource.includes('sampleRequestIdRef'), '快速切换班中样本时必须阻止旧响应覆盖新抽屉')
assert((pageSource.match(/sampleRequestIdRef\.current !== requestId/g) ?? []).length >= 3, '打卡、分段样本和最终结果写入前都必须拒绝旧请求')
assert(pageSource.includes('sampleRequestIdRef.current += 1'), '关闭抽屉必须使仍在执行的样本请求失效')
assert(pageSource.includes('trajectoryMapVisible ?'), '轨迹地图必须由显式状态门控，打开抽屉时不得自动加载')
assert(!pageSource.includes('tile.openstreetmap.org'), '考勤主页面不得直接加载 OSM 图块')
assert(trajectoryMapSource.includes("await import('leaflet')"), 'Leaflet 必须在用户明确加载轨迹地图后动态导入')
assert(trajectoryMapSource.includes('https://tile.openstreetmap.org/'), '轨迹地图应使用明确的 OSM 图块地址')
assert(trajectoryMapSource.includes('map?.remove()'), '关闭或切换轨迹时必须销毁 Leaflet 地图实例')
assert(serviceSource.includes('MY_PUNCH_ADJUSTMENTS_ENDPOINT'), '补卡 mutation 路由必须集中在服务层')
assert(serviceSource.includes("`${API_BASE}/records`"), '考勤记录必须走 Punch.ViewManagedStore 保护的 records endpoint')

const requiredLocalePaths = [
  'tabs.records',
  'fields.schedule',
  'fields.segments',
  'fields.boundaries',
  'fields.workedAndBreak',
  'fields.exceptions',
  'fields.overtime',
  'fields.adjustmentMode',
  'fields.originalPunch',
  'fields.requestedPunchTimeLocal',
  'fields.preview',
  'actions.adjustPunch',
  'actions.preview',
  'actions.createMissingPunch',
  'actions.replacePunch',
  'drawer.adjustPunch',
  'validation.originalPunchRequired',
  'validation.reasonRequired',
  'messages.adjustmentPreviewInvalid',
  'messages.adjustmentPreviewFailed',
  'messages.previewExpired',
  'messages.adjustmentApplied',
  'messages.adjustmentSubmitted',
  'messages.adjustmentFailed',
  'messages.previewBeforeSave',
  'messages.originalPunchAudit',
  'messages.noOriginalPunch',
  'messages.newPunchAudit',
  'messages.proposedPunchStatusUnavailable',
  'messages.adjustmentWillAutoApprove',
  'messages.adjustmentWillReview',
  'recordLabels.minutes',
  'recordLabels.late',
  'recordLabels.earlyLeave',
  'recordLabels.earlyArrival',
  'recordLabels.lateDeparture',
  'recordLabels.candidateOvertime',
  'recordLabels.approvedOvertime',
  'recordLabels.crossStore',
  'recordLabels.original',
  'recordLabels.effective',
  'recordLabels.workedMinutesChange',
  'recordLabels.candidateOvertimeChange',
  'status.missingClockOut',
  'status.scheduleState.NotStarted',
  'status.scheduleState.InProgress',
  'status.scheduleState.Completed',
  'status.scheduleState.MissingClockOut',
  'status.segment.NotStarted',
  'status.segment.Open',
  'status.segment.Completed',
  'status.segment.MissingClockOut',
  'status.overtimeApproval.NotRequired',
  'status.overtimeApproval.Pending',
  'status.overtimeApproval.Approved',
  'status.overtimeApproval.Rejected',
]

function readLocalePath(locale: Record<string, unknown>, pathValue: string) {
  return pathValue.split('.').reduce<unknown>((value, key) => (
    value && typeof value === 'object' ? (value as Record<string, unknown>)[key] : undefined
  ), locale)
}

for (const pathValue of requiredLocalePaths) {
  const zhValue = readLocalePath(zhLocale, pathValue)
  const enValue = readLocalePath(enLocale, pathValue)
  assert(typeof zhValue === 'string' && zhValue.trim().length > 0, `中文 locale 缺少 ${pathValue}`)
  assert(typeof enValue === 'string' && enValue.trim().length > 0, `英文 locale 缺少 ${pathValue}`)
  assert(!/[\u3400-\u9fff]/.test(enValue), `英文 locale ${pathValue} 不应包含中文`)
}

// 用餐未休息加工时：文案在页面级消息文件；审批标题与来源类型是模板键，静态扫描扫不到，这里逐个核对。
const zhPageLocale = JSON.parse(readFileSync(
  path.resolve(process.cwd(), 'src/pages/PosAdmin/ScheduleAttendance/scheduleAttendanceMessages.zh.json'),
  'utf8',
)).posAdmin.scheduleAttendance
const enPageLocale = JSON.parse(readFileSync(
  path.resolve(process.cwd(), 'src/pages/PosAdmin/ScheduleAttendance/scheduleAttendanceMessages.en.json'),
  'utf8',
)).posAdmin.scheduleAttendance
for (const pathValue of ['approvalPresentation.MealBreak.title', 'status.sourceType.MealBreak']) {
  const zhValue = readLocalePath(zhPageLocale, pathValue)
  const enValue = readLocalePath(enPageLocale, pathValue)
  assert(typeof zhValue === 'string' && zhValue.trim().length > 0, `页面级中文文案缺少 ${pathValue}`)
  assert(typeof enValue === 'string' && enValue.trim().length > 0, `页面级英文文案缺少 ${pathValue}`)
  assert(!/[㐀-鿿]/.test(enValue), `页面级英文文案 ${pathValue} 不应包含中文`)
}
assert(recordColumnsSource.includes('buildAttendanceMealPaySummary(record)'), '考勤记录应展示用餐扣除、加回与计薪工时')
assert(pageSource.includes("reviewTarget.sourceType === 'MealBreak'")
  && pageSource.includes('validateMealBreakApproval'), '拒绝用餐加工时必须先校验审核备注')
assert(pageSource.includes('record.mealClaim'), '用餐加工时审批必须展示员工声明的次数与原因')

console.log('attendanceRecordUiContract.test.ts: ok')
