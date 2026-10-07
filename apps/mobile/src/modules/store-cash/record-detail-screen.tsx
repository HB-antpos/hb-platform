// 存款 / 支出详情：金额、日期、存单与图片、作废信息；有作废权限（服务端 canVoid）时可作废。
// 图片带几分钟有效的私有签名地址：每次进详情都重新取，点开前发现快过期或加载失败就再取一次。
import { useCallback, useRef, useState } from "react";
import { Image, Pressable, ScrollView, StyleSheet, View } from "react-native";
import { useMutation, useQuery } from "@tanstack/react-query";
import { useLocalSearchParams, useRouter, type Href } from "expo-router";
import { ActivityIndicator, Button, Text } from "react-native-paper";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import { useAuthStore } from "@/store/auth-store";
import { canViewStoreCash } from "./access";
import {
  fetchCashDepositDetail,
  fetchCashExpenseDetail,
  voidCashDeposit,
  voidCashExpense,
} from "./api";
import { anyAttachmentUrlStale, toViewerImages } from "./attachments";
import { formatUtcInZone } from "./dates";
import { isRecordNotFoundError, resolveCashErrorMessage } from "./errors";
import { ImageViewerModal } from "./ImageViewerModal";
import { categoryLabel } from "./labels";
import { formatAud } from "./money";
import { storeCashKeys } from "./query-keys";
import { firstParam, parseRecordDetailKind } from "./routes";
import type { CashAttachment, CashDepositDetail, CashExpenseDetail } from "./types";
import { useCashContextQuery, useInvalidateCashData } from "./use-store-cash";
import { VoidReasonDialog } from "./VoidReasonDialog";
import { CashCard, CashScreenFrame, NoticeBanner, StatusChip, ValueRow, cashUiStyles } from "./ui";

const THUMB = 84;

export function RecordDetailScreen() {
  const { t } = useAppTranslation(["storeCash", "common"]);
  const router = useRouter();
  const params = useLocalSearchParams<{
    kind?: string | string[];
    guid?: string | string[];
    storeCode?: string | string[];
    created?: string | string[];
  }>();
  const kind = parseRecordDetailKind(params.kind);
  const guid = firstParam(params.guid);
  const storeCode = firstParam(params.storeCode);
  const created = firstParam(params.created) === "1";
  const isAuthenticated = useAuthStore((state) => state.isAuthenticated);
  const hasPermission = useAuthStore((state) => state.access.hasPermission);
  const isReview = useAuthStore((state) => state.iosReviewOfflineGuardActive);
  const allowed = canViewStoreCash(isAuthenticated, hasPermission, isReview);
  const goBack = useCallback(() => {
    if (router.canGoBack()) router.back();
    else router.replace("/(shell)/store-cash" as Href);
  }, [router]);
  const title = kind === "expense" ? t("detail.expenseTitle") : t("detail.depositTitle");

  if (!allowed) {
    return (
      <CashScreenFrame title={title} onBack={goBack}>
        <View style={styles.center}><Text style={styles.message}>{t("states.notAllowed")}</Text></View>
      </CashScreenFrame>
    );
  }
  if (!kind || !guid) {
    return (
      <CashScreenFrame title={title} onBack={goBack}>
        <View style={styles.center}><Text style={styles.message}>{t("detail.notFound")}</Text></View>
      </CashScreenFrame>
    );
  }
  return <DetailBody kind={kind} guid={guid} storeCode={storeCode} created={created} onBack={goBack} title={title} />;
}

