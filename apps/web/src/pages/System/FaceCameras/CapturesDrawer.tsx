import { LeftOutlined, PictureOutlined, ReloadOutlined, RightOutlined } from '@ant-design/icons'
import { Alert, Button, Drawer, Empty, Space, Tag, Tooltip, Typography } from 'antd'
import type { ColumnsType } from 'antd/es/table'
import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'

import { MeasuredTable } from '../../../components/MeasuredTable'
import { getFaceCameraCaptures } from '../../../services/faceCameraService'
import type { FaceCamera, FaceCapture, FaceCapturePage, FaceCaptureParseStatus } from '../../../types/faceCamera'
import FaceImageViewerModal from './FaceImageViewerModal'
import { getRowImageAction } from './imageLogic'
import {
  CAPTURE_PAGE_SIZE,
  PARSE_STATUS_COLORS,
  canGoNextPage,
  canGoPrevPage,
  createLatestGuard,
  formatDateTime,
  formatShortDateTime,
} from './logic'

// 文案键写成字面量映射而不是模板拼接：契约测试按字面量扫描键。
const PARSE_STATUS_KEYS: Record<FaceCaptureParseStatus, string> = {
  0: 'faceCameras.captures.parse.parsed',
  1: 'faceCameras.captures.parse.partial',
  2: 'faceCameras.captures.parse.unparsed',
  3: 'faceCameras.captures.parse.notFace',
}

// 列宽：抽屉 960 宽、内容区约 912，合计控制在内容区以内，桌面上不出现横向滚动（窄屏仍可横向滚动）。
const COLUMN_WIDTHS = {
  receivedAt: 124,
  images: 120,
  eventTime: 124,
  eventType: 120,
  faceCount: 64,
  parseStatus: 150,
  parseNote: 140,
} as const
const TABLE_WIDTH_WITHOUT_IMAGES =
  COLUMN_WIDTHS.receivedAt + COLUMN_WIDTHS.eventTime + COLUMN_WIDTHS.eventType + COLUMN_WIDTHS.faceCount + COLUMN_WIDTHS.parseStatus + COLUMN_WIDTHS.parseNote

interface CapturesDrawerProps {
  /** 要查看的摄像机。父组件仅在选中摄像机时挂载本组件，换摄像机会重新挂载，页码与数据天然从头开始。 */
  camera: FaceCamera
  onClose: () => void
}

/**
 * 最近抓拍：表格只展示报文元数据，列表本身不请求任何图片（没有预加载、没有缩略图）；
 * 有「查看人脸抓拍图片」权限者每行多一个「查看图片」入口，点击后才在弹框里按需读取。
 * 后端没有总数，用上一页 / 下一页翻页。
 */
export default function CapturesDrawer({ camera, onClose }: CapturesDrawerProps) {
  const { t } = useTranslation()
  const [page, setPage] = useState(1)
  const [result, setResult] = useState<FaceCapturePage | null>(null)
  const [loading, setLoading] = useState(false)
  const [failed, setFailed] = useState(false)
  const [tick, setTick] = useState(0)
  // 正在查看图片的抓拍；弹框仅在它非空时挂载，关闭 / 换行 / 卸载都会清理 objectURL（见 FaceImageViewerModal）。
  const [viewTarget, setViewTarget] = useState<FaceCapture | null>(null)
  const [guard] = useState(createLatestGuard)
  const abortRef = useRef<AbortController | null>(null)
  const cameraId = camera.cameraId
  const canViewImages = result?.canViewImages === true

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
        width: COLUMN_WIDTHS.receivedAt,
        // 同页都在近几天内：去掉年份（悬停看完整时间）。
        render: (value: string) => <span title={formatDateTime(value)}>{formatShortDateTime(value)}</span>,
      },
      // 图片列放第 2 列（不用横向滚动就能看到）。没有查看图片权限时后端不返回图片元数据，这一列整列不出现，页面上看不出图片结构。
      ...(canViewImages
        ? [
            {
              title: t('faceCameras.captures.images'),
              key: 'images',
              width: COLUMN_WIDTHS.images,
              render: (_: unknown, record: FaceCapture) => {
                const action = getRowImageAction(canViewImages, record.images)
                if (action === 'view') {
                  return (
                    <Button size="small" icon={<PictureOutlined />} onClick={() => setViewTarget(record)}>
                      {t('faceCameras.images.view')}
                    </Button>
                  )
                }
                if (action === 'local') {
                  return (
                    <Tooltip title={t('faceCameras.images.localTip')}>
                      <Tag>{t('faceCameras.images.local')}</Tag>
                    </Tooltip>
                  )
                }
                return '-'
              },
            },
          ]
        : []),
      {
        title: t('faceCameras.captures.eventTime'),
        dataIndex: 'cameraEventTime',
        width: COLUMN_WIDTHS.eventTime,
        render: (value: string | null) => <span title={formatDateTime(value)}>{formatShortDateTime(value)}</span>,
      },
      {
        title: t('faceCameras.captures.eventType'),
        dataIndex: 'eventType',
        width: COLUMN_WIDTHS.eventType,
        ellipsis: true,
        render: (value: string | null) => value ?? '-',
      },
      {
        title: t('faceCameras.captures.faceCount'),
        dataIndex: 'faceCount',
        width: COLUMN_WIDTHS.faceCount,
        align: 'right',
      },
      {
        title: t('faceCameras.captures.parseStatus'),
        dataIndex: 'parseStatus',
        width: COLUMN_WIDTHS.parseStatus,
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
        width: COLUMN_WIDTHS.parseNote,
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
    [canViewImages, t],
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
        {/* 本店未开启处理（档案 Mode=Off）：接收端读完即删，下面不会再有新记录。Capture / Match 都留存照片，不另做提示。 */}
        {result?.storeMode === 'off' ? (
          <Alert type="warning" showIcon message={t('faceCameras.captures.storeOff', { store: camera.storeCode })} />
        ) : null}
        {result ? (
          <Typography.Text type="secondary">
            {result.lastSeenAtUtc
              ? t('faceCameras.captures.lastSeen', { time: formatDateTime(result.lastSeenAtUtc) })
              : t('faceCameras.captures.neverSeen')}
          </Typography.Text>
        ) : null}
        <Typography.Text type="secondary">
          {canViewImages ? t('faceCameras.captures.imagesOnDemand') : t('faceCameras.captures.metadataOnly')}
        </Typography.Text>
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
          scroll={{ x: canViewImages ? TABLE_WIDTH_WITHOUT_IMAGES + COLUMN_WIDTHS.images : TABLE_WIDTH_WITHOUT_IMAGES }}
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
      {viewTarget ? (
        <FaceImageViewerModal
          key={viewTarget.captureId}
          cameraId={cameraId}
          capture={viewTarget}
          onClose={() => setViewTarget(null)}
        />
      ) : null}
    </Drawer>
  )
}
