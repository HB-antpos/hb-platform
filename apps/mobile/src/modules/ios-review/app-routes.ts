import {
  IOS_REVIEW_PERMISSION_CODES,
  IOS_REVIEW_STORES,
  createIosReviewUser,
} from "./identity";
import { IOS_REVIEW_SAMPLE_BARCODE } from "./helpers";
import { IOS_REVIEW_MENU_ITEMS } from "./menu";
import type { IosReviewDomainName, ReviewDataStore } from "./data-store";
import {
  type ReviewTransport,
  type ReviewTransportRequest,
  type ReviewTransportResult,
} from "./transport";
import { normalizeAttendanceToday } from "../attendance/attendance-today-normalization";
import {
  computeSeasonalCardHolidayWindows,
  findNextSeasonalCardOpening,
  SEASONAL_CARD_OPEN_DAYS_AFTER_HOLIDAY,
} from "../seasonal-cards/holiday-window";
import {
  sortReportRows,
  type ReportSort,
  type ReportSortValues,
} from "../product-report/sorting";

type ReviewMethod = ReviewTransportRequest["method"];
type JsonRecord = Record<string, any>;
type RouteHandler = (
  request: ReviewTransportRequest,
) => ReviewTransportResult | Promise<ReviewTransportResult>;

interface AppRouteState {
  now: string;
  today: string;
  products: JsonRecord[];
  productDetails: Map<string, JsonRecord>;
  hqSyncOperations: Map<string, JsonRecord>;
  carts: Map<string, JsonRecord>;
  orders: JsonRecord[];
  locations: JsonRecord[];
  containers: JsonRecord[];
  containerDetails: JsonRecord[];
  domesticBatches: JsonRecord[];
  domesticProducts: JsonRecord[];
  invoices: JsonRecord[];
  invoiceDetails: JsonRecord[];
  advertisements: JsonRecord[];
  promotions: JsonRecord[];
  installmentOrders: JsonRecord[];
  vouchers: JsonRecord[];
  seasonalCatalog: JsonRecord[];
  seasonalSubmissions: JsonRecord[];
  punches: JsonRecord[];
  punchAdjustments: JsonRecord[];
  availability: JsonRecord[];
  leaveRequests: JsonRecord[];
  approvals: JsonRecord[];
  schedules: JsonRecord[];
  holidays: JsonRecord[];
  users: JsonRecord[];
  posPermissionCodes: string[];
  employeeProfile: JsonRecord;
  cashierBarcode: JsonRecord;
  devices: JsonRecord[];
  appDevices: JsonRecord[];
  mirrorIds: Map<string, string>;
  sequence: number;
  localBarcodeCounters: Record<string, number>;
}

interface ReviewAttendanceQrAuthorization {
  qrToken: string;
  storeCode: string;
  storeName: string;
  deviceCode: string;
  issuedAtUtc: string;
  expiresAtUtc: string;
}

interface ReviewAttendanceRouteState {
  authorizations: Map<string, ReviewAttendanceQrAuthorization>;
  punchResults: Map<string, JsonRecord>;
}

interface AppRouteStateHolder {
  current: AppRouteState;
}

interface ReviewDomesticSetTemplate {
  templateId: string;
  supplierCode: string;
  templateName: string;
  setProductName: string;
  isEnabled: boolean;
  setQuantity: number;
  updatedAt: string;
  subItems: JsonRecord[];
}

const stateHolders = new WeakMap<ReviewDataStore, AppRouteStateHolder>();
const attendanceRouteStates = new WeakMap<AppRouteState, ReviewAttendanceRouteState>();
// 模板是审核模式自己的持久化快照；按 AppRouteState 隔离，避免不同审核数据仓库互相污染。
const domesticSetTemplateStates = new WeakMap<AppRouteState, ReviewDomesticSetTemplate[]>();
const IOS_REVIEW_MANAGER_SEGMENT_LIMIT = 3;
const IOS_REVIEW_ATTENDANCE_QR_DEVICE_CODE = "IOS-REVIEW-POS-001";
const IOS_REVIEW_ATTENDANCE_QR_LIFETIME_MS = 30_000;
const IOS_REVIEW_PUNCH_AUTHORIZATION_LIFETIME_MS = 2 * 60_000;

function clone<T>(value: T): T {
  if (Array.isArray(value)) {
    return value.map((item) => clone(item)) as T;
  }
  if (value && typeof value === "object" && !(value instanceof Map)) {
    return Object.fromEntries(
      Object.entries(value as Record<string, unknown>).map(([key, item]) => [
        key,
        clone(item),
      ]),
    ) as T;
  }
  return value;
}

function toDateOnly(date: Date) {
  return date.toISOString().slice(0, 10);
}

function resolveReviewStoreTimeZone(storeCode: unknown) {
  const normalizedStoreCode = String(storeCode ?? "")
    .trim()
    .toUpperCase();
  const stateCode = String(
    IOS_REVIEW_STORES.find(
      (store) => store.storeCode.toUpperCase() === normalizedStoreCode,
    )?.stateCode ?? "",
  ).toUpperCase();
  if (stateCode === "QLD") return "Australia/Brisbane";
  if (stateCode === "VIC") return "Australia/Melbourne";
  // NSW 及无法识别的门店都沿用 Central Backend 的 Sydney 回退规则。
  return "Australia/Sydney";
}

function toStoreLocalDateOnly(value: unknown, storeCode: unknown) {
  const instant = new Date(String(value ?? ""));
  if (!Number.isFinite(instant.getTime())) return "";
  const parts = new Intl.DateTimeFormat("en-AU", {
    timeZone: resolveReviewStoreTimeZone(storeCode),
    year: "numeric",
    month: "2-digit",
    day: "2-digit",
  }).formatToParts(instant);
  const byType = Object.fromEntries(
    parts.map((part) => [part.type, part.value]),
  );
  return `${byType.year}-${byType.month}-${byType.day}`;
}

function toStoreLocalDateTime(value: unknown, storeCode: unknown) {
  const instant = new Date(String(value ?? ""));
  if (!Number.isFinite(instant.getTime())) return "";
  const parts = new Intl.DateTimeFormat("en-AU", {
    timeZone: resolveReviewStoreTimeZone(storeCode),
    year: "numeric",
    month: "2-digit",
    day: "2-digit",
    hour: "2-digit",
    minute: "2-digit",
    second: "2-digit",
    hourCycle: "h23",
  }).formatToParts(instant);
  const byType = Object.fromEntries(
    parts.map((part) => [part.type, part.value]),
  );
  return `${byType.year}-${byType.month}-${byType.day}T${byType.hour}:${byType.minute}:${byType.second}`;
}

function createInitialState(now = new Date()): AppRouteState {
  const timestamp = now.toISOString();
  const today = toDateOnly(now);
  const attendanceToday = toStoreLocalDateOnly(timestamp, "REV001");
  const products: JsonRecord[] = [
    {
      productCode: "REV-PROD-001",
      itemNumber: "REV-MUG-001",
      barcode: IOS_REVIEW_SAMPLE_BARCODE,
      productName: "Demo Ceramic Mug",
      productImage:
        "data:image/svg+xml,%3Csvg%20xmlns='http://www.w3.org/2000/svg'%3E%3C/svg%3E",
      categoryName: "Homeware",
      warehouseCategoryGUID: "review-category-homeware",
      grade: "A",
      oemPrice: 12.95,
      retailPrice: 12.95,
      purchasePrice: 5.2,
      domesticPrice: 5.2,
      importPrice: 5.2,
      stockQuantity: 48,
      minOrderQuantity: 1,
      middlePackageQuantity: 6,
      packingQuantity: 24,
      packQty: 24,
      volume: 0.002,
      isInStock: true,
      isActive: true,
      warehouseIsActive: true,
      localSupplierCode: "REV-SUP-001",
      supplierCode: "REV-SUP-001",
      supplierName: "Demo Local Supplier",
      locationGuid: "review-location-001",
      locationCode: "A-01-01",
      locationBarcode: "REVLOC000001",
      updatedAt: timestamp,
    },
    {
      productCode: "REV-PROD-002",
      itemNumber: "REV-TOWEL-002",
      barcode: "9330000000024",
      productName: "Demo Cotton Towel",
      categoryName: "Homeware",
      warehouseCategoryGUID: "review-category-homeware",
      grade: "A",
      oemPrice: 8.5,
      retailPrice: 8.5,
      purchasePrice: 3.4,
      importPrice: 3.4,
      stockQuantity: 72,
      minOrderQuantity: 1,
      middlePackageQuantity: 12,
      packingQuantity: 48,
      packQty: 48,
      volume: 0.001,
      isInStock: true,
      isActive: true,
      warehouseIsActive: true,
      localSupplierCode: "REV-SUP-001",
      supplierCode: "REV-SUP-001",
      supplierName: "Demo Local Supplier",
      locationGuid: "review-location-001",
      locationCode: "A-01-01",
      locationBarcode: "REVLOC000001",
      updatedAt: timestamp,
    },
  ];
  const firstCartLine = createCartLine(products[0], 2, timestamp);
  const carts = new Map<string, JsonRecord>([
    ["REV001", createCart("REV001", [firstCartLine], timestamp)],
    ["REV002", createCart("REV002", [], timestamp)],
    ["REVWH", createCart("REVWH", [], timestamp)],
  ]);
  const orders = [
    {
      orderGUID: "review-order-001",
      orderNo: "REV-ORDER-0001",
      storeCode: "REV001",
      storeName: "Demo Brisbane",
      orderDate: timestamp,
      flowStatus: 2,
      totalAmount: firstCartLine.amount,
      oemTotalAmount: firstCartLine.amount,
      importTotalAmount: firstCartLine.importAmount,
      totalOrderAmount: firstCartLine.amount,
      totalQuantity: firstCartLine.quantity,
      totalAllocQuantity: firstCartLine.quantity,
      totalOrderVolume: firstCartLine.totalVolume,
      totalAllocVolume: firstCartLine.totalVolume,
      remarks: "Completed offline demo order",
      items: [clone(firstCartLine)],
      createdAt: timestamp,
      updatedAt: timestamp,
    },
  ];
  const locations = [
    {
      locationGuid: "review-location-001",
      locationCode: "A-01-01",
      locationBarcode: "REVLOC000001",
      status: 1,
      locationType: 1,
      productCount: 1,
      updatedAt: timestamp,
      updatedBy: "App Review Demo",
      products: [
        {
          productCode: products[0].productCode,
          itemNumber: products[0].itemNumber,
          productName: products[0].productName,
          productImage: products[0].productImage,
          middlePackageQuantity: products[0].middlePackageQuantity,
        },
      ],
    },
  ];
  const containers = [
    {
      hguid: "review-container",
      HGUID: "review-container",
      柜号: "REV-CN-001",
      containerNumber: "REV-CN-001",
      预计到岸日期: today,
      状态: "Draft",
      totalPieces: 120,
      totalAmount: 624,
      totalVolume: 18.5,
    },
  ];
  const containerDetails = [
    {
      hguid: "review-container-detail-001",
      HGUID: "review-container-detail-001",
      商品编码: products[0].productCode,
      商品名称: products[0].productName,
      英文名称: "Demo Ceramic Mug",
      商品图片: products[0].productImage,
      贴牌价格: products[0].oemPrice,
      国内价格: products[0].domesticPrice,
      进口价格: products[0].importPrice,
      装柜数量: 120,
      合计装柜体积: 18.5,
      是否新商品: false,
      商品信息: {
        商品编码: products[0].productCode,
        货号: products[0].itemNumber,
        条形码: products[0].barcode,
        商品名称: products[0].productName,
        英文名称: "Demo Ceramic Mug",
        商品图片: products[0].productImage,
        localSupplierCode: "REV-SUP-001",
      },
      matchType: "productCode",
      localProductCode: products[0].productCode,
      domesticProductCode: products[0].productCode,
    },
  ];
  const domesticProducts = products.map((product) => ({
    productCode: product.productCode,
    supplierCode: "REV-CN-SUP",
    supplierName: "Demo China Supplier",
    productName: product.productName,
    englishProductName: product.productName,
    hbProductNo: product.itemNumber,
    barcode: product.barcode,
    productSpecification: "Demo specification",
    productType: 1,
    domesticPrice: product.domesticPrice,
    oemPrice: product.oemPrice,
    importPrice: product.importPrice,
    packingQuantity: product.packingQuantity,
    unitVolume: product.volume,
    middlePackQuantity: product.middlePackageQuantity,
    productImage: product.productImage,
    isActive: true,
  }));
  const domesticBatches = [
    {
      batchNumber: "REV-BATCH-001",
      supplierCode: "REV-CN-SUP",
      supplierName: "Demo China Supplier",
      prefixCode: "REV",
      normalProductCount: 2,
      setProductCount: 0,
      totalCount: 2,
      createdTime: timestamp,
      createdBy: "App Review Demo",
      items: domesticProducts.map((item) => ({
        ...item,
        itemNumber: item.hbProductNo,
        privateLabelPrice: item.oemPrice,
      })),
    },
  ];
  const invoices = [
    {
      invoiceGuid: "review-invoice-001",
      invoiceNo: "REV-INV-0001",
      storeCode: "REV001",
      storeName: "Demo Brisbane",
      supplierCode: "REV-SUP-001",
      supplierName: "Demo Local Supplier",
      orderDate: today,
      inboundDate: today,
      flowStatus: 2,
      inboundStatus: 1,
      totalAmount: 320.5,
      receivedTotalAmount: 320.5,
      priceIncreaseItemCount: 1,
      priceDecreaseItemCount: 0,
      remarks: "Synthetic local supplier invoice",
      createdAt: timestamp,
      updatedAt: timestamp,
    },
  ];
  const invoiceDetails = products.map((product, index) => ({
    detailGuid: `review-invoice-line-${index + 1}`,
    invoiceGuid: "review-invoice-001",
    storeCode: "REV001",
    supplierCode: "REV-SUP-001",
    productCode: product.productCode,
    storeProductCode: product.productCode,
    itemNumber: product.itemNumber,
    barcode: product.barcode,
    productName: product.productName,
    quantity: index + 2,
    purchasePrice: product.purchasePrice,
    lastPurchasePrice: product.purchasePrice,
    retailPrice: product.retailPrice,
    amount: product.purchasePrice * (index + 2),
    productImage: product.productImage,
  }));
  const advertisements = [
    {
      id: "review-ad-001",
      title: "Demo Weekend Specials",
      description: "Synthetic banner for App Review",
      mediaType: "Image",
      mediaUrl: products[0].productImage,
      thumbnailUrl: products[0].productImage,
      objectKey: "ios-review/advertisements/review-ad-001.svg",
      originalFileName: "demo-weekend-specials.svg",
      contentType: "image/svg+xml",
      fileSize: 256,
      effectiveStart: timestamp,
      effectiveEnd: new Date(now.getTime() + 7 * 86400000).toISOString(),
      isEnabled: true,
      sortOrder: 1,
      stores: [{ storeCode: "REV001" }],
    },
  ];
  const promotions = [
    {
      id: "review-promotion-001",
      name: "Demo Mug Bundle",
      description: "Buy two demo mugs for a fixed price",
      effectiveStart: timestamp,
      effectiveEnd: new Date(now.getTime() + 7 * 86400000).toISOString(),
      isEnabled: true,
      isExclusive: true,
      priority: 10,
      applyQuantity: 2,
      fixedPrice: 20,
      maxApplicationsPerOrder: 2,
      products: [{ productCode: products[0].productCode, unitWeight: 1 }],
      stores: [{ storeCode: "REV001" }],
      productsCount: 1,
      storesCount: 1,
      scopeType: "StoreOnly",
      canEditInStoreScope: true,
      canCopyToStore: true,
    },
  ];
  const installmentOrders = [
    {
      installmentGuid: "review-installment-001",
      installmentNumber: "REV-INS-0001",
      storeCode: "REV001",
      storeName: "Demo Brisbane",
      deviceCode: "REV-IOS-01",
      cashierName: "App Review Demo",
      customerPhone: "0400000001",
      customerName: "Demo Customer",
      createdAt: timestamp,
      totalAmount: 120,
      minimumDownPayment: 24,
      downPaymentAmount: 40,
      paidAmount: 120,
      balanceAmount: 0,
      status: 3,
      updatedAt: timestamp,
      note: "Review customer pickup completed",
      pickupInfo: {
        pickedUpAt: timestamp,
        pickedUpBy: "App Review Demo",
        pickupNote: "Identity confirmed at pickup",
      },
      cancellationInfo: null,
      lines: [
        {
          installmentLineGuid: "review-installment-line-001",
          productCode: products[0].productCode,
          referenceCode: products[0].barcode,
          displayName: products[0].productName,
          lookupCode: products[0].barcode,
          quantity: 1.25,
          unitPrice: 100,
          discountAmount: 5,
          actualAmount: 120,
          itemNumber: "REV-ITEM-001",
        },
      ],
      payments: [
        {
          paymentGuid: "review-payment-001",
          method: 1,
          amount: 40,
          reference: "REV-PAY-001",
          status: 1,
          recordedAt: timestamp,
          cashierId: "review-user",
          deviceCode: "REV-IOS-01",
        },
        {
          paymentGuid: "review-payment-voided-001",
          method: 2,
          amount: 10,
          reference: "REV-PAY-VOIDED-001",
          status: 2,
          recordedAt: timestamp,
          cashierId: "review-user",
          deviceCode: "REV-IOS-01",
        },
        {
          paymentGuid: "review-payment-002",
          method: 1,
          amount: 80,
          reference: "REV-PAY-002",
          status: 1,
          recordedAt: timestamp,
          cashierId: "review-user",
          deviceCode: "REV-IOS-01",
        },
      ],
    },
    {
      installmentGuid: "review-installment-002",
      installmentNumber: "REV-INS-0002",
      storeCode: "REV001",
      storeName: "Demo Brisbane",
      deviceCode: "REV-IOS-02",
      cashierName: "App Review Demo",
      customerPhone: "0400000002",
      customerName: "Demo Customer Two",
      // UTC 仍在前一天，但 Brisbane 已进入 7 月 16 日，用于锁定门店本地日期边界。
      createdAt: "2026-07-15T23:30:00.000Z",
      totalAmount: 60,
      minimumDownPayment: 12,
      downPaymentAmount: 20,
      paidAmount: 60,
      balanceAmount: 0,
      status: 3,
      updatedAt: "2026-07-16T00:00:00.000Z",
      note: "Review timezone boundary fixture",
      pickupInfo: null,
      cancellationInfo: null,
      lines: [],
      payments: [],
    },
  ];
  const vouchers = [
    {
      id: "review-voucher-001",
      voucherCode: "REV-VOUCHER-0001",
      voucherType: 1,
      storeCode: "REV001",
      storeName: "Demo Brisbane",
      customerCode: "REV-CUSTOMER-001",
      customerName: "Demo Customer",
      amount: 50,
      remainingAmount: 35,
      status: "Active",
      createTime: timestamp,
      updateTime: timestamp,
      expiredDate: new Date(now.getTime() + 30 * 86400000).toISOString(),
      createUser: "App Review Demo",
      remark: "Synthetic voucher",
      ledger: [
        {
          id: "review-ledger-001",
          voucherCode: "REV-VOUCHER-0001",
          action: "issued",
          amount: 50,
          remainingAmount: 50,
          actionTime: timestamp,
          operatorName: "App Review Demo",
        },
      ],
      relatedOrders: [
        {
          orderGuid: orders[0].orderGUID,
          orderNo: orders[0].orderNo,
          storeCode: "REV001",
          amount: 15,
          orderTime: timestamp,
        },
      ],
    },
  ];
  // 与生产目录同构：5 个节日 × 4 个价格（$1/$2/$3/其他），批量填报要求一次覆盖一个节日的全部价格。
  const seasonalCardTypeNames = [
    "Christmas Card",
    "Valentine's Day Card",
    "Mother's Day Card",
    "Easter Card",
    "Father's Day Card",
  ];
  const seasonalCatalog = [1, 2, 3, 4, 5].flatMap((cardType) =>
    [1, 2, 3, 4].map((priceOption) => ({
      catalogGuid:
        cardType === 1 && priceOption === 1
          ? "review-seasonal-catalog-001"
          : `review-seasonal-catalog-${cardType}-${priceOption}`,
      cardType,
      cardTypeName: seasonalCardTypeNames[cardType - 1],
      priceOption,
      priceOptionName: priceOption === 4 ? "Other" : "Fixed price",
      priceLabel: priceOption === 4 ? "Other" : `$${priceOption}`,
      fixedUnitPrice: priceOption === 4 ? null : priceOption,
      allowsCustomUnitPrice: priceOption === 4,
      isEnabled: true,
      sortOrder: priceOption,
    })),
  );
  const seasonalSubmissions = [
    {
      submissionGuid: "review-seasonal-submission-001",
      storeCode: "REV001",
      catalogGuid: "review-seasonal-catalog-001",
      cardType: 1,
      cardTypeName: "Christmas Card",
      priceOption: 1,
      seasonYear: now.getUTCFullYear(),
      unitPrice: 1,
      priceLabel: "$1",
      localSupplierCode: null,
      supplierName: null,
      batchGuid: null,
      remainingQuantity: 36,
      remark: "Synthetic stock count",
      submittedByName: "App Review Demo",
      submittedAt: timestamp,
    },
  ];
  const schedules = [
    {
      scheduleGuid: "review-schedule-001",
      storeCode: "REV001",
      storeName: "Demo Brisbane",
      userGuid: "review-user",
      employeeName: "App Review Demo",
      workDate: attendanceToday,
      startTime: "09:00",
      endTime: "17:00",
      status: "Active",
      remark: "Offline demo shift",
      isMine: true,
    },
  ];
  const availability = [
    {
      availabilityGuid: "review-availability-001",
      storeCode: "REV001",
      storeName: "Demo Brisbane",
      workDate: today,
      startTime: "08:00",
      endTime: "18:00",
      note: "Available",
      status: "Submitted",
    },
  ];
  const leaveRequests = [
    {
      leaveGuid: "review-leave-001",
      storeCode: "REV001",
      storeName: "Demo Brisbane",
      leaveType: "AnnualLeave",
      startDate: today,
      endDate: today,
      reason: "Synthetic leave request",
      status: "Pending",
      submittedAt: timestamp,
    },
  ];
  const approvals = [
    {
      approvalGuid: "review-approval-001",
      sourceGuid: "review-leave-001",
      sourceType: "Leave",
      employeeName: "Demo Staff",
      storeCode: "REV001",
      storeName: "Demo Brisbane",
      workDate: today,
      title: "Annual leave request",
      detail: "Synthetic approval",
      status: "Pending",
      submittedAt: timestamp,
    },
  ];
  const holidays = [
    {
      holidayGuid: "review-holiday-001",
      storeCode: "REV001",
      storeName: "Demo Brisbane",
      holidayDate: attendanceToday,
      holidayName: "Demo Public Holiday",
      businessStatus: "Open",
      openTime: "10:00",
      closeTime: "16:00",
      isPaidHoliday: true,
      remark: "Synthetic holiday",
    },
  ];
  const users = [
    {
      userGuid: "review-staff-001",
      userGUID: "review-staff-001",
      username: "demo_staff",
      fullName: "Demo Staff",
      displayName: "Demo Staff",
      email: "demo-staff@example.invalid",
      phone: "0400000002",
      storeCode: "REV001",
      storeName: "Demo Brisbane",
      roleNames: ["StoreStaff"],
      accessStoreAssignments: [
        {
          storeGUID: IOS_REVIEW_STORES[0].storeGUID,
          isPrimary: false,
        },
      ],
      accessRoleGuids: ["review-role-store-staff"],
      directPermissionCodes: ["PosTerminal.Sales.AddItem"],
      status: 1,
      isActive: true,
      createdAt: timestamp,
      updatedAt: timestamp,
    },
  ];
  // 为可启用 POS 的演示分店提供单人和同店批量打印样例。
  users.push(...["Birgit", "Emily Chen", "Jason Lee", "Sarah Wang", "Michael"].map((name, index) => ({
    ...users[0],
    userGuid: `review-pos-staff-${index + 1}`,
    userGUID: `review-pos-staff-${index + 1}`,
    username: name.toLowerCase().replace(/ /g, "."),
    employmentType: index % 2 ? "fullTime" : "casual",
    fullName: name,
    displayName: name,
    email: `review-pos-${index + 1}@example.invalid`,
    storeCode: "REV003",
    storeName: "Demo Branch 03",
    status: index === 4 ? 0 : 1,
    isActive: index !== 4,
    accessStoreAssignments: [{ storeGUID: IOS_REVIEW_STORES.find((store) => store.storeCode === "REV003")!.storeGUID, isPrimary: false }],
  })));
  const employeeProfile = {
    username: "ios_app_review",
    sensitiveRevision: 1,
    identityType: "passport",
    displayName: "App Review Demo",
    bankBsb: "000-000",
    bankAccountNumber: "00000000",
    superannuationCompanyName: "Demo Super",
    superannuationCompanyCode: "DEMO",
    superannuationAccountNumber: "DEMO-0001",
    birthday: "1990-01-01",
    phone: "0400 000 001",
    gender: "other",
    employmentType: "fullTime",
    avatarUrl: "",
    identityId: "DEMO-ID-0001",
    identityPhotoUrl: products[0].productImage,
    identityPhotoUrlExpiresAt: new Date(now.getTime() + 86400000).toISOString(),
    address: "123 Demo Street, Brisbane QLD 4000",
    createdAt: timestamp,
    updatedAt: timestamp,
  };
  const cashierBarcode = {
    exists: true,
    barcode: IOS_REVIEW_SAMPLE_BARCODE,
    format: "EAN13",
    printCount: 0,
    createdAt: timestamp,
    updatedAt: timestamp,
  };
  const devices = [
    {
      id: "review-device-001",
      hardwareId: "REV-IOS-HARDWARE-001",
      systemDeviceNumber: "REV-SYS-001",
      deviceNumber: "REV-DEVICE-001",
      deviceType: "Mobile",
      deviceSystem: "iOS",
      deviceName: "App Review iPhone",
      storeCode: "REV001",
      storeName: "Demo Brisbane",
      status: 1,
      appVersion: "1.0.2",
      platform: "ios",
      lastSeenAt: timestamp,
      createdAt: timestamp,
      updatedAt: timestamp,
    },
  ];
  const appDevices = [
    {
      id: "review-app-device-001",
      hardwareId: "REV-IOS-HARDWARE-001",
      systemDeviceNumber: "REV-SYS-001",
      deviceSystem: "iOS",
      platform: "ios",
      storeCode: "REV001",
      appVersion: "1.0.2",
      appBuildVersion: "review",
      runtimeVersion: "1.0.2",
      channel: "production",
      updateSource: "embedded",
      lastSeenAtUtc: timestamp,
      isOnline: true,
      lastAuthMode: "iosReview",
      lastSeenUserGuid: "review-user",
      lastSeenUsername: "ios_app_review",
      lastSeenUserFullName: "App Review Demo",
    },
  ];
  const productDetails = new Map(
    products.map((product) => [
      String(product.productCode),
      createProductDetailFixture(product),
    ]),
  );

  return {
    now: timestamp,
    today,
    products,
    productDetails,
    hqSyncOperations: new Map(),
    carts,
    orders,
    locations,
    containers,
    containerDetails,
    domesticBatches,
    domesticProducts,
    invoices,
    invoiceDetails,
    advertisements,
    promotions,
    installmentOrders,
    vouchers,
    seasonalCatalog,
    seasonalSubmissions,
    punches: [],
    punchAdjustments: [],
    availability,
    leaveRequests,
    approvals,
    schedules,
    holidays,
    users,
    posPermissionCodes: [...IOS_REVIEW_PERMISSION_CODES],
    employeeProfile,
    cashierBarcode,
    devices,
    appDevices,
    mirrorIds: new Map(),
    sequence: 100,
    localBarcodeCounters: {},
  };
}

