export type PrinterConnectionState =
  | "disconnected"
  | "connecting"
  | "ready"
  | "failed";

export type PrinterOperationState = "completed" | "failed" | "unknown";
export type PrinterOperationKind = "print" | "drawer";
export type PrinterEncoding = "gb18030" | "gbk" | "utf8";

export type BluetoothPrinterDevice = {
  id: string;
  name: string;
  rssi: number | null;
  isXprinter: boolean;
  /** 经典蓝牙是否已在系统中配对；BLE 或旧原生包为 null/缺失。 */
  bonded?: boolean | null;
};

export type BluetoothPrinterStatus = {
  supported: boolean;
  enabled: boolean;
  connection: PrinterConnectionState;
  peripheralId: string | null;
  writeMode: "withResponse" | "withoutResponse" | null;
};

export type PrinterOperationResult = {
  operationId: string;
  state: PrinterOperationState;
  message: string | null;
};

export type PrinterNativeEvent =
  | ({ type: "status"; reason: string } & BluetoothPrinterStatus)
  | ({ type: "operation"; kind: PrinterOperationKind } & PrinterOperationResult)
  | { type: "error"; code: string; message: string };

export interface HbPrinterBridge {
  getStatus(): Promise<BluetoothPrinterStatus>;
  scan(durationMs: number, includeAll: boolean): Promise<BluetoothPrinterDevice[]>;
  connect(peripheralId: string, timeoutMs: number): Promise<BluetoothPrinterStatus>;
  disconnect(): Promise<BluetoothPrinterStatus>;
  write(
    operationId: string,
    bytes: number[],
    timeoutMs: number,
    kind: PrinterOperationKind,
  ): Promise<PrinterOperationResult>;
  printText(
    operationId: string,
    text: string,
    encoding: PrinterEncoding,
    appendLineFeed: boolean,
    cutAfterPrint: boolean,
    timeoutMs: number,
  ): Promise<PrinterOperationResult>;
  openCashDrawer(
    operationId: string,
    pin: 0 | 1,
    onTime: number,
    offTime: number,
    timeoutMs: number,
  ): Promise<PrinterOperationResult>;
  subscribe(listener: (event: PrinterNativeEvent) => void): () => void;
}
