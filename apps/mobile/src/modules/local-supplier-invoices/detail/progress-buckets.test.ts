import {
  DETAIL_PROGRESS_BUCKET_COLORS,
  DETAIL_PROGRESS_BUCKETS,
  filterDetailsByProgressBucket,
  getDetailProgressBucket,
  getDetailProgressStats,
  getExecutedPercent,
  getPendingExecutionDetailGuids,
} from "./progress-buckets";
import { DetailAction, EXECUTED_DETAIL_ACTION } from "./detail-status";
import { assertDeepEqual, assertEqual, makeItem } from "./test-helpers";

// 口径搬自 Web InvoiceEdit/workbenchLogic.test.ts 的「处理进度分段」。
const details = [
  makeItem({ detailGuid: "executed", activityType: 99 }),
  makeItem({ detailGuid: "create", existingProductCount: 0, activityType: DetailAction.CreateProduct }),
  makeItem({ detailGuid: "update", activityType: DetailAction.UpdatePurchasePrice }),
  makeItem({ detailGuid: "multi", barcodeStatus: 2, barcodeMatchCount: 0, activityType: DetailAction.AddMultiCode }),
  makeItem({ detailGuid: "wait", barcodeStatus: 2, barcodeMatchCount: 2, activityType: DetailAction.WaitForOperation }),
  // 未检测行即使被手工设成「新建商品」，也必须先归入未检测，不能进入待执行。
  makeItem({ detailGuid: "unchecked", existingProductCount: null, activityType: DetailAction.CreateProduct }),
  makeItem({ detailGuid: "none", activityType: DetailAction.None }),
];

assertEqual(EXECUTED_DETAIL_ACTION, 99, "已执行操作类型为 99");
assertEqual(getDetailProgressBucket(details[0]), "executed", "99 应归为已执行");
assertEqual(getDetailProgressBucket(details[1]), "pending", "新建商品应归为待执行");
assertEqual(getDetailProgressBucket(details[2]), "pending", "更新进货价应归为待执行");
assertEqual(getDetailProgressBucket(details[4]), "waiting", "等待操作应单独成段");
assertEqual(getDetailProgressBucket(details[5]), "unchecked", "未检测行优先归入未检测");
assertEqual(getDetailProgressBucket(details[6]), "none", "操作类型为无应归为无需操作");
assertEqual(
  getDetailProgressBucket(details[6], { none: DetailAction.UpdateItemNumber }),
  "pending",
  "rowActions 覆盖明细原值"
);
assertEqual(
  getDetailProgressBucket(makeItem({ existingProductCount: null, activityType: 99 })),
  "executed",
  "已执行优先于未检测（与 Web 判定顺序一致）"
);
assertEqual(
  getDetailProgressBucket(makeItem({ existingProductCount: undefined, activityType: DetailAction.None })),
  "unchecked",
  "undefined 与 null 同义，都是未检测"
);
assertEqual(
  getDetailProgressBucket(makeItem({ isCreatedByThisInvoice: true, existingProductCount: null })),
  "none",
  "本单新品视为已检测，不归未检测"
);

// 五段互斥且合计等于行数
const stats = getDetailProgressStats(details);
assertDeepEqual(
  { executed: stats.executed, pending: stats.pending, waiting: stats.waiting, unchecked: stats.unchecked, none: stats.none },
  { executed: 1, pending: 3, waiting: 1, unchecked: 1, none: 1 },
  "五段计数"
);
assertEqual(
  stats.executed + stats.pending + stats.waiting + stats.unchecked + stats.none,
  stats.total,
  "五段合计必须等于明细行数"
);
assertEqual(stats.total, details.length, "total 等于明细行数");
for (const detail of details) {
  const hits = DETAIL_PROGRESS_BUCKETS.filter(
    (bucket) => filterDetailsByProgressBucket([detail], bucket).length === 1
  );
  assertEqual(hits.length, 1, `${detail.detailGuid} 只属于一个分段`);
}

// 待执行按操作拆分
assertDeepEqual(
  stats.pendingByAction,
  {
    [DetailAction.CreateProduct]: 1,
    [DetailAction.UpdatePurchasePrice]: 1,
    [DetailAction.UpdateItemNumber]: 0,
    [DetailAction.AddMultiCode]: 1,
  },
  "待执行应按操作类型拆分计数"
);
assertEqual(
  Object.values(stats.pendingByAction).reduce((sum, count) => sum + count, 0),
  stats.pending,
  "拆分合计等于待执行行数"
);

// rowActions 参与统计：把 none 行改成「更新货号」后，待执行 +1、无需操作 -1
const statsWithRowAction = getDetailProgressStats(details, { none: DetailAction.UpdateItemNumber });
assertEqual(statsWithRowAction.pending, 4, "rowActions 让待执行 +1");
assertEqual(statsWithRowAction.none, 0, "rowActions 让无需操作 -1");
assertEqual(statsWithRowAction.pendingByAction[DetailAction.UpdateItemNumber], 1, "更新货号拆分 +1");

// 执行目标行 / 分段筛选
assertDeepEqual(
  getPendingExecutionDetailGuids(details),
  ["create", "update", "multi"],
  "待执行只应包含已检测且待执行的行（未检测的 unchecked 不在内）"
);
assertDeepEqual(
  filterDetailsByProgressBucket(details, "waiting").map((item) => item.detailGuid),
  ["wait"],
  "按分段筛选只保留该段"
);
assertEqual(filterDetailsByProgressBucket(details, "all").length, details.length, "all 不过滤");

// 已执行占比
assertEqual(getExecutedPercent(stats), 14, "1/7 四舍五入为 14%");
assertEqual(getExecutedPercent({ total: 3, executed: 2 }), 67, "2/3 四舍五入为 67%");
assertEqual(getExecutedPercent({ total: 0, executed: 0 }), 0, "空明细为 0");

// 颜色常量
assertDeepEqual(
  DETAIL_PROGRESS_BUCKET_COLORS,
  { executed: "#389e0d", unchecked: "#8c6bd8", pending: "#1677ff", waiting: "#fa8c16", none: "#c9d0db" },
  "分段颜色与 Web 一致"
);
