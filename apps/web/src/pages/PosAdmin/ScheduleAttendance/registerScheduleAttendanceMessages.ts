// 分店侧栏（StorePicker）和「上周 / 本周 / 下周」按钮的文案在排班考勤页的语言包里。
// 考勤工资页等其他页面单独打开时排班考勤页模块没加载过，文案还没注册，会显示成原始键名，
// 所以由组件自己保证注册（重复注册幂等，已有键不会被覆盖）。
import { registerPageMessages } from '../../../i18n/registerPageMessages'
import scheduleAttendanceMessagesEn from './scheduleAttendanceMessages.en.json'
import scheduleAttendanceMessagesZh from './scheduleAttendanceMessages.zh.json'

registerPageMessages({ zh: scheduleAttendanceMessagesZh, en: scheduleAttendanceMessagesEn })
