import assert from "node:assert/strict";

import {
  resolveDefaultSupplierAssistantStore,
  resolvePrimaryStoreCode,
  supplierAssistantStoreStorageKey,
  toSupplierAssistantStores,
} from "./store-selection";

const stores = toSupplierAssistantStores({
  stores: [
    { storeCode: "1042", storeName: "Orion" },
    { storeCode: "1012", storeName: "Bankstown" },
    { storeCode: "1012", storeName: "重复的分店" },
    { storeCode: " ", storeName: "空编码" },
    { storeCode: "1099", storeName: "" },
    null,
    "bad",
  ],
});

// 接口数据：去重、去掉空编码、名称为空时用编码。
assert.deepEqual(stores, [
  { storeCode: "1042", storeName: "Orion" },
  { storeCode: "1012", storeName: "Bankstown" },
  { storeCode: "1099", storeName: "1099" },
]);
assert.deepEqual(toSupplierAssistantStores(null), []);
assert.deepEqual(toSupplierAssistantStores({ stores: "x" }), []);

// 默认分店：上次选过的优先（不区分大小写），其次主分店，单店直接用，多店都没有则要员工选。
assert.equal(resolveDefaultSupplierAssistantStore(stores, { rememberedStoreCode: "1012", primaryStoreCode: "1042" })?.storeCode, "1012");
assert.equal(resolveDefaultSupplierAssistantStore(stores, { rememberedStoreCode: "9999", primaryStoreCode: "1042" })?.storeCode, "1042");
assert.equal(resolveDefaultSupplierAssistantStore(stores, { rememberedStoreCode: null, primaryStoreCode: "8888" }), null);
assert.equal(resolveDefaultSupplierAssistantStore(stores, {}), null);
assert.equal(
  resolveDefaultSupplierAssistantStore([{ storeCode: "1042", storeName: "Orion" }], {})?.storeCode,
  "1042",
);
assert.equal(resolveDefaultSupplierAssistantStore([], { rememberedStoreCode: "1042" }), null);

// 主分店只认 isPrimary 标记，不猜第一家。
assert.equal(resolvePrimaryStoreCode([{ storeCode: "1012" }, { storeCode: "1042", isPrimary: true }]), "1042");
assert.equal(resolvePrimaryStoreCode([{ storeCode: "1012" }]), null);
assert.equal(resolvePrimaryStoreCode(undefined), null);

// 记住的分店按账号分开存。
assert.equal(supplierAssistantStoreStorageKey("ABC-1"), "supplier_assistant_store:abc-1");
assert.equal(supplierAssistantStoreStorageKey(""), null);
assert.equal(supplierAssistantStoreStorageKey(null), null);

console.log("supplier-assistant store-selection: ok");
