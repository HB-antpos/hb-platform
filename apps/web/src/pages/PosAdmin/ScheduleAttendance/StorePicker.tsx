import { DoubleLeftOutlined, PushpinOutlined, SearchOutlined } from '@ant-design/icons'
import { Checkbox, Input, Switch, Tooltip } from 'antd'
import type { InputRef } from 'antd'
import type { KeyboardEvent, ReactNode } from 'react'
import { useEffect, useMemo, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'

import { ALL_STORES_TOKEN, buildStoreSections, moveActiveIndex } from './storePickerLogic'
import type { StorePickerOption } from './storePickerLogic'
import './storePicker.css'
import './registerScheduleAttendanceMessages'

/**
 * 分店列表：侧栏（docked）和工具栏按钮弹出的弹层（floating）共用。
 * 点一下即切换；「最近使用」置顶；可按名称 / 编码搜索，↑↓ 移动、回车选中。
 */
/**
 * 可选的「多选」模式（默认不传 = 只能单选，其他页面不受影响）：
 * 头部多一个「多选」开关；开着时每家分店前有勾选框，点一下勾选 / 取消，「全部分店」= 清空选择（不限分店）。
 */
export interface StorePickerMulti {
  enabled: boolean
  /** 已勾选的分店代码（按点选顺序）。 */
  selected: readonly string[]
  onEnabledChange: (enabled: boolean) => void
  /** 勾选 / 取消勾选一家。 */
  onToggle: (storeCode: string) => void
  /** 点了「全部分店」：清空选择。 */
  onClear: () => void
}

interface StorePickerProps {
  variant: 'docked' | 'floating'
  options: readonly StorePickerOption[]
  value?: string
  recent: readonly string[]
  /** 是否提供「全部分店」（不限分店）这一项。 */
  allowAll: boolean
  onSelect: (storeCode: string | undefined) => void
  /** docked：收起成工具栏按钮；floating：固定到左侧。 */
  onToggleDock: () => void
  /** 弹层打开时自动聚焦搜索框。 */
  autoFocus?: boolean
  /**
   * 每家分店右侧的当期状态（键为分店代码，'*' 表示「全部分店」那一项）：
   * 考勤审核 = 本期待审数，工时 / 工资表 = 是否已锁定。没有就不显示。
   */
  badges?: Readonly<Record<string, ReactNode>>
  /** 是否记住「上次所选分店」：排班考勤类页面会记，底部提示才写「记住上次分店」；设备管理等页面不记，传 false。 */
  remembersLastStore?: boolean
  /** 多选模式；不传则只能单选。 */
  multi?: StorePickerMulti
}

interface FlatItem {
  key: string
  value: string | undefined
  label: string
  code?: string
}

const ALL_KEY = '__all__'

export default function StorePicker({ variant, options, value, recent, allowAll, onSelect, onToggleDock, autoFocus, badges, remembersLastStore = true, multi }: StorePickerProps) {
  const multiOn = multi?.enabled === true
  const { t } = useTranslation()
  const [keyword, setKeyword] = useState('')
  const [activeIndex, setActiveIndex] = useState(-1)
  const listRef = useRef<HTMLDivElement>(null)
  const inputRef = useRef<InputRef>(null)
  const sections = useMemo(() => buildStoreSections(options, recent, keyword), [keyword, options, recent])

  // 键盘导航按「看到的顺序」走：全部分店 → 最近使用 → 全部分店列表（最近使用的分店会出现两次，各算一项）
  const flat = useMemo<FlatItem[]>(() => {
    const items: FlatItem[] = []
    if (allowAll && !keyword.trim()) items.push({ key: ALL_KEY, value: undefined, label: t('posAdmin.scheduleAttendance.storePicker.allStores') })
    sections.recent.forEach((option) => items.push({ key: `recent-${option.value}`, value: option.value, label: option.label, code: option.value }))
    sections.all.forEach((option) => items.push({ key: `all-${option.value}`, value: option.value, label: option.label, code: option.value }))
    return items
  }, [allowAll, keyword, sections, t])

  useEffect(() => setActiveIndex(keyword.trim() ? 0 : -1), [keyword])

  // 弹层有入场动画，挂载那一刻聚焦会被吞掉：等一帧再聚焦，打开就能直接打字搜索
  useEffect(() => {
    if (!autoFocus) return undefined
    const timer = window.setTimeout(() => inputRef.current?.focus({ preventScroll: true }), 60)
    return () => window.clearTimeout(timer)
  }, [autoFocus])

  // 高亮项滚进可视区
  useEffect(() => {
    if (activeIndex < 0) return
    const node = listRef.current?.querySelector<HTMLElement>(`[data-index="${activeIndex}"]`)
    node?.scrollIntoView({ block: 'nearest' })
  }, [activeIndex])

  const choose = (item: FlatItem | undefined) => {
    if (!item) return
    if (multi && multiOn) {
      // 多选：勾选 / 取消 / 清空；不清搜索词，方便连着勾几家
      if (item.value === undefined) multi.onClear()
      else multi.onToggle(item.value)
      return
    }
    onSelect(item.value)
    setKeyword('')
  }

  const handleKeyDown = (event: KeyboardEvent<HTMLInputElement>) => {
    if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
      event.preventDefault()
      setActiveIndex((prev) => moveActiveIndex(prev, event.key === 'ArrowDown' ? 1 : -1, flat.length))
    } else if (event.key === 'Enter') {
      event.preventDefault()
      choose(flat[activeIndex >= 0 ? activeIndex : 0])
    }
  }

  const renderItem = (item: FlatItem, index: number) => {
    // 多选开着：「全部分店」= 一家都没勾；其余按是否已勾选。单选：与所选值相等。
    const selected = multi && multiOn
      ? item.value === undefined ? multi.selected.length === 0 : multi.selected.includes(item.value)
      : item.value === value
    const badge = badges?.[item.value ?? ALL_STORES_TOKEN]
    return (
      <button
        key={item.key}
        type="button"
        data-index={index}
        className={`ssp-item${selected ? ' is-selected' : ''}${index === activeIndex ? ' is-active' : ''}`}
        aria-current={selected ? 'true' : undefined}
        title={item.code ? `${item.label} · ${item.code}` : item.label}
        onClick={() => choose(item)}
        onMouseEnter={() => setActiveIndex(index)}
      >
        {/* 勾选框只是状态展示，点击由整行按钮处理（不参与 Tab 顺序） */}
        {multiOn && item.value !== undefined ? <Checkbox checked={selected} tabIndex={-1} style={{ pointerEvents: 'none' }} /> : null}
        <span className="ssp-item-name">{item.label}</span>
        {item.code ? <span className="ssp-item-code">{item.code}</span> : null}
        {/* 数字 0 也要显示（某家店 0 台 / 0 条）：只排除没给、null、false */}
        {badge !== undefined && badge !== null && badge !== false ? <span className="ssp-item-badge">{badge}</span> : null}
      </button>
    )
  }

  const recentStart = flat.findIndex((item) => item.key.startsWith('recent-'))
  const allStart = flat.findIndex((item) => item.key.startsWith('all-'))
  const allItem = flat[0]?.key === ALL_KEY ? flat[0] : undefined

  return (
    <div className={`ssp ssp--${variant}`}>
      <div className="ssp-head">
        <span className="ssp-title">{t('posAdmin.scheduleAttendance.storePicker.title')}</span>
        <span className="ssp-count">{options.length}</span>
        {multi ? (
          <label className="ssp-multi">
            <Switch size="small" checked={multiOn} onChange={multi.onEnabledChange} aria-label={t('posAdmin.scheduleAttendance.storePicker.multi')} />
            <span>{t('posAdmin.scheduleAttendance.storePicker.multi')}</span>
          </label>
        ) : null}
        {variant === 'docked' ? (
          <Tooltip title={t('posAdmin.scheduleAttendance.storePicker.collapse')}>
            <button type="button" className="ssp-dock-toggle" aria-label={t('posAdmin.scheduleAttendance.storePicker.collapse')} onClick={onToggleDock}>
              <DoubleLeftOutlined />
            </button>
          </Tooltip>
        ) : (
          <button type="button" className="ssp-pin" onClick={onToggleDock}>
            <PushpinOutlined /> {t('posAdmin.scheduleAttendance.storePicker.pin')}
          </button>
        )}
      </div>
      <div className="ssp-search">
        <Input
          allowClear
          size="small"
          ref={inputRef}
          prefix={<SearchOutlined />}
          placeholder={t('posAdmin.scheduleAttendance.storePicker.searchPlaceholder')}
          value={keyword}
          onChange={(event) => setKeyword(event.target.value)}
          onKeyDown={handleKeyDown}
        />
      </div>
      <div className="ssp-list" ref={listRef} role="listbox">
        {allItem ? renderItem(allItem, 0) : null}
        {recentStart >= 0 ? (
          <>
            <div className="ssp-group">{t('posAdmin.scheduleAttendance.storePicker.recent')}</div>
            {flat.slice(recentStart, allStart < 0 ? undefined : allStart).map((item, offset) => renderItem(item, recentStart + offset))}
          </>
        ) : null}
        {allStart >= 0 ? (
          <>
            {!keyword.trim() && recentStart >= 0 ? <div className="ssp-group">{t('posAdmin.scheduleAttendance.storePicker.all')}</div> : null}
            {flat.slice(allStart).map((item, offset) => renderItem(item, allStart + offset))}
          </>
        ) : (
          keyword.trim() ? <div className="ssp-empty">{t('posAdmin.scheduleAttendance.storePicker.empty')}</div> : null
        )}
      </div>
      {variant === 'docked' ? (
        <div className="ssp-foot">
          {t(multiOn
            ? 'posAdmin.scheduleAttendance.storePicker.footHintMulti'
            : remembersLastStore ? 'posAdmin.scheduleAttendance.storePicker.footHint' : 'posAdmin.scheduleAttendance.storePicker.footHintNoMemory')}
        </div>
      ) : null}
    </div>
  )
}
