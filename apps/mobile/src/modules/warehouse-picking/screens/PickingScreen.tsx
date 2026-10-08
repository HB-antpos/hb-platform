import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { Pressable, ScrollView, StyleSheet, TextInput, View } from "react-native";
import { ActivityIndicator, Button, Snackbar, Text } from "react-native-paper";
import { SafeAreaView } from "react-native-safe-area-context";
import { useRouter } from "expo-router";
import { useIsFocused } from "@react-navigation/native";
import { useHidBarcodeScanner } from "@/modules/scanner/use-hid-barcode-scanner";
import { playScanFeedbackSound, preloadScanFeedbackSounds } from "@/modules/scanner/scan-sound";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import {
  claimPickSlip,
  deletePickStockout,
  fetchPickProgress,
  joinPickOrder,
  lookupPickCode,
  newClientRequestId,
  postPickRecord,
  putMinOrderQuantity,
  putPickLineTotal,
  putPickStockout,
  resolvePickOrder,
} from "../api";
import { readPickingError } from "../api-normalization";
import { buildPickCodeIndex, resolvePickScan } from "../code-resolver";
import {
  firstOpenLine,
  hasAssignments,
  hasLocation,
  hasMinOrderQuantity,
  incompleteSegments,
  isMySegment,
  isOpenLine,
  isMyLine,
  isStockout,
  lineInScope,
  mergeProgressLines,
  primaryLocation,
  scopeCounts,
  sortLinesByLocation,
  summarizeLines,
  upNextLines,
  type MineContext,
  type SegmentSummary,
} from "../pick-math";
import { hydratePickPreferences, usePickPreferences } from "../pick-preferences";
import {
  activeTeammates,
  claimNoticeFromClaim,
  claimRouteParams,
  shortPickerName,
  teammateByLine,
  type ClaimNotice,
} from "../pick-view-model";
import { usePickerStore } from "../picker-store";
import { PICKER_RECONFIRM_CODES, pickingErrorMessage } from "../picking-errors";
import { PICK_MATCH, PICK_SESSION_STATUS, PICK_SOURCE } from "../types";
import type { PickLineMutationResult, PickParticipant, PickProgressLine, PickScope, PickSessionInfo, PickSheet, PickSheetLine } from "../types";
import { AllLinesSheet } from "../components/AllLinesSheet";
import { CurrentLineCard } from "../components/CurrentLineCard";
import { LineChooserSheet } from "../components/LineChooserSheet";
import { ManualQtySheet } from "../components/ManualQtySheet";
import { MinOrderQtySheet } from "../components/MinOrderQtySheet";
import { PickCameraSheet } from "../components/PickCameraSheet";
import { PickHeader, PickerChip } from "../components/PickHeader";
import { HelpOthersCard } from "../components/IncompleteSegments";
import {
  ASSIGNED_HEADER_SCOPES,
  ASSIGNED_SHEET_SCOPES,
  DEFAULT_SCOPES,
  HELPING_HEADER_SCOPES,
  HELPING_SHEET_SCOPES,
  PickScopeTabs,
} from "../components/PickScopeTabs";
import { RouteSheet } from "../components/RouteSheet";
import { ScanStatusBanner, type ScanBannerState } from "../components/ScanStatusBanner";
import { StockoutSheet } from "../components/StockoutSheet";
import { UpNextList } from "../components/UpNextList";
import { PICK_COLORS } from "../components/pick-theme";
import { PICKING_HOME, pickingRoute, pickingSlipRoute } from "./PickOrderListView";

const PROGRESS_POLL_MS = 8000;
const NETWORK_RETRY_DELAYS_MS = [800, 2000];

type PendingScan = { code: string; matchedBy: number | null; label: string | null };

