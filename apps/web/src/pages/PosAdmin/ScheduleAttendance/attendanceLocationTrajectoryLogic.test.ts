import {
  buildAttendanceLocationTrajectory,
  normalizeAttendanceUtcText,
  splitAttendanceSampleDateRange,
} from './attendanceLocationTrajectoryLogic'
import type {
  AttendanceLocationSampleDto,
  AttendancePunchDto,
} from '../../../types/scheduleAttendance'

function assertEqual<T>(actual: T, expected: T, message: string) {
  if (actual !== expected) {
    throw new Error(`${message}: expected ${String(expected)}, got ${String(actual)}`)
  }
}

function assertDeepEqual(actual: unknown, expected: unknown, message: string) {
  const actualJson = JSON.stringify(actual)
  const expectedJson = JSON.stringify(expected)
  if (actualJson !== expectedJson) {
    throw new Error(`${message}: expected ${expectedJson}, got ${actualJson}`)
  }
}

function punch(
  punchGuid: string,
  punchType: 'ClockIn' | 'ClockOut',
  punchTimeUtc: string,
  punchTimeLocal: string,
  overrides: Partial<AttendancePunchDto> = {},
): AttendancePunchDto {
  return {
    punchGuid,
    scheduleGuid: 'schedule-1',
    storeCode: 'S001',
    userGuid: 'user-1',
    workDate: '2026-10-04',
    punchType,
    punchTimeUtc,
    punchTimeLocal,
    status: 'Normal',
    ...overrides,
  }
}

function sample(
  sampleGuid: string,
  locationCapturedAtUtc: string,
  locationLatitude: number,
  locationLongitude: number,
  overrides: Partial<AttendanceLocationSampleDto> = {},
): AttendanceLocationSampleDto {
  return {
    sampleGuid,
    userGuid: 'user-1',
    storeCode: 'S001',
    deviceSystem: 'iOS',
    eventType: 'Background',
    locationLatitude,
    locationLongitude,
    locationCapturedAtUtc,
    ...overrides,
  }
}

const firstIn = punch(
  'in-1',
  'ClockIn',
  '2026-10-03T22:00:00.000Z',
  '2026-10-04T09:00:00+11:00',
  { locationLatitude: -33.86, locationLongitude: 151.20, locationAccuracy: 8 },
)
const firstOut = punch(
  'out-1',
  'ClockOut',
  '2026-10-04T01:00:00.000Z',
  '2026-10-04T12:00:00+11:00',
)
const ignoredDuplicateIn = punch(
  'in-duplicate',
  'ClockIn',
  '2026-10-03T23:00:00.000Z',
  '2026-10-04T10:00:00+11:00',
)
const orphanOut = punch(
  'out-orphan',
  'ClockOut',
  '2026-10-04T01:30:00.000Z',
  '2026-10-04T12:30:00+11:00',
)
const secondIn = punch(
  'in-2',
  'ClockIn',
  '2026-10-04T02:00:00.000Z',
  '2026-10-04T13:00:00+11:00',
)
const secondOut = punch(
  'out-2',
  'ClockOut',
  '2026-10-04T06:00:00.000Z',
  '2026-10-04T17:00:00+11:00',
  { locationLatitude: -33.87, locationLongitude: 151.21 },
)

const multiSegmentResult = buildAttendanceLocationTrajectory({
  selectedPunch: secondIn,
  punches: [
    secondOut,
    orphanOut,
    ignoredDuplicateIn,
    firstOut,
    secondIn,
    firstIn,
    punch(
      'other-schedule-in',
      'ClockIn',
      '2026-10-04T03:00:00.000Z',
      '2026-10-04T14:00:00+11:00',
      { scheduleGuid: 'schedule-2' },
    ),
  ],
  samples: [
    sample('whole-day-first-segment', '2026-10-03T23:30:00.000Z', -33.80, 151.10),
    sample('at-second-start', '2026-10-04T02:00:00.000Z', -33.81, 151.11),
    sample('second-middle', '2026-10-04T04:00:00.000Z', -33.82, 151.12),
    sample('at-second-end', '2026-10-04T06:00:00.000Z', -33.83, 151.13),
  ],
  nowUtc: '2026-10-04T07:00:00.000Z',
})
assertEqual(multiSegmentResult.ok, true, '多班段应能定位选中有效卡所在班段')
assertEqual(multiSegmentResult.segmentIndex, 2, '重复上班卡与孤立下班卡不得改变后端配对结果')
assertDeepEqual(
  multiSegmentResult.points.map((point) => point.key),
  ['punch:in-2', 'sample:second-middle', 'punch:out-2'],
  '完成班段只能纳入严格开区间内样本，不能混入整日或边界样本',
)
assertEqual(multiSegmentResult.sampleCount, 1, '样本数应只统计选中班段')
assertDeepEqual(
  multiSegmentResult.mapPoints.map((point) => point.key),
  ['sample:second-middle', 'punch:out-2'],
  '只有带有效坐标的时间线点可以进入地图',
)

