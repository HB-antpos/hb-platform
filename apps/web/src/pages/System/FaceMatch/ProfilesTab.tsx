import { CheckCircleFilled, CloseCircleFilled, ReloadOutlined } from '@ant-design/icons'
import { Alert, Button, Col, DatePicker, Divider, Form, Input, Radio, Row, Space, Spin, Switch, Tag, Typography, message } from 'antd'
import type { ColumnsType } from 'antd/es/table'
import dayjs from 'dayjs'
import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'

import { MeasuredTable } from '../../../components/MeasuredTable'
import {
  checkFaceStoreProfile,
  getFaceStoreProfile,
  getFaceStoreProfiles,
  updateFaceStoreProfile,
} from '../../../services/faceMatchService'
import type { FaceGateReport, FaceStoreProfile, FaceStoreProfileListItem } from '../../../types/faceMatch'
import { createLatestGuard, formatDateTime } from '../FaceCameras/logic'
import {
  DATE_FORMAT,
  PROFILE_MODE_CAPTURE,
  PROFILE_MODE_MATCH,
  PROFILE_MODE_OPTIONS,
  buildProfileRequest,
  describeFaceMatchError,
  countMissing,
  getGateReportFromError,
  getMissingLabelKey,
  isSameProfileRequest,
  profileToFormValues,
  summarizeGates,
  type FaceProfileFormValues,
} from './logic'
import { NUMERIC_STYLE, StoreSelect, type FaceMatchTabContext } from './shared'
import { useRemote } from './useRemote'

const CHECK_DEBOUNCE_MS = 500

// 日期 / 日期时间在表单里统一存字符串（yyyy-MM-dd / UTC ISO），DatePicker 通过这两组属性做双向转换。
const dateFieldProps = {
  getValueProps: (value: string | undefined) => ({ value: value ? dayjs(value, DATE_FORMAT) : null }),
  normalize: (value: dayjs.Dayjs | null) => (value ? value.format(DATE_FORMAT) : ''),
}
const dateTimeFieldProps = {
  getValueProps: (value: string | undefined) => ({ value: value ? dayjs(value) : null }),
  normalize: (value: dayjs.Dayjs | null) => (value ? value.toDate().toISOString() : ''),
}

interface ProfileFormProps {
  context: FaceMatchTabContext
  profile: FaceStoreProfile
  onSaved: () => void
}

