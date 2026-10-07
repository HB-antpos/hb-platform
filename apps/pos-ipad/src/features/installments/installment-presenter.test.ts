import assert from "node:assert/strict";
import test from "node:test";

import {
  INSTALLMENTS_ADD_REPAYMENT_PERMISSION,
  INSTALLMENTS_CANCEL_PERMISSION,
  INSTALLMENTS_CONFIRM_PICKUP_PERMISSION,
  INSTALLMENTS_CREATE_PERMISSION,
  INSTALLMENTS_REPRINT_PERMISSION,
  INSTALLMENTS_VIEW_PERMISSION,
} from "@hb/pos-domain/features/installments/installment-authorization";
import type { InstallmentDetails } from "./installment-models";
import {
  InstallmentPresenter,
  InstallmentWorkflowError,
  type InstallmentCreateDraft,
  type InstallmentCreateDraftPort,
  type InstallmentReprintPort,
  type InstallmentWorkflowPort,
} from "./installment-presenter";

import type { InstallmentStatus, InstallmentSummary } from "@/core/contracts";


const allPermissions = [
  INSTALLMENTS_VIEW_PERMISSION,
  INSTALLMENTS_CREATE_PERMISSION,
  INSTALLMENTS_ADD_REPAYMENT_PERMISSION,
  INSTALLMENTS_CANCEL_PERMISSION,
  INSTALLMENTS_CONFIRM_PICKUP_PERMISSION,
  INSTALLMENTS_REPRINT_PERMISSION,
];

test("查看权限控制历史读取，首次加载使用 51 条探针和默认门店范围", async () => {
  const workflow = new FakeWorkflow();
  const unauthorized = presenter(workflow, {
    permissions: [INSTALLMENTS_CREATE_PERMISSION],
  });

  await unauthorized.load();

  assert.equal(unauthorized.getState().kind, "unauthorized");
  assert.equal(workflow.listInputs.length, 0);

  const authorized = presenter(workflow);
  authorized.setSearchQuery(" Bob ");
  await authorized.load();

  assert.deepEqual(workflow.listInputs, [
    {
      dateFilter: { preset: "all", fromDate: null, toDate: null },
      deviceScope: "store",
      keyword: "Bob",
      online: true,
      skip: 0,
      status: null,
      take: 51,
    },
  ]);
  assert.equal(authorized.getState().orders.length, 2);
  assert.equal(authorized.getState().kind, "ready");
});

test("分页每次显示 50 条，以 51 条探针判断 hasMore 并按 guid 去重", async () => {
  const workflow = new FakeWorkflow();
  workflow.listResults.push(
    Array.from({ length: 51 }, (_, index) => summaryAt(index)),
    [summaryAt(49), ...Array.from({ length: 5 }, (_, index) => summaryAt(index + 50))],
  );
  const subject = presenter(workflow);

  await subject.load();

  assert.equal(subject.getState().orders.length, 50);
  assert.equal(subject.getState().hasMore, true);
  assert.equal(subject.getState().loadingMore, false);

  await subject.loadMore();

  assert.equal(subject.getState().orders.length, 55);
  assert.equal(new Set(subject.getState().orders.map((item) => item.installmentGuid)).size, 55);
  assert.equal(subject.getState().hasMore, false);
  assert.deepEqual(
    workflow.listInputs.map((input) => ({ skip: input.skip, take: input.take })),
    [
      { skip: 0, take: 51 },
      { skip: 50, take: 51 },
    ],
  );
});

test("状态、设备与日期筛选选择即刷新，并重置分页和详情选择", async () => {
  const workflow = new FakeWorkflow();
  const subject = presenter(workflow);
  await subject.load();
  await subject.select(GUID_ACTIVE);

  await subject.setStatusFilter("Active");
  assert.equal(subject.getState().selectedGuid, null);
  assert.equal(subject.getState().details, null);
  assert.equal(workflow.listInputs.at(-1)?.status, "Active");
  assert.equal(workflow.listInputs.at(-1)?.skip, 0);

  await subject.setDeviceScope("device");
  assert.equal(workflow.listInputs.at(-1)?.deviceScope, "device");

  await subject.setDateFilter({
    preset: "last7",
    fromDate: null,
    toDate: null,
  });
  assert.deepEqual(workflow.listInputs.at(-1)?.dateFilter, {
    preset: "last7",
    fromDate: null,
    toDate: null,
  });

  const callsBeforeInvalid = workflow.listInputs.length;
  await subject.setDateFilter({
    preset: "custom",
    fromDate: "2026-08-04",
    toDate: "2026-08-03",
  });
  assert.equal(workflow.listInputs.length, callsBeforeInvalid);
  assert.equal(subject.getState().statusCode, "invalid-date-filter");
  assert.equal(subject.getState().dateFilter.preset, "last7");
});

test("离线或 transport failure 使列表、选择和详情立即失效", async () => {
  const workflow = new FakeWorkflow();
  const subject = presenter(workflow);
  await subject.load();
  await subject.select(GUID_ACTIVE);

  subject.setOnline(false);

  assert.deepEqual(subject.getState().orders, []);
  assert.equal(subject.getState().selectedGuid, null);
  assert.equal(subject.getState().details, null);
  assert.equal(subject.getState().statusCode, "online-required");

  subject.setOnline(true);
  workflow.listResults.push(
    Array.from({ length: 51 }, (_, index) => summaryAt(index)),
  );
  await subject.load();
  await subject.select(GUID_ACTIVE);
  workflow.listError = new InstallmentWorkflowError(
    "online-required",
    "safe failure",
  );
  await subject.loadMore();

  assert.deepEqual(subject.getState().orders, []);
  assert.equal(subject.getState().selectedGuid, null);
  assert.equal(subject.getState().details, null);
  assert.equal(subject.getState().statusCode, "online-required");
});

