import { CopyOutlined } from '@ant-design/icons'
import { Button, Spin, Tooltip, message } from 'antd'
import type { ColumnsType } from 'antd/es/table'
import { useEffect, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { MeasuredTable } from '../../../components/MeasuredTable'
import { getProductsByPrefix } from '../../../services/productPrefixCodeService'
import { ProductType } from '../../../types/domesticProduct'
import type { PrefixCodeProductItem } from '../../../types/productPrefixCode'
import { copyTextToClipboard } from '../../../utils/clipboard'
import { createLatestRequestGuard, runLatestGuardedRequest } from '../../../utils/latestRequestGuard'
import { formatPrefixProductPrice } from './prefixListLogic'
import './prefixCode.css'

const PRODUCTS_PAGE_SIZE = 10

interface PrefixProductsPanelProps {
  prefixCode: string
  prefixName: string
}

interface PanelState {
  products: PrefixCodeProductItem[]
  total: number
  page: number
  pageSize: number
  loading: boolean
  failed: boolean
}

/** 货号 / 条码单元格：等宽文本 + 复制按钮，方便直接粘到别处核对。 */
function CopyableText({ value }: { value?: string }) {
  const { t } = useTranslation()
  if (!value) {
    return <span className="prefix-code-faint">--</span>
  }

  return (
    <span className="prefix-code-copyable">
      <span className="prefix-code-mono">{value}</span>
      <Tooltip title={t('common.copy')}>
        <Button
          type="text"
          size="small"
          className="prefix-code-copy-btn"
          icon={<CopyOutlined />}
          aria-label={t('common.copy')}
          onClick={() =>
            void copyTextToClipboard(value, {
              successMessage: t('common.copySuccess'),
              failureMessage: t('common.copyFailed'),
            })
          }
        />
      </Tooltip>
    </span>
  )
}

function ProductTypeTag({ value }: { value: ProductType }) {
  const { t } = useTranslation()
  if (value === ProductType.SET) {
    return <span className="prefix-code-type prefix-code-type-set">{t('prefixCode.typeSet')}</span>
  }
  if (value === ProductType.MULTICODE) {
    return <span className="prefix-code-type prefix-code-type-multi">{t('prefixCode.typeMulti')}</span>
  }
  return <span className="prefix-code-type">{t('prefixCode.typeNormal')}</span>
}

/**
 * 展开行：某个前缀下的国内商品。
 * 每个前缀对应一个独立的面板实例，各自持有分页状态和「最新请求」守卫：
 * - 同一前缀快速翻页时，旧页响应不能覆盖新页；
 * - 不同前缀互不影响；
 * - 收起 / 被主列表换页卸载时守卫作废，晚到的响应不会再写已卸载的面板。
 */
export default function PrefixProductsPanel({ prefixCode, prefixName }: PrefixProductsPanelProps) {
  const { t } = useTranslation()
  const [state, setState] = useState<PanelState>({
    products: [],
    total: 0,
    page: 1,
    pageSize: PRODUCTS_PAGE_SIZE,
    loading: true,
    failed: false,
  })
  const requestGuardRef = useRef(createLatestRequestGuard())

  const loadProducts = (nextPage: number, nextPageSize: number) =>
    runLatestGuardedRequest(requestGuardRef.current, () => getProductsByPrefix(prefixCode, { page: nextPage, pageSize: nextPageSize }), {
      onStart: () => setState((current) => ({ ...current, loading: true, failed: false, page: nextPage, pageSize: nextPageSize })),
      onSuccess: (result) =>
        setState({
          products: result.items,
          total: result.total,
          page: nextPage,
          pageSize: nextPageSize,
          loading: false,
          failed: false,
        }),
      onError: (error) => {
        console.error(error)
        message.error(error instanceof Error && error.message ? error.message : t('prefixCode.loadProductsFailed'))
        setState((current) => ({ ...current, failed: true }))
      },
      // 旧请求结束时不能关闭较新请求的 loading（guard 只放行最新一次的 onSettled）。
      onSettled: () => setState((current) => ({ ...current, loading: false })),
    })

  useEffect(() => {
    void loadProducts(1, PRODUCTS_PAGE_SIZE)
    return () => requestGuardRef.current.invalidate()
    // 面板以 prefixCode 为 key 挂载，前缀不会在面板生命周期内变化。
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [prefixCode])

  const columns: ColumnsType<PrefixCodeProductItem> = [
    {
      title: t('prefixCode.productHbNo'),
      dataIndex: 'hbProductNo',
      width: 168,
      render: (value?: string) => <CopyableText value={value} />,
    },
    {
      title: t('domesticProducts.productName'),
      dataIndex: 'productName',
      render: (value?: string) => (value ? <span className="prefix-code-ellipsis" title={value}>{value}</span> : <span className="prefix-code-faint">--</span>),
    },
    {
      title: t('domesticProducts.barcode'),
      dataIndex: 'barcode',
      width: 190,
      render: (value?: string) => <CopyableText value={value} />,
    },
    {
      title: t('prefixCode.productType'),
      dataIndex: 'productType',
      width: 84,
      render: (value: ProductType) => <ProductTypeTag value={value} />,
    },
    {
      title: t('domesticProducts.domesticPrice'),
      dataIndex: 'domesticPrice',
      width: 104,
      align: 'right',
      render: (value?: number) => formatPrefixProductPrice(value),
    },
    {
      title: t('domesticProducts.status'),
      dataIndex: 'isActive',
      width: 84,
      render: (value: boolean) => (
        <span className={value ? 'prefix-code-status prefix-code-status-on' : 'prefix-code-status'}>
          {value ? t('common.active') : t('common.inactive')}
        </span>
      ),
    },
  ]

  return (
    <div data-testid="prefix-code-products-panel">
      <div className="prefix-code-products-title">
        {state.failed ? (
          <>
            <span>{t('prefixCode.loadProductsFailed')}</span>
            <Button type="link" size="small" onClick={() => void loadProducts(state.page, state.pageSize)}>
              {t('common.retry')}
            </Button>
          </>
        ) : (
          <span>{t('prefixCode.productsSummary', { prefix: prefixName, total: state.total })}</span>
        )}
      </div>
      <Spin spinning={state.loading}>
        <div className="prefix-code-products-table">
          <MeasuredTable
            metricId="domestic-purchase.product-prefix-code-management.table-1"
            rowKey="productCode"
            size="small"
            tableLayout="fixed"
            columns={columns}
            dataSource={state.products}
            locale={{ emptyText: state.loading ? ' ' : t('prefixCode.productsEmpty') }}
            pagination={{
              current: state.page,
              pageSize: state.pageSize,
              total: state.total,
              size: 'small',
              showSizeChanger: true,
              hideOnSinglePage: true,
              onChange: (nextPage, nextPageSize) => {
                // 改每页条数时回到第一页，避免页码越界。
                void loadProducts(nextPageSize === state.pageSize ? nextPage : 1, nextPageSize)
              },
            }}
          />
        </div>
      </Spin>
    </div>
  )
}
