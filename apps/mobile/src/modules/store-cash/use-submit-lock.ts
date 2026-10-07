import { useCallback, useRef, useState } from "react";

/**
 * 提交防重复点击：用 ref 同步加锁（state 更新是异步的，连点两下仍会放过第二次），
 * 同时用 state 驱动按钮的 loading / disabled。
 */
export function useSubmitLock() {
  const lockRef = useRef(false);
  const [busy, setBusy] = useState(false);

  const run = useCallback(async (task: () => Promise<void>) => {
    if (lockRef.current) return;
    lockRef.current = true;
    setBusy(true);
    try {
      await task();
    } finally {
      lockRef.current = false;
      setBusy(false);
    }
  }, []);

  return { busy, run, lockRef };
}
