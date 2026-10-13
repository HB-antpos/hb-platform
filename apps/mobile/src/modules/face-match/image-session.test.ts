import assert from "node:assert/strict";
import {
  CAPTURE_SLOT,
  createReviewImageSession,
  referenceSlot,
  type ReviewImageStates,
} from "./image-session";

function httpError(status: number, errorCode?: string) {
  return Object.assign(new Error("failed"), { response: { status, data: errorCode ? { errorCode } : {} } });
}

/** 可手动完成的异步取图，便于控制时序。 */
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
  // ───────── 只在 start 之后才请求：先抓拍，再第一张参考照片；其余按需 ─────────
  {
    const calls: string[] = [];
    const snapshots: ReviewImageStates[] = [];
    const session = createReviewImageSession({
      referenceCount: 3,
      fetchCapture: async () => {
        calls.push("capture");
        return "data:image/jpeg;base64,CAP";
      },
      fetchReference: async (index) => {
        calls.push(`reference-${index}`);
        return `data:image/jpeg;base64,REF${index}`;
      },
      onChange: (states) => snapshots.push(states),
    });
    assert.deepEqual(calls, [], "创建会话不发任何请求");
    await session.start();
    assert.deepEqual(calls, ["capture", "reference-0"], "只先取抓拍和第一张参考照片，没有预加载其余参考照片");
    assert.deepEqual(session.getStates().get(CAPTURE_SLOT), { status: "loaded", uri: "data:image/jpeg;base64,CAP" });
    assert.equal(session.getStates().has(referenceSlot(1)), false);

    await session.loadReference(2);
    await session.loadReference(2);
    assert.deepEqual(calls, ["capture", "reference-0", "reference-2"], "已加载的不重复请求");
    await session.loadReference(7);
    await session.loadReference(-1);
    assert.equal(calls.length, 3, "越界下标不请求");
    session.dispose();
  }

  // ───────── 没有参考照片：只取抓拍 ─────────
  {
    const calls: string[] = [];
    const session = createReviewImageSession({
      referenceCount: 0,
      fetchCapture: async () => {
        calls.push("capture");
        return "data:image/png;base64,X";
      },
      fetchReference: async () => {
        calls.push("reference");
        return "";
      },
      onChange: () => undefined,
    });
    await session.start();
    assert.deepEqual(calls, ["capture"]);
    session.dispose();
  }

  // ───────── dispose：中止在途请求、清空状态、之后不再回调，迟到的响应被丢弃 ─────────
  {
    const pending = deferred<string>();
    let aborted = false;
    let notifications = 0;
    const session = createReviewImageSession({
      referenceCount: 1,
      fetchCapture: (signal) => {
        signal.addEventListener("abort", () => {
          aborted = true;
        });
        return pending.promise;
      },
      fetchReference: async () => "data:image/jpeg;base64,REF",
      onChange: () => {
        notifications += 1;
      },
    });
    const started = session.start();
    assert.equal(notifications, 1, "loading 状态通知一次");
    session.dispose();
    assert.equal(aborted, true, "关闭时中止在途请求");
    assert.equal(session.getStates().size, 0, "关闭时清空全部状态（图片引用立即丢弃）");
    pending.resolve("data:image/jpeg;base64,LATE");
    await started;
    assert.equal(notifications, 1, "迟到的响应不再回调");
    assert.equal(session.getStates().size, 0, "迟到的图片不会重新进入状态");
    await session.loadReference(0);
    assert.equal(notifications, 1, "关闭后不再发起新的请求");
  }

  // ───────── 加载成功后 dispose：状态里的图片引用被清空 ─────────
  {
    const session = createReviewImageSession({
      referenceCount: 0,
      fetchCapture: async () => "data:image/jpeg;base64,SECRET",
      fetchReference: async () => "",
      onChange: () => undefined,
    });
    await session.start();
    assert.equal(session.getStates().size, 1);
    session.dispose();
    assert.equal(session.getStates().size, 0);
    session.dispose(); // 幂等
  }

  // ───────── 共享失败：无权限 / 未配置 / 登录过期不再逐张重试 ─────────
  {
    let referenceCalls = 0;
    const session = createReviewImageSession({
      referenceCount: 3,
      fetchCapture: async () => {
        throw httpError(403, "FACE_MATCH_FORBIDDEN");
      },
      fetchReference: async () => {
        referenceCalls += 1;
        return "x";
      },
      onChange: () => undefined,
    });
    await session.start();
    assert.deepEqual(session.getStates().get(CAPTURE_SLOT), { status: "error", kind: "forbidden" });
    assert.deepEqual(session.getStates().get(referenceSlot(0)), { status: "error", kind: "forbidden" });
    await session.loadReference(1);
    assert.equal(referenceCalls, 0, "共享失败后其余图片直接标记同样的失败，不再请求");
    assert.deepEqual(session.getStates().get(referenceSlot(1)), { status: "error", kind: "forbidden" });
    session.dispose();
  }

  // ───────── 单张被清理：只影响这一张，其余仍可加载 ─────────
  {
    const session = createReviewImageSession({
      referenceCount: 2,
      fetchCapture: async () => {
        throw httpError(404, "FACE_MATCH_IMAGE_GONE");
      },
      fetchReference: async (index) => `data:image/jpeg;base64,R${index}`,
      onChange: () => undefined,
    });
    await session.start();
    assert.deepEqual(session.getStates().get(CAPTURE_SLOT), { status: "error", kind: "gone" });
    assert.deepEqual(session.getStates().get(referenceSlot(0)), { status: "loaded", uri: "data:image/jpeg;base64,R0" });
    await session.loadReference(1);
    assert.equal(session.getStates().get(referenceSlot(1))?.status, "loaded");
    session.dispose();
  }

  // ───────── 通知里的状态快照是新对象（React 依赖引用变化重绘） ─────────
  {
    const seen: ReviewImageStates[] = [];
    const session = createReviewImageSession({
      referenceCount: 0,
      fetchCapture: async () => "data:image/jpeg;base64,A",
      fetchReference: async () => "",
      onChange: (states) => seen.push(states),
    });
    await session.start();
    assert.equal(seen.length, 2, "loading + loaded 各通知一次");
    assert.notEqual(seen[0], seen[1]);
    session.dispose();
  }
}

run().then(
  () => console.log("image-session.test.ts: ok"),
  (error) => {
    console.error(error);
    process.exit(1);
  },
);
