import {
  buildAttendanceRecordSummary,
  buildLocalPunchAdjustmentPreview,
  validateOvertimeApproval,
  getPunchAdjustmentOptions,
  deriveOriginalPunchGuid,
  getPunchAdjustmentPayloadSnapshot,
  isLatestMatchingPunchAdjustmentPreview,
  getDefaultPunchAdjustmentMode,
  resolvePunchAdjustmentOriginalGuid,
  canAdjustOwnAttendanceRecord,
  getProposedAdjustmentPunchStatus,
  isKnownAttendanceApprovalSourceType,
  getSupplementalAttendanceApprovalDetail,
  getAttendanceWeekdayIndex,
  insertAttendanceWeekday,
  insertAttendanceWeekdaysInText,
  validateMealBreakApproval,
  buildAttendanceMealPaySummary,
} from './attendanceRecordLogic'
import type { AttendanceScheduleDto } from '../../../types/scheduleAttendance'

function assertEqual<T>(actual: T, expected: T, message: string) {
  if (actual !== expected) {
    throw new Error(`${message}: expected ${String(expected)}, got ${String(actual)}`)
  }
}

assertEqual(getAttendanceWeekdayIndex('2026-07-28'), 1, '工作日期应映射到周二的本地化索引')
assertEqual(getAttendanceWeekdayIndex('2026-08-02'), 6, '周日应映射到每周最后一个本地化索引')
assertEqual(getAttendanceWeekdayIndex('2026-08-03'), 0, '跨月后的周一应映射到每周第一个本地化索引')
assertEqual(getAttendanceWeekdayIndex('2026-02-30'), undefined, '无效日历日期不应显示星期')
assertEqual(getAttendanceWeekdayIndex('not-a-date'), undefined, '无效日期文本不应显示星期')
assertEqual(getAttendanceWeekdayIndex(undefined), undefined, '缺少工作日期时不应显示星期')
assertEqual(
  insertAttendanceWeekday('2026-08-24 00:15', '周一'),
  '2026-08-24 周一 00:15',
  'UTC 时间跨午夜转换为门店本地时间后，应按展示日期插入星期',
)
assertEqual(insertAttendanceWeekday('2026-08-24', 'Mon'), '2026-08-24 Mon', '英文星期应插入日期之后')
assertEqual(insertAttendanceWeekday('--', '周一'), '--', '空日期占位符不应附加星期')
assertEqual(insertAttendanceWeekday('invalid', '周一'), 'invalid', '无法识别日期前缀时应保留原始显示值')
assertEqual(
  insertAttendanceWeekdaysInText(
    '2026-07-23 – 2026-07-25 · Family care',
    (value) => {
      const weekdayIndex = getAttendanceWeekdayIndex(value)
      return weekdayIndex === undefined ? undefined : ['周一', '周二', '周三', '周四', '周五', '周六', '周日'][weekdayIndex]
    },
  ),
  '2026-07-23 周四 – 2026-07-25 周六 · Family care',
  '审核详情中的业务日期范围应逐个附加星期且保留其他原始文本',
)

for (const sourceType of ['Punch', 'Leave', 'PunchAdjustment', 'Overtime', 'MissingClockOut']) {
  assertEqual(isKnownAttendanceApprovalSourceType(sourceType), true, `${sourceType} 应使用本地化审批文案`)
}
assertEqual(isKnownAttendanceApprovalSourceType('CustomApproval'), false, '未知来源应保留后端标题与明细回退')
assertEqual(getSupplementalAttendanceApprovalDetail({
  sourceType: 'Punch',
  displayedTitle: 'Punch exception',
  detail: 'ClockIn · Late · Traffic delay',
}), 'ClockIn · Late · Traffic delay', '打卡 DTO 中的类型、状态和用户原因不得被吞掉')
assertEqual(getSupplementalAttendanceApprovalDetail({
  sourceType: 'Leave',
  displayedTitle: 'Leave request',
  detail: '2026-07-23 – 2026-07-25 · Family care',
}), '2026-07-23 – 2026-07-25 · Family care', '请假 DTO 中的完整日期范围和用户原因不得被吞掉')
assertEqual(getSupplementalAttendanceApprovalDetail({
  sourceType: 'Leave',
  displayedTitle: 'Leave request',
  detail: ' Leave request ',
}), undefined, '与展示标题重复的明细不得重复渲染')
assertEqual(getSupplementalAttendanceApprovalDetail({
  sourceType: 'MissingClockOut',
  displayedTitle: 'Missing clock-out',
  detail: 'backend system detail',
}), undefined, '新类型不得恢复后端硬编码系统说明')

