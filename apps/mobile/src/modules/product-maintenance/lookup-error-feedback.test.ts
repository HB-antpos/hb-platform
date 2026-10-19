import { AxiosError, AxiosHeaders } from "axios";
import { describeLookupRequestError } from "./lookup-error-feedback";

function assertEqual(actual: unknown, expected: unknown, label: string) {
  if (actual !== expected) {
    throw new Error(`${label}: expected ${String(expected)}, got ${String(actual)}`);
  }
}

function axiosError(code: string | undefined, status?: number) {
  const config = { headers: new AxiosHeaders() };
  const response = status === undefined
    ? undefined
    : { status, statusText: "", headers: {}, config, data: {} };
  return new AxiosError("Request failed", code, config, {}, response);
}

const timeout = describeLookupRequestError(axiosError("ECONNABORTED"));
assertEqual(timeout?.key, "messages.lookupTimeout", "axios timeout maps to lookup timeout");
assertEqual(timeout?.retryable, true, "lookup timeout offers retry");

const etimedout = describeLookupRequestError(axiosError("ETIMEDOUT"));
assertEqual(etimedout?.key, "messages.lookupTimeout", "ETIMEDOUT also maps to lookup timeout");

const network = describeLookupRequestError(axiosError("ERR_NETWORK"));
assertEqual(network?.key, "messages.lookupNetworkError", "connection failure maps to network error");
assertEqual(network?.retryable, true, "network error offers retry");

const server = describeLookupRequestError(axiosError("ERR_BAD_RESPONSE", 502));
assertEqual(server?.key, "messages.lookupServerError", "5xx maps to server busy");
assertEqual(server?.values?.status, 502, "server busy carries the status code");
assertEqual(server?.retryable, true, "server busy offers retry");

const clientError = describeLookupRequestError(axiosError("ERR_BAD_REQUEST", 403));
assertEqual(clientError?.key, "messages.lookupFailed", "4xx keeps the generic lookup failure");
assertEqual(clientError?.retryable, false, "4xx does not offer retry");

assertEqual(
  describeLookupRequestError(new Error("snapshot read failed")),
  null,
  "non-axios errors are left to the page's real-cause message",
);
