import type { Plugin } from "@opencode-ai/plugin"
import { Agent as HttpAgent, request as httpRequest } from "node:http"
import { existsSync, readFileSync } from "node:fs"
import { appendFile, mkdir } from "node:fs/promises"
import { connect as netConnect, type Socket } from "node:net"
import { homedir } from "node:os"
import { join } from "node:path"
import { Readable } from "node:stream"
import { connect as tlsConnect } from "node:tls"

const DEFAULT_PROXY_URL = "http://127.0.0.1:8181"
const CONFIG_DIR = process.env.OPENCODE_CONFIG_DIR?.trim() || join(homedir(), ".config", "opencode")
const CONFIG_FILE = process.env.TOKEN_MONITOR_PROXY_CONFIG?.trim() || join(CONFIG_DIR, "token-monitor-proxy.json")
const LOG_DIR = join(homedir(), ".local", "share", "opencode")
const LOG_FILE = join(LOG_DIR, "token-proxy.log")

type ProxyConfig = {
  enabled?: boolean
  proxyUrl?: string
  providers?: string[]
  disableDirectFallback?: boolean
}

type FetchLike = (input: RequestInfo | URL, init?: RequestInit) => Promise<Response>

const normalizeProxyUrl = (value: unknown) => {
  const trimmed = typeof value === "string" ? value.trim() : ""
  if (!trimmed) return DEFAULT_PROXY_URL
  return /^[a-z][a-z0-9+.-]*:\/\//i.test(trimmed) ? trimmed : `http://${trimmed}`
}

const readConfig = () => {
  if (!existsSync(CONFIG_FILE)) {
    return { config: {} as ProxyConfig, source: "default", error: null as string | null }
  }

  try {
    const raw = readFileSync(CONFIG_FILE, "utf8").replace(/^\uFEFF/, "")
    const parsed = JSON.parse(raw)
    if (!parsed || typeof parsed !== "object" || Array.isArray(parsed)) {
      throw new Error("config must be a JSON object")
    }
    return { config: parsed as ProxyConfig, source: "file", error: null as string | null }
  } catch (error) {
    return {
      config: {} as ProxyConfig,
      source: "default",
      error: error instanceof Error ? error.message : String(error),
    }
  }
}

const appendLine = async (file: string, line: string) => {
  try {
    await mkdir(LOG_DIR, { recursive: true })
    await appendFile(file, line, "utf8")
  } catch {}
}

const log = (line: string) => appendLine(LOG_FILE, `${new Date().toISOString()} ${line}\n`)

const waitForConnect = (socket: Socket, event: "connect" | "secureConnect") =>
  new Promise<void>((resolve, reject) => {
    const onReady = () => {
      socket.off("error", onError)
      resolve()
    }
    const onError = (error: Error) => {
      socket.off(event === "connect" ? "connect" : "secureConnect", onReady)
      reject(error)
    }
    socket.once(event, onReady)
    socket.once("error", onError)
  })

const readConnectResponse = (socket: Socket) =>
  new Promise<void>((resolve, reject) => {
    let buffer = Buffer.alloc(0)
    const cleanup = () => {
      socket.off("data", onData)
      socket.off("error", onError)
      socket.off("close", onClose)
      socket.off("end", onClose)
    }
    const onData = (chunk: Buffer) => {
      buffer = Buffer.concat([buffer, chunk])
      const end = buffer.indexOf("\r\n\r\n")
      if (end < 0) {
        if (buffer.length > 64 * 1024) {
          cleanup()
          reject(new Error("proxy CONNECT response is too large"))
        }
        return
      }
      const head = buffer.subarray(0, end).toString("latin1")
      const status = /^HTTP\/\d(?:\.\d)?\s+(\d{3})\b/i.exec(head)
      cleanup()
      if (!status || Number(status[1]) !== 200) {
        reject(new Error(`proxy CONNECT failed: ${head.split("\r\n")[0] ?? "empty response"}`))
        return
      }
      resolve()
    }
    const onError = (error: Error) => {
      cleanup()
      reject(error)
    }
    const onClose = () => {
      cleanup()
      reject(new Error("proxy closed the CONNECT tunnel"))
    }
    socket.on("data", onData)
    socket.once("error", onError)
    socket.once("close", onClose)
    socket.once("end", onClose)
  })

