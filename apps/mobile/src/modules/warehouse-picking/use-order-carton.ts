import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { clauseSeparator } from "@/shared/i18n/punctuation";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { printErrorDetail } from "../packing/packing-errors";
import { usePackPreferences } from "../packing/pack-preferences";
import { printCartonLabel } from "../packing/pack-print";
import type { PackCartonDetail } from "../packing/types";
import { readPickingError } from "./api-normalization";
import { createOrderCarton, fetchPackProgress, sealOrderCarton } from "./pack-api";
import {
  applyCartonDetail,
  applyCartonSync,
  cartonFromDetail,
  cartonFromProgress,
  cartonPiecesOf,
  classifyCartonWriteError,
  isPackApiMissing,
  resumableCarton,
  summarizeCartons,
} from "./pack-carton-logic";
import type { CartonSync, CurrentCarton, PackProgress, PackProgressCarton } from "./pack-types";
import { pickingErrorMessage } from "./picking-errors";

const PROGRESS_POLL_MS = 8000;

export interface SealInput {
  weightKg: number | null;
  /** 封箱成功后打印箱标签。 */
  print: boolean;
  /** 封箱成功后立即新建下一只箱并设为当前箱；false = 先不开箱（回到不装箱拣货）。 */
  createNext: boolean;
}

export type SealOutcome = { ok: true } | { ok: false; message: string };

export interface OrderCartonController {
  /** 装箱接口可用；后端没有二期接口（404）时为 false，界面完全按旧流程不显示装箱入口。 */
  available: boolean;
  /** 第一次拉取装箱进度已结束（成功或失败），用来决定要不要显示「开始装箱」。 */
  loaded: boolean;
  progress: PackProgress | null;
  carton: CurrentCarton | null;
  /** 当前箱 GUID；写请求发送时读取，没有当前箱为 null（不带 cartonGuid，行为与旧版一致）。 */
  getCartonGuid: () => string | null;
  /** 已装箱 N 只 · M 件（只算有商品的箱，当前箱用实时数字）。 */
  summary: { cartonCount: number; pieceCount: number };
  creating: boolean;
  sealing: boolean;
  sealVisible: boolean;
  setSealVisible: (visible: boolean) => void;
  refresh: () => Promise<PackProgress | null>;
  /** 开始装箱：先看有没有自己以前建的箱可以接着用，没有才新建。 */
  startCarton: () => Promise<{ ok: true; resumed: boolean } | { ok: false }>;
  /** 把进度里的某只箱设为当前箱（核对页「去封箱」：封别人没封的箱 / 自己的旧箱）。 */
  selectCarton: (carton: PackProgressCarton) => void;
  sealCurrent: (input: SealInput) => Promise<SealOutcome>;
  /** 拣货写入成功后更新当前箱；productCode 是这次写入的商品。 */
  applyWrite: (sync: CartonSync | null, productCode: string | null) => void;
  /** 先拣后装：完整箱详情覆盖当前箱。 */
  applyDetail: (detail: PackCartonDetail) => void;
  /** 写入带箱失败：返回要显示的提示（并按需更新 / 丢弃当前箱）；不是装箱相关的错误返回 null。 */
  handleWriteError: (error: unknown) => string | null;
  /** 当前箱里该商品的件数；未知返回 null。 */
  cartonPieces: (productCode: string) => number | null;
}

/**
 * 订单的「当前箱」：拣货页（边拣边装）和先拣后装页共用。
 * - 进入页面拉一次装箱进度，有自己以前建的、装箱中的箱就自动接着用；用户不点「开始装箱」就一直没有当前箱，完全按旧流程拣货；
 * - 每个人各用各的箱（以 isMine 为准）；
 * - 新建 / 封箱 / 打印都在这里，调用方只管 UI。
 */
