import assert from "node:assert/strict";
import test from "node:test";

import {
  buildCustomerDisplaySnapshot,
  CustomerDisplayPublisher,
  type CustomerDisplayFrame,
  type CustomerDisplayProductImagePort,
} from "./customer-display-publisher";

import type {
  CartSnapshot,
  CustomerDisplaySnapshot,
  DisplayStatus,
  ExternalCustomerDisplayPort,
} from "@/core/contracts";

const advertisementCacheRootUri =
  "file:///cache/customer-display-ads/";
const productImageCacheRootUri =
  "file:///cache/customer-display-product-images/";

test("客显快照按 WPF 含税总额的 1/11 计算 GST，且只投影冻结购物车白名单", () => {
  const snapshot = buildCustomerDisplaySnapshot(
    7,
    {
      mode: "cart",
      cart: cart(),
      changeCents: 0,
      advert: {
        kind: "image",
        localUri:
          "file:///cache/customer-display-ads/ad-1.jpg",
      },
    },
    advertisementCacheRootUri,
  );

  assert.deepEqual(snapshot, {
    revision: 7,
    mode: "cart",
    items: [
      {
        name: "Tea",
        quantity: "2",
        unitPrice: { currency: "AUD", cents: 667 },
        amount: { currency: "AUD", cents: 1_234 },
        itemNumber: "I-1",
        lookupCode: "930000000001",
        // 折前 = 实收 1234 + 折扣 100；折扣率 100/1334 = 7.4962% -> 7.5。
        grossAmount: { currency: "AUD", cents: 1_334 },
        discountRate: "7.5",
      },
    ],
    summary: {
      itemQuantity: "2",
      skuCount: 1,
      subtotal: { currency: "AUD", cents: 1_334 },
    },
    visibleItemStart: 0,
    gst: { currency: "AUD", cents: 112 },
    discount: { currency: "AUD", cents: 100 },
    total: { currency: "AUD", cents: 1_234 },
    change: { currency: "AUD", cents: 0 },
    advert: {
      kind: "image",
      localUri:
        "file:///cache/customer-display-ads/ad-1.jpg",
    },
  });
  assert.doesNotMatch(
    JSON.stringify(snapshot),
    /"(?:cashier|token|customer|provider|authorization|reference)[^"]*":/iu,
  );
  assert.equal(Object.isFrozen(snapshot), true);
  assert.equal(Object.isFrozen(snapshot.items[0]?.unitPrice), true);
  assert.equal(Object.isFrozen(snapshot.summary), true);
  assert.equal(Object.isFrozen(snapshot.summary?.subtotal), true);
});

test("客显汇总使用定点十进制累加称重数量，不引入浮点尾差", () => {
  const source = cart();
  const snapshot = buildCustomerDisplaySnapshot(8, {
    mode: "cart",
    cart: {
      ...source,
      lines: [
        { ...source.lines[0]!, lineId: "line-01", quantity: "0.1" },
        { ...source.lines[0]!, lineId: "line-02", quantity: "0.2" },
      ],
    },
    changeCents: 0,
    advert: null,
  });

  assert.equal(snapshot.summary?.itemQuantity, "0.3");
  assert.equal(snapshot.summary?.skuCount, 2);
});

test("空购物车也始终发布零值汇总", () => {
  const snapshot = buildCustomerDisplaySnapshot(9, {
    mode: "idle",
    cart: null,
    changeCents: 0,
    advert: null,
  });

  assert.deepEqual(snapshot.summary, {
    itemQuantity: "0",
    skuCount: 0,
    subtotal: { currency: "AUD", cents: 0 },
  });
  assert.equal(
    (snapshot as CustomerDisplaySnapshot & { visibleItemStart?: number })
      .visibleItemStart,
    0,
  );
});

test("客显只接受本地广告 URI、非负 revision 和安全整数找零", () => {
  for (const localUri of [
    "https://example.com/remote.mp4",
    "file://evil.example/cache/customer-display-ads/ad.mp4",
    "file:///private/customer-data/ad.mp4",
    "file:///cache/customer-display-ads/%2e%2e/secret.mp4",
  ]) {
    assert.throws(
      () =>
        buildCustomerDisplaySnapshot(
          1,
          {
            mode: "idle",
            cart: null,
            changeCents: 0,
            advert: { kind: "video", localUri },
          },
          advertisementCacheRootUri,
        ),
      /local advertisement URI/i,
    );
  }
  assert.throws(
    () =>
      buildCustomerDisplaySnapshot(
        -1,
        {
          mode: "change",
          cart: cart(),
          changeCents: 10,
          advert: null,
        },
        advertisementCacheRootUri,
      ),
    /revision/i,
  );
});

