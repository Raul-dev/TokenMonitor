/**
 * Чистый fold: событие журнала DSH → строки таблиц токен-логгера.
 *
 * Модуль не знает ни про SQLite, ни про DSH: на вход идут только
 * `{ type, seq, time, data }` и состояние сессии, на выход — «записи»
 * (records), которые применяет db.js. Поэтому fold тестируется node:test
 * на «золотых» событиях без запуска DSH.
 */

import { textOfContent, textPreview } from './redact.js';

/**
 * Изменяемое состояние одной сессии между событиями.
 * Нужно потому, что часть событий не несёт turn/step (user/message, title,
 * request/header), а часть — только ссылку на вызов инструмента.
 */
export function createState() {
  return {
    turn: null,
    step: null,
    provider: null,
    model: null,
    reasoningEffort: null,
    headerSeq: null,
    contextSeq: null,
    contextWindow: null,
    toolCalls: new Map(),
    stepStart: new Map(),
  };
}

/**
 * Запись сессии из заголовка: `session/created` (live) и backfill.
 * @param {object} header SessionHeader DSH
 * @param {object} info { now, source }
 */
export function sessionRecord(header, { now, source = 'live' } = {}) {
  const values = {
    session_id: header.id,
    parent_session_id: header.parentSession ?? null,
    origin: header.origin ?? null,
    delegation_depth: header.delegationDepth ?? null,
    agent_preset: header.agentPreset ?? null,
    cwd: header.cwd ?? null,
    created_ms: header.createdAt ?? now,
    updated_ms: header.createdAt ?? now,
    first_seen_ms: now,
    last_ingest_ms: now,
    is_seeded: header.isSeeded ? 1 : 0,
    format_version: header.version ?? null,
    source,
  };
  return { kind: 'session', values };
}

const EMPTY_INFO = { now: Date.now(), capture: 'full', captureText: 'preview', previewChars: 200, source: 'live' };

/**
 * Отфильтровать записи под режим capture (PLAN §5.2).
 * `usage` пишет только токены и границы, `full` — всё производное,
 * `raw` дополнительно получает raw_events (их добавляет foldEvent).
 * @param {object[]} records
 * @param {'usage'|'full'|'raw'} capture
 */
export function filterRecords(records, capture) {
  if (capture !== 'usage') return records;
  const keep = new Set(['session', 'cursor', 'turn', 'step', 'usage']);
  return records.filter((record) => keep.has(record.kind));
}

/**
 * Свернуть одно событие в записи таблиц.
 * @param {object} state состояние сессии (createState)
 * @param {object} event SessionEvent DSH
 * @param {object} info { sessionId, now, capture, captureText, previewChars, source }
 * @returns {object[]} записи для db.applyRecords
 */