test("详情重试复用当前选择，并区分服务不可用与未知失败", async () => {
  const workflow = new FakeWorkflow();
  const subject = presenter(workflow);
  workflow.detailError = new InstallmentWorkflowError(
    "service-unavailable",
    "safe failure",
  );

  await subject.select(GUID_ACTIVE);
  assert.equal(subject.getState().statusCode, "service-unavailable");
  assert.equal(subject.getState().selectedGuid, GUID_ACTIVE);

  workflow.detailError = null;
  await subject.retryDetails();
  assert.equal(subject.getState().details?.installmentGuid, GUID_ACTIVE);
  assert.equal(workflow.detailCalls.length, 2);
});

test("选择历史行读取详情，较旧异步结果不能覆盖新选择", async () => {
  const workflow = new FakeWorkflow();
  const first = deferred<InstallmentDetails | null>();
  workflow.detailResults.set(GUID_ACTIVE, first.promise);
  workflow.detailResults.set(
    GUID_PAID,
    Promise.resolve(details("PaidOff")),
  );
  const subject = presenter(workflow);
  await subject.load();

  const selectFirst = subject.select(GUID_ACTIVE);
  await subject.select(GUID_PAID);
  first.resolve(details("Active"));
  await selectFirst;

  assert.equal(subject.getState().selectedGuid, GUID_PAID);
  assert.equal(subject.getState().details?.status, "PaidOff");
});

test("重打能力严格要求原始权限、在线当前详情、非忙碌且 Port 判定可打", async () => {
  const workflow = new FakeWorkflow();
  const reprintPort = new FakeReprintPort();
  const subject = presenter(workflow, { reprintPort });

  assert.equal(subject.capabilities.reprint, false);
  await subject.load();
  await subject.select(GUID_ACTIVE);
  assert.equal(subject.capabilities.reprint, true);

  reprintPort.eligible = false;
  assert.equal(subject.capabilities.reprint, false);
  reprintPort.eligible = true;

  const repayment = deferred<InstallmentDetails>();
  workflow.nextRepayment = repayment.promise;
  subject.setRepaymentAmount("10.00");
  const pendingRepayment = subject.addRepayment();
  assert.equal(subject.capabilities.reprint, false);
  repayment.resolve(details("Active"));
  await pendingRepayment;

  const wrongPermission = presenter(new FakeWorkflow(), {
    permissions: [
      INSTALLMENTS_VIEW_PERMISSION,
      "permissions.posterminal.history.reprint",
    ],
    reprintPort,
  });
  await wrongPermission.select(GUID_ACTIVE);
  assert.equal(wrongPermission.capabilities.reprint, false);

  const recoveryWorkflow = new FakeWorkflow();
  recoveryWorkflow.createError = new InstallmentWorkflowError(
    "payment-recovery-required",
    "Payment outcome is unknown.",
  );
  const recovery = presenter(recoveryWorkflow, { reprintPort });
  await recovery.select(GUID_ACTIVE);
  recovery.showCreate();
  fillValidCreateForm(recovery);
  await recovery.create();
  assert.equal(recovery.getState().recoveryRequired, true);
  assert.equal(recovery.capabilities.reprint, false);

  subject.setOnline(false);
  assert.equal(subject.capabilities.reprint, false);
});

test("重打为单飞状态机，成功与失败都不改写详情和列表", async () => {
  const workflow = new FakeWorkflow();
  const reprintPort = new FakeReprintPort();
  const pending = deferred<void>();
  reprintPort.nextResult = pending.promise;
  const subject = presenter(workflow, { reprintPort });
  await subject.load();
  await subject.select(GUID_ACTIVE);
  const detailsBefore = subject.getState().details;
  const ordersBefore = subject.getState().orders;

  const first = subject.reprintSelected();
  const second = subject.reprintSelected();

  assert.equal(first, second);
  assert.deepEqual(reprintPort.calls, [GUID_ACTIVE]);
  assert.deepEqual(subject.getState().reprint, {
    kind: "submitting",
    installmentGuid: GUID_ACTIVE,
  });

  pending.resolve();
  await first;
  assert.deepEqual(subject.getState().reprint, {
    kind: "succeeded",
    installmentGuid: GUID_ACTIVE,
  });
  assert.equal(subject.getState().details, detailsBefore);
  assert.equal(subject.getState().orders, ordersBefore);

  reprintPort.nextResult = Promise.reject(new Error("printer unavailable"));
  await subject.reprintSelected();
  assert.deepEqual(subject.getState().reprint, {
    kind: "failed",
    installmentGuid: GUID_ACTIVE,
  });
  assert.equal(subject.getState().details, detailsBefore);
  assert.equal(subject.getState().orders, ordersBefore);
});

