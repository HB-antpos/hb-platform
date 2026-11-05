import { CloseOutlined, SearchOutlined } from '@ant-design/icons'
import { Drawer } from 'antd'
import { useMemo, useRef, useState, type FC } from 'react'
import type {
  HolidayCategoryDetail,
  HolidayCategoryKey,
  HolidayProduct,
  HolidayProductStore,
  HolidayProductStores,
} from '../../../services/holidaySalesReportService'
import {
  CATEGORY_COLOR,
  CATEGORY_LABEL_KEY,
  DRAWER_PAGE_SIZE,
  filterAndSortProducts,
  formatCount,
  formatMoney,
  formatMonthDay,
  formatPercent,
  formatSellThrough,
  hasNoPurchase,
  nextDrawerCount,
  productFilterCounts,
  sellThroughTone,
  TONE_COLOR,
  type ProductFilter,
  type ProductSort,
} from './logic'
import { ProductImage, supplierTag, type Tr } from './shared'
import styles from './styles.module.css'

const DRAWER_WIDTH = 'min(1120px, 100vw)'
const FILTERS: { id: ProductFilter; labelKey: string }[] = [
  { id: 'all', labelKey: 'drill.filterAll' },
  { id: 'early', labelKey: 'drill.filterEarly' },
  { id: 'slow', labelKey: 'drill.filterSlow' },
  { id: 'over', labelKey: 'drill.filterOver' },
]
const SORTS: { id: ProductSort; labelKey: string }[] = [
  { id: 'amount', labelKey: 'drill.sortAmount' },
  { id: 'sellThrough', labelKey: 'drill.sortSellThrough' },
  { id: 'leftover', labelKey: 'drill.sortLeftover' },
  { id: 'lost', labelKey: 'drill.sortLost' },
]

const SellThroughBar: FC<{ value: number | null }> = ({ value }) => (
  <div className={styles.barCell}>
    <div className={styles.bar}>
      <b style={{ width: `${Math.min(value ?? 0, 1) * 100}%`, background: TONE_COLOR[sellThroughTone(value)] }} />
    </div>
    <span className={styles.barValueSm}>{formatSellThrough(value)}</span>
  </div>
)

const AmountBar: FC<{ value: number; max: number; color: string }> = ({ value, max, color }) => (
  <div className={styles.barCell}>
    <div className={styles.bar}>
      <b style={{ width: `${max > 0 ? (Math.max(value, 0) / max) * 100 : 0}%`, background: color }} />
    </div>
    <span className={styles.barValue}>{formatMoney(value)}</span>
  </div>
)

const Kpi: FC<{ label: string; value: string; sub?: string; tone?: 'bad' | 'warn'; valueColor?: string }> = ({ label, value, sub, tone, valueColor }) => (
  <div className={`${styles.dKpi} ${tone === 'bad' ? styles.dKpiBad : tone === 'warn' ? styles.dKpiWarn : ''}`}>
    <span className={styles.dKpiLabel}>{label}</span>
    <span className={styles.dKpiValue} style={valueColor ? { color: valueColor } : undefined}>{value}</span>
    <span className={styles.dKpiSub}>{sub ?? ' '}</span>
  </div>
)

const CloseButton: FC<{ onClose: () => void; tr: Tr }> = ({ onClose, tr }) => (
  <button type="button" className={styles.dClose} onClick={onClose} aria-label={tr('drill.close')} title={tr('drill.close')}>
    <CloseOutlined />
  </button>
)

/** 卖完日：节前提前卖完是红色标签，节日当天才卖完是橙色标签，没有卖断是「—」。 */
const SoldOutChip: FC<{ store: HolidayProductStore; tr: Tr }> = ({ store, tr }) => {
  if (store.soldOutDate === null) return <span className={styles.hint}>—</span>
  const date = formatMonthDay(store.soldOutDate)
  return store.daysEarly !== null && store.daysEarly > 0 ? (
    <span className={`${styles.chipTag} ${styles.chipBad}`}>{tr('drill.soldOutEarly', { date, days: store.daysEarly })}</span>
  ) : (
    <span className={`${styles.chipTag} ${styles.chipWarn}`}>{tr('drill.soldOutOnDay', { date })}</span>
  )
}

