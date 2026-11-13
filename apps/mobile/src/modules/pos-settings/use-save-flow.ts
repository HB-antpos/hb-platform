// 「保存并下发」流程：先弹确认底部弹层，确认后才调用接口，成功后关弹层并回调刷新。
// 失败信息显示在弹层内部（BusinessSheet 是原生 Modal，页面级提示会被它盖住）。
import { useCallback, useRef, useState } from "react";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { buildPatch, type PosSettingChange } from "./logic";
import { resolvePosSettingsErrorMessage } from "./errors";
import type { PosSettingsPatch } from "./types";

export function useSaveFlow<T>(options: {
  changes: readonly PosSettingChange[];
  save: (patch: PosSettingsPatch) => Promise<T>;
  onSaved: (result: T) => void | Promise<void>;
}) {
  const { t, language } = useAppTranslation(["posSettings", "common"]);
  const [open, setOpen] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  // 确认弹层打开期间页面不可编辑，但仍取最新的 changes，避免闭包拿到旧值。
  const optionsRef = useRef(options);
  optionsRef.current = options;
  const busyRef = useRef(false);

  const request = useCallback(() => {
    if (optionsRef.current.changes.length === 0) return;
    setError(null);
    setOpen(true);
  }, []);

  const cancel = useCallback(() => {
    if (busyRef.current) return;
    setOpen(false);
    setError(null);
  }, []);

  const confirm = useCallback(async () => {
    // ref 同步挡住连点：state 在下一次渲染才生效。
    if (busyRef.current) return;
    const { changes, save, onSaved } = optionsRef.current;
    if (changes.length === 0) {
      setOpen(false);
      return;
    }
    busyRef.current = true;
    setBusy(true);
    setError(null);
    try {
      const result = await save(buildPatch(changes));
      setOpen(false);
      await onSaved(result);
    } catch (caught) {
      setError(
        resolvePosSettingsErrorMessage(caught, { t, language, fallbackKey: "posSettings:confirm.saveFailed" }),
      );
    } finally {
      busyRef.current = false;
      setBusy(false);
    }
  }, [language, t]);

  return { open, busy, error, request, cancel, confirm };
}
