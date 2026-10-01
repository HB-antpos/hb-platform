import { create } from "zustand";
import { AppAsyncStorage } from "@/shared/storage/async-storage";
import type { PickRoute, PickScope } from "./types";

const STORAGE_KEY = "hbweb_warehouse_picking_preferences";

interface PickPreferencesState {
  route: PickRoute;
  scope: PickScope;
  setRoute: (route: PickRoute) => void;
  setScope: (scope: PickScope) => void;
}

/** 读回本机存的偏好：只接受已知取值，其余回落到默认（M 型、全部）。 */
export function normalizeStoredPickPreferences(raw: unknown): { route: PickRoute; scope: PickScope } {
  const value = (raw ?? {}) as { route?: unknown; scope?: unknown };
  return {
    route: value.route === "s" ? "s" : "m",
    scope: value.scope === "located" || value.scope === "unlocated" ? value.scope : "all",
  };
}

// 读盘完成前用户已经切换过：以用户这次的选择为准，不被旧值覆盖。
let changedBeforeHydrate = false;

function persist(state: Pick<PickPreferencesState, "route" | "scope">) {
  void AppAsyncStorage.setObject(STORAGE_KEY, { route: state.route, scope: state.scope }).catch(() => undefined);
}

/**
 * 走位方式与拣货范围是这台 PDA 的偏好：落盘保存，一起拣的同事各用各的，不影响别人的顺序。
 * 与拣货人不同，这里没有身份信息，换人不用清。
 */
export const usePickPreferences = create<PickPreferencesState>((set, get) => ({
  route: "m",
  scope: "all",
  setRoute: (route) => {
    changedBeforeHydrate = true;
    set({ route });
    persist(get());
  },
  setScope: (scope) => {
    changedBeforeHydrate = true;
    set({ scope });
    persist(get());
  },
}));

let hydratePromise: Promise<void> | null = null;

/** 启动拣货页时读一次本机偏好；读失败保持默认。 */
export function hydratePickPreferences() {
  if (!hydratePromise) {
    hydratePromise = AppAsyncStorage.getObject(STORAGE_KEY)
      .then((stored) => {
        if (!changedBeforeHydrate) usePickPreferences.setState(normalizeStoredPickPreferences(stored));
      })
      .catch(() => undefined);
  }
  return hydratePromise;
}
