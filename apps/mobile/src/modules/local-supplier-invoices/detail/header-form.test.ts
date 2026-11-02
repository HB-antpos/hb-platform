import {
  buildHeaderFormValues,
  buildHeaderSavePayload,
  isHeaderDirty,
  isValidDateOnly,
  normalizeDateOnly,
} from "./header-form";
import { assertDeepEqual, assertEqual } from "./test-helpers";

// 口径搬自 Web invoiceHeaderForm.test.ts / workbenchLogic.test.ts 的表头部分。
const invoice = {
  storeCode: "S01",
  supplierCode: "SUP01",
  orderDate: "2026-10-01T00:00:00",
  inboundDate: "",
  remarks: "Halloween",
};

assertEqual(normalizeDateOnly("2026-07-18T00:00:00Z"), "2026-07-18", "ISO 日期取日期部分");
assertEqual(normalizeDateOnly("2026-07-18"), "2026-07-18", "纯日期不变");
assertEqual(normalizeDateOnly(" 2026-07-18 "), "2026-07-18", "首尾空格被忽略");
assertEqual(normalizeDateOnly(""), "", "空串");
assertEqual(normalizeDateOnly(null), "", "null");
assertEqual(normalizeDateOnly("07/18/2026"), "", "无法识别的格式给空串");
assertEqual(isValidDateOnly("2026-02-28"), true, "合法日期");
assertEqual(isValidDateOnly("2028-02-29"), true, "闰日合法");
assertEqual(isValidDateOnly("2026-02-30"), false, "不存在的日期");
assertEqual(isValidDateOnly("2026-13-01"), false, "月份越界");
assertEqual(isValidDateOnly("2026-07-18T00:00:00"), false, "带时间部分不是纯日期");
assertEqual(isValidDateOnly("20260718"), false, "格式错误");

const values = buildHeaderFormValues(invoice);
assertDeepEqual(
  values,
  { storeCode: "S01", supplierCode: "SUP01", orderDate: "2026-10-01", inboundDate: "", remarks: "Halloween" },
  "表单值使用编码与日期部分"
);

const payload = buildHeaderSavePayload({
  storeCode: " S02 ",
  supplierCode: " SUP02 ",
  orderDate: "2026-07-20",
  inboundDate: "2026-07-21T00:00:00Z",
  remarks: " 新备注 ",
});
assertDeepEqual(
  payload,
  { storeCode: "S02", supplierCode: "SUP02", orderDate: "2026-07-20", inboundDate: "2026-07-21", remarks: "新备注" },
  "payload：编码与备注去空格，日期取日期部分"
);
assertDeepEqual(
  JSON.parse(JSON.stringify(buildHeaderSavePayload({ storeCode: "  ", remarks: "", orderDate: "bad" }))),
  {},
  "空值与非法日期省略（与 Web 一致：空 = 不提交）"
);

// 未保存判定
assertEqual(isHeaderDirty(values, invoice), false, "刚加载的表头不算修改");
assertEqual(isHeaderDirty({ ...values, remarks: " Halloween " }, invoice), false, "首尾空格不影响落库");
assertEqual(isHeaderDirty({ ...values, orderDate: "2026-10-01T09:00:00Z" }, invoice), false, "日期格式差异不算修改");
assertEqual(isHeaderDirty({ ...values, inboundDate: "2026-10-06" }, invoice), true, "填写入库日期算修改");
assertEqual(isHeaderDirty({ ...values, supplierCode: "SUP02" }, invoice), true, "更换供应商算修改");
assertEqual(isHeaderDirty({ ...values, storeCode: "S02" }, invoice), true, "更换分店算修改");
assertEqual(isHeaderDirty({ ...values, remarks: "" }, { ...invoice, remarks: "" }), false, "空备注不算修改");
assertEqual(isHeaderDirty({ ...values, remarks: "x" }, { ...invoice, remarks: "" }), true, "新增备注算修改");
assertEqual(isHeaderDirty(values, null), false, "订单未加载时不算修改");
assertEqual(isHeaderDirty(undefined, invoice), false, "无表单值不算修改");
assertEqual(isHeaderDirty({ remarks: "Halloween" }, invoice), true, "缺省字段按空处理（与 Web 一致）：少了分店等字段会判为修改，调用方应传完整表单值");
