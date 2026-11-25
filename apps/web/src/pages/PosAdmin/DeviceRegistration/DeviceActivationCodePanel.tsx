import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import {
  Alert,
  Button,
  Descriptions,
  Form,
  Input,
  Modal,
  QRCode,
  Segmented,
  Select,
  Space,
  Tag,
  Typography,
  message,
} from 'antd'
import type { ColumnsType } from 'antd/es/table'
import { MeasuredTable } from '../../../components/MeasuredTable'
import {
  createDeviceActivationCode,
  createMobileDeviceActivationCode,
  getDeviceActivationCodes,
  getDeviceActivationManageableStores,
  getMobileDeviceActivationCodes,
  getMobileDeviceActivationManageableAccounts,
  getMobileDeviceActivationManageableStores,
  revokeDeviceActivationCode,
  revokeMobileDeviceActivationCode,
} from '../../../services/deviceActivationCodeService'
import type {
  DeviceActivationCodeCreatePayload,
  DeviceActivationCodeSummary,
  DeviceActivationManageableStore,
  DeviceActivationStatus,
  DeviceActivationSystem,
  MobileDeviceActivationManageableAccount,
} from '../../../types/deviceActivationCode'
import {
  createLatestRequestGuard,
  runLatestGuardedRequest,
} from '../../../utils/latestRequestGuard'

import { formatDeviceSystemLabel } from './deviceSystemOptions'

const PAGE_SIZE = 30
const EMPTY_VALUE = '--'
// AndroidTablet = 安卓平板大屏收银，与 iPadOS 分开签发开通码（平台须精确匹配）。
const POS_DEVICE_SYSTEMS: DeviceActivationSystem[] = ['Windows', 'iPadOS', 'AndroidTablet', 'Android', 'iOS']
const MOBILE_DEVICE_SYSTEMS: DeviceActivationSystem[] = ['Android', 'iOS']
const STATUSES: DeviceActivationStatus[] = ['Available', 'Consumed', 'Expired', 'Revoked']
type ActivationType = 'POS' | 'Mobile'

interface ActivationCodeRow extends DeviceActivationCodeSummary {
  activationType: ActivationType
}

interface CreateFormValues extends DeviceActivationCodeCreatePayload {
  activationType: ActivationType
  targetUserGuid?: string
}

const CREATE_DEFAULTS: Pick<CreateFormValues, 'activationType' | 'deviceSystem' | 'validForMinutes'> = {
  activationType: 'POS',
  deviceSystem: 'Windows',
  validForMinutes: 1440,
}

const STATUS_COLORS: Record<DeviceActivationStatus, string> = {
  Available: 'green',
  Consumed: 'blue',
  Expired: 'default',
  Revoked: 'red',
}

type RevokeFormValues = { reason: string }

interface DeviceActivationCodePanelProps {
  canManage: boolean
  canManageMobile: boolean
}

function formatDateTime(value?: string | null) {
  if (!value) return EMPTY_VALUE
  const timestamp = Date.parse(value)
  return Number.isNaN(timestamp) ? value : new Date(timestamp).toLocaleString()
}

