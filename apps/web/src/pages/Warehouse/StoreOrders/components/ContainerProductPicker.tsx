import { ArrowLeftOutlined } from '@ant-design/icons'
import { Button, Image, Input, InputNumber, Modal, Space, Steps, Tag, Typography, message } from 'antd'
import type { ColumnsType } from 'antd/es/table'
import type { SortOrder } from 'antd/es/table/interface'
import { useEffect, useMemo, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { ContainerNumberRevealButton, useContainerNumberDisplay } from '../../../../components/ContainerNumber'
import { getContainerList, getContainerProducts } from '../../../../services/containerService'
import type { ContainerDetail, ContainerMain } from '../../../../types/container'
import { formatSydneyDate, getSydneyDateTagColor } from '../../../../utils/sydneyDate'
import { MeasuredTable } from '../../../../components/MeasuredTable'

interface ContainerProductPickerProps {
  open: boolean
  loading?: boolean
  onClose: () => void
  onConfirm: (items: Array<{ productCode: string; quantity: number; importPrice?: number }>) => Promise<void>
  alreadySelectedCodes?: string[]
}

type ContainerRow = ContainerMain & { key: string }
type ProductRow = ContainerDetail & { key: string }

export default function ContainerProductPicker({
  open,
  loading,
  onClose,
  onConfirm,
  alreadySelectedCodes = [],
}: ContainerProductPickerProps) {
  const { t } = useTranslation()
  const { format: formatContainerNumber } = useContainerNumberDisplay()
  const [step, setStep] = useState(0)
  const [fetching, setFetching] = useState(false)
  const [submitting, setSubmitting] = useState(false)
  const [containers, setContainers] = useState<ContainerRow[]>([])
  const [selectedContainer, setSelectedContainer] = useState<ContainerRow | null>(null)
  const [products, setProducts] = useState<ProductRow[]>([])
  const [containerKeyword, setContainerKeyword] = useState('')
  const [productKeyword, setProductKeyword] = useState('')
  const [selectedRowKeys, setSelectedRowKeys] = useState<React.Key[]>([])
  const [productQuantities, setProductQuantities] = useState<Record<string, number | undefined>>({})
  const [productPage, setProductPage] = useState(1)
  const [productPageSize, setProductPageSize] = useState(50)
  const [productSortOrder, setProductSortOrder] = useState<SortOrder>('ascend')

  const resetState = () => {
    setStep(0)
    setContainers([])
    setSelectedContainer(null)
    setProducts([])
    setContainerKeyword('')
    setProductKeyword('')
    setSelectedRowKeys([])
    setProductQuantities({})
    setProductPage(1)
    setProductPageSize(50)
    setProductSortOrder('ascend')
  }

  useEffect(() => {
    if (!open) {
      return
    }

    resetState()

    const loadContainers = async () => {
      setFetching(true)
      try {
        const result = await getContainerList({
          page: 1,
          pageSize: 1000,
          sortBy: '预计到岸日期',
          sortDirection: 'desc',
        })
        setContainers(result.containers.map((item) => ({ ...item, key: item.hguid })))
      } catch (error) {
        console.error(error)
      message.error(error instanceof Error ? error.message : t('containers.messages.loadListFailed'))
      } finally {
        setFetching(false)
      }
    }

    void loadContainers()
  }, [open])

  const handleSelectContainer = async (container: ContainerRow) => {
    setSelectedContainer(container)
    setStep(1)
    setSelectedRowKeys([])
    setProductQuantities({})
    setProductPage(1)
    setProductPageSize(50)
    setProductSortOrder('ascend')
    setFetching(true)
    try {
      const result = await getContainerProducts(container.hguid)
      const rows = result
        .filter((item) => item.商品编码 || item.商品信息?.商品编码)
        .map((item) => ({
          ...item,
          key: item.hguid,
        }))
      setProducts(rows)
    } catch (error) {
      console.error(error)
      message.error(error instanceof Error ? error.message : t('storeOrders.containerPickerLoadProductsFailed'))
      setStep(0)
    } finally {
      setFetching(false)
    }
  }

  const filteredContainers = useMemo(
    () =>
      containers.filter(
        (item) =>
          !containerKeyword.trim() ||
          item.货柜编号?.toLowerCase().includes(containerKeyword.trim().toLowerCase()),
      ),
    [containerKeyword, containers],
  )

  const filteredProducts = useMemo(() => {
    const matched = products.filter((item) => {
        if (!productKeyword.trim()) {
          return true
        }

        const keyword = productKeyword.trim().toLowerCase()
        return (
          item.商品信息?.货号?.toLowerCase().includes(keyword) ||
          item.商品信息?.商品名称?.toLowerCase().includes(keyword) ||
          item.商品编码?.toLowerCase().includes(keyword)
        )
      })

    const sorted = [...matched].sort((left, right) => {
      const leftValue = left.商品信息?.货号 || ''
      const rightValue = right.商品信息?.货号 || ''
      const compareResult = leftValue.localeCompare(rightValue, 'zh-CN', { numeric: true, sensitivity: 'base' })
      return productSortOrder === 'descend' ? -compareResult : compareResult
    })

    return sorted
  }, [productKeyword, productSortOrder, products])

  const pagedProducts = useMemo(() => {
    const start = (productPage - 1) * productPageSize
    return filteredProducts.slice(start, start + productPageSize)
  }, [filteredProducts, productPage, productPageSize])

  const handleConfirm = async () => {
    const items: Array<{ productCode: string; quantity: number; importPrice?: number }> = []

    selectedRowKeys.forEach((key) => {
      const row = products.find((item) => item.key === key)
      const productCode = row?.商品编码 || row?.商品信息?.商品编码
      const quantity = productQuantities[String(key)]
      if (!row || !productCode || quantity === undefined || quantity <= 0) {
        return
      }

      items.push({
        productCode,
        quantity,
        importPrice: row.进口价格,
      })
    })

    if (!selectedRowKeys.length) {
      message.warning(t('storeOrders.detail.selectProductsFirst'))
      return
    }

    if (!items.length) {
      message.warning(t('storeOrders.containerPickerEnterPositiveQty'))
      return
    }

    setSubmitting(true)
    try {
      await onConfirm(items)
      onClose()
    } finally {
      setSubmitting(false)
    }
  }

  const renderSydneyDateTag = (value?: string) => {
    const displayDate = formatSydneyDate(value)
    if (displayDate === '--') {
      return '--'
    }

    return <Tag color={getSydneyDateTagColor(value)}>{displayDate}</Tag>
  }

  const containerColumns: ColumnsType<ContainerRow> = [
    {
      title: (
        <span style={{ display: 'inline-flex', alignItems: 'center', gap: 2 }}>
          {t('containers.fields.containerNumber')}
          <ContainerNumberRevealButton />
        </span>
      ),
      dataIndex: '货柜编号',
      width: 160,
      render: (value?: string) => (value ? formatContainerNumber(value) : '--'),
    },
    {
      title: t('containers.fields.estimatedArrivalDate'),
      dataIndex: '预计到岸日期',
      width: 140,
      render: renderSydneyDateTag,
    },
    {
      title: t('containers.fields.totalPieces'),
      dataIndex: '合计件数',
      width: 100,
      render: (value?: number) => value ?? 0,
    },
    {
      title: t('column.totalQuantity'),
      dataIndex: '合计数量',
      width: 100,
      render: (value?: number) => value ?? 0,
    },
  ]

  const productColumns: ColumnsType<ProductRow> = [
    {
      title: t('column.image'),
      width: 72,
      render: (_, record) =>
        record.商品信息?.商品图片 ? (
          <Image
            src={record.商品信息.商品图片}
            alt={record.商品信息?.商品名称}
            width={48}
            height={48}
            style={{ borderRadius: 4, objectFit: 'cover' }}
            fallback="data:image/gif;base64,R0lGODlhAQABAAD/ACwAAAAAAQABAAACADs="
          />
        ) : (
          '--'
        ),
    },
    {
      title: t('column.itemNumber'),
      dataIndex: '商品信息',
      width: 120,
      sorter: true,
      sortOrder: productSortOrder,
      render: (_, record) => record.商品信息?.货号 || '--',
    },
    {
      title: t('column.productName'),
      ellipsis: true,
      render: (_, record) => record.商品信息?.商品名称 || '--',
    },
    {
      title: t('column.importPrice'),
      dataIndex: '进口价格',
      width: 90,
      render: (value?: number) => (value === undefined || value === null ? '--' : value.toFixed(2)),
    },
    {
      title: t('column.retailPrice'),
      dataIndex: '零售价格',
      width: 90,
      // 零售价只展示商品主数据，加入订单时不写入订单明细。
      render: (_, record) => {
        const value = record.商品信息?.零售价格
        return value === undefined || value === null ? '--' : Number(value).toFixed(2)
      },
    },
    {
      title: t('column.containerQty'),
      dataIndex: '装柜数量',
      width: 100,
      render: (value?: number) => value ?? 0,
    },
    {
      title: t('column.shipQty'),
      width: 120,
      render: (_, record) => {
        const productCode = record.商品编码 || record.商品信息?.商品编码
        const disabled = productCode ? alreadySelectedCodes.includes(productCode) : false

        return (
          <InputNumber
            min={0}
            precision={0}
            disabled={disabled}
            placeholder={t('common.enter')}
            style={{ width: '100%' }}
            value={productQuantities[String(record.key)]}
            onChange={(value) =>
              setProductQuantities((current) => ({
                ...current,
                [String(record.key)]: value === null ? undefined : Number(value),
              }))
            }
          />
        )
      },
    },
    {
      title: t('column.status'),
      width: 90,
      render: (_, record) => {
        const productCode = record.商品编码 || record.商品信息?.商品编码
        const disabled = productCode ? alreadySelectedCodes.includes(productCode) : false
        return disabled ? <Tag color="warning">{t('storeOrders.containerPickerAlreadyInOrder')}</Tag> : <Tag color="success">{t('storeOrders.containerPickerCanAdd')}</Tag>
      },
    },
  ]

  return (
    <Modal
      title={t('storeOrders.containerPickerTitle')}
      open={open}
      width={1000}
      destroyOnHidden
      onCancel={onClose}
      footer={null}
    >
      <Steps
        current={step}
        style={{ marginBottom: 16 }}
        items={[{ title: t('storeOrders.selectContainer') }, { title: t('storeOrders.selectProducts') }]}
      />

      {step === 0 ? (
        <Space direction="vertical" size={12} style={{ width: '100%' }}>
          <Input
            allowClear
            value={containerKeyword}
            placeholder={t('storeOrders.containerPickerSearchContainer')}
            onChange={(event) => setContainerKeyword(event.target.value)}
          />
          <MeasuredTable<ContainerRow> metricId="warehouse.store-orders.container-product-picker.table-1"
            rowKey="key"
            loading={fetching}
            dataSource={filteredContainers}
            columns={containerColumns}
            pagination={false}
            scroll={{ y: 420 }}
            onRow={(record) => ({
              onClick: () => void handleSelectContainer(record),
              style: { cursor: 'pointer' },
            })}
          />
        </Space>
      ) : (
        <Space direction="vertical" size={12} style={{ width: '100%' }}>
          <Space wrap style={{ justifyContent: 'space-between', width: '100%' }}>
            <Space wrap>
              <Tag color="blue">
                {selectedContainer?.货柜编号
                  ? formatContainerNumber(selectedContainer.货柜编号)
                  : t('storeOrders.containerNotSelected')}
              </Tag>
              <Typography.Text type="secondary">{t('storeOrders.containerPickerQuantityHint')}</Typography.Text>
            </Space>
            <Button icon={<ArrowLeftOutlined />} onClick={() => setStep(0)}>
              {t('storeOrders.backToContainer')}
            </Button>
          </Space>

          <Input
            allowClear
            value={productKeyword}
            placeholder={t('storeOrders.containerPickerSearchProduct')}
            onChange={(event) => setProductKeyword(event.target.value)}
          />

          <MeasuredTable<ProductRow> metricId="warehouse.store-orders.container-product-picker.table-2"
            rowKey="key"
            loading={fetching}
            dataSource={pagedProducts}
            columns={productColumns}
            pagination={{
              current: productPage,
              pageSize: productPageSize,
              total: filteredProducts.length,
              showSizeChanger: true,
              pageSizeOptions: ['20', '50', '100', '500'],
              onChange: (page, pageSize) => {
                setProductPage(page)
                setProductPageSize(pageSize)
              },
            }}
            scroll={{ y: 420 }}
            onChange={(_, __, sorter) => {
              const nextSorter = Array.isArray(sorter) ? sorter[0] : sorter
              setProductSortOrder(nextSorter?.order === 'descend' ? 'descend' : 'ascend')
            }}
            rowSelection={{
              selectedRowKeys,
              onChange: (keys) => setSelectedRowKeys(keys),
              preserveSelectedRowKeys: true,
              getCheckboxProps: (record) => {
                const productCode = record.商品编码 || record.商品信息?.商品编码
                return {
                  disabled: productCode ? alreadySelectedCodes.includes(productCode) : true,
                }
              },
            }}
          />

          <div style={{ display: 'flex', justifyContent: 'flex-end' }}>
            <Space>
              <Button onClick={onClose}>{t('common.cancel')}</Button>
              <Button type="primary" loading={loading || submitting} onClick={() => void handleConfirm()}>
                {t('storeOrders.containerPickerAddSelected', { count: selectedRowKeys.length })}
              </Button>
            </Space>
          </div>
        </Space>
      )}
    </Modal>
  )
}
