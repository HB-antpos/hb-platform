import { useEffect, useSyncExternalStore } from 'react'

import {
  getProductImageVersion,
  getProductImageVersionsRevision,
  requestProductImageVersion,
  subscribeProductImageVersions,
} from '../utils/productImageVersions'

/**
 * 单张商品图的版本号（COS 修改时间）。
 * 首次渲染还没有版本号，调用方先按原地址显示；查到该图最近换过后组件重新渲染并拿到版本号。
 */
export function useProductImageVersion(src: string | null | undefined): string | undefined {
  const version = useSyncExternalStore(subscribeProductImageVersions, () => getProductImageVersion(src))
  useEffect(() => {
    requestProductImageVersion(src)
  }, [src])
  return version
}

/**
 * 一次渲染整页商品图的表格用：登记这一页的图片地址，返回按地址取版本号的函数。
 * 版本号表变化时通过修订计数触发重新渲染。
 */
export function useProductImageVersions(
  urls: readonly (string | null | undefined)[],
): (url: string | null | undefined) => string | undefined {
  useSyncExternalStore(subscribeProductImageVersions, getProductImageVersionsRevision)
  // 地址列表每次渲染都是新数组，用拼接后的字符串判断这一页的图片是否真的变了。
  const urlsKey = urls.filter(Boolean).join('\n')
  useEffect(() => {
    if (urlsKey) {
      urlsKey.split('\n').forEach(requestProductImageVersion)
    }
  }, [urlsKey])
  return getProductImageVersion
}
