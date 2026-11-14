import { LoadingOutlined, ReloadOutlined } from '@ant-design/icons'
import { Alert, Button, Tag, Tooltip } from 'antd'
import type { ColumnsType } from 'antd/es/table'
import { useCallback, useEffect, useMemo, useState } from 'react'
import { useTranslation } from 'react-i18next'

import { MeasuredTable } from '../../../../components/MeasuredTable'
import { getWarehousePackProgress, readPackProgressFailure } from '../../../../services/warehousePackProgressService'
import type { WarehousePackProgress, WarehousePackProgressCarton, WarehousePackProgressLine } from '../../../../types/packing'

import {
  formatPackDiff,
  formatPackPieces,
  hasPackLineMismatch,
  packCartonStatusKind,
  packLineDiff,
  resolvePackCompletionWarning,
  shouldShowPackProgress,
  sortPackLines,
  summarizePackProgress,
} from './packProgressLogic'
import './messages'
import './packProgress.css'

/**
 * 读取订单的装箱进度。只读、不打扰主流程：无权限（403）、接口失败一律静默，只在控制台留一条记录，
 * 界面当作「没有装箱情况」处理；刷新失败时保留上一次读到的数据。
 */
function usePackProgress(orderGuid: string, enabled: boolean, reloadKey?: unknown) {
  const [progress, setProgress] = useState<WarehousePackProgress | null>(null)
  const [loading, setLoading] = useState(false)
  const [version, setVersion] = useState(0)

  useEffect(() => {
    if (!enabled || !orderGuid) {
      return undefined
    }
    const controller = new AbortController()
    setLoading(true)
    getWarehousePackProgress(orderGuid, controller.signal)
      .then((next) => {
        if (!controller.signal.aborted) {
          setProgress(next)
        }
      })
      .catch((error: unknown) => {
        if (controller.signal.aborted) {
          return
        }
        const failure = readPackProgressFailure(error)
        console.warn('[pack-progress] 装箱情况读取失败，已隐藏', { orderGuid, status: failure.status, message: failure.message })
      })
      .finally(() => {
        if (!controller.signal.aborted) {
          setLoading(false)
        }
      })
    return () => controller.abort()
  }, [enabled, orderGuid, reloadKey, version])

  const reload = useCallback(() => setVersion((current) => current + 1), [])
  return { progress, loading, reload }
}

interface PackProgressSectionProps {
  orderGuid: string
  /** 订单详情页是否在前台（keep-alive 切回来时重新读取）。 */
  active?: boolean
  /** 订单流程状态变了（完成出库等）时重新读取。 */
  flowStatus?: number | null
}

/**
 * 订单详情「装箱情况」卡片（只读）：三格摘要、按商品的订货 / 已拣 / 已装对照、订单名下的箱。
 * 订单没有任何箱（没用装箱功能）、无权限或读取失败时整块不渲染。
 */
