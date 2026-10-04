import { useState } from "react";
import { FlatList, RefreshControl, StyleSheet, View } from "react-native";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import {
  ActivityIndicator,
  Button,
  Card,
  Chip,
  Dialog,
  Portal,
  Text,
  TextInput,
} from "react-native-paper";
import { minorEmploymentApi } from "@/modules/minor-employment/api";
import type { MinorManagerCandidate } from "@/modules/minor-employment/types";

/** 档案状态 → 店长可读的进度说明（后端原始状态码）。 */
const STATUS_LABELS: Record<string, [string, string]> = {
  draft: ["员工填写中", "Employee drafting"],
  awaiting_guardian_signature: ["等待监护人签署", "Awaiting guardian"],
  signed_pending_employee_submit: [
    "监护人已签，待员工提交",
    "Signed, not submitted",
  ],
  pending_hr_review: ["待 HR 审核", "HR review"],
  approved: ["已通过", "Approved"],
  returned: ["HR 已退回", "Returned by HR"],
};

/**
 * 店长发起未成年用工资料填写：列出可管理门店里未满 18 岁的员工，
 * 可"通知员工填写"（员工端出现待办）或撤销；员工提交 HR 审核后请求自动完成。
 */
export function MinorEmployeeRequestsPanel({
  storeCode,
  en,
}: {
  storeCode?: string;
  en: boolean;
}) {
  const label = (zh: string, english: string) => (en ? english : zh);
  const queryClient = useQueryClient();
  const queryKey = ["minor-employment", "manager-candidates", storeCode ?? ""];
  const query = useQuery({
    queryKey,
    queryFn: () => minorEmploymentApi.getManagerCandidates(storeCode),
  });
  const [target, setTarget] = useState<MinorManagerCandidate | null>(null);
  const [note, setNote] = useState("");
  const create = useMutation({
    mutationFn: () => {
      if (!target) throw new Error("No employee selected");
      return minorEmploymentApi.createRequest(target.userGUID, note);
    },
    onSuccess: async () => {
      setTarget(null);
      setNote("");
      await queryClient.invalidateQueries({ queryKey });
    },
  });
  const cancel = useMutation({
    mutationFn: (id: number) => minorEmploymentApi.cancelRequest(id),
    onSuccess: () => queryClient.invalidateQueries({ queryKey }),
  });

  return (
    <>
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
                "按员工资料里的生日计算，只列出你管理门店里未满 18 岁的员工。QLD 学龄儿童开工前须有监护人签署的 CE1；NSW 为公司要求。",
                "Employees under 18 in your stores, by profile birthday. QLD school-aged children need a signed CE1 before starting; NSW is company policy.",
              )}
            </Text>
            {query.isError ? (
              <Text accessibilityRole="alert" style={styles.error}>
                {query.error.message}
              </Text>
            ) : null}
            {cancel.isError ? (
              <Text accessibilityRole="alert" style={styles.error}>
                {cancel.error.message}
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
                      : label("未建档", "Not started")}
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
                {item.openRequest ? (
                  <Text style={styles.pending}>
                    {label("已通知员工填写", "Employee notified")}
                    {item.openRequest.createdAt
                      ? ` · ${item.openRequest.createdAt.slice(0, 10)}`
                      : ""}
                    {item.openRequest.note ? ` · ${item.openRequest.note}` : ""}
                  </Text>
                ) : null}
              </Card.Content>
              {!done ? (
                <Card.Actions>
                  {item.openRequest ? (
                    <Button
                      loading={
                        cancel.isPending &&
                        cancel.variables === item.openRequest.id
                      }
                      disabled={cancel.isPending}
                      onPress={() => cancel.mutate(item.openRequest!.id)}
                    >
                      {label("撤销通知", "Withdraw")}
                    </Button>
                  ) : (
                    <Button
                      mode="contained-tonal"
                      icon="send-outline"
                      onPress={() => {
                        create.reset();
                        setNote("");
                        setTarget(item);
                      }}
                    >
                      {label("通知员工填写", "Ask employee to fill in")}
                    </Button>
                  )}
                </Card.Actions>
              ) : null}
            </Card>
          );
        }}
      />
      <Portal>
        <Dialog
          visible={target !== null}
          onDismiss={() => {
            if (!create.isPending) setTarget(null);
          }}
        >
          <Dialog.Title>
            {label("通知员工填写", "Ask employee to fill in")}
          </Dialog.Title>
          <Dialog.Content style={styles.content}>
            <Text>
              {label(
                `${target?.employeeName ?? ""} 会在「个人信息 › 未成年用工合规」看到待办，填好后由系统把签署邮件发给监护人。`,
                `${target?.employeeName ?? ""} will see a to-do in their profile. The signing email goes to the guardian once the form is filled in.`,
              )}
            </Text>
            <TextInput
              mode="outlined"
              label={label("备注（选填）", "Note (optional)")}
              value={note}
              onChangeText={setNote}
              multiline
              maxLength={500}
            />
            {create.isError ? (
              <Text accessibilityRole="alert" style={styles.error}>
                {create.error.message}
              </Text>
            ) : null}
          </Dialog.Content>
          <Dialog.Actions>
            <Button disabled={create.isPending} onPress={() => setTarget(null)}>
              {label("取消", "Cancel")}
            </Button>
            <Button
              loading={create.isPending}
              disabled={create.isPending}
              onPress={() => create.mutate()}
            >
              {label("发送通知", "Send")}
            </Button>
          </Dialog.Actions>
        </Dialog>
      </Portal>
    </>
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
  pending: { color: "#175AB1" },
  error: { color: "#B42318" },
});