function DetailBody({
  kind,
  guid,
  storeCode,
  created,
  onBack,
  title,
}: {
  kind: "deposit" | "expense";
  guid: string;
  storeCode: string;
  created: boolean;
  onBack: () => void;
  title: string;
}) {
  const { t, language } = useAppTranslation(["storeCash", "common"]);
  const invalidate = useInvalidateCashData();
  const contextQuery = useCashContextQuery(true);
  const timeZoneId = contextQuery.data?.stores.find((store) => store.storeCode === storeCode)?.timeZoneId ?? "";
  const [voidVisible, setVoidVisible] = useState(false);
  const [voidError, setVoidError] = useState("");
  const [viewerIndex, setViewerIndex] = useState<number | null>(null);
  const [refreshingImages, setRefreshingImages] = useState(false);
  const errorRefetchedRef = useRef(false);

  // 签名下载地址几分钟就过期：不缓存、每次进详情重新取
  const query = useQuery<CashDepositDetail | CashExpenseDetail>({
    queryKey: kind === "deposit" ? storeCashKeys.depositDetail(guid) : storeCashKeys.expenseDetail(guid),
    queryFn: () => (kind === "deposit" ? fetchCashDepositDetail(guid) : fetchCashExpenseDetail(guid)),
    gcTime: 0,
    staleTime: 0,
    refetchOnMount: "always",
  });

  const voidMutation = useMutation<CashDepositDetail | CashExpenseDetail, unknown, string>({
    mutationFn: (reason: string) =>
      kind === "deposit" ? voidCashDeposit(guid, { reason }) : voidCashExpense(guid, { reason }),
    onSuccess: async () => {
      setVoidVisible(false);
      setVoidError("");
      await invalidate();
    },
    onError: (cause) => setVoidError(resolveCashErrorMessage(cause, { t, language })),
  });

  const detail = query.data;
  const attachments: CashAttachment[] =
    !detail ? [] : kind === "deposit"
      ? (detail as CashDepositDetail).slips.flatMap((slip) => slip.attachments)
      : (detail as CashExpenseDetail).attachments;

  /** 点开图片前若签名地址快过期，先重新取一次详情拿新地址。 */
  const openViewer = async (index: number) => {
    if (anyAttachmentUrlStale(attachments, Date.now())) {
      setRefreshingImages(true);
      try {
        await query.refetch();
      } finally {
        setRefreshingImages(false);
      }
    }
    errorRefetchedRef.current = false;
    setViewerIndex(index);
  };

  const handleImageError = () => {
    // 加载失败多半是签名地址过期：重新取一次详情换新地址，只重取一次避免无限循环
    if (errorRefetchedRef.current) return;
    errorRefetchedRef.current = true;
    void query.refetch();
  };

  if (query.isPending) {
    return (
      <CashScreenFrame title={title} onBack={onBack}>
        <View style={styles.center}>
          <ActivityIndicator />
          <Text style={styles.message}>{t("states.loading")}</Text>
        </View>
      </CashScreenFrame>
    );
  }
  if (query.isError || !detail) {
    return (
      <CashScreenFrame title={title} onBack={onBack}>
        <View style={styles.center}>
          <Text style={styles.message}>
            {isRecordNotFoundError(query.error)
              ? t("detail.notFound")
              : resolveCashErrorMessage(query.error, { t, language, fallbackKey: "storeCash:states.loadFailed" })}
          </Text>
          {isRecordNotFoundError(query.error) ? null : (
            <Button mode="outlined" onPress={() => void query.refetch()}>{t("common:actions.retry")}</Button>
          )}
        </View>
      </CashScreenFrame>
    );
  }

  const voided = detail.status === "Voided";
  const amount = kind === "deposit" ? (detail as CashDepositDetail).totalAmount : (detail as CashExpenseDetail).amount;
  const date = kind === "deposit" ? (detail as CashDepositDetail).depositDate : (detail as CashExpenseDetail).expenseDate;
  const viewerImages = toViewerImages(attachments);
  let imageCursor = 0;

  const renderThumbs = (items: CashAttachment[]) => (
    <View style={styles.thumbs}>
      {items.map((attachment) => {
        const index = imageCursor++;
        return (
          <Pressable
            key={attachment.attachmentGuid}
            accessibilityRole="imagebutton"
            accessibilityLabel={t("photo.preview")}
            disabled={refreshingImages}
            onPress={() => void openViewer(index)}
            style={styles.thumbWrap}
          >
            <Image source={{ uri: attachment.url }} style={styles.thumb} resizeMode="cover" onError={handleImageError} />
          </Pressable>
        );
      })}
    </View>
  );

  return (
    <CashScreenFrame
      title={title}
      onBack={onBack}
      footer={
        detail.canVoid && !voided ? (
          <Button
            mode="outlined"
            icon="cancel"
            textColor={HB_COLORS.danger}
            onPress={() => {
              setVoidError("");
              setVoidVisible(true);
            }}
            contentStyle={styles.buttonContent}
          >
            {t("void.action")}
          </Button>
        ) : undefined
      }
    >
      <ScrollView style={cashUiStyles.scroll} contentContainerStyle={cashUiStyles.content}>
        {created && !voided ? <NoticeBanner tone="success" icon="check-circle-outline" text={t("detail.created")} /> : null}
        {voided ? <NoticeBanner tone="danger" icon="cancel" text={t("detail.voidedBanner")} /> : null}

        <CashCard>
          <View style={styles.heroTop}>
            <Text style={[styles.heroAmount, voided ? styles.voidedAmount : null]}>{formatAud(amount)}</Text>
            {voided ? <StatusChip tone="danger" label={t("records.voided")} /> : null}
            {kind === "expense" ? <StatusChip tone="info" label={categoryLabel(t, (detail as CashExpenseDetail).category)} /> : null}
          </View>
          <ValueRow label={kind === "deposit" ? t("detail.depositDate") : t("detail.expenseDate")} value={date} />
          {kind === "deposit" ? (
            <>
              {(detail as CashDepositDetail).coveredFromDate && (detail as CashDepositDetail).coveredToDate ? (
                <ValueRow
                  label={t("detail.covered")}
                  value={`${(detail as CashDepositDetail).coveredFromDate} ~ ${(detail as CashDepositDetail).coveredToDate}`}
                />
              ) : null}
              {(detail as CashDepositDetail).overrideReason ? (
                <ValueRow label={t("detail.overrideReason")} value={(detail as CashDepositDetail).overrideReason ?? ""} />
              ) : null}
            </>
          ) : (
            (detail as CashExpenseDetail).payeeName ? (
              <ValueRow label={t("detail.payee")} value={(detail as CashExpenseDetail).payeeName ?? ""} />
            ) : null
          )}
          {detail.note ? <ValueRow label={t("detail.note")} value={detail.note} /> : null}
          <ValueRow label={t("detail.createdBy")} value={detail.createdByName ?? "--"} muted />
          <ValueRow label={t("detail.createdAt")} value={formatUtcInZone(detail.createdAtUtc, timeZoneId)} muted />
        </CashCard>

        {voided ? (
          <CashCard title={t("detail.voidInfo")}>
            <ValueRow label={t("detail.voidReason")} value={detail.voidReason ?? "--"} />
            <ValueRow label={t("detail.voidedBy")} value={detail.voidedByName ?? "--"} muted />
            <ValueRow
              label={t("detail.voidedAt")}
              value={detail.voidedAtUtc ? formatUtcInZone(detail.voidedAtUtc, timeZoneId) : "--"}
              muted
            />
          </CashCard>
        ) : null}

        {kind === "deposit" ? (
          (detail as CashDepositDetail).slips.map((slip, index) => (
            <CashCard key={slip.slipGuid} title={t("deposit.slip.title", { index: index + 1 })}>
              <ValueRow label={t("detail.slipAmount")} value={formatAud(slip.amount)} strong />
              {slip.slipNo ? <ValueRow label={t("detail.slipNo")} value={slip.slipNo} muted /> : null}
              {slip.attachments.length > 0 ? renderThumbs(slip.attachments) : <Text style={styles.hint}>{t("detail.noImages")}</Text>}
            </CashCard>
          ))
        ) : (
          <CashCard title={t("detail.images")}>
            {attachments.length > 0 ? renderThumbs(attachments) : <Text style={styles.hint}>{t("detail.noImages")}</Text>}
          </CashCard>
        )}
      </ScrollView>

      <ImageViewerModal
        visible={viewerIndex !== null}
        images={viewerImages}
        initialIndex={viewerIndex ?? 0}
        onClose={() => setViewerIndex(null)}
        onImageError={handleImageError}
      />
      <VoidReasonDialog
        visible={voidVisible}
        title={kind === "deposit" ? t("void.depositTitle") : t("void.expenseTitle")}
        description={t("void.description", { date, amount: formatAud(amount) })}
        busy={voidMutation.isPending}
        errorMessage={voidError}
        onCancel={() => setVoidVisible(false)}
        onConfirm={(reason) => voidMutation.mutate(reason)}
      />
    </CashScreenFrame>
  );
}

const styles = StyleSheet.create({
  center: { flex: 1, alignItems: "center", justifyContent: "center", gap: 12, padding: 24 },
  message: { color: HB_COLORS.textPrimary, textAlign: "center" },
  hint: { color: HB_COLORS.textSecondary, fontSize: 13 },
  heroTop: { flexDirection: "row", flexWrap: "wrap", alignItems: "center", gap: HB_SPACING.xs },
  heroAmount: { color: HB_COLORS.textPrimary, fontSize: 30, fontWeight: "800", fontVariant: ["tabular-nums"] },
  voidedAmount: { color: HB_COLORS.textSecondary, textDecorationLine: "line-through" },
  thumbs: { flexDirection: "row", flexWrap: "wrap", gap: HB_SPACING.xs },
  thumbWrap: { width: THUMB, height: THUMB, borderRadius: HB_RADIUS.control, overflow: "hidden" },
  thumb: { width: THUMB, height: THUMB, backgroundColor: HB_COLORS.surfaceMuted },
  buttonContent: { minHeight: 48 },
});
