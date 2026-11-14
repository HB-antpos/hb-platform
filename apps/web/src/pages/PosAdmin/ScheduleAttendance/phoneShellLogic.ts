// 手机上的「横屏看板」：什么时候算手机、什么时候该显示横屏看板、以什么方式显示。纯函数，便于单测。

/** 手机的判断依据：触屏设备 + 短边不超过 600px（平板短边 ≥ 744，不算手机）。 */
export interface PhoneProbe {
  /** matchMedia('(pointer: coarse)')。 */
  coarsePointer: boolean
  /** navigator.maxTouchPoints。 */
  maxTouchPoints: number
  /** Math.min(screen.width, screen.height)：不随手机旋转变化。 */
  shortSide: number
}

export const PHONE_MAX_SHORT_SIDE = 600

export function isPhoneDevice(probe: PhoneProbe): boolean {
  const touch = probe.coarsePointer || probe.maxTouchPoints > 0
  return touch && probe.shortSide > 0 && probe.shortSide <= PHONE_MAX_SHORT_SIDE
}

/**
 * 横屏看板的显示方式：
 * - none：不显示（普通页面）；
 * - landscape：手机本身是横的，全屏看板，可以点卡片编辑；
 * - rotated：手机竖着拿且没能锁定横屏（iPhone Safari 不支持、或系统开了旋转锁定），
 *   把看板旋转 90° 铺满屏幕，只读——旁边的编辑浮层、弹窗不会跟着转，所以不开放编辑，把手机转过来就变成 landscape。
 */
export type ShellMode = 'none' | 'landscape' | 'rotated'

export interface ShellState {
  phone: boolean
  /** 当前是否横屏（matchMedia('(orientation: landscape)')）。 */
  landscape: boolean
  /** 用户在竖屏时点了「横屏查看整周」，或已经在看板里还没退出。 */
  requested: boolean
  /** 用户在横屏看板里点了「退出」：横屏时不再自动进入，直到手机回到竖屏。 */
  autoDismissed: boolean
}

export function getShellMode(state: ShellState): ShellMode {
  if (!state.phone) return 'none'
  if (state.landscape) return state.requested || !state.autoDismissed ? 'landscape' : 'none'
  return state.requested ? 'rotated' : 'none'
}

/** 回到竖屏后，之前的「退出」不再生效：下次再横过来仍然自动进入。 */
export function nextAutoDismissed(landscape: boolean, current: boolean): boolean {
  return landscape ? current : false
}
