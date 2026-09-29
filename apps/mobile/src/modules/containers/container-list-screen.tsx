import { useCallback, useMemo, useState } from "react";
import { RefreshControl, ScrollView, StyleSheet, View } from "react-native";
import { useRouter } from "expo-router";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import {
  ActivityIndicator,
  Button,
  Card,
  Checkbox,
  Chip,
  Divider,
  Menu,
  Modal,
  Portal,
  SegmentedButtons,
  Snackbar,
  Surface,
  Text,
  TextInput,
} from "react-native-paper";
import { SafeAreaView } from "react-native-safe-area-context";
import { EmptyState } from "@/components/ui/EmptyState";
import { useAuthStore } from "@/store/auth-store";
import {
  createContainer,
  getContainerList,
  pushContainersToHbSales,
  updateContainer,
} from "./api";
import { CONTAINER_LIST_PAGE_SIZE, getContainerGuid, trimToUndefined } from "./query";
import type { ContainerMain, ContainerQueryRequest, CreateContainerRequest } from "./types";

const DATE_TYPES = [
  { value: "预计到岸日期", label: "预计到岸" },
  { value: "实际到货日期", label: "实际到货" },
  { value: "装柜日期", label: "装柜" },
];

const STATUS_OPTIONS = [
  { value: 0, label: "已装柜" },
  { value: 1, label: "在途" },
  { value: 2, label: "已完成" },
  { value: 7, label: "已取消" },
];

const EMPTY_CREATE_FORM = {
  containerNumber: "",
  loadingDate: "",
  estimatedArrivalDate: "",
  exchangeRate: "",
  shippingFee: "",
  remark: "",
};

function formatDate(value?: string) {
  return value ? value.slice(0, 10) : "--";
}

function formatNumber(value?: number, digits = 2) {
  return value == null || !Number.isFinite(value) ? "--" : value.toFixed(digits);
}

function formatMoney(value?: number) {
  return value == null || !Number.isFinite(value) ? "--" : `¥${value.toFixed(2)}`;
}

function statusLabel(status?: number) {
  return STATUS_OPTIONS.find((item) => item.value === status)?.label ?? `状态 ${status ?? "--"}`;
}

function parseOptionalNumber(value: string) {
  const trimmed = value.trim();
  if (!trimmed) return undefined;
  const parsed = Number(trimmed);
  return Number.isFinite(parsed) ? parsed : Number.NaN;
}

function buildCreatePayload(form: typeof EMPTY_CREATE_FORM): CreateContainerRequest {
  return {
    货柜编号: form.containerNumber.trim(),
    装柜日期: trimToUndefined(form.loadingDate),
    预计到岸日期: trimToUndefined(form.estimatedArrivalDate),
    汇率: parseOptionalNumber(form.exchangeRate),
    运费: parseOptionalNumber(form.shippingFee),
    备注: trimToUndefined(form.remark),
  };
}

function summarizePage(containers: ContainerMain[]) {
  return containers.reduce(
    (summary, item) => ({
      pieces: summary.pieces + (item.合计件数 ?? 0),
      quantity: summary.quantity + (item.合计数量 ?? 0),
      amount: summary.amount + (item.合计金额 ?? 0),
      volume: summary.volume + (item.总体积 ?? 0),
    }),
    { pieces: 0, quantity: 0, amount: 0, volume: 0 },
  );
}

