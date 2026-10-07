import assert from "node:assert/strict";
import { createClientRequestIdHolder } from "./client-request-id";

let counter = 0;
const holder = createClientRequestIdHolder(() => `req-${++counter}`);

// 一次表单会话只生成一次，失败重试沿用同一个
assert.equal(counter, 0, "创建持有者时不生成");
const first = holder.current();
assert.equal(first, "req-1");
assert.equal(holder.current(), first, "重复读取返回同一个");
assert.equal(holder.current(), first, "提交失败后重试仍是同一个");
assert.equal(counter, 1, "重试不会多生成");

// 提交成功后才换新的
assert.equal(holder.rotate(), first, "rotate 返回被换掉的旧号");
const second = holder.current();
assert.equal(second, "req-2");
assert.notEqual(second, first, "成功后换新号");
assert.equal(holder.current(), second);

// 尚未生成就 rotate 不报错
const fresh = createClientRequestIdHolder(() => "x");
assert.equal(fresh.rotate(), null);
assert.equal(fresh.current(), "x");

console.log("client-request-id.test.ts: ok");
