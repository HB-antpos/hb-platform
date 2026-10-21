import {
  getDetailEnglishName,
  getDetailGuid,
  getDetailProductName,
  getDetailRealtimeImportPrice,
  getDetailRealtimeRetailPrice,
  trimToUndefined,
} from "./query";
import type {
  ContainerDetail,
  ContainerDetailConcurrentConflict,
  ContainerDetailSaveValidationError,
  UpdateContainerDetailRequest,
} from "./types";

export interface ContainerDetailEditForm {
  productName: string;
  englishName: string;
  domesticPrice: string;
  /** 本次进口价（货柜明细自己的价格，新商品建档数据）：只有新商品可改。 */
  importPrice: string;
  /** 本次零售价（货柜明细自己的价格，新商品建档数据）：只有新商品可改。 */
  oemPrice: string;
  /** 已有进口价（仓库实时进货价）：只有已有商品可改，保存时回写仓库表 / 商品主档 / 分店进货价。 */
  warehouseImportPrice: string;
  /** 已有零售价（仓库实时零售价）：只有已有商品可改，保存时回写仓库表 / 商品主档，并按开关联动分店价。 */
  warehouseOemPrice: string;
  floatRate: string;
  containerQuantity: string;
  middlePackQuantity: string;
  isActive: boolean;
}

function numberInput(value?: number | null) {
  return value == null || !Number.isFinite(value) ? "" : String(value);
}

function parseOptionalNumber(value: string) {
  const trimmed = value.trim();
  if (!trimmed) return undefined;
  const parsed = Number(trimmed);
  return Number.isFinite(parsed) ? parsed : Number.NaN;
}

/** 与 retail-price-sync 一致的「新商品」判定：新商品还没有仓库记录，只能改本次价，不能改已有价。 */
export function isNewProductDetail(detail: ContainerDetail) {
  return Boolean(detail.是否新商品 ?? detail.warehouseIsActive === false);
}

export function getContainerDetailServerFieldTokens(detail: ContainerDetail) {
  return detail.serverFieldTokens ?? detail.ServerFieldTokens ?? {};
}

export function buildContainerDetailEditForm(detail: ContainerDetail): ContainerDetailEditForm {
  return {
    productName: getDetailProductName(detail),
    englishName: getDetailEnglishName(detail),
    domesticPrice: numberInput(detail.国内价格),
    importPrice: numberInput(detail.进口价格),
    oemPrice: numberInput(detail.贴牌价格),
    warehouseImportPrice: numberInput(getDetailRealtimeImportPrice(detail)),
    warehouseOemPrice: numberInput(getDetailRealtimeRetailPrice(detail)),
    floatRate: numberInput(detail.调整浮率),
    containerQuantity: numberInput(detail.装柜数量),
    middlePackQuantity: numberInput(detail.中包数),
    isActive: detail.IsActive ?? detail.warehouseIsActive ?? true,
  };
}

/** 本地校验错误带 code：英文界面按 code 显示译文，中文界面仍显示 message 原文。 */
function editPayloadError(code: "CONTAINER_EDIT_INVALID_NUMBER" | "CONTAINER_EDIT_NO_CHANGES", message: string) {
  return Object.assign(new Error(message), { code });
}

