import { beforeEach, describe, expect, it, jest } from "@jest/globals";
import { act, fireEvent, render, waitFor } from "@testing-library/react-native";
import { StyleSheet } from "react-native";

import {
  resolveSpecialProductsAccess,
  SPECIAL_PRODUCTS_ADD_TO_CART_PERMISSION,
  SPECIAL_PRODUCTS_MANAGE_PERMISSION,
  SPECIAL_PRODUCTS_CATEGORY_RAIL_WIDTH,
  SPECIAL_PRODUCTS_CATEGORY_ROW_HEIGHT,
  SPECIAL_PRODUCTS_MIN_TOUCH_TARGET,
  SPECIAL_PRODUCTS_VIEW_PERMISSION,
  specialProductsDragTargetIndex,
  specialProductsGridColumns,
  SpecialProductsScreen,
  SpecialProductsUnavailableScreen,
  type SpecialProductsScreenPresenter,
  type SpecialProductsState,
} from "./index";

import { deriveSpecialProductsCategoryView } from "./special-product-categories-view";

import type {
  SpecialProductCategory,
  SpecialProductCategoryAssignment,
} from "@hb/pos-domain/core/contracts/special-product-categories";

import type { SpecialProductItem } from "@/core/contracts";

const mockPlayTouchSound = jest.fn();

jest.mock("@/ui/feedback/pos-sound-context", () => ({
  usePosSound: () => ({ play: mockPlayTouchSound }),
}));

let mockLanguage: "en" | "zh" = "zh";

jest.mock("react-i18next", () => ({
  useTranslation: () => ({
    i18n: { language: mockLanguage, resolvedLanguage: mockLanguage },
    // 页面外壳的顶栏状态条会调用 t()；这里原样返回键名即可
    t: (key: string) => key,
  }),
}));

