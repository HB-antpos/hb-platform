import type { PaymentAttempt } from "@hb/pos-domain/core/contracts/payment";
import type { PaymentAttemptRepositoryPort } from "@hb/pos-domain/core/contracts/repositories";

/**
 * 主管结案 ACK 只关闭后端会话（让终端管理闸门与下一笔付款放行），不代表任何金融结论：
 * 结论已先落在本地人工结案账本里。后端会把仍非终态的会话写成 SupervisorResolved，
 * 已有的 Linkly 终态则原样保留。
 */
export interface LinklySupervisorResolvedAcknowledger {
  acknowledgeSupervisorResolved(attempt: PaymentAttempt): Promise<void>;
}

export type SupervisorResolutionAckQueueEntry = Readonly<{
  attemptId: string;
}>;

export interface SupervisorResolutionAckQueuePort {
  listPending(limit?: number): Promise<readonly SupervisorResolutionAckQueueEntry[]>;
  markAcknowledged(attemptId: string, acknowledgedAtIso: string): Promise<boolean>;
  recordFailure(attemptId: string, errorCode: string, attemptedAtIso: string): Promise<void>;
}

export type SupervisorResolutionAckResult = Readonly<{
  acknowledged: number;
  pending: number;
}>;

/** 后端明确表示该会话不存在：没有可关闭的会话，视为已处理。 */
export const LINKLY_SUPERVISOR_ACK_SESSION_NOT_FOUND = "LINKLY_SUPERVISOR_ACK_SESSION_NOT_FOUND";

export class LinklySupervisorAckSessionNotFoundError extends Error {
  public readonly code = LINKLY_SUPERVISOR_ACK_SESSION_NOT_FOUND;
  public constructor() {
    super(LINKLY_SUPERVISOR_ACK_SESSION_NOT_FOUND);
    this.name = "LinklySupervisorAckSessionNotFoundError";
  }
}

export type SupervisorResolutionAckOptions = Readonly<{
  queue: SupervisorResolutionAckQueuePort;
  ledger: Pick<PaymentAttemptRepositoryPort, "get">;
  acknowledger: LinklySupervisorResolvedAcknowledger;
  nowIso(): string;
}>;

/**
 * 逐条补发主管结案 ACK。失败只记录错误码并保留队列行，下次触发（打开恢复中心、
 * 冷启动、再次结案）继续重试；进程内同一时刻只有一个 drain，避免并发重复 POST。
 */
export class SupervisorResolutionAcknowledgementService {
  private inflight: Promise<SupervisorResolutionAckResult> | null = null;

  public constructor(private readonly options: SupervisorResolutionAckOptions) {}

  public drain(): Promise<SupervisorResolutionAckResult> {
    if (this.inflight) return this.inflight;
    const operation = this.drainOnce().finally(() => {
      if (this.inflight === operation) this.inflight = null;
    });
    this.inflight = operation;
    return operation;
  }

  private async drainOnce(): Promise<SupervisorResolutionAckResult> {
    const entries = await this.options.queue.listPending();
    let acknowledged = 0;
    let pending = 0;
    for (const entry of entries) {
      if (await this.acknowledgeOne(entry.attemptId)) acknowledged += 1;
      else pending += 1;
    }
    return Object.freeze({ acknowledged, pending });
  }

  private async acknowledgeOne(attemptId: string): Promise<boolean> {
    const { queue, ledger, acknowledger } = this.options;
    try {
      const attempt = await ledger.get(attemptId);
      if (!attempt || attempt.provider !== "linkly-cloud") {
        await queue.recordFailure(attemptId, "SUPERVISOR_ACK_ATTEMPT_NOT_FOUND", this.options.nowIso());
        return false;
      }
      // 迟到的 provider 终态（含 Approved）已经落账：后端会话不再是“非终态”，
      // 由既有终态 ACK 管线按真实结果确认，这里不再用主管标记覆盖语义。
      if (
        attempt.providerAcknowledgedAtIso ||
        attempt.state === "Approved" ||
        attempt.state === "Declined" ||
        attempt.state === "Cancelled"
      ) {
        return await queue.markAcknowledged(attemptId, this.options.nowIso());
      }
      await acknowledger.acknowledgeSupervisorResolved(attempt);
      return await queue.markAcknowledged(attemptId, this.options.nowIso());
    } catch (error) {
      if (error instanceof LinklySupervisorAckSessionNotFoundError) {
        return await queue.markAcknowledged(attemptId, this.options.nowIso()).catch(() => false);
      }
      await queue
        .recordFailure(attemptId, errorCode(error), this.options.nowIso())
        .catch(() => undefined);
      return false;
    }
  }
}

function errorCode(error: unknown): string {
  if (error instanceof Error) {
    const code = (error as Error & { code?: unknown }).code;
    if (typeof code === "string" && code.trim()) return code.trim();
    if (/^[A-Z0-9_]{3,64}$/u.test(error.message)) return error.message;
  }
  return "SUPERVISOR_ACK_FAILED";
}
