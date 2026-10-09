import type {
  SettingsLinklyTerminalSelectionSnapshot,
  SettingsPaymentSettingsInput,
} from "../../features/settings/settings-presenter";
import {
  mergeSettingsSquareDevices,
  normalizeSettingsSquareDeviceId,
  type SettingsSquareDevice,
} from "@hb/pos-domain/features/settings/settings-square-setup";
import type { components } from "@hb/pos-api-client/openapi";
import {
  unwrapHbposEnvelope,
  type HbposEnvelope,
  type HbposTransport,
} from "../api/hbpos-api";

type SquareDevice = components["schemas"]["SquareDeviceDto"];
type LinklyLogon =
  components["schemas"]["LinklyCloudBackendLogonTestResponse"];
type LinklyStatus = components["schemas"]["LinklyCloudBackendStatusTestResponse"];

// Linkly 上游最多等待 240 秒，额外留出 API 收尾时间；仅覆盖终端验证请求。
const LINKLY_TEST_TIMEOUT_MS = 270_000;

/**
 * 设置页的测试调用不会创建 checkout 或扣款。Square Sandbox 使用官方 checkout
 * 测试终端（Sandbox 不支持 Devices API），Production 才验证设备仍在后端可见；
 * Linkly 复用 WPF 的 Backend Async logon-test。
 */
export class HbposSettingsPaymentTestApi {
  public constructor(private readonly transport: HbposTransport) {}

  public async test(
    provider: "square" | "linkly",
    input: SettingsPaymentSettingsInput,
    signal: AbortSignal,
    terminals?: SettingsLinklyTerminalSelectionSnapshot | null,
  ): Promise<void> {
    if (provider === "square") {
      await this.testSquare(input, signal);
      return;
    }
    await this.testLinkly(input, signal, terminals);
  }

  private async testSquare(
    input: SettingsPaymentSettingsInput,
    signal: AbortSignal,
  ): Promise<void> {
    const configuration = input.square;
    if (!configuration) {
      throw new Error("Square test configuration is unavailable.");
    }
    const locationId = requiredText(
      configuration.locationId,
      "Square location is required for payment test.",
    );
    const candidateDeviceId = normalizeSettingsSquareDeviceId(
      configuration.deviceId,
    );
    throwIfAborted(signal);
    const devices =
      configuration.environment === "Sandbox"
        ? mergeSettingsSquareDevices("Sandbox", locationId, [])
        : await this.listProductionSquareDevices(locationId, signal);
    const found = devices.some(
      (device) =>
        candidateDeviceId !== null &&
        device.id.toLowerCase() === candidateDeviceId.toLowerCase() &&
        normalizedText(device.locationId) === locationId &&
        normalizedText(device.status).toUpperCase() !== "DISABLED",
    );
    if (!found) {
      throw new Error(
        "Square device is not available at the selected location.",
      );
    }
  }

  private async listProductionSquareDevices(
    locationId: string,
    signal: AbortSignal,
  ): Promise<readonly SettingsSquareDevice[]> {
    const response = await this.transport.request<
      HbposEnvelope<readonly SquareDevice[]>
    >({
      method: "GET",
      url: "/api/v1/square/devices",
      params: {
        environment: "Production",
        locationId,
      },
      signal,
    });
    return mergeSettingsSquareDevices(
      "Production",
      locationId,
      unwrapHbposEnvelope(response.data).flatMap(
        (device): SettingsSquareDevice[] => {
          const id = normalizeSettingsSquareDeviceId(device.id);
          if (!id) return [];
          return [
            {
              id,
              code: normalizedOptionalText(device.code),
              name: normalizedOptionalText(device.name) ?? id,
              status: normalizedOptionalText(device.status),
              locationId: normalizedOptionalText(device.locationId),
              sandboxTest: false,
            },
          ];
        },
      ),
    );
  }

