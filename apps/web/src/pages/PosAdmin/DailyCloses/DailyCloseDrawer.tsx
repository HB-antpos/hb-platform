import { Alert, Button, Drawer, Skeleton } from 'antd'
import { useTranslation } from 'react-i18next'

import type {
  DailyCloseDetail,
  DailyCloseDifferenceKind,
  DailyCloseListItem,
} from '../../../types/dailyClose'

import { BackfillChip, DifferenceTag, useDataSourceLabel, useSourceLongLabel } from './DailyCloseTags'
import {
  EMPTY_VALUE,
  buildCashCountSections,
  buildTenderSummary,
  formatCount,
  formatDenomination,
  formatInStoreTime,
  formatMoney,
  formatSignedMoney,
  formatWeekday,
  getDetailNotice,
  hasCashFigures,
  isBackfilled,
  shortGuid,
  type CashCountSection,
  type DailyCloseErrorKind,
} from './logic'

// 支付方式名称：字面量映射，契约测试才能按键扫描。
const TENDER_LABEL_KEYS: Record<string, string> = {
  Cash: 'dailyCloses.tenders.Cash',
  Card: 'dailyCloses.tenders.Card',
  Voucher: 'dailyCloses.tenders.Voucher',
}

const DIFFERENCE_BOX_LABEL_KEYS: Record<DailyCloseDifferenceKind, string> = {
  short: 'dailyCloses.recon.differenceShort',
  over: 'dailyCloses.recon.differenceOver',
  even: 'dailyCloses.recon.differenceEven',
  none: 'dailyCloses.recon.differenceNone',
}

export interface DailyCloseDrawerProps {
  open: boolean
  guid: string | null
  /** 列表里的同一行：详情加载期间先用它显示标题与状态，避免抽屉先空白。 */
  preview: DailyCloseListItem | null
  detail: DailyCloseDetail | null
  loading: boolean
  errorKind: DailyCloseErrorKind | null
  onClose: () => void
  onRetry: () => void
}

/** 抽屉里「没有数据」的虚线占位框：区块本来应该有，只是还没补传，不能整块隐藏。 */
function PlaceholderBox() {
  const { t } = useTranslation()
  return (
    <div className="daily-closes-placeholder" role="note">
      {t('dailyCloses.placeholder')}
    </div>
  )
}

/** 现金盘点的一栏（纸币或硬币）：面额 × 张数 = 小计，0 张灰显但保留。 */
function CashCountTable({
  title,
  quantityTitle,
  subtotalTitle,
  totalTitle,
  section,
}: {
  title: string
  quantityTitle: string
  subtotalTitle: string
  totalTitle: string
  section: CashCountSection
}) {
  return (
    <table className="daily-closes-mini-table">
      <thead>
        <tr>
          <th scope="col">{title}</th>
          <th scope="col" className="is-num">
            {quantityTitle}
          </th>
          <th scope="col" className="is-num">
            {subtotalTitle}
          </th>
        </tr>
      </thead>
      <tbody>
        {section.rows.map((row) => (
          <tr key={row.denominationCents} className={row.quantity === 0 ? 'is-zero' : undefined}>
            <td>{formatDenomination(row.denominationCents)}</td>
            <td className="is-num">× {formatCount(row.quantity)}</td>
            <td className="is-num">{formatMoney(row.subtotalAmount)}</td>
          </tr>
        ))}
        <tr className="is-total">
          <td>{totalTitle}</td>
          <td />
          <td className="is-num">{formatMoney(section.subtotal)}</td>
        </tr>
      </tbody>
    </table>
  )
}

