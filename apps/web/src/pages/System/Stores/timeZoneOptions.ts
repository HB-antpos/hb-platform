export const storeTimeZoneOptions = [
  { value: 'Australia/Brisbane', label: 'Australia/Brisbane (Queensland)' },
  { value: 'Australia/Sydney', label: 'Australia/Sydney (New South Wales)' },
  { value: 'Australia/Melbourne', label: 'Australia/Melbourne (Victoria)' },
]

export const UNSET_STORE_TIME_ZONE_FILTER = '__unset__'

// 列表里的紧凑写法：全称会撑成两行，列表只显示「城市 · 州」，全称放进 tooltip。
const storeTimeZoneShortLabels: Record<string, string> = {
  'Australia/Brisbane': 'Brisbane · QLD',
  'Australia/Sydney': 'Sydney · NSW',
  'Australia/Melbourne': 'Melbourne · VIC',
}

export function formatStoreTimeZoneShort(timeZoneId?: string) {
  if (!timeZoneId) {
    return '--'
  }

  return storeTimeZoneShortLabels[timeZoneId] ?? timeZoneId
}

export function formatStoreTimeZoneId(timeZoneId?: string) {
  if (!timeZoneId) {
    return '--'
  }

  return storeTimeZoneOptions.find((option) => option.value === timeZoneId)?.label ?? timeZoneId
}
