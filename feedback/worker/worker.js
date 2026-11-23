/**
 * 语阅反馈按需触发器（Cloudflare Worker，纯后台 Cron，不需要绑定域名）。
 *
 * 每 5 分钟检查一次 ntfy.sh 上最近 12 小时的消息：
 *   1. 没有合法的 v2 反馈消息 → 直接退出（不调 GitHub API，0 消耗）。
 *   2. 对照 inokoru/yuyue 已有的「反馈」Issue（按 ntfy-id 与 24h text-hash 去重）：
 *      若全部已处理 → 直接退出（0 Actions 分钟消耗）。
 *   3. 若 inokoru/yuyue-feedback 已有排队或运行中的 relay 工作流 → 跳过重复触发。
 *   4. 否则调用 GitHub API 触发一次 workflow_dispatch，几秒内由 Actions 拉取附件并建 Issue。
 *
 * 环境变量 / Secrets：
 *   Secrets（加密存储）：
 *     NTFY_TOPIC      ntfy 话题名
 *     DISPATCH_TOKEN  GitHub Fine-grained PAT，仅需给 inokoru/yuyue-feedback 仓库开启 Actions: Read and write
 *
 *   Vars（wrangler.toml 已配默认值，可按需覆盖）：
 *     NTFY_SERVER           默认 https://ntfy.sh
 *     ISSUE_REPO            默认 inokoru/yuyue
 *     RELAY_REPO            默认 inokoru/yuyue-feedback
 *     WORKFLOW_FILE         默认 relay.yml
 *     REF                   默认 main
 *     LABEL                 默认 反馈
 *     MIN_INTERVAL_MINUTES  两次触发的最小间隔分钟数（默认 25：平时首条 ≤5 分钟起跑，被连刷时合并批次保住 2000 分钟免费额度）
 */

const TEXT_MAX = 1000;
const DEDUP_WINDOW_MS = 24 * 3600 * 1000;
const ISSUE_SCAN_WINDOW_MS = 48 * 3600 * 1000;
const DEFAULT_MIN_INTERVAL_MINUTES = 25;
const ID_RE = /ntfy-id:\s*([^\s<]+)/;
const HASH_RE = /text-hash:\s*([0-9a-f]{16})/;

export function safeId(raw) {
  return String(raw || "").replace(/[^A-Za-z0-9_-]/g, "").slice(0, 80);
}

export function openMessage(raw) {
  const text = String(raw || "").trim();
  if (!text.startsWith("{")) return null;
  let data;
  try {
    data = JSON.parse(text);
  } catch {
    return null;
  }
  if (!data || typeof data !== "object" || Array.isArray(data) || data.v !== 2) {
    return null;
  }
  if (typeof data.text !== "string") return null;
  const trimmed = data.text.trim();
  if (!trimmed || data.text.length > TEXT_MAX) return null;
  return data;
}

export async function textHash(text) {
  const normalized = String(text || "").trim().split(/\s+/).filter(Boolean).join(" ");
  const bytes = new TextEncoder().encode(normalized);
  const digest = await crypto.subtle.digest("SHA-256", bytes);
  return Array.from(new Uint8Array(digest), (b) => b.toString(16).padStart(2, "0"))
    .join("")
    .slice(0, 16);
}

export async function parseNtfyCandidates(ndjson) {
  const seenInBatch = new Set();
  const candidates = [];
  for (const line of String(ndjson || "").split(/\r?\n/)) {
    if (!line.trim()) continue;
    let item;
    try {
      item = JSON.parse(line);
    } catch {
      continue;
    }
    if (!item || typeof item !== "object" || item.event !== "message") continue;
    if (item.attachment) continue;
    const id = safeId(item.id);
    if (!id || seenInBatch.has(id)) continue;
    const data = openMessage(item.message);
    if (!data) continue;
    seenInBatch.add(id);
    candidates.push({
      id,
      time: typeof item.time === "number" && Number.isFinite(item.time) ? item.time : 0,
      hash: await textHash(data.text),
    });
  }
  return candidates;
}

