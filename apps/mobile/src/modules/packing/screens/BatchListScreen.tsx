import { FlatList, RefreshControl, View } from "react-native";
import { ActivityIndicator, Button } from "react-native-paper";
import { SafeAreaView } from "react-native-safe-area-context";
import { useRouter } from "expo-router";
import { useIsFocused } from "@react-navigation/native";
import { useQuery } from "@tanstack/react-query";
import { EmptyState } from "@/components/ui/EmptyState";
import { PickHeader } from "@/modules/warehouse-picking/components/PickHeader";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import { fetchCartonBatches } from "../api";
import { readPackingError } from "../api-normalization";
import { isBatchApiMissing } from "../batch-logic";
import { packingErrorMessage } from "../packing-errors";
import { formatLocalTime } from "../packing-logic";
import { usePackRoutes } from "../use-pack-side";
import { usePackScope } from "../use-pack-scope";
import { OwnerBar } from "../components/OwnerBar";
import { PrinterStatusTag } from "../components/PrinterStatusTag";
import { Tag, UnitRow, packStyles } from "../components/pack-ui";

/** 最近的批次列表：进入某一批继续打印 / 补打，或新建一批。后端没有批次接口（404）时提示当前服务器不支持。 */
export function BatchListScreen() {
  const { t, language } = useAppTranslation("packing");
  const router = useRouter();
  // 跳转留在当前入口（仓库打包 / 分店打包）的路由栈里。
  const routes = usePackRoutes();
  const focused = useIsFocused();
  const scope = usePackScope();
  const owner = scope.owner;
  const ownerCode = owner?.code ?? "";
  const query = useQuery({
    queryKey: ["packing", "batches", ownerCode],
    queryFn: ({ signal }) => fetchCartonBatches(ownerCode, 30, signal),
    enabled: focused && Boolean(ownerCode),
    staleTime: 5_000,
  });
  const goBack = () => (router.canGoBack() ? router.back() : router.replace(routes.home));
  const info = query.isError ? readPackingError(query.error) : null;
  const unavailable = info ? isBatchApiMissing(info.status, info.code) : false;

  const empty = query.isLoading ? (
    <ActivityIndicator style={packStyles.loading} />
  ) : query.isError ? (
    <EmptyState
      title={unavailable ? t("batch.listUnavailable") : packingErrorMessage(query.error, t, language)}
      actionLabel={unavailable ? undefined : t("actions.retry")}
      onAction={unavailable ? undefined : () => void query.refetch()}
    />
  ) : (
    <EmptyState title={t("batch.listEmpty")} description={t("batch.listEmptyHint")} />
  );

  return (
    <SafeAreaView edges={["top", "bottom", "left", "right"]} style={packStyles.screen}>
      <PickHeader title={t("batch.listTitle")} subtitle={t("batch.listSubtitle")} onBack={goBack} backLabel={t("actions.back")}>
        <OwnerBar owners={scope.owners} owner={owner} onChange={scope.setOwner} right={<PrinterStatusTag />} />
      </PickHeader>
      <FlatList
        contentContainerStyle={packStyles.body}
        data={query.data ?? []}
        keyExtractor={(item) => item.batchGuid}
        ListHeaderComponent={
          unavailable ? null : (
            <Button
              mode="contained"
              icon="plus"
              onPress={() => router.push(routes.batchNew)}
              style={packStyles.button}
              contentStyle={packStyles.buttonContent}
            >
              {t("batch.newBatch")}
            </Button>
          )
        }
        ListEmptyComponent={empty}
        refreshControl={<RefreshControl refreshing={query.isRefetching} onRefresh={() => void query.refetch()} />}
        renderItem={({ item }) => {
          const allPrinted = item.count - item.voidedCount > 0 && item.printedCount >= item.count - item.voidedCount;
          return (
            <View style={packStyles.cardList}>
              <UnitRow
                icon="tag-multiple-outline"
                tone={allPrinted ? "green" : "blue"}
                title={t("batch.itemTitle", { first: item.firstCartonNo, last: item.lastCartonNo })}
                monoTitle
                subtitle={`${t("batch.itemStats", { count: item.count, printed: item.printedCount, unactivated: item.unactivatedCount })}\n${t("batch.itemSubtitle", { time: formatLocalTime(item.createdAtUtc), creator: item.createdByName || "—" })}`}
                right={item.voidedCount > 0 ? <Tag tone="gray" label={t("batch.itemVoided", { count: item.voidedCount })} /> : undefined}
                onPress={() => router.push(routes.batch(item.batchGuid))}
                accessibilityLabel={t("batch.itemTitle", { first: item.firstCartonNo, last: item.lastCartonNo })}
              />
            </View>
          );
        }}
      />
    </SafeAreaView>
  );
}