const sameTimeFirstIn = punch(
  'm-first-in',
  'ClockIn',
  '2026-10-04T00:00:00.000Z',
  '2026-10-04T11:00:00+11:00',
  { segmentIndex: 1 },
)
const sameTimeFirstOut = punch(
  'z-first-out',
  'ClockOut',
  '2026-10-04T01:00:00.000Z',
  '2026-10-04T12:00:00+11:00',
  { segmentIndex: 1 },
)
const sameTimeSecondIn = punch(
  'a-second-in',
  'ClockIn',
  '2026-10-04T01:00:00.000Z',
  '2026-10-04T12:00:00+11:00',
  { segmentIndex: 2 },
)
const sameTimeSecondOut = punch(
  'n-second-out',
  'ClockOut',
  '2026-10-04T02:00:00.000Z',
  '2026-10-04T13:00:00+11:00',
  { segmentIndex: 2 },
)
const sameTimeBoundaryResult = buildAttendanceLocationTrajectory({
  selectedPunch: sameTimeSecondIn,
  punches: [sameTimeFirstIn, sameTimeFirstOut, sameTimeSecondIn, sameTimeSecondOut],
  samples: [sample('same-time-second-sample', '2026-10-04T01:30:00.000Z', -33.82, 151.12)],
  nowUtc: '2026-10-04T03:00:00.000Z',
})
assertEqual(sameTimeBoundaryResult.ok, true, '同刻下班和下一段上班必须使用后端班段序号')
assertEqual(sameTimeBoundaryResult.segmentIndex, 2, 'GUID 排序不得改变后端已计算的班段')
assertDeepEqual(
  sameTimeBoundaryResult.points.map((point) => point.key),
  ['punch:a-second-in', 'sample:same-time-second-sample', 'punch:n-second-out'],
  '同刻班段边界只能返回选中班段',
)

const ambiguousSameTimeResult = buildAttendanceLocationTrajectory({
  selectedPunch: { ...sameTimeSecondIn, segmentIndex: undefined },
  punches: [
    { ...sameTimeFirstIn, segmentIndex: undefined },
    { ...sameTimeFirstOut, segmentIndex: undefined },
    { ...sameTimeSecondIn, segmentIndex: undefined },
    { ...sameTimeSecondOut, segmentIndex: undefined },
  ],
  samples: [],
  nowUtc: '2026-10-04T03:00:00.000Z',
})
assertEqual(ambiguousSameTimeResult.ok, false, '缺少后端班段序号的同刻边界不得猜测')
assertEqual(ambiguousSameTimeResult.reason, 'SEGMENT_NOT_FOUND', '同刻歧义应返回稳定原因')

const duplicateInResult = buildAttendanceLocationTrajectory({
  selectedPunch: ignoredDuplicateIn,
  punches: [firstIn, ignoredDuplicateIn, firstOut],
  samples: [],
  nowUtc: '2026-10-04T07:00:00.000Z',
})
assertEqual(duplicateInResult.ok, false, '被后端忽略的重复上班卡不能伪装成班段')
assertEqual(duplicateInResult.reason, 'SEGMENT_NOT_FOUND', '重复上班卡应返回稳定原因代码')

const orphanOutResult = buildAttendanceLocationTrajectory({
  selectedPunch: orphanOut,
  punches: [firstIn, firstOut, orphanOut],
  samples: [],
  nowUtc: '2026-10-04T07:00:00.000Z',
})
assertEqual(orphanOutResult.ok, false, '没有开放班段的孤立下班卡不能伪装成班段')
assertEqual(orphanOutResult.reason, 'SEGMENT_NOT_FOUND', '孤立下班卡应返回稳定原因代码')