function githubHeaders(token) {
  const headers = {
    Accept: "application/vnd.github+json",
    "X-GitHub-Api-Version": "2022-11-28",
    "User-Agent": "yuyue-feedback-cron",
  };
  if (token) {
    headers.Authorization = `Bearer ${token}`;
  }
  return headers;
}

async function fetchRecentMarkers(fetchFn, issueRepo, label, token, nowMs) {
  const seenIds = new Set();
  const seenHashes = new Set();
  const dedupCutoff = nowMs - DEDUP_WINDOW_MS;
  const scanCutoff = nowMs - ISSUE_SCAN_WINDOW_MS;

  for (let page = 1; page <= 5; page++) {
    const params = new URLSearchParams({
      state: "all",
      labels: label,
      sort: "created",
      direction: "desc",
      per_page: "100",
      page: String(page),
    });
    const url = `https://api.github.com/repos/${issueRepo}/issues?${params}`;
    const resp = await fetchFn(url, { headers: githubHeaders(token) });
    if (!resp.ok) {
      throw new Error(`读取 ${issueRepo} Issues 失败：HTTP ${resp.status}`);
    }
    const issues = await resp.json();
    if (!Array.isArray(issues) || issues.length === 0) break;

    let oldestCreatedMs = Infinity;
    for (const issue of issues) {
      if (!issue || typeof issue !== "object" || "pull_request" in issue) continue;
      const body = String(issue.body || "");
      const idMatch = ID_RE.exec(body);
      if (idMatch) seenIds.add(idMatch[1]);

      const createdMs = issue.created_at ? Date.parse(issue.created_at) : NaN;
      if (!Number.isNaN(createdMs) && createdMs < oldestCreatedMs) {
        oldestCreatedMs = createdMs;
      }
      if (Number.isNaN(createdMs) || createdMs >= dedupCutoff) {
        const hashMatch = HASH_RE.exec(body);
        if (hashMatch) seenHashes.add(hashMatch[1]);
      }
    }

    if (issues.length < 100 || oldestCreatedMs < scanCutoff) break;
  }

  return { seenIds, seenHashes };
}

async function checkRelayRuns(fetchFn, relayRepo, workflowFile, token, nowMs, minIntervalMs) {
  const url = `https://api.github.com/repos/${relayRepo}/actions/workflows/${encodeURIComponent(workflowFile)}/runs?per_page=10`;
  const resp = await fetchFn(url, { headers: githubHeaders(token) });
  if (!resp.ok) return { active: false, coolingDown: false, lastSuccessStartedSec: null };
  const data = await resp.json();
  const runs = Array.isArray(data?.workflow_runs) ? data.workflow_runs : [];

  let lastSuccessStartedSec = null;
  for (const r of runs) {
    if (r && r.status === "completed" && r.conclusion === "success") {
      const ts = Date.parse(r.run_started_at || r.created_at || "");
      if (!Number.isNaN(ts)) {
        lastSuccessStartedSec = Math.floor(ts / 1000);
        break;
      }
    }
  }

  const active = runs.some(
    (r) => r && (r.status === "queued" || r.status === "in_progress" || r.status === "waiting")
  );
  if (active) return { active: true, coolingDown: false, lastSuccessStartedSec };

  if (minIntervalMs > 0) {
    for (const r of runs) {
      if (!r || r.conclusion === "cancelled") continue;
      const createdMs = r.created_at ? Date.parse(r.created_at) : NaN;
      if (!Number.isNaN(createdMs) && nowMs - createdMs < minIntervalMs) {
        return { active: false, coolingDown: true, lastSuccessStartedSec };
      }
    }
  }
  return { active: false, coolingDown: false, lastSuccessStartedSec };
}