const multiSegmentSchedule: AttendanceScheduleDto = {
  scheduleGuid: 'schedule-1',
  storeCode: 'S001',
  userGuid: 'user-1',
  workDate: '2026-07-21',
  startTime: '09:00',
  endTime: '17:00',
  status: 'Active',
  workedMinutes: 495,
  breakMinutes: 45,
  candidateOvertimeMinutes: 45,
  approvedOvertimeMinutes: 15,
  segments: [
    {
      segmentIndex: 1,
      clockIn: '2026-07-21T08:30:00+10:00',
      clockOut: '2026-07-21T12:00:00+10:00',
      durationMinutes: 210,
      status: 'Completed',
    },
    {
      segmentIndex: 2,
      clockIn: '2026-07-21T12:45:00+10:00',
      clockOut: '2026-07-21T17:30:00+10:00',
      durationMinutes: 285,
      status: 'Completed',
    },
  ],
}

const summary = buildAttendanceRecordSummary(multiSegmentSchedule)
assertEqual(summary.firstClockIn, '2026-07-21T08:30:00+10:00', '迟到边界应使用首个上班时间')
assertEqual(summary.finalClockOut, '2026-07-21T17:30:00+10:00', '早退边界应使用最终下班时间')
assertEqual(summary.workedMinutes, 495, '工时应累加各班段且排除间隔')
assertEqual(summary.breakMinutes, 45, '班段之间的间隔应单独展示')
assertEqual(summary.candidateOvertimeMinutes, 45, '应保留候选加班')
assertEqual(summary.approvedOvertimeMinutes, 15, '应保留批准加班')

const adjustmentPreview = buildLocalPunchAdjustmentPreview(
  multiSegmentSchedule,
  'ClockOut',
  '2026-07-21T18:00:00+10:00',
  'replace',
)
assertEqual(adjustmentPreview.originalPunchTimeLocal, '2026-07-21T17:30:00+10:00', '预览应显示原始边界时间')
assertEqual(adjustmentPreview.afterWorkedMinutes, 525, '延后最终下班应增加有效工时')
assertEqual(adjustmentPreview.afterCandidateOvertimeMinutes, 90, '预览应重算早到与晚退候选加班')
assertEqual(adjustmentPreview.overtimeMinutesDelta, 45, '预览应显示自动加班变化')

