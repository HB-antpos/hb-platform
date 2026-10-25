import type ExcelJS from 'exceljs'
import dayjs from 'dayjs'
import type {
  AttendanceHoursResultDto,
  AttendancePayRateDto,
  AttendancePayResultDto,
} from '../../../types/attendancePayroll'
import { periodEnd, round2, summarizeEmployeeHours } from './payrollLogic'

// 导出 Excel：exceljs 只在点击导出时动态加载，页面包里只有类型。
// 工时一律十进制小时、最多 2 位小数；金额保留 2 位。

const XLSX_MIME = 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet'

export interface PayrollExportInput {
  tab: 'hours' | 'pay' | 'rates'
  storeName: string
  periodStart: string
  weeks: 1 | 2
  hours: AttendanceHoursResultDto | null
  pay: AttendancePayResultDto | null
  rates: AttendancePayRateDto[]
  text: (key: string, values?: Record<string, unknown>) => string
}

type Row = (string | number | null)[]

/** 组装工作表行（不依赖 exceljs，单测直接核对）。 */
export function buildPayrollExportRows(input: PayrollExportInput): Row[] {
  const { text } = input
  const hourValue = (value: number | null | undefined) => (value ? round2(value) : null)
  if (input.tab === 'hours' && input.hours) {
    const header: Row = [
      text('attendancePayroll.hours.employee'),
      text('attendancePayroll.export.employmentType'),
      ...input.hours.days.map((day) => dayjs(day.date).format('MM-DD')),
      ...(['weekday', 'saturday', 'sunday', 'publicHoliday', 'leave', 'total'] as const).map((key) => text(`attendancePayroll.tierLong.${key}`)),
    ]
    const rows = input.hours.employees.map((employee) => {
      const totals = summarizeEmployeeHours(employee)
      return [
        employee.userName,
        text(`attendancePayroll.employmentType.${employee.employmentType}`),
        // 单元格数 = 计薪工时；没上班但有带薪假时填申请工时
        ...employee.days.map((day) => hourValue(day.paidHours ?? day.leave?.hours ?? (day.publicHolidayPay?.state === 'Confirmed' ? day.publicHolidayPay.hours : null))),
        hourValue(totals.weekday),
        hourValue(totals.saturday),
        hourValue(totals.sunday),
        hourValue(totals.publicHoliday),
        hourValue(totals.leave),
        hourValue(totals.total),
      ]
    })
    return [header, ...rows]
  }
  if (input.tab === 'pay' && input.pay) {
    const header: Row = [
      text('attendancePayroll.hours.employee'),
      text('attendancePayroll.export.payMode'),
      ...(['weekday', 'saturday', 'sunday', 'publicHoliday', 'leave', 'total'] as const).map((key) => text(`attendancePayroll.tierLong.${key}`)),
      text('attendancePayroll.export.transferHours'),
      text('attendancePayroll.export.transferAmount'),
      text('attendancePayroll.export.cashHours'),
      text('attendancePayroll.pay.allowance'),
      text('attendancePayroll.export.cashAmount'),
      text('attendancePayroll.pay.gross'),
      text('attendancePayroll.pay.note'),
    ]
    const rows = input.pay.rows.map((row) => [
      row.userName,
      text(`attendancePayroll.payMode.${row.payMode}`),
      hourValue(row.hours.weekday),
      hourValue(row.hours.saturday),
      hourValue(row.hours.sunday),
      hourValue(row.hours.publicHoliday),
      hourValue(row.hours.leave),
      hourValue(row.hours.total),
      hourValue(row.transfer.hours),
      row.transfer.amount,
      hourValue(row.cash.hours),
      row.allowance,
      round2(row.cash.amount + row.allowance),
      row.grossPay,
      row.note ?? '',
    ])
    return [header, ...rows]
  }
  const header: Row = [
    text('attendancePayroll.rates.columns.employee'),
    text('attendancePayroll.rates.columns.payMode'),
    text('attendancePayroll.rates.fixedHours'),
    text('attendancePayroll.tierLong.weekday'),
    text('attendancePayroll.tierLong.saturday'),
    text('attendancePayroll.tierLong.sunday'),
    text('attendancePayroll.tierLong.publicHoliday'),
    text('attendancePayroll.rates.columns.cash'),
    text('attendancePayroll.rates.allowance'),
    text('attendancePayroll.rates.minimum'),
    text('attendancePayroll.rates.effectiveFrom'),
  ]
  const rows = input.rates.map((rate) => [
    rate.userName ?? rate.userGuid,
    text(`attendancePayroll.payMode.${rate.payMode}`),
    rate.fixedTransferHoursPerWeek ?? null,
    rate.transferWeekdayRate ?? null,
    rate.transferSaturdayRate ?? null,
    rate.transferSundayRate ?? null,
    rate.transferPublicHolidayRate ?? null,
    rate.cashRate ?? null,
    rate.cashAllowancePerPeriod ?? 0,
    rate.minimumRate ?? null,
    rate.effectiveFrom ?? '',
  ])
  return [header, ...rows]
}

export function buildPayrollExportFileName(input: PayrollExportInput): string {
  const tabName = input.text(`attendancePayroll.tabs.${input.tab}`)
  const range = input.tab === 'rates'
    ? dayjs().format('YYYYMMDD')
    : `${dayjs(input.periodStart).format('YYYYMMDD')}-${dayjs(periodEnd(input.periodStart, input.weeks)).format('YYYYMMDD')}`
  return `${input.storeName}_${tabName}_${range}.xlsx`.replace(/[\\/:*?"<>|]/g, '_')
}

export function fillPayrollSheet(sheet: ExcelJS.Worksheet, rows: Row[]) {
  rows.forEach((row) => sheet.addRow(row))
  sheet.getRow(1).font = { bold: true }
  sheet.views = [{ state: 'frozen', xSplit: 1, ySplit: 1 }]
  sheet.columns.forEach((column, index) => {
    column.width = index === 0 ? 20 : 12
  })
}

export async function exportPayrollWorkbook(input: PayrollExportInput): Promise<{ fileName: string }> {
  const { default: Excel } = await import('exceljs')
  const workbook = new Excel.Workbook()
  fillPayrollSheet(workbook.addWorksheet(input.text(`attendancePayroll.tabs.${input.tab}`)), buildPayrollExportRows(input))
  const buffer = await workbook.xlsx.writeBuffer()
  const fileName = buildPayrollExportFileName(input)
  const url = URL.createObjectURL(new Blob([buffer], { type: XLSX_MIME }))
  const link = document.createElement('a')
  link.href = url
  link.download = fileName
  link.click()
  setTimeout(() => URL.revokeObjectURL(url), 1000)
  return { fileName }
}
