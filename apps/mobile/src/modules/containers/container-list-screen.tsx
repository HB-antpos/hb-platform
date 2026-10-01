import { useCallback, useMemo, useState } from "react";
import { Pressable, RefreshControl, ScrollView, StyleSheet, View } from "react-native";
import { useRouter } from "expo-router";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import {
  ActivityIndicator,
  Badge,
  Button,
  Checkbox,
  Chip,
  Divider,
  Icon,
  IconButton,
  Menu,
  Modal,
  Portal,
  Snackbar,
  Surface,
  Text,
  TextInput,
} from "react-native-paper";
import { SafeAreaView } from "react-native-safe-area-context";
import { EmptyState } from "@/components/ui/EmptyState";
import { formatMonthDate } from "@/components/attendance/MonthDatePicker";
import { HB_COLORS, HB_RADIUS, HB_SPACING } from "@/shared/theme/tokens";
import { useAuthStore } from "@/store/auth-store";
import {
  createContainer,
  getContainerList,
  pushContainersToHbSales,
  updateContainer,
} from "./api";
import { ContainerArrivalDateSheet, type ArrivalDateField } from "./container-arrival-date-sheet";
import { ContainerListFilterSheet } from "./container-list-filter-sheet";
import {
  CONTAINER_SORT_OPTIONS,
  CONTAINER_STATUS_OPTIONS,
  DATE_RANGE_FIELDS,
  DEFAULT_CONTAINER_SORT,
  containerStatusLabel,
  countSheetFilters,
  describeDateRange,
  formatShortDate,
  getArrivalInsight,
  type ArrivalInsight,
  type ContainerListFilters,
} from "./container-list-logic";
import { CONTAINER_LIST_PAGE_SIZE, getContainerGuid, trimToUndefined } from "./query";
import type { ContainerMain, CreateContainerRequest, UpdateContainerRequest } from "./types";

const EMPTY_CREATE_FORM = {
  containerNumber: "",
  loadingDate: "",
  estimatedArrivalDate: "",
  exchangeRate: "",
  shippingFee: "",
  remark: "",
};

const DEFAULT_FILTERS: ContainerListFilters = { dateType: DEFAULT_CONTAINER_SORT };

// 状态标签配色：在途用操作蓝，已完成用成功绿，已取消弱化为灰。
const STATUS_TONES: Record<number, { background: string; color: string }> = {
  0: { background: HB_COLORS.surfaceMuted, color: HB_COLORS.textPrimary },
  1: { background: "#EAF2FF", color: "#073B83" },
  2: { background: "#E7F6EC", color: "#054F31" },
  7: { background: HB_COLORS.surfaceMuted, color: HB_COLORS.textSecondary },
};

// 条件标签的图标（筛选、关闭）同样默认取主题绿色，局部改为次要文字色。
const ACTIVE_FILTER_CHIP_THEME = { colors: { onSecondaryContainer: HB_COLORS.textSecondary } };

const INSIGHT_COLORS: Record<ArrivalInsight["tone"], string> = {
  warning: HB_COLORS.warning,
  muted: HB_COLORS.textSecondary,
  accent: HB_COLORS.action,
};

function formatInteger(value?: number) {
  return value == null || !Number.isFinite(value) ? "--" : Math.round(value).toLocaleString("en-US");
}

function formatVolume(value?: number) {
  return value == null || !Number.isFinite(value) ? "--" : value.toFixed(2);
}

function formatMoney(value?: number) {
  return value == null || !Number.isFinite(value)
    ? "--"
    : `¥${value.toLocaleString("en-US", { minimumFractionDigits: 0, maximumFractionDigits: 0 })}`;
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
      amount: summary.amount + (item.合计金额 ?? 0),
      volume: summary.volume + (item.总体积 ?? 0),
    }),
    { pieces: 0, amount: 0, volume: 0 },
  );
}

