import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import {
  Alert,
  Button,
  Card,
  DatePicker,
  Empty,
  Form,
  Input,
  Modal,
  Popconfirm,
  Radio,
  Select,
  Space,
  Spin,
  Switch,
  Tag,
  Tooltip,
  Typography,
  message,
} from 'antd'
import type { ColumnsType } from 'antd/es/table'
import { PlusOutlined, ReloadOutlined } from '@ant-design/icons'
import dayjs from 'dayjs'
import { MeasuredTable } from '../../../components/MeasuredTable'
import { registerPageMessages } from '../../../i18n/registerPageMessages'
import { appUpdatePolicyService } from '../../../services/appUpdatePolicyService'
import type {
  AppUpdateTargetStoreOption,
  PosIpadOtaAssignment,
  PosIpadOtaRelease,
  PosIpadOtaTargetDeviceOption,
} from '../../../types/appUpdatePolicy'
import {
  executeLatestRequestLane,
  LatestRequestLane,
} from './appUpdatePolicyRequestLogic'
import {
  buildCreatePosIpadOtaAssignmentRequest,
  buildPosIpadOtaAssignmentFormValue,
  buildToggleEnabledRequest,
  buildUpdatePosIpadOtaAssignmentRequest,
  classifyPosIpadOtaAssignmentError,
  filterAssignableReleases,
  isExpiresAtValid,
  OTA_ASSIGNMENT_NOTE_MAX_LENGTH,
  OTA_ASSIGNMENT_RELEASE_MESSAGE_MAX_LENGTH,
  parseUtcTimestamp,
  shouldReloadAfterAssignmentError,
  sortPosIpadOtaAssignments,
  type PosIpadOtaAssignmentErrorKind,
  type PosIpadOtaAssignmentFormValue,
} from './posIpadOtaAssignmentsLogic'
import messagesEn from './posIpadOtaAssignmentsMessages.en.json'
import messagesZh from './posIpadOtaAssignmentsMessages.zh.json'
import { formatAppDownloadLocalDateTime } from './time'

// 覆盖层文案随页面代码块注册，不进入首屏 i18n 包（首屏 gzip 预算很紧）。
registerPageMessages({ zh: messagesZh, en: messagesEn })

const K = 'system.appDownloads.otaAssignments'
const DEVICE_PAGE_SIZE = 20
const DEVICE_SEARCH_DEBOUNCE_MS = 300

interface PosIpadOtaAssignmentsPanelProps {
  canManage: boolean
  /** 已登记的 OTA 发布（由外层统一加载，覆盖与基线共用同一份）。 */
  releases: PosIpadOtaRelease[]
  /** 外层发布列表是否已成功加载；未就绪时不允许新增 / 修改覆盖。 */
  releasesReady: boolean
  storeOptions: AppUpdateTargetStoreOption[]
  /** 全局基线所指发布的 runtime，用来过滤可选发布；基线未指定发布时为 null。 */
  baselineRuntimeVersion: string | null
  /** 外部刷新信号：数值变化时重新加载覆盖列表。 */
  refreshVersion?: number
}

interface LoadStatus {
  loading: boolean
  loaded: boolean
  failed: boolean
}

type ModalState =
  | { mode: 'create'; assignment: null }
  | { mode: 'edit'; assignment: PosIpadOtaAssignment }

function formatReleaseLabel(release: PosIpadOtaRelease) {
  return `${release.channel} · ${release.runtimeVersion} · ${release.iosUpdateId.slice(0, 8)}`
}

function storeLabel(store: AppUpdateTargetStoreOption) {
  return `${store.storeCode} · ${store.storeName}`
}

/** 目标的一句话描述，供删除确认等文案使用。 */
function describeTarget(assignment: PosIpadOtaAssignment, unknownStore: string) {
  const store = assignment.storeName ?? assignment.storeCode ?? unknownStore
  return assignment.targetType === 'device'
    ? `${assignment.deviceNumber ?? `#${assignment.deviceRegistrationId ?? '?'}`} · ${store}`
    : `${store}${assignment.storeCode && assignment.storeName ? ` (${assignment.storeCode})` : ''}`
}

