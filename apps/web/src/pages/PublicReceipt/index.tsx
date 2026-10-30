import { useEffect, useState } from 'react'
import { createPortal } from 'react-dom'
import { useParams } from 'react-router-dom'

import { fetchPublicReceipt } from '../../services/publicReceiptService'
import type { PosReceiptDocument } from '../../types/posmSalesOrder'
import ReceiptPaper from '../PosmSalesOrders/receipt/ReceiptPaper'

import './publicReceipt.css'

type LoadState =
  | { kind: 'loading' }
  | { kind: 'ready'; document: PosReceiptDocument }
  | { kind: 'gone' }
  | { kind: 'error' }

/** 从纸面行里取合计（"Total(inc GST)   $23.85"），用来在页头显示金额；取不到就不显示。 */
function readTotal(document: PosReceiptDocument): string | null {
  const line = document.lines.find((candidate) => candidate.text.startsWith('Total(inc GST)'))
  return line?.text.match(/\$-?\d[\d,]*\.\d{2}$/)?.[0] ?? null
}

/**
 * 客人扫码打开的在线小票（免登录、只读、英文）。
 * 页面不带登录态，也不请求任何需要登录的接口；令牌无效 / 过期 / 被撤销统一显示「链接已失效」，不区分原因。
 */
export default function PublicReceiptPage() {
  const { token = '' } = useParams()
  const [state, setState] = useState<LoadState>({ kind: 'loading' })
  // 重试只重新发起请求，不整页刷新。
  const [attempt, setAttempt] = useState(0)

  useEffect(() => {
    // 公开页不该被搜索引擎收录；也不向外部站点发送 Referer（链接里有令牌）。
    const robots = document.createElement('meta')
    robots.name = 'robots'
    robots.content = 'noindex,nofollow'
    const referrer = document.createElement('meta')
    referrer.name = 'referrer'
    referrer.content = 'no-referrer'
    document.head.append(robots, referrer)
    const previousTitle = document.title
    document.title = 'Receipt | Hot Bargain'
    return () => {
      robots.remove()
      referrer.remove()
      document.title = previousTitle
    }
  }, [])

  useEffect(() => {
    const controller = new AbortController()
    setState({ kind: 'loading' })
    fetchPublicReceipt(token, controller.signal)
      .then((receipt) => setState(receipt ? { kind: 'ready', document: receipt } : { kind: 'gone' }))
      .catch(() => {
        if (!controller.signal.aborted) setState({ kind: 'error' })
      })
    return () => controller.abort()
  }, [token, attempt])

  const total = state.kind === 'ready' ? readTotal(state.document) : null
  return (
    <div className="public-receipt-page">
      <header className="public-receipt-header">
        <strong>Hot Bargain</strong>
        <span>E-receipt</span>
      </header>

      {state.kind === 'loading' ? <p className="public-receipt-note">Loading your receipt…</p> : null}

      {state.kind === 'gone' ? (
        <div className="public-receipt-state" role="alert">
          <strong>This receipt link is no longer available</strong>
          <span>It may have expired or been replaced. Please ask the store for a new QR code.</span>
        </div>
      ) : null}

      {state.kind === 'error' ? (
        <div className="public-receipt-state" role="alert">
          <strong>We couldn&apos;t load this receipt</strong>
          <span>Please check your connection and try again.</span>
          <button type="button" onClick={() => setAttempt((value) => value + 1)}>
            Try again
          </button>
        </div>
      ) : null}

      {state.kind === 'ready' ? (
        <>
          <section className="public-receipt-summary">
            <div className={state.document.statusKind === 'refund' ? 'is-refund' : 'is-paid'}>
              <small>{state.document.statusKind === 'refund' ? 'Refund' : 'Paid'}</small>
              {total ? <strong>{total}</strong> : null}
            </div>
          </section>
          <main className="public-receipt-paper">
            <ReceiptPaper document={state.document} />
          </main>
          <div className="public-receipt-actions">
            <button type="button" onClick={() => window.print()}>
              Print or save as PDF
            </button>
          </div>
          {createPortal(
            <div id="posm-receipt-print-root">
              <ReceiptPaper document={state.document} variant="print" />
            </div>,
            document.body,
          )}
        </>
      ) : null}
    </div>
  )
}