  private async testLinkly(
    input: SettingsPaymentSettingsInput,
    signal: AbortSignal,
    terminals?: SettingsLinklyTerminalSelectionSnapshot | null,
  ): Promise<void> {
    const configuration = input.linkly;
    if (!configuration) {
      throw new Error("Linkly test configuration is unavailable.");
    }
    const params = linklyLogonTestParams(configuration.environment, terminals);
    try {
      throwIfAborted(signal);
      const statusResponse = await this.transport.request<HbposEnvelope<LinklyStatus>>({
        method: "POST",
        url: "/api/v1/linkly/cloud-backend/status-test",
        params,
        signal,
        timeoutMs: LINKLY_TEST_TIMEOUT_MS,
      });
      const status = unwrapHbposEnvelope(statusResponse.data);
      assertLinklyTestResponseConfirmed(status.httpStatus);
      const code = status.responseCode?.trim().toUpperCase();
      if (status.succeeded === true && status.loggedOn === true) return;
      if (status.succeeded !== true && code !== "TF") {
        throw new Error("Linkly terminal status test failed.");
      }
      // 明确需要签到，或旧服务未提供 LoggedOn 时，使用原有 Logon 验证兼容路径。
      // 不把状态查询成功、配对成功或缺失字段当成已签到，也不自动重放不明请求。
      throwIfAborted(signal);
      const response = await this.transport.request<HbposEnvelope<LinklyLogon>>({
        method: "POST",
        url: "/api/v1/linkly/cloud-backend/logon-test",
        params,
        signal,
        timeoutMs: LINKLY_TEST_TIMEOUT_MS,
      });
      const result = unwrapHbposEnvelope(response.data);
      assertLinklyTestResponseConfirmed(result.httpStatus);
      if (result.succeeded !== true || result.responseCode?.trim() !== "00") {
        throw new Error("Linkly Cloud logon test was declined.");
      }
    } catch (error) {
      if (signal.aborted) throw error;
      if (error && typeof error === "object" && (
        ("kind" in error && error.kind === "transport") ||
        ("code" in error && ["ECONNABORTED", "ETIMEDOUT", "ERR_NETWORK"].includes(String(error.code))) ||
        ("status" in error && typeof error.status === "number" &&
          (error.status === 408 || error.status >= 500))
      )) throw linklyTestUnconfirmed();
      throw error;
    }
  }
}

function linklyTestUnconfirmed(): Error {
  return Object.assign(new Error("Linkly test result is not confirmed."), {
    code: "LINKLY_TEST_UNCONFIRMED",
  });
}

function assertLinklyTestResponseConfirmed(httpStatus: number | undefined): void {
  if (httpStatus === undefined || httpStatus === 202 || httpStatus === 408 || httpStatus >= 500) {
    throw linklyTestUnconfirmed();
  }
  if (httpStatus !== 200) throw new Error("Linkly terminal test failed.");
}

function linklyLogonTestParams(
  environment: "Sandbox" | "Production",
  terminals?: SettingsLinklyTerminalSelectionSnapshot | null,
): Readonly<Record<string, string | number>> {
  if (terminals?.mode !== "Active") return { environment };
  const terminalId = terminals.selectedTerminalId?.trim() ?? "";
  if (
    terminals.environment !== environment ||
    !terminalId ||
    !Number.isSafeInteger(terminals.selectionRevision) ||
    terminals.selectionRevision <= 0
  ) {
    throw new Error("Linkly terminal selection is invalid for payment test.");
  }
  return {
    environment,
    terminalId,
    selectionRevision: terminals.selectionRevision,
  };
}

function normalizedText(value: unknown): string {
  return typeof value === "string" ? value.trim() : "";
}

function requiredText(value: unknown, message: string): string {
  const normalized = normalizedText(value);
  if (!normalized) throw new Error(message);
  return normalized;
}

function normalizedOptionalText(value: unknown): string | null {
  const normalized = normalizedText(value);
  return normalized || null;
}

function throwIfAborted(signal: AbortSignal): void {
  if (!signal.aborted) return;
  const error = new Error("Settings payment test aborted.");
  error.name = "AbortError";
  throw error;
}