export async function checkAndTrigger(env, options = {}) {
  const fetchFn = options.fetch || fetch;
  const nowMs = options.nowMs ?? Date.now();
  const log = options.log || ((msg) => console.log(msg));

  const topic = String(env.NTFY_TOPIC || "").trim();
  const token = String(env.DISPATCH_TOKEN || "").trim();
  if (!topic || !token) {
    throw new Error("缺少环境变量 NTFY_TOPIC 或 DISPATCH_TOKEN");
  }

  const server = String(env.NTFY_SERVER || "https://ntfy.sh").trim().replace(/\/+$/, "");
  const issueRepo = String(env.ISSUE_REPO || "inokoru/yuyue").trim();
  const relayRepo = String(env.RELAY_REPO || "inokoru/yuyue-feedback").trim();
  const workflowFile = String(env.WORKFLOW_FILE || "relay.yml").trim();
  const ref = String(env.REF || "main").trim();
  const label = String(env.LABEL || "反馈").trim();
  const rawMin = env.MIN_INTERVAL_MINUTES;
  const minMinutes =
    rawMin === undefined || rawMin === "" ? DEFAULT_MIN_INTERVAL_MINUTES : Math.max(0, Number(rawMin) || 0);
  const minIntervalMs = minMinutes * 60 * 1000;

  const pollUrl = `${server}/${encodeURIComponent(topic)}/json?poll=1&since=12h`;
  const ntfyResp = await fetchFn(pollUrl, {
    headers: {
      Accept: "application/x-ndjson",
      "User-Agent": "yuyue-feedback-cron",
    },
  });
  if (ntfyResp.status === 429) {
    log("ntfy 返回 429（共享出口 IP 瞬时限流），跳过本轮，等 5 分钟后重试");
    return { triggered: false, reason: "ntfy_rate_limited", pending: 0 };
  }
  if (!ntfyResp.ok) {
    throw new Error(`拉取 ntfy 失败：HTTP ${ntfyResp.status}`);
  }

  const candidates = await parseNtfyCandidates(await ntfyResp.text());
  if (candidates.length === 0) {
    log("ntfy 暂无反馈消息");
    return { triggered: false, reason: "empty", pending: 0 };
  }

  const { seenIds, seenHashes } = await fetchRecentMarkers(fetchFn, issueRepo, label, token, nowMs);
  const unmatched = candidates.filter((c) => !seenIds.has(c.id) && !seenHashes.has(c.hash));
  if (unmatched.length === 0) {
    log(`ntfy 中有 ${candidates.length} 条消息，均已处理，跳过触发`);
    return { triggered: false, reason: "already_handled", pending: 0 };
  }

  const runState = await checkRelayRuns(fetchFn, relayRepo, workflowFile, token, nowMs, minIntervalMs);
  const pending =
    runState.lastSuccessStartedSec === null
      ? unmatched
      : unmatched.filter((c) => c.time === 0 || c.time >= runState.lastSuccessStartedSec - 15);
  if (pending.length === 0) {
    log("ntfy 中未匹配 Issue 的消息均早于上次成功运行（对应 Issue 可能已手动删除），跳过触发");
    return { triggered: false, reason: "deleted_or_older_than_last_run", pending: 0 };
  }
  if (runState.active) {
    log(`发现 ${pending.length} 条待处理反馈，但工作流正在运行中，等下一轮`);
    return { triggered: false, reason: "already_running", pending: pending.length };
  }
  if (runState.coolingDown) {
    log(`发现 ${pending.length} 条待处理反馈，距上次运行未满 ${minMinutes} 分钟，攒批到下一轮`);
    return { triggered: false, reason: "cooldown", pending: pending.length };
  }

  const dispatchUrl = `https://api.github.com/repos/${relayRepo}/actions/workflows/${encodeURIComponent(workflowFile)}/dispatches`;
  const dispatchResp = await fetchFn(dispatchUrl, {
    method: "POST",
    headers: {
      ...githubHeaders(token),
      "Content-Type": "application/json",
    },
    body: JSON.stringify({ ref }),
  });

  if (dispatchResp.status !== 204) {
    const detail = await dispatchResp.text();
    throw new Error(`触发 workflow_dispatch 失败：HTTP ${dispatchResp.status} ${detail}`);
  }

  log(`发现 ${pending.length} 条待处理反馈，已触发 ${relayRepo}@${ref} (${workflowFile})`);
  return { triggered: true, reason: "dispatched", pending: pending.length };
}

export default {
  async scheduled(_controller, env, ctx) {
    ctx.waitUntil(checkAndTrigger(env));
  },

  async fetch() {
    return new Response("Not Found", { status: 404 });
  },
};
