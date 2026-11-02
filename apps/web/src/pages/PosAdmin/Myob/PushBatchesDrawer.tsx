import { Alert, Button, Drawer, Empty, Modal, Tag, message } from 'antd'
import type { ColumnsType } from 'antd/es/table'
import { useCallback, useEffect, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'

import { MeasuredTable } from '../../../components/MeasuredTable'
import { getMyobPushBatch, getMyobPushBatches, undoMyobPushBatch } from '../../../services/myobService'
import type { MyobPushBatchDetail, MyobPushBatchItem, MyobPushBatchSummary, MyobPushUserResult } from '../../../types/myob'
import { extractMyobError, formatMyobTime } from './myobLogic'
import {
  canUndoBatch,
  getBatchOutcomeChips,
  getPushCodeMessageKey,
  getPushStatusLabelKey,
  getPushStatusTone,
  summarizeUndo,
} from './myobPushLogic'
import { formatHours, formatPeriodRange } from './myobTimesheetLogic'

interface PushBatchesDrawerProps {
  open: boolean
  connectionGuid: string
  connectionName: string
  periodStart: string
  periodEnd: string
  onClose: () => void
  /** 撤销后通知上层刷新预览（MYOB 里的工时变了，推送状态也变了）。 */
  onUndone: () => void
}

interface DetailState {
  loading: boolean
  error: string | null
  data: MyobPushBatchDetail | null
}

const CHIP_TONES = { failed: 'error', mismatch: 'warning', pushed: 'success', upToDate: 'success', skipped: 'default', undone: 'default' } as const

/**
 * 推送记录：当前公司文件 + 工资周期的所有批次。展开看逐员工结果；有真正写过 MYOB 的批次可以「撤销」——
 * 把推送前的快照写回 MYOB；已发薪 / 内容被人改过 / 有更新批次的员工不会被改动，撤销结果里逐个说明。
 */
export default function PushBatchesDrawer({ open, connectionGuid, connectionName, periodStart, periodEnd, onClose, onUndone }: PushBatchesDrawerProps) {
  const { t } = useTranslation()
  const [batches, setBatches] = useState<MyobPushBatchSummary[]>([])
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [details, setDetails] = useState<Record<string, DetailState>>({})
  const [undoingGuid, setUndoingGuid] = useState<string | null>(null)
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
      const data = await getMyobPushBatches(connectionGuid, periodStart)
      if (alive.current && mine === seq.current) {
        setBatches(data)
        setDetails({})
        setError(null)
      }
    } catch (err) {
      if (alive.current && mine === seq.current) {
        const parsed = extractMyobError(err, t('myob.push.batches.loadFailed'))
        const key = getPushCodeMessageKey(parsed.code)
        setError(key ? t(key) : parsed.message)
      }
    } finally {
      if (alive.current && mine === seq.current) {
        setLoading(false)
      }
    }
  }, [connectionGuid, periodStart, t])

  useEffect(() => {
    if (open) {
      void load()
    }
  }, [open, load])

  const loadDetail = async (batchGuid: string) => {
    setDetails((previous) => ({ ...previous, [batchGuid]: { loading: true, error: null, data: null } }))
    try {
      const data = await getMyobPushBatch(batchGuid)
      if (alive.current) {
        setDetails((previous) => ({ ...previous, [batchGuid]: { loading: false, error: null, data } }))
      }
    } catch (err) {
      if (alive.current) {
        setDetails((previous) => ({
          ...previous,
          [batchGuid]: { loading: false, error: extractMyobError(err, t('myob.push.batches.detailFailed')).message, data: null },
        }))
      }
    }
  }

  const showUndoResult = (results: MyobPushUserResult[]) => {
    const summary = summarizeUndo(results)
    const titleKey =
      summary.kind === 'all'
        ? 'myob.push.batches.undoResult.all'
        : summary.kind === 'partial'
          ? 'myob.push.batches.undoResult.partial'
          : 'myob.push.batches.undoResult.none'
    const columns: ColumnsType<MyobPushUserResult> = [
      { title: t('myob.push.run.columns.employee'), key: 'employee', width: 150, render: (_, item) => <b>{item.userName}</b> },
      {
        title: t('myob.push.run.columns.status'),
        key: 'status',
        width: 150,
        render: (_, item) => (
          <Tag color={getPushStatusTone(item.status)} style={{ marginInlineEnd: 0 }}>
            {t(getPushStatusLabelKey(item.status))}
          </Tag>
        ),
      },
      {
        title: t('myob.push.run.columns.detail'),
        key: 'detail',
        render: (_, item) => {
          const key = getPushCodeMessageKey(item.code)
          return key ? t(key) : item.message || <span className="myob-muted">—</span>
        },
      },
    ]
    Modal.info({
      title: t(titleKey, { undone: summary.undone, total: summary.total }),
      width: 700,
      okText: t('myob.push.batches.undoResult.ok'),
      content: (
        <MeasuredTable<MyobPushUserResult>
          metricId="pos-admin.myob.push-undo-result"
          size="small"
          rowKey="userGuid"
          columns={columns}
          dataSource={results}
          pagination={results.length > 8 ? { pageSize: 8, showSizeChanger: false } : false}
        />
      ),
    })
  }

  const handleUndo = (batch: MyobPushBatchSummary) => {
    Modal.confirm({
      title: t('myob.push.batches.undoConfirmTitle'),
      content: (
        <>
          <p>{t('myob.push.batches.undoConfirmContent', { count: batch.undoable })}</p>
          <p className="myob-sub">{t('myob.push.batches.undoConfirmNote')}</p>
        </>
      ),
      okText: t('myob.push.batches.undoConfirmOk'),
      cancelText: t('myob.push.batches.undoCancel'),
      okButtonProps: { danger: true },
      onOk: async () => {
        setUndoingGuid(batch.batchGuid)
        try {
          const result = await undoMyobPushBatch(batch.batchGuid)
          if (alive.current) {
            showUndoResult(result.results)
          }
          onUndone()
        } catch (err) {
          const parsed = extractMyobError(err, t('myob.push.batches.undoFailed'))
          const key = getPushCodeMessageKey(parsed.code)
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

  const itemColumns: ColumnsType<MyobPushBatchItem> = [
    { title: t('myob.push.run.columns.employee'), key: 'employee', width: 170, render: (_, item) => <b>{item.userName}</b> },
    {
      title: t('myob.push.run.columns.status'),
      key: 'status',
      width: 150,
      render: (_, item) => (
        <Tag color={getPushStatusTone(item.status)} style={{ marginInlineEnd: 0 }}>
          {t(getPushStatusLabelKey(item.status))}
        </Tag>
      ),
    },
    {
      title: t('myob.push.batches.columns.hours'),
      key: 'hours',
      width: 90,
      align: 'right',
      render: (_, item) => (item.pushedHours > 0 ? formatHours(item.pushedHours) : <span className="myob-muted">—</span>),
    },
    {
      title: t('myob.push.run.columns.detail'),
      key: 'detail',
      render: (_, item) => {
        const key = getPushCodeMessageKey(item.errorCode)
        if (key) {
          return t(key)
        }
        return item.undoneAt ? t('myob.push.batches.undoneAt', { time: formatMyobTime(item.undoneAt) }) : <span className="myob-muted">—</span>
      },
    },
  ]

  const columns: ColumnsType<MyobPushBatchSummary> = [
    { title: t('myob.push.batches.columns.time'), key: 'time', width: 150, render: (_, batch) => formatMyobTime(batch.startedAt) },
    { title: t('myob.push.batches.columns.actor'), key: 'actor', width: 110, render: (_, batch) => batch.actorName || '--' },
    {
      title: t('myob.push.batches.columns.result'),
      key: 'result',
      render: (_, batch) => (
        <span className="myob-push-chips">
          <span className="myob-muted">{t('myob.push.batches.total', { count: batch.total })}</span>
          {getBatchOutcomeChips(batch).map((chip) => (
            <Tag key={chip.key} color={CHIP_TONES[chip.key]} style={{ marginInlineEnd: 0 }}>
              {t(chip.labelKey, { count: chip.count })}
            </Tag>
          ))}
        </span>
      ),
    },
    {
      title: t('myob.push.batches.columns.actions'),
      key: 'actions',
      width: 90,
      render: (_, batch) =>
        canUndoBatch(batch) ? (
          <Button size="small" danger loading={undoingGuid === batch.batchGuid} onClick={() => handleUndo(batch)}>
            {t('myob.push.batches.undo')}
          </Button>
        ) : null,
    },
  ]

  return (
    <Drawer
      open={open}
      title={t('myob.push.batches.title')}
      width={760}
      onClose={onClose}
      destroyOnHidden
      extra={
        <Button onClick={() => void load()} loading={loading}>
          {t('myob.push.batches.reload')}
        </Button>
      }
    >
      <div className="myob-sub myob-push-batches-sub">
        {t('myob.push.batches.subtitle', { name: connectionName, range: formatPeriodRange(periodStart, periodEnd) })}
      </div>
      <Alert type="info" showIcon className="myob-run-alert" message={t('myob.push.batches.undoNote')} />
      {error ? <Alert type="error" showIcon className="myob-run-alert" message={error} /> : null}
      <MeasuredTable<MyobPushBatchSummary>
        metricId="pos-admin.myob.push-batches"
        size="middle"
        rowKey="batchGuid"
        columns={columns}
        dataSource={batches}
        loading={loading}
        pagination={false}
        scroll={{ x: 'max-content' }}
        locale={{ emptyText: <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={t('myob.push.batches.empty')} /> }}
        expandable={{
          onExpand: (expanded, batch) => {
            if (expanded && !details[batch.batchGuid]) {
              void loadDetail(batch.batchGuid)
            }
          },
          expandedRowRender: (batch) => {
            const state = details[batch.batchGuid]
            if (!state || state.loading) {
              return <span className="myob-muted">{t('myob.push.batches.detailLoading')}</span>
            }
            if (state.error) {
              return <Alert type="error" showIcon message={state.error} />
            }
            return (
              <MeasuredTable<MyobPushBatchItem>
                metricId="pos-admin.myob.push-batch-items"
                size="small"
                rowKey={(item) => `${item.userGuid}-${item.startedAt}`}
                columns={itemColumns}
                dataSource={state.data?.items ?? []}
                pagination={false}
                scroll={{ x: 'max-content' }}
              />
            )
          },
        }}
      />
    </Drawer>
  )
}