function DateBlock({
  label,
  value,
  emptyText,
  insight,
  editable,
  onPress,
}: {
  label: string;
  value?: string;
  emptyText: string;
  insight?: ArrivalInsight | null;
  editable: boolean;
  onPress: () => void;
}) {
  const shortDate = formatShortDate(value);
  const isWarning = insight?.tone === "warning";
  return (
    <Pressable
      accessibilityRole={editable ? "button" : undefined}
      accessibilityLabel={editable ? `修改${label}，当前 ${shortDate || emptyText}` : undefined}
      disabled={!editable}
      onPress={onPress}
      style={({ pressed }) => [
        styles.dateBlock,
        !shortDate && editable ? styles.dateBlockEmpty : null,
        isWarning ? styles.dateBlockWarning : null,
        pressed ? styles.dateBlockPressed : null,
      ]}
    >
      <View style={styles.dateBlockHeader}>
        <Text style={styles.dateLabel}>{label}</Text>
        {editable ? <Icon source="pencil-outline" size={14} color={HB_COLORS.textSecondary} /> : null}
      </View>
      {shortDate ? (
        <View style={styles.dateValueRow}>
          <Text style={[styles.dateValue, isWarning ? { color: HB_COLORS.warning } : null]}>{shortDate}</Text>
          {insight ? (
            <Text style={[styles.dateInsight, { color: INSIGHT_COLORS[insight.tone] }]} numberOfLines={1}>
              {insight.text}
            </Text>
          ) : null}
        </View>
      ) : (
        <Text style={editable ? styles.dateEmptyAction : styles.dateEmpty}>{editable ? `+ ${emptyText}` : "--"}</Text>
      )}
    </Pressable>
  );
}

