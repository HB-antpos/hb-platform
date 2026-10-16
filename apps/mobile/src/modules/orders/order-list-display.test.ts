import {
  buildOrderListRequest,
  filterOrderDetailLinesByItemNumber,
  getOrderDetailLineAllocatedImportAmount,
  formatOrderDate,
  getOrderDetailTotalAllocatedImportAmount,
  getOrderRowNumber,
} from "./order-list-display";
import type { StoreOrderDetailLine } from "./types";

function assertEqual(actual: unknown, expected: unknown, label: string) {
  if (actual !== expected) {
    throw new Error(`${label}: expected ${String(expected)}, got ${String(actual)}`);
  }
}

function assertCodes(actual: StoreOrderDetailLine[], expected: string[], label: string) {
  const actualCodes = actual.map((item) => item.productCode).join(",");
  const expectedCodes = expected.join(",");
  if (actualCodes !== expectedCodes) {
    throw new Error(`${label}: expected ${expectedCodes}, got ${actualCodes}`);
  }
}

function makeLine(
  productCode: string,
  overrides: Partial<StoreOrderDetailLine> = {}
): StoreOrderDetailLine {
  return {
    detailGUID: `detail-${productCode}`,
    productCode,
    quantity: 1,
    price: 1,
    amount: 1,
    importPrice: 1,
    importAmount: 1,
    minOrderQuantity: 1,
    isActive: true,
    ...overrides,
  };
}

assertEqual(getOrderRowNumber(1, 10, 0), 1, "第一页第一行行号为 1");
assertEqual(getOrderRowNumber(2, 10, 0), 11, "第二页第一行行号为 11");
assertEqual(getOrderRowNumber(2, 10, 9), 20, "第二页第十行行号为 20");

assertEqual(formatOrderDate(undefined, "en-AU"), "--", "空订单日期显示占位符");
assertEqual(formatOrderDate("not-a-date", "en-AU"), "not-a-date", "无法解析的订单日期保持原值");

const formattedOrderDate = formatOrderDate("2026-06-01T00:00:00", "en-AU");
if (formattedOrderDate.includes("00:00") || formattedOrderDate.includes(":")) {
  throw new Error(`订单日期不能包含时间: got ${formattedOrderDate}`);
}
if (!formattedOrderDate.includes("2026")) {
  throw new Error(`订单日期应保留年份: got ${formattedOrderDate}`);
}

const defaultRequest = buildOrderListRequest();
assertEqual(defaultRequest.pageNumber, 1, "默认订单列表页码为 1");
assertEqual(defaultRequest.pageSize, 10, "默认订单列表每页 10 条");
assertEqual(defaultRequest.statusList.join(","), "1,3,2", "默认订单列表状态为历史订单状态");

const customRequest = buildOrderListRequest({ pageNumber: 3, pageSize: 25, statusList: [2] });
assertEqual(customRequest.pageNumber, 3, "显式订单列表页码保持调用方设置");
assertEqual(customRequest.pageSize, 25, "显式订单列表每页数量保持调用方设置");
assertEqual(customRequest.statusList.join(","), "2", "显式订单列表状态保持调用方设置");

const scopedRequest = buildOrderListRequest({ storeCodes: ["BRISBANE", "SYDNEY"], statusList: [1] });
assertEqual(scopedRequest.storeCode, undefined, "多店范围请求不应携带单店参数");
assertEqual(scopedRequest.storeCodes?.join(","), "BRISBANE,SYDNEY", "多店范围请求保留可管理分店代码");
assertEqual(buildOrderListRequest({ storeCodes: [] }).storeCodes, undefined, "空多店范围不应发起无范围请求");

const matchRequest = buildOrderListRequest({
  productKeyword: " 9328644 ",
  startDate: "2026-04-17",
  endDate: "2026-10-17",
});
assertEqual(matchRequest.productKeyword, "9328644", "商品匹配关键字应去掉首尾空格后发送");
assertEqual(matchRequest.startDate, "2026-04-17", "下单起始日期原样发送");
assertEqual(matchRequest.endDate, "2026-10-17", "下单结束日期原样发送");
const emptyMatchRequest = buildOrderListRequest({ productKeyword: "   ", startDate: "", endDate: " " });
assertEqual(emptyMatchRequest.productKeyword, undefined, "空白商品匹配关键字不发送");
assertEqual(emptyMatchRequest.startDate, undefined, "空起始日期不发送");
assertEqual(emptyMatchRequest.endDate, undefined, "空结束日期不发送");

const lines = [
  makeLine("P001", { itemNumber: "HB-001", barcode: "BAR-001" }),
  makeLine("P002", { itemNumber: "", barcode: "BAR-002" }),
  makeLine("P003", { itemNumber: "HB-003", barcode: "BAR-MATCH" }),
];

assertCodes(filterOrderDetailLinesByItemNumber(lines, ""), ["P001", "P002", "P003"], "空关键词返回全部明细");
assertCodes(filterOrderDetailLinesByItemNumber(lines, " hb-001 "), ["P001"], "货号匹配忽略首尾空格和大小写");
assertCodes(filterOrderDetailLinesByItemNumber(lines, "bar-002"), ["P002"], "货号为空时使用条码兜底");
assertCodes(filterOrderDetailLinesByItemNumber(lines, "bar-match"), [], "存在货号时不使用条码覆盖匹配");
assertCodes(filterOrderDetailLinesByItemNumber(lines, "missing"), [], "无匹配时返回空数组");

const staleAmountLine = makeLine("P004", {
  allocQuantity: 2,
  importPrice: 7,
  importAmount: 55,
});
assertEqual(
  getOrderDetailLineAllocatedImportAmount(staleAmountLine),
  14,
  "旧响应缺少发货金额时明细应按发货数量和进口价兜底"
);
assertEqual(
  getOrderDetailTotalAllocatedImportAmount({
    orderGUID: "ORDER-001",
    totalAmount: 0,
    totalQuantity: 10,
    totalImportAmount: 55,
    totalVolume: 0,
    items: [staleAmountLine],
  }),
  14,
  "旧响应缺少发货总额时不应回退订货金额"
);
