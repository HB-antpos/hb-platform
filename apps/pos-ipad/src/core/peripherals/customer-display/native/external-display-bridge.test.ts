import assert from "node:assert/strict";
import test from "node:test";

import type {
  CustomerDisplaySnapshot,
  DisplayStatus,
  ExternalCustomerDisplayPort,
} from "../../../contracts/external-display";

import {
  createExternalDisplayBridge,
  sanitizeCustomerDisplaySnapshot,
  type ExternalDisplayNativeModule,
  type NativeExternalDisplayStatusEvent,
  type NativeExternalDisplayStatus,
} from "./external-display-bridge";

const disconnectedStatus: NativeExternalDisplayStatus = {
  state: "disconnected",
  enabled: false,
  connected: false,
  revision: 0,
  widthPixels: 0,
  heightPixels: 0,
  scale: 0,
  reason: "native-module-unavailable",
};

const money = (cents: number) => ({ currency: "AUD" as const, cents });
const advertisementCacheRootUri =
  "file:///var/mobile/Containers/Data/customer-display-ads/";
const productImageCacheRootUri =
  "file:///var/mobile/Containers/Data/customer-display-product-images/";

const snapshot: CustomerDisplaySnapshot = {
  revision: 1,
  mode: "cart",
  items: [
    {
      name: "Sparkling Water",
      quantity: "2",
      amount: money(598),
    },
  ],
  gst: money(54),
  discount: money(100),
  total: money(498),
  change: money(0),
  advert: {
    kind: "image",
    localUri:
      "file:///var/mobile/Containers/Data/customer-display-ads/ad.png",
  },
};

test("adapter implements the frozen external display port", () => {
  const port: ExternalCustomerDisplayPort = createExternalDisplayBridge({
    advertisementCacheRootUri,
    nativeModule: null,
  });

  assert.equal(typeof port.publish, "function");
});

test("simulator or missing native module stays explicitly disconnected", async () => {
  const bridge = createExternalDisplayBridge({
    advertisementCacheRootUri,
    nativeModule: null,
  });

  assert.equal(await bridge.getStatus(), "disconnected");
  assert.equal(await bridge.setEnabled(true), undefined);
  assert.equal(await bridge.forceBlank(), undefined);
  assert.equal(await bridge.disableForSafety(), undefined);
  await assert.rejects(
    () => bridge.publish(snapshot),
    /native module is unavailable/i,
  );
});

test("安全清屏绕过 snapshot 发布并拒绝过期 producer 的伪成功结果", async () => {
  let forceBlankCalls = 0;
  let responseReason = "sensitive-content-reset";
  const nativeModule: ExternalDisplayNativeModule = {
    async getStatus() {
      return disconnectedStatus;
    },
    async setEnabled() {
      return disconnectedStatus;
    },
    async forceBlank() {
      forceBlankCalls += 1;
      return {
        ...disconnectedStatus,
        reason: responseReason,
      };
    },
    async publishSnapshot(value) {
      return {
        accepted: true,
        revision: value.revision,
        latestRevision: value.revision,
        reason: "accepted",
      };
    },
    async markReactSurfaceReady() {},
    async markReactSurfaceRendered() {},
    addListener() {
      return { remove() {} };
    },
  };
  const bridge = createExternalDisplayBridge({
    advertisementCacheRootUri,
    nativeModule,
  });

  await bridge.forceBlank();
  assert.equal(forceBlankCalls, 1);

  responseReason = "producer-session-expired";
  await assert.rejects(
    () => bridge.forceBlank(),
    /safe blank was rejected.*producer-session-expired/i,
  );
});

test("旧原生安全隐藏会传播 setEnabled 异常并拒绝过期 producer 的伪成功状态", async () => {
  let behavior: "throw" | "expired" | "disabled" = "throw";
  const setEnabledArguments: boolean[] = [];
  const nativeModule: ExternalDisplayNativeModule = {
    async getStatus() {
      return disconnectedStatus;
    },
    async setEnabled(enabled) {
      setEnabledArguments.push(enabled);
      if (behavior === "throw") {
        throw new Error("legacy native disable failed");
      }
      if (behavior === "disabled") {
        return {
          ...disconnectedStatus,
          reason: "external-display-disabled",
        };
      }
      return {
        ...disconnectedStatus,
        enabled: true,
        reason: "producer-session-expired",
      };
    },
    async publishSnapshot(value) {
      return {
        accepted: true,
        revision: value.revision,
        latestRevision: value.revision,
        reason: "accepted",
      };
    },
    async markReactSurfaceReady() {},
    async markReactSurfaceRendered() {},
    addListener() {
      return { remove() {} };
    },
  };
  const bridge = createExternalDisplayBridge({
    advertisementCacheRootUri,
    nativeModule,
  });

  await assert.rejects(
    async () => bridge.disableForSafety(),
    /legacy native disable failed/,
  );

  behavior = "expired";
  await assert.rejects(
    async () => bridge.disableForSafety(),
    (error: unknown) => {
      assert.equal(
        (error as { code?: string }).code,
        "EXTERNAL_DISPLAY_SAFE_DISABLE_REJECTED",
      );
      assert.match(
        (error as Error).message,
        /safe disable was rejected.*producer-session-expired/i,
      );
      return true;
    },
  );

  behavior = "disabled";
  await bridge.disableForSafety();
  assert.deepEqual(setEnabledArguments, [false, false, false]);
});

