import type { ReactNode } from 'react'

import './storePicker.css'

interface StoreSidebarLayoutProps {
  /** 来自 useStoreSidebar：侧栏是否展开。 */
  docked: boolean
  sidebar: ReactNode
  children: ReactNode
}

/** 左侧分店列表 + 右侧内容的两栏布局；侧栏没展开时只渲染内容，不留空列。 */
export default function StoreSidebarLayout({ docked, sidebar, children }: StoreSidebarLayoutProps) {
  return (
    <div className={`ssl-layout${docked ? ' has-sidebar' : ''}`}>
      {docked ? <aside className="ssl-aside">{sidebar}</aside> : null}
      <div className="ssl-main">{children}</div>
    </div>
  )
}