const originalIn = punch(
  'original-in',
  'ClockIn',
  '2026-10-03T21:45:00.000Z',
  '2026-10-04T08:45:00+11:00',
)
const replacementIn = punch(
  'replacement-in',
  'ClockIn',
  '2026-10-03T21:55:00.000Z',
  '2026-10-04T08:55:00+11:00',
  { supersedesPunchGuid: 'original-in' },
)
const terminalIn = punch(
  'terminal-in',
  'ClockIn',
  '2026-10-03T22:05:00.000Z',
  '2026-10-04T09:05:00+11:00',
  { supersedesPunchGuid: 'replacement-in' },
)
const terminalOut = punch(
  'terminal-out',
  'ClockOut',
  '2026-10-04T01:00:00.000Z',
  '2026-10-04T12:00:00+11:00',
)
const replacementResult = buildAttendanceLocationTrajectory({
  selectedPunch: originalIn,
  punches: [terminalOut, terminalIn, originalIn, replacementIn],
  samples: [],
  nowUtc: '2026-10-04T07:00:00.000Z',
})
assertEqual(replacementResult.ok, true, '选择旧卡时应沿补卡链定位终端有效卡')
assertEqual(replacementResult.clockIn?.punchGuid, 'terminal-in', '被替代卡必须从配对中剔除')
assertDeepEqual(
  replacementResult.points.map((point) => point.key),
  ['punch:terminal-in', 'punch:terminal-out'],
  '时间线只能展示终端有效卡',
)

const cycleA = punch(
  'cycle-a',
  'ClockIn',
  '2026-10-03T22:00:00.000Z',
  '2026-10-04T09:00:00+11:00',
  { supersedesPunchGuid: 'cycle-b' },
)
const cycleB = punch(
  'cycle-b',
  'ClockIn',
  '2026-10-03T22:01:00.000Z',
  '2026-10-04T09:01:00+11:00',
  { supersedesPunchGuid: 'cycle-a' },
)
const cycleResult = buildAttendanceLocationTrajectory({
  selectedPunch: cycleA,
  punches: [cycleA, cycleB],
  samples: [],
  nowUtc: '2026-10-04T07:00:00.000Z',
})
assertEqual(cycleResult.ok, false, '补卡链循环必须安全失败')
assertEqual(cycleResult.reason, 'SUPERSEDE_CYCLE', '循环应返回稳定原因代码')

const missingTarget = punch(
  'missing-target-replacement',
  'ClockIn',
  '2026-10-03T22:00:00.000Z',
  '2026-10-04T09:00:00+11:00',
  { supersedesPunchGuid: 'missing-original' },
)
const missingTargetResult = buildAttendanceLocationTrajectory({
  selectedPunch: missingTarget,
  punches: [missingTarget],
  samples: [],
  nowUtc: '2026-10-04T07:00:00.000Z',
})
assertEqual(missingTargetResult.ok, false, '补卡链缺失目标卡时不能猜测有效班段')
assertEqual(missingTargetResult.reason, 'SUPERSEDE_TARGET_MISSING', '缺失链目标应返回稳定原因代码')

const nullScheduleIn = punch(
  'null-schedule-in',
  'ClockIn',
  '2026-10-03T22:00:00.000Z',
  '2026-10-04T09:00:00+11:00',
  { scheduleGuid: undefined },
)
const nullScheduleOut = punch(
  'null-schedule-out',
  'ClockOut',
  '2026-10-04T01:00:00.000Z',
  '2026-10-04T12:00:00+11:00',
  { scheduleGuid: undefined },
)
const nullScheduleResult = buildAttendanceLocationTrajectory({
  selectedPunch: nullScheduleIn,
  punches: [
    nullScheduleIn,
    nullScheduleOut,
    punch(
      'scheduled-out',
      'ClockOut',
      '2026-10-03T23:00:00.000Z',
      '2026-10-04T10:00:00+11:00',
    ),
  ],
  samples: [],
  nowUtc: '2026-10-04T07:00:00.000Z',
})
assertEqual(nullScheduleResult.ok, true, '无排班打卡应只和无排班打卡分组')
assertEqual(nullScheduleResult.clockOut?.punchGuid, 'null-schedule-out', '无排班组不能混入有排班下班卡')

