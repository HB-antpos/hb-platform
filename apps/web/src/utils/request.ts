import type { ApiResponse, PagedResult } from '../types/api'
import { getClientPublicIpHeaders } from './clientPublicIp'
import { isCenterLogIngestRequest, reportRequestError } from './centerLogClient'

export interface RequestOptions {
  method?: 'GET' | 'POST' | 'PUT' | 'DELETE' | 'PATCH'
  params?: Record<string, unknown>
  data?: unknown | FormData
  headers?: Record<string, string>
  signal?: AbortSignal
  cache?: RequestCache
  skipAuthRedirect?: boolean
  /** 仅在最终成功的响应上回调，供调用方读取响应头（如 X-Price-Notification）；回调异常不影响请求结果。 */
  onResponse?: (response: Response) => void
  /** 成功响应按二进制读取（如下载 PDF）；失败响应仍按 JSON / 文本解析，错误码照常可读。 */
  responseType?: 'blob'
}

export class RequestError extends Error {
  status: number
  payload?: unknown

  constructor(message: string, status: number, payload?: unknown) {
    super(message)
    this.name = 'RequestError'
    this.status = status
    this.payload = payload
  }
}

function buildQueryString(params?: Record<string, unknown>) {
  if (!params) {
    return ''
  }

  const searchParams = new URLSearchParams()

  Object.entries(params).forEach(([key, value]) => {
    if (value === undefined || value === null || value === '') {
      return
    }

    if (Array.isArray(value)) {
      value.forEach((item) => {
        if (item !== undefined && item !== null && item !== '') {
          searchParams.append(key, String(item))
        }
      })
      return
    }

    searchParams.append(key, String(value))
  })

  const query = searchParams.toString()
  return query ? `?${query}` : ''
}

const API_BASE_URL = (((import.meta as ImportMeta & { env?: ImportMetaEnv }).env?.VITE_API_BASE_URL) || '').trim()
const LOGIN_PATH = '/login'
export const AUTH_EXPIRED_EVENT = 'hbweb:auth-expired'
const AUTH_WHITELIST = new Set([
  '/api/Auth/session/login',
  '/api/Auth/session/logout',
  '/api/Auth/session/refresh',
])

let authRedirecting = false

let refreshPromise: Promise<boolean> | null = null

function buildRequestUrl(url: string, params?: Record<string, unknown>) {
  const requestPath = url.startsWith('http://') || url.startsWith('https://')
    ? url
    : `${API_BASE_URL}${url}`.replace(/([^:]\/)\/+/g, '$1')

  return `${requestPath}${buildQueryString(params)}`
}

async function tryRefreshToken(): Promise<boolean> {
  if (refreshPromise) {
    return refreshPromise
  }

  refreshPromise = (async () => {
    try {
      const refreshUrl = buildRequestUrl('/api/Auth/session/refresh')
      const response = await fetch(refreshUrl, {
        method: 'POST',
        credentials: 'include',
        headers: {
          'Content-Type': 'application/json',
          ...(await getClientPublicIpHeaders()),
        },
        body: JSON.stringify({}),
      })

      if (!response.ok) {
        return false
      }

      const payload = await response.json()
      return !!(payload?.success ?? payload?.data)
    } catch {
      return false
    } finally {
      refreshPromise = null
    }
  })()

  return refreshPromise
}

function handleUnauthorized(requestUrl: string) {
  if (typeof window === 'undefined' || authRedirecting) {
    return
  }

  const currentPath = `${window.location.pathname}${window.location.search}`
  const normalizedUrl = requestUrl.replace(API_BASE_URL, '')

  if (window.location.pathname === LOGIN_PATH || AUTH_WHITELIST.has(normalizedUrl)) {
    return
  }

  authRedirecting = true
  window.dispatchEvent(new Event(AUTH_EXPIRED_EVENT))
  window.location.replace(`${LOGIN_PATH}?redirect=${encodeURIComponent(currentPath)}`)
}

async function parseResponse<T>(response: Response): Promise<T> {
  const contentType = response.headers.get('content-type') || ''
  if (contentType.includes('application/json')) {
    return (await response.json()) as T
  }

  return (await response.text()) as T
}

