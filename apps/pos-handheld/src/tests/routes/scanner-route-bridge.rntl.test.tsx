import { afterEach, beforeEach, expect, jest, test } from "@jest/globals";
import { act, render, waitFor } from "@testing-library/react-native";
import { AppState, TextInput, type NativeMethods } from "react-native";

import { HidScannerCapture, HidScannerRouter } from "@/core/peripherals/scanner";
import {
  RouteHidScannerCapture,
  ScannerRouteProvider,
} from "@/ui/scanner/scanner-route-bridge";

let mockRuntime: any;
let mockPathname = "/sales";
let mockIsFocused = true;
// RN 运行时无焦点返回 null，当前类型声明遗漏了这一分支。
const textInputState = TextInput.State as { currentlyFocusedInput(): NativeMethods | null };

jest.mock("expo-router", () => ({
  usePathname: () => mockPathname,
}));

jest.mock("@react-navigation/native", () => ({
  useIsFocused: () => mockIsFocused,
}));

jest.mock("@/core/runtime/pos-runtime-context", () => ({
  usePosRuntime: () => mockRuntime,
}));

beforeEach(() => {
  mockPathname = "/sales";
  mockIsFocused = true;
});

afterEach(() => {
  jest.restoreAllMocks();
  jest.useRealTimers();
});

test("隐藏扫码框失焦或 Android 弹窗返回后恢复焦点，包括 RN 仍缓存旧焦点的情况", async () => {
  jest.useFakeTimers();
  const listeners = new Map<string, (state?: string) => void>();
  jest.spyOn(AppState, "addEventListener").mockImplementation((event, listener) => {
    listeners.set(event, listener as (state?: string) => void);
    return { remove: jest.fn() };
  });
  const focusedInput = jest.spyOn(textInputState, "currentlyFocusedInput").mockReturnValue(null);
  const prototype = (TextInput as unknown as {
    prototype: { focus: jest.Mock; blur: jest.Mock };
  }).prototype;
  const screen = await render(<HidScannerCapture active scanner={new HidScannerRouter()} />);
  await act(async () => { jest.advanceTimersByTime(100); });
  const input = prototype.focus.mock.contexts.at(-1) as TextInput;
  prototype.focus.mockClear();
  const hiddenInput = screen.container.queryAll(
    (instance) => instance.props.caretHidden === true,
    { matchDeepestOnly: true },
  )[0]!;

  await act(async () => {
    hiddenInput.props.onBlur();
    jest.advanceTimersByTime(100);
  });
  expect(prototype.focus).toHaveBeenCalledTimes(1);

  // 原生窗口回来时 JS 可能仍认为隐藏框聚焦；必须重新建立原生编辑器连接。
  focusedInput.mockReturnValue(input);
  prototype.focus.mockClear();
  prototype.blur.mockClear();
  expect(listeners.has("focus")).toBe(true);
  await act(async () => {
    listeners.get("focus")!();
    jest.advanceTimersByTime(100);
  });
  expect(prototype.blur).toHaveBeenCalledTimes(1);
  expect(prototype.focus).toHaveBeenCalledTimes(1);

  prototype.focus.mockClear();
  focusedInput.mockReturnValue(null);
  await act(async () => {
    listeners.get("change")!("active");
    jest.advanceTimersByTime(100);
  });
  expect(prototype.focus).toHaveBeenCalledTimes(1);
  await screen.unmount();
});

test("焦点恢复不抢手动输入，暂停或卸载后取消恢复和系统监听", async () => {
  jest.useFakeTimers();
  const listeners = new Map<string, (state?: string) => void>();
  const remove = jest.fn();
  jest.spyOn(AppState, "addEventListener").mockImplementation((event, listener) => {
    listeners.set(event, listener as (state?: string) => void);
    return { remove };
  });
  const focusedInput = jest.spyOn(textInputState, "currentlyFocusedInput").mockReturnValue(null);
  const prototype = (TextInput as unknown as {
    prototype: { focus: jest.Mock };
  }).prototype;
  const scanner = new HidScannerRouter();
  const screen = await render(<HidScannerCapture active scanner={scanner} />);
  await act(async () => { jest.advanceTimersByTime(100); });
  prototype.focus.mockClear();
  focusedInput.mockReturnValue({} as TextInput);
  await act(async () => {
    listeners.get("focus")?.();
    jest.advanceTimersByTime(100);
  });
  expect(prototype.focus).not.toHaveBeenCalled();

  focusedInput.mockReturnValue(null);
  await act(async () => { listeners.get("focus")?.(); });
  await screen.rerender(<HidScannerCapture active={false} scanner={scanner} />);
  await act(async () => { jest.advanceTimersByTime(100); });
  expect(prototype.focus).not.toHaveBeenCalled();
  expect(remove).toHaveBeenCalledTimes(2);

  await screen.rerender(<HidScannerCapture active scanner={scanner} />);
  await screen.unmount();
  await act(async () => { jest.advanceTimersByTime(100); });
  expect(prototype.focus).not.toHaveBeenCalled();
  expect(remove).toHaveBeenCalledTimes(4);
});

