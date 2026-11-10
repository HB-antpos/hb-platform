import { forwardRef, useCallback, useRef } from "react";
import {
  Pressable,
  type GestureResponderEvent,
  type PressableProps,
  type View,
} from "react-native";

import {
  type TouchSoundKind,
  usePosSound,
} from "@/ui/feedback/pos-sound-context";
import { usePosThemeVersion } from "@/ui/theme";

export type PosPressableProps = PressableProps &
  Readonly<{
    longPressSound?: TouchSoundKind | false;
    sound?: TouchSoundKind | false;
  }>;

/** 为原生 Pressable 增加不中断业务回调的轻量触控音。 */
export const PosPressable = forwardRef<View, PosPressableProps>(
  function PosPressable(
    {
      disabled = false,
      longPressSound,
      onLongPress,
      onPress,
      onPressIn,
      sound = "tap",
      ...props
    },
    ref,
  ) {
    usePosThemeVersion();
    const { play } = usePosSound();
    const longPressTriggered = useRef(false);
    const handlesLongPress =
      typeof onLongPress === "function" || Boolean(longPressSound);

    const handlePressIn = useCallback(
      (event: GestureResponderEvent) => {
        longPressTriggered.current = false;
        onPressIn?.(event);
      },
      [onPressIn],
    );

    const handleLongPress = useCallback(
      (event: GestureResponderEvent) => {
        longPressTriggered.current = true;
        if (!disabled && longPressSound) play(longPressSound);
        onLongPress?.(event);
      },
      [disabled, longPressSound, onLongPress, play],
    );

    const handlePress = useCallback(
      (event: GestureResponderEvent) => {
        const shouldPlay = !disabled && !longPressTriggered.current && sound;
        longPressTriggered.current = false;
        if (shouldPlay) play(shouldPlay);
        // 不等待音频，确保任何业务导航或提交都有原有的响应时序。
        onPress?.(event);
      },
      [disabled, onPress, play, sound],
    );

    return (
      <Pressable
        {...props}
        disabled={disabled}
        onLongPress={handlesLongPress ? handleLongPress : undefined}
        onPress={handlePress}
        onPressIn={handlePressIn}
        ref={ref}
      />
    );
  },
);

PosPressable.displayName = "PosPressable";