function createCartLine(
  product: JsonRecord,
  quantity: number,
  timestamp: string,
) {
  return {
    detailGUID: `review-cart-line-${product.productCode}`,
    productCode: product.productCode,
    itemNumber: product.itemNumber,
    barcode: product.barcode,
    grade: product.grade,
    productName: product.productName,
    productImage: product.productImage,
    price: product.oemPrice ?? product.retailPrice ?? 0,
    quantity,
    allocQuantity: quantity,
    amount: (product.oemPrice ?? product.retailPrice ?? 0) * quantity,
    importPrice: product.importPrice ?? product.purchasePrice ?? 0,
    importAmount:
      (product.importPrice ?? product.purchasePrice ?? 0) * quantity,
    volume: product.volume ?? 0,
    totalVolume: (product.volume ?? 0) * quantity,
    minOrderQuantity: product.minOrderQuantity ?? 1,
    isActive: true,
    locationCode: product.locationCode,
    rrp: product.oemPrice ?? product.retailPrice,
    updatedAt: timestamp,
  };
}

function createProductDetailFixture(product: JsonRecord): JsonRecord {
  const productCode = String(product.productCode);
  return {
    ...clone(product),
    productType: 1,
    productTypeLabel: "Standard",
    localSupplierCode: product.localSupplierCode,
    localSupplierName: product.supplierName,
    storePrice: {
      uuid: `review-store-price-${productCode}`,
      storeCode: "REV001",
      storeName: "Demo Brisbane",
      productCode,
      storeProductCode: productCode,
      supplierCode: product.supplierCode,
      purchasePrice: product.purchasePrice,
      retailPrice: product.retailPrice,
      discountRate: 0,
      isAutoPricing: false,
      isSpecialProduct: false,
      isActive: true,
      rate: 1,
    },
    clearancePrice: null,
    setCodes: [
      {
        setCodeId: `review-set-${productCode}`,
        productCode,
        setProductCode: productCode,
        setItemNumber: product.itemNumber,
        setBarcode: product.barcode,
        setPurchasePrice: product.purchasePrice,
        setRetailPrice: product.retailPrice,
        setQuantity: 1,
        setType: 1,
        setTypeDescription: "Standard",
        isActive: true,
      },
    ],
    multiCodes: [
      {
        uuid:
          productCode === "REV-PROD-001"
            ? "review-multi-001"
            : `review-multi-${productCode}`,
        setCodeId: "",
        storeCode: "REV001",
        productCode,
        multiCodeProductCode: productCode,
        storeMultiCodeProductCode: productCode,
        barcode: product.barcode,
        purchasePrice: product.purchasePrice,
        retailPrice: product.retailPrice,
        discountRate: 0,
        isAutoPricing: false,
        isSpecialProduct: false,
        isActive: true,
        rate: 1,
      },
    ],
    setCodeCount: 1,
    multiCodeCount: 1,
    codesIncluded: true,
  };
}

function createCart(
  storeCode: string,
  items: JsonRecord[],
  timestamp: string,
): JsonRecord {
  return summarizeCart({
    orderGUID: `review-cart-${storeCode}`,
    orderNo: `REV-CART-${storeCode}`,
    storeCode,
    storeName:
      IOS_REVIEW_STORES.find((store) => store.storeCode === storeCode)
        ?.storeName ?? storeCode,
    remarks: "Offline review cart",
    orderDate: timestamp,
    flowStatus: 0,
    items,
  });
}

function summarizeCart(cart: JsonRecord): JsonRecord {
  const items = Array.isArray(cart.items) ? cart.items : [];
  return {
    ...cart,
    totalAmount: items.reduce(
      (sum: number, item: JsonRecord) => sum + Number(item.amount ?? 0),
      0,
    ),
    totalQuantity: items.reduce(
      (sum: number, item: JsonRecord) => sum + Number(item.quantity ?? 0),
      0,
    ),
    totalImportAmount: items.reduce(
      (sum: number, item: JsonRecord) => sum + Number(item.importAmount ?? 0),
      0,
    ),
    totalSku: new Set(items.map((item: JsonRecord) => item.productCode)).size,
    totalVolume: items.reduce(
      (sum: number, item: JsonRecord) => sum + Number(item.totalVolume ?? 0),
      0,
    ),
  };
}

function asRecord(value: unknown): JsonRecord {
  return value && typeof value === "object" && !Array.isArray(value)
    ? (value as JsonRecord)
    : {};
}

// 审核模式的货柜明细查询：与真实接口一样支持分页、搜索、筛选和排序，
// 这样审核时翻页/换每页条数/排序的表现与线上一致（演示数据本身可能不足一页）。
const REVIEW_CONTAINER_SORT_ACCESSORS: Record<string, (row: JsonRecord) => unknown> = {
  itemNumber: (row) => row.商品信息?.货号,
  productName: (row) => row.商品名称 ?? row.商品信息?.商品名称,
  englishName: (row) => row.英文名称 ?? row.商品信息?.英文名称,
  barcode: (row) => row.商品信息?.条形码,
  containerQuantity: (row) => row.装柜数量,
  containerPieces: (row) => row.装柜件数,
  packingQuantity: (row) => row.单件装箱数,
  unitVolume: (row) => row.单件体积,
  domesticPrice: (row) => row.国内价格,
  importPrice: (row) => row.进口价格,
  warehouseImportPrice: (row) => row.warehouseImportPrice,
  oemPrice: (row) => row.贴牌价格,
  newProduct: (row) => (row.是否新商品 ? 1 : 0),
  warehouseStatus: (row) => (row.warehouseIsActive === true ? 1 : 0),
};

function reviewContainsText(value: unknown, keyword: unknown) {
  const needle = String(keyword ?? "").trim().toLowerCase();
  if (!needle) return true;
  return String(value ?? "").toLowerCase().includes(needle);
}

function reviewInRange(value: unknown, min: unknown, max: unknown) {
  const number = typeof value === "number" ? value : Number.NaN;
  if (typeof min === "number" && !(number >= min)) return false;
  if (typeof max === "number" && !(number <= max)) return false;
  return true;
}

function reviewCompare(left: unknown, right: unknown) {
  // 空值排在最前（升序），与数据库 NULL 最小的默认行为一致。
  if (left == null && right == null) return 0;
  if (left == null) return -1;
  if (right == null) return 1;
  if (typeof left === "number" && typeof right === "number") return left - right;
  return String(left).localeCompare(String(right), "en", { numeric: true, sensitivity: "base" });
}

export function queryReviewContainerDetails(details: JsonRecord[], rawBody: unknown) {
  const body = asRecord(rawBody);
  const list = (value: unknown): string[] => (Array.isArray(value) ? value.map(String) : []);
  const productTypes = list(body.productTypes);
  const warehouseStatuses = list(body.warehouseStatus);
  const matchTypes = list(body.matchTypes);
  const tags = list(body.selectedTags).filter((tag) => tag !== "all");
  const isNew = (row: JsonRecord) => Boolean(row.是否新商品);
  const isActive = (row: JsonRecord) => row.warehouseIsActive === true;

  const matched = details.filter((row) => {
    const info = asRecord(row.商品信息);
    if (!reviewContainsText(info.货号, body.itemNumber)) return false;
    if (!reviewContainsText(info.条形码, body.barcode)) return false;
    if (!reviewContainsText(row.商品名称 ?? info.商品名称, body.productName)) return false;
    if (!reviewContainsText(row.英文名称 ?? info.英文名称, body.englishName)) return false;
    if (productTypes.length) {
      const type = row.商品类型 === "套装子商品" ? "setChild" : row.商品类型 === "套装商品" ? "set" : row.商品类型 === "多码商品" ? "multi" : "normal";
      if (!productTypes.includes(type)) return false;
    }
    if (warehouseStatuses.length && !warehouseStatuses.includes(isActive(row) ? "active" : "inactive")) return false;
    if (matchTypes.length && !matchTypes.includes(String(row.matchType ?? "unmatched"))) return false;
    if (!reviewInRange(row.装柜数量, body.containerQuantityMin, body.containerQuantityMax)) return false;
    if (!reviewInRange(row.中包数, body.middlePackQuantityMin, body.middlePackQuantityMax)) return false;
    if (!reviewInRange(row.warehouseImportPrice, body.warehouseImportPriceMin, body.warehouseImportPriceMax)) return false;
    if (!reviewInRange(row.贴牌价格, body.oemPriceMin, body.oemPriceMax)) return false;
    if (tags.includes("new") && !isNew(row)) return false;
    if (tags.includes("existing") && isNew(row)) return false;
    if (tags.includes("active") && !isActive(row)) return false;
    if (tags.includes("inactive") && isActive(row)) return false;
    return true;
  });

  const accessor = REVIEW_CONTAINER_SORT_ACCESSORS[String(body.sortBy ?? "itemNumber")]
    ?? REVIEW_CONTAINER_SORT_ACCESSORS.itemNumber;
  const direction = body.sortOrder === "descend" || body.sortOrder === "desc" ? -1 : 1;
  const sorted = matched
    .map((row, index) => ({ row, index }))
    // 排序键相同时保持原顺序，保证翻页稳定。
    .sort((left, right) => direction * reviewCompare(accessor(left.row), accessor(right.row)) || left.index - right.index)
    .map((entry) => entry.row);

  // 与后端一致：pageSize 夹在 1..1000，未传/非正数取 100；页码至少为 1。
  const requestedSize = Number(body.pageSize);
  const pageSize = Math.min(1000, Math.max(1, Number.isFinite(requestedSize) && requestedSize > 0 ? Math.floor(requestedSize) : 100));
  const requestedPage = Number(body.pageNumber);
  const pageNumber = Math.max(1, Number.isFinite(requestedPage) ? Math.floor(requestedPage) : 1);
  const start = (pageNumber - 1) * pageSize;
  const stats = {
    all: details.length,
    new: details.filter(isNew).length,
    existing: details.filter((row) => !isNew(row)).length,
    noOemPrice: details.filter((row) => isNew(row) && !(Number(row.贴牌价格) > 0)).length,
    abnormalImport: details.filter((row) => !(Number(row.进口价格) > 0)).length,
    active: details.filter(isActive).length,
    inactive: details.filter((row) => !isActive(row)).length,
  };

  return {
    items: clone(sorted.slice(start, start + pageSize)),
    itemsTotal: sorted.length,
    pageNumber,
    pageSize,
    hasMore: start + pageSize < sorted.length,
    totalComputed: true,
    statsComputed: true,
    tagStats: stats,
  };
}

function nextId(state: AppRouteState, prefix: string) {
  state.sequence += 1;
  return `${prefix}-${state.sequence}`;
}

function createReviewHqSyncOperation(
  state: AppRouteState,
  productCode: unknown,
  storeCode?: unknown,
) {
  const operationId = nextId(state, "review-hq-sync");
  const operation = {
    operationId,
    status: "pending",
    productCode: String(productCode ?? ""),
    storeCode: storeCode == null ? null : String(storeCode),
    attemptCount: 0,
    nextAttemptAt: state.now,
    retryable: true,
    errorCode: null,
    message: "Saved locally. Updating HQ.",
  };
  state.hqSyncOperations.set(operationId, operation);
  return clone(operation);
}

function page(items: JsonRecord[], pageNumber = 1, pageSize = 20) {
  return {
    items: clone(items),
    total: items.length,
    totalCount: items.length,
    page: pageNumber,
    pageNumber,
    pageSize,
  };
}

function reportUnitPrice(amount: unknown, quantity: unknown) {
  const safeQuantity = Number(quantity);
  return safeQuantity > 0 ? Number(amount) / safeQuantity : 0;
}

// 与后端商品明细排序口径一致：本期值 → 同期值 → 商品编码；均价在数量不大于 0 时按 0。
const REVIEW_REPORT_PRODUCT_SORT_VALUES: ReportSortValues<JsonRecord> = {
  amount: (row) => [Number(row.salesAmount), Number(row.compareSalesAmount)],
  quantity: (row) => [Number(row.quantity), Number(row.compareQuantity)],
  unitPrice: (row) => [
    reportUnitPrice(row.salesAmount, row.quantity),
    reportUnitPrice(row.compareSalesAmount, row.compareQuantity),
  ],
};

// 与后端 ProductReportSort.Parse 一致：字段只认白名单、未知回退金额；asc/ascend 为升序，其余降序。
// 两个参数都没传时返回 null，保持 fixture 原有顺序。
function parseReviewReportSort(query: URLSearchParams): ReportSort | null {
  const field = query.get("sortField")?.trim().toLowerCase() ?? "";
  const order = query.get("sortOrder")?.trim().toLowerCase() ?? "";
  if (!field && !order) return null;
  return {
    field: field === "quantity" ? "quantity" : field === "unitprice" ? "unitPrice" : "amount",
    order: order === "asc" || order === "ascend" ? "asc" : "desc",
  };
}

function pagedSlice(items: JsonRecord[], pageNumber: number, pageSize: number) {
  const safePageNumber =
    Number.isFinite(pageNumber) && pageNumber > 0 ? Math.trunc(pageNumber) : 1;
  const safePageSize =
    Number.isFinite(pageSize) && pageSize > 0 ? Math.trunc(pageSize) : 20;
  const start = (safePageNumber - 1) * safePageSize;
  const result = page(
    items.slice(start, start + safePageSize),
    safePageNumber,
    safePageSize,
  );
  return {
    ...result,
    total: items.length,
    totalCount: items.length,
  };
}

function findByAnyId(items: JsonRecord[], id: string) {
  return items.find((item) =>
    [
      item.id,
      item.orderGuid,
      item.orderGUID,
      item.voucherCode,
      item.submissionGuid,
      item.invoiceGuid,
      item.batchNumber,
      item.locationGuid,
      item.userGuid,
      item.userGUID,
      item.scheduleGuid,
      item.holidayGuid,
    ].some((value) => String(value ?? "") === id),
  );
}

function register(
  transport: ReviewTransport,
  methods: readonly ReviewMethod[],
  path: string | RegExp,
  handle: RouteHandler,
) {
  methods.forEach((method) => transport.register({ method, path, handle }));
}

function getHolder(dataStore: ReviewDataStore) {
  let holder = stateHolders.get(dataStore);
  if (!holder) {
    holder = { current: createInitialState(dataStore.getNow()) };
    stateHolders.set(dataStore, holder);
  }
  return holder;
}

function getDomesticSetTemplates(state: AppRouteState) {
  let templates = domesticSetTemplateStates.get(state);
  if (!templates) {
    templates = [];
    domesticSetTemplateStates.set(state, templates);
  }
  return templates;
}

function createReviewDomesticCode(state: AppRouteState, kind: string) {
  return nextId(state, `REV-DOMESTIC-${kind}`);
}

function createReviewDomesticBarcode(state: AppRouteState) {
  const sequence = nextId(state, "REV-BARCODE").replace(/\D/g, "");
  return `933${sequence.padStart(10, "0").slice(-10)}`;
}

function asPositiveInteger(value: unknown, fallback: number) {
  const number = Number(value);
  return Number.isSafeInteger(number) && number > 0 ? number : fallback;
}

function createReviewDomesticProduct(
  state: AppRouteState,
  supplierCode: string,
  item: JsonRecord,
  productType: 0 | 1,
) {
  const productCode = createReviewDomesticCode(state, productType === 1 ? "SET" : "NORMAL");
  const hbProductNo = createReviewDomesticCode(state, productType === 1 ? "HB-SET" : "HB-NORMAL");
  const barcode = createReviewDomesticBarcode(state);
  const productName = String(item.productName ?? "");
  return {
    productCode,
    supplierCode,
    supplierName: String(item.supplierName ?? "Review Supplier"),
    productName,
    englishProductName: productName,
    hbProductNo,
    barcode,
    productSpecification: "Review demo specification",
    productType,
    domesticPrice: item.setPrice ?? null,
    oemPrice: item.privateLabelPrice ?? null,
    importPrice: null,
    packingQuantity: null,
    unitVolume: null,
    middlePackQuantity: null,
    productImage: null,
    isActive: true,
  };
}

function createReviewDomesticBatch(
  state: AppRouteState,
  dataStore: ReviewDataStore,
  payload: JsonRecord,
) {
  const supplierCode = String(payload.supplierCode ?? "").trim();
  const inputItems = Array.isArray(payload.items) ? payload.items : [];
  const normalItems: JsonRecord[] = [];
  const setItems: JsonRecord[] = [];
  const createdParents: JsonRecord[] = [];
  const detailItems: JsonRecord[] = [];

  for (const rawItem of inputItems) {
    const item = asRecord(rawItem);
    if (Number(item.productType) === 1) setItems.push(item);
    else normalItems.push(item);
  }

  for (const item of normalItems) {
    const product = createReviewDomesticProduct(state, supplierCode, item, 0);
    state.domesticProducts.unshift(product);
    const created = {
      ...product,
      privateLabelPrice: item.privateLabelPrice ?? null,
      setQuantity: null,
      setPrice: null,
      subItems: [],
    };
    createdParents.push(created);
    detailItems.push({ ...created, subItems: undefined });
  }

  for (const item of setItems) {
    const createCount = asPositiveInteger(item.createCount, 1);
    const subItems = Array.isArray(item.subItems) ? item.subItems : [];
    for (let copyIndex = 0; copyIndex < createCount; copyIndex++) {
      const product = createReviewDomesticProduct(state, supplierCode, item, 1);
      state.domesticProducts.unshift(product);
      const createdSubItems: JsonRecord[] = [];
      const created = {
        ...product,
        privateLabelPrice: item.privateLabelPrice ?? null,
        setQuantity: item.setQuantity ?? subItems.length,
        setPrice: item.setPrice ?? null,
        subItems: createdSubItems,
      };
      createdParents.push(created);
      detailItems.push({ ...created, subItems: undefined });

      for (let subIndex = 0; subIndex < subItems.length; subIndex++) {
        const subItem = asRecord(subItems[subIndex]);
        const subProductCode = createReviewDomesticCode(state, "SET-ITEM");
        const subHbProductNo = createReviewDomesticCode(state, "HB-SET-ITEM");
        const subBarcode = createReviewDomesticBarcode(state);
        const subProductName = String(subItem.productName ?? "");
        const createdSubItem = {
          productCode: subProductCode,
          hbProductNo: subHbProductNo,
          barcode: subBarcode,
          productName: subProductName,
          productType: 2,
          privateLabelPrice: subItem.privateLabelPrice ?? null,
          parentProductCode: product.productCode,
          parentHBProductNo: product.hbProductNo,
        };
        createdSubItems.push(createdSubItem);
        detailItems.push(createdSubItem);
      }
    }
  }

  const batchNumber = nextId(state, "REV-BATCH");
  const batch = {
    batchNumber,
    supplierCode,
    supplierName: String(payload.supplierName ?? "Review Supplier"),
    prefixCode: payload.prefixCode ?? null,
    normalProductCount: normalItems.length,
    setProductCount: createdParents.filter((item) => Number(item.productType) === 1).length,
    totalCount: detailItems.length,
    totalCreated: detailItems.length,
    createdTime: state.now,
    createdBy: "App Review Demo",
    items: detailItems,
  };
  state.domesticBatches.unshift(batch);
  mirrorCreate(state, dataStore, "domesticPurchase", batchNumber, batchNumber);
  return { batch, createdParents };
}

function mirrorCreate(
  state: AppRouteState,
  dataStore: ReviewDataStore,
  domain: IosReviewDomainName,
  key: string,
  label: string,
  status = "active",
) {
  const entity = dataStore.create(domain, { label, status });
  state.mirrorIds.set(key, entity.id);
}

function mirrorUpdate(
  state: AppRouteState,
  dataStore: ReviewDataStore,
  domain: IosReviewDomainName,
  key: string,
  label: string,
  status = "updated",
) {
  const mirrorId = state.mirrorIds.get(key) ?? dataStore.list(domain)[0]?.id;
  if (!mirrorId) {
    mirrorCreate(state, dataStore, domain, key, label, status);
    return;
  }
  dataStore.update(domain, mirrorId, { label, status });
  state.mirrorIds.set(key, mirrorId);
}

function mirrorRemove(
  state: AppRouteState,
  dataStore: ReviewDataStore,
  domain: IosReviewDomainName,
  key: string,
) {
  const mirrorId = state.mirrorIds.get(key) ?? dataStore.list(domain)[0]?.id;
  if (mirrorId) dataStore.remove(domain, mirrorId);
  state.mirrorIds.delete(key);
}

