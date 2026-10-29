import { canLinkMatchedProduct, isProductLinkConfirmed } from "./matched-product-link";
import { assertEqual, makeItem } from "./test-helpers";

const admin = { isAdmin: true, hasUnsavedEdit: false };
const notExists = makeItem({ existingProductCount: 0, barcodeStatus: 2, barcodeMatchCount: 1 });

// 口径搬自 Web workbenchLogic.test.ts，并加上手机端的管理员 / 未保存前置条件。
assertEqual(canLinkMatchedProduct(notExists, admin), true, "管理员 + 主档不存在 + 无未保存修改 才允许选用");
assertEqual(canLinkMatchedProduct(notExists, { isAdmin: false, hasUnsavedEdit: false }), false, "非管理员不允许");
assertEqual(canLinkMatchedProduct(notExists, { isAdmin: true, hasUnsavedEdit: true }), false, "该行有未保存修改不允许");
assertEqual(canLinkMatchedProduct(notExists, { isAdmin: false, hasUnsavedEdit: true }), false, "两项都不满足不允许");
assertEqual(canLinkMatchedProduct(makeItem({ existingProductCount: 1 }), admin), false, "主档已存在的行不允许选用");
assertEqual(canLinkMatchedProduct(makeItem({ existingProductCount: null }), admin), false, "未检测的行不允许选用");
assertEqual(canLinkMatchedProduct(makeItem({ existingProductCount: undefined }), admin), false, "undefined 同样是未检测");
assertEqual(
  canLinkMatchedProduct(makeItem({ existingProductCount: 0, isCreatedByThisInvoice: true }), admin),
  false,
  "本单新品不允许选用"
);

// 选用后复核
assertEqual(isProductLinkConfirmed({ productCode: "P-LINK" }, "P-LINK"), true, "编码一致");
assertEqual(isProductLinkConfirmed({ productCode: "p-link " }, "P-LINK"), true, "忽略大小写与空白（检测结果侧）");
assertEqual(isProductLinkConfirmed({ productCode: "P-LINK" }, "  p-link"), true, "忽略大小写与空白（所选编码侧）");
assertEqual(isProductLinkConfirmed({ productCode: "" }, "P-LINK"), false, "后端清掉关联时判定未关联");
assertEqual(isProductLinkConfirmed({ productCode: "   " }, "P-LINK"), false, "空白编码判定未关联");
assertEqual(isProductLinkConfirmed({ productCode: "P-OTHER" }, "P-LINK"), false, "关联到别的商品判定未确认");
assertEqual(isProductLinkConfirmed(undefined, "P-LINK"), false, "缺少检测结果判定未关联");
assertEqual(isProductLinkConfirmed(null, "P-LINK"), false, "null 检测结果判定未关联");
assertEqual(isProductLinkConfirmed({ productCode: "" }, ""), false, "两边都为空也不算确认");