const scheduleWithPunches: AttendanceScheduleDto = {
  ...multiSegmentSchedule,
  segments: [
    {
      segmentIndex: 1,
      status: 'Completed',
      clockIn: {
        punchGuid: 'in-1',
        storeCode: 'S001',
        userGuid: 'user-1',
        workDate: '2026-07-21',
        punchType: 'ClockIn',
        punchTimeLocal: '2026-07-21T08:30:00+10:00',
        status: 'Normal',
      },
      clockOut: {
        punchGuid: 'out-1',
        storeCode: 'S001',
        userGuid: 'user-1',
        workDate: '2026-07-21',
        punchType: 'ClockOut',
        punchTimeLocal: '2026-07-21T12:00:00+10:00',
        status: 'Normal',
      },
    },
    {
      segmentIndex: 2,
      status: 'Completed',
      clockIn: {
        punchGuid: 'in-2',
        storeCode: 'S001',
        userGuid: 'user-1',
        workDate: '2026-07-21',
        punchType: 'ClockIn',
        punchTimeLocal: '2026-07-21T12:45:00+10:00',
        status: 'Normal',
      },
      clockOut: {
        punchGuid: 'out-2',
        storeCode: 'S001',
        userGuid: 'user-1',
        workDate: '2026-07-21',
        punchType: 'ClockOut',
        punchTimeLocal: '2026-07-21T17:30:00+10:00',
        status: 'Normal',
      },
    },
  ],
}
assertEqual(getPunchAdjustmentOptions(scheduleWithPunches, 'ClockOut').length, 2, '纠正已有下班卡时应列出所有班段 punch')
assertEqual(deriveOriginalPunchGuid(scheduleWithPunches, 'ClockIn'), 'in-1', '上班纠正默认关联首个原始 punch')
assertEqual(deriveOriginalPunchGuid(scheduleWithPunches, 'ClockOut'), 'out-2', '下班纠正默认关联最终原始 punch')
assertEqual(
  deriveOriginalPunchGuid({ ...scheduleWithPunches, segments: [{ segmentIndex: 1, clockIn: scheduleWithPunches.segments?.[0]?.clockIn }] }, 'ClockOut'),
  undefined,
  '新增缺失下班卡时才允许 originalPunchGuid 为空',
)

const previewPayload = {
  storeCode: 'S001',
  scheduleGuid: 'schedule-1',
  originalPunchGuid: 'out-2',
  punchType: 'ClockOut' as const,
  requestedPunchTimeLocal: '2026-07-21T17:45:00',
  reason: '修正下班时间',
}
const previewSnapshot = getPunchAdjustmentPayloadSnapshot(previewPayload)
assertEqual(
  isLatestMatchingPunchAdjustmentPreview({
    requestId: 2,
    latestRequestId: 2,
    previewPayloadSnapshot: previewSnapshot,
    currentPayloadSnapshot: previewSnapshot,
  }),
  true,
  '仅最新且 payload 完全一致的 preview 可以落地',
)
assertEqual(
  isLatestMatchingPunchAdjustmentPreview({
    requestId: 1,
    latestRequestId: 2,
    previewPayloadSnapshot: previewSnapshot,
    currentPayloadSnapshot: previewSnapshot,
  }),
  false,
  '旧 preview 响应不得覆盖新请求',
)
assertEqual(
  isLatestMatchingPunchAdjustmentPreview({
    requestId: 2,
    latestRequestId: 2,
    previewPayloadSnapshot: previewSnapshot,
    currentPayloadSnapshot: getPunchAdjustmentPayloadSnapshot({ ...previewPayload, originalPunchGuid: 'out-1' }),
  }),
  false,
  'originalPunchGuid 或其他 payload 字段变化后旧 preview 必须失效',
)

const openSecondSegmentSchedule: AttendanceScheduleDto = {
  ...scheduleWithPunches,
  hasMissingClockOut: true,
  segments: [
    scheduleWithPunches.segments?.[0]!,
    {
      segmentIndex: 2,
      status: 'Open',
      clockIn: scheduleWithPunches.segments?.[1]?.clockIn,
    },
  ],
}
assertEqual(
  getPunchAdjustmentOptions(openSecondSegmentSchedule, 'ClockOut').map((option) => option.value).join(','),
  'out-1',
  '第二段漏最终下班时列表仍可提供第一段 break out 供显式纠正',
)
assertEqual(
  getDefaultPunchAdjustmentMode(openSecondSegmentSchedule, 'ClockOut'),
  'create',
  '第二段漏最终下班时默认必须是新增打卡，不能 supersede 第一段 break out',
)
assertEqual(
  resolvePunchAdjustmentOriginalGuid('create', 'out-1'),
  undefined,
  '新增打卡模式即使表单残留旧 GUID 也必须发送空 originalPunchGuid',
)
assertEqual(
  getPunchAdjustmentPayloadSnapshot({ ...previewPayload, originalPunchGuid: resolvePunchAdjustmentOriginalGuid('create', 'out-1') }).includes('"originalPunchGuid":null'),
  true,
  '新增最终下班的 preview/create snapshot 应明确包含空 originalPunchGuid',
)
assertEqual(
  resolvePunchAdjustmentOriginalGuid('replace', 'out-1'),
  'out-1',
  '只有显式纠正模式才允许携带原打卡 GUID',
)

