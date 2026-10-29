import { Alert, Button, Modal, Table, Tag, message } from 'antd'
import type { ColumnsType } from 'antd/es/table'
import { useCallback, useEffect, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'

import { getMyobSyncBatches, undoMyobSyncBatch } from '../../../services/myobService'
import type { MyobSyncBatch } from '../../../types/myob'

import { extractMyobError, formatMyobTime } from './myobLogic'
import { getBatchStatusKey, getSyncErrorMessageKey } from './myobSyncLogic'

interface MyobSyncBatchesModalProps {
  open: boolean
  connectionGuid: string
  onClose: () => void
  /** 在这里撤销成功后通知上层（员工资料变了，列表要刷新）。 */
  onUndone: () => void
}

const STATUS_COLORS = { Active: 'processing', Undone: 'default', Expired: 'default' } as const

/** 同步记录：最近 30 次，可撤销的带「撤销」按钮（二次确认）。 */
export default function MyobSyncBatchesModal({ open, connectionGuid, onClose, onUndone }: MyobSyncBatchesModalProps) {
  const { t } = useTranslation()
  const [batches, setBatches] = useState<MyobSyncBatch[]>([])
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [undoingGuid, setUndoingGuid] = useState<string | null>(null)
  // 只认最后一次请求；卸载后不再写状态
  const seq = useRef(0)
  const alive = useRef(true)
  useEffect(() => {
    alive.current = true
    return () => {
      alive.current = false
    }
  }, [])

  const load = useCallback(async () => {
    const mine = ++seq.current
    setLoading(true)
    try {
      const data = await getMyobSyncBatches(connectionGuid)
      if (alive.current && mine === seq.current) {
        setBatches(data)
        setError(null)
      }
    } catch (err) {
      if (alive.current && mine === seq.current) {
        setError(extractMyobError(err, t('myob.sync.batch.loadFailed')).message)
      }
    } finally {
      if (alive.current && mine === seq.current) {
        setLoading(false)
      }
    }
  }, [connectionGuid, t])

  useEffect(() => {
    if (open) {
      void load()
    }
  }, [open, load])

  const handleUndo = (batch: MyobSyncBatch) => {
    Modal.confirm({
      title: t('myob.sync.step3.undoConfirmTitle'),
      content: t('myob.sync.step3.undoConfirmContent', { people: batch.employeeCount, changes: batch.changeCount }),
      okText: t('myob.sync.step3.undoConfirmOk'),
      cancelText: t('myob.sync.step3.undoCancel'),
      okButtonProps: { danger: true },
      onOk: async () => {
        setUndoingGuid(batch.batchGuid)
        try {
          const result = await undoMyobSyncBatch(batch.batchGuid)
          message.success(t('myob.sync.step3.undone', { people: result.restoredEmployeeCount, changes: result.restoredChangeCount }))
          if (result.skippedChanged.length > 0) {
            message.warning(
              t('myob.sync.step3.undoSkipped', { count: result.skippedChanged.reduce((sum, item) => sum + item.fields.length, 0) }),
            )
          }
          onUndone()
        } catch (err) {
          const parsed = extractMyobError(err, t('myob.sync.step3.undoFailed'))
          const key = getSyncErrorMessageKey(parsed.code)
          message.error(key ? t(key) : parsed.message)
        } finally {
          if (alive.current) {
            setUndoingGuid(null)
            void load()
          }
        }
      },
    })
  }

  const now = Date.now()
  const columns: ColumnsType<MyobSyncBatch> = [
    { title: t('myob.sync.batch.columns.time'), key: 'time', width: 150, render: (_, row) => formatMyobTime(row.createdAt) },
    { title: t('myob.sync.batch.columns.actor'), key: 'actor', width: 110, render: (_, row) => row.actorName || '--' },
    {
      title: t('myob.sync.batch.columns.scope'),
      key: 'scope',
      render: (_, row) => (
        <>
          {t('myob.sync.batch.scope', { people: row.employeeCount, changes: row.changeCount })}
          {row.sensitiveCount > 0 ? <span className="myob-sub">{t('myob.sync.batch.scopeSensitive', { count: row.sensitiveCount })}</span> : null}
        </>
      ),
    },
    {
      title: t('myob.sync.batch.columns.status'),
      key: 'status',
      width: 200,
      render: (_, row) => {
        const status = getBatchStatusKey(row, now)
        return (
          <>
            <Tag color={STATUS_COLORS[status]} style={{ marginInlineEnd: 0 }}>
              {t(`myob.sync.batch.status.${status}`)}
            </Tag>
            {status === 'Undone' && row.undoneAt ? (
              <span className="myob-sub">{t('myob.sync.batch.undoneBy', { name: row.undoneByName || '--', time: formatMyobTime(row.undoneAt) })}</span>
            ) : null}
            {status === 'Active' ? <span className="myob-sub">{t('myob.sync.batch.deadline', { time: formatMyobTime(row.undoDeadline) })}</span> : null}
          </>
        )
      },
    },
    {
      title: t('myob.sync.batch.columns.actions'),
      key: 'actions',
      width: 80,
      render: (_, row) =>
        row.canUndo && getBatchStatusKey(row, now) === 'Active' ? (
          <Button size="small" danger loading={undoingGuid === row.batchGuid} onClick={() => handleUndo(row)}>
            {t('myob.sync.batch.undo')}
          </Button>
        ) : null,
    },
  ]

  return (
    <Modal open={open} title={t('myob.sync.batch.title')} width={760} footer={null} destroyOnClose onCancel={onClose}>
      <p className="myob-sub">{t('myob.sync.batch.hint')}</p>
      {error ? (
        <Alert
          type="error"
          showIcon
          style={{ marginBottom: 12 }}
          message={t('myob.sync.batch.loadFailed')}
          description={error}
          action={
            <Button size="small" onClick={() => void load()}>
              {t('myob.sync.batch.retry')}
            </Button>
          }
        />
      ) : null}
      <Table<MyobSyncBatch>
        size="small"
        rowKey="batchGuid"
        columns={columns}
        dataSource={batches}
        loading={loading}
        pagination={false}
        scroll={{ y: 420 }}
        locale={{ emptyText: t('myob.sync.batch.empty') }}
      />
    </Modal>
  )
}
