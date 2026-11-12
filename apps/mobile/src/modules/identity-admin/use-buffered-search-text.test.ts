import assert from "node:assert/strict";
import Module from "node:module";
import React, { act } from "react";

// 搜索框本地输入缓冲的真实渲染测试：
// 输入框自己持有文字；delay > 0 时停手后才通知上层；清除立即通知并取消待发；
// 上层主动改值会同步回输入框，自己发出去的值不会回灌（否则会打断正在进行的输入）。
type HookResult = { text: string; handleChange: (value: string) => void; clear: () => void };

async function run() {
  Object.assign(globalThis, { __DEV__: false, IS_REACT_ACT_ENVIRONMENT: true, React });
  const mockModule = (filename: string, exports: object) => {
    const module = new Module(filename);
    module.filename = filename;
    module.loaded = true;
    module.exports = exports;
    require.cache[filename] = module;
  };
  const rendererReactFilename = require.resolve("react", { paths: [require.resolve("test-renderer")] });
  if (rendererReactFilename !== require.resolve("react")) mockModule(rendererReactFilename, React);

  const { createRoot } = await import("test-renderer");
  const { useBufferedSearchText } = await import("./use-buffered-search-text");

  const api: { current: HookResult | null } = { current: null };
  function Probe({ value, onChange, delay }: { value: string; onChange: (value: string) => void; delay: number }) {
    api.current = useBufferedSearchText(value, onChange, delay);
    return React.createElement("Text", null, api.current.text);
  }
  const wait = (ms: number) => act(async () => { await new Promise((resolve) => setTimeout(resolve, ms)); });
  const mount = async (value: string, onChange: (value: string) => void, delay: number) => {
    const root = createRoot();
    await act(async () => { root.render(React.createElement(Probe, { value, onChange, delay })); });
    const rerender = (nextValue: string) => act(async () => { root.render(React.createElement(Probe, { value: nextValue, onChange, delay })); });
    return { root, rerender };
  };
  const type = (value: string) => act(async () => { api.current!.handleChange(value); });

  // ---------- delay = 0：与原来一致，每个按键立即通知上层 ----------
  {
    const emitted: string[] = [];
    await mount("", (value) => emitted.push(value), 0);
    await type("l");
    await type("lu");
    assert.deepEqual(emitted, ["l", "lu"]);
    assert.equal(api.current!.text, "lu");
  }

  // ---------- delay > 0：连续输入只在停手后通知一次，文字立刻显示 ----------
  {
    const emitted: string[] = [];
    await mount("", (value) => emitted.push(value), 40);
    await type("l");
    await type("lu");
    await type("lulu");
    assert.equal(api.current!.text, "lulu", "输入框文字立即更新，不等上层");
    assert.deepEqual(emitted, [], "停手之前上层一次都没收到（页面不会随按键重绘）");
    await wait(90);
    assert.deepEqual(emitted, ["lulu"], "停手后只通知一次，且是最终文字");
  }

  // ---------- 自己发出去的值回灌时不打断输入 ----------
  {
    const emitted: string[] = [];
    const { rerender } = await mount("", (value) => emitted.push(value), 20);
    await type("lu");
    await wait(60);
    assert.deepEqual(emitted, ["lu"]);
    await type("lul"); // 还没发出去，输入还在继续
    await rerender("lu"); // 上层把刚才收到的 "lu" 当作新 value 传回来
    assert.equal(api.current!.text, "lul", "回灌自己发出去的值不能覆盖正在输入的文字");
    await wait(60);
    assert.deepEqual(emitted, ["lu", "lul"]);
  }

  // ---------- 上层主动改值：同步回输入框，并丢弃尚未发出的旧输入 ----------
  {
    const emitted: string[] = [];
    const { rerender } = await mount("", (value) => emitted.push(value), 40);
    await type("lu");
    await rerender("x"); // 例如重新打开弹层时被重置
    assert.equal(api.current!.text, "x");
    await wait(90);
    assert.deepEqual(emitted, [], "被覆盖的旧输入不能稍后把新值冲掉");
  }

  // ---------- 清除：立即通知空串，并取消尚未发出的那一次 ----------
  {
    const emitted: string[] = [];
    await mount("", (value) => emitted.push(value), 40);
    await type("lulu");
    await act(async () => { api.current!.clear(); });
    assert.equal(api.current!.text, "");
    assert.deepEqual(emitted, [""], "清除立即通知，不等停手");
    await wait(90);
    assert.deepEqual(emitted, [""], "之前待发的 lulu 被取消，不会再发出");
  }

  console.log("use-buffered-search-text tests passed");
}

run().catch((error) => {
  console.error(error);
  process.exit(1);
});
