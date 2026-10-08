import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { resolve } from "node:path";
import { createIosReviewDataStore } from "./data-store";
import { IOS_REVIEW_STORES } from "./identity";
import { createIosReviewTransport } from "./transport";
import { resetIosReviewAppRouteState } from "./app-routes";
import { normalizeInvoiceGridResponse } from "../local-supplier-invoices/api";
import {
  buildSeasonalCardBatchRequest,
  normalizeSeasonalCardBatchResponse,
  normalizeSeasonalCardCatalogResponse,
  normalizeSeasonalCardOverviewResponse,
  normalizeSeasonalCardSubmissionsResponse,
} from "../seasonal-cards/api";
import {
  buildSeasonalCardBatchPayload,
  createSeasonalCardDraft,
  getSeasonalCardOptionsForType,
} from "../seasonal-cards/submit-draft";
import { normalizeDeviceManagementListResponse } from "../device-management/api";
import {
  normalizeChinaSupplierBranchTotalsSnapshot,
  normalizeProductBranchRows,
  normalizeProductBranchReportSnapshot,
  normalizeProductPage,
  normalizeProductReportProductPageSnapshot,
  normalizeProductReportTotalRevenue,
  normalizeSupplierBranchReportSnapshot,
  normalizeSupplierReportSnapshot,
  normalizeSupplierBranchRows,
  normalizeSupplierRows,
} from "../product-report/api";
import {
  normalizeBranchHourlyRevenueSnapshot,
  normalizeDailyRevenueSnapshot,
  normalizeExecutiveBranchPerformance,
  normalizeHourlyRevenueSnapshot,
} from "../reports/api";
import { normalizeStatisticsFreshness } from "../reports/statistics-freshness";
import {
  normalizePromotionsResponse,
  normalizeValidPromotionsResponse,
} from "../promotions/api";
import { normalizeWarehousePriceSyncResponse } from "../product-maintenance/warehouse-price-sync";
import { buildInstallmentOrderListPayload } from "../installment-orders/api";
import {
  buildAttendanceQrPunchPayload,
  normalizeAttendanceQrResolveResult,
} from "../attendance/attendance-qr";

type Method = "GET" | "POST" | "PUT" | "PATCH" | "DELETE";

