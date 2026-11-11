import assert from "node:assert/strict";
import test from "node:test";

import { HbposSpecialProductCategoriesApi } from "./hbpos-special-product-categories-api";

import {
  hasSpecialProductCategoryErrorCode,
  SPECIAL_PRODUCT_CATEGORY_ORDER_CONFLICT_CODE,
} from "@hb/pos-domain/core/contracts/special-product-categories";
import { HbposApiError } from "../../transport";

import type { HbposTransport, HbposTransportRequest } from "../../transport";
import type { components } from "../../openapi";

type GeneratedSnapshot =
  components["schemas"]["CatalogSpecialProductCategoriesResponse"];

test("getCategories 校验并映射整店快照，请求只带 storeCode", async () => {
  const transport = new RecordingTransport([envelope(snapshot())]);

  const result = await new HbposSpecialProductCategoriesApi(
    transport,
  ).getCategories({ storeCode: " S1 " });

  assert.deepEqual(transport.requests, [
    {
      method: "GET",
      params: { storeCode: "S1" },
      url: "/api/v1/catalog/special-products/categories",
    },
  ]);
  assert.equal(result.storeCode, "S1");
  assert.equal(result.revision, "638000000000000000");
  assert.deepEqual(
    result.categories.map((category) => [
      category.categoryId,
      category.name,
      category.color,
      category.sortOrder,
    ]),
    [
      ["C1", "Balloons", "#1D6FB8", 0],
      ["C2", "Cards", null, 1],
    ],
  );
  assert.deepEqual(
    result.assignments.map((assignment) => [
      assignment.productCode,
      assignment.categoryId,
      assignment.sortOrder,
    ]),
    [
      ["P1", "C1", 0],
      ["P2", "C1", 1],
    ],
  );
});

test("分类并列 SortOrder 时按 categoryId 升序（不看名称），与服务端口径一致", async () => {
  const transport = new RecordingTransport([
    envelope(
      snapshot({
        assignments: [],
        categories: [
          { categoryId: "b-guid", color: null, name: "Aaa", sortOrder: 1 },
          { categoryId: "c-guid", color: null, name: "Bbb", sortOrder: 0 },
          { categoryId: "a-guid", color: null, name: "Zzz", sortOrder: 1 },
        ],
      }),
    ),
  ]);

  const result = await new HbposSpecialProductCategoriesApi(
    transport,
  ).getCategories({ storeCode: "S1" });

  assert.deepEqual(
    result.categories.map((item) => item.categoryId),
    ["c-guid", "a-guid", "b-guid"],
  );
});

test("没建任何分类时返回空数组（revision 为 0 也合法）", async () => {
  const transport = new RecordingTransport([
    envelope(snapshot({ assignments: [], categories: [], revision: "0" })),
  ]);

  const result = await new HbposSpecialProductCategoriesApi(
    transport,
  ).getCategories({ storeCode: "S1" });

  assert.equal(result.categories.length, 0);
  assert.equal(result.assignments.length, 0);
});

test("assign 发送 POST 请求，categoryId 为 null 表示回到未分类", async () => {
  const transport = new RecordingTransport([
    envelope(snapshot()),
    envelope(snapshot()),
  ]);
  const api = new HbposSpecialProductCategoriesApi(transport);

  await api.assign({ categoryId: "C2", productCode: "P1", storeCode: "S1" });
  await api.assign({ categoryId: null, productCode: "P1", storeCode: "S1" });

  assert.deepEqual(transport.requests, [
    {
      data: { categoryId: "C2", productCode: "P1", storeCode: "S1" },
      method: "POST",
      url: "/api/v1/catalog/special-products/categories/assign",
    },
    {
      data: { categoryId: null, productCode: "P1", storeCode: "S1" },
      method: "POST",
      url: "/api/v1/catalog/special-products/categories/assign",
    },
  ]);
});

test("setOrder 发送完整排列并返回最新快照", async () => {
  const transport = new RecordingTransport([envelope(snapshot())]);

  const result = await new HbposSpecialProductCategoriesApi(
    transport,
  ).setOrder({
    categoryId: "C1",
    productCodes: ["P2", "P1"],
    storeCode: "S1",
  });

  assert.deepEqual(transport.requests, [
    {
      data: { categoryId: "C1", productCodes: ["P2", "P1"], storeCode: "S1" },
      method: "POST",
      url: "/api/v1/catalog/special-products/categories/order",
    },
  ]);
  assert.equal(result.categories.length, 2);
});

test("请求参数非法时不发请求并抛稳定错误码", async () => {
  const transport = new RecordingTransport([]);
  const api = new HbposSpecialProductCategoriesApi(transport);
  const invalidRequest = (field: string) => (error: unknown) =>
    error instanceof HbposApiError &&
    error.code === "SPECIAL_PRODUCT_CATEGORIES_REQUEST_INVALID" &&
    error.message.includes(field);

  await assert.rejects(
    () => api.getCategories({ storeCode: "  " }),
    invalidRequest("storeCode"),
  );
  await assert.rejects(
    () =>
      api.assign({
        categoryId: "" as unknown as string,
        productCode: "P1",
        storeCode: "S1",
      }),
    invalidRequest("categoryId"),
  );
  await assert.rejects(
    () =>
      api.assign({
        categoryId: undefined as unknown as string,
        productCode: "P1",
        storeCode: "S1",
      }),
    invalidRequest("categoryId"),
  );
  for (const productCodes of [
    [],
    ["P1", "P1"],
    ["P1", " "],
    "P1" as unknown as string[],
  ]) {
    await assert.rejects(
      () =>
        api.setOrder({ categoryId: "C1", productCodes, storeCode: "S1" }),
      invalidRequest("productCodes"),
    );
  }
  assert.equal(transport.requests.length, 0);
});

