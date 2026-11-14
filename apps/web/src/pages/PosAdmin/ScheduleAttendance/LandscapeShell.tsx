import type { ReactNode } from 'react'
import { useEffect, useState } from 'react'
import { createPortal } from 'react-dom'

import { isPhoneDevice } from './phoneShellLogic'
import type { ShellMode } from './phoneShellLogic'

/** 手机上的横屏看板外壳：铺满整个屏幕的一层，里面放周视图。 */

const LANDSCAPE_QUERY = '(orientation: landscape)'

interface PhoneEnvironment {
  phone: boolean
  landscape: boolean
}

const readEnvironment = (): PhoneEnvironment => {
  if (typeof window === 'undefined' || typeof window.matchMedia !== 'function') return { phone: false, landscape: false }
  return {
    phone: isPhoneDevice({
      coarsePointer: window.matchMedia('(pointer: coarse)').matches,
      maxTouchPoints: navigator.maxTouchPoints ?? 0,
      // screen 的短边不随旋转变化，用它区分手机和平板
      shortSide: Math.min(window.screen?.width ?? 0, window.screen?.height ?? 0),
    }),
    landscape: window.matchMedia(LANDSCAPE_QUERY).matches,
  }
}

/** 是不是手机、当前是不是横屏；旋转手机时实时更新。 */
export function usePhoneEnvironment(): PhoneEnvironment {
  const [environment, setEnvironment] = useState<PhoneEnvironment>(readEnvironment)
  useEffect(() => {
    const update = () => setEnvironment((prev) => {
      const next = readEnvironment()
      return prev.phone === next.phone && prev.landscape === next.landscape ? prev : next
    })
    const query = window.matchMedia(LANDSCAPE_QUERY)
    // 老版本 Safari 的 matchMedia 只有 addListener
    if (query.addEventListener) query.addEventListener('change', update)
    else query.addListener(update)
    window.addEventListener('resize', update)
    window.addEventListener('orientationchange', update)
    return () => {
      if (query.removeEventListener) query.removeEventListener('change', update)
      else query.removeListener(update)
      window.removeEventListener('resize', update)
      window.removeEventListener('orientationchange', update)
    }
  }, [])
  return environment
}

type LockableOrientation = ScreenOrientation & {
  lock?: (orientation: string) => Promise<void>
  unlock?: () => void
}

/**
 * 尽力把手机转成横屏：先进全屏，再请求锁定横屏。必须在用户点击时调用（浏览器的要求）。
 * Android Chrome 可行；iPhone Safari 没有这两个能力，返回 false，页面改用「旋转 90°」兜底。
 */
export async function tryLockLandscape(): Promise<boolean> {
  try {
    if (!document.fullscreenElement && document.documentElement.requestFullscreen) {
      await document.documentElement.requestFullscreen()
    }
  } catch {
    // 全屏被拒绝（例如 iframe 里）不影响后面的锁定尝试
  }
  try {
    const orientation = window.screen?.orientation as LockableOrientation | undefined
    if (!orientation?.lock) return false
    await orientation.lock('landscape')
    return true
  } catch {
    return false
  }
}

/** 退出看板时还原：解除方向锁定、退出全屏。 */
export function releaseLandscape(): void {
  try {
    (window.screen?.orientation as LockableOrientation | undefined)?.unlock?.()
  } catch {
    // 没有锁定过 / 不支持，忽略
  }
  try {
    if (document.fullscreenElement) void document.exitFullscreen()
  } catch {
    // 忽略
  }
}

interface LandscapeShellProps {
  mode: Exclude<ShellMode, 'none'>
  children: ReactNode
}

/** 铺满屏幕的一层（挂到 body 上，不受页面里 transform 的影响）；rotated 模式下整体旋转 90°。 */
export default function LandscapeShell({ mode, children }: LandscapeShellProps) {
  // 打开时锁住底下页面的滚动，退出时还原
  useEffect(() => {
    document.body.classList.add('sce-shell-open')
    return () => document.body.classList.remove('sce-shell-open')
  }, [])
  return createPortal(
    <div className={`sce-shell sce-shell--${mode}`} role="dialog" aria-modal="true">
      {children}
    </div>,
    document.body,
  )
}