test("重打期间阻断全部分期写入口，写动作期间也不能开始重打", async () => {
  const scenarios = [
    {
      name: "create",
      status: "Active" as const,
      prepare(subject: InstallmentPresenter) {
        subject.showCreate();
        fillValidCreateForm(subject);
      },
      invoke(subject: InstallmentPresenter) {
        return subject.create();
      },
    },
    {
      name: "repayment",
      status: "Active" as const,
      prepare(subject: InstallmentPresenter) {
        subject.setRepaymentAmount("10.00");
      },
      invoke(subject: InstallmentPresenter) {
        return subject.addRepayment();
      },
    },
    {
      name: "cancel",
      status: "Active" as const,
      prepare() {},
      invoke(subject: InstallmentPresenter) {
        return subject.cancelWithRefund();
      },
    },
    {
      name: "void",
      status: "Active" as const,
      prepare() {},
      invoke(subject: InstallmentPresenter) {
        return subject.voidSelected();
      },
    },
    {
      name: "pickup",
      status: "PaidOff" as const,
      prepare() {},
      invoke(subject: InstallmentPresenter) {
        return subject.confirmPickup();
      },
    },
  ];

  for (const scenario of scenarios) {
    const workflow = new FakeWorkflow();
    const reprintPort = new FakeReprintPort();
    const pendingReprint = deferred<void>();
    reprintPort.nextResult = pendingReprint.promise;
    const subject = presenter(workflow, { reprintPort });
    await subject.select(
      scenario.status === "PaidOff" ? GUID_PAID : GUID_ACTIVE,
    );
    scenario.prepare(subject);

    const reprint = subject.reprintSelected();
    const blockedWrite = scenario.invoke(subject);

    assert.equal(
      blockedWrite,
      reprint,
      `${scenario.name} 应复用在途重打 Promise，不能启动写动作`,
    );
    assert.deepEqual(workflow.writeCalls, []);
    assert.equal(subject.getState().busy, true);

    pendingReprint.resolve();
    await reprint;
    assert.equal(subject.getState().busy, false);
  }

  const workflow = new FakeWorkflow();
  const pendingRepayment = deferred<InstallmentDetails>();
  workflow.nextRepayment = pendingRepayment.promise;
  const reprintPort = new FakeReprintPort();
  const subject = presenter(workflow, { reprintPort });
  await subject.load();
  await subject.select(GUID_ACTIVE);
  subject.setRepaymentAmount("10.00");

  const repayment = subject.addRepayment();
  await subject.reprintSelected();

  assert.deepEqual(reprintPort.calls, []);
  pendingRepayment.resolve(details("Active"));
  await repayment;
});

test("跨终端续付 capability 未启用时，所有既有分期写动作与重打均失败关闭", async () => {
  const scenarios = [
    {
      name: "repayment",
      status: "Active" as const,
      prepare(subject: InstallmentPresenter) {
        subject.setRepaymentAmount("10.00");
      },
      invoke(subject: InstallmentPresenter) {
        return subject.addRepayment();
      },
    },
    {
      name: "cancel",
      status: "Active" as const,
      prepare() {},
      invoke(subject: InstallmentPresenter) {
        return subject.cancelWithRefund();
      },
    },
    {
      name: "void",
      status: "Active" as const,
      prepare() {},
      invoke(subject: InstallmentPresenter) {
        return subject.voidSelected();
      },
    },
    {
      name: "pickup",
      status: "PaidOff" as const,
      prepare() {},
      invoke(subject: InstallmentPresenter) {
        return subject.confirmPickup();
      },
    },
  ];

  for (const scenario of scenarios) {
    const workflow = new FakeWorkflow();
    const installmentGuid =
      scenario.status === "PaidOff" ? GUID_PAID : GUID_ACTIVE;
    workflow.detailResults.set(
      installmentGuid,
      Promise.resolve({
        ...details(scenario.status),
        deviceCode: "IPAD-2",
      }),
    );
    const reprintPort = new FakeReprintPort();
    const subject = presenter(workflow, {
      reprintPort,
      trustedDeviceCode: "IPAD-1",
    });
    await subject.load();
    await subject.select(installmentGuid);
    scenario.prepare(subject);

    assert.equal(subject.capabilities.selectedDetailsWritable, false);
    assert.equal(subject.capabilities.selectedDetailsRepayable, false);
    assert.equal(subject.capabilities.reprint, false);
    await scenario.invoke(subject);
    await subject.reprintSelected();

    assert.deepEqual(
      workflow.writeCalls,
      [],
      `${scenario.name} 不得写入跨终端分期`,
    );
    assert.deepEqual(reprintPort.calls, []);
    assert.equal(subject.getState().statusCode, "conflict");
  }
});

test("同店跨终端 capability 启用后仅允许 Active 续付，高风险动作仍失败关闭", async () => {
  const workflow = new FakeWorkflow();
  workflow.crossDeviceRepaymentEnabled = true;
  workflow.repaymentClaimsRequired = false;
  workflow.detailResults.set(
    GUID_ACTIVE,
    Promise.resolve({ ...details("Active"), deviceCode: "IPAD-2" }),
  );
  const reprintPort = new FakeReprintPort();
  const subject = presenter(workflow, { reprintPort });

  await subject.load();
  await subject.load();
  await subject.select(GUID_ACTIVE);

  assert.equal(subject.capabilities.selectedDetailsRepayable, true);
  assert.equal(subject.capabilities.selectedDetailsWritable, false);
  assert.equal(subject.capabilities.reprint, false);

  subject.setRepaymentAmount("5.00");
  await subject.addRepayment();

  assert.equal(
    workflow.writeCalls.filter((call) => call.kind === "repayment").length,
    1,
  );
  assert.deepEqual(reprintPort.calls, []);

  const highRiskWorkflow = new FakeWorkflow();
  highRiskWorkflow.crossDeviceRepaymentEnabled = true;
  highRiskWorkflow.detailResults.set(
    GUID_ACTIVE,
    Promise.resolve({ ...details("Active"), deviceCode: "IPAD-2" }),
  );
  const highRiskSubject = presenter(highRiskWorkflow, { reprintPort });
  await highRiskSubject.load();
  await highRiskSubject.select(GUID_ACTIVE);
  await highRiskSubject.cancelWithRefund();
  await highRiskSubject.voidSelected();
  await highRiskSubject.reprintSelected();

  assert.deepEqual(highRiskWorkflow.writeCalls, []);
  assert.deepEqual(reprintPort.calls, []);
  assert.equal(highRiskSubject.getState().statusCode, "conflict");

  const pickupWorkflow = new FakeWorkflow();
  pickupWorkflow.crossDeviceRepaymentEnabled = true;
  pickupWorkflow.detailResults.set(
    GUID_PAID,
    Promise.resolve({ ...details("PaidOff"), deviceCode: "IPAD-2" }),
  );
  const pickupSubject = presenter(pickupWorkflow, { reprintPort });
  await pickupSubject.load();
  await pickupSubject.select(GUID_PAID);
  await pickupSubject.confirmPickup();
  await pickupSubject.reprintSelected();

  assert.deepEqual(pickupWorkflow.writeCalls, []);
  assert.deepEqual(reprintPort.calls, []);
  assert.equal(pickupSubject.getState().statusCode, "conflict");
});

