import { runCheckProducts, type CheckRunDeps } from "./check-job";
import { assertDeepEqual, assertEqual } from "./test-helpers";
import type { CheckProductsJobSnapshot, CheckProductsSummary } from "@/modules/local-supplier-invoices/types";

const summary: CheckProductsSummary = {
  total: 3,
  productExists: 2,
  productNotExists: 1,
  barcodeNormal: 3,
  barcodeAbnormal: 0,
};

function job(status: string, extra: Partial<CheckProductsJobSnapshot> = {}): CheckProductsJobSnapshot {
  return { jobId: "job-1", status, message: "", summary: null, ...extra };
}

/** 假时钟：sleep 只推进时间不真等，now 返回累计值，轮询超时用例不用等 10 分钟。 */
function makeDeps(overrides: Partial<CheckRunDeps> & { polls?: CheckProductsJobSnapshot[] } = {}) {
  let clock = 0;
  const calls = { start: 0, poll: 0, sync: 0, sleep: [] as number[] };
  const polls = [...(overrides.polls ?? [])];
  const deps: CheckRunDeps = {
    startJob: async () => {
      calls.start += 1;
      return job("Queued");
    },
    getJob: async () => {
      calls.poll += 1;
      const next = polls.shift();
      if (!next) throw new Error("unexpected extra poll");
      return next;
    },
    checkSync: async () => {
      calls.sync += 1;
      return summary;
    },
    isMissingEndpoint: () => false,
    sleep: async (ms) => {
      calls.sleep.push(ms);
      clock += ms;
    },
    now: () => clock,
    isCancelled: () => false,
    ...overrides,
  };
  return { deps, calls };
}

async function main() {
  // 排队 -> 运行中 -> 成功：按间隔轮询到终态，返回任务里的汇总。
  {
    const { deps, calls } = makeDeps({
      polls: [job("Running"), job("Succeeded", { summary })],
    });
    const outcome = await runCheckProducts(deps, "inv-1", ["d-1"], { pollIntervalMs: 2000 });
    assertDeepEqual(outcome, { kind: "completed", summary }, "succeeded job completes with its summary");
    assertEqual(calls.start, 1, "job is submitted once");
    assertEqual(calls.poll, 2, "polls until terminal status");
    assertDeepEqual(calls.sleep, [2000, 2000], "sleeps the configured interval before each poll");
  }

  // 提交时就已是终态（重复请求命中已完成的任务）：不需要轮询。
  {
    const { deps, calls } = makeDeps({
      startJob: async () => job("Succeeded", { summary }),
    });
    const outcome = await runCheckProducts(deps, "inv-1");
    assertEqual(outcome.kind, "completed", "already finished job completes immediately");
    assertEqual(calls.poll, 0, "no polling for an already terminal job");
  }

  // 失败：带回服务端的失败原因。
  {
    const { deps } = makeDeps({ polls: [job("Failed", { message: "检测失败了" })] });
    const outcome = await runCheckProducts(deps, "inv-1");
    assertDeepEqual(outcome, { kind: "failed", message: "检测失败了" }, "failed job reports the server message");
  }

  // 超时：任务一直在跑，超过上限就停止等待，而不是无限轮询。
  {
    const { deps, calls } = makeDeps({
      getJob: async () => job("Running"),
    });
    const outcome = await runCheckProducts(deps, "inv-1", undefined, { pollIntervalMs: 1000, timeoutMs: 3500 });
    assertEqual(outcome.kind, "timeout", "never-ending job times out");
    assertEqual(calls.sleep.length, 4, "polls stop once the deadline is reached");
  }

  // 取消：睡眠之后发现页面已关闭，不再请求任务状态。
  {
    let cancelled = false;
    const { deps, calls } = makeDeps({
      isCancelled: () => cancelled,
      sleep: async () => {
        cancelled = true;
      },
    });
    const outcome = await runCheckProducts(deps, "inv-1");
    assertEqual(outcome.kind, "cancelled", "cancelled run stops polling");
    assertEqual(calls.poll, 0, "no request is sent after cancellation");
  }

  // 任务接口 404（环境还没发布）：回退旧同步检测，且只调一次。
  {
    const missing = new Error("404");
    const { deps, calls } = makeDeps({
      startJob: async () => {
        throw missing;
      },
      isMissingEndpoint: (error) => error === missing,
    });
    const outcome = await runCheckProducts(deps, "inv-1", ["d-1", "d-2"]);
    assertDeepEqual(outcome, { kind: "completed", summary }, "missing job endpoint falls back to the sync check");
    assertEqual(calls.sync, 1, "sync fallback runs exactly once");
  }

  // 其他提交错误（例如没有权限）必须原样抛出，不能被当成「接口不存在」吞掉。
  {
    const denied = new Error("403");
    const { deps, calls } = makeDeps({
      startJob: async () => {
        throw denied;
      },
    });
    let thrown: unknown = null;
    try {
      await runCheckProducts(deps, "inv-1");
    } catch (error) {
      thrown = error;
    }
    assertEqual(thrown, denied, "non-404 start errors propagate");
    assertEqual(calls.sync, 0, "no sync fallback for non-404 errors");
  }

  // 轮询过程中的网络错误同样向上抛，由界面提示失败。
  {
    const { deps } = makeDeps({
      getJob: async () => {
        throw new Error("network down");
      },
    });
    let message = "";
    try {
      await runCheckProducts(deps, "inv-1");
    } catch (error) {
      message = (error as Error).message;
    }
    assertEqual(message, "network down", "poll errors propagate");
  }
}

void main();
