import assert from "node:assert/strict";
import { existsSync, readFileSync } from "node:fs";

const appRoot = new URL("../", import.meta.url);
const snapshotPath = new URL("openapi/hbpos.openapi.json", appRoot);
const generatedPath = new URL("src/generated/hbpos/schema.d.ts", appRoot);

assert.equal(existsSync(snapshotPath), true, "缺少由 Hbpos.Api 测试宿主导出的 OpenAPI 快照");
assert.equal(existsSync(generatedPath), true, "缺少由 openapi-typescript 生成的 DTO 类型");

const document = JSON.parse(readFileSync(snapshotPath, "utf8"));
assert.equal(Object.keys(document.paths ?? {}).length, 116, "共享 OpenAPI 必须锁定当前 116 条路径");
assert.equal(
  Object.keys(document.components?.schemas ?? {}).length,
  278,
  "共享 OpenAPI 必须锁定当前 278 个 schema",
);

for (const route of [
  "/api/v1/devices/register",
  "/api/v1/devices/verify",
  "/api/v1/app-updates/pos-ipad",
  "/api/v1/app-updates/pos-handheld",
  "/api/v1/app-updates/pos-handheld/ota",
  "/api/v1/orders/sync",
  "/api/v1/catalog/sellable-items",
  "/api/v1/catalog/sellable-items/page",
  "/api/v1/catalog/sync-plan",
  "/api/v1/catalog/delta/page",
  "/api/v1/catalog/sellable-items/code-conflicts",
  "/api/v1/cashiers/barcode-login",
  "/api/v1/square/checkouts",
  "/api/v1/linkly/cloud-backend/terminals",
  "/api/v1/linkly/cloud-backend/terminal-selection",
  "/api/v1/linkly/cloud-backend/terminals/{terminalId}/pair",
  "/api/v1/linkly/cloud-backend/terminals/{terminalId}/connection-test",
  "/api/v1/linkly/cloud-backend/terminals/{terminalId}/assignment",
  "/api/v1/linkly/cloud-backend/transactions",
  "/api/v1/vouchers/lock",
  "/api/v1/installments",
  "/api/v1/installments/history",
  "/api/v1/installments/capabilities",
  "/api/v1/installments/{installmentGuid}/repayment-claims",
  "/api/v1/installments/{installmentGuid}/repayment-claims/{operationGuid}",
  "/api/v1/installments/{installmentGuid}/repayment-claims/{operationGuid}/begin-provider",
  "/api/v1/installments/{installmentGuid}/repayment-claims/{operationGuid}/prepare-provider",
  "/api/v1/installments/{installmentGuid}/repayment-claims/{operationGuid}/resolve",
  "/api/v1/installments/{installmentGuid}/repayment-claims/{operationGuid}/commit",
  "/api/v1/installments/{installmentGuid}/cancel-claims",
  "/api/v1/installments/{installmentGuid}/cancel-claims/{operationGuid}",
  "/api/v1/installments/{installmentGuid}/cancel-claims/{operationGuid}/begin-refund",
  "/api/v1/installments/{installmentGuid}/cancel-claims/{operationGuid}/resolve",
  "/api/v1/installments/{installmentGuid}/cancel-claims/{operationGuid}/commit",
  "/api/v1/operation-audits",
  "/api/v1/operation-audits/{eventId}",
  "/api/v1/operation-audits/batch"
]) {
  assert.ok(document.paths?.[route], `OpenAPI 快照缺少 POS 路由：${route}`);
}

const activationSchemas = [
  "DeviceActivationCodePreviewRequest",
  "DeviceActivationCodePreviewResponse",
  "DeviceActivationCodePreviewResponseApiResult",
  "DeviceActivationCodeRebindRequest",
  "DeviceActivationCodeRedeemRequest",
  "DeviceActivationCodeRedeemResponse",
  "DeviceActivationCodeRedeemResponseApiResult",
];
for (const schema of activationSchemas) {
  assert.ok(document.components?.schemas?.[schema], `OpenAPI 快照缺少开通码 schema：${schema}`);
}
assert.ok(document.components?.schemas?.ProblemDetails, "OpenAPI 快照缺少 ProblemDetails");