test("同店跨终端取消退款、作废和提货分别由独立 capability 放行", async () => {
  const scenarios = [
    {
      capability: "crossDeviceCancelRefundEnabled" as const,
      expectedCapability: "selectedDetailsCancelRefundable" as const,
      expectedWrite: "cancel",
      status: "Active" as const,
      invoke: (subject: InstallmentPresenter) => subject.cancelWithRefund(),
    },
    {
      capability: "crossDeviceVoidEnabled" as const,
      expectedCapability: "selectedDetailsVoidable" as const,
      expectedWrite: "void",
      status: "Active" as const,
      invoke: (subject: InstallmentPresenter) => subject.voidSelected(),
    },
    {
      capability: "crossDevicePickupEnabled" as const,
      expectedCapability: "selectedDetailsPickupConfirmable" as const,
      expectedWrite: "pickup",
      status: "PaidOff" as const,
      invoke: (subject: InstallmentPresenter) => subject.confirmPickup(),
    },
  ];

  for (const scenario of scenarios) {
    const workflow = new FakeWorkflow();
    workflow[scenario.capability] = true;
    const installmentGuid =
      scenario.status === "PaidOff" ? GUID_PAID : GUID_ACTIVE;
    workflow.detailResults.set(
      installmentGuid,
      Promise.resolve({
        ...details(scenario.status),
        deviceCode: "IPAD-2",
      }),
    );
    const subject = presenter(workflow);

    await subject.load();
    await subject.select(installmentGuid);

    assert.equal(subject.capabilities[scenario.expectedCapability], true);
    await scenario.invoke(subject);
    assert.deepEqual(
      workflow.writeCalls.map((call) => call.kind),
      [scenario.expectedWrite],
    );
  }
});

test("capability 每轮 load 先失败关闭，GET 失败不阻断只读历史", async () => {
  const workflow = new FakeWorkflow();
  workflow.crossDeviceRepaymentEnabled = true;
  workflow.detailResults.set(
    GUID_ACTIVE,
    Promise.resolve({ ...details("Active"), deviceCode: "IPAD-2" }),
  );
  const subject = presenter(workflow);

  await subject.load();
  await subject.load();
  await subject.select(GUID_ACTIVE);
  assert.equal(subject.getState().kind, "ready");
  assert.equal(subject.capabilities.selectedDetailsRepayable, true);

  workflow.capabilityError = new Error("capabilities unavailable");
  await subject.load();
  await subject.select(GUID_ACTIVE);

  assert.equal(subject.getState().kind, "ready");
  assert.equal(subject.capabilities.selectedDetailsRepayable, false);
});

test("同机续付也要求服务端 claim supported，失败关闭不影响历史与详情", async () => {
  const workflow = new FakeWorkflow();
  workflow.repaymentClaimsSupported = false;
  const subject = presenter(workflow);

  await subject.load();
  await subject.select(GUID_ACTIVE);

  assert.equal(subject.getState().kind, "ready");
  assert.ok(subject.getState().details);
  assert.equal(subject.capabilities.selectedDetailsRepayable, false);
  subject.setRepaymentAmount("5.00");
  await subject.addRepayment();
  assert.deepEqual(workflow.writeCalls, []);
  assert.equal(subject.getState().statusCode, "conflict");
});

test("跨店详情即使设备相同且 capability 启用也不得续付或执行高风险动作", async () => {
  const workflow = new FakeWorkflow();
  workflow.crossDeviceRepaymentEnabled = true;
  workflow.detailResults.set(
    GUID_ACTIVE,
    Promise.resolve({
      ...details("Active"),
      storeCode: "S2",
      deviceCode: "IPAD-1",
    }),
  );
  const subject = presenter(workflow, { trustedStoreCode: "S1" });

  await subject.load();
  await subject.select(GUID_ACTIVE);

  assert.equal(subject.capabilities.selectedDetailsRepayable, false);
  assert.equal(subject.capabilities.selectedDetailsWritable, false);
  subject.setRepaymentAmount("5.00");
  await subject.addRepayment();
  await subject.cancelWithRefund();

  assert.deepEqual(workflow.writeCalls, []);
  assert.equal(subject.getState().statusCode, "conflict");
});