export default function DeviceActivationCodePanel({
  canManage,
  canManageMobile,
}: DeviceActivationCodePanelProps) {
  const { t } = useTranslation()
  const [createForm] = Form.useForm<CreateFormValues>()
  const [revokeForm] = Form.useForm<RevokeFormValues>()
  const [items, setItems] = useState<ActivationCodeRow[]>([])
  const [posStores, setPosStores] = useState<DeviceActivationManageableStore[]>([])
  const [mobileStores, setMobileStores] = useState<DeviceActivationManageableStore[]>([])
  const [mobileAccounts, setMobileAccounts] = useState<MobileDeviceActivationManageableAccount[]>([])
  const [accountsLoading, setAccountsLoading] = useState(false)
  const [loading, setLoading] = useState(false)
  const [refreshVersion, setRefreshVersion] = useState(0)
  const [page, setPage] = useState(1)
  const [total, setTotal] = useState(0)
  const [activationType, setActivationType] = useState<ActivationType>(canManage ? 'POS' : 'Mobile')
  const [storeCode, setStoreCode] = useState<string>()
  const [deviceSystem, setDeviceSystem] = useState<DeviceActivationSystem>()
  const [status, setStatus] = useState<DeviceActivationStatus>()
  const [createOpen, setCreateOpen] = useState(false)
  const [createSaving, setCreateSaving] = useState(false)
  const [createdGrant, setCreatedGrant] = useState<ActivationCodeRow | null>(null)
  const [createdCode, setCreatedCode] = useState<string | null>(null)
  const [revokeGrant, setRevokeGrant] = useState<ActivationCodeRow | null>(null)
  const [revokeSaving, setRevokeSaving] = useState(false)
  const listRequestGuardRef = useRef(createLatestRequestGuard())
  const accountRequestGuardRef = useRef(createLatestRequestGuard())
  const createActivationType = Form.useWatch('activationType', createForm) ?? activationType
  const createStoreCode = Form.useWatch('storeCode', createForm)

  const activationTypeOptions = useMemo(() => [
    ...(canManage
      ? [{ label: t('posAdmin.devices.activation.deviceTypes.POS'), value: 'POS' as const }]
      : []),
    ...(canManageMobile
      ? [{ label: t('posAdmin.devices.activation.deviceTypes.Mobile'), value: 'Mobile' as const }]
      : []),
  ], [canManage, canManageMobile, t])

  const load = useCallback(async () => {
    if ((activationType === 'POS' && !canManage) ||
        (activationType === 'Mobile' && !canManageMobile)) {
      listRequestGuardRef.current.invalidate()
      setItems([])
      setTotal(0)
      setLoading(false)
      return
    }
    const request = activationType === 'Mobile'
      ? getMobileDeviceActivationCodes
      : getDeviceActivationCodes
    await runLatestGuardedRequest(listRequestGuardRef.current, () => request({
        page,
        pageSize: PAGE_SIZE,
        storeCode,
        deviceSystem,
        status,
      }), {
        onStart: () => setLoading(true),
        onSuccess: (result) => {
          setItems(result.items.map((item) => ({ ...item, activationType })))
          setTotal(result.total)
        },
        onError: (error) => {
          console.error(t('posAdmin.devices.activation.loadFailed'), error)
          message.error(t('posAdmin.devices.activation.loadFailed'))
        },
        onSettled: () => setLoading(false),
      })
  }, [activationType, canManage, canManageMobile, deviceSystem, page, status, storeCode, t])

  useEffect(() => {
    const requests: Promise<unknown>[] = []
    if (canManage) {
      requests.push(
        getDeviceActivationManageableStores()
          .then(setPosStores),
      )
    }
    if (canManageMobile) {
      requests.push(
        getMobileDeviceActivationManageableStores()
          .then(setMobileStores),
      )
    }
    void Promise.all(requests).catch((error) => {
      console.error(t('posAdmin.devices.activation.loadStoresFailed'), error)
      message.error(t('posAdmin.devices.activation.loadStoresFailed'))
    })
  }, [canManage, canManageMobile, t])

  useEffect(() => {
    if (!createOpen || createActivationType !== 'Mobile' || !createStoreCode) {
      accountRequestGuardRef.current.invalidate()
      setMobileAccounts([])
      setAccountsLoading(false)
      return
    }

    void runLatestGuardedRequest(
      accountRequestGuardRef.current,
      () => getMobileDeviceActivationManageableAccounts(createStoreCode),
      {
        onStart: () => {
          setMobileAccounts([])
          setAccountsLoading(true)
        },
        onSuccess: setMobileAccounts,
        onError: (error) => {
          setMobileAccounts([])
          console.error(t('posAdmin.devices.activation.loadAccountsFailed'), error)
          message.error(t('posAdmin.devices.activation.loadAccountsFailed'))
        },
        onSettled: () => setAccountsLoading(false),
      },
    )
  }, [createActivationType, createOpen, createStoreCode, t])

  useEffect(() => {
    void load()
  }, [load, refreshVersion])

  useEffect(() => {
    if (activationType === 'POS' && !canManage && canManageMobile) {
      setActivationType('Mobile')
      setPage(1)
    } else if (activationType === 'Mobile' && !canManageMobile && canManage) {
      setActivationType('POS')
      setPage(1)
    }
  }, [activationType, canManage, canManageMobile])

  useEffect(() => () => {
    listRequestGuardRef.current.invalidate()
    accountRequestGuardRef.current.invalidate()
  }, [])

  const stores = activationType === 'Mobile' ? mobileStores : posStores
  const createStores = createActivationType === 'Mobile' ? mobileStores : posStores
  const storeNameMap = useMemo(
    () => Object.fromEntries(
      [...posStores, ...mobileStores].map((store) => [store.storeCode, store.storeName]),
    ),
    [mobileStores, posStores],
  )

  async function submitCreate() {
    try {
      const values = await createForm.validateFields()
      setCreateSaving(true)
      const result = values.activationType === 'Mobile'
        ? await createMobileDeviceActivationCode({
            storeCode: values.storeCode,
            deviceSystem: values.deviceSystem as 'Android' | 'iOS',
            targetUserGuid: values.targetUserGuid!,
            validForMinutes: values.validForMinutes,
            reason: values.reason,
          })
        : await createDeviceActivationCode({
            storeCode: values.storeCode,
            deviceSystem: values.deviceSystem,
            validForMinutes: values.validForMinutes,
            reason: values.reason,
          })
      setCreatedGrant({ ...result.grant, activationType: values.activationType })
      setCreatedCode(result.activationCode)
      setCreateOpen(false)
      createForm.resetFields()
      message.success(t('posAdmin.devices.activation.createSuccess'))
      if (activationType !== values.activationType) {
        setStoreCode(undefined)
        setDeviceSystem(undefined)
        setStatus(undefined)
        setPage(1)
        setActivationType(values.activationType)
        return
      }
      // 当前已经在第一页时主动刷新；否则只切换页码，让现有 effect 使用新页码加载，避免旧闭包重复请求错误页面。
      if (page === 1) {
        void load()
      } else {
        setPage(1)
      }
    } catch (error) {
      if ((error as { errorFields?: unknown[] })?.errorFields) return
      console.error(t('posAdmin.devices.activation.createFailed'), error)
      message.error(t('posAdmin.devices.activation.createFailed'))
    } finally {
      setCreateSaving(false)
    }
  }

  function closeCreatedResult() {
    // 明文只允许停留在本次创建结果的内存中，关闭后不可恢复。
    setCreatedCode(null)
    setCreatedGrant(null)
  }

  async function copyCreatedCode() {
    if (!createdCode) return
    try {
      await navigator.clipboard.writeText(createdCode)
      message.success(t('posAdmin.devices.activation.copySuccess'))
    } catch (error) {
      console.error(t('posAdmin.devices.activation.copyFailed'), error)
      message.error(t('posAdmin.devices.activation.copyFailed'))
    }
  }

  function downloadCreatedQrCode() {
    const canvas = document.querySelector<HTMLCanvasElement>('#device-activation-code-qr canvas')
    if (!canvas || !createdGrant) {
      message.error(t('posAdmin.devices.activation.downloadFailed'))
      return
    }
    const link = document.createElement('a')
    const clientName = createdGrant.activationType === 'Mobile' ? 'mobile' : 'pos'
    link.download = `hb-${clientName}-device-${createdGrant.storeCode}-${createdGrant.deviceSystem}.png`
    link.href = canvas.toDataURL('image/png')
    link.click()
  }

  async function submitRevoke() {
    if (!revokeGrant) return
    try {
      const values = await revokeForm.validateFields()
      setRevokeSaving(true)
      if (revokeGrant.activationType === 'Mobile') {
        await revokeMobileDeviceActivationCode(revokeGrant.grantId, values.reason)
      } else {
        await revokeDeviceActivationCode(revokeGrant.grantId, values.reason)
      }
      setRevokeGrant(null)
      revokeForm.resetFields()
      message.success(t('posAdmin.devices.activation.revokeSuccess'))
      // 以当前筛选重新加载，避免撤销期间切换 POS/Mobile 后由旧闭包覆盖新列表。
      setRefreshVersion((version) => version + 1)
    } catch (error) {
      if ((error as { errorFields?: unknown[] })?.errorFields) return
      console.error(t('posAdmin.devices.activation.revokeFailed'), error)
      message.error(t('posAdmin.devices.activation.revokeFailed'))
    } finally {
      setRevokeSaving(false)
    }
  }

  const columns = useMemo<ColumnsType<ActivationCodeRow>>(() => [
    {
      title: t('posAdmin.devices.activation.deviceType'),
      dataIndex: 'activationType',
      width: 100,
      render: (value: ActivationType) => (
        <Tag color={value === 'Mobile' ? 'cyan' : 'purple'}>
          {t(`posAdmin.devices.activation.deviceTypes.${value}`)}
        </Tag>
      ),
    },
    {
      title: t('column.store'),
      width: 180,
      render: (_value, record) =>
        `${record.storeCode}${(record.storeName || storeNameMap[record.storeCode])
          ? ` / ${record.storeName || storeNameMap[record.storeCode]}`
          : ''}`,
    },
    {
      title: t('posAdmin.devices.deviceSystem'),
      dataIndex: 'deviceSystem',
      width: 110,
      render: (value: DeviceActivationSystem) => <Tag color="geekblue">{formatDeviceSystemLabel(value, t)}</Tag>,
    },
    {
      title: t('posAdmin.devices.activation.targetAccount'),
      width: 210,
      render: (_value, record) => record.activationType === 'Mobile' ? (
        <Space direction="vertical" size={0}>
          <Typography.Text>{record.targetFullName || record.targetUsername || EMPTY_VALUE}</Typography.Text>
          {record.targetFullName && record.targetUsername ? (
            <Typography.Text type="secondary">{record.targetUsername}</Typography.Text>
          ) : null}
        </Space>
      ) : EMPTY_VALUE,
    },
    {
      title: t('column.status'),
      dataIndex: 'status',
      width: 110,
      render: (value: DeviceActivationStatus) => (
        <Tag color={STATUS_COLORS[value]}>
          {t(`posAdmin.devices.activation.statuses.${value}`)}
        </Tag>
      ),
    },
    {
      title: t('posAdmin.devices.activation.expiresAt'),
      dataIndex: 'expiresAtUtc',
      width: 180,
      render: formatDateTime,
    },
    {
      title: t('posAdmin.devices.activation.createdAudit'),
      width: 220,
      render: (_value, record) => (
        <Space direction="vertical" size={0}>
          <Typography.Text>{record.createdBy || EMPTY_VALUE}</Typography.Text>
          <Typography.Text type="secondary">{formatDateTime(record.createdAtUtc)}</Typography.Text>
          <Typography.Text type="secondary" ellipsis={{ tooltip: record.reason }}>
            {record.reason || EMPTY_VALUE}
          </Typography.Text>
        </Space>
      ),
    },
    {
      title: t('posAdmin.devices.activation.consumedAudit'),
      width: 230,
      render: (_value, record) => record.consumedAtUtc ? (
        <Space direction="vertical" size={0}>
          <Typography.Text>{record.consumedDeviceCode || EMPTY_VALUE}</Typography.Text>
          <Typography.Text type="secondary">{formatDateTime(record.consumedAtUtc)}</Typography.Text>
          <Typography.Text type="secondary">{record.consumptionKind || EMPTY_VALUE}</Typography.Text>
        </Space>
      ) : EMPTY_VALUE,
    },
    {
      title: t('column.actions'),
      width: 100,
      fixed: 'right',
      render: (_value, record) => (
        record.activationType === 'Mobile' ? canManageMobile : canManage
      ) && (record.status === 'Available' || record.status === 'Expired') ? (
        <Button danger type="link" onClick={() => setRevokeGrant(record)}>
          {t('posAdmin.devices.activation.revoke')}
        </Button>
      ) : EMPTY_VALUE,
    },
  ], [canManage, canManageMobile, storeNameMap, t])

  const currentDeviceSystems = activationType === 'Mobile'
    ? MOBILE_DEVICE_SYSTEMS
    : POS_DEVICE_SYSTEMS
  const createDeviceSystems = createActivationType === 'Mobile'
    ? MOBILE_DEVICE_SYSTEMS
    : POS_DEVICE_SYSTEMS
  const canManageCurrentType = activationType === 'Mobile' ? canManageMobile : canManage

  return (
    <Space direction="vertical" size={16} style={{ width: '100%' }}>
      <Alert
        type="info"
        showIcon
        message={t('posAdmin.devices.activation.noteTitle')}
        description={t('posAdmin.devices.activation.noteDescription')}
      />

      <Space wrap>
        <Segmented<ActivationType>
          value={activationType}
          options={activationTypeOptions}
          onChange={(value) => {
            setActivationType(value)
            setStoreCode(undefined)
            setDeviceSystem(undefined)
            setStatus(undefined)
            setPage(1)
          }}
        />
        <Select
          allowClear
          showSearch
          optionFilterProp="label"
          placeholder={t('posAdmin.devices.filterByStore')}
          style={{ width: 240 }}
          value={storeCode}
          onChange={(value) => { setPage(1); setStoreCode(value) }}
          options={stores.map((store) => ({
            label: `${store.storeCode} / ${store.storeName}`,
            value: store.storeCode,
          }))}
        />
        <Select<DeviceActivationSystem>
          allowClear
          placeholder={t('posAdmin.devices.filterByDeviceSystem')}
          style={{ width: 150 }}
          value={deviceSystem}
          onChange={(value) => { setPage(1); setDeviceSystem(value) }}
          options={currentDeviceSystems.map((value) => ({ label: formatDeviceSystemLabel(value, t), value }))}
        />
        <Select<DeviceActivationStatus>
          allowClear
          placeholder={t('posAdmin.devices.activation.filterByStatus')}
          style={{ width: 150 }}
          value={status}
          onChange={(value) => { setPage(1); setStatus(value) }}
          options={STATUSES.map((value) => ({
            label: t(`posAdmin.devices.activation.statuses.${value}`),
            value,
          }))}
        />
        <Button onClick={() => void load()}>{t('common.refresh')}</Button>
        {canManageCurrentType ? (
          <Button
            type="primary"
            onClick={() => {
              createForm.setFieldsValue({
                activationType,
                deviceSystem: activationType === 'Mobile' ? 'Android' : 'Windows',
                validForMinutes: 1440,
              })
              setCreateOpen(true)
            }}
          >
            {t('posAdmin.devices.activation.create')}
          </Button>
        ) : null}
      </Space>

      <MeasuredTable<ActivationCodeRow> metricId="pos-admin.device-registration.table-3"
        rowKey="grantId"
        loading={loading}
        columns={columns}
        dataSource={items}
        scroll={{ x: 1510 }}
        pagination={{
          current: page,
          pageSize: PAGE_SIZE,
          total,
          showSizeChanger: false,
          onChange: setPage,
        }}
      />

      <Modal
        open={createOpen}
        title={t('posAdmin.devices.activation.createTitle')}
        okText={t('posAdmin.devices.activation.create')}
        cancelText={t('common.cancel')}
        confirmLoading={createSaving}
        onOk={() => void submitCreate()}
        onCancel={() => {
          setCreateOpen(false)
          createForm.resetFields()
        }}
        destroyOnHidden
      >
        <Form form={createForm} layout="vertical" initialValues={CREATE_DEFAULTS}>
          <Form.Item
            name="activationType"
            label={t('posAdmin.devices.activation.deviceType')}
            rules={[{ required: true }]}
          >
            <Select
              options={activationTypeOptions}
              onChange={(value: ActivationType) => {
                createForm.setFieldsValue({
                  activationType: value,
                  storeCode: undefined,
                  targetUserGuid: undefined,
                  deviceSystem: value === 'Mobile' ? 'Android' : 'Windows',
                })
                setMobileAccounts([])
                setAccountsLoading(false)
              }}
            />
          </Form.Item>
          <Form.Item
            name="storeCode"
            label={t('column.store')}
            rules={[{ required: true, message: t('posAdmin.devices.activation.storeRequired') }]}
          >
            <Select
              showSearch
              optionFilterProp="label"
              onChange={(value) => {
                createForm.setFieldValue('targetUserGuid', undefined)
                setMobileAccounts([])
                setAccountsLoading(Boolean(value) && createActivationType === 'Mobile')
              }}
              options={createStores.map((store) => ({
                label: `${store.storeCode} / ${store.storeName}`,
                value: store.storeCode,
              }))}
            />
          </Form.Item>
          <Form.Item
            name="deviceSystem"
            label={t('posAdmin.devices.deviceSystem')}
            rules={[{ required: true, message: t('posAdmin.devices.deviceSystemRequired') }]}
          >
            <Select options={createDeviceSystems.map((value) => ({ label: formatDeviceSystemLabel(value, t), value }))} />
          </Form.Item>
          {createActivationType === 'Mobile' ? (
            <>
              <Form.Item
                name="targetUserGuid"
                label={t('posAdmin.devices.activation.targetAccount')}
                rules={[{
                  required: true,
                  message: t('posAdmin.devices.activation.targetAccountRequired'),
                }]}
              >
                <Select
                  showSearch
                  optionFilterProp="label"
                  loading={accountsLoading}
                  disabled={!createStoreCode || accountsLoading}
                  options={mobileAccounts.map((account) => ({
                    label: account.fullName
                      ? `${account.fullName} / ${account.username}`
                      : account.username,
                    value: account.userGuid,
                  }))}
                />
              </Form.Item>
              <Alert
                type="info"
                showIcon
                message={t('posAdmin.devices.activation.mobileAccountScopeHint')}
                style={{ marginBottom: 16 }}
              />
            </>
          ) : null}
          <Form.Item
            name="validForMinutes"
            label={t('posAdmin.devices.activation.validFor')}
            rules={[{ required: true }]}
          >
            <Select options={[
              { value: 30, label: t('posAdmin.devices.activation.ttl30Minutes') },
              { value: 120, label: t('posAdmin.devices.activation.ttl2Hours') },
              { value: 1440, label: t('posAdmin.devices.activation.ttl24Hours') },
            ]} />
          </Form.Item>
          <Form.Item
            name="reason"
            label={t('posAdmin.devices.activation.reason')}
            rules={[
              { required: true, message: t('posAdmin.devices.activation.reasonRequired') },
              { max: 200, message: t('posAdmin.devices.activation.reasonTooLong') },
            ]}
          >
            <Input.TextArea rows={3} maxLength={200} showCount />
          </Form.Item>
        </Form>
      </Modal>

      <Modal
        open={Boolean(createdCode && createdGrant)}
        title={t('posAdmin.devices.activation.createdTitle')}
        onCancel={closeCreatedResult}
        destroyOnHidden
        width={680}
        footer={(
          <Space wrap>
            <Button onClick={() => void copyCreatedCode()}>{t('posAdmin.devices.activation.copy')}</Button>
            <Button onClick={downloadCreatedQrCode}>{t('posAdmin.devices.activation.download')}</Button>
            <Button type="primary" onClick={closeCreatedResult}>{t('common.close')}</Button>
          </Space>
        )}
      >
        {createdGrant && createdCode ? (
          <Space direction="vertical" align="center" size={16} style={{ width: '100%' }}>
            <Descriptions bordered column={1} size="small" style={{ width: '100%' }}>
              <Descriptions.Item label={t('posAdmin.devices.activation.deviceType')}>
                {t(`posAdmin.devices.activation.deviceTypes.${createdGrant.activationType}`)}
              </Descriptions.Item>
              <Descriptions.Item label={t('column.store')}>
                {createdGrant.storeCode} / {createdGrant.storeName || storeNameMap[createdGrant.storeCode] || EMPTY_VALUE}
              </Descriptions.Item>
              <Descriptions.Item label={t('posAdmin.devices.deviceSystem')}>
                {formatDeviceSystemLabel(createdGrant.deviceSystem, t)}
              </Descriptions.Item>
              {createdGrant.activationType === 'Mobile' ? (
                <Descriptions.Item label={t('posAdmin.devices.activation.targetAccount')}>
                  {createdGrant.targetFullName
                    ? `${createdGrant.targetFullName} / ${createdGrant.targetUsername}`
                    : createdGrant.targetUsername || EMPTY_VALUE}
                </Descriptions.Item>
              ) : null}
              <Descriptions.Item label={t('posAdmin.devices.activation.expiresAt')}>
                {formatDateTime(createdGrant.expiresAtUtc)}
              </Descriptions.Item>
            </Descriptions>
            <div id="device-activation-code-qr">
              <QRCode value={createdCode} size={256} errorLevel="M" />
            </div>
            <Typography.Text
              code
              copyable={{ text: createdCode }}
              style={{ maxWidth: '100%', overflowWrap: 'anywhere' }}
            >
              {createdCode}
            </Typography.Text>
            <Alert type="warning" showIcon message={t('posAdmin.devices.activation.oneTimeWarning')} />
          </Space>
        ) : null}
      </Modal>

      <Modal
        open={Boolean(revokeGrant)}
        title={t('posAdmin.devices.activation.revokeTitle')}
        okText={t('posAdmin.devices.activation.revoke')}
        okButtonProps={{ danger: true }}
        cancelText={t('common.cancel')}
        confirmLoading={revokeSaving}
        onOk={() => void submitRevoke()}
        onCancel={() => {
          setRevokeGrant(null)
          revokeForm.resetFields()
        }}
        destroyOnHidden
      >
        <Form form={revokeForm} layout="vertical">
          <Form.Item
            name="reason"
            label={t('posAdmin.devices.activation.revokeReason')}
            rules={[
              { required: true, message: t('posAdmin.devices.activation.revokeReasonRequired') },
              { max: 200, message: t('posAdmin.devices.activation.reasonTooLong') },
            ]}
          >
            <Input.TextArea rows={3} maxLength={200} showCount />
          </Form.Item>
        </Form>
      </Modal>
    </Space>
  )
}
