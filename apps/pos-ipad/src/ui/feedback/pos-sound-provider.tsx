import {
  createAudioPlayer,
  setAudioModeAsync,
  type AudioPlayer,
} from "expo-audio";
import { useCallback, useEffect, useMemo, useRef, useState } from "react";

import {
  PosSoundContext,
  type PosSoundContextValue,
  type PosSoundCue,
} from "./pos-sound-context";

import {
  readButtonSoundEnabled,
  readSpecialNodeSoundEnabled,
  saveButtonSoundEnabled,
  saveSpecialNodeSoundEnabled,
} from "@/ui/preferences/terminal-ui-preferences";
import { usePosThemeVersion } from "@/ui/theme";

type PosSoundGroup = "button" | "special-node";

const SOUND_GROUP_BY_CUE: Readonly<Record<PosSoundCue, PosSoundGroup>> = {
  tap: "button",
  key: "button",
  navigate: "button",
  danger: "button",
  "query-found": "special-node",
  "query-empty": "special-node",
  "query-error": "special-node",
  "cart-added": "special-node",
  "cart-incremented": "special-node",
  "cart-not-found": "special-node",
  "cart-failed-blocked": "special-node",
};

const AUDIO_MODE = {
  allowsRecording: false,
  interruptionMode: "mixWithOthers",
  playsInSilentMode: true,
  shouldPlayInBackground: false,
} as const;

const SOURCES: Readonly<Record<PosSoundCue, number>> = {
  tap: require("../../../assets/sounds/tap.wav"),
  key: require("../../../assets/sounds/key.wav"),
  navigate: require("../../../assets/sounds/navigate.wav"),
  danger: require("../../../assets/sounds/danger.wav"),
  "query-found": require("../../../assets/sounds/query-found.wav"),
  "query-empty": require("../../../assets/sounds/query-empty.wav"),
  "query-error": require("../../../assets/sounds/query-error.wav"),
  "cart-added": require("../../../assets/sounds/cart-added.wav"),
  "cart-incremented": require("../../../assets/sounds/cart-incremented.wav"),
  "cart-not-found": require("../../../assets/sounds/cart-not-found.wav"),
  "cart-failed-blocked": require("../../../assets/sounds/cart-failed-blocked.wav"),
};

type ActivePlayback = Readonly<{
  group: PosSoundGroup;
  player: AudioPlayer;
  sequence: number;
}>;

