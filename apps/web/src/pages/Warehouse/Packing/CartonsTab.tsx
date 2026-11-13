import { ReloadOutlined } from '@ant-design/icons'
import { Button, Checkbox, Input, Segmented, Typography } from 'antd'
import type { ColumnsType } from 'antd/es/table'
import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'

import { MeasuredTable } from '../../../components/MeasuredTable'
import { listPackCartons } from '../../../services/warehousePackingService'
import type { PackCartonFilter, PackCartonList, PackCartonSummary } from '../../../types/warehousePacking'

import { formatPackTime } from './packingLogic'
import { CartonStatusTag, DestinationText, showPackingError, usePackingPage } from './packingShared'
import { buildCartonLabelFields, printPackLabels } from './packLabel'

const EMPTY_LIST: PackCartonList = { items: [], packingCount: 0, sealedCount: 0, looseSealedCount: 0 }

/** 箱列表：状态 / 关键字 / 只看我的筛选；点开详情抽屉做改去向、补打、重新开箱、作废。 */
export default function CartonsTab({ active }: { active: boolean }) {
  const { t } = useTranslation()
  const { openCarton, dataVersion } = usePackingPage()
  const [filter, setFilter] = useState<PackCartonFilter>('all')
  const [keyword, setKeyword] = useState('')
  const [mine, setMine] = useState(false)
  const [list, setList] = useState<PackCartonList>(EMPTY_LIST)
  const [loading, setLoading] = useState(false)
  const requestSeqRef = useRef(0)

  const load = useCallback(() => {
    const sequence = ++requestSeqRef.current
    setLoading(true)
    listPackCartons({ filter, keyword, mine })
      .then((result) => {
        // 只采用最后一次查询的结果，避免慢请求覆盖新筛选。
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
  }, [filter, keyword, mine])

  // 切到本标签、筛选变化或别处改了数据时刷新；标签不可见时不请求。
  useEffect(() => {
    if (active) {
      load()
    }
  }, [active, load, dataVersion])

  const columns = useMemo<ColumnsType<PackCartonSummary>>(
    () => [
      {
        title: t('warehouseUi.packing.columns.cartonNo'),
        dataIndex: 'cartonNo',
        width: 150,
        render: (value: string) => <b className="wh-packing-mono">{value}</b>,
      },
      {
        title: t('common.status'),
        dataIndex: 'status',
        width: 90,
        render: (value: number) => <CartonStatusTag status={value} t={t} />,
      },
      {
        title: t('warehouseUi.packing.columns.destination'),
        key: 'destination',
        ellipsis: true,
        render: (_value, record) => <DestinationText destination={record.destination} t={t} />,
      },
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
        title: t('warehouseUi.packing.columns.palletOrLocation'),
        key: 'pallet',
        width: 150,
        render: (_value, record) =>
          record.palletNo ? (
            <span className="wh-packing-mono">{record.palletNo}</span>
          ) : record.locationCode ? (
            record.locationCode
          ) : (
            <Typography.Text type="secondary">{t('warehouseUi.packing.loose')}</Typography.Text>
          ),
      },
      {
        title: t('warehouseUi.packing.columns.order'),
        dataIndex: 'orderNo',
        width: 130,
        render: (value: string | null | undefined) => (value ? <span className="wh-packing-mono">{value}</span> : '—'),
      },
      { title: t('warehouseUi.packing.columns.createdBy'), dataIndex: 'createdByName', width: 100, ellipsis: true },
      {
        title: t('warehouseUi.packing.columns.sealedAt'),
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
            <Button type="link" size="small" onClick={() => openCarton(record.cartonGuid)}>
              {t('common.detail')}
            </Button>
            <Button type="link" size="small" onClick={() => printPackLabels([buildCartonLabelFields(record)])}>
              {t('common.print')}
            </Button>
          </>
        ),
      },
    ],
    [openCarton, t],
  )

  return (
    <div className="wh-packing-panel wh-packing-list">
      <div className="wh-packing-filters">
        <Segmented<PackCartonFilter>
          value={filter}
          onChange={setFilter}
          options={[
            { value: 'all', label: t('warehouseUi.packing.filter.all') },
            { value: 'packing', label: t('warehouseUi.packing.filter.packing', { count: list.packingCount }) },
            { value: 'sealed', label: t('warehouseUi.packing.filter.sealed', { count: list.sealedCount }) },
            { value: 'loose', label: t('warehouseUi.packing.filter.loose', { count: list.looseSealedCount }) },
          ]}
        />
        <Input.Search
          className="wh-packing-keyword"
          allowClear
          placeholder={t('warehouseUi.packing.filter.cartonKeyword')}
          onSearch={(value) => setKeyword(value.trim())}
        />
        <Checkbox checked={mine} onChange={(event) => setMine(event.target.checked)}>
          {t('warehouseUi.packing.filter.mine')}
        </Checkbox>
        <span className="wh-packing-filters-spacer" />
        <Typography.Text type="secondary">
          {t('warehouseUi.packing.filter.cartonTotal', { count: list.items.length })}
        </Typography.Text>
        <Button icon={<ReloadOutlined />} onClick={load} aria-label={t('common.refresh')} />
      </div>
      <MeasuredTable<PackCartonSummary>
        metricId="warehouse.packing.cartons"
        size="middle"
        rowKey="cartonGuid"
        loading={loading}
        columns={columns}
        dataSource={list.items}
        pagination={{ defaultPageSize: 20, showSizeChanger: true, hideOnSinglePage: true }}
        scroll={{ x: 1200 }}
        onRow={(record) => ({ onDoubleClick: () => openCarton(record.cartonGuid) })}
      />
    </div>
  )
}
