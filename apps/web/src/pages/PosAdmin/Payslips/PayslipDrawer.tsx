import { CloseOutlined, LockOutlined } from '@ant-design/icons'
import { Alert, Button, Drawer, Skeleton, Tag } from 'antd'
import { useCallback, useEffect, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'

import { getMyobPayslipDetail, getMyobPayslipHours } from '../../../services/myobPayslipService'
import type {
  MyobPayslipDetail,
  MyobPayslipHours,
  MyobPayslipKey,
  MyobPayslipLine,
  MyobPayslipStore,
} from '../../../types/myobPayslip'
import { extractMyobError } from '../Myob/myobLogic'

import { countDetailLines, formatHours, formatMoney, formatPayslipPeriod } from './payslipLogic'

const NOT_FOUND_CODE = 'MYOB_PAYSLIP_NOT_FOUND'

interface PayslipDrawerProps {
  /** 要打开的工资单；null 表示关闭。 */
  target: MyobPayslipKey | null
  onClose: () => void
}

type HoursState =
  | { status: 'idle' }
  | { status: 'loading' }
  | { status: 'error' }
  | { status: 'done'; data: MyobPayslipHours }

/** 分店标签：有考勤的实线蓝色；按当前分店推断的虚线（与列表一致）。 */
export function PayslipStoreTags({ stores, inferred }: { stores: MyobPayslipStore[]; inferred: boolean }) {
  const { t } = useTranslation()
  return (
    <>
      {stores.map((store) => (
        <Tag
          key={store.storeCode}
          color={inferred ? undefined : 'blue'}
          className={inferred ? 'payslip-tag-inferred' : undefined}
          title={inferred ? t('payslips.placement.inferredTip') : undefined}
        >
          {store.storeLabel}
        </Tag>
      ))}
    </>
  )
}

function LinesTable({
  lines,
  mode,
  ariaLabel,
}: {
  lines: MyobPayslipLine[]
  /** wage：小时 / 费率 / 本期 / 累计；amount：本期 / 累计；hours：金额列实际是小时数（假期累计）。 */
  mode: 'wage' | 'amount' | 'hours'
  ariaLabel: string
}) {
  const { t } = useTranslation()
  const money = (value: number | null) => (mode === 'hours' ? (value === null ? '—' : `${formatHours(value)} h`) : formatMoney(value))
  return (
    <table className="payslip-lines" aria-label={ariaLabel}>
      <thead>
        <tr>
          <th>{t('payslips.drawer.columns.description')}</th>
          {mode === 'wage' ? (
            <>
              <th className="payslip-num">{t('payslips.drawer.columns.hours')}</th>
              <th className="payslip-num">{t('payslips.drawer.columns.rate')}</th>
            </>
          ) : null}
          <th className="payslip-num">{t('payslips.drawer.columns.thisPay')}</th>
          <th className="payslip-num">{t('payslips.drawer.columns.ytd')}</th>
        </tr>
      </thead>
      <tbody>
        {lines.map((line, index) => (
          <tr key={`${line.description ?? ''}-${index}`}>
            <td>{line.description ?? '—'}</td>
            {mode === 'wage' ? (
              <>
                <td className="payslip-num">{formatHours(line.hours)}</td>
                <td className="payslip-num">{formatMoney(line.rate)}</td>
              </>
            ) : null}
            <td className="payslip-num">{money(line.amount)}</td>
            <td className="payslip-num">{money(line.yearToDate)}</td>
          </tr>
        ))}
      </tbody>
    </table>
  )
}

function Section({ title, sub, children }: { title: string; sub: string; children: React.ReactNode }) {
  return (
    <section>
      <p className="payslip-section-title">
        {title}
        <small>{sub}</small>
      </p>
      {children}
    </section>
  )
}

/**
 * 单张工资单明细抽屉（只读）。数据全部来自 MYOB，英文用词保持 MYOB 原文；页脚固定声明「以 MYOB 发出的为准」。
 * 打开抽屉会在后端留下一条查看记录（只记谁看了谁哪一期，不含金额）；「我方考勤工时」点击后才请求，不自动加载。
 */
export default function PayslipDrawer({ target, onClose }: PayslipDrawerProps) {
  const { t } = useTranslation()
  const [detail, setDetail] = useState<MyobPayslipDetail | null>(null)
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [hours, setHours] = useState<HoursState>({ status: 'idle' })
  const detailSeq = useRef(0)
  const hoursSeq = useRef(0)

  const load = useCallback(async () => {
    if (!target) {
      return
    }
    const seq = ++detailSeq.current
    setLoading(true)
    try {
      const data = await getMyobPayslipDetail(target)
      if (seq === detailSeq.current) {
        setDetail(data)
        setError(null)
      }
    } catch (err) {
      if (seq === detailSeq.current) {
        const { code, message } = extractMyobError(err, t('payslips.drawer.loadFailed'))
        setError(code === NOT_FOUND_CODE ? t('payslips.drawer.notFound') : message)
      }
    } finally {
      if (seq === detailSeq.current) {
        setLoading(false)
      }
    }
  }, [t, target])

  useEffect(() => {
    // 打开 / 换一张：清空旧内容再加载，避免短暂显示上一个人的工资（含工时区块）。
    setDetail(null)
    setError(null)
    setHours({ status: 'idle' })
    hoursSeq.current += 1
    if (target) {
      void load()
    } else {
      detailSeq.current += 1
    }
  }, [target, load])

  const loadHours = async () => {
    if (!target) {
      return
    }
    const seq = ++hoursSeq.current
    setHours({ status: 'loading' })
    try {
      const data = await getMyobPayslipHours(target)
      if (seq === hoursSeq.current) {
        setHours({ status: 'done', data })
      }
    } catch {
      if (seq === hoursSeq.current) {
        setHours({ status: 'error' })
      }
    }
  }

  const inferred = detail?.placement === 'Inferred'
  const deductionLines = detail ? [...detail.taxes, ...detail.deductions] : []

  return (
    <Drawer
      open={target !== null}
      width={640}
      rootClassName="payslip-drawer"
      destroyOnClose
      onClose={onClose}
      // 关闭按钮放在标题栏右侧（antd 默认在左侧）
      closable={false}
      extra={<Button type="text" icon={<CloseOutlined />} aria-label={t('payslips.drawer.close')} onClick={onClose} />}
      title={
        <div className="payslip-drawer-head">
          <div>
            {detail?.employeeName ?? ''}
            {detail?.displayId ? <span className="payslip-mono">{detail.displayId}</span> : null}
          </div>
          {detail ? (
            <div className="payslip-drawer-sub">
              {detail.companyName}
              {detail.companyAbn ? ` · ABN ${detail.companyAbn}` : ''}
            </div>
          ) : null}
          {detail ? (
            <div className="payslip-drawer-tags">
              <Tag color="success">{t('payslips.drawer.paid')}</Tag>
              {detail.payFrequency ? <Tag>{detail.payFrequency}</Tag> : null}
              <PayslipStoreTags stores={detail.stores} inferred={inferred} />
            </div>
          ) : null}
        </div>
      }
      footer={
        <div className="payslip-footer">
          <div>
            {t('payslips.drawer.disclaimerZh')} {t('payslips.drawer.disclaimerEn')}
          </div>
          {detail ? (
            <div className="payslip-muted">
              <LockOutlined /> {t('payslips.drawer.logged')}
            </div>
          ) : null}
        </div>
      }
    >
      {error ? (
        <Alert
          type="error"
          showIcon
          message={t('payslips.drawer.loadFailed')}
          description={error}
          action={
            <Button size="small" onClick={() => void load()}>
              {t('payslips.retry')}
            </Button>
          }
        />
      ) : null}

      {loading && !detail ? <Skeleton active paragraph={{ rows: 10 }} /> : null}

      {detail ? (
        <>
          <div className="payslip-muted">
            {t('payslips.drawer.period', { range: formatPayslipPeriod(detail.periodStart, detail.periodEnd) })}
            {' · '}
            {t('payslips.drawer.paymentDate', { date: detail.paymentDate })}
          </div>

          {detail.samePaymentDateCount > 1 ? (
            <Alert type="info" showIcon message={t('payslips.drawer.samePaymentDate', { count: detail.samePaymentDateCount - 1 })} />
          ) : null}

          <div className="payslip-big">
            <div>
              <span>{t('payslips.drawer.gross')}</span>
              <b>{formatMoney(detail.grossPay)}</b>
            </div>
            <div className="payslip-net">
              <span>{t('payslips.drawer.net')}</span>
              <b>{formatMoney(detail.netPay)}</b>
            </div>
          </div>

          {countDetailLines([detail.wages, deductionLines, detail.superannuation, detail.entitlements, detail.others]) === 0 ? (
            <Alert type="warning" showIcon message={t('payslips.drawer.noLines')} />
          ) : null}

          {detail.wages.length > 0 ? (
            <Section title={t('payslips.drawer.sections.wages.title')} sub={t('payslips.drawer.sections.wages.sub')}>
              <LinesTable lines={detail.wages} mode="wage" ariaLabel={t('payslips.drawer.sections.wages.title')} />
            </Section>
          ) : null}
          {deductionLines.length > 0 ? (
            <Section title={t('payslips.drawer.sections.deductions.title')} sub={t('payslips.drawer.sections.deductions.sub')}>
              <LinesTable lines={deductionLines} mode="amount" ariaLabel={t('payslips.drawer.sections.deductions.title')} />
            </Section>
          ) : null}
          {detail.superannuation.length > 0 ? (
            <Section
              title={t('payslips.drawer.sections.superannuation.title')}
              sub={
                detail.superFundName
                  ? `${t('payslips.drawer.sections.superannuation.sub')} · ${t('payslips.drawer.fund', { name: detail.superFundName })}`
                  : t('payslips.drawer.sections.superannuation.sub')
              }
            >
              <LinesTable lines={detail.superannuation} mode="amount" ariaLabel={t('payslips.drawer.sections.superannuation.title')} />
            </Section>
          ) : null}
          {detail.entitlements.length > 0 ? (
            <Section title={t('payslips.drawer.sections.entitlements.title')} sub={t('payslips.drawer.sections.entitlements.sub')}>
              <LinesTable lines={detail.entitlements} mode="hours" ariaLabel={t('payslips.drawer.sections.entitlements.title')} />
            </Section>
          ) : null}
          {detail.others.length > 0 ? (
            <Section title={t('payslips.drawer.sections.others.title')} sub={t('payslips.drawer.sections.others.sub')}>
              <LinesTable lines={detail.others} mode="amount" ariaLabel={t('payslips.drawer.sections.others.title')} />
            </Section>
          ) : null}

          <section className="payslip-hours-card" aria-label={t('payslips.hours.title')}>
            <p className="payslip-section-title">
              {t('payslips.hours.title')}
              <small>{t('payslips.hours.sub')}</small>
            </p>
            {detail.stores.length === 0 ? (
              <div className="payslip-muted">{t('payslips.hours.noStores')}</div>
            ) : hours.status === 'done' ? (
              <>
                {hours.data.stores.map((store) => (
                  <div className="payslip-hours-row" key={store.storeCode}>
                    <span>
                      <Tag color={inferred ? undefined : 'blue'} className={inferred ? 'payslip-tag-inferred' : undefined}>
                        {store.storeLabel}
                      </Tag>
                      {t('payslips.hours.work')}
                      {store.leaveHours > 0 ? (
                        <span className="payslip-muted">
                          {' · '}
                          {t('payslips.hours.leave')} {formatHours(store.leaveHours)} h
                        </span>
                      ) : null}
                    </span>
                    {store.failed ? <Tag color="error">{t('payslips.hours.storeFailed')}</Tag> : <b>{formatHours(store.workHours)} h</b>}
                  </div>
                ))}
                <div className="payslip-hours-row">
                  <span className="payslip-muted">{t('payslips.hours.total')}</span>
                  <b>{formatHours(hours.data.totalWorkHours)} h</b>
                </div>
                <div className="payslip-hours-note">{t('payslips.hours.note')}</div>
              </>
            ) : (
              <div>
                {hours.status === 'error' ? (
                  <div className="payslip-muted" style={{ marginBottom: 8 }}>
                    {t('payslips.hours.loadFailed')}
                  </div>
                ) : null}
                <Button loading={hours.status === 'loading'} onClick={() => void loadHours()}>
                  {hours.status === 'loading'
                    ? t('payslips.hours.loading')
                    : hours.status === 'error'
                      ? t('payslips.hours.retry')
                      : t('payslips.hours.load')}
                </Button>
              </div>
            )}
          </section>
        </>
      ) : null}
    </Drawer>
  )
}