export function PosSoundProvider({
  children,
}: Readonly<{ children: React.ReactNode }>) {
  usePosThemeVersion();
  const [buttonSoundEnabled, setButtonSoundEnabledState] = useState(
    readButtonSoundEnabled,
  );
  const [specialNodeSoundEnabled, setSpecialNodeSoundEnabledState] = useState(
    readSpecialNodeSoundEnabled,
  );
  const enabledByGroup = useRef<Record<PosSoundGroup, boolean>>({
    button: buttonSoundEnabled,
    "special-node": specialNodeSoundEnabled,
  });
  const groupPlaybackVersion = useRef<Record<PosSoundGroup, number>>({
    button: 0,
    "special-node": 0,
  });
  const players = useRef<Partial<Record<PosSoundCue, AudioPlayer>>>({});
  const activePlayback = useRef<ActivePlayback | null>(null);
  const playbackSequence = useRef(0);
  const mounted = useRef(false);

  useEffect(() => {
    // 设置过程绝不阻塞首屏；设备或原生模块异常时静默退化为无音效。
    void setAudioModeAsync(AUDIO_MODE).catch(() => undefined);
  }, []);

  useEffect(() => {
    mounted.current = true;
    const createdPlayers: AudioPlayer[] = [];
    for (const cue of Object.keys(SOURCES) as PosSoundCue[]) {
      try {
        const player = createAudioPlayer(SOURCES[cue]);
        players.current[cue] = player;
        createdPlayers.push(player);
      } catch {
        // 个别资源不可用时保留其他 cue，播放时会安全 no-op。
      }
    }

    return () => {
      // 使所有未完成的原生 seek 失效，防止卸载后异步回调反播。
      mounted.current = false;
      playbackSequence.current += 1;
      const playersToPause = new Set(createdPlayers);
      if (activePlayback.current) {
        playersToPause.add(activePlayback.current.player);
      }
      for (const player of playersToPause) {
        try {
          player.pause();
        } catch {
          // 卸载时单个原生播放器异常不能阻止其他资源释放。
        }
      }
      activePlayback.current = null;
      players.current = {};
      for (const player of createdPlayers) {
        try {
          // AudioPlayer 是 SharedObject，release 才能确定性解除原生资源。
          player.release();
        } catch {
          // 单个 SharedObject 释放失败不能影响 React 树退出。
        }
      }
    };
  }, []);

  const requestPlayback = useCallback((cue: PosSoundCue) => {
    const group = SOUND_GROUP_BY_CUE[cue];
    if (!enabledByGroup.current[group]) return;

    const player = players.current[cue];
    if (!player) return;
    const groupVersion = groupPlaybackVersion.current[group];
    const sequence = playbackSequence.current + 1;
    playbackSequence.current = sequence;

    try {
      if (activePlayback.current) {
        activePlayback.current.player.pause();
      }
    } catch {
      // 先前音效的中断失败不应阻止当前 cue 尝试播放。
    }
    activePlayback.current = { group, player, sequence };

    try {
      // expo-audio 的原生 currentTime 实际只读，必须等 seek 完成后再播放。
      void player.seekTo(0).then(
        () => {
          if (
            !mounted.current ||
            !enabledByGroup.current[group] ||
            groupVersion !== groupPlaybackVersion.current[group] ||
            sequence !== playbackSequence.current ||
            players.current[cue] !== player
          ) {
            return;
          }
          try {
            player.play();
          } catch {
            // 播放失败不得影响对应的业务事件。
          }
        },
        () => {
          // seek 被原生层拒绝时安全退化为无音效。
        },
      );
    } catch {
      // seek 同步抛错时也不得影响对应的业务事件。
    }
  }, []);

  const setGroupEnabled = useCallback(
    (group: PosSoundGroup, nextEnabled: boolean) => {
      const changed = enabledByGroup.current[group] !== nextEnabled;
      enabledByGroup.current[group] = nextEnabled;

      if (group === "button") {
        setButtonSoundEnabledState(nextEnabled);
        void saveButtonSoundEnabled(nextEnabled);
      } else {
        setSpecialNodeSoundEnabledState(nextEnabled);
        void saveSpecialNodeSoundEnabled(nextEnabled);
      }

      if (!changed) return;
      groupPlaybackVersion.current[group] += 1;

      if (!nextEnabled) {
        const active = activePlayback.current;
        if (active?.group === group) {
          try {
            active.player.pause();
          } catch {
            // 原生暂停失败不能回滚设置；分组版本仍会阻止晚到的 seek 反播。
          }
          if (activePlayback.current === active) activePlayback.current = null;
        }
        return;
      }

      // 开启时明确试听本组代表音；关闭和同值设置均不发声。
      requestPlayback(group === "button" ? "tap" : "cart-added");
    },
    [requestPlayback],
  );

  const setButtonSoundEnabled = useCallback(
    (nextEnabled: boolean) => setGroupEnabled("button", nextEnabled),
    [setGroupEnabled],
  );

  const setSpecialNodeSoundEnabled = useCallback(
    (nextEnabled: boolean) => setGroupEnabled("special-node", nextEnabled),
    [setGroupEnabled],
  );

  const play = useCallback(
    (cue: PosSoundCue) => requestPlayback(cue),
    [requestPlayback],
  );

  const value = useMemo<PosSoundContextValue>(
    () => ({
      buttonSoundEnabled,
      play,
      setButtonSoundEnabled,
      setSpecialNodeSoundEnabled,
      specialNodeSoundEnabled,
    }),
    [
      buttonSoundEnabled,
      play,
      setButtonSoundEnabled,
      setSpecialNodeSoundEnabled,
      specialNodeSoundEnabled,
    ],
  );

  return (
    <PosSoundContext.Provider value={value}>{children}</PosSoundContext.Provider>
  );
}
