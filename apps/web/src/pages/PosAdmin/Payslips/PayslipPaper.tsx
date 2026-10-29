import type { ReactNode } from 'react'

import type { MyobPayslipDetail } from '../../../types/myobPayslip'

import { formatPaperDate, formatPaperMoney, paperLineCells } from './payslipLogic'

/** 纸面左侧的「标签 + 值」一行；size 决定标签列宽度（与 MYOB 默认 Pay Slip 的对齐一致）。 */
function Kv({ label, size, top, children }: { label: string; size?: 'w2' | 'r'; top?: number; children?: ReactNode }) {
  return (
    <div className={`payslip-m-kv${size ? ` payslip-m-kv--${size}` : ''}`} style={top ? { marginTop: top } : undefined}>
      <span>{label}</span>
      <span>{children}</span>
    </div>
  )
}

interface PayslipPaperProps {
  detail: MyobPayslipDetail
  /** 通栏表底部留白高度（MYOB 原样有一块高的空表格）；默认 110px。 */
  fillHeight?: number
}

/**
 * MYOB 默认 Pay Slip 纸面（按 MYOB 下载的真实 PDF 还原：通栏表 + TYPE 列、右上 Reference Number、右侧 GROSS / NET）。
 * 与后端生成的 PDF / 邮件附件是同一份版式，所见即所发。版式用词（ABN. / Pay Slip For: 等）是 MYOB 原文，不走多语言；
 * 纸面始终白底黑字（它是一份文档，不跟随深色主题）。数据缺失一律留空，不显示「—」。
 * 没有对应字段的 Memo/payslip message 只渲染标签 + 空行，保持版式一致。
 */
export default function PayslipPaper({ detail, fillHeight = 110 }: PayslipPaperProps) {
  return (
    <div className="payslip-paper" role="group" aria-label="Pay Slip">
      <div className="payslip-m-co">{detail.companyName}</div>

      <div className="payslip-m-grid">
        <div>
          <Kv label="ABN.">{detail.companyAbn}</Kv>
          <Kv label="Pay Slip For:" top={5}>
            {detail.employeeName}
          </Kv>
        </div>
        <div>
          <Kv label="Reference Number:" size="r">
            {detail.referenceNumber}
          </Kv>
          <Kv label="Payment Date:" size="r" top={5}>
            {formatPaperDate(detail.paymentDate)}
          </Kv>
        </div>
      </div>

      <div className="payslip-m-grid payslip-m-grid--gap">
        <div>
          <Kv label="Hourly Rate:" size="w2">
            {formatPaperMoney(detail.hourlyRate, 4)}
          </Kv>
          <Kv label="Pay Period From:" size="w2" top={12}>
            {formatPaperDate(detail.periodStart)}
            <i>To:</i>
            <i className="payslip-m-to">{formatPaperDate(detail.periodEnd)}</i>
          </Kv>
          <Kv label="Superannuation Fund:" size="w2" top={12}>
            {detail.superFundName}
          </Kv>
        </div>
        <div className="payslip-m-totals">
          <Kv label="GROSS PAY:" size="r">
            {formatPaperMoney(detail.grossPay)}
          </Kv>
          <Kv label="NET PAY:" size="r" top={12}>
            {formatPaperMoney(detail.netPay)}
          </Kv>
        </div>
      </div>

      <div className="payslip-m-memo">
        Memo/payslip message:
        <br />
        &nbsp;
      </div>

      <div className="payslip-m-tblw">
        <table className="payslip-m-tbl">
          <colgroup>
            <col style={{ width: 'auto' }} />
            <col style={{ width: 52 }} />
            <col style={{ width: 70 }} />
            <col style={{ width: 74 }} />
            <col style={{ width: 86 }} />
            <col style={{ width: 94 }} />
          </colgroup>
          <thead>
            <tr>
              <th style={{ textAlign: 'left' }}>Description</th>
              <th>Hours</th>
              <th>Calc. Rate</th>
              <th>Amount</th>
              <th>YTD</th>
              <th>Type</th>
            </tr>
          </thead>
          <tbody>
            {detail.lines.map((line, index) => {
              const cells = paperLineCells(line)
              return (
                <tr key={`${cells.description}-${index}`}>
                  <td>{cells.description}</td>
                  <td>{cells.hours}</td>
                  <td>{cells.rate}</td>
                  <td>{cells.amount}</td>
                  <td>{cells.yearToDate}</td>
                  <td>{cells.type}</td>
                </tr>
              )
            })}
            {/* 底部留白：MYOB 原样的表格有一块高的空白区 */}
            <tr className="payslip-m-fill" style={{ ['--payslip-fill' as string]: `${fillHeight}px` }}>
              <td />
              <td />
              <td />
              <td />
              <td />
              <td />
            </tr>
          </tbody>
        </table>
      </div>

      <div className="payslip-m-page">Page 1 of 1</div>
    </div>
  )
}
