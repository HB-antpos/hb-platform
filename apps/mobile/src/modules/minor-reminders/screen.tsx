import { useState } from "react";
import {
  FlatList,
  Linking,
  RefreshControl,
  StyleSheet,
  View,
} from "react-native";
import {
  useInfiniteQuery,
  useMutation,
  useQueryClient,
} from "@tanstack/react-query";
import { Stack, useLocalSearchParams } from "expo-router";
import {
  ActivityIndicator,
  Button,
  Card,
  Chip,
  Dialog,
  Portal,
  SegmentedButtons,
  Text,
  TextInput,
} from "react-native-paper";
import { SafeAreaView } from "react-native-safe-area-context";
import { useAuthStore } from "@/store/auth-store";
import { useAppTranslation } from "@/shared/i18n/use-app-translation";
import {
  actOnMinorReminder,
  getMinorReminders,
  type MinorReminder,
} from "./api";
import { MinorEmployeeRequestsPanel } from "./minor-employee-requests";

export function MinorRemindersScreen() {
  const { language } = useAppTranslation("attendance");
  const en = language.startsWith("en");
  const label = (zh: string, english: string) => (en ? english : zh);
  const { storeCode } = useLocalSearchParams<{ storeCode?: string }>();
  const user = useAuthStore((state) => state.user);
  const queryClient = useQueryClient();
  const [status, setStatus] = useState("open");
  // 顶部切换：排班提醒待办 / 未成年员工资料（店长发起填写）。
  const [view, setView] = useState<"reminders" | "employees">("reminders");
  const [selected, setSelected] = useState<{
    row: MinorReminder;
    action: "acknowledge" | "escalate";
  } | null>(null);
  const [comment, setComment] = useState("");
  const queryKey = ["minor-reminders", user?.userGUID, storeCode, status];
  const query = useInfiniteQuery({
    queryKey,
    initialPageParam: 1,
    queryFn: ({ pageParam }) => getMinorReminders(status, pageParam, storeCode),
    getNextPageParam: (page) =>
      page.page * page.pageSize < page.total ? page.page + 1 : undefined,
  });
  const mutation = useMutation({
    mutationFn: () => {
      if (!selected) throw new Error("No reminder selected");
      return actOnMinorReminder(selected.row, selected.action, comment);
    },
    onSuccess: async () => {
      setSelected(null);
      setComment("");
      await queryClient.invalidateQueries({ queryKey: ["minor-reminders"] });
    },
  });
  const rows = query.data?.pages.flatMap((page) => page.items) ?? [];
  const categories: Record<string, string> = {
    qld_law: label("QLD 法定要求", "QLD legislation"),
    education: label("学校 / 教育安排", "Education"),
    award: label("适用 Award 待核实", "Award review"),
    company_safety: label("通勤与安全", "Travel and safety"),
    incomplete: label("资料待补充", "Information required"),
  };
  return (
    <SafeAreaView style={styles.page} edges={["bottom"]}>
      <Stack.Screen
        options={{
          title: label("未成年用工提醒", "Minor employment reminders"),
        }}
      />
      <View style={styles.header}>
        <Text variant="headlineSmall">
          {label("经理提醒待办", "Manager follow-up")}
        </Text>
        <Text style={styles.muted}>
          {label(
            "提醒不阻断排班发布或打卡。确认知悉会保留风险；升级后由上级在同一列表跟进。",
            "Reminders do not block publishing or punches. Acknowledgement keeps the finding; escalated items remain available for senior review.",
          )}
        </Text>
        {storeCode ? <Chip>{storeCode}</Chip> : null}
        <SegmentedButtons
          value={view}
          onValueChange={(value) => setView(value as "reminders" | "employees")}
          buttons={[
            { value: "reminders", label: label("排班提醒", "Reminders") },
            {
              value: "employees",
              label: label("未成年员工资料", "Under-18 staff"),
            },
          ]}
        />
        {view === "reminders" ? (
          <SegmentedButtons
            value={status}
            onValueChange={setStatus}
            buttons={[
              { value: "open", label: label("待处理", "Open") },
              { value: "escalated", label: label("已升级", "Escalated") },
              { value: "acknowledged", label: label("已知悉", "Noted") },
              { value: "resolved", label: label("已解除", "Resolved") },
            ]}
          />
        ) : null}
      </View>
      {view === "employees" ? (
        <MinorEmployeeRequestsPanel storeCode={storeCode} en={en} />
      ) : null}
      {view === "reminders" && query.isError ? (
        <View style={styles.header}>
          <Text accessibilityRole="alert">{query.error.message}</Text>
          <Button onPress={() => query.refetch()}>
            {label("重试", "Retry")}
          </Button>
        </View>
      ) : null}
      {view === "reminders" ? (
        <FlatList
          data={rows}
          keyExtractor={(row) => String(row.id)}
          contentContainerStyle={styles.list}
          refreshControl={
            <RefreshControl
              refreshing={query.isRefetching}
              onRefresh={() => query.refetch()}
            />
          }
          ListEmptyComponent={
            query.isPending ? (
              <ActivityIndicator />
            ) : !query.isError ? (
              <Text style={styles.muted}>
                {label("当前没有此状态的提醒", "No reminders with this status")}
              </Text>
            ) : null
          }
          ListFooterComponent={
            query.hasNextPage ? (
              <Button
                loading={query.isFetchingNextPage}
                onPress={() => query.fetchNextPage()}
              >
                {label("加载更多", "Load more")}
              </Button>
            ) : null
          }
          renderItem={({ item }) => (
            <Card mode="outlined" style={styles.card}>
              <Card.Content style={styles.content}>
                <Text variant="titleMedium">
                  {item.employeeName || item.userGUID}
                </Text>
                <Text style={styles.muted}>
                  {item.storeCode} ·{" "}
                  {item.workDate?.slice(0, 10) ??
                    label("日期待确认", "Date unconfirmed")}
                </Text>
                <Text style={styles.category}>
                  {categories[item.ruleCategory] ?? item.ruleCategory}
                </Text>
                <Text>{item.message}</Text>
                {item.actualMinutes != null && item.limitMinutes != null ? (
                  <Text>
                    {label("已计算", "Calculated")}:{" "}
                    {(item.actualMinutes / 60).toFixed(1)}h /{" "}
                    {label("参考上限", "Limit")}:{" "}
                    {(item.limitMinutes / 60).toFixed(1)}h
                  </Text>
                ) : null}
                {item.actionActor ? (
                  <Text style={styles.muted}>
                    {item.actionActor}
                    {item.actionComment ? ` · ${item.actionComment}` : ""}
                  </Text>
                ) : null}
                {item.sourceUrl?.startsWith("https://") ? (
                  <Button
                    icon="open-in-new"
                    onPress={() => Linking.openURL(item.sourceUrl!)}
                  >
                    {label("查看规则依据", "View source")}
                  </Button>
                ) : null}
              </Card.Content>
              <Card.Actions>
                {item.status !== "acknowledged" &&
                item.status !== "resolved" ? (
                  <Button
                    onPress={() => {
                      mutation.reset();
                      setComment("");
                      setSelected({ row: item, action: "acknowledge" });
                    }}
                  >
                    {label("确认知悉", "Acknowledge")}
                  </Button>
                ) : null}
                {item.status !== "escalated" && item.status !== "resolved" ? (
                  <Button
                    mode="contained-tonal"
                    onPress={() => {
                      mutation.reset();
                      setComment("");
                      setSelected({ row: item, action: "escalate" });
                    }}
                  >
                    {label("升级跟进", "Escalate")}
                  </Button>
                ) : null}
              </Card.Actions>
            </Card>
          )}
        />
      ) : null}
      <Portal>
        <Dialog
          visible={selected !== null}
          onDismiss={() => {
            if (!mutation.isPending) setSelected(null);
          }}
        >
          <Dialog.Title>
            {selected?.action === "escalate"
              ? label("升级跟进", "Escalate")
              : label("确认知悉", "Acknowledge")}
          </Dialog.Title>
          <Dialog.Content>
            <TextInput
              mode="outlined"
              label={label("备注（选填）", "Comment (optional)")}
              value={comment}
              onChangeText={setComment}
              multiline
              maxLength={1000}
            />
            {mutation.isError ? (
              <Text accessibilityRole="alert">{mutation.error.message}</Text>
            ) : null}
          </Dialog.Content>
          <Dialog.Actions>
            <Button
              disabled={mutation.isPending}
              onPress={() => setSelected(null)}
            >
              {label("取消", "Cancel")}
            </Button>
            <Button
              loading={mutation.isPending}
              disabled={mutation.isPending}
              onPress={() => mutation.mutate()}
            >
              {label("保存", "Save")}
            </Button>
          </Dialog.Actions>
        </Dialog>
      </Portal>
    </SafeAreaView>
  );
}
const styles = StyleSheet.create({
  page: { flex: 1, backgroundColor: "#F4F7FB" },
  header: { padding: 16, gap: 12 },
  list: { padding: 16, paddingTop: 0, gap: 12 },
  card: { backgroundColor: "#FFFFFF" },
  content: { gap: 8 },
  muted: { color: "#526174", lineHeight: 21 },
  category: { color: "#175AB1", fontWeight: "600" },
});
