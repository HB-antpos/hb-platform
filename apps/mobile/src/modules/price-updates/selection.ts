import type { StorePriceUpdateTask, StorePriceUpdateTaskPage } from "./types";

export type SelectAllState = "none" | "some" | "all";

/** 批量处理的「全选」三态：未选 / 部分 / 全部（相对当前筛选下的任务总数）。 */
export function resolveSelectAllState(selectedCount: number, total: number): SelectAllState {
  if (selectedCount <= 0 || total <= 0) return "none";
  return selectedCount >= total ? "all" : "some";
}

/** 全选时逐页拉取的页大小（后端上限 200）。 */
export const SELECT_ALL_PAGE_SIZE = 200;
/** 兜底：最多拉 100 页（2 万条），防止异常数据让加载循环无限进行。 */
const SELECT_ALL_MAX_PAGES = 100;

/**
 * 逐页拉取「当前筛选下的全部任务」。列表页是滚动分页的，只加载了前几十条；
 * 「全选」必须覆盖没滚到的任务，所以点全选时在这里把剩下的拉齐。
 *
 * 用 offset 分页，期间若别人改了任务（完成 / 新增），后面的页可能与前面重叠：
 * 这里按任务 id 去重，并在一页没有带来任何新任务时停止，避免死循环。
 */
export async function loadAllTasks(
  fetchPage: (page: number, pageSize: number) => Promise<StorePriceUpdateTaskPage>,
  options: { shouldCancel?: () => boolean } = {}
): Promise<StorePriceUpdateTask[]> {
  const collected: StorePriceUpdateTask[] = [];
  const seen = new Set<number>();
  let total = Number.POSITIVE_INFINITY;

  for (let page = 1; page <= SELECT_ALL_MAX_PAGES && collected.length < total; page += 1) {
    if (options.shouldCancel?.()) break;
    const result = await fetchPage(page, SELECT_ALL_PAGE_SIZE);
    total = result.total;

    let added = 0;
    for (const item of result.items) {
      if (!seen.has(item.id)) {
        seen.add(item.id);
        collected.push(item);
        added += 1;
      }
    }
    if (added === 0) break;
  }
  return collected;
}

/**
 * 把已选 id 还原成任务对象：全选拉回来的完整列表（pool）里有的、列表页已加载的（loaded，更新鲜，覆盖同 id）都认。
 * 顺序跟随 pool，再补 loaded 里 pool 没有的，保证批量处理按列表顺序（超期最久的在前）逐个进行。
 */
export function resolveSelectedTasks(
  selectedIds: ReadonlySet<number>,
  pool: ReadonlyMap<number, StorePriceUpdateTask>,
  loaded: readonly StorePriceUpdateTask[]
): StorePriceUpdateTask[] {
  const merged = new Map<number, StorePriceUpdateTask>(pool);
  for (const task of loaded) {
    merged.set(task.id, task);
  }
  return [...merged.values()].filter((task) => selectedIds.has(task.id));
}