for (const schema of [
  "LinklyCloudTerminalListResponse",
  "LinklyCloudTerminalPairResponse",
  "LinklyCloudTerminalSelectionRequest",
  "LinklyCloudTerminalSelectionResponse",
  "LinklyCloudTerminalSummary",
  "LinklyCloudAssignableDevice",
  "LinklyCloudTerminalConnectionTestRequest",
  "LinklyCloudTerminalConnectionTestResponse",
  "LinklyCloudTerminalAssignmentRequest",
]) {
  assert.ok(document.components?.schemas?.[schema], `OpenAPI 快照缺少多终端 schema：${schema}`);
}

for (const schema of ["LinklyCloudTerminalConnectionTestRequest", "LinklyCloudTerminalAssignmentRequest"]) {
  assert.equal(document.components.schemas[schema].properties.expectedTerminalVersion.type, "string",
    "终端版本必须以字符串原样传输，不能转换成 JavaScript 日期或数字");
}

const activationContracts = [
  [
    "/api/v1/devices/activation-code/preview",
    "DeviceActivationCodePreviewRequest",
    "DeviceActivationCodePreviewResponseApiResult",
  ],
  [
    "/api/v1/devices/activation-code/redeem",
    "DeviceActivationCodeRedeemRequest",
    "DeviceActivationCodeRedeemResponseApiResult",
  ],
  [
    "/api/v1/devices/activation-code/rebind",
    "DeviceActivationCodeRebindRequest",
    "DeviceActivationCodeRedeemResponseApiResult",
  ],
];
for (const [route, requestSchema, responseSchema] of activationContracts) {
  const operation = document.paths?.[route]?.post;
  assert.equal(
    operation?.requestBody?.content?.["application/json"]?.schema?.$ref,
    `#/components/schemas/${requestSchema}`,
    `${route} 请求必须使用 ${requestSchema}`,
  );
  assert.equal(
    operation?.responses?.["200"]?.content?.["application/json"]?.schema?.$ref,
    `#/components/schemas/${responseSchema}`,
    `${route} 成功响应必须使用 ${responseSchema}`,
  );
  assert.equal(
    operation?.responses?.["400"]?.content?.["application/json"]?.schema?.$ref,
    "#/components/schemas/ProblemDetails",
    `${route} 的 400 必须使用 ProblemDetails`,
  );
}
assert.equal(
  document.paths?.["/api/v1/devices/activation-code/rebind"]?.post?.responses?.["401"]
    ?.content?.["application/json"]?.schema?.$ref,
  "#/components/schemas/ObjectApiResult",
  "rebind 的 401 必须保持 ObjectApiResult 合同",
);

