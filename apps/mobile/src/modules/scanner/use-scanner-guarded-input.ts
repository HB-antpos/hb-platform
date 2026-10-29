import { useCallback, useEffect, useMemo, useRef } from "react";
import { Platform } from "react-native";
import { createScannerInputGuard } from "./scanner-input-guard";

interface FocusableInput {
  isFocused: () => boolean;
  blur: () => void;
}

interface UseScannerGuardedInputOptions {
  value: string;
  onChangeText: (value: string) => void;
  /** 识别为扫码后的路由（字段已恢复原值并失焦）。 */
  onScan: (barcode: string) => void;
  enabled?: boolean;
}

/**
 * 给普通可见输入框加扫码防护：扫码枪把条码打进框里时恢复原值、失焦，并把条码交给 onScan。
 * 失焦后焦点回到页面级扫码监听（原生模式）或隐藏扫码输入框（文本模式），后续扫码不再进这个框。
 * 与搜索框专用的 useVisibleSearchScannerInput 一致，只在 Android 上按写入节奏识别。
 */
export function useScannerGuardedInput<T extends FocusableInput>({
  value,
  onChangeText,
  onScan,
  enabled = true,
}: UseScannerGuardedInputOptions) {
  const inputRef = useRef<T>(null);
  const onChangeTextRef = useRef(onChangeText);
  onChangeTextRef.current = onChangeText;
  const onScanRef = useRef(onScan);
  onScanRef.current = onScan;
  const enabledRef = useRef(enabled);
  enabledRef.current = enabled;

  const guard = useMemo(
    () =>
      createScannerInputGuard({
        apply: (next) => onChangeTextRef.current(next),
        onScan: (barcode) => {
          inputRef.current?.blur();
          onScanRef.current(barcode);
        },
        isScannerCandidate: () =>
          enabledRef.current && Platform.OS === "android" && Boolean(inputRef.current?.isFocused()),
      }),
    [],
  );

  useEffect(() => {
    guard.syncValue(value);
  }, [guard, value]);

  useEffect(() => () => guard.dispose(), [guard]);

  const handleChangeText = useCallback((next: string) => guard.change(next), [guard]);

  /** 包装 onSubmitEditing：扫码枪追加的回车先按扫码处理，不触发字段原本的提交。 */
  const guardSubmit = useCallback(
    (submit?: () => void) => () => {
      if (guard.flush()) return;
      submit?.();
    },
    [guard],
  );

  /** 包装 onBlur：扫码进行中失焦（例如回车导致失焦）时不执行字段原本的失焦校验。 */
  const guardBlur = useCallback(
    (blur?: () => void) => () => {
      if (guard.isPending()) return;
      blur?.();
    },
    [guard],
  );

  return { inputRef, onChangeText: handleChangeText, guardSubmit, guardBlur };
}
