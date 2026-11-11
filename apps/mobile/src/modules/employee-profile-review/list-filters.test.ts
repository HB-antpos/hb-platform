import assert from "node:assert/strict";
import {
  EMPLOYEE_PROFILE_REVIEW_SEGMENTS,
  getEmployeeProfileReviewListQueryKey,
  getEmployeeProfileReviewSort,
  getEmployeeProfileReviewStatusFilter,
  normalizeEmployeeProfileReviewSearch,
} from "./list-filters";

assert.deepEqual([...EMPLOYEE_PROFILE_REVIEW_SEGMENTS], ["pending", "processed"]);
assert.equal(getEmployeeProfileReviewStatusFilter("pending"), "Pending");
assert.equal(getEmployeeProfileReviewStatusFilter("processed"), "Processed");

assert.equal(normalizeEmployeeProfileReviewSearch("  amy "), "amy");
assert.equal(normalizeEmployeeProfileReviewSearch(undefined), "");
assert.equal(normalizeEmployeeProfileReviewSearch("x".repeat(150)).length, 100);

// 查询键必须保留统一前缀，权限失效清理（前缀匹配）才能覆盖所有分段和搜索结果。
const key = getEmployeeProfileReviewListQueryKey("processed", " bob ");
assert.deepEqual(key, ["employeeProfileReview", "requests", "Processed", "bob"]);
assert.notDeepEqual(
  getEmployeeProfileReviewListQueryKey("pending", ""),
  ["employeeProfileReview", "requests", "Pending", "count"],
  "列表键不能与外壳角标计数键冲突"
);

console.log("list-filters tests passed");

// 待审核等得最久的在前；已处理沿用后端默认顺序（不传 sort）。排序由分段决定，所以不进查询键。
assert.equal(getEmployeeProfileReviewSort("pending"), "oldest");
assert.equal(getEmployeeProfileReviewSort("processed"), undefined);