assert.ok(document.components?.schemas?.DeviceRegisterRequest?.properties?.deviceSystem);
assert.ok(document.components?.schemas?.DeviceVerifyRequest?.properties?.deviceSystem);
assert.ok(document.components?.schemas?.CatalogSyncPageResponse?.properties?.catalogVersion);
assert.ok(document.components?.schemas?.CatalogSyncPageResponse?.properties?.pageChecksum);
assert.ok(document.components?.schemas?.CatalogSyncPlanResponse?.properties?.downloadLeaseId);
assert.ok(document.components?.schemas?.CatalogSyncPlanResponse?.properties?.deltaOperationCount);
assert.deepEqual(
  new Set(
    document.paths?.["/api/v1/catalog/sellable-items/page"]?.get?.parameters
      ?.map(parameter => parameter.name)
  ).has("downloadLeaseId"),
  true,
  "full page 必须暴露可选下载租约参数"
);
assert.deepEqual(
  new Set(
    document.paths?.["/api/v1/catalog/delta/page"]?.get?.parameters
      ?.map(parameter => parameter.name)
  ).has("downloadLeaseId"),
  true,
  "delta page 必须暴露可选下载租约参数"
);
assert.equal(
  document.components?.schemas?.LinklyCloudBackendSessionResponse?.properties?.cardTransaction?.$ref,
  "#/components/schemas/LinklyCloudBackendCardTransactionDto"
);
assert.deepEqual(
  Object.keys(document.components?.schemas?.LinklyCloudBackendCardTransactionDto?.properties ?? {}),
  [
    "txnRef",
    "rfn",
    "authCode",
    "cardType",
    "maskedCardNumber",
    "merchantId",
    "responseCode",
    "responseText",
    "stan",
    "bankDateTime",
    "amountCents"
  ]
);
assert.deepEqual(
  Object.keys(document.components?.schemas?.OperationAuditReadRecordDto?.properties ?? {}),
  [
    "eventId",
    "occurredAtIso",
    "operationType",
    "outcome",
    "cashierName",
    "storeCode",
    "deviceCode",
    "orderGuid",
    "receiptNumber",
    "correlationId",
    "safeMessage",
    "paymentAmountCents",
    "productCount",
    "primaryProduct",
    "uploadState",
    "items"
  ],
  "远程审计合同只能暴露白名单安全投影"
);

const claimPath = "/api/v1/installments/{installmentGuid}/repayment-claims";
const claimOperationPath = `${claimPath}/{operationGuid}`;
const cancelClaimPath = "/api/v1/installments/{installmentGuid}/cancel-claims";
const cancelClaimOperationPath = `${cancelClaimPath}/{operationGuid}`;
assert.ok(document.paths?.["/api/v1/installments/capabilities"]?.get);
assert.ok(document.paths?.[claimPath]?.post);
assert.ok(document.paths?.[claimOperationPath]?.get);
assert.ok(document.paths?.[`${claimOperationPath}/begin-provider`]?.post);
assert.ok(document.paths?.[`${claimOperationPath}/prepare-provider`]?.post);
assert.ok(document.paths?.[`${claimOperationPath}/resolve`]?.post);
assert.ok(document.paths?.[`${claimOperationPath}/commit`]?.post);
assert.equal(
  document.paths?.["/api/v1/installments/capabilities"]?.get?.responses?.["200"]
    ?.content?.["application/json"]?.schema?.$ref,
  "#/components/schemas/InstallmentRepaymentCapabilitiesResponseApiResult"
);
assert.equal(
  document.paths?.[claimOperationPath]?.get?.responses?.["200"]
    ?.content?.["application/json"]?.schema?.$ref,
  "#/components/schemas/InstallmentRepaymentClaimDtoApiResult"
);
assert.ok(document.paths?.[cancelClaimPath]?.post);
assert.ok(document.paths?.[cancelClaimOperationPath]?.get);
assert.ok(document.paths?.[`${cancelClaimOperationPath}/begin-refund`]?.post);
assert.ok(document.paths?.[`${cancelClaimOperationPath}/resolve`]?.post);
assert.ok(document.paths?.[`${cancelClaimOperationPath}/commit`]?.post);
assert.equal(
  document.paths?.[cancelClaimOperationPath]?.get?.responses?.["200"]
    ?.content?.["application/json"]?.schema?.$ref,
  "#/components/schemas/InstallmentCancelClaimDtoApiResult"
);