test("同 pathname 的保留路由只让聚焦实例订阅，切焦点后订阅随之转移", async () => {
  const scanner = new HidScannerRouter();
  const subscribeRouted = jest.spyOn(scanner, "subscribeRouted");
  const hiddenOnScan =
    jest.fn<(value: string, source?: "hid" | "camera") => void>();
  const visibleOnScan =
    jest.fn<(value: string, source?: "hid" | "camera") => void>();
  mockRuntime = {
    services: {
      operationAuthorization: {
        status: "available",
        getState: () => ({ kind: "idle" }),
        subscribe: () => () => undefined,
      },
      scanner: { router: scanner },
    },
  };

  const route = (onScan: typeof hiddenOnScan) => (
    <ScannerRouteProvider>
      <RouteHidScannerCapture
        context="product"
        onScan={onScan}
        path="/sales"
      />
    </ScannerRouteProvider>
  );
  mockIsFocused = false;
  const hiddenScreen = await render(route(hiddenOnScan));
  mockIsFocused = true;
  const visibleScreen = await render(route(visibleOnScan));

  expect(subscribeRouted).toHaveBeenCalledTimes(1);
  scanner.setCaptureActive(true);
  scanner.acceptHidText("VISIBLE-SKU\n");
  expect(hiddenOnScan).not.toHaveBeenCalled();
  expect(visibleOnScan).toHaveBeenCalledWith("VISIBLE-SKU", "hid");

  mockIsFocused = false;
  await visibleScreen.rerender(route(visibleOnScan));
  mockIsFocused = true;
  await hiddenScreen.rerender(route(hiddenOnScan));
  await waitFor(() => {
    expect(subscribeRouted).toHaveBeenCalledTimes(2);
  });
  scanner.setCaptureActive(true);
  scanner.acceptHidText("NEXT-SKU\n");
  expect(hiddenOnScan).toHaveBeenCalledWith("NEXT-SKU", "hid");
  expect(visibleOnScan).toHaveBeenCalledTimes(1);

  await hiddenScreen.unmount();
  await visibleScreen.unmount();
});

test("路由切换和主管弹窗只把完整 HID 条码交给当前 context，并在关闭后恢复销售焦点", async () => {
  const scanner = new HidScannerRouter();
  const listeners = new Set<() => void>();
  let authorizationState: { kind: "awaiting-supervisor" | "idle" } = {
    kind: "idle",
  };
  const onScan =
    jest.fn<(value: string, source?: "hid" | "camera") => void>();
  mockRuntime = {
    services: {
      operationAuthorization: {
        status: "available",
        getState: () => authorizationState,
        subscribe: (listener: () => void) => {
          listeners.add(listener);
          return () => listeners.delete(listener);
        },
      },
      scanner: { router: scanner },
    },
  };

  const screen = await render(
    <ScannerRouteProvider>
      <RouteHidScannerCapture
        context="product"
        onScan={onScan}
        path="/sales"
      />
    </ScannerRouteProvider>,
  );

  await waitFor(() => {
    scanner.setCaptureActive(true);
    scanner.acceptHidText("SKU-1\n");
    expect(onScan).toHaveBeenLastCalledWith("SKU-1", "hid");
  });

  await act(async () => {
    authorizationState = { kind: "awaiting-supervisor" };
    listeners.forEach((listener) => listener());
  });
  scanner.setCaptureActive(true);
  scanner.acceptHidText("SUPERVISOR-1\n");
  expect(onScan).toHaveBeenCalledTimes(1);

  await act(async () => {
    authorizationState = { kind: "idle" };
    listeners.forEach((listener) => listener());
  });
  await waitFor(() => {
    scanner.setCaptureActive(true);
    scanner.acceptHidText("SKU-2\n");
    expect(onScan).toHaveBeenLastCalledWith("SKU-2", "hid");
  });

  await scanner.startCamera();
  scanner.acceptCameraText("CAMERA-SKU");
  expect(onScan).toHaveBeenLastCalledWith("CAMERA-SKU", "camera");
  await scanner.stopCamera();

  await screen.unmount();
  scanner.acceptHidText("SKU-3\n");
  expect(onScan).toHaveBeenCalledTimes(3);
});

