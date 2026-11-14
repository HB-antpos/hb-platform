import { Alert, Button, Radio, Tag } from 'antd'
import StatusPill from '../../../components/listToolbar/StatusPill'
import type { PosSettingDefinition } from '../../../types/posSettings'
import { deviceStatusTone, EMPTY_CELL, TRI_STATES, type EffectiveValue, type SettingLayer, type TriState } from './logic'

export type Tr = (key: string, params?: Record<string, string | number>) => string

// 文案键必须以 tr('字面量') 调用的形式出现：契约测试按这个形式扫描键，才能发现孤立键与缺失键。
// 所以设置项、端类型、来源等「键 → 文案」的映射都写成 switch，而不是拼接键名。

/** 设置项名称；目录里有但文案里还没补的键回退显示键名。 */
export function settingName(key: string, tr: Tr): string {
  switch (key) {
    case 'cashMovement.enabled': return tr('settings.cashMovement.name')
    case 'cashDrawer.noSaleRescan': return tr('settings.noSaleRescan.name')
    case 'returns.rescan': return tr('settings.returnsRescan.name')
    case 'receipt.bankReceipt': return tr('settings.bankReceipt.name')
    case 'payment.manualCard': return tr('settings.manualCard.name')
    case 'payment.voucher': return tr('settings.voucher.name')
    case 'payment.orderList': return tr('settings.orderList.name')
    case 'maintenance.autoRestart': return tr('settings.autoRestart.name')
    case 'zeller.unmatchedRefund': return tr('settings.zellerUnmatchedRefund.name')
    default: return key
  }
}

/** 设置项一句话说明；未知键没有说明（返回 null）。 */
export function settingDescription(key: string, tr: Tr): string | null {
  switch (key) {
    case 'cashMovement.enabled': return tr('settings.cashMovement.desc')
    case 'cashDrawer.noSaleRescan': return tr('settings.noSaleRescan.desc')
    case 'returns.rescan': return tr('settings.returnsRescan.desc')
    case 'receipt.bankReceipt': return tr('settings.bankReceipt.desc')
    case 'payment.manualCard': return tr('settings.manualCard.desc')
    case 'payment.voucher': return tr('settings.voucher.desc')
    case 'payment.orderList': return tr('settings.orderList.desc')
    case 'maintenance.autoRestart': return tr('settings.autoRestart.desc')
    case 'zeller.unmatchedRefund': return tr('settings.zellerUnmatchedRefund.desc')
    default: return null
  }
}

export function clientKindLabel(kind: string, tr: Tr): string {
  switch (kind) {
    case 'wpf': return tr('clientKind.wpf')
    case 'ipad': return tr('clientKind.ipad')
    case 'handheld': return tr('clientKind.handheld')
    case 'other': return tr('clientKind.other')
    default: return kind || EMPTY_CELL
  }
}

/** 三态选项文案：「不覆盖」这一项随层级改名（全局 = 未设置、门店 = 跟随全局、设备 = 跟随门店）。 */
export function triLabel(layer: SettingLayer, tri: TriState, tr: Tr): string {
  if (tri === 'on') return tr('tri.on')
  if (tri === 'off') return tr('tri.off')
  if (layer === 'global') return tr('tri.inheritGlobal')
  return layer === 'store' ? tr('tri.inheritStore') : tr('tri.inheritDevice')
}

export function sourceLabel(source: string, tr: Tr): string {
  switch (source) {
    case 'global': return tr('source.global')
    case 'store': return tr('source.store')
    case 'device': return tr('source.device')
    case 'default': return tr('source.default')
    default: return tr('source.none')
  }
}

export function deviceStatusLabel(status: string, tr: Tr): string {
  switch (status) {
    case 'applied': return tr('deviceStatus.applied')
    case 'pending': return tr('deviceStatus.pending')
    default: return tr('deviceStatus.never')
  }
}

/** 设备状态标签：绿 = 已应用最新、橙 = 待同步、灰 = 尚未同步（悬停说明原因）。 */
export function DeviceStatusPill({ status, tr }: { status: string; tr: Tr }) {
  let hint: string | undefined
  if (status === 'pending') hint = tr('deviceStatus.pendingHint')
  else if (status !== 'applied') hint = tr('deviceStatus.neverHint')
  return <StatusPill tone={deviceStatusTone(status)} title={hint}>{deviceStatusLabel(status, tr)}</StatusPill>
}

/** 设置项名称单元格：名称 +「新」标记 + 一句话说明 + 适用端。 */
export function SettingNameCell({ definition, tr, showClients = true }: {
  definition: Pick<PosSettingDefinition, 'key' | 'isNew' | 'clientKinds'>
  tr: Tr
  showClients?: boolean
}) {
  const description = settingDescription(definition.key, tr)
  return (
    <div className="pos-settings-name-cell">
      <div className="pos-settings-name">
        <span>{settingName(definition.key, tr)}</span>
        {definition.isNew ? <Tag color="orange" className="pos-settings-new">{tr('newBadge')}</Tag> : null}
      </div>
      {description ? <div className="pos-settings-desc">{description}</div> : null}
      {showClients ? (
        <div className="pos-settings-desc">{definition.clientKinds.map((kind) => clientKindLabel(kind, tr)).join(' · ')}</div>
      ) : null}
    </div>
  )
}

/** 三态单选：未设置 / 跟随上一层、开、关。只读时仍显示当前选择，但不可点。 */
export function TriControl({ layer, value, onChange, disabled, label, tr }: {
  layer: SettingLayer
  value: TriState
  onChange: (next: TriState) => void
  disabled: boolean
  /** 读屏用的控件名称（设置项名称）。 */
  label: string
  tr: Tr
}) {
  return (
    <Radio.Group
      className={`pos-settings-tri is-${value}`}
      optionType="button"
      buttonStyle="solid"
      size="small"
      value={value}
      disabled={disabled}
      aria-label={label}
      onChange={(event) => onChange(event.target.value as TriState)}
      options={TRI_STATES.map((tri) => ({ value: tri, label: triLabel(layer, tri, tr) }))}
    />
  )
}

/** 开 / 关 / 沿用本机 的胶囊：颜色之外始终带文字。 */
export function ValueTag({ value, tr }: { value: boolean | null | undefined; tr: Tr }) {
  if (value === true) return <Tag color="green" className="pos-settings-value">{tr('value.on')}</Tag>
  if (value === false) return <Tag className="pos-settings-value">{tr('value.off')}</Tag>
  if (value === null) return <Tag className="pos-settings-value is-native">{tr('value.native')}</Tag>
  return <span className="pos-settings-muted">{EMPTY_CELL}</span>
}

/** 最终值 + 来源：「开 来自门店」。 */
export function EffectiveCell({ effective, tr }: { effective: EffectiveValue | undefined; tr: Tr }) {
  if (!effective) return <span className="pos-settings-muted">{EMPTY_CELL}</span>
  return (
    <span className="pos-settings-effective">
      <ValueTag value={effective.value} tr={tr} />
      <span className="pos-settings-muted">{sourceLabel(effective.source, tr)}</span>
    </span>
  )
}

export function LoadErrorAlert({ message, tr, onRetry }: { message: string; tr: Tr; onRetry: () => void }) {
  return (
    <Alert
      type="error"
      showIcon
      message={message}
      action={<Button size="small" onClick={onRetry}>{tr('actions.retry')}</Button>}
    />
  )
}
