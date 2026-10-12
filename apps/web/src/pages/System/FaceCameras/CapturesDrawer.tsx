import { LeftOutlined, ReloadOutlined, RightOutlined } from '@ant-design/icons'
import { Alert, Button, Drawer, Empty, Space, Tag, Tooltip, Typography } from 'antd'
import type { ColumnsType } from 'antd/es/table'
import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'

import { MeasuredTable } from '../../../components/MeasuredTable'
import { getFaceCameraCaptures } from '../../../services/faceCameraService'
import type { FaceCamera, FaceCapture, FaceCapturePage, FaceCaptureParseStatus } from '../../../types/faceCamera'
import {
  CAPTURE_PAGE_SIZE,
  PARSE_STATUS_COLORS,
  canGoNextPage,
  canGoPrevPage,
  createLatestGuard,
  formatDateTime,
} from './logic'

// 文案键写成字面量映射而不是模板拼接：契约测试按字面量扫描键。
const PARSE_STATUS_KEYS: Record<FaceCaptureParseStatus, string> = {
  0: 'faceCameras.captures.parse.parsed',
  1: 'faceCameras.captures.parse.partial',
  2: 'faceCameras.captures.parse.unparsed',
  3: 'faceCameras.captures.parse.notFace',
}

interface CapturesDrawerProps {
  /** 要查看的摄像机。父组件仅在选中摄像机时挂载本组件，换摄像机会重新挂载，页码与数据天然从头开始。 */
  camera: FaceCamera
  onClose: () => void
}

/** 最近抓拍：只展示报文元数据（没有也不会请求图片）；后端没有总数，用上一页 / 下一页翻页。 */
export default function CapturesDrawer({ camera, onClose }: CapturesDrawerProps) {
  const { t } = useTranslation()
  const [page, setPage] = useState(1)
  const [result, setResult] = useState<FaceCapturePage | null>(null)
  const [loading, setLoading] = useState(false)
  const [failed, setFailed] = useState(false)
  const [tick, setTick] = useState(0)
  const [guard] = useState(createLatestGuard)
  const abortRef = useRef<AbortController | null>(null)
  const cameraId = camera.cameraId

  const load = useCallback(async () => {
    abortRef.current?.abort()
    const controller = new AbortController()
    abortRef.current = controller
    const requestId = guard.begin()
    setLoading(true)
    setFailed(false)
    try {
      const data = await getFaceCameraCaptures(cameraId, page, CAPTURE_PAGE_SIZE, controller.signal)
      if (guard.isLatest(requestId)) setResult(data)
    } catch {
      // 主动取消不是失败；其余失败显示可重试提示（请求层已做中心日志上报）。
      if (controller.signal.aborted || !guard.isLatest(requestId)) return
      setFailed(true)
    } finally {
      if (guard.isLatest(requestId)) setLoading(false)
    }
  }, [cameraId, guard, page])

  useEffect(() => {
    void load()
  }, [load, tick])

  // 关闭 / 卸载时取消在途请求并让其结果作废。
  useEffect(
    () => () => {
      abortRef.current?.abort()
      guard.invalidate()
    },
    [guard],
  )

  const columns = useMemo<ColumnsType<FaceCapture>>(
    () => [
      {
        title: t('faceCameras.captures.receivedAt'),
        dataIndex: 'receivedAtUtc',
        width: 160,
        render: (value: string) => formatDateTime(value),
      },
      {
        title: t('faceCameras.captures.eventTime'),
        dataIndex: 'cameraEventTime',
        width: 160,
        render: (value: string | null) => formatDateTime(value),
      },
      {
        title: t('faceCameras.captures.eventType'),
        dataIndex: 'eventType',
        width: 120,
        ellipsis: true,
        render: (value: string | null) => value ?? '-',
      },
      {
        title: t('faceCameras.captures.faceCount'),
        dataIndex: 'faceCount',
        width: 80,
        align: 'right',
      },
      {
        title: t('faceCameras.captures.parseStatus'),
        dataIndex: 'parseStatus',
        width: 190,
        render: (_: unknown, record) => (
          <Space size={4} wrap>
            <Tag color={PARSE_STATUS_COLORS[record.parseStatus]} style={{ marginInlineEnd: 0 }}>
              {t(PARSE_STATUS_KEYS[record.parseStatus])}
            </Tag>
            {record.isDuplicate ? <Tag color="blue">{t('faceCameras.captures.duplicate')}</Tag> : null}
          </Space>
        ),
      },
      {
        title: t('faceCameras.captures.parseNote'),
        dataIndex: 'parseNote',
        width: 180,
        ellipsis: { showTitle: false },
        render: (value: string | null) =>
          value ? (
            <Tooltip title={value} placement="topLeft">
              <span>{value}</span>
            </Tooltip>
          ) : (
            '-'
          ),
      },
    ],
    [t],
  )

  return (
    <Drawer
      open
      width="min(960px, 100vw)"
      title={t('faceCameras.captures.title', { cameraId })}
      destroyOnHidden
      onClose={onClose}
      extra={
        <Button icon={<ReloadOutlined />} loading={loading} onClick={() => setTick((value) => value + 1)}>
          {t('faceCameras.actions.refresh')}
        </Button>
      }
    >
      <Space direction="vertical" size={12} style={{ width: '100%' }}>
        <Typography.Text type="secondary">{t('faceCameras.captures.metadataOnly')}</Typography.Text>
        {failed ? (
          <Alert
            type="error"
            showIcon
            message={t('faceCameras.captures.loadFailed')}
            action={
              <Button size="small" onClick={() => setTick((value) => value + 1)}>
                {t('faceCameras.actions.retry')}
              </Button>
            }
          />
        ) : null}
        <MeasuredTable<FaceCapture>
          metricId="system.face-cameras.captures-table-1"
          rowKey="captureId"
          size="small"
          loading={loading}
          dataSource={result?.items ?? []}
          columns={columns}
          pagination={false}
          scroll={{ x: 890 }}
          locale={{ emptyText: loading ? ' ' : <Empty description={t('faceCameras.captures.empty')} /> }}
        />
        <Space style={{ justifyContent: 'flex-end', width: '100%' }}>
          <Button
            icon={<LeftOutlined />}
            disabled={loading || !canGoPrevPage(page)}
            onClick={() => setPage((value) => value - 1)}
          >
            {t('faceCameras.captures.prev')}
          </Button>
          <Typography.Text type="secondary">{t('faceCameras.captures.pageLabel', { page })}</Typography.Text>
          <Button
            disabled={loading || !canGoNextPage(page, result?.hasMore === true)}
            onClick={() => setPage((value) => value + 1)}
          >
            {t('faceCameras.captures.next')}
            <RightOutlined />
          </Button>
        </Space>
      </Space>
    </Drawer>
  )
}
