import { useMemo, useState } from "react";
import { RefreshControl, ScrollView, StyleSheet, View } from "react-native";
import {
  ActivityIndicator,
  Button,
  DataTable,
  SegmentedButtons,
  Surface,
  Text,
} from "react-native-paper";
import { BusinessSheet } from "@/components/ui/BusinessSheet";
import { BUSINESS_UI } from "@/components/ui/business-ui";
import { EmptyState } from "@/components/ui/EmptyState";
import {
  SelectionListModal,
  type SelectionListItem,
} from "@/components/ui/SelectionListModal";
import { HB_COLORS, HB_SPACING } from "@/shared/theme/tokens";
import { resolveLocalizedErrorMessage } from "@/shared/i18n/error-message";
import { resolveLocaleTag } from "@/shared/i18n/types";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import {
  buildSeasonalCardYearOptions,
  getSeasonalCardHistoryCardTypeOptions,
  getSeasonalCardLocalizedTypeLabel,
} from "@/modules/seasonal-cards/form";
import {
  formatSeasonalCardDateTime,
  formatSeasonalCardMoney,
} from "@/modules/seasonal-cards/format";
import {
  buildSeasonalCardHistoryTable,
  groupSeasonalCardHistory,
  type SeasonalCardHistoryEntry,
} from "@/modules/seasonal-cards/history";
import {
  useSeasonalCardSubmissionDetail,
  useSeasonalCardSubmissions,
} from "@/modules/seasonal-cards/hooks";
import { getSeasonalCardPriceDisplayLabel } from "@/modules/seasonal-cards/submit-draft";
import type { SeasonalCardType } from "@/modules/seasonal-cards/types";
import { SEASONAL_CARD_COLORS } from "./palette";

type HistoryDisplayMode = "list" | "table";

// 一批 4 行；每页取 100 行，减少同一批次被分页拆开的机会。
const PAGE_SIZE = 100;

function getPageCount(total: number, pageSize: number) {
  return Math.max(1, Math.ceil(total / pageSize));
}

function DetailLine({ label, value }: { label: string; value: string }) {
  return (
    <View style={styles.detailLine}>
      <Text variant="labelMedium" style={styles.detailLineLabel}>
        {label}
      </Text>
      <Text variant="bodyMedium" style={styles.detailLineValue}>
        {value}
      </Text>
    </View>
  );
}

function FilterButton({
  label,
  value,
  onPress,
}: {
  label: string;
  value: string;
  onPress: () => void;
}) {
  return (
    <View style={styles.filter}>
      <Text style={styles.filterLabel}>{label}</Text>
      <Button
        compact
        mode="outlined"
        onPress={onPress}
        contentStyle={styles.filterButtonContent}
        labelStyle={styles.filterButtonLabel}
      >
        {value}
      </Button>
    </View>
  );
}

