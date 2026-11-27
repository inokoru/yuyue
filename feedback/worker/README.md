# 反馈按需触发器（Cloudflare Worker · 旧版归档）

> **注意（已停用）**：Apple、Android 与 Windows 客户端已统一迁移至 **AirFeedback** 单通道直连方案，本目录下的 `ntfy.sh → Cloudflare Worker → GitHub Actions` 中转脚本目前处于**未启用状态**，仅作归档参考。当前生效的反馈与崩溃上报架构见 [docs/dev/feedback.md](../../docs/dev/feedback.md)。

GitHub Actions 原生的 `on.schedule` 在私有仓库上经常延迟 4～8 小时甚至静默丢弃，超过 `ntfy.sh` 附件 3 小时的留存时间。

这个 Worker 只跑后台定时任务（Cron Trigger），**不需要服务器、不需要绑定任何域名、也不对外暴露公网接口**：

1. 每 **5 分钟**拉一次 `ntfy.sh/<话题>/json?poll=1&since=12h`；
2. 没消息时直接退出（不调 GitHub API，0 消耗）；
3. 有消息时对照 `inokoru/yuyue` 已有的「反馈」Issue（按 `ntfy-id` 与 24 小时 `text-hash` 去重）；
4. 只有确实存在**未处理的反馈**、且距上次运行已满冷却间隔（默认 **25 分钟**：平时首条 ≤ 5 分钟起跑，遭遇连续轰炸时自动合并批次，死守私有库每月 2,000 分钟免费额度）时，才调用 GitHub API 触发 `inokoru/yuyue-feedback` 的 `workflow_dispatch`。

---

## 1. 准备一个最小权限的 GitHub Token（`DISPATCH_TOKEN`）

打开 GitHub → **Settings** → **Developer settings** → **Personal access tokens** → **Fine-grained tokens** → **Generate new token**：

- **Resource owner**：选择组织 **`inokoru`**（不要选个人账号，否则看不到组织私有库）
- **Repository access**：选择 **Only select repositories** → 勾选 **`inokoru/yuyue-feedback`**
- **Repository permissions**：只开 **`Actions` → `Read and write`**（其余全部保持 `No access`）

生成后复制保存，这就是下面要填的 `DISPATCH_TOKEN`。

---

## 2. 部署方式（二选一）

### 方式 A：命令行一键部署（推荐，自动配好定时与变量）

在本机终端（开启代理以确保能连上 Cloudflare API）执行：

```sh
cd feedback/worker
npx wrangler login
npx wrangler secret put NTFY_TOPIC       # 粘贴 ntfy 话题名后回车
npx wrangler secret put DISPATCH_TOKEN   # 粘贴刚才生成的 GitHub Token 后回车
npx wrangler deploy
```

部署完成后无需再管，Cloudflare 会按 `wrangler.toml` 里的 `*/5 * * * *` 每 5 分钟自动执行。

### 方式 B：Cloudflare 网页后台纯手动粘贴（零命令行）

1. 登录 [Cloudflare Dashboard](https://dash.cloudflare.com/)（免费账号即可，无需绑信用卡）→ 左侧 **Compute (Workers)** → **Workers & Pages** → **Create** → **Create Worker**，名字填 `yuyue-feedback-cron`，点 **Deploy**。
2. 点右上角 **Edit code**，把 `feedback/worker/worker.js` 的全部内容粘贴进去覆盖默认代码，点右上角 **Deploy**。
3. 回到该 Worker 的 **Settings**：
   - **Variables and Secrets** → 点 **Add** 添加两个 **Secret**：
     - `NTFY_TOPIC`：你的 ntfy 话题名
     - `DISPATCH_TOKEN`：第 1 步生成的 GitHub Fine-grained Token
   - **Trigger Events** → 点 **Add Cron Trigger**，输入 `*/5 * * * *`（每 5 分钟）并保存。
   - （可选）在 **Domains & Routes** 里把 `workers.dev` 关掉，彻底不对外暴露 URL。

---

## 3. 同步更新私有仓库 `inokoru/yuyue-feedback`

把更新后的 `feedback/relay/relay.yml` 复制到私有仓库 `inokoru/yuyue-feedback` 的 `.github/workflows/relay.yml` 并推送到 `main`，去掉失效的原生 `schedule` 和每周空提交保活任务。

本地自测：

```sh
node --test feedback/worker/test_worker.mjs
```
