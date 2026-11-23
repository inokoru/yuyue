import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { dirname, join } from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";

import worker, {
  checkAndTrigger,
  openMessage,
  parseNtfyCandidates,
  safeId,
  textHash,
} from "./worker.js";

const __dirname = dirname(fileURLToPath(import.meta.url));
const BASE_ENV = {
  NTFY_TOPIC: "secret-topic",
  DISPATCH_TOKEN: "ghp_test_token",
};

function makeNtfyLine(id, payload, extra = {}) {
  return JSON.stringify({
    id,
    event: "message",
    message: typeof payload === "string" ? payload : JSON.stringify(payload),
    ...extra,
  });
}

function jsonResponse(body, status = 200) {
  return new Response(body === null ? null : JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json" },
  });
}

test("openMessage and safeId mirror relay.py validation", () => {
  assert.equal(safeId("ab c-->x"), "abc--x");
  assert.deepEqual(openMessage('{"v":2,"category":"bug","text":"翻页卡住"}'), {
    v: 2,
    category: "bug",
    text: "翻页卡住",
  });
  for (const junk of [
    "hello",
    "v2shot",
    "v1file",
    "{",
    '{"v":1,"text":"x"}',
    '{"v":2,"text":"   "}',
    JSON.stringify({ v: 2, text: "字".repeat(1001) }),
    "[1]",
  ]) {
    assert.equal(openMessage(junk), null);
  }
});

test("textHash matches Python relay.text_hash byte-for-byte", async () => {
  const sample = "  翻页卡住\n第二行\t  English 123  ";
  const jsHash = await textHash(sample);
  const pyHash = execFileSync(
    "python3",
    [
      "-c",
      "import sys; sys.path.insert(0, sys.argv[1]); import relay; print(relay.text_hash({'text': sys.argv[2]}), end='')",
      join(__dirname, "../relay"),
      sample,
    ],
    { encoding: "utf8" }
  );
  assert.equal(jsHash, pyHash);
});

test("skips GitHub API completely when ntfy only has empty, junk, or attachment lines", async () => {
  const calls = [];
  const ndjson = [
    JSON.stringify({ id: "k1", event: "keepalive" }),
    makeNtfyLine("s1", "v2shot", { attachment: { url: "https://ntfy.sh/file/s1.jpg" } }),
    makeNtfyLine("f1", "v1file", { attachment: { url: "https://ntfy.sh/file/f1.bin" } }),
    makeNtfyLine("j1", "random spam"),
  ].join("\n");

  const res = await checkAndTrigger(BASE_ENV, {
    log: () => {},
    fetch: async (url) => {
      calls.push(url);
      return new Response(ndjson, { status: 200 });
    },
  });

  assert.deepEqual(res, { triggered: false, reason: "empty", pending: 0 });
  assert.equal(calls.length, 1);
  assert.match(calls[0], /^https:\/\/ntfy\.sh\/secret-topic\/json\?poll=1&since=12h$/);
});

test("skips workflow_dispatch when ntfy-id or 24h text-hash already exists in Issues", async () => {
  const nowMs = Date.parse("2026-11-23T12:00:00Z");
  const dupText = "同一条重复反馈";
  const dupHash = await textHash(dupText);

  const ndjson = [
    makeNtfyLine("done1", { v: 2, category: "bug", text: "已经处理过的反馈" }),
    makeNtfyLine("newIdSameText", { v: 2, category: "bug", text: dupText }),
  ].join("\n");

  const calls = [];
  const res = await checkAndTrigger(BASE_ENV, {
    nowMs,
    log: () => {},
    fetch: async (url) => {
      calls.push(url);
      if (url.startsWith("https://ntfy.sh/")) {
        return new Response(ndjson, { status: 200 });
      }
      if (url.includes("/repos/inokoru/yuyue/issues?")) {
        return jsonResponse([
          {
            body: "<!-- ntfy-id: done1 text-hash: 0000000000000000 -->\n正文",
            created_at: "2026-11-23T10:00:00Z",
          },
          {
            body: `<!-- ntfy-id: earlierId text-hash: ${dupHash} -->\n${dupText}`,
            created_at: "2026-11-23T02:00:00Z", // within 24h
          },
        ]);
      }
      throw new Error(`unexpected call: ${url}`);
    },
  });

  assert.deepEqual(res, { triggered: false, reason: "already_handled", pending: 0 });
  assert.equal(calls.length, 2);
});

