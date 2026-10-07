import type { ReactNode } from 'react'
import './listToolbar.css'

export interface StatusTabItem<K extends string = string> {
  key: K
  label: ReactNode
  /** 该状态下的记录数；未知（加载中或接口不支持）时不传，不显示计数。 */
  count?: number | null
  /** 计数的强调色：warning 用于需要处理的积压，danger 用于逾期等异常。 */
  tone?: 'default' | 'warning' | 'danger'
  /** 标签前的小徽标，如等级字母。 */
  prefix?: ReactNode
}

interface StatusTabsProps<K extends string> {
  items: StatusTabItem<K>[]
  activeKey: K
  onChange: (key: K) => void
  /** 整组按钮的无障碍名称，如「订单状态」。 */
  ariaLabel: string
  /** 行尾附加内容（右对齐）。 */
  extra?: ReactNode
}

function formatCount(count: number) {
  return count.toLocaleString('en-US')
}

/**
 * 列表顶部的状态页签：既是最常用的筛选，也直接给出各状态的数量。
 * 原先状态筛选分散在顶部复选框和列头放大镜两处，且看不出每个状态有多少条。
 */
export default function StatusTabs<K extends string>({ items, activeKey, onChange, ariaLabel, extra }: StatusTabsProps<K>) {
  return (
    <div className="list-toolbar-status-tabs" role="group" aria-label={ariaLabel}>
      {items.map((item) => {
        const active = item.key === activeKey
        return (
          <button
            key={item.key}
            type="button"
            aria-pressed={active}
            className={`list-toolbar-status-tab${active ? ' list-toolbar-status-tab-active' : ''}`}
            onClick={() => {
              if (!active) {
                onChange(item.key)
              }
            }}
          >
            {item.prefix}
            <span>{item.label}</span>
            {typeof item.count === 'number' ? (
              <span className={`list-toolbar-status-count list-toolbar-status-count-${item.tone ?? 'default'}`}>
                {formatCount(item.count)}
              </span>
            ) : null}
          </button>
        )
      })}
      {extra ? <span className="list-toolbar-status-extra">{extra}</span> : null}
    </div>
  )
}
