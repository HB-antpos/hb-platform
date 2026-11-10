import { beforeEach, describe, expect, it, jest } from "@jest/globals";
import { fireEvent, render } from "@testing-library/react-native";
import { StyleSheet } from "react-native";

import {
  ATTENDANCE_AUDIT_MIN_TOUCH_TARGET,
  AttendanceAuditScreen,
  type AttendanceAuditScreenPresenter,
  type AttendanceAuditPresenterState,
} from "./index";

let mockLanguage: "en" | "zh" = "zh";

jest.mock("react-i18next", () => ({
  useTranslation: () => ({
    i18n: { language: mockLanguage, resolvedLanguage: mockLanguage },
    // 页面外壳的顶栏用全局 t() 取状态文案，这里原样返回键即可。
    t: (key: string) => key,
  }),
}));

describe("AttendanceAuditScreen", () => {
  beforeEach(() => {
    mockLanguage = "zh";
  });

  it("横屏显示短时 QR、倒计时和审计主从区，触控目标至少 44pt", async () => {
    const presenter = new ScreenPresenter();
    const onBack = jest.fn();
    const screen = await render(
      <AttendanceAuditScreen onBack={onBack} presenter={presenter} />,
    );

    expect(presenter.startCalls).toBe(1);
    expect(screen.getByText("考勤与审计")).toBeTruthy();
    expect(
      StyleSheet.flatten(
        screen.getByTestId("attendance-audit-workspace").props.style,
      ).flexDirection,
    ).toBe("row");
    expect(
      screen.getByTestId("attendance-qr-image").props.source.uri,
    ).toBe("data:image/png;base64,QQ==");
    expect(screen.getByText("15 秒")).toBeTruthy();
    expect(screen.queryByText(/HBATE1/)).toBeNull();
    const keyboardScroll = screen.getByTestId(
      "attendance-audit-filters-keyboard-scroll",
    );
    expect(keyboardScroll.props.automaticallyAdjustKeyboardInsets).toBe(true);
    expect(keyboardScroll.props.keyboardDismissMode).toBe("interactive");
    expect(keyboardScroll.props.keyboardShouldPersistTaps).toBe("handled");

    for (const testID of [
      "attendance-audit-back",
      "attendance-qr-refresh",
      "audit-source-local",
      "audit-source-remote",
      "audit-search-submit",
    ]) {
      expect(
        StyleSheet.flatten(screen.getByTestId(testID).props.style)
          .minHeight,
      ).toBeGreaterThanOrEqual(ATTENDANCE_AUDIT_MIN_TOUCH_TARGET);
    }

    await fireEvent.press(screen.getByTestId("attendance-audit-back"));
    expect(onBack).toHaveBeenCalledTimes(1);
    await fireEvent.press(screen.getByTestId("attendance-qr-refresh"));
    expect(presenter.refreshCalls).toBe(1);
    await fireEvent.press(screen.getByTestId("audit-source-remote"));
    expect(presenter.sources).toEqual(["remote"]);
    await screen.unmount();
  });

  it("按当前语言只渲染一种审计文案，旧双语字符串不会出现", async () => {
    const chinese = await render(
      <AttendanceAuditScreen presenter={new ScreenPresenter()} />,
    );
    expect(chinese.getByText("考勤与审计")).toBeTruthy();
    expect(chinese.queryByText("Attendance & audit")).toBeNull();
    expect(chinese.queryByText("考勤与审计 / Attendance & audit")).toBeNull();
    await chinese.unmount();

    mockLanguage = "en";
    const english = await render(
      <AttendanceAuditScreen presenter={new ScreenPresenter()} />,
    );
    expect(english.getByText("Attendance & audit")).toBeTruthy();
    expect(english.queryByText("考勤与审计")).toBeNull();
    expect(english.queryByText("考勤与审计 / Attendance & audit")).toBeNull();
    await english.unmount();
  });

  it("可信时间回拨锁存时移除 QR 并显示必须在线重同步的安全提示", async () => {
    const presenter = new ScreenPresenter({
      qr: {
        kind: "clock-invalid",
        qrImageUri: null,
        requiresOnlineResync: true,
        secondsRemaining: 0,
        statusCode: "clock-rollback",
      },
    });
    const screen = await render(
      <AttendanceAuditScreen presenter={presenter} />,
    );

    expect(screen.queryByTestId("attendance-qr-image")).toBeNull();
    expect(screen.getByTestId("attendance-clock-lock")).toBeTruthy();
    expect(screen.getByText("时钟回拨")).toBeTruthy();
    expect(screen.getByText(/在线重新同步可信时间/)).toBeTruthy();
    await screen.unmount();
  });

  it("没有 Audit.View 仅保护审计区，不阻止考勤 QR", async () => {
    const presenter = new ScreenPresenter({
      audit: {
        access: { canView: false },
        kind: "unauthorized",
        statusCode: "permission-required",
      },
    });
    const screen = await render(
      <AttendanceAuditScreen presenter={presenter} />,
    );

    expect(screen.getByTestId("attendance-qr-image")).toBeTruthy();
    expect(screen.getByTestId("audit-permission-required")).toBeTruthy();
    expect(screen.queryByTestId("audit-search-submit")).toBeNull();
    await screen.unmount();
  });

  it("审计筛选、选择与详情只展示 presenter 的脱敏字段", async () => {
    const presenter = new ScreenPresenter();
    const screen = await render(
      <AttendanceAuditScreen presenter={presenter} />,
    );

    await fireEvent.changeText(
      screen.getByTestId("audit-search-input"),
      "receipt-1",
    );
    await fireEvent.press(screen.getByTestId("audit-upload-pending"));
    await fireEvent.press(screen.getByTestId("audit-search-submit"));
    await fireEvent.press(
      screen.getByTestId("audit-row-11111111-1111-4111-8111-111111111111"),
    );

    expect(presenter.queries).toEqual(["receipt-1"]);
    expect(presenter.uploadStates).toEqual(["pending"]);
    expect(presenter.loadCalls).toBe(1);
    expect(presenter.selected).toEqual([
      "11111111-1111-4111-8111-111111111111",
    ]);
    expect(
      screen.getAllByText("Bearer [REDACTED_TOKEN]").length,
    ).toBeGreaterThan(0);
    expect(screen.queryByText(/very-secret-value/)).toBeNull();
    await screen.unmount();
  });

  it("新布局：二维码 210 见方且白底、倒计时 40 号字、列表行高至少 84、安全刷新 64 高", async () => {
    const screen = await render(
      <AttendanceAuditScreen presenter={new ScreenPresenter()} />,
    );

    const qrImage = screen.getByTestId("attendance-qr-image");
    const qrStyle = StyleSheet.flatten(qrImage.props.style);
    expect(qrStyle.width).toBe(210);
    expect(qrStyle.height).toBe(210);
    // 二维码外框固定白底，不跟随主题。
    expect(
      StyleSheet.flatten(qrImage.parent!.props.style).backgroundColor,
    ).toBe("#FFFFFF");
    expect(StyleSheet.flatten(screen.getByText("15 秒").props.style).fontSize)
      .toBe(40);
    expect(
      StyleSheet.flatten(screen.getByTestId("attendance-qr-refresh").props.style)
        .minHeight,
    ).toBe(64);
    expect(
      StyleSheet.flatten(
        screen.getByTestId("audit-row-11111111-1111-4111-8111-111111111111")
          .props.style,
      ).minHeight,
    ).toBeGreaterThanOrEqual(84);
    // 列表第二行带「小票」前缀；详情里 8 个字段都在。
    expect(screen.getByText("小票 receipt-1")).toBeTruthy();
    for (const label of [
      "操作",
      "结果",
      "时间",
      "收银员",
      "小票",
      "订单",
      "金额",
      "关联",
    ]) {
      expect(screen.getByText(label)).toBeTruthy();
    }
    expect(screen.getByText("#1 · 数量 1 · 金额 $1.00")).toBeTruthy();
    await screen.unmount();
  });

  it("来源与上传状态是两组分段：当前项高亮，离线时远程不可点", async () => {
    const presenter = new ScreenPresenter({
      audit: { online: false, uploadState: "uploaded" },
    });
    const screen = await render(
      <AttendanceAuditScreen presenter={presenter} />,
    );

    expect(
      screen.getByTestId("audit-source-local").props.accessibilityState.checked,
    ).toBe(true);
    const remote = screen.getByTestId("audit-source-remote");
    expect(remote.props.accessibilityState.disabled).toBe(true);
    await fireEvent.press(remote);
    expect(presenter.sources).toEqual([]);

    expect(
      screen.getByTestId("audit-upload-uploaded").props.accessibilityState
        .checked,
    ).toBe(true);
    expect(
      screen.getByTestId("audit-upload-all").props.accessibilityState.checked,
    ).toBe(false);
    // 「全部」对应 presenter 的 null。
    await fireEvent.press(screen.getByTestId("audit-upload-all"));
    expect(presenter.uploadStates).toEqual([null]);
    for (const testID of [
      "audit-upload-all",
      "audit-upload-pending",
      "audit-upload-uploaded",
      "audit-upload-rejected",
    ]) {
      expect(
        StyleSheet.flatten(screen.getByTestId(testID).props.style).minHeight,
      ).toBeGreaterThanOrEqual(56);
    }
    await screen.unmount();
  });
});

