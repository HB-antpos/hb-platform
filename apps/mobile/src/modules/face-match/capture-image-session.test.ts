import assert from "node:assert/strict";
import { createCaptureImageSession, type CaptureImageStates } from "./capture-image-session";

function httpError(status: number, errorCode?: string) {
  return Object.assign(new Error("failed"), { response: { status, data: errorCode ? { errorCode } : {} } });
}

function deferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (error: unknown) => void;
  const promise = new Promise<T>((res, rej) => {
    resolve = res;
    reject = rej;
  });
  return { promise, resolve, reject };
}

async function run() {
  // ───────── 创建不发请求；start 并行读取存在编号的槽位 ─────────
  {
    const calls: string[] = [];
    const session = createCaptureImageSession({
      fetchers: {
        face: async () => {
          calls.push("face");
          return "data:image/jpeg;base64,FACE";
        },
        scene: async () => {
          calls.push("scene");
          return "data:image/jpeg;base64,SCENE";
        },
      },
      onChange: () => undefined,
    });
    assert.deepEqual(calls, [], "创建会话不发任何请求");
    await session.start();
    assert.deepEqual(calls.sort(), ["face", "scene"]);
    assert.deepEqual(session.getStates().get("face"), { status: "loaded", uri: "data:image/jpeg;base64,FACE" });
    assert.deepEqual(session.getStates().get("scene"), { status: "loaded", uri: "data:image/jpeg;base64,SCENE" });
    session.dispose();
    assert.equal(session.getStates().size, 0, "dispose 后清空全部状态引用");
  }

  // ───────── 没有编号的槽位不请求、不出现在状态里 ─────────
  {
    const calls: string[] = [];
    const session = createCaptureImageSession({
      fetchers: {
        face: null,
        scene: async () => {
          calls.push("scene");
          return "data:image/png;base64,S";
        },
      },
      onChange: () => undefined,
    });
    await session.start();
    assert.deepEqual(calls, ["scene"]);
    assert.equal(session.getStates().has("face"), false);
    session.dispose();
  }

  // ───────── 单张失败不影响另一张，错误按类别归类 ─────────
  {
    const session = createCaptureImageSession({
      fetchers: {
        face: async () => {
          throw httpError(403, "FACE_MATCH_FORBIDDEN");
        },
        scene: async () => "data:image/jpeg;base64,OK",
      },
      onChange: () => undefined,
    });
    await session.start();
    assert.deepEqual(session.getStates().get("face"), { status: "error", kind: "forbidden" });
    assert.equal(session.getStates().get("scene")?.status, "loaded");
    session.dispose();
  }

  // ───────── 关闭后才返回的响应直接丢弃，此后不再回调；关闭时中止在途请求 ─────────
  {
    const pending = deferred<string>();
    let aborted = false;
    const snapshots: CaptureImageStates[] = [];
    const session = createCaptureImageSession({
      fetchers: {
        face: (signal) => {
          signal.addEventListener("abort", () => {
            aborted = true;
          });
          return pending.promise;
        },
        scene: null,
      },
      onChange: (states) => snapshots.push(states),
    });
    const started = session.start();
    assert.equal(session.getStates().get("face")?.status, "loading");
    session.dispose();
    assert.equal(aborted, true, "关闭时中止在途请求");
    const callbacksAtDispose = snapshots.length;
    pending.resolve("data:image/jpeg;base64,LATE");
    await started;
    assert.equal(snapshots.length, callbacksAtDispose, "关闭后不再回调");
    assert.equal(session.getStates().size, 0, "迟到的图片不进入状态");
  }

  // ───────── 命中流的行：只有命中图一个槽位（hit），face / scene 没有读取函数；hit 槽位省略时与旧行为一致 ─────────
  {
    const calls: string[] = [];
    const session = createCaptureImageSession({
      fetchers: {
        face: null,
        scene: null,
        hit: async (signal) => {
          assert.equal(signal.aborted, false);
          calls.push("hit");
          return "data:image/jpeg;base64,HIT";
        },
      },
      onChange: () => undefined,
    });
    await session.start();
    assert.deepEqual(calls, ["hit"]);
    assert.deepEqual(session.getStates().get("hit"), { status: "loaded", uri: "data:image/jpeg;base64,HIT" });
    assert.equal(session.getStates().has("face"), false);
    assert.equal(session.getStates().has("scene"), false);
    session.dispose();
    assert.equal(session.getStates().size, 0, "dispose 后命中图引用也被清空");

    // 命中图读取失败按类别归类，不影响其他槽位
    const failing = createCaptureImageSession({
      fetchers: {
        face: async () => "data:image/jpeg;base64,FACE",
        scene: null,
        hit: async () => {
          throw httpError(410, "FACE_MATCH_IMAGE_GONE");
        },
      },
      onChange: () => undefined,
    });
    await failing.start();
    assert.equal(failing.getStates().get("face")?.status, "loaded");
    assert.equal(failing.getStates().get("hit")?.status, "error");
    failing.dispose();

    // 关闭时中止命中图的在途请求，迟到的响应丢弃
    const pending = deferred<string>();
    let aborted = false;
    const late = createCaptureImageSession({
      fetchers: {
        face: null,
        scene: null,
        hit: (signal) => {
          signal.addEventListener("abort", () => {
            aborted = true;
          });
          return pending.promise;
        },
      },
      onChange: () => undefined,
    });
    const started = late.start();
    late.dispose();
    assert.equal(aborted, true);
    pending.resolve("data:image/jpeg;base64,LATE");
    await started;
    assert.equal(late.getStates().size, 0);
  }

  // ───────── 关闭后失败也不再回调 ─────────
  {
    const pending = deferred<string>();
    const snapshots: CaptureImageStates[] = [];
    const session = createCaptureImageSession({
      fetchers: { face: () => pending.promise, scene: null },
      onChange: (states) => snapshots.push(states),
    });
    const started = session.start();
    session.dispose();
    const before = snapshots.length;
    pending.reject(httpError(500));
    await started;
    assert.equal(snapshots.length, before);
  }
}

run().then(
  () => console.log("capture-image-session.test.ts: ok"),
  (error) => {
    console.error(error);
    process.exit(1);
  },
);