function ContainerCard({
  item,
  today,
  menuVisible,
  selected,
  canEditContainer,
  onCloseMenu,
  onOpenMenu,
  onOpenDetail,
  onEditDate,
  onPushHbSales,
  onToggleSelected,
  onUpdateStatus,
}: {
  item: ContainerMain;
  today: string;
  menuVisible: boolean;
  selected: boolean;
  canEditContainer: boolean;
  onCloseMenu: () => void;
  onOpenMenu: () => void;
  onOpenDetail: () => void;
  onEditDate: (field: ArrivalDateField) => void;
  onPushHbSales: () => void;
  onToggleSelected: () => void;
  onUpdateStatus: (status: number) => void;
}) {
  const insight = getArrivalInsight(item, today);
  const tone = STATUS_TONES[item.状态 ?? -1] ?? STATUS_TONES[0];
  const loadingDate = formatShortDate(item.装柜日期);

  return (
    <Surface style={[styles.card, selected ? styles.cardSelected : null]} elevation={0}>
      <View style={styles.cardHeader}>
        <Pressable style={styles.cardTitleBlock} onPress={onOpenDetail} accessibilityRole="button">
          <Text style={styles.cardTitle} numberOfLines={1}>
            {item.货柜编号 || getContainerGuid(item) || "未命名货柜"}
          </Text>
          <Text style={styles.cardSubtitle} numberOfLines={1}>
            {`装柜 ${loadingDate || "--"}`}
            {item.汇率 != null ? ` · 汇率 ${item.汇率.toFixed(4)}` : ""}
          </Text>
        </Pressable>
        <View style={[styles.statusPill, { backgroundColor: tone.background }]}>
          <Text style={[styles.statusPillText, { color: tone.color }]}>{containerStatusLabel(item.状态)}</Text>
        </View>
        {canEditContainer ? (
          <Menu
            visible={menuVisible}
            onDismiss={onCloseMenu}
            anchor={<IconButton icon="dots-vertical" size={20} style={styles.menuButton} onPress={onOpenMenu} accessibilityLabel="更多操作" />}
          >
            {CONTAINER_STATUS_OPTIONS.filter((status) => status.value !== item.状态).map((status) => (
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
              leadingIcon="send"
              title="推送 HBSales"
              onPress={() => {
                onCloseMenu();
                onPushHbSales();
              }}
            />
          </Menu>
        ) : null}
      </View>

      <View style={styles.dateRow}>
        <DateBlock
          label="预计到库"
          value={item.预计到岸日期}
          emptyText="设置"
          insight={insight?.target === "estimated" ? insight : null}
          editable={canEditContainer}
          onPress={() => onEditDate("estimated")}
        />
        <DateBlock
          label="实际到库"
          value={item.实际到货日期}
          emptyText="登记"
          insight={insight?.target === "actual" ? insight : null}
          editable={canEditContainer}
          onPress={() => onEditDate("actual")}
        />
      </View>

      <View style={styles.metricRow}>
        <Text style={styles.metricText} numberOfLines={1}>
          <Text style={styles.metricLabel}>件 </Text>{formatInteger(item.合计件数)}
          <Text style={styles.metricLabel}>  数量 </Text>{formatInteger(item.合计数量)}
          <Text style={styles.metricLabel}>  体积 </Text>{formatVolume(item.总体积)}
        </Text>
        <Text style={styles.amount}>{formatMoney(item.合计金额)}</Text>
      </View>
      {item.备注 ? <Text style={styles.remark} numberOfLines={2}>{item.备注}</Text> : null}

      <View style={styles.cardFooter}>
        {canEditContainer ? (
          <Pressable style={styles.selectToggle} onPress={onToggleSelected} accessibilityRole="checkbox" accessibilityState={{ checked: selected }}>
            <Checkbox.Android status={selected ? "checked" : "unchecked"} onPress={onToggleSelected} />
            <Text style={styles.selectText}>选择</Text>
          </Pressable>
        ) : <View />}
        <Button compact icon="chevron-right" contentStyle={styles.detailButtonContent} onPress={onOpenDetail}>
          明细
        </Button>
      </View>
    </Surface>
  );
}

export function ContainerListScreen() {
  const router = useRouter();
  const queryClient = useQueryClient();
  const access = useAuthStore((state) => state.access);
  const [appliedFilters, setAppliedFilters] = useState<ContainerListFilters>(DEFAULT_FILTERS);
  const [searchText, setSearchText] = useState("");
  const [filterVisible, setFilterVisible] = useState(false);
  const [page, setPage] = useState(1);
  const [createVisible, setCreateVisible] = useState(false);
  const [createForm, setCreateForm] = useState(EMPTY_CREATE_FORM);
  const [menuGuid, setMenuGuid] = useState("");
  const [selectedContainerGuids, setSelectedContainerGuids] = useState<string[]>([]);
  const [dateEditing, setDateEditing] = useState<{ container: ContainerMain; field: ArrivalDateField } | null>(null);
  const [dateError, setDateError] = useState("");
  const [snackbar, setSnackbar] = useState("");
  // 以设备本地日期判断逾期；门店与仓库设备都在澳洲本地时区。
  const today = useMemo(() => formatMonthDate(new Date()), []);

  const listQuery = useQuery({
    queryKey: ["containers", "list", appliedFilters, page],
    queryFn: () => getContainerList({ ...appliedFilters, page, pageSize: CONTAINER_LIST_PAGE_SIZE }),
    enabled: access.canViewContainers,
  });

  const containers = listQuery.data?.containers ?? [];
  const totalPages = Math.max(1, listQuery.data?.totalPages ?? 1);
  const summary = useMemo(() => summarizePage(containers), [containers]);
  const selectedContainerSet = useMemo(() => new Set(selectedContainerGuids), [selectedContainerGuids]);
  const sheetFilterCount = countSheetFilters(appliedFilters);
  const canCreateContainer = access.canCreateContainer;
  const canEditContainer = access.canEditContainer;
  const activeStatus = appliedFilters.statuses?.length === 1 ? appliedFilters.statuses[0] : undefined;
  const sortLabel = CONTAINER_SORT_OPTIONS.find((option) => option.value === appliedFilters.dateType)?.label ?? "预计到库";

  const handleBack = useCallback(() => {
    // 深链直达时可能没有历史栈，此时回到货柜管理入口所在的工作台（仅有 Container.View 的账号进不了商品和货位管理）。
    if (router.canGoBack()) {
      router.back();
      return;
    }

    router.navigate("/(shell)/workbench");
  }, [router]);

  const invalidateList = () => queryClient.invalidateQueries({ queryKey: ["containers"] });

  // 任何筛选变化都回到第一页并清空勾选，避免批量推送误带上看不见的货柜。
  const applyFilters = (next: ContainerListFilters) => {
    setPage(1);
    setSelectedContainerGuids([]);
    setAppliedFilters(next);
  };

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

  const arrivalDateMutation = useMutation({
    mutationFn: ({ containerGuid, patch }: { containerGuid: string; patch: UpdateContainerRequest }) =>
      updateContainer(containerGuid, patch),
    onSuccess: () => {
      // 先关弹层再提示：Snackbar 会被原生 Modal 盖住。
      setDateEditing(null);
      void invalidateList();
      setSnackbar("到库日期已更新");
    },
    onError: (error) => setDateError(error instanceof Error ? error.message : "到库日期更新失败"),
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

  const submitSearch = () => {
    applyFilters({ ...appliedFilters, containerNumberFilter: trimToUndefined(searchText) });
  };

  const selectStatus = (status?: number) => {
    applyFilters({ ...appliedFilters, statuses: status == null ? undefined : [status] });
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

  const openDateEditor = (container: ContainerMain, field: ArrivalDateField) => {
    setDateError("");
    setDateEditing({ container, field });
  };

  // 页面上可直接看到并逐个移除的已生效条件（弹层里的货号和日期区间）。
  const activeFilterChips = [
    ...(appliedFilters.itemNumberFilter
      ? [{ key: "item", label: `货号 ${appliedFilters.itemNumberFilter}`, clear: { itemNumberFilter: undefined } }]
      : []),
    ...DATE_RANGE_FIELDS.flatMap((range) => {
      const text = describeDateRange(appliedFilters[range.start], appliedFilters[range.end]);
      return text
        ? [{ key: range.key, label: `${range.label} ${text}`, clear: { [range.start]: undefined, [range.end]: undefined } }]
        : [];
    }),
  ];

  if (!access.canViewContainers) {
    return (
      <SafeAreaView style={styles.safeArea}>
        <EmptyState title="无权访问货柜" description="请联系管理员开通货柜查看权限" />
      </SafeAreaView>
    );
  }

  return (
    <SafeAreaView style={styles.safeArea} edges={["top", "left", "right"]}>
      <View style={styles.topBar}>
        <IconButton icon="arrow-left" onPress={handleBack} accessibilityLabel="返回" style={styles.topBarIcon} />
        <Text style={styles.topBarTitle}>货柜管理</Text>
        <View>
          <IconButton icon="tune-variant" onPress={() => setFilterVisible(true)} accessibilityLabel="筛选" style={styles.topBarIcon} />
          {sheetFilterCount ? <Badge size={16} style={styles.filterBadge}>{sheetFilterCount}</Badge> : null}
        </View>
        {canCreateContainer ? (
          <IconButton
            icon="plus"
            iconColor={HB_COLORS.action}
            onPress={() => setCreateVisible(true)}
            accessibilityLabel="新建货柜"
            style={styles.topBarIcon}
          />
        ) : null}
      </View>

      <ScrollView
        contentContainerStyle={styles.content}
        keyboardShouldPersistTaps="handled"
        refreshControl={<RefreshControl refreshing={listQuery.isRefetching} onRefresh={() => listQuery.refetch()} />}
      >
        <TextInput
          mode="outlined"
          dense
          placeholder="搜索货柜编号"
          value={searchText}
          onChangeText={setSearchText}
          onSubmitEditing={submitSearch}
          returnKeyType="search"
          autoCapitalize="characters"
          autoCorrect={false}
          left={<TextInput.Icon icon="magnify" />}
          right={searchText ? (
            <TextInput.Icon
              icon="close-circle"
              accessibilityLabel="清除搜索"
              onPress={() => {
                setSearchText("");
                applyFilters({ ...appliedFilters, containerNumberFilter: undefined });
              }}
            />
          ) : undefined}
          style={styles.search}
        />

        <ScrollView horizontal showsHorizontalScrollIndicator={false} contentContainerStyle={styles.statusRow}>
          <Chip compact selected={activeStatus == null} showSelectedCheck={false} onPress={() => selectStatus(undefined)} style={activeStatus == null ? styles.statusChipOn : styles.statusChip} textStyle={activeStatus == null ? styles.statusChipTextOn : styles.statusChipText}>
            全部
          </Chip>
          {CONTAINER_STATUS_OPTIONS.map((status) => {
            const selected = activeStatus === status.value;
            return (
              <Chip
                key={status.value}
                compact
                selected={selected}
                showSelectedCheck={false}
                onPress={() => selectStatus(selected ? undefined : status.value)}
                style={selected ? styles.statusChipOn : styles.statusChip}
                textStyle={selected ? styles.statusChipTextOn : styles.statusChipText}
              >
                {status.label}
              </Chip>
            );
          })}
        </ScrollView>

        {activeFilterChips.length ? (
          <View style={styles.activeFilterRow}>
            {activeFilterChips.map((chip) => (
              <Chip
                key={chip.key}
                compact
                icon="filter-variant"
                onClose={() => applyFilters({ ...appliedFilters, ...chip.clear })}
                onPress={() => setFilterVisible(true)}
                style={styles.activeFilterChip}
                textStyle={styles.activeFilterText}
                theme={ACTIVE_FILTER_CHIP_THEME}
              >
                {chip.label}
              </Chip>
            ))}
          </View>
        ) : null}

        <View style={styles.summaryRow}>
          <Text style={styles.summaryText} numberOfLines={1}>
            共 {listQuery.data?.totalCount ?? 0} 柜 · 按{sortLabel}从新到旧
          </Text>
          <Text style={styles.summaryText} numberOfLines={1}>
            本页 {formatInteger(summary.pieces)} 件 · {formatMoney(summary.amount)}
          </Text>
        </View>

        {listQuery.isLoading ? (
          <ActivityIndicator style={styles.loading} />
        ) : listQuery.isError ? (
          <EmptyState title="货柜加载失败" description="下拉刷新重试" />
        ) : containers.length ? (
          containers.map((item) => {
            const containerGuid = getContainerGuid(item);
            return (
              <ContainerCard
                key={containerGuid || item.id || item.ID}
                item={item}
                today={today}
                menuVisible={menuGuid === containerGuid}
                selected={selectedContainerSet.has(containerGuid)}
                canEditContainer={canEditContainer}
                onOpenMenu={() => setMenuGuid(containerGuid)}
                onCloseMenu={() => setMenuGuid("")}
                onOpenDetail={() => router.push(`/containers/${encodeURIComponent(containerGuid)}`)}
                onEditDate={(field) => openDateEditor(item, field)}
                onPushHbSales={() => pushMutation.mutate([containerGuid])}
                onToggleSelected={() => toggleContainerSelection(containerGuid)}
                onUpdateStatus={(status) => statusMutation.mutate({ containerGuid, status })}
              />
            );
          })
        ) : (
          <EmptyState title="没有货柜" description="调整筛选条件后再试" />
        )}

        {totalPages > 1 ? (
          <View style={styles.pagination}>
            <Button mode="outlined" disabled={page <= 1} onPress={() => setPage((value) => Math.max(1, value - 1))}>
              上一页
            </Button>
            <Text style={styles.pageText}>{page} / {totalPages}</Text>
            <Button mode="outlined" disabled={page >= totalPages} onPress={() => setPage((value) => value + 1)}>
              下一页
            </Button>
          </View>
        ) : null}
      </ScrollView>

      {canEditContainer && selectedContainerGuids.length ? (
        <SafeAreaView edges={["bottom"]} style={styles.bulkBar}>
          <Text style={styles.bulkText}>已选 {selectedContainerGuids.length} 柜</Text>
          <Button onPress={() => setSelectedContainerGuids([])}>取消</Button>
          <Button
            icon="send"
            mode="contained"
            loading={pushMutation.isPending}
            disabled={pushMutation.isPending}
            onPress={pushSelectedContainers}
          >
            推送 HBSales
          </Button>
        </SafeAreaView>
      ) : null}

      <ContainerListFilterSheet
        visible={filterVisible}
        filters={appliedFilters}
        onDismiss={() => setFilterVisible(false)}
        onApply={(next) => {
          setFilterVisible(false);
          applyFilters(next);
        }}
      />

      <ContainerArrivalDateSheet
        container={dateEditing?.container ?? null}
        initialField={dateEditing?.field ?? "estimated"}
        saving={arrivalDateMutation.isPending}
        errorMessage={dateError}
        onDismiss={() => setDateEditing(null)}
        onSave={(patch) => {
          const containerGuid = getContainerGuid(dateEditing?.container);
          if (!containerGuid) return;
          setDateError("");
          arrivalDateMutation.mutate({ containerGuid, patch });
        }}
      />

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
            label="预计到库日期"
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
          <View style={styles.modalActions}>
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
    backgroundColor: HB_COLORS.background,
  },
  topBar: {
    flexDirection: "row",
    alignItems: "center",
    paddingHorizontal: HB_SPACING.xxs,
    paddingVertical: HB_SPACING.xxs,
  },
  topBarIcon: {
    margin: 0,
  },
  topBarTitle: {
    flex: 1,
    fontSize: 18,
    fontWeight: "600",
    color: HB_COLORS.textPrimary,
  },
  filterBadge: {
    position: "absolute",
    top: 4,
    right: 2,
    backgroundColor: HB_COLORS.action,
  },
  content: {
    gap: HB_SPACING.sm,
    paddingHorizontal: HB_SPACING.md,
    // 外层只避让顶部安全区，底部留足空间给 Home 指示条。
    paddingBottom: HB_SPACING.xl,
  },
  search: {
    backgroundColor: HB_COLORS.white,
  },
  statusRow: {
    gap: HB_SPACING.xs,
  },
  statusChip: {
    backgroundColor: HB_COLORS.white,
    borderWidth: 1,
    borderColor: HB_COLORS.outlineMuted,
  },
  statusChipOn: {
    backgroundColor: "#EAF2FF",
    borderWidth: 1,
    borderColor: HB_COLORS.brand,
  },
  activeFilterRow: {
    flexDirection: "row",
    flexWrap: "wrap",
    gap: HB_SPACING.xs,
  },
  // Paper Chip 默认文字取主题 secondary（绿色），这里显式用正文色与操作蓝。
  statusChipText: {
    color: HB_COLORS.textPrimary,
  },
  statusChipTextOn: {
    color: HB_COLORS.action,
  },
  activeFilterChip: {
    backgroundColor: HB_COLORS.surfaceMuted,
  },
  activeFilterText: {
    fontSize: 12,
    color: HB_COLORS.textPrimary,
  },
  summaryRow: {
    flexDirection: "row",
    justifyContent: "space-between",
    gap: HB_SPACING.xs,
  },
  summaryText: {
    flexShrink: 1,
    fontSize: 12,
    color: HB_COLORS.textSecondary,
  },
  card: {
    gap: HB_SPACING.sm,
    padding: HB_SPACING.sm,
    borderRadius: HB_RADIUS.surface,
    borderWidth: 1,
    borderColor: HB_COLORS.outlineMuted,
    backgroundColor: HB_COLORS.white,
  },
  cardSelected: {
    borderColor: HB_COLORS.brand,
  },
  cardHeader: {
    flexDirection: "row",
    alignItems: "center",
    gap: HB_SPACING.xs,
  },
  cardTitleBlock: {
    flex: 1,
    minWidth: 0,
  },
  cardTitle: {
    fontSize: 16,
    fontWeight: "600",
    color: HB_COLORS.textPrimary,
  },
  cardSubtitle: {
    marginTop: 2,
    fontSize: 12,
    color: HB_COLORS.textSecondary,
  },
  statusPill: {
    paddingHorizontal: HB_SPACING.xs,
    paddingVertical: 2,
    borderRadius: 999,
  },
  statusPillText: {
    fontSize: 12,
    fontWeight: "500",
  },
  menuButton: {
    margin: 0,
  },
  dateRow: {
    flexDirection: "row",
    gap: HB_SPACING.xs,
  },
  dateBlock: {
    flex: 1,
    minWidth: 0,
    minHeight: 56,
    paddingHorizontal: HB_SPACING.sm,
    paddingVertical: 6,
    borderRadius: HB_RADIUS.control,
    borderWidth: 1,
    borderColor: HB_COLORS.outlineMuted,
    backgroundColor: HB_COLORS.surfaceMuted,
  },
  dateBlockEmpty: {
    borderStyle: "dashed",
    borderColor: HB_COLORS.outline,
    backgroundColor: HB_COLORS.white,
  },
  dateBlockWarning: {
    borderColor: "#FEC84B",
    backgroundColor: "#FFFAEB",
  },
  dateBlockPressed: {
    opacity: 0.7,
  },
  dateBlockHeader: {
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
  },
  dateLabel: {
    fontSize: 11,
    color: HB_COLORS.textSecondary,
  },
  dateValueRow: {
    flexDirection: "row",
    alignItems: "baseline",
    gap: 6,
    marginTop: 2,
  },
  dateValue: {
    fontSize: 16,
    fontWeight: "600",
    color: HB_COLORS.textPrimary,
  },
  dateInsight: {
    flexShrink: 1,
    fontSize: 11,
  },
  dateEmpty: {
    marginTop: 2,
    fontSize: 15,
    color: HB_COLORS.textSecondary,
  },
  dateEmptyAction: {
    marginTop: 2,
    fontSize: 14,
    color: HB_COLORS.action,
  },
  metricRow: {
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    gap: HB_SPACING.xs,
  },
  metricText: {
    flexShrink: 1,
    fontSize: 13,
    color: HB_COLORS.textPrimary,
  },
  metricLabel: {
    color: HB_COLORS.textSecondary,
  },
  amount: {
    fontSize: 14,
    fontWeight: "600",
    color: HB_COLORS.textPrimary,
  },
  remark: {
    fontSize: 12,
    color: HB_COLORS.textSecondary,
  },
  cardFooter: {
    flexDirection: "row",
    alignItems: "center",
    justifyContent: "space-between",
    marginHorizontal: -HB_SPACING.xs,
    marginBottom: -HB_SPACING.xs,
    borderTopWidth: StyleSheet.hairlineWidth,
    borderTopColor: HB_COLORS.outlineMuted,
  },
  selectToggle: {
    flexDirection: "row",
    alignItems: "center",
  },
  selectText: {
    fontSize: 13,
    color: HB_COLORS.textSecondary,
  },
  detailButtonContent: {
    flexDirection: "row-reverse",
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
  bulkBar: {
    flexDirection: "row",
    alignItems: "center",
    gap: HB_SPACING.xs,
    paddingHorizontal: HB_SPACING.md,
    paddingTop: HB_SPACING.xs,
    borderTopWidth: StyleSheet.hairlineWidth,
    borderTopColor: HB_COLORS.outline,
    backgroundColor: HB_COLORS.white,
  },
  bulkText: {
    flex: 1,
    fontSize: 14,
    color: HB_COLORS.textPrimary,
  },
  inputRow: {
    flexDirection: "row",
    gap: 10,
  },
  inputHalf: {
    flex: 1,
  },
  modalActions: {
    flexDirection: "row",
    justifyContent: "flex-end",
    gap: 8,
  },
  modal: {
    margin: 18,
    gap: 10,
    padding: 16,
    borderRadius: 18,
    backgroundColor: "#FFFFFF",
  },
});