describe("SpecialProductsScreen", () => {
  beforeEach(() => {
    mockLanguage = "zh";
    mockPlayTouchSound.mockClear();
  });

  it("离线显示本地列表并允许加购，但所有管理写操作禁用且触控至少 44pt", async () => {
    const presenter = new ScreenPresenter({
      items: [product("A"), product("B")],
      online: false,
    });
    const onBack = jest.fn();
    const screen = await render(
      <SpecialProductsScreen onBack={onBack} presenter={presenter} />,
    );

    // 卡片整卡即可加购（触屏目标），离线时管理写操作仍禁用
    const cardA = screen.getByTestId("special-product-card-A");
    expect(cardA).toBeTruthy();
    expect(screen.getByTestId("special-products-offline-note")).toBeTruthy();
    const download = screen.getByTestId("special-products-download");
    const addProduct = screen.getByTestId("special-products-add-product");
    const organize = screen.getByTestId("special-products-organize");
    for (const control of [cardA, download, addProduct, organize]) {
      expect(StyleSheet.flatten(control.props.style).minHeight).toBeGreaterThanOrEqual(
        SPECIAL_PRODUCTS_MIN_TOUCH_TARGET,
      );
    }
    expect(cardA.props.accessibilityState).toEqual({ disabled: false });
    expect(download.props.accessibilityState).toEqual({ disabled: true });
    expect(addProduct.props.accessibilityState).toEqual({ disabled: true });
    // 「整理」与其他管理操作同条件：离线时禁用，点了也进不去
    expect(organize.props.accessibilityState).toEqual({ disabled: true });
    await fireEvent.press(organize);
    expect(
      screen.queryByTestId("special-products-organize-banner"),
    ).toBeNull();
    // 日常状态下卡片底部没有 ↑ / ↓ / 取消标记（整理中掉线的禁用态见下一条用例）
    expect(screen.queryByTestId("special-products-remove-A")).toBeNull();
    expect(screen.queryByTestId("special-products-move-down-A")).toBeNull();
    // 每行 5 个卡片：卡片宽度按 5 列计算（19.2% × 5 + 4 个 10pt 间距）
    expect(
      StyleSheet.flatten(
        screen.getByTestId("special-product-card-A-shell").props.style,
      ).width,
    ).toBe("19.2%");
    // 页头四个操作同为 56 高，且不带 marginTop 偏移
    for (const testID of [
      "special-products-refresh-local",
      "special-products-download",
      "special-products-add-product",
      "special-products-organize",
    ]) {
      const style = StyleSheet.flatten(screen.getByTestId(testID).props.style);
      expect(style.minHeight).toBe(56);
      expect(style.marginTop).toBeUndefined();
    }

    await fireEvent.press(cardA);
    expect(presenter.addToCartCalls).toEqual(["A"]);
    await fireEvent.press(download);
    expect(presenter.downloadCalls).toBe(0);

    const back = screen.getByTestId("special-products-back");
    expect(StyleSheet.flatten(back.props.style).minHeight).toBeGreaterThanOrEqual(
      SPECIAL_PRODUCTS_MIN_TOUCH_TARGET,
    );
    await fireEvent.press(back);
    expect(onBack).toHaveBeenCalledTimes(1);
    await screen.unmount();
  });

  it("整理中掉线：↑ / ↓ / 取消标记禁用且至少 44pt，卡片不加购，但仍可退出整理", async () => {
    const presenter = new ScreenPresenter({
      items: [product("A"), product("B")],
      online: true,
    });
    const screen = await render(
      <SpecialProductsScreen presenter={presenter} />,
    );

    await fireEvent.press(screen.getByTestId("special-products-organize"));
    await act(async () => {
      presenter.setOnline(false);
    });

    expect(screen.getByTestId("special-products-offline-note")).toBeTruthy();
    const remove = screen.getByTestId("special-products-remove-A");
    const moveDown = screen.getByTestId("special-products-move-down-A");
    for (const control of [remove, moveDown]) {
      expect(StyleSheet.flatten(control.props.style).minHeight).toBeGreaterThanOrEqual(
        SPECIAL_PRODUCTS_MIN_TOUCH_TARGET,
      );
    }
    expect(remove.props.accessibilityState).toEqual({ disabled: true });
    expect(moveDown.props.accessibilityState).toEqual({ disabled: true });
    await fireEvent.press(remove);
    await fireEvent.press(moveDown);
    expect(presenter.markCalls).toEqual([]);
    expect(presenter.reorderCalls).toEqual([]);

    // 仍在整理状态：卡片既不能拖也不加购
    const cardA = screen.getByTestId("special-product-card-A");
    expect(cardA.props.accessibilityState).toEqual({ disabled: true });
    await fireEvent.press(cardA);
    expect(presenter.addToCartCalls).toEqual([]);

    // 「完成整理」不随离线禁用，退出后恢复离线加购
    const done = screen.getByTestId("special-products-organize");
    expect(done.props.accessibilityState).toEqual({ disabled: false });
    await fireEvent.press(done);
    expect(
      screen.queryByTestId("special-products-organize-banner"),
    ).toBeNull();
    expect(screen.queryByTestId("special-products-remove-A")).toBeNull();
    await fireEvent.press(screen.getByTestId("special-product-card-A"));
    expect(presenter.addToCartCalls).toEqual(["A"]);
    await screen.unmount();
  });

  it("默认不在整理状态：不显示管理按钮，点卡片加购，长按不会抬起卡片", async () => {
    const presenter = new ScreenPresenter({
      items: [product("A"), product("B")],
      online: true,
    });
    const screen = await render(
      <SpecialProductsScreen presenter={presenter} />,
    );

    expect(screen.getByText("整理")).toBeTruthy();
    expect(screen.queryByText("完成整理")).toBeNull();
    expect(
      screen.queryByTestId("special-products-organize-banner"),
    ).toBeNull();
    for (const code of ["A", "B"]) {
      expect(screen.queryByTestId(`special-products-move-up-${code}`)).toBeNull();
      expect(screen.queryByTestId(`special-products-move-down-${code}`)).toBeNull();
      expect(screen.queryByTestId(`special-products-remove-${code}`)).toBeNull();
    }
    expect(screen.getByText("点商品卡片 = 加入购物车并返回收银")).toBeTruthy();
    // 价格放大到 21 号
    expect(
      StyleSheet.flatten(screen.getAllByText("$12.50")[0]?.props.style).fontSize,
    ).toBe(21);

    const cardA = screen.getByTestId("special-product-card-A");
    // 有管理权限且在线，但没进整理：长按拖动不启用
    expect(cardA.props.onLongPress).toBeUndefined();
    await fireEvent.press(cardA);
    expect(presenter.addToCartCalls).toEqual(["A"]);
    expect(mockPlayTouchSound).toHaveBeenCalledWith("tap");
    await screen.unmount();
  });

  it("进入整理后显示管理按钮和提示条，点卡片不加购；完成整理后恢复", async () => {
    const presenter = new ScreenPresenter({
      items: [product("A"), product("B")],
      online: true,
    });
    const screen = await render(
      <SpecialProductsScreen presenter={presenter} />,
    );

    await fireEvent.press(screen.getByTestId("special-products-organize"));

    // 页头按钮变成「完成整理」，顶部出现黄色提示条
    expect(screen.getByText("完成整理")).toBeTruthy();
    expect(screen.queryByText("整理")).toBeNull();
    expect(
      screen.getByTestId("special-products-organize-banner"),
    ).toBeTruthy();
    expect(
      screen.getByText(
        "整理模式：用 ↑ ↓ 或长按拖动调整顺序，「取消标记」把商品移出特殊商品。此时点卡片不会加购。",
      ),
    ).toBeTruthy();
    expect(screen.queryByText("点商品卡片 = 加入购物车并返回收银")).toBeNull();

    // 每张卡片底部出现 ↑ / ↓ / 取消标记；第一张不能上移、最后一张不能下移
    const moveUpA = screen.getByTestId("special-products-move-up-A");
    const moveDownA = screen.getByTestId("special-products-move-down-A");
    const removeA = screen.getByTestId("special-products-remove-A");
    for (const control of [moveUpA, moveDownA, removeA]) {
      expect(StyleSheet.flatten(control.props.style).minHeight).toBeGreaterThanOrEqual(46);
    }
    expect(moveUpA.props.accessibilityState).toEqual({ disabled: true });
    expect(moveDownA.props.accessibilityState).toEqual({ disabled: false });
    expect(removeA.props.accessibilityState).toEqual({ disabled: false });
    expect(
      screen.getByTestId("special-products-move-up-B").props.accessibilityState,
    ).toEqual({ disabled: false });
    expect(
      screen.getByTestId("special-products-move-down-B").props
        .accessibilityState,
    ).toEqual({ disabled: true });
    expect(screen.getAllByText("取消标记")).toHaveLength(2);

    // 整理时点卡片不加购，也不发加购的触控音
    mockPlayTouchSound.mockClear();
    await fireEvent.press(screen.getByTestId("special-product-card-A"));
    expect(presenter.addToCartCalls).toEqual([]);
    expect(mockPlayTouchSound).not.toHaveBeenCalled();

    // 管理按钮照常工作
    await fireEvent.press(moveDownA);
    expect(presenter.reorderCalls).toEqual([{ delta: 1, productCode: "A" }]);
    await fireEvent.press(
      screen.getByTestId("special-products-move-up-B"),
    );
    expect(presenter.reorderCalls).toEqual([
      { delta: 1, productCode: "A" },
      { delta: -1, productCode: "B" },
    ]);
    await fireEvent.press(removeA);
    expect(presenter.markCalls).toEqual([
      { isSpecialProduct: false, productCode: "A" },
    ]);
    expect(mockPlayTouchSound).toHaveBeenLastCalledWith("danger");
    expect(presenter.addToCartCalls).toEqual([]);

    // 完成整理：管理按钮与提示条消失，点卡片恢复加购
    await fireEvent.press(screen.getByTestId("special-products-organize"));
    expect(screen.getByText("整理")).toBeTruthy();
    expect(
      screen.queryByTestId("special-products-organize-banner"),
    ).toBeNull();
    expect(screen.queryByTestId("special-products-move-up-A")).toBeNull();
    expect(screen.queryByTestId("special-products-move-down-A")).toBeNull();
    expect(screen.queryByTestId("special-products-remove-A")).toBeNull();
    await fireEvent.press(screen.getByTestId("special-product-card-A"));
    expect(presenter.addToCartCalls).toEqual(["A"]);
    await screen.unmount();
  });

  it("整理模式文案按当前语言显示，英文界面不混入中文", async () => {
    mockLanguage = "en";
    const presenter = new ScreenPresenter({
      items: [product("A")],
      online: true,
    });
    const screen = await render(
      <SpecialProductsScreen presenter={presenter} />,
    );

    expect(screen.getByText("Organize")).toBeTruthy();
    expect(screen.queryByText("整理")).toBeNull();
    await fireEvent.press(screen.getByTestId("special-products-organize"));
    expect(screen.getByText("Done")).toBeTruthy();
    expect(screen.getByText(/^Organize mode:/)).toBeTruthy();
    expect(screen.getByText("Remove")).toBeTruthy();
    expect(screen.queryByText("完成整理")).toBeNull();
    expect(screen.queryByText("取消标记")).toBeNull();
    await screen.unmount();
  });

  it("仅 View 权限可浏览卡片，但不可加购且不显示管理面板", async () => {
    const presenter = new ScreenPresenter({
      items: [product("A")],
      permissions: [SPECIAL_PRODUCTS_VIEW_PERMISSION],
    });
    const screen = await render(
      <SpecialProductsScreen presenter={presenter} />,
    );

    const cardA = screen.getByTestId("special-product-card-A");
    expect(cardA).toBeTruthy();
    expect(cardA.props.accessibilityState).toEqual({ disabled: true });
    expect(screen.queryByTestId("special-products-add-product")).toBeNull();
    expect(screen.queryByTestId("special-products-remove-A")).toBeNull();
    // 没有管理权限就没有「整理」入口
    expect(screen.queryByTestId("special-products-organize")).toBeNull();
    expect(
      StyleSheet.flatten(
        screen.getByTestId("special-products-refresh-local").props.style,
      ).minHeight,
    ).toBeGreaterThanOrEqual(SPECIAL_PRODUCTS_MIN_TOUCH_TARGET);
    await screen.unmount();
  });

  it("卡片显示商品图片，无图或加载失败时回落文字占位", async () => {
    const withImage: SpecialProductItem = {
      ...product("IMG"),
      productImage: "data:image/png;base64,iVBORw0KGgo=",
    };
    const fileUri: SpecialProductItem = {
      ...product("FILE"),
      productImage: "file:///etc/passwd",
    };
    const presenter = new ScreenPresenter({
      items: [withImage, product("PLAIN"), fileUri],
    });
    const screen = await render(
      <SpecialProductsScreen presenter={presenter} />,
    );

    // 有图卡片渲染图片容器且无占位；无图卡片显示商品名首字符占位
    // （占位是装饰性文本带 accessibilityElementsHidden，需显式查询隐藏元素）
    expect(
      screen.getByTestId("special-product-card-image-IMG-content"),
    ).toBeTruthy();
    expect(
      screen.queryByTestId("special-product-card-image-IMG-placeholder", {
        includeHiddenElements: true,
      }),
    ).toBeNull();
    expect(
      screen.getByTestId("special-product-card-image-PLAIN-placeholder", {
        includeHiddenElements: true,
      }),
    ).toBeTruthy();
    // 协议白名单：非 https/http/data:image 的 uri（如 file://）不进入渲染管线
    expect(
      screen.queryByTestId("special-product-card-image-FILE-content"),
    ).toBeNull();
    expect(
      screen.getByTestId("special-product-card-image-FILE-placeholder", {
        includeHiddenElements: true,
      }),
    ).toBeTruthy();

    // 图片加载失败时回落为文字占位
    fireEvent(
      screen.getByTestId("special-product-card-image-IMG-content"),
      "error",
    );
    await waitFor(() => {
      expect(
        screen.getByTestId("special-product-card-image-IMG-placeholder", {
          includeHiddenElements: true,
        }),
      ).toBeTruthy();
    });
    await screen.unmount();
  });

  it("卡片明确显示货号，缺失时保留货号占位", async () => {
    const withoutItemNumber: SpecialProductItem = {
      ...product("NO-ITEM-NUMBER"),
      itemNumber: null,
    };
    const presenter = new ScreenPresenter({
      items: [product("A"), withoutItemNumber],
    });
    const screen = await render(
      <SpecialProductsScreen presenter={presenter} />,
    );

    expect(screen.getByText("货号：item-A")).toBeTruthy();
    expect(screen.getByText("货号：—")).toBeTruthy();

    await screen.unmount();
  });

  it("在线管理通过弹窗搜索并把候选添加为特殊商品", async () => {
    const candidateWithImage: SpecialProductItem = {
      ...product("IMG2"),
      productImage: "data:image/png;base64,iVBORw0KGgo=",
    };
    const presenter = new ScreenPresenter({
      candidates: [product("C"), candidateWithImage],
      items: [product("A"), product("B")],
      online: true,
    });
    const screen = await render(
      <SpecialProductsScreen presenter={presenter} />,
    );

    // 弹窗默认关闭
    expect(screen.queryByTestId("special-products-add-modal")).toBeNull();

    // 点击「添加商品」打开弹窗，键盘滚动容器保留触控
    await fireEvent.press(screen.getByTestId("special-products-add-product"));
    expect(screen.getByTestId("special-products-add-modal")).toBeTruthy();
    // 打开弹窗时清空上次搜索
    expect(presenter.searchQueries).toContain("");
    expect(
      screen.getByTestId("special-products-add-modal-scroll").props
        .keyboardShouldPersistTaps,
    ).toBe("handled");

    // 候选行同样展示商品图片：有图渲染缩略图、无图显示占位
    expect(
      screen.getByTestId("special-products-candidate-image-IMG2-content"),
    ).toBeTruthy();
    expect(
      screen.getByTestId("special-products-candidate-image-C-placeholder", {
        includeHiddenElements: true,
      }),
    ).toBeTruthy();

    await fireEvent.changeText(
      screen.getByTestId("special-products-search-input"),
      "Product C",
    );
    await fireEvent.press(screen.getByTestId("special-products-search"));
    expect(presenter.searchCalls).toBe(1);
    expect(presenter.searchQueries).toContain("Product C");

    // 点击候选行 = 添加为特殊商品并自动关闭弹窗
    await fireEvent.press(screen.getByTestId("special-products-candidate-C"));
    expect(presenter.markCalls).toEqual([
      { isSpecialProduct: true, productCode: "C" },
    ]);
    expect(screen.queryByTestId("special-products-add-modal")).toBeNull();

    // 关闭按钮也能收起弹窗
    await fireEvent.press(screen.getByTestId("special-products-add-product"));
    await fireEvent.press(
      screen.getByTestId("special-products-add-modal-close"),
    );
    expect(screen.queryByTestId("special-products-add-modal")).toBeNull();

    // ↓ 收在整理模式里：先进入整理再操作
    await fireEvent.press(screen.getByTestId("special-products-organize"));
    await fireEvent.press(
      screen.getByTestId("special-products-move-down-A"),
    );
    expect(presenter.reorderCalls).toEqual([
      { delta: 1, productCode: "A" },
    ]);
    await screen.unmount();
  });

  it("整理状态下长按卡片拖动到目标格后调用 moveTo 持久化排序", async () => {
    const presenter = new ScreenPresenter({
      items: [product("A"), product("B"), product("C"), product("D")],
      online: true,
    });
    const screen = await render(
      <SpecialProductsScreen presenter={presenter} />,
    );

    // 长按拖动只在整理状态下启用
    await fireEvent.press(screen.getByTestId("special-products-organize"));
    const cardA = screen.getByTestId("special-product-card-A");
    const shellA = screen.getByTestId("special-product-card-A-shell");
    // 卡片实测尺寸：宽 85.6（单列）、高 240 → 行高 252
    // responder 必须由受审计的拖动壳层承接，手动调用真实回调。
    await act(async () => {
      shellA.props.onLayout({
        nativeEvent: { layout: { height: 240, width: 85.6 } },
      });
    });
    // PanResponder 从 event.touchHistory（顶层）读取手势数据
    const moveEvent = (pageX: number, pageY: number) => ({
      nativeEvent: {
        changedTouches: [{ pageX, pageY }],
        identifier: 0,
        touches: [{ pageX, pageY }],
      },
      touchHistory: {
        indexOfSingleActiveTouch: 0,
        mostRecentTimeStamp: 100,
        numberActiveTouches: 1,
        touchBank: [
          {
            currentPageX: pageX,
            currentPageY: pageY,
            currentTimeStamp: 100,
            previousPageX: 0,
            previousPageY: 0,
            previousTimeStamp: 0,
            startPageX: 0,
            startPageY: 0,
            startTimeStamp: 0,
            touchActive: true,
          },
        ],
      },
    });
    // 长按抬起卡片 → 拖到 (110,80)：PanResponder dx=110 → 约 1.3 格（取整 1 格）→ 松手落位
    await fireEvent(cardA, "longPress");
    await act(async () => {
      shellA.props.onResponderGrant(moveEvent(20, 80));
      shellA.props.onResponderMove(moveEvent(110, 80));
    });
    // 拖拽中目标格高亮（边框加粗）
    expect(
      StyleSheet.flatten(
        screen.getByTestId("special-product-card-B-shell").props.style,
      ).borderWidth,
    ).toBe(2);
    // 松手：重放完整手势序列（重渲染后壳层的 handler 已更新，
    // gestureState 按 touchHistory 帧差重新累计，dx 仍为 110）
    await act(async () => {
      shellA.props.onResponderGrant(moveEvent(20, 80));
      shellA.props.onResponderMove(moveEvent(110, 80));
      shellA.props.onResponderRelease(moveEvent(110, 80));
    });

    expect(presenter.moveToCalls).toEqual([{ productCode: "A", toIndex: 1 }]);
    await screen.unmount();
  });

  it("可拖拽卡片长按播放一次排序音且不加购", async () => {
    const presenter = new ScreenPresenter({
      items: [product("A"), product("B")],
      online: true,
    });
    const screen = await render(
      <SpecialProductsScreen presenter={presenter} />,
    );

    // 进入整理后卡片才可拖拽；清掉点「整理」按钮本身的触控音再计数
    await fireEvent.press(screen.getByTestId("special-products-organize"));
    mockPlayTouchSound.mockClear();
    const cardA = screen.getByTestId("special-product-card-A");
    await fireEvent(cardA, "pressIn");
    await fireEvent(cardA, "longPress");
    await fireEvent.press(cardA);

    expect(mockPlayTouchSound).toHaveBeenCalledTimes(1);
    expect(mockPlayTouchSound).toHaveBeenCalledWith("navigate");
    expect(presenter.addToCartCalls).toEqual([]);
    await screen.unmount();
  });

  it("不可拖拽卡片不注册长按音且普通点击仍加购", async () => {
    const presenter = new ScreenPresenter({
      items: [product("A")],
      online: true,
      permissions: [
        SPECIAL_PRODUCTS_VIEW_PERMISSION,
        SPECIAL_PRODUCTS_ADD_TO_CART_PERMISSION,
      ],
    });
    const screen = await render(
      <SpecialProductsScreen presenter={presenter} />,
    );

    const cardA = screen.getByTestId("special-product-card-A");
    await fireEvent(cardA, "pressIn");
    await fireEvent(cardA, "longPress");
    await fireEvent.press(cardA);

    expect(cardA.props.onLongPress).toBeUndefined();
    expect(mockPlayTouchSound).toHaveBeenCalledTimes(1);
    expect(mockPlayTouchSound).toHaveBeenCalledWith("tap");
    expect(mockPlayTouchSound).not.toHaveBeenCalledWith("navigate");
    expect(presenter.addToCartCalls).toEqual(["A"]);
    await screen.unmount();
  });

  it("失败状态只显示稳定安全文案", async () => {
    const presenter = new ScreenPresenter({
      items: [product("A")],
      online: true,
      statusCode: "download-failed",
    });
    const screen = await render(
      <SpecialProductsScreen presenter={presenter} />,
    );

    expect(screen.getByText(/下载未完成/)).toBeTruthy();
    expect(screen.queryByText(/very-secret/)).toBeNull();
    expect(screen.queryByText(/https:\/\/host/)).toBeNull();
    await screen.unmount();
  });

  it("按当前语言只显示一种特殊商品文案，旧双语字符串不会出现", async () => {
    const chinese = await render(
      <SpecialProductsScreen presenter={new ScreenPresenter()} />,
    );
    expect(chinese.getByText("特殊商品")).toBeTruthy();
    expect(chinese.queryByText("Special products")).toBeNull();
    expect(chinese.queryByText("特殊商品 / Special products")).toBeNull();
    await chinese.unmount();

    mockLanguage = "en";
    const english = await render(
      <SpecialProductsScreen presenter={new ScreenPresenter()} />,
    );
    expect(english.getByText("Special products")).toBeTruthy();
    expect(english.queryByText("特殊商品")).toBeNull();
    expect(english.queryByText("特殊商品 / Special products")).toBeNull();
    await english.unmount();
  });

  it("不可用页显示安全说明，并可从页头返回销售页", async () => {
    const onBack = jest.fn();
    const screen = await render(
      <SpecialProductsUnavailableScreen onBack={onBack} />,
    );

    expect(
      screen.getByTestId("special-products-runtime-unavailable"),
    ).toBeTruthy();
    expect(screen.getByText("功能暂不可用")).toBeTruthy();
    expect(
      screen.getByText("本机运行时尚未提供特殊商品服务，请返回销售页。"),
    ).toBeTruthy();
    const back = screen.getByTestId("special-products-unavailable-back");
    expect(StyleSheet.flatten(back.props.style).minHeight).toBe(56);
    await fireEvent.press(back);
    expect(onBack).toHaveBeenCalledTimes(1);
    await screen.unmount();
  });

  it("添加商品弹窗点击面板外遮罩关闭", async () => {
    const presenter = new ScreenPresenter({
      items: [product("A")],
      online: true,
    });
    const screen = await render(
      <SpecialProductsScreen presenter={presenter} />,
    );

    await fireEvent.press(screen.getByTestId("special-products-add-product"));
    expect(screen.getByTestId("special-products-add-modal")).toBeTruthy();
    await fireEvent.press(
      screen.getByTestId("special-products-add-modal-backdrop"),
    );
    expect(screen.queryByTestId("special-products-add-modal")).toBeNull();
    await screen.unmount();
  });

  describe("分店级特殊商品分类", () => {
    const categories: SpecialProductCategory[] = [
      { categoryId: "C1", color: "#1D6FB8", name: "Balloons", sortOrder: 0 },
      { categoryId: "C2", color: null, name: "Cards", sortOrder: 1 },
    ];
    const items = ["A", "B", "C", "D", "E", "F"].map((code) => product(code));
    const assignments: SpecialProductCategoryAssignment[] = [
      { categoryId: "C1", productCode: "A", sortOrder: 0 },
      { categoryId: "C1", productCode: "B", sortOrder: 1 },
      { categoryId: "C2", productCode: "C", sortOrder: 0 },
    ];

    it("没有任何分类时不显示分类栏与搜索框，网格仍是 5 列，整理里没有「移到分类」", async () => {
      const presenter = new ScreenPresenter({
        items: [product("A"), product("B")],
        online: true,
      });
      const screen = await render(
        <SpecialProductsScreen presenter={presenter} />,
      );

      expect(screen.queryByTestId("special-products-category-rail")).toBeNull();
      expect(screen.queryByTestId("special-products-filter-input")).toBeNull();
      expect(
        StyleSheet.flatten(
          screen.getByTestId("special-product-card-A-shell").props.style,
        ).width,
      ).toBe("19.2%");
      expect(specialProductsGridColumns(false)).toBe(5);
      await fireEvent.press(screen.getByTestId("special-products-organize"));
      expect(
        screen.queryByTestId("special-products-move-category-A"),
      ).toBeNull();
      expect(
        screen.getByText(
          "整理模式：用 ↑ ↓ 或长按拖动调整顺序，「取消标记」把商品移出特殊商品。此时点卡片不会加购。",
        ),
      ).toBeTruthy();
      await screen.unmount();
    });

    it("有分类时显示分类栏（带数量）与搜索框，网格收为 4 列，行高 ≥ 58 整行可点", async () => {
      const presenter = new ScreenPresenter({
        assignments,
        categories,
        items,
        online: true,
      });
      const screen = await render(
        <SpecialProductsScreen presenter={presenter} />,
      );

      expect(screen.getByTestId("special-products-category-rail")).toBeTruthy();
      // 栏内可滚动
      expect(
        screen.getByTestId("special-products-category-rail-scroll"),
      ).toBeTruthy();
      expect(
        StyleSheet.flatten(
          screen.getByTestId("special-products-category-rail").props.style,
        ).width,
      ).toBe(SPECIAL_PRODUCTS_CATEGORY_RAIL_WIDTH);
      for (const [testID, label] of [
        ["special-products-category-all", "全部 6"],
        ["special-products-category-C1", "Balloons 2"],
        ["special-products-category-C2", "Cards 1"],
        ["special-products-category-uncategorized", "未分类 3"],
      ] as const) {
        const row = screen.getByTestId(testID);
        expect(row.props.accessibilityLabel).toBe(label);
        expect(StyleSheet.flatten(row.props.style).minHeight).toBeGreaterThanOrEqual(
          SPECIAL_PRODUCTS_CATEGORY_ROW_HEIGHT,
        );
      }
      expect(
        screen.getByTestId("special-products-category-all").props
          .accessibilityState,
      ).toMatchObject({ selected: true });
      expect(
        screen.getByTestId("special-products-category-C1").props
          .accessibilityState,
      ).toMatchObject({ selected: false });
      expect(screen.getByTestId("special-products-filter-input")).toBeTruthy();
      expect(
        StyleSheet.flatten(
          screen.getByTestId("special-product-card-A-shell").props.style,
        ).width,
      ).toBe("24%");
      expect(specialProductsGridColumns(true)).toBe(4);
      expect(screen.getByText("在全部 6 项里找")).toBeTruthy();
      await screen.unmount();
    });

    it("「未分类」数量为 0 时该行隐藏；分类栏提示只对有管理权限者显示", async () => {
      const allAssigned = items.map((item, index) => ({
        categoryId: index % 2 === 0 ? "C1" : "C2",
        productCode: item.productCode,
        sortOrder: index,
      }));
      const manager = new ScreenPresenter({
        assignments: allAssigned,
        categories,
        items,
        online: true,
      });
      const withHint = await render(
        <SpecialProductsScreen presenter={manager} />,
      );
      expect(
        withHint.queryByTestId("special-products-category-uncategorized"),
      ).toBeNull();
      expect(withHint.getByTestId("special-products-category-hint")).toBeTruthy();
      expect(
        withHint.getByText("分类在手机 App「特殊商品分类」里新建、改名、排序"),
      ).toBeTruthy();
      await withHint.unmount();

      const viewer = new ScreenPresenter({
        assignments,
        categories,
        items,
        online: true,
        permissions: [SPECIAL_PRODUCTS_VIEW_PERMISSION],
      });
      const withoutHint = await render(
        <SpecialProductsScreen presenter={viewer} />,
      );
      expect(withoutHint.getByTestId("special-products-category-rail")).toBeTruthy();
      expect(
        withoutHint.queryByTestId("special-products-category-hint"),
      ).toBeNull();
      await withoutHint.unmount();
    });

    it("点分类行切换视图：只显示该分类的商品，标题与数量跟随", async () => {
      const presenter = new ScreenPresenter({
        assignments,
        categories,
        items,
        online: true,
      });
      const screen = await render(
        <SpecialProductsScreen presenter={presenter} />,
      );

      await fireEvent.press(screen.getByTestId("special-products-category-C1"));
      expect(presenter.selectCalls).toEqual(["C1"]);
      expect(screen.getByTestId("special-product-card-A")).toBeTruthy();
      expect(screen.getByTestId("special-product-card-B")).toBeTruthy();
      expect(screen.queryByTestId("special-product-card-C")).toBeNull();
      expect(screen.getByText("2 项")).toBeTruthy();
      expect(
        screen.getByTestId("special-products-category-C1").props
          .accessibilityState,
      ).toMatchObject({ selected: true });

      await fireEvent.press(
        screen.getByTestId("special-products-category-uncategorized"),
      );
      expect(screen.getByTestId("special-product-card-D")).toBeTruthy();
      expect(screen.queryByTestId("special-product-card-A")).toBeNull();
      await screen.unmount();
    });

    it("搜索框在全部特殊商品里过滤；无命中显示空状态；清除按钮回到原分类", async () => {
      const presenter = new ScreenPresenter({
        assignments,
        categories,
        items,
        online: true,
        selectedCategoryId: "C2",
      });
      const screen = await render(
        <SpecialProductsScreen presenter={presenter} />,
      );
      expect(screen.queryByTestId("special-product-card-A")).toBeNull();

      await fireEvent.changeText(
        screen.getByTestId("special-products-filter-input"),
        "lookup-a",
      );
      expect(presenter.searchTexts).toEqual(["lookup-a"]);
      // 搜索不受当前分类（C2）限制：A 在 C1 也能搜到
      expect(screen.getByTestId("special-product-card-A")).toBeTruthy();
      expect(screen.getByText("搜索结果")).toBeTruthy();

      await fireEvent.changeText(
        screen.getByTestId("special-products-filter-input"),
        "不存在的商品",
      );
      expect(screen.getByTestId("special-products-view-empty")).toBeTruthy();
      expect(screen.getByText("没有匹配的特殊商品")).toBeTruthy();

      await fireEvent.press(screen.getByTestId("special-products-filter-clear"));
      expect(presenter.searchTexts.at(-1)).toBe("");
      expect(screen.queryByTestId("special-products-view-empty")).toBeNull();
      expect(screen.getByTestId("special-product-card-C")).toBeTruthy();
      expect(screen.queryByTestId("special-product-card-A")).toBeNull();
      await screen.unmount();
    });

    it("整理模式：每张卡片多「移到分类」（≥44pt）；点开清单，当前分类打勾，选中即提交", async () => {
      const presenter = new ScreenPresenter({
        assignments,
        categories,
        items,
        online: true,
        selectedCategoryId: "C1",
      });
      const screen = await render(
        <SpecialProductsScreen presenter={presenter} />,
      );

      await fireEvent.press(screen.getByTestId("special-products-organize"));
      expect(
        screen.getByText(
          "整理模式：用 ↑ ↓ 或长按拖动调整「Balloons」里的顺序（全店收银机一致），「移到分类」换分类。此时点卡片不会加购。",
        ),
      ).toBeTruthy();
      const move = screen.getByTestId("special-products-move-category-A");
      expect(StyleSheet.flatten(move.props.style).minHeight).toBeGreaterThanOrEqual(
        SPECIAL_PRODUCTS_MIN_TOUCH_TARGET,
      );
      expect(move.props.accessibilityState).toEqual({ disabled: false });

      await fireEvent.press(move);
      expect(screen.getByTestId("special-products-move-sheet")).toBeTruthy();
      // 卡片与清单标题各有一处商品名
      expect(screen.getAllByText("Product A")).toHaveLength(2);
      // 各分类 + 未分类，当前分类（C1）打勾
      expect(
        screen.getByTestId("special-products-move-option-C1-check"),
      ).toBeTruthy();
      expect(
        screen.queryByTestId("special-products-move-option-C2-check"),
      ).toBeNull();
      expect(
        screen.getByTestId("special-products-move-option-uncategorized"),
      ).toBeTruthy();
      for (const testID of [
        "special-products-move-option-C1",
        "special-products-move-option-C2",
        "special-products-move-option-uncategorized",
      ]) {
        expect(
          StyleSheet.flatten(screen.getByTestId(testID).props.style).minHeight,
        ).toBeGreaterThanOrEqual(SPECIAL_PRODUCTS_MIN_TOUCH_TARGET);
      }

      // 选中另一个分类：提交并关闭清单
      await fireEvent.press(
        screen.getByTestId("special-products-move-option-C2"),
      );
      expect(presenter.assignCalls).toEqual([
        { categoryId: "C2", productCode: "A" },
      ]);
      expect(screen.queryByTestId("special-products-move-sheet")).toBeNull();

      // 移到「未分类」= null；选回当前分类不请求
      await fireEvent.press(
        screen.getByTestId("special-products-move-category-A"),
      );
      await fireEvent.press(
        screen.getByTestId("special-products-move-option-uncategorized"),
      );
      expect(presenter.assignCalls.at(-1)).toEqual({
        categoryId: null,
        productCode: "A",
      });
      await fireEvent.press(
        screen.getByTestId("special-products-move-category-A"),
      );
      await fireEvent.press(
        screen.getByTestId("special-products-move-option-C1"),
      );
      expect(presenter.assignCalls).toHaveLength(2);

      // 点遮罩关闭
      await fireEvent.press(
        screen.getByTestId("special-products-move-category-A"),
      );
      await fireEvent.press(
        screen.getByTestId("special-products-move-sheet-backdrop"),
      );
      expect(screen.queryByTestId("special-products-move-sheet")).toBeNull();
      await screen.unmount();
    });

    it("整理中掉线：「移到分类」与清单选项都禁用，不提交", async () => {
      const presenter = new ScreenPresenter({
        assignments,
        categories,
        items,
        online: true,
      });
      const screen = await render(
        <SpecialProductsScreen presenter={presenter} />,
      );
      await fireEvent.press(screen.getByTestId("special-products-organize"));
      await fireEvent.press(
        screen.getByTestId("special-products-move-category-A"),
      );
      // 清单开着时掉线：选项禁用
      await act(async () => {
        presenter.setOnline(false);
      });
      const option = screen.getByTestId("special-products-move-option-C2");
      expect(option.props.accessibilityState).toEqual({
        disabled: true,
        selected: false,
      });
      await fireEvent.press(option);
      expect(presenter.assignCalls).toEqual([]);
      await fireEvent.press(
        screen.getByTestId("special-products-move-sheet-close"),
      );
      expect(
        screen.getByTestId("special-products-move-category-A").props
          .accessibilityState,
      ).toEqual({ disabled: true });
      await screen.unmount();
    });

    it("没有 Manage 权限的收银员整理入口不存在，也不会弹出移动清单", async () => {
      const presenter = new ScreenPresenter({
        assignments,
        categories,
        items,
        online: true,
        permissions: [
          SPECIAL_PRODUCTS_VIEW_PERMISSION,
          SPECIAL_PRODUCTS_ADD_TO_CART_PERMISSION,
        ],
      });
      const screen = await render(
        <SpecialProductsScreen presenter={presenter} />,
      );
      expect(screen.queryByTestId("special-products-organize")).toBeNull();
      expect(
        screen.queryByTestId("special-products-move-category-A"),
      ).toBeNull();
      await screen.unmount();
    });

    it("搜索中整理：↑ ↓ 禁用并提示，移到分类仍可用；拖动不可用", async () => {
      const presenter = new ScreenPresenter({
        assignments,
        categories,
        items,
        online: true,
      });
      const screen = await render(
        <SpecialProductsScreen presenter={presenter} />,
      );
      await fireEvent.press(screen.getByTestId("special-products-organize"));
      await fireEvent.changeText(
        screen.getByTestId("special-products-filter-input"),
        "product",
      );

      expect(
        screen.getByText(/^整理模式：搜索时不能调整顺序/),
      ).toBeTruthy();
      expect(
        screen.getByTestId("special-products-move-down-A").props
          .accessibilityState,
      ).toEqual({ disabled: true });
      expect(
        screen.getByTestId("special-products-move-up-B").props
          .accessibilityState,
      ).toEqual({ disabled: true });
      expect(
        screen.getByTestId("special-products-move-category-A").props
          .accessibilityState,
      ).toEqual({ disabled: false });
      expect(
        screen.getByTestId("special-product-card-A").props.onLongPress,
      ).toBeUndefined();
      await screen.unmount();
    });

    it("4 列网格的拖动按 4 列换算目标索引（与渲染用同一个列数）", async () => {
      // 纯函数：从索引 0 向下拖一整行，4 列 → 4，5 列 → 5
      expect(specialProductsDragTargetIndex(0, 0, 252, 4, 85.6, 252, 20)).toBe(4);
      expect(specialProductsDragTargetIndex(0, 0, 252, 5, 85.6, 252, 20)).toBe(5);

      const presenter = new ScreenPresenter({
        assignments,
        categories,
        items,
        online: true,
      });
      const screen = await render(
        <SpecialProductsScreen presenter={presenter} />,
      );
      await fireEvent.press(screen.getByTestId("special-products-organize"));
      const cardA = screen.getByTestId("special-product-card-A");
      const shellA = screen.getByTestId("special-product-card-A-shell");
      await act(async () => {
        shellA.props.onLayout({
          nativeEvent: { layout: { height: 242, width: 85.6 } },
        });
      });
      const gesture = (pageX: number, pageY: number) => ({
        nativeEvent: {
          changedTouches: [{ pageX, pageY }],
          identifier: 0,
          touches: [{ pageX, pageY }],
        },
        touchHistory: {
          indexOfSingleActiveTouch: 0,
          mostRecentTimeStamp: 100,
          numberActiveTouches: 1,
          touchBank: [
            {
              currentPageX: pageX,
              currentPageY: pageY,
              currentTimeStamp: 100,
              previousPageX: 0,
              previousPageY: 0,
              previousTimeStamp: 0,
              startPageX: 0,
              startPageY: 0,
              startTimeStamp: 0,
              touchActive: true,
            },
          ],
        },
      });
      await fireEvent(cardA, "longPress");
      await act(async () => {
        shellA.props.onResponderGrant(gesture(0, 0));
        shellA.props.onResponderMove(gesture(0, 252));
        shellA.props.onResponderRelease(gesture(0, 252));
      });
      // 6 个商品、4 列：向下拖一行 → 索引 4（5 列时会是 5）
      expect(presenter.moveToCalls).toEqual([{ productCode: "A", toIndex: 4 }]);
      await screen.unmount();
    });
  });
});

