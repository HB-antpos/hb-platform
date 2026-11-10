import {
  createContext,
  type PropsWithChildren,
  useContext,
  useEffect,
  useMemo,
  useSyncExternalStore,
} from "react";

import {
  createExpoPosRuntimeController,
  type ExpoPosRuntimeServices,
} from "./expo-pos-runtime";
import type {
  PosRuntimeController,
  PosRuntimeState,
  RuntimeBackendState,
  RuntimeDeviceState,
} from "./pos-runtime";
import { usePosThemeVersion } from "@/ui/theme";

type PosRuntimeContextValue = Readonly<{
  state: PosRuntimeState;
  services: ExpoPosRuntimeServices | null;
  updateOperationalState(input: Readonly<{
    backend: RuntimeBackendState;
    device: Exclude<RuntimeDeviceState, "unknown">;
  }>): void;
  retry(): Promise<void>;
}>;

const PosRuntimeContext = createContext<PosRuntimeContextValue | null>(null);
const productionController = createExpoPosRuntimeController();

export function PosRuntimeProvider({ children }: PropsWithChildren) {
  usePosThemeVersion();
  return (
    <PosRuntimeProviderWithController controller={productionController}>
      {children}
    </PosRuntimeProviderWithController>
  );
}

export function PosRuntimeProviderWithController({
  children,
  controller,
}: PropsWithChildren<{
  controller: PosRuntimeController<ExpoPosRuntimeServices>;
}>) {
  usePosThemeVersion();
  const state = useSyncExternalStore(
    (listener) => controller.subscribe(listener),
    () => controller.getState(),
    () => controller.getState(),
  );

  useEffect(() => {
    void controller.start().catch(() => {
      // controller 已保存明确 failed 状态，由 UI 提供重试；不能让未处理 Promise 终止 RN。
    });
  }, [controller]);

  const value = useMemo<PosRuntimeContextValue>(
    () => ({
      state,
      services: controller.getServices(),
      updateOperationalState: (input) => {
        controller.updateOperationalState(input);
      },
      retry: async () => {
        await controller.stop();
        await controller.start();
      },
    }),
    [controller, state],
  );

  return (
    <PosRuntimeContext.Provider value={value}>
      {children}
    </PosRuntimeContext.Provider>
  );
}

export function usePosRuntime(): PosRuntimeContextValue {
  const value = useContext(PosRuntimeContext);
  if (!value) {
    throw new Error("usePosRuntime must be used inside PosRuntimeProvider.");
  }
  return value;
}