/* ---------------------------------------------------------------------------------------------
 * 新增 / 编辑弹窗：只在打开时挂载，表单与设备候选状态随弹窗生灭，不会残留上一次的选择。
 * ------------------------------------------------------------------------------------------- */

interface AssignmentFormModalProps {
  state: ModalState
  saving: boolean
  releases: PosIpadOtaRelease[]
  storeOptions: AppUpdateTargetStoreOption[]
  baselineRuntimeVersion: string | null
  onCancel: () => void
  onSubmit: (value: PosIpadOtaAssignmentFormValue) => Promise<void>
}

function AssignmentFormModal({
  state,
  saving,
  releases,
  storeOptions,
  baselineRuntimeVersion,
  onCancel,
  onSubmit,
}: AssignmentFormModalProps) {
  const { t } = useTranslation()
  const [form] = Form.useForm<PosIpadOtaAssignmentFormValue>()
  const editing = state.mode === 'edit' ? state.assignment : null
  const initialValues = useMemo(() => {
    const value = buildPosIpadOtaAssignmentFormValue(editing)
    const expiresAt = parseUtcTimestamp(editing?.expiresAtUtc)
    return {
      ...value,
      // 原发布已不在登记列表里（被清理 / 回滚）时不回显裸 ID，留空让用户重新选择。
      releaseId: releases.some((item) => item.id === value.releaseId) ? value.releaseId : null,
      expiresAt: expiresAt ? dayjs(expiresAt) : null,
    }
  }, [editing, releases])
  const targetType = Form.useWatch('targetType', form) ?? initialValues.targetType
  const storeGuid = Form.useWatch('storeGuid', form) ?? null

  const [deviceKeyword, setDeviceKeyword] = useState('')
  const [debouncedKeyword, setDebouncedKeyword] = useState('')
  const [deviceItems, setDeviceItems] = useState<PosIpadOtaTargetDeviceOption[]>([])
  const [devicePage, setDevicePage] = useState(1)
  const [deviceTotal, setDeviceTotal] = useState(0)
  const [deviceLoading, setDeviceLoading] = useState(false)
  const [deviceFailed, setDeviceFailed] = useState(false)
  const deviceLaneRef = useRef(new LatestRequestLane())

  const releaseOptions = useMemo(
    () => filterAssignableReleases(releases, baselineRuntimeVersion, editing?.releaseId)
      .map((release) => ({
        label: formatReleaseLabel(release),
        value: release.id,
      })),
    [baselineRuntimeVersion, editing?.releaseId, releases],
  )

  // 搜索关键字防抖，避免每敲一个字就打一次设备接口。
  useEffect(() => {
    const timer = window.setTimeout(
      () => setDebouncedKeyword(deviceKeyword.trim()),
      DEVICE_SEARCH_DEBOUNCE_MS,
    )
    return () => window.clearTimeout(timer)
  }, [deviceKeyword])

  const loadDevices = useCallback(async (page: number, append: boolean) => {
    setDeviceLoading(true)
    setDeviceFailed(false)
    const result = await executeLatestRequestLane(
      deviceLaneRef.current,
      (signal) => appUpdatePolicyService.getPosIpadOtaTargetDevices(
        {
          storeGuid,
          keyword: debouncedKeyword,
          page,
          pageSize: DEVICE_PAGE_SIZE,
        },
        signal,
      ),
      (response) => {
        setDeviceItems((current) => {
          if (!append) {
            return response.items
          }
          const known = new Set(current.map((item) => item.deviceRegistrationId))
          return [
            ...current,
            ...response.items.filter((item) => !known.has(item.deviceRegistrationId)),
          ]
        })
        setDevicePage(response.page)
        setDeviceTotal(response.total)
      },
    )
    if (result.status === 'failed') {
      console.error('Failed to load iPad OTA target devices', result.error)
      setDeviceFailed(true)
    }
    if (result.status !== 'stale') {
      setDeviceLoading(false)
    }
  }, [debouncedKeyword, storeGuid])

  // 设备模式下，分店 / 关键字变化都从第 1 页重新拉取；切回分店模式则作废进行中的请求。
  useEffect(() => {
    const lane = deviceLaneRef.current
    if (!editing && targetType === 'device') {
      void loadDevices(1, false)
    } else {
      lane.invalidate()
    }
    return () => lane.invalidate()
  }, [editing, loadDevices, targetType])

  const hasMoreDevices = deviceItems.length < deviceTotal

  const deviceOptions = useMemo(
    () => deviceItems.map((device) => {
      const store = device.storeName ?? device.storeCode ?? t(`${K}.unknownStore`)
      const detail = [
        store,
        device.remarks ? `${t(`${K}.form.deviceRemarks`)}: ${device.remarks}` : null,
        device.appVersion ? `${t(`${K}.form.deviceVersion`)}: ${device.appVersion}` : null,
      ].filter(Boolean).join(' · ')
      return {
        value: device.deviceRegistrationId,
        // 选中后输入框里只显示设备编号 + 分店，下拉里显示完整信息。
        displayLabel: `${device.systemDeviceNumber} · ${store}`,
        label: (
          <div>
            <Space size={6}>
              <Typography.Text strong>{device.systemDeviceNumber}</Typography.Text>
              <Tag color={device.isOnline ? 'green' : 'default'} style={{ marginInlineEnd: 0 }}>
                {device.isOnline ? t(`${K}.form.deviceOnline`) : t(`${K}.form.deviceOffline`)}
              </Tag>
            </Space>
            <div>
              <Typography.Text type="secondary" style={{ fontSize: 12 }}>
                {detail}
              </Typography.Text>
            </div>
          </div>
        ),
      }
    }),
    [deviceItems, t],
  )

  async function handleOk() {
    const value = await form.validateFields()
    await onSubmit(value)
  }

  const runtimeHint = baselineRuntimeVersion
    ? t(`${K}.form.releaseRuntimeHint`, { runtime: baselineRuntimeVersion })
    : t(`${K}.form.releaseRuntimeUnknown`)

  return (
    <Modal
      open
      title={editing ? t(`${K}.form.editTitle`) : t(`${K}.form.createTitle`)}
      okText={t('common.save')}
      cancelText={t('common.cancel')}
      confirmLoading={saving}
      onOk={() => void handleOk().catch(() => undefined)}
      onCancel={onCancel}
      maskClosable={false}
      width={640}
      destroyOnHidden
    >
      <Form<PosIpadOtaAssignmentFormValue>
        form={form}
        layout="vertical"
        initialValues={initialValues}
        style={{ marginTop: 16 }}
      >
        {editing ? (
          <Alert
            type="info"
            showIcon
            style={{ marginBottom: 16 }}
            message={describeTarget(editing, t(`${K}.unknownStore`))}
            description={t(`${K}.form.targetImmutable`)}
          />
        ) : (
          <>
            <Form.Item name="targetType" label={t(`${K}.form.targetType`)}>
              <Radio.Group
                optionType="button"
                buttonStyle="solid"
                onChange={() => {
                  // 切换类型时清掉上一类型的目标选择，避免把旧值带进请求。
                  form.setFieldsValue({ storeGuid: null, deviceRegistrationId: null })
                  setDeviceKeyword('')
                  setDebouncedKeyword('')
                }}
                options={[
                  { value: 'store', label: t(`${K}.form.targetTypeStore`) },
                  { value: 'device', label: t(`${K}.form.targetTypeDevice`) },
                ]}
              />
            </Form.Item>

            {targetType === 'store' ? (
              <Form.Item
                name="storeGuid"
                label={t(`${K}.form.store`)}
                rules={[{ required: true, message: t(`${K}.form.storeRequired`) }]}
              >
                <Select
                  showSearch
                  optionFilterProp="label"
                  placeholder={t(`${K}.form.selectStore`)}
                  options={storeOptions.map((store) => ({
                    label: storeLabel(store),
                    value: store.storeGuid,
                  }))}
                />
              </Form.Item>
            ) : (
              <>
                <Form.Item
                  name="storeGuid"
                  label={t(`${K}.form.deviceStore`)}
                  extra={t(`${K}.form.deviceStoreHelp`)}
                >
                  <Select
                    allowClear
                    showSearch
                    optionFilterProp="label"
                    placeholder={t(`${K}.form.allStores`)}
                    options={storeOptions.map((store) => ({
                      label: storeLabel(store),
                      value: store.storeGuid,
                    }))}
                    onChange={() => form.setFieldsValue({ deviceRegistrationId: null })}
                  />
                </Form.Item>
                <Form.Item
                  name="deviceRegistrationId"
                  label={t(`${K}.form.device`)}
                  rules={[{ required: true, message: t(`${K}.form.deviceRequired`) }]}
                >
                  <Select
                    showSearch
                    // 过滤交给服务端，本地不再二次过滤。
                    filterOption={false}
                    optionLabelProp="displayLabel"
                    placeholder={t(`${K}.form.selectDevice`)}
                    loading={deviceLoading}
                    options={deviceOptions}
                    onSearch={setDeviceKeyword}
                    notFoundContent={
                      deviceLoading
                        ? <Spin size="small" />
                        : deviceFailed
                          ? (
                            <Space direction="vertical" size={4}>
                              <Typography.Text type="danger">
                                {t(`${K}.form.deviceLoadFailed`)}
                              </Typography.Text>
                              <Button size="small" onClick={() => void loadDevices(1, false)}>
                                {t('system.appDownloads.updatePolicy.retry')}
                              </Button>
                            </Space>
                          )
                          : <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={t(`${K}.form.deviceSearchEmpty`)} />
                    }
                    popupRender={(menu) => (
                      <>
                        {menu}
                        {hasMoreDevices ? (
                          <div style={{ padding: 8, textAlign: 'center' }}>
                            <Button
                              type="link"
                              size="small"
                              loading={deviceLoading}
                              // 阻止 mousedown 抢走 Select 焦点，否则点击「加载更多」会先收起下拉。
                              onMouseDown={(event) => event.preventDefault()}
                              onClick={() => void loadDevices(devicePage + 1, true)}
                            >
                              {t(`${K}.form.deviceLoadMore`, {
                                loaded: deviceItems.length,
                                total: deviceTotal,
                              })}
                            </Button>
                          </div>
                        ) : null}
                      </>
                    )}
                  />
                </Form.Item>
              </>
            )}
          </>
        )}

        <Form.Item
          name="releaseId"
          label={t(`${K}.form.release`)}
          extra={runtimeHint}
          rules={[{ required: true, message: t(`${K}.form.releaseRequired`) }]}
        >
          <Select
            showSearch
            optionFilterProp="label"
            placeholder={t(`${K}.form.selectRelease`)}
            options={releaseOptions}
          />
        </Form.Item>
        <Form.Item
          name="forceUpdate"
          label={t(`${K}.form.forceUpdate`)}
          valuePropName="checked"
        >
          <Switch />
        </Form.Item>
        <Form.Item name="releaseMessage" label={t(`${K}.form.releaseMessage`)}>
          <Input.TextArea
            rows={3}
            maxLength={OTA_ASSIGNMENT_RELEASE_MESSAGE_MAX_LENGTH}
            showCount
            placeholder={t(`${K}.form.releaseMessagePlaceholder`)}
          />
        </Form.Item>
        <Form.Item name="note" label={t(`${K}.form.note`)}>
          <Input
            maxLength={OTA_ASSIGNMENT_NOTE_MAX_LENGTH}
            showCount
            placeholder={t(`${K}.form.notePlaceholder`)}
          />
        </Form.Item>
        <Form.Item
          name="expiresAt"
          label={t(`${K}.form.expiresAt`)}
          extra={t(`${K}.form.expiresAtHelp`)}
          dependencies={['enabled']}
          rules={[
            ({ getFieldValue }) => ({
              // 与后端一致：只有启用的覆盖要求到期时间晚于当前；停用的可保留过去时间。
              validator: async (_rule, value) => {
                if (!isExpiresAtValid(Boolean(getFieldValue('enabled')), value, Date.now())) {
                  throw new Error(t(`${K}.form.expiresAtInvalid`))
                }
              },
            }),
          ]}
        >
          <DatePicker
            showTime={{ format: 'HH:mm' }}
            format="YYYY-MM-DD HH:mm"
            style={{ width: '100%' }}
          />
        </Form.Item>
        <Form.Item name="enabled" label={t(`${K}.form.enabled`)} valuePropName="checked">
          <Switch
            checkedChildren={t(`${K}.enabled`)}
            unCheckedChildren={t(`${K}.disabled`)}
          />
        </Form.Item>
      </Form>
    </Modal>
  )
}