const secondClockInPreview = buildLocalPunchAdjustmentPreview(
  scheduleWithPunches,
  'ClockIn',
  '2026-07-21T13:00:00+10:00',
  'replace',
  'in-2',
)
assertEqual(secondClockInPreview.originalPunchTimeLocal, '2026-07-21T12:45:00+10:00', '第二段上班纠正应精确关联所选 punch')
assertEqual(secondClockInPreview.afterWorkedMinutes, 480, '第二段上班延后 15 分钟应只减少该班段工时')
assertEqual(secondClockInPreview.exceptions.includes('Late'), false, '中间上班卡不得误判为首班迟到')

const breakClockOutPreview = buildLocalPunchAdjustmentPreview(
  scheduleWithPunches,
  'ClockOut',
  '2026-07-21T12:15:00+10:00',
  'replace',
  'out-1',
)
assertEqual(breakClockOutPreview.originalPunchTimeLocal, '2026-07-21T12:00:00+10:00', '休息边界下班纠正应精确关联所选 punch')
assertEqual(breakClockOutPreview.afterWorkedMinutes, 510, '中间下班延后 15 分钟应只增加该班段工时')
assertEqual(breakClockOutPreview.exceptions.includes('EarlyLeave'), false, '中间下班卡不得误判为最终早退')

const createClockOutPreview = buildLocalPunchAdjustmentPreview(
  openSecondSegmentSchedule,
  'ClockOut',
  '2026-07-21T17:30:00+10:00',
  'create',
)
assertEqual(createClockOutPreview.originalPunchTimeLocal, undefined, '新增打卡不得伪造原始 punch 时间')
assertEqual(createClockOutPreview.exceptions.includes('MissingClockOutResolved'), true, '新增缺失下班卡应预览漏下班异常解除')

const withinGracePreview = buildLocalPunchAdjustmentPreview(
  scheduleWithPunches,
  'ClockIn',
  '2026-07-21T09:03:00+10:00',
  'replace',
  'in-1',
)
assertEqual(withinGracePreview.exceptions.includes('Late'), false, '本地预览不知道服务端 grace，不得把 09:03 推断为迟到')
assertEqual(getProposedAdjustmentPunchStatus({
  isValid: true,
  workedMinutesDelta: -3,
  candidateOvertimeMinutesDelta: 0,
  wouldAutoApprove: false,
  proposedSession: {
    workedMinutes: 492,
    breakMinutes: 45,
    candidateOvertimeMinutes: 45,
    segments: [{
      segmentIndex: 1,
      status: 'Completed',
      clockIn: {
        punchGuid: 'preview-in',
        storeCode: 'S001',
        userGuid: 'user-1',
        workDate: '2026-07-21',
        punchType: 'ClockIn',
        punchTimeLocal: '2026-07-21T09:03:00+10:00',
        status: 'Normal',
      },
    }],
  },
}, 'ClockIn', '2026-07-21T09:03:00'), 'Normal', '09:03 在 grace 内必须展示服务端 proposed punch 的 Normal 状态')