test("发布器去重相同画面，失败不影响主收银，后续 revision 仍严格递增", async () => {
  const display = new FakeDisplay();
  const publisher = new CustomerDisplayPublisher(display, {
    advertisementCacheRootUri,
  });
  const frame = {
    mode: "cart" as const,
    cart: cart(),
    changeCents: 0,
    advert: null,
  };

  const first = await publisher.publish(frame);
  assert.equal(first.status, "published");
  const firstRevision = first.revision;
  assert.deepEqual(await publisher.publish(frame), {
    status: "unchanged",
    revision: firstRevision,
  });

  display.failNextPublish = true;
  assert.deepEqual(
    await publisher.publish({ ...frame, mode: "payment" }),
    {
      status: "failed",
      revision: firstRevision + 1,
      errorCode: "DISPLAY_PUBLISH_FAILED",
    },
  );
  assert.deepEqual(
    await publisher.publish({ ...frame, mode: "success" }),
    {
      status: "published",
      revision: firstRevision + 2,
    },
  );
  assert.deepEqual(
    display.snapshots.map((snapshot) => snapshot.revision),
    [firstRevision, firstRevision + 2],
  );
});

test("窗口变化发布失败后，下一帧仍重试尚未显示的窗口位置", async () => {
  const display = new FakeDisplay();
  const publisher = new CustomerDisplayPublisher(display);
  const initial = cartWithItems(20);
  const editedFirst = changeLines(initial, [0]);

  await publisher.publish(frame(initial));
  assert.equal(latestVisibleItemStart(display), 14);

  display.failNextPublish = true;
  assert.equal((await publisher.publish(frame(editedFirst))).status, "failed");
  assert.equal(latestVisibleItemStart(display), 14);

  await publisher.publish(frame(editedFirst, "payment"));
  assert.equal(latestVisibleItemStart(display), 0);
});

test("发布器初始显示末尾 6 行，新增和商品编辑只在目标离开窗口时移动", async () => {
  const display = new FakeDisplay();
  const publisher = new CustomerDisplayPublisher(display);
  const initial = cartWithItems(14);

  await publisher.publish(frame(initial));
  assert.equal(latestVisibleItemStart(display), 8);

  const appended = cartWithItems(15);
  await publisher.publish(frame(appended));
  assert.equal(latestVisibleItemStart(display), 9);

  const editedOffscreen = changeLines(appended, [0]);
  await publisher.publish(frame(editedOffscreen));
  assert.equal(latestVisibleItemStart(display), 0);

  const editedInsideWindow = changeLines(editedOffscreen, [5]);
  await publisher.publish(frame(editedInsideWindow));
  assert.equal(latestVisibleItemStart(display), 0);
  assert.doesNotMatch(JSON.stringify(display.snapshots.at(-1)), /line-\d+/u);
});

test("发布器多行同时变化选择顺序中最后一行，删除后显示相邻商品", async () => {
  const display = new FakeDisplay();
  const publisher = new CustomerDisplayPublisher(display);
  const initial = cartWithItems(20);

  await publisher.publish(frame(initial));
  assert.equal(latestVisibleItemStart(display), 14);

  const multipleChanges = changeLines(initial, [2, 15]);
  await publisher.publish(frame(multipleChanges));
  assert.equal(latestVisibleItemStart(display), 14);

  const removedAbove = removeLineAt(multipleChanges, 5);
  await publisher.publish(frame(removedAbove));
  assert.equal(latestVisibleItemStart(display), 5);

  const removedTail = removeLineAt(
    removedAbove,
    removedAbove.lines.length - 1,
  );
  await publisher.publish(frame(removedTail));
  assert.equal(latestVisibleItemStart(display), 12);
});