export function useOrderCarton({
  orderGuid,
  enabled,
  productOf,
  onAuthError,
  notify,
}: {
  orderGuid: string;
  enabled: boolean;
  /** 订单行 → 商品编码：写入响应里没有商品，用来记录「这个商品在箱里几件」。 */
  productOf: (detailGuid: string) => string | null;
  /** 拣货人凭证失效等需要重新确认拣货人的错误：返回 true 表示已处理（调用方已跳走）。 */
  onAuthError: (error: unknown) => boolean;
  notify: (message: string) => void;
}): OrderCartonController {
  const { t, language } = useAppTranslation("warehousePicking");
  const smallLabel = usePackPreferences((state) => state.smallLabel);
  const [progress, setProgress] = useState<PackProgress | null>(null);
  const [carton, setCartonState] = useState<CurrentCarton | null>(null);
  const [available, setAvailable] = useState(true);
  const [loaded, setLoaded] = useState(false);
  const [creating, setCreating] = useState(false);
  const [sealing, setSealing] = useState(false);
  const [sealVisible, setSealVisible] = useState(false);
  const cartonRef = useRef<CurrentCarton | null>(null);
  const resumedRef = useRef(false);
  const busyRef = useRef(false);
  const latestRef = useRef({ productOf, onAuthError, notify, t, language, smallLabel });
  latestRef.current = { productOf, onAuthError, notify, t, language, smallLabel };

  const setCarton = useCallback((next: CurrentCarton | null) => {
    cartonRef.current = next;
    setCartonState(next);
  }, []);

  const refresh = useCallback(async () => {
    try {
      const next = await fetchPackProgress(orderGuid);
      setProgress(next);
      setAvailable(true);
      if (!resumedRef.current) {
        resumedRef.current = true;
        // 进入页面时自动接着用自己以前建的箱；这期间用户已经自己建了箱就不抢。
        const mine = resumableCarton(next);
        if (mine && !cartonRef.current) setCarton(cartonFromProgress(mine));
      }
      return next;
    } catch (error) {
      const info = readPickingError(error);
      // 后端还没有装箱接口：隐藏入口；其他失败（断网等）只是这次没拿到进度，不影响拣货。
      if (isPackApiMissing(info.status, info.code)) setAvailable(false);
      return null;
    } finally {
      setLoaded(true);
    }
  }, [orderGuid, setCarton]);

  useEffect(() => {
    if (!enabled) return;
    void refresh();
  }, [enabled, refresh]);

  // 有当前箱时才定期同步（同事的箱数 / 件数）；不装箱的拣货员不多发请求。
  const hasCarton = carton !== null;
  useEffect(() => {
    if (!enabled || !hasCarton) return;
    const timer = setInterval(() => void refresh(), PROGRESS_POLL_MS);
    return () => clearInterval(timer);
  }, [enabled, hasCarton, refresh]);

  const errorText = useCallback((error: unknown) => {
    const { t: translate, language: lang } = latestRef.current;
    return pickingErrorMessage(error, translate, lang);
  }, []);

  const startCarton = useCallback(async () => {
    if (busyRef.current) return { ok: false as const };
    busyRef.current = true;
    setCreating(true);
    try {
      if (!resumedRef.current) {
        // 进度还没拿到（刚进页面 / 上次失败）：先看看有没有自己没封的箱，避免多建一只。
        await refresh();
        if (cartonRef.current) {
          latestRef.current.notify(latestRef.current.t("carton.resumed", { cartonNo: cartonRef.current.cartonNo }));
          return { ok: true as const, resumed: true };
        }
      }
      const detail = await createOrderCarton(orderGuid);
      setCarton(cartonFromDetail(detail));
      void refresh();
      return { ok: true as const, resumed: false };
    } catch (error) {
      if (!latestRef.current.onAuthError(error)) latestRef.current.notify(errorText(error));
      return { ok: false as const };
    } finally {
      busyRef.current = false;
      setCreating(false);
    }
  }, [errorText, orderGuid, refresh, setCarton]);

  const sealCurrent = useCallback(
    async (input: SealInput): Promise<SealOutcome> => {
      const current = cartonRef.current;
      if (!current || busyRef.current) return { ok: false, message: latestRef.current.t("carton.errors.unavailable") };
      busyRef.current = true;
      setSealing(true);
      try {
        let sealed: PackCartonDetail;
        try {
          sealed = await sealOrderCarton(orderGuid, current.cartonGuid, input.weightKg);
        } catch (error) {
          if (latestRef.current.onAuthError(error)) return { ok: false, message: "" };
          return { ok: false, message: errorText(error) };
        }

        // 封箱已成功：下面打印 / 开新箱任何一步失败都不回滚，只提示。
        setCarton(null);
        const messages: string[] = [];
        if (input.print) {
          try {
            await printCartonLabel(sealed, 1, latestRef.current.smallLabel);
            messages.push(latestRef.current.t("carton.sealedPrinted", { cartonNo: sealed.cartonNo }));
          } catch (error) {
            messages.push(latestRef.current.t("carton.sealedPrintFailed", { cartonNo: sealed.cartonNo, detail: printErrorDetail(error) }));
          }
        } else {
          messages.push(latestRef.current.t("carton.sealedOnly", { cartonNo: sealed.cartonNo }));
        }
        if (input.createNext) {
          try {
            const next = await createOrderCarton(orderGuid);
            setCarton(cartonFromDetail(next));
            messages.push(latestRef.current.t("carton.nextCreated", { cartonNo: next.cartonNo }));
          } catch (error) {
            if (!latestRef.current.onAuthError(error)) {
              messages.push(latestRef.current.t("carton.nextFailed", { detail: errorText(error) }));
            }
          }
        }
        void refresh();
        latestRef.current.notify(messages.join(clauseSeparator(latestRef.current.language)));
        return { ok: true };
      } finally {
        busyRef.current = false;
        setSealing(false);
      }
    },
    [errorText, orderGuid, refresh, setCarton],
  );

  const selectCarton = useCallback(
    (target: PackProgressCarton) => {
      setCarton(cartonFromProgress(target));
    },
    [setCarton],
  );

  const applyWrite = useCallback(
    (sync: CartonSync | null, productCode: string | null) => {
      if (!sync) return;
      setCarton(applyCartonSync(cartonRef.current, sync, productCode));
    },
    [setCarton],
  );

  const applyDetail = useCallback(
    (detail: PackCartonDetail) => {
      setCarton(applyCartonDetail(cartonRef.current, detail));
    },
    [setCarton],
  );

  const handleWriteError = useCallback(
    (error: unknown): string | null => {
      const info = readPickingError(error);
      const failure = classifyCartonWriteError(info.code, info.data);
      if (!failure) return null;
      const { t: translate } = latestRef.current;
      if (failure.kind === "qtyInsufficient") {
        // 以服务端的箱内数量为准：之后的「本箱 X」不再显示过期数字。
        const detailGuid = (info.data as { line?: { detailGuid?: unknown } } | null)?.line?.detailGuid;
        const productCode = typeof detailGuid === "string" ? latestRef.current.productOf(detailGuid) : null;
        if (failure.carton) setCarton(applyCartonSync(cartonRef.current, failure.carton, productCode));
        return translate("carton.errors.qtyInsufficient", { count: failure.pieces ?? 0 });
      }
      if (failure.kind === "cartonUnavailable") {
        // 这只箱已经封了 / 发出 / 不存在：不能再装；丢掉当前箱，重新拉进度看现状。
        setCarton(null);
        void refresh();
        return translate("carton.errors.unavailable");
      }
      return null;
    },
    [refresh, setCarton],
  );

  const getCartonGuid = useCallback(() => cartonRef.current?.cartonGuid ?? null, []);
  const cartonPieces = useCallback((productCode: string) => cartonPiecesOf(cartonRef.current, productCode), []);

  const summary = useMemo(() => summarizeCartons(progress?.cartons ?? [], carton), [carton, progress]);

  return {
    available,
    loaded,
    progress,
    carton,
    getCartonGuid,
    summary,
    creating,
    sealing,
    sealVisible,
    setSealVisible,
    refresh,
    startCarton,
    selectCarton,
    sealCurrent,
    applyWrite,
    applyDetail,
    handleWriteError,
    cartonPieces,
  };
}
