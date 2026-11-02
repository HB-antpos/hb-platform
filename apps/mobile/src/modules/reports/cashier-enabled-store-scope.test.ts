import assert from "node:assert/strict";
import {
  getCashierEnabledStoreCodes,
  getCashierScopedBranchCodes,
  getCashierScopedBranchSelection,
} from "./cashier-enabled-store-scope";

const enabledCodes = getCashierEnabledStoreCodes([
  { value: " 1001 " },
  { value: "1002" },
  { value: "1001" },
  { value: "AbC" },
  { value: "abc" },
  { value: "   " },
]);

assert.deepEqual(
  enabledCodes,
  ["1001", "1002", "AbC"],
  "启用收银分店代码必须去空白并按大小写不敏感去重",
);
assert.deepEqual(
  getCashierScopedBranchCodes(enabledCodes),
  enabledCodes,
  "未选择单店时必须显式传入完整启用分店范围",
);
assert.deepEqual(
  getCashierScopedBranchCodes(enabledCodes, " abc "),
  ["AbC"],
  "单店选择必须使用启用白名单中的规范代码",
);
assert.deepEqual(
  getCashierScopedBranchCodes(enabledCodes, "9999"),
  [],
  "停用或已失效的单店选择必须 fail closed",
);
assert.deepEqual(
  getCashierScopedBranchCodes([], undefined),
  [],
  "没有启用收银分店时不得退化成全店范围",
);

assert.deepEqual(
  getCashierScopedBranchSelection(enabledCodes, []),
  enabledCodes,
  "未选分店时必须显式传入完整启用分店范围",
);
assert.deepEqual(
  getCashierScopedBranchSelection(enabledCodes, ["1002", " abc "]),
  ["1002", "AbC"],
  "多店选择按白名单顺序、使用白名单中的规范代码",
);
assert.deepEqual(
  getCashierScopedBranchSelection(enabledCodes, ["9999", "1001"]),
  ["1001"],
  "已停用的分店从多店选择中剔除，其余仍可查询",
);
assert.deepEqual(
  getCashierScopedBranchSelection(enabledCodes, ["9999"]),
  [],
  "所选分店全部失效时必须 fail closed，不得退化成全店范围",
);
assert.deepEqual(
  getCashierScopedBranchSelection([], []),
  [],
  "没有启用收银分店时多选范围同样为空",
);

console.log("cashier-enabled-store-scope.test.ts: ok");