function ContainerCard({
  item,
  menuVisible,
  selected,
  onCloseMenu,
  onOpenMenu,
  onOpenDetail,
  onPushHbSales,
  onToggleSelected,
  onUpdateStatus,
  canEditContainer,
}: {
  item: ContainerMain;
  menuVisible: boolean;
  selected: boolean;
  onCloseMenu: () => void;
  onOpenMenu: () => void;
  onOpenDetail: () => void;
  onPushHbSales: () => void;
  onToggleSelected: () => void;
  onUpdateStatus: (status: number) => void;
  canEditContainer: boolean;
}) {
  return (
    <Card style={styles.card} mode="outlined">
      <Card.Title
        left={() => (
          <Checkbox.Android
            status={selected ? "checked" : "unchecked"}
            onPress={onToggleSelected}
            accessibilityLabel="选择货柜"
          />
        )}
        title={item.货柜编号 || getContainerGuid(item) || "未命名货柜"}
        subtitle={`预计 ${formatDate(item.预计到岸日期)} · 实际 ${formatDate(item.实际到货日期)}`}
        right={() => canEditContainer ? (
          <Menu
            visible={menuVisible}
            onDismiss={onCloseMenu}
            anchor={<Button icon="dots-vertical" onPress={onOpenMenu}>操作</Button>}
          >
            {STATUS_OPTIONS.map((status) => (
              <Menu.Item
                key={status.value}
                title={`设为${status.label}`}
                onPress={() => {
                  onCloseMenu();
                  onUpdateStatus(status.value);
                }}
              />
            ))}
            <Divider />
            <Menu.Item
              title="推送 HBSales"
              onPress={() => {
                onCloseMenu();
                onPushHbSales();
              }}
            />
          </Menu>
        ) : null}
      />
      <Card.Content>
        <View style={styles.chipRow}>
          <Chip compact>{statusLabel(item.状态)}</Chip>
          <Chip compact>件数 {formatNumber(item.合计件数, 0)}</Chip>
          <Chip compact>数量 {formatNumber(item.合计数量, 0)}</Chip>
        </View>
        <View style={styles.metricGrid}>
          <Metric label="金额" value={formatMoney(item.合计金额)} />
          <Metric label="体积" value={formatNumber(item.总体积, 3)} />
          <Metric label="装柜" value={formatDate(item.装柜日期)} />
          <Metric label="汇率" value={formatNumber(item.汇率, 4)} />
        </View>
        {item.备注 ? <Text style={styles.remark}>{item.备注}</Text> : null}
      </Card.Content>
      <Card.Actions>
        <Button icon="chevron-right" mode="contained" onPress={onOpenDetail}>
          明细
        </Button>
      </Card.Actions>
    </Card>
  );
}

function Metric({ label, value }: { label: string; value: string }) {
  return (
    <View style={styles.metric}>
      <Text variant="labelSmall" style={styles.muted}>{label}</Text>
      <Text variant="bodyMedium">{value}</Text>
    </View>
  );
}

