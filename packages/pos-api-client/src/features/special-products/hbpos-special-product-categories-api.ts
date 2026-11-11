import { HbposApiError, unwrapHbposEnvelope, type HbposEnvelope, type HbposTransport } from "../../transport";
import type {
  SpecialProductCategoriesRemotePort,
  SpecialProductCategory,
  SpecialProductCategoryAssignment,
  SpecialProductCategorySnapshot,
} from "@hb/pos-domain/core/contracts/special-product-categories";
import type { components } from "../../openapi";

type GeneratedSnapshot =
  components["schemas"]["CatalogSpecialProductCategoriesResponse"];
type GeneratedCategory = components["schemas"]["SpecialProductCategoryDto"];
type GeneratedAssignment =
  components["schemas"]["SpecialProductCategoryAssignmentDto"];
type GeneratedAssignRequest =
  components["schemas"]["CatalogSpecialProductCategoryAssignRequest"];
type GeneratedOrderRequest =
  components["schemas"]["CatalogSpecialProductCategoryOrderRequest"];

/** 一个分类下的商品数量上限：与一家店特殊商品「千级以内」的量级对齐并留足余量，防止异常入参。 */
const MAX_ORDER_PRODUCT_CODES = 10_000;

/**
 * 分店级特殊商品分类（POS API 三个接口）。
 * 三个接口成功时都返回同一种整店快照，这里统一校验后交给调用方整体覆盖本地。
 * 409 `SPECIAL_PRODUCT_CATEGORY_ORDER_CONFLICT` 等业务错误由 transport 抛成带 `code` 的
 * HbposApiError，原样透传给 presenter 按稳定错误码处理。
 */
export class HbposSpecialProductCategoriesApi
  implements SpecialProductCategoriesRemotePort
{
  public constructor(private readonly transport: HbposTransport) {}

  public async getCategories(
    input: Readonly<{ storeCode: string }>,
  ): Promise<SpecialProductCategorySnapshot> {
    const storeCode = requestIdentity(input.storeCode, "storeCode");
    const response = await this.transport.request<
      HbposEnvelope<GeneratedSnapshot>
    >({
      method: "GET",
      url: "/api/v1/catalog/special-products/categories",
      params: { storeCode },
    });
    return normalizeSnapshot(unwrapHbposEnvelope(response.data), storeCode);
  }

  public async assign(
    input: Readonly<{
      storeCode: string;
      productCode: string;
      categoryId: string | null;
    }>,
  ): Promise<SpecialProductCategorySnapshot> {
    const storeCode = requestIdentity(input.storeCode, "storeCode");
    const productCode = requestIdentity(input.productCode, "productCode");
    // null 明确表示「移出分类」，undefined / 空串一律视为非法入参，避免误清空归属。
    const categoryId =
      input.categoryId === null
        ? null
        : requestIdentity(input.categoryId, "categoryId");
    const request: GeneratedAssignRequest = {
      storeCode,
      productCode,
      categoryId,
    };
    const response = await this.transport.request<
      HbposEnvelope<GeneratedSnapshot>
    >({
      method: "POST",
      url: "/api/v1/catalog/special-products/categories/assign",
      data: request,
    });
    return normalizeSnapshot(unwrapHbposEnvelope(response.data), storeCode);
  }

  public async setOrder(
    input: Readonly<{
      storeCode: string;
      categoryId: string;
      productCodes: readonly string[];
    }>,
  ): Promise<SpecialProductCategorySnapshot> {
    const storeCode = requestIdentity(input.storeCode, "storeCode");
    const categoryId = requestIdentity(input.categoryId, "categoryId");
    const productCodes = requestProductCodes(input.productCodes);
    const request: GeneratedOrderRequest = {
      storeCode,
      categoryId,
      productCodes,
    };
    const response = await this.transport.request<
      HbposEnvelope<GeneratedSnapshot>
    >({
      method: "POST",
      url: "/api/v1/catalog/special-products/categories/order",
      data: request,
    });
    return normalizeSnapshot(unwrapHbposEnvelope(response.data), storeCode);
  }
}