class ScreenPresenter implements SpecialProductsScreenPresenter {
  public readonly addToCartCalls: string[] = [];
  public readonly assignCalls: {
    productCode: string;
    categoryId: string | null;
  }[] = [];
  public readonly selectCalls: string[] = [];
  public readonly searchTexts: string[] = [];
  private requestedSelection = "all";
  public downloadCalls = 0;
  public readonly markCalls: {
    productCode: string;
    isSpecialProduct: boolean;
  }[] = [];
  public readonly moveToCalls: {
    productCode: string;
    toIndex: number;
  }[] = [];
  public readonly reorderCalls: {
    productCode: string;
    delta: -1 | 1;
  }[] = [];
  public searchCalls = 0;
  public readonly searchQueries: string[] = [];
  private readonly listeners = new Set<() => void>();
  private state: SpecialProductsState;

  public constructor(
    options: Partial<{
      assignments: readonly SpecialProductCategoryAssignment[];
      candidates: readonly SpecialProductItem[];
      categories: readonly SpecialProductCategory[];
      items: readonly SpecialProductItem[];
      online: boolean;
      permissions: readonly string[];
      selectedCategoryId: string;
      statusCode: SpecialProductsState["statusCode"];
    }> = {},
  ) {
    this.requestedSelection = options.selectedCategoryId ?? "all";
    this.state = {
      access: resolveSpecialProductsAccess(
        options.permissions ?? [
          SPECIAL_PRODUCTS_VIEW_PERMISSION,
          SPECIAL_PRODUCTS_MANAGE_PERMISSION,
          SPECIAL_PRODUCTS_ADD_TO_CART_PERMISSION,
        ],
      ),
      assignments: options.assignments ?? [],
      busy: false,
      candidates: options.candidates ?? [],
      categories: options.categories ?? [],
      categoryCounts: { all: 0, byCategory: {}, uncategorized: 0 },
      items: options.items ?? [],
      kind: "ready",
      online: options.online ?? false,
      search: "",
      searching: false,
      searchQuery: "",
      selectedCategoryId: "all",
      statusCode: options.statusCode ?? null,
      visibleItems: [],
    };
    this.derive();
  }

