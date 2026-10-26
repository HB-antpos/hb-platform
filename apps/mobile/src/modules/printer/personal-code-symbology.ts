export type PersonalCodeSymbology = "qr" | "barcode";

/** 默认二维码；只有用户在本机明确切换后才会使用一维条码。 */
export const DEFAULT_PERSONAL_CODE_SYMBOLOGY: PersonalCodeSymbology = "qr";
export const PERSONAL_CODE_SYMBOLOGY_STORAGE_KEY = "hb.personalCode.symbology.v1";

export interface PersonalCodeSymbologyStorage {
  getString(key: string): Promise<string | null>;
  setString(key: string, value: string): Promise<void>;
}

export function normalizePersonalCodeSymbology(value: unknown): PersonalCodeSymbology {
  // 任何未知、损坏或旧版本的值都回落到二维码，避免误出一维码。
  return value === "barcode" ? "barcode" : DEFAULT_PERSONAL_CODE_SYMBOLOGY;
}

/**
 * 设备级偏好控制器：屏幕显示和打印共用同一份内存值，保证所见即所印。
 * 存储读写失败只影响持久化，不影响本次会话内的显示与打印一致性。
 */
export function createPersonalCodeSymbologyController(storage: PersonalCodeSymbologyStorage) {
  let current: PersonalCodeSymbology = DEFAULT_PERSONAL_CODE_SYMBOLOGY;
  let userChanged = false;
  let hydratePromise: Promise<PersonalCodeSymbology> | null = null;
  const listeners = new Set<() => void>();

  const notify = () => listeners.forEach((listener) => listener());

  return {
    getSnapshot: () => current,
    subscribe(listener: () => void) {
      listeners.add(listener);
      return () => { listeners.delete(listener); };
    },
    /** 幂等：只读一次存储；读取期间用户已手动切换时，以用户操作为准。 */
    hydrate(): Promise<PersonalCodeSymbology> {
      hydratePromise ??= storage
        .getString(PERSONAL_CODE_SYMBOLOGY_STORAGE_KEY)
        .then((stored) => {
          if (!userChanged) {
            const next = normalizePersonalCodeSymbology(stored);
            if (next !== current) {
              current = next;
              notify();
            }
          }
          return current;
        })
        .catch(() => current);
      return hydratePromise;
    },
    async set(next: PersonalCodeSymbology) {
      userChanged = true;
      if (next !== current) {
        current = next;
        notify();
      }
      try {
        await storage.setString(PERSONAL_CODE_SYMBOLOGY_STORAGE_KEY, next);
      } catch {
        // 持久化失败时保留本次会话选择，下次启动回落为默认二维码。
      }
    },
  };
}
