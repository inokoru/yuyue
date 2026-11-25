## 改动说明

<!-- 简述做了什么、为什么做 -->

## 多端对齐检查（Layer 0 & Layer 1）

- **涉及平台**：
  - [ ] Core (`core/yuyue-core`)
  - [ ] Reader (`reader/`)
  - [ ] Mac / iOS (`apple/`)
  - [ ] Android (`android/`)
  - [ ] Windows (`windows/`)
- [ ] **逻辑下沉**：非界面业务逻辑已放入 `yuyue-core` 或 `reader/`，未在多端重复实现
- [ ] **功能对照表**：已更新 `docs/parity.yaml`（标 `done` 的附测试证据，未跟进端写明 `note`）并通过 `node scripts/check-parity.mjs`
- [ ] **契约与测试**：
  - [ ] 核心共享用例（`fixtures/conformance/`）或核心单测已补充并通过
  - [ ] 若改动书页消息/设置，已运行 `cd reader && pnpm schema && pnpm test`
  - [ ] 若涉及主线界面或跨设备同步，已跑对应 Midscene 场景 / `node scripts/sync-webdav-e2e.mjs` / [docs/dev/testing.md](docs/dev/testing.md) 并在下方写明设备与结果

## 验证记录

<!-- 写明跑了哪些命令/场景、在什么设备、结果如何；界面改动附截图或 Midscene HTML 报告 -->