const dstFirst = punch(
  'dst-first',
  'ClockIn',
  '2026-04-04T15:45:00.000Z',
  '2026-04-05T02:45:00+11:00',
  { workDate: '2026-04-05' },
)
const dstSecond = punch(
  'dst-second',
  'ClockOut',
  '2026-04-04T16:15:00.000Z',
  '2026-04-05T02:15:00+10:00',
  { workDate: '2026-04-05' },
)
const dstResult = buildAttendanceLocationTrajectory({
  selectedPunch: dstFirst,
  punches: [dstSecond, dstFirst],
  samples: [sample('dst-sample', '2026-04-04T16:00:00.000Z', -33.84, 151.14)],
  nowUtc: '2026-04-04T17:00:00.000Z',
})
assertDeepEqual(
  dstResult.points.map((point) => point.key),
  ['punch:dst-first', 'sample:dst-sample', 'punch:dst-second'],
  '夏令时回拨时必须按 UTC 而不是本地墙钟排序和配对',
)

const crossMidnightIn = punch(
  'overnight-in',
  'ClockIn',
  '2026-07-24T12:00:00.000Z',
  '2026-07-24T22:00:00+10:00',
  { workDate: '2026-07-24' },
)
const crossMidnightOut = punch(
  'overnight-out',
  'ClockOut',
  '2026-07-24T18:00:00.000Z',
  '2026-07-25T04:00:00+10:00',
  { workDate: '2026-07-24' },
)
const crossMidnightResult = buildAttendanceLocationTrajectory({
  selectedPunch: crossMidnightOut,
  punches: [crossMidnightOut, crossMidnightIn],
  samples: [],
  nowUtc: '2026-07-24T19:00:00.000Z',
})
assertEqual(crossMidnightResult.fromDate, '2026-07-24', '跨午夜查询应从上班卡本地日期开始')
assertEqual(crossMidnightResult.toDate, '2026-07-25', '跨午夜查询应以下班卡本地日期结束')

const openIn = punch(
  'open-in',
  'ClockIn',
  '2026-07-24T00:00:00.000Z',
  '2026-07-24T10:00:00+10:00',
  { workDate: '2026-07-24', locationLatitude: 91, locationLongitude: 151 },
)
const openResult = buildAttendanceLocationTrajectory({
  selectedPunch: openIn,
  punches: [openIn],
  samples: [
    sample('open-at-start', '2026-07-24T00:00:00.000Z', -33.84, 151.14),
    sample('open-middle', '2026-07-24T00:30:00.000Z', -33.85, 151.15, {
      locationAccuracy: 6,
    }),
    sample('open-at-now', '2026-07-24T01:00:00.000Z', -33.86, 151.16),
    sample('open-after-now', '2026-07-24T01:00:00.001Z', -33.87, 151.17),
    sample('invalid-coordinate', '2026-07-24T00:45:00.000Z', 95, 181),
    sample('missing-coordinate', '2026-07-24T00:50:00.000Z', -33.88, 151.18, {
      locationLatitude: undefined as unknown as number,
    }),
  ],
  nowUtc: '2026-07-24T01:00:00.000Z',
  currentStoreLocalDate: '2026-07-24',
})
assertEqual(openResult.ok, true, '开放班段应返回成功')
assertEqual(openResult.isOpen, true, '开放班段应明确标记')
assertEqual(openResult.toDate, '2026-07-24', '开放班段查询结束日期必须使用门店当前本地日期')
assertDeepEqual(
  openResult.points.map((point) => point.key),
  [
    'punch:open-in',
    'sample:open-middle',
    'sample:invalid-coordinate',
    'sample:missing-coordinate',
    'sample:open-at-now',
  ],
  '开放班段样本边界应为 start < sample <= now',
)
assertDeepEqual(
  openResult.mapPoints.map((point) => point.key),
  ['sample:open-middle', 'sample:open-at-now'],
  '非法或缺失坐标应保留审计时间线但不得进入地图',
)

assertDeepEqual(
  splitAttendanceSampleDateRange('2026-07-01', '2026-07-18'),
  [
    { fromDate: '2026-07-01', toDate: '2026-07-07' },
    { fromDate: '2026-07-08', toDate: '2026-07-14' },
    { fromDate: '2026-07-15', toDate: '2026-07-18' },
  ],
  '长期开放班段应拆成不重叠且连续的短日期窗口',
)
assertDeepEqual(
  splitAttendanceSampleDateRange('2026-07-18', '2026-07-01'),
  [],
  '非法日期范围不得发起样本查询',
)
assertEqual(openResult.sampleCount, 4, '非法坐标样本仍是班段内审计样本')