export function ContainerListScreen() {
  const router = useRouter();
  const queryClient = useQueryClient();
  const access = useAuthStore((state) => state.access);
  const [filters, setFilters] = useState<ContainerQueryRequest>({ dateType: "预计到岸日期" });
  const [appliedFilters, setAppliedFilters] = useState<ContainerQueryRequest>({ dateType: "预计到岸日期" });
  const [page, setPage] = useState(1);
  const [createVisible, setCreateVisible] = useState(false);
  const [createForm, setCreateForm] = useState(EMPTY_CREATE_FORM);
  const [menuGuid, setMenuGuid] = useState("");
  const [selectedContainerGuids, setSelectedContainerGuids] = useState<string[]>([]);
  const [snackbar, setSnackbar] = useState("");

  const listQuery = useQuery({
    queryKey: ["containers", "list", appliedFilters, page],
    queryFn: () => getContainerList({ ...appliedFilters, page, pageSize: CONTAINER_LIST_PAGE_SIZE }),
    enabled: access.canViewContainers,
  });

  const containers = listQuery.data?.containers ?? [];
  const totalPages = listQuery.data?.totalPages ?? 1;
  const summary = useMemo(() => summarizePage(containers), [containers]);
  const selectedContainerSet = useMemo(() => new Set(selectedContainerGuids), [selectedContainerGuids]);
  const canCreateContainer = access.canCreateContainer;
  const canEditContainer = access.canEditContainer;

  const handleBack = useCallback(() => {
    // 深链直达时可能没有历史栈，此时回到货柜功能所属的仓库页。
    if (router.canGoBack()) {
      router.back();
      return;
    }

    router.navigate("/(shell)/warehouse");
  }, [router]);

  const invalidateList = () => queryClient.invalidateQueries({ queryKey: ["containers"] });

  const createMutation = useMutation({
    mutationFn: async () => {
      const payload = buildCreatePayload(createForm);
      if (!payload.货柜编号) {
        throw new Error("货柜编号不能为空");
      }
      if (Number.isNaN(payload.汇率) || Number.isNaN(payload.运费)) {
        throw new Error("汇率或运费不是有效数字");
      }
      return createContainer(payload);
    },
    onSuccess: (containerGuid) => {
      setCreateVisible(false);
      setCreateForm(EMPTY_CREATE_FORM);
      void invalidateList();
      setSnackbar("货柜已创建");
      if (containerGuid) {
        router.push(`/containers/${encodeURIComponent(containerGuid)}`);
      }
    },
    onError: (error) => setSnackbar(error instanceof Error ? error.message : "创建货柜失败"),
  });

  const statusMutation = useMutation({
    mutationFn: ({ containerGuid, status }: { containerGuid: string; status: number }) =>
      updateContainer(containerGuid, { 状态: status }),
    onSuccess: () => {
      void invalidateList();
      setSnackbar("状态已更新");
    },
    onError: (error) => setSnackbar(error instanceof Error ? error.message : "状态更新失败"),
  });

  // 「同步 HQ」（HQ 货柜 → HBweb）已于 2026-09-29 随全部 HQ → HBweb 同步入口停用（PR #384，后端返回 410）；
  // 「推送已选到 HBSales」方向相反，继续保留。
  const pushMutation = useMutation({
    mutationFn: (containerGuids: string[]) => pushContainersToHbSales(containerGuids),
    onSuccess: (result) => {
      setSelectedContainerGuids([]);
      setSnackbar(result.message ?? result.Message ?? "已推送 HBSales");
    },
    onError: (error) => setSnackbar(error instanceof Error ? error.message : "推送 HBSales 失败"),
  });

  const applyFilters = () => {
    setPage(1);
    setSelectedContainerGuids([]);
    setAppliedFilters({
      ...filters,
      containerNumberFilter: trimToUndefined(filters.containerNumberFilter),
      itemNumberFilter: trimToUndefined(filters.itemNumberFilter),
      startDate: trimToUndefined(filters.startDate),
      endDate: trimToUndefined(filters.endDate),
    });
  };

  const toggleContainerSelection = (containerGuid: string) => {
    if (!containerGuid) return;
    setSelectedContainerGuids((current) =>
      current.includes(containerGuid)
        ? current.filter((item) => item !== containerGuid)
        : [...current, containerGuid],
    );
  };

  const pushSelectedContainers = () => {
    // 批量推送必须只处理用户勾选的货柜，避免误把当前页全部发往 HBSales。
    const guids = selectedContainerGuids.filter(Boolean);
    if (!guids.length) {
      setSnackbar("请选择要推送的货柜");
      return;
    }
    pushMutation.mutate(guids);
  };

  if (!access.canViewContainers) {
    return (
      <SafeAreaView style={styles.safeArea}>
        <EmptyState title="无权访问货柜" description="请联系管理员开通货柜查看权限" />
      </SafeAreaView>
    );
  }

  return (
    <SafeAreaView style={styles.safeArea}>
      <ScrollView
        contentContainerStyle={styles.content}
        refreshControl={<RefreshControl refreshing={listQuery.isRefetching} onRefresh={() => listQuery.refetch()} />}
      >
        <Button icon="arrow-left" onPress={handleBack}>返回</Button>
        <Surface style={styles.filterPanel} mode="flat">
          <SegmentedButtons
            value={filters.dateType ?? "预计到岸日期"}
            onValueChange={(value) => setFilters((current) => ({ ...current, dateType: value }))}
            buttons={DATE_TYPES}
          />
          <View style={styles.inputRow}>
            <TextInput
              mode="outlined"
              label="开始日期"
              placeholder="YYYY-MM-DD"
              value={filters.startDate ?? ""}
              onChangeText={(value) => setFilters((current) => ({ ...current, startDate: value }))}
              style={styles.inputHalf}
            />
            <TextInput
              mode="outlined"
              label="结束日期"
              placeholder="YYYY-MM-DD"
              value={filters.endDate ?? ""}
              onChangeText={(value) => setFilters((current) => ({ ...current, endDate: value }))}
              style={styles.inputHalf}
            />
          </View>
          <TextInput
            mode="outlined"
            label="货柜编号"
            value={filters.containerNumberFilter ?? ""}
            onChangeText={(value) => setFilters((current) => ({ ...current, containerNumberFilter: value }))}
          />
          <TextInput
            mode="outlined"
            label="货号"
            value={filters.itemNumberFilter ?? ""}
            onChangeText={(value) => setFilters((current) => ({ ...current, itemNumberFilter: value }))}
          />
          <View style={styles.chipRow}>
            {STATUS_OPTIONS.map((status) => {
              const selected = filters.statuses?.includes(status.value) ?? false;
              return (
                <Chip
                  key={status.value}
                  selected={selected}
                  onPress={() => {
                    setFilters((current) => {
                      const statuses = current.statuses ?? [];
                      return {
                        ...current,
                        statuses: selected
                          ? statuses.filter((item) => item !== status.value)
                          : [...statuses, status.value],
                      };
                    });
                  }}
                >
                  {status.label}
                </Chip>
              );
            })}
          </View>
          <View style={styles.actionRow}>
            <Button icon="filter" mode="contained" onPress={applyFilters}>筛选</Button>
            {canCreateContainer ? (
              <Button icon="plus" mode="outlined" onPress={() => setCreateVisible(true)}>创建</Button>
            ) : null}
          </View>
          {canEditContainer ? (
            <Button
              icon="send"
              mode="outlined"
              loading={pushMutation.isPending}
              disabled={pushMutation.isPending || !selectedContainerGuids.length}
              onPress={pushSelectedContainers}
            >
              推送已选到 HBSales
            </Button>
          ) : null}
        </Surface>

        <Surface style={styles.summaryPanel} mode="flat">
          <Metric label="当前页" value={`${containers.length} / ${listQuery.data?.totalCount ?? 0}`} />
          <Metric label="件数" value={formatNumber(summary.pieces, 0)} />
          <Metric label="数量" value={formatNumber(summary.quantity, 0)} />
          <Metric label="金额" value={formatMoney(summary.amount)} />
          <Metric label="体积" value={formatNumber(summary.volume, 3)} />
        </Surface>

        {listQuery.isLoading ? (
          <ActivityIndicator style={styles.loading} />
        ) : containers.length ? (
          containers.map((item) => {
            const containerGuid = getContainerGuid(item);
            return (
              <ContainerCard
                key={containerGuid || item.id || item.ID}
                item={item}
                menuVisible={menuGuid === containerGuid}
                selected={selectedContainerSet.has(containerGuid)}
                onOpenMenu={() => setMenuGuid(containerGuid)}
                onCloseMenu={() => setMenuGuid("")}
                onOpenDetail={() => router.push(`/containers/${encodeURIComponent(containerGuid)}`)}
                onPushHbSales={() => pushMutation.mutate([containerGuid])}
                onToggleSelected={() => toggleContainerSelection(containerGuid)}
                onUpdateStatus={(status) => statusMutation.mutate({ containerGuid, status })}
                canEditContainer={canEditContainer}
              />
            );
          })
        ) : (
          <EmptyState title="没有货柜" description="调整筛选条件后再试" />
        )}

        <View style={styles.pagination}>
          <Button mode="outlined" disabled={page <= 1} onPress={() => setPage((value) => Math.max(1, value - 1))}>
            上一页
          </Button>
          <Text style={styles.pageText}>{page} / {totalPages}</Text>
          <Button mode="outlined" disabled={page >= totalPages} onPress={() => setPage((value) => value + 1)}>
            下一页
          </Button>
        </View>
      </ScrollView>

      <Portal>
        <Modal visible={createVisible} onDismiss={() => setCreateVisible(false)} contentContainerStyle={styles.modal}>
          <Text variant="titleMedium">创建货柜</Text>
          <TextInput
            mode="outlined"
            label="货柜编号"
            value={createForm.containerNumber}
            onChangeText={(value) => setCreateForm((current) => ({ ...current, containerNumber: value }))}
          />
          <TextInput
            mode="outlined"
            label="装柜日期"
            placeholder="YYYY-MM-DD"
            value={createForm.loadingDate}
            onChangeText={(value) => setCreateForm((current) => ({ ...current, loadingDate: value }))}
          />
          <TextInput
            mode="outlined"
            label="预计到岸日期"
            placeholder="YYYY-MM-DD"
            value={createForm.estimatedArrivalDate}
            onChangeText={(value) => setCreateForm((current) => ({ ...current, estimatedArrivalDate: value }))}
          />
          <View style={styles.inputRow}>
            <TextInput
              mode="outlined"
              label="汇率"
              keyboardType="decimal-pad"
              value={createForm.exchangeRate}
              onChangeText={(value) => setCreateForm((current) => ({ ...current, exchangeRate: value }))}
              style={styles.inputHalf}
            />
            <TextInput
              mode="outlined"
              label="运费"
              keyboardType="decimal-pad"
              value={createForm.shippingFee}
              onChangeText={(value) => setCreateForm((current) => ({ ...current, shippingFee: value }))}
              style={styles.inputHalf}
            />
          </View>
          <TextInput
            mode="outlined"
            label="备注"
            value={createForm.remark}
            onChangeText={(value) => setCreateForm((current) => ({ ...current, remark: value }))}
          />
          <View style={styles.actionRow}>
            <Button onPress={() => setCreateVisible(false)}>取消</Button>
            <Button mode="contained" loading={createMutation.isPending} onPress={() => createMutation.mutate()}>
              保存
            </Button>
          </View>
        </Modal>
      </Portal>
      <Snackbar visible={Boolean(snackbar)} onDismiss={() => setSnackbar("")}>{snackbar}</Snackbar>
    </SafeAreaView>
  );
}

