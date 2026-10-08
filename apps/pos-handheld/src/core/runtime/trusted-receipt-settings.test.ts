import assert from "node:assert/strict";
import test from "node:test";

import type { ReceiptPrinterSettings } from "../db/pos-settings-repository";

import { resolveTrustedReceiptPrinterSettings } from "./trusted-receipt-settings";

const settings: ReceiptPrinterSettings = {
  printEnabled: true,
  drawerEnabled: false,
  peripheralId: "printer-1",
  paper: "80mm",
  locale: "en",
  brandName: "Hot Bargain",
  storeName: "Saved Store",
  address: "1 Queen St",
  phone: "0712345678",
  abn: "12 345 678 901",
  returnPolicy: "Refunds within 14 days.",
  voucherTerms: "Valid at all stores.",
  installmentTerms: "Deposit $30 minimum.",
  profileStoreCode: "1042",
  profileVersion: 0,
  profileAckedVersion: 0,
};

test("当前店本机保存店名优先，设备展示名仅在本机为空时兜底", async () => {
  const localWins = await resolveTrustedReceiptPrinterSettings(
    settings,
    "1042",
    async () => ({
      deviceCode: "POS_1042_1155",
      storeCode: "1042",
      storeName: "Redbank Plaza",
      terminalName: "",
    }),
  );
  const deviceFallback = await resolveTrustedReceiptPrinterSettings(
    { ...settings, storeName: "" },
    "1042",
    async () => ({
      deviceCode: "POS_1042_1155",
      storeCode: "1042",
      storeName: "Redbank Plaza",
      terminalName: "",
    }),
  );

  assert.equal(localWins.storeName, "Saved Store");
  assert.equal(localWins.address, settings.address);
  assert.equal(localWins.peripheralId, settings.peripheralId);
  assert.equal(deviceFallback.storeName, "Redbank Plaza");
});

test("可信名称缺失或读取失败时依次回退保存名称和门店编码", async () => {
  const saved = await resolveTrustedReceiptPrinterSettings(
    settings,
    "1042",
    async () => ({
      deviceCode: "POS_1042_1155",
      storeCode: "1042",
      storeName: "   ",
      terminalName: "",
    }),
  );
  const code = await resolveTrustedReceiptPrinterSettings(
    { ...settings, storeName: "" },
    "1042",
    async () => {
      throw new Error("keychain unavailable");
    },
  );

  assert.equal(saved.storeName, "Saved Store");
  assert.equal(code.storeName, "1042");
});

test("包含控制字符的设备展示名称不能进入小票设置", async () => {
  const resolved = await resolveTrustedReceiptPrinterSettings(
    settings,
    "1042",
    async () => ({
      deviceCode: "POS_1042_1155",
      storeCode: "1042",
      storeName: "Unsafe\u001b@Store",
      terminalName: "",
    }),
  );

  assert.equal(resolved.storeName, "Saved Store");
});

test("旧有资料首次读取当前分店时持久化绑定 profileStoreCode", async () => {
  const persisted: { value: ReceiptPrinterSettings | null } = { value: null };
  const resolved = await resolveTrustedReceiptPrinterSettings(
    { ...settings, profileStoreCode: "" },
    "1042",
    async () => ({
      deviceCode: "POS_1042_1155",
      storeCode: "1042",
      storeName: "Redbank Plaza",
      terminalName: "",
    }),
    async (next) => { persisted.value = next; },
  );

  assert.equal(resolved.profileStoreCode, "1042");
  assert.equal(persisted.value?.profileStoreCode, "1042");
  assert.equal(persisted.value?.peripheralId, settings.peripheralId);
  assert.equal(persisted.value?.printEnabled, settings.printEnabled);
  assert.equal(persisted.value?.drawerEnabled, settings.drawerEnabled);
});

test("legacy 绑定落盘失败时不采用无 scope 旧资料，返回保留硬件的安全 fallback", async () => {
  const resolved = await resolveTrustedReceiptPrinterSettings(
    { ...settings, profileStoreCode: "" },
    "1042",
    undefined,
    async () => { throw new Error("save failed"); },
  );

  assert.equal(resolved.profileStoreCode, "1042");
  assert.equal(resolved.brandName, "");
  assert.equal(resolved.storeName, "1042");
  assert.equal(resolved.address, "");
  assert.equal(resolved.phone, "");
  assert.equal(resolved.abn, "");
  assert.equal(resolved.returnPolicy, "");
  assert.equal(resolved.voucherTerms, "");
  assert.equal(resolved.installmentTerms, "");
  assert.equal(resolved.peripheralId, settings.peripheralId);
  assert.equal(resolved.printEnabled, settings.printEnabled);
  assert.equal(resolved.drawerEnabled, settings.drawerEnabled);
  assert.equal(resolved.paper, settings.paper);
  assert.equal(resolved.locale, settings.locale);
});