  /** 与真实 presenter 同一个纯函数派生分类视图，保证 fake 与生产口径一致。 */
  private derive(): void {
    const view = deriveSpecialProductsCategoryView({
      assignments: this.state.assignments,
      categories: this.state.categories,
      items: this.state.items,
      search: this.state.search,
      selection: this.requestedSelection,
    });
    this.state = {
      ...this.state,
      categoryCounts: view.counts,
      selectedCategoryId: view.selectedCategoryId,
      visibleItems: view.visibleItems,
    };
  }

  public selectCategory(selection: string): void {
    this.selectCalls.push(selection);
    this.requestedSelection = selection;
    this.state = { ...this.state, search: "" };
    this.derive();
    for (const listener of this.listeners) listener();
  }

  public setSearch(search: string): void {
    this.searchTexts.push(search);
    this.state = { ...this.state, search };
    this.derive();
    for (const listener of this.listeners) listener();
  }

  public async assignToCategory(
    productCode: string,
    categoryId: string | null,
  ): Promise<void> {
    this.assignCalls.push({ productCode, categoryId });
  }

  public readonly getState = () => this.state;

  public readonly subscribe = (listener: () => void) => {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  };

  public async load(): Promise<void> {}

  /** 模拟路由层同步网络状态（presenter.setOnline），用于整理中掉线的场景。 */
  public setOnline(online: boolean): void {
    this.patch({ online });
  }