function DailyCloseDetailBody({ detail }: { detail: DailyCloseDetail }) {
  const { t, i18n } = useTranslation()
  const sourceLongLabel = useSourceLongLabel()
  const dataSourceLabel = useDataSourceLabel()

  const kind = detail.differenceKind
  const notice = getDetailNotice(detail)
  const backfilled = isBackfilled(detail)
  const hasCash = hasCashFigures(detail)
  const weekday = formatWeekday(detail.businessDate, i18n.language.startsWith('zh') ? 'zh-CN' : 'en-AU')
  const tenders = detail.tenders.length ? buildTenderSummary(detail.tenders) : null
  const cashCounts = buildCashCountSections(detail.cashCounts, detail.noteSubtotal, detail.coinSubtotal)
  // 统计时段按门店本地时间显示，并在文案里写明时区口径。
  const periodText =
    detail.periodFromUtc && detail.periodToUtc
      ? t('dailyCloses.info.periodValue', {
          from: formatInStoreTime(detail.periodFromUtc, detail.storeTimeZoneId, 'minutes'),
          to: formatInStoreTime(detail.periodToUtc, detail.storeTimeZoneId, 'minutes'),
        })
      : null
  const storeLabel = detail.storeName
    ? t('dailyCloses.drawer.storeWithCode', { name: detail.storeName, code: detail.storeCode })
    : detail.storeCode
  const businessDateLabel = [
    weekday ? t('dailyCloses.drawer.dateWithWeekday', { date: detail.businessDate, weekday }) : detail.businessDate,
    detail.businessDateInferred ? t('dailyCloses.drawer.inferred') : null,
  ]
    .filter(Boolean)
    .join(' · ')

  // 其他信息：成对的条目在前，整行的（统计时段、GUID）在后；补录记录没有上传时间与版本。
  const infoItems: { key: string; label: string; value: string; wide?: boolean; mono?: boolean }[] = [
    { key: 'orderCount', label: t('dailyCloses.info.orderCount'), value: formatCount(detail.orderCount) },
    { key: 'returnQuantity', label: t('dailyCloses.info.returnQuantity'), value: formatCount(detail.returnQuantity) },
    { key: 'source', label: t('dailyCloses.info.source'), value: sourceLongLabel(detail.clientKind) },
    backfilled
      ? { key: 'dataSource', label: t('dailyCloses.info.dataSource'), value: dataSourceLabel(detail.dataSource) }
      : {
          key: 'uploadedAt',
          label: t('dailyCloses.info.uploadedAt'),
          value: formatInStoreTime(detail.receivedAtUtc, detail.storeTimeZoneId, 'seconds'),
        },
    ...(detail.appVersion
      ? [{ key: 'appVersion', label: t('dailyCloses.info.appVersion'), value: detail.appVersion }]
      : []),
    ...(periodText ? [{ key: 'period', label: t('dailyCloses.info.period'), value: periodText, wide: true }] : []),
    { key: 'guid', label: t('dailyCloses.info.guid'), value: detail.dailyCloseGuid, wide: true, mono: true },
  ]

  const pairedInfoItems = infoItems.filter((item) => !item.wide)
  const wideInfoItems = infoItems.filter((item) => item.wide)

  return (
    <div className="daily-closes-detail">
      <div className="daily-closes-ident">
        <span>
          {t('dailyCloses.drawer.store')} <b>{storeLabel}</b>
        </span>
        <span>
          {t('dailyCloses.drawer.device')} <b>{detail.deviceCode || EMPTY_VALUE}</b>
        </span>
        <span>
          {t('dailyCloses.drawer.cashier')} <b>{detail.cashierName || detail.cashierId || EMPTY_VALUE}</b>
        </span>
        <span>
          {t('dailyCloses.drawer.businessDate')}{' '}
          <b>{businessDateLabel}</b>
        </span>
        <span>
          {t('dailyCloses.drawer.savedAt')} <b>{formatInStoreTime(detail.savedAtUtc, detail.storeTimeZoneId, 'seconds')}</b>
        </span>
      </div>

      {notice ? (
        <Alert
          type="info"
          showIcon
          className="daily-closes-notice"
          message={t('dailyCloses.notice.title')}
          description={
            <>
              <p>{notice === 'cashOnly' ? t('dailyCloses.notice.cashOnly') : t('dailyCloses.notice.traceOnly')}</p>
              {detail.businessDateInferred ? <p>{t('dailyCloses.notice.inferred')}</p> : null}
              <p>{t('dailyCloses.notice.autoComplete')}</p>
            </>
          }
        />
      ) : null}

      <section className="daily-closes-section" aria-labelledby="dc-sec-recon">
        <h5 id="dc-sec-recon">{t('dailyCloses.recon.title')}</h5>
        <div className="daily-closes-recon">
          <div className="daily-closes-recon-box">
            <small>{t('dailyCloses.recon.counted')}</small>
            <strong>{hasCash ? formatMoney(detail.countedCashAmount) : EMPTY_VALUE}</strong>
          </div>
          <span className="daily-closes-recon-op" aria-hidden="true">
            −
          </span>
          <div className="daily-closes-recon-box">
            <small>{t('dailyCloses.recon.expected')}</small>
            <strong>{hasCash ? formatMoney(detail.expectedCashAmount) : EMPTY_VALUE}</strong>
          </div>
          <span className="daily-closes-recon-op" aria-hidden="true">
            =
          </span>
          <div className={`daily-closes-recon-box is-diff is-${kind}`}>
            <small>{t(DIFFERENCE_BOX_LABEL_KEYS[kind])}</small>
            <strong>{hasCash ? formatSignedMoney(detail.cashDifference) : EMPTY_VALUE}</strong>
          </div>
        </div>
        <p className="daily-closes-recon-note">
          {hasCash ? t('dailyCloses.recon.formula') : t('dailyCloses.recon.noAmountNote')}
        </p>
      </section>

      <section className="daily-closes-section" aria-labelledby="dc-sec-tenders">
        <h5 id="dc-sec-tenders">{t('dailyCloses.tenders.title')}</h5>
        {tenders ? (
          <table className="daily-closes-mini-table">
            <thead>
              <tr>
                <th scope="col">{t('dailyCloses.tenders.method')}</th>
                <th scope="col" className="is-num">
                  {t('dailyCloses.tenders.sales')}
                </th>
                <th scope="col" className="is-num">
                  {t('dailyCloses.tenders.refund')}
                </th>
                <th scope="col" className="is-num">
                  {t('dailyCloses.tenders.net')}
                </th>
              </tr>
            </thead>
            <tbody>
              {tenders.rows.map((row) => (
                <tr key={row.method}>
                  <td>{TENDER_LABEL_KEYS[row.method] ? t(TENDER_LABEL_KEYS[row.method]) : row.method}</td>
                  <td className="is-num">{formatMoney(row.salesAmount)}</td>
                  <td className="is-num">{formatMoney(row.refundAmount)}</td>
                  <td className="is-num">{formatMoney(row.netAmount)}</td>
                </tr>
              ))}
              <tr className="is-total">
                <td>{t('dailyCloses.tenders.total')}</td>
                <td className="is-num">{formatMoney(tenders.total.salesAmount)}</td>
                <td className="is-num">{formatMoney(tenders.total.refundAmount)}</td>
                <td className="is-num">{formatMoney(tenders.total.netAmount)}</td>
              </tr>
            </tbody>
          </table>
        ) : (
          <PlaceholderBox />
        )}
      </section>

      <section className="daily-closes-section" aria-labelledby="dc-sec-counts">
        <h5 id="dc-sec-counts">{t('dailyCloses.cashCounts.title')}</h5>
        {cashCounts ? (
          <div className="daily-closes-count-cols">
            <CashCountTable
              title={t('dailyCloses.cashCounts.notes')}
              quantityTitle={t('dailyCloses.cashCounts.noteQuantity')}
              subtotalTitle={t('dailyCloses.cashCounts.subtotal')}
              totalTitle={t('dailyCloses.cashCounts.noteSubtotal')}
              section={cashCounts.notes}
            />
            <CashCountTable
              title={t('dailyCloses.cashCounts.coins')}
              quantityTitle={t('dailyCloses.cashCounts.coinQuantity')}
              subtotalTitle={t('dailyCloses.cashCounts.subtotal')}
              totalTitle={t('dailyCloses.cashCounts.coinSubtotal')}
              section={cashCounts.coins}
            />
          </div>
        ) : (
          <PlaceholderBox />
        )}
      </section>

      <section className="daily-closes-section" aria-labelledby="dc-sec-info">
        <h5 id="dc-sec-info">{t('dailyCloses.info.title')}</h5>
        <dl className="daily-closes-info">
          {[...pairedInfoItems, ...(pairedInfoItems.length % 2 === 1 ? [null] : []), ...wideInfoItems].map((item, index) =>
            item ? (
              <div key={item.key} className={item.wide ? 'is-wide' : undefined}>
                <dt>{item.label}</dt>
                <dd className={item.mono ? 'daily-closes-mono' : undefined}>{item.value}</dd>
              </div>
            ) : (
              // 成对的条目是奇数个时补一个空格，避免右侧露出分隔线的底色
              <div key={`filler-${index}`} aria-hidden="true" />
            ),
          )}
        </dl>
      </section>
    </div>
  )
}