export function PickingScreen({
  orderGuid,
  focusDetailGuid,
  focusSegmentNo = null,
  claimNotice = null,
  helpRequest = null,
}: {
  orderGuid: string;
  focusDetailGuid: string | null;
  /** 扫分单进入时的段号：“我的”就是这一段。 */
  focusSegmentNo?: number | null;
  claimNotice?: ClaimNotice | null;
  /** 从完成页拦截框点“去帮忙”回来：要帮的段号；nonce 让连点同一段也能重新定位。 */
  helpRequest?: { segmentNo: number; nonce: string } | null;
}) {
  const { t, language } = useAppTranslation("warehousePicking");
  const router = useRouter();
  const focused = useIsFocused();
  const picker = usePickerStore((state) => state.picker);
  const clearPicker = usePickerStore((state) => state.clearPicker);
  const myGuid = picker?.userGuid ?? null;
  const route = usePickPreferences((state) => state.route);
  const prefScope = usePickPreferences((state) => state.scope);
  const setRoute = usePickPreferences((state) => state.setRoute);
  const setPrefScope = usePickPreferences((state) => state.setScope);
  // 有拣货分配的订单：范围是本单临时状态（默认“我的”），不改本机偏好；分段来自扫的分单。
  const [segmentNo, setSegmentNo] = useState<number | null>(focusSegmentNo);
  const [assignedScope, setAssignedScope] = useState<PickScope | null>(focusSegmentNo ? "mine" : null);
  // 正在帮的段：页头临时多一档“帮 某某”，只看那一段；帮忙不改负责人。
  const [helpSegmentNo, setHelpSegmentNo] = useState<number | null>(null);

  const [sheet, setSheet] = useState<PickSheet | null>(null);
  const [lines, setLines] = useState<PickSheetLine[]>([]);
  const [participants, setParticipants] = useState<PickParticipant[]>([]);
  const [session, setSession] = useState<PickSessionInfo | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [reloadKey, setReloadKey] = useState(0);
  const [currentGuid, setCurrentGuid] = useState<string | null>(null);
  const [banner, setBanner] = useState<ScanBannerState>({ kind: "ready" });
  const [scannedChildCode, setScannedChildCode] = useState<string | null>(null);
  const [minOrderSheet, setMinOrderSheet] = useState<{ detailGuid: string; pendingScan: PendingScan | null } | null>(null);
  const [manualGuid, setManualGuid] = useState<string | null>(null);
  const [chooser, setChooser] = useState<{ code: string; detailGuids: string[]; matchedBy: number | null; label: string | null } | null>(null);
  const [allLinesVisible, setAllLinesVisible] = useState(false);
  const [cameraVisible, setCameraVisible] = useState(false);
  const [routeVisible, setRouteVisible] = useState(false);
  const [stockoutGuid, setStockoutGuid] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const [pendingWrites, setPendingWrites] = useState(0);
  const [snackbar, setSnackbar] = useState("");
  const [nowMs, setNowMs] = useState(() => Date.now());

  const linesRef = useRef<PickSheetLine[]>([]);
  const writeChainRef = useRef<Promise<unknown>>(Promise.resolve());
  const pendingWritesRef = useRef(0);
  const lastWriteAtRef = useRef(0);
  linesRef.current = lines;

  useEffect(() => {
    preloadScanFeedbackSounds();
    void hydratePickPreferences();
  }, []);

  const handleAuthError = useCallback(
    (error: unknown) => {
      const { code } = readPickingError(error);
      if (code && PICKER_RECONFIRM_CODES.has(code)) {
        // 员工码凭证失效或员工已不能拣货：回到入口重新确认拣货人。
        clearPicker();
        router.replace(PICKING_HOME);
        return true;
      }
      return false;
    },
    [clearPicker, router],
  );
  // 加载与轮询只依赖订单；回调与文案经 ref 取最新值，避免引用变化导致重复加入或定时器被反复重置。
  const latestRef = useRef({ handleAuthError, t, language, myGuid, segmentNo });
  latestRef.current = { handleAuthError, t, language, myGuid, segmentNo };

  // 加入拣货（已提交的订单转为配货中、登记参与人）并取回拣货单；重复进入是幂等的。
  useEffect(() => {
    let cancelled = false;
    setLoadError(null);
    joinPickOrder(orderGuid)
      .then((result) => {
        if (cancelled) return;
        setSheet(result);
        setLines(result.lines);
        setParticipants(result.participants);
        setSession(result.session);
        // 默认落在当前范围、当前走位下第一条还要拣的行；范围里没有行时退回全部。
        // 有分配的订单默认看“我的”（派给我 / 扫分单领取的那段），没有我的行就看全部。
        const preferences = usePickPreferences.getState();
        const sorted = sortLinesByLocation(result.lines, preferences.route);
        const mineInit: MineContext = { pickerUserGuid: latestRef.current.myGuid, segmentNo: latestRef.current.segmentNo };
        const initScope: PickScope = hasAssignments(result.lines)
          ? result.lines.some((line) => isMyLine(line, mineInit)) ? "mine" : "all"
          : preferences.scope === "mine" ? "all" : preferences.scope;
        const scoped = sorted.filter((line) => lineInScope(line, initScope, mineInit));
        setCurrentGuid((current) => current ?? firstOpenLine(scoped.length > 0 ? scoped : sorted)?.detailGuid ?? null);
      })
      .catch((error) => {
        const latest = latestRef.current;
        if (cancelled || latest.handleAuthError(error)) return;
        setLoadError(pickingErrorMessage(error, latest.t, latest.language));
      });
    return () => {
      cancelled = true;
    };
  }, [orderGuid, reloadKey]);

  // 从完成页点差异行回来时定位到那一行。
  useEffect(() => {
    if (focusDetailGuid) setCurrentGuid(focusDetailGuid);
  }, [focusDetailGuid]);

  const readonly = session?.status === PICK_SESSION_STATUS.submitted;

  const refreshProgress = useCallback(async () => {
    const startedAt = Date.now();
    try {
      const progress = await fetchPickProgress(orderGuid);
      // 轮询期间本机刚写过：这次结果可能早于写入，丢弃，等下一轮。
      if (pendingWritesRef.current > 0 || lastWriteAtRef.current > startedAt) return;
      setLines((current) => mergeProgressLines(current, progress.lines));
      setParticipants(progress.participants);
      setSession(progress.session);
    } catch (error) {
      latestRef.current.handleAuthError(error);
    } finally {
      setNowMs(Date.now());
    }
  }, [orderGuid]);

  // 一起拣：前台时定期同步同事的拣货进度与所在位置。
  useEffect(() => {
    if (!focused || !sheet || readonly) return;
    const timer = setInterval(() => void refreshProgress(), PROGRESS_POLL_MS);
    return () => clearInterval(timer);
  }, [focused, readonly, refreshProgress, sheet]);

  const applyLine = useCallback((line: PickProgressLine) => {
    setLines((current) => mergeProgressLines(current, [line]));
  }, []);

  /**
   * 写入统一排队串行发送：响应按发送顺序回来，后发的结果不会被先发的旧结果覆盖。
   * 断网（没有响应）时用同一请求编号重试，服务端按编号去重不会重复计数。
   */
  const runWrite = useCallback(
    (task: () => Promise<PickLineMutationResult>, optimistic?: { detailGuid: string; delta: number }) => {
      if (optimistic && optimistic.delta !== 0) {
        setLines((current) =>
          current.map((line) =>
            line.detailGuid === optimistic.detailGuid ? { ...line, pickedTotal: line.pickedTotal + optimistic.delta } : line,
          ),
        );
      }
      pendingWritesRef.current += 1;
      setPendingWrites(pendingWritesRef.current);

      const run = async (): Promise<PickLineMutationResult | null> => {
        try {
          const result = await withNetworkRetry(task);
          applyLine(result.line);
          return result;
        } catch (error) {
          if (!handleAuthError(error)) {
            const info = readPickingError(error);
            const conflictLine = (info.data as { line?: PickProgressLine } | null)?.line;
            if (conflictLine?.detailGuid) applyLine(conflictLine);
            playScanFeedbackSound("error");
            setSnackbar(info.status ? pickingErrorMessage(error, t, language) : t("picking.syncFailed"));
          }
          // 失败后以服务端为准重新同步、撤销乐观加上的数量；放到本次写入收尾之后再发，避免被判为过期结果丢弃。
          setTimeout(() => void refreshProgress(), 0);
          return null;
        } finally {
          lastWriteAtRef.current = Date.now();
          pendingWritesRef.current -= 1;
          setPendingWrites(pendingWritesRef.current);
        }
      };
      const next = writeChainRef.current.then(run, run);
      writeChainRef.current = next.catch(() => undefined);
      return next;
    },
    [applyLine, handleAuthError, language, refreshProgress, t],
  );

  const recordScan = useCallback(
    (line: PickSheetLine, scan: PendingScan, pieces: number, explicitPieces: boolean) => {
      const clientRequestId = newClientRequestId();
      return runWrite(
        () =>
          postPickRecord(orderGuid, {
            detailGuid: line.detailGuid,
            source: PICK_SOURCE.scan,
            scannedCode: scan.code,
            matchedBy: scan.matchedBy,
            pieces: explicitPieces ? pieces : null,
            clientRequestId,
          }),
        { detailGuid: line.detailGuid, delta: pieces },
      );
    },
    [orderGuid, runWrite],
  );

  const scanLine = useCallback(
    (detailGuid: string, scan: PendingScan) => {
      const line = linesRef.current.find((item) => item.detailGuid === detailGuid);
      if (!line) return;
      setCurrentGuid(detailGuid);
      setScannedChildCode(scan.matchedBy === PICK_MATCH.setChild ? scan.code : null);
      if (!hasMinOrderQuantity(line)) {
        playScanFeedbackSound("blocked");
        setBanner({ kind: "warning", title: t("picking.feedbackMinOrderMissing"), message: t("picking.feedbackMinOrderMissingHint") });
        setMinOrderSheet({ detailGuid, pendingScan: scan });
        return;
      }

      const pieces = line.minOrderQuantity as number;
      playScanFeedbackSound("added");
      if (isStockout(line)) {
        // 标了没货的行又扫到货：照常计入，服务端同时取消没货标记。
        setBanner({ kind: "success", title: t("picking.feedbackAdded", { pieces }), message: t("picking.stockoutAutoCleared") });
      } else if (scan.matchedBy === PICK_MATCH.setChild) {
        setBanner({
          kind: "success",
          title: t("picking.feedbackAddedSet", { pieces }),
          message: t("picking.feedbackScannedChild", { name: scan.label || scan.code }),
        });
      } else {
        setBanner({
          kind: "success",
          title: t("picking.feedbackAdded", { pieces }),
          message: scan.matchedBy === PICK_MATCH.multiCode ? t("picking.feedbackMultiCode") : line.productName || line.productCode,
        });
      }
      void recordScan(line, scan, pieces, false);
    },
    [recordScan, t],
  );

  const codeIndex = useMemo(() => buildPickCodeIndex(sheet?.codes ?? []), [sheet]);
  const sortedLines = useMemo(() => sortLinesByLocation(lines, route), [lines, route]);
  const assigned = useMemo(() => hasAssignments(lines), [lines]);
  const mine = useMemo<MineContext>(() => ({ pickerUserGuid: myGuid, segmentNo, helpSegmentNo }), [helpSegmentNo, myGuid, segmentNo]);
  const counts = useMemo(() => scopeCounts(lines, mine), [lines, mine]);
  // 有分配：我的 / 全部（全部明细里还能切有货位 / 无货位）；没有分配：沿用本机偏好（全部 / 有货位 / 无货位）。
  const scope: PickScope = assigned
    ? assignedScope ?? (counts.mine > 0 ? "mine" : "all")
    : prefScope === "mine" ? "all" : prefScope;
  const scopedLines = useMemo(() => sortedLines.filter((line) => lineInScope(line, scope, mine)), [mine, scope, sortedLines]);

  /**
   * 领取（或确认）某一段后：“我的”切到这段、定位到这段第一条待拣行，并提示领取结果。
   * 已被别人领取时只提示，照样可以帮忙拣，记录记在扫码人名下。
   */
  const applyClaim = useCallback(
    (notice: ClaimNotice) => {
      setSegmentNo(notice.segmentNo);
      setAssignedScope("mine");
      const segmentLines = sortLinesByLocation(linesRef.current, route).filter((line) => line.assignmentSegmentNo === notice.segmentNo);
      const target = firstOpenLine(segmentLines);
      if (target) {
        setCurrentGuid(target.detailGuid);
        setScannedChildCode(null);
      }
      const values = { no: notice.segmentNo, count: notice.segmentCount, lines: notice.lineCount };
      if (notice.kind === "other") {
        setBanner({
          kind: "warning",
          title: t("picking.slipOther", { ...values, name: notice.claimerName ?? "—" }),
          message: t("picking.slipOtherHint"),
        });
      } else {
        setBanner({
          kind: "success",
          title: notice.kind === "now" ? t("picking.slipClaimedNow", values) : t("picking.slipMine", values),
          message: t("picking.slipLines", values),
        });
      }
    },
    [route, t],
  );

  /** 去帮某一段：切到“帮 某某”，定位到那段第一条待拣行，并说明记录记在自己名下。 */
  const startHelp = useCallback(
    (targetSegmentNo: number) => {
      const segmentLines = sortLinesByLocation(linesRef.current, route).filter((line) => line.assignmentSegmentNo === targetSegmentNo);
      if (segmentLines.length === 0) return;
      setHelpSegmentNo(targetSegmentNo);
      setAssignedScope("help");
      const target = firstOpenLine(segmentLines);
      if (target) {
        setCurrentGuid(target.detailGuid);
        setScannedChildCode(null);
      }
      const assignee = segmentLines.find((line) => line.assigneeName)?.assigneeName;
      setBanner({
        kind: "info",
        title: t("picking.helpStarted", {
          name: assignee ? shortPickerName(assignee) : t("finish.segmentClaimable"),
          no: targetSegmentNo,
        }),
        message: t("picking.helpStartedHint"),
      });
    },
    [route, t],
  );

  // 从完成页点“去帮忙”回来：拣货单加载后应用一次。
  const appliedHelpRef = useRef<string | null>(null);
  useEffect(() => {
    if (!sheet || !helpRequest || appliedHelpRef.current === helpRequest.nonce) return;
    appliedHelpRef.current = helpRequest.nonce;
    startHelp(helpRequest.segmentNo);
  }, [helpRequest, sheet, startHelp]);

  // 从订单列表扫分单进来：拣货单加载后应用一次领取提示。
  const appliedNoticeRef = useRef<ClaimNotice | null>(null);
  useEffect(() => {
    if (!sheet || !claimNotice || appliedNoticeRef.current === claimNotice) return;
    appliedNoticeRef.current = claimNotice;
    applyClaim(claimNotice);
  }, [applyClaim, claimNotice, sheet]);

  const handleScan = useCallback(
    (raw: string) => {
      if (!sheet) return;
      setNowMs(Date.now());
      if (readonly) {
        playScanFeedbackSound("blocked");
        setBanner({ kind: "warning", title: t("picking.readonlyTitle", { name: session?.submittedByName ?? "" }), message: t("picking.readonlyHint") });
        return;
      }

      const resolution = resolvePickScan(raw, codeIndex, currentGuid);
      switch (resolution.kind) {
        case "slip": {
          // 拣货中扫到分单：领取这一段；是别的订单的分单就切过去。
          playScanFeedbackSound("found");
          claimPickSlip(resolution.code)
            .then((claim) => {
              if (claim.orderGuid.toLowerCase() !== orderGuid.toLowerCase()) {
                router.replace(pickingSlipRoute(claim.orderGuid, claimRouteParams(claim)));
                return;
              }
              applyClaim(claimNoticeFromClaim(claim));
              void refreshProgress();
            })
            .catch((error) => {
              if (handleAuthError(error)) return;
              playScanFeedbackSound("not_found");
              setBanner({ kind: "error", title: pickingErrorMessage(error, t, language), message: null, code: resolution.code, product: null });
            });
          return;
        }
        case "order": {
          if (sheet.orderNo && resolution.orderNo === sheet.orderNo.toUpperCase()) {
            playScanFeedbackSound("found");
            setBanner({ kind: "ready" });
            return;
          }
          playScanFeedbackSound("multiple");
          setBanner({
            kind: "info",
            title: t("picking.otherOrder", { orderNo: resolution.orderNo }),
            actionLabel: t("picking.openOrder"),
            onAction: () => {
              resolvePickOrder(resolution.code)
                .then((guid) => router.replace(pickingRoute(guid)))
                .catch((error) => setSnackbar(pickingErrorMessage(error, t, language)));
            },
          });
          return;
        }
        case "location": {
          const candidates = resolution.detailGuids
            .map((guid) => linesRef.current.find((line) => line.detailGuid === guid))
            .filter((line): line is PickSheetLine => Boolean(line));
          const target = candidates.find(isOpenLine) ?? candidates[0];
          if (!target) return;
          playScanFeedbackSound("found");
          setCurrentGuid(target.detailGuid);
          setScannedChildCode(null);
          setBanner({ kind: "info", title: t("picking.feedbackLocation", { location: target.locationCode ?? resolution.code }) });
          return;
        }
        case "multiple":
          playScanFeedbackSound("multiple");
          setChooser({ code: resolution.code, detailGuids: resolution.detailGuids, matchedBy: resolution.matchedBy, label: resolution.label });
          return;
        case "line":
          scanLine(resolution.detailGuid, { code: resolution.code, matchedBy: resolution.matchedBy, label: resolution.label });
          return;
        case "none": {
          playScanFeedbackSound("not_found");
          const code = resolution.code;
          setBanner({ kind: "error", title: t("picking.notInOrder"), message: t("picking.notInOrderHint"), code, product: null });
          // 查一下扫到的是什么商品，只用于提示；查不到保持只显示条码。
          lookupPickCode(code)
            .then((product) =>
              setBanner((current) =>
                current.kind === "error" && current.code === code
                  ? { ...current, product: { name: product.productName, image: product.productImage, location: product.locationCode } }
                  : current,
              ),
            )
            .catch(() => undefined);
          return;
        }
      }
    },
    [applyClaim, codeIndex, currentGuid, handleAuthError, language, orderGuid, readonly, refreshProgress, router, scanLine, session?.submittedByName, sheet, t],
  );

  const sheetsOpen = Boolean(minOrderSheet || manualGuid || chooser || allLinesVisible || cameraVisible || routeVisible || stockoutGuid);
  const hid = useHidBarcodeScanner({ enabled: focused && Boolean(sheet) && !sheetsOpen, onScan: handleScan });

  const currentLine = lines.find((line) => line.detailGuid === currentGuid) ?? null;
  // 进度、剩余行与“接下来”只算当前范围；扫到范围外的行照常计入并跳过去。
  const summary = summarizeLines(scopedLines);
  const openCount = scopedLines.filter(isOpenLine).length;
  const stockoutPieces = scopedLines
    .filter(isStockout)
    .reduce((sum, line) => sum + (line.orderedQuantity - line.pickedTotal), 0);
  const teammates = activeTeammates(participants, myGuid, nowMs);
  const teammateMap = teammateByLine(participants, myGuid, nowMs);
  const upNext = upNextLines(scopedLines, currentGuid, 3);
  // 我的（或正在帮的）那段拣完了：列出别人还没拣完的段，提示去帮忙。
  const helpTargets: SegmentSummary[] =
    assigned && !readonly && (scope === "mine" || scope === "help") && scopedLines.length > 0 && openCount === 0
      ? incompleteSegments(lines).filter((segment) => !isMySegment(segment, mine) && segment.segmentNo !== helpSegmentNo)
      : [];
  const helpAssignee = helpSegmentNo == null ? null : lines.find((line) => line.assignmentSegmentNo === helpSegmentNo && line.assigneeName)?.assigneeName;
  const helpName = helpAssignee ? shortPickerName(helpAssignee) : t("finish.segmentClaimable");
  const headerScopes = !assigned ? DEFAULT_SCOPES : helpSegmentNo != null ? HELPING_HEADER_SCOPES : ASSIGNED_HEADER_SCOPES;
  const sheetScopes = !assigned ? DEFAULT_SCOPES : helpSegmentNo != null ? HELPING_SHEET_SCOPES : ASSIGNED_SHEET_SCOPES;

  const changeScope = (next: PickScope) => {
    if (assigned) setAssignedScope(next);
    else setPrefScope(next);
    const nextScoped = sortedLines.filter((line) => lineInScope(line, next, mine));
    if (currentLine && !lineInScope(currentLine, next, mine)) {
      const target = firstOpenLine(nextScoped);
      if (target) {
        setCurrentGuid(target.detailGuid);
        setScannedChildCode(null);
        setBanner({ kind: "ready" });
      }
    }
  };

  const clearStockout = async (detailGuid: string) => {
    setSaving(true);
    const result = await runWrite(() => deletePickStockout(orderGuid, detailGuid));
    setSaving(false);
    if (result) {
      setCurrentGuid(detailGuid);
      setBanner({ kind: "info", title: t("picking.stockoutCleared") });
    }
  };

  const confirmStockout = async (reason: number) => {
    const line = linesRef.current.find((item) => item.detailGuid === stockoutGuid);
    if (!line) return;
    // 标记后跳到本范围内按走位顺序的下一条待拣行（标记前算好，当前行本身会被排除）。
    const next = upNextLines(scopedLines, line.detailGuid, 1)[0] ?? null;
    setSaving(true);
    const result = await runWrite(() => putPickStockout(orderGuid, line.detailGuid, reason));
    setSaving(false);
    // 弹层是原生 Modal，会压住报错提示条：失败也先关掉，让拣货员看到原因（如刚被同事拣齐）。
    setStockoutGuid(null);
    if (!result) return;
    const short = Math.max(0, line.orderedQuantity - result.line.pickedTotal);
    const location = primaryLocation(line.locationCode);
    setBanner({
      kind: "stockout",
      title: hasLocation(line)
        ? t("picking.stockoutMarked", { location, count: short })
        : t("picking.stockoutMarkedNoLocation", { count: short }),
      message: t("picking.stockoutMarkedHint"),
      actionLabel: t("picking.stockoutUndo"),
      onAction: () => void clearStockout(line.detailGuid),
    });
    if (next) {
      setCurrentGuid(next.detailGuid);
      setScannedChildCode(null);
    }
  };

  const changeButton = (source: number) => {
    if (!currentLine || !hasMinOrderQuantity(currentLine)) return;
    const pack = currentLine.minOrderQuantity as number;
    const delta = source === PICK_SOURCE.decrement ? -Math.min(pack, currentLine.pickedTotal) : pack;
    if (delta === 0) return;
    const clientRequestId = newClientRequestId();
    const detailGuid = currentLine.detailGuid;
    setBanner({ kind: "ready" });
    void runWrite(() => postPickRecord(orderGuid, { detailGuid, source, clientRequestId }), { detailGuid, delta });
  };

  const confirmManual = async (total: number) => {
    const detailGuid = manualGuid;
    if (!detailGuid) return;
    const clientRequestId = newClientRequestId();
    setSaving(true);
    // 期望合计在真正发送时读取：前面排队的写入完成后本地合计已校正为服务端值。
    const result = await runWrite(() => {
      const expectedTotal = linesRef.current.find((line) => line.detailGuid === detailGuid)?.pickedTotal ?? 0;
      return putPickLineTotal(orderGuid, detailGuid, { total, expectedTotal, clientRequestId });
    });
    setSaving(false);
    if (result) {
      setManualGuid(null);
      setBanner({ kind: "ready" });
    }
  };

  const saveMinOrder = async (value: number) => {
    if (!minOrderSheet) return;
    const { detailGuid, pendingScan } = minOrderSheet;
    const line = linesRef.current.find((item) => item.detailGuid === detailGuid);
    if (!line) return;
    setSaving(true);
    try {
      const saved = await putMinOrderQuantity(orderGuid, detailGuid, value);
      // 同一商品在本单可能出现多行，一起更新中包数。
      setLines((current) =>
        current.map((item) =>
          item.productCode.toLowerCase() === line.productCode.toLowerCase() ? { ...item, minOrderQuantity: saved } : item,
        ),
      );
      setMinOrderSheet(null);
      if (pendingScan) {
        playScanFeedbackSound("added");
        setBanner({ kind: "success", title: t("picking.feedbackAdded", { pieces: saved }), message: line.productName || line.productCode });
        void recordScan({ ...line, minOrderQuantity: saved }, pendingScan, saved, false);
      } else {
        setBanner({ kind: "ready" });
      }
    } catch (error) {
      if (!handleAuthError(error)) {
        const info = readPickingError(error);
        const current = (info.data as { minOrderQuantity?: number } | null)?.minOrderQuantity;
        if (typeof current === "number") {
          setLines((items) => items.map((item) => (item.detailGuid === detailGuid ? { ...item, minOrderQuantity: current } : item)));
          setMinOrderSheet(null);
        }
        setSnackbar(pickingErrorMessage(error, t, language));
      }
    } finally {
      setSaving(false);
    }
  };

  const countOnePiece = () => {
    if (!minOrderSheet) return;
    const line = linesRef.current.find((item) => item.detailGuid === minOrderSheet.detailGuid);
    const scan = minOrderSheet.pendingScan;
    setMinOrderSheet(null);
    if (!line || !scan) return;
    playScanFeedbackSound("added");
    setBanner({ kind: "success", title: t("picking.feedbackAdded", { pieces: 1 }), message: line.productName || line.productCode });
    void recordScan(line, scan, 1, true);
  };

  const goBack = () => (router.canGoBack() ? router.back() : router.replace(PICKING_HOME));
  const switchPicker = () => {
    clearPicker();
    router.replace(PICKING_HOME);
  };

  if (!sheet) {
    return (
      <SafeAreaView edges={["top", "bottom", "left", "right"]} style={styles.center}>
        {loadError ? (
          <>
            <Text accessibilityLiveRegion="polite" style={styles.centerText}>
              {loadError}
            </Text>
            <Button mode="contained" onPress={() => setReloadKey((key) => key + 1)}>
              {t("actions.retry")}
            </Button>
            <Button onPress={goBack}>{t("actions.back")}</Button>
          </>
        ) : (
          <ActivityIndicator />
        )}
      </SafeAreaView>
    );
  }

  const progressRatio = summary.orderedPieces > 0 ? Math.min(1, summary.pickedPieces / summary.orderedPieces) : 0;
  const stockoutRatio =
    summary.orderedPieces > 0 ? Math.min(1 - progressRatio, stockoutPieces / summary.orderedPieces) : 0;
  const progressText = t("picking.progress", {
    lines: summary.completeLineCount,
    totalLines: summary.lineCount,
    pieces: summary.pickedPieces,
    totalPieces: summary.orderedPieces,
  });
  const chooserLines = chooser
    ? chooser.detailGuids
        .map((guid) => lines.find((line) => line.detailGuid === guid))
        .filter((line): line is PickSheetLine => Boolean(line))
    : [];
  const manualLine = manualGuid ? lines.find((line) => line.detailGuid === manualGuid) ?? null : null;
  const minOrderLine = minOrderSheet ? lines.find((line) => line.detailGuid === minOrderSheet.detailGuid) ?? null : null;
  const stockoutLine = stockoutGuid ? lines.find((line) => line.detailGuid === stockoutGuid) ?? null : null;

  return (
    <SafeAreaView edges={["top", "bottom", "left", "right"]} style={styles.screen}>
      <PickHeader
        title={sheet.orderNo || t("title")}
        monoTitle
        subtitle={t("picking.subtitle", { store: sheet.storeName || sheet.storeCode || "—", count: lines.length })}
        onBack={goBack}
        backLabel={t("actions.back")}
        right={
          picker ? (
            <PickerChip
              name={picker.name}
              teammates={teammates.map((item) => item.pickerName)}
              label={
                teammates.length > 0
                  ? `${shortPickerName(picker.name)} ${t("picking.othersCount", { count: teammates.length })}`
                  : shortPickerName(picker.name)
              }
              accessibilityLabel={t("picker.switchPicker", { name: picker.name })}
              onPress={switchPicker}
            />
          ) : null
        }
      >
        <View style={styles.progressBlock}>
          <PickScopeTabs value={scope} counts={counts} scopes={headerScopes} helpName={helpName} onChange={changeScope} />
          <View style={styles.progressRow}>
            <Text numberOfLines={1} style={[styles.progressText, styles.progressMain]}>
              {scope === "all"
                ? progressText
                : t("picking.scopeProgress", {
                    scope:
                      scope === "help"
                        ? t("picking.scopeHelp", { name: helpName })
                        : t(scope === "mine" ? "picking.scopeMine" : scope === "located" ? "picking.scopeLocated" : "picking.scopeUnlocated"),
                    progress: progressText,
                  })}
            </Text>
            <View style={styles.leftRow}>
              {pendingWrites > 0 ? <ActivityIndicator size={10} /> : null}
              {summary.stockoutLineCount > 0 ? (
                <Text style={[styles.progressText, styles.stockoutText]}>{t("picking.stockoutCount", { count: summary.stockoutLineCount })}</Text>
              ) : null}
              <Text style={styles.progressText}>{t("picking.left", { count: openCount })}</Text>
            </View>
          </View>
          <View style={styles.track}>
            <View style={[styles.fill, { width: `${Math.round(progressRatio * 100)}%` }]} />
            {stockoutRatio > 0 ? <View style={[styles.fillStockout, { width: `${Math.max(1, Math.round(stockoutRatio * 100))}%` }]} /> : null}
          </View>
        </View>
      </PickHeader>

      <ScrollView contentContainerStyle={styles.content} keyboardShouldPersistTaps="handled">
        {readonly ? (
          <ScanStatusBanner
            state={{ kind: "warning", title: t("picking.readonlyTitle", { name: session?.submittedByName ?? "" }), message: t("picking.readonlyHint") }}
            readyTitle=""
            readyHint=""
            cameraLabel={t("picking.cameraLabel")}
            onCamera={() => setCameraVisible(true)}
            locationLabel={(location) => t("picking.notInOrderLocation", { location })}
          />
        ) : (
          <ScanStatusBanner
            state={banner}
            readyTitle={t("picking.ready")}
            readyHint={t("picking.readyHint")}
            cameraLabel={t("picking.cameraLabel")}
            onCamera={() => setCameraVisible(true)}
            locationLabel={(location) => t("picking.notInOrderLocation", { location })}
          />
        )}

        {currentLine ? (
          <CurrentLineCard
            line={currentLine}
            myUserGuid={myGuid}
            scannedChildCode={scannedChildCode}
            readonly={readonly}
            busy={saving}
            onPlus={() => changeButton(PICK_SOURCE.increment)}
            onMinus={() => changeButton(PICK_SOURCE.decrement)}
            onManual={() => setManualGuid(currentLine.detailGuid)}
            onSetMinOrder={() => setMinOrderSheet({ detailGuid: currentLine.detailGuid, pendingScan: null })}
            onStockout={() => setStockoutGuid(currentLine.detailGuid)}
            onClearStockout={() => void clearStockout(currentLine.detailGuid)}
          />
        ) : null}

        {helpTargets.length > 0 ? (
          <HelpOthersCard segments={helpTargets} mine={mine} onHelp={(segment) => startHelp(segment.segmentNo)} />
        ) : null}

        <UpNextList
          current={currentLine}
          lines={upNext}
          route={route}
          scope={scope}
          teammateByLine={teammateMap}
          onRoutePress={() => setRouteVisible(true)}
          onSelect={(detailGuid) => {
            setCurrentGuid(detailGuid);
            setScannedChildCode(null);
            setBanner({ kind: "ready" });
          }}
        />
      </ScrollView>

      <View style={styles.footer}>
        <Pressable accessibilityRole="button" onPress={() => setAllLinesVisible(true)} style={styles.secondaryButton}>
          <Text style={styles.secondaryText}>{t("picking.allLines")}</Text>
        </Pressable>
        <Pressable
          accessibilityRole="button"
          onPress={() => router.push(pickingRoute(orderGuid, "/finish"))}
          style={styles.primaryButton}
        >
          <Text style={styles.primaryText}>{t("picking.finish")}</Text>
        </Pressable>
      </View>

      {minOrderLine ? (
        <MinOrderQtySheet
          key={minOrderLine.detailGuid}
          visible
          line={minOrderLine}
          withPendingScan={Boolean(minOrderSheet?.pendingScan)}
          saving={saving}
          onDismiss={() => setMinOrderSheet(null)}
          onSave={(value) => void saveMinOrder(value)}
          onCountOne={countOnePiece}
        />
      ) : null}
      {manualLine ? (
        <ManualQtySheet
          key={manualLine.detailGuid}
          visible
          line={manualLine}
          saving={saving}
          onDismiss={() => setManualGuid(null)}
          onConfirm={(total) => void confirmManual(total)}
        />
      ) : null}
      <LineChooserSheet
        visible={Boolean(chooser)}
        code={chooser?.code}
        lines={chooserLines}
        onDismiss={() => setChooser(null)}
        onPick={(detailGuid) => {
          const scan = chooser;
          setChooser(null);
          if (scan) scanLine(detailGuid, { code: scan.code, matchedBy: scan.matchedBy, label: scan.label });
        }}
      />
      {stockoutLine ? (
        <StockoutSheet
          key={stockoutLine.detailGuid}
          visible
          line={stockoutLine}
          saving={saving}
          onDismiss={() => setStockoutGuid(null)}
          onConfirm={(reason) => void confirmStockout(reason)}
        />
      ) : null}
      <RouteSheet visible={routeVisible} value={route} onChange={setRoute} onDismiss={() => setRouteVisible(false)} />
      <AllLinesSheet
        visible={allLinesVisible}
        lines={scopedLines}
        scope={scope}
        scopeCounts={counts}
        scopes={sheetScopes}
        helpName={helpName}
        route={route}
        onScopeChange={changeScope}
        onRoutePress={() => setRoute(route === "m" ? "s" : "m")}
        onDismiss={() => setAllLinesVisible(false)}
        onPick={(detailGuid) => {
          setAllLinesVisible(false);
          setCurrentGuid(detailGuid);
          setScannedChildCode(null);
          setBanner({ kind: "ready" });
        }}
      />
      <PickCameraSheet
        visible={cameraVisible}
        title={t("picking.cameraTitle")}
        onDismiss={() => setCameraVisible(false)}
        onBarcode={handleScan}
      />
      <Snackbar visible={Boolean(snackbar)} onDismiss={() => setSnackbar("")} duration={3500} style={styles.snackbar}>
        {snackbar}
      </Snackbar>
      {hid.textInputProps ? (
        <TextInput {...hid.textInputProps} style={styles.hiddenInput} accessible={false} importantForAccessibility="no-hide-descendants" />
      ) : null}
    </SafeAreaView>
  );
}