  public setSearchQuery(searchQuery: string): void {
    this.searchQueries.push(searchQuery);
    this.patch({ searchQuery });
  }

  public async searchCandidates(): Promise<void> {
    this.searchCalls += 1;
  }

  public async addToCart(productCode: string): Promise<void> {
    this.addToCartCalls.push(productCode);
  }

  public async download(): Promise<void> {
    this.downloadCalls += 1;
  }

  public async mark(
    productCode: string,
    isSpecialProduct: boolean,
  ): Promise<void> {
    this.markCalls.push({ productCode, isSpecialProduct });
  }

  public async reorder(
    productCode: string,
    delta: -1 | 1,
  ): Promise<void> {
    this.reorderCalls.push({ productCode, delta });
  }

  public async moveTo(productCode: string, toIndex: number): Promise<void> {
    this.moveToCalls.push({ productCode, toIndex });
  }

  private patch(patch: Partial<SpecialProductsState>): void {
    this.state = { ...this.state, ...patch };
    for (const listener of this.listeners) listener();
  }
}

function product(productCode: string): SpecialProductItem {
  return {
    barcode: `barcode-${productCode}`,
    discountRate: null,
    displayName: `Product ${productCode}`,
    itemNumber: `item-${productCode}`,
    lookupCode: `lookup-${productCode}`,
    priceSource: 0,
    productCode,
    productImage: null,
    quantityFactor: 1,
    referenceCode: null,
    retailPriceCents: 1_250,
    sortOrder: 0,
    storeCode: "S1",
  };
}
