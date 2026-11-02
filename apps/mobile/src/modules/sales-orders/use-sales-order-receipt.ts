import { useCallback, useEffect, useRef, useState } from "react";
import {
  createSalesOrderReceiptLink,
  fetchSalesOrderReceipt,
  regenerateSalesOrderReceiptLink,
} from "./api";
import type { SalesOrderReceiptLink, SalesOrderReceiptView } from "./types";

interface ReceiptState {
  view: SalesOrderReceiptView | null;
  loading: boolean;
  /** 业务错误码（如 RECEIPT_UNSUPPORTED）；没有错误码的网络类错误为空串。 */
  errorCode: string | null;
}

/**
 * 订单详情里的在线小票：按订单加载纸面与当前分享链接，并提供「生成 / 重新生成链接」。
 * 切换订单或关闭面板时取消上一次请求，并用 guid 校验丢弃串单的慢响应。
 */
export function useSalesOrderReceipt(orderGuid: string, enabled: boolean) {
  const [state, setState] = useState<ReceiptState>({ view: null, loading: false, errorCode: null });
  const guidRef = useRef("");
  const abortRef = useRef<AbortController | null>(null);

  const load = useCallback(
    async (silent = false) => {
      abortRef.current?.abort();
      if (!orderGuid || !enabled) {
        guidRef.current = "";
        setState({ view: null, loading: false, errorCode: null });
        return;
      }
      const controller = new AbortController();
      abortRef.current = controller;
      guidRef.current = orderGuid;
      setState((previous) => ({
        view: previous.view?.document.orderGuid.toLowerCase() === orderGuid.toLowerCase() ? previous.view : null,
        loading: !silent,
        errorCode: null,
      }));
      try {
        const view = await fetchSalesOrderReceipt(orderGuid, controller.signal);
        if (guidRef.current !== orderGuid) return;
        setState({ view, loading: false, errorCode: null });
      } catch (error) {
        if (controller.signal.aborted || guidRef.current !== orderGuid) return;
        const code = (error as { code?: unknown } | null)?.code;
        setState({ view: null, loading: false, errorCode: typeof code === "string" ? code : "" });
      }
    },
    [orderGuid, enabled],
  );

  useEffect(() => {
    void load();
    return () => abortRef.current?.abort();
  }, [load]);

  const applyLink = useCallback(
    (link: SalesOrderReceiptLink) => {
      if (guidRef.current !== orderGuid) return;
      setState((previous) =>
        previous.view ? { ...previous, view: { ...previous.view, link } } : previous,
      );
      // 静默刷新纸面：有了链接，小票底部才会出现在线二维码。
      void load(true);
    },
    [orderGuid, load],
  );

  /** 已有有效链接就复用，没有才新建。 */
  const ensureLink = useCallback(async () => {
    const link = await createSalesOrderReceiptLink(orderGuid);
    applyLink(link);
    return link;
  }, [orderGuid, applyLink]);

  const regenerate = useCallback(async () => {
    const link = await regenerateSalesOrderReceiptLink(orderGuid);
    applyLink(link);
    return link;
  }, [orderGuid, applyLink]);

  return { ...state, reload: () => load(), ensureLink, regenerate };
}
