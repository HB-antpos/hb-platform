import { message } from 'antd'
import { useEffect } from 'react'
import { useTranslation } from 'react-i18next'
import { getUserStores } from '../services/userService'
import { useShopStore } from '../store/shop'

/**
 * 进入商城或切换登录用户时拉取可订货分店，并在只有一家分店时自动选中。
 *
 * effect 不能依赖 selectedStore 对象：setUserStores 每次都会换成新数组里同 storeCode 的新对象，
 * 引用一变 effect 就重跑、再次拉取，选中分店后会按网络往返节奏无限请求分店列表。
 */
export function useShopUserStores(userGuid: string | undefined) {
  const { t } = useTranslation()
  const setUserStores = useShopStore((state) => state.setUserStores)
  const setSelectedStore = useShopStore((state) => state.setSelectedStore)
  const resetShop = useShopStore((state) => state.reset)

  useEffect(() => {
    let cancelled = false

    const fetchStores = async () => {
      if (!userGuid) {
        resetShop()
        return
      }

      try {
        const stores = (await getUserStores(userGuid)).slice().sort((left, right) =>
          (left.storeName || left.storeCode || '').localeCompare(right.storeName || right.storeCode || '', undefined, {
            sensitivity: 'base',
          }),
        )
        if (cancelled) {
          return
        }

        setUserStores(stores)
        // 列表回来后再读取最新的选中分店（setUserStores 可能刚清掉已失效的分店），不经闭包也不进依赖。
        if (!useShopStore.getState().selectedStore && stores.length === 1) {
          setSelectedStore(stores[0])
        }
      } catch {
        if (!cancelled) {
          message.error(t('shop.loadStoresFailed', 'Failed to load stores'))
        }
      }
    }

    void fetchStores()

    return () => {
      cancelled = true
    }
  }, [userGuid, resetShop, setSelectedStore, setUserStores, t])
}
