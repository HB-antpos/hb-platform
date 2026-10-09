import assert from "node:assert/strict";
import test from "node:test";

import type {
  CustomerDisplayAdvertisementItem,
  CustomerDisplayAdvertisementResponse,
} from "./advertisement-api";
import {
  CustomerDisplayAdvertisementPlayback,
  type CachedCustomerDisplayAdvertisement,
} from "./advertisement-playback";

test("只缓存当前有效素材，按 sort/id 轮播本地 URI，并在刷新间隔内复用快照", async () => {
  let remoteCalls = 0;
  const published: (
    | Readonly<{ kind: "image" | "video"; localUri: string }>
    | null
  )[] = [];
  const playback = new CustomerDisplayAdvertisementPlayback({
    now: () => new Date("2026-07-28T00:00:00.000Z"),
    remote: {
      async getActive(): Promise<CustomerDisplayAdvertisementResponse> {
        remoteCalls += 1;
        return {
          storeCode: "S001",
          generatedAtIso: "2026-07-28T00:00:00.000Z",
          items: [
            advert("expired", {
              effectiveEndIso: "2026-07-27T23:59:59.000Z",
            }),
            advert("video", { kind: "video", sortOrder: 2 }),
            advert("image", { sortOrder: 1 }),
          ],
        };
      },
    },
    cache: {
      async cache(items) {
        return items.map(
          (item): CachedCustomerDisplayAdvertisement => ({
            ...item,
            localUri: `file:///cache/${item.id}.${item.kind === "image" ? "png" : "mp4"}`,
          }),
        );
      },
    },
    sink: {
      async setAdvert(advertisement) {
        published.push(advertisement);
      },
    },
  });

  assert.equal(await playback.refresh("S001"), "updated");
  assert.equal(await playback.refresh("S001"), "unchanged");
  assert.equal(remoteCalls, 1);
  assert.deepEqual(published, [
    { kind: "image", localUri: "file:///cache/image.png" },
  ]);

  assert.equal(await playback.advance(), true);
  assert.deepEqual(published.at(-1), {
    kind: "video",
    localUri: "file:///cache/video.mp4",
  });
});

test("远端或缓存失败保留最后一个本地快照；门店切换失败不泄漏旧门店广告", async () => {
  let fail = false;
  const published: unknown[] = [];
  const playback = new CustomerDisplayAdvertisementPlayback({
    now: () => new Date("2026-07-28T00:00:00.000Z"),
    remote: {
      async getActive(storeCode) {
        if (fail) throw new Error("offline");
        return {
          storeCode,
          generatedAtIso: "2026-07-28T00:00:00.000Z",
          items: [advert("one")],
        };
      },
    },
    cache: {
      async cache(items) {
        return items.map((item) => ({
          ...item,
          localUri: "file:///cache/one.png",
        }));
      },
    },
    sink: {
      async setAdvert(advertisement) {
        published.push(advertisement);
      },
    },
  });

  assert.equal(await playback.refresh("S001"), "updated");
  fail = true;
  assert.equal(await playback.refresh("S001", true), "retained");
  assert.equal(await playback.refresh("S002", true), "cleared");
  assert.equal(published.at(-1), null);
});

test("开始播放按五分钟刷新、十秒轮播，停止后取消两个计时器", async () => {
  const scheduled: Readonly<{
    intervalMs: number;
    listener(): void;
    cancel(): void;
  }>[] = [];
  let cancellations = 0;
  const playback = new CustomerDisplayAdvertisementPlayback({
    now: () => new Date("2026-07-28T00:00:00.000Z"),
    remote: {
      async getActive(storeCode) {
        return {
          storeCode,
          generatedAtIso: "2026-07-28T00:00:00.000Z",
          items: [],
        };
      },
    },
    cache: { async cache() { return []; } },
    sink: { async setAdvert() {} },
    scheduler: {
      every(intervalMs, listener) {
        const entry = {
          intervalMs,
          listener,
          cancel() {
            cancellations += 1;
          },
        };
        scheduled.push(entry);
        return entry.cancel;
      },
    },
  });

  playback.start("S001");
  assert.deepEqual(
    scheduled.map((entry) => entry.intervalMs),
    [300_000, 10_000],
  );
  playback.stop();
  assert.equal(cancellations, 2);
});

type Published = Readonly<{ kind: "image" | "video"; localUri: string }> | null;

/** 构造一个固定返回给定素材的播放器，sink 记录每次发布的本地 URI。 */
function slotPlayback(items: readonly CustomerDisplayAdvertisementItem[]) {
  const published: Published[] = [];
  const playback = new CustomerDisplayAdvertisementPlayback({
    now: () => new Date("2026-07-28T00:00:00.000Z"),
    remote: {
      async getActive(
        storeCode,
      ): Promise<CustomerDisplayAdvertisementResponse> {
        return {
          storeCode,
          generatedAtIso: "2026-07-28T00:00:00.000Z",
          items,
        };
      },
    },
    cache: {
      async cache(requested) {
        return requested.map(
          (item): CachedCustomerDisplayAdvertisement => ({
            ...item,
            localUri: `file:///cache/${item.id}.png`,
          }),
        );
      },
    },
    sink: {
      async setAdvert(advertisement) {
        published.push(advertisement);
      },
    },
  });
  const ids = () =>
    published.map((entry) =>
      entry === null ? null : entry.localUri.slice("file:///cache/".length, -4),
    );
  return { playback, published, ids };
}