test("同一次 HID 回车同时触发 keyPress 和 submitEditing 时只提交一次条码", async () => {
  const scanner = new HidScannerRouter();
  const onScan =
    jest.fn<(value: string, source?: "hid" | "camera") => void>();
  const onHidTextChange = jest.fn();
  mockRuntime = {
    services: {
      operationAuthorization: {
        status: "available",
        getState: () => ({ kind: "idle" }),
        subscribe: () => () => undefined,
      },
      scanner: { router: scanner },
    },
  };

  const screen = await render(
    <ScannerRouteProvider>
      <RouteHidScannerCapture
        context="product"
        onHidTextChange={onHidTextChange}
        onScan={onScan}
        path="/sales"
      />
    </ScannerRouteProvider>,
  );

  const hidInput = () => {
    const matches = screen.container.queryAll(
      (instance) => instance.props.caretHidden === true,
      { matchDeepestOnly: true },
    );
    expect(matches).toHaveLength(1);
    return matches[0]!;
  };
  scanner.setCaptureActive(true);
  await act(async () => {
    hidInput().props.onChangeText("930000000001");
  });
  expect(onHidTextChange).toHaveBeenCalledTimes(1);
  await act(async () => {
    hidInput().props.onKeyPress({
      nativeEvent: { key: "Enter" },
    });
    hidInput().props.onSubmitEditing({
      nativeEvent: { text: "930000000001" },
    });
  });

  expect(onScan).toHaveBeenCalledTimes(1);
  expect(onScan).toHaveBeenCalledWith("930000000001", "hid");

  await act(async () => {
    hidInput().props.onChangeText("930000000001");
  });
  await act(async () => {
    hidInput().props.onKeyPress({
      nativeEvent: { key: "Enter" },
    });
    hidInput().props.onSubmitEditing({
      nativeEvent: { text: "930000000001" },
    });
  });
  expect(onScan).toHaveBeenCalledTimes(2);
  await screen.unmount();
});

/** 渲染只挂一个隐藏 HID 输入框的销售路由，返回该输入框的取值函数。 */
async function renderHidRoute(
  onScan: (value: string, source?: "hid" | "camera") => void,
) {
  const scanner = new HidScannerRouter();
  mockRuntime = {
    services: {
      operationAuthorization: {
        status: "available",
        getState: () => ({ kind: "idle" }),
        subscribe: () => () => undefined,
      },
      scanner: { router: scanner },
    },
  };
  const screen = await render(
    <ScannerRouteProvider>
      <RouteHidScannerCapture context="product" onScan={onScan} path="/sales" />
    </ScannerRouteProvider>,
  );
  const hidInput = () => {
    const matches = screen.container.queryAll(
      (instance) => instance.props.caretHidden === true,
      { matchDeepestOnly: true },
    );
    expect(matches).toHaveLength(1);
    return matches[0]!;
  };
  scanner.setCaptureActive(true);
  return { hidInput, scanner, screen };
}

test("隐藏 HID 输入框是非受控的：JS 不回写 value，避免新架构下回写被原生丢弃后两边错位", async () => {
  const onScan =
    jest.fn<(value: string, source?: "hid" | "camera") => void>();
  const { hidInput, screen } = await renderHidRoute(onScan);

  await act(async () => {
    hidInput().props.onChangeText("930000000001");
  });
  expect(hidInput().props.value).toBeUndefined();
  await screen.unmount();
});

test("原生清空被丢弃、下一枪接在上一枪残留后面时，只提交新增的条码", async () => {
  const onScan =
    jest.fn<(value: string, source?: "hid" | "camera") => void>();
  const { hidInput, screen } = await renderHidRoute(onScan);

  await act(async () => {
    hidInput().props.onChangeText("930000000001");
  });
  await act(async () => {
    hidInput().props.onSubmitEditing({
      nativeEvent: { text: "930000000001" },
    });
  });
  expect(onScan).toHaveBeenLastCalledWith("930000000001", "hid");

  // 清空命令被原生按事件计数丢弃：原生里仍是上一枪，下一枪逐字追加在后面。
  for (const typed of ["9300000000019", "93000000000193", "930000000001930000000002"]) {
    await act(async () => {
      hidInput().props.onChangeText(typed);
    });
  }
  await act(async () => {
    hidInput().props.onKeyPress({ nativeEvent: { key: "Enter" } });
    hidInput().props.onSubmitEditing({
      nativeEvent: { text: "930000000001930000000002" },
    });
  });

  expect(onScan).toHaveBeenCalledTimes(2);
  expect(onScan).toHaveBeenLastCalledWith("930000000002", "hid");
  await screen.unmount();
});

test("回车时 JS 还没处理完最后几个字符，也按原生回传的全文提交完整条码", async () => {
  const onScan =
    jest.fn<(value: string, source?: "hid" | "camera") => void>();
  const { hidInput, screen } = await renderHidRoute(onScan);

  await act(async () => {
    hidInput().props.onChangeText("93000");
  });
  await act(async () => {
    hidInput().props.onKeyPress({ nativeEvent: { key: "Enter" } });
    hidInput().props.onSubmitEditing({
      nativeEvent: { text: "930000000003" },
    });
  });

  expect(onScan).toHaveBeenCalledTimes(1);
  expect(onScan).toHaveBeenCalledWith("930000000003", "hid");
  await screen.unmount();
});