function binaryExport() {
  // 以最小 ZIP 文件头模拟 xlsx/pdf 二进制，避免审核模式触发任何真实导出请求。
  return new Uint8Array([0x50, 0x4b, 0x03, 0x04, 0x52, 0x45, 0x56]).buffer;
}

/**
 * 重置所有实际 App endpoint 使用的内存 fixture。
 * 登出时应与审核会话标记和 React Query 缓存一并清理。
 */
export function resetIosReviewAppRouteState(dataStore: ReviewDataStore) {
  dataStore.reset();
  const holder = getHolder(dataStore);
  holder.current = createInitialState(dataStore.getNow());
}

/**
 * 注册 App 实际使用的审核模式端点。
 * 仅登记已审计路径；未知请求继续由 transport 统一 fail-closed。
 */
export function registerIosReviewAppRoutes(
  transport: ReviewTransport,
  dataStore: ReviewDataStore,
) {
  const holder = getHolder(dataStore);
  const state = () => holder.current;

  register(transport, ["GET"], "/navigation/app-menu", () => ({
    data: IOS_REVIEW_MENU_ITEMS.map((item) => ({ ...item })),
  }));
  register(transport, ["GET"], "/auth/current", () => ({
    data: createIosReviewUser(),
  }));
  register(transport, ["POST"], "/auth/logout", () => ({
    data: { success: true },
  }));
  // 绑定 / 更换邮箱：审核模式不发邮件，任意 6 位验证码即通过，并写回演示个人资料。
  register(transport, ["POST"], "/auth/email-change/request", ({ body }) => {
    const email = String(asRecord(body).newEmail ?? "").trim().toLowerCase();
    if (!email.includes("@")) throw new Error("IOS_REVIEW_EMAIL_REQUIRED");
    return { data: { maskedEmail: maskReviewEmail(email), expiresAtUtc: state().now } };
  });
  register(transport, ["POST"], "/auth/email-change/confirm", ({ body }) => {
    const record = asRecord(body);
    const email = String(record.newEmail ?? "").trim().toLowerCase();
    if (!email.includes("@") || !/^\d{6}$/.test(String(record.code ?? ""))) {
      throw new Error("IOS_REVIEW_EMAIL_CODE_INVALID");
    }
    Object.assign(state().employeeProfile, { email, updatedAt: state().now });
    return { data: { email } };
  });
  register(transport, ["GET"], "/react/v1/preorders/active", ({ query }) => ({
    // iOS 审核离线数据不创建 Preorder，必须显式返回已解锁，避免 fail-closed 门禁锁死演示流程。
    data: {
      storeCode: query.get("storeCode") ?? "",
      normalOrderBlocked: false,
      activations: [],
    },
  }));
  register(
    transport,
    ["GET", "POST"],
    /^\/Users\/guid\/([^/]+)\/stores$/i,
    ({ method, match, body }) => {
      const userGuid = decodeURIComponent(match?.[1] ?? "");
      const user = findByAnyId(state().users, userGuid);
      if (!user) {
        return { data: IOS_REVIEW_STORES.map((store) => ({ ...store })) };
      }

      if (method === "POST") {
        user.accessStoreAssignments = Array.isArray(body)
          ? body.map((item) => {
              const assignment = asRecord(item);
              return {
                storeGUID: String(
                  assignment.StoreGUID ?? assignment.storeGUID ?? "",
                ),
                isPrimary: Boolean(
                  assignment.IsPrimary ?? assignment.isPrimary,
                ),
              };
            })
          : [];
        mirrorUpdate(
          state(),
          dataStore,
          "users",
          userGuid,
          "User store access",
        );
        return { data: true };
      }

      const assignments = Array.isArray(user.accessStoreAssignments)
        ? user.accessStoreAssignments.map(asRecord)
        : [];
      return {
        data: assignments.flatMap((assignment) => {
          const store = IOS_REVIEW_STORES.find(
            (item) => item.storeGUID === assignment.storeGUID,
          );
          return store
            ? [{ ...store, isPrimary: Boolean(assignment.isPrimary) }]
            : [];
        }),
      };
    },
  );
  register(transport, ["GET"], "/stores/all-by-name", () => ({
    data: IOS_REVIEW_STORES.map((store) => ({ ...store })),
  }));
  register(transport, ["GET"], "/Roles/active", () => ({
    data: [
      {
        roleGUID: "review-role-store-staff",
        roleName: "StoreStaff",
        description: "Standard review store account",
        isActive: true,
      },
      {
        roleGUID: "review-role-store-manager",
        roleName: "StoreManager",
        description: "Derived from managed stores",
        isActive: true,
      },
    ],
  }));
  register(transport, ["GET"], "/Roles/permissions", () => ({
    data: [
      {
        category: "Users",
        displayName: "User management",
        description: "Synthetic account access permissions",
        permissions: [
          {
            name: "Users.View",
            displayName: "View users",
            description: "View staff accounts",
            category: "Users",
            isSystemPermission: true,
          },
          {
            name: "Users.Create",
            displayName: "Create users",
            description: "Create store staff accounts",
            category: "Users",
            isSystemPermission: true,
          },
          {
            name: "Users.ResetPassword",
            displayName: "Reset user passwords",
            description: "Reset store staff account passwords",
            category: "Users",
            isSystemPermission: true,
          },
        ],
      },
      {
        category: "PosTerminal",
        displayName: "POS terminal",
        description: "Synthetic account default POS permissions",
        permissions: [
          {
            name: "PosTerminal.Sales.AddItem",
            displayName: "Add sale item",
            description: "Account default before store override",
            category: "PosTerminal",
            isSystemPermission: true,
          },
        ],
      },
    ],
  }));
  register(transport, ["GET"], "/react/v1/warehouse-categories/tree", () => ({
    data: [
      {
        categoryGUID: "review-category-homeware",
        categoryName: "Homeware",
        children: [],
      },
    ],
  }));
  register(transport, ["GET"], "/react/v1/product-grades/options", () => ({
    data: { items: [{ grade: "A" }, { grade: "B" }] },
  }));
  register(transport, ["GET"], "/react/v1/local-suppliers/active", () => ({
    data: [
      {
        supplierCode: "REV-SUP-001",
        supplierName: "Demo Local Supplier",
      },
    ],
  }));

  register(transport, ["POST"], "/react/v1/store-order/products", () => ({
    data: page(state().products, 1, 24),
  }));
  register(
    transport,
    ["POST"],
    "/react/v1/store-order/dynamic-data",
    ({ body }) => {
      const payload = asRecord(body);
      const storeCode = String(payload.storeCode ?? "REV001");
      const cart = state().carts.get(storeCode);
      const codes = Array.isArray(payload.productCodes)
        ? payload.productCodes
        : state().products.map((product) => product.productCode);
      return {
        data: codes.map((productCode: string) => ({
          productCode,
          lastOrderDate: state().today,
          lastQuantity: 2,
          lastAllocQuantity: 2,
          cartQuantity:
            cart?.items?.find(
              (item: JsonRecord) => item.productCode === productCode,
            )?.quantity ?? 0,
        })),
      };
    },
  );
  register(
    transport,
    ["POST"],
    "/react/v1/store-order/products/scan-lookup",
    ({ body }) => {
      const payload = asRecord(body);
      const barcode = String(payload.barcode ?? payload.Barcode ?? "");
      return {
        data: {
          barcode,
          items: state().products.filter(
            (product) => product.barcode === barcode,
          ),
        },
      };
    },
  );
  register(
    transport,
    ["GET"],
    /^\/react\/v1\/store-order\/cart\/([^/]+)$/i,
    ({ match }) => {
      const storeCode = decodeURIComponent(match?.[1] ?? "REV001");
      const current = state();
      const cart =
        current.carts.get(storeCode) ?? createCart(storeCode, [], current.now);
      current.carts.set(storeCode, cart);
      return { data: clone(summarizeCart(cart)) };
    },
  );

  const mutateCart: RouteHandler = ({ path, body }) => {
    const current = state();
    const payload = asRecord(body);
    const storeCode = String(payload.storeCode ?? "REV001");
    const cart =
      current.carts.get(storeCode) ?? createCart(storeCode, [], current.now);
    current.carts.set(storeCode, cart);
    // scan-lookup-add 只传条码；在本地先解析为商品编码，再复用购物车写入逻辑。
    const scannedProduct = current.products.find(
      (item) =>
        item.barcode === payload.barcode || item.barcode === payload.Barcode,
    );
    const productCode = String(
      payload.productCode ?? scannedProduct?.productCode ?? "",
    );
    const existingIndex = cart.items.findIndex(
      (item: JsonRecord) => item.productCode === productCode,
    );
    let changedItem: JsonRecord | null = null;
    let removed = false;

    if (path.endsWith("/clear")) {
      cart.items = [];
      mirrorCreate(
        current,
        dataStore,
        "carts",
        `cart-clear:${storeCode}`,
        `Cleared ${storeCode}`,
        "cleared",
      );
    } else if (path.endsWith("/remove")) {
      const detailGuid = String(payload.detailGUID ?? payload.detailGuid ?? "");
      const removeIndex = cart.items.findIndex(
        (item: JsonRecord) =>
          item.detailGUID === detailGuid || item.productCode === productCode,
      );
      if (removeIndex >= 0) {
        const [removedItem] = cart.items.splice(removeIndex, 1);
        removed = true;
        mirrorRemove(
          current,
          dataStore,
          "carts",
          `cart:${storeCode}:${removedItem.productCode}`,
        );
      }
    } else {
      const product = current.products.find(
        (item) => item.productCode === productCode,
      );
      if (!product) {
        throw new Error(`IOS_REVIEW_PRODUCT_NOT_FOUND: ${productCode}`);
      }
      const quantity = Math.max(0, Number(payload.quantity ?? 1));
      if (existingIndex >= 0) {
        changedItem = {
          ...cart.items[existingIndex],
          quantity,
          amount: Number(cart.items[existingIndex].price) * quantity,
          importAmount:
            Number(cart.items[existingIndex].importPrice) * quantity,
          totalVolume: Number(cart.items[existingIndex].volume ?? 0) * quantity,
          updatedAt: current.now,
        };
        cart.items[existingIndex] = changedItem;
        mirrorUpdate(
          current,
          dataStore,
          "carts",
          `cart:${storeCode}:${productCode}`,
          `Cart ${productCode}`,
          "updated",
        );
      } else {
        changedItem = createCartLine(product, quantity, current.now);
        cart.items.push(changedItem);
        mirrorCreate(
          current,
          dataStore,
          "carts",
          `cart:${storeCode}:${productCode}`,
          `Cart ${productCode}`,
        );
      }
    }

    const summary = summarizeCart(cart);
    Object.assign(cart, summary);
    if (path.includes("/scan-")) {
      const mutation = {
        productCode,
        removed,
        changedItem: clone(changedItem),
        summary: {
          orderGUID: cart.orderGUID,
          storeCode,
          cartRevision: current.sequence,
          totalAmount: cart.totalAmount,
          totalImportAmount: cart.totalImportAmount,
          totalQuantity: cart.totalQuantity,
          totalSku: cart.totalSku,
        },
      };
      if (path.endsWith("/scan-lookup-add")) {
        return {
          data: {
            barcode: String(payload.barcode ?? payload.Barcode ?? ""),
            matchType: "Barcode",
            items: changedItem ? [clone(changedItem)] : [],
            added: Boolean(changedItem),
            cart: mutation,
          },
        };
      }
      return { data: mutation };
    }
    return { data: clone(cart) };
  };
  register(
    transport,
    ["POST"],
    /^\/react\/v1\/store-order\/cart\/(?:add|update|remove|clear|scan-add|scan-update|scan-lookup-add)$/i,
    mutateCart,
  );
  register(transport, ["POST"], "/react/v1/store-order/submit", ({ body }) => {
    const current = state();
    const payload = asRecord(body);
    const storeCode = String(payload.storeCode ?? "REV001");
    const cart = summarizeCart(
      current.carts.get(storeCode) ?? createCart(storeCode, [], current.now),
    );
    const orderGUID = nextId(current, "review-order");
    const order = {
      ...clone(cart),
      orderGUID,
      orderNo: `REV-ORDER-${String(current.sequence).padStart(4, "0")}`,
      flowStatus: 1,
      remarks: String(payload.remarks ?? cart.remarks ?? ""),
      totalOrderAmount: cart.totalAmount,
      importTotalAmount: cart.totalImportAmount,
      oemTotalAmount: cart.totalAmount,
      totalAllocQuantity: cart.totalQuantity,
      totalOrderVolume: cart.totalVolume,
      totalAllocVolume: cart.totalVolume,
      createdAt: current.now,
      updatedAt: current.now,
    };
    current.orders.unshift(order);
    current.carts.set(storeCode, createCart(storeCode, [], current.now));
    mirrorCreate(
      current,
      dataStore,
      "orders",
      orderGUID,
      order.orderNo,
      "submitted",
    );
    return { data: clone(order), status: 201 };
  });
  register(transport, ["POST"], "/react/v1/store-order/list", () => ({
    data: page(state().orders),
  }));
  register(
    transport,
    ["GET"],
    /^\/react\/v1\/store-order\/detail\/([^/]+)$/i,
    ({ match }) => {
      const id = decodeURIComponent(match?.[1] ?? "");
      const order = findByAnyId(state().orders, id);
      if (!order) throw new Error(`IOS_REVIEW_ORDER_NOT_FOUND: ${id}`);
      return { data: clone(order) };
    },
  );

  register(
    transport,
    ["GET"],
    "/react/v1/product-warehouse/mobile/lookup",
    ({ query }) => {
      const keyword = (query.get("keyword") ?? "").toLowerCase();
      return {
        data: state().products.filter((product) =>
          [
            product.productCode,
            product.itemNumber,
            product.barcode,
            product.productName,
          ]
            .join(" ")
            .toLowerCase()
            .includes(keyword),
        ),
      };
    },
  );
  register(
    transport,
    ["GET", "PATCH"],
    /^\/react\/v1\/product-warehouse\/mobile\/([^/]+)$/i,
    ({ method, match, body }) => {
      const productCode = decodeURIComponent(match?.[1] ?? "");
      const product = state().products.find(
        (item) => item.productCode === productCode,
      );
      if (!product)
        throw new Error(`IOS_REVIEW_PRODUCT_NOT_FOUND: ${productCode}`);
      if (method === "PATCH") {
        Object.assign(product, asRecord(body), { updatedAt: state().now });
        mirrorUpdate(
          state(),
          dataStore,
          "warehouse",
          `warehouse:${productCode}`,
          product.productName,
        );
      }
      return { data: clone(product) };
    },
  );
  register(
    transport,
    ["PUT"],
    /^\/react\/v1\/product-warehouse\/mobile\/([^/]+)\/location$/i,
    ({ match, body }) => {
      const productCode = decodeURIComponent(match?.[1] ?? "");
      const product = state().products.find(
        (item) => item.productCode === productCode,
      );
      if (!product)
        throw new Error(`IOS_REVIEW_PRODUCT_NOT_FOUND: ${productCode}`);
      const location = state().locations.find(
        (item) => item.locationGuid === asRecord(body).locationGuid,
      );
      Object.assign(product, {
        locationGuid: location?.locationGuid ?? null,
        locationCode: location?.locationCode ?? null,
        locationBarcode: location?.locationBarcode ?? null,
        updatedAt: state().now,
      });
      mirrorUpdate(
        state(),
        dataStore,
        "warehouse",
        `warehouse:${productCode}`,
        product.productName,
      );
      return { data: clone(product) };
    },
  );
  register(
    transport,
    ["GET"],
    /^\/react\/v1\/product-warehouse\/mobile\/([^/]+)\/print-payload$/i,
    ({ match, query }) => {
      const productCode = decodeURIComponent(match?.[1] ?? "");
      const product = state().products.find(
        (item) => item.productCode === productCode,
      );
      if (!product)
        throw new Error(`IOS_REVIEW_PRODUCT_NOT_FOUND: ${productCode}`);
      if (query.get("type") === "location") {
        const location = state().locations.find(
          (item) => item.locationGuid === product.locationGuid,
        );
        return {
          data: clone({
            ...location,
            itemNumber: product.itemNumber,
            productName: product.productName,
          }),
        };
      }
      return { data: clone(product) };
    },
  );
  register(
    transport,
    ["POST"],
    /^\/react\/v1\/product-warehouse\/mobile\/[^/]+\/image-upload-signature$/i,
    () => ({
      data: {
        url: "data:application/octet-stream;base64,UkVWSUVX",
        objectKey: "ios-review/warehouse/demo-image.svg",
        headers: {},
      },
    }),
  );
  register(
    transport,
    ["POST"],
    "/react/v1/product-warehouse/detect",
    ({ body }) => ({
      data: (Array.isArray(asRecord(body).Items)
        ? asRecord(body).Items
        : []
      ).map((item: JsonRecord) => ({
        localProductCode: item.ProductCode ?? "REV-PROD-001",
        domesticProductCode: item.ProductCode ?? "REV-PROD-001",
        matchType: "productCode",
        hasProductCodeConflict: false,
      })),
    }),
  );

  register(transport, ["GET"], "/react/v1/locations/lookup", ({ query }) => {
    const keyword = (query.get("keyword") ?? "").toLowerCase();
    return {
      data: state().locations.filter((location) =>
        [location.locationCode, location.locationBarcode]
          .join(" ")
          .toLowerCase()
          .includes(keyword),
      ),
    };
  });
  register(transport, ["GET"], "/react/v1/locations/mobile/unused", () => ({
    data: {
      items: clone(state().locations.filter((item) => item.productCount === 0)),
    },
  }));
  register(
    transport,
    ["GET"],
    /^\/react\/v1\/locations\/([^/]+)$/i,
    ({ match }) => {
      const id = decodeURIComponent(match?.[1] ?? "");
      const location = findByAnyId(state().locations, id);
      if (!location) throw new Error(`IOS_REVIEW_LOCATION_NOT_FOUND: ${id}`);
      return { data: clone(location) };
    },
  );
  register(transport, ["POST"], "/react/v1/locations", ({ body }) => {
    const current = state();
    const payload = asRecord(body);
    const location: JsonRecord = {
      ...payload,
      locationGuid: nextId(current, "review-location"),
      productCount: 0,
      products: [],
      updatedAt: current.now,
      updatedBy: "App Review Demo",
    };
    current.locations.push(location);
    mirrorCreate(
      current,
      dataStore,
      "warehouse",
      location.locationGuid,
      String(location.locationCode ?? "Demo location"),
    );
    return { data: clone(location), status: 201 };
  });
  register(
    transport,
    ["PUT", "DELETE"],
    /^\/react\/v1\/locations\/([^/]+)$/i,
    ({ method, match, body }) => {
      const current = state();
      const id = decodeURIComponent(match?.[1] ?? "");
      const index = current.locations.findIndex(
        (item) => item.locationGuid === id,
      );
      if (index < 0) throw new Error(`IOS_REVIEW_LOCATION_NOT_FOUND: ${id}`);
      if (method === "DELETE") {
        current.locations.splice(index, 1);
        mirrorRemove(current, dataStore, "warehouse", id);
        return { data: { success: true } };
      }
      current.locations[index] = {
        ...current.locations[index],
        ...asRecord(body),
        locationGuid: id,
        updatedAt: current.now,
      };
      mirrorUpdate(
        current,
        dataStore,
        "warehouse",
        id,
        String(current.locations[index].locationCode ?? id),
      );
      return { data: clone(current.locations[index]) };
    },
  );
  register(
    transport,
    ["POST"],
    /^\/react\/v1\/locations\/([^/]+)\/products\/bind$/i,
    ({ match, body }) => {
      const current = state();
      const id = decodeURIComponent(match?.[1] ?? "");
      const location = findByAnyId(current.locations, id);
      if (!location) throw new Error(`IOS_REVIEW_LOCATION_NOT_FOUND: ${id}`);
      const identifier = String(asRecord(body).productIdentifier ?? "");
      const product = current.products.find((item) =>
        [item.productCode, item.itemNumber, item.barcode].includes(identifier),
      );
      if (
        product &&
        !location.products.some(
          (item: JsonRecord) => item.productCode === product.productCode,
        )
      ) {
        location.products.push(clone(product));
        location.productCount = location.products.length;
      }
      mirrorUpdate(
        current,
        dataStore,
        "warehouse",
        id,
        String(location.locationCode ?? id),
      );
      return { data: clone(location) };
    },
  );
  register(
    transport,
    ["DELETE"],
    /^\/react\/v1\/locations\/([^/]+)\/products\/([^/]+)$/i,
    ({ match }) => {
      const current = state();
      const id = decodeURIComponent(match?.[1] ?? "");
      const productCode = decodeURIComponent(match?.[2] ?? "");
      const location = findByAnyId(current.locations, id);
      if (!location) throw new Error(`IOS_REVIEW_LOCATION_NOT_FOUND: ${id}`);
      location.products = location.products.filter(
        (item: JsonRecord) => item.productCode !== productCode,
      );
      location.productCount = location.products.length;
      mirrorUpdate(
        current,
        dataStore,
        "warehouse",
        id,
        String(location.locationCode ?? id),
      );
      return { data: clone(location) };
    },
  );

  register(transport, ["POST"], "/react/v1/containers/list", () => ({
    data: page(state().containers),
  }));
  register(
    transport,
    ["GET"],
    /^\/react\/v1\/containers\/([^/]+)$/i,
    ({ match }) => {
      const id = decodeURIComponent(match?.[1] ?? "");
      const container = state().containers.find(
        (item) => item.hguid === id || item.HGUID === id,
      );
      if (!container) throw new Error(`IOS_REVIEW_CONTAINER_NOT_FOUND: ${id}`);
      return { data: clone(container) };
    },
  );
  register(transport, ["POST"], "/react/v1/containers", ({ body }) => {
    const current = state();
    const id = nextId(current, "review-container");
    current.containers.push({ ...asRecord(body), hguid: id, HGUID: id });
    mirrorCreate(current, dataStore, "warehouse", id, "Created demo container");
    return { data: { containerGuid: id }, status: 201 };
  });
  register(
    transport,
    ["PUT"],
    /^\/react\/v1\/containers\/([^/]+)$/i,
    ({ match, body }) => {
      const id = decodeURIComponent(match?.[1] ?? "");
      const container = state().containers.find(
        (item) => item.hguid === id || item.HGUID === id,
      );
      if (!container) throw new Error(`IOS_REVIEW_CONTAINER_NOT_FOUND: ${id}`);
      Object.assign(container, asRecord(body));
      mirrorUpdate(
        state(),
        dataStore,
        "warehouse",
        id,
        "Updated demo container",
      );
      return { data: { success: true } };
    },
  );
  register(
    transport,
    ["POST"],
    /^\/react\/v1\/containers\/([^/]+)\/products\/query$/i,
    ({ body }) => ({
      data: queryReviewContainerDetails(state().containerDetails, body),
    }),
  );
  register(
    transport,
    ["POST"],
    /^\/react\/v1\/containers\/[^/]+\/products\/export$/i,
    () => ({
      data: binaryExport(),
      headers: {
        "content-type":
          "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        "content-disposition": "attachment; filename=review-container.xlsx",
      },
    }),
  );
  register(
    transport,
    ["POST"],
    /^\/react\/v1\/containers\/(?:batch-update-details|batch-delete-details|sync-from-hq|push-to-hbsales|details\/align-domestic-product-code)$/i,
    ({ path, body }) => {
      const current = state();
      const requested = Array.isArray(body)
        ? body.length
        : Array.isArray(asRecord(body).hguids)
          ? asRecord(body).hguids.length
          : 1;
      mirrorUpdate(current, dataStore, "warehouse", path, path, "completed");
      return {
        data: {
          success: true,
          totalRequested: requested,
          totalUpdated: requested,
          totalDeleted: path.includes("delete") ? requested : 0,
          oldProductCode: "REV-PROD-OLD",
          newProductCode: "REV-PROD-001",
          updatedDomesticProducts: 1,
          updatedContainerDetails: 1,
          syncedCount: 1,
        },
      };
    },
  );
  register(
    transport,
    ["POST"],
    /^\/react\/v1\/containers\/[^/]+\/actions\/(?:apply-float-rate|apply-prices|recalculate-costs|backfill-last-prices)$/i,
    () => ({
      data: { success: true, totalRequested: 1, totalUpdated: 1 },
    }),
  );
  register(
    transport,
    ["POST"],
    /^\/react\/v1\/(?:container-products\/create-new-products|container-products\/submit-container|products\/push-to-hq)\/jobs$/i,
    ({ path, body }) => {
      const current = state();
      const jobId = nextId(current, "review-job");
      mirrorCreate(current, dataStore, "warehouse", jobId, path, "completed");
      return {
        data: {
          jobId,
          operationId: asRecord(body).operationId,
          status: "Succeeded",
          result: {
            createdCount: 1,
            successCount: 1,
            totalCount: 1,
            failedCount: 0,
            containerCompleted: true,
            errors: [],
          },
        },
        status: 201,
      };
    },
  );
  register(
    transport,
    ["GET"],
    /^\/react\/v1\/(?:container-products\/create-new-products|products\/push-to-hq)\/jobs\/([^/]+)$/i,
    ({ match }) => ({
      data: {
        jobId: decodeURIComponent(match?.[1] ?? "review-job"),
        status: "Succeeded",
        result: {
          createdCount: 1,
          successCount: 1,
          totalCount: 1,
          failedCount: 0,
          containerCompleted: true,
          errors: [],
        },
      },
    }),
  );

  register(transport, ["GET"], "/v1/domestic-product-creation/batches", () => ({
    data: page(state().domesticBatches),
  }));
  register(
    transport,
    ["GET"],
    "/v1/domestic-product-creation/templates",
    ({ query }) => {
      const supplierCode = (query.get("supplierCode") ?? "").trim();
      return {
        // 与真实接口默认行为一致：审核列表只暴露当前供应商的启用模板。
        data: clone(
          getDomesticSetTemplates(state()).filter(
            (template) => template.isEnabled && template.supplierCode === supplierCode,
          ),
        ),
      };
    },
  );
  register(
    transport,
    ["GET"],
    /^\/v1\/domestic-product-creation\/templates\/([^/]+)$/i,
    ({ match, query }) => {
      const templateId = decodeURIComponent(match?.[1] ?? "");
      const supplierCode = (query.get("supplierCode") ?? "").trim();
      const template = getDomesticSetTemplates(state()).find(
        (item) =>
          item.templateId === templateId &&
          item.supplierCode === supplierCode &&
          item.isEnabled,
      );
      if (!template) {
        throw new Error(`IOS_REVIEW_DOMESTIC_SET_TEMPLATE_NOT_FOUND: ${templateId}`);
      }
      return { data: clone(template) };
    },
  );
  register(
    transport,
    ["POST"],
    "/v1/domestic-product-creation/templates",
    ({ body }) => {
      const current = state();
      const payload = asRecord(body);
      const rawSubItems = Array.isArray(payload.subItems) ? payload.subItems : [];
      const now = current.now;
      const template: ReviewDomesticSetTemplate = {
        templateId: nextId(current, "REV-TEMPLATE"),
        supplierCode: String(payload.supplierCode ?? "").trim(),
        templateName: String(payload.templateName ?? "").trim(),
        setProductName: String(payload.setProductName ?? "").trim(),
        isEnabled: payload.isEnabled === false ? false : true,
        setQuantity: rawSubItems.length,
        updatedAt: now,
        // 每次保存都建立新的数组和子项对象，后续重复应用不会共享可变引用。
        subItems: rawSubItems.map((rawItem, index) => {
          const item = asRecord(rawItem);
          return {
            productName: String(item.productName ?? ""),
            privateLabelPrice: Number(item.privateLabelPrice ?? 0),
            sortOrder: Number.isFinite(Number(item.sortOrder))
              ? Number(item.sortOrder)
              : index,
          };
        }),
      };
      getDomesticSetTemplates(current).unshift(template);
      return { data: clone(template), status: 201 };
    },
  );
  register(
    transport,
    ["GET"],
    /^\/v1\/domestic-product-creation\/batch\/([^/]+)$/i,
    ({ match }) => {
      const id = decodeURIComponent(match?.[1] ?? "");
      const batch = findByAnyId(state().domesticBatches, id);
      if (!batch) throw new Error(`IOS_REVIEW_BATCH_NOT_FOUND: ${id}`);
      return { data: clone(batch) };
    },
  );
  register(
    transport,
    ["POST"],
    "/v1/domestic-product-creation/batch",
    ({ body }) => {
      const result = createReviewDomesticBatch(state(), dataStore, asRecord(body));
      return { data: clone(result.batch), status: 201 };
    },
  );
  register(
    transport,
    ["PUT"],
    /^\/v1\/domestic-product-creation\/batch\/([^/]+)\/items$/i,
    ({ match, body }) => {
      const current = state();
      const id = decodeURIComponent(match?.[1] ?? "");
      const batch = findByAnyId(current.domesticBatches, id);
      if (!batch) throw new Error(`IOS_REVIEW_BATCH_NOT_FOUND: ${id}`);
      batch.items = Array.isArray(asRecord(body).items)
        ? asRecord(body).items
        : body;
      mirrorUpdate(current, dataStore, "domesticPurchase", id, id);
      return { data: clone(batch) };
    },
  );
  register(
    transport,
    ["GET"],
    /^\/v1\/domestic-product-creation\/batch\/[^/]+\/export$/i,
    () => ({
      data: binaryExport(),
      headers: {
        "content-type":
          "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
      },
    }),
  );
  register(transport, ["GET"], "/v1/DomesticProducts", () => ({
    data: page(state().domesticProducts),
  }));
  register(
    transport,
    ["PUT"],
    /^\/v1\/DomesticProducts\/([^/]+)$/i,
    ({ match, body }) => {
      const current = state();
      const code = decodeURIComponent(match?.[1] ?? "");
      const product = current.domesticProducts.find(
        (item) => item.productCode === code,
      );
      if (!product) throw new Error(`IOS_REVIEW_PRODUCT_NOT_FOUND: ${code}`);
      Object.assign(product, asRecord(body), { productCode: code });
      mirrorUpdate(
        current,
        dataStore,
        "domesticPurchase",
        code,
        product.productName,
      );
      return { data: clone(product) };
    },
  );
  register(transport, ["GET"], "/v1/ChinaSuppliers/active", () => ({
    data: [{ supplierCode: "REV-CN-SUP", supplierName: "Demo China Supplier" }],
  }));
  register(transport, ["GET"], "/v1/ProductPrefixCodes", () => ({
    data: {
      items: [
        {
          prefixCode: "REV",
          prefixName: "Review Demo",
          prefixDescription: "Synthetic prefix",
        },
      ],
    },
  }));

  register(
    transport,
    ["POST"],
    "/react/v1/local-supplier-invoices/grid",
    () => ({
      data: page(state().invoices),
    }),
  );
  register(
    transport,
    ["GET"],
    /^\/react\/v1\/local-supplier-invoices\/([^/]+)$/i,
    ({ match }) => {
      const id = decodeURIComponent(match?.[1] ?? "");
      const invoice = findByAnyId(state().invoices, id);
      if (!invoice) throw new Error(`IOS_REVIEW_INVOICE_NOT_FOUND: ${id}`);
      return { data: clone(invoice) };
    },
  );
  register(
    transport,
    ["POST"],
    /^\/react\/v1\/local-supplier-invoices\/([^/]+)\/details\/grid$/i,
    () => ({
      data: page(state().invoiceDetails, 1, 50),
    }),
  );

  register(transport, ["POST"], "/react/v1/advertisements/grid", () => ({
    data: page(state().advertisements),
  }));
  register(
    transport,
    ["POST"],
    "/react/v1/advertisements/upload-signature",
    () => ({
      data: {
        url: "data:application/octet-stream;base64,UkVWSUVX",
        uploadUrl: "data:application/octet-stream;base64,UkVWSUVX",
        mediaUrl: "data:image/svg+xml,%3Csvg%3E%3C/svg%3E",
        objectKey: "ios-review/advertisements/upload.svg",
        headers: {},
      },
    }),
  );
  register(transport, ["POST"], "/react/v1/advertisements", ({ body }) => {
    const current = state();
    const advertisement: JsonRecord = {
      ...asRecord(body),
      id: nextId(current, "review-ad"),
    };
    current.advertisements.unshift(advertisement);
    mirrorCreate(
      current,
      dataStore,
      "advertisements",
      advertisement.id,
      String(advertisement.title ?? "Demo ad"),
    );
    return { data: clone(advertisement), status: 201 };
  });
  register(
    transport,
    ["GET", "PUT", "DELETE"],
    /^\/react\/v1\/advertisements\/([^/]+)$/i,
    ({ method, match, body }) => {
      const current = state();
      const id = decodeURIComponent(match?.[1] ?? "");
      const index = current.advertisements.findIndex((item) => item.id === id);
      if (index < 0)
        throw new Error(`IOS_REVIEW_ADVERTISEMENT_NOT_FOUND: ${id}`);
      if (method === "DELETE") {
        current.advertisements.splice(index, 1);
        mirrorRemove(current, dataStore, "advertisements", id);
        return { data: { success: true } };
      }
      if (method === "PUT") {
        current.advertisements[index] = {
          ...current.advertisements[index],
          ...asRecord(body),
          id,
        };
        mirrorUpdate(
          current,
          dataStore,
          "advertisements",
          id,
          String(current.advertisements[index].title ?? id),
        );
      }
      return { data: clone(current.advertisements[index]) };
    },
  );
  register(
    transport,
    ["POST"],
    /^\/react\/v1\/advertisements\/([^/]+)\/enable$/i,
    ({ match, query }) => {
      const id = decodeURIComponent(match?.[1] ?? "");
      const item = state().advertisements.find(
        (candidate) => candidate.id === id,
      );
      if (!item) throw new Error(`IOS_REVIEW_ADVERTISEMENT_NOT_FOUND: ${id}`);
      item.isEnabled = query.get("enable") !== "false";
      mirrorUpdate(
        state(),
        dataStore,
        "advertisements",
        id,
        item.title,
        item.isEnabled ? "active" : "disabled",
      );
      return { data: true };
    },
  );

  register(transport, ["POST"], "/react/v1/promotions/store/grid", () => ({
    data: page(state().promotions),
  }));
  register(transport, ["POST"], "/react/v1/promotions/store", ({ body }) => {
    const current = state();
    const promotion: JsonRecord = {
      ...asRecord(body),
      id: nextId(current, "review-promotion"),
      scopeType: "StoreOnly",
      canEditInStoreScope: true,
      canCopyToStore: true,
    };
    current.promotions.unshift(promotion);
    mirrorCreate(
      current,
      dataStore,
      "promotions",
      promotion.id,
      String(promotion.name ?? "Demo promotion"),
    );
    return { data: clone(promotion), status: 201 };
  });
  register(
    transport,
    ["POST"],
    "/react/v1/promotions/store/copy",
    ({ body }) => {
      const current = state();
      const payload = asRecord(body);
      const source =
        current.promotions.find(
          (item) => item.id === payload.sourcePromotionId,
        ) ?? current.promotions[0];
      const promotion = {
        ...clone(source),
        id: nextId(current, "review-promotion"),
        name: payload.name ?? `${source?.name ?? "Demo promotion"} Copy`,
        stores: [{ storeCode: payload.storeCode ?? "REV002" }],
        scopeType: "StoreOnly",
      };
      current.promotions.unshift(promotion);
      mirrorCreate(
        current,
        dataStore,
        "promotions",
        promotion.id,
        promotion.name,
      );
      return { data: clone(promotion), status: 201 };
    },
  );
  register(
    transport,
    ["GET", "PUT"],
    /^\/react\/v1\/promotions\/store\/([^/]+)$/i,
    ({ method, match, body }) => {
      const id = decodeURIComponent(match?.[1] ?? "");
      const promotion = state().promotions.find((item) => item.id === id);
      if (!promotion) throw new Error(`IOS_REVIEW_PROMOTION_NOT_FOUND: ${id}`);
      if (method === "PUT") {
        Object.assign(promotion, asRecord(body), { id });
        mirrorUpdate(state(), dataStore, "promotions", id, promotion.name);
      }
      return { data: clone(promotion) };
    },
  );
  register(
    transport,
    ["POST"],
    /^\/react\/v1\/promotions\/store\/([^/]+)\/enable$/i,
    ({ match, query }) => {
      const id = decodeURIComponent(match?.[1] ?? "");
      const promotion = state().promotions.find((item) => item.id === id);
      if (!promotion) throw new Error(`IOS_REVIEW_PROMOTION_NOT_FOUND: ${id}`);
      promotion.isEnabled = query.get("enable") !== "false";
      mirrorUpdate(
        state(),
        dataStore,
        "promotions",
        id,
        promotion.name,
        promotion.isEnabled ? "active" : "disabled",
      );
      return { data: true };
    },
  );
  register(
    transport,
    ["GET"],
    "/react/v1/promotions/valid/by-product",
    ({ query }) => {
      const productCode = query.get("productCode") ?? "";
      const storeCode = query.get("storeCode") ?? "";
      const now = Date.parse(state().now);
      const promotions = state().promotions
        .filter((promotion) => {
          const products = Array.isArray(promotion.products) ? promotion.products : [];
          const stores = Array.isArray(promotion.stores) ? promotion.stores : [];
          // 与真实接口一致：软删除关联既不能匹配商品/门店，也不能阻止总部活动判定。
          const activeStores = stores.filter(
            (store) => asRecord(store).isDeleted !== true,
          );
          const productMatches = products.some(
            (product) => {
              const productLink = asRecord(product);
              return (
                productLink.isDeleted !== true
                && String(productLink.productCode ?? "") === productCode
              );
            },
          );
          const storeMatches = activeStores.length === 0 || activeStores.some(
            (store) => String(asRecord(store).storeCode ?? "") === storeCode,
          );
          const startsAt = Date.parse(String(promotion.effectiveStart ?? ""));
          const endsAt = Date.parse(String(promotion.effectiveEnd ?? ""));
          return (
            promotion.isDeleted !== true
            && promotion.isEnabled !== false
            && productMatches
            && storeMatches
            && startsAt <= now
            && endsAt >= now
          );
        })
        // 审核数据保持与真实接口一致的确定性顺序，避免多活动展示只依赖 fixture 插入顺序。
        .sort((left, right) => {
          const priorityDifference = Number(right.priority ?? 0) - Number(left.priority ?? 0);
          if (priorityDifference !== 0) {
            return priorityDifference;
          }

          const startDifference =
            Date.parse(String(left.effectiveStart ?? ""))
            - Date.parse(String(right.effectiveStart ?? ""));
          if (startDifference !== 0) {
            return startDifference;
          }

          return String(left.id ?? "").localeCompare(String(right.id ?? ""));
        });
      return { data: clone(promotions) };
    },
  );

  register(
    transport,
    ["GET"],
    /^\/react\/v1\/store-product-maintenance\/hq-sync\/([^/]+)$/i,
    ({ match }) => {
      const operationId = decodeURIComponent(match?.[1] ?? "");
      const operation = state().hqSyncOperations.get(operationId);
      if (!operation) {
        throw new Error(`IOS_REVIEW_HQ_SYNC_NOT_FOUND: ${operationId}`);
      }
      operation.status = "succeeded";
      operation.nextAttemptAt = null;
      operation.retryable = false;
      operation.errorCode = null;
      operation.message = "Saved and updated in HQ.";
      return { data: clone(operation) };
    },
  );
  register(
    transport,
    ["POST"],
    /^\/react\/v1\/store-product-maintenance\/hq-sync\/([^/]+)\/retry$/i,
    ({ match }) => {
      const operationId = decodeURIComponent(match?.[1] ?? "");
      const operation = state().hqSyncOperations.get(operationId);
      if (!operation) {
        throw new Error(`IOS_REVIEW_HQ_SYNC_NOT_FOUND: ${operationId}`);
      }
      if (operation.status !== "blocked") {
        throw new Error(`IOS_REVIEW_HQ_SYNC_RETRY_NOT_ALLOWED: ${operationId}`);
      }
      operation.status = "pending";
      operation.nextAttemptAt = state().now;
      operation.retryable = true;
      operation.errorCode = null;
      operation.message = "Saved locally. Updating HQ.";
      return { data: clone(operation) };
    },
  );
  register(
    transport,
    ["POST"],
    "/react/v1/store-product-maintenance/lookup",
    ({ body }) => {
      const keyword = String(asRecord(body).keyword ?? "").toLowerCase();
      return {
        data: state().products.filter((product) =>
          [
            product.productCode,
            product.itemNumber,
            product.barcode,
            product.productName,
          ]
            .join(" ")
            .toLowerCase()
            .includes(keyword),
        ),
      };
    },
  );
  register(
    transport,
    ["GET"],
    /^\/react\/v1\/store-product-maintenance\/([^/]+)(?:\/fast-detail)?$/i,
    ({ match }) => {
      const code = decodeURIComponent(match?.[1] ?? "");
      const detail = state().productDetails.get(code);
      if (!detail) throw new Error(`IOS_REVIEW_PRODUCT_NOT_FOUND: ${code}`);
      return { data: clone(detail) };
    },
  );
  register(
    transport,
    ["GET"],
    /^\/react\/v1\/store-product-maintenance\/([^/]+)\/codes$/i,
    ({ match, query }) => {
      const code = decodeURIComponent(match?.[1] ?? "");
      const detail = state().productDetails.get(code);
      if (!detail) throw new Error(`IOS_REVIEW_PRODUCT_NOT_FOUND: ${code}`);
      const requestedType = query.get("type") ?? query.get("productType");
      const items = requestedType === "2" ? detail.multiCodes : detail.setCodes;
      return {
        data: {
          items: clone(items),
          totalCount: items.length,
          page: Number(query.get("page") ?? 1),
          pageSize: Number(query.get("pageSize") ?? 50),
          hasMore: false,
        },
      };
    },
  );
  register(
    transport,
    ["POST"],
    "/react/v1/products/generate-local-barcode",
    ({ query }) => {
      if (!query.get("supplierCode")?.trim() || query.get("supplierCode")?.trim() === "200") {
        throw new Error("此供应商商品不能自行创建，请联系管理员");
      }
      const current = state();
      // 审核演示仅在会话内分配示例编号，不调用生产预留接口。
      const date = new Date(new Date(current.now).getTime() + 10 * 60 * 60 * 1000)
        .toISOString().slice(2, 10).replace(/-/g, "");
      const prefix = `9529${date}`;
      const sequence = current.localBarcodeCounters[prefix] ?? 0;
      if (sequence >= 100) throw new Error("当天的 100 个自动条码已用满，请联系管理员");
      current.localBarcodeCounters[prefix] = sequence + 1;
      const body = `${prefix}${String(sequence).padStart(2, "0")}`;
      const sum = [...body].reduce((total, digit, index) => total + Number(digit) * (index % 2 === 0 ? 1 : 3), 0);
      return { data: { barcode: `${body}${(10 - sum % 10) % 10}` } };
    },
  );
  register(
    transport,
    ["POST"],
    "/react/v1/products/create-with-prices",
    ({ body }) => {
      const current = state();
      const payload = asRecord(body);
      const productCode = String(
        payload.productCode ?? nextId(current, "REV-PROD"),
      );
      const product: JsonRecord = {
        ...payload,
        productCode,
        barcode: payload.barcode ?? `9330000${current.sequence}`,
        isActive: true,
        warehouseIsActive: true,
        updatedAt: current.now,
      };
      current.products.push(product);
      current.productDetails.set(
        productCode,
        createProductDetailFixture(product),
      );
      mirrorCreate(
        current,
        dataStore,
        "products",
        productCode,
        String(product.productName ?? productCode),
      );
      return {
        data: {
          productCode,
          product: clone(product),
          storeProductCodes: {},
          created: true,
          hqSync: createReviewHqSyncOperation(current, productCode),
        },
        status: 201,
      };
    },
  );
  register(
    transport,
    ["PUT"],
    /^\/react\/v1\/store-product-maintenance\/store-prices\/([^/]+)$/i,
    ({ match, body }) => {
      const current = state();
      const uuid = decodeURIComponent(match?.[1] ?? "review-store-price");
      const detail = Array.from(current.productDetails.values()).find(
        (item) => item.storePrice?.uuid === uuid,
      );
      if (!detail) throw new Error(`IOS_REVIEW_STORE_PRICE_NOT_FOUND: ${uuid}`);
      Object.assign(detail.storePrice, asRecord(body), { uuid });
      mirrorUpdate(current, dataStore, "products", uuid, "Store price");
      return {
        data: {
          ...clone(detail.storePrice),
          hqSync: createReviewHqSyncOperation(
            current,
            detail.productCode,
            detail.storePrice.storeCode,
          ),
        },
      };
    },
  );
  register(
    transport,
    ["POST"],
    /^\/react\/v1\/store-product-maintenance\/store-prices\/([^/]+)\/sync-warehouse$/i,
    ({ match, body }) => {
      const current = state();
      const uuid = decodeURIComponent(match?.[1] ?? "");
      const detail = Array.from(current.productDetails.values()).find(
        (item) => item.storePrice?.uuid === uuid,
      );
      if (!detail) throw new Error(`IOS_REVIEW_STORE_PRICE_NOT_FOUND: ${uuid}`);
      const payload = asRecord(body);
      const previousPurchasePrice = detail.storePrice.purchasePrice ?? null;
      const previousRetailPrice = detail.storePrice.retailPrice ?? null;
      const warehousePurchasePrice =
        payload.expectedWarehousePurchasePrice ?? previousPurchasePrice;
      const warehouseRetailPrice =
        payload.expectedWarehouseRetailPrice ?? previousRetailPrice;
      detail.storePrice.purchasePrice = warehousePurchasePrice;
      const retailUpdated = Boolean(payload.confirmRetailPrice);
      if (retailUpdated) detail.storePrice.retailPrice = warehouseRetailPrice;
      mirrorUpdate(
        current,
        dataStore,
        "products",
        uuid,
        "Warehouse price sync",
        "synced",
      );
      return {
        data: {
          status: retailUpdated ? "synced" : "confirmation_required",
          purchaseUpdated: warehousePurchasePrice !== previousPurchasePrice,
          retailUpdated,
          retailConfirmationRequired: !retailUpdated,
          storePrice: clone(detail.storePrice),
          warehousePurchasePrice,
          warehouseRetailPrice,
          previousStorePurchasePrice: previousPurchasePrice,
          previousStoreRetailPrice: previousRetailPrice,
          discountRate: detail.storePrice.discountRate ?? 0,
          previousDiscountedRetailPrice: previousRetailPrice,
          newDiscountedRetailPrice: detail.storePrice.retailPrice,
          hqSync: createReviewHqSyncOperation(
            current,
            detail.productCode,
            detail.storePrice.storeCode,
          ),
        },
      };
    },
  );
  register(
    transport,
    ["POST"],
    "/react/v1/store-product-maintenance/evaluate-auto-pricing",
    ({ body }) => ({
      data: {
        ...asRecord(body),
        currentRetailPrice: 12.95,
        recalculatedRetailPrice: 12.95,
        currentRetailPriceFormatted: "$12.95",
        recalculatedRetailPriceFormatted: "$12.95",
        hasValidPurchasePrice: true,
        shouldUpdate: false,
      },
    }),
  );
  register(
    transport,
    ["PUT"],
    /^\/react\/v1\/store-product-maintenance\/products\/([^/]+)\/(?:type|clearance-price)$/i,
    ({ path, match, body }) => {
      const code = decodeURIComponent(match?.[1] ?? "");
      const product = state().products.find(
        (item) => item.productCode === code,
      );
      const detail = state().productDetails.get(code);
      if (!product || !detail)
        throw new Error(`IOS_REVIEW_PRODUCT_NOT_FOUND: ${code}`);
      Object.assign(product, asRecord(body));
      Object.assign(detail, asRecord(body));
      mirrorUpdate(state(), dataStore, "products", code, product.productName);
      if (path.endsWith("/type")) {
        return {
          data: {
            productCode: code,
            productType: product.productType ?? 1,
            productTypeLabel: "Standard",
            hqSync: createReviewHqSyncOperation(
              state(),
              code,
              asRecord(body).storeCode,
            ),
          },
        };
      }
      const clearancePayload = asRecord(body);
      if (clearancePayload.clearancePrice == null) {
        detail.clearancePrice = null;
        return {
          data: {
            uuid: `review-clearance-${code}`,
            storeCode: clearancePayload.storeCode ?? "REV001",
            storeName: "Demo Brisbane",
            productCode: code,
            clearanceBarcode: null,
            clearancePrice: null,
            hqSync: createReviewHqSyncOperation(
              state(),
              code,
              clearancePayload.storeCode,
            ),
          },
        };
      }
      detail.clearancePrice = {
        uuid: `review-clearance-${code}`,
        storeCode: "REV001",
        storeName: "Demo Brisbane",
        productCode: code,
        clearanceBarcode:
          asRecord(body).clearanceBarcode ?? IOS_REVIEW_SAMPLE_BARCODE,
        clearancePrice: clearancePayload.clearancePrice,
      };
      return {
        data: {
          ...clone(detail.clearancePrice),
          hqSync: createReviewHqSyncOperation(
            state(),
            code,
            asRecord(body).storeCode,
          ),
        },
      };
    },
  );
  register(
    transport,
    ["POST"],
    "/react/v1/store-product-maintenance/set-codes",
    ({ body }) => {
      const current = state();
      const payload = asRecord(body);
      const detail = current.productDetails.get(
        String(payload.productCode ?? ""),
      );
      if (!detail)
        throw new Error(
          `IOS_REVIEW_PRODUCT_NOT_FOUND: ${payload.productCode ?? ""}`,
        );
      if (Number(payload.productType) === 2) {
        const item = {
          uuid: nextId(current, "review-multi-code"),
          setCodeId: "",
          storeCode: payload.storeCode ?? "REV001",
          productCode: payload.productCode,
          multiCodeProductCode: payload.productCode,
          storeMultiCodeProductCode: payload.productCode,
          barcode: payload.barcode,
          purchasePrice: detail.storePrice?.purchasePrice ?? null,
          retailPrice: payload.retailPrice ?? null,
          discountRate: 0,
          isAutoPricing: false,
          isSpecialProduct: false,
          isActive: payload.isActive ?? true,
          rate: 1,
        };
        detail.multiCodes.push(item);
        detail.multiCodeCount = detail.multiCodes.length;
        mirrorCreate(current, dataStore, "products", item.uuid, "Multi code");
        return {
          data: {
            ...clone(item),
            hqSync: createReviewHqSyncOperation(
              current,
              payload.productCode,
              payload.storeCode,
            ),
          },
          status: 201,
        };
      }
      const item = {
        setCodeId: nextId(current, "review-set-code"),
        productCode: payload.productCode,
        setProductCode: payload.productCode,
        setItemNumber: detail.itemNumber,
        setBarcode: payload.barcode,
        setPurchasePrice: detail.storePrice?.purchasePrice ?? null,
        setRetailPrice: payload.retailPrice ?? null,
        setQuantity: 1,
        setType: payload.productType ?? 1,
        setTypeDescription: "Standard",
        isActive: payload.isActive ?? true,
      };
      detail.setCodes.push(item);
      detail.setCodeCount = detail.setCodes.length;
      mirrorCreate(current, dataStore, "products", item.setCodeId, "Set code");
      return {
        data: {
          ...clone(item),
          hqSync: createReviewHqSyncOperation(
            current,
            payload.productCode,
            payload.storeCode,
          ),
        },
        status: 201,
      };
    },
  );
  register(
    transport,
    ["PUT", "DELETE"],
    /^\/react\/v1\/store-product-maintenance\/(set-codes|multi-codes)\/([^/]+)$/i,
    ({ method, match, body }) => {
      const id = decodeURIComponent(match?.[2] ?? "");
      const detail = Array.from(state().productDetails.values()).find(
        (item) =>
          item.setCodes.some((code: JsonRecord) => code.setCodeId === id) ||
          item.multiCodes.some((code: JsonRecord) => code.uuid === id),
      );
      if (!detail) throw new Error(`IOS_REVIEW_PRODUCT_CODE_NOT_FOUND: ${id}`);
      // 页面当前对 Set/Multi Code 共用 /set-codes/:id；必须按真实 id 归属判断集合。
      const collection = detail.setCodes.some(
        (code: JsonRecord) => code.setCodeId === id,
      )
        ? "setCodes"
        : "multiCodes";
      const idKey = collection === "multiCodes" ? "uuid" : "setCodeId";
      const index = detail[collection].findIndex(
        (code: JsonRecord) => code[idKey] === id,
      );
      if (method === "DELETE") {
        const removed = detail[collection][index];
        const hqSync = createReviewHqSyncOperation(
          state(),
          removed?.productCode ?? detail.productCode,
          removed?.storeCode,
        );
        detail[collection].splice(index, 1);
        detail.setCodeCount = detail.setCodes.length;
        detail.multiCodeCount = detail.multiCodes.length;
        mirrorRemove(state(), dataStore, "products", id);
        return { data: { deleted: true, hqSync } };
      }
      const payload = asRecord(body);
      const values =
        collection === "setCodes"
          ? {
              setBarcode: payload.barcode,
              setRetailPrice: payload.retailPrice,
              isActive: payload.isActive,
            }
          : payload;
      detail[collection][index] = {
        ...detail[collection][index],
        ...values,
        [idKey]: id,
      };
      mirrorUpdate(state(), dataStore, "products", id, "Product code");
      return {
        data: {
          ...clone(detail[collection][index]),
          hqSync: createReviewHqSyncOperation(
            state(),
            detail[collection][index].productCode ?? detail.productCode,
            payload.storeCode ?? detail[collection][index].storeCode,
          ),
        },
      };
    },
  );

  register(
    transport,
    ["POST"],
    "/react/v1/installment-orders/list",
    ({ body }) => {
      const payload = asRecord(body);
      const storeCodes = (payload.branchCode ? [payload.branchCode] : [])
        .map((value: unknown) => String(value).trim().toUpperCase())
        .filter(Boolean);
      const statuses = (payload.status != null ? [payload.status] : [])
        .map((value: unknown) => Number(value))
        .filter((value: number) => Number.isFinite(value));
      const customerPhone = String(payload.customerPhone ?? "")
        .trim()
        .toLowerCase();
      const customerName = String(payload.customerName ?? "")
        .trim()
        .toLowerCase();
      const startDate = String(payload.startDate ?? "").trim();
      const endDate = String(payload.endDate ?? "").trim();

      // 审核模式执行与真实接口一致的筛选，避免固定 fixture 掩盖页面参数错误。
      const filtered = state().installmentOrders.filter((order) => {
        const storeCode = String(order.storeCode ?? "").toUpperCase();
        const createdDate = toStoreLocalDateOnly(
          order.createdAt,
          order.storeCode,
        );
        if (storeCodes.length && !storeCodes.includes(storeCode)) return false;
        if (statuses.length && !statuses.includes(Number(order.status)))
          return false;
        if (
          customerPhone &&
          !String(order.customerPhone ?? "")
            .toLowerCase()
            .includes(customerPhone)
        )
          return false;
        if (
          customerName &&
          !String(order.customerName ?? "")
            .toLowerCase()
            .includes(customerName)
        )
          return false;
        if (startDate && createdDate < startDate) return false;
        if (endDate && createdDate > endDate) return false;
        return true;
      });
      const summaries = filtered.map(
        ({
          lines: _lines,
          payments: _payments,
          pickupInfo: _pickupInfo,
          cancellationInfo: _cancellationInfo,
          note: _note,
          deviceCode: _deviceCode,
          ...order
        }) => order,
      );
      const pageNumber = Number(payload.pageNumber ?? 1);
      const pageSize = Number(payload.pageSize ?? 20);
      return { data: pagedSlice(summaries, pageNumber, pageSize) };
    },
  );
  register(
    transport,
    ["GET"],
    /^\/react\/v1\/installment-orders\/detail\/([^/]+)$/i,
    ({ match }) => {
      const id = decodeURIComponent(match?.[1] ?? "");
      const order = state().installmentOrders.find(
        (item) => String(item.installmentGuid ?? "") === id,
      );
      if (!order) throw new Error(`IOS_REVIEW_INSTALLMENT_NOT_FOUND: ${id}`);
      const {
        lines = [],
        payments = [],
        pickupInfo = null,
        cancellationInfo = null,
        ...detailOrder
      } = order;
      return {
        data: {
          // 主单与明细集合保持独立，匹配 Central Backend 的详情契约。
          order: clone(detailOrder),
          lines: clone(lines),
          payments: clone(payments),
          pickupInfo: clone(pickupInfo),
          cancellationInfo: clone(cancellationInfo),
        },
      };
    },
  );

  register(transport, ["POST"], "/react/v1/store-vouchers/list", () => ({
    data: page(state().vouchers),
  }));
  register(
    transport,
    ["GET"],
    /^\/react\/v1\/store-vouchers\/([^/]+)$/i,
    ({ match }) => {
      const id = decodeURIComponent(match?.[1] ?? "");
      const voucher = findByAnyId(state().vouchers, id);
      if (!voucher) throw new Error(`IOS_REVIEW_VOUCHER_NOT_FOUND: ${id}`);
      return {
        data: {
          voucher: clone(voucher),
          ledger: clone(voucher.ledger ?? []),
          relatedOrders: clone(voucher.relatedOrders ?? []),
        },
      };
    },
  );

  register(
    transport,
    ["GET"],
    "/react/v1/seasonal-card-remaining/catalog",
    () => ({
      data: clone(state().seasonalCatalog),
    }),
  );
  register(
    transport,
    ["GET"],
    "/react/v1/seasonal-card-remaining/submissions",
    () => ({
      data: page(state().seasonalSubmissions),
    }),
  );
  register(
    transport,
    ["POST"],
    "/react/v1/seasonal-card-remaining/submissions",
    ({ body }) => {
      const current = state();
      const payload = asRecord(body);
      const catalog = current.seasonalCatalog.find(
        (item) => item.catalogGuid === payload.catalogGuid,
      );
      const submission = {
        ...payload,
        submissionGuid: nextId(current, "review-seasonal-submission"),
        cardType: catalog?.cardType ?? 1,
        cardTypeName: catalog?.cardTypeName ?? "Christmas Card",
        unitPrice: payload.customUnitPrice ?? catalog?.fixedUnitPrice ?? 2,
        priceLabel: catalog?.priceLabel ?? "$2.00",
        submittedByName: "App Review Demo",
        submittedAt: current.now,
      };
      current.seasonalSubmissions.unshift(submission);
      mirrorCreate(
        current,
        dataStore,
        "seasonalCards",
        submission.submissionGuid,
        "Seasonal card submission",
      );
      return { data: clone(submission), status: 201 };
    },
  );
  register(
    transport,
    ["GET"],
    /^\/react\/v1\/seasonal-card-remaining\/submissions\/([^/]+)$/i,
    ({ match }) => {
      const id = decodeURIComponent(match?.[1] ?? "");
      const submission = findByAnyId(state().seasonalSubmissions, id);
      if (!submission)
        throw new Error(`IOS_REVIEW_SEASONAL_SUBMISSION_NOT_FOUND: ${id}`);
      return { data: clone(submission) };
    },
  );
  register(
    transport,
    ["GET"],
    "/react/v1/seasonal-card-remaining/overview",
    ({ query }) => {
      const current = state();
      const storeCode = query.get("storeCode")?.trim() || "REV001";
      const supplierCode = query.get("localSupplierCode")?.trim() ?? "";
      const today = reviewSeasonalToday(current, storeCode);
      return {
        data: {
          storeCode,
          seasonYear: Number(today.slice(0, 4)),
          today,
          localSupplierCode: supplierCode,
          supplierName: reviewSeasonalSupplierName(supplierCode),
          holidays: reviewSeasonalWindows(today).map((window) => ({
            cardType: window.cardType,
            cardTypeName:
              current.seasonalCatalog.find((item) => item.cardType === window.cardType)
                ?.cardTypeName ?? "",
            isOpen: window.isOpen,
            seasonYear: window.seasonYear,
            holidayDate: window.holidayDate,
            opensOn: window.opensOn,
            closesOn: window.closesOn,
            currentBatch: window.isOpen
              ? reviewSeasonalLatestBatch(
                  current.seasonalSubmissions,
                  storeCode,
                  window.seasonYear ?? 0,
                  window.cardType,
                  supplierCode,
                )
              : null,
          })),
        },
      };
    },
  );
  register(
    transport,
    ["POST"],
    "/react/v1/seasonal-card-remaining/submissions/batch",
    ({ body }) => {
      const current = state();
      const payload = asRecord(body);
      const storeCode = String(payload.storeCode ?? "").trim() || "REV001";
      const seasonYear = Number(payload.seasonYear);
      const cardType = Number(payload.cardType);
      const supplierCode = String(payload.localSupplierCode ?? "").trim();
      const items = Array.isArray(payload.items) ? payload.items.map(asRecord) : [];
      const catalogs = current.seasonalCatalog.filter(
        (item) => item.isEnabled && item.cardType === cardType,
      );
      // 与服务端一致：业务失败走 HTTP 200 + success=false 信封，由 apiClient 抛出带 errorCode 的错误。
      const fail = (message: string, errorCode: string, details: unknown = null) => ({
        data: { success: false, message, errorCode, details, data: null },
      });
      // 与服务端一致：只能填报开放中的节日，且年份必须是开放窗口对应的年份。
      const openWindow = reviewSeasonalWindows(reviewSeasonalToday(current, storeCode)).find(
        (window) => window.cardType === cardType,
      );
      if (!openWindow?.isOpen || openWindow.seasonYear !== seasonYear) {
        return fail("Festival is not open for submission", "SEASONAL_CARD_WINDOW_CLOSED");
      }
      if (!supplierCode || !reviewSeasonalSupplierName(supplierCode)) {
        return fail("Supplier not found", "SUPPLIER_NOT_FOUND");
      }
      const itemGuids = new Set(items.map((item) => String(item.catalogGuid ?? "")));
      if (
        !catalogs.length ||
        itemGuids.size !== items.length ||
        itemGuids.size !== catalogs.length ||
        catalogs.some((item) => !itemGuids.has(String(item.catalogGuid)))
      ) {
        return fail("Batch items mismatch", "BATCH_ITEMS_MISMATCH");
      }

      const previous = reviewSeasonalLatestBatch(
        current.seasonalSubmissions,
        storeCode,
        seasonYear,
        cardType,
        supplierCode,
      );
      const expected = String(payload.expectedPreviousBatchGuid ?? "").trim();
      if ((previous?.batchGuid ?? "") !== expected) {
        return fail("Updated by someone else", "SEASONAL_CARD_STALE", previous);
      }

      const lines = catalogs.map((catalog) => {
        const item = items.find(
          (entry) => String(entry.catalogGuid) === catalog.catalogGuid,
        )!;
        const quantity = Math.max(0, Math.trunc(Number(item.remainingQuantity) || 0));
        const unitPrice = catalog.allowsCustomUnitPrice
          ? Number(item.customUnitPrice ?? 0) || 0
          : Number(catalog.fixedUnitPrice ?? 0);
        return { catalog, quantity, unitPrice };
      });
      if (
        lines.some(
          (line) =>
            line.catalog.allowsCustomUnitPrice && line.quantity > 0 && line.unitPrice <= 0,
        )
      ) {
        return fail("Custom price required", "CUSTOM_PRICE_REQUIRED");
      }
      if (
        previous &&
        lines.every((line) => {
          const before = previous.lines.find(
            (entry: JsonRecord) => entry.catalogGuid === line.catalog.catalogGuid,
          );
          return (
            before &&
            before.remainingQuantity === line.quantity &&
            Number(before.unitPrice) === line.unitPrice
          );
        })
      ) {
        return fail("No changes", "SEASONAL_CARD_NO_CHANGES");
      }

      const batchGuid = nextId(current, "review-seasonal-batch");
      const supplierName = reviewSeasonalSupplierName(supplierCode);
      const remark = typeof payload.remark === "string" ? payload.remark : null;
      const rows = lines.map((line) => ({
        submissionGuid: nextId(current, "review-seasonal-submission"),
        storeCode,
        catalogGuid: line.catalog.catalogGuid,
        cardType,
        cardTypeName: line.catalog.cardTypeName,
        priceOption: line.catalog.priceOption,
        seasonYear,
        unitPrice: line.unitPrice,
        priceLabel: line.catalog.priceLabel,
        remainingQuantity: line.quantity,
        remark,
        submittedByName: "App Review Demo",
        submittedAt: current.now,
        localSupplierCode: supplierCode,
        supplierName,
        batchGuid,
      }));
      current.seasonalSubmissions.unshift(...rows);
      mirrorCreate(current, dataStore, "seasonalCards", batchGuid, "Seasonal card batch");
      return {
        data: reviewSeasonalBatchDto(rows),
      };
    },
  );

  registerAttendanceRoutes(transport, dataStore, holder);
  registerUserRoutes(transport, dataStore, holder);
  registerEmployeeProfileRoutes(transport, dataStore, holder);
  registerDeviceRoutes(transport, dataStore, holder);
  registerReportRoutes(transport, holder);
}

