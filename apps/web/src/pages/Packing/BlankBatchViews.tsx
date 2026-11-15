import { ArrowLeftOutlined, PrinterOutlined, ReloadOutlined } from '@ant-design/icons'
import { Button, Space, Spin, Tag, Typography } from 'antd'
import type { ColumnsType } from 'antd/es/table'
import { useCallback, useEffect, useMemo, useState } from 'react'
import { useTranslation } from 'react-i18next'

import { MeasuredTable } from '../../components/MeasuredTable'
import { getPackBatch, listPackBatches } from '../../services/packingService'
import type { PackBlankBatch, PackBlankBatchDetail, PackCartonSummary } from '../../types/packing'

import { blankPrintState, filterReprintable, filterUnprinted, isUnactivatedBlank, isVoidedCarton, shortBatchCode } from './blankCartons'
import { formatPackTime } from './packingLogic'
import { showPackingError } from './packingShared'

const BATCH_LIST_LIMIT = 30

/** 打印状态：还没打印（橙色）/ 已打印 N 次 + 最近一次时间。 */
export function BlankPrintText({ carton }: { carton: PackCartonSummary }) {
  const { t } = useTranslation()
  const state = blankPrintState(carton)
  if (state.kind === 'unprinted') {
    return <Typography.Text className="wh-packing-over">{t('packingUi.blank.notPrinted')}</Typography.Text>
  }
  return (
    <Typography.Text type="secondary">
      {t('packingUi.blank.printedTimes', { count: state.count })}
      {carton.labelPrintedAtUtc ? ` · ${formatPackTime(carton.labelPrintedAtUtc)}` : ''}
    </Typography.Text>
  )
}

interface BatchListProps {
  ownerCode: string
  /** 外部有改动（创建 / 打印 / 作废）时自增，列表据此刷新。 */
  refreshKey: number
  onOpen: (batchGuid: string) => void
}

/** 最近批次：时间、数量、已打印 / 未启用 / 作废计数，点进某批看每张状态。 */
export function BlankBatchList({ ownerCode, refreshKey, onOpen }: BatchListProps) {
  const { t } = useTranslation()
  const [batches, setBatches] = useState<PackBlankBatch[]>([])
  const [loading, setLoading] = useState(false)

  const load = useCallback(() => {
    setLoading(true)
    listPackBatches({ ownerCode, limit: BATCH_LIST_LIMIT })
      .then(setBatches)
      .catch(showPackingError)
      .finally(() => setLoading(false))
  }, [ownerCode])

  useEffect(() => {
    load()
  }, [load, refreshKey])

  const columns = useMemo<ColumnsType<PackBlankBatch>>(
    () => [
      {
        title: t('packingUi.blank.batches.createdAt'),
        dataIndex: 'createdAtUtc',
        width: 110,
        render: (value: string) => formatPackTime(value),
      },
      { title: t('packingUi.blank.batches.count'), dataIndex: 'count', width: 60, align: 'right' },
      {
        title: t('packingUi.blank.batches.range'),
        key: 'range',
        render: (_value, record) => (
          <span className="wh-packing-mono">
            {record.firstCartonNo} ~ {record.lastCartonNo}
          </span>
        ),
      },
      {
        title: t('packingUi.blank.batches.printed'),
        key: 'printed',
        width: 90,
        align: 'right',
        render: (_value, record) => (
          <span className={record.printedCount < record.count ? 'wh-packing-over' : undefined}>
            {record.printedCount} / {record.count}
          </span>
        ),
      },
      { title: t('packingUi.blank.batches.unactivated'), dataIndex: 'unactivatedCount', width: 80, align: 'right' },
      { title: t('packingUi.blank.batches.voided'), dataIndex: 'voidedCount', width: 60, align: 'right' },
      {
        title: '',
        key: 'actions',
        width: 60,
        render: (_value, record) => (
          <Button type="link" size="small" onClick={() => onOpen(record.batchGuid)}>
            {t('packingUi.blank.batches.open')}
          </Button>
        ),
      },
    ],
    [onOpen, t],
  )

  return (
    <div className="wh-packing-blank-batches">
      <div className="wh-packing-blank-batches-head">
        <Typography.Text type="secondary">{t('packingUi.blank.view.batches')}</Typography.Text>
        <Button size="small" icon={<ReloadOutlined />} onClick={load} aria-label={t('common.refresh')} />
      </div>
      <MeasuredTable<PackBlankBatch>
        metricId="packing.blankBatches"
        size="small"
        rowKey="batchGuid"
        loading={loading}
        columns={columns}
        dataSource={batches}
        pagination={false}
        locale={{ emptyText: t('packingUi.blank.batches.empty') }}
        scroll={{ x: 'max-content' }}
      />
    </div>
  )
}