test("无资格重打 fail-closed，切换选择后旧打印结果不得覆盖当前状态", async () => {
  const noPort = presenter(new FakeWorkflow());
  await noPort.select(GUID_ACTIVE);
  await noPort.reprintSelected();
  assert.deepEqual(noPort.getState().reprint, { kind: "unavailable" });

  const workflow = new FakeWorkflow();
  const reprintPort = new FakeReprintPort();
  const pending = deferred<void>();
  reprintPort.nextResult = pending.promise;
  const subject = presenter(workflow, { reprintPort });
  await subject.select(GUID_ACTIVE);
  const reprint = subject.reprintSelected();

  await subject.select(GUID_PAID);
  assert.deepEqual(subject.getState().reprint, { kind: "idle" });
  pending.resolve();
  await reprint;

  assert.equal(subject.getState().selectedGuid, GUID_PAID);
  assert.deepEqual(subject.getState().reprint, { kind: "idle" });
});

test("所有写操作离线时失败关闭，不能调用 workflow", async () => {
  const workflow = new FakeWorkflow();
  const subject = presenter(workflow);
  subject.setOnline(false);
  subject.showCreate();
  fillValidCreateForm(subject);

  await subject.create();
  await subject.select(GUID_ACTIVE);
  subject.setRepaymentAmount("10.00");
  await subject.addRepayment();
  await subject.cancelWithRefund();
  await subject.voidSelected();
  await subject.select(GUID_PAID);
  await subject.confirmPickup();

  assert.equal(subject.getState().statusCode, "online-required");
  assert.deepEqual(workflow.writeCalls, []);
});

test("创建严格对齐 WPF 的客户、AUD 50 总额和 AUD 20 首付规则", async () => {
  const workflow = new FakeWorkflow();
  const drafts = new MutableDraftPort({
    revision: 7,
    totalCents: 4_999,
    lines: [
      {
        lineKey: "L1",
        displayName: "Tea",
        quantity: "1",
        actualAmountCents: 4_999,
      },
    ],
  });
  const subject = presenter(workflow, { drafts });
  subject.showCreate();
  fillValidCreateForm(subject);

  await subject.create();
  assert.equal(subject.getState().statusCode, "invalid-create");
  assert.deepEqual(workflow.writeCalls, []);

  drafts.set({
    revision: 8,
    totalCents: 5_000,
    lines: [
      {
        lineKey: "L1",
        displayName: "Tea",
        quantity: "1",
        actualAmountCents: 5_000,
      },
    ],
  });
  subject.setCreateDownPayment("19.99");
  await subject.create();
  assert.equal(subject.getState().statusCode, "invalid-create");

  subject.setCreateDownPayment("20.00");
  await subject.create();

  assert.deepEqual(workflow.writeCalls, [
    {
      kind: "create",
      input: {
        customerName: "Bob",
        customerPhone: "0400000000",
        downPaymentCents: 2_000,
        draftRevision: 8,
        method: "cash",
        note: "Collect Friday",
        voucherReference: null,
        voucherReservationToken: null,
      },
    },
  ]);
  assert.equal(subject.getState().statusCode, "create-complete");
  assert.equal(subject.getState().pane, "history");
});

test("券首付和补款只接收券码，workflow token 固定为 null 且公开状态没有 token 字段", async () => {
  const workflow = new FakeWorkflow();
  const subject = presenter(workflow);
  subject.showCreate();
  fillValidCreateForm(subject);
  subject.setCreatePaymentMethod("voucher");

  await subject.create();
  assert.equal(subject.getState().statusCode, "invalid-create");
  assert.deepEqual(workflow.writeCalls, []);

  subject.setCreateVoucherReference(" VOUCHER-REF ");
  await subject.create();
  assert.deepEqual(workflow.writeCalls[0], {
    kind: "create",
    input: {
      customerName: "Bob",
      customerPhone: "0400000000",
      downPaymentCents: 2_000,
      draftRevision: 1,
      method: "voucher",
      note: "Collect Friday",
      voucherReference: "VOUCHER-REF",
      voucherReservationToken: null,
    },
  });
  assert.equal(subject.getState().createVoucherReference, "");
  assert.equal(
    "createVoucherReservationToken" in subject.getState(),
    false,
  );
  assert.equal("setCreateVoucherReservationToken" in subject, false);

  await subject.load();
  await subject.select(GUID_ACTIVE);
  subject.setRepaymentAmount("10.00");
  subject.setRepaymentMethod("voucher");
  await subject.addRepayment();
  assert.equal(subject.getState().statusCode, "invalid-repayment");

  subject.setRepaymentVoucherReference(" REPAY-REF ");
  await subject.addRepayment();
  assert.deepEqual(workflow.writeCalls.at(-1), {
    kind: "repayment",
    input: {
      amountCents: 1_000,
      installmentGuid: GUID_ACTIVE,
      method: "voucher",
      voucherReference: "REPAY-REF",
      voucherReservationToken: null,
    },
  });
  assert.equal(subject.getState().repaymentVoucherReference, "");
  assert.equal(
    "repaymentVoucherReservationToken" in subject.getState(),
    false,
  );
  assert.equal("setRepaymentVoucherReservationToken" in subject, false);
});

test("券码在 workflow 开始前从公开状态清空，失败后也不恢复", async () => {
  const workflow = new FakeWorkflow();
  const createResult = deferred<InstallmentDetails>();
  workflow.nextCreate = createResult.promise;
  const subject = presenter(workflow);
  subject.showCreate();
  fillValidCreateForm(subject);
  subject.setCreatePaymentMethod("voucher");
  subject.setCreateVoucherReference(" CREATE-CODE ");

  const pendingCreate = subject.create();
  assert.equal(subject.getState().createVoucherReference, "");
  assert.equal(workflow.writeCalls.length, 1);
  createResult.resolve(details("Active"));
  await pendingCreate;

  await subject.load();
  await subject.select(GUID_ACTIVE);
  subject.setRepaymentAmount("10.00");
  subject.setRepaymentMethod("voucher");
  subject.setRepaymentVoucherReference(" REPAY-CODE ");
  workflow.repaymentError = new Error(
    "provider failed without echoing voucher material",
  );

  await subject.addRepayment();

  assert.equal(subject.getState().repaymentVoucherReference, "");
  assert.equal(subject.getState().statusCode, "action-failed");
});

