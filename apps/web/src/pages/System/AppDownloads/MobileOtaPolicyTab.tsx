import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import {
  Alert,
  Button,
  Card,
  Col,
  Descriptions,
  Empty,
  Form,
  Input,
  Modal,
  Row,
  Segmented,
  Select,
  Space,
  Switch,
  Tabs,
  Tag,
  Timeline,
  Typography,
  message,
} from 'antd'
import type { ColumnsType } from 'antd/es/table'
import {
  LinkOutlined,
  ReloadOutlined,
  SaveOutlined,
} from '@ant-design/icons'
import { mobileOtaPolicyService } from '../../../services/mobileOtaPolicyService'
import { getMobileAppOtaUpdates } from '../../../services/mobileAppBuildService'
import { MeasuredTable } from '../../../components/MeasuredTable'
import type { MobileAppOtaUpdate } from '../../../types/mobileAppBuild'
import type {
  AppOtaRelease,
  MobileOtaAdditionalTarget,
  MobileOtaEnvironment,
  MobileOtaPlatform,
  MobileOtaPolicy,
  MobileOtaPolicyRevision,
} from '../../../types/mobileOtaPolicy'
import { isAppUpdatePolicyVersionConflict } from './appUpdatePolicyLogic'
import {
  executeLatestRequestLane,
  LatestRequestLane,
  savePolicyWithConflictReload,
} from './appUpdatePolicyRequestLogic'
import {
  buildMobileOtaPolicyRequest,
  formatMobileOtaReleaseLabel,
  isMobileOtaReleaseCompatibleWithLane,
  parseMobileOtaRevisionSnapshot,
  resolveMobileOtaReleaseActivation,
  type MobileOtaPolicyFormValue,
} from './mobileOtaPolicyLogic'
import { formatAppDownloadLocalDateTime } from './time'

interface MobileOtaPolicyTabProps {
  canManage: boolean
  refreshVersion?: number
  /** 固定平台时不再显示平台页签（版本发布中心按平台拆成独立轨道）。 */
  platform?: MobileOtaPlatform
  /** 策略保存成功后通知外层刷新摘要。 */
  onChanged?: () => void
}

interface LoadStatus {
  loading: boolean
  loaded: boolean
  failed: boolean
}

const INITIAL_LOAD_STATUS: LoadStatus = {
  loading: false,
  loaded: false,
  failed: false,
}

function safeExternalUrl(value?: string | null) {
  if (!value) {
    return null
  }
  try {
    const url = new URL(value)
    return url.protocol === 'https:' ? url.toString() : null
  } catch {
    return null
  }
}

function shortIdentity(value?: string | null) {
  return value ? value.slice(0, 8) : '--'
}

