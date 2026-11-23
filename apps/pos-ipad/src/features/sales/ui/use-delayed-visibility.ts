import { useEffect, useRef, useState } from "react";

export type DelayedVisibilityOptions = Readonly<{
  /** 状态持续超过这么久才显示，避免几百毫秒就结束的后台任务每次都闪一下。 */
  showDelayMs: number;
  /** 一旦显示，至少停留这么久，避免刚出现就消失的刺眼闪烁。 */
  minVisibleMs: number;
  /** 为 true 时跳过延迟立即显示（如用户主动点结账、正在等结果），最短停留仍然生效。 */
  immediate?: boolean;
}>;

/**
 * 把一个频繁开关的「忙碌」状态转成稳定的显示状态：
 * active 变 true → 等 showDelayMs 仍为 true 才显示；active 变 false → 补足 minVisibleMs 再隐藏；
 * 隐藏倒计时期间 active 又变 true → 取消隐藏、继续显示。
 */
export function useDelayedVisibility(
  active: boolean,
  { showDelayMs, minVisibleMs, immediate = false }: DelayedVisibilityOptions,
): boolean {
  const [visible, setVisible] = useState(active && immediate);
  const visibleRef = useRef(visible);
  const shownAtRef = useRef<number | null>(visible ? Date.now() : null);
  const timerRef = useRef<ReturnType<typeof setTimeout> | null>(null);

  useEffect(() => {
    const clearTimer = () => {
      if (timerRef.current !== null) {
        clearTimeout(timerRef.current);
        timerRef.current = null;
      }
    };
    const show = () => {
      timerRef.current = null;
      visibleRef.current = true;
      shownAtRef.current = Date.now();
      setVisible(true);
    };
    const hide = () => {
      timerRef.current = null;
      visibleRef.current = false;
      shownAtRef.current = null;
      setVisible(false);
    };

    clearTimer();
    if (active) {
      // 已经显示（含隐藏倒计时被取消）：保持显示即可。
      if (visibleRef.current) return clearTimer;
      if (immediate || showDelayMs <= 0) show();
      else timerRef.current = setTimeout(show, showDelayMs);
      return clearTimer;
    }
    // 还在延迟期就结束了：清掉定时器，根本不显示。
    if (!visibleRef.current) return clearTimer;
    const elapsed = Date.now() - (shownAtRef.current ?? 0);
    const remaining = minVisibleMs - elapsed;
    if (remaining <= 0) hide();
    else timerRef.current = setTimeout(hide, remaining);
    return clearTimer;
  }, [active, immediate, minVisibleMs, showDelayMs]);

  return visible;
}
