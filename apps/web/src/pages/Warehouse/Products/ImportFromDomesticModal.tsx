import { CheckCircleOutlined } from '@ant-design/icons'
import { Button, Checkbox, Image, Input, InputNumber, Modal, Result, Space, Tag, message } from 'antd'
import type { ColumnsType } from 'antd/es/table'
import { useEffect, useMemo, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import {
  getDomesticProductsNotInWarehouse,
  importFromDomestic,
  type DomesticProductNotInWarehouseItem,
  type ImportFromDomesticItem,
} from '../../../services/warehouseProductService'
import { ProductTypeLabels } from '../../../types/domesticProduct'
import { createLatestRequestGuard, runLatestGuardedRequest } from '../../../utils/latestRequestGuard'
import './compact.css'
import { MeasuredTable } from '../../../components/MeasuredTable'

function formatPrice(value?: number) {
  if (value === undefined || value === null) {
    return '--'
  }

  return value.toFixed(2)
}

interface EditablePriceData {
  domesticPrice?: number
  oemPrice?: number
  importPrice?: number
  volume?: number
}

interface ImportFromDomesticModalProps {
  open: boolean
  onCancel: () => void
  onSuccess: () => void
  /** 是否显示「供应商」列：该接口只返回国内供应商名称（没有代码），无查看名称权限时整列隐藏。 */
  showSupplierName?: boolean
}

function sortByItemNumber<T extends { itemNumber?: string }>(items: T[]) {
  return [...items].sort((left, right) =>
    (left.itemNumber || '').localeCompare(right.itemNumber || '', 'zh-CN', { numeric: true }),
  )
}

interface CurrentPageSelectableItem {
  productCode: string
}

export function mergeCurrentPageSelection<T extends CurrentPageSelectableItem>(
  selectedKeys: React.Key[],
  currentPageItems: T[],
) {
  const mergedKeys = new Set(selectedKeys.map(String))

  currentPageItems.forEach((item) => {
    mergedKeys.add(item.productCode)
  })

  return Array.from(mergedKeys)
}

export function removeCurrentPageSelection<T extends CurrentPageSelectableItem>(
  selectedKeys: React.Key[],
  currentPageItems: T[],
) {
  const currentPageKeys = new Set(currentPageItems.map((item) => item.productCode))

  return selectedKeys.map(String).filter((key) => !currentPageKeys.has(key))
}

export default function ImportFromDomesticModal({
  open,
  onCancel,
  onSuccess,
  showSupplierName = true,
}: ImportFromDomesticModalProps) {
  const { t } = useTranslation()
  const [loading, setLoading] = useState(false)
  const [importing, setImporting] = useState(false)
  const [items, setItems] = useState<DomesticProductNotInWarehouseItem[]>([])
  const [selectedRowKeys, setSelectedRowKeys] = useState<React.Key[]>([])
  const [page, setPage] = useState(1)
  const [pageSize, setPageSize] = useState(20)
  const [total, setTotal] = useState(0)
  const [searchText, setSearchText] = useState('')
  const [syncStorePrices, setSyncStorePrices] = useState(true)
  const [syncMultiCodes, setSyncMultiCodes] = useState(true)
  const [editablePrices, setEditablePrices] = useState<Record<string, EditablePriceData>>({})
  const listRequestGuardRef = useRef(createLatestRequestGuard())
  // 打开会话与列表请求分别隔离，关闭再打开后旧响应不能写入新弹窗。
  const nextSessionIdRef = useRef(0)
  const activeSessionIdRef = useRef<number | null>(null)

  const loadItems = async (
    overrides?: { page?: number; pageSize?: number; searchText?: string },
    sessionId = activeSessionIdRef.current,
  ) => {
    if (sessionId === null || activeSessionIdRef.current !== sessionId) {
      return
    }

    const nextPage = overrides?.page ?? page
    const nextPageSize = overrides?.pageSize ?? pageSize
    const nextSearchText = overrides?.searchText ?? searchText

    await runLatestGuardedRequest(
      listRequestGuardRef.current,
      () =>
        getDomesticProductsNotInWarehouse({
          page: nextPage,
          pageSize: nextPageSize,
          globalSearch: nextSearchText.trim() || undefined,
        }),
      {
        onStart: () => {
          if (activeSessionIdRef.current === sessionId) setLoading(true)
        },
        onSuccess: (result) => {
          if (activeSessionIdRef.current !== sessionId) return
          setItems(sortByItemNumber(result.data || []))
          setTotal(result.total || 0)
          setPage(nextPage)
          setPageSize(nextPageSize)
        },
        onError: (error) => {
          if (activeSessionIdRef.current !== sessionId) return
          console.error(error)
          message.error(error instanceof Error ? error.message : t('warehouse.loadDomesticFailed', '加载国内商品失败'))
        },
        onSettled: () => {
          if (activeSessionIdRef.current === sessionId) setLoading(false)
        },
      },
    )
  }

  const invalidateListSession = () => {
    activeSessionIdRef.current = null
    listRequestGuardRef.current.invalidate()
    setLoading(false)
    setImporting(false)
  }

  const handleCancel = () => {
    invalidateListSession()
    onCancel()
  }

  useEffect(() => {
    if (!open) {
      invalidateListSession()
      setSelectedRowKeys([])
      setEditablePrices({})
      setSearchText('')
      setPage(1)
      setPageSize(20)
      setTotal(0)
      setSyncStorePrices(true)
      setSyncMultiCodes(true)
      return
    }

    const sessionId = ++nextSessionIdRef.current
    activeSessionIdRef.current = sessionId
    listRequestGuardRef.current.invalidate()
    void loadItems({ page: 1 }, sessionId)

    return () => {
      if (activeSessionIdRef.current === sessionId) {
        activeSessionIdRef.current = null
        listRequestGuardRef.current.invalidate()
      }
    }
  }, [open])

  const handleSelectCurrentPage = () => {
    // 只全选当前页已展示商品，避免误选分页或搜索结果中用户还没看到的商品。
    setSelectedRowKeys((current) => mergeCurrentPageSelection(current, items))
  }

  const handleClearCurrentPageSelection = () => {
    // 只取消当前页商品，保留其他分页中已经手动选择的商品。
    setSelectedRowKeys((current) => removeCurrentPageSelection(current, items))
  }

  const columns = useMemo<ColumnsType<DomesticProductNotInWarehouseItem>>(
    () => [
      {
        title: t('warehouse.importDomestic.image', '图片'),
        dataIndex: 'productImage',
        width: 58,
        render: (value: string | undefined, record) => (
          <Image
            src={value}
            alt={record.productName}
            width={36}
            height={36}
            style={{ borderRadius: 4, objectFit: 'cover' }}
            fallback="data:image/gif;base64,R0lGODlhAQABAAD/ACwAAAAAAQABAAACADs="
          />
        ),
      },
      {
        title: t('productImport.hbProductNoCol', '货号'),
        dataIndex: 'itemNumber',
        width: 112,
      },
      {
        title: t('domesticProducts.barcode', '条码'),
        dataIndex: 'barcode',
        width: 126,
        render: (value?: string) => value || '--',
      },
      {
        title: t('domesticProducts.productName', '商品名称'),
        dataIndex: 'productName',
        width: 160,
        render: (value?: string) => value ? <div className="warehouse-import-domestic-two-line">{value}</div> : '--',
      },
      {
        title: t('productCreation.type', '类型'),
        dataIndex: 'productType',
        width: 76,
        render: (value: number) => ProductTypeLabels[value as keyof typeof ProductTypeLabels] || '--',
      },
      // 纯名称列：没有「查看国内供应商名称」权限时后端不返回名称，整列不显示，避免一列空值。
      ...(showSupplierName
        ? [{
            title: t('domesticProducts.supplier', '供应商'),
            dataIndex: 'supplierName',
            width: 132,
            render: (value?: string) => value ? <div className="warehouse-import-domestic-two-line">{value}</div> : '--',
          }]
        : []),
      {
        title: t('domesticProducts.domesticPrice', '国内价'),
        dataIndex: 'domesticPrice',
        width: 80,
        render: (_value, record) => (
          <InputNumber
            min={0}
            precision={2}
            value={editablePrices[record.productCode]?.domesticPrice ?? record.domesticPrice}
            onChange={(value) =>
              setEditablePrices((current) => ({
                ...current,
                [record.productCode]: {
                  ...current[record.productCode],
                  domesticPrice: value ?? undefined,
                },
              }))
            }
          />
        ),
      },
      {
        title: t('warehouse.retail', '零售'),
        dataIndex: 'oemPrice',
        width: 80,
        render: (_value, record) => (
          <InputNumber
            min={0}
            precision={2}
            value={editablePrices[record.productCode]?.oemPrice ?? record.oemPrice}
            onChange={(value) =>
              setEditablePrices((current) => ({
                ...current,
                [record.productCode]: {
                  ...current[record.productCode],
                  oemPrice: value ?? undefined,
                },
              }))
            }
          />
        ),
      },
      {
        title: t('warehouse.importPrice', '进口价'),
        dataIndex: 'importPrice',
        width: 80,
        render: (_value, record) => (
          <InputNumber
            min={0}
            precision={2}
            value={editablePrices[record.productCode]?.importPrice ?? record.importPrice}
            onChange={(value) =>
              setEditablePrices((current) => ({
                ...current,
                [record.productCode]: {
                  ...current[record.productCode],
                  importPrice: value ?? undefined,
                },
              }))
            }
          />
        ),
      },
      {
        title: t('warehouse.volume', '体积'),
        dataIndex: 'volume',
        width: 78,
        render: (_value, record) => (
          <InputNumber
            min={0}
            precision={4}
            value={editablePrices[record.productCode]?.volume ?? record.volume}
            onChange={(value) =>
              setEditablePrices((current) => ({
                ...current,
                [record.productCode]: {
                  ...current[record.productCode],
                  volume: value ?? undefined,
                },
              }))
            }
          />
        ),
      },
      {
        title: t('warehouse.structure', '结构'),
        key: 'flags',
        width: 78,
        render: (_, record) => (
          <Space size={[4, 4]} wrap>
            {record.hasSetProducts ? <Tag color="gold">{t('productCreation.set', '套装')}</Tag> : null}
            {record.hasMultiCodes ? <Tag color="blue">{t('warehouse.multiCode', '多码')}</Tag> : null}
            {!record.hasSetProducts && !record.hasMultiCodes ? <Tag>{t('productCreation.normal', '普通')}</Tag> : null}
          </Space>
        ),
      },
    ],
    [editablePrices, showSupplierName],
  )

  const handleImport = async () => {
    const sessionId = activeSessionIdRef.current
    if (sessionId === null) {
      return
    }

    if (!selectedRowKeys.length) {
      message.warning(t('warehouse.selectAtLeastOne', '请至少选择一个商品'))
      return
    }

    const selectedItems: ImportFromDomesticItem[] = selectedRowKeys.map((key) => {
      const productCode = String(key)
      const source = items.find((item) => item.productCode === productCode)
      const override = editablePrices[productCode]

      return {
        productCode,
        domesticPrice: override?.domesticPrice ?? source?.domesticPrice,
        oemPrice: override?.oemPrice ?? source?.oemPrice,
        importPrice: override?.importPrice ?? source?.importPrice,
        volume: override?.volume ?? source?.volume,
      }
    })

    const invalidItems = selectedItems.filter(
      (item) =>
        !item.domesticPrice ||
        item.domesticPrice <= 0 ||
        !item.oemPrice ||
        item.oemPrice <= 0 ||
        !item.importPrice ||
        item.importPrice <= 0,
    )

    if (invalidItems.length) {
      message.warning(t('warehouse.importDomestic.invalidItems', '有 {{count}} 个商品的国内价/零售价/进口价未正确填写', { count: invalidItems.length }))
      return
    }

    try {
      setImporting(true)
      const result = await importFromDomestic({
        items: selectedItems,
        syncBranchPrice: syncStorePrices,
        syncMultiCodePrice: syncMultiCodes,
      })

      if (activeSessionIdRef.current !== sessionId) {
        return
      }

      if (!result.success) {
        message.error(result.message || t('warehouse.importFailed', '导入失败'))
        return
      }

      const successCount = result.successCount ?? selectedItems.length
      const failedCount = result.failedCount ?? 0
      const failedMessages = result.errors?.length
        ? result.errors
        : (result.results ?? []).filter((item) => !item.success).map((item) => item.message || item.productCode)

      Modal.success({
        title: failedCount ? t('warehouse.partialImportSuccess', '部分导入成功') : t('warehouse.importSuccess', '导入成功'),
        icon: <CheckCircleOutlined style={{ color: failedCount ? '#faad14' : '#52c41a' }} />,
        content: (
          <Result
            status={failedCount ? 'warning' : 'success'}
            title={failedCount ? t('warehouse.partialImportSuccessTitle', '部分商品导入成功') : t('warehouse.allImported', '已成功导入商品')}
            subTitle={t('warehouse.importResult', '成功 {{success}} 个，失败 {{failed}} 个', { success: successCount, failed: failedCount })}
            extra={
              failedMessages.length ? (
                <div style={{ textAlign: 'left', maxHeight: 180, overflow: 'auto' }}>
                  {failedMessages.map((item, index) => (
                    <div key={`${item}_${index}`}>- {item}</div>
                  ))}
                </div>
              ) : null
            }
          />
        ),
        onOk: () => {
          if (activeSessionIdRef.current !== sessionId) {
            return
          }

          onSuccess()
          handleCancel()
        },
      })
    } catch (error) {
      if (activeSessionIdRef.current !== sessionId) {
        return
      }

      console.error(error)
      message.error(error instanceof Error ? error.message : t('warehouse.importFailed', '导入失败'))
    } finally {
      if (activeSessionIdRef.current === sessionId) {
        setImporting(false)
      }
    }
  }

  return (
    <Modal
      title={t('warehouse.importFromDomestic', '从国内导入')}
      open={open}
      width={1400}
      wrapClassName="warehouse-import-domestic-modal"
      destroyOnHidden
      okText={t('warehouse.importDomestic.importSelected', '导入选中 ({{count}})', { count: selectedRowKeys.length })}
      cancelText={t('common.close', '关闭')}
      confirmLoading={importing}
      onCancel={handleCancel}
      onOk={() => void handleImport()}
    >
      <Space direction="vertical" size={8} style={{ width: '100%' }}>
        <Space wrap size={8}>
          <Input.Search
            allowClear
            placeholder={t('warehouse.searchProduct', '搜索货号 / 条码 / 商品名称')}
            style={{ width: 260 }}
            value={searchText}
            onChange={(event) => setSearchText(event.target.value)}
            onSearch={(value) => void loadItems({ page: 1, searchText: value })}
          />
          <Checkbox checked={syncStorePrices} onChange={(event) => setSyncStorePrices(event.target.checked)}>
            {t('warehouse.importDomestic.syncStorePrice', '同步分店价格')}
          </Checkbox>
          <Checkbox checked={syncMultiCodes} onChange={(event) => setSyncMultiCodes(event.target.checked)}>
            {t('warehouse.importDomestic.syncMultiCode', '同步多码价格')}
          </Checkbox>
          <Button onClick={() => void loadItems({ page: 1 })}>{t('common.refresh', '刷新')}</Button>
          <Button disabled={!items.length} onClick={handleSelectCurrentPage}>
            {t('warehouse.importDomestic.selectCurrentPage', '全选当前页')}
          </Button>
          <Button disabled={!items.length} onClick={handleClearCurrentPageSelection}>
            {t('warehouse.importDomestic.clearCurrentPageSelection', '取消当前页选择')}
          </Button>
        </Space>

        <Space size={16}>
          <span>{t('warehouse.importDomestic.pendingImport', '待导入')}: {total}</span>
          <span>{t('warehouse.importDomestic.selected', '已选中')}: {selectedRowKeys.length}</span>
          {selectedRowKeys.length ? (
            <span>
              {t('warehouse.importDomestic.currentRetailValue', '当前零售列取值')}: {
                formatPrice(
                  editablePrices[String(selectedRowKeys[0])]?.oemPrice ??
                    items.find((item) => item.productCode === String(selectedRowKeys[0]))?.oemPrice,
                )
              }
            </span>
          ) : null}
        </Space>

        <MeasuredTable metricId="warehouse.products.import-from-domestic-modal.table-1"
          className="warehouse-import-domestic-compact-table"
          rowKey="productCode"
          size="small"
          virtual
          loading={loading}
          columns={columns}
          dataSource={items}
          rowSelection={{
            selectedRowKeys,
            onChange: setSelectedRowKeys,
            preserveSelectedRowKeys: true,
            columnWidth: 42,
          }}
          pagination={{
            current: page,
            pageSize,
            total,
            showSizeChanger: true,
            onChange: (nextPage, nextPageSize) => void loadItems({ page: nextPage, pageSize: nextPageSize }),
          }}
          // 紧凑样式只服务当前弹窗：保留 20/page，但提高可视行数并减少横向滚动压力。
          scroll={{ x: 1120, y: 560 }}
        />
      </Space>
    </Modal>
  )
}