test("发布器混合新增、删除与编辑时选择当前顺序中最后一个变化项", async () => {
  const display = new FakeDisplay();
  const publisher = new CustomerDisplayPublisher(display);
  const initial = cartWithItems(20);

  await publisher.publish(frame(initial));
  assert.equal(latestVisibleItemStart(display), 14);

  const inserted = {
    ...initial.lines[0]!,
    lineId: "line-new",
    productCode: "P-new",
    itemNumber: "I-new",
    lookupCode: "930000000999",
    displayName: "New item",
  };
  const addedAndEditedLines = [
    ...initial.lines.slice(0, 3),
    inserted,
    ...initial.lines.slice(3),
  ].map((line) =>
    line.lineId === "line-16"
      ? {
          ...line,
          unitPrice: {
            currency: "AUD" as const,
            cents: line.unitPrice.cents + 1,
          },
        }
      : line,
  );
  const addedAndEdited: CartSnapshot = {
    ...initial,
    revision: initial.revision + 1,
    lines: addedAndEditedLines,
  };

  await publisher.publish(frame(addedAndEdited));
  assert.equal(latestVisibleItemStart(display), 14);

  const deletedAndEdited: CartSnapshot = {
    ...addedAndEdited,
    revision: addedAndEdited.revision + 1,
    lines: addedAndEdited.lines
      .filter((line) => line.lineId !== "line-5")
      .map((line) =>
        line.lineId === "line-17"
          ? {
              ...line,
              discount: {
                currency: "AUD" as const,
                cents: line.discount.cents + 50,
              },
              actualAmount: {
                currency: "AUD" as const,
                cents: line.actualAmount.cents - 50,
              },
            }
          : line,
      ),
  };

  await publisher.publish(frame(deletedAndEdited));
  assert.equal(latestVisibleItemStart(display), 14);
});

test("发布器使用完整购物车判断 100 行边界变化且不突破快照上限", async () => {
  const display = new FakeDisplay();
  const publisher = new CustomerDisplayPublisher(display);
  const initial = cartWithItems(101);

  await publisher.publish(frame(initial));
  assert.equal(display.snapshots.at(-1)?.items.length, 100);
  assert.equal(latestVisibleItemStart(display), 94);

  const removedFirst = removeLineAt(initial, 0);
  await publisher.publish(frame(removedFirst));
  assert.equal(display.snapshots.at(-1)?.items[0]?.name, "Item 2");
  assert.equal(latestVisibleItemStart(display), 0);

  const boundaryDisplay = new FakeDisplay();
  const boundaryPublisher = new CustomerDisplayPublisher(boundaryDisplay);
  const firstHundred = cartWithItems(100);
  await boundaryPublisher.publish(frame(firstHundred));
  await boundaryPublisher.publish(frame(cartWithItems(101)));
  assert.equal(boundaryDisplay.snapshots.at(-1)?.items.length, 100);
  assert.equal(latestVisibleItemStart(boundaryDisplay), 94);
});

test("支付和广告变化保留窗口，清车后下一单重新从末尾开始", async () => {
  const display = new FakeDisplay();
  const publisher = new CustomerDisplayPublisher(display, {
    advertisementCacheRootUri,
  });
  const initial = cartWithItems(20);
  const editedFirst = changeLines(initial, [0]);

  await publisher.publish(frame(initial));
  await publisher.publish(frame(editedFirst));
  assert.equal(latestVisibleItemStart(display), 0);

  await publisher.publish(frame(editedFirst, "payment"));
  assert.equal(latestVisibleItemStart(display), 0);
  await publisher.publish({
    ...frame(editedFirst, "payment"),
    advert: {
      kind: "image",
      localUri: "file:///cache/customer-display-ads/ad-2.jpg",
    },
  });
  assert.equal(latestVisibleItemStart(display), 0);

  const totalOnlyChange: CartSnapshot = {
    ...editedFirst,
    revision: editedFirst.revision + 1,
    subtotal: {
      currency: "AUD",
      cents: editedFirst.subtotal.cents + 100,
    },
    actualAmount: {
      currency: "AUD",
      cents: editedFirst.actualAmount.cents + 100,
    },
  };
  await publisher.publish(frame(totalOnlyChange));
  assert.equal(latestVisibleItemStart(display), 0);

  await publisher.publish(frame(null, "idle"));
  assert.equal(latestVisibleItemStart(display), 0);
  await publisher.publish(frame(cartWithItems(20)));
  assert.equal(latestVisibleItemStart(display), 14);
});

