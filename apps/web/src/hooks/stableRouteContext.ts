interface RouteWithPath {
  path: string
}

/**
 * 页面实例应看到的路由：只接受与本实例路由模式（如 `/warehouse/store-order/detail/:id`）相同的新路由，否则沿用上一次的结果。
 * KeepAlive 隐藏的标签页也会收到全局路由变化；若跟着变成列表页（参数为空）或别的页面（同名参数是别人的 id），
 * 依赖参数的 effect 会清空状态、停掉后台任务轮询，甚至用别人的 id 发请求。
 * 保活缓存键就是 pathname，保活实例的参数本不会合法变化；同一路由模式下的参数变化（非保活路由复用实例）仍照常更新。
 */
export function resolveOwnRoute<TRoute extends RouteWithPath>(
  ownRoutePath: string | undefined,
  previous: TRoute | null,
  next: TRoute | null,
): TRoute | null {
  // 挂载时没解析出路由，无法判定本页身份，保持原有「跟随当前地址」的行为。
  if (!ownRoutePath) return next
  return next?.path === ownRoutePath ? next : previous
}