test("补款仅允许 Active 且金额不超过余额；重复点击只产生一次写调用", async () => {
  const workflow = new FakeWorkflow();
  const repayment = deferred<InstallmentDetails>();
  workflow.nextRepayment = repayment.promise;
  const subject = presenter(workflow);
  await subject.load();
  await subject.select(GUID_ACTIVE);
  subject.setRepaymentAmount("80.01");

  await subject.addRepayment();
  assert.equal(subject.getState().statusCode, "invalid-repayment");

  subject.setRepaymentAmount("80.00");
  const first = subject.addRepayment();
  const second = subject.addRepayment();
  assert.equal(first, second);
  assert.equal(
    workflow.writeCalls.filter((call) => call.kind === "repayment").length,
    1,
  );
  repayment.resolve(details("PaidOff"));
  await first;

  assert.equal(subject.getState().details?.status, "PaidOff");
  assert.equal(subject.getState().statusCode, "repayment-complete");
  subject.setRepaymentAmount("1.00");
  await subject.addRepayment();
  assert.equal(subject.getState().statusCode, "invalid-repayment");
});

test("取消退款允许 Active 与已付清未提货，作废只允许 Active，取货只允许 PaidOff，并复用 WPF 权限边界", async () => {
  const workflow = new FakeWorkflow();
  const subject = presenter(workflow);
  await subject.select(GUID_ACTIVE);
  subject.setCancelReason(" Customer request ");
  await subject.cancelWithRefund();

  assert.deepEqual(workflow.writeCalls.at(-1), {
    kind: "cancel",
    input: {
      installmentGuid: GUID_ACTIVE,
      reason: "Customer request",
      refundMode: "original-route",
    },
  });

  workflow.detailResults.set(
    GUID_ACTIVE,
    Promise.resolve(details("Active")),
  );
  await subject.select(GUID_ACTIVE);
  subject.setVoidReason(" Incorrect order ");
  await subject.voidSelected();
  assert.deepEqual(workflow.writeCalls.at(-1), {
    kind: "void",
    input: {
      installmentGuid: GUID_ACTIVE,
      reason: "Incorrect order",
    },
  });

  await subject.select(GUID_PAID);
  subject.setPickupNote(" ID checked ");
  await subject.confirmPickup();
  assert.deepEqual(workflow.writeCalls.at(-1), {
    kind: "pickup",
    input: {
      installmentGuid: GUID_PAID,
      note: "ID checked",
    },
  });

  const noCancel = presenter(workflow, {
    permissions: [
      INSTALLMENTS_VIEW_PERMISSION,
      INSTALLMENTS_CONFIRM_PICKUP_PERMISSION,
    ],
  });
  await noCancel.select(GUID_ACTIVE);
  await noCancel.cancelWithRefund();
  assert.equal(noCancel.getState().statusCode, "permission-required");
});

test("已付清未提货可取消退款但不可作废；已提货、已取消都不可取消", async () => {
  const workflow = new FakeWorkflow();
  const subject = presenter(workflow);
  await subject.select(GUID_PAID);

  assert.equal(subject.capabilities.selectedDetailsCancelRefundable, true);
  assert.equal(subject.capabilities.selectedDetailsVoidable, false);
  assert.equal(subject.capabilities.selectedDetailsPickupConfirmable, true);
  await subject.cancelWithRefund();
  assert.deepEqual(workflow.writeCalls.at(-1), {
    kind: "cancel",
    input: {
      installmentGuid: GUID_PAID,
      reason: null,
      refundMode: "original-route",
    },
  });

  const writes = workflow.writeCalls.length;
  await subject.select(GUID_PAID);
  await subject.voidSelected();
  assert.equal(workflow.writeCalls.length, writes);

  for (const status of ["PickedUp", "Cancelled"] as const) {
    workflow.detailResults.set(GUID_PAID, Promise.resolve({
      ...details("PaidOff"),
      status,
    }));
    await subject.select(GUID_PAID);
    assert.equal(subject.capabilities.selectedDetailsCancelRefundable, false);
    await subject.cancelWithRefund();
    assert.equal(workflow.writeCalls.length, writes);
  }
});

