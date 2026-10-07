import { Tooltip } from 'antd'
import type { TFunction } from 'i18next'
import { useTranslation } from 'react-i18next'
import type { StoreReceiptProfileStatusItem } from '../../../types/storeReceiptProfile'
import { formatPublishedAt, summarizeDeviceApply } from './receiptProfileLogic'

/** 状态文案：用字面量映射而不是拼接键名，页面消息契约测试才能扫到每个键。 */
function statusLabel(status: StoreReceiptProfileStatusItem['status'], t: TFunction) {
  switch (status) {
    case 'synced':
      return t('system.stores.receiptProfile.statusSynced')
    case 'pending':
      return t('system.stores.receiptProfile.statusPending')
    default:
      return t('system.stores.receiptProfile.statusNever')
  }
}

function statusTooltipLines(item: StoreReceiptProfileStatusItem, t: TFunction): string[] {
  const lines: string[] = []
  if (item.status === 'never') {
    lines.push(t('system.stores.receiptProfile.tipNever'))
    return lines
  }
  lines.push(
    item.status === 'pending'
      ? t('system.stores.receiptProfile.tipPending', { version: item.latestVersion })
      : t('system.stores.receiptProfile.tipSynced', { version: item.latestVersion }),
  )
  const publishedAt = formatPublishedAt(item.publishedAtUtc)
  if (publishedAt) {
    lines.push(
      item.publishedBy
        ? t('system.stores.receiptProfile.tipPublishedBy', { time: publishedAt.local, name: item.publishedBy })
        : t('system.stores.receiptProfile.tipPublished', { time: publishedAt.local }),
    )
  }
  return lines
}

interface ReceiptProfileStatusCellProps {
  /** 该分店的下发状态；还没拿到（加载中）或拿不到（接口失败/门店已不存在）时为 undefined。 */
  status?: StoreReceiptProfileStatusItem
  /** 状态正在异步加载：没有数据时显示骨架占位，而不是空白。 */
  loading: boolean
  onOpenDevices: (item: StoreReceiptProfileStatusItem) => void
}

/**
 * 列表「小票下发」单元格：两行，沿用「品牌 / ABN」列的紧凑两行写法，不再多占一列宽度。
 * 第一行 = 下发状态 + 版本号（悬浮显示下发时间与操作人）；第二行 = 设备应用台数（可点开设备明细）。
 */
export function ReceiptProfileStatusCell({ status, loading, onOpenDevices }: ReceiptProfileStatusCellProps) {
  const { t } = useTranslation()

  if (!status) {
    return loading ? (
      <div className="sys-store-rp-skeleton" aria-hidden="true">
        <span />
        <span />
      </div>
    ) : (
      <span className="sys-store-faint">--</span>
    )
  }

  const device = summarizeDeviceApply(status)

  return (
    <div className="sys-store-two">
      <Tooltip
        title={(
          <>
            {statusTooltipLines(status, t).map((line) => <div key={line}>{line}</div>)}
          </>
        )}
      >
        <span className={`sys-store-rp-status sys-store-rp-status-${status.status}`}>
          {statusLabel(status.status, t)}
          {status.latestVersion > 0 ? <span className="sys-store-rp-ver">v{status.latestVersion}</span> : null}
        </span>
      </Tooltip>
      {device.tone === 'none' ? null : device.tone === 'noDevices' ? (
        <span className="sys-store-sub sys-store-faint">{t('system.stores.receiptProfile.deviceNone')}</span>
      ) : (
        <Tooltip title={t('system.stores.receiptProfile.deviceOpenTip', { version: status.latestVersion })}>
          <button
            type="button"
            className={`sys-store-rp-devices sys-store-rp-devices-${device.tone}`}
            onClick={() => onOpenDevices(status)}
          >
            {t('system.stores.receiptProfile.deviceApplied', { applied: device.applied, total: device.total })}
          </button>
        </Tooltip>
      )}
    </div>
  )
}
