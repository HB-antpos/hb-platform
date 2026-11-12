import { useCallback, useEffect, useRef, useState } from "react";

/**
 * 搜索框的「本地输入缓冲」：输入框显示的文字由自己持有，不再每敲一个字就让整个页面重新渲染。
 *
 * 为什么要这样：权限详情页的搜索词原来放在 600 行的页面组件里，每个按键都会重绘整页和弹层里的用户列表；
 * 受控输入框的 JS 值一旦落后于原生值，React Native 就会把原生文本写回，拼音输入法正在组词时
 * 容易出现首字母重复（lulu → llulu）。输入框自己持有文字、上层只在停手后收一次，可以避开这一类问题。
 *
 * - delayMs = 0：每个按键立即通知上层（与原来的行为一致，其他调用方不受影响）。
 * - delayMs > 0：停手 delayMs 毫秒后才通知上层；点「清除」立即通知，并取消尚未发出的那一次。
 * - 上层主动改值（比如打开弹层时重置为空）会同步回输入框；自己发出去的值不会再被回灌，避免打断输入。
 */
export function useBufferedSearchText(value: string, onChange: (value: string) => void, delayMs = 0) {
  const [text, setText] = useState(value);
  const lastEmitted = useRef(value);
  const timer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const onChangeRef = useRef(onChange);

  useEffect(() => {
    onChangeRef.current = onChange;
  });

  const cancelPending = useCallback(() => {
    if (timer.current) {
      clearTimeout(timer.current);
      timer.current = null;
    }
  }, []);

  // 上层主动改值才同步回来；同时丢弃还没发出去的旧输入，避免它稍后把新值覆盖掉。
  useEffect(() => {
    if (value !== lastEmitted.current) {
      lastEmitted.current = value;
      cancelPending();
      setText(value);
    }
  }, [cancelPending, value]);

  useEffect(() => cancelPending, [cancelPending]);

  const emit = useCallback((next: string) => {
    lastEmitted.current = next;
    onChangeRef.current(next);
  }, []);

  const handleChange = useCallback((next: string) => {
    setText(next);
    cancelPending();
    if (delayMs <= 0) {
      emit(next);
      return;
    }
    timer.current = setTimeout(() => {
      timer.current = null;
      emit(next);
    }, delayMs);
  }, [cancelPending, delayMs, emit]);

  const clear = useCallback(() => {
    cancelPending();
    setText("");
    emit("");
  }, [cancelPending, emit]);

  return { text, handleChange, clear };
}
