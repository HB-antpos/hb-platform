import type { CurrentUser } from '../../../types/auth'
import { P } from '../../../types/permissions'
import { buildAccess } from '../../../utils/access'

function createUser(permissions: string[], roleNames: string[] = []): CurrentUser {
  return {
    userGUID: 'attendance-user',
    username: 'attendance-user',
    email: 'attendance@example.com',
    permissions,
    roleNames,
    storeNames: [],
  }
}

function assertEqual(actual: unknown, expected: unknown, message: string) {
  if (actual !== expected) {
    throw new Error(`${message}: expected ${String(expected)}, got ${String(actual)}`)
  }
}

const scheduleOnly = buildAccess(createUser([P.Attendance.ScheduleViewStore]))
assertEqual(scheduleOnly.canViewAttendanceSchedule, true, 'Schedule.ViewStore 应允许查看排班')
assertEqual(scheduleOnly.canViewAttendancePunches, false, 'Schedule.ViewStore 不得读取 punch 派生考勤记录')

const scheduleAndPunches = buildAccess(createUser([
  P.Attendance.ScheduleViewStore,
  P.Attendance.PunchViewManagedStore,
]))
assertEqual(scheduleAndPunches.canViewAttendanceSchedule, true, '组合权限应允许查看排班')
assertEqual(scheduleAndPunches.canViewAttendancePunches, true, 'Punch.ViewManagedStore 应允许查看考勤记录')

const editOnly = buildAccess(createUser([P.Attendance.ScheduleEditManagedStore]))
assertEqual(editOnly.canViewAttendanceSchedule, true, 'Schedule.EditManagedStore 应隐含查看排班')

const admin = buildAccess(createUser([], ['Admin']))
assertEqual(admin.canViewAttendanceSchedule, true, 'Admin 应允许查看排班')
assertEqual(admin.canViewAttendancePunches, true, 'Admin 应允许查看考勤记录')

console.log('attendanceRecordPermission.test.ts: ok')
