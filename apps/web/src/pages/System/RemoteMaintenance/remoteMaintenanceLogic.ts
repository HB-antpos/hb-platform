import type {
  RemoteMaintenanceDevice,
  RemoteMaintenanceDeviceQuery,
  RemoteOnlineStatus,
  RemoteServiceStatus,
} from '../../../types/remoteMaintenance'

export function createRemoteMaintenanceRequestGate() {
  let sequence = 0
  return {
    begin() {
      sequence += 1
      return sequence
    },
    invalidate() {
      sequence += 1
    },
    isCurrent(requestId: number) {
      return requestId === sequence
    },
  }
}

export function buildRemoteMaintenanceQuery(
  page: number,
  pageSize: number,
  filters: {
    keyword: string
    storeCode: string
    onlineStatus?: RemoteOnlineStatus
    serviceStatus?: RemoteServiceStatus
  },
): RemoteMaintenanceDeviceQuery {
  return {
    page,
    pageSize,
    ...(filters.keyword.trim() ? { keyword: filters.keyword.trim() } : {}),
    ...(filters.storeCode.trim() ? { storeCode: filters.storeCode.trim() } : {}),
    ...(filters.onlineStatus ? { onlineStatus: filters.onlineStatus } : {}),
    ...(filters.serviceStatus ? { serviceStatus: filters.serviceStatus } : {}),
  }
}

export function isRemoteMaintenanceVisible() {
  return typeof document === 'undefined' || document.visibilityState !== 'hidden'
}

export interface RemoteMaintenancePollSchedulerOptions {
  isVisible: () => boolean
  refresh: () => Promise<void>
  onInvalidate: () => void
  setTimer: (callback: () => void, delayMs: number) => number
  clearTimer: (timer: number) => void
}

/** 将页面可见性、定时器和请求失效绑定在同一生命周期，卸载后 promise finally 不能复活轮询。 */
export function createRemoteMaintenancePollScheduler(options: RemoteMaintenancePollSchedulerOptions) {
  let disposed = false
  let timer: number | undefined

  const clearPoll = () => {
    if (timer !== undefined) options.clearTimer(timer)
    timer = undefined
  }

  const schedulePoll = () => {
    if (disposed) return
    clearPoll()
    if (!options.isVisible()) return
    timer = options.setTimer(() => {
      if (disposed) return
      void options.refresh().finally(() => {
        if (!disposed) schedulePoll()
      })
    }, 10_000)
  }

  const refreshAndSchedule = () => {
    if (disposed) return
    void options.refresh().finally(() => {
      if (!disposed) schedulePoll()
    })
  }

  return {
    start() {
      if (options.isVisible()) refreshAndSchedule()
    },
    handleVisibilityChange() {
      if (disposed) return
      clearPoll()
      options.onInvalidate()
      if (options.isVisible()) refreshAndSchedule()
    },
    dispose() {
      if (disposed) return
      disposed = true
      clearPoll()
      options.onInvalidate()
    },
  }
}

export function getRemoteOnlineStatusColor(status: RemoteOnlineStatus) {
  return status === 'online' ? 'green' : status === 'offline' ? 'orange' : 'default'
}

export function getRemoteServiceStatusColor(status: RemoteServiceStatus) {
  switch (status) {
    case 'running':
      return 'green'
    case 'starting':
    case 'stopping':
      return 'processing'
    case 'stopped':
      return 'orange'
    case 'checkFailed':
      return 'red'
    default:
      return 'default'
  }
}

/**
 * 「客户端版本」列显示的版本：优先心跳观察到的实际版本，还没上报或读不到时退回登记版本；
 * 只有偏离和程序缺失两种情况才给标签（一致 / 无法判断不加标签，避免整列都是标签）。
 */
export function getRustDeskVersionDisplay(device: Pick<RemoteMaintenanceDevice, 'clientVersion' | 'observedRustDeskVersion' | 'rustDeskVersionStatus'>) {
  const version = device.observedRustDeskVersion || device.clientVersion || '--'
  const tagKey = device.rustDeskVersionStatus === 'drift' || device.rustDeskVersionStatus === 'missing'
    ? device.rustDeskVersionStatus
    : null
  return { version, tagKey }
}

export function formatRemoteMaintenanceDateTime(value: string | null) {
  if (!value) return '--'
  const date = new Date(value)
  return Number.isNaN(date.getTime()) ? value : date.toLocaleString()
}

export function formatRemoteMaintenanceFileSize(sizeBytes: number) {
  if (!sizeBytes) return '--'
  if (sizeBytes >= 1024 * 1024) return `${(sizeBytes / 1024 / 1024).toFixed(1)} MB`
  return `${(sizeBytes / 1024).toFixed(1)} KB`
}