test("native revision gate can reject an older concurrent delivery", async () => {
  let latestRevision = 0;
  const deliveredRevisions: number[] = [];
  const nativeModule: ExternalDisplayNativeModule = {
    async getStatus() {
      return disconnectedStatus;
    },
    async setEnabled() {
      return disconnectedStatus;
    },
    async publishSnapshot(value) {
      if (value.revision === 1) {
        await new Promise((resolve) => setTimeout(resolve, 20));
      }

      deliveredRevisions.push(value.revision);
      const accepted = value.revision > latestRevision;
      if (accepted) {
        latestRevision = value.revision;
      }

      return {
        accepted,
        revision: value.revision,
        latestRevision,
        reason: accepted ? "accepted" : "stale-revision",
      };
    },
    async markReactSurfaceReady() {},
    async markReactSurfaceRendered() {},
    addListener() {
      return { remove() {} };
    },
  };
  const bridge = createExternalDisplayBridge({
    advertisementCacheRootUri,
    nativeModule,
  });

  const results = await Promise.allSettled([
    bridge.publish(snapshot),
    bridge.publish({ ...snapshot, revision: 2, total: money(698) }),
  ]);

  assert.deepEqual(deliveredRevisions, [2, 1]);
  assert.equal(latestRevision, 2);
  assert.equal(results[0]?.status, "rejected");
  assert.equal(results[1]?.status, "fulfilled");
});

test("status subscription exposes only the frozen status values", () => {
  let nativeListener:
    | ((event: NativeExternalDisplayStatusEvent) => void)
    | undefined;
  let removed = false;
  const nativeModule: ExternalDisplayNativeModule = {
    async getStatus() {
      return disconnectedStatus;
    },
    async setEnabled() {
      return disconnectedStatus;
    },
    async publishSnapshot(value) {
      return {
        accepted: true,
        revision: value.revision,
        latestRevision: value.revision,
        reason: "accepted",
      };
    },
    async markReactSurfaceReady() {},
    async markReactSurfaceRendered() {},
    addListener(eventName, listener) {
      if (eventName === "onStatusChanged") {
        nativeListener = listener as (
          event: NativeExternalDisplayStatusEvent,
        ) => void;
      }
      return {
        remove() {
          removed = true;
        },
      };
    },
  };
  const statuses: DisplayStatus[] = [];
  const bridge = createExternalDisplayBridge({
    advertisementCacheRootUri,
    nativeModule,
  });
  const unsubscribe = bridge.subscribe((status) => statuses.push(status));

  nativeListener?.({
    ...disconnectedStatus,
    event: "ready",
    state: "ready",
    connected: true,
  });
  unsubscribe();

  assert.deepEqual(statuses, ["ready"]);
  assert.equal(removed, true);
});

test("strict snapshot schema rejects credentials, customer data and card references", () => {
  assert.throws(() =>
    sanitizeCustomerDisplaySnapshot(
      {
        ...snapshot,
        deviceAuthorization: "secret",
      },
      advertisementCacheRootUri,
    ),
  );
  assert.throws(() =>
    sanitizeCustomerDisplaySnapshot(
      {
        ...snapshot,
        items: [
          {
            ...snapshot.items[0]!,
            cardReference: "card-ref",
          },
        ],
      },
      advertisementCacheRootUri,
    ),
  );
  assert.throws(() =>
    sanitizeCustomerDisplaySnapshot(
      {
        ...snapshot,
        customerName: "Private Customer",
      },
      advertisementCacheRootUri,
    ),
  );
  assert.throws(() =>
    sanitizeCustomerDisplaySnapshot(
      {
        ...snapshot,
        summary: {
          itemQuantity: "1",
          skuCount: 1,
          subtotal: money(299),
          cardReference: "card-ref",
        },
      },
      advertisementCacheRootUri,
    ),
  );
});

test("冻结快照兼容新版单价、汇总与窗口字段，也继续接受旧版字段集", () => {
  const legacy = sanitizeCustomerDisplaySnapshot(
    snapshot,
    advertisementCacheRootUri,
  );
  assert.equal(legacy.summary, undefined);
  assert.equal(legacy.items[0]?.unitPrice, undefined);
  assert.equal(
    (legacy as CustomerDisplaySnapshot & { visibleItemStart?: number })
      .visibleItemStart,
    undefined,
  );

  const current = sanitizeCustomerDisplaySnapshot(
    {
      ...snapshot,
      items: [
        {
          ...snapshot.items[0]!,
          unitPrice: money(299),
        },
      ],
      summary: {
        itemQuantity: "2",
        skuCount: 1,
        subtotal: money(598),
      },
      visibleItemStart: 0,
    },
    advertisementCacheRootUri,
  );

  assert.deepEqual(current.items[0]?.unitPrice, money(299));
  assert.deepEqual(current.summary, {
    itemQuantity: "2",
    skuCount: 1,
    subtotal: money(598),
  });
  assert.equal(
    (current as CustomerDisplaySnapshot & { visibleItemStart?: number })
      .visibleItemStart,
    0,
  );
});