const openTunnel = async (proxy: URL, target: URL) => {
  const proxyPort = Number(proxy.port || (proxy.protocol === "https:" ? 443 : 80))
  const targetPort = Number(target.port || (target.protocol === "https:" ? 443 : 80))
  const raw = netConnect({ host: proxy.hostname, port: proxyPort })
  await waitForConnect(raw, "connect")

  let tunnel: Socket = raw
  if (proxy.protocol === "https:") {
    const secureProxy = tlsConnect({ socket: raw, servername: proxy.hostname })
    await waitForConnect(secureProxy, "secureConnect")
    tunnel = secureProxy
  }

  const authorization =
    proxy.username || proxy.password
      ? `Proxy-Authorization: Basic ${Buffer.from(
          `${decodeURIComponent(proxy.username)}:${decodeURIComponent(proxy.password)}`,
        ).toString("base64")}\r\n`
      : ""
  tunnel.write(
    `CONNECT ${target.hostname}:${targetPort} HTTP/1.1\r\nHost: ${target.hostname}:${targetPort}\r\n${authorization}\r\n`,
  )
  await readConnectResponse(tunnel)
  return { tunnel, targetPort }
}

const createProxyFetch = (proxyUrl: string, disableDirectFallback: boolean): FetchLike => {
  const proxy = new URL(proxyUrl)
  const direct = async (
    input: RequestInfo | URL,
    init: RequestInit | undefined,
    reason: string,
  ) => {
    if (disableDirectFallback) {
      void log(`proxy direct fallback disabled reason=${reason}`)
      throw new Error(`proxy direct fallback disabled: ${reason}`)
    }
    return fetch(input, init)
  }

  return async (input, init = {}) => {
    const target = new URL(
      typeof Request !== "undefined" && input instanceof Request ? input.url : String(input),
    )
    if (target.protocol !== "http:" && target.protocol !== "https:") {
      return direct(input, init, "unsupported-protocol")
    }

    let tunnel: Socket
    let targetPort: number
    try {
      ;({ tunnel, targetPort } = await openTunnel(proxy, target))
    } catch (error) {
      void log(
        `proxy fetch tunnel failed target=${target.host} error=${error instanceof Error ? error.message : String(error)}`,
      )
      return direct(input, init, "tunnel-failed")
    }

    let socket = tunnel
    if (target.protocol === "https:") {
      const secure = tlsConnect({
        socket: tunnel,
        servername: target.hostname,
        ALPNProtocols: ["http/1.1"],
      })
      try {
        await waitForConnect(secure, "secureConnect")
      } catch (error) {
        tunnel.destroy()
        void log(
          `proxy fetch tls failed target=${target.host} error=${error instanceof Error ? error.message : String(error)}`,
        )
        return direct(input, init, "tls-failed")
      }
      socket = secure
    }

    const headers = new Headers(init.headers as HeadersInit | undefined)
    headers.set("host", target.host)
    headers.set("accept-encoding", "identity")
    headers.set("connection", "close")

    return new Promise<Response>((resolve, reject) => {
      let settled = false
      let request: ReturnType<typeof httpRequest> | undefined
      let response: import("node:http").IncomingMessage | undefined
      const fail = (error: Error) => {
        if (settled) return
        settled = true
        request?.destroy()
        response?.destroy()
        socket.destroy()
        reject(error)
      }
      const onAbort = () => {
        request?.destroy(new Error("request aborted"))
        response?.destroy(new Error("request aborted"))
        socket.destroy()
        fail(new Error("request aborted"))
      }

      try {
        const agent = new HttpAgent({ keepAlive: false, maxSockets: 1 })
        agent.createConnection = () => socket
        request = httpRequest(
          {
            method: init.method ?? "GET",
            host: target.hostname,
            port: targetPort,
            path: `${target.pathname}${target.search}`,
            headers: Object.fromEntries(headers.entries()),
            agent,
          } as import("node:http").RequestOptions,
          (incoming) => {
            response = incoming
            const responseHeaders = new Headers()
            for (const [key, value] of Object.entries(incoming.headers)) {
              if (value === undefined) continue
              if (Array.isArray(value)) {
                for (const item of value) responseHeaders.append(key, item)
              } else {
                responseHeaders.set(key, String(value))
              }
            }
            const noBody = incoming.statusCode === 204 || incoming.statusCode === 304 || init.method === "HEAD"
            const body = noBody ? null : (Readable.toWeb(incoming) as ReadableStream<Uint8Array>)
            settled = true
            resolve(
              new Response(body, {
                status: incoming.statusCode ?? 200,
                statusText: incoming.statusMessage ?? "",
                headers: responseHeaders,
              }),
            )
          },
        )
      } catch (error) {
        fail(error instanceof Error ? error : new Error(String(error)))
        return
      }

      request.once("error", (error) => fail(error))
      if (init.signal) {
        if (init.signal.aborted) {
          onAbort()
          return
        }
        init.signal.addEventListener("abort", onAbort, { once: true })
      }

      const body = init.body
      try {
        if (body === undefined || body === null) {
          request.end()
        } else if (typeof body === "string" || Buffer.isBuffer(body) || body instanceof Uint8Array) {
          request.end(body)
        } else if (typeof (body as ReadableStream).getReader === "function") {
          Readable.fromWeb(body as ReadableStream<Uint8Array>).pipe(request)
        } else if (typeof (body as AsyncIterable<Uint8Array>)[Symbol.asyncIterator] === "function") {
          Readable.from(body as AsyncIterable<Uint8Array>).pipe(request)
        } else {
          request.end(String(body))
        }
      } catch (error) {
        fail(error instanceof Error ? error : new Error(String(error)))
      }
    })
  }
}

