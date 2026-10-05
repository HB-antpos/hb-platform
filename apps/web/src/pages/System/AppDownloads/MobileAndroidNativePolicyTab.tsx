import { ReloadOutlined, SaveOutlined } from '@ant-design/icons'
import {
  Alert,
  Button,
  Card,
  Col,
  Descriptions,
  Form,
  Input,
  InputNumber,
  Modal,
  Row,
  Space,
  Switch,
  Tag,
  Typography,
  message,
} from 'antd'
import { useCallback, useEffect, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'

import { registerPageMessages } from '../../../i18n/registerPageMessages'
import { appUpdatePolicyService } from '../../../services/appUpdatePolicyService'
import type { MobileAndroidNativeUpdatePolicy } from '../../../types/appUpdatePolicy'

import { isAppUpdatePolicyVersionConflict } from './appUpdatePolicyLogic'
import {
  executeLatestRequestLane,
  LatestRequestLane,
  savePolicyWithConflictReload,
} from './appUpdatePolicyRequestLogic'
import {
  buildMobileAndroidNativePolicyConfirmation,
  buildMobileAndroidNativePolicyFormValue,
  buildMobileAndroidNativePolicyRequest,
  formatMobileAndroidBuildLabel,
  mobileAndroidNativePolicyErrorMessageKey,
  MOBILE_ANDROID_RELEASE_MESSAGE_MAX_LENGTH,
  resolveMobileAndroidNativePolicySaveError,
  validateMobileAndroidNativePolicy,
  type MobileAndroidNativePolicyFormValue,
} from './mobileAndroidNativePolicyLogic'
import mobileAndroidMessagesEn from './mobileAndroidNativePolicyMessages.en.json'
import mobileAndroidMessagesZh from './mobileAndroidNativePolicyMessages.zh.json'
import { formatAppDownloadLocalDateTime } from './time'

// 页签文案随懒加载页面注册，不进入首屏 i18n 包（首屏 gzip 预算很紧）。
registerPageMessages({ zh: mobileAndroidMessagesZh, en: mobileAndroidMessagesEn })

interface MobileAndroidNativePolicyTabProps {
  canManage: boolean
  refreshVersion?: number
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

export default function MobileAndroidNativePolicyTab({
  canManage,
  refreshVersion = 0,
}: MobileAndroidNativePolicyTabProps) {
  const { t } = useTranslation()
  const [form] = Form.useForm<MobileAndroidNativePolicyFormValue>()
  const [policy, setPolicy] = useState<MobileAndroidNativeUpdatePolicy | null>(null)
  const [status, setStatus] = useState<LoadStatus>({ ...INITIAL_LOAD_STATUS })
  const [saving, setSaving] = useState(false)
  const requestLaneRef = useRef(new LatestRequestLane())
  const enabled = Form.useWatch('enabled', form) ?? false
  const minimumSupportedBuildNumber = Form.useWatch('minimumSupportedBuildNumber', form) ?? null
  const latestBuild = policy?.latestBuild ?? null

  const loadPolicy = useCallback(async () => {
    setStatus((current) => ({ ...current, loading: true, failed: false }))
    const result = await executeLatestRequestLane(
      requestLaneRef.current,
      (signal) => appUpdatePolicyService.getMobileAndroidNativePolicy(signal),
      (nextPolicy) => {
        setPolicy(nextPolicy)
        form.setFieldsValue(buildMobileAndroidNativePolicyFormValue(nextPolicy))
      },
    )

    if (result.status === 'applied') {
      setStatus({ loading: false, loaded: true, failed: false })
    } else if (result.status === 'failed') {
      console.error('Failed to load Mobile Android native update policy', result.error)
      setStatus((current) => ({ ...current, loading: false, failed: true }))
      message.error(t('system.appDownloads.updatePolicy.mobileAndroid.loadFailed'))
    }
    return result.status
  }, [form, t])

  useEffect(() => {
    const lane = requestLaneRef.current
    void loadPolicy()
    return () => lane.invalidate()
  }, [loadPolicy, refreshVersion])

  const domainReady = status.loaded && !status.loading && !status.failed && policy !== null

  async function savePolicy(value: MobileAndroidNativePolicyFormValue) {
    if (!policy) {
      return
    }
    // 保存前作废进行中的加载，避免旧响应在保存后回写表单。
    requestLaneRef.current.invalidate()
    setStatus((current) => ({ ...current, loading: false }))
    setSaving(true)
    try {
      const result = await savePolicyWithConflictReload(
        () => appUpdatePolicyService.saveMobileAndroidNativePolicy(
          buildMobileAndroidNativePolicyRequest(value, policy.policyVersion),
        ),
        loadPolicy,
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
      await loadPolicy()
    } catch (error) {
      console.error('Failed to save Mobile Android native update policy', error)
      // 后端业务校验（如最低构建号高于公开包）给出具体原因，其余走通用失败提示。
      const validationError = resolveMobileAndroidNativePolicySaveError(error)
      message.error(validationError
        ? t(mobileAndroidNativePolicyErrorMessageKey(validationError))
        : t('system.appDownloads.updatePolicy.saveFailed'))
      throw error
    } finally {
      setSaving(false)
    }
  }

  function confirmSave(value: MobileAndroidNativePolicyFormValue) {
    if (!policy) {
      message.error(t('system.appDownloads.updatePolicy.mobileAndroid.loadFailed'))
      return
    }
    const confirmation = buildMobileAndroidNativePolicyConfirmation(value, latestBuild)
    Modal.confirm({
      title: confirmation.enabled
        ? t('system.appDownloads.updatePolicy.mobileAndroid.confirmTitle')
        : t('system.appDownloads.updatePolicy.disableConfirmTitle'),
      content: confirmation.enabled ? (
        <Space direction="vertical" size={12} style={{ width: '100%' }}>
          <Alert
            type="warning"
            showIcon
            message={t('system.appDownloads.updatePolicy.mobileAndroid.blockWarning', {
              build: confirmation.minimumSupportedBuildNumber,
            })}
          />
          <Descriptions bordered size="small" column={1}>
            <Descriptions.Item label={t('system.appDownloads.updatePolicy.mobileAndroid.latestBuild')}>
              {confirmation.latestBuildLabel}
            </Descriptions.Item>
            <Descriptions.Item label={t('system.appDownloads.updatePolicy.mobileAndroid.minimumBuild')}>
              {confirmation.minimumSupportedBuildNumber ?? '--'}
            </Descriptions.Item>
            <Descriptions.Item label={t('system.appDownloads.updatePolicy.releaseMessage')}>
              <Typography.Text style={{ overflowWrap: 'anywhere', whiteSpace: 'pre-wrap' }}>
                {confirmation.releaseMessage || '--'}
              </Typography.Text>
            </Descriptions.Item>
          </Descriptions>
        </Space>
      ) : t('system.appDownloads.updatePolicy.mobileAndroid.disableConfirmDescription'),
      okText: t('common.confirm'),
      cancelText: t('common.cancel'),
      // 启用会直接拦截低版本设备，确认按钮用危险色提醒。
      okButtonProps: confirmation.enabled ? { danger: true } : undefined,
      width: 560,
      onOk: () => savePolicy(value),
    })
  }

  return (
    <Space direction="vertical" size={12} style={{ width: '100%' }}>
      {status.failed ? (
        <Alert
          type="error"
          showIcon
          message={t('system.appDownloads.updatePolicy.mobileAndroid.loadFailed')}
          action={(
            <Button size="small" onClick={() => void loadPolicy()}>
              {t('system.appDownloads.updatePolicy.retry')}
            </Button>
          )}
        />
      ) : null}
      <Alert
        type="info"
        showIcon
        message={t('system.appDownloads.updatePolicy.mobileAndroid.boundaryTitle')}
        description={t('system.appDownloads.updatePolicy.mobileAndroid.boundaryDescription')}
      />
      <Card
        size="small"
        title={t('system.appDownloads.updatePolicy.mobileAndroid.latestBuildTitle')}
        loading={status.loading && !status.loaded}
        extra={(
          <Button icon={<ReloadOutlined />} loading={status.loading} onClick={() => void loadPolicy()}>
            {t('common.refresh')}
          </Button>
        )}
      >
        {latestBuild ? (
          <Descriptions size="small" column={{ xs: 1, sm: 2, lg: 3 }}>
            <Descriptions.Item label={t('system.appDownloads.updatePolicy.mobileAndroid.latestBuild')}>
              <Typography.Text strong>{formatMobileAndroidBuildLabel(latestBuild)}</Typography.Text>
            </Descriptions.Item>
            <Descriptions.Item label={t('system.appDownloads.updatePolicy.mobileAndroid.completedAt')}>
              {formatAppDownloadLocalDateTime(latestBuild.completedAt)}
            </Descriptions.Item>
            <Descriptions.Item label="EAS Build ID">
              <Typography.Text copyable={latestBuild.easBuildId ? { text: latestBuild.easBuildId } : false}>
                {latestBuild.easBuildId || '--'}
              </Typography.Text>
            </Descriptions.Item>
          </Descriptions>
        ) : (
          <Alert
            type="warning"
            showIcon
            message={t('system.appDownloads.updatePolicy.mobileAndroid.noLatestBuild')}
          />
        )}
      </Card>

      <Card
        size="small"
        title={t('system.appDownloads.updatePolicy.policy')}
        loading={status.loading && !status.loaded}
        extra={(
          <Space size={4} wrap>
            <Tag color={policy?.enabled ? 'green' : 'default'}>
              {policy?.enabled
                ? t('system.appDownloads.updatePolicy.enabled')
                : t('system.appDownloads.updatePolicy.disabled')}
            </Tag>
            <Tag>{t('system.appDownloads.updatePolicy.policyVersion')}: {policy?.policyVersion ?? 0}</Tag>
          </Space>
        )}
      >
        <Descriptions size="small" column={{ xs: 1, sm: 2, lg: 3 }} style={{ marginBottom: 16 }}>
          <Descriptions.Item label={t('system.appDownloads.updatePolicy.mobileAndroid.minimumBuild')}>
            {policy?.enabled ? policy.minimumSupportedBuildNumber ?? '--' : '--'}
          </Descriptions.Item>
          <Descriptions.Item label={t('system.appDownloads.updatePolicy.updatedAt')}>
            {formatAppDownloadLocalDateTime(policy?.updatedAt)}
          </Descriptions.Item>
          <Descriptions.Item label={t('system.appDownloads.updatePolicy.updatedBy')}>
            {policy?.updatedBy || '--'}
          </Descriptions.Item>
        </Descriptions>

        <Form<MobileAndroidNativePolicyFormValue>
          form={form}
          layout="vertical"
          disabled={!canManage}
          onFinish={confirmSave}
        >
          <Row gutter={[16, 0]}>
            <Col xs={24} md={8}>
              <Form.Item
                name="enabled"
                label={t('system.appDownloads.updatePolicy.policyStatus')}
                valuePropName="checked"
              >
                <Switch
                  // 没有公开包时不允许启用；已启用的策略仍可关闭。
                  disabled={!canManage || (!latestBuild && !enabled)}
                  checkedChildren={t('system.appDownloads.updatePolicy.enabled')}
                  unCheckedChildren={t('system.appDownloads.updatePolicy.disabled')}
                />
              </Form.Item>
            </Col>
            <Col xs={24} md={16}>
              <Form.Item
                name="minimumSupportedBuildNumber"
                label={t('system.appDownloads.updatePolicy.mobileAndroid.minimumBuild')}
                extra={latestBuild
                  ? t('system.appDownloads.updatePolicy.mobileAndroid.minimumBuildHelp', {
                      max: latestBuild.appBuildVersion,
                    })
                  : t('system.appDownloads.updatePolicy.mobileAndroid.noLatestBuild')}
                dependencies={['enabled']}
                rules={[
                  ({ getFieldValue }) => ({
                    validator: async (_rule, value?: number | null) => {
                      // 关键位置：与后端 PUT 共用同一套边界（必填、≥1、≤ 公开包 versionCode）。
                      const error = validateMobileAndroidNativePolicy(
                        {
                          enabled: Boolean(getFieldValue('enabled')),
                          minimumSupportedBuildNumber: value ?? null,
                        },
                        latestBuild,
                      )
                      if (error) {
                        throw new Error(t(mobileAndroidNativePolicyErrorMessageKey(error), {
                          max: latestBuild?.appBuildVersion,
                        }))
                      }
                    },
                  }),
                ]}
              >
                <InputNumber
                  min={1}
                  max={latestBuild?.appBuildVersion}
                  precision={0}
                  style={{ width: '100%' }}
                  disabled={!canManage || !enabled}
                  placeholder={latestBuild ? String(latestBuild.appBuildVersion) : undefined}
                />
              </Form.Item>
            </Col>
            <Col span={24}>
              <Form.Item
                name="releaseMessage"
                label={t('system.appDownloads.updatePolicy.releaseMessage')}
              >
                <Input.TextArea
                  rows={3}
                  maxLength={MOBILE_ANDROID_RELEASE_MESSAGE_MAX_LENGTH}
                  showCount
                  disabled={!canManage || !enabled}
                  placeholder={t('system.appDownloads.updatePolicy.releaseMessagePlaceholder')}
                />
              </Form.Item>
            </Col>
          </Row>
          {enabled && minimumSupportedBuildNumber ? (
            <Alert
              type="warning"
              showIcon
              style={{ marginBottom: 16 }}
              message={t('system.appDownloads.updatePolicy.mobileAndroid.blockWarning', {
                build: minimumSupportedBuildNumber,
              })}
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
    </Space>
  )
}
