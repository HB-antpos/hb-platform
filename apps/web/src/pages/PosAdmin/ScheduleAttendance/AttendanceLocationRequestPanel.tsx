import { EnvironmentOutlined, ReloadOutlined } from '@ant-design/icons'
import { Alert, Button, Card, List, Modal, Space, Tag, Typography, message } from 'antd'
import { useCallback, useEffect, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import {
  createAttendanceLocationRequest,
  getAttendanceLocationRequests,
} from '../../../services/scheduleAttendanceService'
import type { AttendanceLocationRequestDto } from '../../../types/scheduleAttendance'
import {
  LOCATION_REQUEST_BLOCK_MESSAGE_KEYS,
  describeLocationRequestError,
  hasLocationRequestCoordinates,
  prepareLocationRequestList,
  resolveLocationRequestStatusView,
} from './attendanceLocationRequestLogic'
import type { LocationRequestEligibility } from './attendanceLocationRequestLogic'

// 抽屉里只展示该员工最近的请求；服务端默认 50、上限 200，这里取够看即可。
const REQUEST_LIST_LIMIT = 20

export interface AttendanceLocationRequestMapPreview {
  title: string
  latitude: number
  longitude: number
  accuracy?: number
  capturedAt?: string
}

interface AttendanceLocationRequestPanelProps {
  storeCode: string
  targetUserGuid: string
  targetUserName: string
  /** 前端预判：false 时按钮置灰并说明原因；点击后的最终裁决永远是服务端错误码。 */
  eligibility: LocationRequestEligibility
  /** 把服务端 UTC 时间格式化成门店本地时间（页面已有的格式化函数）。 */
  formatUtcTime: (value?: string) => string
  onViewMap: (preview: AttendanceLocationRequestMapPreview) => void
  /** 刷新整个抽屉（含班中定位样本时间轴），员工响应后新点位会出现在时间轴里。 */
  onRefreshAll: () => void
}

/** 班中定位样本抽屉里的「要求员工上报位置」：发起请求（二次确认）+ 该员工最近的请求列表。 */
export default function AttendanceLocationRequestPanel({
  storeCode,
  targetUserGuid,
  targetUserName,
  eligibility,
  formatUtcTime,
  onViewMap,
  onRefreshAll,
}: AttendanceLocationRequestPanelProps) {
  const { t } = useTranslation()
  const [requests, setRequests] = useState<AttendanceLocationRequestDto[]>([])
  const [listLoading, setListLoading] = useState(false)
  const [listFailed, setListFailed] = useState(false)
  const [confirmOpen, setConfirmOpen] = useState(false)
  const [submitting, setSubmitting] = useState(false)
  // 列表请求只认最后一次；抽屉关闭 / 切换员工后组件卸载，晚到的响应不能再写 state。
  const listRequestIdRef = useRef(0)
  const submittingRef = useRef(false)
  const mountedRef = useRef(true)

  useEffect(() => {
    mountedRef.current = true
    return () => {
      mountedRef.current = false
      listRequestIdRef.current += 1
    }
  }, [])

  const loadRequests = useCallback(async () => {
    const requestId = ++listRequestIdRef.current
    setListLoading(true)
    setListFailed(false)
    try {
      const items = await getAttendanceLocationRequests({ storeCode, targetUserGuid, limit: REQUEST_LIST_LIMIT })
      if (listRequestIdRef.current !== requestId) return
      setRequests(prepareLocationRequestList(items, { storeCode, targetUserGuid, limit: REQUEST_LIST_LIMIT }))
    } catch (error) {
      console.error(error)
      if (listRequestIdRef.current !== requestId) return
      setRequests([])
      setListFailed(true)
    } finally {
      if (listRequestIdRef.current === requestId) setListLoading(false)
    }
  }, [storeCode, targetUserGuid])

  useEffect(() => {
    void loadRequests()
  }, [loadRequests])

  const submitRequest = async () => {
    if (submittingRef.current) return
    submittingRef.current = true
    setSubmitting(true)
    try {
      await createAttendanceLocationRequest({ storeCode, targetUserGuid })
      message.success(t('posAdmin.scheduleAttendance.locationRequest.sent', { name: targetUserName }))
    } catch (error) {
      console.error(error)
      // 已有待响应 / 限频等错误：服务端已有记录，刷新列表让操作人看到那条请求。
      message.error(describeLocationRequestError(error, t))
    } finally {
      submittingRef.current = false
      if (mountedRef.current) {
        setSubmitting(false)
        setConfirmOpen(false)
        void loadRequests()
      }
    }
  }

  const renderRequest = (item: AttendanceLocationRequestDto) => {
    const statusView = resolveLocationRequestStatusView(item.status, item.statusReason)
    const canOpenMap = hasLocationRequestCoordinates(item)
    const responded = statusView.kind === 'Responded'
    return (
      <List.Item key={item.requestGuid}>
        <Space direction="vertical" size={3} style={{ width: '100%' }}>
          <Space size={[8, 4]} wrap>
            <Tag color={statusView.color}>
              {statusView.kind === 'Unknown' && item.status ? item.status : t(statusView.messageKey)}
            </Tag>
            <Typography.Text>{formatUtcTime(item.createdAtUtc)}</Typography.Text>
            <Typography.Text type="secondary">
              {t('posAdmin.scheduleAttendance.locationRequest.requestedBy', {
                name: item.requestedByName || item.requestedByUserGuid || '--',
              })}
            </Typography.Text>
            {statusView.kind === 'Pending' ? (
              <Typography.Text type="secondary">
                {t('posAdmin.scheduleAttendance.locationRequest.expiresAt', { time: formatUtcTime(item.expiresAtUtc) })}
              </Typography.Text>
            ) : null}
            {responded && item.respondedAtUtc ? (
              <Typography.Text type="secondary">
                {t('posAdmin.scheduleAttendance.locationRequest.respondedAt', { time: formatUtcTime(item.respondedAtUtc) })}
              </Typography.Text>
            ) : null}
          </Space>
          {responded ? (
            <Space size={[8, 4]} wrap>
              {canOpenMap ? (
                <>
                  <Typography.Text>
                    {item.locationLatitude!.toFixed(6)}, {item.locationLongitude!.toFixed(6)}
                  </Typography.Text>
                  {typeof item.locationAccuracy === 'number' ? (
                    <Tag>{t('posAdmin.scheduleAttendance.fields.locationAccuracy')}: {Math.round(item.locationAccuracy)}m</Tag>
                  ) : null}
                  <Button
                    type="link"
                    size="small"
                    icon={<EnvironmentOutlined />}
                    onClick={() => onViewMap({
                      title: `${targetUserName} / ${t('posAdmin.scheduleAttendance.locationRequest.eventType.ServerRequest')} / ${formatUtcTime(item.locationCapturedAtUtc ?? item.respondedAtUtc)}`,
                      latitude: item.locationLatitude!,
                      longitude: item.locationLongitude!,
                      accuracy: item.locationAccuracy,
                      capturedAt: item.locationCapturedAtUtc ?? item.respondedAtUtc,
                    })}
                  >
                    {t('posAdmin.scheduleAttendance.actions.viewMap')}
                  </Button>
                </>
              ) : (
                <Typography.Text type="secondary">
                  {t('posAdmin.scheduleAttendance.messages.locationNotRecorded')}
                </Typography.Text>
              )}
            </Space>
          ) : null}
        </Space>
      </List.Item>
    )
  }

  return (
    <Card
      size="small"
      title={t('posAdmin.scheduleAttendance.locationRequest.title')}
      extra={(
        <Space size={8}>
          <Button size="small" icon={<ReloadOutlined />} onClick={onRefreshAll}>
            {t('common.refresh')}
          </Button>
          <Button
            type="primary"
            size="small"
            icon={<EnvironmentOutlined />}
            disabled={!eligibility.canRequest}
            onClick={() => setConfirmOpen(true)}
          >
            {t('posAdmin.scheduleAttendance.locationRequest.button')}
          </Button>
        </Space>
      )}
    >
      <Space direction="vertical" size={10} style={{ width: '100%' }}>
        <Typography.Text type="secondary">
          {t('posAdmin.scheduleAttendance.locationRequest.hint')}
        </Typography.Text>
        {!eligibility.canRequest ? (
          <Alert type="info" showIcon message={t(LOCATION_REQUEST_BLOCK_MESSAGE_KEYS[eligibility.reason])} />
        ) : null}
        {listFailed ? (
          <Alert
            type="warning"
            showIcon
            message={t('posAdmin.scheduleAttendance.locationRequest.loadFailed')}
            action={(
              <Button size="small" onClick={() => void loadRequests()}>
                {t('common.retry')}
              </Button>
            )}
          />
        ) : null}
        <List
          size="small"
          loading={listLoading}
          dataSource={requests}
          locale={{ emptyText: t('posAdmin.scheduleAttendance.locationRequest.empty') }}
          renderItem={renderRequest}
        />
      </Space>

      <Modal
        title={t('posAdmin.scheduleAttendance.locationRequest.confirmTitle', { name: targetUserName })}
        open={confirmOpen}
        okText={t('posAdmin.scheduleAttendance.locationRequest.confirmOk')}
        cancelText={t('common.cancel')}
        confirmLoading={submitting}
        maskClosable={!submitting}
        keyboard={!submitting}
        cancelButtonProps={{ disabled: submitting }}
        onOk={() => void submitRequest()}
        onCancel={() => { if (!submitting) setConfirmOpen(false) }}
      >
        <Space direction="vertical" size={6}>
          <Typography.Text>{t('posAdmin.scheduleAttendance.locationRequest.confirmNotify')}</Typography.Text>
          <Typography.Text>{t('posAdmin.scheduleAttendance.locationRequest.confirmValidity')}</Typography.Text>
          <Typography.Text type="secondary">{t('posAdmin.scheduleAttendance.locationRequest.confirmAudit')}</Typography.Text>
        </Space>
      </Modal>
    </Card>
  )
}