const styles = StyleSheet.create({
  safeArea: {
    flex: 1,
    backgroundColor: "#F4F6F8",
  },
  content: {
    gap: 16,
    padding: 16,
    paddingTop: 12,
    paddingBottom: 24,
  },
  filterPanel: {
    gap: 10,
    padding: 12,
    borderRadius: 12,
    borderWidth: 1,
    borderColor: "#E4E7EC",
    backgroundColor: "#FFFFFF",
  },
  summaryPanel: {
    flexDirection: "row",
    flexWrap: "wrap",
    gap: 10,
    padding: 12,
    borderRadius: 12,
    borderWidth: 1,
    borderColor: "#E4E7EC",
    backgroundColor: "#FFFFFF",
  },
  card: {
    borderRadius: 12,
    borderColor: "#E4E7EC",
    backgroundColor: "#FFFFFF",
  },
  chipRow: {
    flexDirection: "row",
    flexWrap: "wrap",
    gap: 8,
  },
  inputRow: {
    flexDirection: "row",
    gap: 10,
  },
  inputHalf: {
    flex: 1,
  },
  actionRow: {
    flexDirection: "row",
    flexWrap: "wrap",
    gap: 8,
    alignItems: "center",
  },
  metricGrid: {
    flexDirection: "row",
    flexWrap: "wrap",
    gap: 10,
    marginTop: 10,
  },
  metric: {
    minWidth: 92,
    flexGrow: 1,
  },
  muted: {
    color: "#64748B",
  },
  remark: {
    marginTop: 10,
    color: "#475569",
  },
  loading: {
    marginVertical: 28,
  },
  pagination: {
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "center",
    gap: 16,
  },
  pageText: {
    minWidth: 70,
    textAlign: "center",
  },
  modal: {
    margin: 18,
    gap: 10,
    padding: 16,
    borderRadius: 18,
    backgroundColor: "#FFFFFF",
  },
});
