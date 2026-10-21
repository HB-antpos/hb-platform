import assert from "node:assert/strict";
import {
  applyContainerDetailServerConflicts,
  buildContainerDetailEditForm,
  buildContainerDetailEditPayload,
  isCurrentContainerDetailEditSession,
  reconcileContainerDetailPartialSave,
} from "./container-detail-edit-state";
import type { ContainerDetail, ContainerDetailConcurrentConflict } from "./types";

// 新商品：本次进口价 / 本次零售价只存货柜明细，可以改；已有价只有已有商品能改（见文件末尾）。
const detail: ContainerDetail = {
  HGUID: "DETAIL-1",
  是否新商品: true,
  商品名称: "旧名称",
  英文名称: "Old name",
  国内价格: 9.9,
  进口价格: 4.1,
  贴牌价格: 8.8,
  warehouseIsActive: true,
  商品信息: { 商品名称: "旧名称", 英文名称: "Old name" },
  serverFieldTokens: {
    "商品名称": "name-old",
    "国内价格": "domestic-old",
    "贴牌价格": "oem-old",
    IsActive: "active-old",
    "进口价格": "import-old",
  },
};

const conflicts: ContainerDetailConcurrentConflict[] = [
  {
    hguid: "DETAIL-1",
    field: "国内价格",
    code: "CONCURRENT_FIELD_UPDATE",
    message: "服务器已更新",
    serverValue: null,
    submittedValue: 10.5,
    currentServerFieldToken: "domestic-current",
  },
  {
    hguid: "DETAIL-1",
    field: "商品名称",
    code: "CONCURRENT_FIELD_UPDATE",
    message: "服务器已更新",
    serverValue: "服务器名称",
    submittedValue: "我的名称",
    currentServerFieldToken: "name-current",
  },
  {
    hguid: "DETAIL-1",
    field: "贴牌价格",
    code: "CONCURRENT_FIELD_UPDATE",
    message: "服务器已更新",
    serverValue: 12.3,
    submittedValue: 10,
    currentServerFieldToken: "oem-current",
  },
  {
    hguid: "DETAIL-1",
    field: "IsActive",
    code: "CONCURRENT_FIELD_UPDATE",
    message: "服务器已更新",
    serverValue: false,
    submittedValue: true,
    currentServerFieldToken: "active-current",
  },
];

const form = {
  ...buildContainerDetailEditForm(detail),
  domesticPrice: "10.5",
  productName: "我的名称",
  oemPrice: "10",
  isActive: true,
  importPrice: "4.5",
};
const resolved = applyContainerDetailServerConflicts(detail, form, conflicts);

assert.equal(resolved.form.domesticPrice, "", "服务器 null 数值必须恢复为空输入，不能误写为 0");
assert.equal(resolved.detail.国内价格, undefined, "服务器 null 数值必须同步写入编辑基线");
assert.equal(resolved.form.productName, "服务器名称");
assert.equal(resolved.detail.商品名称, "服务器名称");
assert.equal(resolved.detail.商品信息?.商品名称, "服务器名称", "关联商品信息必须与编辑基线同步");
assert.equal(resolved.detail.贴牌价格, 12.3, "采用服务器值后「本次零售价」基线同步更新");
assert.equal(resolved.detail.warehouseOEMPrice, undefined, "「本次零售价」只是明细价格，不再联动仓库实时零售价");
assert.equal(resolved.detail.IsActive, false);
assert.equal(resolved.detail.warehouseIsActive, false, "仓库状态基线必须与采用值同步");
assert.equal(resolved.detail.serverFieldTokens?.["国内价格"], "domestic-current");

const payload = buildContainerDetailEditPayload(resolved.detail, resolved.form);
assert.equal(payload.进口价格, 4.5, "未解决的其它本地修改必须保留");
assert.equal(payload.国内价格, undefined, "采用服务器数值后后续保存不得重提该字段");
assert.equal(payload.商品名称, undefined, "采用服务器名称后后续保存不得重提该字段");
assert.equal(payload.贴牌价格, undefined, "采用服务器价格后后续保存不得重提该字段");
assert.equal(payload.IsActive, undefined, "采用服务器状态后后续保存不得重提该字段");