/** 单个门店的档案表单 + 闸门缺项面板。父组件以 storeCode + 更新时间作 key：保存或换店后整体重建。 */
function ProfileForm({ context, profile, onSaved }: ProfileFormProps) {
  const { t } = useTranslation()
  const { config, writeBlocked } = context
  const [form] = Form.useForm<FaceProfileFormValues>()
  const [gates, setGates] = useState<FaceGateReport>(profile.gates)
  const [checking, setChecking] = useState(false)
  const [saving, setSaving] = useState(false)
  const [saveError, setSaveError] = useState<string | null>(null)
  const [dirty, setDirty] = useState(false)
  const timerRef = useRef<number | null>(null)
  const abortRef = useRef<AbortController | null>(null)
  const guard = useMemo(() => createLatestGuard(), [])
  const initialValues = useMemo(() => profileToFormValues(profile, config.region), [profile, config.region])
  const initialRequest = useMemo(() => buildProfileRequest(initialValues), [initialValues])

  // 实时检查闸门：不保存，只列出每个被请求启用项缺哪一项；输入停顿 500ms 后发一次，乱序响应丢弃。
  const runCheck = useCallback(async () => {
    const request = buildProfileRequest(form.getFieldsValue(true) as FaceProfileFormValues)
    setDirty(!isSameProfileRequest(request, initialRequest))
    abortRef.current?.abort()
    const controller = new AbortController()
    abortRef.current = controller
    const requestId = guard.begin()
    setChecking(true)
    try {
      const report = await checkFaceStoreProfile(profile.storeCode, request, controller.signal)
      if (guard.isLatest(requestId)) setGates(report)
    } catch {
      // 检查失败不打断编辑：保留上一份结果（保存时后端仍会强制校验）。
    } finally {
      if (guard.isLatest(requestId)) setChecking(false)
    }
  }, [form, guard, initialRequest, profile.storeCode])

  const scheduleCheck = useCallback(() => {
    if (timerRef.current !== null) window.clearTimeout(timerRef.current)
    timerRef.current = window.setTimeout(() => void runCheck(), CHECK_DEBOUNCE_MS)
  }, [runCheck])

  useEffect(
    () => () => {
      if (timerRef.current !== null) window.clearTimeout(timerRef.current)
      abortRef.current?.abort()
      guard.invalidate()
    },
    [guard],
  )

  const handleSave = async () => {
    setSaving(true)
    setSaveError(null)
    try {
      await updateFaceStoreProfile(profile.storeCode, buildProfileRequest(form.getFieldsValue(true) as FaceProfileFormValues))
      void message.success(t('faceMatch.profile.saved'))
      onSaved()
    } catch (error) {
      // 409 FACE_MATCH_GATE_NOT_MET：details 就是闸门报告，与实时检查展示在同一处。
      const report = getGateReportFromError(error)
      if (report) setGates(report)
      setSaveError(describeFaceMatchError(t, error))
    } finally {
      setSaving(false)
    }
  }

  const rows = summarizeGates(gates)
  const missingCount = countMissing(rows)
  const region = config.region
  const regionMismatch = profile.exists && !profile.crossBorderReviewValid

  return (
    <Row gutter={[24, 16]}>
      <Col xs={24} xl={15}>
        <Form
          form={form}
          layout="vertical"
          autoComplete="off"
          initialValues={initialValues}
          disabled={writeBlocked}
          onValuesChange={() => {
            setDirty(true)
            scheduleCheck()
          }}
        >
          <Form.Item name="mode" label={t('faceMatch.profile.fields.mode')}>
            <Radio.Group optionType="button" buttonStyle="solid">
              {PROFILE_MODE_OPTIONS.map((option) => (
                <Radio.Button key={option.value} value={option.value}>
                  {t(option.labelKey)}
                </Radio.Button>
              ))}
            </Radio.Group>
          </Form.Item>
          <Form.Item noStyle shouldUpdate={(prev, next) => prev.mode !== next.mode}>
            {({ getFieldValue }) => {
              const option = PROFILE_MODE_OPTIONS.find((candidate) => candidate.value === getFieldValue('mode'))
              return option ? (
                <Typography.Paragraph type="secondary" style={{ marginTop: -8 }}>
                  {t(option.hintKey)}
                </Typography.Paragraph>
              ) : null
            }}
          </Form.Item>

          <Divider orientation="left" plain>
            {t('faceMatch.profile.sections.switches')}
          </Divider>
          <Row gutter={16}>
            <Col xs={24} md={12}>
              <Form.Item name="evaluateMatch" valuePropName="checked" label={t('faceMatch.profile.fields.evaluateMatch')} extra={t('faceMatch.profile.hints.evaluateMatch')}>
                <Switch />
              </Form.Item>
            </Col>
            <Col xs={24} md={12}>
              <Form.Item name="staffExclusionEnabled" valuePropName="checked" label={t('faceMatch.profile.fields.staffExclusionEnabled')} extra={t('faceMatch.profile.hints.staffExclusion')}>
                <Switch />
              </Form.Item>
            </Col>
            <Col xs={24} md={12}>
              <Form.Item name="trafficDedupEnabled" valuePropName="checked" label={t('faceMatch.profile.fields.trafficDedupEnabled')} extra={t('faceMatch.profile.hints.trafficDedup')}>
                <Switch />
              </Form.Item>
            </Col>
            <Col xs={24} md={12}>
              <Form.Item name="unwelcomeListEnabled" valuePropName="checked" label={t('faceMatch.profile.fields.unwelcomeListEnabled')} extra={t('faceMatch.profile.hints.unwelcomeList')}>
                <Switch />
              </Form.Item>
            </Col>
          </Row>

          <Divider orientation="left" plain>
            {t('faceMatch.profile.sections.basics')}
          </Divider>
          <Form.Item
            name="allowedSubjects"
            label={t('faceMatch.profile.fields.allowedSubjects')}
            extra={t('faceMatch.profile.hints.allowedSubjects')}
          >
            <Input maxLength={200} />
          </Form.Item>
          <Row gutter={16}>
            <Col xs={24} md={12}>
              <Form.Item name="piaReference" label={t('faceMatch.profile.fields.piaReference')}>
                <Input maxLength={100} />
              </Form.Item>
            </Col>
            <Col xs={24} md={12}>
              <Form.Item name="signageInstalledOn" label={t('faceMatch.profile.fields.signageInstalledOn')} extra={t('faceMatch.profile.hints.signage')} {...dateFieldProps}>
                <DatePicker style={{ width: '100%' }} format={DATE_FORMAT} />
              </Form.Item>
            </Col>
            <Col xs={24} md={12}>
              <Form.Item name="legalSignoffBy" label={t('faceMatch.profile.fields.legalSignoffBy')}>
                <Input maxLength={100} />
              </Form.Item>
            </Col>
            <Col xs={24} md={12}>
              <Form.Item name="legalSignoffAt" label={t('faceMatch.profile.fields.legalSignoffAt')} {...dateTimeFieldProps}>
                <DatePicker showTime style={{ width: '100%' }} format="YYYY-MM-DD HH:mm" />
              </Form.Item>
            </Col>
            <Col xs={24} md={12}>
              <Form.Item
                name="staffNoticeDate"
                label={t('faceMatch.profile.fields.staffNoticeDate')}
                extra={profile.jurisdiction === 'NSW' ? t('faceMatch.profile.hints.staffNoticeNsw') : t('faceMatch.profile.hints.staffNoticeOther')}
                {...dateFieldProps}
              >
                <DatePicker style={{ width: '100%' }} format={DATE_FORMAT} />
              </Form.Item>
            </Col>
            <Col xs={24} md={12}>
              <Form.Item name="timeZoneId" label={t('faceMatch.profile.fields.timeZoneId')} extra={t('faceMatch.profile.hints.timeZone', { zone: profile.effectiveTimeZoneId || '-', jurisdiction: profile.jurisdiction ?? '-' })}>
                <Input maxLength={64} placeholder="Australia/Sydney" />
              </Form.Item>
            </Col>
          </Row>

          <Divider orientation="left" plain>
            {t('faceMatch.profile.sections.crossBorder')}
          </Divider>
          {regionMismatch ? (
            <Alert type="warning" showIcon style={{ marginBottom: 16 }} message={t('faceMatch.profile.regionMismatch', { region: region ?? '-' })} />
          ) : null}
          <Row gutter={16}>
            <Col xs={24} md={12}>
              <Form.Item
                name="crossBorderRegion"
                label={t('faceMatch.profile.fields.crossBorderRegion')}
                extra={
                  <span>
                    {t('faceMatch.profile.hints.crossBorderRegion', { region: region ?? '-' })}
                    {region ? (
                      <Button type="link" size="small" onClick={() => { form.setFieldValue('crossBorderRegion', region); setDirty(true); scheduleCheck() }}>
                        {t('faceMatch.profile.useConfigRegion')}
                      </Button>
                    ) : null}
                  </span>
                }
              >
                <Input maxLength={64} />
              </Form.Item>
            </Col>
            <Col xs={24} md={12}>
              <Form.Item name="crossBorderReviewRef" label={t('faceMatch.profile.fields.crossBorderReviewRef')}>
                <Input maxLength={100} />
              </Form.Item>
            </Col>
            <Col xs={24} md={12}>
              <Form.Item name="crossBorderReviewedBy" label={t('faceMatch.profile.fields.crossBorderReviewedBy')}>
                <Input maxLength={100} />
              </Form.Item>
            </Col>
            <Col xs={24} md={12}>
              <Form.Item name="crossBorderReviewedAt" label={t('faceMatch.profile.fields.crossBorderReviewedAt')} {...dateTimeFieldProps}>
                <DatePicker showTime style={{ width: '100%' }} format="YYYY-MM-DD HH:mm" />
              </Form.Item>
            </Col>
          </Row>

          <Divider orientation="left" plain>
            {t('faceMatch.profile.sections.dedup')}
          </Divider>
          <Row gutter={16}>
            <Col xs={24} md={12}>
              <Form.Item name="dedupLegalSignoffBy" label={t('faceMatch.profile.fields.dedupLegalSignoffBy')} extra={t('faceMatch.profile.hints.dedupSignoff')}>
                <Input maxLength={100} />
              </Form.Item>
            </Col>
            <Col xs={24} md={12}>
              <Form.Item name="dedupLegalSignoffAt" label={t('faceMatch.profile.fields.dedupLegalSignoffAt')} {...dateTimeFieldProps}>
                <DatePicker showTime style={{ width: '100%' }} format="YYYY-MM-DD HH:mm" />
              </Form.Item>
            </Col>
          </Row>

          <Divider orientation="left" plain>
            {t('faceMatch.profile.sections.unwelcome')}
          </Divider>
          <Row gutter={16}>
            <Col xs={24} md={12}>
              <Form.Item name="unwelcomeAssessmentRef" label={t('faceMatch.profile.fields.unwelcomeAssessmentRef')} extra={t('faceMatch.profile.hints.unwelcomeAssessment')}>
                <Input maxLength={100} />
              </Form.Item>
            </Col>
            <Col xs={24} md={12}>
              <Form.Item name="privacyContact" label={t('faceMatch.profile.fields.privacyContact')}>
                <Input maxLength={200} />
              </Form.Item>
            </Col>
            <Col xs={24} md={12}>
              <Form.Item name="unwelcomeLegalSignoffBy" label={t('faceMatch.profile.fields.unwelcomeLegalSignoffBy')} extra={t('faceMatch.profile.hints.unwelcomeSignoff')}>
                <Input maxLength={100} />
              </Form.Item>
            </Col>
            <Col xs={24} md={12}>
              <Form.Item name="unwelcomeLegalSignoffAt" label={t('faceMatch.profile.fields.unwelcomeLegalSignoffAt')} {...dateTimeFieldProps}>
                <DatePicker showTime style={{ width: '100%' }} format="YYYY-MM-DD HH:mm" />
              </Form.Item>
            </Col>
            <Col xs={24} md={12}>
              <Form.Item name="staffTrainingDate" label={t('faceMatch.profile.fields.staffTrainingDate')} {...dateFieldProps}>
                <DatePicker style={{ width: '100%' }} format={DATE_FORMAT} />
              </Form.Item>
            </Col>
          </Row>
        </Form>
      </Col>

      <Col xs={24} xl={9}>
        {/* 闸门面板：随表单实时刷新；大屏时吸顶，保存按钮始终在视野内。 */}
        <div style={{ position: 'sticky', top: 16 }}>
          <Space direction="vertical" size={12} style={{ width: '100%' }}>
            <Typography.Title level={5} style={{ margin: 0 }}>
              {t('faceMatch.gates.title')} {checking ? <Spin size="small" /> : null}
            </Typography.Title>
            {rows.length === 0 ? (
              <Alert type="info" showIcon message={t('faceMatch.gates.nothingRequested')} />
            ) : (
              rows.map((row) => (
                <Alert
                  key={row.facet}
                  type={row.missing.length === 0 ? 'success' : 'warning'}
                  showIcon
                  icon={row.missing.length === 0 ? <CheckCircleFilled /> : <CloseCircleFilled />}
                  message={row.facetLabelKey ? t(row.facetLabelKey) : row.facet}
                  description={
                    row.missing.length === 0 ? (
                      t('faceMatch.gates.satisfied')
                    ) : (
                      <div>
                        <div>{t('faceMatch.gates.missingTitle', { count: row.missing.length })}</div>
                        <ul style={{ margin: '4px 0 0', paddingInlineStart: 18 }}>
                          {row.missing.map((key) => {
                            const labelKey = getMissingLabelKey(key)
                            return <li key={key}>{labelKey ? t(labelKey) : key}</li>
                          })}
                        </ul>
                      </div>
                    )
                  }
                />
              ))
            )}
            {saveError ? <Alert type="error" showIcon message={saveError} /> : null}
            {missingCount > 0 ? <Typography.Text type="warning">{t('faceMatch.gates.saveWillFail', { count: missingCount })}</Typography.Text> : null}
            <Space>
              <Button type="primary" loading={saving} disabled={writeBlocked} onClick={() => void handleSave()}>
                {t('faceMatch.profile.save')}
              </Button>
              <Button disabled={saving || !dirty} onClick={() => { form.resetFields(); setDirty(false); setSaveError(null); setGates(profile.gates) }}>
                {t('faceMatch.profile.reset')}
              </Button>
            </Space>
            <Typography.Text type="secondary" style={{ fontSize: 12 }}>
              {profile.updatedAtUtc
                ? t('faceMatch.profile.updatedAt', { time: formatDateTime(profile.updatedAtUtc), by: profile.updatedBy ?? '-' })
                : t('faceMatch.profile.neverSaved')}
            </Typography.Text>
          </Space>
        </div>
      </Col>
    </Row>
  )
}