test("空闲位只轮播 landscape+any，收银位只轮播 portrait+any", async () => {
  const { playback, ids } = slotPlayback([
    advert("a-land", { sortOrder: 1, orientation: "landscape" }),
    advert("b-port", { sortOrder: 2, orientation: "portrait" }),
    advert("c-any", { sortOrder: 3, orientation: "any" }),
  ]);

  // 默认空闲位：首条 a-land，轮转跳过 b-port。
  await playback.refresh("S001");
  await playback.advance();
  await playback.advance();
  await playback.advance();
  assert.deepEqual(ids(), ["a-land", "c-any", "a-land", "c-any"]);

  // 切到收银位：当前 c-any 仍适合，不换；之后只轮转 portrait+any。
  await playback.setSlot("checkout");
  assert.deepEqual(ids().slice(4), []);
  await playback.advance();
  await playback.advance();
  await playback.advance();
  assert.deepEqual(ids().slice(4), ["b-port", "c-any", "b-port"]);
});

test("刷新后的首条按当前广告位选取", async () => {
  const { playback, ids } = slotPlayback([
    advert("a-land", { sortOrder: 1, orientation: "landscape" }),
    advert("b-port", { sortOrder: 2, orientation: "portrait" }),
  ]);
  await playback.setSlot("checkout");
  await playback.refresh("S001");
  assert.deepEqual(ids(), ["b-port"]);

  await playback.setSlot("idle");
  assert.deepEqual(ids(), ["b-port", "a-land"]);
  await playback.refresh("S001", true);
  assert.deepEqual(ids().at(-1), "a-land");
});

test("当前位置没有任何匹配素材时退回播放全部", async () => {
  const { playback, ids } = slotPlayback([
    advert("a-land", { sortOrder: 1, orientation: "landscape" }),
    advert("b-land", { sortOrder: 2, orientation: "landscape" }),
  ]);
  await playback.refresh("S001");
  await playback.setSlot("checkout");
  // 收银位没有竖版/通用素材：保持当前、轮转时在全部素材间转。
  await playback.advance();
  await playback.advance();
  assert.deepEqual(ids(), ["a-land", "b-land", "a-land"]);
  assert.equal(ids().includes(null), false);
});

test("位置切换时当前广告不适合新位置就立即换下一条并发布；适合则不重复发布", async () => {
  const { playback, ids } = slotPlayback([
    advert("a-land", { sortOrder: 1, orientation: "landscape" }),
    advert("b-port", { sortOrder: 2, orientation: "portrait" }),
    advert("c-any", { sortOrder: 3 }),
  ]);
  await playback.refresh("S001");
  assert.deepEqual(ids(), ["a-land"]);

  await playback.setSlot("checkout");
  assert.deepEqual(ids(), ["a-land", "b-port"]);

  // 同一位置重复设置不发布。
  await playback.setSlot("checkout");
  assert.deepEqual(ids(), ["a-land", "b-port"]);

  await playback.advance(); // c-any
  await playback.setSlot("idle"); // c-any 两边都适合，保持
  assert.deepEqual(ids(), ["a-land", "b-port", "c-any"]);

  await playback.advance(); // 空闲位：a-land
  await playback.setSlot("checkout"); // a-land 不适合收银位 -> 立即换 b-port
  assert.deepEqual(ids().slice(3), ["a-land", "b-port"]);
});

test("未知方向与旧缓存项缺省 orientation 都当 any，两个位置都可播", async () => {
  const { playback, ids } = slotPlayback([
    advert("a-legacy", { sortOrder: 1 }),
    advert("b-unknown", {
      sortOrder: 2,
      orientation: "square" as unknown as "any",
    }),
  ]);
  await playback.refresh("S001");
  await playback.setSlot("checkout");
  await playback.advance();
  await playback.setSlot("idle");
  await playback.advance();
  assert.deepEqual(ids(), ["a-legacy", "b-unknown", "a-legacy"]);
});

test("位置在素材加载前设置、没有素材时不发布也不报错", async () => {
  const { playback, published } = slotPlayback([]);
  await playback.setSlot("checkout");
  assert.deepEqual(published, []);
  await playback.refresh("S001");
  assert.deepEqual(published, [null]);
});

function advert(
  id: string,
  overrides: Partial<CustomerDisplayAdvertisementItem> = {},
): CustomerDisplayAdvertisementItem {
  return {
    id,
    kind: "image",
    remoteUrl: `https://cdn.example.com/${id}.png`,
    objectKey: `ads/${id}.png`,
    originalFileName: `${id}.png`,
    contentType: "image/png",
    fileSize: 1_024,
    effectiveStartIso: "2026-07-27T00:00:00.000Z",
    effectiveEndIso: "2026-07-29T00:00:00.000Z",
    sortOrder: 0,
    ...overrides,
  };
}