export default function PackProgressSection({ orderGuid, active = true, flowStatus }: PackProgressSectionProps) {
  const { t } = useTranslation()
  const { progress, loading, reload } = usePackProgress(orderGuid, active, flowStatus)

  const summary = useMemo(() => (progress ? summarizePackProgress(progress) : null), [progress])
  const lines = useMemo(() => sortPackLines(progress?.lines ?? []), [progress])

  const lineColumns = useMemo<ColumnsType<WarehousePackProgressLine>>(
    () => [
      { title: t('storeOrders.packProgress.col.itemNumber'), dataIndex: 'itemNumber', width: 110, render: (value?: string | null) => value || '—' },
      {
        title: t('storeOrders.packProgress.col.product'),
        key: 'product',
        ellipsis: true,
        render: (_value, line) => line.productName || line.productCode,
      },
      {
        title: t('storeOrders.packProgress.col.ordered'),
        dataIndex: 'orderedPieces',
        width: 72,
        align: 'right',
        render: (value: number) => formatPackPieces(value),
      },
      {
        title: t('storeOrders.packProgress.col.picked'),
        dataIndex: 'pickedPieces',
        width: 72,
        align: 'right',
        render: (value: number) => formatPackPieces(value),
      },
      {
        title: t('storeOrders.packProgress.col.packed'),
        dataIndex: 'packedPieces',
        width: 72,
        align: 'right',
        render: (value: number) => formatPackPieces(value),
      },
      {
        title: <Tooltip title={t('storeOrders.packProgress.col.diffTip')}>{t('storeOrders.packProgress.col.diff')}</Tooltip>,
        key: 'diff',
        width: 64,
        align: 'right',
        render: (_value, line) => (
          <span className={hasPackLineMismatch(line) ? 'pack-progress-diff is-warn' : 'pack-progress-diff'}>
            {formatPackDiff(packLineDiff(line))}
          </span>
        ),
      },
    ],
    [t],
  )

  const cartonColumns = useMemo<ColumnsType<WarehousePackProgressCarton>>(
    () => [
      {
        title: t('storeOrders.packProgress.col.cartonNo'),
        dataIndex: 'cartonNo',
        width: 120,
        render: (value: string) => <span className="pack-progress-mono">{value}</span>,
      },
      {
        title: t('storeOrders.packProgress.col.status'),
        dataIndex: 'status',
        width: 84,
        render: (status: number) => {
          const kind = packCartonStatusKind(status)
          const color = kind === 'packing' ? 'blue' : kind === 'sealed' ? 'green' : kind === 'transit' ? 'purple' : undefined
          return <Tag color={color}>{t(`storeOrders.packProgress.status.${kind}`)}</Tag>
        },
      },
      {
        title: t('storeOrders.packProgress.col.pallet'),
dataIndex: 'palletNo',
        width: 120,
        render: (value?: string | null) => (value ? <span className="pack-progress-mono">{value}</span> : '—'),
      },
      { title: t('storeOrders.packProgress.col.sku'), dataIndex: 'skuCount', width: 56, align: 'right' },
      { title: t('storeOrders.packProgress.col.pieces'), dataIndex: 'pieceCount', width: 56, align: 'right' },
    ],
    [t],
  )

  // 没有任何箱 / 没读到数据：整块不显示（不闪空卡片，也不提示错误）。
  if (!progress || !summary || !shouldShowPackProgress(progress)) {
    return null
  }

  const title = t('storeOrders.packProgress.title')
  return (
    <section className="pack-progress-section" aria-label={title}>
      <div className="pack-progress-head">
        <h2 className="pack-progress-title">{title}</h2>
        {summary.mismatchLineCount > 0 ? (
          <span className="pack-progress-pill is-warn">{t('storeOrders.packProgress.pillMismatch', { count: summary.mismatchLineCount })}</span>
        ) : progress.reconciled ? (
          <span className="pack-progress-pill is-ok">{t('storeOrders.packProgress.pillReconciled')}</span>
        ) : null}
        {progress.openCartonCount > 0 ? (
          <span className="pack-progress-pill is-warn">{t('storeOrders.packProgress.pillOpenCartons', { count: progress.openCartonCount })}</span>
        ) : null}
        <span className="pack-progress-spacer" />
        <Button
          size="small"
          type="text"
          icon={loading ? <LoadingOutlined /> : <ReloadOutlined />}
          disabled={loading}
          aria-label={t('storeOrders.packProgress.refresh')}
          title={t('storeOrders.packProgress.refresh')}
          onClick={reload}
        />
      </div>

      <div className="pack-progress-body">
        <div className="pack-progress-stats">
          <div className="pack-progress-stat">
            <span className="pack-progress-stat-label">{t('storeOrders.packProgress.statPicked')}</span>
            <b className="pack-progress-stat-value">{formatPackPieces(summary.pickedPieces)}</b>
          </div>
          <div className={summary.packedDiffersFromPicked ? 'pack-progress-stat is-warn' : 'pack-progress-stat'}>
            <span className="pack-progress-stat-label">{t('storeOrders.packProgress.statPacked')}</span>
            <b className="pack-progress-stat-value">{formatPackPieces(summary.packedPieces)}</b>
            {summary.packedDiffersFromPicked ? (
              <small className="pack-progress-stat-note">{t('storeOrders.packProgress.statPackedWarn')}</small>
            ) : null}
          </div>
          <div className="pack-progress-stat">
            <span className="pack-progress-stat-label">{t('storeOrders.packProgress.statCartons')}</span>
            <b className="pack-progress-stat-value">{summary.cartonCount}</b>
            <small className="pack-progress-stat-note">
              {t('storeOrders.packProgress.cartonBreakdown', {
                packing: summary.packingCartonCount,
                sealed: summary.sealedCartonCount,
                transit: summary.transitCartonCount,
              })}
            </small>
          </div>
        </div>

        <div className="pack-progress-tables">
          <div className="pack-progress-table-block">
            <h3 className="pack-progress-subtitle">{t('storeOrders.packProgress.linesTitle')}</h3>
            <MeasuredTable<WarehousePackProgressLine>
              metricId="warehouse.store-order-detail.pack-progress-lines"
              className="pack-progress-table"
              rowKey="productCode"
              size="small"
              pagination={false}
              columns={lineColumns}
              dataSource={lines}
              rowClassName={(line) => (hasPackLineMismatch(line) ? 'pack-progress-row-warn' : '')}
              scroll={{ x: 480, y: 320 }}
            />
          </div>
          <div className="pack-progress-table-block">
            <h3 className="pack-progress-subtitle">{t('storeOrders.packProgress.cartonsTitle')}</h3>
            <MeasuredTable<WarehousePackProgressCarton>
              metricId="warehouse.store-order-detail.pack-progress-cartons"
              className="pack-progress-table"
              rowKey="cartonGuid"
              size="small"
              pagination={false}
              columns={cartonColumns}
              dataSource={progress.cartons}
              scroll={{ x: 420, y: 320 }}
            />
          </div>
        </div>
      </div>
    </section>
  )
}

