import type { CashDrawerPort } from "@hb/pos-domain/core/contracts/drawer";
import type { PrinterPort } from "@hb/pos-domain/core/contracts/printer";
import { createDefaultHbPrinterAdapter } from "../peripherals/printer/native";

export type RuntimePrinterAdapter = PrinterPort &
  Pick<CashDrawerPort, "open">;

export type HbPrinterModuleLoader = (moduleName: "HbPrinter") => unknown;

/**
 * 将 Expo Modules 的解析延后至真正的连接、打印或开钱箱动作；运行时构造不触碰硬件。
 * beforeScan 在每次扫描前执行（Android 用于弹出系统蓝牙/定位授权框）。
 */
export function createLazyHbPrinterAdapter(
  loadNativeModule: HbPrinterModuleLoader,
  beforeScan?: () => Promise<void>,
): RuntimePrinterAdapter {
  let adapter: RuntimePrinterAdapter | undefined;
  const resolveAdapter = (): RuntimePrinterAdapter => {
    adapter ??= createDefaultHbPrinterAdapter(
      () => loadNativeModule("HbPrinter") as never,
    );
    return adapter;
  };

  return {
    getStatus: () => resolveAdapter().getStatus(),
    scan: async (timeoutMs) => {
      await beforeScan?.();
      return resolveAdapter().scan(timeoutMs);
    },
    connect: (peripheralId) => resolveAdapter().connect(peripheralId),
    disconnect: () => resolveAdapter().disconnect(),
    print: (operationId, bytes) => resolveAdapter().print(operationId, bytes),
    open: (operationId) => resolveAdapter().open(operationId),
    subscribe: (listener) => resolveAdapter().subscribe(listener),
  };
}
