import { useEffect, useMemo, useState } from 'react'
import { getLocalSupplierCategoryTree } from '../../../services/localSupplierCategoryService'
import {
  createSupplierCategoryTreeCache,
  type SupplierCategoryTreeCache,
  type SupplierCategoryTreeFetcher,
} from './supplierCategoryTreeCache'

export interface SupplierCategoryTreesApi extends Omit<SupplierCategoryTreeCache, 'subscribe'> {
  /** 缓存每次变化递增，供 useMemo 依赖（缓存对象本身引用不变）。 */
  version: number
}

/**
 * 商品管理页共享的供应商分类树：顶部级联框、编辑表单与管理弹窗用同一份缓存，
 * 管理弹窗里切换促销后级联框与表单也能立即看到。
 */
export function useSupplierCategoryTrees(fetcher: SupplierCategoryTreeFetcher = getLocalSupplierCategoryTree): SupplierCategoryTreesApi {
  const [cache] = useState(() => createSupplierCategoryTreeCache(fetcher))
  const [version, setVersion] = useState(0)

  useEffect(() => cache.subscribe(() => setVersion((value) => value + 1)), [cache])

  return useMemo(() => ({
    version,
    get: cache.get,
    ensure: cache.ensure,
    reload: cache.reload,
    invalidate: cache.invalidate,
    patchNode: cache.patchNode,
  }), [cache, version])
}