test("同一 JS producer session 重建 Publisher 后继续使用更大的 revision", async () => {
  const display = new FakeDisplay();
  const firstPublisher = new CustomerDisplayPublisher(display, {
    advertisementCacheRootUri,
  });
  const first = await firstPublisher.publish({
    mode: "idle",
    cart: null,
    changeCents: 0,
    advert: null,
  });
  const secondPublisher = new CustomerDisplayPublisher(display, {
    advertisementCacheRootUri,
  });
  const second = await secondPublisher.publish({
    mode: "cart",
    cart: cart(),
    changeCents: 0,
    advert: null,
  });

  assert.equal(first.status, "published");
  assert.equal(second.status, "published");
  assert.ok(second.revision > first.revision);
  assert.deepEqual(
    display.snapshots.map((snapshot) => snapshot.revision),
    [first.revision, second.revision],
  );
});

test("启停和状态读取失败均返回受控结果，不把外屏故障抛给交易流程", async () => {
  const display = new FakeDisplay();
  const publisher = new CustomerDisplayPublisher(display, {
    advertisementCacheRootUri,
  });
  display.failSetEnabled = true;
  display.failGetStatus = true;

  assert.deepEqual(await publisher.setEnabled(true), {
    status: "failed",
    errorCode: "DISPLAY_ENABLE_FAILED",
  });
  assert.equal(await publisher.getStatus(), "failed");
});

function lineWith(
  overrides: Partial<CartSnapshot["lines"][number]>,
): CartSnapshot["lines"][number] {
  return { ...cart().lines[0]!, ...overrides };
}

function itemsOf(lines: CartSnapshot["lines"][number][]) {
  return buildCustomerDisplaySnapshot(1, {
    mode: "cart",
    cart: { ...cart(), lines },
    changeCents: 0,
    advert: null,
  }).items;
}

test("货号与条码为空串或 null 时省略，超长时截断到契约上限", () => {
  const [empty, nulled, long] = itemsOf([
    lineWith({ itemNumber: "", lookupCode: "   " }),
    lineWith({ itemNumber: null }),
    lineWith({ itemNumber: "N".repeat(80), lookupCode: "B".repeat(70) }),
  ]);
  assert.equal("itemNumber" in empty!, false);
  assert.equal("lookupCode" in empty!, false);
  assert.equal("itemNumber" in nulled!, false);
  assert.equal(nulled?.lookupCode, "930000000001");
  assert.equal(long?.itemNumber?.length, 64);
  assert.equal(long?.lookupCode?.length, 64);
});

test("折扣行提供折前金额与折扣率；无折扣、退货行和折扣率归零时都不提供", () => {
  const zero = { currency: "AUD" as const, cents: 0 };
  const [none, ten, half, full, tiny, tinyBig, sale, negativeGross] = itemsOf([
    lineWith({ discount: zero, actualAmount: { currency: "AUD", cents: 1_000 } }),
    // 折前 1000，折扣 100 -> 10%
    lineWith({
      discount: { currency: "AUD", cents: 100 },
      actualAmount: { currency: "AUD", cents: 900 },
    }),
    // 折前 800，折扣 100 -> 12.5%
    lineWith({
      discount: { currency: "AUD", cents: 100 },
      actualAmount: { currency: "AUD", cents: 700 },
    }),
    // 全额折扣：实收 0，折前 500 -> 100%
    lineWith({
      discount: { currency: "AUD", cents: 500 },
      actualAmount: zero,
    }),
    // 0.01%：1 / 10000
    lineWith({
      discount: { currency: "AUD", cents: 1 },
      actualAmount: { currency: "AUD", cents: 9_999 },
    }),
    // 0.001% 四舍五入为 0 -> 省略
    lineWith({
      discount: { currency: "AUD", cents: 1 },
      actualAmount: { currency: "AUD", cents: 99_999 },
    }),
    // 1/3 -> 33.33（保留两位，半数进位）
    lineWith({
      discount: { currency: "AUD", cents: 100 },
      actualAmount: { currency: "AUD", cents: 200 },
    }),
    // 退货行（实收为负）若带折扣，折前金额符号与实收一致
    lineWith({
      discount: { currency: "AUD", cents: 100 },
      actualAmount: { currency: "AUD", cents: -900 },
    }),
  ]);

  assert.equal(none?.grossAmount, undefined);
  assert.equal(none?.discountRate, undefined);
  assert.deepEqual(ten?.grossAmount, { currency: "AUD", cents: 1_000 });
  assert.equal(ten?.discountRate, "10");
  assert.deepEqual(half?.grossAmount, { currency: "AUD", cents: 800 });
  assert.equal(half?.discountRate, "12.5");
  assert.deepEqual(full?.grossAmount, { currency: "AUD", cents: 500 });
  assert.equal(full?.discountRate, "100");
  assert.equal(tiny?.discountRate, "0.01");
  assert.equal(tinyBig?.grossAmount, undefined);
  assert.equal(tinyBig?.discountRate, undefined);
  assert.equal(sale?.discountRate, "33.33");
  assert.deepEqual(negativeGross?.grossAmount, { currency: "AUD", cents: -1_000 });
  assert.equal(negativeGross?.discountRate, "10");

  // 真实退货行：折扣恒为 0，实收为负，不应有折扣字段。
  const [returned] = itemsOf([
    lineWith({
      kind: "return",
      discount: zero,
      actualAmount: { currency: "AUD", cents: -1_334 },
    }),
  ]);
  assert.equal("grossAmount" in returned!, false);
  assert.equal("discountRate" in returned!, false);
});