for (const [route, method, requestSchema] of [
  [claimPath, "post", "InstallmentRepaymentClaimCreateRequest"],
  [`${claimOperationPath}/begin-provider`, "post", "InstallmentRepaymentClaimBeginProviderRequest"],
  [`${claimOperationPath}/prepare-provider`, "post", "InstallmentRepaymentClaimPrepareProviderRequest"],
  [`${claimOperationPath}/resolve`, "post", "InstallmentRepaymentClaimResolveRequest"],
  [`${claimOperationPath}/commit`, "post", "InstallmentRepaymentClaimCommitRequest"]
]) {
  assert.equal(
    document.paths?.[route]?.[method]?.requestBody?.content?.["application/json"]?.schema?.$ref,
    `#/components/schemas/${requestSchema}`,
    `${route} 必须使用 ${requestSchema}`
  );
  assert.equal(
    document.paths?.[route]?.[method]?.responses?.["200"]?.content?.["application/json"]?.schema?.$ref,
    "#/components/schemas/InstallmentRepaymentClaimDtoApiResult",
    `${route} 必须返回 claim DTO 包装`
  );
}
assert.ok(
  document.components?.schemas?.InstallmentRepaymentCapabilitiesResponse?.properties
    ?.repaymentClaimPrepareProviderV1,
  "capabilities 必须暴露 prepare-provider capability"
);
assert.deepEqual(
  Object.keys(
    document.components?.schemas?.InstallmentRepaymentClaimPrepareProviderRequest
      ?.properties ?? {}
  ),
  ["paymentGuid", "amount", "method", "idempotencyKey", "provider", "providerAttemptId"],
  "prepare-provider body 只能包含六个绑定字段"
);

for (const [route, method, requestSchema] of [
  [cancelClaimPath, "post", "InstallmentCancelClaimCreateRequest"],
  [`${cancelClaimOperationPath}/resolve`, "post", "InstallmentCancelClaimResolveRequest"],
  [`${cancelClaimOperationPath}/commit`, "post", "InstallmentCancelClaimCommitRequest"]
]) {
  assert.equal(
    document.paths?.[route]?.[method]?.requestBody?.content?.["application/json"]?.schema?.$ref,
    `#/components/schemas/${requestSchema}`,
    `${route} 必须使用 ${requestSchema}`
  );
  assert.equal(
    document.paths?.[route]?.[method]?.responses?.["200"]?.content?.["application/json"]?.schema?.$ref,
    "#/components/schemas/InstallmentCancelClaimDtoApiResult",
    `${route} 必须返回取消 claim DTO 包装`
  );
}
assert.equal(
  document.paths?.[`${cancelClaimOperationPath}/begin-refund`]?.post?.requestBody,
  undefined,
  "begin-refund 不得接受可伪造的 body 身份或退款结果"
);
assert.equal(
  document.paths?.[`${cancelClaimOperationPath}/begin-refund`]?.post?.responses?.["200"]
    ?.content?.["application/json"]?.schema?.$ref,
  "#/components/schemas/InstallmentCancelClaimDtoApiResult"
);

assert.deepEqual(
  document.paths?.[claimPath]?.post?.parameters?.map(parameter => [
    parameter.name,
    parameter.required,
    parameter.schema?.format
  ]),
  [["installmentGuid", true, "uuid"]],
  `${claimPath} 必须使用 installmentGuid 路由身份`
);
for (const route of [claimOperationPath, `${claimOperationPath}/begin-provider`, `${claimOperationPath}/resolve`, `${claimOperationPath}/commit`]) {
  const method = route === claimOperationPath ? "get" : "post";
  const parameters = document.paths?.[route]?.[method]?.parameters ?? [];
  assert.deepEqual(
    parameters.map(parameter => [parameter.name, parameter.required, parameter.schema?.format]),
    [
      ["installmentGuid", true, "uuid"],
      ["operationGuid", true, "uuid"]
    ],
    `${route} 必须固定 installmentGuid 与 operationGuid 路由身份`
  );
}
assert.deepEqual(
  document.paths?.[cancelClaimPath]?.post?.parameters?.map(parameter => [
    parameter.name,
    parameter.required,
    parameter.schema?.format
  ]),
  [["installmentGuid", true, "uuid"]],
  `${cancelClaimPath} 必须使用 installmentGuid 路由身份`
);
for (const route of [
  cancelClaimOperationPath,
  `${cancelClaimOperationPath}/begin-refund`,
  `${cancelClaimOperationPath}/resolve`,
  `${cancelClaimOperationPath}/commit`
]) {
  const method = route === cancelClaimOperationPath ? "get" : "post";
  const parameters = document.paths?.[route]?.[method]?.parameters ?? [];
  assert.deepEqual(
    parameters.map(parameter => [parameter.name, parameter.required, parameter.schema?.format]),
    [
      ["installmentGuid", true, "uuid"],
      ["operationGuid", true, "uuid"]
    ],
    `${route} 必须固定 installmentGuid 与 operationGuid 路由身份`
  );
}

