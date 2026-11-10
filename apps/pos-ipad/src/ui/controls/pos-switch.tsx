import { forwardRef, useCallback } from "react";
import { Switch, type SwitchProps } from "react-native";

import {
  type TouchSoundKind,
  usePosSound,
} from "@/ui/feedback/pos-sound-context";
import { usePosThemeVersion } from "@/ui/theme";

export type PosSwitchProps = SwitchProps &
  Readonly<{ sound?: TouchSoundKind | false }>;

/** 保持原生 Switch 属性合同，并在业务状态切换前同步播放轻触反馈。 */
export const PosSwitch = forwardRef<Switch, PosSwitchProps>(
  function PosSwitch(
    {
      disabled = false,
      onValueChange,
      sound = "tap",
      ...props
    },
    ref,
  ) {
    usePosThemeVersion();
    const { play } = usePosSound();

    const handleValueChange = useCallback(
      (nextValue: boolean) => {
        if (disabled) return;
        if (sound) play(sound);
        // 不等待音频，确保开关状态与原有业务回调时序保持一致。
        onValueChange?.(nextValue);
      },
      [disabled, onValueChange, play, sound],
    );

    return (
      <Switch
        {...props}
        disabled={disabled}
        onValueChange={handleValueChange}
        ref={ref}
      />
    );
  },
);

PosSwitch.displayName = "PosSwitch";
