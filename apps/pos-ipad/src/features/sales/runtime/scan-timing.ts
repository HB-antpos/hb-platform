import {
  POS_CLIENT_METRICS,
  clientMetrics,
  type ClientMetricDraft,
  type ClientMetricOutcome,
} from "@/core/performance/client-metrics";

type ScanTimingSession = Readonly<{
  startedAt: number;
}>;

const MAX_ACTIVE_SESSIONS = 32;

/**
 * 正式 HID 业务时序：从最后一个字符进入 JS 开始，到权威 cart 结果发布为止。
 * 会话只保存单调时间和本地序号，不保存条码、商品、订单或员工信息。
 */
export class ScanTimingCollector {
  private readonly sessions = new Map<string, ScanTimingSession>();
  private lastHidCharacterAt: number | null = null;

  public constructor(
    private readonly dependencies: Readonly<{
      now(): number;
      record(draft: ClientMetricDraft): void;
    }> = {
      now: monotonicNow,
      record: (draft) => clientMetrics.record(draft),
    },
  ) {}

  public noteHidCharacter(): void {
    this.lastHidCharacterAt = this.dependencies.now();
  }

  public beginHid(id: string | undefined): void {
    if (id === undefined) return;
    const submittedAt = this.dependencies.now();
    const startedAt = this.lastHidCharacterAt ?? submittedAt;
    this.lastHidCharacterAt = null;
    this.setSession(id, { startedAt });
  }

  public complete(
    id: string | undefined,
    outcome: ClientMetricOutcome,
  ): void {
    if (id === undefined) return;
    const session = this.sessions.get(id);
    if (!session) return;
    this.sessions.delete(id);
    try {
      this.dependencies.record({
        metric: POS_CLIENT_METRICS.scanToCart,
        valueMs: Math.max(0, this.dependencies.now() - session.startedAt),
        dimensions: { outcome },
      });
    } catch {
      // 指标旁路失败不能反向改变加购结果。
    }
  }

  /** 一码多商品需人工选择时丢弃会话：选择耗时不属于 scan-to-cart 指标。 */
  public discard(id: string | undefined): void {
    if (id === undefined) return;
    this.sessions.delete(id);
  }

  /** 兼容测量期调用面；新路径统一由 complete 明确成功或失败。 */
  public mark(id: string | undefined, label: string): void {
    if (label === "cart-published") this.complete(id, "success");
  }

  /** 声音不再属于 scan-to-cart 指标终点，保留空操作以兼容现有声音桥。 */
  public expectSound(_id: string | undefined, _cue: string): void {}

  public soundPlaying(_cue: string): void {}

  public discardExpectedSound(_cue: string): void {}

  private setSession(id: string, session: ScanTimingSession): void {
    this.sessions.set(id, session);
    while (this.sessions.size > MAX_ACTIVE_SESSIONS) {
      const oldest = this.sessions.keys().next().value;
      if (oldest === undefined) return;
      this.sessions.delete(oldest);
    }
  }
}

export const scanTiming = new ScanTimingCollector();

function monotonicNow(): number {
  return typeof performance === "undefined"
    ? Date.now()
    : performance.now();
}
