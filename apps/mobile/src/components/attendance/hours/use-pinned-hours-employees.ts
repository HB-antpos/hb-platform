// 工时统计「手动添加」名单：按门店记在本机（AsyncStorage），换周、重开 App 仍保留。
// 读写失败只是不记住，不影响页面；先改内存再异步落盘，界面不用等存储。
import { useCallback, useEffect, useState } from "react";
import { normalizePinnedGuids, pinnedHoursStorageKey } from "@/modules/attendance/payroll-hours-visibility";
import { AppAsyncStorage } from "@/shared/storage/async-storage";

const EMPTY: ReadonlySet<string> = new Set();

export function usePinnedHoursEmployees(storeCode: string | null) {
  const [state, setState] = useState<{ storeCode: string | null; pinned: ReadonlySet<string> }>({ storeCode: null, pinned: EMPTY });

  useEffect(() => {
    if (!storeCode) return;
    let cancelled = false;
    AppAsyncStorage.getObject(pinnedHoursStorageKey(storeCode))
      .then((value) => {
        if (!cancelled) setState({ storeCode, pinned: new Set(normalizePinnedGuids(value)) });
      })
      .catch(() => {
        if (!cancelled) setState({ storeCode, pinned: EMPTY });
      });
    return () => {
      cancelled = true;
    };
  }, [storeCode]);

  // 换门店后、新名单读回前，不沿用上一家的名单。
  const pinned = state.storeCode === storeCode ? state.pinned : EMPTY;

  const persist = useCallback(
    (next: ReadonlySet<string>) => {
      if (!storeCode) return;
      setState({ storeCode, pinned: next });
      AppAsyncStorage.setObject(pinnedHoursStorageKey(storeCode), [...next]).catch(() => undefined);
    },
    [storeCode],
  );

  const add = useCallback((guids: string[]) => persist(new Set([...pinned, ...guids])), [persist, pinned]);
  const remove = useCallback(
    (guid: string) => {
      const next = new Set(pinned);
      next.delete(guid);
      persist(next);
    },
    [persist, pinned],
  );

  return { pinned, add, remove };
}