// ---------- 商品钻取：各分店的进货 / 售出 / 卖断 / 剩余 ----------

export const ProductStoresDrawer: FC<{
  open: boolean
  onClose: () => void
  product: HolidayProduct | null
  data: HolidayProductStores | undefined
  loading: boolean
  error?: string
  /** 单店视图里当前看的分店，在表里高亮。 */
  currentStore: string | null
  tr: Tr
}> = ({ open, onClose, product, data, loading, error, currentStore, tr }) => {
  const name = data?.productName ?? product?.productName ?? ''
  const tag = supplierTag(data?.supplierCode ?? product?.supplierCode ?? '')
  const categoryKey = data?.categoryKey ?? product?.categoryKey
  const maxAmount = data ? data.stores.reduce((current, store) => Math.max(current, store.amount), 0) : 0
  const total = data?.total

  return (
    <Drawer open={open} onClose={onClose} width={DRAWER_WIDTH} closable={false} destroyOnHidden styles={{ body: { padding: 0 } }} aria-label={tr('drill.title', { name })}>
      <div className={styles.drawerScope}>
      <div className={styles.dHeader}>
        <ProductImage src={data?.imageUrl ?? product?.imageUrl ?? null} label={tr('product.noImage')} className={styles.dThumb} />
        <div className={styles.dIdentity}>
          <div className={styles.dTitle}>{name}</div>
          <div className={styles.dMeta}>
            <i className={`${styles.tag} ${tag.className}`}>{tag.label}</i>
            {categoryKey && (
              <span className={styles.catTag} style={{ color: CATEGORY_COLOR[categoryKey], borderColor: `${CATEGORY_COLOR[categoryKey]}66` }}>
                {tr(CATEGORY_LABEL_KEY[categoryKey])}
              </span>
            )}
            <span>{data?.itemNumber ?? product?.itemNumber}</span>
            <span>· {tr('drill.storeData')}</span>
          </div>
        </div>
        <CloseButton onClose={onClose} tr={tr} />
      </div>

      {loading && <div className={styles.empty}>{tr('state.loading')}</div>}
      {error && !loading && <div className={styles.errorBox} role="alert">{tr('state.error', { message: error })}</div>}

      {data && total && (
        <>
          <div className={styles.dKpis}>
            <Kpi label={tr('kpi.amount')} value={formatMoney(total.amount)} sub={tr('drill.kpiStoresSold', { count: data.stores.filter((store) => store.amount > 0).length })} />
            <Kpi label={tr('drill.kpiSoldBought')} value={`${formatCount(total.quantity)} / ${formatCount(total.purchaseQuantity)}`} sub={tr('drill.units')} />
            <Kpi label={tr('cat.colSellThrough')} value={formatSellThrough(total.sellThrough)} valueColor={TONE_COLOR[sellThroughTone(total.sellThrough)]} sub={tr('drill.kpiStoresStocked', { count: data.storeCount })} />
            <Kpi label={tr('drill.kpiSoldOutStores')} value={tr('drill.storeCount', { count: data.soldOutStoreCount })} sub={tr('product.lost', { amount: formatMoney(total.estimatedLostAmount) })} tone="bad" />
            <Kpi label={tr('kpi.left')} value={tr('kpi.leftValue', { qty: formatCount(total.leftoverQuantity) })} sub={tr('kpi.leftSub', { cost: formatMoney(total.leftoverCost) })} tone="warn" />
          </div>
          {data.stores.length === 0 ? (
            <div className={styles.empty}>{tr('drill.empty')}</div>
          ) : (
            <div className={styles.dBody}>
              <div className={styles.tableScroll}>
                <table className={styles.dTable}>
                  <thead>
                    <tr>
                      <th>{tr('drill.colStore')}</th>
                      <th className={styles.wide}>{tr('drill.colAmount')}</th>
                      <th className={styles.num}>{tr('drill.colQuantity')}</th>
                      <th className={styles.num}>{tr('drill.colPurchase')}</th>
                      <th className={styles.wide}>{tr('drill.colSellThrough')}</th>
                      <th>{tr('drill.colSoldOut')}</th>
                      <th className={styles.num}>{tr('drill.colLost')}</th>
                      <th className={styles.num}>{tr('drill.colLeft')}</th>
                      <th className={styles.num}>{tr('drill.colFirstSale')}</th>
                    </tr>
                  </thead>
                  <tbody>
                    <tr className={styles.dTotalRow}>
                      <td>{tr('drill.total')}</td>
                      <td>{formatMoney(total.amount)}</td>
                      <td className={styles.num}>{formatCount(total.quantity)}</td>
                      <td className={styles.num}>{formatCount(total.purchaseQuantity)}</td>
                      <td>{formatSellThrough(total.sellThrough)}</td>
                      <td>{tr('drill.soldOutStores', { count: data.soldOutStoreCount })}</td>
                      <td className={styles.num}>{formatMoney(total.estimatedLostAmount)}</td>
                      <td className={styles.num}>{formatCount(total.leftoverQuantity)}</td>
                      <td />
                    </tr>
                    {data.stores.map((store) => (
                      <tr key={store.branchCode} className={store.branchCode === currentStore ? styles.dCurrentRow : undefined}>
                        <td>
                          {store.branchCode} {store.branchName}{' '}
                          {store.branchCode === currentStore && <span className={`${styles.pill} ${styles.pillWarn}`}>{tr('drill.current')}</span>}
                          {store.purchaseQuantity <= 0 && <span className={styles.pill}>{tr('drill.noPurchase')}</span>}
                        </td>
                        <td><AmountBar value={store.amount} max={maxAmount} color="#1677ff" /></td>
                        <td className={styles.num}>{formatCount(store.quantity)}</td>
                        <td className={styles.num}>{formatCount(store.purchaseQuantity)}</td>
                        <td><SellThroughBar value={store.sellThrough} /></td>
                        <td><SoldOutChip store={store} tr={tr} /></td>
                        <td className={`${styles.num} ${store.estimatedLostAmount > 0 ? styles.bad : ''}`}>{formatMoney(store.estimatedLostAmount)}</td>
                        <td className={`${styles.num} ${store.leftoverQuantity > 0 ? styles.warnText : ''}`}>{formatCount(store.leftoverQuantity)}</td>
                        <td className={styles.num}>{store.firstSaleDate ? formatMonthDay(store.firstSaleDate) : '—'}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
              <p className={styles.note}>{tr('drill.soldOutNote')}</p>
            </div>
          )}
        </>
      )}
      </div>
    </Drawer>
  )
}

// ---------- 大类钻取：分店 / 商品两个标签 ----------

type CategoryTab = 'stores' | 'products'

export const CategoryDrawer: FC<{
  open: boolean
  onClose: () => void
  categoryKey: HolidayCategoryKey | null
  data: HolidayCategoryDetail | undefined
  loading: boolean
  error?: string
  /** 页面在单店视图时，抽屉只看这家店：没有「分店」标签。 */
  single: boolean
  /** 合计视图里点了某家分店后，商品清单只看这家店。 */
  storeFilter: { code: string; name: string } | null
  onPickStore: (code: string, name: string) => void
  onClearStore: () => void
  onOpenProduct: (product: HolidayProduct) => void
  tr: Tr
}> = ({ open, onClose, categoryKey, data, loading, error, single, storeFilter, onPickStore, onClearStore, onOpenProduct, tr }) => {
  const [tab, setTab] = useState<CategoryTab>(single ? 'products' : 'stores')
  const [filter, setFilter] = useState<ProductFilter>('all')
  const [sort, setSort] = useState<ProductSort>('amount')
  const [query, setQuery] = useState('')
  const [visible, setVisible] = useState(DRAWER_PAGE_SIZE)
  const activeTab: CategoryTab = single || storeFilter ? 'products' : tab
  // 只看某家店时服务端不返回分店列表，「分店」标签的计数沿用未筛选时的数量，避免显示成 0
  const storeCountRef = useRef(0)
  if (data && !data.branchCode) storeCountRef.current = data.stores.length
  const storeTabCount = data && !data.branchCode ? data.stores.length : storeCountRef.current

  const products = useMemo(() => data?.products ?? [], [data])
  const counts = useMemo(() => productFilterCounts(products), [products])
  const rows = useMemo(() => filterAndSortProducts(products, filter, query, sort), [products, filter, query, sort])
  const color = categoryKey ? CATEGORY_COLOR[categoryKey] : '#8c8c8c'
  const title = categoryKey ? tr(CATEGORY_LABEL_KEY[categoryKey]) : ''
  const category = data?.category
  const maxStore = data ? data.stores.reduce((current, store) => Math.max(current, store.amount), 0) : 0
  const resetPaging = () => setVisible(DRAWER_PAGE_SIZE)

  return (
    <Drawer open={open} onClose={onClose} width={DRAWER_WIDTH} closable={false} destroyOnHidden styles={{ body: { padding: 0 } }} aria-label={tr('drill.catTitle', { name: title })}>
      <div className={styles.drawerScope}>
      <div className={styles.dHeader}>
        <i className={styles.dSwatch} style={{ background: color }} aria-hidden="true" />
        <div className={styles.dIdentity}>
          <div className={styles.dTitle}>{title}</div>
          <div className={styles.dMeta}>
            <span>{tr('drill.catHint', { count: products.length })}</span>
            {data?.branchName && <span>· {data.branchCode} {data.branchName}</span>}
          </div>
        </div>
        <CloseButton onClose={onClose} tr={tr} />
      </div>

      {loading && <div className={styles.empty}>{tr('state.loading')}</div>}
      {error && !loading && <div className={styles.errorBox} role="alert">{tr('state.error', { message: error })}</div>}

      {data && category && (
        <>
          <div className={styles.dKpis}>
            <Kpi label={tr('kpi.amount')} value={formatMoney(category.amount)} sub={tr('kpi.leftValue', { qty: formatCount(category.quantity) })} />
            <Kpi label={tr('drill.kpiSkus')} value={formatCount(category.skuCount)} sub={tr('drill.kpiSkusSub', { count: products.length })} />
            <Kpi label={tr('cat.colSellThrough')} value={formatSellThrough(category.sellThrough)} valueColor={TONE_COLOR[sellThroughTone(category.sellThrough)]} sub={tr('drill.kpiBought', { qty: formatCount(category.purchaseQuantity) })} />
            <Kpi label={tr('kpi.lost')} value={formatMoney(category.estimatedLostAmount)} tone="bad" />
            <Kpi label={tr('kpi.left')} value={tr('kpi.leftValue', { qty: formatCount(category.leftoverQuantity) })} sub={tr('kpi.leftSub', { cost: formatMoney(category.leftoverCost) })} tone="warn" />
          </div>

          {!single && (
            <div className={styles.dTabs} role="tablist">
              <button type="button" role="tab" aria-selected={activeTab === 'stores'} className={activeTab === 'stores' ? styles.dTabOn : styles.dTab} onClick={() => { onClearStore(); setTab('stores') }}>
                {tr('drill.tabStores')}
                <em>{storeTabCount}</em>
              </button>
              <button type="button" role="tab" aria-selected={activeTab === 'products'} className={activeTab === 'products' ? styles.dTabOn : styles.dTab} onClick={() => setTab('products')}>
                {tr('drill.tabProducts')}
                <em>{products.length}</em>
              </button>
            </div>
          )}

          {activeTab === 'stores' && !single && (
            <div className={styles.dBody}>
              <div className={styles.tableScroll}>
                <table className={styles.dTable}>
                  <thead>
                    <tr>
                      <th>#</th>
                      <th>{tr('stores.colStore')}</th>
                      <th className={styles.wide}>{tr('stores.colAmount')}</th>
                      <th className={styles.num}>{tr('cat.colShare')}</th>
                      <th className={styles.num}>{tr('stores.colQuantity')}</th>
                      <th className={styles.num}>{tr('stores.colPurchase')}</th>
                      <th className={styles.wide}>{tr('stores.colSellThrough')}</th>
                      <th className={styles.num}>{tr('stores.colEarly')}</th>
                      <th className={styles.num}>{tr('stores.colLost')}</th>
                      <th className={styles.num}>{tr('stores.colLeft')}</th>
                      <th />
                    </tr>
                  </thead>
                  <tbody>
                    {data.stores.map((store, index) => (
                      <tr
                        key={store.branchCode}
                        className={styles.clickRow}
                        tabIndex={0}
                        onClick={() => { onPickStore(store.branchCode, store.branchName); setTab('products'); resetPaging() }}
                        onKeyDown={(event) => {
                          if (event.key === 'Enter' || event.key === ' ') {
                            event.preventDefault()
                            onPickStore(store.branchCode, store.branchName)
                            setTab('products')
                            resetPaging()
                          }
                        }}
                      >
                        <td className={styles.rank}>{index + 1}</td>
                        <td>
                          {store.branchCode} {store.branchName} {hasNoPurchase(store) && <span className={styles.pill}>{tr('drill.noPurchase')}</span>}
                        </td>
                        <td><AmountBar value={store.amount} max={maxStore} color={color} /></td>
                        <td className={styles.num}>{category.amount > 0 ? formatPercent(store.amount / category.amount) : '—'}</td>
                        <td className={styles.num}>{formatCount(store.quantity)}</td>
                        <td className={styles.num}>{formatCount(store.purchaseQuantity)}</td>
                        <td><SellThroughBar value={store.sellThrough} /></td>
                        <td className={`${styles.num} ${store.earlySoldOutCount > 0 ? styles.bad : ''}`}>{formatCount(store.earlySoldOutCount)}</td>
                        <td className={styles.num}>{formatMoney(store.estimatedLostAmount)}</td>
                        <td className={`${styles.num} ${store.leftoverCost > 2000 ? styles.warnText : ''}`}>{formatMoney(store.leftoverCost)}</td>
                        <td className={styles.openText}>{tr('drill.openProducts')} ›</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
              <p className={styles.note}>{tr('drill.storesNote', { name: title })}</p>
            </div>
          )}

          {activeTab === 'products' && (
            <>
              <div className={styles.dToolbar}>
                <label className={styles.dSearch}>
                  <SearchOutlined aria-hidden="true" />
                  <input
                    type="search"
                    value={query}
                    placeholder={tr('drill.search')}
                    aria-label={tr('drill.search')}
                    onChange={(event) => { setQuery(event.target.value); resetPaging() }}
                  />
                </label>
                {storeFilter && (
                  <button type="button" className={styles.storeChip} onClick={() => { onClearStore(); resetPaging() }} aria-label={tr('drill.clearStore')}>
                    {tr('drill.storeOnly', { store: `${storeFilter.code} ${storeFilter.name}` })} ×
                  </button>
                )}
                <div className={styles.chips} role="group" aria-label={tr('drill.filter')}>
                  {FILTERS.map((option) => (
                    <button
                      key={option.id}
                      type="button"
                      className={filter === option.id ? styles.chipOn : styles.chip}
                      aria-pressed={filter === option.id}
                      onClick={() => { setFilter(option.id); resetPaging() }}
                    >
                      {tr(option.labelKey, { count: counts[option.id] })}
                    </button>
                  ))}
                </div>
                <select className={`${styles.select} ${styles.dSort}`} aria-label={tr('drill.sortLabel')} value={sort} onChange={(event) => { setSort(event.target.value as ProductSort); resetPaging() }}>
                  {SORTS.map((option) => (
                    <option key={option.id} value={option.id}>{tr(option.labelKey)}</option>
                  ))}
                </select>
              </div>
              <div className={styles.dBody}>
                {rows.length === 0 ? (
                  <div className={styles.empty}>{tr('board.empty')}</div>
                ) : (
                  <ol className={styles.dProducts}>
                    {rows.slice(0, visible).map((product, index) => (
                      <CategoryProductRow key={product.productCode} product={product} index={index} single={single || storeFilter !== null} tr={tr} onOpen={onOpenProduct} />
                    ))}
                  </ol>
                )}
                {rows.length > visible && (
                  <button type="button" className={styles.moreButton} onClick={() => setVisible((current) => nextDrawerCount(current, rows.length))}>
                    {tr('board.more', { count: Math.min(rows.length - visible, DRAWER_PAGE_SIZE), left: rows.length - visible })}
                  </button>
                )}
              </div>
            </>
          )}
        </>
      )}
      </div>
    </Drawer>
  )
}

const CategoryProductRow: FC<{ product: HolidayProduct; index: number; single: boolean; tr: Tr; onOpen: (product: HolidayProduct) => void }> = ({
  product,
  index,
  single,
  tr,
  onOpen,
}) => {
  const tag = supplierTag(product.supplierCode)
  const open = () => onOpen(product)
  return (
    <li
      className={styles.dProductRow}
      tabIndex={0}
      aria-label={tr('drill.open', { name: product.productName })}
      onClick={open}
      onKeyDown={(event) => {
        if (event.key === 'Enter' || event.key === ' ') {
          event.preventDefault()
          open()
        }
      }}
    >
      <span className={styles.rank}>{index + 1}</span>
      <ProductImage src={product.imageUrl} label={tr('product.noImage')} />
      <div className={styles.productMain}>
        <div className={styles.productName} title={product.productName}>{product.productName}</div>
        <div className={styles.productMeta}>
          <i className={`${styles.tag} ${tag.className}`}>{tag.label}</i>
          <span>{product.itemNumber}</span>
        </div>
      </div>
      <div>
        <div className={styles.valueHead}>{formatMoney(product.amount)}</div>
        <div className={styles.valueSub}>{tr('drill.rowSold', { sold: formatCount(product.quantity), bought: formatCount(product.purchaseQuantity) })}</div>
      </div>
      <div>
        <SellThroughBar value={product.sellThrough} />
        {!single && (
          <div className={styles.valueSub}>
            {tr('product.stores', { count: product.storeCount })}
            {product.soldOutStoreCount > 0 && <> · <span className={styles.bad}>{tr('product.soldOutStores', { count: product.soldOutStoreCount })}</span></>}
          </div>
        )}
        {single && product.soldOutDate && (
          <div className={`${styles.valueSub} ${styles.bad}`}>{tr('product.earlyStore', { date: formatMonthDay(product.soldOutDate), qty: formatCount(product.purchaseQuantity) })}</div>
        )}
      </div>
      <div className={`${styles.valueSub} ${styles.alignRight}`}>
        {product.leftoverQuantity > 0 ? (
          <>
            <span className={styles.warnText}>{tr('product.overHead', { qty: formatCount(product.leftoverQuantity) })}</span>
            <br />
            {tr('drill.leftCost', { cost: formatMoney(product.leftoverCost) })}
          </>
        ) : (
          <span className={styles.hint}>{tr('drill.noLeft')}</span>
        )}
      </div>
      <span className={styles.chev} aria-hidden="true">›</span>
    </li>
  )
}