const schemas = document.components?.schemas ?? {};
const installmentHistoryParameters =
  document.paths?.["/api/v1/installments/history"]?.get?.parameters ?? [];
assert.equal(installmentHistoryParameters.length, 11);
for (const parameterName of ["updatedFrom", "updatedTo"]) {
  assert.deepEqual(
    installmentHistoryParameters.find(parameter => parameter.name === parameterName)?.schema,
    { type: "string", format: "date-time" }
  );
}
assert.deepEqual(
  installmentHistoryParameters.find(parameter => parameter.name === "orderByUpdatedAt")?.schema,
  { type: "boolean", default: false }
);
assert.deepEqual(
  schemas.InstallmentSummaryDto?.properties?.cancellationKind,
  { $ref: "#/components/schemas/InstallmentCancellationKind" }
);
assert.deepEqual(
  schemas.InstallmentDetailsDto?.properties?.updatedAt,
  { type: "string", format: "date-time", nullable: true }
);
assert.deepEqual(
  Object.keys(schemas.InstallmentRepaymentCapabilitiesResponse?.properties ?? {}),
  [
    "repaymentClaimsSupported",
    "repaymentClaimsRequired",
    "crossDeviceRepaymentEnabled",
    "preparedClaimTtlSeconds",
    "cancelClaimsSupported",
    "cancelClaimsRequired",
    "cancelPreparedClaimTtlSeconds",
    "crossDeviceCancelRefundEnabled",
    "crossDeviceVoidEnabled",
    "crossDevicePickupEnabled",
    "cardRepaymentSupported",
    "repaymentClaimPrepareProviderV1"
  ]
);
assert.deepEqual(
  schemas.InstallmentRepaymentCapabilitiesResponse?.properties?.preparedClaimTtlSeconds,
  { type: "integer", format: "int32" }
);
assert.deepEqual(
  schemas.InstallmentRepaymentCapabilitiesResponse?.properties?.cancelPreparedClaimTtlSeconds,
  { type: "integer", format: "int32" }
);
assert.deepEqual(
  Object.keys(schemas.InstallmentRepaymentClaimCreateRequest?.properties ?? {}),
  ["operationGuid", "paymentGuid", "amount", "method", "idempotencyKey"]
);
assert.deepEqual(
  schemas.InstallmentRepaymentClaimCreateRequest?.properties?.operationGuid,
  { type: "string", format: "uuid" }
);
assert.deepEqual(
  schemas.InstallmentRepaymentClaimCreateRequest?.properties?.paymentGuid,
  { type: "string", format: "uuid" }
);
assert.deepEqual(
  schemas.InstallmentRepaymentClaimCreateRequest?.properties?.amount,
  { type: "number", format: "double" }
);
assert.equal(
  schemas.InstallmentRepaymentClaimCreateRequest?.properties?.method?.$ref,
  "#/components/schemas/PaymentMethodKind"
);
assert.deepEqual(
  Object.keys(schemas.InstallmentRepaymentClaimBeginProviderRequest?.properties ?? {}),
  ["provider", "providerAttemptId"]
);
assert.deepEqual(
  Object.keys(schemas.InstallmentRepaymentClaimResolveRequest?.properties ?? {}),
  ["outcome", "cashNotCollectedConfirmed", "providerAttemptId"]
);
assert.deepEqual(
  schemas.InstallmentRepaymentClaimResolveRequest?.properties?.outcome,
  { $ref: "#/components/schemas/InstallmentRepaymentClaimResolveOutcome" },
  "resolve outcome 必须存在并保持非 nullable enum 引用"
);
assert.equal(
  schemas.InstallmentRepaymentClaimResolveRequest?.required,
  undefined,
  "兼容契约不得把 cash-not-collected 证据字段升级为 required"
);
assert.deepEqual(
  schemas.InstallmentRepaymentClaimResolveRequest?.properties
    ?.cashNotCollectedConfirmed,
  { type: "boolean" }
);
assert.deepEqual(
  schemas.InstallmentRepaymentClaimResolveRequest?.properties
    ?.providerAttemptId,
  { type: "string", nullable: true }
);
assert.deepEqual(
  Object.keys(schemas.InstallmentRepaymentClaimCommitRequest?.properties ?? {}),
  ["reference", "reservationToken", "cardTransactions"]
);
assert.equal(
  schemas.InstallmentRepaymentClaimCommitRequest?.properties?.cardTransactions?.items?.$ref,
  "#/components/schemas/CardTransactionDto"
);
assert.deepEqual(schemas.InstallmentRepaymentClaimResolveOutcome?.enum, [1, 2, 3]);
assert.deepEqual(schemas.InstallmentRepaymentClaimStatus?.enum, [1, 2, 3, 4, 5, 6]);
assert.deepEqual(
  Object.keys(schemas.InstallmentRepaymentClaimDto?.properties ?? {}),
  [
    "installmentGuid",
    "operationGuid",
    "paymentGuid",
    "amount",
    "method",
    "idempotencyKey",
    "status",
    "provider",
    "providerAttemptId",
    "createdAtUtc",
    "updatedAtUtc",
    "expiresAtUtc",
    "commit",
    "alreadyExists"
  ]
);
assert.equal(
  schemas.InstallmentRepaymentClaimDto?.properties?.commit?.$ref,
  "#/components/schemas/InstallmentAppendPaymentResponse"
);
for (const guidField of ["installmentGuid", "operationGuid", "paymentGuid"]) {
  assert.deepEqual(
    schemas.InstallmentRepaymentClaimDto?.properties?.[guidField],
    { type: "string", format: "uuid" }
  );
}
assert.deepEqual(
  schemas.InstallmentRepaymentClaimDto?.properties?.amount,
  { type: "number", format: "double" }
);
for (const requestSchema of [
  "InstallmentRepaymentClaimCreateRequest",
  "InstallmentRepaymentClaimBeginProviderRequest",
  "InstallmentRepaymentClaimResolveRequest",
  "InstallmentRepaymentClaimCommitRequest"
]) {
  const requestProperties = schemas[requestSchema]?.properties ?? {};
  for (const untrustedIdentityField of ["storeCode", "deviceCode", "cashierId", "cashierName"]) {
    assert.equal(
      Object.hasOwn(requestProperties, untrustedIdentityField),
      false,
      `${requestSchema} 不得接受 body 身份字段 ${untrustedIdentityField}`
    );
  }
}

