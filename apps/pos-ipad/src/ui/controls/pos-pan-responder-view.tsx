import type { ComponentType } from "react";
import {
  View,
  type GestureResponderEvent,
  type PanResponderInstance,
  type ViewProps,
} from "react-native";
import { usePosThemeVersion } from "@/ui/theme";

type AndroidNativeResponderBlockProps = Readonly<{
  onShouldBlockNativeResponder?:
    | ((event: GestureResponderEvent) => boolean)
    | undefined;
}>;

type PanResponderHandlers = PanResponderInstance["panHandlers"] &
  AndroidNativeResponderBlockProps;

// RN 的 ViewProps 类型未声明 Android 专用回调，但 PanResponder 运行时会生成它。
const PanResponderHost = View as unknown as ComponentType<
  ViewProps & AndroidNativeResponderBlockProps
>;

export type PosPanResponderViewProps = Pick<
  ViewProps,
  "children" | "onLayout" | "pointerEvents" | "style" | "testID"
> &
  Readonly<{
    panHandlers: PanResponderInstance["panHandlers"];
  }>;

/**
 * 仅承接已创建 PanResponder 的外层壳：显式绑定完整原生 responder 集合，
 * 使业务页面不会通过动态 spread 绕过触控审计。
 */
export function PosPanResponderView({
  children,
  onLayout,
  panHandlers,
  pointerEvents,
  style,
  testID,
}: PosPanResponderViewProps) {
  usePosThemeVersion();
  const handlers = panHandlers as PanResponderHandlers;
  return (
    <PanResponderHost
      onLayout={onLayout}
      onMoveShouldSetResponder={handlers.onMoveShouldSetResponder}
      onMoveShouldSetResponderCapture={
        handlers.onMoveShouldSetResponderCapture
      }
      onResponderEnd={handlers.onResponderEnd}
      onResponderGrant={handlers.onResponderGrant}
      onResponderMove={handlers.onResponderMove}
      onResponderReject={handlers.onResponderReject}
      onResponderRelease={handlers.onResponderRelease}
      onResponderStart={handlers.onResponderStart}
      onResponderTerminate={handlers.onResponderTerminate}
      onResponderTerminationRequest={
        handlers.onResponderTerminationRequest
      }
      onShouldBlockNativeResponder={
        handlers.onShouldBlockNativeResponder
      }
      onStartShouldSetResponder={handlers.onStartShouldSetResponder}
      onStartShouldSetResponderCapture={
        handlers.onStartShouldSetResponderCapture
      }
      pointerEvents={pointerEvents}
      style={style}
      testID={testID}
    >
      {children}
    </PanResponderHost>
  );
}