interface BatchDetailProps {
  batchGuid: string
  refreshKey: number
  onBack: () => void
  /** 打印这些箱（交给外层的打印弹窗）。 */
  onPrint: (cartons: PackCartonSummary[]) => void
}

/** 批次详情：每张的状态与打印情况；「补打未打印的」「全部重打」。 */
export function BlankBatchDetail({ batchGuid, refreshKey, onBack, onPrint }: BatchDetailProps) {
  const { t } = useTranslation()
  const [detail, setDetail] = useState<PackBlankBatchDetail | null>(null)
  const [loading, setLoading] = useState(true)

  const load = useCallback(() => {
    setLoading(true)
    getPackBatch(batchGuid)
      .then(setDetail)
      .catch((error) => {
        showPackingError(error)
      })
      .finally(() => setLoading(false))
  }, [batchGuid])

  useEffect(() => {
    load()
  }, [load, refreshKey])

  const cartons = useMemo(() => detail?.cartons ?? [], [detail])
  const unprinted = useMemo(() => filterUnprinted(cartons), [cartons])
  const reprintable = useMemo(() => filterReprintable(cartons), [cartons])

  const columns = useMemo<ColumnsType<PackCartonSummary>>(
    () => [
      {
        title: t('packingUi.blank.batch.seq'),
        key: 'seq',
        width: 60,
        align: 'right',
        render: (_value, record, index) => record.batchSeq ?? index + 1,
      },
      {
        title: t('packingUi.columns.cartonNo'),
        dataIndex: 'cartonNo',
        width: 140,
        render: (value: string) => <b className="wh-packing-mono">{value}</b>,
      },
      {
        title: t('packingUi.blank.batch.state'),
        key: 'state',
        width: 90,
        render: (_value, record) =>
          isVoidedCarton(record) ? (
            <Tag>{t('packingUi.blank.voided')}</Tag>
          ) : isUnactivatedBlank(record) ? (
            <Tag>{t('packingUi.blank.unactivated')}</Tag>
          ) : (
            <Tag color="green">{t('packingUi.blank.activated')}</Tag>
          ),
      },
      {
        title: t('packingUi.blank.columns.label'),
        key: 'print',
        render: (_value, record) => <BlankPrintText carton={record} />,
      },
      {
        title: '',
        key: 'actions',
        width: 70,
        render: (_value, record) =>
          isUnactivatedBlank(record) ? (
            <Button type="link" size="small" onClick={() => onPrint([record])}>
              {t('packingUi.blank.batch.rowPrint')}
            </Button>
          ) : null,
      },
    ],
    [onPrint, t],
  )

  const batch = detail?.batch
  return (
    <div className="wh-packing-blank-batch-detail">
      <div className="wh-packing-blank-batches-head">
        <Button type="link" size="small" icon={<ArrowLeftOutlined />} onClick={onBack}>
          {t('packingUi.blank.batch.back')}
        </Button>
        <Button size="small" icon={<ReloadOutlined />} onClick={load} aria-label={t('common.refresh')} />
      </div>
      {batch ? (
        <>
          <Typography.Text strong>
            {t('packingUi.blank.batch.summary', {
              count: batch.count,
              first: batch.firstCartonNo,
              last: batch.lastCartonNo,
              creator: batch.createdByName,
            })}
          </Typography.Text>
          <Typography.Text type="secondary">
            {formatPackTime(batch.createdAtUtc)} · {shortBatchCode(batch.batchGuid)} ·{' '}
            {t('packingUi.blank.batches.printed')} {batch.printedCount} / {t('packingUi.blank.batches.unactivated')} {batch.unactivatedCount} /{' '}
            {t('packingUi.blank.batches.voided')} {batch.voidedCount}
          </Typography.Text>
          <Space wrap>
            <Button icon={<PrinterOutlined />} disabled={unprinted.length === 0} onClick={() => onPrint(unprinted)}>
              {t('packingUi.blank.batch.printUnprinted', { count: unprinted.length })}
            </Button>
            <Button icon={<PrinterOutlined />} disabled={reprintable.length === 0} onClick={() => onPrint(reprintable)}>
              {t('packingUi.blank.batch.reprintAll', { count: reprintable.length })}
            </Button>
          </Space>
        </>
      ) : loading ? (
        <Spin />
      ) : null}
      <MeasuredTable<PackCartonSummary>
        metricId="packing.blankBatchCartons"
        size="small"
        rowKey="cartonGuid"
        loading={loading}
        columns={columns}
        dataSource={cartons}
        pagination={{ pageSize: 50, hideOnSinglePage: true }}
        scroll={{ x: 'max-content' }}
      />
    </div>
  )
}
