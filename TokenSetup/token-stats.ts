import type { Plugin } from "@opencode-ai/plugin"
import type { AssistantMessage } from "@opencode-ai/sdk"
import { appendFile, mkdir } from "node:fs/promises"
import { homedir } from "node:os"
import { join } from "node:path"

const TOAST_DURATION_MS = 8000
const LOG_DIR = join(homedir(), ".local", "share", "opencode")
const LOG_FILE = join(LOG_DIR, "token-stats.log")
const EVENTS_FILE = join(LOG_DIR, "token-events.jsonl")

const appendLine = async (file: string, line: string) => {
  try {
    await mkdir(LOG_DIR, { recursive: true })
    await appendFile(file, line, "utf8")
  } catch {}
}

const log = (line: string) => appendLine(LOG_FILE, `${new Date().toISOString()} ${line}\n`)

const formatTokens = (value: number) => {
  if (value < 1000) return String(value)
  if (value < 10_000) return `${(value / 1000).toFixed(1)}k`
  if (value < 1_000_000) return `${Math.round(value / 1000)}k`
  return `${(value / 1_000_000).toFixed(2)}m`
}

const formatDuration = (ms: number) => {
  const seconds = Math.max(0, ms) / 1000
  if (seconds < 60) return `${seconds.toFixed(1)}s`
  const minutes = Math.floor(seconds / 60)
  const rest = Math.round(seconds - minutes * 60)
  return `${minutes}m${String(rest).padStart(2, "0")}s`
}

const isStep = (info: AssistantMessage | { role: string }, parentID: string): info is AssistantMessage =>
  info.role === "assistant" &&
  (info as AssistantMessage).parentID === parentID &&
  !(info as AssistantMessage).summary &&
  typeof (info as AssistantMessage).tokens === "object"

export const TokenStats: Plugin = async ({ client, directory }) => {
  const reported = new Map<string, string>()

  await log(`plugin loaded (pid=${process.pid})`)

  return {
    event: async ({ event }) => {
      if (event.type !== "session.idle") return

      const sessionID = event.properties.sessionID

      try {
        let isChild = false
        try {
          const session = await client.session.get({ path: { id: sessionID }, query: { directory } })
          isChild = Boolean(session.data?.parentID)
        } catch (error) {
          await log(`session.get failed for ${sessionID}: ${String(error)}`)
        }
        if (isChild) return

        const response = await client.session.messages({ path: { id: sessionID }, query: { directory } })
        const messages = response.data ?? []

        const lastUser = [...messages].reverse().find((item) => item.info.role === "user")
        if (!lastUser) {
          await log(`no user message in ${sessionID}`)
          return
        }

        const steps = messages.filter((item) => isStep(item.info, lastUser.info.id)).map((item) => item.info as AssistantMessage)
        if (steps.length === 0) {
          await log(`no assistant steps in ${sessionID}`)
          return
        }

        const lastStep = steps[steps.length - 1]
        if (!lastStep.time.completed) return

        const marker = `${lastStep.id}:${lastStep.time.completed}`
        if (reported.get(sessionID) === marker) return
        reported.set(sessionID, marker)

        const total = steps.reduce(
          (acc, step) => {
            acc.input += step.tokens.input
            acc.output += step.tokens.output
            acc.reasoning += step.tokens.reasoning
            acc.cacheRead += step.tokens.cache.read
            acc.cacheWrite += step.tokens.cache.write
            return acc
          },
          { input: 0, output: 0, reasoning: 0, cacheRead: 0, cacheWrite: 0 },
        )

        const duration = lastStep.time.completed - lastUser.info.time.created
        const billed = total.input + total.cacheRead + total.cacheWrite + total.output + total.reasoning
        const context = lastStep.tokens.input + lastStep.tokens.cache.read

        const parts = [
          `split ${formatTokens(total.input)} + ${formatTokens(total.output)}`,
          `ctx ${formatTokens(context)}`,
          `steps ${steps.length}`,
          total.cacheRead > 0 ? `cacheRead ${formatTokens(total.cacheRead)}` : undefined,
          total.cacheWrite > 0 ? `cacheWrite ${formatTokens(total.cacheWrite)}` : undefined,
          total.reasoning > 0 ? `reasoning ${formatTokens(total.reasoning)}` : undefined,
        ].filter((part): part is string => Boolean(part))

        const title = `${formatTokens(billed)} tokens in ${formatDuration(duration)}`

        await appendLine(
          EVENTS_FILE,
          `${JSON.stringify({
            time: new Date().toISOString(),
            sessionId: sessionID,
            messageId: lastStep.id,
            model: lastStep.modelID,
            provider: lastStep.providerID,
            steps: steps.length,
            input: total.input,
            output: total.output,
            reasoning: total.reasoning,
            cacheRead: total.cacheRead,
            cacheWrite: total.cacheWrite,
            billed,
            context,
            durationMs: duration,
          })}\n`,
        )

        await log(`report ${sessionID} ${title} | ${parts.join(" · ")}`)

        await client.tui.showToast({
          body: { title, message: parts.join(" · "), variant: "info", duration: TOAST_DURATION_MS },
        })
      } catch (error) {
        await log(`error in ${sessionID}: ${String(error)}`)
      }
    },
  }
}
