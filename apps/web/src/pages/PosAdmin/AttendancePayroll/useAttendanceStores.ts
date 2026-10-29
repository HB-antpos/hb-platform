import { message } from 'antd'
import { useEffect, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { getActiveStores } from '../../../services/storeService'
import { useAuthStore } from '../../../store/auth'
import {
  buildStoreOptionsFromUserStores,
  filterStoreOptionsByManagedCodes,
} from '../../../utils/managedStoreScope'

export interface AttendanceStoreOption {
  label: string
  value: string
}

/**
 * 考勤审核 / 工时与工资共用的分店选项：只列可管理的分店（与排班考勤页同一套范围规则），
 * required = true 时默认选中第一家（工资按店按期算，必须有店）。
 */
export function useAttendanceStores(required: boolean) {
  const { t } = useTranslation()
  const access = useAuthStore((state) => state.access)
  const currentUser = useAuthStore((state) => state.currentUser)
  const managedStoreCodes = access.managedStoreCodes?.()
  const managedKey = managedStoreCodes?.join(',') ?? 'all'
  const visibleStoreCodes = access.visibleStoreCodes?.()
  const [storeOptions, setStoreOptions] = useState<AttendanceStoreOption[]>([])
  const [storeCode, setStoreCode] = useState<string | undefined>()

  useEffect(() => {
    let cancelled = false
    const apply = (options: AttendanceStoreOption[]) => {
      if (cancelled) return
      const managed = filterStoreOptionsByManagedCodes(options, managedStoreCodes)
      setStoreOptions(managed)
      setStoreCode((current) => {
        if (current && managed.some((option) => option.value === current)) return current
        return required ? managed[0]?.value : undefined
      })
    }
    if (visibleStoreCodes !== null) {
      apply(buildStoreOptionsFromUserStores(currentUser?.stores))
    } else {
      void getActiveStores().then(apply).catch(() => {
        if (!cancelled) message.error(t('posAdmin.scheduleAttendance.messages.loadStoresFailed'))
      })
    }
    return () => {
      cancelled = true
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [currentUser?.stores, managedKey, required])

  return { storeOptions, storeCode, setStoreCode }
}