export function buildContainerDetailEditPayload(
  detail: ContainerDetail,
  form: ContainerDetailEditForm,
  overrideAcknowledgements?: Record<string, string>,
): UpdateContainerDetailRequest {
  const payload: UpdateContainerDetailRequest = { hguid: getDetailGuid(detail) };
  const changedFields: string[] = [];
  const addNumber = (
    field: "国内价格" | "进口价格" | "贴牌价格" | "调整浮率" | "装柜数量" | "中包数",
    input: string,
    current: number | undefined,
  ) => {
    const value = parseOptionalNumber(input);
    if (Number.isNaN(value)) throw editPayloadError("CONTAINER_EDIT_INVALID_NUMBER", "编辑字段存在无效数字");
    if (value !== undefined && value !== current) {
      payload[field] = value;
      changedFields.push(field);
    }
  };
  const productName = trimToUndefined(form.productName);
  if (productName !== undefined && productName !== trimToUndefined(getDetailProductName(detail))) {
    payload.商品名称 = productName;
    changedFields.push("商品名称");
  }
  const englishName = trimToUndefined(form.englishName);
  if (englishName !== trimToUndefined(getDetailEnglishName(detail))) {
    if (englishName === undefined) payload.ClearEnglishName = true;
    else payload.英文名称 = englishName;
    changedFields.push("英文名称");
  }
  addNumber("国内价格", form.domesticPrice, detail.国内价格);
  // 本次价只有新商品可改；已有商品这两个输入框是禁用的，值与明细一致，不会进请求。
  if (isNewProductDetail(detail)) {
    addNumber("进口价格", form.importPrice, detail.进口价格);
    addNumber("贴牌价格", form.oemPrice, detail.贴牌价格);
  }
  // 已有价只有已有商品可改；它们不是货柜明细字段，没有字段令牌，改用提交时看到的仓库价做并发基线。
  if (!isNewProductDetail(detail)) {
    const addWarehousePrice = (
      field: "仓库进口价" | "仓库零售价",
      input: string,
      current: number | undefined,
      expectedField: "expectedWarehouseImportPrice" | "expectedWarehouseOemPrice",
    ) => {
      const value = parseOptionalNumber(input);
      if (Number.isNaN(value)) throw editPayloadError("CONTAINER_EDIT_INVALID_NUMBER", "编辑字段存在无效数字");
      if (value !== undefined && value !== current) {
        payload[field] = value;
        if (current !== undefined) payload[expectedField] = current;
        changedFields.push(field);
      }
    };
    addWarehousePrice("仓库进口价", form.warehouseImportPrice, getDetailRealtimeImportPrice(detail), "expectedWarehouseImportPrice");
    addWarehousePrice("仓库零售价", form.warehouseOemPrice, getDetailRealtimeRetailPrice(detail), "expectedWarehouseOemPrice");
  }
  addNumber("调整浮率", form.floatRate, detail.调整浮率);
  addNumber("装柜数量", form.containerQuantity, detail.装柜数量);
  addNumber("中包数", form.middlePackQuantity, detail.中包数);
  const currentActive = detail.IsActive ?? detail.warehouseIsActive ?? true;
  if (form.isActive !== currentActive) {
    payload.IsActive = form.isActive;
    changedFields.push("IsActive");
  }
  if (!changedFields.length) throw editPayloadError("CONTAINER_EDIT_NO_CHANGES", "没有需要保存的修改");
  const tokens = getContainerDetailServerFieldTokens(detail);
  payload.expectedServerFieldTokens = Object.fromEntries(
    changedFields.flatMap((field) => typeof tokens[field] === "string" ? [[field, tokens[field]]] : []),
  );
  if (overrideAcknowledgements && Object.keys(overrideAcknowledgements).length) {
    payload.overrideAcknowledgements = overrideAcknowledgements;
  }
  return payload;
}

function serverNumber(value: unknown) {
  return typeof value === "number" && Number.isFinite(value) ? value : undefined;
}

function applyServerValueToForm(
  form: ContainerDetailEditForm,
  conflict: ContainerDetailConcurrentConflict,
): ContainerDetailEditForm {
  const value = conflict.serverValue;
  if (conflict.field === "商品名称") return { ...form, productName: value == null ? "" : String(value) };
  if (conflict.field === "英文名称") return { ...form, englishName: value == null ? "" : String(value) };
  if (conflict.field === "国内价格") return { ...form, domesticPrice: numberInput(serverNumber(value)) };
  if (conflict.field === "进口价格") return { ...form, importPrice: numberInput(serverNumber(value)) };
  if (conflict.field === "贴牌价格") return { ...form, oemPrice: numberInput(serverNumber(value)) };
  if (conflict.field === "调整浮率") return { ...form, floatRate: numberInput(serverNumber(value)) };
  if (conflict.field === "装柜数量") return { ...form, containerQuantity: numberInput(serverNumber(value)) };
  if (conflict.field === "中包数") return { ...form, middlePackQuantity: numberInput(serverNumber(value)) };
  if (conflict.field === "IsActive") return { ...form, isActive: Boolean(value) };
  return form;
}

function applyServerValueToDetail(
  detail: ContainerDetail,
  conflict: ContainerDetailConcurrentConflict,
): ContainerDetail {
  const value = conflict.serverValue;
  const productInfo = detail.商品信息;
  const withTokens = (next: ContainerDetail) => ({
    ...next,
    serverFieldTokens: {
      ...getContainerDetailServerFieldTokens(next),
      [conflict.field]: conflict.currentServerFieldToken,
    },
  });
  if (conflict.field === "商品名称") return withTokens({
    ...detail,
    商品名称: value == null ? "" : String(value),
    商品信息: productInfo ? { ...productInfo, 商品名称: value == null ? "" : String(value) } : productInfo,
  });
  if (conflict.field === "英文名称") return withTokens({
    ...detail,
    英文名称: value == null ? "" : String(value),
    商品信息: productInfo ? { ...productInfo, 英文名称: value == null ? "" : String(value) } : productInfo,
  });
  if (conflict.field === "国内价格") return withTokens({ ...detail, 国内价格: serverNumber(value) });
  if (conflict.field === "进口价格") return withTokens({ ...detail, 进口价格: serverNumber(value) });
  // 「本次零售价」只是明细自己的价格，不再绑定仓库实时价，冲突只更新明细字段。
  if (conflict.field === "贴牌价格") return withTokens({ ...detail, 贴牌价格: serverNumber(value) });
  if (conflict.field === "调整浮率") return withTokens({ ...detail, 调整浮率: serverNumber(value) });
  if (conflict.field === "装柜数量") return withTokens({ ...detail, 装柜数量: serverNumber(value) });
  if (conflict.field === "中包数") return withTokens({ ...detail, 中包数: serverNumber(value) });
  if (conflict.field === "IsActive") return withTokens({ ...detail, IsActive: Boolean(value), warehouseIsActive: Boolean(value) });
  return withTokens(detail);
}

