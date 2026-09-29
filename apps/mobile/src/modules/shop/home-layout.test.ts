import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { resolveHomeProductColumns } from "./home-layout";

assert.equal(resolveHomeProductColumns(360), 1, "Zebra TC26（360dp）一行一个商品");
assert.equal(resolveHomeProductColumns(430), 1, "大屏手机仍是单列行");
assert.equal(resolveHomeProductColumns(699), 1, "低于宽屏阈值保持单列");
assert.equal(resolveHomeProductColumns(700), 2, "宽屏阈值起并排两列");
assert.equal(resolveHomeProductColumns(820), 2, "平板两列");

const currentDirectory = dirname(fileURLToPath(import.meta.url));
const homeSource = readFileSync(resolve(currentDirectory, "../../../app/(shell)/home.tsx"), "utf8");
assert.match(
  homeSource,
  /key=\{`product-grid-\$\{productColumns\}`\}[\s\S]*?numColumns=\{productColumns\}/,
  "列数随窗口宽度变化时 FlatList 必须随列数换 key 重建，否则运行时改 numColumns 会报错",
);
assert.match(
  homeSource,
  /columnWrapperStyle=\{productColumns > 1 \? [^}]+ : undefined\}/,
  "单列时不能传 columnWrapperStyle，否则 FlatList 会直接抛错",
);

console.log("home-layout.test.ts: ok");
