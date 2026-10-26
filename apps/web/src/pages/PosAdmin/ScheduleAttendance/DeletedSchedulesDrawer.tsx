import { UndoOutlined } from '@ant-design/icons'
import { Alert, Button, Drawer, Popconfirm, Typography, message } from 'antd'
import type { ColumnsType } from 'antd/es/table'
import dayjs from 'dayjs'
import { useCallback, useEffect, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { MeasuredTable } from '../../../components/MeasuredTable'
import { getDeletedAttendanceSchedules, restoreAttendanceSchedule } from '../../../services/attendancePayrollService'
import type { AttendanceDeletedScheduleDto } from '../../../types/attendancePayroll'

// 「已删除排班」：有打卡的排班也允许删除，但只是软删除（规格 0.1 第 2 条），这里列出并可恢复；
// 恢复后后端重新对账、派生审批。文案在页面级消息文件 posAdmin.scheduleAttendance.deletedSchedules.*。

interface DeletedSchedulesDrawerProps {
  open: boolean
  storeCode?: string
  fromDate: string
  toDate: string
  canRestore: boolean
  onClose: () => void
  onRestored: () => void
}

export default function DeletedSchedulesDrawer(props: DeletedSchedulesDrawerProps) {
  const { open, storeCode, fromDate, toDate, canRestore } = props
  const { t } = useTranslation()
  const [items, setItems] = useState<AttendanceDeletedScheduleDto[]>([])
  const [loading, setLoading] = useState(false)
  const [restoring, setRestoring] = useState<string>()

  const load = useCallback(async () => {
    if (!storeCode) return
    setLoading(true)
    try {
      setItems(await getDeletedAttendanceSchedules({ storeCode, fromDate, toDate }))
    } catch (error) {
      message.error(error instanceof Error ? error.message : t('posAdmin.scheduleAttendance.deletedSchedules.loadFailed'))
    } finally {
      setLoading(false)
    }
  }, [fromDate, storeCode, t, toDate])

  useEffect(() => {
    if (open) void load()
  }, [load, open])

  const restore = async (item: AttendanceDeletedScheduleDto) => {
    setRestoring(item.scheduleGuid)
    try {
      await restoreAttendanceSchedule(item.scheduleGuid)
      message.success(t('posAdmin.scheduleAttendance.deletedSchedules.restored'))
      setItems((current) => current.filter((row) => row.scheduleGuid !== item.scheduleGuid))
      props.onRestored()
    } catch (error) {
      message.error(error instanceof Error ? error.message : t('posAdmin.scheduleAttendance.deletedSchedules.restoreFailed'))
    } finally {
      setRestoring(undefined)
    }
  }

  const columns: ColumnsType<AttendanceDeletedScheduleDto> = [
    { title: t('posAdmin.scheduleAttendance.deletedSchedules.employee'), key: 'employee', width: 180, render: (_, item) => item.userName || item.userGuid },
    {
      title: t('posAdmin.scheduleAttendance.deletedSchedules.workDate'),
      key: 'workDate',
      width: 140,
      render: (_, item) => `${dayjs(item.workDate).format('YYYY-MM-DD')} ${t(`posAdmin.scheduleAttendance.weekdays.${(dayjs(item.workDate).day() + 6) % 7}`)}`,
    },
    { title: t('posAdmin.scheduleAttendance.deletedSchedules.time'), key: 'time', width: 120, render: (_, item) => `${item.startTime.slice(0, 5)} - ${item.endTime.slice(0, 5)}` },
    {
      title: t('posAdmin.scheduleAttendance.deletedSchedules.deleted'),
      key: 'deleted',
      width: 170,
      render: (_, item) => (
        <div>
          <div>{item.deletedBy ?? '--'}</div>
          <Typography.Text type="secondary">{item.deletedAt ? dayjs(item.deletedAt).format('YYYY-MM-DD HH:mm') : ''}</Typography.Text>
        </div>
      ),
    },
    {
      title: t('posAdmin.scheduleAttendance.deletedSchedules.action'),
      key: 'action',
      width: 100,
      fixed: 'right',
      render: (_, item) => (
        <Popconfirm title={t('posAdmin.scheduleAttendance.deletedSchedules.restoreConfirm')} onConfirm={() => void restore(item)} disabled={!canRestore}>
          <Button size="small" icon={<UndoOutlined />} disabled={!canRestore} loading={restoring === item.scheduleGuid}>{t('posAdmin.scheduleAttendance.deletedSchedules.restore')}</Button>
        </Popconfirm>
      ),
    },
  ]

  return (
    <Drawer title={t('posAdmin.scheduleAttendance.deletedSchedules.title')} width={820} open={open} onClose={props.onClose}>
      <Alert type="info" showIcon style={{ marginBottom: 12 }} message={t('posAdmin.scheduleAttendance.deletedSchedules.hint', { from: fromDate, to: toDate })} />
      <MeasuredTable
        metricId="pos-admin.schedule-attendance.deleted-schedules"
        rowKey="scheduleGuid"
        size="small"
        loading={loading}
        columns={columns}
        dataSource={storeCode ? items : []}
        pagination={false}
        scroll={{ x: 700 }}
        locale={{ emptyText: storeCode ? t('posAdmin.scheduleAttendance.deletedSchedules.empty') : t('posAdmin.scheduleAttendance.selectStoreToSchedule') }}
      />
    </Drawer>
  )
}
