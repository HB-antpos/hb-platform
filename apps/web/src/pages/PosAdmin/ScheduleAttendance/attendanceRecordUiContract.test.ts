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
const recordColumnsStart = pageSource.indexOf('const recordColumns:')
const availabilityColumnsStart = pageSource.indexOf('const availabilityColumns:', recordColumnsStart)
assert(recordColumnsStart >= 0 && availabilityColumnsStart > recordColumnsStart, '应能定位考勤记录表格定义')
const recordColumnsSource = pageSource.slice(recordColumnsStart, availabilityColumnsStart)
const punchColumnsStart = pageSource.indexOf('const punchColumns:')
const holidayColumnsStart = pageSource.indexOf('const holidayColumns:', punchColumnsStart)
assert(punchColumnsStart >= 0 && holidayColumnsStart > punchColumnsStart, '应能定位打卡记录表格定义')
const punchColumnsSource = pageSource.slice(punchColumnsStart, holidayColumnsStart)
const adjustmentHandlersStart = pageSource.indexOf('const openPunchAdjustmentModal =')
const saveSettingsStart = pageSource.indexOf('const saveSettings =', adjustmentHandlersStart)
assert(adjustmentHandlersStart >= 0 && saveSettingsStart > adjustmentHandlersStart, '应能定位补卡业务处理逻辑')
const adjustmentHandlersSource = pageSource.slice(adjustmentHandlersStart, saveSettingsStart)
const adjustmentModalStart = pageSource.indexOf("title={t('posAdmin.scheduleAttendance.drawer.adjustPunch")
// 审核弹窗已随审核页签迁到「考勤审核」页，补卡弹窗现在是页面最后一个弹窗。
const adjustmentModalEnd = pageSource.lastIndexOf('</Card>')
assert(adjustmentModalStart >= 0 && adjustmentModalEnd > adjustmentModalStart, '应能定位补卡修改弹窗')
const adjustmentModalSource = pageSource.slice(adjustmentModalStart, adjustmentModalEnd)
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
assert(pageSource.includes("{formatDateTime(settings.updatedAt)}"), '最后更新时间属于审计字段，不应附加星期')
assert(pageSource.includes('formatBusinessDisplayText(formatDateTimeInTimeZone('), '门店时区时间应先转换再按展示日期附加星期')
assert(pageSource.includes('formatBusinessDisplayText(formatStoredLocalDateTime('), '存储的门店本地时间应按本地日期附加星期')
assert(!recordColumnsSource.match(/[\u3400-\u9fff]/), '考勤记录表格不应残留用户可见的硬编码中文')
assert(!adjustmentHandlersSource.match(/[\u3400-\u9fff]/), '补卡业务提示不应依赖硬编码中文回退')
assert(!adjustmentModalSource.match(/[\u3400-\u9fff]/), '补卡修改弹窗不应残留用户可见的硬编码中文')
assert(!pageSource.includes("tabs.records', '考勤记录'"), '考勤记录页签名称应完全由 locale 提供')
assert(recordColumnsSource.includes("statusLabel('segment', segment.status)"), '班段状态应使用本地化状态文案')
assert(recordColumnsSource.includes("statusLabel('scheduleState', record.scheduleState)"), '考勤排班状态应使用本地化状态文案')
assert(recordColumnsSource.includes("statusLabel('overtimeApproval', record.overtimeApprovalStatus)"), '加班审批状态应使用本地化状态文案')
// 排班考勤页不再有审核页签：审批统一在「考勤审核」页处理，这里锁住不会回流。
assert(!pageSource.includes("'approvals'"), '排班考勤页不应再有审核页签')
assert(!pageSource.includes('getPendingAttendanceApprovals'), '排班考勤页不应再直接拉取待审核列表')
assert(!pageSource.includes('approveAttendanceApproval') && !pageSource.includes('rejectAttendanceApproval'), '审批提交只能在考勤审核页')
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
  'recordLabels.duration.minutes',
  'recordLabels.duration.hours',
  'recordLabels.duration.hoursMinutes',
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

