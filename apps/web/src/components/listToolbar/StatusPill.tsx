import type { ReactNode } from 'react'
import './listToolbar.css'

export type StatusPillTone = 'blue' | 'orange' | 'green' | 'gray' | 'red' | 'purple'

interface StatusPillProps {
  tone: StatusPillTone
  children: ReactNode
  title?: string
}

/**
 * 状态胶囊：圆点 + 文字。仓库各列表统一用它表示状态，颜色只承载状态含义；
 * 日期、分店等普通字段不再按值上色，避免满屏彩色标签干扰状态辨认。
 */
export default function StatusPill({ tone, children, title }: StatusPillProps) {
  return (
    <span className={`list-toolbar-pill list-toolbar-pill-${tone}`} title={title}>
      <span className="list-toolbar-pill-dot" aria-hidden="true" />
      {children}
    </span>
  )
}
