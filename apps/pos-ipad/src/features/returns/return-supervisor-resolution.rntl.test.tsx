import { expect, jest, test } from "@jest/globals";
import { fireEvent, render, waitFor } from "@testing-library/react-native";

import type { ReceiptReturnContext } from "@hb/pos-domain/features/returns/return-domain";
import { ReturnPresenter } from "@hb/pos-domain/features/returns/return-presenter";
import {
  ReturnWorkflow,
  type ReturnExecutionCommand,
  type ReturnExecutionOutcome,
  type ReturnExecutionPort,
  type ReturnSupervisorResolutionCommand,
} from "@hb/pos-domain/features/returns/return-workflow";
import { ReturnScreen } from "./return-screen";

jest.mock("react-i18next", () => ({
  useTranslation: () => ({
    i18n: { language: "en", resolvedLanguage: "en" },
  }),
}));

class Execution implements ReturnExecutionPort {
  public readonly resolveCalls: ReturnSupervisorResolutionCommand[] = [];
  public resolveOutcome: ReturnExecutionOutcome = { status: "declined" };
  public async execute(_command: ReturnExecutionCommand): Promise<ReturnExecutionOutcome> {
    return { status: "unknown", recoveryKey: "key" };
  }
  public async recover(): Promise<ReturnExecutionOutcome> {
    return { status: "unknown", recoveryKey: "key" };
  }
  public async resolveUnknown(command: ReturnSupervisorResolutionCommand): Promise<ReturnExecutionOutcome> {
    this.resolveCalls.push(command);
    return this.resolveOutcome;
  }
}

function receiptContext(): ReceiptReturnContext {
  return {
    originalOrderGuid: "order-a",
    receiptLabel: "HB-1001",
    loadedFrom: "remote",
    returnRecordsMayBeStale: false,
    lines: [{
      selectionKey: "detail-a",
      originalOrderGuid: "order-a",
      originalOrderDetailGuid: "detail-a",
      returnSourceKey: "return:order-a:detail-a",
      productCode: "P-1",
      itemNumber: "1001",
      lookupCode: "1001",
      displayName: "Blue cup",
      availableQuantity: 2,
      unitRefundCents: 1_000,
      remainingAmountCents: 2_000,
      syncProvenance: { referenceCode: "RECEIPT-REF", priceSource: 0 },
    }],
    tenderCapacities: [{
      capacityId: "card-capacity",
      originalOrderGuid: "order-a",
      method: "card",
      remainingCents: 2_000,
      offlineCashProof: null,
    }],
  };
}

function createPresenter(execution: Execution, withSupervisor: boolean): ReturnPresenter {
  const workflow = new ReturnWorkflow({
    lookup: {
      lookupReceipt: async () => receiptContext(),
      lookupNoReceiptProduct: async () => null,
      createNoReceiptOpenItem: async () => null,
    },
    connectivity: { isOnline: async () => true },
    supervisorAuthorization: {
      authorizeNoReceiptReturn: async () => ({ authorizationKey: "grant" }),
      ...(withSupervisor
        ? {
            authorizeUnknownResolution: async () => ({
              authorizationId: "auth-1",
              supervisorActor: { cashierId: "supervisor-1", cashierName: "Supervisor", userGuid: null },
              requestingActor: { cashierId: "cashier-1", cashierName: "Cashier", userGuid: null },
            }),
          }
        : {}),
    },
    sessionGuard: { captureLease: () => "lease-1", assertActive: () => undefined },
    execution,
    createActionId: () => "return-action-1",
  });
  return new ReturnPresenter(workflow);
}

async function openUnknown(presenter: ReturnPresenter) {
  const screen = await render(<ReturnScreen locale="en" presenter={presenter} />);
  await fireEvent.changeText(screen.getByTestId("return-order-query"), "HB-1001");
  await fireEvent.press(screen.getByTestId("return-order-search"));
  await waitFor(() => expect(screen.getByTestId("return-row-return-line-1")).toBeTruthy());
  await fireEvent.press(screen.getByTestId("return-increase-return-line-1"));
  await fireEvent.press(screen.getByTestId("return-method-card"));
  await fireEvent.press(screen.getByTestId("return-confirm"));
  await waitFor(() => expect(screen.getByTestId("return-unknown")).toBeTruthy());
  return screen;
}

test("H9：退款未知页提供主管结案；缺凭据时按钮禁用，确认未退款后解除锁定", async () => {
  const execution = new Execution();
  const presenter = createPresenter(execution, true);
  const screen = await openUnknown(presenter);

  expect(screen.getByTestId("return-resolution")).toBeTruthy();
  expect(screen.getByTestId("return-unknown-action")).toBeTruthy();
  expect(screen.getByTestId("return-resolution-not-refunded").props.accessibilityState.disabled).toBe(true);
  expect(screen.getByTestId("return-resolution-keep-waiting").props.accessibilityState.disabled).toBe(true);

  await fireEvent.changeText(screen.getByTestId("return-resolution-evidence"), "terminal receipt 8841");
  await fireEvent.changeText(screen.getByTestId("return-resolution-note"), "Checked the Linkly portal");
  expect(screen.getByTestId("return-resolution-not-refunded").props.accessibilityState.disabled).toBe(false);

  await fireEvent.press(screen.getByTestId("return-resolution-not-refunded"));
  await waitFor(() => expect(screen.getByTestId("return-failed")).toBeTruthy());
  expect(execution.resolveCalls).toHaveLength(1);
  expect(execution.resolveCalls[0]).toMatchObject({
    finding: "not-refunded",
    evidenceReference: "terminal receipt 8841",
    note: "Checked the Linkly portal",
  });
  expect(
    screen.getByText(/A supervisor confirmed the customer was not refunded/),
  ).toBeTruthy();
});

test("H9：继续等待只记录决定，页面保持 Unknown 锁定并给出反馈", async () => {
  const execution = new Execution();
  execution.resolveOutcome = { status: "unknown", recoveryKey: "key" };
  const presenter = createPresenter(execution, true);
  const screen = await openUnknown(presenter);
  await fireEvent.changeText(screen.getByTestId("return-resolution-evidence"), "receipt");
  await fireEvent.changeText(screen.getByTestId("return-resolution-note"), "Still checking");
  await fireEvent.press(screen.getByTestId("return-resolution-keep-waiting"));
  await waitFor(() => expect(screen.getByTestId("return-resolution-waiting-recorded")).toBeTruthy());
  expect(screen.getByTestId("return-unknown")).toBeTruthy();
  expect(execution.resolveCalls[0]?.finding).toBe("keep-waiting");
});

test("H9：该端未接线主管结案时不展示入口（仍只能恢复）", async () => {
  const presenter = createPresenter(new Execution(), false);
  const screen = await openUnknown(presenter);
  expect(screen.queryByTestId("return-resolution")).toBeNull();
  expect(screen.getByTestId("return-unknown-action")).toBeTruthy();
});
