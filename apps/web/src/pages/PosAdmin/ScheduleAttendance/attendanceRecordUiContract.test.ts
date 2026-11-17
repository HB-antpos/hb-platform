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

// 员工、分店在编辑时不能直接改（后端 UpdateSchedule 不改这两项）：旧的「新建 / 编辑排班」抽屉已移除，
// 编辑只走浮层（沿用原员工），换人走「新建给对方 + 删除原班次」；可上班时间按日期范围查询、列表显示后端回填的员工名。
assert(!pageSource.includes("drawer.createSchedule") && !pageSource.includes('openScheduleDrawer'), '旧的新建 / 编辑排班抽屉应已移除')
assert(
  /await updateAttendanceSchedule\(schedule\.scheduleGuid, payload\)/.test(pageSource) && /userGuid: row\.userGuid,/.test(pageSource),
  '编辑已有班次应沿用原员工走更新接口',
)
assert(
  pageSource.includes('getAttendanceAvailability({ ...queryParams(page, pageSize), weekStartDate: undefined })'),
  '可上班时间应按 fromDate / toDate 查询，不再带 weekStartDate',
)

// 周排班（按天分列）拖拽 / 复制 / 托盘拖到某天 / 右上角「×」：源码接线契约（交互本身靠目测与逻辑单测）
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
  // 托盘里的常用班次不再直接建班：落到某一天只打开排班弹窗（预选这一天和这个时段），由弹窗预览后再创建
  assert(!runDropSource.includes('buildPresetPayload('), '托盘拖放不应绕过弹窗直接建班')
  assert(runDropSource.includes('restoreAttendanceSchedule(origin.scheduleGuid)'), '换员工的撤销要把原班次恢复回来')
}
assert(pageSource.includes('draggable={canDrag}'), '班次块只有 canDrag 时才可拖')
assert(pageSource.includes('isScheduleLocked({'), '已有打卡记录的班次要锁定')
assert(pageSource.includes('schedule.status !== \'Cancelled\' && !locked'), '已取消 / 已锁定的班次不可拖')
assert(pageSource.includes('className="sce-card-x"'), '班次卡片右上角要有「×」')
assert(/<Popconfirm[\s\S]{0,400}onConfirm=\{\(\) => deleteScheduleViaX\(schedule\)\}/.test(pageSource), '「×」要先弹删除确认')
assert(pageSource.includes('onClick={(event) => event.stopPropagation()}'), '点「×」不能触发外层的编辑浮层')
assert(pageSource.includes('SHIFT_PRESETS.map((preset) => ('), '拖拽托盘使用固定的 5 个常用班次')
assert(/getDropVerdict\(\s*source,/.test(pageSource), '每一天的列要按落点判定显示可放 / 提醒 / 不能放')
assert(pageSource.includes('openBatchModal({ dateKeys: [dateKey], start: source.start, end: source.end })'), '托盘常用班次落到某天要打开排班弹窗并预填')
assert(pageSource.includes("window.setTimeout(() => {\n      setAltCopy(alt)"), '拖动开始要异步改界面，避免浏览器取消拖动')
assert(!pageSource.includes('buildShiftPresets') && !pageSource.includes('pickDefaultShift'), '常用班次不再按本周班次推导')

// 按天分列的周视图：不再有员工行，每天一列；卡片按「开始时间 → 员工名」排序，一人一色
{
  assert(pageSource.includes('<WeekBoard'), '周排班应使用按天分列的看板')
  assert(!pageSource.includes('scheduleColumns') && !pageSource.includes('renderScheduleCell'), '周排班不应再用员工行的表格')
  assert(!pageSource.includes('filterRowsByKeyword'), '搜索员工只做高亮，不再过滤出员工行')
  assert(pageSource.includes('groupSchedulesByDay(weekSchedules, weekDateKeys, boardNameOf)'), '每天的班次要按时间、看板上显示的名字排序后再渲染')
  assert(pageSource.includes('assignStaffColors('), '员工颜色统一由 assignStaffColors 分配')
  // 员工名显示切换（昵称 / 全名）：只影响看板（图例 + 卡片 + 排序）；颜色、搜索、弹窗仍用全名
  assert(pageSource.includes('readStaffNameMode(getBrowserStorage())') && pageSource.includes('writeStaffNameMode(storage, next)'), '员工名显示方式要按浏览器记住')
  assert(pageSource.includes('buildStaffLegend(weekSchedules, boardNameOf)'), '图例要用看板当前显示的名字')
  assert(pageSource.includes('const name = boardNameOf(schedule.userGuid)'), '班次卡片要用看板当前显示的名字')
  assert(pageSource.includes('assignStaffColors(Array.from(userNameMap'), '员工颜色仍按全名分配，切换开关不能换色')
  assert(pageSource.includes('name: displayStoreUserName(user)') && pageSource.includes('label: displayStoreUserName(user)'), '批量排班弹窗、换人下拉仍用全名，避免选错人')
  assert(pageSource.includes('...Array.from(nicknameNames'), '搜索要同时匹配全名和昵称')
  // 员工管理列表（store-users/grid）会剔除其他店长：店长的昵称 / 名只能从可排班员工接口取，不能只拿它的年龄
  assert(pageSource.includes('mergeStaffNameSources('), '看板显示名的原料要合并员工管理列表与可排班员工（店长只在后者里）')
  assert(pageSource.includes('setEmployeeNameSources(employees.map('), '可排班员工的姓名字段要保留下来，不能只取年龄')
  assert((pageSource.match(/<Switch (size="small" )?\{\.\.\.staffNameSwitchProps\}/g) ?? []).length === 2, '工具栏和手机横屏顶栏各有一个员工名开关')
  // 看板工时一律显示扣除用餐后的计薪时长（与手机端同一口径：满 4.5 小时自动扣 30 分钟）；总时长 / 扣餐分钟放进悬停提示
  assert(pageSource.includes('formatHours(paidHours)'), '班次卡片显示扣除用餐后的计薪时长')
  assert(pageSource.includes('formatHours(stat.paidHours)'), '每天列头合计显示计薪时长')
  assert(pageSource.includes('formatHours(entry.paidHours)'), '图例合计显示计薪时长')
  assert(pageSource.includes('weekHours: total.paidHours'), '批量排班弹窗里「本周已排」也用计薪时长')
  assert(!pageSource.includes('getScheduleHours('), '看板不再用不扣用餐的班次时长')
  assert(pageSource.includes("board.cardHoursTip'"), '卡片悬停要写明「班次 − 用餐 = 计薪」')
  {
    const boardKeys = (file: string) => Object.keys(JSON.parse(readFileSync(path.resolve(process.cwd(), 'src/pages/PosAdmin/ScheduleAttendance', file), 'utf8')).posAdmin.scheduleAttendance.board)
    const zhBoard = boardKeys('scheduleAttendanceMessages.zh.json')
    const enBoard = boardKeys('scheduleAttendanceMessages.en.json')
    for (const key of ['nameModeLabel', 'nameModeNickname', 'nameModeFull', 'nameModeTipNickname', 'nameModeTipFull', 'nameModeTipRemember', 'nameAltFull', 'nameAltNickname', 'cardHoursTip']) {
      assert(zhBoard.includes(key) && enBoard.includes(key), `员工名切换文案 ${key} 中英文都要有`)
    }
  }
  // 编辑浮层「改给其他员工」复用拖拽换人的落地逻辑（新建给对方 + 删除原班次，可撤销）
  const transferStart = pageSource.indexOf('const transferSchedule = async')
  const transferEnd = pageSource.indexOf('const deleteScheduleViaX', transferStart)
  assert(transferStart >= 0 && transferEnd > transferStart, '应能定位换人逻辑')
  assert(/await runDrop\(\s*\{ kind: 'move', schedule,/.test(pageSource.slice(transferStart, transferEnd)), '换人要复用拖拽移动的落地逻辑')
  assert(pageSource.includes('onTransfer: (targetUserGuid: string) => void transferSchedule(schedule, targetUserGuid)'), '编辑浮层要接上换人')
  // 工具栏入口改为批量排班弹窗；弹窗创建成功后刷新周视图
  assert(pageSource.includes('onClick={() => openBatchModal()}'), '工具栏「排班」按钮应打开批量排班弹窗')
  assert(pageSource.includes('<BatchScheduleModal'), '页面应挂载批量排班弹窗')
}

// 手机横屏看板：手机横过来自动进入，竖着拿点按钮进入；旋转兜底模式只读（浮层、弹窗不会跟着旋转）
{
  assert(pageSource.includes('getShellMode({ phone: phoneEnv.phone, landscape: phoneEnv.landscape, requested: shellRequested, autoDismissed: shellDismissed })'), '横屏看板的显示方式应由 getShellMode 决定')
  assert(pageSource.includes('<LandscapeShell mode={shellMode}>'), '页面应挂载手机横屏看板外壳')
  assert(pageSource.includes("const shellReadOnly = shellMode === 'rotated'"), '旋转兜底模式应只读')
  assert(pageSource.includes('const canEditSchedule = !shellReadOnly &&'), '只读模式下卡片不能点开编辑')
  assert(pageSource.includes('const canAdd = !shellReadOnly &&'), '只读模式下不显示添加班次入口')
  assert(pageSource.includes('void tryLockLandscape()'), '点「横屏查看整周」要尝试锁定横屏')
  assert(pageSource.includes('releaseLandscape()'), '退出看板要还原方向锁定与全屏')
}


// 分店改为左侧列表（宽屏常驻，窄屏收起成工具栏按钮），周切换为「‹ 本周 ›」+ 周范围
{
  assert(!pageSource.includes("placeholder={t('posAdmin.scheduleAttendance.filters.store')}"), '分店不再用下拉框选择')
  assert(pageSource.includes('<StorePicker\n              variant="docked"'), '宽屏应有常驻左侧的分店列表')
  assert(pageSource.includes('<StorePicker\n          variant="floating"'), '侧栏收起后分店按钮应弹出同一份分店列表')
  assert(pageSource.includes('title={storeSwitcher ?? ('), '侧栏收起时分店按钮放在卡片标题位置')
  assert(pageSource.includes("const storeSidebarDocked = activeTab !== 'review' && shouldDockSidebar({"), '侧栏是否展开由 shouldDockSidebar 决定，考勤审核页签不显示')
  assert(pageSource.includes('onSelect={selectStore}'), '选分店要经过 selectStore（记住上次分店与最近使用）')
  assert(pageSource.includes('resolveInitialStore(storeOptions, safeStorageGet(storage, STORAGE_KEYS.lastStore))'), '进入页面要恢复上次选的分店')
  assert(pageSource.includes("t('posAdmin.scheduleAttendance.weekNav.thisWeek')"), '周切换应有「本周」按钮')
  assert(pageSource.includes('onClick={() => setSelectedWeek(dayjs())}'), '「本周」按钮回到当前周')
  assert(pageSource.includes('format={(value) => formatWeekRange(value)}'), '周选择器显示周范围')
  assert(!pageSource.includes('<Tag color="blue" style={{ lineHeight'), '原来的蓝色周标签已并入周范围')
}

// 表格页签（考勤记录 / 员工可用时间 / 打卡记录）：左侧分店列表更早展开 + 员工下拉筛选 + 员工列显示昵称 / 全名
{
  // 左侧分店列表：周排班仍按 7 天看板的阈值，其它页签放宽
  assert(
    pageSource.includes("dockMinWidth: activeTab === 'schedules' ? STORE_SIDEBAR_DOCK_MIN_WIDTH : STORE_SIDEBAR_TABLE_DOCK_MIN_WIDTH"),
    '周排班用 7 天看板的阈值，表格页签用放宽后的阈值',
  )
  // 员工筛选：取代「员工 GUID」输入框；只在按员工查询的三个页签出现，选中即查询
  assert(!pageSource.includes("placeholder={t('posAdmin.scheduleAttendance.filters.userGuid')}"), '员工 GUID 输入框已被员工下拉取代')
  assert(
    pageSource.includes("const employeeFilterActive = activeTab === 'records' || activeTab === 'availability' || activeTab === 'punches'"),
    '员工下拉只在考勤记录 / 员工可用时间 / 打卡记录页签出现',
  )
  assert(pageSource.includes(') : employeeFilterActive ? employeeSelect : null}'), '筛选栏里非周排班页签放员工下拉')
  assert(pageSource.includes('filterOption={(input, option) => matchesEmployeeKeyword('), '员工下拉要同时匹配昵称 / 名 / 全名 / 登录名 / GUID')
  assert(pageSource.includes('disabled={!storeCode}'), '全部分店没有员工名单，员工下拉要置灰')
  assert(pageSource.includes('}, [activeTab, storeCode, dateRange, selectedWeek, userGuid])'), '选中 / 清除员工后要立即重新查询')
  // 员工名开关：周排班与这三个页签共用同一个开关（工具栏里只有一个 <Switch>，横屏顶栏另有一个）
  assert(pageSource.includes("{activeTab === 'schedules' || employeeFilterActive ? ("), '员工名开关也要出现在员工筛选页签')
  // 换店清掉上一家店选的员工
  {
    const selectStoreStart = pageSource.indexOf('const selectStore = useCallback(')
    const selectStoreEnd = pageSource.indexOf('const setSidebarDock', selectStoreStart)
    assert(selectStoreStart >= 0 && selectStoreEnd > selectStoreStart, '应能定位 selectStore')
    const selectStoreSource = pageSource.slice(selectStoreStart, selectStoreEnd)
    assert(selectStoreSource.includes('setUserGuid(undefined)') && selectStoreSource.includes('employeeFilter.storeChanged'), '换到另一家店要清掉已选员工并提示')
    assert(selectStoreSource.includes('code !== storeCode && userGuid'), '只有真的换了店、且选过员工才清除')
  }
  // 员工名单：非周排班页签按分店加载一次，已加载过的分店不重复请求；快速换店时旧响应不能覆盖新名单
  assert(
    pageSource.includes("if (activeTab !== 'records' && activeTab !== 'availability' && activeTab !== 'punches') return"),
    '员工名单要在员工筛选页签里加载',
  )
  assert(pageSource.includes("if (storeUsersKeyRef.current === (storeCode ?? '')) return"), '已加载过的分店不重复请求员工名单')
  assert(pageSource.includes('if (requestId !== storeUsersRequestRef.current) return'), '旧分店的员工名单响应要丢弃')
  // 员工列：显示名跟开关走，名字取不到显示 GUID 前 8 位；完整 GUID 在悬停提示里可复制
  assert(pageSource.includes('resolveEmployeeCell({'), '员工列要按开关显示昵称 / 全名')
  assert(pageSource.includes('copyable={{ text: guid, tooltips: false }}'), '完整 GUID 放进悬停提示并可复制')
  assert(!pageSource.includes('{record.userGuid || record.applicantUserGuid ||'), '员工列不再直接铺两行 GUID')
  // 选了单店后隐藏「分店」列（整列都是同一家店），选「全部分店」时保留
  assert(recordColumnsSource.includes("key: 'store'") && punchColumnsSource.includes("key: 'store'"), '考勤记录 / 打卡记录的分店列要有 key 才能被隐藏')
  assert(pageSource.includes('columns={hideStoreColumn(recordColumns)}') && pageSource.includes('columns={hideStoreColumn(punchColumns)}'), '选了单店要隐藏分店列')
  assert(pageSource.includes('(storeCode ? columns.filter((column) => column.key !== \'store\') : columns)'), '只有选了单店才隐藏分店列')
  // 文案中英文都要有
  {
    const filterKeys = (file: string) => Object.keys(JSON.parse(readFileSync(path.resolve(process.cwd(), 'src/pages/PosAdmin/ScheduleAttendance', file), 'utf8')).posAdmin.scheduleAttendance.employeeFilter)
    const zhKeys = filterKeys('scheduleAttendanceMessages.zh.json')
    const enKeys = filterKeys('scheduleAttendanceMessages.en.json')
    for (const key of ['placeholder', 'needStore', 'empty', 'storeStaff', 'matchCount', 'storeChanged', 'notInRoster']) {
      assert(zhKeys.includes(key) && enKeys.includes(key), `员工筛选文案 ${key} 中英文都要有`)
    }
    assert(pageSource.includes("t(`posAdmin.scheduleAttendance.board.${display.alt.kind === 'full' ? 'nameAltFull' : 'nameAltNickname'}`"), '员工列第二行复用看板的「全名：/ 昵称：」文案')
  }
}