class FakeProductImages implements CustomerDisplayProductImagePort {
  public readonly peeked: string[] = [];
  public readonly images = new Map<string, string>();
  public throwOnPeek = false;
  public peek(line: { productCode: string; lookupCode: string }): string | null {
    this.peeked.push(line.productCode);
    if (this.throwOnPeek) throw new Error("resolver exploded");
    return this.images.get(line.productCode) ?? null;
  }
}

test("商品缩略图只为可见窗口取图，本地地址进入快照且新增图片会改变指纹", async () => {
  const display = new FakeDisplay();
  const images = new FakeProductImages();
  const publisher = new CustomerDisplayPublisher(display, {
    productImageCacheRootUri,
    productImageResolver: images,
  });
  const source = cartWithItems(10);

  const first = await publisher.publish(frame(source));
  assert.equal(first.status, "published");
  // 10 行、窗口 6：起点 4，只对 P-5..P-10 取图。
  assert.deepEqual(images.peeked.slice(0, 6), [
    "P-5",
    "P-6",
    "P-7",
    "P-8",
    "P-9",
    "P-10",
  ]);
  assert.equal(
    display.snapshots.at(-1)?.items.every((item) => item.imageUri === undefined),
    true,
  );

  // 图片就绪后同一帧重发：指纹变化，不能被去重吞掉。
  images.images.set("P-6", `${productImageCacheRootUri}abc.jpg`);
  const second = await publisher.publish(frame(source));
  assert.equal(second.status, "published");
  const published = display.snapshots.at(-1)!;
  assert.equal(published.items[5]?.imageUri, `${productImageCacheRootUri}abc.jpg`);
  assert.equal(published.items[4]?.imageUri, undefined);
  // 窗口外（P-1）即便有图也不带。
  assert.equal(published.items[0]?.imageUri, undefined);

  // 再发一次没有变化 -> unchanged。
  assert.equal((await publisher.publish(frame(source))).status, "unchanged");
});

test("商品缩略图解析器异常、越界地址或缺少根目录时按无图处理，不影响发布", async () => {
  const source = cartWithItems(2);

  const display = new FakeDisplay();
  const images = new FakeProductImages();
  const publisher = new CustomerDisplayPublisher(display, {
    productImageCacheRootUri,
    productImageResolver: images,
  });
  images.throwOnPeek = true;
  assert.equal((await publisher.publish(frame(source))).status, "published");
  assert.equal(display.snapshots.at(-1)?.items[0]?.imageUri, undefined);

  images.throwOnPeek = false;
  images.images.set("P-1", "file:///private/other/place.jpg");
  images.images.set("P-2", "https://example.com/p.jpg");
  assert.equal(
    (await publisher.publish(frame(source, "payment"))).status,
    "published",
  );
  assert.equal(
    display.snapshots.at(-1)?.items.every((item) => item.imageUri === undefined),
    true,
  );

  const noRootDisplay = new FakeDisplay();
  const noRootImages = new FakeProductImages();
  noRootImages.images.set("P-1", `${productImageCacheRootUri}abc.jpg`);
  const noRootPublisher = new CustomerDisplayPublisher(noRootDisplay, {
    productImageResolver: noRootImages,
  });
  await noRootPublisher.publish(frame(source));
  assert.equal(noRootDisplay.snapshots.at(-1)?.items[0]?.imageUri, undefined);
  assert.deepEqual(noRootImages.peeked, []);
});