test("profileStoreCode 与当前店不匹配时清空资料但保留硬件设置", async () => {
  const persisted: { value: ReceiptPrinterSettings | null } = { value: null };
  const resolved = await resolveTrustedReceiptPrinterSettings(
    { ...settings, profileStoreCode: "9999" },
    "1042",
    undefined,
    async (next) => { persisted.value = next; },
  );

  assert.equal(resolved.profileStoreCode, "1042");
  assert.equal(resolved.brandName, "");
  assert.equal(resolved.storeName, "1042");
  assert.equal(resolved.address, "");
  assert.equal(resolved.phone, "");
  assert.equal(resolved.abn, "");
  assert.equal(resolved.returnPolicy, "");
  assert.equal(resolved.voucherTerms, "");
  assert.equal(resolved.installmentTerms, "");
  assert.equal(resolved.peripheralId, settings.peripheralId);
  assert.equal(resolved.printEnabled, settings.printEnabled);
  assert.equal(resolved.drawerEnabled, settings.drawerEnabled);
  assert.equal(persisted.value?.peripheralId, settings.peripheralId);
  assert.equal(persisted.value?.printEnabled, settings.printEnabled);
  assert.equal(persisted.value?.drawerEnabled, settings.drawerEnabled);
  assert.equal(persisted.value?.profileStoreCode, "1042");
  assert.equal(persisted.value?.brandName, "");
  assert.equal(persisted.value?.storeName, "");
  assert.equal(persisted.value?.address, "");
  assert.equal(persisted.value?.phone, "");
  assert.equal(persisted.value?.abn, "");
  assert.equal(persisted.value?.returnPolicy, "");
  // 旧店的券使用说明 / 分期条款同样作废，不能串到新店小票上。
  assert.equal(persisted.value?.voucherTerms, "");
  assert.equal(persisted.value?.installmentTerms, "");
});

test("换店清空资料时同时清空下发版本与已回执版本，新店下一轮同步重新拉取", async () => {
  const managed: ReceiptPrinterSettings = {
    ...settings,
    profileStoreCode: "9999",
    profileVersion: 7,
    profileAckedVersion: 7,
  };
  const persisted: { value: ReceiptPrinterSettings | null } = { value: null };

  const resolved = await resolveTrustedReceiptPrinterSettings(
    managed,
    "1042",
    undefined,
    async (next) => { persisted.value = next; },
  );

  assert.equal(resolved.profileVersion, 0);
  assert.equal(resolved.profileAckedVersion, 0);
  assert.equal(persisted.value?.profileVersion, 0);
  assert.equal(persisted.value?.profileAckedVersion, 0);
  // 硬件设置照旧保留
  assert.equal(persisted.value?.peripheralId, settings.peripheralId);
});

test("本机无 scope 的旧资料绑定落盘失败走 fallback 时同样不带下发版本", async () => {
  const resolved = await resolveTrustedReceiptPrinterSettings(
    { ...settings, profileStoreCode: "", profileVersion: 3, profileAckedVersion: 3 },
    "1042",
    undefined,
    async () => { throw new Error("db busy"); },
  );

  assert.equal(resolved.brandName, "");
  assert.equal(resolved.profileVersion, 0);
  assert.equal(resolved.profileAckedVersion, 0);
});

test("当前店已应用的下发资料与版本原样保留，不触发清理落盘", async () => {
  let persistCalls = 0;
  const managed: ReceiptPrinterSettings = {
    ...settings,
    profileVersion: 7,
    profileAckedVersion: 6,
  };

  const resolved = await resolveTrustedReceiptPrinterSettings(
    managed,
    "1042",
    undefined,
    async () => { persistCalls += 1; },
  );

  assert.equal(resolved.profileVersion, 7);
  assert.equal(resolved.profileAckedVersion, 6);
  assert.equal(resolved.brandName, "Hot Bargain");
  assert.equal(resolved.voucherTerms, "Valid at all stores.");
  assert.equal(resolved.installmentTerms, "Deposit $30 minimum.");
  assert.equal(persistCalls, 0);
});

test("无 scope 的旧设置只有券使用说明 / 分期条款非空时也视为旧资料，首次读取绑定当前店", async () => {
  const onlyTerms: ReceiptPrinterSettings = {
    ...settings,
    brandName: "",
    storeName: "",
    address: "",
    phone: "",
    abn: "",
    returnPolicy: "",
    profileStoreCode: "",
  };
  const persisted: { value: ReceiptPrinterSettings | null } = { value: null };

  const resolved = await resolveTrustedReceiptPrinterSettings(
    onlyTerms,
    "1042",
    undefined,
    async (next) => { persisted.value = next; },
  );

  assert.equal(resolved.profileStoreCode, "1042");
  assert.equal(persisted.value?.profileStoreCode, "1042");
  // 绑定成功时本机条款原样保留。
  assert.equal(resolved.voucherTerms, "Valid at all stores.");
  assert.equal(persisted.value?.installmentTerms, "Deposit $30 minimum.");
});

test("全新空 profile 不误绑定 legacy，设备店名仅在本机为空时兜底", async () => {
  let persistCalls = 0;
  const emptyProfile: ReceiptPrinterSettings = {
    ...settings,
    brandName: "",
    storeName: "",
    address: "",
    phone: "",
    abn: "",
    returnPolicy: "",
    voucherTerms: "",
    installmentTerms: "",
    profileStoreCode: "",
  };
  const fresh = await resolveTrustedReceiptPrinterSettings(
    emptyProfile,
    "1042",
    async () => ({
      deviceCode: "POS_1042_1155",
      storeCode: "1042",
      storeName: "Redbank Plaza",
      terminalName: "",
    }),
    async () => { persistCalls += 1; },
  );

  assert.equal(fresh.profileStoreCode, "");
  assert.equal(fresh.storeName, "Redbank Plaza");
  assert.equal(persistCalls, 0);

  const localWins = await resolveTrustedReceiptPrinterSettings(
    { ...emptyProfile, storeName: "Local Store" },
    "1042",
    async () => ({
      deviceCode: "POS_1042_1155",
      storeCode: "1042",
      storeName: "Redbank Plaza",
      terminalName: "",
    }),
  );
  assert.equal(localWins.storeName, "Local Store");
});