export function getContainerDetailEditableFieldValue(detail: ContainerDetail, field: string): unknown {
  if (field === "商品名称") return getDetailProductName(detail);
  if (field === "英文名称") return getDetailEnglishName(detail);
  if (field === "国内价格") return detail.国内价格;
  if (field === "进口价格") return detail.进口价格;
  if (field === "贴牌价格") return detail.贴牌价格;
  if (field === "调整浮率") return detail.调整浮率;
  if (field === "装柜数量") return detail.装柜数量;
  if (field === "中包数") return detail.中包数;
  if (field === "IsActive") return detail.IsActive ?? detail.warehouseIsActive ?? true;
  return undefined;
}

export function applyContainerDetailServerConflicts(
  detail: ContainerDetail,
  form: ContainerDetailEditForm,
  conflicts: ContainerDetailConcurrentConflict[],
) {
  return conflicts.reduce((current, conflict) => ({
    detail: applyServerValueToDetail(current.detail, conflict),
    form: applyServerValueToForm(current.form, conflict),
  }), { detail, form });
}

export function reconcileContainerDetailPartialSave({
  baseline,
  form,
  submittedPayload,
  latest,
  validationErrors,
  conflicts,
}: {
  baseline: ContainerDetail;
  form: ContainerDetailEditForm;
  submittedPayload: UpdateContainerDetailRequest;
  latest: ContainerDetail | null;
  validationErrors: ContainerDetailSaveValidationError[];
  conflicts: ContainerDetailConcurrentConflict[];
}) {
  const submittedFields = Object.keys(submittedPayload.expectedServerFieldTokens ?? {});
  const hasRowError = validationErrors.some((item) => item.field === "*");
  const retainedFields = new Set([
    ...validationErrors.filter((item) => item.field !== "*").map((item) => item.field),
    ...conflicts.map((item) => item.field),
  ]);
  const savedFields = latest && !hasRowError
    ? submittedFields.filter((field) => !retainedFields.has(field))
    : [];
  const latestTokens = latest ? getContainerDetailServerFieldTokens(latest) : {};
  const savedAsServerValues: ContainerDetailConcurrentConflict[] = latest
    ? savedFields.flatMap((field) => {
      const token = latestTokens[field];
      if (!token) return [];
      return [{
        hguid: getDetailGuid(baseline).trim(),
        field,
        code: "CONCURRENT_FIELD_UPDATE" as const,
        message: "已保存",
        serverValue: getContainerDetailEditableFieldValue(latest, field),
        submittedValue: undefined,
        currentServerFieldToken: token,
      }];
    })
    : [];
  const applied = applyContainerDetailServerConflicts(baseline, form, savedAsServerValues);
  // 已有价（仓库进口价 / 仓库零售价）不走字段令牌，用提交时看到的仓库价做并发基线。
  // 保存成功后基线必须刷成服务器最新值，否则同一弹窗里再次保存会拿旧基线被后端当成「被他人修改」；
  // 被拒绝（仓库价已变）的字段同样刷新基线，让用户看到提示后确认覆盖时能再次提交。
  const submittedWarehouseFields = (["仓库进口价", "仓库零售价"] as const).filter((field) => submittedPayload[field] !== undefined);
  const warehouseBaselineRefreshed = Boolean(latest) && !hasRowError && submittedWarehouseFields.length > 0;
  let detail = applied.detail;
  if (latest && warehouseBaselineRefreshed) {
    if (submittedWarehouseFields.includes("仓库进口价")) {
      const next = getDetailRealtimeImportPrice(latest);
      detail = { ...detail, warehouseImportPrice: next, WarehouseImportPrice: next };
    }
    if (submittedWarehouseFields.includes("仓库零售价")) {
      const next = getDetailRealtimeRetailPrice(latest);
      detail = { ...detail, warehouseOEMPrice: next, WarehouseOEMPrice: next };
    }
  }
  return {
    ...applied,
    detail,
    savedFields: new Set(savedAsServerValues.map((item) => item.field)),
    retainedFields,
    warehouseBaselineRefreshed,
  };
}

export function isCurrentContainerDetailEditSession({
  expectedSessionId,
  expectedHguid,
  currentSessionId,
  currentHguid,
}: {
  expectedSessionId: string;
  expectedHguid: string;
  currentSessionId: string;
  currentHguid: string;
}) {
  return Boolean(expectedSessionId)
    && expectedSessionId === currentSessionId
    && expectedHguid === currentHguid;
}
