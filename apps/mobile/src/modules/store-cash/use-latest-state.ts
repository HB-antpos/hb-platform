import { useCallback, useRef, useState } from "react";

/**
 * 既是 React 状态，又始终能同步读到最新值的草稿容器。
 * 提交流程里「上传照片 → 回写 attachmentGuid → 再用最新草稿构造请求」是跨 await 的，
 * 直接读渲染闭包里的 state 会拿到上传前的旧草稿，所以每次更新同步写入 ref。
 */
export function useLatestState<T>(initial: T | null) {
  const ref = useRef<T | null>(initial);
  const [state, setState] = useState<T | null>(initial);

  const replace = useCallback((next: T | null) => {
    ref.current = next;
    setState(next);
  }, []);

  const update = useCallback((updater: (current: T) => T) => {
    if (ref.current == null) return;
    ref.current = updater(ref.current);
    setState(ref.current);
  }, []);

  return { state, ref, replace, update };
}
