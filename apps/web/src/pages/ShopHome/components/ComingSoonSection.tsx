import { CalendarOutlined, ReloadOutlined } from '@ant-design/icons'
import { Alert, Button, Empty, Image, Segmented, Select, Skeleton, Spin, Switch, Tag, Typography } from 'antd'
import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'

import BarcodePreview from '../../../components/BarcodePreview'
import { ContainerNumberRevealButton, useContainerNumberDisplay } from '../../../components/ContainerNumber'
import {
  getComingSoonContainerProducts,
  getComingSoonContainerSummaries,
} from '../../../services/containerService'
import type { ComingSoonHomeContainerSummary, ComingSoonHomeProduct } from '../../../types/container'

import styles from './ComingSoonSection.module.css'
import { getComingSoonDateTone } from './comingSoonWarehouseDate'

const { Text, Title } = Typography
// 每页商品数：默认 50，可选 100 / 200。最大 200 是单页 DOM 的硬上限（卡片带图片与条码画布），不要再加更大的档位。
const PRODUCT_PAGE_SIZE_OPTIONS = [50, 100, 200] as const
const DEFAULT_PRODUCT_PAGE_SIZE: number = PRODUCT_PAGE_SIZE_OPTIONS[0]
const PRODUCT_IMAGE_FALLBACK = `data:image/svg+xml;charset=UTF-8,${encodeURIComponent('<svg xmlns="http://www.w3.org/2000/svg" width="160" height="120" viewBox="0 0 160 120"><rect width="160" height="120" fill="#f5f7fa"/><path d="M52 82l23-27 15 18 12-14 17 23z" fill="#cbd5e1"/><circle cx="66" cy="40" r="8" fill="#94a3b8"/></svg>')}`
const BARCODE_OPTIONS = { width: 1, height: 34, displayValue: false, margin: 0 }
type FilterMode = 'all' | 'reorder' | 'new'
type ProductLoadStatus = 'idle' | 'loading' | 'loaded' | 'error'
interface ContainerProductState {
  status: ProductLoadStatus
  products: ComingSoonHomeProduct[]
  error?: string
}

function formatDate(value?: string) {
  if (!value) return '-'
  const date = new Date(value)
  return Number.isNaN(date.getTime())
    ? value
    : date.toLocaleDateString('en-AU', {
        month: 'short',
        day: 'numeric',
        year: 'numeric',
      })
}
function formatComingSoonRetailPrice(price?: number) {
  if (typeof price !== 'number' || Number.isNaN(price)) return ''
  return new Intl.NumberFormat('en-AU', {
    style: 'currency',
    currency: 'AUD',
    minimumFractionDigits: 2,
  }).format(price)
}
function matchesFilter(product: ComingSoonHomeProduct, mode: FilterMode) {
  return mode === 'all' || (mode === 'new' ? product.isNewProduct : !product.isNewProduct)
}
function getContainerFilterStats(products: ComingSoonHomeProduct[]) {
  return products.reduce(
    (stats, product) => {
      stats.all += 1
      if (product.isNewProduct) stats.new += 1
      else stats.reorder += 1
      return stats
    },
    { all: 0, reorder: 0, new: 0 },
  )
}
function getContainerProductState(
  states: Record<string, ContainerProductState>,
  guid: string,
): ContainerProductState {
  return states[guid] ?? { status: 'idle', products: [] }
}

