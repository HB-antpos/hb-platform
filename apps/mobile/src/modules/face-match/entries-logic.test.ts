import assert from "node:assert/strict";
import {
  ENTRY_STATUS_FILTERS,
  INCIDENT_CATEGORY_OPTIONS,
  REVIEW_STATUS_FILTERS,
  buildListParams,
  canWithdrawEntry,
  entryStatusFromFilter,
  entryStatusTone,
  formatApprovalProgress,
  incidentCategoryLabelKey,
  mergePages,
  needsMultipleApprovals,
  nextPageParam,
  resolveEntryActions,
  reviewStatusFromFilter,
  reviewStatusTone,
  summarizeStoreScope,
} from "./entries-logic";

// ───────── 事件类别：三项，没有一般偷窃 ─────────
assert.deepEqual(
  INCIDENT_CATEGORY_OPTIONS.map((option) => option.value),
  [1, 2, 3],
);
assert.equal(INCIDENT_CATEGORY_OPTIONS.length, 3);
assert.equal(incidentCategoryLabelKey(3), "unwelcome.category.other");
assert.equal(incidentCategoryLabelKey(4), null);
assert.equal(incidentCategoryLabelKey(0), null);

// ───────── 状态筛选：全部 / 待批准 / 已生效 / 已过期 / 已撤回 ─────────
assert.deepEqual(
  ENTRY_STATUS_FILTERS.map((filter) => filter.status),
  [undefined, 0, 1, 2, 3],
);
assert.equal(entryStatusFromFilter("all"), undefined);
assert.equal(entryStatusFromFilter("pending"), 0);
assert.equal(entryStatusFromFilter("withdrawn"), 3);
assert.deepEqual(
  REVIEW_STATUS_FILTERS.map((filter) => filter.status),
  [0, 1, 2],
);
assert.equal(reviewStatusFromFilter("confirmed"), 2);
assert.equal(reviewStatusFromFilter("pending"), 0);
assert.equal(entryStatusTone(0), "warning");
assert.equal(entryStatusTone(1), "success");
assert.equal(entryStatusTone(2), "neutral");
assert.equal(entryStatusTone(3), "neutral");
assert.equal(reviewStatusTone(0), "warning");
assert.equal(reviewStatusTone(2), "danger");
assert.equal(reviewStatusTone(1), "neutral");

// ───────── 查询参数：与 Web 一致，status 以字符串、空值不带 ─────────
assert.deepEqual(buildListParams({ page: 1, pageSize: 20 }), { status: undefined, storeCode: undefined, page: 1, pageSize: 20 });
assert.deepEqual(buildListParams({ status: 0, storeCode: "1008", page: 3, pageSize: 20 }), { status: "0", storeCode: "1008", page: 3, pageSize: 20 });
assert.equal(buildListParams({ status: 0, page: 1, pageSize: 20 }).status, "0", "待批准(0)不能被当成「未筛选」丢掉");
assert.equal(buildListParams({ storeCode: "", page: 1, pageSize: 20 }).storeCode, undefined);

// ───────── 分页 ─────────
assert.equal(nextPageParam({ page: 1, hasMore: true }), 2);
assert.equal(nextPageParam({ page: 4, hasMore: false }), undefined);
{
  const merged = mergePages(
    [
      { items: [{ id: "a" }, { id: "b" }] },
      { items: [{ id: "b" }, { id: "c" }] },
    ],
    (item) => item.id,
  );
  assert.deepEqual(
    merged.map((item) => item.id),
    ["a", "b", "c"],
    "相邻页重复的条目只保留一次",
  );
  assert.deepEqual(mergePages([], (item: { id: string }) => item.id), []);
}

// ───────── 批准进度 / 多位批准人 / 门店标签 ─────────
assert.equal(formatApprovalProgress({ approvalsDone: 1, approvalsNeeded: 2 }), "1 / 2");
assert.equal(needsMultipleApprovals({ approvalsNeeded: 2 }), true);
assert.equal(needsMultipleApprovals({ approvalsNeeded: 1 }), false);
assert.deepEqual(summarizeStoreScope(["1", "2"]), { visible: ["1", "2"], extra: 0 });
assert.deepEqual(summarizeStoreScope(["1", "2", "3", "4", "5"]), { visible: ["1", "2", "3"], extra: 2 });

// ───────── 行内操作：批准只看服务端 canApprove + Approve 权限；其余看 Manage ─────────
{
  const pending = { status: 0, canApprove: true };
  const all = { unwelcomeManage: true, unwelcomeApprove: true };
  assert.deepEqual(resolveEntryActions(pending, all, false), { approve: true, dispute: true, withdraw: true });
  // 服务端说不能批准（例如提交人本人且非管理员、已批过）→ 即使有 Approve 权限也不显示
  assert.equal(resolveEntryActions({ status: 0, canApprove: false }, all, false).approve, false);
  // 有 canApprove 但没有 Approve 权限标志 → 不显示
  assert.equal(resolveEntryActions(pending, { unwelcomeManage: true, unwelcomeApprove: false }, false).approve, false);
  // 只有 Approve 权限（无 Manage）：不能写备注、不能撤回
  assert.deepEqual(resolveEntryActions(pending, { unwelcomeManage: false, unwelcomeApprove: true }, false), { approve: true, dispute: false, withdraw: false });
  // 已撤回 / 已过期不能再撤回
  assert.equal(resolveEntryActions({ status: 3, canApprove: false }, all, false).withdraw, false);
  assert.equal(resolveEntryActions({ status: 2, canApprove: false }, all, false).withdraw, false);
  assert.equal(resolveEntryActions({ status: 1, canApprove: false }, all, false).withdraw, true, "生效中的可以撤回");
  // 功能未开启：一律只读
  assert.deepEqual(resolveEntryActions(pending, all, true), { approve: false, dispute: false, withdraw: false });
  assert.equal(canWithdrawEntry({ status: 0 }), true);
  assert.equal(canWithdrawEntry({ status: 3 }), false);
}

console.log("entries-logic.test.ts: ok");
