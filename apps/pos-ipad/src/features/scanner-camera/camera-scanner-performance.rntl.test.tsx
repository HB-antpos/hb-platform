/* eslint-disable @typescript-eslint/no-require-imports -- Jest runtime mock must load before the components. */
import { expect, jest, test } from "@jest/globals";
import { render } from "@testing-library/react-native";
import { StyleSheet } from "react-native";

jest.doMock("react-i18next", () => ({
  useTranslation: () => ({
    i18n: { language: "en", resolvedLanguage: "en" },
  }),
}));

jest.doMock("expo-camera", () => {
  const React = require("react");
  const { View } = require("react-native");
  const CameraView = (props: Record<string, unknown>) =>
    React.createElement(View, { ...props, testID: "camera-scanner-preview" });
  CameraView.isAvailableAsync = jest
    .fn<() => Promise<boolean>>()
    .mockResolvedValue(true);
  return {
    CameraView,
    useCameraPermissions: jest.fn(() => [
      { granted: true, status: "granted" },
      jest.fn(),
    ]),
  };
});

const { CameraScannerModal } = require("./camera-scanner-modal") as typeof import("./camera-scanner-modal");
const { CameraScannerInline } = require("./camera-scanner-inline") as typeof import("./camera-scanner-inline");
const { applyPosTheme } = require("@/ui/theme") as typeof import("@/ui/theme");
type CameraScannerPort = import("./camera-scanner-session").CameraScannerPort;

test("单次与连续相机启用 ean13、code128 和 qr", async () => {
  const modal = await render(
    <CameraScannerModal
      context="product"
      onClose={jest.fn()}
      onScan={jest.fn()}
      scanner={new CameraScannerPortStub()}
      visible
    />,
  );
  const inline = await render(
    <CameraScannerInline
      context="product"
      onClose={jest.fn()}
      onScan={jest.fn(async () => true)}
      scanner={new CameraScannerPortStub()}
      visible
    />,
  );

  const expectedSettings = { barcodeTypes: ["ean13", "code128", "qr"] };
  expect((await modal.findByTestId("camera-scanner-preview")).props.barcodeScannerSettings).toEqual(expectedSettings);
  expect((await inline.findByTestId("camera-scanner-preview")).props.barcodeScannerSettings).toEqual(expectedSettings);

  await modal.unmount();
  await inline.unmount();
});

test("单次与连续相机均显示横向窄取景框", async () => {
  const modal = await render(
    <CameraScannerModal
      context="product"
      onClose={jest.fn()}
      onScan={jest.fn()}
      scanner={new CameraScannerPortStub()}
      visible
    />,
  );
  const inline = await render(
    <CameraScannerInline
      context="product"
      onClose={jest.fn()}
      onScan={jest.fn(async () => true)}
      scanner={new CameraScannerPortStub()}
      visible
    />,
  );

  await modal.findByTestId("camera-scanner-preview");
  await inline.findByTestId("camera-scanner-preview");
  expectHorizontalNarrowTarget(findViewfinder(modal.toJSON()));
  expectHorizontalNarrowTarget(findViewfinder(inline.toJSON()));

  await modal.unmount();
  await inline.unmount();
});

test("关闭按钮不小于 56 高；换成深色主题后对准框仍是白色", async () => {
  applyPosTheme("dark");
  try {
    const modal = await render(
      <CameraScannerModal
        context="product"
        onClose={jest.fn()}
        onScan={jest.fn()}
        scanner={new CameraScannerPortStub()}
        visible
      />,
    );
    const inline = await render(
      <CameraScannerInline
        context="product"
        onClose={jest.fn()}
        onScan={jest.fn(async () => true)}
        scanner={new CameraScannerPortStub()}
        visible
      />,
    );

    await modal.findByTestId("camera-scanner-preview");
    await inline.findByTestId("camera-scanner-preview");
    for (const button of [
      modal.getByTestId("camera-scanner-close"),
      inline.getByTestId("camera-scanner-inline-close"),
    ]) {
      expect(
        StyleSheet.flatten(button.props.style).minHeight,
      ).toBeGreaterThanOrEqual(56);
    }
    // 对准框压在实时画面上，不能跟着主题变成深色。
    for (const tree of [modal.toJSON(), inline.toJSON()]) {
      expect(findViewfinder(tree).props.style).toEqual(
        expect.objectContaining({ borderColor: "#FFFFFF" }),
      );
    }

    await modal.unmount();
    await inline.unmount();
  } finally {
    applyPosTheme("classic");
  }
});

class CameraScannerPortStub implements CameraScannerPort {
  public acceptCameraText(): boolean {
    return true;
  }

  public async startCamera(): Promise<void> {}

  public async stopCamera(): Promise<void> {}
}

function expectHorizontalNarrowTarget(target: { props: { style: unknown } }) {
  const style = target.props.style as Record<string, unknown>;
  const top = percentage(style.top);
  const bottom = percentage(style.bottom);
  const left = percentage(style.left);
  const right = percentage(style.right);

  expect(style).toEqual(expect.objectContaining({ borderWidth: 2 }));
  expect(top).toBe(bottom);
  expect(left).toBe(right);
  expect(100 - top - bottom).toBeLessThanOrEqual(40);
  expect(100 - left - right).toBeGreaterThanOrEqual(70);
}

function percentage(value: unknown): number {
  expect(value).toMatch(/^\d+%$/);
  return Number.parseInt(value as string, 10);
}

function findViewfinder(tree: unknown): { props: { style: unknown } } {
  if (Array.isArray(tree)) {
    for (const child of tree) {
      try {
        return findViewfinder(child);
      } catch {
        // 继续查找兄弟节点，直到定位不可交互的取景框。
      }
    }
  }
  if (
    tree &&
    typeof tree === "object" &&
    "props" in tree &&
    (tree as { props?: { pointerEvents?: unknown } }).props?.pointerEvents ===
      "none"
  ) {
    return tree as { props: { style: unknown } };
  }
  if (tree && typeof tree === "object" && "children" in tree) {
    return findViewfinder((tree as { children?: unknown }).children);
  }
  throw new Error("未找到相机取景框");
}