/** 日结明细抽屉（只读）：现金对账 → 支付方式汇总 → 现金盘点明细 → 其他信息。 */
export default function DailyCloseDrawer({
  open,
  guid,
  preview,
  detail,
  loading,
  errorKind,
  onClose,
  onRetry,
}: DailyCloseDrawerProps) {
  const { t } = useTranslation()
  const head = detail ?? preview

  const errorMessage: Record<DailyCloseErrorKind, string> = {
    forbidden: t('dailyCloses.errors.forbidden'),
    notFound: t('dailyCloses.errors.detailNotFound'),
    invalidQuery: t('dailyCloses.errors.detailFailed'),
    failed: t('dailyCloses.errors.detailFailed'),
  }

  return (
    <Drawer
      rootClassName="daily-closes-drawer"
      open={open}
      width={600}
      destroyOnHidden
      onClose={onClose}
      closable={{ placement: 'end', 'aria-label': t('dailyCloses.drawer.close') }}
      title={
        <div className="daily-closes-drawer-title">
          <span>{t('dailyCloses.drawer.title')}</span>
          {guid ? <span className="daily-closes-mono daily-closes-drawer-id">#{shortGuid(guid)}</span> : null}
          {head ? <DifferenceTag kind={head.differenceKind} /> : null}
          {head && isBackfilled(head) ? <BackfillChip /> : null}
        </div>
      }
    >
      {errorKind ? (
        <Alert
          type={errorKind === 'notFound' ? 'warning' : 'error'}
          showIcon
          message={errorMessage[errorKind]}
          action={
            errorKind === 'failed' || errorKind === 'invalidQuery' ? (
              <Button size="small" onClick={onRetry}>
                {t('dailyCloses.errors.retry')}
              </Button>
            ) : undefined
          }
        />
      ) : loading || !detail ? (
        <Skeleton active paragraph={{ rows: 10 }} aria-busy="true" />
      ) : (
        <DailyCloseDetailBody detail={detail} />
      )}
    </Drawer>
  )
}