/** 门店档案页签（Manage）：门店概览表 + 选店后的档案表单与闸门面板。 */
export default function ProfilesTab({ context }: { context: FaceMatchTabContext }) {
  const { t } = useTranslation()
  const { storeOptions, storeOptionsFailed } = context
  const [storeCode, setStoreCode] = useState<string | null>(null)

  const listLoader = useCallback((signal: AbortSignal) => getFaceStoreProfiles(signal), [])
  const list = useRemote<FaceStoreProfileListItem[]>(listLoader)
  const profileLoader = useMemo(
    () => (storeCode ? (signal: AbortSignal) => getFaceStoreProfile(storeCode, signal) : null),
    [storeCode],
  )
  const profileState = useRemote<FaceStoreProfile>(profileLoader)

  const columns = useMemo<ColumnsType<FaceStoreProfileListItem>>(
    () => [
      {
        title: t('faceMatch.common.store'),
        key: 'store',
        width: 220,
        ellipsis: true,
        render: (_: unknown, record) => (
          <span>
            <Typography.Text strong>{record.storeCode}</Typography.Text>
            {record.storeName ? <Typography.Text type="secondary"> · {record.storeName}</Typography.Text> : null}
          </span>
        ),
      },
      {
        title: t('faceMatch.profile.fields.mode'),
        dataIndex: 'mode',
        width: 100,
        render: (value: number) => {
          const option = PROFILE_MODE_OPTIONS.find((candidate) => candidate.value === value)
          return (
            <Tag color={value === PROFILE_MODE_MATCH ? 'red' : value === PROFILE_MODE_CAPTURE ? 'orange' : 'default'}>
              {option ? t(option.labelKey) : value}
            </Tag>
          )
        },
      },
      {
        title: t('faceMatch.profile.overview.enabledItems'),
        key: 'enabled',
        render: (_: unknown, record) => (
          <Space size={4} wrap>
            {record.evaluateMatch ? <Tag>{t('faceMatch.profile.fields.evaluateMatch')}</Tag> : null}
            {record.staffExclusionEnabled ? <Tag>{t('faceMatch.profile.fields.staffExclusionEnabled')}</Tag> : null}
            {record.trafficDedupEnabled ? <Tag>{t('faceMatch.profile.fields.trafficDedupEnabled')}</Tag> : null}
            {record.unwelcomeListEnabled ? <Tag>{t('faceMatch.profile.fields.unwelcomeListEnabled')}</Tag> : null}
          </Space>
        ),
      },
      {
        title: t('faceMatch.profile.overview.crossBorder'),
        dataIndex: 'crossBorderReviewValid',
        width: 120,
        render: (value: boolean) => (value ? <Tag color="green">{t('faceMatch.profile.overview.valid')}</Tag> : <Tag>{t('faceMatch.profile.overview.notValid')}</Tag>),
      },
      {
        title: t('faceMatch.common.updatedAt'),
        dataIndex: 'updatedAtUtc',
        width: 170,
        render: (value: string | null) => <span style={NUMERIC_STYLE}>{formatDateTime(value)}</span>,
      },
    ],
    [t],
  )

  return (
    <Space direction="vertical" size={16} style={{ width: '100%' }}>
      <Space wrap>
        <Typography.Text>{t('faceMatch.common.store')}</Typography.Text>
        <StoreSelect
          options={storeOptions}
          failed={storeOptionsFailed}
          value={storeCode}
          onChange={setStoreCode}
          placeholder={t('faceMatch.profile.pickStore')}
          style={{ width: 280 }}
        />
        <Button icon={<ReloadOutlined />} loading={list.loading} onClick={list.reload}>
          {t('faceMatch.common.refresh')}
        </Button>
      </Space>

      {list.error ? <Alert type="error" showIcon message={describeFaceMatchError(t, list.error)} /> : null}
      <MeasuredTable<FaceStoreProfileListItem>
        metricId="system.face-match.profiles-table-1"
        rowKey="storeCode"
        size="small"
        loading={list.loading && !list.data}
        dataSource={list.data ?? []}
        columns={columns}
        pagination={false}
        scroll={{ x: 800, y: 240 }}
        onRow={(record) => ({ onClick: () => setStoreCode(record.storeCode), style: { cursor: 'pointer' } })}
        rowClassName={(record) => (record.storeCode === storeCode ? 'ant-table-row-selected' : '')}
        locale={{ emptyText: t('faceMatch.profile.overview.empty') }}
      />

      {!storeCode ? (
        <Alert type="info" showIcon message={t('faceMatch.profile.pickStoreHint')} />
      ) : profileState.error && !profileState.data ? (
        <Alert type="error" showIcon message={describeFaceMatchError(t, profileState.error)} action={<Button size="small" onClick={profileState.reload}>{t('faceMatch.common.retry')}</Button>} />
      ) : !profileState.data ? (
        <Spin />
      ) : (
        <>
          <Typography.Title level={5} style={{ margin: 0 }}>
            {t('faceMatch.profile.formTitle', { store: `${profileState.data.storeCode}${profileState.data.storeName ? ` · ${profileState.data.storeName}` : ''}` })}
          </Typography.Title>
          <ProfileForm
            key={`${profileState.data.storeCode}|${profileState.data.updatedAtUtc ?? ''}`}
            context={context}
            profile={profileState.data}
            onSaved={() => {
              profileState.reload()
              list.reload()
            }}
          />
        </>
      )}
    </Space>
  )
}
