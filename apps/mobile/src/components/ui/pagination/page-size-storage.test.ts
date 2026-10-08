import assert from "node:assert/strict";
import { createPageSizeStorage, type PageSizeStorageAdapter } from "./page-size-storage";

const OPTIONS = [50, 100, 200, 500] as const;

function memoryStorage(initial: Record<string, string> = {}, options: { failRead?: boolean; failWrite?: boolean } = {}) {
  const data = new Map(Object.entries(initial));
  let reads = 0;
  const adapter: PageSizeStorageAdapter = {
    async getString(key) {
      reads += 1;
      if (options.failRead) throw new Error("read failed");
      return data.get(key) ?? null;
    },
    async setString(key, value) {
      if (options.failWrite) throw new Error("write failed");
      data.set(key, value);
    },
  };
  return { adapter, data, reads: () => reads };
}

async function main() {
  // 读到合法值：返回并缓存，第二次不再读存储，peek 同步可见
  {
    const store = memoryStorage({ "k.size": "200" });
    const sizeStorage = createPageSizeStorage({ key: "k.size", options: OPTIONS, defaultSize: 50, storage: store.adapter });
    assert.equal(sizeStorage.peek(), null);
    assert.equal(await sizeStorage.read(), 200);
    assert.equal(sizeStorage.peek(), 200);
    assert.equal(await sizeStorage.read(), 200);
    assert.equal(store.reads(), 1);
  }

  // 没存过 / 脏值 / 不在可选项里：回到默认值
  for (const initial of [{} as Record<string, string>, { "k.size": "abc" }, { "k.size": "20" }]) {
    const sizeStorage = createPageSizeStorage({ key: "k.size", options: OPTIONS, defaultSize: 100, storage: memoryStorage(initial).adapter });
    assert.equal(await sizeStorage.read(), 100);
  }

  // 读失败：回到默认值并缓存，不抛错
  {
    const sizeStorage = createPageSizeStorage({ key: "k.size", options: OPTIONS, defaultSize: 50, storage: memoryStorage({}, { failRead: true }).adapter });
    assert.equal(await sizeStorage.read(), 50);
    assert.equal(sizeStorage.peek(), 50);
  }

  // remember：立刻更新内存缓存，并以字符串形式落盘到同一个 key
  {
    const store = memoryStorage();
    const sizeStorage = createPageSizeStorage({ key: "hb.example.pageSize", options: OPTIONS, defaultSize: 50, storage: store.adapter });
    sizeStorage.remember(500);
    assert.equal(sizeStorage.peek(), 500);
    await new Promise((resolve) => setTimeout(resolve, 0));
    assert.equal(store.data.get("hb.example.pageSize"), "500");
    assert.equal(await sizeStorage.read(), 500);
  }

  // 落盘失败不抛错（只是不记住）
  {
    const sizeStorage = createPageSizeStorage({ key: "k.size", options: OPTIONS, defaultSize: 50, storage: memoryStorage({}, { failWrite: true }).adapter });
    sizeStorage.remember(100);
    await new Promise((resolve) => setTimeout(resolve, 0));
    assert.equal(sizeStorage.peek(), 100);
  }

  // 两个页面各用各的缓存，互不影响
  {
    const store = memoryStorage({ "a.size": "100", "b.size": "500" });
    const a = createPageSizeStorage({ key: "a.size", options: OPTIONS, defaultSize: 50, storage: store.adapter });
    const b = createPageSizeStorage({ key: "b.size", options: OPTIONS, defaultSize: 50, storage: store.adapter });
    assert.equal(await a.read(), 100);
    assert.equal(await b.read(), 500);
  }

  console.log("page size storage tests passed");
}

void main();
