import { PlusOutlined, ReloadOutlined } from '@ant-design/icons'
import { Alert, Button, Empty, Grid, Modal, Space, Switch, Tag, Tooltip, Typography, message } from 'antd'
import type { ColumnsType } from 'antd/es/table'
import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'

import { MeasuredTable } from '../../../components/MeasuredTable'
import PageContainer from '../../../components/PageContainer'
import { registerPageMessages } from '../../../i18n/registerPageMessages'
import {
  createFaceCamera,
  getFaceCameras,
  rotateFaceCameraToken,
  updateFaceCamera,
} from '../../../services/faceCameraService'
import { getActiveStores, type StoreOption } from '../../../services/storeService'
import type {
  CreateFaceCameraRequest,
  FaceCamera,
  FaceCameraList,
  UpdateFaceCameraRequest,
} from '../../../types/faceCamera'
import CameraFormModal from './CameraFormModal'
import CapturesDrawer from './CapturesDrawer'
import SecretRevealModal from './SecretRevealModal'
import {
  CAMERA_STATUS_COLORS,
  SECRET_REVEAL_CLOSED,
  buildUpdateRequest,
  closeSecretReveal,
  countCameraStatuses,
  formatDateTime,
  getCameraStatus,
  getFaceCameraErrorCode,
  hasParseFailures,
  isForbiddenError,
  openSecretReveal,
  toFormValues,
  type CameraStatus,
  type FaceCameraErrorCode,
  type SecretRevealState,
} from './logic'
import en from './messages.en.json'
import zh from './messages.zh.json'

// 页面文案随本页代码块懒注册，不进首屏 i18n 包（首屏体积预算很紧）；全局语言包只有菜单标题一个键。
registerPageMessages({ zh, en })

// 文案键写成字面量映射而不是模板拼接：契约测试按字面量扫描键。
const STATUS_LABEL_KEYS: Record<CameraStatus, string> = {
  recentlyActive: 'faceCameras.status.recentlyActive',
  noRecentPush: 'faceCameras.status.noRecentPush',
  disabled: 'faceCameras.status.disabled',
}

const ERROR_KEYS: Record<FaceCameraErrorCode, string> = {
  FACE_CAMERA_ID_INVALID: 'faceCameras.errors.FACE_CAMERA_ID_INVALID',
  FACE_CAMERA_STORE_CODE_INVALID: 'faceCameras.errors.FACE_CAMERA_STORE_CODE_INVALID',
  FACE_CAMERA_STORE_NOT_FOUND: 'faceCameras.errors.FACE_CAMERA_STORE_NOT_FOUND',
  FACE_CAMERA_FIELD_INVALID: 'faceCameras.errors.FACE_CAMERA_FIELD_INVALID',
  FACE_CAMERA_ID_EXISTS: 'faceCameras.errors.FACE_CAMERA_ID_EXISTS',
  FACE_CAMERA_TOKEN_CONFLICT: 'faceCameras.errors.FACE_CAMERA_TOKEN_CONFLICT',
  FACE_CAMERA_NOT_FOUND: 'faceCameras.errors.FACE_CAMERA_NOT_FOUND',
}

const NUMERIC_STYLE = { fontVariantNumeric: 'tabular-nums' } as const