const partialBaseline: ContainerDetail = {
  HGUID: "DETAIL-PARTIAL",
  是否新商品: true,
  国内价格: 9.9,
  进口价格: 4.1,
  serverFieldTokens: { "国内价格": "domestic-old", "进口价格": "import-old" },
};
const partialForm = {
  ...buildContainerDetailEditForm(partialBaseline),
  domesticPrice: "10.2",
  importPrice: "4.5",
};
const partialPayload = buildContainerDetailEditPayload(partialBaseline, partialForm);
const partial = reconcileContainerDetailPartialSave({
  baseline: partialBaseline,
  form: partialForm,
  submittedPayload: partialPayload,
  latest: {
    ...partialBaseline,
    国内价格: 10.2,
    serverFieldTokens: { "国内价格": "domestic-new", "进口价格": "import-old" },
  },
  validationErrors: [{
    hguid: "DETAIL-PARTIAL",
    field: "进口价格",
    code: "SET_RELATION_INVALID",
    message: "套装关系不允许该进口价",
  }],
  conflicts: [],
});
assert.equal(partial.form.importPrice, "4.5", "任意字段验证失败都必须保留原始本地输入");
assert.equal(partial.detail.国内价格, 10.2, "同批成功字段必须从服务器最新值更新编辑基线");
assert.equal(partial.detail.serverFieldTokens?.["国内价格"], "domestic-new", "同批成功字段必须使用刷新后的令牌");
const retryPayload = buildContainerDetailEditPayload(partial.detail, partial.form);
assert.equal(retryPayload.国内价格, undefined, "同批成功字段后续保存不能再次提交");
assert.equal(retryPayload.进口价格, 4.5, "失败字段必须仍可重试");
assert.equal(retryPayload.expectedServerFieldTokens?.["进口价格"], "import-old", "失败字段继续使用自己的基线令牌");

// ---- 已有商品：只能改「已有进口价 / 已有零售价」，本次价只读 ----
const existingDetail: ContainerDetail = {
  HGUID: "DETAIL-EXISTING",
  是否新商品: false,
  国内价格: 9.9,
  进口价格: 4.1,
  贴牌价格: 6.5,
  warehouseImportPrice: 3.3,
  warehouseOEMPrice: 8.8,
  warehouseIsActive: true,
  serverFieldTokens: { "国内价格": "domestic-old", "进口价格": "import-old", "贴牌价格": "oem-old" },
};
const existingForm = buildContainerDetailEditForm(existingDetail);
assert.equal(existingForm.importPrice, "4.1", "本次进口价取明细自己的价格");
assert.equal(existingForm.oemPrice, "6.5", "本次零售价取明细自己的价格，不再显示仓库实时零售价");
assert.equal(existingForm.warehouseImportPrice, "3.3", "已有进口价取仓库实时进货价");
assert.equal(existingForm.warehouseOemPrice, "8.8", "已有零售价取仓库实时零售价");

const existingPayload = buildContainerDetailEditPayload(existingDetail, {
  ...existingForm,
  importPrice: "9",
  oemPrice: "9",
  warehouseImportPrice: "3.5",
  warehouseOemPrice: "9.99",
});
assert.equal(existingPayload.进口价格, undefined, "已有商品的本次进口价不会进请求（输入框本来就是禁用的）");
assert.equal(existingPayload.贴牌价格, undefined, "已有商品的本次零售价不会进请求");
assert.equal(existingPayload.仓库进口价, 3.5, "已有进口价用独立字段提交");
assert.equal(existingPayload.仓库零售价, 9.99, "已有零售价用独立字段提交");
assert.equal(existingPayload.expectedWarehouseImportPrice, 3.3, "带上提交时看到的仓库进货价作并发基线");
assert.equal(existingPayload.expectedWarehouseOemPrice, 8.8, "带上提交时看到的仓库零售价作并发基线");
assert.deepEqual(existingPayload.expectedServerFieldTokens, {}, "已有价不是货柜明细字段，没有字段令牌");
assert.throws(
  () => buildContainerDetailEditPayload(existingDetail, existingForm),
  /没有需要保存的修改/,
  "已有商品没改已有价就是没有修改",
);
assert.throws(
  () => buildContainerDetailEditPayload(existingDetail, { ...existingForm, warehouseOemPrice: "abc" }),
  /编辑字段存在无效数字/,
  "已有价输入了无效数字要拒绝",
);

