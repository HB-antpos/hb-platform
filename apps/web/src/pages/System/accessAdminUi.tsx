import { InfoCircleFilled } from '@ant-design/icons'
import { Button } from 'antd'
import type { CSSProperties, ReactNode } from 'react'
import { getRoleAccentColor } from '../../utils/userTableColors'

/*
 * 角色管理与用户管理共用的小部件。
 * 两个页面是独立的懒加载分块，这里只用内联样式：共享 CSS 文件会被拆成单独的纯 CSS 分块，
 * 触发首屏包体检查报错。
 */

const chipStyle: CSSProperties = {
  display: 'inline-flex',
  alignItems: 'center',
  gap: 6,
  maxWidth: '100%',
  height: 24,
  padding: '0 8px',
  borderRadius: 7,
  background: '#f1f3f7',
  color: '#3c4350',
  fontSize: 12.5,
  lineHeight: '24px',
  whiteSpace: 'nowrap',
}

export function AccentDot({ color, size = 8, hollow = false }: { color: string; size?: number; hollow?: boolean }) {
  return (
    <span
      aria-hidden="true"
      style={{
        display: 'inline-block',
        flex: 'none',
        width: size,
        height: size,
        borderRadius: '50%',
        boxSizing: 'border-box',
        background: hollow ? '#fff' : color,
        border: hollow ? `1.5px solid ${color}` : undefined,
      }}
    />
  )
}

/** 角色标签：中性底色 + 角色语义色点，颜色沿用 userTableColors 的角色配色。 */
export function RoleChip({ roleName }: { roleName: string }) {
  return (
    <span style={chipStyle} title={roleName}>
      <AccentDot color={getRoleAccentColor(roleName)} />
      <span style={{ overflow: 'hidden', textOverflow: 'ellipsis' }}>{roleName}</span>
    </span>
  )
}

/** 中性标签：分店等不带语义的取值，不再按名称哈希上色。 */
export function NeutralChip({ children, outlined = false, title }: { children: ReactNode; outlined?: boolean; title?: string }) {
  return (
    <span
      title={title}
      style={{
        ...chipStyle,
        background: outlined ? '#fff' : chipStyle.background,
        border: outlined ? '1px solid #d5dae3' : undefined,
      }}
    >
      {children}
    </span>
  )
}

export function StatusDot({ active, label }: { active: boolean; label: string }) {
  return (
    <span style={{ display: 'inline-flex', alignItems: 'center', gap: 6, color: active ? undefined : '#667085', whiteSpace: 'nowrap' }}>
      <AccentDot color={active ? '#52c41a' : '#b4bac4'} />
      {label}
    </span>
  )
}

/**
 * 待保存更改栏：贴在所在滚动容器底部，统一承载「放弃 / 保存」。
 * 没有更改时不渲染，避免常驻按钮误导用户以为还有东西没保存。
 */
export function PendingChangesBar({
  visible,
  summary,
  detail,
  discardLabel,
  saveLabel,
  saving,
  saveDisabled = false,
  onDiscard,
  onSave,
}: {
  visible: boolean
  summary: ReactNode
  detail?: ReactNode
  discardLabel: string
  saveLabel: string
  saving: boolean
  saveDisabled?: boolean
  onDiscard: () => void
  onSave: () => void
}) {
  if (!visible) return null

  return (
    <div
      role="status"
      style={{
        position: 'sticky',
        bottom: 0,
        zIndex: 3,
        display: 'flex',
        flexWrap: 'wrap',
        alignItems: 'center',
        gap: 12,
        padding: '12px 20px',
        borderTop: '1px solid #e8ebf0',
        background: '#fbfcfe',
      }}
    >
      <span style={{ display: 'flex', flexWrap: 'wrap', alignItems: 'center', gap: 8, minWidth: 0 }}>
        <InfoCircleFilled style={{ color: '#1677ff' }} />
        <strong>{summary}</strong>
        {detail ? <span style={{ color: '#667085' }}>{detail}</span> : null}
      </span>
      <span style={{ display: 'flex', gap: 8, marginLeft: 'auto' }}>
        <Button disabled={saving} onClick={onDiscard}>
          {discardLabel}
        </Button>
        <Button type="primary" loading={saving} disabled={saveDisabled} onClick={onSave}>
          {saveLabel}
        </Button>
      </span>
    </div>
  )
}
