# 语阅 Windows 端 Midscene 视觉 E2E 测试

基于 `@midscene/test` + `@midscene/computer` 搭建的 Windows 桌面视觉自动化测试目录，用来补齐 [docs/dev/testing.md](../../docs/dev/testing.md) 里单元测试覆盖不到的界面排版、深色模式、PDF 渲染和跨组件交互验收。

测试启动 `Yuyue.exe` 时会自动传入 `--library <临时目录>`，**不会触碰你的真实书库**；每条用例结束或异常中断后会自动关闭测试进程并清理临时书库。

## 1. 前置准备

1. 先构建 Windows 端调试包（如果还没有编译）：
   ```powershell
   pwsh windows/build-core.ps1 -Debug
   dotnet build windows/Yuyue/Yuyue.csproj
   ```
2. 安装依赖（在 `tests/midscene/` 目录执行）：
   ```powershell
   cd tests/midscene
   pnpm install --ignore-workspace
   ```
3. 配置本地视觉模型：
   ```powershell
   Copy-Item .env.example .env
   ```
   编辑 `.env`，按你本机的推理后端（Ollama / vLLM / LM Studio / UI-TARS）取消对应模板的注释并填写模型名。
   > `.env` 已被 `.gitignore` 忽略，不会提交进仓库。

4. 校验模型连通性与坐标协议（不会启动桌面程序）：
   ```powershell
   pnpm run model:verify
   ```

## 2. 运行测试

```powershell
# 运行 cases/ 下全部用例
pnpm test

# 只跑单个用例文件
pnpm test cases/01-shelf-smoke.yaml

# 查看本机显示器列表（多屏时可填入 .env 的 COMPUTER_DISPLAY_ID）
pnpm run displays
```

测试报告生成在 `tests/midscene/midscene_run/report/`（已加入 `.gitignore`），打开 HTML 即可逐帧回放每一步截图、定位坐标和模型推理过程。

## 3. 自定义节点与新增用例

[midscene.config.ts](./midscene.config.ts) 已为语阅注册两个专用生命周期节点：

- **`yuyue.launch`**：以独立临时 `--library` 启动 `Yuyue.exe`，支持 `fixtures`（直接引用 `fixtures/books/` 下的夹具并导入打开）、`theme`（`light` / `dark`）、`seedDemo`、`open`、`tab`、`settings`、`reuseLibrary` 等参数。
- **`yuyue.close`**：关闭当前测试进程；传 `keepLibrary: true` 可保留临时书库，配合下一次 `yuyue.launch({ reuseLibrary: true })` 测试“退出再开仍保留进度/设置”。

修改 `midscene.config.ts` 中的节点定义后，执行以下命令刷新节点说明文档（供人和 AI 写 YAML 用例参考）：

```powershell
pnpm run nodes
```

## 4. Windows 端常见注意事项

- **不要混用管理员权限（UIPI 隔离）**：确保终端与 `Yuyue.exe` 同为普通用户权限，否则 Windows 会拦截模拟键鼠输入。
- **执行期间不要动鼠标**：`@midscene/computer` 走系统级真实键鼠事件，运行测试时避免切窗口。
- **本地模型推荐**：优先选择具备 GUI 坐标定位能力的模型（如 `Qwen3-VL`、`Qwen2.5-VL`、`UI-TARS-1.5`）；纯通用聊天模型容易点击偏位。
