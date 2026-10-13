import { ReloadOutlined } from '@ant-design/icons'
import { Alert, Button, Drawer, Space, Tag, Tooltip, Typography, message } from 'antd'
import type { ColumnsType } from 'antd/es/table'
import { useCallback, useMemo, useState } from 'react'
import { useTranslation } from 'react-i18next'

import { MeasuredTable } from '../../../components/MeasuredTable'
import {
  getFaceConsents,
  getFaceEnrollments,
  withdrawFaceConsent,
  withdrawFaceEnrollment,
} from '../../../services/faceMatchService'
import type { FaceConsent, FaceEnrollment, FaceStaffCandidate } from '../../../types/faceMatch'
import { formatDateTime } from '../FaceCameras/logic'
import ConsentModal from './ConsentModal'
import EnrollmentModal from './EnrollmentModal'
import {
  CONSENT_METHOD_LABEL_KEYS,
  describeFaceMatchError,
  isActiveEnrollment,
  pickEnrollableConsents,
} from './logic'
import ReasonModal from './ReasonModal'
import { NUMERIC_STYLE, type FaceMatchTabContext } from './shared'
import { useRemote } from './useRemote'

interface StaffDetailDrawerProps {
  context: FaceMatchTabContext
  staff: FaceStaffCandidate
  /** 同意或登记发生变化：外层刷新员工列表里的登记状态。 */
  onChanged: () => void
  onClose: () => void
}

type WithdrawTarget = { kind: 'consent' | 'enrollment'; id: string } | null

