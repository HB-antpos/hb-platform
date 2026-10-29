import {
  buildPromotionCopyPayload,
  buildPromotionGridPayload,
  buildPromotionPayload,
  normalizePromotionDetail,
  normalizePromotionsResponse,
  normalizeStatusCounts,
  normalizeValidPromotionsResponse,
} from "./api";

function assertEqual(actual: unknown, expected: unknown, label: string) {
  if (actual !== expected) {
    throw new Error(`${label}: expected ${String(expected)}, got ${String(actual)}`);
  }
}

function assertDeepEqual(actual: unknown, expected: unknown, label: string) {
  const actualText = JSON.stringify(actual);
  const expectedText = JSON.stringify(expected);
  if (actualText !== expectedText) {
    throw new Error(`${label}: expected ${expectedText}, got ${actualText}`);
  }
}

assertDeepEqual(
  buildPromotionGridPayload({ storeCode: " S01 " }),
  {
    storeCode: "S01",
    globalSearch: undefined,
    startRow: 0,
    pageSize: 20,
    sortModel: undefined,
  },
  "默认促销列表查询使用后端 grid 协议"
);

assertDeepEqual(
  buildPromotionGridPayload({
    page: 2.7,
    pageSize: 49.9,
    keyword: "  周年庆  ",
    storeCode: " STO01 ",
    sortModel: [{ colId: "priority", sort: "desc" }],
  }),
  {
    storeCode: "STO01",
    globalSearch: "周年庆",
    startRow: 20,
    pageSize: 20,
    sortModel: [{ colId: "priority", sort: "desc" }],
  },
  "促销列表查询会裁剪字段并规范分页"
);

assertDeepEqual(
  buildPromotionGridPayload({ storeCode: "S01", status: "all", hideEnded: true }).status,
  "notended",
  "全部分段隐藏已结束时请求未结束过滤"
);
assertEqual(
  buildPromotionGridPayload({ storeCode: "S01", status: "all", hideEnded: false }).status,
  undefined,
  "全部分段不隐藏已结束时不传状态"
);
assertEqual(
  buildPromotionGridPayload({ storeCode: "S01", hideEnded: true }).status,
  "notended",
  "未指定分段等同全部"
);
assertEqual(
  buildPromotionGridPayload({ storeCode: "S01", status: "ended", hideEnded: true }).status,
  "ended",
  "其他分段不受隐藏已结束影响"
);
assertEqual(
  buildPromotionGridPayload({ storeCode: "S01", status: "active", hideEnded: true }).status,
  "active",
  "生效中分段不受隐藏已结束影响"
);

assertDeepEqual(
  buildPromotionPayload({
    name: "  清仓促销 ",
    storeCode: " STO01 ",
    priority: 7.8,
    products: [
      { productCode: " SKU01 ", unitWeight: 1.8 },
      { productCode: " ", unitWeight: 9 },
      { productCode: "SKU02", unitWeight: "2.5" },
    ],
  }),
  {
    name: "清仓促销",
    description: undefined,
    effectiveStart: "",
    effectiveEnd: "",
    isEnabled: true,
    isExclusive: true,
    priority: 7,
    applyQuantity: 0,
    fixedPrice: 0,
    maxApplicationsPerOrder: undefined,
    products: [
      { productCode: "SKU01", unitWeight: 1 },
      { productCode: "SKU02", unitWeight: 2 },
    ],
    stores: [{ storeCode: "STO01" }],
  },
  "创建和编辑促销时会清洗门店与商品明细"
);

assertDeepEqual(
  buildPromotionCopyPayload({
    sourcePromotionId: " promo-1 ",
    storeCode: " STO09 ",
  }),
  {
    sourcePromotionId: "promo-1",
    storeCode: "STO09",
  },
  "复制到门店请求会裁剪必要字段"
);

const list = normalizePromotionsResponse({
  success: true,
  data: {
    items: [
      {
        Id: 12,
        Name: "总部主推",
        ScopeType: "Headquarters",
        Priority: "4",
        CanEditInStoreScope: 0,
        CanCopyToStore: 1,
        ProductsCount: 2,
        StoresCount: 0,
      },
    ],
    Total: "3",
  },
});