test("dispatches workflow when a new feedback arrives or previous identical text is older than 24h", async () => {
  const nowMs = Date.parse("2026-11-23T12:00:00Z");
  const repeatedAfter24h = "翻页卡住";
  const oldHash = await textHash(repeatedAfter24h);
  const ndjson = makeNtfyLine("fresh1", { v: 2, category: "bug", text: repeatedAfter24h });

  let dispatchedBody = null;
  const res = await checkAndTrigger(BASE_ENV, {
    nowMs,
    log: () => {},
    fetch: async (url, init = {}) => {
      if (url.startsWith("https://ntfy.sh/")) {
        return new Response(ndjson, { status: 200 });
      }
      if (url.includes("/repos/inokoru/yuyue/issues?")) {
        return jsonResponse([
          {
            body: `<!-- ntfy-id: oldId text-hash: ${oldHash} -->\n${repeatedAfter24h}`,
            created_at: "2026-11-20T00:00:00Z", // older than 24h -> must NOT block new report
          },
        ]);
      }
      if (url.includes("/actions/workflows/relay.yml/runs?")) {
        return jsonResponse({ workflow_runs: [{ status: "completed" }] });
      }
      if (url.endsWith("/actions/workflows/relay.yml/dispatches")) {
        dispatchedBody = JSON.parse(init.body);
        return new Response(null, { status: 204 });
      }
      throw new Error(`unexpected call: ${url}`);
    },
  });

  assert.deepEqual(res, { triggered: true, reason: "dispatched", pending: 1 });
  assert.deepEqual(dispatchedBody, { ref: "main" });
});

test("skips duplicate dispatch while relay workflow is already running", async () => {
  const ndjson = makeNtfyLine("fresh2", { v: 2, category: "idea", text: "希望能加目录搜索" });

  const res = await checkAndTrigger(BASE_ENV, {
    log: () => {},
    fetch: async (url) => {
      if (url.startsWith("https://ntfy.sh/")) return new Response(ndjson, { status: 200 });
      if (url.includes("/repos/inokoru/yuyue/issues?")) return jsonResponse([]);
      if (url.includes("/actions/workflows/relay.yml/runs?")) {
        return jsonResponse({ workflow_runs: [{ status: "in_progress" }] });
      }
      throw new Error(`should not dispatch when active: ${url}`);
    },
  });

  assert.deepEqual(res, { triggered: false, reason: "already_running", pending: 1 });
});

test("gracefully skips on ntfy 429 without throwing", async () => {
  const res = await checkAndTrigger(BASE_ENV, {
    log: () => {},
    fetch: async () => new Response("too many requests", { status: 429 }),
  });
  assert.deepEqual(res, { triggered: false, reason: "ntfy_rate_limited", pending: 0 });
});

test("enforces MIN_INTERVAL_MINUTES cooldown against burst spam, but fires immediately when idle", async () => {
  const nowMs = Date.parse("2026-11-23T12:00:00Z");
  const ndjson = makeNtfyLine("burst1", { v: 2, category: "bug", text: "连续反馈" });

  const recentRes = await checkAndTrigger(BASE_ENV, {
    nowMs,
    log: () => {},
    fetch: async (url) => {
      if (url.startsWith("https://ntfy.sh/")) return new Response(ndjson, { status: 200 });
      if (url.includes("/repos/inokoru/yuyue/issues?")) return jsonResponse([]);
      if (url.includes("/actions/workflows/relay.yml/runs?")) {
        return jsonResponse({
          workflow_runs: [{ status: "completed", conclusion: "success", created_at: "2026-11-23T11:45:00Z" }],
        });
      }
      throw new Error(`should hold during cooldown: ${url}`);
    },
  });
  assert.deepEqual(recentRes, { triggered: false, reason: "cooldown", pending: 1 });
});

test("does not re-trigger for deleted Issues whose ntfy timestamp predates last successful run", async () => {
  const nowMs = Date.parse("2026-11-23T14:00:00Z");
  const deletedIssueMsgTime = Math.floor(Date.parse("2026-11-23T11:50:00Z") / 1000);
  const ndjson = makeNtfyLine(
    "deletedMsg1",
    { v: 2, category: "bug", text: "已被手动删掉的测试帖" },
    { time: deletedIssueMsgTime }
  );

  const res = await checkAndTrigger(BASE_ENV, {
    nowMs,
    log: () => {},
    fetch: async (url) => {
      if (url.startsWith("https://ntfy.sh/")) return new Response(ndjson, { status: 200 });
      if (url.includes("/repos/inokoru/yuyue/issues?")) return jsonResponse([]); // Issue was deleted!
      if (url.includes("/actions/workflows/relay.yml/runs?")) {
        return jsonResponse({
          workflow_runs: [
            {
              status: "completed",
              conclusion: "success",
              created_at: "2026-11-23T11:55:25Z",
              run_started_at: "2026-11-23T11:55:25Z",
            },
          ],
        });
      }
      throw new Error(`should not re-trigger deleted message: ${url}`);
    },
  });
  assert.deepEqual(res, { triggered: false, reason: "deleted_or_older_than_last_run", pending: 0 });
});

test("fetch handler returns 404", async () => {
  const resp = await worker.fetch();
  assert.equal(resp.status, 404);
});

test("parseNtfyCandidates deduplicates repeated ntfy ids in the same poll", async () => {
  const line = makeNtfyLine("same1", { v: 2, text: "测试" });
  const found = await parseNtfyCandidates(`${line}\n${line}`);
  assert.equal(found.length, 1);
});