test("扫码器不带回车：停顿自动提交后原生清空被丢弃，下一枪仍只提交新增部分", async () => {
  jest.useFakeTimers();
  const onScan =
    jest.fn<(value: string, source?: "hid" | "camera") => void>();
  const { hidInput, screen } = await renderHidRoute(onScan);

  // DataWedge 整串注入、不带回车：停顿后按扫码处理。
  await act(async () => {
    hidInput().props.onChangeText("930000000005");
  });
  await act(async () => {
    jest.advanceTimersByTime(200);
  });
  expect(onScan).toHaveBeenCalledTimes(1);
  expect(onScan).toHaveBeenLastCalledWith("930000000005", "hid");

  // 原生清空没生效，下一枪整串追加在残留后面。
  await act(async () => {
    hidInput().props.onChangeText("930000000005930000000006");
  });
  await act(async () => {
    jest.advanceTimersByTime(200);
  });
  expect(onScan).toHaveBeenCalledTimes(2);
  expect(onScan).toHaveBeenLastCalledWith("930000000006", "hid");
  await screen.unmount();
});

test("没有 submitEditing 时 keyPress 回车在 120ms 后兜底提交，且与随后的回车不重复", async () => {
  jest.useFakeTimers();
  const onScan =
    jest.fn<(value: string, source?: "hid" | "camera") => void>();
  const { hidInput, screen } = await renderHidRoute(onScan);

  await act(async () => {
    hidInput().props.onChangeText("930000000007");
    hidInput().props.onKeyPress({ nativeEvent: { key: "Enter" } });
  });
  expect(onScan).not.toHaveBeenCalled();
  await act(async () => {
    jest.advanceTimersByTime(120);
  });
  expect(onScan).toHaveBeenCalledTimes(1);
  expect(onScan).toHaveBeenCalledWith("930000000007", "hid");

  // 迟到的 submitEditing 带同一段原生全文：已整体消费，不重放。
  await act(async () => {
    hidInput().props.onSubmitEditing({
      nativeEvent: { text: "930000000007" },
    });
  });
  expect(onScan).toHaveBeenCalledTimes(1);
  await screen.unmount();
});

test("DataWedge 整段写入：清空成功（选区回到 0）后，以上一枪开头的新码整段提交，不截尾", async () => {
  jest.useFakeTimers();
  const onScan =
    jest.fn<(value: string, source?: "hid" | "camera") => void>();
  const { hidInput, screen } = await renderHidRoute(onScan);

  await act(async () => {
    hidInput().props.onChangeText("G0930");
  });
  await act(async () => {
    jest.advanceTimersByTime(200);
  });
  expect(onScan).toHaveBeenLastCalledWith("G0930", "hid");

  // 安卓 JS 清空成功不回传文本事件，只派发选区 (0,0)；随后 DataWedge 一次写入整个新码。
  await act(async () => {
    hidInput().props.onSelectionChange({ nativeEvent: { selection: { start: 0, end: 0 } } });
  });
  await act(async () => {
    hidInput().props.onChangeText("G093093");
  });
  await act(async () => {
    jest.advanceTimersByTime(200);
  });
  expect(onScan).toHaveBeenCalledTimes(2);
  expect(onScan).toHaveBeenLastCalledWith("G093093", "hid");
  await screen.unmount();
});

test("DataWedge 整段写入：清空被丢弃（没有选区 0）时仍只提交新增部分；输入开始后的选区 0 不算清空确认", async () => {
  jest.useFakeTimers();
  const onScan =
    jest.fn<(value: string, source?: "hid" | "camera") => void>();
  const { hidInput, screen } = await renderHidRoute(onScan);

  await act(async () => {
    hidInput().props.onChangeText("G0930");
  });
  await act(async () => {
    jest.advanceTimersByTime(200);
  });
  // 清空被丢弃：原生里仍是上一枪，新码整段追加在后面。
  await act(async () => {
    hidInput().props.onChangeText("G0930G093093");
  });
  // 输入已开始后才来的选区 (0,0)（如光标被移动）不能把已消费前缀清掉。
  await act(async () => {
    hidInput().props.onSelectionChange({ nativeEvent: { selection: { start: 0, end: 0 } } });
  });
  await act(async () => {
    jest.advanceTimersByTime(200);
  });
  expect(onScan).toHaveBeenCalledTimes(2);
  expect(onScan).toHaveBeenLastCalledWith("G093093", "hid");
  await screen.unmount();
});