assertEqual(canAdjustOwnAttendanceRecord({ isAdmin: true, isStoreManager: false, isOwnSchedule: true, isManagedStore: false }), true, '纯 Admin 可补自己的考勤')
assertEqual(canAdjustOwnAttendanceRecord({ isAdmin: true, isStoreManager: false, isOwnSchedule: false, isManagedStore: false }), false, 'Admin 自助入口仍只处理本人考勤')
assertEqual(canAdjustOwnAttendanceRecord({ isAdmin: false, isStoreManager: true, isOwnSchedule: true, isManagedStore: true }), true, 'StoreManager 可补本人且所属管理分店的考勤')
assertEqual(canAdjustOwnAttendanceRecord({ isAdmin: false, isStoreManager: true, isOwnSchedule: true, isManagedStore: false }), false, 'StoreManager 不得补非管理分店考勤')
assertEqual(canAdjustOwnAttendanceRecord({ isAdmin: false, isStoreManager: true, isOwnSchedule: false, isManagedStore: true }), false, 'StoreManager 自助入口不得补他人考勤')

assertEqual(
  validateOvertimeApproval({ candidateMinutes: 45, approvedMinutes: 45, action: 'approve' }),
  null,
  '全额批准无需备注',
)
assertEqual(
  validateOvertimeApproval({ candidateMinutes: 45, approvedMinutes: 30, action: 'approve' }),
  'remarkRequired',
  '减少候选加班必须备注',
)
assertEqual(
  validateOvertimeApproval({ candidateMinutes: 45, approvedMinutes: 30, action: 'approve', remark: '仅批准实际工作部分' }),
  null,
  '减少候选加班并填写备注应通过',
)
assertEqual(
  validateOvertimeApproval({ candidateMinutes: 45, approvedMinutes: 10, action: 'approve', remark: '说明' }),
  'invalidIncrement',
  '批准分钟必须为 15 分钟倍数',
)
assertEqual(
  validateOvertimeApproval({ candidateMinutes: 45, approvedMinutes: 60, action: 'approve', remark: '说明' }),
  'outOfRange',
  '批准分钟不得超过候选范围',
)
assertEqual(
  validateOvertimeApproval({ candidateMinutes: 45, approvedMinutes: 0, action: 'reject' }),
  'remarkRequired',
  '拒绝加班必须备注',
)

// 用餐未休息加工时
assertEqual(isKnownAttendanceApprovalSourceType('MealBreak'), true, 'MealBreak 应走本地化展示文案')
assertEqual(validateMealBreakApproval({ action: 'approve' }), null, '批准用餐加工时不需要备注')
assertEqual(validateMealBreakApproval({ action: 'reject', remark: ' ' }), 'remarkRequired', '拒绝用餐加工时必须备注')
assertEqual(validateMealBreakApproval({ action: 'reject', remark: '当天有轮休' }), null, '填写备注后可以拒绝')

const mealBaseSchedule: AttendanceScheduleDto = {
  scheduleGuid: 'meal-schedule',
  storeCode: 'S001',
  userGuid: 'user-1',
  workDate: '2026-05-18',
  startTime: '09:00',
  endTime: '17:00',
  status: 'Active',
}
assertEqual(buildAttendanceMealPaySummary(mealBaseSchedule), undefined, '旧后端没有用餐字段时单元格留空')
assertEqual(
  buildAttendanceMealPaySummary({ ...mealBaseSchedule, mealDeductionMinutes: 0, paidMinutes: 480 }),
  undefined,
  '排班不用餐且无加回申请时不显示',
)
const mealSummary = buildAttendanceMealPaySummary({
  ...mealBaseSchedule,
  mealDeductionMinutes: 30,
  pendingMealAddBackMinutes: 30,
  approvedMealAddBackMinutes: 0,
  paidMinutes: 490,
})
assertEqual(mealSummary?.deductionMinutes, 30, '应展示用餐扣除分钟')
assertEqual(mealSummary?.pendingAddBackMinutes, 30, '应展示待审加回分钟')
assertEqual(mealSummary?.approvedAddBackMinutes, 0, '未批准时加回为 0')
assertEqual(mealSummary?.paidMinutes, 490, '应展示后端算好的计薪工时')

console.log('attendanceRecordLogic.test.ts: ok')
