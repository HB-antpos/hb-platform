import { PrinterOutlined } from '@ant-design/icons'
import { Alert, Button, InputNumber, Modal, Progress, Space, Typography } from 'antd'
import { useEffect, useMemo, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'

import { markPackLabelsPrinted, readPackingError } from '../../services/packingService'
import type { PackCartonSummary } from '../../types/packing'

import { cartonsToReportPrinted, chunkPrintQueue, clampCopies, countPrintPages, uniqueCartons } from './blankCartons'
import { buildCartonPrintLabels, printPackLabelsAndWait } from './packLabel'

type RunStatus = 'idle' | 'printing' | 'marking' | 'paused' | 'done' | 'markFailed' | 'unconfirmed' | 'failed'

interface BlankLabelPrintModalProps {
  /** 要打印的箱（未启用的空箱标印空箱标，其他印内容标）。 */
  cartons: PackCartonSummary[]
  defaultCopies?: number
  /** 打开后立刻开始打印（「创建并打印」）；否则等点「开始打印」。 */
  autoStart?: boolean
  onClose: () => void
  /** 每一次打印上报成功后调用：调用方刷新列表 / 批次。 */
  onPrinted?: () => void
}

/**
 * 批量打印：一张箱标一页 70×26 mm，浏览器打印对话框关闭后（afterprint）上报「已打印」。
 * 浏览器无法确认纸是否真的打出来，所以文案是「已发送打印」；对话框没有回执 / 上报失败时给出「标记为已打印 / 重新标记」，
 * 漏打的标签可以重新打印。一次最多 100 页，超过的分成几次打印，每次单独上报，进度不会因为中途停下而丢。
 * 调用方用 key 区分每次打开（换一批箱就重新挂载）。
 */
export default function BlankLabelPrintModal({ cartons, defaultCopies = 1, autoStart = false, onClose, onPrinted }: BlankLabelPrintModalProps) {
  const { t } = useTranslation()
  const queue = useMemo(() => uniqueCartons(cartons), [cartons])
  const [copies, setCopies] = useState(clampCopies(defaultCopies))
  const chunks = useMemo(() => chunkPrintQueue(queue, copies), [queue, copies])
  const [status, setStatus] = useState<RunStatus>('idle')
  const [nextIndex, setNextIndex] = useState(0)
  const [sent, setSent] = useState(0)
  const [skipped, setSkipped] = useState(0)
  const [markError, setMarkError] = useState('')
  // 当前这一次打印的序号：标记失败 / 没有回执时，重试要针对同一次打印。
  const currentRef = useRef(0)
  const startedRef = useRef(false)

  const reportable = useMemo(() => cartonsToReportPrinted(queue).length > 0, [queue])
  const busy = status === 'printing' || status === 'marking'
  const total = queue.length

  const afterSent = (index: number) => {
    setSent((value) => value + (chunks[index]?.length ?? 0))
    onPrinted?.()
    if (index + 1 < chunks.length) {
      setNextIndex(index + 1)
      setStatus('paused')
    } else {
      setStatus('done')
    }
  }

  const mark = async (index: number) => {
    const guids = cartonsToReportPrinted(chunks[index] ?? [])
    if (guids.length === 0) {
      afterSent(index)
      return
    }
    setStatus('marking')
    try {
      const result = await markPackLabelsPrinted(guids)
      setSkipped((value) => value + (result?.skipped?.length ?? 0))
    } catch (error) {
      setMarkError(readPackingError(error).message)
      setStatus('markFailed')
      return
    }
    afterSent(index)
  }

  const run = async (index: number) => {
    const chunk = chunks[index]
    if (!chunk?.length) {
      return
    }
    currentRef.current = index
    setStatus('printing')
    const outcome = await printPackLabelsAndWait(buildCartonPrintLabels(chunk, copies))
    if (outcome === 'failed') {
      setStatus('failed')
    } else if (outcome === 'unconfirmed') {
      setStatus('unconfirmed')
    } else {
      await mark(index)
    }
  }

  useEffect(() => {
    if (autoStart && !startedRef.current) {
      startedRef.current = true
      void run(0)
    }
    // 只在挂载时按 autoStart 触发一次。
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  const reprintAll = () => {
    setSent(0)
    setSkipped(0)
    setNextIndex(0)
    void run(0)
  }

  const footer = (
    <Space>
      <Button disabled={busy} onClick={onClose}>
        {t('packingUi.blank.print.close')}
      </Button>
      {status === 'idle' ? (
        <Button type="primary" icon={<PrinterOutlined />} onClick={() => void run(0)}>
          {t('packingUi.blank.print.start')}
        </Button>
      ) : null}
      {busy ? (
        <Button type="primary" loading disabled>
          {t('packingUi.blank.print.start')}
        </Button>
      ) : null}
      {status === 'paused' ? (
        <Button type="primary" icon={<PrinterOutlined />} onClick={() => void run(nextIndex)}>
          {t('packingUi.blank.print.continue', { count: total - sent })}
        </Button>
      ) : null}
      {status === 'markFailed' ? (
        <Button type="primary" onClick={() => void mark(currentRef.current)}>
          {t('packingUi.blank.print.retryMark')}
        </Button>
      ) : null}
      {status === 'unconfirmed' ? (
        <>
          <Button onClick={() => void run(currentRef.current)}>{t('packingUi.blank.print.reprint')}</Button>
          <Button type="primary" onClick={() => void mark(currentRef.current)}>
            {t('packingUi.blank.print.markPrinted')}
          </Button>
        </>
      ) : null}
      {status === 'failed' ? (
        <Button type="primary" icon={<PrinterOutlined />} onClick={() => void run(currentRef.current)}>
          {t('packingUi.blank.print.reprint')}
        </Button>
      ) : null}
      {status === 'done' ? (
        <Button icon={<PrinterOutlined />} onClick={reprintAll}>
          {t('packingUi.blank.print.reprint')}
        </Button>
      ) : null}
    </Space>
  )

  return (
    <Modal
      open
      title={t('packingUi.blank.print.title')}
      width={520}
      footer={footer}
      onCancel={busy ? undefined : onClose}
      closable={!busy}
      maskClosable={false}
      destroyOnClose
    >
      <div className="wh-packing-print-run">
        <Typography.Text>
          {t('packingUi.blank.print.summary', { cartons: total, copies, pages: countPrintPages(total, copies) })}
        </Typography.Text>
        <div className="wh-packing-print-copies">
          <Typography.Text type="secondary">{t('packingUi.blank.print.copies')}</Typography.Text>
          <InputNumber
            min={1}
            max={5}
            precision={0}
            value={copies}
            disabled={status !== 'idle' && status !== 'done'}
            onChange={(value) => setCopies(clampCopies(value))}
            addonAfter={t('packingUi.blank.create.copiesUnit')}
          />
        </div>
        {status !== 'idle' ? (
          <Progress
            percent={total ? Math.round((sent / total) * 100) : 0}
            format={() => t('packingUi.blank.print.progress', { done: sent, total })}
            status={status === 'markFailed' || status === 'failed' ? 'exception' : status === 'done' ? 'success' : 'active'}
          />
        ) : null}
        {status === 'printing' ? <Alert type="info" showIcon message={t('packingUi.blank.print.printing')} /> : null}
        {status === 'done' ? <Alert type="success" showIcon message={t('packingUi.blank.print.sent', { count: sent })} /> : null}
        {status === 'markFailed' ? (
          <Alert type="warning" showIcon message={t('packingUi.blank.print.markFailed', { reason: markError })} />
        ) : null}
        {status === 'unconfirmed' ? <Alert type="warning" showIcon message={t('packingUi.blank.print.unconfirmed')} /> : null}
        {status === 'failed' ? <Alert type="error" showIcon message={t('packingUi.blank.print.failed')} /> : null}
        {skipped > 0 ? <Alert type="warning" showIcon message={t('packingUi.blank.print.skipped', { count: skipped })} /> : null}
        {!reportable ? <Typography.Text type="secondary">{t('packingUi.blank.print.noReport')}</Typography.Text> : null}
        <Typography.Text type="secondary">{t('packingUi.blank.create.paperHint')}</Typography.Text>
      </div>
    </Modal>
  )
}
