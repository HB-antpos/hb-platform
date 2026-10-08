import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { join } from "node:path";

// HB新品 页必须使用公共分页条，且每页条数的本机记忆沿用旧 key（不重置用户已保存的选择）
const screen = readFileSync(join(__dirname, "container-new-products-screen.tsx"), "utf8");
const storage = readFileSync(join(__dirname, "page-size-storage.ts"), "utf8");

assert.match(screen, /from "@\/components\/ui\/pagination\/PaginationBar"/);
assert.match(screen, /<PaginationBar/);
assert.match(screen, /pageSizeOptions=\{CONTAINER_NEW_PRODUCTS_PAGE_SIZE_OPTIONS\}/);
assert.doesNotMatch(screen, /PagerButton|PageSheet/, "旧的私有分页实现应已删除");
assert.match(storage, /"hb\.containerNewProducts\.pageSize"/, "存储 key 不能变");

console.log("container-new-products screen pagination contract tests passed");