assertEqual(list.items.length, 1, "促销列表归一化保留记录");
assertEqual(list.items[0]?.id, "12", "促销列表归一化会把 id 转成字符串");
assertEqual(list.items[0]?.scopeType, "Headquarters", "促销列表归一化保留适用范围");
assertEqual(list.items[0]?.priority, 4, "促销列表归一化会转优先级");
assertEqual(list.items[0]?.canEditInStoreScope, false, "促销列表归一化会转换编辑权限");
assertEqual(list.items[0]?.canCopyToStore, true, "促销列表归一化会转换复制权限");
assertEqual(list.items[0]?.productsCount, 2, "促销列表归一化保留商品数量");
assertEqual(list.total, 3, "促销列表归一化保留总数");
assertEqual(list.pageNumber, 1, "促销列表归一化使用默认页码");
assertEqual(list.pageSize, 20, "促销列表归一化使用默认页大小");

const detail = normalizePromotionDetail({
  item: {
    id: "promo-9",
    name: "本店搭配购",
    scopeType: "StoreOnly",
    priority: "0",
    canEditInStoreScope: true,
    canCopyToStore: false,
    products: [{ productCode: "SKU09", unitWeight: "3.25" }],
    stores: [{ storeCode: "MEL01" }],
  },
});

assertEqual(detail?.id, "promo-9", "促销详情归一化解包 item 节点");
assertEqual(detail?.scopeType, "StoreOnly", "促销详情归一化保留本店范围");
assertEqual(detail?.priority, 0, "促销详情归一化保留默认优先级");
assertEqual(detail?.products[0]?.productCode, "SKU09", "促销详情归一化保留商品编码");
assertEqual(detail?.products[0]?.unitWeight, 3, "促销详情归一化保留商品权重");

const validPromotions = normalizeValidPromotionsResponse({
  Success: true,
  Data: [
    {
      Id: "promo-active-1",
      Name: "满二件组合价",
      ApplyQuantity: "2",
      FixedPrice: "9.9",
      Priority: "8",
    },
    {
      id: "promo-active-2",
      name: "任选三件",
      applyQuantity: 3,
      fixedPrice: 12,
      priority: 2,
    },
  ],
});

assertEqual(validPromotions.length, 2, "有效商品促销归一化会解包 ApiResponse 数组");
assertEqual(validPromotions[0]?.id, "promo-active-1", "有效商品促销归一化支持 PascalCase");
assertEqual(validPromotions[0]?.applyQuantity, 2, "有效商品促销归一化会转换满足数量");
assertEqual(validPromotions[0]?.fixedPrice, 9.9, "有效商品促销归一化会转换组合价");
assertEqual(validPromotions[1]?.name, "任选三件", "有效商品促销归一化支持 camelCase");

const directValidPromotions = normalizeValidPromotionsResponse([
  {
    id: "promo-direct",
    name: "直接数组活动",
    applyQuantity: 4,
    fixedPrice: 20,
  },
]);

assertEqual(directValidPromotions.length, 1, "有效商品促销归一化支持已解包的直接数组");
assertEqual(directValidPromotions[0]?.fixedPrice, 20, "直接数组组合价保持数值");

// 状态分段：「全部」不传参数（旧后端兼容），其余原样透传；非法值丢弃。
assertEqual(buildPromotionGridPayload({ storeCode: "1033", status: "active" }).status, "active", "状态分段透传");
assertEqual(buildPromotionGridPayload({ storeCode: "1033", status: "all" }).status, undefined, "全部不传状态");
assertEqual(
  buildPromotionGridPayload({ storeCode: "1033", status: "bogus" as never }).status,
  undefined,
  "非法状态丢弃",
);

const counts = normalizeStatusCounts({ Active: 12, upcoming: "3", disabled: -1, ended: null, total: 20 });
assertEqual(counts.active, 12, "数量兼容大写字段");
assertEqual(counts.upcoming, 3, "数量兼容字符串");
assertEqual(counts.disabled, 0, "负数归零");
assertEqual(counts.ended, 0, "空值归零");
assertEqual(counts.total, 20, "合计");
