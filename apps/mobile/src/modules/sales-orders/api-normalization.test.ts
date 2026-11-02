import assert from "node:assert/strict";
import {
  normalizeSalesOrderBranchCatalog,
  normalizeSalesOrderDetail,
  normalizeSalesOrderListPage,
  normalizeSalesOrderReceiptLink,
  normalizeSalesOrderReceiptView,
} from "./api-normalization";

const basePage = {
  items: [
    {
      orderGuid: "ORDER-1",
      branchCode: "S1",
      branchName: "北岸店",
      deviceCode: "POS-02",
      orderTime: "2026-09-17T04:32:00",
      skuCount: 3,
      itemCount: 3,
      quantityTotal: 5,
      totalAmount: 196.5,
      discountAmount: 10,
      actualAmount: 186.5,
      status: 1,
      matchedProducts: [{ productCode: "C9E9", itemNumber: "HB10023", productName: "保温杯", barcode: null, quantity: 2 }],
    },
    // WhenWritingNull 会省略空字段，命中列表缺省应视为空数组
    { orderGuid: "ORDER-2", status: 3 },
    { orderGuid: "ORDER-3", status: 1, hasRefundItems: true, actualAmount: -12 },
  ],
  total: 3,
  pageNumber: 1,
  pageSize: 20,
  scope: "authorized-stores",
  range: { startDate: "2026-09-17", endDate: "2026-09-17", dayCount: 1 },
  sortDirection: "desc",
};

const page = normalizeSalesOrderListPage({ success: true, data: basePage });
assert.equal(page.items.length, 3);
assert.equal(page.items[0].hasRefundItems, false, "后端明确返回 false");
assert.equal(page.items[1].hasRefundItems, false, "旧后端不返回 hasRefundItems 时按不含退款商品");
assert.equal(page.items[2].hasRefundItems, true, "新后端标出含退款商品的订单");
assert.equal(page.items[0].matchedProducts[0].productCode, "C9E9");
assert.equal(page.items[0].matchedProducts[0].itemNumber, "HB10023");
assert.deepEqual(page.items[1].matchedProducts, []);
assert.equal(page.items[1].branchName, null);
assert.equal(page.items[0].quantityTotal, 5);
assert.equal(page.items[1].quantityTotal, null);
assert.equal(page.scope, "authorized-stores");

// 区间越界的响应必须拒绝，不能让后端异常放大的数据进入列表
assert.throws(
  () =>
    normalizeSalesOrderListPage({
      success: true,
      data: { ...basePage, range: { startDate: "2026-08-01", endDate: "2026-09-17", dayCount: 48 } },
    }),
  /invalid/,
);
// 业务失败信封抛出并保留错误码
assert.throws(
  () => normalizeSalesOrderListPage({ success: false, message: "太长", errorCode: "DATE_RANGE_TOO_LONG" }),
  (error: unknown) => (error as { code?: string }).code === "DATE_RANGE_TOO_LONG",
);
assert.throws(() => normalizeSalesOrderListPage({ success: true, data: { items: "x" } }), /invalid/);

const catalog = normalizeSalesOrderBranchCatalog({
  success: true,
  data: { scope: "all-stores", branches: [{ storeCode: "S1", storeName: " " }, { storeCode: "S2", storeName: "西区店" }] },
});
assert.equal(catalog.scope, "all-stores");
assert.equal(catalog.branches[0].storeName, "S1", "门店名缺失时退回编码");
assert.equal(catalog.branches[1].storeName, "西区店");

const detail = normalizeSalesOrderDetail({
  success: true,
  data: {
    order: { orderGuid: "ORDER-1", status: 1, actualAmount: 186.5 },
    orderDetails: [{ productCode: "HB10023", productName: "保温杯", quantity: 2, unitPrice: 45, actualAmount: 90 }],
    paymentDetails: null,
  },
});
assert.equal(detail.order.orderGuid, "ORDER-1");
assert.equal(detail.order.hasRefundItems, false, "详情缺省按不含退款商品");
assert.equal(
  normalizeSalesOrderDetail({ success: true, data: { order: { orderGuid: "ORDER-9", status: 1, hasRefundItems: true }, orderDetails: [] } }).order.hasRefundItems,
  true,
  "详情订单头带上 hasRefundItems，详情金额才能着色",
);
assert.equal(detail.lines.length, 1);
assert.equal(detail.lines[0].productImage, null);
assert.equal(detail.lines[0].itemNumber, null, "主档缺失时货号为空，由界面回退显示商品编码");
assert.deepEqual(detail.payments, []);
assert.throws(() => normalizeSalesOrderDetail({ success: true, data: { orderDetails: [] } }), /invalid/);

// 在线小票：后端已排好版，空行的 text 与 align / bold 可能被 WhenWritingNull 省略，缺省按空串 / 左对齐 / 不加粗。
const receiptView = normalizeSalesOrderReceiptView({
  success: true,
  data: {
    document: {
      orderGuid: "7e4c91d2-0b5a-4f3e-9a21-5c8d6e0f13ab",
      shortCode: "6E0F13AB",
      statusKind: "paid",
      lines: [
        { kind: "text", text: "HotBargain", align: "center", bold: true },
        { kind: "blank" },
        { kind: "barcode", text: "6E0F13AB", align: "center" },
        { kind: "separator", text: "-".repeat(42) },
      ],
    },
    link: null,
  },
});
assert.equal(receiptView.link, null, "没有有效链接时为 null");
assert.deepEqual(receiptView.document.lines[1], { kind: "blank", text: "", align: "left", bold: false });
assert.deepEqual(receiptView.document.lines[2], { kind: "barcode", text: "6E0F13AB", align: "center", bold: false });
assert.equal(receiptView.document.lines[0].bold, true);

// link 字段整体缺省（旧后端 / WhenWritingNull）也按没有链接处理。
assert.equal(
  normalizeSalesOrderReceiptView({
    success: true,
    data: { document: { orderGuid: "O-1", shortCode: "", statusKind: "refund", lines: [] } },
  }).link,
  null,
);

const receiptLink = normalizeSalesOrderReceiptLink({
  success: true,
  data: {
    url: "https://hotbargain.vip/r/7K2M9QXD4TBH",
    expiresAtUtc: "2027-01-28T04:00:00Z",
    createdAtUtc: "2026-10-30T04:00:00Z",
    openCount: 2,
  },
});
assert.equal(receiptLink.url, "https://hotbargain.vip/r/7K2M9QXD4TBH");
assert.equal(receiptLink.openCount, 2);

// 非法载荷必须抛错，不能把半截数据画成小票。
assert.throws(() => normalizeSalesOrderReceiptView({ success: true, data: { document: { orderGuid: "", lines: [] } } }));
assert.throws(() =>
  normalizeSalesOrderReceiptView({
    success: true,
    data: { document: { orderGuid: "O-1", shortCode: "", statusKind: "paid", lines: [{ kind: "html", text: "x" }] } },
  }),
);
assert.throws(() => normalizeSalesOrderReceiptLink({ success: true, data: { url: "", expiresAtUtc: "", createdAtUtc: "", openCount: 0 } }));

console.log("sales-orders api-normalization tests passed");
