import { FlatList, RefreshControl, StyleSheet, View } from "react-native";
import { useRouter } from "expo-router";
import { useQuery } from "@tanstack/react-query";
import { ActivityIndicator, Button, Card, Chip, Text } from "react-native-paper";
import { minorEmploymentApi } from "@/modules/minor-employment/api";
import { buildDetailRoute } from "@/modules/people/detail/detail-navigation";

/** 档案状态 → 店长可读的进度说明（后端原始状态码）。 */
const STATUS_LABELS: Record<string, [string, string]> = {
  draft: ["未发送", "Not sent"],
  awaiting_guardian_signature: ["待家长签字", "Awaiting guardian"],
  signed_pending_employee_submit: [
    "家长已签字",
    "Signed by guardian",
  ],
  pending_hr_review: ["待 HR 审核", "Awaiting HR review"],
  approved: ["已通过", "Approved"],
  returned: ["HR 已退回", "Returned by HR"],
};

/**
 * 店长查看可管理门店里未满 18 岁的员工及其资料进度。
 * 员工本人不再填写：「去发送家长签字链接」跳到该员工的人员详情，在「未成年用工合规」一节里给家长发链接。
 */
export function MinorEmployeeRequestsPanel({
  storeCode,
  en,
}: {
  storeCode?: string;
  en: boolean;
}) {
  const label = (zh: string, english: string) => (en ? english : zh);
  const router = useRouter();
  const query = useQuery({
    queryKey: ["minor-employment", "manager-candidates", storeCode ?? ""],
    queryFn: () => minorEmploymentApi.getManagerCandidates(storeCode),
  });

  return (
    <FlatList
      data={query.data ?? []}
      keyExtractor={(row) => row.userGUID}
      contentContainerStyle={styles.list}
      refreshControl={
        <RefreshControl
          refreshing={query.isRefetching}
          onRefresh={() => query.refetch()}
        />
      }
      ListHeaderComponent={
        <View style={styles.headerText}>
          <Text style={styles.muted}>
            {label(
              "按员工资料里的生日计算，只列出你管理门店里未满 18 岁的员工。QLD 学龄儿童开工前须有监护人签署的 CE1；NSW 为公司要求。资料由你在员工详情里发送给家长填写并签字，员工本人无需操作。",
              "Employees under 18 in your stores, by profile birthday. QLD school-aged children need a signed CE1 before starting; NSW is company policy. Send the form to the guardian from the employee's details; the employee doesn't need to do anything.",
            )}
          </Text>
          {query.isError ? (
            <Text accessibilityRole="alert" style={styles.error}>
              {query.error.message}
            </Text>
          ) : null}
        </View>
      }
      ListEmptyComponent={
        query.isPending ? (
          <ActivityIndicator />
        ) : !query.isError ? (
          <Text style={styles.muted}>
            {label("没有未满 18 岁的员工", "No employees under 18")}
          </Text>
        ) : null
      }
      renderItem={({ item }) => {
        const status = item.complianceStatus
          ? STATUS_LABELS[item.complianceStatus]
          : undefined;
        const done =
          item.complianceStatus === "approved" ||
          item.complianceStatus === "pending_hr_review";
        const detailStoreCode = item.storeCode || storeCode;
        return (
          <Card mode="outlined" style={styles.card}>
            <Card.Content style={styles.content}>
              <View style={styles.titleRow}>
                <Text variant="titleMedium" style={styles.flex}>
                  {item.employeeName}
                </Text>
                <Chip compact>
                  {status
                    ? label(status[0], status[1])
                    : label("未发送", "Not sent")}
                </Chip>
              </View>
              <Text style={styles.muted}>
                {[
                  label(`${item.age} 岁`, `Age ${item.age}`),
                  item.storeCode,
                  item.stateCode,
                ]
                  .filter(Boolean)
                  .join(" · ")}
              </Text>
            </Card.Content>
            {!done ? (
              <Card.Actions>
                {/* 原「通知员工填写」改为跳到人员详情，由店长在那里给家长发送签字链接。 */}
                <Button
                  mode="contained-tonal"
                  icon="send-outline"
                  onPress={() =>
                    router.push(
                      buildDetailRoute({
                        userGuid: item.userGUID,
                        storeCode: detailStoreCode,
                        userName: item.employeeName,
                      }) as unknown as Parameters<typeof router.push>[0],
                    )
                  }
                >
                  {label("去发送家长签字链接", "Send guardian signing link")}
                </Button>
              </Card.Actions>
            ) : null}
          </Card>
        );
      }}
    />
  );
}

const styles = StyleSheet.create({
  list: { padding: 16, paddingTop: 0, gap: 12 },
  headerText: { gap: 8, paddingBottom: 4 },
  card: { backgroundColor: "#FFFFFF" },
  content: { gap: 8 },
  titleRow: { flexDirection: "row", alignItems: "center", gap: 8 },
  flex: { flex: 1 },
  muted: { color: "#526174", lineHeight: 21 },
  error: { color: "#B42318" },
});
