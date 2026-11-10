import { forwardRef, useCallback, useRef } from "react";
import {
  TextInput,
  type GestureResponderEvent,
  type TextInputProps,
} from "react-native";

import {
  type TouchSoundKind,
  usePosSound,
} from "@/ui/feedback/pos-sound-context";
import { usePosThemeVersion } from "@/ui/theme";

export type PosTextInputProps = TextInputProps &
  Readonly<{ sound?: TouchSoundKind | false }>;

/** 仅确认由手指轻触输入框时播放；焦点、受控 value 和 HID 文本均保持静默。 */
export const PosTextInput = forwardRef<TextInput, PosTextInputProps>(
  function PosTextInput(
    {
      editable = true,
      onTouchCancel,
      onTouchEnd,
      onTouchMove,
      onTouchStart,
      sound = "key",
      ...props
    },
    ref,
  ) {
    usePosThemeVersion();
    const { play } = usePosSound();
    const touch = useRef({ active: false, moved: false });

    const handleTouchStart = useCallback(
      (event: GestureResponderEvent) => {
        touch.current = { active: true, moved: false };
        onTouchStart?.(event);
      },
      [onTouchStart],
    );

    const handleTouchMove = useCallback(
      (event: GestureResponderEvent) => {
        touch.current.moved = true;
        onTouchMove?.(event);
      },
      [onTouchMove],
    );

    const handleTouchCancel = useCallback(
      (event: GestureResponderEvent) => {
        touch.current.active = false;
        onTouchCancel?.(event);
      },
      [onTouchCancel],
    );

    const handleTouchEnd = useCallback(
      (event: GestureResponderEvent) => {
        const shouldPlay =
          editable && touch.current.active && !touch.current.moved && sound;
        touch.current.active = false;
        if (shouldPlay) play(shouldPlay);
        onTouchEnd?.(event);
      },
      [editable, onTouchEnd, play, sound],
    );

    return (
      <TextInput
        {...props}
        ref={ref}
        editable={editable}
        onTouchCancel={handleTouchCancel}
        onTouchEnd={handleTouchEnd}
        onTouchMove={handleTouchMove}
        onTouchStart={handleTouchStart}
      />
    );
  },
);

PosTextInput.displayName = "PosTextInput";
