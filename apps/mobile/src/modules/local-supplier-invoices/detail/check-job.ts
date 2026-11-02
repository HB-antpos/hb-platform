import type {
  CheckProductsJobSnapshot,
  CheckProductsSummary,
} from "@/modules/local-supplier-invoices/types";

/** 轮询间隔与总时长上限，与 Web 的后台任务轮询口径一致（2 秒一次，最多等 10 分钟）。 */
export const CHECK_JOB_POLL_INTERVAL_MS = 2000;
export const CHECK_JOB_TIMEOUT_MS = 10 * 60 * 1000;

export type CheckRunOutcome =
  | { kind: "completed"; summary: CheckProductsSummary | null }
  | { kind: "failed"; message: string }
  /** 等待超过上限：任务可能仍在服务端跑，结果以稍后重读明细为准。 */
  | { kind: "timeout" }
  /** 页面已关闭 / 离开：停止轮询，不再回写界面。任务本身不会被取消，服务端会照常跑完。 */
  | { kind: "cancelled" };

export interface CheckRunDeps {
  startJob: (invoiceGuid: string, detailGuids?: string[]) => Promise<CheckProductsJobSnapshot>;
  getJob: (invoiceGuid: string, jobId: string) => Promise<CheckProductsJobSnapshot>;
  /** 后台任务接口不存在（404）时的同步回退。 */
  checkSync: (invoiceGuid: string, detailGuids?: string[]) => Promise<CheckProductsSummary | null>;
  isMissingEndpoint: (error: unknown) => boolean;
  sleep: (ms: number) => Promise<void>;
  now: () => number;
  isCancelled: () => boolean;
}

function isTerminalStatus(status: string) {
  return status === "Succeeded" || status === "Failed";
}

/**
 * 整单（或指定行）商品检测：提交后台任务 → 轮询到终态。
 *
 * 关键逻辑：
 * - 长任务只轮询后台 job，不把一个 HTTP 请求挂到网关超时；
 * - 提交接口 404（环境还没发布任务端点）时回退旧同步接口，与 Web 一致；
 * - 同一张单已有任务在跑时由 startJob 直接返回那个任务，这里按普通任务继续等；
 * - 每次睡眠前后都检查 isCancelled，页面关闭后立刻停止，不再发请求。
 * 检测结果由服务端写回明细，调用方拿到 completed 后重读明细即可。
 */
export async function runCheckProducts(
  deps: CheckRunDeps,
  invoiceGuid: string,
  detailGuids?: string[],
  options: { pollIntervalMs?: number; timeoutMs?: number } = {}
): Promise<CheckRunOutcome> {
  const pollIntervalMs = options.pollIntervalMs ?? CHECK_JOB_POLL_INTERVAL_MS;
  const timeoutMs = options.timeoutMs ?? CHECK_JOB_TIMEOUT_MS;

  let job: CheckProductsJobSnapshot;
  try {
    job = await deps.startJob(invoiceGuid, detailGuids);
  } catch (error) {
    if (!deps.isMissingEndpoint(error)) throw error;
    return { kind: "completed", summary: await deps.checkSync(invoiceGuid, detailGuids) };
  }

  const deadline = deps.now() + timeoutMs;
  while (!isTerminalStatus(job.status)) {
    if (deps.isCancelled()) return { kind: "cancelled" };
    if (deps.now() >= deadline) return { kind: "timeout" };
    await deps.sleep(pollIntervalMs);
    if (deps.isCancelled()) return { kind: "cancelled" };
    job = await deps.getJob(invoiceGuid, job.jobId);
  }

  if (job.status === "Failed") {
    return { kind: "failed", message: job.message };
  }
  return { kind: "completed", summary: job.summary };
}
