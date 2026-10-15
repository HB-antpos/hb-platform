import assert from "node:assert/strict";
import { SELECT_ALL_PAGE_SIZE, loadAllTasks, resolveSelectAllState, resolveSelectedTasks } from "./selection";
import { createTask } from "./test-helpers";
import type { StorePriceUpdateTask, StorePriceUpdateTaskPage } from "./types";

async function main() {
  assert.equal(SELECT_ALL_PAGE_SIZE, 200);

  // ---- 全选三态：相对当前筛选下的总数 ----
  assert.equal(resolveSelectAllState(0, 128), "none");
  assert.equal(resolveSelectAllState(30, 128), "some", "只选了已加载的一页：部分选中");
  assert.equal(resolveSelectAllState(128, 128), "all");
  assert.equal(resolveSelectAllState(129, 128), "all", "选中数超过总数（总数在期间减少）也算全选");
  assert.equal(resolveSelectAllState(5, 0), "none", "总数为 0 没有可选的");

  // ---- 逐页拉全：覆盖没滚动加载的任务 ----
  function page(items: StorePriceUpdateTask[], total: number, pageNo: number): StorePriceUpdateTaskPage {
    return {
      items,
      total,
      page: pageNo,
      pageSize: SELECT_ALL_PAGE_SIZE,
      pendingCount: total,
      pendingPriceUpdateCount: total,
      pendingLabelOnlyCount: 0,
      completedCount: 0,
      pendingOverdueCount: 0,
      overdueDays: 7,
      hqSyncEnabled: false,
    };
  }
  const tasks = (from: number, to: number) =>
    Array.from({ length: to - from + 1 }, (_, index) => createTask({ id: from + index }));

  const requested: number[] = [];
  const all = await loadAllTasks(async (pageNo, pageSize) => {
    requested.push(pageNo);
    assert.equal(pageSize, SELECT_ALL_PAGE_SIZE);
    return pageNo === 1 ? page(tasks(1, 200), 450, 1) : pageNo === 2 ? page(tasks(201, 400), 450, 2) : page(tasks(401, 450), 450, 3);
  });
  assert.equal(all.length, 450, "450 项要拉 3 页，不能只选第一页");
  assert.deepEqual(requested, [1, 2, 3], "拉够总数就停，不多请求");

  // 期间别人处理了任务，offset 分页漂移：后一页与前一页重叠 → 按 id 去重；整页没有新任务就停，避免死循环
  const drifting = await loadAllTasks(async (pageNo) =>
    pageNo === 1 ? page(tasks(1, 3), 10, 1) : page(tasks(2, 4), 10, pageNo)
  );
  assert.deepEqual(drifting.map((task) => task.id), [1, 2, 3, 4], "重叠部分去重");

  let stuckCalls = 0;
  const stuck = await loadAllTasks(async () => {
    stuckCalls += 1;
    return page(tasks(1, 3), 1000, 1);
  });
  assert.equal(stuck.length, 3);
  assert.equal(stuckCalls, 2, "第二页没带来任何新任务就停止，不能无限循环");

  // 取消：用户切了筛选 / 退出批量，不再继续拉
  let cancelled = false;
  let cancelCalls = 0;
  const partial = await loadAllTasks(
    async (pageNo) => {
      cancelCalls += 1;
      cancelled = true;
      return page(tasks((pageNo - 1) * 200 + 1, pageNo * 200), 1000, pageNo);
    },
    { shouldCancel: () => cancelled }
  );
  assert.equal(cancelCalls, 1, "取消后不再请求下一页");
  assert.equal(partial.length, 200);

  // ---- 还原已选任务：全选拉回的 pool + 列表已加载的（更新鲜，覆盖同 id） ----
  const pool = new Map(tasks(1, 5).map((task) => [task.id, task] as const));
  const loaded = [createTask({ id: 2, targetRetailPrice: 99 }), createTask({ id: 6 })];
  const selected = resolveSelectedTasks(new Set([2, 4, 6, 99]), pool, loaded);
  assert.deepEqual(selected.map((task) => task.id), [2, 4, 6], "顺序跟随 pool，再补 pool 里没有的；未知 id 直接忽略");
  assert.equal(selected[0].targetRetailPrice, 99, "同 id 以列表已加载的（更新鲜）为准");
  assert.deepEqual(resolveSelectedTasks(new Set(), pool, loaded), []);

  console.log("price-updates/selection.test.ts: ok");
}

void main();
