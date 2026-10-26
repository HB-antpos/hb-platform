import { useEffect, useSyncExternalStore } from "react";

import { AppAsyncStorage } from "@/shared/storage/async-storage";
import {
  createPersonalCodeSymbologyController,
  type PersonalCodeSymbology,
} from "./personal-code-symbology";

/** 全应用共享一个实例：本人码卡片显示、员工弹窗打印都读同一份偏好。 */
export const personalCodeSymbology = createPersonalCodeSymbologyController(AppAsyncStorage);

export function usePersonalCodeSymbology() {
  const symbology = useSyncExternalStore(
    personalCodeSymbology.subscribe,
    personalCodeSymbology.getSnapshot,
    personalCodeSymbology.getSnapshot
  );
  useEffect(() => {
    void personalCodeSymbology.hydrate();
  }, []);
  return [symbology, personalCodeSymbology.set] as readonly [
    PersonalCodeSymbology,
    (next: PersonalCodeSymbology) => Promise<void>
  ];
}