const newProductPayload = buildContainerDetailEditPayload(
  { HGUID: "DETAIL-NEW", 是否新商品: true, 进口价格: 1, 贴牌价格: 2, warehouseIsActive: false },
  { ...buildContainerDetailEditForm({ HGUID: "DETAIL-NEW", 是否新商品: true, 进口价格: 1, 贴牌价格: 2 }), importPrice: "1.5", oemPrice: "2.5", warehouseOemPrice: "7" },
);
assert.equal(newProductPayload.进口价格, 1.5, "新商品可以改本次进口价");
assert.equal(newProductPayload.贴牌价格, 2.5, "新商品可以改本次零售价");
assert.equal(newProductPayload.仓库零售价, undefined, "新商品没有仓库记录，已有价不会进请求");

// 保存后刷新已有价基线：同一弹窗再次保存时不能拿旧基线，否则会被后端当成「被他人修改」。
const afterSave = reconcileContainerDetailPartialSave({
  baseline: existingDetail,
  form: { ...existingForm, warehouseOemPrice: "9.99" },
  submittedPayload: { ...existingPayload, 仓库进口价: undefined, expectedWarehouseImportPrice: undefined },
  latest: { ...existingDetail, warehouseOEMPrice: 9.99 },
  validationErrors: [],
  conflicts: [],
});
assert.equal(afterSave.warehouseBaselineRefreshed, true, "提交了已有价且整行没有错误时要刷新已有价基线");
assert.equal(afterSave.detail.warehouseOEMPrice, 9.99, "基线刷成服务器最新的仓库零售价");
assert.equal(afterSave.detail.WarehouseOEMPrice, 9.99, "两套命名字段同步刷新");
assert.equal(afterSave.detail.warehouseImportPrice, 3.3, "没提交的已有进口价保持原基线");
const rejected = reconcileContainerDetailPartialSave({
  baseline: existingDetail,
  form: { ...existingForm, warehouseImportPrice: "3.5" },
  submittedPayload: { hguid: "DETAIL-EXISTING", 仓库进口价: 3.5, expectedWarehouseImportPrice: 3.3, expectedServerFieldTokens: {} },
  latest: { ...existingDetail, warehouseImportPrice: 3.9 },
  validationErrors: [{
    hguid: "DETAIL-EXISTING",
    field: "仓库进口价",
    code: "WAREHOUSE_PRICE_CHANGED",
    message: "仓库进口价已被他人修改，请刷新后重试",
  }],
  conflicts: [],
});
assert.equal(rejected.form.warehouseImportPrice, "3.5", "被拒绝的已有价保留用户输入");
assert.equal(rejected.detail.warehouseImportPrice, 3.9, "被拒绝后基线刷成他人改过的最新仓库价，用户确认后才能再次提交");
assert.equal(
  buildContainerDetailEditPayload(rejected.detail, rejected.form).expectedWarehouseImportPrice,
  3.9,
  "再次提交使用新的基线",
);
const rowError = reconcileContainerDetailPartialSave({
  baseline: existingDetail,
  form: { ...existingForm, warehouseImportPrice: "3.5" },
  submittedPayload: { hguid: "DETAIL-EXISTING", 仓库进口价: 3.5, expectedWarehouseImportPrice: 3.3, expectedServerFieldTokens: {} },
  latest: { ...existingDetail, warehouseImportPrice: 3.9 },
  validationErrors: [{ hguid: "DETAIL-EXISTING", field: "*", code: "DETAIL_NOT_FOUND", message: "货柜明细不存在" }],
  conflicts: [],
});
assert.equal(rowError.warehouseBaselineRefreshed, false, "整行错误时不刷新基线");

assert.equal(isCurrentContainerDetailEditSession({
  expectedSessionId: "session-a",
  expectedHguid: "DETAIL-A",
  currentSessionId: "session-a",
  currentHguid: "DETAIL-A",
}), true, "保存响应只能更新发起保存的当前编辑会话");
assert.equal(isCurrentContainerDetailEditSession({
  expectedSessionId: "session-a",
  expectedHguid: "DETAIL-A",
  currentSessionId: "session-b",
  currentHguid: "DETAIL-B",
}), false, "迟到响应不得污染后来打开的另一行编辑弹窗");

console.log("container-detail-edit-state.test.ts: ok");