function MobileOtaLane({
  canManage,
  environment,
  platform,
  refreshVersion,
  onChanged,
}: {
  canManage: boolean
  environment: MobileOtaEnvironment
  platform: MobileOtaPlatform
  refreshVersion: number
  onChanged?: () => void
}) {
  const { t } = useTranslation()
  const [form] = Form.useForm<MobileOtaPolicyFormValue>()
  const [releases, setReleases] = useState<AppOtaRelease[]>([])
  const [policy, setPolicy] = useState<MobileOtaPolicy | null>(null)
  const [revisions, setRevisions] = useState<MobileOtaPolicyRevision[]>([])
  const [status, setStatus] = useState<LoadStatus>({ ...INITIAL_LOAD_STATUS })
  const [saving, setSaving] = useState(false)
  const requestLaneRef = useRef(new LatestRequestLane())
  const enabled = Form.useWatch('enabled', form) ?? false
  const required = Form.useWatch('required', form) ?? false
  const targetReleaseId = Form.useWatch('targetReleaseId', form) ?? null

  const loadLane = useCallback(async () => {
    setStatus((current) => ({ ...current, loading: true, failed: false }))
    const result = await executeLatestRequestLane(
      requestLaneRef.current,
      (signal) => Promise.all([
        mobileOtaPolicyService.getReleases(environment, platform, signal),
        mobileOtaPolicyService.getPolicy(environment, platform, signal),
        mobileOtaPolicyService.getRevisions(environment, platform, signal),
      ]),
      ([nextReleases, nextPolicy, nextRevisions]) => {
        // Web 再做一次 lane 约束，避免服务端联调错误把跨环境候选带入策略表单。
        setReleases(nextReleases.filter((release) => (
          isMobileOtaReleaseCompatibleWithLane(release, environment, platform)
        )))
        setPolicy(nextPolicy)
        setRevisions(nextRevisions)
        form.setFieldsValue({
          enabled: nextPolicy.enabled,
          required: nextPolicy.required,
          targetReleaseId: nextPolicy.targetReleaseId,
          releaseMessage: nextPolicy.releaseMessage,
        })
      },
    )

    if (result.status === 'applied') {
      setStatus({ loading: false, loaded: true, failed: false })
    } else if (result.status === 'failed') {
      console.error('Failed to load Mobile OTA policy lane', {
        environment,
        platform,
        error: result.error,
      })
      setStatus((current) => ({ ...current, loading: false, failed: true }))
      message.error(t('system.appDownloads.updatePolicy.mobileOta.loadFailed'))
    }
    return result.status
  }, [environment, form, platform, t])

  useEffect(() => {
    void loadLane()
    return () => requestLaneRef.current.invalidate()
  }, [loadLane, refreshVersion])

  const selectedRelease = useMemo(
    () => releases.find((release) => release.id === targetReleaseId)
      ?? (policy?.targetRelease?.id === targetReleaseId ? policy.targetRelease : null),
    [policy?.targetRelease, releases, targetReleaseId],
  )
  const selectableReleases = useMemo(
    () => releases.filter((release) => !release.legacy),
    [releases],
  )
  const domainReady = status.loaded && !status.loading && !status.failed && policy !== null
  const additionalTargets = useMemo(() => policy?.additionalTargets ?? [], [policy?.additionalTargets])
  // 附加目标按「Runtime · Update Group 前 8 位 · Commit」展示，便于和上方发布事实表逐行对照。
  const formatAdditionalTarget = useCallback((target: MobileOtaAdditionalTarget) => {
    const release = releases.find((item) => item.id === target.targetReleaseId)
    const runtime = target.targetRuntimeVersion ?? release?.runtimeVersion ?? '--'
    return release
      ? `${runtime} · ${shortIdentity(release.updateGroupId)} · ${shortIdentity(release.gitCommitHash)}`
      : `${runtime} · ${shortIdentity(target.targetReleaseId)}`
  }, [releases])

  const releaseColumns: ColumnsType<AppOtaRelease> = useMemo(() => [
    {
      title: t('system.appDownloads.updatePolicy.status'),
      width: 110,
      render: (_, release) => {
        // 主目标与按 Runtime 的附加目标都在投放，附加目标额外标出所属 Runtime。
        const activation = resolveMobileOtaReleaseActivation(release.id, policy)
        if (activation?.kind === 'primary') {
          return <Tag color="processing">{t('system.appDownloads.updatePolicy.active')}</Tag>
        }
        if (activation?.kind === 'additional') {
          return (
            <Tag color="processing">
              {t('system.appDownloads.updatePolicy.mobileOta.activeAdditional', {
                runtime: activation.runtimeVersion ?? release.runtimeVersion,
              })}
            </Tag>
          )
        }
        return release.legacy
          ? <Tag>{t('system.appDownloads.updatePolicy.mobileOta.legacy')}</Tag>
          : <Tag color="success">{t('system.appDownloads.updatePolicy.registered')}</Tag>
      },
    },
    {
      title: t('system.appDownloads.updatePolicy.runtime'),
      dataIndex: 'runtimeVersion',
      width: 120,
    },
    {
      title: t('system.appDownloads.updatePolicy.mobileOta.releaseChannel'),
      dataIndex: 'releaseChannel',
      width: 310,
      render: (value: string) => <Typography.Text copyable>{value || '--'}</Typography.Text>,
    },
    {
      title: 'Update ID',
      dataIndex: 'updateId',
      width: 260,
      render: (value: string) => <Typography.Text copyable>{value || '--'}</Typography.Text>,
    },
    {
      title: t('system.appDownloads.updatePolicy.updateGroupId'),
      dataIndex: 'updateGroupId',
      width: 260,
      render: (value: string) => <Typography.Text copyable>{value || '--'}</Typography.Text>,
    },
    {
      title: 'Commit',
      dataIndex: 'gitCommitHash',
      width: 110,
      render: (value: string | null) => shortIdentity(value),
    },
    {
      title: t('system.appDownloads.updatePolicy.releaseMessage'),
      dataIndex: 'message',
      width: 260,
      ellipsis: true,
      render: (value: string | null) => value || '--',
    },
    {
      title: t('system.appDownloads.updatePolicy.mobileOta.rollbackSource'),
      width: 180,
      render: (_, release) => release.isRollback
        ? (
            <Typography.Text copyable={{ text: release.rollbackOfReleaseId || '' }}>
              {shortIdentity(release.rollbackOfReleaseId)}
            </Typography.Text>
          )
        : '--',
    },
    {
      title: t('system.appDownloads.updatePolicy.publishedAt'),
      dataIndex: 'publishedAtUtc',
      width: 180,
      render: (value: string) => formatAppDownloadLocalDateTime(value),
    },
    {
      title: t('system.appDownloads.updatePolicy.updatedBy'),
      dataIndex: 'createdBy',
      width: 130,
      render: (value: string | null) => value || '--',
    },
    {
      title: t('common.actions'),
      width: 130,
      fixed: 'right',
      render: (_, release) => {
        const dashboardUrl = safeExternalUrl(release.dashboardUrl)
        return dashboardUrl ? (
          <Button
            size="small"
            icon={<LinkOutlined />}
            onClick={() => window.open(dashboardUrl, '_blank', 'noopener,noreferrer')}
          >
            Dashboard
          </Button>
        ) : '--'
      },
    },
  ], [policy, t])

  async function savePolicy(value: MobileOtaPolicyFormValue) {
    if (!policy) {
      return
    }
    requestLaneRef.current.invalidate()
    setStatus((current) => ({ ...current, loading: false }))
    setSaving(true)
    try {
      const result = await savePolicyWithConflictReload(
        () => mobileOtaPolicyService.savePolicy(
          environment,
          platform,
          buildMobileOtaPolicyRequest(value, policy.policyVersion),
        ),
        loadLane,
        isAppUpdatePolicyVersionConflict,
      )
      if (result !== 'saved') {
        const key = result === 'conflict-reloaded'
          ? 'system.appDownloads.updatePolicy.versionConflict'
          : result === 'conflict-reload-superseded'
            ? 'system.appDownloads.updatePolicy.versionConflictReloadSuperseded'
            : 'system.appDownloads.updatePolicy.versionConflictReloadFailed'
        message.warning(t(key))
        return
      }
      message.success(t('system.appDownloads.updatePolicy.saveSuccess'))
      onChanged?.()
      await loadLane()
    } catch (error) {
      console.error('Failed to save Mobile OTA policy', error)
      message.error(t('system.appDownloads.updatePolicy.saveFailed'))
      throw error
    } finally {
      setSaving(false)
    }
  }

  function confirmSave(value: MobileOtaPolicyFormValue) {
    if (!policy) {
      return
    }
    const release = releases.find((item) => item.id === value.targetReleaseId)
    Modal.confirm({
      title: value.enabled
        ? t('system.appDownloads.updatePolicy.mobileOta.confirmTitle')
        : t('system.appDownloads.updatePolicy.disableConfirmTitle'),
      content: value.enabled ? (
        <Space direction="vertical" size={12} style={{ width: '100%' }}>
          <Alert
            type={value.required ? 'warning' : 'info'}
            showIcon
            message={value.required
              ? t('system.appDownloads.updatePolicy.mobileOta.requiredWarning')
              : t('system.appDownloads.updatePolicy.mobileOta.optionalNotice')}
            description={t('system.appDownloads.updatePolicy.mobileOta.compatibilityBoundary')}
          />
          <Descriptions bordered size="small" column={1}>
            <Descriptions.Item label={t('system.appDownloads.updatePolicy.environment')}>
              {environment}
            </Descriptions.Item>
            <Descriptions.Item label={t('system.appDownloads.updatePolicy.mobileOta.platform')}>
              {platform === 'ios' ? 'iOS' : 'Android'}
            </Descriptions.Item>
            <Descriptions.Item label={t('system.appDownloads.updatePolicy.release')}>
              {release ? formatMobileOtaReleaseLabel(release) : value.targetReleaseId || '--'}
            </Descriptions.Item>
            {/* 表单不提交附加目标，后端按「未提交即保留」处理；这里明确告知保存不会改动它们。 */}
            <Descriptions.Item label={t('system.appDownloads.updatePolicy.mobileOta.additionalTargetsKept')}>
              {additionalTargets.length > 0
                ? additionalTargets.map(formatAdditionalTarget).join('；')
                : t('system.appDownloads.updatePolicy.mobileOta.additionalTargetsNone')}
            </Descriptions.Item>
          </Descriptions>
        </Space>
      ) : t('system.appDownloads.updatePolicy.disableConfirmDescription'),
      okText: t('common.confirm'),
      cancelText: t('common.cancel'),
      okButtonProps: value.required ? { danger: true } : undefined,
      width: 600,
      onOk: () => savePolicy(value),
    })
  }

  const revisionItems = revisions.map((revision) => {
    const snapshot = parseMobileOtaRevisionSnapshot(revision.snapshotJson)
    const snapshotEnabled = snapshot.enabled === true
    const snapshotRequired = snapshot.required === true
    const snapshotTarget = typeof snapshot.targetReleaseId === 'string'
      ? snapshot.targetReleaseId
      : null
    return {
      color: snapshotEnabled ? (snapshotRequired ? 'red' : 'blue') : 'gray',
      children: (
        <Space direction="vertical" size={2} style={{ width: '100%' }}>
          <Space size={6} wrap>
            <Tag>{t('system.appDownloads.updatePolicy.policyVersion')}: {revision.policyVersion}</Tag>
            <Tag color={snapshotEnabled ? 'processing' : 'default'}>
              {snapshotEnabled
                ? t('system.appDownloads.updatePolicy.enabled')
                : t('system.appDownloads.updatePolicy.disabled')}
            </Tag>
            {snapshotEnabled ? (
              <Tag color={snapshotRequired ? 'error' : 'success'}>
                {snapshotRequired
                  ? t('system.appDownloads.updatePolicy.mobileOta.required')
                  : t('system.appDownloads.updatePolicy.mobileOta.optional')}
              </Tag>
            ) : null}
          </Space>
          <Typography.Text type="secondary">
            {formatAppDownloadLocalDateTime(revision.createdAt)} · {revision.createdBy || '--'} · {revision.operation || '--'}
          </Typography.Text>
          <Typography.Text copyable={{ text: revision.snapshotJson }}>
            {t('system.appDownloads.updatePolicy.mobileOta.snapshotTarget')}: {shortIdentity(snapshotTarget)}
          </Typography.Text>
          <Typography.Paragraph
            type="secondary"
            ellipsis={{ rows: 2, expandable: true, symbol: t('common.more') }}
            style={{ marginBottom: 0, overflowWrap: 'anywhere' }}
          >
            {revision.snapshotJson}
          </Typography.Paragraph>
        </Space>
      ),
    }
  })

  return (
    <Space direction="vertical" size={12} style={{ width: '100%' }}>
      {status.failed ? (
        <Alert
          type="error"
          showIcon
          message={t('system.appDownloads.updatePolicy.mobileOta.loadFailed')}
          action={(
            <Button size="small" onClick={() => void loadLane()}>
              {t('system.appDownloads.updatePolicy.retry')}
            </Button>
          )}
        />
      ) : null}
      <Card
        size="small"
        title={t('system.appDownloads.updatePolicy.releaseFacts')}
        extra={(
          <Button icon={<ReloadOutlined />} loading={status.loading} onClick={() => void loadLane()}>
            {t('common.refresh')}
          </Button>
        )}
      >
        <Alert
          type="info"
          showIcon
          style={{ marginBottom: 12 }}
          message={t('system.appDownloads.updatePolicy.mobileOta.registrationBoundary')}
          description={t('system.appDownloads.updatePolicy.mobileOta.registrationDescription')}
        />
        <MeasuredTable<AppOtaRelease>
          metricId="system.app-downloads.mobile-ota-policy.table-1"
          rowKey="id"
          size="small"
          columns={releaseColumns}
          dataSource={releases}
          loading={status.loading && !status.loaded}
          scroll={{ x: 2180 }}
          locale={{ emptyText: <Empty description={t('system.appDownloads.updatePolicy.noReleases')} /> }}
          pagination={{ pageSize: 5, hideOnSinglePage: true }}
        />
      </Card>

      <Card
        size="small"
        title={t('system.appDownloads.updatePolicy.mobileOta.strategyTitle')}
        loading={status.loading && !status.loaded}
        extra={(
          <Space size={6} wrap>
            <Tag color={policy?.enabled ? 'processing' : 'default'}>
              {policy?.enabled
                ? t('system.appDownloads.updatePolicy.enabled')
                : t('system.appDownloads.updatePolicy.disabled')}
            </Tag>
            <Tag>{t('system.appDownloads.updatePolicy.policyVersion')}: {policy?.policyVersion ?? 0}</Tag>
          </Space>
        )}
      >
        <Descriptions size="small" column={{ xs: 1, sm: 2, lg: 4 }} style={{ marginBottom: 16 }}>
          <Descriptions.Item label={t('system.appDownloads.updatePolicy.environment')}>
            {environment}
          </Descriptions.Item>
          <Descriptions.Item label={t('system.appDownloads.updatePolicy.mobileOta.platform')}>
            {platform === 'ios' ? 'iOS' : 'Android'}
          </Descriptions.Item>
          <Descriptions.Item label={t('system.appDownloads.updatePolicy.runtime')}>
            {selectedRelease?.runtimeVersion ?? policy?.targetRuntimeVersion ?? '--'}
          </Descriptions.Item>
          <Descriptions.Item label={t('system.appDownloads.updatePolicy.updatedAt')}>
            {formatAppDownloadLocalDateTime(policy?.updatedAt)}
          </Descriptions.Item>
          <Descriptions.Item label={t('system.appDownloads.updatePolicy.updatedBy')}>
            {policy?.updatedBy || '--'}
          </Descriptions.Item>
          <Descriptions.Item label={t('system.appDownloads.updatePolicy.mobileOta.additionalTargets')} span="filled">
            {additionalTargets.length > 0 ? (
              <Space size={[6, 6]} wrap>
                {additionalTargets.map((target) => (
                  <Tag key={target.targetReleaseId} color={policy?.enabled ? 'processing' : 'default'}>
                    {formatAdditionalTarget(target)}
                  </Tag>
                ))}
              </Space>
            ) : t('system.appDownloads.updatePolicy.mobileOta.additionalTargetsNone')}
          </Descriptions.Item>
        </Descriptions>
        <Alert
          type="info"
          showIcon
          style={{ marginBottom: 16 }}
          message={t('system.appDownloads.updatePolicy.mobileOta.auditChain')}
        />
        <Form<MobileOtaPolicyFormValue>
          form={form}
          layout="vertical"
          disabled={!canManage}
          onFinish={confirmSave}
        >
          <Row gutter={[16, 0]}>
            <Col xs={24} sm={12} lg={5}>
              <Form.Item name="enabled" label={t('system.appDownloads.updatePolicy.policyStatus')} valuePropName="checked">
                <Switch
                  checkedChildren={t('system.appDownloads.updatePolicy.enabled')}
                  unCheckedChildren={t('system.appDownloads.updatePolicy.disabled')}
                />
              </Form.Item>
            </Col>
            <Col xs={24} sm={12} lg={5}>
              <Form.Item name="required" label={t('system.appDownloads.updatePolicy.mobileOta.updateMode')} valuePropName="checked">
                <Switch
                  disabled={!canManage || !enabled}
                  checkedChildren={t('system.appDownloads.updatePolicy.mobileOta.required')}
                  unCheckedChildren={t('system.appDownloads.updatePolicy.mobileOta.optional')}
                />
              </Form.Item>
            </Col>
            <Col xs={24} lg={14}>
              <Form.Item
                name="targetReleaseId"
                label={t('system.appDownloads.updatePolicy.release')}
                dependencies={['enabled']}
                rules={[
                  ({ getFieldValue }) => ({
                    validator: async (_rule, value?: string) => {
                      if (getFieldValue('enabled') && !value) {
                        throw new Error(t('system.appDownloads.updatePolicy.releaseRequired'))
                      }
                    },
                  }),
                ]}
              >
                <Select
                  showSearch
                  optionFilterProp="label"
                  disabled={!canManage || !enabled}
                  placeholder={t('system.appDownloads.updatePolicy.selectRelease')}
                  options={selectableReleases.map((release) => ({
                    value: release.id,
                    label: formatMobileOtaReleaseLabel(release),
                  }))}
                  notFoundContent={<Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={t('system.appDownloads.updatePolicy.noReleases')} />}
                />
              </Form.Item>
            </Col>
            <Col span={24}>
              <Form.Item name="releaseMessage" label={t('system.appDownloads.updatePolicy.releaseMessage')}>
                <Input.TextArea
                  rows={3}
                  maxLength={1000}
                  showCount
                  disabled={!canManage || !enabled}
                  placeholder={t('system.appDownloads.updatePolicy.releaseMessagePlaceholder')}
                />
              </Form.Item>
            </Col>
          </Row>
          {required && enabled ? (
            <Alert
              type="warning"
              showIcon
              style={{ marginBottom: 16 }}
              message={t('system.appDownloads.updatePolicy.mobileOta.requiredWarning')}
              description={t('system.appDownloads.updatePolicy.mobileOta.compatibilityBoundary')}
            />
          ) : null}
          {canManage ? (
            <Button
              type="primary"
              htmlType="submit"
              icon={<SaveOutlined />}
              loading={saving}
              disabled={!domainReady}
            >
              {t('common.save')}
            </Button>
          ) : null}
        </Form>
      </Card>

      <Card size="small" title={t('system.appDownloads.updatePolicy.mobileOta.revisionTitle')}>
        {revisionItems.length > 0
          ? <Timeline items={revisionItems} />
          : <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={t('system.appDownloads.updatePolicy.mobileOta.noRevisions')} />}
      </Card>
    </Space>
  )
}

