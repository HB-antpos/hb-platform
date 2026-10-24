import { SearchOutlined } from '@ant-design/icons'
import {
  Alert,
  Button,
  Card,
  DatePicker,
  Empty,
  Input,
  Select,
  Space,
  Statistic,
  Tag,
  Typography,
} from 'antd'
import type { ColumnsType, TableProps } from 'antd/es/table'
import type { SorterResult } from 'antd/es/table/interface'
import type { Dayjs } from 'dayjs'
import { useEffect, useMemo, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { useNavigate } from 'react-router-dom'
import { ContainerNumberRevealButton, useContainerNumberDisplay } from '../../../components/ContainerNumber'
import { queryWarehouseProductContainers } from '../../../services/warehouseProductRecordsService'
import type {
  WarehouseProductContainerItem,
  WarehouseProductContainerReport,
  WarehouseProductRecordSortDirection,
} from '../../../types/warehouseProductRecords'
import { createLatestRequestGuard } from '../../../utils/latestRequestGuard'
import {
  CONTAINER_STATUS_VALUES,
  formatAustralianCurrency,
  formatChineseCurrency,
  formatQuantity,
  getContainerDetailPath,
  getDefaultContainerStatuses,
  isAbortError,
  mapContainerTableChangeToQuery,
} from './logic'
import { MeasuredTable } from '../../../components/MeasuredTable'

const { RangePicker } = DatePicker

interface ContainersPanelProps {
  productCode: string
  enabled: boolean
}

function getStatusColor(status: number | null): string {
  if (status == null) return 'default'
  if (status === 7) return 'red'
  if (status === 4 || status === 5 || status === 6) return 'green'
  if (status === 3) return 'blue'
  return 'default'
}

export default function ContainersPanel({ productCode, enabled }: ContainersPanelProps) {
  const { t } = useTranslation()
  const { format: formatContainerNumber } = useContainerNumberDisplay()
  const navigate = useNavigate()
  const [keyword, setKeyword] = useState('')
  const [arrivalRange, setArrivalRange] = useState<[Dayjs, Dayjs] | null>(null)
  const [statuses, setStatuses] = useState<number[]>(getDefaultContainerStatuses())
  const [committed, setCommitted] = useState({
    keyword: '',
    arrivalStartDate: undefined as string | undefined,
    arrivalEndDate: undefined as string | undefined,
    statuses: getDefaultContainerStatuses(),
  })
  const [pageNumber, setPageNumber] = useState(1)
  const [pageSize, setPageSize] = useState(20)
  const [sortBy, setSortBy] = useState('effectiveArrivalDate')
  const [sortDirection, setSortDirection] = useState<WarehouseProductRecordSortDirection>('desc')
  const [report, setReport] = useState<WarehouseProductContainerReport | null>(null)
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<unknown>(null)
  const abortRef = useRef<AbortController>()
  const guardRef = useRef(createLatestRequestGuard())

  const commitQuery = () => {
    setCommitted({
      keyword: keyword.trim(),
      arrivalStartDate: arrivalRange?.[0]?.format('YYYY-MM-DD'),
      arrivalEndDate: arrivalRange?.[1]?.format('YYYY-MM-DD'),
      statuses: [...statuses],
    })
    setPageNumber(1)
  }

  const resetQuery = () => {
    setKeyword('')
    setArrivalRange(null)
    setStatuses(getDefaultContainerStatuses())
    setCommitted({
      keyword: '',
      arrivalStartDate: undefined,
      arrivalEndDate: undefined,
      statuses: getDefaultContainerStatuses(),
    })
    setPageNumber(1)
    setSortBy('effectiveArrivalDate')
    setSortDirection('desc')
  }

  useEffect(() => {
    abortRef.current?.abort()
    guardRef.current.invalidate()
    if (!enabled) {
      setLoading(false)
      return
    }

    const controller = new AbortController()
    abortRef.current = controller
    const requestId = guardRef.current.begin()
    setReport(null)
    setLoading(true)
    setError(null)

    queryWarehouseProductContainers(
      productCode,
      {
        containerKeyword: committed.keyword || undefined,
        arrivalStartDate: committed.arrivalStartDate,
        arrivalEndDate: committed.arrivalEndDate,
        statuses: committed.statuses.length ? committed.statuses : undefined,
        pageNumber,
        pageSize,
        sortBy,
        sortDirection,
      },
      controller.signal,
    )
      .then((result) => {
        if (!guardRef.current.isLatest(requestId)) return
        setReport(result)
      })
      .catch((nextError) => {
        if (!guardRef.current.isLatest(requestId) || isAbortError(nextError)) return
        setReport(null)
        setError(nextError)
      })
      .finally(() => {
        if (guardRef.current.isLatest(requestId)) setLoading(false)
      })

    return () => {
      controller.abort()
      guardRef.current.invalidate()
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [enabled, productCode, committed, pageNumber, pageSize, sortBy, sortDirection])

  const handleTableChange: TableProps<WarehouseProductContainerItem>['onChange'] = (
    pagination,
    _filters,
    sorter,
  ) => {
    const activeSorter = (Array.isArray(sorter) ? sorter[0] : sorter) as SorterResult<WarehouseProductContainerItem>
    const next = mapContainerTableChangeToQuery(pagination, {
      field: typeof activeSorter?.field === 'string' ? activeSorter.field : undefined,
      order: activeSorter?.order,
    })
    setPageNumber(next.pageNumber)
    setPageSize(next.pageSize)
    setSortBy(next.sortBy)
    setSortDirection(next.sortDirection)
  }

  const columns: ColumnsType<WarehouseProductContainerItem> = [
    {
      title: t('warehouseProductRecords.detailCode'),
      dataIndex: 'detailCode',
      key: 'detailCode',
      width: 140,
      fixed: 'left',
      render: (value: string) => <Typography.Text>{value}</Typography.Text>,
    },
    {
      title: (
        <span style={{ display: 'inline-flex', alignItems: 'center', gap: 2 }}>
          {t('warehouseProductRecords.containerNumber')}
          <ContainerNumberRevealButton />
        </span>
      ),
      dataIndex: 'containerNumber',
      key: 'containerNumber',
      width: 140,
      sorter: true,
      sortOrder: sortBy === 'containerNumber' ? (sortDirection === 'asc' ? 'ascend' : 'descend') : null,
      render: (value: string | null, record) => (
        value
          ? <Typography.Link onClick={() => navigate(getContainerDetailPath(record.containerCode))}>{formatContainerNumber(value)}</Typography.Link>
          : '-'
      ),
    },
    {
      title: t('warehouseProductRecords.containerCode'),
      dataIndex: 'containerCode',
      key: 'containerCode',
      width: 140,
      render: (value: string) => <Typography.Text>{value}</Typography.Text>,
    },
    {
      title: t('warehouseProductRecords.loadingDate'),
      dataIndex: 'loadingDate',
      key: 'loadingDate',
      width: 120,
      sorter: true,
      sortOrder: sortBy === 'loadingDate' ? (sortDirection === 'asc' ? 'ascend' : 'descend') : null,
      render: (value: string | null) => value || '-',
    },
    {
      title: t('warehouseProductRecords.estimatedArrivalDate'),
      dataIndex: 'estimatedArrivalDate',
      key: 'estimatedArrivalDate',
      width: 120,
      render: (value: string | null) => value || '-',
    },
    {
      title: t('warehouseProductRecords.actualArrivalDate'),
      dataIndex: 'actualArrivalDate',
      key: 'actualArrivalDate',
      width: 120,
      render: (value: string | null) => value || '-',
    },
    {
      title: t('warehouseProductRecords.effectiveArrivalDate'),
      dataIndex: 'effectiveArrivalDate',
      key: 'effectiveArrivalDate',
      width: 120,
      sorter: true,
      sortOrder: sortBy === 'effectiveArrivalDate' ? (sortDirection === 'asc' ? 'ascend' : 'descend') : null,
      render: (value: string | null) => value || '-',
    },
    {
      title: t('warehouseProductRecords.status'),
      dataIndex: 'status',
      key: 'status',
      width: 100,
      sorter: true,
      sortOrder: sortBy === 'status' ? (sortDirection === 'asc' ? 'ascend' : 'descend') : null,
      render: (value: number | null) => (
        <Tag color={getStatusColor(value)}>
          {value == null
            ? t('warehouseProductRecords.statusUnknown')
            : t(`warehouseProductRecords.status${value}`)}
        </Tag>
      ),
    },
    {
      title: t('warehouseProductRecords.loadingPieces'),
      dataIndex: 'loadingPieces',
      key: 'loadingPieces',
      width: 100,
      align: 'right',
      render: formatQuantity,
    },
    {
      title: t('warehouseProductRecords.loadingQuantity'),
      dataIndex: 'loadingQuantity',
      key: 'loadingQuantity',
      width: 110,
      align: 'right',
      sorter: true,
      sortOrder: sortBy === 'loadingQuantity' ? (sortDirection === 'asc' ? 'ascend' : 'descend') : null,
      render: formatQuantity,
    },
    {
      title: t('warehouseProductRecords.domesticPrice'),
      dataIndex: 'domesticPrice',
      key: 'domesticPrice',
      width: 110,
      align: 'right',
      render: formatChineseCurrency,
    },
    {
      title: t('warehouseProductRecords.importPrice'),
      dataIndex: 'importPrice',
      key: 'importPrice',
      width: 110,
      align: 'right',
      render: formatAustralianCurrency,
    },
    {
      title: t('warehouseProductRecords.totalAmount'),
      dataIndex: 'totalAmount',
      key: 'totalAmount',
      width: 120,
      align: 'right',
      render: formatChineseCurrency,
    },
  ]

  const statusOptions = useMemo(
    () => CONTAINER_STATUS_VALUES.map((value) => ({
      label: t(`warehouseProductRecords.status${value}`),
      value,
    })),
    [t],
  )

  if (!enabled) {
    return null
  }

  return (
    <Space direction="vertical" size={12} style={{ width: '100%' }}>
      <Card size="small">
        <Space wrap>
          <Typography.Text>{t('warehouseProductRecords.containerKeyword')}:</Typography.Text>
          <Input
            allowClear
            value={keyword}
            prefix={<SearchOutlined />}
            placeholder={t('warehouseProductRecords.containerKeywordPlaceholder')}
            style={{ width: 220 }}
            onChange={(event) => setKeyword(event.target.value)}
            onPressEnter={commitQuery}
          />
          <Typography.Text>{t('warehouseProductRecords.arrivalRange')}:</Typography.Text>
          <RangePicker
            allowClear
            value={arrivalRange}
            onChange={(value) => setArrivalRange(value as [Dayjs, Dayjs] | null)}
          />
          <Typography.Text>{t('warehouseProductRecords.status')}:</Typography.Text>
          <Select
            mode="multiple"
            allowClear
            style={{ minWidth: 220 }}
            value={statuses}
            options={statusOptions}
            placeholder={t('warehouseProductRecords.nonCancelledStatuses')}
            onChange={(value) => setStatuses(value as number[])}
          />
          <Button type="primary" icon={<SearchOutlined />} onClick={commitQuery}>
            {t('common.query')}
          </Button>
          <Button onClick={resetQuery}>{t('common.reset')}</Button>
        </Space>
      </Card>

      {error ? (
        <Alert
          type="error"
          showIcon
          message={t('warehouseProductRecords.loadFailed')}
          description={error instanceof Error ? error.message : undefined}
        />
      ) : null}

      <Card size="small">
        <Space size={32} wrap style={{ marginBottom: 12 }}>
          <Statistic title={t('warehouseProductRecords.containerCount')} value={report?.summary.containerCount ?? 0} />
          <Statistic title={t('warehouseProductRecords.loadingPieces')} value={report?.summary.loadingPieces ?? 0} />
          <Statistic title={t('warehouseProductRecords.loadingQuantity')} value={report?.summary.loadingQuantity ?? 0} />
          <Statistic title={t('warehouseProductRecords.totalAmount')} value={report?.summary.totalAmount ?? 0} precision={2} prefix="¥" />
        </Space>

        {report && report.items.length === 0 && !loading ? (
          <Empty description={t('warehouseProductRecords.noData')} />
        ) : (
          <MeasuredTable metricId="warehouse.product-records.containers-panel.table-1"
            rowKey="detailCode"
            size="small"
            loading={loading}
            columns={columns}
            dataSource={report?.items ?? []}
            scroll={{ x: 1450 }}
            pagination={{
              current: pageNumber,
              pageSize,
              total: report?.totalCount ?? 0,
              showSizeChanger: true,
              showTotal: (total) => t('common.total', { count: total }),
            }}
            onChange={handleTableChange}
            locale={{ emptyText: t('warehouseProductRecords.noData') }}
          />
        )}
      </Card>
    </Space>
  )
}
