/**
 * Юнит/интеграционные тесты без DSH: node --test DeepSeekSetup/plugin/test
 * «Золотые» события повторяют формы из реального журнала 0.2.2-rc.3.
 */
import assert from 'node:assert/strict';
import { test } from 'node:test';
import { TokenStore } from '../src/db.js';
import { createState, foldEvent, sessionRecord } from '../src/fold.js';

const HEADER = {
  version: 4,
  id: 'session-test',
  createdAt: 1794896063039,
  cwd: 'D:\\Work\\Demo',
  isSeeded: false,
  delegationDepth: 0,
  agentPreset: 'standard',
};

const T0 = 1794896282000;

function ev(type, seq, time, data, extra = {}) {
  return { type, seq, time, data, ...extra };
}

const EVENTS = [
  ev('turn/start', 0, T0, { turn: 1 }),
  ev('step/start', 1, T0 + 10, { turn: 1, step: 1 }),
  ev('request/header', 2, T0 + 20, {
    header: { config: { provider: 'deepseek-account', model: 'deepseek-flash', reasoningEffort: 'high' } },
  }),
  ev('request/context', 3, T0 + 21, { provider: 'deepseek-account', model: 'deepseek-flash', contextWindow: 1048576 }),
  ev('user/message', 4, T0 + 30, {
    content: [{ type: 'text', text: 'посчитай токены этой сессии' }],
    source: { kind: 'user' },
  }),
  ev('assistant/message', 5, T0 + 5000, {
    turn: 1,
    step: 1,
    message: {
      role: 'assistant',
      id: 'msg-1',
      content: [
        { type: 'reasoning', text: 'думаю…' },
        { type: 'text', text: 'готово' },
        { type: 'tool-call', id: 'call_1', name: 'read', arguments: '{"path":"a.txt"}' },
      ],
      source: { kind: 'model', provider: 'deepseek-account', model: 'deepseek-flash' },
    },
    usage: {
      inputTokens: 453,
      outputTokens: 637,
      cacheReadTokens: 96256,
      cacheWriteTokens: 0,
      totalTokens: 97346,
    },
    stream: [{ type: 'chunk', time: T0 + 1000, chunk: {} }],
  }),
  ev('tool/call', 6, T0 + 5010, { turn: 1, step: 1, callId: 'call_1', name: 'read', arguments: '{"path":"a.txt"}' }),
  ev('tool/result', 7, T0 + 5030, {
    turn: 1,
    step: 1,
    message: {
      role: 'tool',
      toolCallId: 'call_1',
      isError: false,
      content: [{ type: 'text', text: 'file body' }],
    },
  }),
  ev('step/end', 8, T0 + 6000, { turn: 1, step: 1 }),
  ev('assistant/attempt', 9, T0 + 7000, {
    turn: 1,
    step: 1,
    stream: [{ type: 'chunk', time: T0 + 6900 }],
  }),
  ev('llm/retry', 10, T0 + 7100, {
    turn: 1,
    step: 1,
    retryId: 'r1',
    provider: 'deepseek-account',
    mode: 'normal',
    policyKey: 'p',
    retry: 1,
    delayMs: 1500,
    failure: { message: 'rate limited', code: 'RATE_LIMIT' },
  }),
  ev('llm/retry-started', 11, T0 + 8600, { turn: 1, step: 1, retryId: 'r1', retry: 1 }),
  ev('turn/end', 12, T0 + 9000, { turn: 1, reason: { kind: 'completed' } }),
  ev('session/title', 13, T0 + 9100, { title: 'токены сессии', messageSeqs: [4], source: { kind: 'fallback' } }),
];

function ingest(store, events, info = {}) {
  const state = createState();
  const records = [sessionRecord(HEADER, { now: T0, source: 'live' })];
  for (const event of events) {
    records.push(...foldEvent(state, event, {
      sessionId: HEADER.id,
      now: T0 + 99999,
      capture: 'full',
      captureText: 'preview',
      previewChars: 200,
      source: 'live',
      ...info,
    }));
  }
  store.writeRecords(records);
}

function newStore() {
  return new TokenStore({ dbPath: ':memory:', logger: { warn() {}, info() {} } }).open();
}

