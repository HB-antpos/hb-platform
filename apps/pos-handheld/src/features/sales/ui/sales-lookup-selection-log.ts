import type {
  SalesLookupSelectionLogEvent,
  SalesPresenter,
} from "./sales-presenter";

/** 只依赖程序日志的 record 入口，避免销售 UI 绑定具体日志运行时。 */
export type SalesLookupSelectionLogSink = Readonly<{
  record(
    draft: Readonly<{
      level: "Information" | "Warning";
      message: string;
      category: string;
      properties: Readonly<Record<string, string | number | boolean>>;
    }>,
  ): void;
}>;

/**
 * 把一码多商品待选的生命周期写入中心程序日志（对齐 WPF PosScan pending choice 日志）。
 * 必须在创建 presenter 的同一处绑定：presenter.destroy() 会先发出「页面销毁放弃」再清理监听，
 * 若由子组件 effect 订阅，卸载顺序可能让这条日志丢失。
 */
export function bindSalesLookupSelectionLog(
  presenter: Pick<SalesPresenter, "subscribeLookupSelectionLog">,
  sink: SalesLookupSelectionLogSink | null | undefined,
): () => void {
  if (!sink) return () => undefined;
  return presenter.subscribeLookupSelectionLog((event) => {
    try {
      sink.record(toLogDraft(event));
    } catch {
      // 程序日志是旁路，不能影响收银。
    }
  });
}

export function toLogDraft(event: SalesLookupSelectionLogEvent) {
  const properties: Record<string, string | number | boolean> = {
    action: event.action,
    selectionId: event.selectionId,
    source: event.source,
    lookupCodeTail: event.lookupCodeTail,
    lookupCodeLength: event.lookupCodeLength,
    candidateCount: event.candidateCount,
    candidateProducts: event.candidateProducts,
    queueLength: event.queueLength,
  };
  if (event.choiceId !== null) properties.choiceId = event.choiceId;
  if (event.waitedMs !== null) properties.waitedMs = event.waitedMs;
  if (event.sameLookupCodeAsPrevious !== null) {
    properties.sameLookupCodeAsPrevious = event.sameLookupCodeAsPrevious;
  }
  if (event.reason !== null) properties.reason = event.reason;
  if (event.productCode !== null) properties.productCode = event.productCode;
  return {
    // 拒收与非选定放弃意味着「扫到的商品没进购物车」，用 Warning 便于运维筛查。
    level:
      event.action === "rejected" || event.action === "abandoned"
        ? ("Warning" as const)
        : ("Information" as const),
    message: `Pending scan choice ${event.action}.`,
    category: "sales.scan-choice",
    properties,
  };
}
