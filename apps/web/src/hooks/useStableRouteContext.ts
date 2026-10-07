import { useRef } from 'react'
import { useLocation } from 'react-router-dom'

import { resolveRoute, type ResolvedRoute } from '../router/routes'

import { resolveOwnRoute } from './stableRouteContext'

export function useStableRouteContext(): ResolvedRoute | null {
  const location = useLocation()
  const pathnameRef = useRef(location.pathname)
  const routeRef = useRef<ResolvedRoute | null>(resolveRoute(pathnameRef.current))
  // 关键位置：挂载时解析到的路由模式即本页实例身份；KeepAlive 隐藏后全局地址切到别的页面时不跟随，避免参数被清空或换成别人的 id。
  const ownRoutePathRef = useRef(routeRef.current?.path)

  if (location.pathname !== pathnameRef.current) {
    pathnameRef.current = location.pathname
    routeRef.current = resolveOwnRoute(ownRoutePathRef.current, routeRef.current, resolveRoute(pathnameRef.current))
  }

  return routeRef.current
}
