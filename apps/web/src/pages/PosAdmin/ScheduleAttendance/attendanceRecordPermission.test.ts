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

// 无权限的页签直接隐藏：只有排班查看权限时，员工可用时间 / 打卡记录 / 公共假期 / 考勤设置页签的判定都应为 false
assertEqual(scheduleOnly.canViewAttendanceAvailability, false, '只有排班查看权限不应显示员工可用时间页签')
assertEqual(scheduleOnly.canViewAttendanceHoliday, false, '只有排班查看权限不应显示公共假期页签')
assertEqual(scheduleOnly.canEditAttendanceSettings, false, '只有排班查看权限不应显示考勤设置页签')

// 公共假期页签与后端 GET attendance/holidays 同口径：只认 Holiday.ViewStore，只有编辑权限不算
const holidayViewer = buildAccess(createUser([P.Attendance.ScheduleViewStore, P.Attendance.HolidayViewStore]))
assertEqual(holidayViewer.canViewAttendanceHoliday, true, 'Holiday.ViewStore 应显示公共假期页签')
const holidayEditOnly = buildAccess(createUser([P.Attendance.ScheduleViewStore, P.Attendance.HolidayEditManagedStore]))
assertEqual(holidayEditOnly.canViewAttendanceHoliday, false, '只有 Holiday.EditManagedStore 不应显示公共假期页签（后端读接口只认 ViewStore）')
assertEqual(holidayEditOnly.canEditAttendanceHoliday, true, '编辑权限仍应允许公共假期的增删改入口')

// 考勤设置页签：读写同一个码 Attendance.Settings.Edit
const settingsEditor = buildAccess(createUser([P.Attendance.ScheduleViewStore, P.Attendance.SettingsEdit]))
assertEqual(settingsEditor.canEditAttendanceSettings, true, 'Settings.Edit 应显示考勤设置页签')

// 员工可用时间页签：Availability.ViewManagedStore
const availabilityViewer = buildAccess(createUser([P.Attendance.ScheduleViewStore, P.Attendance.AvailabilityViewManagedStore]))
assertEqual(availabilityViewer.canViewAttendanceAvailability, true, 'Availability.ViewManagedStore 应显示员工可用时间页签')

const admin = buildAccess(createUser([], ['Admin']))
assertEqual(admin.canViewAttendanceSchedule, true, 'Admin 应允许查看排班')
assertEqual(admin.canViewAttendancePunches, true, 'Admin 应允许查看考勤记录')
assertEqual(admin.canViewAttendanceHoliday, true, 'Admin 应显示公共假期页签')
assertEqual(admin.canEditAttendanceSettings, true, 'Admin 应显示考勤设置页签')

console.log('attendanceRecordPermission.test.ts: ok')