assert.deepEqual(
  Object.keys(schemas.InstallmentCancelClaimCreateRequest?.properties ?? {}),
  ["operationGuid", "idempotencyKey", "reason", "refundPlanFingerprint"]
);
assert.deepEqual(
  schemas.InstallmentCancelClaimCreateRequest?.properties?.operationGuid,
  { type: "string", format: "uuid" }
);
assert.deepEqual(
  Object.keys(schemas.InstallmentCancelClaimResolveRequest?.properties ?? {}),
  ["outcome", "approvedRefunds"]
);
assert.equal(
  schemas.InstallmentCancelClaimResolveRequest?.properties?.approvedRefunds?.items?.$ref,
  "#/components/schemas/InstallmentRefundPaymentCommandDto"
);
assert.deepEqual(
  Object.keys(schemas.InstallmentCancelClaimCommitRequest?.properties ?? {}),
  ["refunds"]
);
assert.equal(
  schemas.InstallmentCancelClaimCommitRequest?.properties?.refunds?.items?.$ref,
  "#/components/schemas/InstallmentRefundPaymentCommandDto"
);
assert.deepEqual(
  schemas.InstallmentRefundPaymentCommandDto?.properties?.originalPaymentGuid,
  { type: "string", format: "uuid" }
);
assert.deepEqual(schemas.InstallmentCancelClaimResolveOutcome?.enum, [1, 2, 3]);
assert.deepEqual(schemas.InstallmentCancelClaimStatus?.enum, [1, 2, 3, 4, 5, 6]);
assert.deepEqual(
  Object.keys(schemas.InstallmentCancelClaimDto?.properties ?? {}),
  [
    "installmentGuid",
    "operationGuid",
    "idempotencyKey",
    "refundPlanFingerprint",
    "status",
    "createdAtUtc",
    "updatedAtUtc",
    "expiresAtUtc",
    "commit",
    "alreadyExists",
    "originalDeviceCode",
    "executingDeviceCode"
  ]
);
for (const guidField of ["installmentGuid", "operationGuid"]) {
  assert.deepEqual(
    schemas.InstallmentCancelClaimDto?.properties?.[guidField],
    { type: "string", format: "uuid" }
  );
}
assert.equal(
  schemas.InstallmentCancelClaimDto?.properties?.commit?.$ref,
  "#/components/schemas/InstallmentCancelClaimCommitResponse"
);
assert.equal(
  schemas.InstallmentCancelClaimCommitResponse?.properties?.details?.$ref,
  "#/components/schemas/InstallmentDetailsDto"
);
for (const requestSchema of [
  "InstallmentCancelClaimCreateRequest",
  "InstallmentCancelClaimResolveRequest",
  "InstallmentCancelClaimCommitRequest"
]) {
  const requestProperties = schemas[requestSchema]?.properties ?? {};
  for (const untrustedIdentityField of ["storeCode", "deviceCode", "cashierId", "cashierName"]) {
    assert.equal(
      Object.hasOwn(requestProperties, untrustedIdentityField),
      false,
      `${requestSchema} 不得接受 body 身份字段 ${untrustedIdentityField}`
    );
  }
}