class ScreenPresenter implements AttendanceAuditScreenPresenter {
  public startCalls = 0;
  public refreshCalls = 0;
  public loadCalls = 0;
  public readonly queries: string[] = [];
  public readonly sources: string[] = [];
  public readonly uploadStates: (string | null)[] = [];
  public readonly selected: string[] = [];
  private readonly listeners = new Set<() => void>();
  private state: AttendanceAuditPresenterState;

  public constructor(
    options: Partial<{
      audit: Partial<AttendanceAuditPresenterState["audit"]>;
      qr: Partial<AttendanceAuditPresenterState["qr"]>;
    }> = {},
  ) {
    this.state = {
      audit: {
        access: { canView: true },
        detail: auditRecord(),
        detailLoading: false,
        kind: "ready",
        online: true,
        query: "",
        rows: [auditRecord()],
        selectedEventId: auditRecord().eventId,
        source: "local",
        statusCode: null,
        uploadState: null,
        ...options.audit,
      },
      qr: {
        deviceText: "IPAD-1",
        kind: "ready",
        online: true,
        qrImageUri: "data:image/png;base64,QQ==",
        requiresOnlineResync: false,
        secondsRemaining: 15,
        statusCode: "online-verified",
        storeText: "Store One (S1)",
        ...options.qr,
      },
    };
  }

