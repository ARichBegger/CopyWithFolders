# 发布流程

本文档说明如何从代码变更走到一个 GitHub Release。

## 版本号规则

采用[语义化版本](https://semver.org/lang/zh-CN/) `MAJOR.MINOR.PATCH`：

| 场景 | 示例 |
|---|---|
| 不兼容的行为变更（如配置格式、剪贴板策略语义） | `3.0.0` → `4.0.0` |
| 向后兼容的新功能 | `3.0.0` → `3.1.0` |
| 向后兼容的缺陷修复 | `3.0.0` → `3.0.1` |

版本号需在三处保持一致：

1. `src/CopyWithFolders/CopyWithFolders.csproj` 的 `<Version>` 与 `<FileVersion>`
2. Git 标签名：`v3.0.0`
3. `CHANGELOG.md` 的标题

## 发布步骤

### 1. 准备变更

```powershell
# 确保工作区干净
git status

# 跑通全部测试
dotnet run --project tests/CopyLogicTest -c Release
dotnet run --project tests/UiLayoutTest -c Release   # 需要交互式桌面
```

### 2. 更新元数据

- 在 `CHANGELOG.md` 顶部把 `[Unreleased]` 的内容移入新版本小节，
  并补上发布日期与对比链接
- 修改 `csproj` 中的 `<Version>` / `<FileVersion>`
- 如有必要，同步更新 README 中的截图与说明

### 3. 提交并打标签

```powershell
git add -A
git commit -m "chore(release): v3.1.0"
git tag -a v3.1.0 -m "CopyWithFolders v3.1.0"
git push origin main
git push origin v3.1.0
```

### 4. 自动构建与发布

推送标签后，`.github/workflows/release.yml` 会自动：

1. 在 `windows-latest` 上还原依赖
2. 运行逻辑测试
3. 发布自包含单文件 exe
4. 计算 SHA256
5. 创建 GitHub Release 并附加 exe

若自动流程失败，可在本地手动发布：

```powershell
pwsh ./build.ps1                # 产出 dist/CopyWithFolders.exe
gh release create v3.1.0 dist/CopyWithFolders.exe `
  --title "CopyWithFolders v3.1.0" `
  --notes-file CHANGELOG.md
```

### 5. 发布后核对

- [ ] Release 页面能下载到 exe，且文件名正确
- [ ] 在一台**没有装 .NET** 的机器上双击可以运行
- [ ] `settings.json` 能在 exe 同目录正常生成
- [ ] README 徽章显示的版本号已更新
- [ ] `CHANGELOG.md` 的对比链接可点击

## 持续集成

`.github/workflows/ci.yml` 在每次 push 到 `main` 或提交 PR 时运行：

- 还原 + 编译（`TreatWarningsAsErrors` 未开启，但要求零警告）
- 运行核心逻辑测试

界面排版测试需要交互式桌面会话，因此**不在 CI 中运行**，
请在本地发布前手动执行一次。

## 维护要点

- **标签不可移动**：已发布的标签不要 force-push 覆盖，
  需要修正时发新的 PATCH 版本
- **发布说明写给用户看**：以"用户能感知的变化"为主线，
  内部重构放在末尾或省略
- **保留预发布**：大改动可先打 `v3.1.0-rc.1` 之类的预发布标签验证
- **依赖升级**：项目不依赖第三方 NuGet 包，只需关注 .NET SDK 与运行时的安全更新