function normalizeSnapshot(
  snapshot: GeneratedSnapshot,
  expectedStoreCode: string,
): SpecialProductCategorySnapshot {
  // 门店串号属于数据损坏信号，必须拒绝而不是覆盖本机。
  if (
    responseIdentity(snapshot.storeCode, "response.storeCode") !==
    expectedStoreCode
  ) {
    throw invalidResponse("response.storeCode");
  }
  const generatedAt = responseTimestamp(
    snapshot.generatedAt,
    "response.generatedAt",
  );
  const revision = responseIdentity(snapshot.revision, "response.revision");

  // 服务端口径：SortOrder 升序，并列再按 categoryId（Guid）升序；这里按同一规则稳定排序，不用名称。
  const categories = responseArray(
    snapshot.categories,
    "response.categories",
  )
    .map(normalizeCategory)
    .sort(
      (left, right) =>
        left.sortOrder - right.sortOrder ||
        compareOrdinal(left.categoryId, right.categoryId),
    );
  const categoryIds = new Set<string>();
  for (const category of categories) {
    if (categoryIds.has(category.categoryId)) {
      throw invalidResponse("category.categoryId");
    }
    categoryIds.add(category.categoryId);
  }

  const assignments = responseArray(
    snapshot.assignments,
    "response.assignments",
  ).map(normalizeAssignment);
  const productCodes = new Set<string>();
  for (const assignment of assignments) {
    // 一个商品在一家店只属于一个分类；归属必须指向本次返回的分类。
    if (productCodes.has(assignment.productCode)) {
      throw invalidResponse("assignment.productCode");
    }
    productCodes.add(assignment.productCode);
    if (!categoryIds.has(assignment.categoryId)) {
      throw invalidResponse("assignment.categoryId");
    }
  }

  return Object.freeze({
    storeCode: expectedStoreCode,
    generatedAt,
    revision,
    categories: Object.freeze(categories),
    assignments: Object.freeze(assignments),
  });
}

/** 按字符码位比较（不受语言环境影响），与服务端对 Guid 文本的升序一致。 */
function compareOrdinal(left: string, right: string): number {
  return left < right ? -1 : left > right ? 1 : 0;
}

function normalizeCategory(item: GeneratedCategory): SpecialProductCategory {
  return Object.freeze({
    categoryId: responseIdentity(item?.categoryId, "category.categoryId"),
    name: responseIdentity(item?.name, "category.name"),
    color: responseColor(item?.color),
    sortOrder: responseNonNegativeInteger(
      item?.sortOrder,
      "category.sortOrder",
    ),
  });
}

function normalizeAssignment(
  item: GeneratedAssignment,
): SpecialProductCategoryAssignment {
  return Object.freeze({
    productCode: responseIdentity(item?.productCode, "assignment.productCode"),
    categoryId: responseIdentity(item?.categoryId, "assignment.categoryId"),
    sortOrder: responseNonNegativeInteger(
      item?.sortOrder,
      "assignment.sortOrder",
    ),
  });
}

function requestIdentity(value: unknown, field: string): string {
  if (typeof value !== "string") throw invalidRequest(field);
  const normalized = value.trim();
  if (
    normalized.length === 0 ||
    normalized.length > 128 ||
    /[\u0000-\u001f\u007f]/u.test(normalized)
  ) {
    throw invalidRequest(field);
  }
  return normalized;
}

function requestProductCodes(value: unknown): string[] {
  if (
    !Array.isArray(value) ||
    value.length === 0 ||
    value.length > MAX_ORDER_PRODUCT_CODES
  ) {
    throw invalidRequest("productCodes");
  }
  const codes = value.map((entry) => requestIdentity(entry, "productCodes"));
  // 排列里出现重复商品必然是调用方 bug，直接拒绝，不让服务端去判冲突。
  if (new Set(codes).size !== codes.length) {
    throw invalidRequest("productCodes");
  }
  return codes;
}

function responseIdentity(value: unknown, field: string): string {
  const text = responseText(value, field);
  if (text.length > 128) throw invalidResponse(field);
  return text;
}

function responseText(value: unknown, field: string): string {
  if (
    typeof value !== "string" ||
    value.length === 0 ||
    value.length > 4_096 ||
    /[\u0000-\u001f\u007f]/u.test(value)
  ) {
    throw invalidResponse(field);
  }
  return value;
}

/** 分类颜色只接受 #RRGGBB；缺省 / 空串视为没有颜色，不合规的值拒绝整份快照。 */
function responseColor(value: unknown): string | null {
  if (value === null || value === undefined || value === "") return null;
  if (typeof value !== "string" || !/^#[0-9a-fA-F]{6}$/u.test(value)) {
    throw invalidResponse("category.color");
  }
  return value;
}

function responseTimestamp(value: unknown, field: string): string {
  const text = responseText(value, field);
  if (Number.isNaN(new Date(text).valueOf())) {
    throw invalidResponse(field);
  }
  return text;
}

function responseArray<T>(
  value: readonly T[] | null | undefined,
  field: string,
): readonly T[] {
  if (!Array.isArray(value)) throw invalidResponse(field);
  return value;
}

function responseNonNegativeInteger(value: unknown, field: string): number {
  if (!Number.isSafeInteger(value) || Number(value) < 0) {
    throw invalidResponse(field);
  }
  return Number(value);
}

function invalidRequest(field: string): HbposApiError {
  return new HbposApiError(
    `Special product categories request field is invalid: ${field}.`,
    {
      kind: "envelope",
      code: "SPECIAL_PRODUCT_CATEGORIES_REQUEST_INVALID",
    },
  );
}

function invalidResponse(field: string): HbposApiError {
  return new HbposApiError(
    `Special product categories response field is invalid: ${field}.`,
    {
      kind: "envelope",
      code: "SPECIAL_PRODUCT_CATEGORIES_RESPONSE_INVALID",
    },
  );
}