test("可视窗口必须是能完整容纳最多 6 行的有效起点", () => {
  const items = Array.from({ length: 13 }, (_, index) => ({
    ...snapshot.items[0]!,
    name: `Item ${index + 1}`,
  }));

  assert.doesNotThrow(() =>
    sanitizeCustomerDisplaySnapshot(
      { ...snapshot, items, visibleItemStart: 7 },
      advertisementCacheRootUri,
    ),
  );
  for (const visibleItemStart of [-1, 0.5, 8]) {
    assert.throws(() =>
      sanitizeCustomerDisplaySnapshot(
        { ...snapshot, items, visibleItemStart },
        advertisementCacheRootUri,
      ),
    );
  }
});

test("advertisements must be local files", () => {
  assert.throws(
    () =>
      sanitizeCustomerDisplaySnapshot(
        {
          ...snapshot,
          advert: {
            kind: "image",
            localUri: "https://example.com/ad.png",
          },
        },
        advertisementCacheRootUri,
      ),
    /local advertisement URI/,
  );
});

const productImageItem = (imageUri: string) => ({
  ...snapshot.items[0]!,
  itemNumber: "A100",
  lookupCode: "9300000000017",
  grossAmount: money(1_000),
  discountRate: "10",
  imageUri,
});

test("商品缩略图只接受商品图缓存目录内的本地 file URI", () => {
  const accepted = sanitizeCustomerDisplaySnapshot(
    {
      ...snapshot,
      items: [
        productImageItem(`${productImageCacheRootUri}abc123.jpg`),
      ],
    },
    advertisementCacheRootUri,
    productImageCacheRootUri,
  );
  assert.equal(
    accepted.items[0]?.imageUri,
    `${productImageCacheRootUri}abc123.jpg`,
  );

  for (const imageUri of [
    "https://example.com/p.jpg",
    "file:///var/mobile/Containers/Data/customer-display-ads/ad.png",
    "file:///var/mobile/Containers/Data/customer-display-product-images/",
    "file:///var/mobile/Containers/Data/customer-display-product-images/%2e%2e/secret.jpg",
    "file:///var/mobile/Containers/Data/customer-display-product-images/a.jpg?x=1",
    "file:///var/mobile/Containers/Data/customer-display-product-images/a.jpg#frag",
    "file://user:pw@localhost/var/mobile/Containers/Data/customer-display-product-images/a.jpg",
    "file://evil.example/var/mobile/Containers/Data/customer-display-product-images/a.jpg",
  ]) {
    assert.throws(
      () =>
        sanitizeCustomerDisplaySnapshot(
          { ...snapshot, items: [productImageItem(imageUri)] },
          advertisementCacheRootUri,
          productImageCacheRootUri,
        ),
      /local product image URI/,
      imageUri,
    );
  }
});

test("缺少商品图缓存根目录时拒绝任何 imageUri，无图快照不受影响", async () => {
  assert.throws(
    () =>
      sanitizeCustomerDisplaySnapshot(
        {
          ...snapshot,
          items: [productImageItem(`${productImageCacheRootUri}abc.jpg`)],
        },
        advertisementCacheRootUri,
      ),
    /product image cache root/,
  );
  assert.doesNotThrow(() =>
    sanitizeCustomerDisplaySnapshot(snapshot, advertisementCacheRootUri),
  );

  // 经由桥接发布同样会在到达原生层之前被拒绝。
  let nativePublishCalls = 0;
  const bridge = createExternalDisplayBridge({
    advertisementCacheRootUri,
    nativeModule: {
      async getStatus() {
        return disconnectedStatus;
      },
      async setEnabled() {
        return disconnectedStatus;
      },
      async publishSnapshot(value) {
        nativePublishCalls += 1;
        return {
          accepted: true,
          revision: value.revision,
          latestRevision: value.revision,
          reason: "accepted",
        };
      },
      async markReactSurfaceReady() {},
      async markReactSurfaceRendered() {},
      addListener() {
        return { remove() {} };
      },
    },
  });
  await assert.rejects(() =>
    bridge.publish({
      ...snapshot,
      items: [productImageItem(`${productImageCacheRootUri}abc.jpg`)],
    }),
  );
  assert.equal(nativePublishCalls, 0);
});

test("grossAmount 与 discountRate 必须同时出现", () => {
  const { grossAmount: _gross, ...withoutGross } = productImageItem(
    `${productImageCacheRootUri}abc.jpg`,
  );
  void _gross;
  assert.throws(() =>
    sanitizeCustomerDisplaySnapshot(
      { ...snapshot, items: [withoutGross] },
      advertisementCacheRootUri,
      productImageCacheRootUri,
    ),
  );
});
