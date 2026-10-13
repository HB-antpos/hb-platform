import { PlusOutlined, ReloadOutlined } from '@ant-design/icons'
import { Alert, Button, Modal, Select, Space, Tag, Tooltip, Typography, message } from 'antd'
import type { ColumnsType } from 'antd/es/table'
import { useCallback, useMemo, useState } from 'react'
import { useTranslation } from 'react-i18next'

import { MeasuredTable } from '../../../components/MeasuredTable'
import {
  approveUnwelcomeEntry,
  getUnwelcomeEntries,
  setUnwelcomeDisputeNote,
  withdrawUnwelcomeEntry,
} from '../../../services/faceMatchService'
import type { FaceMatchPage, FaceUnwelcomeEntry } from '../../../types/faceMatch'
import { formatDateTime } from '../FaceCameras/logic'
import {
  DISPUTE_NOTE_MAX_LENGTH,
  ENTRY_STATUS_COLORS,
  ENTRY_STATUS_FILTER_OPTIONS,
  ENTRY_STATUS_LABEL_KEYS,
  INCIDENT_CATEGORY_LABEL_KEYS,
  PAGE_SIZE,
  canWithdrawEntry,
  describeFaceMatchError,
  formatApprovalProgress,
} from './logic'
import { NUMERIC_STYLE, Pager, StoreSelect, type FaceMatchTabContext } from './shared'
import ReasonModal from './ReasonModal'
import UnwelcomeEntryModal from './UnwelcomeEntryModal'
import { useRemote } from './useRemote'

type EntryAction = { kind: 'withdraw' | 'dispute'; entry: FaceUnwelcomeEntry } | null

/**
 * 不受欢迎人员名单条目页签（Manage / Approve）：条目表、提交、批准（两人）、撤回、申诉备注、到期提醒。
 * 条目里没有姓名与事件细节，也不展示照片（照片只在复核页并排查看）。
 */
