import { useEffect, useMemo, useState } from 'react'

import { fetchFaceHitThumbnails } from '../../../services/faceMatchService'
import { createHitThumbnailSession, type HitThumbnailStates } from './imageLogic'

/**
 * 一页命中图缩略图：taskIds 变化（换页 / 换筛选 / 刷新出新数据）时中止上一批、撤销旧 objectURL，再发一次批量请求；
 * 卸载时同样 dispose。taskIds 为空时不发请求。返回 taskId → 缩略图状态（objectURL 只在内存里）。
 */
export function useHitThumbnails(taskIds: readonly string[]): HitThumbnailStates {
  const key = taskIds.join(',')
  const ids = useMemo(() => (key ? key.split(',') : []), [key])
  const [states, setStates] = useState<HitThumbnailStates>(() => new Map())

  useEffect(() => {
    if (ids.length === 0) {
      setStates(new Map())
      return
    }
    const session = createHitThumbnailSession({
      taskIds: ids,
      fetchBatch: (batch, signal) => fetchFaceHitThumbnails(batch, signal),
      onChange: setStates,
    })
    void session.start()
    // 换页 / 卸载：中止在途请求并 revoke 全部 objectURL。
    return () => session.dispose()
  }, [ids])

  return states
}
