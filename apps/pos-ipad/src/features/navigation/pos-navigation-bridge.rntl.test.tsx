import { beforeEach, expect, jest, test } from "@jest/globals";
import { act, render } from "@testing-library/react-native";

import { registerPosDestinationGuard } from "./pos-destination-access";
import { PosNavigationBridge } from "./pos-navigation-bridge";

import { SETTINGS_VIEW_PERMISSION } from "@/features/settings/settings-authorization";
import {
  type PosNavigationContextValue,
  usePosNavigation,
} from "@/ui/shell/pos-navigation-context";

const mockRouter = {
  dismissTo: jest.fn(),
  push: jest.fn(),
  replace: jest.fn(),
};
let mockPathname = "/sales";
let mockPermissions: readonly string[] | null = [];
const mockReadPinned = jest.fn<() => string[] | null>();
const mockSavePinned = jest.fn<(ids: readonly string[]) => Promise<void>>();
const mockToggleLanguage = jest.fn<() => Promise<"zh" | "en">>();

jest.mock("expo-router", () => ({
  usePathname: () => mockPathname,
  useRouter: () => mockRouter,
}));

jest.mock("react-i18next", () => ({
  useTranslation: () => ({
    i18n: { language: "zh", resolvedLanguage: "zh" },
    t: (key: string) => key,
  }),
}));

jest.mock("@/i18n", () => ({
  toggleAppLanguage: () => mockToggleLanguage(),
}));

jest.mock("@/core/runtime/pos-runtime-context", () => ({
  usePosRuntime: () => ({ services: null }),
}));

jest.mock("@/features/cashier-login", () => ({
  useCashierLoginStore: (
    selector: (state: {
      activeCashier: { permissions: readonly string[] } | null;
    }) => unknown,
  ) =>
    selector({
      activeCashier: mockPermissions ? { permissions: mockPermissions } : null,
    }),
}));

jest.mock("@/ui/preferences/terminal-ui-preferences", () => ({
  readPinnedDestinations: () => mockReadPinned(),
  savePinnedDestinations: (ids: readonly string[]) => mockSavePinned(ids),
}));

let captured: PosNavigationContextValue | null = null;
function Probe() {
  captured = usePosNavigation();
  return null;
}

async function renderBridge() {
  const screen = await render(
    <PosNavigationBridge>
      <Probe />
    </PosNavigationBridge>,
  );
  if (!captured) throw new Error("navigation context missing");
  return screen;
}

function entry(id: string) {
  const found = captured?.model.entries.find((candidate) => candidate.id === id);
  if (!found) throw new Error(`entry ${id} missing`);
  return found;
}

beforeEach(() => {
  jest.clearAllMocks();
  captured = null;
  mockPathname = "/sales";
  mockPermissions = [];
  mockReadPinned.mockReturnValue(null);
  mockSavePinned.mockResolvedValue(undefined);
  mockToggleLanguage.mockResolvedValue("en");
});

test("入口按当前收银员权限显示；未登录时没有任何入口", async () => {
  mockPermissions = [SETTINGS_VIEW_PERMISSION];
  const screen = await renderBridge();
  const ids = captured!.model.entries.map((candidate) => candidate.id);
  expect(ids).toContain("settings");
  expect(ids).toContain("held-orders");
  expect(ids).not.toContain("daily-close");
  expect(entry("settings").label).toBe("nav.settings");
  expect(entry("settings").description).toBe("nav.desc.settings");
  await screen.unmount();

  mockPermissions = null;
  const loggedOut = await renderBridge();
  expect(captured!.model.entries).toEqual([]);
  await loggedOut.unmount();
});

test("从收银页进入是压栈，从其他页面互相跳转是替换当前页", async () => {
  const screen = await renderBridge();
  entry("returns").onPress();
  expect(mockRouter.push).toHaveBeenCalledWith("/returns");
  expect(mockRouter.replace).not.toHaveBeenCalled();

  mockPathname = "/returns";
  await screen.rerender(
    <PosNavigationBridge>
      <Probe />
    </PosNavigationBridge>,
  );
  entry("held-orders").onPress();
  expect(mockRouter.replace).toHaveBeenCalledWith("/held-orders");
  expect(mockRouter.push).toHaveBeenCalledTimes(1);

  captured!.openSales();
  expect(mockRouter.dismissTo).toHaveBeenCalledWith("/sales");
  await screen.unmount();
});

test("进入前检查没通过时不跳转；不在收银页则先回收银页处理", async () => {
  const unregister = registerPosDestinationGuard("held-orders", () => false);
  try {
    const screen = await renderBridge();
    entry("held-orders").onPress();
    expect(mockRouter.push).not.toHaveBeenCalled();
    expect(mockRouter.dismissTo).not.toHaveBeenCalled();

    mockPathname = "/returns";
    await screen.rerender(
      <PosNavigationBridge>
        <Probe />
      </PosNavigationBridge>,
    );
    entry("held-orders").onPress();
    expect(mockRouter.replace).not.toHaveBeenCalled();
    expect(mockRouter.dismissTo).toHaveBeenCalledWith("/sales");
    await screen.unmount();
  } finally {
    unregister();
  }
});

test("侧栏固定项同步读取已保存的值，修改后先更新页面再异步保存", async () => {
  mockReadPinned.mockReturnValue(["settings", "unknown", "returns"]);
  mockSavePinned.mockRejectedValueOnce(new Error("preferences unavailable"));
  const screen = await renderBridge();
  expect(captured!.model.pinned).toEqual(["settings", "returns"]);

  await act(async () => {
    captured!.model.onPinnedChange?.(["returns"]);
    await Promise.resolve();
  });
  expect(captured!.model.pinned).toEqual(["returns"]);
  expect(mockSavePinned).toHaveBeenCalledWith(["returns"]);

  captured!.switchLanguage();
  expect(mockToggleLanguage).toHaveBeenCalledTimes(1);
  await screen.unmount();
});