// 审核模式供应商只有 /local-suppliers/active 里那一家。
const REVIEW_SEASONAL_SUPPLIERS: Record<string, string> = {
  "REV-SUP-001": "Demo Local Supplier",
};

function reviewSeasonalSupplierName(supplierCode: string) {
  return REVIEW_SEASONAL_SUPPLIERS[supplierCode] ?? "";
}

function reviewSeasonalToday(state: AppRouteState, storeCode: string) {
  return toStoreLocalDateOnly(state.now, storeCode) || state.now.slice(0, 10);
}

/**
 * 审核模式的开放窗口：按真实规则推算；当天恰好没有开放节日时，把下一个开放的节日提前到今天开放，
 * 保证审核人员任何时候都能走完填报流程（与生产口径不同，仅限离线演示数据）。
 */
function reviewSeasonalWindows(today: string) {
  const windows = computeSeasonalCardHolidayWindows(today);
  if (windows.some((window) => window.isOpen)) return windows;
  const next = findNextSeasonalCardOpening(windows);
  if (!next) return windows;
  const todayTime = Date.parse(`${today}T00:00:00Z`);
  const closesOn = new Date(
    todayTime + SEASONAL_CARD_OPEN_DAYS_AFTER_HOLIDAY * 24 * 60 * 60 * 1000,
  )
    .toISOString()
    .slice(0, 10);
  return windows.map((window) =>
    window.cardType === next.cardType
      ? { ...window, isOpen: true, opensOn: today, closesOn }
      : window,
  );
}

