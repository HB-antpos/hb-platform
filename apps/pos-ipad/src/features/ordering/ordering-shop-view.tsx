import { forwardRef, useCallback, useEffect, useImperativeHandle, useRef } from "react";
import { WebView, type WebViewMessageEvent, type WebViewNavigation } from "react-native-webview";

import { ORDERING_ACTIVITY_CHANNEL, ORDERING_ACTIVITY_SCRIPT } from "./ordering-activity";
import {
  isOrderingLoginUrl,
  isOrderingShopUrl,
  ORDERING_EMBED_MODE,
  ORDERING_SCAN_EVENT,
  ORDERING_SHOP_PATH,
  ORDERING_WEB_ORIGIN,
} from "./ordering-config";
import type { OrderingWebBridge } from "./ordering-web-bridge";

export type OrderingShopViewHandle = Readonly<{
  /** 扫到的码交给网页：网页按货号 / 条码搜索。 */
  sendScan(barcode: string): void;
  reload(): void;
}>;

export type OrderingShopViewProps = Readonly<{
  bridge: OrderingWebBridge;
  onActivity(): void;
  /** 网页跳去了登录页：会话已失效。 */
  onSessionLost(): void;
  /** 网页想去 /shop 以外的页面，已拦回 /shop。 */
  onBlockedNavigation(): void;
}>;

/** 页面脚本执行前先告诉网页「嵌在订货模式里」（网页隐藏自己的退出、后台入口）。 */
const BEFORE_CONTENT_SCRIPT = `window.__HB_EMBED_MODE__=${JSON.stringify(ORDERING_EMBED_MODE)};true;`;

function originOf(url: string | null | undefined): string | null {
  if (!url) return null;
  try {
    return new URL(url).origin;
  } catch {
    return null;
  }
}

/**
 * 仓库订货页签：整页内嵌 hotbargain.vip/shop（订货前台）。
 *
 * 中文注释：
 * 1. 这个网页窗口同时是订货模式的「会话宿主」：登录后的 Cookie、续期都在这里；
 *    订货助手的授权码、退出也通过 OrderingWebBridge 在这里同源执行。
 * 2. 只允许停在 /shop 及其子页：网站内部跳到别的页面（后台等）立即拦回 /shop；
 *    跳到登录页说明会话没了，交给工作台退出；外站链接一律不打开。
 */
export const OrderingShopView = forwardRef<OrderingShopViewHandle, OrderingShopViewProps>(
  function OrderingShopView({ bridge, onActivity, onBlockedNavigation, onSessionLost }, ref) {
    const webViewRef = useRef<WebView>(null);
    const inject = useCallback((script: string) => {
      webViewRef.current?.injectJavaScript(script);
    }, []);

    useImperativeHandle(
      ref,
      () => ({
        sendScan: (barcode: string) => {
          inject(
            `window.dispatchEvent(new CustomEvent(${JSON.stringify(ORDERING_SCAN_EVENT)},{detail:{barcode:${JSON.stringify(barcode)}}}));true;`,
          );
        },
        reload: () => webViewRef.current?.reload(),
      }),
      [inject],
    );

    // 卸载时撤掉注入函数，等待中的授权请求立即失败，不会挂着。
    useEffect(() => () => bridge.attach(null), [bridge]);

    const onLoadEnd = useCallback(
      (event: { nativeEvent: { url?: string } }) => {
        if (originOf(event.nativeEvent.url) === ORDERING_WEB_ORIGIN) bridge.attach(inject);
      },
      [bridge, inject],
    );

    const onNavigationStateChange = useCallback(
      (navigation: WebViewNavigation) => {
        if (isOrderingLoginUrl(navigation.url)) {
          onSessionLost();
          return;
        }
        if (originOf(navigation.url) === ORDERING_WEB_ORIGIN && !isOrderingShopUrl(navigation.url)) {
          inject(`location.replace(${JSON.stringify(ORDERING_SHOP_PATH)});true;`);
          onBlockedNavigation();
        }
      },
      [inject, onBlockedNavigation, onSessionLost],
    );

    const onShouldStartLoadWithRequest = useCallback(
      (request: { url: string }) => {
        if (request.url === "about:blank") return true;
        if (originOf(request.url) === ORDERING_WEB_ORIGIN) return true;
        // 外站链接（帮助文档、供应商网站等）不在订货模式里打开。
        onBlockedNavigation();
        return false;
      },
      [onBlockedNavigation],
    );

    const onMessage = useCallback(
      (event: WebViewMessageEvent) => {
        const { data, url } = event.nativeEvent;
        if (bridge.handleMessage(data, url)) return;
        try {
          const envelope = JSON.parse(data) as Record<string, unknown>;
          if (envelope.channel === ORDERING_ACTIVITY_CHANNEL) onActivity();
        } catch {
          // 其他消息忽略
        }
      },
      [bridge, onActivity],
    );

    return (
      <WebView
        ref={webViewRef}
        injectedJavaScript={`${ORDERING_ACTIVITY_SCRIPT};true;`}
        injectedJavaScriptBeforeContentLoaded={BEFORE_CONTENT_SCRIPT}
        onLoadEnd={onLoadEnd}
        onMessage={onMessage}
        onNavigationStateChange={onNavigationStateChange}
        onShouldStartLoadWithRequest={onShouldStartLoadWithRequest}
        originWhitelist={["https://*"]}
        setSupportMultipleWindows={false}
        sharedCookiesEnabled
        source={{ uri: `${ORDERING_WEB_ORIGIN}${ORDERING_SHOP_PATH}` }}
        testID="ordering-shop-webview"
      />
    );
  },
);