test('step usage и агрегаты попадают в представления', () => {
  const store = newStore();
  try {
    ingest(store, EVENTS);

    const session = store.prepare('SELECT * FROM v_session_summary WHERE session_id = ?').get(HEADER.id);
    assert.equal(session.turns, 1);
    assert.equal(session.steps, 1);
    assert.equal(session.total_tokens, 97346);
    assert.equal(session.input_tokens, 453);
    assert.equal(session.cache_read_tokens, 96256);
    assert.equal(session.max_context_tokens, 96709);
    assert.equal(session.tool_calls, 1);
    assert.equal(session.title, 'токены сессии');
    assert.equal(session.model, 'deepseek-flash');

    const step = store.prepare('SELECT * FROM v_step_feed WHERE session_id = ?').get(HEADER.id);
    assert.equal(step.total_tokens, 97346);
    assert.equal(step.prompt_tokens_total, 96709);
    assert.equal(step.context_used_tokens, 96709);
    assert.equal(step.context_window, 1048576);
    assert.equal(step.tools_count, 1);
    assert.equal(step.retries, 1);
    assert.equal(step.attempts, 1);
    const stepRow = store.prepare('SELECT * FROM steps WHERE session_id = ?').get(HEADER.id);
    assert.equal(stepRow.ttft_ms, 990);
    assert.equal(step.duration_ms, 5990);
    assert.equal(step.usage_source, 'provider');

    const turn = store.prepare('SELECT * FROM v_turn_summary WHERE session_id = ?').get(HEADER.id);
    assert.equal(turn.steps_count, 1);
    assert.equal(turn.end_kind, 'completed');
    assert.equal(turn.prompt_preview, 'посчитай токены этой сессии');

    const tool = store.prepare('SELECT * FROM v_tool_timing WHERE name = ?').get('read');
    assert.equal(tool.calls, 1);
    assert.equal(tool.errors, 0);
    assert.equal(tool.avg_duration_ms, 20);

    const retries = store.prepare('SELECT COUNT(*) AS n FROM retries WHERE session_id = ?').get(HEADER.id);
    assert.equal(retries.n, 2);

    const cache = store.prepare('SELECT * FROM v_cache_efficiency WHERE session_id = ?').get(HEADER.id);
    assert.ok(Math.abs(cache.cache_hit_percent - (96256 * 100) / 96709) < 0.001);
  } finally {
    store.close();
  }
});

test('повторная подача того же батча идемпотентна', () => {
  const store = newStore();
  try {
    ingest(store, EVENTS);
    const before = store.prepare('SELECT SUM(total_tokens) AS sum, COUNT(*) AS n FROM steps').get();
    ingest(store, EVENTS);
    const after = store.prepare('SELECT SUM(total_tokens) AS sum, COUNT(*) AS n FROM steps').get();
    assert.deepEqual(after, before);
    const tools = store.prepare('SELECT COUNT(*) AS n FROM tool_calls').get();
    assert.equal(tools.n, 1);
    const usage = store.prepare('SELECT COUNT(*) AS n FROM usage_samples').get();
    assert.equal(usage.n, 1);
  } finally {
    store.close();
  }
});

test('capture=usage не пишет инструменты и контекст', async () => {
  const store = newStore();
  try {
    const state = createState();
    const records = [sessionRecord(HEADER, { now: T0, source: 'live' })];
    for (const event of EVENTS) {
      const folded = foldEvent(state, event, {
        sessionId: HEADER.id,
        now: T0,
        capture: 'usage',
        captureText: 'off',
        previewChars: 200,
        source: 'live',
      });
      records.push(...folded);
    }
    store.writeRecords(records);
    const tools = store.prepare('SELECT COUNT(*) AS n FROM tool_calls').get();
    assert.equal(tools.n, 1); // fold ещё пишет, фильтрация — в index.js

    const { filterRecords } = await import('../src/fold.js');
    const filtered = filterRecords(records, 'usage');
    assert.ok(filtered.every((record) => record.kind !== 'tool' && record.kind !== 'context'));
  } finally {
    store.close();
  }
});

test('raw_events включаются только при capture=raw', () => {
  const store = newStore();
  try {
    const state = createState();
    const records = [sessionRecord(HEADER, { now: T0, source: 'live' })];
    for (const event of EVENTS.slice(0, 3)) {
      records.push(...foldEvent(state, event, {
        sessionId: HEADER.id,
        now: T0,
        capture: 'full',
        captureText: 'preview',
        previewChars: 200,
        source: 'live',
      }));
    }
    store.writeRecords(records);
    const raw = store.prepare('SELECT COUNT(*) AS n FROM raw_events').get();
    assert.equal(raw.n, 0);
  } finally {
    store.close();
  }
});