assert(recordColumnsSource.includes('buildAttendanceMealPaySummary(record)'), '考勤记录应展示用餐扣除、加回与计薪工时')

console.log('attendanceRecordUiContract.test.ts: ok')

// 编辑排班时员工、分店锁定（后端 UpdateSchedule 不改这两项）；可上班时间按日期范围查询、列表显示后端回填的员工名。
assert(
  /name="storeCode"[\s\S]{0,400}disabled=\{Boolean\(editingSchedule\)\}/.test(pageSource),
  '编辑排班时分店应禁用',
)
assert(
  /name="userGuid"[\s\S]{0,400}disabled=\{Boolean\(editingSchedule\)\}/.test(pageSource),
  '编辑排班时员工应禁用',
)
assert(
  pageSource.includes('getAttendanceAvailability({ ...queryParams(page, pageSize), weekStartDate: undefined })'),
  '可上班时间应按 fromDate / toDate 查询，不再带 weekStartDate',
)

// 周排班拖拽 / 复制 / 托盘添加 / 右上角「×」：源码接线契约（交互本身靠目测与逻辑单测）
{
  const dragStart = pageSource.indexOf('const runDrop = async')
  const dragEnd = pageSource.indexOf('const deleteScheduleViaX', dragStart)
  assert(dragStart >= 0 && dragEnd > dragStart, '应能定位拖拽落地逻辑')
  const runDropSource = pageSource.slice(dragStart, dragEnd)
  // 换员工 = 新建 + 删除原班次；新建接口一律建成草稿，有效班次要补一次更新改回有效，且删除在新建之后
  const transferCreate = runDropSource.indexOf('createAttendanceSchedule(buildTransferCreatePayload(')
  const statusRepair = runDropSource.indexOf("origin.status === 'Active'", transferCreate)
  const originDelete = runDropSource.indexOf('deleteAttendanceSchedule(origin.scheduleGuid)', transferCreate)
  assert(transferCreate >= 0 && statusRepair > transferCreate && originDelete > statusRepair, '换员工应先新建、补状态、再删除原班次')
  // 同员工换日期走更新接口；复制与托盘添加走新建；撤销都有对应的回退
  assert(runDropSource.includes('updateAttendanceSchedule(origin.scheduleGuid, buildMoveUpdatePayload(origin, dateKey))'), '同员工换日期应调用更新接口')
  assert(runDropSource.includes('buildCopyPayload(origin, row.userGuid, dateKey)'), '⌥ 复制应新建草稿')
  assert(runDropSource.includes('buildPresetPayload(targetStoreCode, row.userGuid, dateKey'), '托盘添加应新建草稿')
  assert(runDropSource.includes('restoreAttendanceSchedule(origin.scheduleGuid)'), '换员工的撤销要把原班次恢复回来')
}
assert(pageSource.includes('draggable={canDrag}'), '班次块只有 canDrag 时才可拖')
assert(pageSource.includes('isScheduleLocked({'), '已有打卡记录的班次要锁定')
assert(pageSource.includes('schedule.status !== \'Cancelled\' && !locked'), '已取消 / 已锁定的班次不可拖')
assert(pageSource.includes('className="sce-chip-x"'), '班次块右上角要有「×」')
assert(/<Popconfirm[\s\S]{0,400}onConfirm=\{\(\) => deleteScheduleViaX\(schedule\)\}/.test(pageSource), '「×」要先弹删除确认')
assert(pageSource.includes('onClick={(event) => event.stopPropagation()}'), '点「×」不能触发外层的编辑浮层')
assert(pageSource.includes('SHIFT_PRESETS.map((preset) => ('), '拖拽托盘使用固定的 5 个常用班次')
assert(pageSource.includes('getDropVerdict(source,'), '格子要按落点判定显示可放 / 提醒 / 不能放')
assert(pageSource.includes("window.setTimeout(() => {\n      setAltCopy(alt)"), '拖动开始要异步改界面，避免浏览器取消拖动')
assert(!pageSource.includes('buildShiftPresets') && !pageSource.includes('pickDefaultShift'), '常用班次不再按本周班次推导')