/** 单个员工的同意与登记：记录同意、登记人脸、撤回（二次确认并填写原因）。 */
export default function StaffDetailDrawer({ context, staff, onChanged, onClose }: StaffDetailDrawerProps) {
  const { t } = useTranslation()
  const { config, writeBlocked } = context
  const [consentOpen, setConsentOpen] = useState(false)
  const [enrollOpen, setEnrollOpen] = useState(false)
  const [withdrawTarget, setWithdrawTarget] = useState<WithdrawTarget>(null)

  const consentLoader = useCallback((signal: AbortSignal) => getFaceConsents(staff.userGuid, true, signal), [staff.userGuid])
  const enrollmentLoader = useCallback((signal: AbortSignal) => getFaceEnrollments(staff.userGuid, false, signal), [staff.userGuid])
  const consents = useRemote<FaceConsent[]>(consentLoader)
  const enrollments = useRemote<FaceEnrollment[]>(enrollmentLoader)

  const enrollable = useMemo(() => pickEnrollableConsents(consents.data ?? []), [consents.data])
  // 上传类操作需要私有桶；功能未开启时一律只读。
  const uploadBlocked = writeBlocked || !config.objectStoreConfigured

  const refreshAll = () => {
    consents.reload()
    enrollments.reload()
    onChanged()
  }

  const describeError = useCallback((error: unknown) => describeFaceMatchError(t, error), [t])

  const consentColumns = useMemo<ColumnsType<FaceConsent>>(
    () => [
      { title: t('faceMatch.consent.version'), dataIndex: 'consentVersion', width: 120, ellipsis: true },
      {
        title: t('faceMatch.consent.signedAt'),
        dataIndex: 'signedAtUtc',
        width: 160,
        render: (value: string) => <span style={NUMERIC_STYLE}>{formatDateTime(value)}</span>,
      },
      {
        title: t('faceMatch.consent.methodLabel'),
        dataIndex: 'method',
        width: 90,
        render: (value: string) => (CONSENT_METHOD_LABEL_KEYS[value] ? t(CONSENT_METHOD_LABEL_KEYS[value]) : value),
      },
      {
        title: t('faceMatch.consent.evidence'),
        dataIndex: 'hasEvidence',
        width: 90,
        render: (value: boolean) => (value ? <Tag color="green">{t('faceMatch.consent.hasScan')}</Tag> : <Tag>{t('faceMatch.consent.noScan')}</Tag>),
      },
      {
        title: t('faceMatch.consent.nswNoticeDate'),
        dataIndex: 'nswNoticeDate',
        width: 120,
        render: (value: string | null) => value ?? '-',
      },
      {
        title: t('faceMatch.common.status'),
        key: 'status',
        width: 210,
        render: (_: unknown, record) =>
          record.active ? (
            <Tag color="green">{t('faceMatch.consent.active')}</Tag>
          ) : (
            <Tooltip title={record.withdrawnReason ?? undefined}>
              <Tag>{t('faceMatch.consent.withdrawn', { time: formatDateTime(record.withdrawnAtUtc) })}</Tag>
            </Tooltip>
          ),
      },
      {
        title: t('faceMatch.common.actions'),
        key: 'actions',
        width: 90,
        fixed: 'right',
        render: (_: unknown, record) =>
          record.active ? (
            <Button type="link" size="small" danger disabled={writeBlocked} onClick={() => setWithdrawTarget({ kind: 'consent', id: record.consentId })}>
              {t('faceMatch.consent.withdraw')}
            </Button>
          ) : null,
      },
    ],
    [t, writeBlocked],
  )

  const enrollmentColumns = useMemo<ColumnsType<FaceEnrollment>>(
    () => [
      {
        title: t('faceMatch.enrollment.enrolledAt'),
        dataIndex: 'enrolledAtUtc',
        width: 170,
        render: (value: string) => <span style={NUMERIC_STYLE}>{formatDateTime(value)}</span>,
      },
      { title: t('faceMatch.enrollment.photoCount'), dataIndex: 'photoCount', width: 80, align: 'right' },
      {
        title: t('faceMatch.common.status'),
        key: 'status',
        render: (_: unknown, record) => (
          <Space size={4} wrap>
            {isActiveEnrollment(record) ? <Tag color="green">{t('faceMatch.enrollment.active')}</Tag> : <Tag>{t('faceMatch.enrollment.withdrawn')}</Tag>}
            {/* 同意已撤回但腾讯云侧删除没完成：明确告诉用户系统还会继续重试，不是已经删干净。 */}
            {record.pendingRemoteDeletion ? (
              <Tooltip title={t('faceMatch.enrollment.pendingRemoteDeletion')}>
                <Tag color="orange">{t('faceMatch.enrollment.pendingRemoteDeletionTag')}</Tag>
              </Tooltip>
            ) : null}
          </Space>
        ),
      },
      {
        title: t('faceMatch.common.actions'),
        key: 'actions',
        width: 90,
        fixed: 'right',
        render: (_: unknown, record) =>
          isActiveEnrollment(record) ? (
            <Button type="link" size="small" danger disabled={writeBlocked} onClick={() => setWithdrawTarget({ kind: 'enrollment', id: record.enrollmentId })}>
              {t('faceMatch.enrollment.withdraw')}
            </Button>
          ) : null,
      },
    ],
    [t, writeBlocked],
  )

  const hasPendingRemote = (enrollments.data ?? []).some((item) => item.pendingRemoteDeletion)

  return (
    <Drawer
      open
      width="min(920px, 100vw)"
      title={t('faceMatch.staff.drawerTitle', { name: staff.displayName })}
      destroyOnHidden
      onClose={onClose}
      extra={
        <Button icon={<ReloadOutlined />} loading={consents.loading || enrollments.loading} onClick={refreshAll}>
          {t('faceMatch.common.refresh')}
        </Button>
      }
    >
      <Space direction="vertical" size={16} style={{ width: '100%' }}>
        {staff.requiresNswNotice ? <Alert type="warning" showIcon message={t('faceMatch.staff.nswNotice')} /> : null}
        {!config.objectStoreConfigured ? <Alert type="warning" showIcon message={t('faceMatch.staff.objectStoreMissing')} /> : null}
        {hasPendingRemote ? <Alert type="warning" showIcon message={t('faceMatch.enrollment.pendingRemoteDeletion')} /> : null}

        <section>
          <Space style={{ marginBottom: 8, width: '100%', justifyContent: 'space-between' }}>
            <Typography.Title level={5} style={{ margin: 0 }}>
              {t('faceMatch.consent.sectionTitle')}
            </Typography.Title>
            <Button type="primary" disabled={uploadBlocked} onClick={() => setConsentOpen(true)}>
              {t('faceMatch.consent.record')}
            </Button>
          </Space>
          {consents.error ? <Alert type="error" showIcon message={describeFaceMatchError(t, consents.error)} style={{ marginBottom: 8 }} /> : null}
          <MeasuredTable<FaceConsent>
            metricId="system.face-match.consents-table-1"
            rowKey="consentId"
            size="small"
            loading={consents.loading && !consents.data}
            dataSource={consents.data ?? []}
            columns={consentColumns}
            pagination={false}
            scroll={{ x: 820 }}
            locale={{ emptyText: t('faceMatch.consent.empty') }}
          />
        </section>

        <section>
          <Space style={{ marginBottom: 8, width: '100%', justifyContent: 'space-between' }}>
            <Typography.Title level={5} style={{ margin: 0 }}>
              {t('faceMatch.enrollment.sectionTitle')}
            </Typography.Title>
            <Tooltip title={enrollable.length === 0 ? t('faceMatch.enrollment.needConsent') : undefined}>
              <Button type="primary" disabled={uploadBlocked || enrollable.length === 0} onClick={() => setEnrollOpen(true)}>
                {t('faceMatch.enrollment.enroll')}
              </Button>
            </Tooltip>
          </Space>
          {enrollments.error ? <Alert type="error" showIcon message={describeFaceMatchError(t, enrollments.error)} style={{ marginBottom: 8 }} /> : null}
          <MeasuredTable<FaceEnrollment>
            metricId="system.face-match.enrollments-table-1"
            rowKey="enrollmentId"
            size="small"
            loading={enrollments.loading && !enrollments.data}
            dataSource={enrollments.data ?? []}
            columns={enrollmentColumns}
            pagination={false}
            scroll={{ x: 560 }}
            locale={{ emptyText: t('faceMatch.enrollment.empty') }}
          />
        </section>
      </Space>

      {consentOpen ? <ConsentModal staff={staff} onCreated={refreshAll} onClose={() => setConsentOpen(false)} /> : null}
      {enrollOpen ? <EnrollmentModal staff={staff} consents={enrollable} onCreated={refreshAll} onClose={() => setEnrollOpen(false)} /> : null}

      {withdrawTarget?.kind === 'consent' ? (
        <ReasonModal
          title={t('faceMatch.consent.withdrawTitle')}
          description={t('faceMatch.consent.withdrawWarning')}
          okText={t('faceMatch.consent.withdrawOk')}
          describeError={describeError}
          onSubmit={async (reason) => {
            const result = await withdrawFaceConsent(withdrawTarget.id, reason)
            if (result.pendingRemoteDeletionCount > 0) {
              void message.warning(t('faceMatch.consent.withdrawPending', { count: result.pendingRemoteDeletionCount }), 8)
            } else {
              void message.success(t('faceMatch.consent.withdrawDone', { count: result.withdrawnEnrollmentCount }))
            }
            refreshAll()
          }}
          onClose={() => setWithdrawTarget(null)}
        />
      ) : null}
      {withdrawTarget?.kind === 'enrollment' ? (
        <ReasonModal
          title={t('faceMatch.enrollment.withdrawTitle')}
          description={t('faceMatch.enrollment.withdrawWarning')}
          okText={t('faceMatch.enrollment.withdrawOk')}
          describeError={describeError}
          onSubmit={async (reason) => {
            const result = await withdrawFaceEnrollment(withdrawTarget.id, reason)
            if (result.pendingRemoteDeletion) {
              void message.warning(t('faceMatch.enrollment.pendingRemoteDeletion'), 8)
            } else {
              void message.success(t('faceMatch.enrollment.withdrawDone'))
            }
            refreshAll()
          }}
          onClose={() => setWithdrawTarget(null)}
        />
      ) : null}
    </Drawer>
  )
}
