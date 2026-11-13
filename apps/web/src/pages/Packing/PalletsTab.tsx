import { PlusOutlined, ReloadOutlined } from '@ant-design/icons'
import { Button, Input, Segmented, Typography } from 'antd'
import type { ColumnsType } from 'antd/es/table'
import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'

import { MeasuredTable } from '../../components/MeasuredTable'
import { createPackPallet, getPackPallet, listPackPallets } from '../../services/packingService'
import type { PackPalletFilter, PackPalletList, PackPalletSummary } from '../../types/packing'

import { DestinationModal } from './DestinationSelect'
import type { PackDestinationValue } from './packingLogic'
import { UNASSIGNED_DESTINATION, destinationToRequest, formatPackTime, palletStateKind } from './packingLogic'
import { DestinationText, ReceivedPlace, StateTag, showPackingError, usePackingPage } from './packingShared'
import { buildPalletLabelFields, printPackLabels } from './packLabel'

const EMPTY_LIST: PackPalletList = { items: [], buildingCount: 0, storedCount: 0 }

/** 托盘列表：全部 / 组托中 / 本地存放 / 已发出，关键字筛选、新建托盘；点开详情抽屉管理箱清单、封托、发出。 */
export default function PalletsTab({ active }: { active: boolean }) {
  const { t } = useTranslation()
  const { ownerCode, openPallet, dataVersion, notifyChanged } = usePackingPage()
  const [filter, setFilter] = useState<PackPalletFilter>('all')
  const [keyword, setKeyword] = useState('')
  const [list, setList] = useState<PackPalletList>(EMPTY_LIST)
  const [loading, setLoading] = useState(false)
  const [createOpen, setCreateOpen] = useState(false)
  const requestSeqRef = useRef(0)

  const load = useCallback(() => {
    const sequence = ++requestSeqRef.current
    setLoading(true)
    listPackPallets({ ownerCode, filter, keyword })
      .then((result) => {
        if (sequence === requestSeqRef.current) {
          setList(result ?? EMPTY_LIST)
        }
      })
      .catch((error) => {
        if (sequence === requestSeqRef.current) {
          showPackingError(error)
        }
      })
      .finally(() => {
        if (sequence === requestSeqRef.current) {
          setLoading(false)
        }
      })
  }, [ownerCode, filter, keyword])

  useEffect(() => {
    if (active) {
      load()
    }
  }, [active, load, dataVersion])

  /** 列表里直接打印：先取详情（标签右下的订货单号要看托盘上的箱）。 */
  const printPallet = useCallback((palletGuid: string) => {
    getPackPallet(palletGuid)
      .then((detail) => printPackLabels(buildPalletLabelFields(detail)))
      .catch(showPackingError)
  }, [])

  const submitCreate = async (value: PackDestinationValue) => {
    try {
      const created = await createPackPallet({ ...destinationToRequest(value), ownerCode })
      setCreateOpen(false)
      notifyChanged()
      openPallet(created.palletGuid)
    } catch (error) {
      showPackingError(error)
      throw error
    }
  }

  const columns = useMemo<ColumnsType<PackPalletSummary>>(
    () => [
      {
        title: t('packingUi.columns.palletNo'),
        dataIndex: 'palletNo',
        width: 140,
        render: (value: string) => <b className="wh-packing-mono">{value}</b>,
      },
      {
        title: t('common.status'),
        dataIndex: 'status',
        width: 110,
        render: (_value: number, record) => <StateTag kind={palletStateKind(record, ownerCode)} t={t} />,
      },
      {
        title: t('packingUi.columns.destination'),
        key: 'destination',
        width: 240,
        ellipsis: true,
        render: (_value, record) =>
          palletStateKind(record, ownerCode) === 'received' ? (
            <ReceivedPlace ownerName={record.ownerName} />
          ) : (
            <DestinationText destination={record.destination} t={t} />
          ),
      },
      {
        title: t('packingUi.columns.cartons'),
        key: 'cartons',
        width: 80,
        align: 'right',
        // 在路上且已签收一部分时显示「3/5」，发出方看得到差了几箱。
        render: (_value, record) =>
          record.inTransitCartonCount > 0 && record.inTransitCartonCount < record.cartonCount
            ? `${record.cartonCount - record.inTransitCartonCount}/${record.cartonCount}`
            : record.cartonCount,
      },
      { title: t('packingUi.columns.sku'), dataIndex: 'skuCount', width: 70, align: 'right' },
      { title: t('packingUi.columns.pieces'), dataIndex: 'pieceCount', width: 80, align: 'right' },
      {
        title: t('packingUi.columns.weightKg'),
        dataIndex: 'weightKg',
        width: 90,
        align: 'right',
        render: (value: number | null | undefined) => (value === null || value === undefined ? '—' : value),
      },
      {
        title: t('packingUi.columns.location'),
        dataIndex: 'locationCode',
        width: 130,
        render: (value: string | null | undefined) => value || '—',
      },
      {
        title: t('packingUi.columns.consignmentNo'),
        dataIndex: 'consignmentNo',
        width: 160,
        ellipsis: true,
        render: (value: string | null | undefined) => (value ? <span className="wh-packing-mono">{value}</span> : '—'),
      },
      { title: t('packingUi.columns.builtBy'), dataIndex: 'createdByName', width: 100, ellipsis: true },
      {
        title: t('packingUi.columns.palletSealedAt'),
        dataIndex: 'sealedAtUtc',
        width: 110,
        render: (value: string | null | undefined) => (
          <Typography.Text type="secondary">{formatPackTime(value)}</Typography.Text>
        ),
      },
      {
        title: t('packingUi.columns.dispatchedAt'),
        dataIndex: 'dispatchedAtUtc',
        width: 110,
        render: (value: string | null | undefined) => (
          <Typography.Text type="secondary">{formatPackTime(value)}</Typography.Text>
        ),
      },
      {
        title: t('packingUi.columns.actions'),
        key: 'actions',
        width: 130,
        render: (_value, record) => (
          <>
            <Button type="link" size="small" onClick={() => openPallet(record.palletGuid)}>
              {t('common.detail')}
            </Button>
            <Button type="link" size="small" onClick={() => printPallet(record.palletGuid)}>
              {t('common.print')}
            </Button>
          </>
        ),
      },
    ],
    [openPallet, ownerCode, printPallet, t],
  )

  const cartonTotal = list.items.reduce((sum, item) => sum + item.cartonCount, 0)

  return (
    <div className="wh-packing-panel wh-packing-list">
      <div className="wh-packing-filters">
        <Segmented<PackPalletFilter>
          value={filter}
          onChange={setFilter}
          options={[
            { value: 'all', label: t('packingUi.filter.all') },
            { value: 'building', label: t('packingUi.filter.building', { count: list.buildingCount }) },
            { value: 'stored', label: t('packingUi.filter.palletStored', { count: list.storedCount }) },
            { value: 'outgoing', label: t('packingUi.filter.outgoingPlain') },
          ]}
        />
        <Input.Search
          className="wh-packing-keyword"
          allowClear
          placeholder={t('packingUi.filter.palletKeyword')}
          onSearch={(value) => setKeyword(value.trim())}
        />
        <span className="wh-packing-filters-spacer" />
        <Typography.Text type="secondary">
          {t('packingUi.filter.palletTotal', { count: list.items.length, cartons: cartonTotal })}
        </Typography.Text>
        <Button icon={<ReloadOutlined />} onClick={load} aria-label={t('common.refresh')} />
        <Button type="primary" icon={<PlusOutlined />} onClick={() => setCreateOpen(true)}>
          {t('packingUi.action.newPallet')}
        </Button>
      </div>
      <MeasuredTable<PackPalletSummary>
        metricId="packing.pallets"
        size="middle"
        rowKey="palletGuid"
        loading={loading}
        columns={columns}
        dataSource={list.items}
        pagination={{ defaultPageSize: 20, showSizeChanger: true, hideOnSinglePage: true }}
        scroll={{ x: 1400 }}
        onRow={(record) => ({ onDoubleClick: () => openPallet(record.palletGuid) })}
      />
      <DestinationModal
        open={createOpen}
        title={t('packingUi.action.newPallet')}
        initialValue={UNASSIGNED_DESTINATION}
        onCancel={() => setCreateOpen(false)}
        onSubmit={submitCreate}
      />
    </div>
  )
}