function MobileOtaLegacyHistory({
  environment,
  refreshVersion,
}: {
  environment: MobileOtaEnvironment
  refreshVersion: number
}) {
  const { t } = useTranslation()
  const requestLaneRef = useRef(new LatestRequestLane())
  const [items, setItems] = useState<MobileAppOtaUpdate[]>([])
  const [page, setPage] = useState(1)
  const [pageSize, setPageSize] = useState(5)
  const [total, setTotal] = useState(0)
  const [status, setStatus] = useState<LoadStatus>({ ...INITIAL_LOAD_STATUS })

  const loadHistory = useCallback(async (nextPage = page, nextPageSize = pageSize) => {
    setStatus((current) => ({ ...current, loading: true, failed: false }))
    const result = await executeLatestRequestLane(
      requestLaneRef.current,
      (signal) => getMobileAppOtaUpdates({
        appKey: 'mobile',
        channel: environment,
        page: nextPage,
        pageSize: nextPageSize,
      }, signal),
      (history) => {
        setItems(history.items)
        setTotal(history.total)
        setPage(history.page)
        setPageSize(history.pageSize)
      },
    )
    if (result.status === 'applied') {
      setStatus({ loading: false, loaded: true, failed: false })
    } else if (result.status === 'failed') {
      console.error('Failed to load Mobile OTA legacy history', result.error)
      setStatus((current) => ({ ...current, loading: false, failed: true }))
    }
  }, [environment, page, pageSize])

  useEffect(() => {
    void loadHistory(1, pageSize)
    return () => requestLaneRef.current.invalidate()
  }, [environment, pageSize, refreshVersion])

  const columns: ColumnsType<MobileAppOtaUpdate> = [
    { title: t('system.appDownloads.updatePolicy.channel'), dataIndex: 'channel', width: 130 },
    { title: t('system.appDownloads.updatePolicy.runtime'), dataIndex: 'runtimeVersion', width: 120 },
    { title: t('system.appDownloads.updatePolicy.mobileOta.platform'), dataIndex: 'platform', width: 100 },
    { title: t('system.appDownloads.updatePolicy.releaseMessage'), dataIndex: 'message', width: 260, ellipsis: true },
    {
      title: t('system.appDownloads.updatePolicy.updateGroupId'),
      dataIndex: 'updateGroupId',
      width: 260,
      render: (value: string | null) => <Typography.Text copyable>{value || '--'}</Typography.Text>,
    },
    {
      title: 'Update ID',
      width: 260,
      render: (_, record) => (
        <Typography.Text copyable>
          {record.updateId || record.androidUpdateId || '--'}
        </Typography.Text>
      ),
    },
    { title: 'Commit', dataIndex: 'gitCommitHash', width: 110, render: (value: string | null) => shortIdentity(value) },
    {
      title: t('system.appDownloads.updatePolicy.publishedAt'),
      dataIndex: 'publishedAt',
      width: 180,
      render: (value: string | null) => formatAppDownloadLocalDateTime(value),
    },
    {
      title: t('common.actions'),
      width: 120,
      fixed: 'right',
      render: (_, record) => {
        const dashboardUrl = safeExternalUrl(record.dashboardUrl)
        return dashboardUrl ? (
          <Button
            size="small"
            icon={<LinkOutlined />}
            onClick={() => window.open(dashboardUrl, '_blank', 'noopener,noreferrer')}
          >
            Dashboard
          </Button>
        ) : '--'
      },
    },
  ]

  return (
    <Card
      size="small"
      title={t('system.appDownloads.updatePolicy.mobileOta.legacyHistoryTitle')}
      extra={(
        <Button icon={<ReloadOutlined />} loading={status.loading} onClick={() => void loadHistory()}>
          {t('common.refresh')}
        </Button>
      )}
    >
      <Alert
        type="warning"
        showIcon
        style={{ marginBottom: 12 }}
        message={t('system.appDownloads.updatePolicy.mobileOta.legacyHistoryWarning')}
      />
      {status.failed ? (
        <Alert
          type="error"
          showIcon
          style={{ marginBottom: 12 }}
          message={t('system.appDownloads.ota.loadFailed')}
        />
      ) : null}
      <MeasuredTable<MobileAppOtaUpdate>
        metricId="system.app-downloads.mobile-ota-policy.table-2"
        rowKey="id"
        size="small"
        columns={columns}
        dataSource={items}
        loading={status.loading && !status.loaded}
        scroll={{ x: 1540 }}
        locale={{ emptyText: <Empty description={t('system.appDownloads.updatePolicy.mobileOta.noLegacyHistory')} /> }}
        pagination={{
          current: page,
          pageSize,
          total,
          showSizeChanger: true,
          onChange: (nextPage, nextPageSize) => void loadHistory(nextPage, nextPageSize),
        }}
      />
    </Card>
  )
}