async function rawFetch<T>(url: string, options: RequestOptions = {}): Promise<{ response: Response; payload: T }> {
  const { method = 'GET', params, data, headers, signal, cache } = options
  const requestUrl = buildRequestUrl(url, params)
  const isFormDataBody = typeof FormData !== 'undefined' && data instanceof FormData
  const response = await fetch(requestUrl, {
    method,
    credentials: 'include',
    headers: {
      // FormData 必须交给浏览器/运行时自动补 multipart boundary，不能手动写 JSON 头。
      ...(data && !isFormDataBody ? { 'Content-Type': 'application/json' } : {}),
      ...headers,
    },
    body: data ? (isFormDataBody ? data : JSON.stringify(data)) : undefined,
    signal,
    cache,
  })

  // 下载类接口：只有成功时才读 Blob；失败时后端返回 JSON ApiResponse，仍走常规解析以拿到错误码。
  const payload =
    options.responseType === 'blob' && response.ok ? ((await response.blob()) as T) : await parseResponse<T>(response)
  return { response, payload }
}

function notifyResponse(options: RequestOptions, response: Response) {
  try {
    options.onResponse?.(response)
  } catch {
    // 读取响应头只是附加信息，不能让它把一次成功的保存变成失败。
  }
}

async function request<T>(url: string, options: RequestOptions = {}): Promise<T> {
  const { skipAuthRedirect = false } = options
  const normalizedUrl = url.replace(API_BASE_URL, '')

  let response: Response
  let payload: unknown

  try {
    const result = await rawFetch<unknown>(url, options)
    response = result.response
    payload = result.payload
  } catch (error) {
    // 网络层异常没有响应体，但仍要异步上报，且不能阻塞原始错误抛出。
    reportRequestError({
      url,
      method: options.method ?? 'GET',
      error,
    })
    throw error
  }

  if (!response.ok) {
    if (response.status === 401 && !skipAuthRedirect && !AUTH_WHITELIST.has(normalizedUrl)) {
      const refreshed = await tryRefreshToken()
      if (refreshed) {
        const retryResult = await rawFetch<T>(url, options)
        if (retryResult.response.ok) {
          notifyResponse(options, retryResult.response)
          return retryResult.payload
        }
      }
      handleUnauthorized(url)
    }

    const message =
      typeof payload === 'object' &&
      payload !== null &&
      'message' in payload &&
      typeof payload.message === 'string'
        ? payload.message
        : `请求失败 (${response.status})`
    if (!isCenterLogIngestRequest(url)) {
      // 业务接口返回非 2xx 时补一条结构化日志，方便后台按项目/等级/时间检索。
      reportRequestError({
        url,
        method: options.method ?? 'GET',
        statusCode: response.status,
        error: new RequestError(message, response.status, payload),
        responsePayload: payload,
        traceId: response.headers.get('x-trace-id') ?? response.headers.get('trace-id') ?? undefined,
      })
    }
    throw new RequestError(message, response.status, payload)
  }

  notifyResponse(options, response)
  return payload as T
}

export function unwrapApiData<T>(payload: ApiResponse<T> | T): T {
  if (payload && typeof payload === 'object') {
    const response = payload as ApiResponse<T>
    const success = response.success ?? response.isSuccess

    // 后端业务失败可能仍返回 HTTP 200，必须转成异常，避免页面误报“保存成功”。
    if (success === false) {
      const code = response.code ?? response.errorCode
      const message = response.message || '请求失败'
      throw new RequestError(code ? `${code}: ${message}` : message, 200, payload)
    }

    if ('data' in payload) {
      return response.data as T
    }
  }

  return payload as T
}

export function unwrapPagedResult<T>(payload: ApiResponse<PagedResult<T>> | PagedResult<T>) {
  const result = unwrapApiData(payload)
  return {
    items: result.items ?? [],
    total: result.total ?? result.totalCount ?? 0,
    page: result.page ?? result.pageIndex ?? 1,
    pageSize: result.pageSize ?? 10,
    totalPages: result.totalPages,
  }
}

request.get = <T>(url: string, options?: Omit<RequestOptions, 'method'>) =>
  request<T>(url, { ...options, method: 'GET' })
request.post = <T>(url: string, data?: unknown, options?: Omit<RequestOptions, 'method' | 'data'>) =>
  request<T>(url, { ...options, method: 'POST', data })
request.put = <T>(url: string, data?: unknown, options?: Omit<RequestOptions, 'method' | 'data'>) =>
  request<T>(url, { ...options, method: 'PUT', data })
request.patch = <T>(url: string, data?: unknown, options?: Omit<RequestOptions, 'method' | 'data'>) =>
  request<T>(url, { ...options, method: 'PATCH', data })
request.delete = <T>(url: string, options?: Omit<RequestOptions, 'method'>) =>
  request<T>(url, { ...options, method: 'DELETE' })

export default request as typeof request & {
  get: typeof request.get
  post: typeof request.post
  put: typeof request.put
  patch: typeof request.patch
  delete: typeof request.delete
}