function reviewSeasonalBatchDto(rows: JsonRecord[]) {
  const first = rows[0];
  return {
    batchGuid: first.batchGuid,
    storeCode: first.storeCode,
    storeName: first.storeCode,
    seasonYear: first.seasonYear,
    cardType: first.cardType,
    cardTypeName: first.cardTypeName,
    localSupplierCode: first.localSupplierCode,
    supplierName: first.supplierName,
    remark: first.remark ?? null,
    submittedByName: first.submittedByName,
    submittedAt: first.submittedAt,
    totalQuantity: rows.reduce((sum, row) => sum + Number(row.remainingQuantity), 0),
    totalAmount: rows.reduce(
      (sum, row) => sum + Number(row.remainingQuantity) * Number(row.unitPrice),
      0,
    ),
    isCurrent: true,
    lines: rows
      .slice()
      .sort((left, right) => Number(left.priceOption) - Number(right.priceOption))
      .map((row) => ({
        submissionGuid: row.submissionGuid,
        catalogGuid: row.catalogGuid,
        priceOption: row.priceOption,
        priceLabel: row.priceLabel,
        unitPrice: row.unitPrice,
        remainingQuantity: row.remainingQuantity,
      })),
  };
}

/** 某 分店 + 年份 + 节日 + 供应商 的最新批次；只看带批次号的记录（历史单条记录没有供应商）。 */
function reviewSeasonalLatestBatch(
  submissions: JsonRecord[],
  storeCode: string,
  seasonYear: number,
  cardType: number,
  supplierCode: string,
) {
  const matching = submissions.filter(
    (row) =>
      row.batchGuid &&
      row.storeCode === storeCode &&
      Number(row.seasonYear) === seasonYear &&
      Number(row.cardType) === cardType &&
      row.localSupplierCode === supplierCode,
  );
  if (!matching.length) return null;
  // 新批次总是 unshift 到最前，第一行即最新批次。
  const latestGuid = matching[0].batchGuid;
  return reviewSeasonalBatchDto(matching.filter((row) => row.batchGuid === latestGuid));
}

function reviewPunchTimestamp(punch: JsonRecord | undefined) {
  return String(
    punch?.punchTimeUtc
      ?? punch?.punchTimeLocal
      ?? punch?.effectivePunchTime
      ?? "",
  );
}

function reviewPunchStoreLocalTimestamp(
  punch: JsonRecord | undefined,
  storeCode: unknown,
) {
  const punchTimeUtc = String(punch?.punchTimeUtc ?? "").trim();
  if (punchTimeUtc) return toStoreLocalDateTime(punchTimeUtc, storeCode);
  return String(punch?.punchTimeLocal ?? punch?.effectivePunchTime ?? "");
}

function reviewMinuteOfDay(value: unknown) {
  const match = /(?:T|^)(\d{2}):(\d{2})/.exec(String(value ?? ""));
  return match ? Number(match[1]) * 60 + Number(match[2]) : undefined;
}

function roundReviewOvertime(actualMinutes: number) {
  if (actualMinutes < 15) return 0;
  return Math.floor(actualMinutes / 15 + 0.5) * 15;
}

function toReviewPunchUtc(value: string) {
  const normalized = value.length === 16 ? `${value}:00` : value;
  const withOffset = /(?:Z|[+-]\d{2}:\d{2})$/i.test(normalized)
    ? normalized
    : `${normalized}+10:00`;
  const instant = new Date(withOffset);
  return Number.isFinite(instant.getTime()) ? instant.toISOString() : "";
}

function normalizeReviewUtcInstant(value: unknown) {
  const trimmed = String(value ?? "").trim();
  if (!trimmed) return "";
  const normalized = /(?:Z|[+-]\d{2}:\d{2})$/i.test(trimmed)
    ? trimmed
    : `${trimmed}Z`;
  return Number.isFinite(new Date(normalized).getTime()) ? normalized : "";
}

function buildReviewAttendanceSession(
  current: AppRouteState,
  schedule: JsonRecord,
  punches: JsonRecord[],
) {
  const normalized = normalizeAttendanceToday({
    workDate: schedule.workDate ?? current.today,
    schedules: [schedule],
    punches,
  }).scheduleSessions[0];
  if (!normalized) return undefined;

  const workedMinutes = normalized.segments.reduce(
    (sum, segment) => sum + (segment.workedMinutes ?? segment.durationMinutes ?? 0),
    0,
  );
  const firstClockIn = normalized.segments.find((segment) => segment.clockIn)?.clockIn;
  const finalClockOut = [...normalized.segments]
    .reverse()
    .find((segment) => segment.clockOut)?.clockOut;
  const scheduledStart = reviewMinuteOfDay(schedule.startTime);
  const scheduledEnd = reviewMinuteOfDay(schedule.endTime);
  const firstClockInMinute = reviewMinuteOfDay(
    reviewPunchStoreLocalTimestamp(firstClockIn as JsonRecord, schedule.storeCode),
  );
  const finalClockOutMinute = reviewMinuteOfDay(
    reviewPunchStoreLocalTimestamp(finalClockOut as JsonRecord, schedule.storeCode),
  );
  const earlyActual = scheduledStart !== undefined && firstClockInMinute !== undefined
    ? Math.max(0, scheduledStart - firstClockInMinute)
    : 0;
  const lateActual = scheduledEnd !== undefined && finalClockOutMinute !== undefined
    ? Math.max(0, finalClockOutMinute - scheduledEnd)
    : 0;
  const sequence = summarizeReviewPunchSequence(punches);
  const hasOpenSegment = sequence.hasOpenSegment;
  const completedSegmentCount = sequence.completedSegmentCount;
  const canFinalize = !hasOpenSegment
    && completedSegmentCount > 0
    && finalClockOutMinute !== undefined
    && scheduledEnd !== undefined
    && finalClockOutMinute >= scheduledEnd;

  return {
    ...normalized,
    segmentLimit: IOS_REVIEW_MANAGER_SEGMENT_LIMIT,
    completedSegmentCount,
    workedMinutes,
    hasOpenSegment,
    hasMissingClockOut: false,
    scheduleState: hasOpenSegment
      ? "Working"
      : canFinalize
        ? "Completed"
        : completedSegmentCount > 0
          ? "OnBreak"
          : "NotStarted",
    earlyOvertimeMinutes: roundReviewOvertime(earlyActual),
    lateOvertimeMinutes: roundReviewOvertime(lateActual),
    overtimeRawMinutes: earlyActual + lateActual,
    candidateOvertimeMinutes:
      roundReviewOvertime(earlyActual) + roundReviewOvertime(lateActual),
  };
}

