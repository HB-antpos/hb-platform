import assert from "node:assert/strict";
import { describeCreateNewProductsResult, describeSubmitContainerResult } from "./container-detail-job-results";
import type { ContainerJob, CreateNewProductsRunResult, PushProductsToHqJob } from "./types";

function job(overrides: Partial<Omit<ContainerJob, "result">> & { result?: Partial<ContainerJob["result"]> } = {}): ContainerJob {
  const { result, ...rest } = overrides;
  return {
    jobId: "j1",
    status: "Succeeded",
    result: { createdCount: 2, updatedCount: 0, skippedCount: 0, failedCount: 0, containerCompleted: false, created: [], updated: [], skipped: [], errors: [], ...result },
    ...rest,
  };
}
function run(overrides: Partial<CreateNewProductsRunResult>): CreateNewProductsRunResult {
  return { status: "completed", missingRetailPrice: [], push: { status: "skipped", reason: "not-run" }, job: job(), ...overrides };
}
const pushJob = (overrides: Partial<PushProductsToHqJob> = {}): PushProductsToHqJob => ({ jobId: "p1", status: "Succeeded", ...overrides });

// ---- blocked ----
assert.deepEqual(
  describeCreateNewProductsResult({ status: "blocked", blockedReason: "MISSING_RETAIL_PRICE", missingRetailPrice: [], push: { status: "skipped", reason: "not-run" } }),
  { creation: { tone: "danger", key: "createProducts.result.blockedMissingPrice" }, hq: null, extras: [] },
);
assert.equal(
  describeCreateNewProductsResult({ status: "blocked", blockedReason: "NO_DETAILS", missingRetailPrice: [], push: { status: "skipped", reason: "not-run" } }).creation.key,
  "createProducts.result.blockedNoDetails",
);

// ---- 创建成功 + HQ 同步成功 ----
const ok = describeCreateNewProductsResult(run({ push: { status: "succeeded", job: pushJob({ result: { successCount: 2, failedCount: 0, totalCount: 2, errors: [] } }) } }));
assert.equal(ok.creation.tone, "success");
assert.deepEqual(ok.creation.params, { created: 2 });
assert.equal(ok.hq?.tone, "success");
assert.deepEqual(ok.hq?.params, { success: 2, total: 2 });

// ---- 关键：HQ 推送失败 / 出错，创建仍是成功，HQ 只是 warning ----
for (const push of [
  { status: "failed", job: pushJob({ status: "Failed", message: "HQ 超时" }) },
  { status: "error", message: "network down" },
] as const) {
  const view = describeCreateNewProductsResult(run({ push }));
  assert.equal(view.creation.tone, "success", "创建成功不能因 HQ 推送失败被报告为失败");
  assert.equal(view.creation.key, "createProducts.result.creationSuccess");
  assert.equal(view.hq?.tone, "warning");
  assert.ok(view.hq?.detail, "要附上后端/网络原始信息");
}
assert.equal(describeCreateNewProductsResult(run({ push: { status: "failed", job: pushJob({ status: "Failed", message: "HQ 超时" }) } })).hq?.key, "createProducts.result.hq.failed");
assert.equal(describeCreateNewProductsResult(run({ push: { status: "error", message: "x" } })).hq?.key, "createProducts.result.hq.error");

// ---- 跳过原因 ----
const skippedKey = (reason: "sync-disabled" | "nothing-created" | "no-candidates" | "push-busy") =>
  describeCreateNewProductsResult(run({ push: { status: "skipped", reason } })).hq;
assert.equal(skippedKey("sync-disabled")?.key, "createProducts.result.hq.syncDisabled");
assert.equal(skippedKey("sync-disabled")?.tone, "neutral");
assert.equal(skippedKey("nothing-created")?.key, "createProducts.result.hq.nothingCreated");
assert.equal(skippedKey("no-candidates")?.tone, "warning");
assert.equal(skippedKey("push-busy")?.key, "createProducts.result.hq.pushBusy");
assert.equal(describeCreateNewProductsResult(run({})).hq, null, "未执行同步阶段时不显示 HQ 行");

// ---- 创建任务本身失败 / 部分 / 无新建 ----
const failed = describeCreateNewProductsResult(run({ job: job({ status: "Failed", message: "建档失败" }) }));
assert.equal(failed.creation.tone, "danger");
assert.equal(failed.creation.detail, "建档失败");
const partial = describeCreateNewProductsResult(run({ job: job({ result: { createdCount: 3, skippedCount: 1, failedCount: 2 } }) }));
assert.equal(partial.creation.tone, "warning");
assert.deepEqual(partial.creation.params, { created: 3, skipped: 1, failed: 2 });
assert.equal(describeCreateNewProductsResult(run({ job: job({ result: { createdCount: 0 } }) })).creation.tone, "neutral");

// ---- 未能进入 HQ 候选的新建商品 ----
const unsent = describeCreateNewProductsResult(run({
  plan: { selection: { productCodes: [], items: [] }, unsentCreatedCount: 2, shouldPush: false, warnings: [{ code: "UNSENT_CREATED", count: 2 }, { code: "PUSH_BUSY" }] },
  push: { status: "skipped", reason: "no-candidates" },
}));
assert.deepEqual(unsent.extras, [{ tone: "warning", key: "createProducts.result.hq.unsentCreated", params: { count: 2 } }]);

// ---- 提交整柜 ----
assert.deepEqual(describeSubmitContainerResult(job()).tone, "success");
assert.equal(describeSubmitContainerResult(job()).key, "submitContainer.result.success");
const submitPartial = describeSubmitContainerResult(job({ result: { failedCount: 4 } }));
assert.equal(submitPartial.tone, "warning");
assert.deepEqual(submitPartial.params, { failed: 4 });
const submitFailed = describeSubmitContainerResult(job({ status: "Failed", message: "boom" }));
assert.equal(submitFailed.tone, "danger");
assert.equal(submitFailed.detail, "boom");

console.log("container-detail-job-results.test.ts: ok");