export default function ComingSoonSection() {
  const { t } = useTranslation()
  // 货柜编号默认只显示后 4 位（订货前台账号后端已遮罩）；有权限的账号点标题旁眼睛按钮显示完整编号。
  const { format: formatContainerNumber } = useContainerNumberDisplay()
  const panelRef = useRef<HTMLElement | null>(null)
  const [containers, setContainers] = useState<ComingSoonHomeContainerSummary[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [filterMode, setFilterMode] = useState<FilterMode>('all')
  const [containerFilterModes, setContainerFilterModes] = useState<Record<string, FilterMode>>({})
  const [productStates, setProductStates] = useState<Record<string, ContainerProductState>>({})
  const [selectedContainerGuid, setSelectedContainerGuid] = useState<string | null>(null)
  // 条码默认显示（订货时要核对条码），仍可用右上角开关临时关闭。
  const [showBarcode, setShowBarcode] = useState(true)
  const loadStartedRef = useRef<Set<string>>(new Set())
  const requestTokensRef = useRef<Map<string, number>>(new Map())
  const summaryGenerationRef = useRef(0)

  const loadContainerProducts = useCallback(async (containerGuid: string) => {
    if (loadStartedRef.current.has(containerGuid)) return
    loadStartedRef.current.add(containerGuid)
    const token = (requestTokensRef.current.get(containerGuid) ?? 0) + 1
    requestTokensRef.current.set(containerGuid, token)
    const generation = summaryGenerationRef.current
    setProductStates((prev) => ({
      ...prev,
      [containerGuid]: {
        status: 'loading',
        products: prev[containerGuid]?.products ?? [],
      },
    }))
    try {
      const products = await getComingSoonContainerProducts(containerGuid)
      if (
        summaryGenerationRef.current !== generation ||
        requestTokensRef.current.get(containerGuid) !== token
      )
        return
      setProductStates((prev) => ({
        ...prev,
        [containerGuid]: { status: 'loaded', products },
      }))
    } catch {
      if (
        summaryGenerationRef.current !== generation ||
        requestTokensRef.current.get(containerGuid) !== token
      )
        return
      loadStartedRef.current.delete(containerGuid)
      setProductStates((prev) => ({
        ...prev,
        [containerGuid]: {
          status: 'error',
          products: prev[containerGuid]?.products ?? [],
          error: 'shop.comingSoonWorkspace.productsFailed',
        },
      }))
    }
  }, [])

  useEffect(() => {
    let cancelled = false
    const fetchSummaries = async () => {
      setLoading(true)
      setError(null)
      try {
        const result = await getComingSoonContainerSummaries()
        if (cancelled) return
        summaryGenerationRef.current += 1
        setContainers(result)
        setProductStates({})
        setContainerFilterModes({})
        loadStartedRef.current.clear()
        setSelectedContainerGuid((current) =>
          current && result.some((item) => item.hguid === current) ? current : (result[0]?.hguid ?? null),
        )
      } catch {
        if (!cancelled) {
          setContainers([])
          setProductStates({})
          setContainerFilterModes({})
          setError('shop.comingSoonWorkspace.loadFailed')
        }
      } finally {
        if (!cancelled) setLoading(false)
      }
    }
    void fetchSummaries()
    return () => {
      cancelled = true
    }
  }, [])
  // 全局筛选只隐藏已加载且没有匹配商品的货柜；未加载货柜继续保留，避免把未知状态误判为空。
  const visibleContainers = useMemo(
    () =>
      containers.filter((container) => {
        if (filterMode === 'all') return true
        const state = getContainerProductState(productStates, container.hguid)
        return (
          state.status !== 'loaded' || state.products.some((product) => matchesFilter(product, filterMode))
        )
      }),
    [containers, filterMode, productStates],
  )
  const selectedContainer =
    visibleContainers.find((container) => container.hguid === selectedContainerGuid) ?? visibleContainers[0]
  const selectedState = selectedContainer
    ? getContainerProductState(productStates, selectedContainer.hguid)
    : null
  const selectedFilterMode = selectedContainer
    ? (containerFilterModes[selectedContainer.hguid] ?? filterMode)
    : filterMode
  const selectedProducts =
    selectedState?.products.filter((product) => matchesFilter(product, selectedFilterMode)) ?? []
  const [productPage, setProductPage] = useState(1)
  // 每页条数对所有货柜通用；切换货柜 / 筛选只回到第 1 页，不重置条数。
  const [pageSize, setPageSize] = useState<number>(DEFAULT_PRODUCT_PAGE_SIZE)
  const pageCount = Math.max(1, Math.ceil(selectedProducts.length / pageSize))
  const pagedProducts = selectedProducts.slice((productPage - 1) * pageSize, productPage * pageSize)
  useEffect(() => {
    if (selectedContainer) void loadContainerProducts(selectedContainer.hguid)
  }, [loadContainerProducts, selectedContainer])
  useEffect(() => {
    setProductPage(1)
  }, [selectedContainer?.hguid, selectedFilterMode])
  const stats = useMemo(
    () =>
      containers.reduce(
        (acc, container) => {
          const state = getContainerProductState(productStates, container.hguid)
          acc.totalContainers += 1
          if (container.实际到货日期) acc.arrived += 1
          else acc.incoming += 1
          if (state.status === 'loaded')
            acc.loadedNew += state.products.filter((item) => item.isNewProduct).length
          return acc
        },
        { totalContainers: 0, incoming: 0, arrived: 0, loadedNew: 0 },
      ),
    [containers, productStates],
  )
  const changeGlobalFilter = (mode: FilterMode) => {
    setFilterMode(mode)
    setContainerFilterModes({})
    setProductPage(1)
  }
  const changeContainerFilter = (mode: FilterMode) => {
    if (selectedContainer) {
      setContainerFilterModes((prev) => ({
        ...prev,
        [selectedContainer.hguid]: mode,
      }))
      setProductPage(1)
    }
  }

  const filterOptions = (['all', 'reorder', 'new'] as FilterMode[]).map((mode) => ({
    value: mode,
    label: t(
      mode === 'all'
        ? 'shop.comingSoonFilterAll'
        : mode === 'reorder'
          ? 'shop.comingSoonFilterReorder'
          : 'shop.comingSoonFilterNew',
    ),
  }))
  const selectedFilterStats = getContainerFilterStats(selectedState?.products ?? [])
  const scrollToProducts = () => panelRef.current?.scrollIntoView({ block: 'start', behavior: 'auto' })
  const changeProductPage = (page: number) => {
    setProductPage(page)
    // 商品分页替代原卡片内滚动；翻页回到当前货柜顶部，避免停在长页底部。
    scrollToProducts()
  }
  const changePageSize = (size: number) => {
    // 条数变了，原页码对应的商品范围就不成立了，统一回到第 1 页并回到列表顶部。
    setPageSize(size)
    setProductPage(1)
    scrollToProducts()
  }
  const pageSizeOptions = PRODUCT_PAGE_SIZE_OPTIONS.map((size) => ({
    value: size,
    label: t('shop.comingSoonWorkspace.perPage', { count: size }),
  }))

  // 分页条在商品网格上下各放一份（上方免去翻到底部才能翻页）；两份共用同一套状态，行为完全一致。
  const renderPagination = (position: 'top' | 'bottom') => (
    <div
          className={`${styles.pagination} ${position === 'top' ? styles.paginationTop : ''}`}
          aria-label={t('shop.comingSoonWorkspace.pages')}
        >
      <span>
        {t('shop.comingSoonWorkspace.pageInfo', {
          from: (productPage - 1) * pageSize + 1,
          to: Math.min(productPage * pageSize, selectedProducts.length),
          total: selectedProducts.length,
        })}
      </span>
      <div>
        <Select<number>
          size="small"
          className={styles.pageSizeSelect}
          value={pageSize}
          options={pageSizeOptions}
          onChange={changePageSize}
          aria-label={t('shop.comingSoonWorkspace.pageSize')}
        />
        <Button disabled={productPage <= 1} onClick={() => changeProductPage(productPage - 1)}>
          {t('shop.comingSoonWorkspace.previous')}
        </Button>
        <span aria-live="polite">
          {t('shop.comingSoonWorkspace.page', { page: productPage, pages: pageCount })}
        </span>
        <Button
          disabled={productPage >= pageCount}
          onClick={() => changeProductPage(productPage + 1)}
        >
          {t('shop.comingSoonWorkspace.next')}
        </Button>
      </div>
    </div>
  )

  return (
    <section className={styles.section}>
      <div className={styles.header}>
        <div>
          <Title level={2} className={styles.title}>
            {t('shop.comingSoon')}
          </Title>
          <Text className={styles.subtitle}>{t('shop.comingSoonBannerSubtitle')}</Text>
        </div>
        <div className={styles.summary}>
          <span>
            <strong>{stats.totalContainers}</strong>
            {t('shop.comingSoonWorkspace.containers')}
          </span>
          <span>
            <strong>{stats.incoming}</strong>
            {t('shop.comingSoonWorkspace.incoming')}
          </span>
          <span>
            <strong>{stats.arrived}</strong>
            {t('shop.comingSoonWorkspace.arrived')}
          </span>
          <span>
            <strong>{stats.loadedNew}</strong>
            {t('shop.comingSoonWorkspace.newProducts')}
          </span>
        </div>
      </div>
      {error ? <Alert type="error" showIcon message={t(error)} /> : null}
      <div className={styles.globalFilter}>
        <span>{t('shop.comingSoonWorkspace.globalFilter')}</span>
        <Segmented<FilterMode>
          aria-label={t('shop.comingSoonWorkspace.globalFilter')}
          value={filterMode}
          options={filterOptions}
          onChange={changeGlobalFilter}
        />
      </div>
      {loading && !containers.length ? (
        <div className={styles.loading}>
          <Spin size="large" />
        </div>
      ) : visibleContainers.length ? (
        <div className={styles.workspace}>
          <nav className={styles.index} aria-label={t('shop.comingSoonWorkspace.index')}>
            <div className={styles.indexLabel}>
              {t('shop.comingSoonWorkspace.index')}
              <span>
                {visibleContainers.length}/{containers.length}
              </span>
            </div>
            {visibleContainers.map((container) => {
              const arrived = !!container.实际到货日期
              const tone = getComingSoonDateTone(container)
              const active = selectedContainer?.hguid === container.hguid
              const state = getContainerProductState(productStates, container.hguid)
              return (
                <button
                  type="button"
                  key={container.hguid}
                  className={`${styles.indexItem} ${active ? styles.indexItemActive : ''}`}
                  onClick={() => {
                    setSelectedContainerGuid(container.hguid)
                    setProductPage(1)
                  }}
                  aria-current={active ? 'true' : undefined}
                >
                  <span className={styles.indexMain}>
                    <strong>{container.货柜编号 ? formatContainerNumber(container.货柜编号) : 'N/A'}</strong>
                    <span>
                      <i className={`${styles.dot} ${styles[`dot_${tone}`]}`} />
                      {t(
                        arrived ? 'shop.comingSoonWorkspace.arrived' : 'shop.comingSoonWorkspace.incoming',
                      )}{' '}
                      · {formatDate(arrived ? container.实际到货日期 : container.预计到岸日期)}
                    </span>
                    <span>
                      {state.status === 'loaded'
                        ? t('shop.comingSoonWorkspace.items', { count: state.products.length })
                        : t('shop.comingSoonWorkspace.pending')}
                    </span>
                  </span>
                </button>
              )
            })}
          </nav>
          {selectedContainer ? (
            <section
              ref={panelRef}
              className={styles.panel}
              aria-label={selectedContainer.货柜编号 ? formatContainerNumber(selectedContainer.货柜编号) : 'N/A'}
              aria-busy={selectedState?.status === 'loading'}
            >
              <div className={styles.panelHead}>
                <div className={styles.panelTitle}>
                  <Title level={3}>
                    {selectedContainer.货柜编号 ? formatContainerNumber(selectedContainer.货柜编号) : 'N/A'}
                  </Title>
                  {selectedContainer.货柜编号 ? <ContainerNumberRevealButton /> : null}
                  <Tag color={selectedContainer.实际到货日期 ? 'success' : 'processing'}>
                    {t(
                      selectedContainer.实际到货日期
                        ? 'shop.comingSoonWorkspace.arrived'
                        : 'shop.comingSoonWorkspace.incoming',
                    )}
                  </Tag>
                </div>
                <div
                  className={`${styles.date} ${styles[`date_${getComingSoonDateTone(selectedContainer)}`]}`}
                >
                  <CalendarOutlined />{' '}
                  {t(
                    selectedContainer.实际到货日期
                      ? 'shop.comingSoonWorkspace.arrivalDate'
                      : 'shop.comingSoonWorkspace.eta',
                  )}{' '}
                  {formatDate(selectedContainer.实际到货日期 || selectedContainer.预计到岸日期)}
                </div>
              </div>
              <div className={styles.panelMeta}>
                <Segmented<FilterMode>
                  aria-label={t('shop.comingSoonWorkspace.containerFilter')}
                  value={selectedFilterMode}
                  options={filterOptions.map((option) => ({
                    ...option,
                    label: `${option.label} ${selectedState?.status === 'loaded' ? selectedFilterStats[option.value] : '—'}`,
                  }))}
                  onChange={changeContainerFilter}
                />
                <label className={styles.barcodeToggle}>
                  <Switch
                    checked={showBarcode}
                    onChange={setShowBarcode}
                    aria-label={t('shop.comingSoonWorkspace.showBarcodes')}
                    size="small"
                  />
                  {t('shop.barcode')}
                </label>
              </div>
              {selectedState?.status === 'loading' || selectedState?.status === 'idle' ? (
                <div className={styles.productLoading}>
                  <Skeleton active paragraph={{ rows: 5 }} />
                </div>
              ) : selectedState?.status === 'error' ? (
                <Alert
                  type="warning"
                  showIcon
                  message={t(selectedState.error || 'shop.comingSoonWorkspace.productsFailed')}
                  action={
                    <Button
                      size="small"
                      icon={<ReloadOutlined />}
                      onClick={() => void loadContainerProducts(selectedContainer.hguid)}
                    >
                      {t('common.retry')}
                    </Button>
                  }
                />
              ) : selectedProducts.length ? (
                <>
                  {renderPagination('top')}
                  <div className={styles.productGrid}>
                    {pagedProducts.map((product) => (
                      <article
                        className={styles.product}
                        key={`${selectedContainer.hguid}-${product.id}-${product.productCode || product.itemNumber}`}
                      >
                        <div className={styles.imageWrap}>
                          <Image
                            src={product.productImage || PRODUCT_IMAGE_FALLBACK}
                            fallback={PRODUCT_IMAGE_FALLBACK}
                            alt={
                              product.productName ||
                              product.englishName ||
                              t('shop.comingSoonWorkspace.unknownProduct')
                            }
                            preview={false}
                            loading="lazy"
                          />
                          <Tag
                            className={styles.productTag}
                            color={product.isNewProduct ? 'magenta' : 'default'}
                          >
                            {t(
                              product.isNewProduct
                                ? 'shop.comingSoonFilterNew'
                                : 'shop.comingSoonFilterReorder',
                            )}
                          </Tag>
                        </div>
                        <div className={styles.productBody}>
                          <div className={styles.productName}>
                            {product.productName ||
                              product.englishName ||
                              t('shop.comingSoonWorkspace.unknownProduct')}
                          </div>
                          <div className={styles.itemRow}>
                            <Text copyable className={styles.itemNumber}>
                              {product.itemNumber || '-'}
                            </Text>
                          </div>
                          <div className={styles.productDetail}>
                            <span>{t('shop.rrp', 'RRP')}</span>
                            <strong>{formatComingSoonRetailPrice(product.retailPrice)}</strong>
                          </div>
                          {showBarcode ? (
                            <div className={styles.barcode}>
                              <BarcodePreview
                                value={product.barcode}
                                options={BARCODE_OPTIONS}
                                showText
                                showCopy={false}
                                textNoWrap
                                align="left"
                              />
                            </div>
                          ) : null}
                        </div>
                      </article>
                    ))}
                  </div>
                  {renderPagination('bottom')}
                </>
              ) : (
                <Empty
                  image={Empty.PRESENTED_IMAGE_SIMPLE}
                  description={t('shop.comingSoonWorkspace.empty')}
                />
              )}
            </section>
          ) : null}
        </div>
      ) : (
        <Empty
          image={Empty.PRESENTED_IMAGE_SIMPLE}
          description={t('shop.comingSoonWorkspace.emptyContainers')}
        />
      )}
    </section>
  )
}
