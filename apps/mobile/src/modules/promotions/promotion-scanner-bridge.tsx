/**
 * 促销页硬件扫码（Zebra DataWedge / HID 扫码枪）的分发桥：
 * 路由页面持有唯一的 useHidBarcodeScanner，列表 / 编辑页各自登记处理函数，按当前屏分发。
 * 子页面打开自己的弹层（相机、搜索、日期等）时撤回登记，扫码不会落到被遮住的页面上。
 */
import { createContext, useContext, useEffect, useRef } from "react";

export type PromotionScanOwner = "list" | "editor";
export type PromotionScanHandler = (barcode: string) => void;

export interface PromotionScannerBridge {
  registerScanHandler: (owner: PromotionScanOwner, handler: PromotionScanHandler | null) => void;
  /** 可见输入框聚焦 / 失焦：文本模式下暂停隐藏扫码框抢焦点，避免打断手输。 */
  onVisibleInputFocus: () => void;
  onVisibleInputBlur: () => void;
}

const noop = () => {};

export const PromotionScannerContext = createContext<PromotionScannerBridge>({
  registerScanHandler: noop,
  onVisibleInputFocus: noop,
  onVisibleInputBlur: noop,
});

export function usePromotionScanner() {
  return useContext(PromotionScannerContext);
}

/** active 为 false（自身弹层打开、无分店等）时撤回登记；处理函数始终取最新闭包。 */
export function useRegisterPromotionScanHandler(
  owner: PromotionScanOwner,
  handler: PromotionScanHandler,
  active: boolean,
) {
  const { registerScanHandler } = usePromotionScanner();
  const handlerRef = useRef(handler);
  handlerRef.current = handler;

  useEffect(() => {
    if (!active) {
      registerScanHandler(owner, null);
      return undefined;
    }
    registerScanHandler(owner, (barcode) => handlerRef.current(barcode));
    return () => registerScanHandler(owner, null);
  }, [active, owner, registerScanHandler]);
}
