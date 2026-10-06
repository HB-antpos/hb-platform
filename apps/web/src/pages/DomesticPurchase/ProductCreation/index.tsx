import { DownloadOutlined, PlusOutlined, ReloadOutlined } from '@ant-design/icons'
import { Button, Card, Segmented, Tooltip, message } from 'antd'
import type { ColumnsType } from 'antd/es/table'
import { useCallback, useEffect, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import PageContainer from '../../../components/PageContainer'
import { MeasuredTable } from '../../../components/MeasuredTable'
import { registerPageMessages } from '../../../i18n/registerPageMessages'
import { getActiveChinaSuppliers } from '../../../services/chinaSupplierService'
import { getBatchDetail, getBatchList } from '../../../services/domesticProductCreationService'
import type { BatchInfo } from '../../../types/domesticProductCreation'
import { createLatestRequestGuard, runLatestGuardedRequest } from '../../../utils/latestRequestGuard'
import BatchDetailDrawer from './BatchDetailDrawer'
import BatchWorkspace from './BatchWorkspace'
import CopyValueButton from './CopyValueButton'
import SupplierSelect from './SupplierSelect'
import type { SupplierOption } from './SupplierSelect'
import { BATCH_CREATED_RANGES, buildBatchListParams, formatBatchTime } from './batchListLogic'
import type { BatchCreatedRange, BatchListQuery } from './batchListLogic'
import { getBatchDetailErrorMessage } from './batchDetailErrorMessage'
import { exportProductCreationBatchToExcel, getExportableBatchItems } from './exportBatchDetail'
import './productCreation.css'
import productCreationMessagesEn from './productCreationMessages.en.json'
import productCreationMessagesZh from './productCreationMessages.zh.json'

// 页面级文案随页面代码块懒加载，不进首屏 i18n 包（首屏 gzip 预算很紧，见仓库约定）。
registerPageMessages({ zh: productCreationMessagesZh, en: productCreationMessagesEn })

const DEFAULT_PAGE_SIZE = 20

const RANGE_LABEL_KEYS: Record<BatchCreatedRange, string> = {
  all: 'common.all',
  today: 'productCreation.rangeToday',
  last7: 'productCreation.rangeLast7',
  last30: 'productCreation.rangeLast30',
}

export default function ProductCreationPage() {
  const { t } = useTranslation()
  // 「创建批次」是页内状态切换，不新增路由；列表与工作台互斥显示。
  const [view, setView] = useState<'list' | 'workspace'>('list')
  const [loading, setLoading] = useState(false)
  const [loaded, setLoaded] = useState(false)
  const [data, setData] = useState<BatchInfo[]>([])
  const [total, setTotal] = useState(0)
  const [page, setPage] = useState(1)
  const [pageSize, setPageSize] = useState(DEFAULT_PAGE_SIZE)
  const [supplierFilter, setSupplierFilter] = useState<string | undefined>()
  const [range, setRange] = useState<BatchCreatedRange>('all')
  const [suppliers, setSuppliers] = useState<SupplierOption[]>([])
  const [suppliersLoading, setSuppliersLoading] = useState(false)
  const [detailOpen, setDetailOpen] = useState(false)
  const [selectedBatch, setSelectedBatch] = useState<BatchInfo | null>(null)
  const [exportingBatchNumber, setExportingBatchNumber] = useState<string | null>(null)
  const listGuardRef = useRef(createLatestRequestGuard())

  /**
   * 加载批次列表。overrides 里显式写 undefined 表示「清除该筛选」，所以用对象合并而不是默认参数；
   * 只有最后一次请求能写入页面状态，快速切换筛选/翻页时旧响应不会覆盖新结果。
   */
  const loadData = async (overrides: Partial<BatchListQuery> = {}) => {
    const query: BatchListQuery = { page, pageSize, supplierCode: supplierFilter, range, ...overrides }
    await runLatestGuardedRequest(listGuardRef.current, () => getBatchList(buildBatchListParams(query)), {
      onStart: () => setLoading(true),
      onSuccess: (response) => {
        if (response.success && response.data) {
          setData(response.data.items || [])
          setTotal(response.data.total || 0)
        } else {
          setData([])
          setTotal(0)
        }
        setPage(query.page)
        setPageSize(query.pageSize)
        setSupplierFilter(query.supplierCode)
        setRange(query.range)
        setLoaded(true)
      },
      onError: (error) => {
        console.error(error)
        message.error(getBatchDetailErrorMessage(error, t('productCreation.loadBatchListFailed')))
      },
      // 旧请求结束时不能关闭较新请求的 loading。
      onSettled: () => setLoading(false),
    })
  }

  const loadSuppliers = useCallback(async () => {
    setSuppliersLoading(true)
    try {
      const response = await getActiveChinaSuppliers()
      setSuppliers((response || []).map((item) => ({ supplierCode: item.supplierCode, supplierName: item.supplierName })))
    } catch (error) {
      console.error(error)
      message.error(t('productCreation.loadSupplierFailed'))
    } finally {
      setSuppliersLoading(false)
    }
  }, [t])

  useEffect(() => {
    void loadData({ page: 1 })
    void loadSuppliers()
    const guard = listGuardRef.current
    return () => guard.invalidate()
    // 只在挂载时加载一次；之后由筛选/翻页显式触发。
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  const applyFilters = (patch: Partial<Pick<BatchListQuery, 'supplierCode' | 'range'>>) => {
    void loadData({ page: 1, ...patch })
  }

  const handleCreated = (created?: BatchInfo) => {
    setView('list')
    void loadData({ page: 1 })
    if (created?.batchNumber) {
      // 创建成功后直接展示新批次的明细，方便立刻复制/导出货号与条码。
      setSelectedBatch(created)
      setDetailOpen(true)
    }
  }

  const handleViewDetail = (record: BatchInfo) => {
    setSelectedBatch(record)
    setDetailOpen(true)
  }

  const handleExportBatch = async (record: BatchInfo) => {
    if (exportingBatchNumber) return
    const messageKey = `product-creation-export-${record.batchNumber}`
    setExportingBatchNumber(record.batchNumber)
    message.loading({ content: t('productCreation.exporting'), key: messageKey })
    try {
      const response = await getBatchDetail(record.batchNumber)
      if (!response.success || !response.data) {
        message.error({ content: response.message || t('productCreation.loadDetailFailed'), key: messageKey })
        return
      }
      if (getExportableBatchItems(response.data.items).length === 0) {
        message.warning({ content: t('productCreation.noDataToExport'), key: messageKey })
        return
      }
      await exportProductCreationBatchToExcel(response.data, { batchNumber: record.batchNumber, t })
      message.success({ content: t('productCreation.exportSuccess'), key: messageKey })
    } catch (error) {
      console.error('导出失败:', error)
      message.error({ content: t('productCreation.exportBatchFailed'), key: messageKey })
    } finally {
      setExportingBatchNumber(null)
    }
  }

  const columns: ColumnsType<BatchInfo> = [
    {
      title: t('productCreation.batchNumber'),
      dataIndex: 'batchNumber',
      key: 'batchNumber',
      width: 160,
      render: (value: string) => (
        <span className="pc-code-cell">
          <span className="pc-mono">{value}</span>
          <CopyValueButton value={value} label={value} />
        </span>
      ),
    },
    {
      // 不设宽度：吃掉其它列之外的剩余空间，名称过长时省略并带原生提示。
      title: t('domesticProducts.supplier'),
      dataIndex: 'supplierName',
      key: 'supplierName',
      ellipsis: true,
      render: (value: string, record) => value || record.supplierCode,
    },
    {
      title: t('productCreation.prefixColumn'),
      dataIndex: 'prefixCode',
      key: 'prefixCode',
      width: 80,
      render: (value?: string) => (value ? <span className="pc-prefix-tag">{value}</span> : <span className="pc-faint">—</span>),
    },
    {
      title: t('productCreation.itemCountColumn'),
      key: 'itemCount',
      width: 190,
      render: (_, record) => (
        <span className="pc-count-cell">
          <span className="pc-mono">{record.totalCount}</span>
          <span className="pc-sub">
            {' '}{t('productCreation.itemsUnit')} · {t('productCreation.normal')} {record.normalCount} · {t('productCreation.set')} {record.setCount}
          </span>
        </span>
      ),
    },
    {
      title: t('chinaSuppliers.createdAt'),
      dataIndex: 'createdAt',
      key: 'createdAt',
      width: 144,
      render: (value: string) => <span className="pc-time">{formatBatchTime(value)}</span>,
    },
    {
      title: t('productCreation.createdBy'),
      dataIndex: 'createdBy',
      key: 'createdBy',
      width: 88,
      ellipsis: true,
      render: (value?: string) => value || <span className="pc-faint">—</span>,
    },
    {
      title: t('common.action'),
      key: 'actions',
      width: 124,
      align: 'right',
      render: (_, record) => (
        <span className="pc-row-actions">
          <Button type="link" size="small" onClick={() => handleViewDetail(record)}>
            {t('productCreation.detail')}
          </Button>
          <Button
            type="link"
            size="small"
            icon={<DownloadOutlined />}
            loading={exportingBatchNumber === record.batchNumber}
            disabled={exportingBatchNumber !== null && exportingBatchNumber !== record.batchNumber}
            onClick={() => void handleExportBatch(record)}
          >
            {t('productCreation.exportBatch')}
          </Button>
        </span>
      ),
    },
  ]

  return (
    <>
      {view === 'workspace' ? (
        <BatchWorkspace
          suppliers={suppliers}
          suppliersLoading={suppliersLoading}
          onExit={() => setView('list')}
          onCreated={handleCreated}
        />
      ) : (
        <PageContainer
          compact
          title={t('menu.productCreation')}
          subtitle={loaded ? t('productCreation.batchCountSummary', { count: total }) : undefined}
          extra={(
            <Button type="primary" icon={<PlusOutlined />} onClick={() => setView('workspace')} data-testid="product-creation-create-batch">
              {t('productCreation.createBatch')}
            </Button>
          )}
        >
          <Card>
            <div className="pc-toolbar" data-testid="product-creation-toolbar">
              <SupplierSelect
                allowClear
                suppliers={suppliers}
                loading={suppliersLoading}
                prefix={t('domesticProducts.supplier')}
                placeholder={t('common.all')}
                style={{ width: 260 }}
                value={supplierFilter}
                onChange={(value) => applyFilters({ supplierCode: value || undefined })}
              />
              {/* 标签与分段控件包成一组，窄屏换行时不会把标签孤零零留在上一行 */}
              <span className="pc-filter-group">
                <span className="pc-sub">{t('chinaSuppliers.createdAt')}</span>
                <Segmented<BatchCreatedRange>
                  value={range}
                  options={BATCH_CREATED_RANGES.map((value) => ({ value, label: t(RANGE_LABEL_KEYS[value]) }))}
                  onChange={(value) => applyFilters({ range: value })}
                />
              </span>
              <Tooltip title={t('common.refresh')}>
                <Button icon={<ReloadOutlined />} aria-label={t('common.refresh')} onClick={() => void loadData()} />
              </Tooltip>
            </div>

            <div data-testid="product-creation-batch-list">
              <MeasuredTable<BatchInfo>
                metricId="domestic-purchase.product-creation.batches"
                className="pc-batch-table"
                columns={columns}
                dataSource={data}
                rowKey="batchNumber"
                loading={loading}
                tableLayout="fixed"
                size="middle"
                pagination={{
                  current: page,
                  pageSize,
                  total,
                  showSizeChanger: true,
                  showTotal: (count) => t('common.totalCount', { count }),
                  // 改每页条数时回到第一页，翻页时保持当前筛选。
                  onChange: (nextPage, nextPageSize) => {
                    void loadData({ page: nextPageSize !== pageSize ? 1 : nextPage, pageSize: nextPageSize })
                  },
                }}
              />
            </div>
          </Card>
        </PageContainer>
      )}

      <BatchDetailDrawer
        open={detailOpen}
        batch={selectedBatch}
        // 只关闭抽屉、保留 selectedBatch：关闭动画期间标题不会闪成空白，下次打开会重新加载明细。
        onClose={() => setDetailOpen(false)}
      />
    </>
  )
}
