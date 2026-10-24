import type { TFunction } from 'i18next'
import type { KeyboardEvent } from 'react'

import type { PosApiEntryMode } from '../../../types/posApiEntry'

import { EMPTY_VALUE } from './deviceCells'
import { POS_API_ENTRY_MODES, type DeviceRouteState } from './posApiEntryLogic'

// 文案键写成字面量，页面文案契约测试才能扫描到并校验中英文都存在
export const MODE_LABEL_KEY: Record<PosApiEntryMode, string> = {
  edge: 'posAdmin.devices.apiEntry.modes.edge',
  aws: 'posAdmin.devices.apiEntry.modes.aws',
  direct: 'posAdmin.devices.apiEntry.modes.direct',
}
/** 备用顺序小标签里 AWS 悉尼简写为 AWS。 */
export const MODE_SHORT_KEY: Record<PosApiEntryMode, string> = {
  edge: 'posAdmin.devices.apiEntry.modesShort.edge',
  aws: 'posAdmin.devices.apiEntry.modesShort.aws',
  direct: 'posAdmin.devices.apiEntry.modesShort.direct',
}

/** 线路徽标：三条线路各一种颜色（edge 蓝 / aws 紫 / direct 绿），回退中用琥珀色。 */
export function RouteBadge({ mode, t }: { mode: PosApiEntryMode; t: TFunction }) {
  return <span className={`dev-entry-badge dev-entry-badge-${mode}`}>{t(MODE_LABEL_KEY[mode])}</span>
}

/** 备用顺序：「备用 AWS → 直连」。 */
export function FallbackOrder({ order, t }: { order: PosApiEntryMode[]; t: TFunction }) {
  return (
    <span className="dev-entry-order">
      <span className="dev-entry-order-prefix">{t('posAdmin.devices.apiEntry.fallbackOrderPrefix')}</span>
      {order.map((mode, index) => (
        <span key={mode} className="dev-entry-order-item">
          {index > 0 ? <span className="dev-entry-order-arrow" aria-hidden="true">→</span> : null}
          <span className={`dev-entry-order-tag dev-entry-order-tag-${mode}`}>{t(MODE_SHORT_KEY[mode])}</span>
        </span>
      ))}
    </span>
  )
}

/** 设备列表「线路」列：实际入口徽标；回退中 / 版本过低各带一行小字。 */
export function DeviceRouteCell({ state, t }: { state: DeviceRouteState; t: TFunction }) {
  if (state.kind === 'unknown') {
    return <span className="dev-mgmt-faint">{EMPTY_VALUE}</span>
  }
  if (state.kind === 'fallback') {
    return (
      <div className="dev-mgmt-two">
        <span className="dev-entry-badge dev-entry-badge-fallback">
          {t('posAdmin.devices.apiEntry.route.fallingBack', { route: t(MODE_LABEL_KEY[state.actual]) })}
        </span>
        {/* 回退开始时间后端没有记录（lastApiEntryAt 只是最近一次写入时间），所以只写首选，不编「已回退 N 分钟」 */}
        <span className="dev-mgmt-sub dev-mgmt-ellipsis">
          {t('posAdmin.devices.apiEntry.route.preferredHint', { route: t(MODE_LABEL_KEY[state.preferred]) })}
        </span>
      </div>
    )
  }
  if (state.kind === 'unsupported') {
    return (
      <div className="dev-mgmt-two">
        <RouteBadge mode={state.actual} t={t} />
        <span className="dev-mgmt-sub">{t('posAdmin.devices.apiEntry.route.unsupported')}</span>
      </div>
    )
  }
  return <RouteBadge mode={state.actual} t={t} />
}

/**
 * 单选组的方向键：左右 / 上下在三个选项间移动焦点（以当前获得焦点的选项为起点）。
 * 带 select 时同时选中（弹窗里的卡片是纯本地状态）；行内切换点一下会弹确认框，所以只移动焦点不选中。
 */
export function handleRadioArrowKey(
  event: KeyboardEvent<HTMLElement>,
  options?: { select?: (mode: PosApiEntryMode) => void },
) {
  const delta = event.key === 'ArrowRight' || event.key === 'ArrowDown' ? 1
    : event.key === 'ArrowLeft' || event.key === 'ArrowUp' ? -1
      : 0
  if (delta === 0) {
    return
  }
  event.preventDefault()
  const radios = Array.from(event.currentTarget.querySelectorAll<HTMLElement>('[role="radio"]'))
  const focusedIndex = radios.indexOf(document.activeElement as HTMLElement)
  const nextIndex = ((focusedIndex < 0 ? 0 : focusedIndex) + delta + radios.length) % radios.length
  radios[nextIndex]?.focus()
  options?.select?.(POS_API_ENTRY_MODES[nextIndex])
}