const mergeNoProxy = (value: string | undefined) => {
  const items = (value ?? "")
    .split(/[,\s]+/)
    .map((item) => item.trim())
    .filter(Boolean)
  if (items.includes("*")) return value ?? "*"
  for (const host of ["localhost", "127.0.0.1", "::1"]) {
    if (!items.some((item) => item.toLowerCase() === host)) items.push(host)
  }
  return items.join(",")
}

const { config, source: configSource, error: configError } = readConfig()
const envProxyUrl = process.env.TOKEN_MONITOR_PROXY_URL?.trim() || undefined
const fileProxyUrl = typeof config.proxyUrl === "string" ? config.proxyUrl.trim() : undefined
const proxyUrl = normalizeProxyUrl(envProxyUrl ?? fileProxyUrl)
const urlSource = envProxyUrl ? "env" : fileProxyUrl ? "file" : "default"
const disabledByEnv = process.env.TOKEN_MONITOR_PROXY_DISABLED === "1"
const disabled = disabledByEnv || config.enabled === false || configError !== null
const disableDirectFallback = config.disableDirectFallback === true
const proxyProviders = (() => {
  const values = process.env.TOKEN_MONITOR_PROXY_PROVIDERS?.split(",") ?? config.providers ?? []
  return [...new Set(values.map((item) => item.trim()).filter(Boolean))]
})()

const safeProxy = (() => {
  try {
    const url = new URL(proxyUrl)
    return `${url.protocol}//${url.host}`
  } catch {
    return proxyUrl
  }
})()

if (disabled) {
  const reason = disabledByEnv ? "env" : configError ? "config-error" : "config"
  const problem = configError ? ` config_error=${configError}` : ""
  void log(`plugin loaded (pid=${process.pid}) disabled by ${reason} config=${CONFIG_FILE}${problem}`)
} else {
  process.env.HTTP_PROXY = proxyUrl
  process.env.HTTPS_PROXY = proxyUrl
  process.env.NO_PROXY = mergeNoProxy(process.env.NO_PROXY ?? process.env.no_proxy)
  const details = configSource === "file" ? ` config=${CONFIG_FILE}` : ""
  const problem = configError ? ` config_error=${configError}` : ""
  void log(
    `plugin loaded (pid=${process.pid}) enabled proxy=${safeProxy} source=${urlSource} no_proxy=${process.env.NO_PROXY} direct_fallback=${disableDirectFallback ? "disabled" : "enabled"}${details}${problem}`,
  )
}

export const TokenMonitorProxy: Plugin = async () => {
  if (disabled) return {}
  process.env.HTTP_PROXY = proxyUrl
  process.env.HTTPS_PROXY = proxyUrl
  process.env.NO_PROXY = mergeNoProxy(process.env.NO_PROXY ?? process.env.no_proxy)
  const proxyFetch = createProxyFetch(proxyUrl, disableDirectFallback)
  return {
    config: (cfg) => {
      const providers = (cfg as { provider?: Record<string, { options?: Record<string, unknown> }> }).provider
      if (!providers) return
      const injected: string[] = []
      const ids = proxyProviders.length > 0 ? proxyProviders : Object.keys(providers)
      for (const id of ids) {
        const provider = providers[id]
        if (!provider) continue
        provider.options = { ...(provider.options ?? {}), fetch: proxyFetch }
        injected.push(id)
      }
      if (injected.length > 0) {
        void log(`proxy fetch injected providers=${injected.join(",")} proxy=${safeProxy}`)
      }
    },
  }
}
