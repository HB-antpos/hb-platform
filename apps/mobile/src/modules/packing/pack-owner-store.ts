import { create } from "zustand";
import { AppAsyncStorage } from "@/shared/storage/async-storage";
import { normalizeUserKey } from "./packing-logic";

const STORAGE_KEY = "packing:selectedOwner";


interface PackOwnerState {
  /** 每个账号各记各的上次选择：切换账号不会把 A 选的分店带给 B。 */
  selections: Record<string, string>;
  hydrated: boolean;
  setOwner: (userGuid: string, ownerCode: string) => void;
  hydrate: () => Promise<void>;
}

/** 本机记住「当前在哪家分店 / 总仓打包」。读写失败只影响记忆，不影响业务。 */
export const usePackOwnerStore = create<PackOwnerState>((set, get) => ({
  selections: {},
  hydrated: false,
  setOwner: (userGuid, ownerCode) => {
    const selections = { ...get().selections, [normalizeUserKey(userGuid)]: ownerCode };
    set({ selections });
    void AppAsyncStorage.setObject(STORAGE_KEY, selections).catch(() => undefined);
  },
  hydrate: async () => {
    if (get().hydrated) return;
    try {
      const saved = await AppAsyncStorage.getObject<Record<string, string>>(STORAGE_KEY);
      set({ selections: { ...(saved ?? {}), ...get().selections }, hydrated: true });
    } catch {
      set({ hydrated: true });
    }
  },
}));
