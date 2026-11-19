import assert from "node:assert/strict";
import {
  buildPriceNotificationPreviewMessage,
  buildPriceNotificationSaveMessage,
  buildSyncToOtherStoresMessage,
  parsePriceNotificationHeader,
  pickLastPriceNotification,
} from "./price-notification";
import { echoTranslate as t } from "./test-helpers";

const raw = '{"productCount":1,"needsPriceUpdateStores":6,"labelOnlyStores":0,"cancelledStores":0,"skippedSpecialStores":1,"hasAny":true}';
const expected = {
  productCount: 1,
  needsPriceUpdateStores: 6,
  labelOnlyStores: 0,
  cancelledStores: 0,
  skippedSpecialStores: 1,
  hasAny: true,
};

assert.deepEqual(parsePriceNotificationHeader({ "x-price-notification": raw }), expected);
assert.deepEqual(
  parsePriceNotificationHeader({ "X-Price-Notification": raw }),
  expected,
  "普通对象的头名大小写不敏感"
);
assert.deepEqual(
  parsePriceNotificationHeader({ get: (name: string) => (name === "x-price-notification" ? raw : undefined) }),
  expected,
  "兼容 AxiosHeaders.get"
);
assert.equal(parsePriceNotificationHeader({}), null, "头不存在 = 不涉及价格通知");
assert.equal(parsePriceNotificationHeader(undefined), null);
assert.equal(parsePriceNotificationHeader({ "x-price-notification": "{not json" }), null, "解析失败静默忽略");
assert.equal(parsePriceNotificationHeader({ "x-price-notification": "[1]" }), null);
assert.equal(parsePriceNotificationHeader({ "x-price-notification": "" }), null);

const first = { ...expected, needsPriceUpdateStores: 2 };
const last = { ...expected, needsPriceUpdateStores: 5 };
assert.equal(pickLastPriceNotification([first, last]), last, "连发两个请求以最后一个响应头为准，不相加");
assert.equal(pickLastPriceNotification([first, null]), first, "后一个请求没带头时沿用前一个");
assert.equal(pickLastPriceNotification([null, undefined]), null);

assert.equal(buildPriceNotificationSaveMessage(null, t), null, "无通知头时由调用方沿用原提示");
assert.equal(
  buildPriceNotificationSaveMessage({ ...expected, labelOnlyStores: 2 }, t),
  'notification.savedAndSent|{"detail":"notification.needsPriceUpdate|{\\"count\\":6} · notification.labelOnly|{\\"count\\":2} · notification.skippedSpecial|{\\"count\\":1}"}'
);
assert.equal(
  buildPriceNotificationSaveMessage({ ...expected, needsPriceUpdateStores: 0, labelOnlyStores: 3, skippedSpecialStores: 0 }, t),
  'notification.savedAndSent|{"detail":"notification.labelOnly|{\\"count\\":3}"}',
  "数量为 0 的分段不展示"
);
assert.equal(
  buildPriceNotificationSaveMessage(
    { ...expected, needsPriceUpdateStores: 0, skippedSpecialStores: 0, hasAny: false },
    t
  ),
  "notification.savedNoNotification"
);
assert.equal(
  buildPriceNotificationSaveMessage(
    { ...expected, needsPriceUpdateStores: 0, skippedSpecialStores: 0, cancelledStores: 4 },
    t
  ),
  'notification.reverted|{"count":4}',
  "改回原价只撤销通知时提示撤销家数"
);
assert.equal(
  buildPriceNotificationSaveMessage({ ...expected, skippedSpecialStores: 0, cancelledStores: 2 }, t),
  'notification.savedAndSent|{"detail":"notification.needsPriceUpdate|{\\"count\\":6}"}notification.cancelledSuffix|{"count":2}'
);
assert.equal(
  buildPriceNotificationSaveMessage({ ...expected, needsPriceUpdateStores: 0, hasAny: false }, t),
  'notification.savedOnlySkipped|{"count":1}'
);

assert.equal(
  buildSyncToOtherStoresMessage(5, { ...expected, labelOnlyStores: 4 }, t),
  'notification.syncedStoresWithLabels|{"count":5,"labelCount":4}'
);
assert.equal(buildSyncToOtherStoresMessage(5, null, t), 'notification.syncedStores|{"count":5}');
assert.equal(
  buildSyncToOtherStoresMessage(5, null, t, true),
  'notification.syncedStores|{"count":5}notification.warehouseUpdatedSuffix',
  "仓库价一并更新时在提示末尾补一句"
);
assert.equal(
  buildSyncToOtherStoresMessage(5, { ...expected, labelOnlyStores: 4 }, t, true),
  'notification.syncedStoresWithLabels|{"count":5,"labelCount":4}notification.warehouseUpdatedSuffix'
);
assert.equal(
  buildSyncToOtherStoresMessage(5, null, t, false),
  'notification.syncedStores|{"count":5}',
  "仓库未更新（非仓库商品 / 旧后端）时提示不变"
);

assert.equal(buildPriceNotificationPreviewMessage(null, true, t), null);
assert.equal(
  buildPriceNotificationPreviewMessage({ affectedStores: 0, skippedSpecialStores: 3 }, true, t),
  null,
  "受影响分店为 0 时不显示预告"
);
assert.equal(
  buildPriceNotificationPreviewMessage({ affectedStores: 6, skippedSpecialStores: 0 }, true, t),
  'notification.previewAutoSync|{"count":6}'
);
assert.equal(
  buildPriceNotificationPreviewMessage({ affectedStores: 6, skippedSpecialStores: 2 }, false, t),
  'notification.previewNeedsUpdate|{"count":6}notification.previewSkippedSuffix|{"count":2}'
);

console.log("price-updates/price-notification.test.ts: ok");
