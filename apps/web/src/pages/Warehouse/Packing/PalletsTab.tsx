import { PlusOutlined, ReloadOutlined } from '@ant-design/icons'
import { Button, Input, Segmented, Typography } from 'antd'
import type { ColumnsType } from 'antd/es/table'
import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'

import { MeasuredTable } from '../../../components/MeasuredTable'
import { createPackPallet, getPackPallet, listPackPallets } from '../../../services/warehousePackingService'
import type { PackPalletFilter, PackPalletList, PackPalletSummary } from '../../../types/warehousePacking'

import { DestinationModal } from './DestinationSelect'
import type { PackDestinationValue } from './packingLogic'
import { UNASSIGNED_DESTINATION, destinationToRequest, formatPackTime } from './packingLogic'
import { DestinationText, PalletStatusTag, showPackingError, usePackingPage } from './packingShared'
import { buildPalletLabelFields, printPackLabels } from './packLabel'

const EMPTY_LIST: PackPalletList = { items: [], buildingCount: 0, sealedCount: 0 }

/** 托盘列表：状态 / 关键字筛选、新建托盘；点开详情抽屉管理箱清单和封托。 */
export default function PalletsTab({ active }: { active: boolean }) {
  const { t } = useTranslation()
  const { openPallet, dataVersion, notifyChanged } = usePackingPage()
  const [filter, setFilter] = useState<PackPalletFilter>('all')
  const [keyword, setKeyword] = useState('')
  const [list, setList] = useState<PackPalletList>(EMPTY_LIST)
  const [loading, setLoading] = useState(false)
  const [createOpen, setCreateOpen] = useState(false)
  const requestSeqRef = useRef(0)

  const load = useCallback(() => {
    const sequence = ++requestSeqRef.current
    setLoading(true)
    listPackPallets({ filter, keyword })
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
  }, [filter, keyword])

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
      const created = await createPackPallet(destinationToRequest(value))
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
        title: t('warehouseUi.packing.columns.palletNo'),
        dataIndex: 'palletNo',
        width: 140,
        render: (value: string) => <b className="wh-packing-mono">{value}</b>,
      },
      {
        title: t('common.status'),
        dataIndex: 'status',
        width: 90,
        render: (value: number) => <PalletStatusTag status={value} t={t} />,
      },
      {
        title: t('warehouseUi.packing.columns.destination'),
        key: 'destination',
        ellipsis: true,
        render: (_value, record) => <DestinationText destination={record.destination} t={t} />,
      },
      { title: t('warehouseUi.packing.columns.cartons'), dataIndex: 'cartonCount', width: 60, align: 'right' },
      { title: t('warehouseUi.packing.columns.sku'), dataIndex: 'skuCount', width: 70, align: 'right' },
      { title: t('warehouseUi.packing.columns.pieces'), dataIndex: 'pieceCount', width: 80, align: 'right' },
      {
        title: t('warehouseUi.packing.columns.weightKg'),
        dataIndex: 'weightKg',
        width: 90,
        align: 'right',
        render: (value: number | null | undefined) => (value === null || value === undefined ? '—' : value),
      },
      {
        title: t('warehouseUi.packing.columns.location'),
        dataIndex: 'locationCode',
        width: 130,
        render: (value: string | null | undefined) => value || '—',
      },
      {
        title: t('warehouseUi.packing.columns.consignmentNo'),
        dataIndex: 'consignmentNo',
        width: 160,
        ellipsis: true,
        render: (value: string | null | undefined) => (value ? <span className="wh-packing-mono">{value}</span> : '—'),
      },
      { title: t('warehouseUi.packing.columns.builtBy'), dataIndex: 'createdByName', width: 100, ellipsis: true },
      {
        title: t('warehouseUi.packing.columns.palletSealedAt'),
        dataIndex: 'sealedAtUtc',
        width: 110,
        render: (value: string | null | undefined) => (
          <Typography.Text type="secondary">{formatPackTime(value)}</Typography.Text>
        ),
      },
      {
        title: t('warehouseUi.packing.columns.actions'),
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
    [openPallet, printPallet, t],
  )

  const cartonTotal = list.items.reduce((sum, item) => sum + item.cartonCount, 0)

  return (
    <div className="wh-packing-panel wh-packing-list">
      <div className="wh-packing-filters">
        <Segmented<PackPalletFilter>
          value={filter}
          onChange={setFilter}
          options={[
            { value: 'all', label: t('warehouseUi.packing.filter.all') },
            { value: 'building', label: t('warehouseUi.packing.filter.building', { count: list.buildingCount }) },
            { value: 'sealed', label: t('warehouseUi.packing.filter.palletSealed', { count: list.sealedCount }) },
          ]}
        />
        <Input.Search
          className="wh-packing-keyword"
          allowClear
          placeholder={t('warehouseUi.packing.filter.palletKeyword')}
          onSearch={(value) => setKeyword(value.trim())}
        />
        <span className="wh-packing-filters-spacer" />
        <Typography.Text type="secondary">
          {t('warehouseUi.packing.filter.palletTotal', { count: list.items.length, cartons: cartonTotal })}
        </Typography.Text>
        <Button icon={<ReloadOutlined />} onClick={load} aria-label={t('common.refresh')} />
        <Button type="primary" icon={<PlusOutlined />} onClick={() => setCreateOpen(true)}>
          {t('warehouseUi.packing.action.newPallet')}
        </Button>
      </div>
      <MeasuredTable<PackPalletSummary>
        metricId="warehouse.packing.pallets"
        size="middle"
        rowKey="palletGuid"
        loading={loading}
        columns={columns}
        dataSource={list.items}
        pagination={{ defaultPageSize: 20, showSizeChanger: true, hideOnSinglePage: true }}
        scroll={{ x: 1300 }}
        onRow={(record) => ({ onDoubleClick: () => openPallet(record.palletGuid) })}
      />
      <DestinationModal
        open={createOpen}
        title={t('warehouseUi.packing.action.newPallet')}
        initialValue={UNASSIGNED_DESTINATION}
        onCancel={() => setCreateOpen(false)}
        onSubmit={submitCreate}
      />
    </div>
  )
}
