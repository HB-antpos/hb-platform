import { ArrowLeftOutlined, SearchOutlined } from '@ant-design/icons'
import {
  Alert,
  Button,
  Card,
  DatePicker,
  Drawer,
  Empty,
  Image,
  Input,
  Result,
  Skeleton,
  Space,
  Tag,
  Typography,
} from 'antd'
import type { TablePaginationConfig } from 'antd/es/table'
import type { ColumnsType, TableProps } from 'antd/es/table'
import type { SorterResult } from 'antd/es/table/interface'
import dayjs from 'dayjs'
import { useKeepAliveContext } from 'keepalive-for-react'
import { useEffect, useRef, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { ContainerNumberRevealButton, useContainerNumberDisplay } from '../../../components/ContainerNumber'
import PageContainer from '../../../components/PageContainer'
import { useStableRouteContext } from '../../../hooks/useStableRouteContext'
import {
  queryContainerAllocationSales,
  queryContainerAllocationSalesBranches,
} from '../../../services/containerAllocationSalesService'
import type {
  ContainerAllocationSalesBranch,
  ContainerAllocationSalesBranchesReport,
  ContainerAllocationSalesProduct,
  ContainerAllocationSalesQuery,
  ContainerAllocationSalesReport,
  ContainerAllocationSalesSortDirection,
} from '../../../types/containerAllocationSales'
import { RequestError } from '../../../utils/request'
import {
  buildBranchQuery,
  buildContainerAllocationSalesQuery,
  buildQuickRangeQuery,
  compareContainerAllocationSalesBranches,
  formatAustralianCurrency,
  formatStatisticMessageAmounts,
  getContainerAllocationSalesViewState,
  getGrossMarginDisplay,
  getPaginatedRowNumber,
  isCustomEndDateDisabled,
  mapTableChangeToQuery,
  shouldTriggerTableRowClick,
  shouldLoadContainerAllocationSales,
} from './logic'
import { createLatestRequestGuard } from './requestGuard'
import { MeasuredTable } from '../../../components/MeasuredTable'

const WEEK_OPTIONS = [1, 2, 4, 8, 12]
const PRODUCT_IMAGE_FALLBACK = `data:image/svg+xml;charset=UTF-8,${encodeURIComponent(
  '<svg xmlns="http://www.w3.org/2000/svg" width="40" height="40"><rect width="40" height="40" rx="4" fill="#f5f5f5"/><text x="20" y="24" text-anchor="middle" font-size="10" fill="#999">无图</text></svg>',
)}`

function formatQuantity(value: number | null | undefined) {
  if (value == null) return '-'
  return value.toLocaleString('zh-CN', { maximumFractionDigits: 2 })
}

function salesValue(value: number | null) {
  return value == null ? '-' : formatQuantity(value)
}

export default function ContainerAllocationSalesPage() {
  const navigate = useNavigate()
  // 货柜编号默认只显示后 4 位；有权限的账号点标题旁眼睛按钮显示完整编号。
  const { format: formatContainerNumber } = useContainerNumberDisplay()
  const route = useStableRouteContext()
  const { active } = useKeepAliveContext()
  const containerGuid = route?.params.containerGuid || ''
  const loadedContainerGuidRef = useRef<string | null>(null)
  const mainRequestGuardRef = useRef(createLatestRequestGuard())
  const branchRequestGuardRef = useRef(createLatestRequestGuard())
  const [report, setReport] = useState<ContainerAllocationSalesReport | null>(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<unknown>(null)
  const [search, setSearch] = useState('')
  const [appliedSearch, setAppliedSearch] = useState('')
  const [pageNumber, setPageNumber] = useState(1)
  const [pageSize, setPageSize] = useState(20)
  const [sortBy, setSortBy] = useState('productCode')
  const [sortDirection, setSortDirection] = useState<ContainerAllocationSalesSortDirection>('asc')
  const [startDate, setStartDate] = useState<string>()
  const [endDate, setEndDate] = useState<string>()
  const [selectedWeeks, setSelectedWeeks] = useState<number | null>(4)
  const [drawerOpen, setDrawerOpen] = useState(false)
  const [selectedProduct, setSelectedProduct] = useState<ContainerAllocationSalesProduct | null>(null)
  const [branchReport, setBranchReport] = useState<ContainerAllocationSalesBranchesReport | null>(null)
  const [branchLoading, setBranchLoading] = useState(false)
  const [branchError, setBranchError] = useState<string>()

  const loadReport = async (
    overrides: ContainerAllocationSalesQuery = {},
    commit: { selectedWeeks?: number | null } = {},
  ) => {
    if (!active || !containerGuid) return
    const requestId = mainRequestGuardRef.current.begin()
    // 主查询口径变化后旧分店数据不再有效，同时让在途分店请求失效。
    branchRequestGuardRef.current.invalidate()
    setDrawerOpen(false)
    setBranchLoading(false)
    setLoading(true)
    setError(null)
    const query = buildContainerAllocationSalesQuery({
      startDate,
      endDate,
      search: appliedSearch.trim() || undefined,
      pageNumber,
      pageSize,
      sortBy,
      sortDirection,
    }, overrides)
    try {
      const result = await queryContainerAllocationSales(containerGuid, query)
      if (!mainRequestGuardRef.current.isLatest(requestId)) return
      loadedContainerGuidRef.current = containerGuid
      setReport(result)
      setStartDate(result.startDate ?? undefined)
      setEndDate(result.endDate ?? undefined)
      setPageNumber(result.pageNumber || query.pageNumber || 1)
      setPageSize(result.pageSize || query.pageSize || 20)
      setSortBy(query.sortBy || 'productCode')
      setSortDirection(query.sortDirection || 'asc')
      setAppliedSearch(query.search || '')
      if (commit.selectedWeeks !== undefined) {
        setSelectedWeeks(commit.selectedWeeks)
      }
    } catch (nextError) {
      if (!mainRequestGuardRef.current.isLatest(requestId)) return
      setError(nextError)
    } finally {
      // 旧请求结束时不得关闭较新请求的 loading。
      if (!mainRequestGuardRef.current.isLatest(requestId)) return
      setLoading(false)
    }
  }

  useEffect(() => {
    if (!active) {
      // KeepAlive 隐藏实例不能响应全局 URL，也不能让旧请求回来写状态。
      mainRequestGuardRef.current.invalidate()
      branchRequestGuardRef.current.invalidate()
      setLoading(false)
      setBranchLoading(false)
      setDrawerOpen(false)
      return
    }

    if (!shouldLoadContainerAllocationSales({
      active,
      requestedContainerGuid: containerGuid,
      loadedContainerGuid: loadedContainerGuidRef.current,
    })) {
      return
    }

    // 首次不传日期，由后端统一决定默认 4 周或返回不可查询原因。
    void loadReport({
      startDate: undefined,
      endDate: undefined,
      search: undefined,
      pageNumber: 1,
      sortBy: 'productCode',
      sortDirection: 'asc',
    }, { selectedWeeks: 4 })

    return () => {
      mainRequestGuardRef.current.invalidate()
      branchRequestGuardRef.current.invalidate()
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [active, containerGuid])

  const applyWeekRange = (weeks: number) => {
    if (!report?.arrivalDate || !report.canQuery) return
    const range = buildQuickRangeQuery(report.arrivalDate, weeks)
    void loadReport(range, { selectedWeeks: weeks })
  }

  const applyCustomEndDate = (value: dayjs.Dayjs | null) => {
    if (!value || !report?.arrivalDate || !report.canQuery) return
    const nextEndDate = value.format('YYYY-MM-DD')
    void loadReport(
      { startDate: report.arrivalDate.slice(0, 10), endDate: nextEndDate, pageNumber: 1 },
      { selectedWeeks: null },
    )
  }

  const openBranches = async (product: ContainerAllocationSalesProduct) => {
    if (!startDate || !endDate) return
    setSelectedProduct(product)
    setDrawerOpen(true)
    setBranchReport(null)
    setBranchError(undefined)
    setBranchLoading(true)
    const requestId = branchRequestGuardRef.current.begin()
    try {
      const result = await queryContainerAllocationSalesBranches(
        containerGuid,
        buildBranchQuery(product.productCode, startDate, endDate),
      )
      if (!branchRequestGuardRef.current.isLatest(requestId)) return
      setBranchReport(result)
    } catch (nextError) {
      if (!branchRequestGuardRef.current.isLatest(requestId)) return
      setBranchError(nextError instanceof Error ? nextError.message : '分店明细加载失败')
    } finally {
      if (!branchRequestGuardRef.current.isLatest(requestId)) return
      setBranchLoading(false)
    }
  }

  const handleTableChange: TableProps<ContainerAllocationSalesProduct>['onChange'] = (
    pagination,
    _filters,
    sorter,
  ) => {
    const activeSorter = (Array.isArray(sorter) ? sorter[0] : sorter) as SorterResult<ContainerAllocationSalesProduct>
    const next = mapTableChangeToQuery(pagination, {
      field: typeof activeSorter?.field === 'string' ? activeSorter.field : undefined,
      order: activeSorter?.order,
    })
    void loadReport(next)
  }

  const productColumns: ColumnsType<ContainerAllocationSalesProduct> = [
    {
      title: '序号 / 图片',
      key: 'imageAndIndex',
      width: 150,
      fixed: 'left',
      render: (_, record, index) => (
        <Space size={6}>
          <Typography.Text
            style={{ display: 'inline-block', minWidth: 24, textAlign: 'right' }}
          >
            {getPaginatedRowNumber(pageNumber, pageSize, index)}
          </Typography.Text>
          <Image
            src={record.productImage || PRODUCT_IMAGE_FALLBACK}
            alt=""
            width={40}
            height={40}
            style={{ objectFit: 'contain', borderRadius: 4, border: '1px solid #f0f0f0' }}
            preview={false}
            fallback={PRODUCT_IMAGE_FALLBACK}
          />
          <Button
            type="text"
            size="small"
            aria-label={`查看第 ${getPaginatedRowNumber(pageNumber, pageSize, index)} 个商品 ${record.itemNumber || record.productName || record.productCode} 的分店明细`}
            onClick={() => void openBranches(record)}
            style={{ paddingInline: 4 }}
          >
            明细
          </Button>
        </Space>
      ),
    },
    {
      title: '货号 / 商品名称',
      dataIndex: 'itemNumber',
      key: 'product',
      width: 260,
      // 表格只负责发送排序条件，实际顺序由后端按货号并以商品编码稳定兜底。
      sorter: true,
      sortOrder: sortBy === 'itemNumber' ? (sortDirection === 'asc' ? 'ascend' : 'descend') : null,
      render: (_, record) => (
        <Space direction="vertical" size={0}>
          <Typography.Text>{record.itemNumber || '-'}</Typography.Text>
          <Typography.Text type="secondary" ellipsis={{ tooltip: record.productName || undefined }} style={{ maxWidth: 230 }}>
            {record.productName || '-'}
          </Typography.Text>
        </Space>
      ),
    },
    { title: '装柜数量', dataIndex: 'loadingQuantity', key: 'loadingQuantity', width: 110, align: 'right', sorter: true, render: formatQuantity },
    { title: '配货数量', dataIndex: 'allocationQuantity', key: 'allocationQuantity', width: 110, align: 'right', sorter: true, render: formatQuantity },
    { title: '配货进口金额', dataIndex: 'allocationImportAmount', key: 'allocationImportAmount', width: 140, align: 'right', sorter: true, render: formatAustralianCurrency },
    { title: '销售数量', dataIndex: 'salesQuantity', key: 'salesQuantity', width: 110, align: 'right', sorter: true, render: salesValue },
    { title: '销售金额', dataIndex: 'salesAmount', key: 'salesAmount', width: 130, align: 'right', sorter: true, render: formatAustralianCurrency },
    { title: '销售均价', dataIndex: 'averageSalesPrice', key: 'averageSalesPrice', width: 120, align: 'right', sorter: true, render: formatAustralianCurrency },
    {
      title: '毛利率',
      dataIndex: 'grossMarginRate',
      key: 'grossMarginRate',
      width: 110,
      align: 'right',
      sorter: true,
      render: (_, record) => getGrossMarginDisplay(record),
    },
  ]

  const branchColumns: ColumnsType<ContainerAllocationSalesBranch> = [
    {
      title: '分店编码',
      dataIndex: 'branchCode',
      width: 110,
      fixed: 'left',
      sorter: (left, right, sortOrder) => compareContainerAllocationSalesBranches(left, right, 'branchCode', sortOrder),
    },
    {
      title: '分店名称',
      dataIndex: 'branchName',
      width: 160,
      sorter: (left, right, sortOrder) => compareContainerAllocationSalesBranches(left, right, 'branchName', sortOrder),
    },
    {
      title: '状态',
      dataIndex: 'isActive',
      width: 90,
      sorter: (left, right, sortOrder) => compareContainerAllocationSalesBranches(left, right, 'isActive', sortOrder),
      render: (active: boolean) => <Tag color={active ? 'green' : 'default'}>{active ? '有效' : '已停用'}</Tag>,
    },
    {
      title: '配货数量',
      dataIndex: 'allocationQuantity',
      width: 110,
      align: 'right',
      sorter: (left, right, sortOrder) => compareContainerAllocationSalesBranches(left, right, 'allocationQuantity', sortOrder),
      render: formatQuantity,
    },
    {
      title: '配货进口金额',
      dataIndex: 'allocationImportAmount',
      width: 140,
      align: 'right',
      sorter: (left, right, sortOrder) => compareContainerAllocationSalesBranches(left, right, 'allocationImportAmount', sortOrder),
      render: formatAustralianCurrency,
    },
    {
      title: '销售数量',
      dataIndex: 'salesQuantity',
      width: 110,
      align: 'right',
      sorter: (left, right, sortOrder) => compareContainerAllocationSalesBranches(left, right, 'salesQuantity', sortOrder),
      render: salesValue,
    },
    {
      title: '销售金额',
      dataIndex: 'salesAmount',
      width: 130,
      align: 'right',
      sorter: (left, right, sortOrder) => compareContainerAllocationSalesBranches(left, right, 'salesAmount', sortOrder),
      render: formatAustralianCurrency,
    },
    {
      title: '销售均价',
      dataIndex: 'averageSalesPrice',
      width: 120,
      align: 'right',
      sorter: (left, right, sortOrder) => compareContainerAllocationSalesBranches(left, right, 'averageSalesPrice', sortOrder),
      render: formatAustralianCurrency,
    },
    {
      title: '毛利率',
      dataIndex: 'grossMarginRate',
      width: 110,
      align: 'right',
      sorter: (left, right, sortOrder) => compareContainerAllocationSalesBranches(left, right, 'grossMarginRate', sortOrder),
      render: (_, record) => getGrossMarginDisplay(record),
    },
  ]

  const isNotFound = error instanceof RequestError && error.status === 404
  const viewState = report ? getContainerAllocationSalesViewState({
    canQuery: report.canQuery,
    total: report.total,
    statisticStatus: report.statisticStatus,
    allocationQuantity: report.totals.allocationQuantity,
    salesQuantity: report.totals.salesQuantity,
    search: appliedSearch,
  }) : null

  if (loading && !report) {
    return <PageContainer title="货柜配销数据"><Card><Skeleton active paragraph={{ rows: 10 }} /></Card></PageContainer>
  }

  if (error && !report) {
    return (
      <PageContainer title="货柜配销数据">
        <Result
          status={isNotFound ? '404' : 'error'}
          title={isNotFound ? '货柜不存在' : '数据加载失败'}
          subTitle={error instanceof Error ? error.message : '请稍后重试'}
          extra={<Button onClick={() => navigate('/warehouse/containers')}>返回货柜列表</Button>}
        />
      </PageContainer>
    )
  }

  const totals = report?.totals
  const pagination: TablePaginationConfig = {
    current: pageNumber,
    pageSize,
    total: report?.total ?? 0,
    showSizeChanger: true,
    showTotal: (total) => `共 ${total} 个商品`,
  }

  return (
    <PageContainer
      title={`货柜配销数据${report?.containerNumber ? ` · ${formatContainerNumber(report.containerNumber)}` : ''}`}
      extra={(
        <Space size={4}>
          {report?.containerNumber ? <ContainerNumberRevealButton /> : null}
          <Button icon={<ArrowLeftOutlined />} onClick={() => navigate('/warehouse/containers')}>返回货柜列表</Button>
        </Space>
      )}
    >
      <Space direction="vertical" size={12} style={{ width: '100%' }}>
        <Card size="small">
          <Space wrap>
            <Typography.Text strong>统计起点：</Typography.Text>
            <Typography.Text>{report?.arrivalDate?.slice(0, 10) || '-'}</Typography.Text>
            {report?.arrivalDateBasis === 'Actual' ? <Tag color="green">实际到货日</Tag> : null}
            {report?.arrivalDateBasis === 'Expected' ? <Tag color="gold">预计到岸日</Tag> : null}
            <Typography.Text type="secondary">{report?.rangeLabel}</Typography.Text>
          </Space>
        </Card>

        {!report?.canQuery ? (
          <Alert type="warning" showIcon message="当前货柜暂不可查询" description={report?.queryMessage || '请补充货柜到货日期'} />
        ) : (
          <Card size="small">
            <Space wrap>
              <Typography.Text>到货后：</Typography.Text>
              {WEEK_OPTIONS.map((weeks) => (
                <Button key={weeks} type={selectedWeeks === weeks ? 'primary' : 'default'} onClick={() => applyWeekRange(weeks)}>
                  {weeks} 周
                </Button>
              ))}
              <DatePicker
                allowClear={false}
                value={endDate ? dayjs(endDate) : null}
                disabledDate={(current) => Boolean(report.arrivalDate) && isCustomEndDateDisabled(current, report.arrivalDate!)}
                onChange={applyCustomEndDate}
              />
              <Input
                allowClear
                value={search}
                prefix={<SearchOutlined />}
                placeholder="商品编码、货号或名称"
                style={{ width: 240 }}
                onChange={(event) => setSearch(event.target.value)}
                onPressEnter={() => void loadReport({ search: search.trim() || undefined, pageNumber: 1 })}
              />
              <Button type="primary" icon={<SearchOutlined />} onClick={() => void loadReport({ search: search.trim() || undefined, pageNumber: 1 })}>
                查询
              </Button>
            </Space>
          </Card>
        )}

        {report?.statisticStatus !== 'Fresh' && report?.canQuery ? (
          <Alert
            type="warning"
            showIcon
            message="销售统计暂未就绪"
            description={formatStatisticMessageAmounts(report.statisticMessage)
              || '配货数据仍可查看，销售和毛利字段暂不展示。'}
          />
        ) : null}
        {error && report ? <Alert type="error" showIcon message="刷新失败" description={error instanceof Error ? error.message : '请稍后重试'} /> : null}
        {viewState?.showNoStatistics ? <Alert type="info" showIcon message="所选区间暂无配货或销售统计数据" /> : null}

        {report?.canQuery ? <Card size="small">
          {viewState?.emptyDescription ? (
            <Empty description={viewState.emptyDescription} />
          ) : viewState?.showTable ? (
            <MeasuredTable metricId="warehouse.container-allocation-sales.table-1"
              rowKey="productCode"
              size="small"
              loading={loading}
              columns={productColumns}
              dataSource={report?.items ?? []}
              scroll={{ x: 1230 }}
              pagination={pagination}
              onChange={handleTableChange}
              onRow={(record) => ({
                style: { cursor: 'pointer' },
                onClick: (event) => {
                  if (!shouldTriggerTableRowClick(event.target, event.currentTarget)) return
                  void openBranches(record)
                },
              })}
              summary={() => totals ? (
                <MeasuredTable.Summary.Row>
                  <MeasuredTable.Summary.Cell index={0}><Typography.Text strong>全表合计（{totals.productCount}）</Typography.Text></MeasuredTable.Summary.Cell>
                  <MeasuredTable.Summary.Cell index={1}>-</MeasuredTable.Summary.Cell>
                  <MeasuredTable.Summary.Cell index={2} align="right">{formatQuantity(totals.loadingQuantity)}</MeasuredTable.Summary.Cell>
                  <MeasuredTable.Summary.Cell index={3} align="right">{formatQuantity(totals.allocationQuantity)}</MeasuredTable.Summary.Cell>
                  <MeasuredTable.Summary.Cell index={4} align="right">{formatAustralianCurrency(totals.allocationImportAmount)}</MeasuredTable.Summary.Cell>
                  <MeasuredTable.Summary.Cell index={5} align="right">{salesValue(totals.salesQuantity)}</MeasuredTable.Summary.Cell>
                  <MeasuredTable.Summary.Cell index={6} align="right">{formatAustralianCurrency(totals.salesAmount)}</MeasuredTable.Summary.Cell>
                  <MeasuredTable.Summary.Cell index={7} align="right">{formatAustralianCurrency(totals.averageSalesPrice)}</MeasuredTable.Summary.Cell>
                  <MeasuredTable.Summary.Cell index={8} align="right">{getGrossMarginDisplay(totals)}</MeasuredTable.Summary.Cell>
                </MeasuredTable.Summary.Row>
              ) : null}
            />
          ) : null}
        </Card> : null}
      </Space>

      <Drawer
        title={selectedProduct
          ? `分店明细 · ${selectedProduct.itemNumber || selectedProduct.productName || '商品'}`
          : '分店明细'}
        width="min(1100px, 92vw)"
        open={drawerOpen}
        onClose={() => {
          branchRequestGuardRef.current.invalidate()
          setBranchLoading(false)
          setDrawerOpen(false)
        }}
      >
        {branchReport?.statisticStatus !== 'Fresh' && branchReport ? (
          <Alert
            type="warning"
            showIcon
            message="销售统计暂未就绪"
            description={formatStatisticMessageAmounts(branchReport.statisticMessage) || '仅展示配货数据。'}
            style={{ marginBottom: 12 }}
          />
        ) : null}
        {branchError ? <Alert type="error" showIcon message="分店明细加载失败" description={branchError} style={{ marginBottom: 12 }} /> : null}
        <MeasuredTable metricId="warehouse.container-allocation-sales.table-2"
          rowKey="branchCode"
          size="small"
          loading={branchLoading}
          columns={branchColumns}
          dataSource={branchReport?.items ?? []}
          scroll={{ x: 1080 }}
          pagination={false}
          locale={{ emptyText: branchLoading ? '加载中' : '暂无分店数据' }}
        />
      </Drawer>
    </PageContainer>
  )
}