function summarizeReviewPunchSequence(punches: JsonRecord[]) {
  const sorted = [...punches].sort((left, right) =>
    reviewPunchTimestamp(left).localeCompare(reviewPunchTimestamp(right)),
  );
  let hasOpenSegment = false;
  let completedSegmentCount = 0;
  let previousPunchType = "";
  const invalid = (errorCode: string) => ({
    errorCode,
    hasOpenSegment,
    completedSegmentCount,
  });
  for (const punch of sorted) {
    const punchType = String(punch.punchType ?? "");
    if (punchType === previousPunchType) {
      return invalid("PUNCH_SEQUENCE_CONSECUTIVE_TYPE");
    }
    if (punchType === "ClockIn") {
      if (hasOpenSegment) return invalid("PUNCH_SEQUENCE_CONSECUTIVE_TYPE");
      hasOpenSegment = true;
    } else if (punchType === "ClockOut") {
      if (!hasOpenSegment) return invalid("PUNCH_SEQUENCE_OUT_WITHOUT_IN");
      hasOpenSegment = false;
      completedSegmentCount += 1;
    } else {
      return invalid("PUNCH_TYPE_INVALID");
    }
    previousPunchType = punchType;
  }
  return {
    errorCode: undefined,
    hasOpenSegment,
    completedSegmentCount,
  };
}

function prepareReviewPunchAdjustment(
  current: AppRouteState,
  payload: JsonRecord,
): {
  preview: JsonRecord;
  schedule?: JsonRecord;
  originalPunch?: JsonRecord;
  originalPunchIndex?: number;
  proposedPunch?: JsonRecord;
} {
  const storeCode = String(payload.storeCode ?? "").trim();
  const scheduleGuid = String(payload.scheduleGuid ?? "").trim();
  const originalPunchGuid = String(payload.originalPunchGuid ?? "").trim();
  const punchType = String(payload.punchType ?? "").trim();
  const requestedPunchTimeLocal = String(payload.requestedPunchTimeLocal ?? "").trim();
  const requestedPunchTimeUtc = normalizeReviewUtcInstant(payload.requestedPunchTimeUtc);
  const requestedPunchTimeAtStore = requestedPunchTimeUtc
    ? toStoreLocalDateTime(requestedPunchTimeUtc, storeCode)
    : requestedPunchTimeLocal;
  const reason = String(payload.reason ?? "").trim();
  const workDate = requestedPunchTimeAtStore.slice(0, 10);
  const invalid = (validationErrorCode: string, validationMessage: string) => ({
    preview: {
      isValid: false,
      validationErrorCode,
      validationMessage,
      workedMinutesDelta: 0,
      candidateOvertimeMinutesDelta: 0,
      wouldAutoApprove: false,
    },
  });

  if (!storeCode) return invalid("STORE_REQUIRED", "Store code is required");
  if (!reason) return invalid("REASON_REQUIRED", "Reason is required");
  if (!/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}/.test(requestedPunchTimeAtStore)) {
    return invalid("PUNCH_TIME_INVALID", "Requested punch time is invalid");
  }
  if (punchType !== "ClockIn" && punchType !== "ClockOut") {
    return invalid("PUNCH_TYPE_INVALID", "Punch type is invalid");
  }

  const schedule = current.schedules.find((item) =>
    item.isMine
    && String(item.storeCode ?? "") === storeCode
    && String(item.workDate ?? current.today).slice(0, 10) === workDate
    && (!scheduleGuid || item.scheduleGuid === scheduleGuid),
  );
  if (!schedule) return invalid("SCHEDULE_NOT_FOUND", "No matching schedule");

  const schedulePunches = current.punches.filter((item) =>
    item.scheduleGuid === schedule.scheduleGuid
    && String(item.workDate ?? "").slice(0, 10) === workDate,
  );
  const originalPunchIndex = originalPunchGuid
    ? current.punches.findIndex((item) =>
        item.punchGuid === originalPunchGuid
        && item.scheduleGuid === schedule.scheduleGuid)
    : -1;
  if (originalPunchGuid && originalPunchIndex < 0) {
    return invalid("PUNCH_NOT_FOUND", "Original punch was not found");
  }
  const originalPunch = originalPunchIndex >= 0
    ? current.punches[originalPunchIndex]
    : undefined;
  if (originalPunch && originalPunch.punchType !== punchType) {
    return invalid("PUNCH_TYPE_MISMATCH", "Original punch type does not match");
  }

  const proposedPunch: JsonRecord = {
    ...(originalPunch ?? {}),
    punchGuid: originalPunch?.punchGuid ?? "review-preview-punch",
    scheduleGuid: schedule.scheduleGuid,
    storeCode,
    storeName: schedule.storeName ?? "Demo Brisbane",
    userGuid: "review-user",
    employeeName: "App Review Demo",
    workDate,
    punchType,
    punchTimeLocal: requestedPunchTimeAtStore,
    punchTimeUtc: requestedPunchTimeUtc || toReviewPunchUtc(requestedPunchTimeLocal),
    effectivePunchTime: requestedPunchTimeAtStore,
    status: "Normal",
  };
  const proposedPunches = originalPunch
    ? schedulePunches.map((item) => item.punchGuid === originalPunchGuid ? proposedPunch : item)
    : [...schedulePunches, proposedPunch];
  const sequence = summarizeReviewPunchSequence(proposedPunches);
  if (sequence.errorCode) {
    return invalid(sequence.errorCode, "Punch sequence is invalid");
  }
  const currentStoreDayClockIns = current.punches.filter((item) =>
    item.storeCode === storeCode
    && String(item.workDate ?? "").slice(0, 10) === workDate
    && item.punchType === "ClockIn",
  ).length;
  const proposedStoreDaySegmentCount = currentStoreDayClockIns
    - (originalPunch?.punchType === "ClockIn" ? 1 : 0)
    + (punchType === "ClockIn" ? 1 : 0);
  // Review 账号固定为店长身份；员工路径由真实后端执行 2 段上限，店长演示允许 3 段。
  if (proposedStoreDaySegmentCount > IOS_REVIEW_MANAGER_SEGMENT_LIMIT) {
    return invalid(
      "SEGMENT_LIMIT_REACHED",
      "Daily store segment limit exceeded",
    );
  }
  const existingSession = buildReviewAttendanceSession(current, schedule, schedulePunches);
  const proposedSession = buildReviewAttendanceSession(current, schedule, proposedPunches);
  const preview = {
    isValid: true,
    existingSession,
    proposedSession,
    workedMinutesDelta:
      (proposedSession?.workedMinutes ?? 0) - (existingSession?.workedMinutes ?? 0),
    candidateOvertimeMinutesDelta:
      (proposedSession?.candidateOvertimeMinutes ?? 0)
      - (existingSession?.candidateOvertimeMinutes ?? 0),
    wouldAutoApprove: true,
  };
  return {
    preview,
    schedule,
    originalPunch,
    originalPunchIndex,
    proposedPunch,
  };
}

function registerAttendanceRoutes(
  transport: ReviewTransport,
  dataStore: ReviewDataStore,
  holder: AppRouteStateHolder,
) {
  const state = () => holder.current;
  const attendanceState = () => {
    const current = state();
    let routeState = attendanceRouteStates.get(current);
    if (!routeState) {
      routeState = {
        authorizations: new Map(),
        punchResults: new Map(),
      };
      attendanceRouteStates.set(current, routeState);
    }
    return routeState;
  };
  register(transport, ["GET"], "/react/v1/attendance/my/today", ({ query }) => {
    const current = state();
    const storeCode = query.get("storeCode")?.trim() || undefined;
    const workDate = (
      query.get("workDate")?.trim()
      || toStoreLocalDateOnly(dataStore.getNow().toISOString(), storeCode ?? "REV001")
    ).slice(0, 10);
    const matchesStore = (item: JsonRecord) =>
      !storeCode || String(item.storeCode ?? "") === storeCode;
    const schedules = current.schedules.filter((item) =>
      item.isMine
      && matchesStore(item)
      && String(item.workDate ?? "").slice(0, 10) === workDate,
    );
    const punches = current.punches.filter(
      (item) => matchesStore(item)
        && String(item.workDate ?? "").slice(0, 10) === workDate,
    );
    const holidays = current.holidays.filter(
      (item) => matchesStore(item)
        && String(item.holidayDate ?? "").slice(0, 10) === workDate,
    );
    const sequence = summarizeReviewPunchSequence(punches);
    const canClockOut = !sequence.errorCode && sequence.hasOpenSegment;
    const canClockIn = !sequence.errorCode
      && !sequence.hasOpenSegment
      && sequence.completedSegmentCount < IOS_REVIEW_MANAGER_SEGMENT_LIMIT;
    return {
      data: {
        workDate,
        storeTimeZone: resolveReviewStoreTimeZone(storeCode ?? schedules[0]?.storeCode),
        schedules: clone(schedules),
        punches: clone(punches),
        holidays: clone(holidays),
        nextPunchType: sequence.hasOpenSegment ? "ClockOut" : "ClockIn",
        canClockIn,
        canClockOut,
        canRequestAdjustment: true,
      },
    };
  });
  register(
    transport,
    ["GET"],
    "/react/v1/attendance/my/punch-adjustments",
    () => ({ data: clone(state().punchAdjustments) }),
  );
  register(
    transport,
    ["POST"],
    "/react/v1/attendance/my/punch-adjustments/preview",
    ({ body }) => ({
      data: clone(prepareReviewPunchAdjustment(state(), asRecord(body)).preview),
    }),
  );
  register(
    transport,
    ["POST"],
    "/react/v1/attendance/my/punch-adjustments",
    ({ body }) => {
      const current = state();
      const payload = asRecord(body);
      const prepared = prepareReviewPunchAdjustment(current, payload);
      if (!prepared.preview.isValid || !prepared.schedule || !prepared.proposedPunch) {
        throw new Error(
          `IOS_REVIEW_ATTENDANCE_ADJUSTMENT_INVALID: ${prepared.preview.validationErrorCode ?? "UNKNOWN"}`,
        );
      }

      const adjustmentGuid = nextId(current, "review-punch-adjustment");
      const appliedPunchGuid = nextId(current, "review-punch");
      const appliedPunch = {
        ...prepared.proposedPunch,
        punchGuid: appliedPunchGuid,
        supersedesPunchGuid: prepared.originalPunch?.punchGuid,
        adjustmentGuid,
      };
      if (prepared.originalPunchIndex !== undefined && prepared.originalPunchIndex >= 0) {
        current.punches.splice(prepared.originalPunchIndex, 1, appliedPunch);
      } else {
        current.punches.push(appliedPunch);
      }

      const adjustment = {
        adjustmentGuid,
        storeCode: String(payload.storeCode ?? "").trim(),
        userGuid: "review-user",
        scheduleGuid: prepared.schedule.scheduleGuid,
        originalPunchGuid: prepared.originalPunch?.punchGuid,
        punchType: prepared.proposedPunch.punchType,
        requestedPunchTimeLocal: prepared.proposedPunch.punchTimeLocal,
        requestedPunchTimeUtc: prepared.proposedPunch.punchTimeUtc,
        reason: String(payload.reason ?? "").trim(),
        status: "Applied",
        appliedPunchGuid,
        isManagerSelfDirect: true,
        requestedByUserGuid: "review-user",
        reviewedByUserGuid: "review-user",
        submittedAt: current.now,
        reviewedAt: current.now,
        createdAt: current.now,
      };
      current.punchAdjustments.unshift(adjustment);
      mirrorCreate(
        current,
        dataStore,
        "attendance",
        adjustmentGuid,
        "Punch adjustment",
        "applied",
      );
      return { data: clone(adjustment), status: 201 };
    },
  );
  register(transport, ["GET"], "/react/v1/attendance/my/week", ({ query }) => {
    const current = state();
    const weekStart = query.get("weekStartDate") ?? current.today;
    return {
      data: {
        weekStart,
        weekEnd: weekStart,
        days: [
          {
            workDate: current.today,
            dayOfWeek: new Date(`${current.today}T00:00:00Z`).getUTCDay(),
            schedules: clone(current.schedules.filter((item) => item.isMine)),
          },
        ],
      },
    };
  });
  register(transport, ["GET"], "/react/v1/attendance/my/availability", () => ({
    data: clone(state().availability),
  }));
  register(
    transport,
    ["POST"],
    "/react/v1/attendance/my/availability",
    ({ body }) => {
      const current = state();
      const payload = asRecord(body);
      const segments = Array.isArray(payload.segments)
        ? payload.segments
        : [payload];
      const created = segments.map((segment: JsonRecord) => ({
        ...segment,
        availabilityGuid: nextId(current, "review-availability"),
        storeCode: payload.storeCode ?? "REV001",
        workDate: segment.availableDate ?? segment.workDate ?? current.today,
        note: segment.remark ?? segment.note,
        status: "Submitted",
      }));
      current.availability.push(...created);
      created.forEach((item) =>
        mirrorCreate(
          current,
          dataStore,
          "attendance",
          item.availabilityGuid,
          "Availability",
        ),
      );
      return { data: clone(created), status: 201 };
    },
  );
  register(
    transport,
    ["PUT"],
    /^\/react\/v1\/attendance\/my\/availability\/([^/]+)$/i,
    ({ match, body }) => {
      const current = state();
      const id = decodeURIComponent(match?.[1] ?? "");
      const item = current.availability.find(
        (candidate) => candidate.availabilityGuid === id,
      );
      if (!item) throw new Error(`IOS_REVIEW_AVAILABILITY_NOT_FOUND: ${id}`);
      Object.assign(item, asRecord(body), {
        workDate:
          asRecord(body).availableDate ??
          asRecord(body).workDate ??
          item.workDate,
        note: asRecord(body).remark ?? asRecord(body).note ?? item.note,
      });
      mirrorUpdate(current, dataStore, "attendance", id, "Availability");
      return { data: clone(item) };
    },
  );
  register(
    transport,
    ["POST"],
    /^\/react\/v1\/attendance\/my\/availability\/([^/]+)\/cancel$/i,
    ({ match }) => {
      const id = decodeURIComponent(match?.[1] ?? "");
      const item = state().availability.find(
        (candidate) => candidate.availabilityGuid === id,
      );
      if (item) item.status = "Cancelled";
      mirrorUpdate(
        state(),
        dataStore,
        "attendance",
        id,
        "Availability",
        "cancelled",
      );
      return { data: { success: true } };
    },
  );
  register(
    transport,
    ["POST"],
    "/react/v1/attendance/qr/resolve",
    ({ body }) => {
      const current = state();
      const qrToken = String(asRecord(body).qrToken ?? "").trim();
      if (!qrToken) {
        throw Object.assign(new Error("QR_REQUIRED"), { code: "QR_REQUIRED" });
      }

      const storeCode = "REV001";
      const store = IOS_REVIEW_STORES.find((item) => item.storeCode === storeCode);
      const serverNow = dataStore.getNow();
      if (!store || !Number.isFinite(serverNow.getTime())) {
        throw Object.assign(new Error("IOS_REVIEW_ATTENDANCE_QR_INVALID"), {
          code: "IOS_REVIEW_ATTENDANCE_QR_INVALID",
        });
      }

      const expiresAtUtc = new Date(
        serverNow.getTime() + IOS_REVIEW_ATTENDANCE_QR_LIFETIME_MS,
      ).toISOString();
      const punchAuthorizationExpiresAtUtc = new Date(
        serverNow.getTime() + IOS_REVIEW_PUNCH_AUTHORIZATION_LIFETIME_MS,
      ).toISOString();
      const punchAuthorizationToken = nextId(
        current,
        "review-punch-authorization",
      );
      // 关键逻辑：Review mock 与生产一致，由 resolve 建立原二维码、门店和短时授权的服务端关联。
      attendanceState().authorizations.set(punchAuthorizationToken, {
        qrToken,
        storeCode,
        storeName: store.storeName,
        deviceCode: IOS_REVIEW_ATTENDANCE_QR_DEVICE_CODE,
        issuedAtUtc: serverNow.toISOString(),
        expiresAtUtc: punchAuthorizationExpiresAtUtc,
      });

      return {
        data: {
          storeCode,
          storeName: store.storeName,
          deviceCode: IOS_REVIEW_ATTENDANCE_QR_DEVICE_CODE,
          expiresAtUtc,
          punchAuthorizationToken,
          punchAuthorizationExpiresAtUtc,
        },
      };
    },
  );
  register(transport, ["POST"], "/react/v1/attendance/punch", ({ body }) => {
    const current = state();
    const payload = asRecord(body);
    const qrToken = String(payload.qrToken ?? "").trim();
    const punchAuthorizationToken = String(
      payload.punchAuthorizationToken ?? "",
    ).trim();
    const routeState = attendanceState();
    const existingPunch = routeState.punchResults.get(qrToken);
    if (existingPunch) {
      return { data: clone(existingPunch) };
    }

    const authorization = routeState.authorizations.get(
      punchAuthorizationToken,
    );
    if (!authorization || authorization.qrToken !== qrToken) {
      throw Object.assign(new Error("ATTENDANCE_PUNCH_AUTHORIZATION_INVALID"), {
        code: "ATTENDANCE_PUNCH_AUTHORIZATION_INVALID",
      });
    }

    const serverNow = dataStore.getNow();
    const authorizationIssuedAt = new Date(authorization.issuedAtUtc);
    const authorizationExpiresAt = new Date(authorization.expiresAtUtc);
    if (
      !Number.isFinite(serverNow.getTime())
      || !Number.isFinite(authorizationIssuedAt.getTime())
      || !Number.isFinite(authorizationExpiresAt.getTime())
      || serverNow.getTime() < authorizationIssuedAt.getTime()
    ) {
      throw Object.assign(new Error("ATTENDANCE_PUNCH_AUTHORIZATION_INVALID"), {
        code: "ATTENDANCE_PUNCH_AUTHORIZATION_INVALID",
      });
    }
    if (serverNow.getTime() >= authorizationExpiresAt.getTime()) {
      throw Object.assign(new Error("ATTENDANCE_PUNCH_AUTHORIZATION_EXPIRED"), {
        code: "ATTENDANCE_PUNCH_AUTHORIZATION_EXPIRED",
      });
    }

    const storeCode = authorization.storeCode;
    const workDate = toStoreLocalDateOnly(serverNow.toISOString(), storeCode);
    const storePunches = current.punches.filter((item) =>
      item.storeCode === storeCode
      && String(item.workDate ?? "").slice(0, 10) === workDate,
    );
    const sequence = summarizeReviewPunchSequence(storePunches);
    if (sequence.errorCode) {
      throw Object.assign(new Error(sequence.errorCode), { code: sequence.errorCode });
    }
    if (
      !sequence.hasOpenSegment
      && sequence.completedSegmentCount >= IOS_REVIEW_MANAGER_SEGMENT_LIMIT
    ) {
      throw Object.assign(new Error("SEGMENT_LIMIT_REACHED"), {
        code: "SEGMENT_LIMIT_REACHED",
      });
    }

    const schedule = current.schedules.find((item) =>
      item.isMine
      && item.storeCode === storeCode
      && item.workDate === workDate,
    );
    const punchType = sequence.hasOpenSegment ? "ClockOut" : "ClockIn";
    const serverTimeUtc = serverNow.toISOString();
    const punch: JsonRecord = {
      punchGuid: nextId(current, "review-punch"),
      scheduleGuid: schedule?.scheduleGuid,
      storeCode,
      storeName: authorization.storeName,
      userGuid: "review-user",
      employeeName: "App Review Demo",
      workDate,
      storeTimeZone: resolveReviewStoreTimeZone(storeCode),
      punchType,
      punchTimeUtc: serverTimeUtc,
      punchTimeLocal: toStoreLocalDateTime(
        serverTimeUtc,
        storeCode,
      ),
      // 关键逻辑：UTC instant 是服务端权威时间；本地时间仅供门店显示和业务日派生。
      serverTimeUtc,
      status: "Normal",
      source: "Qr",
      posDeviceCode: authorization.deviceCode,
      locationLatitude: payload.locationLatitude ?? -27.4698,
      locationLongitude: payload.locationLongitude ?? 153.0251,
      locationAccuracy: payload.locationAccuracy ?? 5,
      locationCapturedAtUtc: payload.locationCapturedAtUtc,
    };
    current.punches.push(punch);
    // 关键逻辑：原二维码仅作为私有幂等键；响应和 Today DTO 不回显二维码或短时授权。
    routeState.punchResults.set(qrToken, clone(punch));
    mirrorCreate(
      current,
      dataStore,
      "attendance",
      punch.punchGuid,
      `Punch ${punch.punchType}`,
    );
    return { data: punch, status: 201 };
  });
  register(
    transport,
    ["POST"],
    "/react/v1/attendance/location-samples",
    () => ({
      data: { success: true, simulated: true },
    }),
  );
  register(
    transport,
    ["GET"],
    "/react/v1/attendance/my/leave-requests",
    () => ({
      data: clone(state().leaveRequests),
    }),
  );
  register(
    transport,
    ["POST"],
    /^\/react\/v1\/attendance\/(?:my|managed)\/leave-requests$/i,
    ({ body }) => {
      const current = state();
      const leave: JsonRecord = {
        ...asRecord(body),
        leaveGuid: nextId(current, "review-leave"),
        status: "Pending",
        submittedAt: current.now,
      };
      current.leaveRequests.unshift(leave);
      const approval = {
        approvalGuid: nextId(current, "review-approval"),
        sourceGuid: leave.leaveGuid,
        sourceType: "Leave",
        employeeName: asRecord(body).userGuid
          ? "Demo Staff"
          : "App Review Demo",
        storeCode: leave.storeCode ?? "REV001",
        storeName: "Demo Brisbane",
        workDate: leave.startDate,
        title: `${leave.leaveType ?? "Leave"} request`,
        detail: leave.reason,
        status: "Pending",
        submittedAt: current.now,
      };
      current.approvals.unshift(approval);
      mirrorCreate(
        current,
        dataStore,
        "attendance",
        leave.leaveGuid,
        "Leave request",
        "pending",
      );
      return { data: clone(leave), status: 201 };
    },
  );
  register(
    transport,
    ["POST"],
    /^\/react\/v1\/attendance\/my\/leave-requests\/([^/]+)\/cancel$/i,
    ({ match }) => {
      const id = decodeURIComponent(match?.[1] ?? "");
      const leave = state().leaveRequests.find((item) => item.leaveGuid === id);
      if (leave) leave.status = "Cancelled";
      mirrorUpdate(
        state(),
        dataStore,
        "attendance",
        id,
        "Leave request",
        "cancelled",
      );
      return { data: { success: true } };
    },
  );
  register(
    transport,
    ["POST"],
    "/react/v1/attendance/leave-attachments/upload-signature",
    () => ({
      data: {
        url: "data:application/octet-stream;base64,UkVWSUVX",
        objectKey: "ios-review/attendance/leave-attachment.txt",
        headers: {},
      },
    }),
  );
  register(
    transport,
    ["GET"],
    "/react/v1/attendance/approvals/pending",
    () => ({
      data: clone(
        state().approvals.filter((item) => item.status === "Pending"),
      ),
    }),
  );
  register(
    transport,
    ["POST"],
    /^\/react\/v1\/attendance\/approvals\/([^/]+)\/(approve|reject)$/i,
    ({ match }) => {
      const id = decodeURIComponent(match?.[1] ?? "");
      const decision = match?.[2] === "approve" ? "Approved" : "Rejected";
      const approval = state().approvals.find(
        (item) => item.approvalGuid === id,
      );
      if (approval) {
        approval.status = decision;
        const leave = state().leaveRequests.find(
          (item) => item.leaveGuid === approval.sourceGuid,
        );
        if (leave) leave.status = decision;
      }
      mirrorUpdate(
        state(),
        dataStore,
        "attendance",
        id,
        "Attendance approval",
        decision.toLowerCase(),
      );
      return { data: { success: true } };
    },
  );
  register(transport, ["GET"], "/react/v1/attendance/schedules/week", () => ({
    data: clone(state().schedules),
  }));
  register(
    transport,
    ["POST"],
    "/react/v1/attendance/schedules",
    ({ body }) => {
      const current = state();
      const schedule = {
        ...asRecord(body),
        scheduleGuid: nextId(current, "review-schedule"),
        storeName: "Demo Brisbane",
        employeeName: "App Review Demo",
        status: asRecord(body).status ?? "Draft",
        isMine: asRecord(body).userGuid === "review-user",
      };
      current.schedules.push(schedule);
      mirrorCreate(
        current,
        dataStore,
        "attendance",
        schedule.scheduleGuid,
        "Attendance schedule",
        schedule.status,
      );
      return { data: clone(schedule), status: 201 };
    },
  );
  register(
    transport,
    ["PUT", "DELETE"],
    /^\/react\/v1\/attendance\/schedules\/([^/]+)$/i,
    ({ method, match, body }) => {
      const current = state();
      const id = decodeURIComponent(match?.[1] ?? "");
      const index = current.schedules.findIndex(
        (item) => item.scheduleGuid === id,
      );
      if (index < 0) throw new Error(`IOS_REVIEW_SCHEDULE_NOT_FOUND: ${id}`);
      if (method === "DELETE") {
        current.schedules.splice(index, 1);
        mirrorRemove(current, dataStore, "attendance", id);
        return { data: { success: true } };
      }
      current.schedules[index] = {
        ...current.schedules[index],
        ...asRecord(body),
        scheduleGuid: id,
      };
      mirrorUpdate(
        current,
        dataStore,
        "attendance",
        id,
        "Attendance schedule",
        current.schedules[index].status,
      );
      return { data: clone(current.schedules[index]) };
    },
  );
  register(
    transport,
    ["POST"],
    "/react/v1/attendance/schedules/publish-week",
    () => ({
      data: { success: true, publishedCount: state().schedules.length },
    }),
  );
  register(transport, ["GET"], "/react/v1/attendance/holidays", () => ({
    data: clone(state().holidays),
  }));
  register(
    transport,
    ["POST"],
    "/react/v1/attendance/holidays/sync",
    ({ body }) => ({
      data: {
        ...asRecord(body),
        syncedCount: state().holidays.length,
        createdCount: 0,
        updatedCount: state().holidays.length,
        skippedCount: 0,
        holidays: clone(state().holidays),
        syncedAt: state().now,
      },
    }),
  );
  register(transport, ["POST"], "/react/v1/attendance/holidays", ({ body }) => {
    const current = state();
    const holiday: JsonRecord = {
      ...asRecord(body),
      holidayGuid: nextId(current, "review-holiday"),
    };
    current.holidays.push(holiday);
    mirrorCreate(
      current,
      dataStore,
      "attendance",
      holiday.holidayGuid,
      String(holiday.holidayName ?? "Holiday"),
    );
    return { data: clone(holiday), status: 201 };
  });
  register(
    transport,
    ["PUT", "DELETE"],
    /^\/react\/v1\/attendance\/holidays\/([^/]+)$/i,
    ({ method, match, body }) => {
      const current = state();
      const id = decodeURIComponent(match?.[1] ?? "");
      const index = current.holidays.findIndex(
        (item) => item.holidayGuid === id,
      );
      if (index < 0) throw new Error(`IOS_REVIEW_HOLIDAY_NOT_FOUND: ${id}`);
      if (method === "DELETE") {
        current.holidays.splice(index, 1);
        mirrorRemove(current, dataStore, "attendance", id);
        return { data: { success: true } };
      }
      current.holidays[index] = {
        ...current.holidays[index],
        ...asRecord(body),
        holidayGuid: id,
      };
      mirrorUpdate(
        current,
        dataStore,
        "attendance",
        id,
        String(current.holidays[index].holidayName ?? "Holiday"),
      );
      return { data: clone(current.holidays[index]) };
    },
  );
  register(
    transport,
    ["GET"],
    /^\/react\/v1\/attendance\/employees\/[^/]+\/week$/i,
    () => ({
      data: {
        weekStart: state().today,
        weekEnd: state().today,
        days: [
          {
            workDate: state().today,
            dayOfWeek: 1,
            schedules: clone(state().schedules),
          },
        ],
      },
    }),
  );
  register(
    transport,
    ["GET"],
    /^\/react\/v1\/attendance\/employees\/[^/]+\/records$/i,
    () => ({
      data: {
        schedules: clone(state().schedules),
        availability: clone(state().availability),
        leaveRequests: clone(state().leaveRequests),
      },
    }),
  );
}