async function withNetworkRetry<T>(task: () => Promise<T>): Promise<T> {
  for (let attempt = 0; ; attempt += 1) {
    try {
      return await task();
    } catch (error) {
      const hasResponse = Boolean((error as { response?: unknown } | null)?.response);
      if (hasResponse || attempt >= NETWORK_RETRY_DELAYS_MS.length) throw error;
      await new Promise((resolve) => setTimeout(resolve, NETWORK_RETRY_DELAYS_MS[attempt]));
    }
  }
}

const styles = StyleSheet.create({
  screen: { flex: 1, backgroundColor: PICK_COLORS.background },
  center: { flex: 1, alignItems: "center", justifyContent: "center", gap: 12, padding: 24, backgroundColor: PICK_COLORS.background },
  centerText: { textAlign: "center", color: PICK_COLORS.ink },
  progressBlock: { paddingHorizontal: 12, paddingBottom: 10, gap: 8 },
  progressRow: { flexDirection: "row", justifyContent: "space-between", alignItems: "center" },
  progressText: { fontSize: 12, lineHeight: 16, color: PICK_COLORS.textSecondary, fontVariant: ["tabular-nums"] },
  leftRow: { flexDirection: "row", alignItems: "center", gap: 8 },
  track: { height: 6, borderRadius: 3, backgroundColor: PICK_COLORS.outlineMuted, overflow: "hidden", flexDirection: "row" },
  fill: { height: 6, backgroundColor: PICK_COLORS.action },
  fillStockout: { height: 6, backgroundColor: PICK_COLORS.danger },
  progressMain: { flexShrink: 1 },
  stockoutText: { color: PICK_COLORS.danger, fontWeight: "600" },
  content: { padding: 12, gap: 10 },
  // 底栏必须留在文档流里：Android edge-to-edge 下 SafeAreaView 靠 padding 避开系统导航栏，
  // position: absolute 的子节点不吃父级 padding，会贴到屏幕最底被导航栏盖住、点不到。
  footer: {
    flexDirection: "row",
    gap: 8,
    paddingHorizontal: 12,
    paddingVertical: 8,
    backgroundColor: PICK_COLORS.white,
    borderTopWidth: StyleSheet.hairlineWidth,
    borderTopColor: PICK_COLORS.outlineMuted,
  },
  secondaryButton: { flex: 1, height: 48, borderRadius: 8, borderWidth: 1, borderColor: PICK_COLORS.outline, backgroundColor: PICK_COLORS.white, alignItems: "center", justifyContent: "center" },
  secondaryText: { fontSize: 15, fontWeight: "600", color: PICK_COLORS.ink },
  primaryButton: { flex: 1.6, height: 48, borderRadius: 8, backgroundColor: PICK_COLORS.ink, alignItems: "center", justifyContent: "center" },
  primaryText: { fontSize: 15, fontWeight: "700", color: PICK_COLORS.white },
  snackbar: { marginBottom: 72 },
  hiddenInput: { position: "absolute", width: 1, height: 1, opacity: 0, left: -100 },
});
