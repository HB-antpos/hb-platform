import { Segmented } from 'antd'
import type { CSSProperties } from 'react'
import { Fragment, useState } from 'react'
import { useTranslation } from 'react-i18next'

import { registerPageMessages } from '../../i18n/registerPageMessages'

import {
  ALL_GROUPS_KEY,
  PERMISSION_SURFACES,
} from './permissionTaxonomy'
import type {
  PermissionSurface,
  PermissionSurfaceFilter,
  TaxonomyNavGroup,
  TaxonomyNavSection,
} from './permissionTaxonomy'
import permissionTaxonomyMessagesEn from './permissionTaxonomyMessages.en.json'
import permissionTaxonomyMessagesZh from './permissionTaxonomyMessages.zh.json'

/*
 * 角色管理与权限管理共用的「分类导航 + 使用端」部件。
 * 两个页面是独立的懒加载分块，这里只用内联样式（共享 CSS 会被拆成纯 CSS 分块，触发首屏包体检查报错）。
 */

registerPageMessages({ zh: permissionTaxonomyMessagesZh, en: permissionTaxonomyMessagesEn })

/** 分区与使用端共用同一套色：收银端紫、后台蓝、移动端橙，其余中性灰。 */
const SURFACE_COLORS: Record<PermissionSurface, { fg: string; bg: string }> = {
  pos: { fg: '#7c3aed', bg: '#f3ecff' },
  web: { fg: '#0d7ac4', bg: '#e4f2fc' },
  mobile: { fg: '#c2570c', bg: '#fdeedd' },
}
const SECTION_DOT_COLORS: Record<string, string> = {
  pos: SURFACE_COLORS.pos.fg,
  business: SURFACE_COLORS.web.fg,
  system: '#64748b',
  other: '#98a2b3',
}

export function useSurfaceLabel() {
  const { t } = useTranslation()
  return (surface: PermissionSurfaceFilter) => {
    const defaults: Record<PermissionSurfaceFilter, string> = {
      all: '全部',
      pos: '收银端',
      web: '后台',
      mobile: '移动端',
    }
    return t(`system.permissionTaxonomy.surfaces.${surface}`, defaults[surface])
  }
}

export function SurfaceBadges({ surfaces }: { surfaces: readonly PermissionSurface[] }) {
  const label = useSurfaceLabel()
  return (
    <span style={{ display: 'inline-flex', flexWrap: 'wrap', gap: 4 }}>
      {surfaces.map((surface) => (
        <span
          key={surface}
          style={{
            display: 'inline-flex',
            alignItems: 'center',
            height: 18,
            padding: '0 6px',
            borderRadius: 4,
            background: SURFACE_COLORS[surface].bg,
            color: SURFACE_COLORS[surface].fg,
            fontSize: 11,
            fontWeight: 600,
            lineHeight: 1,
            whiteSpace: 'nowrap',
          }}
        >
          {label(surface)}
        </span>
      ))}
    </span>
  )
}

/** 使用端筛选：全部 / 收银端 / 后台 / 移动端。 */
export function SurfaceFilterBar({
  value,
  onChange,
}: {
  value: PermissionSurfaceFilter
  onChange: (value: PermissionSurfaceFilter) => void
}) {
  const { t } = useTranslation()
  const label = useSurfaceLabel()
  const options: PermissionSurfaceFilter[] = ['all', ...PERMISSION_SURFACES]
  return (
    <span style={{ display: 'inline-flex', alignItems: 'center', gap: 8 }}>
      <span style={{ color: '#98a2b3', fontSize: 12, whiteSpace: 'nowrap' }}>
        {t('system.permissionTaxonomy.surfaceLabel', '使用端')}
      </span>
      <Segmented<PermissionSurfaceFilter>
        aria-label={t('system.permissionTaxonomy.surfaceLabel', '使用端')}
        value={value}
        onChange={onChange}
        options={options.map((option) => ({
          value: option,
          label: (
            <span style={{ display: 'inline-flex', alignItems: 'center', gap: 6 }}>
              {option === 'all' ? null : (
                <span
                  aria-hidden="true"
                  style={{ width: 7, height: 7, borderRadius: 2, background: SURFACE_COLORS[option].fg }}
                />
              )}
              {label(option)}
            </span>
          ),
        }))}
      />
    </span>
  )
}

/**
 * 权限编码：中英文都要完整显示，不用省略号。
 * 只在「.」之后允许换行，避免从单词中间断开（如 Perce / nt）；超长无点片段兜底按字符折行。
 */
