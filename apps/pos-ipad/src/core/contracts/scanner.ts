export type ScannerContext =
  | "cashier-login"
  | "product"
  | "product-search"
  | "supervisor-authorization"
  | "dialog";

/** ble = App 内直连的 BLE 数据模式扫码枪（GATT notify），不经过键盘输入。 */
export type ScanSource = "hid" | "camera" | "ble";

export type CameraScanMode = "single" | "continuous";

export type ScanEvent = Readonly<{
  value: string;
  source: ScanSource;
  context: ScannerContext;
  receivedAtIso: string;
}>;

export interface ScannerPort {
  setContext(context: ScannerContext): void;
  startCamera(): Promise<void>;
  stopCamera(): Promise<void>;
  subscribe(listener: (event: ScanEvent) => void): () => void;
}