test("退款方式：可选全部退代金券；含刷卡原付款时原路退不可用并强制退代金券", async () => {
  const workflow = new FakeWorkflow();
  const subject = presenter(workflow);
  await subject.select(GUID_ACTIVE);
  assert.equal(subject.capabilities.selectedDetailsOriginalRouteRefundable, true);
  assert.equal(subject.capabilities.selectedDetailsCancelRefundMode, "original-route");

  subject.setCancelRefundMode("voucher");
  assert.equal(subject.getState().cancelRefundMode, "voucher");
  assert.equal(subject.capabilities.selectedDetailsCancelRefundMode, "voucher");
  await subject.cancelWithRefund();
  assert.deepEqual(workflow.writeCalls.at(-1), {
    kind: "cancel",
    input: {
      installmentGuid: GUID_ACTIVE,
      reason: null,
      refundMode: "voucher",
    },
  });

  workflow.detailResults.set(GUID_ACTIVE, Promise.resolve({
    ...details("Active"),
    payments: [{
      paymentGuid: "20000000-0000-4000-8000-000000000001",
      method: "card",
      amountCents: 2_000,
      status: "Recorded",
      recordedAtIso: "2026-07-27T01:02:03.000Z",
      cashierId: "C1",
      deviceCode: "IPAD-1",
      cardType: "VISA",
      maskedCardNumber: "****4242",
    }],
  }));
  await subject.select(GUID_ACTIVE);
  assert.equal(subject.capabilities.selectedDetailsOriginalRouteRefundable, false);
  assert.equal(subject.capabilities.selectedDetailsCancelRefundMode, "voucher");
  subject.setCancelRefundMode("original-route");
  assert.equal(subject.getState().cancelRefundMode, "voucher");
  assert.equal(
    subject.getState().statusCode,
    "cancel-refund-method-unsupported",
  );
  await subject.cancelWithRefund();
  assert.deepEqual(workflow.writeCalls.at(-1), {
    kind: "cancel",
    input: {
      installmentGuid: GUID_ACTIVE,
      reason: null,
      refundMode: "voucher",
    },
  });
});

test("退款方式相关 workflow 错误映射为可读状态码", async () => {
  for (const [code, statusCode] of [
    ["refund-method-unsupported", "cancel-refund-method-unsupported"],
    ["refund-mode-unsupported", "cancel-refund-mode-unsupported"],
  ] as const) {
    const workflow = new FakeWorkflow();
    workflow.cancelError = new InstallmentWorkflowError(code, "safe failure");
    const subject = presenter(workflow);
    await subject.select(GUID_ACTIVE);
    await subject.cancelWithRefund();
    assert.equal(subject.getState().statusCode, statusCode);
    assert.equal(subject.getState().recoveryRequired, false);
  }
});

test("Unknown 后允许重试同一 durable action，恢复成功后解除提示", async () => {
  const workflow = new FakeWorkflow();
  workflow.createError = new InstallmentWorkflowError(
    "payment-recovery-required",
    "Payment outcome is unknown.",
  );
  const subject = presenter(workflow);
  subject.showCreate();
  fillValidCreateForm(subject);

  await subject.create();
  assert.equal(subject.getState().recoveryRequired, true);
  assert.equal(
    subject.getState().statusCode,
    "payment-recovery-required",
  );

  workflow.createError = null;
  await subject.recoverBlocking();
  assert.equal(
    workflow.writeCalls.filter((call) => call.kind === "create").length,
    1,
  );
  assert.equal(
    workflow.writeCalls.filter((call) => call.kind === "recover").length,
    1,
  );
  assert.equal(subject.getState().recoveryRequired, false);
  assert.equal(subject.getState().statusCode, "recovery-complete");
});

test("草稿变化会同步到创建页，销毁后不再订阅", () => {
  const drafts = new MutableDraftPort(defaultDraft());
  const subject = presenter(new FakeWorkflow(), { drafts });

  drafts.set({
    ...defaultDraft(),
    revision: 2,
    totalCents: 6_000,
  });
  assert.equal(subject.getState().createDraft?.revision, 2);

  subject.destroy();
  drafts.set({
    ...defaultDraft(),
    revision: 3,
    totalCents: 7_000,
  });
  assert.equal(subject.getState().createDraft?.revision, 2);
});

class FakeWorkflow implements InstallmentWorkflowPort {
  public readonly listInputs: Parameters<InstallmentWorkflowPort["list"]>[0][] = [];
  public readonly listResults: (readonly InstallmentSummary[])[] = [];
  public readonly detailCalls: string[] = [];
  public readonly writeCalls: {
    kind: string;
    input: unknown;
  }[] = [];
  public readonly detailResults = new Map<
    string,
    Promise<InstallmentDetails | null>
  >();
  public nextRepayment: Promise<InstallmentDetails> | null = null;
  public nextCreate: Promise<InstallmentDetails> | null = null;
  public createError: Error | null = null;
  public repaymentError: Error | null = null;
  public listError: Error | null = null;
  public detailError: Error | null = null;
  public capabilityError: Error | null = null;
  public crossDeviceRepaymentEnabled = false;
  public crossDeviceCancelRefundEnabled = false;
  public crossDeviceVoidEnabled = false;
  public crossDevicePickupEnabled = false;
  public repaymentClaimsSupported = true;
  public repaymentClaimsRequired = true;

  public async getRepaymentCapabilities() {
    if (this.capabilityError) throw this.capabilityError;
    return {
      repaymentClaimsSupported: this.repaymentClaimsSupported,
      cardRepaymentSupported: false,
      repaymentClaimsRequired: this.repaymentClaimsRequired,
      crossDeviceRepaymentEnabled: this.crossDeviceRepaymentEnabled,
      crossDeviceCancelRefundEnabled: this.crossDeviceCancelRefundEnabled,
      crossDeviceVoidEnabled: this.crossDeviceVoidEnabled,
      crossDevicePickupEnabled: this.crossDevicePickupEnabled,
      preparedClaimTtlSeconds: 300,
      cancelClaimsSupported: true,
    };
  }

  public async list(
    input: Parameters<InstallmentWorkflowPort["list"]>[0],
  ): Promise<readonly InstallmentSummary[]> {
    this.listInputs.push(input);
    if (this.listError) throw this.listError;
    if (!input.online) {
      throw new InstallmentWorkflowError(
        "online-required",
        "safe failure",
      );
    }
    const scripted = this.listResults.shift();
    if (scripted) return scripted;
    return [
      summary("Active"),
      { ...summary("PaidOff"), installmentGuid: GUID_PAID },
    ];
  }