/**
 * 「完成订单」确认弹窗里的装箱提醒（不阻断）：自己读取装箱进度，有未封箱或没对上账时显示一条警告；
 * 订单没用装箱、读取失败或无权限时什么都不显示，不影响原来的确认流程。
 */
export function PackCompleteReminder({ orderGuid }: { orderGuid: string }) {
  const { t } = useTranslation()
  const { progress } = usePackProgress(orderGuid, true)
  const warning = useMemo(() => resolvePackCompletionWarning(progress), [progress])
  if (!warning) {
    return null
  }

  const reasons: string[] = []
  if (warning.openCartonCount > 0) {
    reasons.push(
      warning.emptyCartonCount > 0
        ? t('storeOrders.packProgress.reminder.openCartonsWithEmpty', { count: warning.openCartonCount, empty: warning.emptyCartonCount })
        : t('storeOrders.packProgress.reminder.openCartons', { count: warning.openCartonCount }),
    )
  }
  if (warning.mismatchLineCount > 0) {
    reasons.push(t('storeOrders.packProgress.reminder.mismatchLines', { count: warning.mismatchLineCount }))
  }
  if (reasons.length === 0) {
    reasons.push(t('storeOrders.packProgress.reminder.notReconciled'))
  }

  return (
    <Alert
      className="pack-progress-reminder"
      type="warning"
      showIcon
      message={t('storeOrders.packProgress.reminder.title')}
      description={
        <>
          <ul className="pack-progress-reminder-list">
            {reasons.map((reason) => (
              <li key={reason}>{reason}</li>
            ))}
          </ul>
          <div>{t('storeOrders.packProgress.reminder.footer')}</div>
        </>
      }
    />
  )
}
