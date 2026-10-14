// 抓拍列表缩略图的 React 钩子：把 thumbnail-loader（纯逻辑）接到组件生命周期上。
// 缩略图只放内存（本钩子的 state 与 loader 内部 Map），不走 react-query、不写磁盘、不进日志（source-contract 守住）。
// 离开页面（卸载 / 失焦）、切后台（含 iOS inactive）、点「眼睛」隐藏时立即清空并中止在途请求。
import { useCallback, useEffect, useRef, useState } from "react";
import { AppState } from "react-native";
import { faceMatchApi } from "./api";
import { peekThumbnailsVisible, readThumbnailsVisible, rememberThumbnailsVisible } from "./thumbnail-preference";
import { createThumbnailLoader, type ThumbnailLoader, type ThumbnailStates, type ThumbnailTarget } from "./thumbnail-loader";

const EMPTY_STATES: ThumbnailStates = new Map();

/**
 * 「眼睛」开关：显示 / 隐藏全部缩略图，记住选择（默认显示）。
 * ready=false 表示本机偏好还没读出来：这段时间不加载任何缩略图，避免用户选了「隐藏」却在冷启动时闪一下真实头像。
 */
export function useThumbnailsVisible(): { visible: boolean; ready: boolean; setVisible: (next: boolean) => void } {
  const [visible, setVisibleState] = useState(() => peekThumbnailsVisible() ?? true);
  const [ready, setReady] = useState(() => peekThumbnailsVisible() !== null);
  useEffect(() => {
    let alive = true;
    void readThumbnailsVisible().then((stored) => {
      if (!alive) return;
      setVisibleState(stored);
      setReady(true);
    });
    return () => {
      alive = false;
    };
  }, []);
  const setVisible = useCallback((next: boolean) => {
    setVisibleState(next);
    setReady(true);
    rememberThumbnailsVisible(next);
  }, []);
  return { visible, ready, setVisible };
}

export interface ThumbnailsHandle {
  /** 当前所有已知项的状态（key = thumbnailKey）。 */
  states: ThumbnailStates;
  /** 声明现在需要哪些项（可见在前、预取在后）；停用期间只记下，恢复后补取。 */
  request: (targets: readonly ThumbnailTarget[]) => void;
  /** 下拉刷新：重试失败的项。 */
  retryFailed: () => void;
  /** 立即清空（点「眼睛」隐藏时同步调用，不等重新渲染）。 */
  clear: () => void;
}

/**
 * @param enabled 页面是否应该加载缩略图：「眼睛」开着、偏好已读出、页面在前台聚焦。
 * 应用离开前台时即使 enabled 仍为 true 也会立即清空，回到前台后按最近一次需求重新取。
 */
export function useThumbnails(enabled: boolean): ThumbnailsHandle {
  const [states, setStates] = useState<ThumbnailStates>(EMPTY_STATES);
  const [appActive, setAppActive] = useState(() => AppState.currentState === "active");
  const loaderRef = useRef<ThumbnailLoader | null>(null);
  const wantedRef = useRef<readonly ThumbnailTarget[]>([]);

  useEffect(() => {
    const subscription = AppState.addEventListener("change", (next) => {
      if (next !== "active") {
        // 应用切换器快照前就清空，不等重新渲染。
        loaderRef.current?.clear();
        setAppActive(false);
      } else {
        setAppActive(true);
      }
    });
    return () => subscription.remove();
  }, []);

  const running = enabled && appActive;
  useEffect(() => {
    if (!running) return;
    const loader = createThumbnailLoader({
      fetchBatch: (items, signal) => faceMatchApi.fetchThumbnails(items, signal),
      onChange: setStates,
    });
    loaderRef.current = loader;
    // 重新显示 / 回到前台 / 回到页面后，补取当前可见的那几项。
    loader.request(wantedRef.current);
    return () => {
      loader.dispose();
      if (loaderRef.current === loader) loaderRef.current = null;
      setStates(EMPTY_STATES);
    };
  }, [running]);

  const request = useCallback((targets: readonly ThumbnailTarget[]) => {
    wantedRef.current = targets;
    loaderRef.current?.request(targets);
  }, []);
  const retryFailed = useCallback(() => loaderRef.current?.retryFailed(), []);
  const clear = useCallback(() => loaderRef.current?.clear(), []);
  return { states, request, retryFailed, clear };
}
