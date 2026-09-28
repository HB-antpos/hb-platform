/**
 * 只采用最后一次发起的请求结果。
 * 每次查询前 begin() 取序号，响应回来后用 isLatest(序号) 判断：期间若又发起了新查询，旧响应直接丢弃。
 * 对比「请求进行中就跳过新请求」的写法：那种写法会漏掉较新的查询，让旧结果留在屏幕上。
 */
export function createLatestRequestGate() {
  let current = 0
  return {
    begin: () => {
      current += 1
      return current
    },
    isLatest: (seq: number) => seq === current,
  }
}

export type LatestRequestGate = ReturnType<typeof createLatestRequestGate>
