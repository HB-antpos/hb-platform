import { DownOutlined, ShopOutlined } from '@ant-design/icons'
import { Button, Grid, Popover } from 'antd'
import { useCallback, useMemo, useState } from 'react'
import type { ReactNode } from 'react'
import { useTranslation } from 'react-i18next'

import StorePicker from './StorePicker'
import {
  STORAGE_KEYS,
  getBrowserStorage,
  parseRecentStores,
  parseSidebarPreference,
  pushRecentStore,
  safeStorageGet,
  safeStorageSet,
} from './storePickerLogic'
import type { SidebarPreference, StorePickerOption } from './storePickerLogic'

interface UseStoreSidebarInput {
  options: readonly StorePickerOption[]
  /** 当前所选分店；undefined 表示「全部分店」（需要 allowAll）。 */
  value: string | undefined
  /** 选了某家分店，或点了「全部分店」（code 为 undefined）。 */
  onSelect: (storeCode: string | undefined) => void
  /** 是否提供「全部分店」。设备管理 / 日结记录 / 总览可以不限分店；必须按单店看的页签传 false。 */
  allowAll: boolean
  /** 每家分店右侧的数字或状态（键为分店代码，'*' 表示「全部分店」那一项）。 */
  badges?: Readonly<Record<string, ReactNode>>
  /** 收起后分店按钮上的文字；缺省按所选分店 / 「全部分店」显示。当前筛选不是单店也不是全部（例如旧链接带了多家）时用它说明。 */
  valueLabel?: string
}

export interface StoreSidebar {
  /** 侧栏是否展开在左侧。 */
  docked: boolean
  /** 左侧列表本体；没有展开时为 null。 */
  sidebar: ReactNode
  /** 侧栏收起（窄屏 / 用户点了收起）时放在页面标题旁的分店按钮，点开是同一份列表；展开或只有 1 家店时为 null。 */
  switcher: ReactNode
}

/**
 * 非排班考勤页（设备管理、日结记录、现金管理）用的「左侧分店列表」。
 * 组件与样式沿用排班考勤的 StorePicker，并共用其「最近使用」「侧栏收起 / 固定」两项界面偏好，
 * 各页切换时习惯不丢；但**不读写「上次所选分店」**——这些页默认就该看全部分店，不能被考勤页上次选的店带偏。
 */
export function useStoreSidebar({ options, value, onSelect, allowAll, badges, valueLabel }: UseStoreSidebarInput): StoreSidebar {
  const { t } = useTranslation()
  const storage = useMemo(() => getBrowserStorage(), [])
  const screens = Grid.useBreakpoint()
  const [recentStores, setRecentStores] = useState<string[]>(() => parseRecentStores(safeStorageGet(getBrowserStorage(), STORAGE_KEYS.recentStores)))
  const [preference, setPreference] = useState<SidebarPreference>(() => parseSidebarPreference(safeStorageGet(getBrowserStorage(), STORAGE_KEYS.sidebarDock)))
  const [popoverOpen, setPopoverOpen] = useState(false)

  const select = useCallback((code: string | undefined) => {
    setPopoverOpen(false)
    onSelect(code)
    if (!code) return
    setRecentStores((previous) => {
      const next = pushRecentStore(previous, code)
      safeStorageSet(storage, STORAGE_KEYS.recentStores, JSON.stringify(next))
      return next
    })
  }, [onSelect, storage])

  const setDock = useCallback((next: Exclude<SidebarPreference, null>) => {
    setPreference(next)
    setPopoverOpen(false)
    safeStorageSet(storage, STORAGE_KEYS.sidebarDock, next)
  }, [storage])

  // 宽屏（≥ 992px）默认停靠在左侧；用户点过「收起 / 固定」就按用户的；只有 1 家店时不需要列表
  const docked = options.length > 1 && (preference ? preference === 'docked' : screens.lg === true)

  const sidebar = docked ? (
    <StorePicker
      variant="docked"
      options={options}
      value={value}
      recent={recentStores}
      allowAll={allowAll}
      badges={badges}
      remembersLastStore={false}
      onSelect={select}
      onToggleDock={() => setDock('floating')}
    />
  ) : null

  const label = valueLabel ?? (value
    ? options.find((option) => option.value === value)?.label ?? value
    : t('posAdmin.scheduleAttendance.storePicker.allStores'))
  const switcher = !docked && options.length > 1 ? (
    <Popover
      trigger="click"
      placement="bottomLeft"
      arrow={false}
      destroyOnHidden
      open={popoverOpen}
      onOpenChange={setPopoverOpen}
      styles={{ body: { padding: 0 } }}
      content={(
        <StorePicker
          variant="floating"
          autoFocus
          options={options}
          value={value}
          recent={recentStores}
          allowAll={allowAll}
          badges={badges}
          remembersLastStore={false}
          onSelect={select}
          onToggleDock={() => setDock('docked')}
        />
      )}
    >
      <Button className="sce-store-button" icon={<ShopOutlined />}>
        <span className="sce-store-button-name">{label}</span>
        <DownOutlined className="sce-store-button-caret" />
      </Button>
    </Popover>
  ) : null

  return { docked, sidebar, switcher }
}