const noSamplesResult = buildAttendanceLocationTrajectory({
  selectedPunch: firstIn,
  punches: [firstIn, firstOut],
  samples: [],
  nowUtc: '2026-10-04T07:00:00.000Z',
})
assertEqual(noSamplesResult.ok, true, '没有后台样本时仍应返回班段打卡时间线')
assertEqual(noSamplesResult.sampleCount, 0, '无样本时样本数应为零')
assertDeepEqual(
  noSamplesResult.points.map((point) => point.kind),
  ['ClockIn', 'ClockOut'],
  '无样本时仍应包含班段边界',
)

const dotNetUtcOpenIn = punch(
  'dotnet-utc-open-in',
  'ClockIn',
  '2026-07-23T22:20:00',
  '2026-07-24T08:20:00',
  {
    workDate: '2026-07-24',
    storeTimeZone: 'Australia/Sydney',
    locationLatitude: -33.86,
    locationLongitude: 151.20,
  },
)
const dotNetUtcResult = buildAttendanceLocationTrajectory({
  selectedPunch: dotNetUtcOpenIn,
  punches: [dotNetUtcOpenIn],
  samples: [
    sample('dotnet-utc-sample', '2026-07-23T22:40:00', -33.85, 151.21),
  ],
  nowUtc: '2026-07-23T23:00:00.000Z',
  currentStoreLocalDate: '2026-07-24',
})
assertEqual(dotNetUtcResult.ok, true, '.NET 无后缀 UTC 打卡应按 UTC 生成开放班段')
assertEqual(dotNetUtcResult.isOpen, true, '.NET 无后缀 UTC 打卡应保留开放班段状态')
assertDeepEqual(
  dotNetUtcResult.points.map((point) => point.key),
  ['punch:dotnet-utc-open-in', 'sample:dotnet-utc-sample'],
  '.NET 无后缀 UTC 样本应按 UTC 边界纳入时间线',
)
assertEqual(
  normalizeAttendanceUtcText('2026-07-23T22:20:00'),
  '2026-07-23T22:20:00Z',
  '严格的 .NET 无后缀时间只在 UTC 语义边界补 Z',
)
assertEqual(
  normalizeAttendanceUtcText('2026-07-23T22:20:00.1234567Z'),
  '2026-07-23T22:20:00.1234567Z',
  '已有 Z 的 UTC 时间必须保持不变',
)
assertEqual(
  normalizeAttendanceUtcText('2026-07-24T08:20:00+10:00'),
  '2026-07-24T08:20:00+10:00',
  '已有明确偏移量的时间必须保持不变',
)
assertEqual(normalizeAttendanceUtcText('2026-02-30T08:20:00'), undefined, '非法日历日期必须拒绝')
assertEqual(normalizeAttendanceUtcText('2026-07-24 08:20:00'), undefined, '本地墙钟文本不得被猜测为 UTC')

const invalidSelectedTime = punch(
  'invalid-selected-time',
  'ClockIn',
  'not-a-date',
  '2026-07-24T10:00:00+10:00',
)
const invalidSelectedResult = buildAttendanceLocationTrajectory({
  selectedPunch: invalidSelectedTime,
  punches: [invalidSelectedTime],
  samples: [],
  nowUtc: '2026-07-24T01:00:00.000Z',
  currentStoreLocalDate: '2026-07-24',
})
assertEqual(invalidSelectedResult.ok, false, '非法选中打卡时间必须安全失败')
assertEqual(invalidSelectedResult.reason, 'INVALID_SELECTED_TIME', '非法选中时间应返回稳定原因代码')

// 样本事件类型要带到轨迹点上：服务端要求员工上报的点（ServerRequest）在时间轴上要能单独标出；上下班端点没有事件类型。
const serverRequestResult = buildAttendanceLocationTrajectory({
  selectedPunch: openIn,
  punches: [openIn],
  samples: [
    sample('regular-sample', '2026-07-24T00:20:00.000Z', -33.85, 151.15, { eventType: 'ShiftSample' }),
    sample('requested-sample', '2026-07-24T00:40:00.000Z', -33.86, 151.16, { eventType: 'ServerRequest' }),
  ],
  nowUtc: '2026-07-24T01:00:00.000Z',
  currentStoreLocalDate: '2026-07-24',
})
assertDeepEqual(
  serverRequestResult.points.map((point) => [point.key, point.eventType]),
  [
    ['punch:open-in', undefined],
    ['sample:regular-sample', 'ShiftSample'],
    ['sample:requested-sample', 'ServerRequest'],
  ],
  '样本轨迹点应保留事件类型，打卡端点不带事件类型',
)

console.log('attendanceLocationTrajectoryLogic.test.ts: ok')