  public readonly getState = () => this.state;
  public readonly subscribe = (listener: () => void) => {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  };
  public start(): void {
    this.startCalls += 1;
  }
  public async refreshAttendanceQr(): Promise<void> {
    this.refreshCalls += 1;
  }
  public async loadAudit(): Promise<void> {
    this.loadCalls += 1;
  }
  public async selectAudit(eventId: string): Promise<void> {
    this.selected.push(eventId);
  }
  public setAuditQuery(value: string): void {
    this.queries.push(value);
  }
  public setAuditSource(value: "local" | "remote"): void {
    this.sources.push(value);
  }
  public setAuditUploadState(value: "pending" | "uploaded" | "rejected" | null): void {
    this.uploadStates.push(value);
  }
}

function auditRecord() {
  return {
    actualAmountDeltaCents: undefined,
    cashierName: "Bearer [REDACTED_TOKEN]",
    correlationId: "corr-1",
    deviceCode: "IPAD-1",
    eventId: "11111111-1111-4111-8111-111111111111",
    items: [
      {
        actualAmountDeltaCents: 100,
        displayName: "Product A",
        lineIndex: 0,
        productCode: "A",
        quantityDelta: "1",
      },
    ],
    occurredAtIso: "2026-07-28T08:00:00.000Z",
    operationType: "PriceOverride",
    orderGuid: "22222222-2222-4222-8222-222222222222",
    outcome: "Succeeded",
    paymentAmountCents: 1_234,
    primaryProduct: "Product A",
    productCount: 1,
    receiptNumber: "receipt-1",
    safeMessage: "Bearer [REDACTED_TOKEN]",
    storeCode: "S1",
    uploadState: "pending" as const,
  };
}