function registerUserRoutes(
  transport: ReviewTransport,
  dataStore: ReviewDataStore,
  holder: AppRouteStateHolder,
) {
  const state = () => holder.current;
  // 演示码和打印记录只保存在本地审核状态中，按员工隔离并保持确认幂等。
  register(transport, ["GET", "POST"], /^\/react\/v1\/store-users\/([^/]+)\/cashier-barcode(?:\/(ensure|print-confirmation))?$/i,
    ({ method, match, body, query }) => {
      const id = decodeURIComponent(match?.[1] ?? "");
      const action = match?.[2];
      const user = findByAnyId(state().users, id);
      const payload = asRecord(body);
      const storeCode = String(payload.storeCode ?? query.get("storeCode") ?? "");
      if (!user || !user.isActive || String(user.storeCode) !== storeCode) throw new Error("IOS_REVIEW_STAFF_CODE_SCOPE_INVALID");
      if ((method === "GET" && action) || (method === "POST" && !action)) throw new Error("IOS_REVIEW_STAFF_CODE_METHOD_INVALID");
      if (action === "ensure" && !user.reviewCashierBarcode) {
        const base = `95288${String(state().users.indexOf(user) + 1).padStart(7, "0")}`;
        const sum = [...base].reduce((total, digit, index) => total + Number(digit) * (index % 2 ? 3 : 1), 0);
        user.reviewCashierBarcode = { exists: true, barcode: `${base}${(10 - sum % 10) % 10}`, format: "EAN13", printCount: 0, createdAt: state().now, updatedAt: state().now };
        user.reviewCashierAttempts = [];
      }
      const barcode = user.reviewCashierBarcode as JsonRecord | undefined;
      if (action === "print-confirmation") {
        if (!barcode || payload.barcode !== barcode.barcode || !payload.printAttemptId) throw new Error("IOS_REVIEW_STAFF_CODE_CONFIRM_INVALID");
        const attempts = user.reviewCashierAttempts as string[];
        if (!attempts.includes(String(payload.printAttemptId))) {
          attempts.push(String(payload.printAttemptId));
          barcode.printCount += 1;
        }
      }
      return { data: clone(barcode ?? { exists: false, barcode: null, format: "EAN13", printCount: 0 }) };
    });
  register(transport, ["POST"], "/react/v1/store-users/grid", ({ body }) => {
    const payload = asRecord(body);
    const storeCode = String(payload.storeCode ?? "").trim();
    const keyword = String(payload.keyword ?? "").trim().toLowerCase();
    return { data: page(state().users.filter((user) =>
      (!storeCode || user.storeCode === storeCode)
      && (!keyword || [user.fullName, user.username, user.phone].some((value) => String(value ?? "").toLowerCase().includes(keyword))))) };
  });
  register(transport, ["POST"], "/react/v1/store-users", ({ body }) => {
    const current = state();
    const payload = asRecord(body);
    const username = String(payload.username ?? "").trim();
    const password = String(payload.password ?? "");
    const storeCode = String(payload.storeCode ?? "").trim();
    const store = IOS_REVIEW_STORES.find((item) => item.storeCode === storeCode);

    if (!username) {
      throw new Error("IOS_REVIEW_USER_USERNAME_REQUIRED");
    }
    // 新版用邮件验证码设置密码，不提交密码；旧方式仍校验初始密码长度。
    const sendSetupEmail = payload.sendPasswordSetupEmail === true;
    const email = String(payload.email ?? "").trim();
    if (sendSetupEmail ? !email.includes("@") : password.trim().length < 6 || password.length > 100) {
      throw new Error(sendSetupEmail ? "IOS_REVIEW_USER_EMAIL_REQUIRED" : "IOS_REVIEW_USER_PASSWORD_INVALID");
    }
    if (!store) {
      throw new Error(`IOS_REVIEW_STORE_NOT_FOUND: ${storeCode}`);
    }
    if (current.users.some((item) => item.username === username)) {
      throw new Error(`IOS_REVIEW_USER_ALREADY_EXISTS: ${username}`);
    }

    const id = nextId(current, "review-staff");
    const status = Number(payload.status) === 0 ? 0 : 1;
    const user: JsonRecord = {
      userGuid: id,
      userGUID: id,
      username,
      fullName: String(payload.fullName ?? "").trim() || username,
      email: String(payload.email ?? "").trim() || undefined,
      phone: String(payload.phone ?? "").trim() || undefined,
      storeCode,
      storeName: store.storeName,
      roleNames: ["StoreStaff"],
      accessStoreAssignments: [
        {
          storeGUID: store.storeGUID,
          isPrimary: false,
        },
      ],
      accessRoleGuids: ["review-role-store-staff"],
      directPermissionCodes: [],
      status,
      isActive: status === 1,
      employmentType: "casual",
      createdAt: current.now,
      updatedAt: current.now,
    };
    current.users.push(user);
    mirrorCreate(
      current,
      dataStore,
      "users",
      id,
      String(user.fullName ?? username),
    );
    // 审核模式不真发邮件，只回传与正式接口同形的发送结果。
    const created = sendSetupEmail
      ? { ...clone(user), passwordSetupEmail: { maskedEmail: maskReviewEmail(email), expiresAtUtc: current.now } }
      : clone(user);
    return { data: created, status: 201 };
  });
  register(
    transport,
    ["POST"],
    /^\/react\/v1\/store-users\/([^/]+)\/password-setup-email$/i,
    ({ match, body }) => {
      const id = decodeURIComponent(match?.[1] ?? "");
      const user = findByAnyId(state().users, id);
      if (!user) throw new Error(`IOS_REVIEW_USER_NOT_FOUND: ${id}`);
      // 老账号补邮箱：与正式接口一致，先写入店长填写的邮箱再「发送」。
      const suppliedEmail = String(asRecord(body).email ?? "").trim().toLowerCase();
      if (suppliedEmail) {
        Object.assign(user, { email: suppliedEmail, updatedAt: state().now });
      }
      const email = String(user.email ?? "");
      if (!email.includes("@") || email.endsWith(".store.local")) throw new Error("IOS_REVIEW_USER_EMAIL_REQUIRED");
      return { data: { maskedEmail: maskReviewEmail(email), expiresAtUtc: state().now } };
    },
  );
  register(
    transport,
    ["GET", "PUT"],
    /^\/react\/v1\/store-users\/([^/]+)$/i,
    ({ method, match, body }) => {
      const id = decodeURIComponent(match?.[1] ?? "");
      const user = findByAnyId(state().users, id);
      if (!user) throw new Error(`IOS_REVIEW_USER_NOT_FOUND: ${id}`);
      if (method === "PUT") {
        Object.assign(user, asRecord(body), {
          userGuid: id,
          updatedAt: state().now,
        });
        mirrorUpdate(
          state(),
          dataStore,
          "users",
          id,
          String(user.fullName ?? user.username ?? id),
        );
      }
      return { data: clone(user) };
    },
  );
  register(
    transport,
    ["GET"],
    /^\/react\/v1\/store-users\/([^/]+)\/profile$/i,
    ({ match }) => {
      const id = decodeURIComponent(match?.[1] ?? "");
      const user = findByAnyId(state().users, id);
      if (!user) throw new Error(`IOS_REVIEW_USER_NOT_FOUND: ${id}`);
      return {
        data: clone({ ...user, address: "123 Demo Street, Brisbane QLD 4000" }),
      };
    },
  );
  register(
    transport,
    ["PUT"],
    /^\/react\/v1\/store-users\/([^/]+)\/(status|password)$/i,
    ({ match, body }) => {
      const id = decodeURIComponent(match?.[1] ?? "");
      const action = match?.[2];
      const user = findByAnyId(state().users, id);
      if (!user) throw new Error(`IOS_REVIEW_USER_NOT_FOUND: ${id}`);
      if (action === "status") {
        user.status = asRecord(body).status ?? user.status;
        user.isActive = String(user.status).toLowerCase() === "active";
      }
      mirrorUpdate(
        state(),
        dataStore,
        "users",
        id,
        String(user.fullName ?? user.username ?? id),
        action === "password" ? "password-reset" : String(user.status),
      );
      return { data: { success: true } };
    },
  );
  register(
    transport,
    ["GET", "POST"],
    /^\/Users\/guid\/([^/]+)\/roles$/i,
    ({ method, match, body }) => {
      const id = decodeURIComponent(match?.[1] ?? "");
      const user = findByAnyId(state().users, id);
      if (!user) throw new Error(`IOS_REVIEW_USER_NOT_FOUND: ${id}`);
      const catalog = [
        {
          roleGUID: "review-role-store-staff",
          roleName: "StoreStaff",
          description: "Standard review store account",
          isActive: true,
        },
        {
          roleGUID: "review-role-store-manager",
          roleName: "StoreManager",
          description: "Derived from managed stores",
          isActive: true,
        },
      ];
      if (method === "POST") {
        const payload = asRecord(body);
        user.accessRoleGuids = Array.isArray(payload.RoleGuids)
          ? [...payload.RoleGuids]
          : [];
        mirrorUpdate(state(), dataStore, "users", id, "User role access");
        return { data: true };
      }
      const roleGuids = Array.isArray(user.accessRoleGuids)
        ? user.accessRoleGuids
        : [];
      return {
        data: catalog.filter((role) => roleGuids.includes(role.roleGUID)),
      };
    },
  );
  register(
    transport,
    ["GET"],
    /^\/Users\/guid\/([^/]+)\/permissions\/state$/i,
    ({ match }) => {
      const id = decodeURIComponent(match?.[1] ?? "");
      const user = findByAnyId(state().users, id);
      if (!user) throw new Error(`IOS_REVIEW_USER_NOT_FOUND: ${id}`);
      const directPermissionCodes = Array.isArray(user.directPermissionCodes)
        ? [...user.directPermissionCodes]
        : [];
      const inheritedPermissionCodes = ["Users.View"];
      return {
        data: {
          userGuid: id,
          isSuperAdmin: false,
          implicitAllPermissions: false,
          inheritedPermissionCodes,
          directPermissionCodes,
          effectivePermissionCodes: Array.from(
            new Set([...inheritedPermissionCodes, ...directPermissionCodes]),
          ),
          inheritedSources: [
            {
              roleName: "StoreStaff",
              permissionCodes: inheritedPermissionCodes,
            },
          ],
        },
      };
    },
  );
  register(
    transport,
    ["POST"],
    /^\/Users\/guid\/([^/]+)\/permissions$/i,
    ({ match, body }) => {
      const id = decodeURIComponent(match?.[1] ?? "");
      const user = findByAnyId(state().users, id);
      if (!user) throw new Error(`IOS_REVIEW_USER_NOT_FOUND: ${id}`);
      const payload = asRecord(body);
      const permissions = payload.permissions ?? payload.Permissions;
      user.directPermissionCodes = Array.isArray(permissions)
        ? [...permissions]
        : [];
      mirrorUpdate(state(), dataStore, "users", id, "User direct access");
      return { data: true };
    },
  );
  register(
    transport,
    ["GET", "PUT", "DELETE"],
    /^\/Users\/guid\/[^/]+\/stores\/[^/]+\/pos-terminal-permissions$/i,
    ({ method, body }) => {
      const current = state();
      if (method === "PUT") {
        current.posPermissionCodes = Array.isArray(
          asRecord(body).grantedPermissionCodes,
        )
          ? [...asRecord(body).grantedPermissionCodes]
          : [];
        mirrorUpdate(
          current,
          dataStore,
          "users",
          "pos-permissions",
          "POS permissions",
        );
      } else if (method === "DELETE") {
        current.posPermissionCodes = [...IOS_REVIEW_PERMISSION_CODES];
        mirrorRemove(current, dataStore, "users", "pos-permissions");
      }
      const assignablePermissions = IOS_REVIEW_PERMISSION_CODES.map((code) => ({
        code,
        name: code,
        group: code.split(".")[0] ?? "General",
        description: `Demo permission ${code}`,
      }));
      return {
        data: {
          mode: method === "DELETE" ? "Inherited" : "Override",
          assignablePermissions,
          inheritedPermissionCodes: [...IOS_REVIEW_PERMISSION_CODES],
          overriddenPermissionCodes: clone(current.posPermissionCodes),
          grantedPermissionCodes: clone(current.posPermissionCodes),
          effectivePermissionCodes: clone(current.posPermissionCodes),
        },
      };
    },
  );
}

function registerEmployeeProfileRoutes(
  transport: ReviewTransport,
  dataStore: ReviewDataStore,
  holder: AppRouteStateHolder,
) {
  const state = () => holder.current;
  register(transport, ["GET", "PUT"], "/EmployeeProfiles/me/sensitive-change-request", ({ method, body }) => {
    const profile = state().employeeProfile;
    if (method === "PUT") {
      const payload = asRecord(body);
      if (Number(payload.expectedSensitiveRevision) !== Number(profile.sensitiveRevision)) {
        throw new Error("EMPLOYEE_PROFILE_SENSITIVE_VERSION_CONFLICT");
      }
      const fields = ["bankBsb", "bankAccountNumber", "superannuationCompanyName", "superannuationCompanyCode", "superannuationAccountNumber", "identityType", "identityId"];
      profile.reviewSensitiveRequest = {
        ...Object.fromEntries(fields.map((key) => [key, payload[key] ?? ""])),
        requestId: 1,
        status: "Pending",
        hasIdentityPhoto: Boolean(profile.identityPhotoUrl),
        identityPhotoUrl: profile.identityPhotoUrl,
        identityPhotoUrlExpiresAt: profile.identityPhotoUrlExpiresAt,
        baseSensitiveRevision: profile.sensitiveRevision,
        submittedAt: state().now,
        submittedBy: "ios_app_review",
        changedFields: fields.filter((key) => String(payload[key] ?? "") !== String(profile[key] ?? "")),
      };
    }
    return { data: clone(profile.reviewSensitiveRequest ?? null) };
  });
  register(
    transport,
    ["GET", "PUT"],
    "/EmployeeProfiles/me",
    ({ method, body }) => {
      if (method === "PUT") {
        Object.assign(state().employeeProfile, asRecord(body), {
          updatedAt: state().now,
        });
        mirrorUpdate(
          state(),
          dataStore,
          "users",
          "employee-profile",
          "Employee profile",
        );
      }
      return { data: clone(state().employeeProfile) };
    },
  );
  register(
    transport,
    ["POST"],
    "/EmployeeProfiles/me/image-upload-signature",
    ({ body }) => ({
      data: {
        url: "data:application/octet-stream;base64,UkVWSUVX",
        objectKey: `ios-review/profile/${String(asRecord(body).kind ?? "avatar")}.svg`,
        headers: {},
      },
    }),
  );
  register(
    transport,
    ["POST"],
    "/EmployeeProfiles/me/images/complete",
    ({ body }) => {
      const kind = String(asRecord(body).kind ?? "avatar");
      const key = kind === "identity" ? "identityPhotoUrl" : "avatarUrl";
      state().employeeProfile[key] = "data:image/svg+xml,%3Csvg%3E%3C/svg%3E";
      state().employeeProfile.updatedAt = state().now;
      mirrorUpdate(
        state(),
        dataStore,
        "users",
        "employee-profile",
        "Employee profile image",
      );
      return { data: clone(state().employeeProfile) };
    },
  );
  register(
    transport,
    ["DELETE"],
    /^\/EmployeeProfiles\/me\/images\/(identity|avatar)$/i,
    ({ match }) => {
      const key = match?.[1] === "identity" ? "identityPhotoUrl" : "avatarUrl";
      state().employeeProfile[key] = "";
      state().employeeProfile.updatedAt = state().now;
      mirrorUpdate(
        state(),
        dataStore,
        "users",
        "employee-profile",
        "Employee profile image",
        "removed",
      );
      return { data: clone(state().employeeProfile) };
    },
  );
  register(transport, ["GET"], "/EmployeeProfiles/me/cashier-barcode", () => ({
    data: clone(state().cashierBarcode),
  }));
  register(
    transport,
    ["POST"],
    "/EmployeeProfiles/me/cashier-barcode/refresh",
    () => {
      state().cashierBarcode = {
        ...state().cashierBarcode,
        barcode: IOS_REVIEW_SAMPLE_BARCODE,
        updatedAt: state().now,
      };
      return { data: clone(state().cashierBarcode) };
    },
  );
  register(
    transport,
    ["POST"],
    "/EmployeeProfiles/me/cashier-barcode/print-confirmation",
    () => {
      state().cashierBarcode.printCount += 1;
      state().cashierBarcode.updatedAt = state().now;
      return { data: clone(state().cashierBarcode) };
    },
  );
}

function registerDeviceRoutes(
  transport: ReviewTransport,
  dataStore: ReviewDataStore,
  holder: AppRouteStateHolder,
) {
  const state = () => holder.current;
  register(transport, ["GET"], "/mobile/device-management/paged", () => ({
    data: {
      devices: clone(state().devices),
      pagination: {
        pageNumber: 1,
        pageSize: 20,
        totalCount: state().devices.length,
        totalPages: 1,
      },
    },
  }));
  register(
    transport,
    ["POST"],
    /^\/mobile\/device-management\/([^/]+)\/(activate|disable|lock)$/i,
    ({ match }) => {
      const id = decodeURIComponent(match?.[1] ?? "");
      const action = match?.[2] ?? "activate";
      const device = state().devices.find((item) => String(item.id) === id);
      if (!device) throw new Error(`IOS_REVIEW_DEVICE_NOT_FOUND: ${id}`);
      device.status = action === "activate" ? 1 : action === "disable" ? 0 : 2;
      device.updatedAt = state().now;
      mirrorUpdate(
        state(),
        dataStore,
        "devices",
        id,
        String(device.deviceName ?? id),
        device.status,
      );
      return { data: { success: true } };
    },
  );
  register(transport, ["GET"], "/mobile/app-device-status/paged", () => ({
    data: {
      items: clone(state().appDevices),
      pageNumber: 1,
      pageSize: 20,
      totalCount: state().appDevices.length,
      totalPages: 1,
    },
  }));
  register(transport, ["GET"], "/mobile/app-device-status/summary", () => ({
    data: {
      total: 1,
      online: 1,
      offline: 0,
      android: 0,
      ios: 1,
      unknownSystem: 0,
    },
  }));
  register(transport, ["POST"], "/mobile/app-device-status/heartbeat", () => ({
    data: { success: true, simulated: true },
  }));
}