export default function MobileOtaPolicyTab({
  canManage,
  refreshVersion = 0,
  platform: fixedPlatform,
  onChanged,
}: MobileOtaPolicyTabProps) {
  const { t } = useTranslation()
  const [environment, setEnvironment] = useState<MobileOtaEnvironment>('production')
  const [selectedPlatform, setPlatform] = useState<MobileOtaPlatform>('android')
  const platform = fixedPlatform ?? selectedPlatform

  const lane = (
    <MobileOtaLane
      key={`${environment}:${platform}`}
      canManage={canManage}
      environment={environment}
      platform={platform}
      refreshVersion={refreshVersion}
      onChanged={onChanged}
    />
  )

  return (
    <Space direction="vertical" size={12} style={{ width: '100%' }}>
      <Alert
        type="info"
        showIcon
        message={t('system.appDownloads.updatePolicy.mobileOta.boundaryTitle')}
        description={t('system.appDownloads.updatePolicy.mobileOta.boundaryDescription')}
      />
      <Segmented<MobileOtaEnvironment>
        value={environment}
        onChange={setEnvironment}
        options={[
          { value: 'production', label: t('system.appDownloads.updatePolicy.mobileOta.production') },
          { value: 'preview', label: t('system.appDownloads.updatePolicy.mobileOta.preview') },
        ]}
      />
      {fixedPlatform ? lane : (
        <Tabs
          activeKey={platform}
          onChange={(key) => setPlatform(key as MobileOtaPlatform)}
          destroyInactiveTabPane={false}
          items={[
            { key: 'android', label: 'Android', children: platform === 'android' ? lane : null },
            { key: 'ios', label: 'iOS', children: platform === 'ios' ? lane : null },
          ]}
        />
      )}
      <MobileOtaLegacyHistory environment={environment} refreshVersion={refreshVersion} />
    </Space>
  )
}