/** 历史记录：同一批次合并成一张卡片显示 4 个价格；批量填报前的单条记录各自独立。 */
export function SeasonalCardHistoryView({
  storeCode,
  enabled,
}: {
  storeCode: string;
  enabled: boolean;
}) {
  const { t, language } = useAppTranslation(["seasonalCards", "common"]);
  const localeTag = useMemo(() => resolveLocaleTag(language), [language]);
  const [seasonYear, setSeasonYear] = useState(String(new Date().getFullYear()));
  const [cardType, setCardType] = useState("");
  const [pageNumber, setPageNumber] = useState(1);
  const [displayMode, setDisplayMode] = useState<HistoryDisplayMode>("list");
  const [yearPickerVisible, setYearPickerVisible] = useState(false);
  const [cardTypePickerVisible, setCardTypePickerVisible] = useState(false);
  const [selectedSubmissionGuid, setSelectedSubmissionGuid] = useState<string | null>(null);

  const historyQuery = useSeasonalCardSubmissions(
    {
      storeCode,
      cardType: cardType ? Number(cardType) : undefined,
      seasonYear,
      pageNumber,
      pageSize: PAGE_SIZE,
    },
    enabled && Boolean(storeCode)
  );
  const detailQuery = useSeasonalCardSubmissionDetail(
    selectedSubmissionGuid,
    Boolean(selectedSubmissionGuid)
  );
  const entries = useMemo(
    () => groupSeasonalCardHistory(historyQuery.data?.items ?? []),
    [historyQuery.data?.items]
  );
  const table = useMemo(
    () => buildSeasonalCardHistoryTable(historyQuery.data?.items ?? []),
    [historyQuery.data?.items]
  );
  const pageCount = getPageCount(
    historyQuery.data?.total ?? 0,
    historyQuery.data?.pageSize ?? PAGE_SIZE
  );
  const yearItems = useMemo<SelectionListItem[]>(
    () =>
      buildSeasonalCardYearOptions(new Date().getFullYear()).map((year) => ({
        key: String(year),
        label: String(year),
      })),
    []
  );
  const cardTypeItems = useMemo<SelectionListItem[]>(
    () =>
      getSeasonalCardHistoryCardTypeOptions().map((item) => ({
        ...item,
        label: t(`cardTypes.${item.key}`),
      })),
    [t]
  );
  const otherLabel = t("form.customPriceOption");
  const getCardTypeLabel = (value: SeasonalCardType | null, name?: string | null) =>
    getSeasonalCardLocalizedTypeLabel(value, name, (key) => t(key));
  const getSupplierLabel = (entry: SeasonalCardHistoryEntry) =>
    entry.supplierName || entry.localSupplierCode || t("history.unassignedSupplier");

  const renderEntry = (entry: SeasonalCardHistoryEntry) => (
    <View key={entry.key} style={[BUSINESS_UI.section, styles.entry]}>
      <View style={styles.entryHeader}>
        <Text style={styles.entryTitle}>
          {[
            entry.seasonYear != null ? String(entry.seasonYear) : "",
            getCardTypeLabel(entry.cardType, entry.cardTypeName) || t("common:none"),
          ]
            .filter(Boolean)
            .join(" · ")}
        </Text>
        {entry.isSuperseded ? (
          <View style={styles.supersededTag}>
            <Text style={styles.supersededText}>{t("history.superseded")}</Text>
          </View>
        ) : null}
      </View>
      <Text style={styles.entrySupplier}>{getSupplierLabel(entry)}</Text>
      <View style={styles.entryLines}>
        {entry.lines.map((line, index) => (
          <View
            key={line.submissionGuid}
            style={[styles.entryLine, index > 0 ? styles.entryLineDivider : null]}
          >
            <Text style={styles.entryLineLabel}>
              {getSeasonalCardPriceDisplayLabel(
                line.priceOption,
                line.priceLabel,
                otherLabel,
                line.unitPrice
              )}
              {line.priceOption === 4 && line.unitPrice
                ? ` (${formatSeasonalCardMoney(line.unitPrice)})`
                : ""}
            </Text>
            <Text style={styles.entryLineValue}>
              {line.remainingQuantity != null ? String(line.remainingQuantity) : "--"}
            </Text>
          </View>
        ))}
      </View>
      <Text style={styles.entryTotal}>
        {t("history.total", {
          count: entry.totalQuantity,
          amount: formatSeasonalCardMoney(entry.totalAmount),
        })}
      </Text>
      <Text style={styles.entryMeta}>
        {t("history.submittedBy", {
          name: entry.submittedByName || "--",
          time: formatSeasonalCardDateTime(entry.submittedAt, localeTag),
        })}
      </Text>
      {entry.remark ? (
        <Text style={styles.entryMeta}>
          {t("history.remark", { remark: entry.remark })}
        </Text>
      ) : null}
      {!entry.isBatch ? (
        <Button
          compact
          mode="text"
          style={styles.detailButton}
          contentStyle={styles.detailButtonContent}
          onPress={() => setSelectedSubmissionGuid(entry.firstSubmissionGuid)}
        >
          {t("common:actions.viewDetail")}
        </Button>
      ) : null}
    </View>
  );

  const renderBody = () => {
    if (!storeCode) {
      return <EmptyState title={t("messages.selectStoreFirst")} />;
    }
    if (historyQuery.isLoading) {
      return (
        <View style={styles.feedback}>
          <ActivityIndicator />
        </View>
      );
    }
    if (historyQuery.isError) {
      return (
        <EmptyState
          title={t("messages.historyLoadFailed")}
          description={resolveLocalizedErrorMessage(historyQuery.error, {
            t,
            language,
            fallbackKey: "messages.historyLoadFailed",
          })}
        />
      );
    }
    if (!historyQuery.data?.items.length) {
      return (
        <EmptyState
          title={t("messages.emptyHistory")}
          description={t("messages.emptyHistoryDescription")}
        />
      );
    }
    if (displayMode === "table") {
      return (
        <Surface style={styles.tableSurface}>
          <ScrollView horizontal showsHorizontalScrollIndicator>
            <DataTable style={styles.table}>
              <DataTable.Header>
                <DataTable.Title style={styles.priceHeaderCell}>
                  {t("history.priceTypeHeader")}
                </DataTable.Title>
                {table.years.map((year) => (
                  <DataTable.Title key={year} numeric style={styles.yearHeaderCell}>
                    {String(year)}
                  </DataTable.Title>
                ))}
              </DataTable.Header>
              {table.rows.map((row) => (
                <DataTable.Row key={row.priceKey}>
                  <DataTable.Cell style={styles.priceHeaderCell}>
                    {getSeasonalCardPriceDisplayLabel(row.priceOption, row.priceLabel, otherLabel)}
                  </DataTable.Cell>
                  {table.years.map((year) => (
                    <DataTable.Cell key={year} numeric style={styles.yearHeaderCell}>
                      {row.cells.get(year) ?? "--"}
                    </DataTable.Cell>
                  ))}
                </DataTable.Row>
              ))}
            </DataTable>
          </ScrollView>
        </Surface>
      );
    }
    return <View style={styles.list}>{entries.map(renderEntry)}</View>;
  };

  return (
    <View style={styles.container}>
      <ScrollView
        contentContainerStyle={styles.content}
        refreshControl={
          <RefreshControl
            refreshing={historyQuery.isRefetching}
            onRefresh={() => {
              void historyQuery.refetch();
            }}
          />
        }
      >
        <View style={styles.filters}>
          <FilterButton
            label={t("history.seasonYear")}
            value={seasonYear || t("history.allYears")}
            onPress={() => setYearPickerVisible(true)}
          />
          <FilterButton
            label={t("history.cardType")}
            value={
              cardType
                ? getCardTypeLabel(Number(cardType) as SeasonalCardType)
                : t("history.allCardTypes")
            }
            onPress={() => setCardTypePickerVisible(true)}
          />
        </View>

        <SegmentedButtons
          value={displayMode}
          onValueChange={(value) => setDisplayMode(value as HistoryDisplayMode)}
          buttons={[
            { value: "list", label: t("history.displayList") },
            { value: "table", label: t("history.displayTable") },
          ]}
        />
        {displayMode === "table" && historyQuery.data?.items.length ? (
          <Text style={styles.tableHint}>{t("history.tableHint")}</Text>
        ) : null}

        {renderBody()}

        {historyQuery.data?.items.length && pageCount > 1 ? (
          <View style={styles.pagination}>
            <Button
              compact
              mode="outlined"
              disabled={pageNumber <= 1}
              contentStyle={styles.pageButtonContent}
              onPress={() => setPageNumber((current) => Math.max(1, current - 1))}
            >
              {t("common:actions.back")}
            </Button>
            <Text variant="bodyMedium">
              {pageNumber} / {pageCount}
            </Text>
            <Button
              compact
              mode="outlined"
              disabled={pageNumber >= pageCount}
              contentStyle={styles.pageButtonContent}
              onPress={() => setPageNumber((current) => Math.min(pageCount, current + 1))}
            >
              {t("history.nextPage")}
            </Button>
          </View>
        ) : null}
      </ScrollView>

      <SelectionListModal
        presentation="sheet"
        visible={yearPickerVisible}
        title={t("history.yearPickerTitle")}
        cancelLabel={t("common:actions.cancel")}
        items={yearItems}
        selectedKey={seasonYear}
        includeAllOption
        allLabel={t("history.allYears")}
        emptyLabel={t("messages.emptyOptions")}
        onDismiss={() => setYearPickerVisible(false)}
        onSelect={(item) => {
          setPageNumber(1);
          setSeasonYear(item?.key ?? "");
          setYearPickerVisible(false);
        }}
      />

      <SelectionListModal
        presentation="sheet"
        visible={cardTypePickerVisible}
        title={t("history.cardTypePickerTitle")}
        cancelLabel={t("common:actions.cancel")}
        items={cardTypeItems}
        selectedKey={cardType}
        includeAllOption
        allLabel={t("history.allCardTypes")}
        emptyLabel={t("messages.emptyOptions")}
        onDismiss={() => setCardTypePickerVisible(false)}
        onSelect={(item) => {
          setPageNumber(1);
          setCardType(item?.key ?? "");
          setCardTypePickerVisible(false);
        }}
      />

      <BusinessSheet
        visible={Boolean(selectedSubmissionGuid)}
        title={t("detailTitle")}
        onDismiss={() => setSelectedSubmissionGuid(null)}
        footer={
          <Button
            mode="outlined"
            contentStyle={BUSINESS_UI.buttonContent}
            onPress={() => setSelectedSubmissionGuid(null)}
          >
            {t("common:actions.close")}
          </Button>
        }
      >
        {detailQuery.isLoading ? (
          <View style={styles.feedback}>
            <ActivityIndicator />
          </View>
        ) : detailQuery.data ? (
          <View style={styles.detailBlock}>
            <DetailLine label={t("labels.storeCode")} value={detailQuery.data.storeCode || "--"} />
            <DetailLine
              label={t("labels.cardType")}
              value={
                getCardTypeLabel(detailQuery.data.cardType, detailQuery.data.cardTypeName) || "--"
              }
            />
            <DetailLine
              label={t("labels.seasonYear")}
              value={detailQuery.data.seasonYear != null ? String(detailQuery.data.seasonYear) : "--"}
            />
            <DetailLine
              label={t("labels.supplier")}
              value={
                detailQuery.data.supplierName ||
                detailQuery.data.localSupplierCode ||
                t("history.unassignedSupplier")
              }
            />
            <DetailLine
              label={t("labels.unitPrice")}
              value={detailQuery.data.priceLabel || formatSeasonalCardMoney(detailQuery.data.unitPrice)}
            />
            <DetailLine
              label={t("labels.remainingQuantity")}
              value={
                detailQuery.data.remainingQuantity != null
                  ? String(detailQuery.data.remainingQuantity)
                  : "--"
              }
            />
            <DetailLine
              label={t("labels.submittedByName")}
              value={detailQuery.data.submittedByName || "--"}
            />
            <DetailLine
              label={t("labels.submittedAt")}
              value={formatSeasonalCardDateTime(detailQuery.data.submittedAt, localeTag)}
            />
            <DetailLine label={t("labels.remark")} value={detailQuery.data.remark || "--"} />
          </View>
        ) : (
          <EmptyState
            title={t("messages.detailLoadFailed")}
            description={t("messages.detailLoadFailedDescription")}
          />
        )}
      </BusinessSheet>
    </View>
  );
}