export function BreakableCode({ code, style }: { code: string; style?: CSSProperties }) {
  const parts = code.split('.')
  return (
    <span
      style={{
        fontFamily: 'ui-monospace, SFMono-Regular, Menlo, Consolas, monospace',
        fontSize: 11.5,
        lineHeight: 1.45,
        color: '#667085',
        overflowWrap: 'anywhere',
        ...style,
      }}
    >
      {parts.map((part, index) => (
        <Fragment key={index}>
          {index > 0 ? '.' : null}
          {index > 0 ? <wbr /> : null}
          {part}
        </Fragment>
      ))}
    </span>
  )
}

const navButtonBase: CSSProperties = {
  display: 'flex',
  alignItems: 'flex-start',
  justifyContent: 'space-between',
  gap: 8,
  width: '100%',
  padding: '6px 10px',
  border: 0,
  borderRadius: 8,
  background: 'transparent',
  color: '#1f2329',
  font: 'inherit',
  fontSize: 13.5,
  lineHeight: '20px',
  textAlign: 'left',
  cursor: 'pointer',
}

/**
 * 分类导航：「全部」+ 三个分区（收银端 / 后台业务 / 系统与人员）下的分组。
 * 分组名允许换行（不截断）；数量文案由调用方决定（已授予 / 总数，或仅总数）。
 */
export function PermissionTaxonomyNav({
  sections,
  activeKey,
  allCount,
  onSelect,
  showGranted,
}: {
  sections: readonly TaxonomyNavSection[]
  activeKey: string
  allCount: string
  onSelect: (key: string) => void
  /** true：显示「已授予 / 总数」；false：只显示总数。 */
  showGranted: boolean
}) {
  const { t } = useTranslation()
  const [hoverKey, setHoverKey] = useState<string | null>(null)

  const countText = (item: { total: number; granted: number }) =>
    showGranted ? `${item.granted} / ${item.total}` : String(item.total)

  const renderItem = (key: string, label: string, count: string, indent = false) => {
    const active = key === activeKey
    return (
      <button
        key={key}
        type="button"
        aria-current={active ? 'true' : undefined}
        onClick={() => onSelect(key)}
        onMouseEnter={() => setHoverKey(key)}
        onMouseLeave={() => setHoverKey((current) => (current === key ? null : current))}
        style={{
          ...navButtonBase,
          paddingLeft: indent ? 18 : 10,
          background: active ? '#e6f4ff' : hoverKey === key ? '#f4f6f9' : 'transparent',
          color: active ? '#1677ff' : '#1f2329',
          fontWeight: active ? 600 : 400,
        }}
      >
        <span style={{ minWidth: 0, overflowWrap: 'anywhere' }}>{label}</span>
        <span
          style={{
            flex: 'none',
            color: active ? '#1677ff' : '#98a2b3',
            fontSize: 12,
            fontVariantNumeric: 'tabular-nums',
          }}
        >
          {count}
        </span>
      </button>
    )
  }

  const groupLabel = (group: TaxonomyNavGroup) =>
    group.builtIn ? t(`system.permissionTaxonomy.groups.${group.key}`, group.label) : group.label

  return (
    <nav aria-label={t('system.permissionTaxonomy.title', '权限分类')} style={{ display: 'flex', flexDirection: 'column', gap: 2 }}>
      {renderItem(ALL_GROUPS_KEY, t('system.permissionTaxonomy.all', '全部'), allCount)}
      {sections.map((section) => (
        <div key={section.key} style={{ display: 'flex', flexDirection: 'column', gap: 2 }}>
          <div
            style={{
              display: 'flex',
              alignItems: 'center',
              justifyContent: 'space-between',
              gap: 8,
              padding: '12px 10px 4px',
              color: '#98a2b3',
              fontSize: 12,
              fontWeight: 600,
            }}
          >
            <span style={{ display: 'inline-flex', alignItems: 'center', gap: 6 }}>
              <span
                aria-hidden="true"
                style={{ width: 7, height: 7, borderRadius: 2, background: SECTION_DOT_COLORS[section.key] }}
              />
              {t(`system.permissionTaxonomy.sections.${section.key}`, section.label)}
            </span>
            <span style={{ fontVariantNumeric: 'tabular-nums' }}>{countText(section)}</span>
          </div>
          {section.groups.map((group) => renderItem(group.key, groupLabel(group), countText(group), true))}
        </div>
      ))}
    </nav>
  )
}