/* ---------------------------------------------------------------------------------------------
 * 覆盖列表面板
 * ------------------------------------------------------------------------------------------- */

export default function PosIpadOtaAssignmentsPanel({
  canManage,
  releases,
  releasesReady,
  storeOptions,
  baselineRuntimeVersion,
  refreshVersion = 0,
}: PosIpadOtaAssignmentsPanelProps) {
  const { t } = useTranslation()
  const [assignments, setAssignments] = useState<PosIpadOtaAssignment[]>([])
  const [status, setStatus] = useState<LoadStatus>({ loading: false, loaded: false, failed: false })
  const [modalState, setModalState] = useState<ModalState | null>(null)
  const [saving, setSaving] = useState(false)
  const [busyId, setBusyId] = useState<string | null>(null)
  const laneRef = useRef(new LatestRequestLane())

  const loadAssignments = useCallback(async () => {
    setStatus((current) => ({ ...current, loading: true, failed: false }))
    const result = await executeLatestRequestLane(
      laneRef.current,
      (signal) => appUpdatePolicyService.getPosIpadOtaAssignments(signal),
      (rows) => setAssignments(sortPosIpadOtaAssignments(rows)),
    )
    if (result.status === 'applied') {
      setStatus({ loading: false, loaded: true, failed: false })
    } else if (result.status === 'failed') {
      console.error('Failed to load iPad OTA assignments', result.error)
      setStatus((current) => ({ ...current, loading: false, failed: true }))
    }
    return result.status
  }, [])

  useEffect(() => {
    const lane = laneRef.current
    void loadAssignments()
    return () => lane.invalidate()
  }, [loadAssignments, refreshVersion])

  /**
   * 统一处理写操作失败：按错误类型给出明确提示；列表已过期的几类错误会重新拉取权威状态，
   * 并且绝不自动重放本次写入。返回值表示弹窗是否应当关闭（版本冲突 / 已删除时表单版本已失效）。
   */
  async function handleMutationError(
    error: unknown,
    fallbackKey: 'saveFailed' | 'deleteFailed',
  ) {
    console.error('iPad OTA assignment mutation failed', error)
    const kind: PosIpadOtaAssignmentErrorKind = classifyPosIpadOtaAssignmentError(error)
    const reloadStatus = shouldReloadAfterAssignmentError(kind) ? await loadAssignments() : null

    switch (kind) {
      case 'version-conflict':
        if (reloadStatus === 'applied') {
          message.warning(t(`${K}.message.versionConflict`))
        } else {
          message.error(t(`${K}.message.versionConflictReloadFailed`))
        }
        return true
      case 'not-found':
        message.warning(t(`${K}.message.notFound`))
        return true
      case 'target-exists':
        message.warning(t(`${K}.message.targetExists`))
        return false
      case 'expires-invalid':
        message.error(t(`${K}.message.expiresInvalid`))
        return false
      case 'release-invalid':
        message.error(t(`${K}.message.releaseInvalid`))
        return false
      case 'release-required':
        message.error(t(`${K}.message.releaseRequired`))
        return false
      case 'message-too-long':
        message.error(t(`${K}.message.messageTooLong`))
        return false
      case 'note-too-long':
        message.error(t(`${K}.message.noteTooLong`))
        return false
      case 'target-invalid':
        message.error(t(`${K}.message.targetInvalid`))
        return false
      default:
        message.error(t(`${K}.message.${fallbackKey}`))
        return false
    }
  }

  async function submitForm(value: PosIpadOtaAssignmentFormValue) {
    if (!modalState) {
      return
    }

    // 保存前作废进行中的加载，避免旧响应在保存后覆盖列表。
    laneRef.current.invalidate()
    setSaving(true)
    try {
      if (modalState.mode === 'edit') {
        const { assignment } = modalState
        await appUpdatePolicyService.updatePosIpadOtaAssignment(
          assignment.id,
          buildUpdatePosIpadOtaAssignmentRequest(value, assignment.policyVersion),
        )
        message.success(t(`${K}.message.updateSuccess`))
      } else {
        await appUpdatePolicyService.createPosIpadOtaAssignment(
          buildCreatePosIpadOtaAssignmentRequest(value),
        )
        message.success(t(`${K}.message.createSuccess`))
      }
      setModalState(null)
      await loadAssignments()
    } catch (error) {
      const shouldClose = await handleMutationError(error, 'saveFailed')
      if (shouldClose) {
        setModalState(null)
      }
    } finally {
      setSaving(false)
    }
  }

  async function toggleEnabled(assignment: PosIpadOtaAssignment, enabled: boolean) {
    // 启用一条已过期的覆盖必须先给新的到期时间，交给编辑弹窗处理。
    if (enabled && assignment.isExpired) {
      setModalState({ mode: 'edit', assignment: { ...assignment, enabled: true } })
      return
    }

    laneRef.current.invalidate()
    setBusyId(assignment.id)
    try {
      await appUpdatePolicyService.updatePosIpadOtaAssignment(
        assignment.id,
        buildToggleEnabledRequest(assignment, enabled),
      )
      message.success(t(`${K}.message.${enabled ? 'toggleEnabledSuccess' : 'toggleDisabledSuccess'}`))
      await loadAssignments()
    } catch (error) {
      await handleMutationError(error, 'saveFailed')
    } finally {
      setBusyId(null)
    }
  }

  async function deleteAssignment(assignment: PosIpadOtaAssignment) {
    laneRef.current.invalidate()
    setBusyId(assignment.id)
    try {
      await appUpdatePolicyService.deletePosIpadOtaAssignment(
        assignment.id,
        assignment.policyVersion,
      )
      message.success(t(`${K}.message.deleteSuccess`))
      await loadAssignments()
    } catch (error) {
      await handleMutationError(error, 'deleteFailed')
    } finally {
      setBusyId(null)
    }
  }

  const columns = useMemo<ColumnsType<PosIpadOtaAssignment>>(() => {
    const unknownStore = t(`${K}.unknownStore`)
    const base: ColumnsType<PosIpadOtaAssignment> = [
      {
        title: t(`${K}.columns.target`),
        key: 'target',
        width: 240,
        render: (_, record) => {
          const muted = !record.enabled || record.isExpired
          const type = muted ? 'secondary' : undefined
          if (record.targetType === 'device') {
            const detail = [
              record.storeName ?? record.storeCode ?? unknownStore,
              record.deviceRemarks,
            ].filter(Boolean).join(' · ')
            return (
              <Space direction="vertical" size={0}>
                <Typography.Text strong type={type}>
                  {record.deviceNumber ?? `#${record.deviceRegistrationId ?? '?'}`}
                </Typography.Text>
                <Typography.Text type="secondary" style={{ fontSize: 12 }}>
                  {detail}
                  {record.deviceAppVersion ? ` · v${record.deviceAppVersion}` : ''}
                </Typography.Text>
              </Space>
            )
          }
          return (
            <Space direction="vertical" size={0}>
              <Typography.Text strong type={type}>
                {record.storeName ?? unknownStore}
              </Typography.Text>
              {record.storeCode ? (
                <Typography.Text type="secondary" style={{ fontSize: 12 }}>
                  {record.storeCode}
                </Typography.Text>
              ) : null}
            </Space>
          )
        },
      },
      {
        title: t(`${K}.columns.targetType`),
        dataIndex: 'targetType',
        width: 90,
        render: (value: PosIpadOtaAssignment['targetType']) => (
          <Tag color={value === 'device' ? 'purple' : 'geekblue'}>
            {t(`${K}.targetType.${value}`)}
          </Tag>
        ),
      },
      {
        title: t(`${K}.columns.release`),
        key: 'release',
        width: 280,
        render: (_, record) => {
          const { release } = record
          if (!release) {
            return (
              <Typography.Text type="warning">
                {t(`${K}.releaseMissing`, { id: record.releaseId.slice(0, 8) })}
              </Typography.Text>
            )
          }
          return (
            <Space direction="vertical" size={0}>
              <Typography.Text strong>{release.channel}</Typography.Text>
              <Typography.Text type="secondary" style={{ fontSize: 12 }}>
                {t(`${K}.runtime`)} {release.runtimeVersion}
                {' · '}
                {formatAppDownloadLocalDateTime(release.publishedAtUtc)}
              </Typography.Text>
            </Space>
          )
        },
      },
      {
        title: t(`${K}.columns.forceUpdate`),
        dataIndex: 'forceUpdate',
        width: 90,
        render: (value: boolean) => (
          <Tag color={value ? 'red' : 'default'}>
            {value ? t(`${K}.forceYes`) : t(`${K}.forceNo`)}
          </Tag>
        ),
      },
      {
        title: t(`${K}.columns.expiresAt`),
        key: 'expiresAt',
        width: 190,
        render: (_, record) => {
          if (!record.expiresAtUtc) {
            return <Typography.Text type="secondary">{t(`${K}.noExpiry`)}</Typography.Text>
          }
          return (
            <Space size={6} wrap>
              <Typography.Text type={record.isExpired ? 'secondary' : undefined}>
                {formatAppDownloadLocalDateTime(record.expiresAtUtc)}
              </Typography.Text>
              {record.isExpired ? <Tag>{t(`${K}.expired`)}</Tag> : null}
            </Space>
          )
        },
      },
      {
        title: t(`${K}.columns.status`),
        dataIndex: 'enabled',
        width: 90,
        render: (value: boolean) => (
          <Tag color={value ? 'green' : 'default'}>
            {value ? t(`${K}.enabled`) : t(`${K}.disabled`)}
          </Tag>
        ),
      },
      {
        title: t(`${K}.columns.note`),
        dataIndex: 'note',
        width: 180,
        ellipsis: { showTitle: false },
        render: (value: string | null) => (value
          ? (
            <Tooltip title={value}>
              <span>{value}</span>
            </Tooltip>
          )
          : '--'),
      },
      {
        title: t(`${K}.columns.updated`),
        key: 'updated',
        width: 190,
        render: (_, record) => (
          <Space direction="vertical" size={0}>
            <Typography.Text>{record.updatedBy || '--'}</Typography.Text>
            <Typography.Text type="secondary" style={{ fontSize: 12 }}>
              {formatAppDownloadLocalDateTime(record.updatedAt)}
            </Typography.Text>
          </Space>
        ),
      },
    ]

    if (!canManage) {
      return base
    }

    base.push({
      title: t(`${K}.columns.actions`),
      key: 'actions',
      fixed: 'right',
      width: 210,
      render: (_, record) => {
        const busy = busyId === record.id
        return (
          <Space size={0}>
            <Button
              type="link"
              size="small"
              disabled={busy || !releasesReady}
              onClick={() => setModalState({ mode: 'edit', assignment: record })}
            >
              {t(`${K}.edit`)}
            </Button>
            <Button
              type="link"
              size="small"
              loading={busy}
              onClick={() => void toggleEnabled(record, !record.enabled)}
            >
              {record.enabled ? t(`${K}.disable`) : t(`${K}.enable`)}
            </Button>
            <Popconfirm
              title={t(`${K}.deleteConfirmTitle`)}
              description={t(`${K}.deleteConfirmDescription`, {
                target: describeTarget(record, unknownStore),
              })}
              okText={t(`${K}.deleteConfirmOk`)}
              cancelText={t('common.cancel')}
              okButtonProps={{ danger: true }}
              onConfirm={() => void deleteAssignment(record)}
            >
              <Button type="link" size="small" danger disabled={busy}>
                {t(`${K}.delete`)}
              </Button>
            </Popconfirm>
          </Space>
        )
      },
    })
    return base
    // toggleEnabled / deleteAssignment 只依赖稳定的 setState 与 service，随渲染重建即可。
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [busyId, canManage, releasesReady, t])

  return (
    <Card
      size="small"
      title={t(`${K}.title`)}
      extra={(
        <Space size={8}>
          <Button
            icon={<ReloadOutlined />}
            loading={status.loading}
            onClick={() => void loadAssignments()}
          >
            {t('common.refresh')}
          </Button>
          {canManage ? (
            <Button
              type="primary"
              icon={<PlusOutlined />}
              disabled={!releasesReady}
              onClick={() => setModalState({ mode: 'create', assignment: null })}
            >
              {t(`${K}.add`)}
            </Button>
          ) : null}
        </Space>
      )}
    >
      <Space direction="vertical" size={12} style={{ width: '100%' }}>
        <Typography.Text type="secondary">{t(`${K}.description`)}</Typography.Text>
        {canManage && !releasesReady ? (
          <Alert type="warning" showIcon message={t(`${K}.releasesUnavailable`)} />
        ) : null}
        {status.failed ? (
          <Alert
            type="error"
            showIcon
            message={t(`${K}.loadFailed`)}
            action={(
              <Button size="small" onClick={() => void loadAssignments()}>
                {t('system.appDownloads.updatePolicy.retry')}
              </Button>
            )}
          />
        ) : null}
        <MeasuredTable<PosIpadOtaAssignment>
          metricId="system.app-downloads.pos-ipad-ota-assignments.table-1"
          rowKey="id"
          size="small"
          columns={columns}
          dataSource={assignments}
          loading={status.loading}
          scroll={{ x: 1560 }}
          locale={{
            // 加载失败时上方已有错误提示，表格里不再误报「暂无覆盖」。
            emptyText: status.failed
              ? <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={t(`${K}.loadFailed`)} />
              : <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={t(`${K}.empty`)} />,
          }}
          pagination={{ pageSize: 10, hideOnSinglePage: true }}
        />
      </Space>

      {modalState ? (
        <AssignmentFormModal
          // 切换编辑对象 / 版本时重建弹窗，保证表单初值与版本号一致。
          key={`${modalState.mode}:${modalState.assignment?.id ?? 'new'}:${modalState.assignment?.policyVersion ?? 0}`}
          state={modalState}
          saving={saving}
          releases={releases}
          storeOptions={storeOptions}
          baselineRuntimeVersion={baselineRuntimeVersion}
          onCancel={() => setModalState(null)}
          onSubmit={submitForm}
        />
      ) : null}
    </Card>
  )
}