class FakeDisplay implements ExternalCustomerDisplayPort {
  public failNextPublish = false;
  public failSetEnabled = false;
  public failGetStatus = false;
  public readonly snapshots: CustomerDisplaySnapshot[] = [];

  public async getStatus(): Promise<DisplayStatus> {
    if (this.failGetStatus) throw new Error("display disconnected");
    return "ready";
  }

  public async setEnabled(): Promise<void> {
    if (this.failSetEnabled) throw new Error("display enable failed");
  }

  public async publish(snapshot: CustomerDisplaySnapshot): Promise<void> {
    if (this.failNextPublish) {
      this.failNextPublish = false;
      throw new Error("display cable removed");
    }
    this.snapshots.push(snapshot);
  }

  public subscribe(): () => void {
    return () => undefined;
  }
}

function cart(): CartSnapshot {
  return {
    revision: 4,
    mode: "sale",
    lines: [
      {
        lineId: "line-1",
        productCode: "P-1",
        itemNumber: "I-1",
        lookupCode: "930000000001",
        displayName: "Tea",
        quantity: "2",
        unitPrice: { currency: "AUD", cents: 667 },
        discount: { currency: "AUD", cents: 100 },
        actualAmount: { currency: "AUD", cents: 1_234 },
        priceSource: "catalog",
        syncProvenance: {
          referenceCode: "must-not-reach-display",
          priceSource: 3,
        },
        kind: "sale",
        returnSourceKey: null,
        originalOrderGuid: null,
        originalOrderDetailGuid: null,
      },
    ],
    subtotal: { currency: "AUD", cents: 1_334 },
    discount: { currency: "AUD", cents: 100 },
    actualAmount: { currency: "AUD", cents: 1_234 },
  };
}

function frame(
  cartSnapshot: CartSnapshot | null,
  mode: CustomerDisplayFrame["mode"] = "cart",
): CustomerDisplayFrame {
  return {
    mode,
    cart: cartSnapshot,
    changeCents: 0,
    advert: null,
  };
}

function cartWithItems(count: number): CartSnapshot {
  const source = cart();
  const base = source.lines[0]!;
  const lines = Array.from({ length: count }, (_, index) => ({
    ...base,
    lineId: `line-${index + 1}`,
    productCode: `P-${index + 1}`,
    itemNumber: `I-${index + 1}`,
    lookupCode: String(930_000_000_001 + index),
    displayName: `Item ${index + 1}`,
    quantity: "1",
    discount: { currency: "AUD" as const, cents: 0 },
    actualAmount: { currency: "AUD" as const, cents: 667 },
  }));
  return {
    ...source,
    revision: count,
    lines,
    subtotal: { currency: "AUD", cents: count * 667 },
    discount: { currency: "AUD", cents: 0 },
    actualAmount: { currency: "AUD", cents: count * 667 },
  };
}

function changeLines(
  source: CartSnapshot,
  indexes: readonly number[],
): CartSnapshot {
  const changed = new Set(indexes);
  return {
    ...source,
    revision: source.revision + 1,
    lines: source.lines.map((line, index) =>
      changed.has(index)
        ? {
            ...line,
            quantity: "2",
            actualAmount: {
              currency: "AUD" as const,
              cents: line.actualAmount.cents + line.unitPrice.cents,
            },
          }
        : line,
    ),
  };
}

function removeLineAt(source: CartSnapshot, index: number): CartSnapshot {
  return {
    ...source,
    revision: source.revision + 1,
    lines: source.lines.filter((_, lineIndex) => lineIndex !== index),
  };
}

function latestVisibleItemStart(display: FakeDisplay): number | undefined {
  return (
    display.snapshots.at(-1) as
      | (CustomerDisplaySnapshot & { visibleItemStart?: number })
      | undefined
  )?.visibleItemStart;
}