const generated = readFileSync(generatedPath, "utf8");
assert.match(generated, /AUTO-GENERATED/);
assert.match(generated, /LinklyCloudBackendCardTransactionDto:/);
assert.match(generated, /OperationAuditReadListDto:/);
assert.match(generated, /OperationAuditReadRecordDto:/);
assert.match(generated, /"\/api\/v1\/installments\/capabilities":/);
assert.match(generated, /"\/api\/v1\/installments\/\{installmentGuid\}\/repayment-claims":/);
assert.match(generated, /"\/api\/v1\/installments\/\{installmentGuid\}\/cancel-claims":/);
assert.match(generated, /InstallmentRepaymentCapabilitiesResponse:/);
assert.match(generated, /InstallmentRepaymentClaimCreateRequest:/);
assert.match(generated, /InstallmentRepaymentClaimDto:/);
assert.match(generated, /InstallmentCancelClaimCreateRequest:/);
assert.match(generated, /InstallmentCancelClaimDto:/);
assert.match(generated, /InstallmentRepaymentClaimResolveOutcome: 1 \| 2 \| 3;/);
assert.match(generated, /InstallmentRepaymentClaimStatus: 1 \| 2 \| 3 \| 4 \| 5 \| 6;/);
assert.match(generated, /InstallmentCancelClaimResolveOutcome: 1 \| 2 \| 3;/);
assert.match(generated, /InstallmentCancelClaimStatus: 1 \| 2 \| 3 \| 4 \| 5 \| 6;/);
assert.match(
  generated,
  /cardTransaction\?: components\["schemas"\]\["LinklyCloudBackendCardTransactionDto"\]/
);

console.log("hbpos OpenAPI snapshot and generated DTOs: ok");