test("响应越过门店范围或结构非法时拒绝整份快照", async () => {
  const cases: readonly (readonly [string, GeneratedSnapshot])[] = [
    ["response.storeCode", snapshot({ storeCode: "S2" })],
    ["response.generatedAt", snapshot({ generatedAt: "not-a-date" })],
    ["response.revision", snapshot({ revision: "" })],
    ["response.categories", snapshot({ categories: null })],
    ["response.assignments", snapshot({ assignments: null })],
    [
      "category.categoryId",
      snapshot({
        categories: [
          { categoryId: "C1", color: null, name: "A", sortOrder: 0 },
          { categoryId: "C1", color: null, name: "B", sortOrder: 1 },
        ],
      }),
    ],
    [
      "category.color",
      snapshot({
        categories: [
          { categoryId: "C1", color: "red", name: "A", sortOrder: 0 },
        ],
        assignments: [],
      }),
    ],
    [
      "category.sortOrder",
      snapshot({
        categories: [
          { categoryId: "C1", color: null, name: "A", sortOrder: -1 },
        ],
        assignments: [],
      }),
    ],
    [
      "assignment.productCode",
      snapshot({
        assignments: [
          { categoryId: "C1", productCode: "P1", sortOrder: 0 },
          { categoryId: "C2", productCode: "P1", sortOrder: 0 },
        ],
      }),
    ],
    [
      "assignment.categoryId",
      snapshot({
        assignments: [
          { categoryId: "MISSING", productCode: "P1", sortOrder: 0 },
        ],
      }),
    ],
  ];

  for (const [field, response] of cases) {
    const transport = new RecordingTransport([envelope(response)]);
    await assert.rejects(
      () =>
        new HbposSpecialProductCategoriesApi(transport).getCategories({
          storeCode: "S1",
        }),
      (error: unknown) =>
        error instanceof HbposApiError &&
        error.code === "SPECIAL_PRODUCT_CATEGORIES_RESPONSE_INVALID" &&
        error.message.includes(field),
      field,
    );
  }
});

test("409 顺序冲突的错误码原样透传，可用领域函数识别", async () => {
  const conflict = new HbposApiError("order conflict", {
    apiEnvelope: true,
    code: SPECIAL_PRODUCT_CATEGORY_ORDER_CONFLICT_CODE,
    kind: "http",
    status: 409,
  });
  const transport = new RecordingTransport([conflict]);

  await assert.rejects(
    () =>
      new HbposSpecialProductCategoriesApi(transport).setOrder({
        categoryId: "C1",
        productCodes: ["P1"],
        storeCode: "S1",
      }),
    (error: unknown) =>
      hasSpecialProductCategoryErrorCode(
        error,
        SPECIAL_PRODUCT_CATEGORY_ORDER_CONFLICT_CODE,
      ),
  );
  assert.equal(
    hasSpecialProductCategoryErrorCode(new Error("x"), "ANY"),
    false,
  );
  assert.equal(hasSpecialProductCategoryErrorCode(null, "ANY"), false);
});

test("信封 success=false 时抛带错误码的 envelope 错误", async () => {
  const transport = new RecordingTransport([
    {
      errorCode: "SPECIAL_PRODUCT_NOT_SPECIAL",
      message: "not special",
      success: false,
    },
  ]);

  await assert.rejects(
    () =>
      new HbposSpecialProductCategoriesApi(transport).assign({
        categoryId: "C1",
        productCode: "P9",
        storeCode: "S1",
      }),
    (error: unknown) =>
      hasSpecialProductCategoryErrorCode(
        error,
        "SPECIAL_PRODUCT_NOT_SPECIAL",
      ),
  );
});

class RecordingTransport implements HbposTransport {
  public readonly requests: HbposTransportRequest[] = [];

  public constructor(private readonly responses: unknown[]) {}

  public async request<T>(request: HbposTransportRequest) {
    this.requests.push(request);
    const response = this.responses.shift();
    if (response === undefined) {
      throw new Error("Unexpected transport request.");
    }
    // 模拟 transport 对非 2xx 抛 HbposApiError 的行为。
    if (response instanceof Error) throw response;
    return { data: response as T, status: 200 };
  }
}

function snapshot(
  overrides: Partial<GeneratedSnapshot> = {},
): GeneratedSnapshot {
  return {
    assignments: [
      { categoryId: "C1", productCode: "P1", sortOrder: 0 },
      { categoryId: "C1", productCode: "P2", sortOrder: 1 },
    ],
    categories: [
      { categoryId: "C1", color: "#1D6FB8", name: "Balloons", sortOrder: 0 },
      { categoryId: "C2", color: null, name: "Cards", sortOrder: 1 },
    ],
    generatedAt: "2026-11-11T01:02:03.000Z",
    revision: "638000000000000000",
    storeCode: "S1",
    ...overrides,
  };
}

function envelope<T>(data: T) {
  return { data, success: true };
}
