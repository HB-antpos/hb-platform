import { CloseOutlined, DownloadOutlined, FileTextOutlined, LockOutlined, MailOutlined } from '@ant-design/icons'
import { Alert, Button, Drawer, Skeleton, Tag, message } from 'antd'
import { useCallback, useEffect, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'

import { getMyobPayslipDetail, getMyobPayslipHours } from '../../../services/myobPayslipService'
import type { MyobPayslipDetail, MyobPayslipHours, MyobPayslipKey, MyobPayslipStore } from '../../../types/myobPayslip'
import { extractMyobError } from '../Myob/myobLogic'

import PayslipEmailModal from './PayslipEmailModal'
import PayslipPaper from './PayslipPaper'
import { downloadAndSavePayslipPdf } from './payslipDownload'
import { buildPayslipPdfFilename, formatHours, formatPaperDate } from './payslipLogic'

const NOT_FOUND_CODE = 'MYOB_PAYSLIP_NOT_FOUND'
const FORBIDDEN_CODE = 'MYOB_PAYSLIP_FORBIDDEN'

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

/**
 * 单张工资单明细抽屉。正文是 MYOB 默认 Pay Slip 纸面（与下载的 PDF / 邮件附件同一份版式），数据全部来自 MYOB；页脚固定声明「以 MYOB 发出的为准」。
 * 打开 / 下载 / 发邮件都会在后端留下记录（只记操作人与期别、收件邮箱，不含金额）；「我方考勤工时」留在纸面之外、点击后才请求，不进 PDF / 邮件。
 * 「下载 PDF」「发送邮件」是否显示完全取决于明细返回的 canDownloadPdf / canSendEmail（无权限不渲染）。
 */
export default function PayslipDrawer({ target, onClose }: PayslipDrawerProps) {
  const { t } = useTranslation()
  const [detail, setDetail] = useState<MyobPayslipDetail | null>(null)
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [hours, setHours] = useState<HoursState>({ status: 'idle' })
  const [downloading, setDownloading] = useState(false)
  const [emailOpen, setEmailOpen] = useState(false)
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
    setEmailOpen(false)
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

  // 下载 PDF：接口需要登录态，走统一请求封装取回 Blob 再触发浏览器保存；失败只给通用提示（不显示后端原文）。
  const downloadPdf = async () => {
    if (!target || !detail) {
      return
    }
    setDownloading(true)
    try {
      await downloadAndSavePayslipPdf(target, buildPayslipPdfFilename(detail.employeeName, detail.paymentDate))
      message.success(t('payslips.drawer.downloaded'))
    } catch (err) {
      const { code } = extractMyobError(err, '')
      message.error(
        code === FORBIDDEN_CODE
          ? t('payslips.drawer.downloadForbidden')
          : code === NOT_FOUND_CODE
            ? t('payslips.drawer.notFound')
            : t('payslips.drawer.downloadFailed'),
      )
    } finally {
      setDownloading(false)
    }
  }

  const inferred = detail?.placement === 'Inferred'

  return (
    <Drawer
      open={target !== null}
      width={760}
      rootClassName="payslip-drawer"
      destroyOnClose
      onClose={onClose}
      // 关闭按钮放在标题栏右侧（antd 默认在左侧）
      closable={false}
      extra={
        <div className="payslip-drawer-actions">
          {detail?.canDownloadPdf ? (
            <Button color="primary" variant="outlined" icon={<DownloadOutlined />} loading={downloading} onClick={() => void downloadPdf()}>
              {t('payslips.drawer.download')}
            </Button>
          ) : null}
          {detail?.canSendEmail ? (
            <Button color="primary" variant="outlined" icon={<MailOutlined />} onClick={() => setEmailOpen(true)}>
              {t('payslips.drawer.sendEmail')}
            </Button>
          ) : null}
          <Button type="text" icon={<CloseOutlined />} aria-label={t('payslips.drawer.close')} onClick={onClose} />
        </div>
      }
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
          {detail.samePaymentDateCount > 1 ? (
            <Alert type="info" showIcon message={t('payslips.drawer.samePaymentDate', { count: detail.samePaymentDateCount - 1 })} />
          ) : null}

          {detail.lines.length === 0 ? <Alert type="warning" showIcon message={t('payslips.drawer.noLines')} /> : null}

          {/* MYOB 默认 Pay Slip 纸面：与下载的 PDF / 邮件附件是同一份版式，所见即所发 */}
          <PayslipPaper detail={detail} />
          <div className="payslip-paper-caption">
            <FileTextOutlined /> {t('payslips.drawer.paperCaption')}
          </div>

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
              <div className="payslip-hours-idle">
                <PayslipStoreTags stores={detail.stores} inferred={inferred} />
                {hours.status === 'error' ? (
                  <div className="payslip-muted" style={{ width: '100%' }}>
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

      {/* 发送邮件弹窗：需要时才挂载，关闭即卸载，每次打开都是全新状态 */}
      {emailOpen && target && detail ? (
        <PayslipEmailModal
          target={target}
          employeeName={detail.employeeName}
          periodLabel={`${formatPaperDate(detail.periodStart)} – ${formatPaperDate(detail.periodEnd)}`}
          onClose={() => setEmailOpen(false)}
          onDownloadPdf={downloadPdf}
        />
      ) : null}
    </Drawer>
  )
}