  public getDetails(input: Readonly<{ installmentGuid: string }>) {
    this.detailCalls.push(input.installmentGuid);
    if (this.detailError) return Promise.reject(this.detailError);
    return (
      this.detailResults.get(input.installmentGuid) ??
      Promise.resolve(
        details(
          input.installmentGuid === GUID_PAID ? "PaidOff" : "Active",
        ),
      )
    );
  }

  public async create(input: unknown): Promise<InstallmentDetails> {
    this.writeCalls.push({ kind: "create", input });
    if (this.createError) throw this.createError;
    return this.nextCreate ?? details("Active");
  }

  public async recoverBlocking(): Promise<InstallmentDetails> {
    this.writeCalls.push({ kind: "recover", input: null });
    if (this.createError) throw this.createError;
    return details("Active");
  }

  public async addRepayment(input: unknown): Promise<InstallmentDetails> {
    this.writeCalls.push({ kind: "repayment", input });
    if (this.repaymentError) throw this.repaymentError;
    return this.nextRepayment ?? details("PaidOff");
  }

  public cancelError: Error | null = null;

  public async cancelWithRefund(input: unknown): Promise<InstallmentDetails> {
    this.writeCalls.push({ kind: "cancel", input });
    if (this.cancelError) throw this.cancelError;
    return details("Cancelled");
  }

  public async void(input: unknown): Promise<InstallmentDetails> {
    this.writeCalls.push({ kind: "void", input });
    return details("Cancelled");
  }

  public async confirmPickup(input: unknown): Promise<InstallmentDetails> {
    this.writeCalls.push({ kind: "pickup", input });
    return details("PickedUp");
  }
}

class MutableDraftPort implements InstallmentCreateDraftPort {
  private readonly listeners = new Set<() => void>();

  public constructor(private value: InstallmentCreateDraft | null) {}

  public getSnapshot = () => this.value;

  public subscribe = (listener: () => void) => {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  };

  public set(value: InstallmentCreateDraft | null) {
    this.value = value;
    for (const listener of this.listeners) listener();
  }
}

class FakeReprintPort implements InstallmentReprintPort {
  public eligible = true;
  public readonly calls: string[] = [];
  public nextResult: Promise<void> = Promise.resolve();

  public canReprint(_details: InstallmentDetails): boolean {
    return this.eligible;
  }

  public reprintExistingInstallment(installmentGuid: string): Promise<void> {
    this.calls.push(installmentGuid);
    return this.nextResult;
  }
}

function presenter(
  workflow: FakeWorkflow,
  overrides: Partial<{
    drafts: InstallmentCreateDraftPort;
    permissions: readonly string[];
    reprintPort: InstallmentReprintPort | null;
    trustedDeviceCode: string;
    trustedStoreCode: string;
  }> = {},
) {
  return new InstallmentPresenter({
    createDrafts: overrides.drafts ?? new MutableDraftPort(defaultDraft()),
    initialOnline: true,
    permissions: overrides.permissions ?? allPermissions,
    trustedDeviceCode: overrides.trustedDeviceCode ?? "IPAD-1",
    trustedStoreCode: overrides.trustedStoreCode ?? "S1",
    ...(overrides.reprintPort !== undefined
      ? { reprintPort: overrides.reprintPort }
      : {}),
    workflow,
  });
}

function fillValidCreateForm(subject: InstallmentPresenter) {
  subject.setCustomerName(" Bob ");
  subject.setCustomerPhone(" 0400000000 ");
  subject.setCreateNote(" Collect Friday ");
  subject.setCreateDownPayment("20.00");
  subject.setCreatePaymentMethod("cash");
}

function defaultDraft(): InstallmentCreateDraft {
  return {
    revision: 1,
    totalCents: 10_000,
    lines: [
      {
        lineKey: "L1",
        displayName: "Tea",
        quantity: "1",
        actualAmountCents: 10_000,
      },
    ],
  };
}

const GUID_ACTIVE = "10000000-0000-4000-8000-000000000001";
const GUID_PAID = "10000000-0000-4000-8000-000000000002";

function summary(status: InstallmentStatus): InstallmentSummary {
  const balanceCents = status === "Active" ? 8_000 : 0;
  return {
    installmentGuid: GUID_ACTIVE,
    installmentNumber: "IP-0001",
    storeCode: "S1",
    deviceCode: "IPAD-1",
    cashierName: "Alice",
    customerName: "Bob",
    customerPhone: "0400000000",
    createdAtIso: "2026-07-27T01:02:03.000Z",
    totalCents: 10_000,
    downPaymentCents: 2_000,
    paidCents: 10_000 - balanceCents,
    balanceCents,
    status,
    updatedAtIso: "2026-07-27T02:03:04.000Z",
  };
}

function summaryAt(index: number): InstallmentSummary {
  const suffix = String(index + 1).padStart(12, "0");
  return {
    ...summary("Active"),
    installmentGuid: `10000000-0000-4000-8000-${suffix}`,
    installmentNumber: `IP-${String(index + 1).padStart(4, "0")}`,
  };
}

function details(status: InstallmentStatus): InstallmentDetails {
  return {
    ...summary(status),
    installmentGuid:
      status === "PaidOff" || status === "PickedUp"
        ? GUID_PAID
        : GUID_ACTIVE,
    cashierId: "C1",
    minimumDownPaymentCents: 2_000,
    lines: [],
    payments: [],
    pickupInfo: null,
    cancellationInfo: null,
    note: null,
  };
}

function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((resolvePromise) => {
    resolve = resolvePromise;
  });
  return { promise, resolve };
}
