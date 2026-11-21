import type { ScanEvent, ScannerContext } from "../../contracts/scanner";

export type ScannerCaptureContext =
  | ScannerContext
  | "emergency-qr"
  | "device-activation"
  /** 设置页「测试扫码」弹窗独占：所有来源的码只进测试面板，不触发任何业务。 */
  | "scanner-test";

export type ScannerScanCategory =
  | "cashier-code"
  | "product-code"
  | "supervisor-code"
  | "dialog-code"
  | "emergency-qr"
  | "device-activation"
  | "scanner-test";

export type ScannerCaptureStatus = "inactive" | "capturing" | "camera";

export type RoutedScanEvent = Readonly<
  Omit<ScanEvent, "context"> & {
    context: ScannerCaptureContext;
    category: ScannerScanCategory;
  }
>;

export type TextInputCaptureResult = Readonly<{
  valueToRender: string;
  emitted: boolean;
  overflowed: boolean;
}>;
