import { CheckCircleFilled, LockOutlined, RollbackOutlined } from '@ant-design/icons'
import { Button, Table, Tag } from 'antd'
import type { ColumnsType } from 'antd/es/table'
import { useState } from 'react'
import { useTranslation } from 'react-i18next'

import type { MyobSyncApplyResult, MyobSyncFailure, MyobSyncField, MyobSyncUndoResult, MyobSyncWritten } from '../../../types/myob'

import {
  countFailedChanges,
  describeSyncFailure,
  isSensitiveField,
  isUndoAvailable,
  splitWritten,
  syncFieldLabelKey,
} from './myobSyncLogic'

interface MyobSyncResultStepProps {
  result: MyobSyncApplyResult
  /** 本次预览里「无差异」的员工数（「N 人无需同步」）。 */
  skippedNoDiff: number
  /** 撤销结果；非空表示已撤销。 */
  undone: MyobSyncUndoResult | null
  undoing: boolean
  now: number
  onUndo: () => void
  onRescope: (failure: MyobSyncFailure) => void
}

function FieldTags({ fields }: { fields: readonly MyobSyncField[] }) {
  const { t } = useTranslation()
  return (
    <span className="myob-sync-tags">
      {fields.map((field) => (
        <Tag key={field} style={{ marginInlineEnd: 0 }}>
          {t(syncFieldLabelKey(field), { defaultValue: field })}
          {isSensitiveField(field) ? <LockOutlined className="myob-sync-lock-icon" aria-label={t('myob.sync.step1.sensitive')} /> : null}
        </Tag>
      ))}
    </span>
  )
}

/** 第 3 步：汇总、已写入、失败（可重新预览）、撤销卡片。 */
export default function MyobSyncResultStep({ result, skippedNoDiff, undone, undoing, now, onUndo, onRescope }: MyobSyncResultStepProps) {
  const { t } = useTranslation()
  const [expanded, setExpanded] = useState(false)
  const split = splitWritten(result.written)
  const shown = expanded ? result.written : split.head
  const failedChanges = countFailedChanges(result.failed)
  const nothingWritten = result.writtenChangeCount === 0

  const columns: ColumnsType<MyobSyncWritten> = [
    {
      title: t('myob.sync.step3.columns.employee'),
      key: 'employee',
      width: 230,
      render: (_, row) => <span className="myob-person-name">{row.userName}</span>,
    },
    { title: t('myob.sync.step3.columns.fields'), key: 'fields', render: (_, row) => <FieldTags fields={row.fields} /> },
    {
      title: t('myob.sync.step3.columns.result'),
      key: 'result',
      width: 100,
      align: 'right',
      render: (_, row) => (
        <Tag color="success" style={{ marginInlineEnd: 0 }}>
          {t('myob.sync.step3.writtenCount', { count: row.fields.length })}
        </Tag>
      ),
    },
  ]

  // 汇总第二行：失败 / 无需同步的情况组合
  const otherLine =
    result.failed.length > 0
      ? skippedNoDiff > 0
        ? t('myob.sync.step3.otherLine', { people: result.failed.length, changes: failedChanges, skipped: skippedNoDiff })
        : t('myob.sync.step3.otherFailedOnly', { people: result.failed.length, changes: failedChanges })
      : skippedNoDiff > 0
        ? t('myob.sync.step3.otherNoFailed', { skipped: skippedNoDiff })
        : null

  return (
    <div className="myob-sync-step">
      <div className={`myob-sync-summary${nothingWritten ? ' myob-sync-summary-none' : ''}`}>
        <CheckCircleFilled className="myob-sync-summary-icon" aria-hidden="true" />
        <div className="myob-sync-summary-text">
          <div className="myob-sync-summary-title">
            {nothingWritten
              ? t('myob.sync.step3.noneWritten')
              : t('myob.sync.step3.writtenTitle', { people: result.writtenEmployeeCount, changes: result.writtenChangeCount })}
          </div>
          {otherLine ? <span className="myob-sub">{otherLine}</span> : null}
        </div>
        {result.writtenSensitiveCount > 0 ? (
          <Tag icon={<LockOutlined />}>{t('myob.sync.step3.sensitiveChip', { count: result.writtenSensitiveCount })}</Tag>
        ) : null}
      </div>

      {result.written.length > 0 ? (
        <>
          <h3 className="myob-section-title">{t('myob.sync.step3.writtenSection')}</h3>
          <Table<MyobSyncWritten> size="small" rowKey="userGuid" columns={columns} dataSource={shown} pagination={false} />
          {split.rest.length > 0 ? (
            <div className="myob-sync-more">
              {expanded ? null : t('myob.sync.step3.rest', { people: split.rest.length, changes: split.restChangeCount })}{' '}
              <Button type="link" size="small" onClick={() => setExpanded((value) => !value)}>
                {expanded ? t('myob.sync.step3.collapse') : t('myob.sync.step3.expandAll')}
              </Button>
            </div>
          ) : null}
        </>
      ) : null}

      {result.failed.map((failure) => {
        const reason = describeSyncFailure(failure)
        return (
          <div key={failure.userGuid} className="myob-sync-failed" role="alert">
            <Tag color="error" style={{ marginInlineEnd: 0 }}>
              {t('myob.sync.step3.failedTag', { count: 1 })}
            </Tag>
            <div className="myob-sync-failed-text">
              <strong>
                {t('myob.sync.step3.failedHead', {
                  name: failure.userName,
                  fields: failure.fields.map((field) => t(syncFieldLabelKey(field), { defaultValue: field })).join('、'),
                  count: failure.fields.length,
                })}
              </strong>
              <span className="myob-sub myob-sync-failed-reason">
                {reason.text ?? (reason.messageKey ? t(reason.messageKey) : failure.code)}
              </span>
            </div>
            <Button size="small" onClick={() => onRescope(failure)}>
              {t('myob.sync.step3.rescope')}
            </Button>
          </div>
        )
      })}

      {/* 撤销卡片：没有 batchGuid（全部失败）或已过截止时间不显示；撤销后换成「已撤销」说明 */}
      {undone ? (
        <div className="myob-sync-undo" role="status">
          <RollbackOutlined className="myob-sync-undo-icon" aria-hidden="true" />
          <div className="myob-sync-undo-text">
            <strong>{t('myob.sync.step3.undoneCard')}</strong>
            <span className="myob-sub">
              {t('myob.sync.step3.undone', { people: undone.restoredEmployeeCount, changes: undone.restoredChangeCount })}
              {undone.skippedChanged.length > 0
                ? ` · ${t('myob.sync.step3.undoSkipped', { count: undone.skippedChanged.reduce((sum, item) => sum + item.fields.length, 0) })}`
                : ''}
            </span>
          </div>
        </div>
      ) : isUndoAvailable(result, now) ? (
        <div className="myob-sync-undo">
          <RollbackOutlined className="myob-sync-undo-icon" aria-hidden="true" />
          <div className="myob-sync-undo-text">
            <strong>{t('myob.sync.step3.undoTitle')}</strong>
            <span className="myob-sub">{t('myob.sync.step3.undoDesc')}</span>
          </div>
          <Button danger loading={undoing} onClick={onUndo}>
            {t('myob.sync.step3.undo')}
          </Button>
        </div>
      ) : null}
    </div>
  )
}