export default function UnwelcomeEntriesTab({ context }: { context: FaceMatchTabContext }) {
  const { t } = useTranslation()
  const { config, storeOptions, storeOptionsFailed, writeBlocked } = context
  const { unwelcomeManage } = config.permissions
  const [modal, modalContextHolder] = Modal.useModal()
  const [status, setStatus] = useState<number | undefined>(undefined)
  const [storeCode, setStoreCode] = useState<string | null>(null)
  const [page, setPage] = useState(1)
  const [createOpen, setCreateOpen] = useState(false)
  const [action, setAction] = useState<EntryAction>(null)
  const [busyIds, setBusyIds] = useState<ReadonlySet<string>>(() => new Set())

  const loader = useMemo(
    () => (signal: AbortSignal) => getUnwelcomeEntries({ status, storeCode: storeCode ?? undefined, page, pageSize: PAGE_SIZE }, signal),
    [status, storeCode, page],
  )
  const entries = useRemote<FaceMatchPage<FaceUnwelcomeEntry>>(loader)

  const describeError = useCallback((error: unknown) => describeFaceMatchError(t, error), [t])
  const uploadBlocked = writeBlocked || !config.objectStoreConfigured

  const setBusy = (entryId: string, busy: boolean) =>
    setBusyIds((current) => {
      const next = new Set(current)
      if (busy) next.add(entryId)
      else next.delete(entryId)
      return next
    })

  // 批准：二次确认；两位批准人的条目（Other）会提示还需要另一位不同的批准人。
  const approve = (entry: FaceUnwelcomeEntry) => {
    modal.confirm({
      title: t('faceMatch.unwelcome.approve.title'),
      content: entry.approvalsNeeded > 1 ? t('faceMatch.unwelcome.approve.contentTwo', { done: entry.approvalsDone, needed: entry.approvalsNeeded }) : t('faceMatch.unwelcome.approve.content'),
      okText: t('faceMatch.unwelcome.approve.ok'),
      cancelText: t('faceMatch.common.cancel'),
      onOk: async () => {
        setBusy(entry.entryId, true)
        try {
          await approveUnwelcomeEntry(entry.entryId)
          void message.success(t('faceMatch.unwelcome.approve.done'))
          entries.reload()
        } catch (error) {
          void message.error(describeError(error))
        } finally {
          setBusy(entry.entryId, false)
        }
      },
    })
  }

  const columns = useMemo<ColumnsType<FaceUnwelcomeEntry>>(
    () => [
      {
        title: t('faceMatch.unwelcome.columns.category'),
        key: 'category',
        width: 190,
        render: (_: unknown, record) => (
          <div>
            <div>{INCIDENT_CATEGORY_LABEL_KEYS[record.incidentCategory] ? t(INCIDENT_CATEGORY_LABEL_KEYS[record.incidentCategory]) : record.incidentCategoryName}</div>
            {record.categoryNote ? (
              <Typography.Text type="secondary" ellipsis style={{ maxWidth: 170 }}>{record.categoryNote}</Typography.Text>
            ) : null}
          </div>
        ),
      },
      { title: t('faceMatch.unwelcome.columns.evidenceRef'), dataIndex: 'evidenceRef', width: 160, ellipsis: true },
      {
        title: t('faceMatch.unwelcome.columns.storeScope'),
        dataIndex: 'storeScope',
        width: 160,
        render: (value: string[]) => (
          <Space size={2} wrap>
            {value.slice(0, 3).map((code) => <Tag key={code} style={{ marginInlineEnd: 0 }}>{code}</Tag>)}
            {value.length > 3 ? <Tooltip title={value.join(', ')}><Tag style={{ marginInlineEnd: 0 }}>+{value.length - 3}</Tag></Tooltip> : null}
          </Space>
        ),
      },
      {
        title: t('faceMatch.common.status'),
        key: 'status',
        width: 150,
        render: (_: unknown, record) => (
          <Space size={4} wrap>
            <Tag color={ENTRY_STATUS_COLORS[record.status] ?? 'default'} style={{ marginInlineEnd: 0 }}>
              {ENTRY_STATUS_LABEL_KEYS[record.status] ? t(ENTRY_STATUS_LABEL_KEYS[record.status]) : record.statusName}
            </Tag>
            {record.expiringSoon ? <Tag color="orange" style={{ marginInlineEnd: 0 }}>{t('faceMatch.unwelcome.expiringSoon', { days: record.daysToExpiry })}</Tag> : null}
            {/* 条目已失效但腾讯云侧人员 / 参考照片还没删干净：提示后台仍在清理（不是已经删除完毕）。 */}
            {record.cleanupPending ? (
              <Tooltip title={t('faceMatch.unwelcome.cleanupPendingTip')}>
                <Tag color="blue" style={{ marginInlineEnd: 0 }}>{t('faceMatch.unwelcome.cleanupPending')}</Tag>
              </Tooltip>
            ) : null}
          </Space>
        ),
      },
      {
        title: t('faceMatch.unwelcome.columns.approvals'),
        key: 'approvals',
        width: 130,
        render: (_: unknown, record) => (
          <Tooltip title={[record.approvedByName, record.secondApprovedByName].filter(Boolean).join(' / ') || undefined}>
            <span style={NUMERIC_STYLE}>{formatApprovalProgress(record)}</span>
          </Tooltip>
        ),
      },
      {
        title: t('faceMatch.unwelcome.columns.expiresAt'),
        dataIndex: 'expiresAtUtc',
        width: 130,
        render: (value: string) => <span style={NUMERIC_STYLE}>{formatDateTime(value).slice(0, 10)}</span>,
      },
      {
        title: t('faceMatch.unwelcome.columns.submitted'),
        key: 'submitted',
        width: 190,
        render: (_: unknown, record) => (
          <div>
            <div>{record.submittedByName}</div>
            <Typography.Text type="secondary" style={NUMERIC_STYLE}>{formatDateTime(record.submittedAtUtc)}</Typography.Text>
          </div>
        ),
      },
      {
        title: t('faceMatch.unwelcome.columns.disputeNote'),
        dataIndex: 'disputeNote',
        width: 180,
        ellipsis: { showTitle: false },
        render: (value: string | null) => (value ? <Tooltip title={value} placement="topLeft"><span>{value}</span></Tooltip> : '-'),
      },
      {
        title: t('faceMatch.common.actions'),
        key: 'actions',
        width: 260,
        fixed: 'right',
        render: (_: unknown, record) => {
          const busy = busyIds.has(record.entryId)
          return (
            <Space size={0} wrap>
              {record.canApprove && config.permissions.unwelcomeApprove ? (
                <Button type="link" size="small" disabled={busy || writeBlocked} onClick={() => approve(record)}>
                  {t('faceMatch.unwelcome.actions.approve')}
                </Button>
              ) : null}
              {unwelcomeManage ? (
                <Button type="link" size="small" disabled={busy || writeBlocked} onClick={() => setAction({ kind: 'dispute', entry: record })}>
                  {t('faceMatch.unwelcome.actions.dispute')}
                </Button>
              ) : null}
              {unwelcomeManage && canWithdrawEntry(record) ? (
                <Button type="link" size="small" danger disabled={busy || writeBlocked} onClick={() => setAction({ kind: 'withdraw', entry: record })}>
                  {t('faceMatch.unwelcome.actions.withdraw')}
                </Button>
              ) : null}
            </Space>
          )
        },
      },
    ],
    // approve 只依赖稳定的 setter 与 modal，随 busyIds / 语言刷新即可。
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [t, busyIds, writeBlocked, unwelcomeManage, config.permissions.unwelcomeApprove, modal],
  )

  return (
    <Space direction="vertical" size={12} style={{ width: '100%' }}>
      {modalContextHolder}
      <Alert type="info" showIcon message={t('faceMatch.unwelcome.intro')} />
      {!config.objectStoreConfigured ? <Alert type="warning" showIcon message={t('faceMatch.unwelcome.objectStoreMissing')} /> : null}
      <Space wrap style={{ width: '100%', justifyContent: 'space-between' }}>
        <Space wrap>
          <Select
            allowClear
            style={{ width: 160 }}
            placeholder={t('faceMatch.unwelcome.allStatuses')}
            value={status}
            options={ENTRY_STATUS_FILTER_OPTIONS.map((option) => ({ value: option.value, label: t(option.labelKey) }))}
            onChange={(value: number | undefined) => {
              setStatus(value)
              setPage(1)
            }}
          />
          <StoreSelect
            options={storeOptions}
            failed={storeOptionsFailed}
            value={storeCode}
            onChange={(value) => {
              setStoreCode(value)
              setPage(1)
            }}
            placeholder={t('faceMatch.unwelcome.allStores')}
            style={{ width: 220 }}
          />
          <Button icon={<ReloadOutlined />} loading={entries.loading} onClick={entries.reload}>
            {t('faceMatch.common.refresh')}
          </Button>
        </Space>
        {unwelcomeManage ? (
          <Button type="primary" icon={<PlusOutlined />} disabled={uploadBlocked} onClick={() => setCreateOpen(true)}>
            {t('faceMatch.unwelcome.actions.create')}
          </Button>
        ) : null}
      </Space>
      {entries.error ? (
        <Alert type="error" showIcon message={describeFaceMatchError(t, entries.error)} action={<Button size="small" onClick={entries.reload}>{t('faceMatch.common.retry')}</Button>} />
      ) : null}
      <MeasuredTable<FaceUnwelcomeEntry>
        metricId="system.face-match.unwelcome-entries-table-1"
        rowKey="entryId"
        size="small"
        loading={entries.loading && !entries.data}
        dataSource={entries.data?.items ?? []}
        columns={columns}
        pagination={false}
        scroll={{ x: 1500 }}
        locale={{ emptyText: t('faceMatch.unwelcome.empty') }}
      />
      <Pager page={page} hasMore={entries.data?.hasMore === true} loading={entries.loading} onChange={setPage} />

      {createOpen ? <UnwelcomeEntryModal context={context} onCreated={entries.reload} onClose={() => setCreateOpen(false)} /> : null}
      {action?.kind === 'withdraw' ? (
        <ReasonModal
          title={t('faceMatch.unwelcome.withdraw.title')}
          description={t('faceMatch.unwelcome.withdraw.warning')}
          okText={t('faceMatch.unwelcome.withdraw.ok')}
          describeError={describeError}
          onSubmit={async (reason) => {
            await withdrawUnwelcomeEntry(action.entry.entryId, reason)
            void message.success(t('faceMatch.unwelcome.withdraw.done'))
            entries.reload()
          }}
          onClose={() => setAction(null)}
        />
      ) : null}
      {action?.kind === 'dispute' ? (
        <ReasonModal
          title={t('faceMatch.unwelcome.dispute.title')}
          description={t('faceMatch.unwelcome.dispute.description')}
          okText={t('faceMatch.unwelcome.dispute.ok')}
          label={t('faceMatch.unwelcome.dispute.label')}
          describeError={describeError}
          danger={false}
          optional
          maxLength={DISPUTE_NOTE_MAX_LENGTH}
          initialValue={action.entry.disputeNote ?? ''}
          onSubmit={async (note) => {
            await setUnwelcomeDisputeNote(action.entry.entryId, note)
            void message.success(t('faceMatch.unwelcome.dispute.done'))
            entries.reload()
          }}
          onClose={() => setAction(null)}
        />
      ) : null}
    </Space>
  )
}
