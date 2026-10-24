import assert from "node:assert/strict";
import {
  normalizeSalesOrderBranchCatalog,
  normalizeSalesOrderDetail,
  normalizeSalesOrderListPage,
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

console.log("sales-orders api-normalization tests passed");