const styles = StyleSheet.create({
  container: { flex: 1 },
  content: { ...BUSINESS_UI.content, paddingBottom: HB_SPACING.xl },
  filters: { flexDirection: "row", gap: HB_SPACING.xs },
  filter: {
    ...BUSINESS_UI.section,
    flex: 1,
    gap: 4,
    padding: HB_SPACING.xs,
  },
  filterLabel: { fontSize: 12, lineHeight: 15, color: HB_COLORS.textSecondary },
  filterButtonContent: { justifyContent: "flex-start", minHeight: 44, paddingHorizontal: 2 },
  filterButtonLabel: { fontSize: 13, marginVertical: 2 },
  tableHint: { fontSize: 12, lineHeight: 17, color: HB_COLORS.textSecondary },
  feedback: { alignItems: "center", justifyContent: "center", minHeight: 120 },
  list: { gap: HB_SPACING.xs },
  entry: { padding: HB_SPACING.sm, gap: 6 },
  entryHeader: { flexDirection: "row", flexWrap: "wrap", alignItems: "center", gap: HB_SPACING.xs },
  entryTitle: { fontSize: 16, lineHeight: 22, fontWeight: "700", color: HB_COLORS.textPrimary, flexShrink: 1 },
  supersededTag: {
    paddingHorizontal: 6,
    paddingVertical: 1,
    borderRadius: 8,
    backgroundColor: HB_COLORS.surfaceMuted,
  },
  supersededText: { fontSize: 11, lineHeight: 15, color: SEASONAL_CARD_COLORS.mutedText },
  entrySupplier: { fontSize: 13, lineHeight: 18, color: HB_COLORS.textSecondary },
  entryLines: {
    borderWidth: 1,
    borderColor: HB_COLORS.outlineMuted,
    borderRadius: 8,
    overflow: "hidden",
  },
  entryLine: {
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    gap: HB_SPACING.sm,
    paddingHorizontal: HB_SPACING.sm,
    paddingVertical: 8,
  },
  entryLineDivider: {
    borderTopWidth: StyleSheet.hairlineWidth,
    borderTopColor: HB_COLORS.outlineMuted,
  },
  entryLineLabel: { flex: 1, fontSize: 14, lineHeight: 20, color: HB_COLORS.textPrimary },
  entryLineValue: { fontSize: 15, lineHeight: 20, fontWeight: "600", color: HB_COLORS.textPrimary, fontVariant: ["tabular-nums"] },
  entryTotal: { fontSize: 14, lineHeight: 20, fontWeight: "600", color: HB_COLORS.textPrimary },
  entryMeta: { fontSize: 12, lineHeight: 18, color: HB_COLORS.textSecondary },
  detailButton: { alignSelf: "flex-start" },
  detailButtonContent: { minHeight: 44 },
  tableSurface: { ...BUSINESS_UI.section },
  table: { minWidth: 320 },
  priceHeaderCell: { minWidth: 96 },
  yearHeaderCell: { minWidth: 72 },
  pagination: {
    alignItems: "center",
    flexDirection: "row",
    gap: HB_SPACING.sm,
    justifyContent: "space-between",
  },
  pageButtonContent: { minHeight: 44 },
  detailBlock: { gap: 6 },
  detailLine: {
    flexDirection: "row",
    gap: 12,
    paddingVertical: 6,
    borderBottomWidth: StyleSheet.hairlineWidth,
    borderBottomColor: HB_COLORS.outlineMuted,
  },
  detailLineLabel: { ...BUSINESS_UI.fieldLabel, width: 104 },
  detailLineValue: { ...BUSINESS_UI.fieldValue, flex: 1, textAlign: "right" },
});