export function foldEvent(state, event, info = {}) {
  const opts = { ...EMPTY_INFO, ...info };
  const { sessionId } = opts;
  if (!sessionId || !event || typeof event.type !== 'string') return [];

  const records = [];
  const data = event.data ?? {};
  const seq = event.seq;
  const time = event.time ?? opts.now;
  const push = (record) => records.push(record);

  // Заголовок журнала (`type: 'session'`) и любые записи без seq не индексируются.
  if (!Number.isInteger(seq)) return records;

  // Курсор и «свежесть» сессии обновляются на каждом событии.
  push({
    kind: 'cursor',
    values: {
      session_id: sessionId,
      last_seq: seq,
      last_time_ms: time,
      updated_ms: opts.now,
      reason: opts.source,
    },
  });
  push({
    kind: 'session',
    values: {
      session_id: sessionId,
      updated_ms: time,
      last_ingest_ms: opts.now,
      last_seq: seq,
      event_count: seq + 1,
    },
  });

  if (opts.capture === 'raw' && Number.isInteger(seq)) {
    const json = safeJson(data);
    push({
      kind: 'raw',
      values: {
        session_id: sessionId,
        seq,
        time_ms: time,
        type: event.type,
        surface_op: event.surfaceOp ?? null,
        ignorable: event.ignorable ? 1 : 0,
        bytes: json.length,
        data_json: json,
        ingested_ms: opts.now,
      },
    });
  }

  switch (event.type) {
    case 'session/title': {
      if (typeof data.title === 'string' && data.title.length > 0) {
        push({ kind: 'session', values: { session_id: sessionId, title: data.title } });
      }
      break;
    }

    case 'request/header': {
      const config = data.header?.config ?? {};
      state.provider = config.provider ?? state.provider;
      state.model = config.model ?? state.model;
      state.reasoningEffort = config.reasoningEffort ?? state.reasoningEffort;
      state.headerSeq = seq;
      push({
        kind: 'session',
        values: {
          session_id: sessionId,
          provider: state.provider,
          model: state.model,
          reasoning_effort: state.reasoningEffort,
        },
      });
      if (state.turn != null && state.step != null) {
        push({
          kind: 'step',
          values: {
            session_id: sessionId,
            turn: state.turn,
            step: state.step,
            request_header_seq: seq,
            provider: state.provider,
            model: state.model,
            reasoning_effort: state.reasoningEffort,
            updated_ms: opts.now,
          },
        });
      }
      break;
    }

    case 'request/context': {
      state.provider = data.provider ?? state.provider;
      state.model = data.model ?? state.model;
      state.contextWindow = data.contextWindow ?? state.contextWindow;
      state.contextSeq = seq;
      push({
        kind: 'session',
        values: {
          session_id: sessionId,
          provider: state.provider,
          model: state.model,
        },
      });
      push({
        kind: 'context',
        values: {
          session_id: sessionId,
          seq,
          time_ms: time,
          turn: state.turn,
          step: state.step,
          pressure_tokens: null,
          projected_tokens: null,
          surface_tokens: null,
          context_window: state.contextWindow,
          system_tokens: null,
          tools_tokens: null,
          message_tokens: null,
        },
      });
      if (state.turn != null && state.step != null) {
        push({
          kind: 'step',
          values: {
            session_id: sessionId,
            turn: state.turn,
            step: state.step,
            request_context_seq: seq,
            context_window: state.contextWindow,
            updated_ms: opts.now,
          },
        });
      }
      break;
    }

    case 'turn/start': {
      state.turn = data.turn ?? state.turn;
      state.step = null;
      if (typeof state.turn !== 'number') break;
      push({
        kind: 'turn',
        values: { session_id: sessionId, turn: state.turn, started_ms: time },
      });
      break;
    }

    case 'turn/end': {
      const turn = data.turn ?? state.turn;
      if (typeof turn !== 'number') break;
      const reason = data.reason;
      const kind = typeof reason === 'string' ? reason : (reason?.kind ?? null);
      const error = typeof reason === 'object' ? reason?.error : null;
      const errorText = typeof error?.message === 'string' ? error.message.slice(0, 500) : null;
      push({
        kind: 'turn',
        values: {
          session_id: sessionId,
          turn,
          ended_ms: time,
          end_kind: kind,
          end_error_code: error?.code ?? null,
          end_error_text: errorText,
        },
      });
      break;
    }

    case 'user/message': {
      if (typeof state.turn !== 'number') break;
      const text = textOfContent(data.content);
      const { preview, chars } = textPreview(text, opts.captureText, opts.previewChars);
      push({
        kind: 'turn',
        values: {
          session_id: sessionId,
          turn: state.turn,
          prompt_preview: preview,
          prompt_chars: chars,
        },
      });
      break;
    }

    case 'step/start': {
      if (typeof data.turn !== 'number' || typeof data.step !== 'number') break;
      state.turn = data.turn;
      state.step = data.step;
      state.stepStart.set(`${data.turn}:${data.step}`, time);
      push({
        kind: 'step',
        values: {
          session_id: sessionId,
          turn: data.turn,
          step: data.step,
          started_ms: time,
          provider: state.provider,
          model: state.model,
          reasoning_effort: state.reasoningEffort,
          request_header_seq: state.headerSeq,
          request_context_seq: state.contextSeq,
          context_window: state.contextWindow,
          updated_ms: opts.now,
        },
      });
      break;
    }

    case 'step/end': {
      if (typeof data.turn !== 'number' || typeof data.step !== 'number') break;
      const started = state.stepStart.get(`${data.turn}:${data.step}`);
      const duration = typeof started === 'number' ? Math.max(0, time - started) : null;
      push({
        kind: 'step',
        values: {
          session_id: sessionId,
          turn: data.turn,
          step: data.step,
          ended_ms: time,
          duration_ms: duration,
          updated_ms: opts.now,
        },
      });
      break;
    }

    case 'assistant/message': {
      const turn = data.turn ?? state.turn;
      const step = data.step ?? state.step;
      if (typeof turn !== 'number' || typeof step !== 'number') break;
      const message = data.message ?? {};
      const usage = data.usage ?? null;
      const source = message.source ?? {};
      state.provider = source.provider ?? state.provider;
      state.model = source.model ?? state.model;

      const text = textOfContent(message.content);
      let reasoningChars = 0;
      if (Array.isArray(message.content)) {
        for (const block of message.content) {
          if (block?.type === 'reasoning' && typeof block.text === 'string') reasoningChars += block.text.length;
        }
      }

      const started = state.stepStart.get(`${turn}:${step}`);
      const stream = Array.isArray(data.stream) ? data.stream : [];
      let ttft = null;
      if (typeof started === 'number') {
        for (const record of stream) {
          const at = typeof record?.time === 'number' ? record.time
            : typeof record?.time0 === 'number' ? record.time0
              : null;
          if (at !== null) {
            if (at >= started) ttft = at - started;
            break;
          }
        }
      }

      const input = num(usage?.inputTokens);
      const cacheRead = num(usage?.cacheReadTokens);
      const output = num(usage?.outputTokens);
      const cacheWrite = num(usage?.cacheWriteTokens);
      const total = typeof usage?.totalTokens === 'number'
        ? usage.totalTokens
        : input + output + cacheRead + cacheWrite;
      const values = {
        session_id: sessionId,
        turn,
        step,
        provider: state.provider,
        model: state.model,
        assistant_seq: seq,
        usage_seq: seq,
        assistant_message_id: message.id ?? null,
        interrupted: data.interrupted ? 1 : 0,
        usage_source: usage ? 'provider' : 'none',
        context_window: state.contextWindow,
        stream_records: stream.length,
        content_chars: text.length,
        reasoning_chars: reasoningChars,
        ttft_ms: ttft,
        updated_ms: opts.now,
      };
      if (usage) {
        Object.assign(values, {
          input_tokens: input,
          prompt_tokens_total: input + cacheRead,
          output_tokens: output,
          reasoning_tokens: num(usage.reasoningTokens),
          cache_read_tokens: cacheRead,
          cache_write_tokens: cacheWrite,
          total_tokens: total,
          context_pressure_tokens: input + cacheRead,
        });
      }
      push({ kind: 'step', values });

      if (usage) {
        push({
          kind: 'usage',
          values: {
            session_id: sessionId,
            seq,
            time_ms: time,
            turn,
            step,
            provider: state.provider,
            model: state.model,
            input_tokens: num(usage.inputTokens),
            output_tokens: num(usage.outputTokens),
            reasoning_tokens: num(usage.reasoningTokens),
            cache_read_tokens: num(usage.cacheReadTokens),
            cache_write_tokens: num(usage.cacheWriteTokens),
            total_tokens: total,
            raw_json: safeJson(usage),
          },
        });
      }

      push({
        kind: 'context',
        values: {
          session_id: sessionId,
          seq,
          time_ms: time,
          turn,
          step,
          pressure_tokens: input + cacheRead,
          projected_tokens: null,
          surface_tokens: null,
          context_window: state.contextWindow,
          system_tokens: null,
          tools_tokens: null,
          message_tokens: null,
        },
      });
      break;
    }

    case 'assistant/attempt': {
      const turn = data.turn ?? state.turn;
      const step = data.step ?? state.step;
      if (typeof turn !== 'number' || typeof step !== 'number') break;
      const stream = Array.isArray(data.stream) ? data.stream : [];
      push({
        kind: 'attempt',
        values: {
          session_id: sessionId,
          seq,
          time_ms: time,
          turn,
          step,
          stream_records: stream.length,
          stream_chars: safeJson(stream).length,
        },
      });
      break;
    }

    case 'llm/retry':
    case 'llm/retry-started': {
      const turn = data.turn ?? state.turn;
      const step = data.step ?? state.step;
      if (typeof turn !== 'number' || typeof step !== 'number') break;
      const failure = data.failure ?? null;
      push({
        kind: 'retry',
        values: {
          session_id: sessionId,
          seq,
          time_ms: time,
          turn,
          step,
          event_kind: event.type === 'llm/retry' ? 'scheduled' : 'started',
          retry_id: data.retryId == null ? null : String(data.retryId),
          provider: data.provider ?? null,
          mode: data.mode ?? null,
          policy_key: data.policyKey ?? null,
          failure_code: failure?.code ?? null,
          failure_text: typeof failure?.message === 'string' ? failure.message.slice(0, 500) : null,
          scheduled_delay_ms: numOrNull(data.delayMs),
        },
      });
      break;
    }

    case 'tool/call': {
      const callId = data.callId;
      if (typeof callId !== 'string') break;
      state.toolCalls.set(callId, time);
      const args = typeof data.arguments === 'string' ? data.arguments : safeJson(data.arguments ?? null);
      const { preview, chars } = textPreview(args, opts.captureText, opts.previewChars);
      push({
        kind: 'tool',
        values: {
          session_id: sessionId,
          call_id: callId,
          turn: data.turn ?? state.turn,
          step: data.step ?? state.step,
          name: typeof data.name === 'string' ? data.name : 'unknown',
          called_ms: time,
          arguments_chars: chars,
          arguments_preview: preview,
        },
      });
      break;
    }

    case 'tool/result': {
      const message = data.message ?? {};
      const callId = message.toolCallId ?? message.source?.callId;
      if (typeof callId !== 'string') break;
      const called = state.toolCalls.get(callId);
      const text = textOfContent(message.content);
      const { preview, chars } = textPreview(text, opts.captureText, opts.previewChars);
      const error = data.error ?? null;
      push({
        kind: 'tool',
        values: {
          session_id: sessionId,
          call_id: callId,
          turn: data.turn ?? state.turn,
          step: data.step ?? state.step,
          finished_ms: time,
          duration_ms: typeof called === 'number' ? Math.max(0, time - called) : null,
          result_chars: chars,
          result_preview: preview,
          is_error: message.isError ? 1 : 0,
          error_name: error?.name ?? null,
          error_code: error?.code ?? null,
          error_reason: error?.message ? String(error.message).slice(0, 500) : null,
          meta_json: data.meta === undefined ? null : safeJson(data.meta),
        },
      });
      break;
    }

    default:
      break;
  }

  return records;
}

function num(value) {
  return typeof value === 'number' && Number.isFinite(value) ? value : 0;
}function numOrNull(value) {
  return typeof value === 'number' && Number.isFinite(value) ? value : null;
}

function safeJson(value) {
  try {
    return JSON.stringify(value ?? null);
  } catch {
    return 'null';
  }
}