export default function FaceCamerasPage() {
  const { t } = useTranslation()
  const screens = Grid.useBreakpoint()
  const [modal, modalContextHolder] = Modal.useModal()

  const [data, setData] = useState<FaceCameraList | null>(null)
  const [loading, setLoading] = useState(false)
  const [loadError, setLoadError] = useState<string | null>(null)
  // 正在操作的摄像机：用于禁用开关 / 按钮，防重复点击。
  const [busyIds, setBusyIds] = useState<ReadonlySet<string>>(() => new Set())
  // 表单：undefined 关闭；null 新增；摄像机对象为编辑。
  const [formTarget, setFormTarget] = useState<FaceCamera | null | undefined>(undefined)
  const [capturesTarget, setCapturesTarget] = useState<FaceCamera | null>(null)
  // 一次性展示的摄像机地址（含 token）只放在这个局部 state：关闭即清空，页面卸载随组件销毁。
  const [secret, setSecret] = useState<SecretRevealState>(SECRET_REVEAL_CLOSED)

  const [storeOptions, setStoreOptions] = useState<StoreOption[]>([])
  const [storeOptionsFailed, setStoreOptionsFailed] = useState(false)
  const storeOptionsRequestedRef = useRef(false)
  const loadAbortRef = useRef<AbortController | null>(null)

  const describeError = useCallback(
    (error: unknown) => {
      const code = getFaceCameraErrorCode(error)
      if (code) return t(ERROR_KEYS[code])
      if (isForbiddenError(error)) return t('faceCameras.errors.forbidden')
      // 服务层在响应缺 token / 地址时抛的固定标记：此时服务端可能已完成创建或轮换，提示先刷新核对。
      if (error instanceof Error && error.message === 'FACE_CAMERA_SECRET_RESPONSE_INVALID') {
        return t('faceCameras.errors.secretResponseInvalid')
      }
      return t('faceCameras.errors.generic')
    },
    [t],
  )

  const loadList = useCallback(async () => {
    loadAbortRef.current?.abort()
    const controller = new AbortController()
    loadAbortRef.current = controller
    setLoading(true)
    setLoadError(null)
    try {
      const result = await getFaceCameras(controller.signal)
      if (controller.signal.aborted) return
      setData(result)
    } catch (error) {
      if (controller.signal.aborted) return
      setLoadError(isForbiddenError(error) ? t('faceCameras.errors.forbidden') : t('faceCameras.errors.loadFailed'))
    } finally {
      if (!controller.signal.aborted) setLoading(false)
    }
  }, [t])

  useEffect(() => {
    void loadList()
  }, [loadList])

  // 卸载时取消在途列表请求。
  useEffect(() => () => loadAbortRef.current?.abort(), [])

  // 分店下拉只在第一次打开表单时取一次；失败（例如账号没有分店查看权限）降级成手填分店编码。
  const ensureStoreOptions = useCallback(() => {
    if (storeOptionsRequestedRef.current) return
    storeOptionsRequestedRef.current = true
    void getActiveStores()
      .then(setStoreOptions)
      .catch(() => setStoreOptionsFailed(true))
  }, [])

  const setBusy = useCallback((cameraId: string, busy: boolean) => {
    setBusyIds((current) => {
      const next = new Set(current)
      if (busy) next.add(cameraId)
      else next.delete(cameraId)
      return next
    })
  }, [])

  const replaceCamera = useCallback((updated: FaceCamera) => {
    setData((current) =>
      current
        ? { ...current, items: current.items.map((item) => (item.cameraId === updated.cameraId ? updated : item)) }
        : current,
    )
  }, [])

  const openForm = (camera: FaceCamera | null) => {
    ensureStoreOptions()
    setFormTarget(camera)
  }

  // 新增：成功后把一次性地址交给展示框；这里只取地址，token 随局部变量离开作用域。
  const handleCreate = useCallback(
    async (request: CreateFaceCameraRequest) => {
      const result = await createFaceCamera(request)
      setSecret(openSecretReveal('created', result))
      void loadList()
    },
    [loadList],
  )

  const handleUpdate = useCallback(
    async (cameraId: string, request: UpdateFaceCameraRequest) => {
      const updated = await updateFaceCamera(cameraId, request)
      replaceCamera(updated)
      void message.success(t('faceCameras.messages.saved'))
    },
    [replaceCamera, t],
  )

  const applyEnabled = useCallback(
    async (camera: FaceCamera, enabled: boolean) => {
      setBusy(camera.cameraId, true)
      try {
        // 开关只改启用状态，其余字段沿用当前值。
        const updated = await updateFaceCamera(camera.cameraId, buildUpdateRequest(toFormValues(camera), enabled))
        replaceCamera(updated)
        void message.success(enabled ? t('faceCameras.messages.enabled') : t('faceCameras.messages.disabled'))
      } catch (error) {
        void message.error(describeError(error))
      } finally {
        setBusy(camera.cameraId, false)
      }
    },
    [describeError, replaceCamera, setBusy, t],
  )

  const toggleEnabled = (camera: FaceCamera, enabled: boolean) => {
    if (enabled) {
      void applyEnabled(camera, true)
      return
    }
    // 停用前二次确认；启用直接生效。
    modal.confirm({
      title: t('faceCameras.confirmDisable.title', { cameraId: camera.cameraId }),
      content: t('faceCameras.confirmDisable.content'),
      okText: t('faceCameras.confirmDisable.ok'),
      cancelText: t('faceCameras.form.cancel'),
      okButtonProps: { danger: true },
      onOk: () => applyEnabled(camera, false),
    })
  }

  const rotateToken = (camera: FaceCamera) => {
    modal.confirm({
      title: t('faceCameras.confirmRotate.title', { cameraId: camera.cameraId }),
      content: t('faceCameras.confirmRotate.content'),
      okText: t('faceCameras.confirmRotate.ok'),
      cancelText: t('faceCameras.form.cancel'),
      okButtonProps: { danger: true },
      onOk: async () => {
        setBusy(camera.cameraId, true)
        try {
          const result = await rotateFaceCameraToken(camera.cameraId)
          setSecret(openSecretReveal('rotated', result))
          void loadList()
        } catch (error) {
          void message.error(describeError(error))
        } finally {
          setBusy(camera.cameraId, false)
        }
      },
    })
  }

  const columns = useMemo<ColumnsType<FaceCamera>>(
    () => [
      {
        title: t('faceCameras.columns.store'),
        key: 'store',
        width: 200,
        ellipsis: true,
        render: (_: unknown, record) => (
          <span>
            <Typography.Text strong>{record.storeCode}</Typography.Text>
            {record.storeName ? <Typography.Text type="secondary"> · {record.storeName}</Typography.Text> : null}
          </span>
        ),
      },
      {
        title: t('faceCameras.columns.cameraId'),
        dataIndex: 'cameraId',
        width: 200,
        ellipsis: true,
        render: (value: string, record) => (
          <div>
            <Typography.Text copyable={{ text: value }} style={{ fontFamily: 'ui-monospace, SFMono-Regular, Menlo, monospace' }}>
              {value}
            </Typography.Text>
            {record.note ? (
              <div>
                <Typography.Text type="secondary" ellipsis style={{ maxWidth: 180 }}>
                  {record.note}
                </Typography.Text>
              </div>
            ) : null}
          </div>
        ),
      },
      {
        title: t('faceCameras.columns.deviceModel'),
        dataIndex: 'deviceModel',
        width: 140,
        ellipsis: true,
        render: (value: string | null) => value ?? '-',
      },
      {
        title: t('faceCameras.columns.status'),
        key: 'status',
        width: 96,
        render: (_: unknown, record) => {
          const status = getCameraStatus(record)
          const tag = <Tag color={CAMERA_STATUS_COLORS[status]}>{t(STATUS_LABEL_KEYS[status])}</Tag>
          // 「暂无推送」容易被理解成故障：悬停说明它只表示最近没有人脸触发推送。
          return status === 'noRecentPush' ? <Tooltip title={t('faceCameras.status.noRecentPushTip')}>{tag}</Tooltip> : tag
        },
      },
      {
        title: t('faceCameras.columns.lastSeen'),
        dataIndex: 'lastSeenAtUtc',
        width: 170,
        render: (value: string | null) => <span style={NUMERIC_STYLE}>{formatDateTime(value)}</span>,
      },
      {
        title: t('faceCameras.columns.stats24h'),
        children: [
          {
            title: t('faceCameras.columns.captureCount'),
            dataIndex: 'captureCount24h',
            width: 84,
            align: 'right',
            render: (value: number) => <span style={NUMERIC_STYLE}>{value}</span>,
          },
          {
            title: t('faceCameras.columns.faceCount'),
            dataIndex: 'faceCount24h',
            width: 84,
            align: 'right',
            render: (value: number) => <span style={NUMERIC_STYLE}>{value}</span>,
          },
          {
            title: t('faceCameras.columns.parseFailureCount'),
            dataIndex: 'parseFailureCount24h',
            width: 96,
            align: 'right',
            // 解析失败数大于 0 用警示色，提示可能是推送格式变了。
            render: (value: number, record) =>
              hasParseFailures(record) ? (
                <Typography.Text type="danger" strong style={NUMERIC_STYLE}>
                  {value}
                </Typography.Text>
              ) : (
                <span style={NUMERIC_STYLE}>{value}</span>
              ),
          },
        ],
      },
      {
        title: t('faceCameras.columns.enabled'),
        key: 'enabled',
        width: 80,
        render: (_: unknown, record) => (
          <Switch
            size="small"
            checked={record.enabled}
            loading={busyIds.has(record.cameraId)}
            disabled={busyIds.has(record.cameraId)}
            aria-label={t('faceCameras.columns.enabled')}
            onChange={(checked) => toggleEnabled(record, checked)}
          />
        ),
      },
      {
        title: t('faceCameras.columns.actions'),
        key: 'actions',
        width: 250,
        fixed: screens.md ? 'right' : undefined,
        render: (_: unknown, record) => {
          const busy = busyIds.has(record.cameraId)
          return (
            <Space size={0} wrap>
              <Button type="link" size="small" disabled={busy} onClick={() => openForm(record)}>
                {t('faceCameras.actions.edit')}
              </Button>
              <Button type="link" size="small" onClick={() => setCapturesTarget(record)}>
                {t('faceCameras.actions.captures')}
              </Button>
              <Button type="link" size="small" danger disabled={busy} onClick={() => rotateToken(record)}>
                {t('faceCameras.actions.rotateToken')}
              </Button>
            </Space>
          )
        },
      },
    ],
    // toggleEnabled / rotateToken / openForm 只依赖稳定的 setter 与 t，随 busyIds 与语言刷新即可。
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [t, busyIds, screens.md, modal],
  )

  const counts = useMemo(() => countCameraStatuses(data?.items ?? []), [data])
  const pushWindowMinutes = data?.onlineWindowMinutes ?? 10
  const statsHours = data?.statsWindowHours ?? 24

  return (
    <PageContainer
      compact
      title={t('faceCameras.title')}
      subtitle={
        data
          ? t('faceCameras.subtitle', {
              total: counts.total,
              active: counts.recentlyActive,
              quiet: counts.noRecentPush,
              disabled: counts.disabled,
            })
          : undefined
      }
      extra={
        <Space>
          <Button icon={<ReloadOutlined />} loading={loading} onClick={() => void loadList()}>
            {t('faceCameras.actions.refresh')}
          </Button>
          <Button type="primary" icon={<PlusOutlined />} onClick={() => openForm(null)}>
            {t('faceCameras.actions.add')}
          </Button>
        </Space>
      }
    >
      {modalContextHolder}
      <Space direction="vertical" size={12} style={{ width: '100%' }}>
        <Alert
          type="info"
          showIcon
          message={t('faceCameras.notice.effect')}
          description={t('faceCameras.notice.pushStatus', { minutes: pushWindowMinutes, hours: statsHours })}
        />
        {loadError ? (
          <Alert
            type="error"
            showIcon
            message={loadError}
            action={
              <Button size="small" onClick={() => void loadList()}>
                {t('faceCameras.actions.retry')}
              </Button>
            }
          />
        ) : null}
        <MeasuredTable<FaceCamera>
          metricId="system.face-cameras.table-1"
          rowKey="cameraId"
          size="small"
          loading={loading && !data}
          dataSource={data?.items ?? []}
          columns={columns}
          pagination={false}
          scroll={{ x: 1400 }}
          locale={{
            emptyText: loadError ? ' ' : <Empty description={t('faceCameras.empty')} />,
          }}
        />
      </Space>

      <CameraFormModal
        camera={formTarget ?? null}
        open={formTarget !== undefined}
        storeOptions={storeOptions}
        storeOptionsFailed={storeOptionsFailed}
        onCreate={handleCreate}
        onUpdate={handleUpdate}
        describeError={describeError}
        onClose={() => setFormTarget(undefined)}
      />

      {capturesTarget ? (
        <CapturesDrawer key={capturesTarget.cameraId} camera={capturesTarget} onClose={() => setCapturesTarget(null)} />
      ) : null}

      {/* 仅在持有一次性地址时挂载；关闭即把状态置回已清空，组件随之卸载。 */}
      {secret.open ? <SecretRevealModal state={secret} onClose={() => setSecret(closeSecretReveal())} /> : null}
    </PageContainer>
  )
}
