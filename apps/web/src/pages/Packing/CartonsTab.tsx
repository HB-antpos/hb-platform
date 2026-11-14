import { ReloadOutlined } from '@ant-design/icons'
import { Button, Checkbox, Input, Segmented, Typography } from 'antd'
import type { ColumnsType } from 'antd/es/table'
import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'

import { MeasuredTable } from '../../components/MeasuredTable'
import { listPackCartons } from '../../services/packingService'
import type { PackCartonFilter, PackCartonList, PackCartonSummary } from '../../types/packing'

import { cartonStateKind, formatPackTime } from './packingLogic'
import { DestinationText, ReceivedPlace, StateTag, showPackingError, usePackingPage } from './packingShared'
import { buildCartonLabelFields, printPackLabels } from './packLabel'

const EMPTY_LIST: PackCartonList = { items: [], packingCount: 0, storedCount: 0, outgoingCount: 0, incomingCount: 0 }

/** 箱列表：全部 / 装箱中 / 本地存放 / 已发出，关键字与只看我的筛选；点开详情抽屉。 */
export default function CartonsTab({ active }: { active: boolean }) {
  const { t } = useTranslation()
  const { ownerCode, openCarton, dataVersion } = usePackingPage()
  const [filter, setFilter] = useState<PackCartonFilter>('all')
  const [keyword, setKeyword] = useState('')
  const [mine, setMine] = useState(false)
  const [list, setList] = useState<PackCartonList>(EMPTY_LIST)
  const [loading, setLoading] = useState(false)
  const requestSeqRef = useRef(0)

  const load = useCallback(() => {
    const sequence = ++requestSeqRef.current
    setLoading(true)
    listPackCartons({ ownerCode, filter, keyword, mine })
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
  }, [ownerCode, filter, keyword, mine])

  // 切到本标签、筛选变化或别处改了数据时刷新；标签不可见时不请求。
  useEffect(() => {
    if (active) {
      load()
    }
  }, [active, load, dataVersion])

  const columns = useMemo<ColumnsType<PackCartonSummary>>(
    () => [
      {
        title: t('packingUi.columns.cartonNo'),
        dataIndex: 'cartonNo',
        width: 150,
        render: (value: string) => <b className="wh-packing-mono">{value}</b>,
      },
      {
        title: t('common.status'),
        dataIndex: 'status',
        width: 110,
        render: (_value: number, record) => <StateTag kind={cartonStateKind(record, ownerCode)} t={t} />,
      },
      {
        title: t('packingUi.columns.destination'),
        key: 'destination',
        width: 240,
        ellipsis: true,
        render: (_value, record) =>
          cartonStateKind(record, ownerCode) === 'received' ? (
            <ReceivedPlace ownerName={record.ownerName} />
          ) : (
            <DestinationText destination={record.destination} t={t} />
          ),
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
        title: t('packingUi.columns.palletOrLocation'),
        key: 'pallet',
        width: 150,
        render: (_value, record) =>
          record.palletNo ? (
            <span className="wh-packing-mono">{record.palletNo}</span>
          ) : record.locationCode ? (
            record.locationCode
          ) : (
            <Typography.Text type="secondary">{t('packingUi.loose')}</Typography.Text>
          ),
      },
      { title: t('packingUi.columns.createdBy'), dataIndex: 'createdByName', width: 100, ellipsis: true },
      {
        title: t('packingUi.columns.sealedAt'),
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
    [openCarton, ownerCode, t],
  )

  return (
    <div className="wh-packing-panel wh-packing-list">
      <div className="wh-packing-filters">
        <Segmented<PackCartonFilter>
          value={filter}
          onChange={setFilter}
          options={[
            { value: 'all', label: t('packingUi.filter.all') },
            { value: 'packing', label: t('packingUi.filter.packing', { count: list.packingCount }) },
            { value: 'stored', label: t('packingUi.filter.stored', { count: list.storedCount }) },
            { value: 'outgoing', label: t('packingUi.filter.outgoing', { count: list.outgoingCount }) },
          ]}
        />
        <Input.Search
          className="wh-packing-keyword"
          allowClear
          placeholder={t('packingUi.filter.cartonKeyword')}
          onSearch={(value) => setKeyword(value.trim())}
        />
        <Checkbox checked={mine} onChange={(event) => setMine(event.target.checked)}>
          {t('packingUi.filter.mine')}
        </Checkbox>
        <span className="wh-packing-filters-spacer" />
        <Typography.Text type="secondary">{t('packingUi.filter.cartonTotal', { count: list.items.length })}</Typography.Text>
        <Button icon={<ReloadOutlined />} onClick={load} aria-label={t('common.refresh')} />
      </div>
      <MeasuredTable<PackCartonSummary>
        metricId="packing.cartons"
        size="middle"
        rowKey="cartonGuid"
        loading={loading}
        columns={columns}
        dataSource={list.items}
        pagination={{ defaultPageSize: 20, showSizeChanger: true, hideOnSinglePage: true }}
        scroll={{ x: 1250 }}
        onRow={(record) => ({ onDoubleClick: () => openCarton(record.cartonGuid) })}
      />
    </div>
  )
}