function registerReportRoutes(
  transport: ReviewTransport,
  holder: AppRouteStateHolder,
) {
  const state = () => holder.current;
  // 审核模式也必须遵守真实报表的完整性包络，避免 mock 让缺元数据的旧响应误通过页面门禁。
  const freshReportMetadata = () => ({
    statisticStatus: "Fresh",
    statisticMessage: null,
    statisticUpdatedAt: state().now,
    cacheVersion: "ios-review-report-v1",
  });
  const roundReportAmount = (value: number) => Math.round(value * 100) / 100;
  const getQueryValues = (query: URLSearchParams, key: string) =>
    query
      .getAll(key)
      .flatMap((value) => value.split(","))
      .map((value) => value.trim())
      .filter(Boolean);
  const getScopedStores = (query: URLSearchParams) => {
    const branchCodes = getQueryValues(query, "branchCodes");
    if (branchCodes.length === 0) return IOS_REVIEW_STORES;
    const branchCodeSet = new Set(branchCodes);
    return IOS_REVIEW_STORES.filter((store) => branchCodeSet.has(store.storeCode));
  };
  const getReportDateRange = (query: URLSearchParams) => {
    const dayMs = 24 * 60 * 60 * 1000;
    const toUtcDay = (value: string | null) => {
      if (!value || !/^\d{4}-\d{2}-\d{2}$/.test(value)) return null;
      const utcMs = Date.parse(`${value}T00:00:00.000Z`);
      if (
        !Number.isFinite(utcMs)
        || new Date(utcMs).toISOString().slice(0, 10) !== value
      ) {
        return null;
      }
      return utcMs;
    };
    const fallbackDay = toUtcDay(state().today)!;
    const startDay = toUtcDay(query.get("startDate")) ?? fallbackDay;
    const endDay = toUtcDay(query.get("endDate")) ?? startDay;
    if (endDay < startDay) return [];
    return Array.from(
      { length: Math.floor((endDay - startDay) / dayMs) + 1 },
      (_, index) => new Date(startDay + index * dayMs).toISOString().slice(0, 10),
    );
  };
  const getStoreFixtureRank = (storeCode: string) => {
    const index = IOS_REVIEW_STORES.findIndex(
      (store) => store.storeCode === storeCode,
    );
    return index < 0 ? 0 : IOS_REVIEW_STORES.length - index;
  };
  const getStoreScale = (query: URLSearchParams) => {
    const branchCodes = getQueryValues(query, "branchCodes");
    if (branchCodes.length === 0) return 1;
    const allStoreWeight = IOS_REVIEW_STORES.reduce(
      (total, store) => total + getStoreFixtureRank(store.storeCode),
      0,
    );
    const scopedStoreWeight = getScopedStores(query).reduce(
      (total, store) => total + getStoreFixtureRank(store.storeCode),
      0,
    );
    return scopedStoreWeight / allStoreWeight;
  };
  const getDailyStorePerformanceFixture = (
    date: string,
    storeCode: string,
  ) => {
    const descendingIndex = getStoreFixtureRank(storeCode) - 1;
    const utcDay = Math.floor(
      Date.parse(`${date}T00:00:00.000Z`) / (24 * 60 * 60 * 1000),
    );
    const weekdayOffset = ((utcDay % 7) + 7) % 7;
    return {
      revenue: 1250 + descendingIndex * 375 + weekdayOffset * 60,
      revenueLY: 1100 + descendingIndex * 300 + weekdayOffset * 45,
      transactions: 42 + descendingIndex * 8 + weekdayOffset,
      transactionsLY: 38 + descendingIndex * 7 + weekdayOffset,
    };
  };
  const sumStorePerformanceFixtures = (
    dates: string[],
    storeCodes: string[],
  ) => dates.reduce(
    (dateTotals, date) => {
      const dailyTotals = storeCodes.reduce(
        (storeTotals, storeCode) => {
          const fixture = getDailyStorePerformanceFixture(date, storeCode);
          storeTotals.revenue += fixture.revenue;
          storeTotals.revenueLY += fixture.revenueLY;
          storeTotals.transactions += fixture.transactions;
          storeTotals.transactionsLY += fixture.transactionsLY;
          return storeTotals;
        },
        { revenue: 0, revenueLY: 0, transactions: 0, transactionsLY: 0 },
      );
      dateTotals.revenue += dailyTotals.revenue;
      dateTotals.revenueLY += dailyTotals.revenueLY;
      dateTotals.transactions += dailyTotals.transactions;
      dateTotals.transactionsLY += dailyTotals.transactionsLY;
      return dateTotals;
    },
    { revenue: 0, revenueLY: 0, transactions: 0, transactionsLY: 0 },
  );
  const distributeReportTotal = (
    total: number,
    weights: number[],
    fractionDigits: number,
  ) => {
    const unit = 10 ** fractionDigits;
    const totalUnits = Math.round(total * unit);
    const weightTotal = weights.reduce((sum, weight) => sum + weight, 0);
    let distributedUnits = 0;
    return weights.map((weight, index) => {
      const units = index === weights.length - 1
        ? totalUnits - distributedUnits
        : Math.round(totalUnits * weight / weightTotal);
      distributedUnits += units;
      return units / unit;
    });
  };
  // 报表 fixture 与全局商品集合隔离，既能覆盖审核页面的数据密度，也不会污染扫码、促销等业务演示。
  const reportSupplierFixtures = Array.from({ length: 8 }, (_, index) => {
    const totalAmount = 1250 - index * 85;
    const compareTotalAmount = 1100 - index * 70;
    const grossMarginRate = 0.3 + (index % 4) * 0.015;
    const compareGrossMarginRate = 0.28 + (index % 3) * 0.012;
    const costAmount = roundReportAmount(totalAmount * (1 - grossMarginRate));
    const compareCostAmount = roundReportAmount(
      compareTotalAmount * (1 - compareGrossMarginRate),
    );
    const orderCount = 8 + index;
    const compareOrderCount = 7 + index;
    return {
      supplierCode: `REV-SUP-${String(index + 1).padStart(3, "0")}`,
      supplierName: `Review Supplier ${String(index + 1).padStart(2, "0")}`,
      totalAmount,
      costAmount,
      grossProfit: roundReportAmount(totalAmount - costAmount),
      grossMarginRate,
      compareTotalAmount,
      compareCostAmount,
      compareGrossProfit: roundReportAmount(compareTotalAmount - compareCostAmount),
      compareGrossMarginRate,
      totalQuantity: 36 + index * 5,
      storeCount: IOS_REVIEW_STORES.length,
      orderCount,
      compareOrderCount,
      averageTransaction: roundReportAmount(totalAmount / orderCount),
      compareAverageTransaction: roundReportAmount(
        compareTotalAmount / compareOrderCount,
      ),
    };
  });
  const reportProductFixtures = Array.from({ length: 24 }, (_, index) => {
    const salesAmount = 2400 - index * 60;
    const compareSalesAmount = 2160 - index * 52;
    const grossMarginRate = 0.32 + (index % 5) * 0.012;
    const compareGrossMarginRate = 0.3 + (index % 4) * 0.01;
    const hasCost = index !== 23;
    const costAmount = hasCost
      ? roundReportAmount(salesAmount * (1 - grossMarginRate))
      : null;
    const compareCostAmount = hasCost
      ? roundReportAmount(compareSalesAmount * (1 - compareGrossMarginRate))
      : null;
    const quantity = 60 - index;
    const compareQuantity = 54 - index;
    return {
      productCode: `REV-RPT-PROD-${String(index + 1).padStart(3, "0")}`,
      itemNumber: `RPT-${String(index + 1).padStart(5, "0")}`,
      productName: `Review Report Product ${String(index + 1).padStart(2, "0")}`,
      supplierCode: reportSupplierFixtures[index % reportSupplierFixtures.length]!.supplierCode,
      // 前 8 项覆盖中国供应商范围，确保审核模式也能发现供应商页签串数回归。
      supplierScope: index < reportSupplierFixtures.length ? "china" : "australia",
      productImage: null,
      quantity,
      compareQuantity,
      salesAmount,
      compareSalesAmount,
      costAmount,
      compareCostAmount,
      grossProfit:
        costAmount === null ? null : roundReportAmount(salesAmount - costAmount),
      compareGrossProfit:
        compareCostAmount === null
          ? null
          : roundReportAmount(compareSalesAmount - compareCostAmount),
      grossMarginRate: hasCost ? grossMarginRate : null,
      compareGrossMarginRate: hasCost ? compareGrossMarginRate : null,
      averageUnitPrice: roundReportAmount(salesAmount / quantity),
      compareAverageUnitPrice: roundReportAmount(
        compareSalesAmount / compareQuantity,
      ),
      orderCount: 18 + index,
      compareOrderCount: 16 + index,
    };
  });
  register(
    transport,
    ["GET"],
    "/react/v1/product-movement-report/store-options",
    () => ({
      data: IOS_REVIEW_STORES.map((store) => ({
        storeCode: store.storeCode,
        storeName: store.storeName,
      })),
    }),
  );
  register(
    transport,
    ["GET"],
    "/react/v1/dashboard/executive-branch-performance",
    ({ query }) => {
      const dates = getReportDateRange(query);
      const items = getScopedStores(query).map((store) => {
        return {
          branchCode: store.storeCode,
          branchName: store.storeName,
          ...sumStorePerformanceFixtures(dates, [store.storeCode]),
        };
      });
      return {
        data: {
          ...freshReportMetadata(),
          statisticsPending: false,
          statisticsExpectedBranchCount: items.length,
          statisticsSnapshotBranchCount: items.length,
          items,
        },
      };
    },
  );
  register(
    transport,
    ["GET"],
    "/react/v1/dashboard/executive-hourly-traffic",
    ({ query }) => {
      const dates = getReportDateRange(query);
      // 与设计原型一致：完整营业时段为 08:00–21:00，共 14 段。
      const weights = Array.from(
        { length: 14 },
        (_, index) => 4 + Math.max(0, 7 - Math.abs(index - 8)) + (index % 3) * 0.8,
      );
      const compareWeights = weights.map(
        (weight, index) => weight * (0.94 + (index % 5) * 0.022),
      );
      // 与真实接口一致按「店×小时」返回：日报累计对比要按店对齐排行，单店下钻仍是 14 行。
      // 末段接收分配后的余数，确保每家店 14 段在分/整数精度上与该店摘要严格守恒。
      const items = getScopedStores(query).flatMap((store) => {
        const storeTotals = sumStorePerformanceFixtures(dates, [store.storeCode]);
        const revenueByHour = distributeReportTotal(storeTotals.revenue, weights, 2);
        const revenueLYByHour = distributeReportTotal(storeTotals.revenueLY, compareWeights, 2);
        const transactionsByHour = distributeReportTotal(storeTotals.transactions, weights, 0);
        const transactionsLYByHour = distributeReportTotal(storeTotals.transactionsLY, compareWeights, 0);
        return weights.map((_, index) => ({
          hour: index + 8,
          branchCode: store.storeCode,
          branchName: store.storeName,
          revenue: revenueByHour[index]!,
          revenueLY: revenueLYByHour[index]!,
          transactions: transactionsByHour[index]!,
          transactionsLY: transactionsLYByHour[index]!,
        }));
      });
      return {
        data: {
          ...freshReportMetadata(),
          statisticsPending: false,
          statisticsExpectedItemCount: items.length,
          statisticsSnapshotItemCount: items.length,
          items,
        },
      };
    },
  );
  register(
    transport,
    ["GET"],
    "/react/v1/dashboard/branch-daily-performance",
    ({ query }) => {
      const scopedStores = getScopedStores(query);
      const items = getReportDateRange(query).flatMap((date) => {
        return scopedStores.map((store) => {
          return {
            date,
            branchCode: store.storeCode,
            branchName: store.storeName,
            ...getDailyStorePerformanceFixture(date, store.storeCode),
          };
        });
      });
      return {
        data: {
          ...freshReportMetadata(),
          statisticsPending: false,
          statisticsExpectedItemCount: items.length,
          statisticsSnapshotItemCount: items.length,
          items,
        },
      };
    },
  );
  register(
    transport,
    ["GET"],
    /^\/react\/v1\/dashboard\/(?:china-supplier-sales-rank|supplier-sales-rank)$/i,
    ({ query }) => {
      const storeScale = getStoreScale(query);
      const storeCount = getScopedStores(query).length;
      const items = reportSupplierFixtures.map((supplier) => {
        if (storeScale === 1) return clone(supplier);
        const totalAmount = roundReportAmount(supplier.totalAmount * storeScale);
        const compareTotalAmount = roundReportAmount(
          supplier.compareTotalAmount * storeScale,
        );
        const costAmount = roundReportAmount(supplier.costAmount * storeScale);
        const compareCostAmount = roundReportAmount(
          supplier.compareCostAmount * storeScale,
        );
        const orderCount = storeCount === 0
          ? 0
          : Math.max(1, Math.round(supplier.orderCount * storeScale));
        const compareOrderCount = storeCount === 0
          ? 0
          : Math.max(1, Math.round(supplier.compareOrderCount * storeScale));
        return {
          ...supplier,
          totalAmount,
          compareTotalAmount,
          costAmount,
          compareCostAmount,
          grossProfit: roundReportAmount(totalAmount - costAmount),
          compareGrossProfit: roundReportAmount(
            compareTotalAmount - compareCostAmount,
          ),
          grossMarginRate: storeCount === 0 ? 0 : supplier.grossMarginRate,
          compareGrossMarginRate:
            storeCount === 0 ? 0 : supplier.compareGrossMarginRate,
          totalQuantity: Math.round(supplier.totalQuantity * storeScale),
          storeCount,
          orderCount,
          compareOrderCount,
          averageTransaction:
            orderCount > 0 ? roundReportAmount(totalAmount / orderCount) : 0,
          compareAverageTransaction:
            compareOrderCount > 0
              ? roundReportAmount(compareTotalAmount / compareOrderCount)
              : 0,
        };
      });
      return {
        data: {
          ...freshReportMetadata(),
          items,
        },
      };
    },
  );
  register(
    transport,
    ["GET"],
    "/react/v1/dashboard/enhanced-sales-product-details",
    ({ query }) => {
      // Mobile 当前发送 pageIndex；审核验收脚本也使用 pageNumber，两者采用同一套真实分页语义。
      const pageNumber = Number(
        query.get("pageNumber") ?? query.get("pageIndex") ?? 1,
      );
      const pageSize = Number(query.get("pageSize") ?? 20);
      const supplierCodes = new Set([
        ...getQueryValues(query, "localSupplierCodes"),
        ...getQueryValues(query, "chinaSupplierCodes"),
      ]);
      const supplierScope = query.get("supplierScope")?.trim().toLocaleLowerCase() ?? "";
      const productSearch = query.get("productSearch")?.trim().toLocaleLowerCase() ?? "";
      const storeScale = getStoreScale(query);
      const filteredRows = reportProductFixtures
        .filter((product) => supplierScope !== "china" || product.supplierScope === "china")
        .filter((product) =>
          supplierCodes.size === 0 || supplierCodes.has(product.supplierCode),
        )
        .filter((product) => {
          if (!productSearch) return true;
          return `${product.productCode} ${product.itemNumber} ${product.productName}`
            .toLocaleLowerCase()
            .includes(productSearch);
        })
        .map((product) => {
          if (storeScale === 1) return clone(product);
          const salesAmount = roundReportAmount(product.salesAmount * storeScale);
          const compareSalesAmount = roundReportAmount(
            product.compareSalesAmount * storeScale,
          );
          const costAmount = product.costAmount === null
            ? null
            : roundReportAmount(product.costAmount * storeScale);
          const compareCostAmount = product.compareCostAmount === null
            ? null
            : roundReportAmount(product.compareCostAmount * storeScale);
          const quantity = Math.round(product.quantity * storeScale);
          const compareQuantity = Math.round(product.compareQuantity * storeScale);
          return {
            ...product,
            quantity,
            compareQuantity,
            salesAmount,
            compareSalesAmount,
            costAmount,
            compareCostAmount,
            grossProfit:
              costAmount === null ? null : roundReportAmount(salesAmount - costAmount),
            compareGrossProfit:
              compareCostAmount === null
                ? null
                : roundReportAmount(compareSalesAmount - compareCostAmount),
            grossMarginRate:
              costAmount === null
                ? null
                : storeScale === 0 ? 0 : product.grossMarginRate,
            compareGrossMarginRate:
              compareCostAmount === null
                ? null
                : storeScale === 0 ? 0 : product.compareGrossMarginRate,
            averageUnitPrice:
              quantity > 0 ? roundReportAmount(salesAmount / quantity) : 0,
            compareAverageUnitPrice:
              compareQuantity > 0
                ? roundReportAmount(compareSalesAmount / compareQuantity)
                : 0,
            orderCount: Math.round(product.orderCount * storeScale),
            compareOrderCount: Math.round(product.compareOrderCount * storeScale),
          };
        });
      const sort = parseReviewReportSort(query);
      const orderedRows = sort
        ? sortReportRows(filteredRows, sort, REVIEW_REPORT_PRODUCT_SORT_VALUES, (row) => String(row.productCode))
        : filteredRows;
      const result = pagedSlice(orderedRows, pageNumber, pageSize);
      return {
        data: {
          ...freshReportMetadata(),
          ...result,
          pageIndex: result.pageNumber,
        },
      };
    },
  );
  register(
    transport,
    ["GET"],
    /^\/react\/v1\/dashboard\/(?:china-supplier-store-sales|supplier-store-sales)$/i,
    ({ query }) => {
      const supplierCode =
        query.get("supplierCodes")?.split(",")[0]?.trim() ||
        reportSupplierFixtures[0]!.supplierCode;
      const supplierIndex = Math.max(
        0,
        reportSupplierFixtures.findIndex(
          (supplier) => supplier.supplierCode === supplierCode,
        ),
      );
      const supplier =
        reportSupplierFixtures[supplierIndex] ?? reportSupplierFixtures[0]!;
      const items = IOS_REVIEW_STORES.map((store, index) => {
        const scale = 1 + supplierIndex * 0.04;
        const totalAmount = roundReportAmount((720 + index * 120) * scale);
        const compareTotalAmount = roundReportAmount((650 + index * 100) * scale);
        const grossMarginRate = 0.3 + ((supplierIndex + index) % 4) * 0.01;
        const compareGrossMarginRate =
          0.28 + ((supplierIndex + index) % 3) * 0.01;
        const costAmount = roundReportAmount(
          totalAmount * (1 - grossMarginRate),
        );
        const compareCostAmount = roundReportAmount(
          compareTotalAmount * (1 - compareGrossMarginRate),
        );
        const orderCount = 6 + index;
        const compareOrderCount = 5 + index;
        return {
          branchCode: store.storeCode,
          branchName: store.storeName,
          supplierCode: supplier.supplierCode,
          supplierName: supplier.supplierName,
          totalAmount,
          costAmount,
          grossProfit: roundReportAmount(totalAmount - costAmount),
          grossMarginRate,
          compareTotalAmount,
          compareCostAmount,
          compareGrossProfit: roundReportAmount(
            compareTotalAmount - compareCostAmount,
          ),
          compareGrossMarginRate,
          totalQuantity: 24 + index * 4,
          orderCount,
          compareOrderCount,
          averageTransaction: roundReportAmount(totalAmount / orderCount),
          compareAverageTransaction: roundReportAmount(
            compareTotalAmount / compareOrderCount,
          ),
        };
      });
      return {
        data: {
          ...freshReportMetadata(),
          items,
        },
      };
    },
  );
  register(
    transport,
    ["GET"],
    "/react/v1/dashboard/china-supplier-branch-totals",
    ({ query }) => {
      // 与真实接口同形：逐店返回全部中国供应商合计。先按排行 mock 的同一缩放算出范围合计，
      // 再按门店权重拆到各店，保证逐店之和与中国供应商排行合计在分位上严格守恒。
      const storeScale = getStoreScale(query);
      const scaleAmount = (value: number) => (storeScale === 1 ? value : roundReportAmount(value * storeScale));
      const totals = reportSupplierFixtures.reduce(
        (sum, supplier) => {
          const totalAmount = scaleAmount(supplier.totalAmount);
          const compareTotalAmount = scaleAmount(supplier.compareTotalAmount);
          sum.totalAmount += totalAmount;
          sum.compareTotalAmount += compareTotalAmount;
          sum.grossProfit += roundReportAmount(totalAmount - scaleAmount(supplier.costAmount));
          sum.compareGrossProfit += roundReportAmount(compareTotalAmount - scaleAmount(supplier.compareCostAmount));
          sum.totalQuantity += storeScale === 1
            ? supplier.totalQuantity
            : Math.round(supplier.totalQuantity * storeScale);
          return sum;
        },
        { totalAmount: 0, compareTotalAmount: 0, grossProfit: 0, compareGrossProfit: 0, totalQuantity: 0 },
      );
      const stores = getScopedStores(query);
      const weights = stores.map((store) => getStoreFixtureRank(store.storeCode));
      const amounts = distributeReportTotal(roundReportAmount(totals.totalAmount), weights, 2);
      const compareAmounts = distributeReportTotal(roundReportAmount(totals.compareTotalAmount), weights, 2);
      const grossProfits = distributeReportTotal(roundReportAmount(totals.grossProfit), weights, 2);
      const compareGrossProfits = distributeReportTotal(roundReportAmount(totals.compareGrossProfit), weights, 2);
      const quantities = distributeReportTotal(totals.totalQuantity, weights, 0);
      const compareQuantities = distributeReportTotal(Math.round(totals.totalQuantity * 0.9), weights, 0);
      const items = stores.map((store, index) => {
        const totalAmount = amounts[index]!;
        const compareTotalAmount = compareAmounts[index]!;
        const grossProfit = grossProfits[index]!;
        const compareGrossProfit = compareGrossProfits[index]!;
        return {
          branchCode: store.storeCode,
          branchName: store.storeName,
          totalAmount,
          totalQuantity: quantities[index]!,
          supplierCount: reportSupplierFixtures.length,
          grossProfit,
          grossMarginRate: totalAmount > 0 ? grossProfit / totalAmount : null,
          costStatus: "Complete",
          compareTotalAmount,
          compareTotalQuantity: compareQuantities[index]!,
          compareGrossProfit,
          compareGrossMarginRate: compareTotalAmount > 0 ? compareGrossProfit / compareTotalAmount : null,
          compareCostStatus: "Complete",
        };
      });
      return {
        data: {
          ...freshReportMetadata(),
          items,
        },
      };
    },
  );
  register(
    transport,
    ["GET"],
    "/react/v1/dashboard/product-sales-by-branches",
    ({ query }) => {
      const productCode =
        query.get("productCode") || reportProductFixtures[0]!.productCode;
      const productIndex = Math.max(
        0,
        reportProductFixtures.findIndex(
          (product) => product.productCode === productCode,
        ),
      );
      const product =
        reportProductFixtures[productIndex] ?? reportProductFixtures[0]!;
      const hasCost = product.costAmount !== null;
      const items = IOS_REVIEW_STORES.map((store, index) => {
        const quantity = 12 + index;
        const compareQuantity = 10 + index;
        const salesAmount = 240 + index * 50;
        const compareSalesAmount = 210 + index * 40;
        const grossMarginRate = 0.32 + ((productIndex + index) % 5) * 0.01;
        const compareGrossMarginRate =
          0.3 + ((productIndex + index) % 4) * 0.01;
        const costAmount = hasCost
          ? roundReportAmount(salesAmount * (1 - grossMarginRate))
          : null;
        const compareCostAmount = hasCost
          ? roundReportAmount(
              compareSalesAmount * (1 - compareGrossMarginRate),
            )
          : null;
        return {
          branchCode: store.storeCode,
          branchName: store.storeName,
          productCode: product.productCode,
          quantity,
          compareQuantity,
          salesAmount: 240 + index * 50,
          compareSalesAmount: 210 + index * 40,
          costAmount,
          compareCostAmount,
          grossProfit:
            costAmount === null
              ? null
              : roundReportAmount(salesAmount - costAmount),
          compareGrossProfit:
            compareCostAmount === null
              ? null
              : roundReportAmount(compareSalesAmount - compareCostAmount),
          grossMarginRate: hasCost ? grossMarginRate : null,
          compareGrossMarginRate: hasCost ? compareGrossMarginRate : null,
          averageUnitPrice: roundReportAmount(salesAmount / quantity),
          compareAverageUnitPrice: roundReportAmount(
            compareSalesAmount / compareQuantity,
          ),
        };
      });
      return {
        data: {
          ...freshReportMetadata(),
          items,
        },
      };
    },
  );
  register(
    transport,
    ["GET"],
    "/react/v1/dashboard/statistics-freshness",
    () => ({
      data: {
        lastSuccessfulAtUtc: state().now,
        latestRunStatus: "Success",
      },
    }),
  );
}

/** 审核模式回传的打码邮箱，与后端 PasswordResetService.MaskEmail 口径一致。 */
function maskReviewEmail(email: string) {
  const [name = "", domain = ""] = email.split("@");
  return `${name.slice(0, name.length <= 2 ? 1 : 2)}***@${domain}`;
}