async function run() {
  const dataStore = createIosReviewDataStore(
    new Date("2026-07-16T00:00:00.000Z"),
  );
  const transport = createIosReviewTransport(dataStore);

  const request = async (
    method: Method,
    url: string,
    body?: unknown,
    params?: Record<string, unknown>,
  ) =>
    (
      await transport.dispatch({
        method,
        url,
        data: body,
        params,
      })
    ).data as any;

  const localBarcodeOne = await request("POST", "/react/v1/products/generate-local-barcode", null, { supplierCode: "SP001" });
  const localBarcodeTwo = await request("POST", "/react/v1/products/generate-local-barcode", null, { supplierCode: "SP001" });
  assert.match(localBarcodeOne.barcode, /^9529\d{9}$/);
  assert.notEqual(localBarcodeOne.barcode, localBarcodeTwo.barcode);
  await assert.rejects(request("POST", "/react/v1/products/generate-local-barcode", null, { supplierCode: "200" }));

  const reviewQrToken = (index: number) =>
    `HBATE1.review_${index}.${"A".repeat(16)}.${"B".repeat(40)}.${"C".repeat(22)}`;
  const attendanceQrVerification = {
    locationLatitude: -27.4698,
    locationLongitude: 153.0251,
    locationAccuracy: 5,
    locationCapturedAtUtc: "2026-07-16T00:00:00.000Z",
  };
  const resolveReviewQr = async (
    send: (
      method: Method,
      url: string,
      body?: unknown,
      params?: Record<string, unknown>,
    ) => Promise<any>,
    qrToken: string,
  ) => normalizeAttendanceQrResolveResult(
    await send("POST", "/react/v1/attendance/qr/resolve", { qrToken }),
  );
  const punchWithReviewQr = async (
    send: (
      method: Method,
      url: string,
      body?: unknown,
      params?: Record<string, unknown>,
    ) => Promise<any>,
    qrToken: string,
  ) => {
    const resolved = await resolveReviewQr(send, qrToken);
    assert.ok(resolved.punchAuthorizationToken);
    assert.ok(resolved.punchAuthorizationExpiresAtUtc);
    const payload = buildAttendanceQrPunchPayload(
      qrToken,
      resolved.punchAuthorizationToken,
      attendanceQrVerification,
    );
    assert.equal("storeCode" in payload, false);
    assert.equal("punchType" in payload, false);
    assert.equal("workDate" in payload, false);
    return {
      payload,
      punch: await send("POST", "/react/v1/attendance/punch", payload),
      resolved,
    };
  };

  const qrDataStore = createIosReviewDataStore(
    new Date("2026-07-16T00:00:00.000Z"),
  );
  const qrTransport = createIosReviewTransport(qrDataStore);
  const qrRequest = async (
    method: Method,
    url: string,
    body?: unknown,
    params?: Record<string, unknown>,
  ) => (
    await qrTransport.dispatch({ method, url, data: body, params })
  ).data as any;
  const invalidAuthorizationQrToken = reviewQrToken(1);
  const invalidAuthorization = await resolveReviewQr(
    qrRequest,
    invalidAuthorizationQrToken,
  );
  await assert.rejects(
    () => qrRequest(
      "POST",
      "/react/v1/attendance/punch",
      buildAttendanceQrPunchPayload(
        invalidAuthorizationQrToken,
        `${invalidAuthorization.punchAuthorizationToken}-invalid`,
        attendanceQrVerification,
      ),
    ),
    /ATTENDANCE_PUNCH_AUTHORIZATION_INVALID/,
    "Review punch 必须拒绝未由 resolve 签发的短时授权",
  );

  let qrToday = await qrRequest(
    "GET",
    "/react/v1/attendance/my/today?storeCode=REV001",
  );
  assert.equal(qrToday.punches.length, 0);
  const expectedQrPunches = [
    {
      punchType: "ClockIn",
      punchCount: 1,
      nextPunchType: "ClockOut",
      canClockIn: false,
      canClockOut: true,
    },
    {
      punchType: "ClockOut",
      punchCount: 2,
      nextPunchType: "ClockIn",
      canClockIn: true,
      canClockOut: false,
    },
    {
      punchType: "ClockIn",
      punchCount: 3,
      nextPunchType: "ClockOut",
      canClockIn: false,
      canClockOut: true,
    },
    {
      punchType: "ClockOut",
      punchCount: 4,
      nextPunchType: "ClockIn",
      canClockIn: true,
      canClockOut: false,
    },
  ] as const;
  for (const [offset, expected] of expectedQrPunches.entries()) {
    const { punch, payload, resolved } = await punchWithReviewQr(
      qrRequest,
      reviewQrToken(offset + 2),
    );
    assert.equal(punch.punchType, expected.punchType);
    assert.equal(punch.storeCode, resolved.storeCode);
    assert.equal(punch.serverTimeUtc, "2026-07-16T00:00:00.000Z");
    assert.equal(punch.workDate, "2026-07-16");
    assert.equal(payload.punchAuthorizationToken, resolved.punchAuthorizationToken);
    assert.equal("qrToken" in punch, false);
    assert.equal("punchAuthorizationToken" in punch, false);
    if (offset === 0) {
      const sameAuthorizationRetry = await qrRequest(
        "POST",
        "/react/v1/attendance/punch",
        payload,
      );
      const reResolved = await resolveReviewQr(qrRequest, reviewQrToken(offset + 2));
      const reResolvedRetry = await qrRequest(
        "POST",
        "/react/v1/attendance/punch",
        buildAttendanceQrPunchPayload(
          reviewQrToken(offset + 2),
          reResolved.punchAuthorizationToken,
          attendanceQrVerification,
        ),
      );
      for (const retry of [sameAuthorizationRetry, reResolvedRetry]) {
        assert.equal(retry.punchGuid, punch.punchGuid);
        assert.equal(retry.punchType, punch.punchType);
        assert.equal("qrToken" in retry, false);
        assert.equal("punchAuthorizationToken" in retry, false);
      }
    }
    qrToday = await qrRequest(
      "GET",
      "/react/v1/attendance/my/today?storeCode=REV001",
    );
    assert.deepEqual(
      {
        punchCount: qrToday.punches.length,
        nextPunchType: qrToday.nextPunchType,
        canClockIn: qrToday.canClockIn,
        canClockOut: qrToday.canClockOut,
      },
      {
        punchCount: expected.punchCount,
        nextPunchType: expected.nextPunchType,
        canClockIn: expected.canClockIn,
        canClockOut: expected.canClockOut,
      },
      "QR ClockIn/ClockOut 后 Today refetch 必须反映服务端推导的下一动作",
    );
  }
  await punchWithReviewQr(qrRequest, reviewQrToken(6));
  await punchWithReviewQr(qrRequest, reviewQrToken(7));
  qrToday = await qrRequest(
    "GET",
    "/react/v1/attendance/my/today?storeCode=REV001",
  );
  assert.deepEqual(
    {
      punchCount: qrToday.punches.length,
      nextPunchType: qrToday.nextPunchType,
      canClockIn: qrToday.canClockIn,
      canClockOut: qrToday.canClockOut,
    },
    {
      punchCount: 6,
      nextPunchType: "ClockIn",
      canClockIn: false,
      canClockOut: false,
    },
    "Review manager 的第三段完成后，QR punch 也必须关闭下一段",
  );
  const fourthSegmentQrToken = reviewQrToken(8);
  const fourthSegmentResolve = await resolveReviewQr(qrRequest, fourthSegmentQrToken);
  await assert.rejects(
    () => qrRequest(
      "POST",
      "/react/v1/attendance/punch",
      buildAttendanceQrPunchPayload(
        fourthSegmentQrToken,
        fourthSegmentResolve.punchAuthorizationToken,
        attendanceQrVerification,
      ),
    ),
    /SEGMENT_LIMIT_REACHED/,
    "Review manager 的 QR punch 不得绕过每日每店三段上限",
  );

  let boundaryNow = new Date("2026-07-15T15:00:00.000Z");
  const boundaryBaseDataStore = createIosReviewDataStore(boundaryNow);
  const boundaryDataStore = {
    ...boundaryBaseDataStore,
    getNow: () => new Date(boundaryNow.getTime()),
  };
  const boundaryTransport = createIosReviewTransport(boundaryDataStore);
  const boundaryRequest = async (
    method: Method,
    url: string,
    body?: unknown,
    params?: Record<string, unknown>,
  ) => (
    await boundaryTransport.dispatch({ method, url, data: body, params })
  ).data as any;
  const businessDayQrToken = reviewQrToken(30);
  const businessDayPunch = await punchWithReviewQr(
    boundaryRequest,
    businessDayQrToken,
  );
  const unusedQrToken = reviewQrToken(31);
  const unusedResolve = await resolveReviewQr(boundaryRequest, unusedQrToken);
  const boundaryToday = await boundaryRequest(
    "GET",
    "/react/v1/attendance/my/today?storeCode=REV001",
  );
  assert.equal(boundaryToday.workDate, "2026-07-16");
  assert.equal(boundaryToday.schedules[0]?.workDate, "2026-07-16");
  assert.equal(boundaryToday.holidays[0]?.holidayDate, "2026-07-16");
  assert.equal(boundaryToday.punches[0]?.workDate, "2026-07-16");
  assert.equal(boundaryToday.punches[0]?.punchGuid, businessDayPunch.punch.punchGuid);
  assert.equal("qrToken" in boundaryToday.punches[0], false);
  assert.equal("punchAuthorizationToken" in boundaryToday.punches[0], false);

  boundaryNow = new Date(businessDayPunch.resolved.punchAuthorizationExpiresAtUtc!);
  const idempotentAtExpiry = await boundaryRequest(
    "POST",
    "/react/v1/attendance/punch",
    businessDayPunch.payload,
  );
  assert.equal(idempotentAtExpiry.punchGuid, businessDayPunch.punch.punchGuid);
  assert.equal(idempotentAtExpiry.punchType, businessDayPunch.punch.punchType);
  await assert.rejects(
    () => boundaryRequest(
      "POST",
      "/react/v1/attendance/punch",
      buildAttendanceQrPunchPayload(
        unusedQrToken,
        unusedResolve.punchAuthorizationToken,
        attendanceQrVerification,
      ),
    ),
    /ATTENDANCE_PUNCH_AUTHORIZATION_EXPIRED/,
    "未落库二维码必须在短时授权到期边界拒绝，已落库重试则先命中幂等结果",
  );

  const menu = await request("GET", "/navigation/app-menu");
  assert.equal(menu.length, 20, "审核菜单必须覆盖全部 20 个业务入口");
  assert.equal(
    menu.some((item: { routeName?: string }) => item.routeName === "product-insights"),
    false,
    "商品进销查询没有离线审核数据契约时不得进入审核菜单",
  );

  const stores = await request("GET", "/Users/guid/review-user/stores");
  assert.equal(stores.length, 28, "审核门店接口必须覆盖 28 店报表规模");
  assert.equal(
    stores.every((store: { storeGUID?: string }) => Boolean(store.storeGUID)),
    true,
    "演示门店必须携带稳定 storeGUID",
  );

  const preorderGate = await request(
    "GET",
    "/react/v1/preorders/active",
    undefined,
    { storeCode: "REV001" },
  );
  assert.deepEqual(preorderGate, {
    storeCode: "REV001",
    normalOrderBlocked: false,
    activations: [],
  });

  const accessRoles = await request(
    "GET",
    "/Users/guid/review-staff-001/roles",
  );
  assert.deepEqual(
    accessRoles.map((role: { roleName?: string }) => role.roleName),
    ["StoreStaff"],
    "审核模式必须覆盖账号角色读取",
  );
  const accessPermissionState = await request(
    "GET",
    "/Users/guid/review-staff-001/permissions/state",
  );
  assert.equal(accessPermissionState.implicitAllPermissions, false);
  assert.deepEqual(accessPermissionState.inheritedPermissionCodes, [
    "Users.View",
  ]);
  assert.ok(
    (await request("GET", "/Roles/active")).length > 0,
    "审核模式必须提供角色目录",
  );
  assert.ok(
    (await request("GET", "/Roles/permissions")).length > 0,
    "审核模式必须提供权限目录",
  );
  assert.equal(
    (await request("GET", "/Roles/permissions")).some(
      (category: { permissions?: { name?: string }[] }) =>
        category.permissions?.some(
          (permission) => permission.name === "Users.Create",
        ),
    ),
    true,
    "Review 权限管理目录必须展示创建员工能力",
  );

  const readContracts: {
    name: string;
    method: Method;
    path: string;
    body?: unknown;
    assertData(data: any): void;
  }[] = [
    {
      name: "home products",
      method: "POST",
      path: "/react/v1/store-order/products",
      body: { storeCode: "REV001" },
      assertData: (data) =>
        assert.equal(data.items[0].barcode, "9330000000017"),
    },
    {
      name: "orders",
      method: "POST",
      path: "/react/v1/store-order/list",
      body: { storeCode: "REV001" },
      assertData: (data) => assert.ok(data.items.length > 0),
    },
    {
      name: "cart",
      method: "GET",
      path: "/react/v1/store-order/cart/REV001",
      assertData: (data) => assert.equal(data.storeCode, "REV001"),
    },
    {
      name: "warehouse",
      method: "GET",
      path: "/react/v1/product-warehouse/mobile/lookup?keyword=9330000000017",
      assertData: (data) => assert.equal(data[0].productCode, "REV-PROD-001"),
    },
    {
      name: "containers",
      method: "POST",
      path: "/react/v1/containers/list",
      body: {},
      assertData: (data) => assert.ok(data.items.length > 0),
    },
    {
      name: "domestic purchase",
      method: "GET",
      path: "/v1/domestic-product-creation/batches",
      assertData: (data) => assert.ok(data.items.length > 0),
    },
    {
      name: "local invoices",
      method: "POST",
      path: "/react/v1/local-supplier-invoices/grid",
      body: {},
      assertData: (data) => assert.ok(data.items.length > 0),
    },
    {
      name: "advertisements",
      method: "POST",
      path: "/react/v1/advertisements/grid",
      body: {},
      assertData: (data) => assert.ok(data.items.length > 0),
    },
    {
      name: "promotions",
      method: "POST",
      path: "/react/v1/promotions/store/grid",
      body: {},
      assertData: (data) => assert.ok(data.items.length > 0),
    },
    {
      name: "product maintenance",
      method: "POST",
      path: "/react/v1/store-product-maintenance/lookup",
      body: { keyword: "9330000000017" },
      assertData: (data) => assert.equal(data[0].barcode, "9330000000017"),
    },
    {
      name: "installment orders",
      method: "POST",
      path: "/react/v1/installment-orders/list",
      body: {},
      assertData: (data) => {
        assert.equal(data.items[0]?.installmentGuid, "review-installment-001");
        assert.equal(data.items[0]?.installmentNumber, "REV-INS-0001");
        assert.equal(data.items[0]?.balanceAmount, 0);
        assert.equal(Object.hasOwn(data.items[0], "deviceCode"), false);
        assert.equal(Object.hasOwn(data.items[0], "note"), false);
        assert.equal(Object.hasOwn(data.items[0], "pickupInfo"), false);
      },
    },
    {
      name: "installment order detail",
      method: "GET",
      path: "/react/v1/installment-orders/detail/review-installment-001",
      assertData: (data) => {
        assert.equal(data.order.installmentGuid, "review-installment-001");
        assert.equal(data.lines[0]?.quantity, 1.25);
        assert.equal(data.payments[0]?.method, 1);
        assert.equal(
          data.payments.some(
            (payment: { status?: number }) => payment.status === 2,
          ),
          true,
        );
        assert.equal(data.pickupInfo?.pickedUpBy, "App Review Demo");
      },
    },
    {
      name: "vouchers",
      method: "POST",
      path: "/react/v1/store-vouchers/list",
      body: {},
      assertData: (data) => assert.ok(data.items.length > 0),
    },
    {
      name: "seasonal cards",
      method: "GET",
      path: "/react/v1/seasonal-card-remaining/catalog",
      assertData: (data) => assert.ok(data.length > 0),
    },
    {
      name: "attendance personal",
      method: "GET",
      path: "/react/v1/attendance/my/today?storeCode=REV001",
      assertData: (data) =>
        assert.equal(data.storeTimeZone, "Australia/Brisbane"),
    },
    {
      name: "attendance management",
      method: "GET",
      path: "/react/v1/attendance/schedules/week?storeCode=REV001",
      assertData: (data) => assert.ok(data.length > 0),
    },
    {
      name: "users",
      method: "POST",
      path: "/react/v1/store-users/grid",
      body: { storeCode: "REV001" },
      assertData: (data) => assert.ok(data.items.length > 0),
    },
    {
      name: "employee profile",
      method: "GET",
      path: "/EmployeeProfiles/me",
      assertData: (data) => assert.equal(data.username, "ios_app_review"),
    },
    {
      name: "device management",
      method: "GET",
      path: "/mobile/device-management/paged",
      assertData: (data) => assert.ok(data.devices.length > 0),
    },
    {
      name: "reports",
      method: "GET",
      path: "/react/v1/dashboard/executive-branch-performance",
      assertData: (data) => assert.ok(data.items.length > 0),
    },
  ];

  for (const contract of readContracts) {
    const data = await request(contract.method, contract.path, contract.body);
    contract.assertData(data);
  }

  const domesticCreationPayload = {
    supplierCode: "REVIEW-CN-A",
    prefixCode: "RVA",
    prefixName: "审核供应商 A",
    items: [
      { productName: "普通商品 1", productType: 0, privateLabelPrice: 1.1 },
      { productName: "普通商品 2", productType: 0, privateLabelPrice: 1.2 },
      ...Array.from({ length: 3 }, (_, setIndex) => ({
        productName: `套装 ${setIndex + 1}`,
        productType: 1,
        privateLabelPrice: 3 + setIndex,
        setQuantity: 2,
        setPrice: 6 + setIndex,
        createCount: 1,
        subItems: [
          { productName: `套装 ${setIndex + 1} 子项 1`, productType: 2, privateLabelPrice: 1.1 },
          { productName: `套装 ${setIndex + 1} 子项 2`, productType: 2, privateLabelPrice: 1.2 },
        ],
      })),
    ],
  };
  const domesticCreation = await request(
    "POST",
    "/v1/domestic-product-creation/batch",
    domesticCreationPayload,
  );
  assert.equal(domesticCreation.totalCreated, 11, "2 普通 + 3 套各 2 子项必须返回 11 条创建记录");
  assert.equal(domesticCreation.normalProductCount, 2);
  assert.equal(domesticCreation.setProductCount, 3);
  const domesticBatchNumber = domesticCreation.batchNumber;
  assert.ok(domesticBatchNumber);

  const domesticBatchDetail = await request(
    "GET",
    `/v1/domestic-product-creation/batch/${encodeURIComponent(domesticBatchNumber)}`,
  );
  assert.equal(domesticBatchDetail.items.length, 11, "批次详情必须完整回读父项和子项");
  assert.equal(new Set(domesticBatchDetail.items.map((item: any) => item.productCode)).size, 11, "父子记录必须使用独立真实主键");
  assert.equal(new Set(domesticBatchDetail.items.map((item: any) => item.hbProductNo)).size, 11, "父子记录必须使用独立货号");
  assert.equal(new Set(domesticBatchDetail.items.map((item: any) => item.barcode)).size, 11, "父子记录必须使用独立条码");
  const createdSets = domesticBatchDetail.items.filter((item: any) => item.productType === 1);
  const createdSubItems = domesticBatchDetail.items.filter((item: any) => item.productType === 2);
  assert.equal(createdSets.length, 3);
  assert.equal(createdSubItems.length, 6);
  for (const setItem of createdSets) {
    const children = createdSubItems.filter((item: any) => item.parentHBProductNo === setItem.hbProductNo);
    assert.equal(children.length, 2, "每个套装父货号必须准确关联两个子项");
    assert.ok(children.every((item: any) => item.parentProductCode === setItem.productCode));
  }

  const templateA = await request("POST", "/v1/domestic-product-creation/templates", {
    supplierCode: "REVIEW-CN-A",
    templateName: "审核模板 A",
    setProductName: "审核礼盒 A",
    subItems: [
      { productName: "A 子项 1", privateLabelPrice: 1.5 },
      { productName: "A 子项 2", privateLabelPrice: 2.5 },
    ],
  });
  const templateDisabled = await request("POST", "/v1/domestic-product-creation/templates", {
    supplierCode: "REVIEW-CN-A",
    templateName: "审核模板停用",
    setProductName: "停用礼盒",
    isEnabled: false,
    subItems: [{ productName: "停用子项", privateLabelPrice: 9 }],
  });
  const templateB = await request("POST", "/v1/domestic-product-creation/templates", {
    supplierCode: "REVIEW-CN-B",
    templateName: "审核模板 B",
    setProductName: "审核礼盒 B",
    subItems: [{ productName: "B 子项", privateLabelPrice: 3.5 }],
  });
  assert.notEqual(templateA.templateId, templateB.templateId);
  assert.equal(templateA.setQuantity, 2);

  const templatesForA = await request(
    "GET",
    "/v1/domestic-product-creation/templates",
    undefined,
    { supplierCode: "REVIEW-CN-A", includeInactive: true },
  );
  assert.deepEqual(templatesForA.map((item: any) => item.templateId), [templateA.templateId], "列表必须隔离供应商并过滤停用模板");
  const templatesForB = await request(
    "GET",
    "/v1/domestic-product-creation/templates",
    undefined,
    { supplierCode: "REVIEW-CN-B" },
  );
  assert.deepEqual(templatesForB.map((item: any) => item.templateId), [templateB.templateId]);
  const templateDetail = await request(
    "GET",
    `/v1/domestic-product-creation/templates/${encodeURIComponent(templateA.templateId)}`,
    undefined,
    { supplierCode: "REVIEW-CN-A" },
  );
  assert.equal(templateDetail.subItems.length, 2);
  await assert.rejects(
    () => request("GET", `/v1/domestic-product-creation/templates/${templateA.templateId}`, undefined, { supplierCode: "REVIEW-CN-B" }),
    /IOS_REVIEW_DOMESTIC_SET_TEMPLATE_NOT_FOUND/,
    "模板详情不得跨供应商读取",
  );
  await assert.rejects(
    () => request("GET", `/v1/domestic-product-creation/templates/${templateDisabled.templateId}`, undefined, { supplierCode: "REVIEW-CN-A" }),
    /IOS_REVIEW_DOMESTIC_SET_TEMPLATE_NOT_FOUND/,
    "模板详情不得读取停用模板",
  );

  // 同一模板重复应用时，返回的快照和生成的父子记录都必须互不共享引用或主键。
  const firstTemplateRead = await request(
    "GET",
    `/v1/domestic-product-creation/templates/${templateA.templateId}`,
    undefined,
    { supplierCode: "REVIEW-CN-A" },
  );
  firstTemplateRead.subItems[0].productName = "只改第一份快照";
  const secondTemplateRead = await request(
    "GET",
    `/v1/domestic-product-creation/templates/${templateA.templateId}`,
    undefined,
    { supplierCode: "REVIEW-CN-A" },
  );
  assert.equal(secondTemplateRead.subItems[0].productName, "A 子项 1", "模板重复应用不得共享可变子项快照");

  const repeatedTemplateBatch = await request("POST", "/v1/domestic-product-creation/batch", {
    supplierCode: "REVIEW-CN-A",
    items: [
      { productName: "重复应用 A", productType: 1, createCount: 1, subItems: secondTemplateRead.subItems },
      { productName: "重复应用 B", productType: 1, createCount: 1, subItems: secondTemplateRead.subItems },
    ],
  });
  const repeatedDetail = await request(
    "GET",
    `/v1/domestic-product-creation/batch/${encodeURIComponent(repeatedTemplateBatch.batchNumber)}`,
  );
  assert.equal(repeatedDetail.items.length, 6);
  assert.equal(new Set(repeatedDetail.items.map((item: any) => item.productCode)).size, 6, "模板重复应用必须生成独立父子主键");
  assert.equal(new Set(repeatedDetail.items.map((item: any) => item.barcode)).size, 6, "模板重复应用必须生成独立父子条码");

  await assert.rejects(
    () => request("POST", "/react/v1/store-users", {
      username: "review_missing_password",
      storeCode: "REV001",
    }),
    /IOS_REVIEW_USER_PASSWORD_INVALID/,
    "Review mock 必须拒绝缺少初始密码的员工创建请求",
  );

  const createdReviewUser = await request("POST", "/react/v1/store-users", {
    username: "review_created_user",
    fullName: "Review Created User",
    email: "review-created@example.invalid",
    phone: "0400000003",
    password: "Review123!",
    passwordFormat: "raw",
    status: 1,
    storeCode: "REV001",
    roleNames: ["Admin"],
    employmentType: "full-time",
  });
  assert.equal(createdReviewUser.username, "review_created_user");
  assert.equal(createdReviewUser.storeCode, "REV001");
  assert.deepEqual(createdReviewUser.roleNames, ["StoreStaff"]);
  assert.equal(createdReviewUser.employmentType, "casual");
  assert.equal(createdReviewUser.status, 1);
  assert.equal("password" in createdReviewUser, false, "审核数据不得回显初始密码");
  const usersAfterCreate = await request(
    "POST",
    "/react/v1/store-users/grid",
    { storeCode: "REV001" },
  );
  assert.ok(
    usersAfterCreate.items.some(
      (user: { userGUID?: string }) => user.userGUID === createdReviewUser.userGUID,
    ),
    "审核模式创建的店员必须可以从列表读回",
  );

  const staffCodeUrl = `/react/v1/store-users/${createdReviewUser.userGUID}/cashier-barcode`;
  assert.equal((await request("GET", staffCodeUrl, null, { storeCode: "REV001" })).exists, false);
  const staffCode = await request("POST", `${staffCodeUrl}/ensure`, { storeCode: "REV001" });
  assert.match(staffCode.barcode, /^95288\d{8}$/);
  assert.equal((await request("POST", `${staffCodeUrl}/ensure`, { storeCode: "REV001" })).barcode, staffCode.barcode);
  const confirmation = { storeCode: "REV001", barcode: staffCode.barcode, printAttemptId: "review-attempt-1" };
  await request("POST", `${staffCodeUrl}/print-confirmation`, confirmation);
  assert.equal((await request("POST", `${staffCodeUrl}/print-confirmation`, confirmation)).printCount, 1);
  await assert.rejects(request("GET", staffCodeUrl, null, { storeCode: "REV002" }));

  const posStaffUrl = "/react/v1/store-users/review-pos-staff-1/cashier-barcode";
  const posStaffCode = await request("POST", `${posStaffUrl}/ensure`, { storeCode: "REV003" });
  assert.notEqual(posStaffCode.barcode, staffCode.barcode, "不同员工使用独立演示码");
  await assert.rejects(request("POST", "/react/v1/store-users/review-pos-staff-5/cashier-barcode/ensure", { storeCode: "REV003" }));
  await assert.rejects(request("POST", `${posStaffUrl}/print-confirmation`, { ...confirmation, storeCode: "REV003" }));
  const posStaffRows = await request("POST", "/react/v1/store-users/grid", { storeCode: "REV003" });
  assert.equal(posStaffRows.items.length, 5);
  assert.ok(posStaffRows.items.every((user: { storeCode: string }) => user.storeCode === "REV003"));

  const mobileRoot = resolve(import.meta.dirname, "../../..");
  const accountProvisioningSources = await Promise.all([
    readFile(resolve(mobileRoot, "app/(shell)/users/index.tsx"), "utf8"),
    readFile(resolve(mobileRoot, "src/modules/users/api.ts"), "utf8"),
    readFile(resolve(mobileRoot, "src/modules/users/hooks.ts"), "utf8"),
    readFile(resolve(mobileRoot, "src/modules/users/types.ts"), "utf8"),
    readFile(resolve(mobileRoot, "src/modules/ios-review/app-routes.ts"), "utf8"),
    readFile(resolve(mobileRoot, "src/modules/ios-review/identity.ts"), "utf8"),
  ]);
  const accountProvisioningSource = accountProvisioningSources.join("\n");
  assert.match(
    accountProvisioningSource,
    /createStoreUser|StoreUserCreatePayload|createMutation|openCreateDialog|account-plus-outline|Users\.Create/,
    "移动端必须暴露受权限控制的店员创建 API、mutation 和 UI 入口",
  );
  assert.match(
    accountProvisioningSources[4],
    /register\(transport, \["POST"\], "\/react\/v1\/store-users"/,
    "Review mock 必须注册员工账号创建端点",
  );

  const editedReviewUser = await request(
    "PUT",
    "/react/v1/store-users/review-staff-001",
    {
      storeCode: "REV001",
      username: "demo_staff",
      fullName: "Edited Demo Staff",
      status: 1,
    },
  );
  assert.equal(editedReviewUser.fullName, "Edited Demo Staff");
  assert.deepEqual(
    await request(
      "PUT",
      "/react/v1/store-users/review-staff-001/status",
      { storeCode: "REV001", status: 0 },
    ),
    { success: true },
    "新增创建能力后必须保留停用员工能力",
  );
  assert.equal(
    (await request("GET", "/react/v1/store-users/review-staff-001")).status,
    0,
    "员工状态更新必须仍可读回",
  );
  assert.deepEqual(
    await request(
      "PUT",
      "/react/v1/store-users/review-staff-001/password",
      {
        storeCode: "REV001",
        newPassword: "review-reset-password",
        passwordFormat: "raw",
      },
    ),
    { success: true },
    "新增创建能力后必须保留重置密码能力",
  );

  const filteredInstallments = await request(
    "POST",
    "/react/v1/installment-orders/list",
    buildInstallmentOrderListPayload({
      page: 1,
      pageSize: 20,
      filters: {
        branchCode: "REV001",
        status: 3,
        customerName: "Demo Customer",
        customerPhone: "0400",
        startDate: "2026-07-16",
        endDate: "2026-07-16",
      },
    }),
  );
  assert.equal(
    filteredInstallments.total,
    2,
    "Brisbane 本地自然日必须包含前一 UTC 日 23:30 的分期",
  );
  assert.equal(filteredInstallments.items.length, 2);
  assert.equal(filteredInstallments.pageNumber, 1);
  assert.equal(filteredInstallments.pageSize, 20);
  assert.deepEqual(
    filteredInstallments.items.map(
      (item: { installmentGuid?: string }) => item.installmentGuid,
    ),
    ["review-installment-001", "review-installment-002"],
    "筛选结果顺序必须稳定",
  );

  const rejectedInstallmentFilters: {
    label: string;
    body: Record<string, unknown>;
  }[] = [
    { label: "分店", body: { branchCode: "REV999" } },
    { label: "状态", body: { status: 2 } },
    { label: "客户姓名", body: { customerName: "不存在客户" } },
    { label: "客户电话", body: { customerPhone: "0499999999" } },
    { label: "开始日期", body: { startDate: "2026-07-17" } },
    { label: "结束日期", body: { endDate: "2026-07-15" } },
  ];
  for (const filter of rejectedInstallmentFilters) {
    const result = await request(
      "POST",
      "/react/v1/installment-orders/list",
      filter.body,
    );
    assert.equal(result.total, 0, `分期审核列表必须执行${filter.label}筛选`);
    assert.equal(result.items.length, 0);
  }

  const secondInstallmentPage = await request(
    "POST",
    "/react/v1/installment-orders/list",
    buildInstallmentOrderListPayload({ page: 2, pageSize: 20 }),
  );
  assert.equal(secondInstallmentPage.total, 2, "分页后总数仍为筛选结果总数");
  assert.equal(
    secondInstallmentPage.items.length,
    0,
    "超出末页不得重复返回分期",
  );
  assert.equal(secondInstallmentPage.pageNumber, 2);
  assert.equal(secondInstallmentPage.pageSize, 20);

  const firstSingleInstallmentPage = await request(
    "POST",
    "/react/v1/installment-orders/list",
    { ...buildInstallmentOrderListPayload({ page: 1 }), pageSize: 1 },
  );
  const secondSingleInstallmentPage = await request(
    "POST",
    "/react/v1/installment-orders/list",
    { ...buildInstallmentOrderListPayload({ page: 2 }), pageSize: 1 },
  );
  assert.equal(
    firstSingleInstallmentPage.items[0]?.installmentGuid,
    "review-installment-001",
  );
  assert.equal(
    secondSingleInstallmentPage.items[0]?.installmentGuid,
    "review-installment-002",
  );

  const installmentDetail = await request(
    "GET",
    "/react/v1/installment-orders/detail/review-installment-001",
  );
  assert.equal(
    Object.hasOwn(installmentDetail.order, "lines"),
    false,
    "详情主单不得重复嵌套商品行",
  );
  assert.equal(
    Object.hasOwn(installmentDetail.order, "payments"),
    false,
    "详情主单不得重复嵌套付款",
  );

  const normalizedInvoices = normalizeInvoiceGridResponse(
    await request("POST", "/react/v1/local-supplier-invoices/grid", {}),
  );
  assert.equal(
    normalizedInvoices.items[0]?.invoiceNo,
    "REV-INV-0001",
    "离线发票 fixture 必须符合现有业务 normalizer 契约",
  );

  const seasonalCatalogRaw = await request(
    "GET",
    "/react/v1/seasonal-card-remaining/catalog",
  );
  assert.equal(seasonalCatalogRaw[0]?.cardType, 1);
  assert.equal(seasonalCatalogRaw[0]?.priceOption, 1);
  const seasonalCatalog =
    normalizeSeasonalCardCatalogResponse(seasonalCatalogRaw);
  assert.equal(seasonalCatalog[0]?.cardType, 1);
  assert.equal(
    seasonalCatalog[0]?.priceOption,
    1,
    "季节卡 fixture 必须使用业务 normalizer 接受的数字枚举",
  );
  const seasonalSubmissions = normalizeSeasonalCardSubmissionsResponse(
    await request("GET", "/react/v1/seasonal-card-remaining/submissions"),
  );
  assert.equal(seasonalSubmissions.items[0]?.cardType, 1);
  assert.equal(
    seasonalSubmissions.items[0]?.batchGuid,
    "",
    "审核 fixture 的历史单条记录没有批次号，历史页按「未指定供应商」显示",
  );

  // 整组填报：overview → batch 提交 → 再查 overview 已填报 → 未修改 / 抢先提交被拒。
  const reviewSupplier = (
    await request("GET", "/react/v1/local-suppliers/active")
  )[0];
  assert.equal(reviewSupplier?.supplierCode, "REV-SUP-001");
  const seasonalOverviewQuery = {
    storeCode: "REV001",
    seasonYear: 2026,
    localSupplierCode: reviewSupplier.supplierCode,
  };
  const emptyOverview = normalizeSeasonalCardOverviewResponse(
    await request(
      "GET",
      "/react/v1/seasonal-card-remaining/overview",
      undefined,
      seasonalOverviewQuery,
    ),
  );
  assert.deepEqual(
    emptyOverview.holidays.map((holiday) => holiday.currentBatch),
    [null, null, null, null, null],
    "审核模式初始没有任何整组填报",
  );
  const christmasOptions = getSeasonalCardOptionsForType(seasonalCatalog, 1);
  assert.equal(christmasOptions.length, 4, "审核目录每个节日 4 个价格");
  const seasonalCombo = {
    storeCode: "REV001",
    seasonYear: 2026,
    cardType: 1 as const,
    localSupplierCode: reviewSupplier.supplierCode,
  };
  const seasonalDraft = createSeasonalCardDraft("review", christmasOptions, null);
  seasonalDraft.quantities[christmasOptions[0]!.catalogGuid] = "12";
  seasonalDraft.quantities[christmasOptions[3]!.catalogGuid] = "2";
  seasonalDraft.customUnitPrice = "4.5";
  const createdBatch = normalizeSeasonalCardBatchResponse(
    await request(
      "POST",
      "/react/v1/seasonal-card-remaining/submissions/batch",
      buildSeasonalCardBatchRequest(
        buildSeasonalCardBatchPayload(seasonalCombo, seasonalDraft, christmasOptions),
      ),
    ),
  );
  assert.ok(createdBatch?.batchGuid, "批量提交返回新批次号");
  assert.equal(createdBatch?.totalQuantity, 14);
  assert.equal(createdBatch?.lines.length, 4);
  const filledOverview = normalizeSeasonalCardOverviewResponse(
    await request(
      "GET",
      "/react/v1/seasonal-card-remaining/overview",
      undefined,
      seasonalOverviewQuery,
    ),
  );
  assert.equal(
    filledOverview.holidays[0]?.currentBatch?.batchGuid,
    createdBatch?.batchGuid,
    "提交后 overview 返回该批次为当前生效",
  );
  assert.equal(filledOverview.holidays[1]?.currentBatch, null);
  const prefilledDraft = createSeasonalCardDraft(
    "review",
    christmasOptions,
    filledOverview.holidays[0]?.currentBatch ?? null,
  );
  const unchangedResponse = await request(
    "POST",
    "/react/v1/seasonal-card-remaining/submissions/batch",
    buildSeasonalCardBatchRequest(
      buildSeasonalCardBatchPayload(seasonalCombo, prefilledDraft, christmasOptions),
    ),
  );
  assert.equal(unchangedResponse.success, false);
  assert.equal(unchangedResponse.errorCode, "SEASONAL_CARD_NO_CHANGES");
  const staleResponse = await request(
    "POST",
    "/react/v1/seasonal-card-remaining/submissions/batch",
    buildSeasonalCardBatchRequest(
      buildSeasonalCardBatchPayload(seasonalCombo, seasonalDraft, christmasOptions),
    ),
  );
  assert.equal(staleResponse.success, false);
  assert.equal(
    staleResponse.errorCode,
    "SEASONAL_CARD_STALE",
    "预填后被抢先提交（expectedPreviousBatchGuid 过期）返回 STALE",
  );
  assert.equal(staleResponse.details?.batchGuid, createdBatch?.batchGuid);
  const batchedHistory = normalizeSeasonalCardSubmissionsResponse(
    await request("GET", "/react/v1/seasonal-card-remaining/submissions"),
  );
  assert.equal(
    batchedHistory.items.filter((item) => item.batchGuid === createdBatch?.batchGuid).length,
    4,
    "历史列表带批次号，4 行可合并成一张卡片",
  );
  assert.equal(batchedHistory.items[0]?.supplierName, "Demo Local Supplier");

  const secondStoreSchedule = await request(
    "POST",
    "/react/v1/attendance/schedules",
    {
      storeCode: "REV002",
      userGuid: "review-user",
      workDate: "2026-07-16",
      startTime: "09:00",
      endTime: "17:00",
      status: "Active",
    },
  );
  const secondStoreToday = await request(
    "GET",
    "/react/v1/attendance/my/today?storeCode=REV002&workDate=2026-07-16",
  );
  assert.equal(secondStoreToday.workDate, "2026-07-16");
  assert.equal(secondStoreToday.schedules.length, 1);
  assert.equal(secondStoreToday.schedules[0]?.storeCode, "REV002");
  assert.equal(secondStoreToday.punches.length, 0);
  assert.equal(secondStoreToday.holidays.length, 0);

  const historicalToday = await request(
    "GET",
    "/react/v1/attendance/my/today?storeCode=REV001&workDate=2026-07-15",
  );
  assert.equal(historicalToday.workDate, "2026-07-15");
  assert.equal(historicalToday.schedules.length, 0, "历史日期不能泄漏当天排班");
  assert.equal(historicalToday.punches.length, 0, "历史日期不能泄漏当天打卡");
  assert.equal(historicalToday.holidays.length, 0, "历史日期不能泄漏当天节假日");

  const outOnlyPayload = {
    storeCode: "REV001",
    scheduleGuid: "review-schedule-001",
    punchType: "ClockOut",
    requestedPunchTimeLocal: "2026-07-16T09:00:00",
    reason: "Invalid out-only review fixture",
  };
  const outOnlyPreview = await request(
    "POST",
    "/react/v1/attendance/my/punch-adjustments/preview",
    outOnlyPayload,
  );
  assert.equal(outOnlyPreview.isValid, false);
  assert.equal(outOnlyPreview.validationErrorCode, "PUNCH_SEQUENCE_OUT_WITHOUT_IN");
  await assert.rejects(
    () => request(
      "POST",
      "/react/v1/attendance/my/punch-adjustments",
      outOnlyPayload,
    ),
    /PUNCH_SEQUENCE_OUT_WITHOUT_IN/,
    "create 不能绕过 preview 的 out-only 校验",
  );

  const utcPriorityPreview = await request(
    "POST",
    "/react/v1/attendance/my/punch-adjustments/preview",
    {
      storeCode: "REV001",
      scheduleGuid: "review-schedule-001",
      punchType: "ClockIn",
      requestedPunchTimeLocal: "2026-07-16T09:00:00",
      // 故意与 local 的 Brisbane 推导值不同：Review mock 必须把 UTC 作为权威 instant。
      requestedPunchTimeUtc: "2026-07-16T01:00:00Z",
      reason: "UTC priority review fixture",
    },
  );
  assert.equal(utcPriorityPreview.isValid, true);
  assert.equal(
    utcPriorityPreview.proposedSession.segments[0]?.clockIn?.punchTimeUtc,
    "2026-07-16T01:00:00Z",
    "iOS Review mock 必须优先使用 requestedPunchTimeUtc，而不是重新从 local 推导 instant",
  );

  let todayAttendance = await request(
    "GET",
    "/react/v1/attendance/my/today?storeCode=REV001",
  );
  assert.deepEqual(
    {
      punchCount: todayAttendance.punches.length,
      nextPunchType: todayAttendance.nextPunchType,
      canClockIn: todayAttendance.canClockIn,
      canClockOut: todayAttendance.canClockOut,
    },
    {
      punchCount: 0,
      nextPunchType: "ClockIn",
      canClockIn: true,
      canClockOut: false,
    },
  );
  await punchWithReviewQr(request, reviewQrToken(20));
  todayAttendance = await request(
    "GET",
    "/react/v1/attendance/my/today?storeCode=REV001",
  );
  assert.deepEqual(
    {
      punchCount: todayAttendance.punches.length,
      nextPunchType: todayAttendance.nextPunchType,
      canClockIn: todayAttendance.canClockIn,
      canClockOut: todayAttendance.canClockOut,
    },
    {
      punchCount: 1,
      nextPunchType: "ClockOut",
      canClockIn: false,
      canClockOut: true,
    },
    "ClockIn 后 refetch 必须显示已打卡并允许 ClockOut",
  );
  const consecutiveClockInPreview = await request(
    "POST",
    "/react/v1/attendance/my/punch-adjustments/preview",
    {
      storeCode: "REV001",
      scheduleGuid: "review-schedule-001",
      punchType: "ClockIn",
      requestedPunchTimeLocal: "2026-07-16T10:15:00",
      reason: "Invalid consecutive clock-in",
    },
  );
  assert.equal(consecutiveClockInPreview.isValid, false);
  assert.equal(
    consecutiveClockInPreview.validationErrorCode,
    "PUNCH_SEQUENCE_CONSECUTIVE_TYPE",
  );
  await punchWithReviewQr(request, reviewQrToken(21));
  todayAttendance = await request(
    "GET",
    "/react/v1/attendance/my/today?storeCode=REV001",
  );
  assert.equal(todayAttendance.punches.length, 2);
  assert.deepEqual(
    {
      nextPunchType: todayAttendance.nextPunchType,
      canClockIn: todayAttendance.canClockIn,
      canClockOut: todayAttendance.canClockOut,
    },
    {
      nextPunchType: "ClockIn",
      canClockIn: true,
      canClockOut: false,
    },
    "第一段完成后必须允许继续第二段 ClockIn",
  );

  const originalClockOut = todayAttendance.punches.find(
    (punch: { punchType?: string }) => punch.punchType === "ClockOut",
  );
  const adjustmentPayload = {
    storeCode: "REV001",
    scheduleGuid: "review-schedule-001",
    originalPunchGuid: originalClockOut.punchGuid,
    punchType: "ClockOut",
    requestedPunchTimeLocal: "2026-07-16T17:00:00",
    reason: "App Review correction",
  };
  const adjustmentPreview = await request(
    "POST",
    "/react/v1/attendance/my/punch-adjustments/preview",
    adjustmentPayload,
  );
  assert.equal(adjustmentPreview.isValid, true);
  assert.equal(adjustmentPreview.wouldAutoApprove, true);
  assert.equal(adjustmentPreview.existingSession.scheduleGuid, "review-schedule-001");
  assert.equal(adjustmentPreview.existingSession.segmentLimit, 3);
  assert.equal(adjustmentPreview.proposedSession.segmentLimit, 3);
  assert.equal(adjustmentPreview.proposedSession.segments[0].clockOut.punchTimeLocal, "2026-07-16T17:00:00");
  assert.equal(adjustmentPreview.existingSession.workedMinutes, 0);
  assert.equal(adjustmentPreview.proposedSession.workedMinutes, 420);
  assert.equal(adjustmentPreview.workedMinutesDelta, 420);
  assert.equal(adjustmentPreview.candidateOvertimeMinutesDelta, 0);

  const createdAdjustment = await request(
    "POST",
    "/react/v1/attendance/my/punch-adjustments",
    adjustmentPayload,
  );
  assert.equal(createdAdjustment.status, "Applied");
  assert.equal(createdAdjustment.originalPunchGuid, originalClockOut.punchGuid);

  const adjustmentRows = await request(
    "GET",
    "/react/v1/attendance/my/punch-adjustments",
  );
  assert.equal(adjustmentRows.length, 1, "补卡提交后必须保存在 Review route state");
  assert.equal(adjustmentRows[0].adjustmentGuid, createdAdjustment.adjustmentGuid);

  todayAttendance = await request(
    "GET",
    "/react/v1/attendance/my/today?storeCode=REV001",
  );
  assert.equal(todayAttendance.punches.length, 2, "修改原打卡不能同时保留旧卡造成重复班段");
  assert.equal(
    todayAttendance.punches.find(
      (punch: { punchType?: string }) => punch.punchType === "ClockOut",
    )?.punchTimeLocal,
    "2026-07-16T17:00:00",
    "补卡提交后的 Today refetch 必须立即反映修正时间",
  );
  assert.equal(todayAttendance.canClockIn, true);
  assert.equal(todayAttendance.canClockOut, false);

  const createMissingPunch = async (
    punchType: "ClockIn" | "ClockOut",
    requestedPunchTimeLocal: string,
  ) => {
    const payload = {
      storeCode: "REV001",
      scheduleGuid: "review-schedule-001",
      punchType,
      requestedPunchTimeLocal,
      reason: `Create review ${punchType}`,
    };
    const preview = await request(
      "POST",
      "/react/v1/attendance/my/punch-adjustments/preview",
      payload,
    );
    assert.equal(preview.isValid, true, `${requestedPunchTimeLocal} 应属于店长允许的三段内`);
    return request(
      "POST",
      "/react/v1/attendance/my/punch-adjustments",
      payload,
    );
  };
  await createMissingPunch("ClockIn", "2026-07-16T17:15:00");
  await createMissingPunch("ClockOut", "2026-07-16T17:30:00");
  await createMissingPunch("ClockIn", "2026-07-16T17:45:00");
  await createMissingPunch("ClockOut", "2026-07-16T18:00:00");
  todayAttendance = await request(
    "GET",
    "/react/v1/attendance/my/today?storeCode=REV001&workDate=2026-07-16",
  );
  assert.deepEqual(
    {
      nextPunchType: todayAttendance.nextPunchType,
      canClockIn: todayAttendance.canClockIn,
      canClockOut: todayAttendance.canClockOut,
    },
    {
      nextPunchType: "ClockIn",
      canClockIn: false,
      canClockOut: false,
    },
    "第三段完成后必须关闭继续 ClockIn",
  );

  const fourthSegmentPayload = {
    storeCode: "REV001",
    scheduleGuid: "review-schedule-001",
    punchType: "ClockIn",
    requestedPunchTimeLocal: "2026-07-16T18:15:00",
    reason: "Fourth manager segment must fail",
  };
  const fourthSegmentPreview = await request(
    "POST",
    "/react/v1/attendance/my/punch-adjustments/preview",
    fourthSegmentPayload,
  );
  assert.equal(fourthSegmentPreview.isValid, false);
  assert.equal(
    fourthSegmentPreview.validationErrorCode,
    "SEGMENT_LIMIT_REACHED",
  );
  await assert.rejects(
    () => request(
      "POST",
      "/react/v1/attendance/my/punch-adjustments",
      fourthSegmentPayload,
    ),
    /SEGMENT_LIMIT_REACHED/,
    "create 不能绕过店长每日每店三段上限",
  );

  const secondStorePreview = await request(
    "POST",
    "/react/v1/attendance/my/punch-adjustments/preview",
    {
      storeCode: "REV002",
      scheduleGuid: secondStoreSchedule.scheduleGuid,
      punchType: "ClockIn",
      requestedPunchTimeLocal: "2026-07-16T09:00:00",
      reason: "Cross-store independent segment count",
    },
  );
  assert.equal(secondStorePreview.isValid, true, "REV001 三段不能占用 REV002 的独立上限");

  const getNormalizedDevices = async () =>
    normalizeDeviceManagementListResponse(
      await request("GET", "/mobile/device-management/paged"),
    ).devices;
  assert.equal((await getNormalizedDevices())[0]?.status, 1);
  await request("POST", "/mobile/device-management/review-device-001/disable");
  assert.equal((await getNormalizedDevices())[0]?.status, 0);
  await request("POST", "/mobile/device-management/review-device-001/lock");
  assert.equal((await getNormalizedDevices())[0]?.status, 2);
  await request("POST", "/mobile/device-management/review-device-001/activate");
  assert.equal((await getNormalizedDevices())[0]?.status, 1);

  type ReviewRevenueMetrics = {
    revenue: number;
    revenueLY: number;
    transactions: number;
    transactionsLY: number;
  };
  const sumReviewRevenueMetrics = (
    items: ReviewRevenueMetrics[],
  ): ReviewRevenueMetrics => items.reduce(
    (totals, item) => ({
      revenue: totals.revenue + item.revenue,
      revenueLY: totals.revenueLY + item.revenueLY,
      transactions: totals.transactions + item.transactions,
      transactionsLY: totals.transactionsLY + item.transactionsLY,
    }),
    { revenue: 0, revenueLY: 0, transactions: 0, transactionsLY: 0 },
  );
  const assertReviewRevenueMetricsConserved = (
    actual: ReviewRevenueMetrics,
    expected: ReviewRevenueMetrics,
    label: string,
  ) => {
    assert.ok(
      Math.abs(actual.revenue - expected.revenue) <= 0.01,
      `${label}：营业额误差必须不超过 0.01`,
    );
    assert.ok(
      Math.abs(actual.revenueLY - expected.revenueLY) <= 0.01,
      `${label}：同期营业额误差必须不超过 0.01`,
    );
    assert.equal(actual.transactions, expected.transactions, `${label}：交易数必须守恒`);
    assert.equal(
      actual.transactionsLY,
      expected.transactionsLY,
      `${label}：同期交易数必须守恒`,
    );
  };

  const reviewExecutivePayload = await request(
    "GET",
    "/react/v1/dashboard/executive-branch-performance",
  );
  const reviewExecutiveSnapshot = normalizeExecutiveBranchPerformance(reviewExecutivePayload);
  assert.equal(reviewExecutiveSnapshot.isComplete, true, "审核 mock 必须声明营业额分店快照完整计数");
  assert.equal(reviewExecutiveSnapshot.rows.length, 28, "审核营业额排行必须返回 28 家连续分店");
  assert.equal(
    new Set(reviewExecutiveSnapshot.rows.map((row) => row.branchCode)).size,
    28,
    "审核营业额排行的 28 家分店代码必须唯一，页面才能按索引显示连续行号",
  );
  assert.equal(
    reviewExecutiveSnapshot.rows.every(
      (row, index, rows) => index === 0 || rows[index - 1]!.revenue >= row.revenue,
    ),
    true,
    "审核营业额排行必须与真实后端一致，按营业额降序返回",
  );
  assert.equal(
    normalizeProductReportTotalRevenue(reviewExecutivePayload).isComplete,
    true,
    "审核 mock 的总额必须同时声明商品统计完整性元数据",
  );
  const selectedStoreExecutiveSnapshot = normalizeExecutiveBranchPerformance(
    await request(
      "GET",
      "/react/v1/dashboard/executive-branch-performance",
      undefined,
      { branchCodes: ["REV002"] },
    ),
  );
  assert.deepEqual(
    selectedStoreExecutiveSnapshot.rows.map((row) => row.branchCode),
    ["REV002"],
    "商品报告选择单店后总额数据源必须只返回该授权分店",
  );
  const reviewHourlyPayload = await request(
    "GET",
    "/react/v1/dashboard/executive-hourly-traffic",
    undefined,
    { startDate: "2026-07-16", endDate: "2026-07-16" },
  );
  // 与真实接口一致：多店分时按「店×小时」返回，日报累计对比才能按店对齐排行。
  const reviewHourlySnapshot = normalizeBranchHourlyRevenueSnapshot(
    reviewHourlyPayload,
  );
  assert.equal(
    reviewHourlySnapshot.isComplete,
    true,
    "审核 mock 分时表必须声明完整项目计数",
  );
  assert.equal(
    reviewHourlySnapshot.rows.length,
    IOS_REVIEW_STORES.length * 14,
    "单日分时表必须逐店覆盖 08:00 至 21:00 的 14 个营业时段",
  );
  assert.deepEqual(
    [...new Set(reviewHourlySnapshot.rows.map((row) => row.hour))],
    Array.from({ length: 14 }, (_, index) => index + 8),
  );
  assert.equal(
    new Set(reviewHourlySnapshot.rows.map((row) => row.branchCode)).size,
    IOS_REVIEW_STORES.length,
    "多店分时每行都必须带分店代码",
  );
  assert.equal(reviewHourlyPayload.statisticsExpectedItemCount, IOS_REVIEW_STORES.length * 14);
  assert.equal(reviewHourlyPayload.statisticsSnapshotItemCount, IOS_REVIEW_STORES.length * 14);
  const reviewDayExecutiveRows = normalizeExecutiveBranchPerformance(
    await request(
      "GET",
      "/react/v1/dashboard/executive-branch-performance",
      undefined,
      { startDate: "2026-07-16", endDate: "2026-07-16" },
    ),
  ).rows;
  for (const branchRow of reviewDayExecutiveRows) {
    const branchHours = reviewHourlySnapshot.rows.filter((row) => row.branchCode === branchRow.branchCode);
    const sum = (pick: (row: (typeof branchHours)[number]) => number) =>
      Math.round(branchHours.reduce((total, row) => total + pick(row), 0) * 100) / 100;
    assert.equal(sum((row) => row.revenue), branchRow.revenue, `${branchRow.branchCode} 分时营业额必须与排行守恒`);
    assert.equal(sum((row) => row.compareRevenue), branchRow.compareRevenue, `${branchRow.branchCode} 分时同期必须与排行守恒`);
    assert.equal(sum((row) => row.transactions), branchRow.transactions, `${branchRow.branchCode} 分时交易数必须与排行守恒`);
  }

  const selectedStoreHourlyPayload = await request(
    "GET",
    "/react/v1/dashboard/executive-hourly-traffic",
    undefined,
    {
      startDate: "2026-07-16",
      endDate: "2026-07-16",
      branchCodes: ["REV002"],
    },
  );
  assert.equal(
    normalizeHourlyRevenueSnapshot(selectedStoreHourlyPayload).rows.length,
    14,
    "选择单店仍须返回完整营业时段",
  );
  assert.ok(
    selectedStoreHourlyPayload.items.reduce(
      (total: number, row: { revenue: number }) => total + row.revenue,
      0,
    )
      < reviewHourlyPayload.items.reduce(
        (total: number, row: { revenue: number }) => total + row.revenue,
        0,
    ),
    "分时表选择单店后只能汇总该店，不能泄漏其余 27 店营业额",
  );
  const anotherStoreHourlyPayload = await request(
    "GET",
    "/react/v1/dashboard/executive-hourly-traffic",
    undefined,
    {
      startDate: "2026-07-16",
      endDate: "2026-07-16",
      branchCodes: ["REV001"],
    },
  );
  assert.notDeepEqual(
    selectedStoreHourlyPayload.items.map(
      (row: { revenue: number }) => row.revenue,
    ),
    anotherStoreHourlyPayload.items.map(
      (row: { revenue: number }) => row.revenue,
    ),
    "相同分店数量但不同 branchCodes 必须得到各自的分时数据",
  );
  const unknownStoreHourlyPayload = await request(
    "GET",
    "/react/v1/dashboard/executive-hourly-traffic",
    undefined,
    {
      startDate: "2026-07-16",
      endDate: "2026-07-16",
      branchCodes: ["NOT-A-REVIEW-STORE"],
    },
  );
  assert.equal(
    unknownStoreHourlyPayload.items.every(
      (row: { revenue: number; transactions: number }) =>
        row.revenue === 0 && row.transactions === 0,
    ),
    true,
    "分时表无匹配分店时必须返回零汇总，不能回退到全店数据",
  );

  const reviewDailyPayload = await request(
    "GET",
    "/react/v1/dashboard/branch-daily-performance",
    undefined,
    {
      startDate: "2026-07-13",
      endDate: "2026-07-19",
      branchCodes: ["REV001", "REV003"],
    },
  );
  const reviewDailySnapshot = normalizeDailyRevenueSnapshot(reviewDailyPayload);
  assert.equal(
    reviewDailySnapshot.isComplete,
    true,
    "审核 mock 逐日表必须声明完整项目计数",
  );
  assert.equal(
    reviewDailySnapshot.rows.length,
    14,
    "周范围必须逐日生成 7 天 × 2 家已选分店",
  );
  assert.deepEqual(
    [...new Set(reviewDailyPayload.items.map((row: { date: string }) => row.date))],
    [
      "2026-07-13",
      "2026-07-14",
      "2026-07-15",
      "2026-07-16",
      "2026-07-17",
      "2026-07-18",
      "2026-07-19",
    ],
    "逐日表必须覆盖 startDate 到 endDate 的每一个自然日",
  );
  assert.deepEqual(
    [
      ...new Set(
        reviewDailyPayload.items.map(
          (row: { branchCode: string }) => row.branchCode,
        ),
      ),
    ],
    ["REV001", "REV003"],
    "逐日表不能泄漏未选择分店",
  );
  assert.equal(reviewDailyPayload.statisticsExpectedItemCount, 14);
  assert.equal(reviewDailyPayload.statisticsSnapshotItemCount, 14);

  const allStoreDailyPayload = await request(
    "GET",
    "/react/v1/dashboard/branch-daily-performance",
    undefined,
    { startDate: "2026-07-16", endDate: "2026-07-16" },
  );
  assert.equal(allStoreDailyPayload.items.length, IOS_REVIEW_STORES.length);
  assert.equal(
    new Set(
      allStoreDailyPayload.items.map(
        (row: { branchCode: string }) => row.branchCode,
      ),
    ).size,
    IOS_REVIEW_STORES.length,
    "未筛选时逐日表必须保留全部 28 家唯一分店",
  );
  assert.equal(
    allStoreDailyPayload.statisticsExpectedItemCount,
    allStoreDailyPayload.items.length,
  );
  assert.equal(
    allStoreDailyPayload.statisticsSnapshotItemCount,
    allStoreDailyPayload.items.length,
  );

  const reviewMonthlyDailyPayload = await request(
    "GET",
    "/react/v1/dashboard/branch-daily-performance",
    undefined,
    {
      startDate: "2026-07-01",
      endDate: "2026-07-31",
      branchCodes: ["REV002"],
    },
  );
  assert.equal(
    normalizeDailyRevenueSnapshot(reviewMonthlyDailyPayload).isComplete,
    true,
  );
  assert.equal(
    reviewMonthlyDailyPayload.items.length,
    31,
    "月范围必须覆盖完整日期，不得只返回当天 fixture",
  );
  assert.equal(reviewMonthlyDailyPayload.items[0]?.date, "2026-07-01");
  assert.equal(reviewMonthlyDailyPayload.items.at(-1)?.date, "2026-07-31");
  assert.equal(
    reviewMonthlyDailyPayload.items.every(
      (row: { branchCode: string }) => row.branchCode === "REV002",
    ),
    true,
    "月范围也不能泄漏未选择分店",
  );
  assert.equal(reviewMonthlyDailyPayload.statisticsExpectedItemCount, 31);
  assert.equal(reviewMonthlyDailyPayload.statisticsSnapshotItemCount, 31);

  const allStoreMonthlyDailyPayload = await request(
    "GET",
    "/react/v1/dashboard/branch-daily-performance",
    undefined,
    { startDate: "2026-07-01", endDate: "2026-07-31" },
  );
  assert.equal(
    allStoreMonthlyDailyPayload.items.length,
    31 * IOS_REVIEW_STORES.length,
    "未筛选月范围必须完整生成 31 天 × 28 家分店",
  );
  assert.equal(
    allStoreMonthlyDailyPayload.statisticsExpectedItemCount,
    allStoreMonthlyDailyPayload.items.length,
  );
  assert.equal(
    allStoreMonthlyDailyPayload.statisticsSnapshotItemCount,
    allStoreMonthlyDailyPayload.items.length,
  );

  const assertExecutiveMatchesDaily = async (
    label: string,
    params: Record<string, unknown>,
  ) => {
    const executivePayload = await request(
      "GET",
      "/react/v1/dashboard/executive-branch-performance",
      undefined,
      params,
    );
    const dailyPayload = await request(
      "GET",
      "/react/v1/dashboard/branch-daily-performance",
      undefined,
      params,
    );
    assert.equal(
      normalizeExecutiveBranchPerformance(executivePayload).isComplete,
      true,
      `${label}：分店排行必须保持完整性元数据`,
    );
    assert.equal(
      normalizeDailyRevenueSnapshot(dailyPayload).isComplete,
      true,
      `${label}：逐日明细必须保持完整性元数据`,
    );
    for (const executiveRow of executivePayload.items as (
      ReviewRevenueMetrics & { branchCode: string }
    )[]) {
      const dailyRows = (dailyPayload.items as (
        ReviewRevenueMetrics & { branchCode: string }
      )[]).filter((row) => row.branchCode === executiveRow.branchCode);
      assert.ok(dailyRows.length > 0, `${label}：每家排行分店都必须有逐日明细`);
      assertReviewRevenueMetricsConserved(
        executiveRow,
        sumReviewRevenueMetrics(dailyRows),
        `${label}/${executiveRow.branchCode}`,
      );
    }
  };
  for (const reportCase of [
    {
      label: "单日全店",
      params: { startDate: "2026-07-17", endDate: "2026-07-17" },
    },
    {
      label: "单日单店",
      params: {
        startDate: "2026-07-17",
        endDate: "2026-07-17",
        branchCodes: ["REV002"],
      },
    },
    {
      label: "周范围全店",
      params: { startDate: "2026-07-13", endDate: "2026-07-19" },
    },
    {
      label: "周范围单店",
      params: {
        startDate: "2026-07-13",
        endDate: "2026-07-19",
        branchCodes: ["REV002"],
      },
    },
    {
      label: "月范围全店",
      params: { startDate: "2026-07-01", endDate: "2026-07-31" },
    },
    {
      label: "月范围单店",
      params: {
        startDate: "2026-07-01",
        endDate: "2026-07-31",
        branchCodes: ["REV002"],
      },
    },
  ]) {
    await assertExecutiveMatchesDaily(reportCase.label, reportCase.params);
  }

  const assertHourlyMatchesExecutive = async (
    label: string,
    params: Record<string, unknown>,
  ) => {
    const executivePayload = await request(
      "GET",
      "/react/v1/dashboard/executive-branch-performance",
      undefined,
      params,
    );
    const hourlyPayload = await request(
      "GET",
      "/react/v1/dashboard/executive-hourly-traffic",
      undefined,
      params,
    );
    assert.equal(
      hourlyPayload.items.length,
      14 * executivePayload.items.length,
      `${label}：必须逐店保留 14 个营业时段`,
    );
    assertReviewRevenueMetricsConserved(
      sumReviewRevenueMetrics(hourlyPayload.items),
      sumReviewRevenueMetrics(executivePayload.items),
      label,
    );
  };
  await assertHourlyMatchesExecutive("单日全店分时守恒", {
    startDate: "2026-07-17",
    endDate: "2026-07-17",
  });
  await assertHourlyMatchesExecutive("单日单店分时守恒", {
    startDate: "2026-07-17",
    endDate: "2026-07-17",
    branchCodes: ["REV002"],
  });

  const supplierPayload = await request("GET", "/react/v1/dashboard/supplier-sales-rank");
  const supplierSnapshot = normalizeSupplierReportSnapshot(supplierPayload);
  assert.equal(supplierSnapshot.isComplete, true, "审核 mock 供应商排行必须声明 Fresh 统计批次");
  const supplierRows = normalizeSupplierRows(
    supplierPayload,
  );
  assert.equal(supplierRows.length, 8, "审核 mock 供应商排行至少需要 8 行可验收数据");
  assert.equal(
    new Set(supplierRows.map((row) => row.supplierCode)).size,
    8,
    "审核 mock 供应商代码必须唯一",
  );
  assert.equal(
    supplierRows.every((row) => row.storeCount === IOS_REVIEW_STORES.length),
    true,
    "每个审核供应商都必须覆盖真实的 28 家分店",
  );

  // 分店中国货合计与真实接口同形逐店返回；全店、单店范围下逐店之和都必须等于中国供应商排行合计。
  const assertChinaBranchTotalsConserved = async (label: string, query: Record<string, unknown>) => {
    const rankPayload = await request("GET", "/react/v1/dashboard/china-supplier-sales-rank", undefined, query);
    const branchPayload = await request("GET", "/react/v1/dashboard/china-supplier-branch-totals", undefined, query);
    const branchSnapshot = normalizeChinaSupplierBranchTotalsSnapshot(branchPayload);
    assert.equal(branchSnapshot.isComplete, true, `${label}：必须声明 Fresh 统计批次`);
    assert.equal(
      branchSnapshot.cacheVersion,
      normalizeSupplierReportSnapshot(rankPayload).cacheVersion,
      `${label}：必须与供应商排行属于同一统计批次`,
    );
    const rankTotal = normalizeSupplierRows(rankPayload).reduce((sum, row) => sum + row.revenue, 0);
    const branchTotal = branchSnapshot.data.reduce((sum, row) => sum + row.revenue, 0);
    assert.equal(Math.round(branchTotal * 100), Math.round(rankTotal * 100), `${label}：逐店之和必须等于排行合计`);
    return branchSnapshot.data;
  };
  const allChinaBranches = await assertChinaBranchTotalsConserved("全店中国货合计守恒", {});
  assert.equal(allChinaBranches.length, IOS_REVIEW_STORES.length, "全店范围必须逐店返回 28 行");
  assert.ok(
    allChinaBranches.every((row) => row.costStatus === "Complete" && row.grossProfit !== null),
    "审核分店中国货合计必须带完整成本，顶部毛利率才能验收",
  );
  const singleChinaBranches = await assertChinaBranchTotalsConserved("单店中国货合计守恒", {
    branchCodes: ["REV002"],
  });
  assert.deepEqual(singleChinaBranches.map((row) => row.branchCode), ["REV002"], "单店范围只能返回该店");
  assert.equal(
    supplierPayload.items.every((item: Record<string, unknown>) =>
      [
        "totalAmount",
        "costAmount",
        "grossProfit",
        "grossMarginRate",
        "orderCount",
        "averageTransaction",
        "compareTotalAmount",
        "compareCostAmount",
        "compareGrossProfit",
        "compareGrossMarginRate",
        "compareOrderCount",
        "compareAverageTransaction",
      ].every((key) => typeof item[key] === "number"),
    ),
    true,
    "供应商排行必须显式提供营业额、成本、毛利、毛利率、订单数、客单价及同期字段",
  );
  assert.equal(supplierRows[0]?.revenue, 1250);
  assert.equal(supplierRows[0]?.totalQuantity, 36);
  assert.equal(supplierRows[0]?.orderCount, 8);
  assert.notEqual(supplierRows[0]?.grossProfit, null);
  assert.notEqual(supplierRows[0]?.compareGrossProfit, null);
  const selectedStoreSupplierPayload = await request(
    "GET",
    "/react/v1/dashboard/supplier-sales-rank",
    undefined,
    { branchCodes: ["REV002"] },
  );
  const selectedStoreSupplierRows = normalizeSupplierRows(selectedStoreSupplierPayload);
  assert.equal(
    selectedStoreSupplierRows.every((row) => row.storeCount === 1),
    true,
    "供应商表选择单店后必须显示 1 家覆盖门店",
  );
  assert.ok(
    selectedStoreSupplierRows[0]!.revenue < supplierRows[0]!.revenue,
    "供应商表选择单店后金额必须按该店范围缩小",
  );
  const anotherStoreSupplierPayload = await request(
    "GET",
    "/react/v1/dashboard/supplier-sales-rank",
    undefined,
    { branchCodes: ["REV001"] },
  );
  assert.notEqual(
    anotherStoreSupplierPayload.items[0]?.totalAmount,
    selectedStoreSupplierPayload.items[0]?.totalAmount,
    "供应商表必须按具体分店 fixture 计算，REV001 与 REV002 不能只有相同的数量比例",
  );
  const unknownStoreSupplierPayload = await request(
    "GET",
    "/react/v1/dashboard/supplier-sales-rank",
    undefined,
    { branchCodes: ["NOT-A-REVIEW-STORE"] },
  );
  assert.equal(
    unknownStoreSupplierPayload.items.every(
      (item: Record<string, number>) =>
        item.storeCount === 0
        && [
          "totalAmount",
          "costAmount",
          "grossProfit",
          "grossMarginRate",
          "compareTotalAmount",
          "compareCostAmount",
          "compareGrossProfit",
          "compareGrossMarginRate",
          "totalQuantity",
          "orderCount",
          "compareOrderCount",
          "averageTransaction",
          "compareAverageTransaction",
        ].every((key) => item[key] === 0),
    ),
    true,
    "供应商表未知分店必须返回零指标，不能回退或泄漏全店数据",
  );

  const freshness = normalizeStatisticsFreshness(
    await request("GET", "/react/v1/dashboard/statistics-freshness"),
  );
  assert.ok(freshness.lastSuccessfulAtUtc);
  assert.equal(freshness.latestRunStatus, "Success");

  const promotions = normalizePromotionsResponse(
    await request("POST", "/react/v1/promotions/store/grid", {}),
  );
  assert.equal(
    promotions.items[0]?.scopeType,
    "StoreOnly",
    "分店促销 fixture 必须使用页面可识别的 StoreOnly scope",
  );
  const productPromotions = normalizeValidPromotionsResponse(
    await request(
      "GET",
      "/react/v1/promotions/valid/by-product?productCode=REV-PROD-001&storeCode=REV001",
    ),
  );
  assert.equal(productPromotions.length, 1, "审核模式商品详情应返回当前有效促销");
  assert.equal(productPromotions[0]?.name, "Demo Mug Bundle");

  const createProductPromotion = async (
    name: string,
    overrides: Record<string, unknown> = {},
  ) => request("POST", "/react/v1/promotions/store", {
    name,
    isEnabled: true,
    effectiveStart: "2026-07-14T00:00:00.000Z",
    effectiveEnd: "2026-07-17T00:00:00.000Z",
    priority: 0,
    applyQuantity: 2,
    fixedPrice: 20,
    products: [{ productCode: "REV-PROD-001" }],
    stores: [{ storeCode: "REV001" }],
    ...overrides,
  });
  const samePriorityFirst = await createProductPromotion("同优先级活动 A", {
    priority: 30,
    effectiveStart: "2026-07-13T00:00:00.000Z",
  });
  const samePrioritySecond = await createProductPromotion("同优先级活动 B", {
    priority: 30,
    effectiveStart: "2026-07-13T00:00:00.000Z",
  });
  const earlierActivity = await createProductPromotion("较早开始活动", {
    priority: 20,
    effectiveStart: "2026-07-12T00:00:00.000Z",
  });
  const laterActivity = await createProductPromotion("较晚开始活动", {
    priority: 20,
    effectiveStart: "2026-07-14T00:00:00.000Z",
  });
  await createProductPromotion("总部活动", {
    priority: 15,
    // 全部门店关联已软删除时，应按总部活动处理。
    stores: [{ storeCode: "REV002", isDeleted: true }],
  });
  await createProductPromotion("其他门店活动", {
    priority: 100,
    stores: [{ storeCode: "REV002" }],
  });
  await createProductPromotion("已删除活动", {
    priority: 100,
    isDeleted: true,
  });
  await createProductPromotion("已删除商品关联", {
    priority: 100,
    products: [{ productCode: "REV-PROD-001", isDeleted: true }],
  });
  await createProductPromotion("已删除当前门店关联", {
    priority: 100,
    stores: [
      { storeCode: "REV001", isDeleted: true },
      { storeCode: "REV002" },
    ],
  });

  const filteredProductPromotions = normalizeValidPromotionsResponse(
    await request(
      "GET",
      "/react/v1/promotions/valid/by-product?productCode=REV-PROD-001&storeCode=REV001",
    ),
  );
  assert.deepEqual(
    filteredProductPromotions.map((promotion) => promotion.name),
    [
      ...[samePriorityFirst, samePrioritySecond]
        .sort((left, right) => String(left.id).localeCompare(String(right.id)))
        .map((promotion) => promotion.name),
      earlierActivity.name,
      laterActivity.name,
      "总部活动",
      "Demo Mug Bundle",
    ],
    "商品活动只应返回当前分店或总部的有效关联，并按优先级、开始时间和 ID 稳定排序",
  );

  const getProductDetail = () =>
    request(
      "GET",
      "/react/v1/store-product-maintenance/REV-PROD-001?storeCode=REV001&includeCodes=true",
    );
  let productDetail = await getProductDetail();
  assert.equal(
    productDetail.storePrice.uuid,
    "review-store-price-REV-PROD-001",
  );
  assert.equal(productDetail.setCodes.length, 1);
  assert.equal(productDetail.multiCodes.length, 1);

  const storePriceMutation = await request(
    "PUT",
    `/react/v1/store-product-maintenance/store-prices/${productDetail.storePrice.uuid}`,
    { retailPrice: 13.75, purchasePrice: 5.5 },
  );
  assert.equal(storePriceMutation.hqSync?.status, "pending");
  assert.equal(storePriceMutation.hqSync?.productCode, "REV-PROD-001");
  const completedHqSync = await request(
    "GET",
    `/react/v1/store-product-maintenance/hq-sync/${storePriceMutation.hqSync.operationId}`,
  );
  assert.equal(completedHqSync.status, "succeeded");
  await assert.rejects(
    () => request(
      "POST",
      `/react/v1/store-product-maintenance/hq-sync/${storePriceMutation.hqSync.operationId}/retry`,
    ),
    /IOS_REVIEW_HQ_SYNC_RETRY_NOT_ALLOWED/,
    "Review adapter 必须与真实接口一致，只允许 blocked 操作人工重试",
  );
  productDetail = await getProductDetail();
  assert.equal(productDetail.storePrice.retailPrice, 13.75);
  assert.equal(productDetail.storePrice.purchasePrice, 5.5);

  const syncResult = normalizeWarehousePriceSyncResponse(
    await request(
      "POST",
      `/react/v1/store-product-maintenance/store-prices/${productDetail.storePrice.uuid}/sync-warehouse`,
      {
        confirmRetailPrice: true,
        expectedWarehousePurchasePrice: 5.8,
        expectedWarehouseRetailPrice: 14.5,
      },
    ),
  );
  assert.equal(syncResult.status, "synced");
  productDetail = await getProductDetail();
  assert.equal(productDetail.storePrice.purchasePrice, 5.8);
  assert.equal(productDetail.storePrice.retailPrice, 14.5);

  const clearanceMutation = await request(
    "PUT",
    "/react/v1/store-product-maintenance/products/REV-PROD-001/clearance-price",
    { storeCode: "REV001", clearancePrice: 9.25 },
  );
  assert.equal(clearanceMutation.hqSync?.status, "pending");
  productDetail = await getProductDetail();
  assert.equal(productDetail.clearancePrice.clearancePrice, 9.25);
  const clearedClearanceMutation = await request(
    "PUT",
    "/react/v1/store-product-maintenance/products/REV-PROD-001/clearance-price",
    { storeCode: "REV001", clearancePrice: null },
  );
  assert.equal(clearedClearanceMutation.clearancePrice, null);
  assert.equal(clearedClearanceMutation.hqSync?.status, "pending");
  productDetail = await getProductDetail();
  assert.equal(productDetail.clearancePrice, null);

  const createdSetCode = await request(
    "POST",
    "/react/v1/store-product-maintenance/set-codes",
    {
      productCode: "REV-PROD-001",
      storeCode: "REV001",
      productType: 1,
      barcode: "9330000000093",
      retailPrice: 18,
      isActive: true,
    },
  );
  const createdMultiCode = await request(
    "POST",
    "/react/v1/store-product-maintenance/set-codes",
    {
      productCode: "REV-PROD-001",
      storeCode: "REV001",
      productType: 2,
      barcode: "9330000000079",
      retailPrice: 16.5,
      isActive: true,
    },
  );
  assert.equal(createdSetCode.hqSync?.status, "pending");
  assert.equal(createdMultiCode.hqSync?.status, "pending");
  assert.ok(createdMultiCode.uuid, "productType=2 必须返回 multi code uuid");
  const multiCodePage = await request(
    "GET",
    "/react/v1/store-product-maintenance/REV-PROD-001/codes?productType=2&page=1&pageSize=50",
  );
  assert.equal(
    multiCodePage.items.some(
      (item: { uuid: string; barcode: string }) =>
        item.uuid === createdMultiCode.uuid && item.barcode === "9330000000079",
    ),
    true,
    "productType=2 创建后必须在 Multi Code refetch 中可见",
  );
  let setCodePage = await request(
    "GET",
    "/react/v1/store-product-maintenance/REV-PROD-001/codes?type=1&page=1&pageSize=50",
  );
  assert.equal(
    setCodePage.items.some(
      (item: { setCodeId: string }) =>
        item.setCodeId === createdSetCode.setCodeId,
    ),
    true,
  );
  await request(
    "PUT",
    `/react/v1/store-product-maintenance/set-codes/${createdSetCode.setCodeId}`,
    { storeCode: "REV001", barcode: "9330000000086", retailPrice: 19 },
  );
  setCodePage = await request(
    "GET",
    "/react/v1/store-product-maintenance/REV-PROD-001/codes?type=1&page=1&pageSize=50",
  );
  assert.equal(
    setCodePage.items.find(
      (item: { setCodeId: string }) =>
        item.setCodeId === createdSetCode.setCodeId,
    )?.setBarcode,
    "9330000000086",
  );
  await request(
    "DELETE",
    `/react/v1/store-product-maintenance/set-codes/${createdSetCode.setCodeId}`,
  );
  setCodePage = await request(
    "GET",
    "/react/v1/store-product-maintenance/REV-PROD-001/codes?type=1&page=1&pageSize=50",
  );
  assert.equal(
    setCodePage.items.some(
      (item: { setCodeId: string }) =>
        item.setCodeId === createdSetCode.setCodeId,
    ),
    false,
  );

  await request(
    "PUT",
    "/react/v1/store-product-maintenance/multi-codes/review-multi-001",
    { barcode: "9330000000062", retailPrice: 11.25, isActive: true },
  );
  const updatedMultiCodePage = await request(
    "GET",
    "/react/v1/store-product-maintenance/REV-PROD-001/codes?type=2&page=1&pageSize=50",
  );
  assert.equal(
    updatedMultiCodePage.items.find(
      (item: { uuid: string }) => item.uuid === "review-multi-001",
    )?.retailPrice,
    11.25,
  );
  assert.equal(
    updatedMultiCodePage.items.find(
      (item: { uuid: string }) => item.uuid === "review-multi-001",
    )?.barcode,
    "9330000000062",
    "没有 setCodeId 的历史多码必须通过 UUID 端点保存条码",
  );

  const createdLeave = await request(
    "POST",
    "/react/v1/attendance/my/leave-requests",
    {
      storeCode: "REV001",
      leaveType: "AnnualLeave",
      startDate: "2026-07-20",
      endDate: "2026-07-20",
      reason: "Review approval flow",
    },
  );
  let pendingApprovals = await request(
    "GET",
    "/react/v1/attendance/approvals/pending?storeCode=REV001",
  );
  const createdApproval = pendingApprovals.find(
    (item: { sourceGuid: string }) =>
      item.sourceGuid === createdLeave.leaveGuid,
  );
  assert.ok(createdApproval, "创建请假后管理端必须立即出现待审批项");
  await request(
    "POST",
    `/react/v1/attendance/approvals/${createdApproval.approvalGuid}/approve`,
    { reviewRemark: "Approved in demo" },
  );
  let leaveRequests = await request(
    "GET",
    "/react/v1/attendance/my/leave-requests",
  );
  assert.equal(
    leaveRequests.find(
      (item: { leaveGuid: string }) =>
        item.leaveGuid === createdLeave.leaveGuid,
    )?.status,
    "Approved",
  );

  const rejectedLeave = await request(
    "POST",
    "/react/v1/attendance/managed/leave-requests",
    {
      userGuid: "review-staff-001",
      storeCode: "REV001",
      leaveType: "PersonalLeave",
      startDate: "2026-07-21",
      endDate: "2026-07-21",
      reason: "Review reject flow",
    },
  );
  pendingApprovals = await request(
    "GET",
    "/react/v1/attendance/approvals/pending?storeCode=REV001",
  );
  const rejectedApproval = pendingApprovals.find(
    (item: { sourceGuid: string }) =>
      item.sourceGuid === rejectedLeave.leaveGuid,
  );
  assert.ok(rejectedApproval);
  await request(
    "POST",
    `/react/v1/attendance/approvals/${rejectedApproval.approvalGuid}/reject`,
    { reviewRemark: "Rejected in demo" },
  );
  leaveRequests = await request(
    "GET",
    "/react/v1/attendance/my/leave-requests",
  );
  assert.equal(
    leaveRequests.find(
      (item: { leaveGuid: string }) =>
        item.leaveGuid === rejectedLeave.leaveGuid,
    )?.status,
    "Rejected",
  );

  const supplierBranchPayload = await request("GET", "/react/v1/dashboard/supplier-store-sales");
  assert.equal(
    normalizeSupplierBranchReportSnapshot(supplierBranchPayload).isComplete,
    true,
    "审核 mock 供应商分店下钻必须声明 Fresh 统计批次",
  );
  const supplierBranchRows = normalizeSupplierBranchRows(
    supplierBranchPayload,
  );
  assert.equal(supplierBranchRows.length, IOS_REVIEW_STORES.length);
  assert.equal(
    new Set(supplierBranchRows.map((row) => row.branchCode)).size,
    IOS_REVIEW_STORES.length,
    "供应商分店下钻必须保留 28 个唯一分店代码",
  );
  assert.equal(
    supplierBranchPayload.items.every((item: Record<string, unknown>) =>
      [
        "costAmount",
        "grossProfit",
        "grossMarginRate",
        "compareCostAmount",
        "compareGrossProfit",
        "compareGrossMarginRate",
      ].every((key) => typeof item[key] === "number"),
    ),
    true,
    "供应商分店下钻必须提供当前期与同期成本、毛利原始字段",
  );
  assert.equal(supplierBranchRows[0]?.supplierCode, "REV-SUP-001");
  assert.equal(supplierBranchRows[0]?.revenue, 720);
  assert.equal(supplierBranchRows[0]?.totalQuantity, 24);
  assert.equal(supplierBranchRows[0]?.orderCount, 6);
  assert.notEqual(supplierBranchRows[0]?.grossProfit, null);
  assert.notEqual(supplierBranchRows[0]?.compareGrossProfit, null);

  const productPayload = await request(
    "GET",
    "/react/v1/dashboard/enhanced-sales-product-details",
    undefined,
    { pageNumber: 1, pageSize: 20 },
  );
  assert.equal(
    normalizeProductReportProductPageSnapshot(productPayload).isComplete,
    true,
    "审核 mock 商品列表必须声明 Fresh 统计批次",
  );
  const firstProductPage = normalizeProductPage(productPayload);
  assert.equal(firstProductPage.rows.length, 20, "审核商品第一页必须严格返回 20 行");
  assert.equal(firstProductPage.total, 24, "审核商品报告必须提供 24 行总量");
  assert.equal(firstProductPage.pageIndex, 1);
  assert.equal(firstProductPage.pageSize, 20);
  const secondProductPayload = await request(
    "GET",
    "/react/v1/dashboard/enhanced-sales-product-details",
    undefined,
    { pageNumber: 2, pageSize: 20 },
  );
  const secondProductPage = normalizeProductPage(secondProductPayload);
  assert.equal(
    normalizeProductReportProductPageSnapshot(secondProductPayload).isComplete,
    true,
    "审核商品第二页也必须保留完整性元数据",
  );
  assert.equal(secondProductPage.rows.length, 4, "审核商品第二页必须返回剩余 4 行");
  assert.equal(secondProductPage.total, 24);
  assert.equal(secondProductPage.pageIndex, 2);
  assert.equal(secondProductPage.pageSize, 20);
  const allProductRows = [...firstProductPage.rows, ...secondProductPage.rows];
  assert.equal(
    new Set(allProductRows.map((row) => row.productCode)).size,
    24,
    "两页商品代码必须保持 24 个唯一值且不能重复",
  );
  // 排序参数与后端口径一致：未传时保持金额降序，数量升序时数量最少的商品排第一。
  assert.equal(firstProductPage.rows[0]?.itemNumber, "RPT-00001", "未传排序参数时必须保持金额降序");
  const quantityAscPage = normalizeProductPage(await request(
    "GET",
    "/react/v1/dashboard/enhanced-sales-product-details",
    undefined,
    { pageIndex: 1, pageSize: 20, sortField: "quantity", sortOrder: "asc" },
  ));
  assert.equal(quantityAscPage.rows[0]?.itemNumber, "RPT-00024", "数量升序时首行必须是数量最少的商品");
  assert.equal(quantityAscPage.total, 24, "排序不能改变商品总数");
  const searchedProductPayload = await request(
    "GET",
    "/react/v1/dashboard/enhanced-sales-product-details",
    undefined,
    { productSearch: "RPT-00024", pageIndex: 1, pageSize: 20 },
  );
  const searchedProductPage = normalizeProductPage(searchedProductPayload);
  assert.equal(searchedProductPage.total, 1, "商品搜索必须真正过滤 Review 报表结果");
  assert.equal(searchedProductPage.rows[0]?.itemNumber, "RPT-00024");

  const supplierFilteredProductPayload = await request(
    "GET",
    "/react/v1/dashboard/enhanced-sales-product-details",
    undefined,
    { localSupplierCodes: ["REV-SUP-002"], pageIndex: 1, pageSize: 20 },
  );
  assert.equal(
    normalizeProductPage(supplierFilteredProductPayload).total,
    3,
    "选择供应商后 Review 商品列表必须只保留该供应商的商品",
  );

  const chinaScopedProductPayload = await request(
    "GET",
    "/react/v1/dashboard/enhanced-sales-product-details",
    undefined,
    { supplierScope: "china", pageIndex: 1, pageSize: 20 },
  );
  const chinaScopedProductPage = normalizeProductPage(chinaScopedProductPayload);
  assert.equal(
    chinaScopedProductPage.total,
    8,
    "中国供应商页未选择具体供应商时，Review 商品列表也必须限定为中国供应商商品",
  );
  assert.ok(
    chinaScopedProductPage.rows.every((row) => Number(row.itemNumber.slice(4)) <= 8),
    "中国供应商范围不得混入澳洲供应商商品 fixture",
  );

  const branchFilteredProductPayload = await request(
    "GET",
    "/react/v1/dashboard/enhanced-sales-product-details",
    undefined,
    { branchCodes: ["REV002"], pageIndex: 1, pageSize: 20 },
  );
  assert.ok(
    branchFilteredProductPayload.items[0].salesAmount < productPayload.items[0].salesAmount,
    "选择单店后 Review 商品金额必须按该店范围缩小",
  );
  const anotherBranchFilteredProductPayload = await request(
    "GET",
    "/react/v1/dashboard/enhanced-sales-product-details",
    undefined,
    { branchCodes: ["REV001"], pageIndex: 1, pageSize: 20 },
  );
  assert.notEqual(
    anotherBranchFilteredProductPayload.items[0]?.salesAmount,
    branchFilteredProductPayload.items[0]?.salesAmount,
    "商品表必须按具体分店 fixture 计算，REV001 与 REV002 的单店金额不能相同",
  );
  const unknownBranchFilteredProductPayload = await request(
    "GET",
    "/react/v1/dashboard/enhanced-sales-product-details",
    undefined,
    {
      branchCodes: ["NOT-A-REVIEW-STORE"],
      pageIndex: 1,
      pageSize: 20,
    },
  );
  assert.equal(
    unknownBranchFilteredProductPayload.items.every(
      (item: Record<string, number>) => [
        "quantity",
        "compareQuantity",
        "salesAmount",
        "compareSalesAmount",
        "costAmount",
        "compareCostAmount",
        "grossProfit",
        "compareGrossProfit",
        "grossMarginRate",
        "compareGrossMarginRate",
        "averageUnitPrice",
        "compareAverageUnitPrice",
        "orderCount",
        "compareOrderCount",
      ].every((key) => item[key] === 0),
    ),
    true,
    "商品表未知分店必须返回零指标，不能回退或泄漏全店数据",
  );
  const allRawProductRows = [
    ...productPayload.items,
    ...secondProductPayload.items,
  ] as Record<string, unknown>[];
  assert.equal(
    allRawProductRows.every((item) =>
      [
        "salesAmount",
        "quantity",
        "compareSalesAmount",
        "compareQuantity",
      ].every((key) => typeof item[key] === "number"),
    ),
    true,
    "商品报告必须显式提供销售额、销量及同期字段",
  );
  assert.equal(
    allRawProductRows.filter(
      (item) =>
        item.costAmount === null &&
        item.grossProfit === null &&
        item.grossMarginRate === null,
    ).length,
    1,
    "商品报告必须保留且仅保留一个当前期成本缺失商品，用于验收空值状态",
  );
  assert.equal(
    allRawProductRows
      .filter((item) => item.costAmount !== null)
      .every((item) =>
        [
          "costAmount",
          "grossProfit",
          "grossMarginRate",
          "compareCostAmount",
          "compareGrossProfit",
          "compareGrossMarginRate",
        ].every((key) => typeof item[key] === "number"),
      ),
    true,
    "其余商品必须显式提供当前期与同期成本、毛利、毛利率字段",
  );
  assert.equal(
    allProductRows.filter((row) => row.grossProfit === null).length,
    1,
    "成本缺失状态必须能通过真实 normalizer 到达页面",
  );
  const productBranchPayload = await request("GET", "/react/v1/dashboard/product-sales-by-branches");
  assert.equal(
    normalizeProductBranchReportSnapshot(productBranchPayload).isComplete,
    true,
    "审核 mock 商品分店下钻必须声明 Fresh 统计批次",
  );
  const productBranchRows = normalizeProductBranchRows(
    productBranchPayload,
  );
  assert.equal(productBranchRows.length, IOS_REVIEW_STORES.length);
  assert.equal(
    new Set(productBranchRows.map((row) => row.branchCode)).size,
    IOS_REVIEW_STORES.length,
    "商品分店下钻必须保留 28 个唯一分店代码",
  );
  assert.equal(
    productBranchPayload.items.every((item: Record<string, unknown>) =>
      [
        "costAmount",
        "grossProfit",
        "grossMarginRate",
        "compareCostAmount",
        "compareGrossProfit",
        "compareGrossMarginRate",
      ].every((key) => typeof item[key] === "number"),
    ),
    true,
    "商品分店下钻必须提供当前期与同期成本、毛利原始字段",
  );
  assert.equal(productBranchRows[0]?.quantity, 12);
  assert.equal(productBranchRows[0]?.salesAmount, 240);
  assert.notEqual(productBranchRows[0]?.grossProfit, null);
  assert.notEqual(productBranchRows[0]?.compareGrossProfit, null);

  const scan = await request(
    "POST",
    "/react/v1/store-order/products/scan-lookup",
    { barcode: "9330000000017", storeCode: "REV001" },
  );
  assert.equal(scan.items[0].productCode, "REV-PROD-001");

  const scanAdd = await request(
    "POST",
    "/react/v1/store-order/cart/scan-lookup-add",
    { barcode: "9330000000017", storeCode: "REV002" },
  );
  assert.equal(scanAdd.added, true);
  assert.equal(scanAdd.items[0].productCode, "REV-PROD-001");

  await request("POST", "/react/v1/store-order/cart/add", {
    storeCode: "REV001",
    productCode: "REV-PROD-002",
    quantity: 2,
  });
  let cart = await request("GET", "/react/v1/store-order/cart/REV001");
  assert.equal(
    cart.items.some(
      (item: { productCode: string; quantity: number }) =>
        item.productCode === "REV-PROD-002" && item.quantity === 2,
    ),
    true,
    "购物车写入必须在后续读取中立即可见",
  );

  await request("POST", "/react/v1/store-order/cart/update", {
    storeCode: "REV001",
    productCode: "REV-PROD-002",
    quantity: 5,
  });
  cart = await request("GET", "/react/v1/store-order/cart/REV001");
  assert.equal(
    cart.items.find(
      (item: { productCode: string }) => item.productCode === "REV-PROD-002",
    ).quantity,
    5,
  );

  const createdSchedule = await request(
    "POST",
    "/react/v1/attendance/schedules",
    {
      storeCode: "REV001",
      userGuid: "review-user",
      workDate: "2026-07-17",
      startTime: "09:00",
      endTime: "17:00",
    },
  );
  assert.equal(createdSchedule.storeCode, "REV001");
  const updatedSchedule = await request(
    "PUT",
    `/react/v1/attendance/schedules/${createdSchedule.scheduleGuid}`,
    { status: "Active" },
  );
  assert.equal(updatedSchedule.status, "Active");
  await request(
    "DELETE",
    `/react/v1/attendance/schedules/${createdSchedule.scheduleGuid}`,
  );

  const permissionsPath =
    "/Users/guid/review-user/stores/review-store/pos-terminal-permissions";
  const permissions = await request("PUT", permissionsPath, {
    grantedPermissionCodes: ["PosTerminal.Sales.AddItem"],
  });
  assert.deepEqual(permissions.grantedPermissionCodes, [
    "PosTerminal.Sales.AddItem",
  ]);

  await request("POST", "/Users/guid/review-staff-001/stores", [
    {
      StoreGUID: IOS_REVIEW_STORES[1].storeGUID,
      IsPrimary: true,
    },
  ]);
  const updatedAccessStores = await request(
    "GET",
    "/Users/guid/review-staff-001/stores",
  );
  assert.equal(
    updatedAccessStores[0]?.storeGUID,
    IOS_REVIEW_STORES[1].storeGUID,
  );
  assert.equal(updatedAccessStores[0]?.isPrimary, true);

  await request("POST", "/Users/guid/review-staff-001/roles", {
    RoleGuids: ["review-role-store-staff"],
  });
  await request("POST", "/Users/guid/review-staff-001/permissions", {
    permissions: ["PosTerminal.Sales.AddItem"],
  });
  const updatedPermissionState = await request(
    "GET",
    "/Users/guid/review-staff-001/permissions/state",
  );
  assert.deepEqual(updatedPermissionState.directPermissionCodes, [
    "PosTerminal.Sales.AddItem",
  ]);

  const profile = await request("PUT", "/EmployeeProfiles/me", {
    displayName: "Updated Demo Reviewer",
  });
  assert.equal(profile.displayName, "Updated Demo Reviewer");

  assert.equal(await request("GET", "/EmployeeProfiles/me/sensitive-change-request"), null);
  const reviewDraft = await request("PUT", "/EmployeeProfiles/me/sensitive-change-request", {
    bankBsb: profile.bankBsb,
    bankAccountNumber: "00001234",
    superannuationCompanyName: profile.superannuationCompanyName,
    superannuationCompanyCode: profile.superannuationCompanyCode,
    superannuationAccountNumber: profile.superannuationAccountNumber,
    identityType: profile.identityType,
    identityId: profile.identityId,
    expectedSensitiveRevision: profile.sensitiveRevision,
  });
  assert.equal(reviewDraft.status, "Pending");
  assert.deepEqual(reviewDraft.changedFields, ["bankAccountNumber"]);
  assert.equal((await request("GET", "/EmployeeProfiles/me")).bankAccountNumber, profile.bankAccountNumber, "敏感资料提交只创建待审草稿，不直接覆盖正式资料");
  assert.equal((await request("GET", "/EmployeeProfiles/me/sensitive-change-request")).bankAccountNumber, "00001234");
  await assert.rejects(request("PUT", "/EmployeeProfiles/me/sensitive-change-request", { expectedSensitiveRevision: -1 }));

  const exportData = await request(
    "POST",
    "/react/v1/containers/review-container/products/export",
    { format: "xlsx" },
  );
  assert.equal(
    exportData instanceof ArrayBuffer,
    true,
    "导出必须返回二进制数据",
  );

  const productMirrorCount = dataStore.list("carts").length;
  assert.ok(productMirrorCount > 1, "代表性写操作必须同步到 ReviewDataStore");

  const overtimeDataStore = createIosReviewDataStore(
    new Date("2026-07-16T00:00:00.000Z"),
  );
  const overtimeTransport = createIosReviewTransport(overtimeDataStore);
  const overtimeRequest = async (method: Method, url: string, body?: unknown) =>
    (await overtimeTransport.dispatch({ method, url, data: body })).data as any;
  const earlyClockIn = await overtimeRequest(
    "POST",
    "/react/v1/attendance/my/punch-adjustments",
    {
      storeCode: "REV001",
      scheduleGuid: "review-schedule-001",
      punchType: "ClockIn",
      requestedPunchTimeLocal: "2000-01-01T00:00:00",
      requestedPunchTimeUtc: "2026-07-15T22:30:00Z",
      reason: "REV001 store local 08:30",
    },
  );
  assert.equal(earlyClockIn.status, "Applied");
  const overtimePreview = await overtimeRequest(
    "POST",
    "/react/v1/attendance/my/punch-adjustments/preview",
    {
      storeCode: "REV001",
      scheduleGuid: "review-schedule-001",
      punchType: "ClockOut",
      requestedPunchTimeLocal: "2000-01-01T00:00:00",
      requestedPunchTimeUtc: "2026-07-16T08:00:00Z",
      reason: "REV001 store local 18:00",
    },
  );
  assert.equal(overtimePreview.isValid, true);
  assert.equal(overtimePreview.proposedSession.earlyOvertimeMinutes, 30);
  assert.equal(overtimePreview.proposedSession.lateOvertimeMinutes, 60);
  assert.equal(overtimePreview.proposedSession.candidateOvertimeMinutes, 90,
    "REV001 的 08:30/18:00 必须按 Brisbane 门店本地时间相对 09:00-17:00 计算加班");

  resetIosReviewAppRouteState(dataStore);
  const resetCart = await request("GET", "/react/v1/store-order/cart/REV001");
  assert.equal(
    resetCart.items.some(
      (item: { productCode: string }) => item.productCode === "REV-PROD-002",
    ),
    false,
    "退出或重启时必须恢复路由 fixture 初始快照",
  );
  assert.equal(
    (await request("GET", "/react/v1/attendance/my/punch-adjustments")).length,
    0,
    "退出或重启时必须清除 Review 内存中的补卡记录",
  );

  await assert.rejects(
    () => request("GET", "/react/v1/not-a-real-endpoint"),
    /IOS_REVIEW_UNHANDLED_REQUEST/,
    "宽泛 prefix 路由不能吞掉未登记 endpoint",
  );
  await assert.rejects(
    () => request("GET", "/react/v1/product-insights"),
    /IOS_REVIEW_UNHANDLED_REQUEST/,
    "商品进销查询未登记审核 fixture 时必须 fail-closed",
  );

  console.log("app-routes.test.ts: ok");
}

run().catch((error) => {
  console.error(error);
  process.exitCode = 1;
});
