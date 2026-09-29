# Codex Companion

一个用于 Windows 10/11 的小型配置工具：为已安装的 Codex 桌面版配置 mid-web 中转、原生主题、QQ 风格外观及内置宠物选择。程序不承接模型转发、充值或计费；这些仍由 mid-web 提供。

## 使用

> 开发状态：`bin/MidWebCodex.exe` 已包含本轮 UI 更新；其中沿用的托盘退出与自动重启逻辑仍为候选实现。全量回归与配置、退出、重启隔离测试已通过；先前中文路径 staging 清理曾失败，独立诊断确认出现过共享占用，不能视为已修复。但在官方 `26.924.2738.0` 的本机检测中仍未确认可调用的退出菜单，用户也反馈没有可用托盘菜单。正在验证主界面“文件”菜单入口。本次 UI 验证没有退出或重启用户真实 Codex；以下退出/自动重启说明仍描述候选实现，完成本机验证前不要将候选构建部署为正式版。

1. 先安装并启动一次官方 Codex 桌面版，配置或恢复前先保存正在进行的工作。
2. 双击 `bin/CodexCompanion.exe`，无需安装 Node.js、Python 或其他运行时。
3. 输入平台 API Key。点击“一键配置”后，程序会将 Codex 的 `auth.json` 替换为仅包含 `OPENAI_API_KEY` 的对象，填入该密钥，并更新 `config.toml` 的 `Codex Relay` provider。
4. 选择主题、皮肤和宠物，点击“一键配置”。助手会正常退出 Codex、保存配置（`model` 固定为 `gpt-5.6-sol`），成功后自动打开 Codex。在 Codex 新建任务使用中转。Codex 已关闭界面但仍在后台运行时，助手会先通过官方托盘回调恢复主窗口，再调用“文件 → 退出”。
5. 点击“充值中心”会在系统浏览器打开 `https://xai-tools.cn/#recharge`；登录后直接进入 mid-web 充值页。点击“官方网站”会打开 `https://xai-tools.cn`。

助手启动后会从 `https://xai-tools.cn/api/advertisements/companion` 拉取 mid-web 广告管理中的启用广告，并每分钟刷新。单条广告显示宣传栏和详情入口；多条广告按后台排序每 6 秒横向滑动切换，也可使用左右按钮、方向键或滚轮切换，悬停、聚焦或查看详情时暂停自动轮播。没有广告或服务暂时不可用时隐藏宣传栏并恢复布局，下次刷新继续重试。宣传栏显示时下方内容自动下移并调整窗口高度；屏幕空间不足时可滚动查看。广告内容不携带本机 API Key，也不会写入本地配置。

再次配置时，API Key 留空可保留本机已加密保存的密钥。点击“一键配置”或“恢复上次配置”会确认官方 Codex EXE；若界面已关闭但进程仍在后台，会调用该官方进程的托盘左键回调恢复主窗口（官方包激活作为后备），再仅按固定控件 ID 调用“文件”菜单最后一项官方“退出”，等待进程全部退出（最多 30 秒）。旧版客户端仍保留托盘退出菜单兼容路径。此流程不发送快捷键，不读取聊天控件，不修改 Codex 的托盘设置，不强制结束进程；若出现官方退出确认，请自行确认。退出项无法唯一确认、被禁用、取消退出、超时或操作期间重新启动时，配置保持不变。

“一键配置”只有在配置保存成功后才自动打开 Codex。退出或配置失败不会执行启动；如果配置已保存但启动失败，会单独提示“配置已保存，但 Codex 重新启动失败”，可点击“打开 Codex”重试。恢复上次配置后仍可手动点击“打开 Codex”。托盘菜单调用使用 Windows 自带的 UI Automation 程序集，无需安装额外依赖。

“打开 Codex”从安装包的 `AppxManifest.xml` 读取与桌面 EXE 对应的真实 `Application Id`（当前版本为 `App`），通过 Windows 应用激活接口启动，保留应用包身份。操作完成后可点击“打开 Codex”。旧任务可能保留原来的模型或服务设置。

一键配置后，`auth.json` 只包含 `{"OPENAI_API_KEY":"<输入的 API Key>"}`，旧字段和登录信息不再保留在该文件中；原文件仍有加密备份，可通过“恢复上次配置”还原。同时写入以下 `config.toml` 设置（token 使用同一 API Key）：

```toml
model_provider = "custom"
model = "gpt-5.6-sol"
model_reasoning_effort = "xhigh"
disable_response_storage = true

[model_providers.custom]
name = "Codex Relay"
base_url = "https://xai-tools.cn/v1"
wire_api = "responses"
requires_openai_auth = false
experimental_bearer_token = "<输入的 API Key>"
```

原有 `notify` 命令保持不变；没有该字段时不会添加机器相关的通知程序路径。官方网站按钮使用 `OfficialWebsiteUrl`（当前为 `https://xai-tools.cn`），一键配置的 API 中转地址使用 `OfficialRelayUrl`（当前为 `https://xai-tools.cn/v1`）。API Key 同时保留本机 DPAPI 加密备份，用于留空时继续配置。

## 外观

助手自身界面已对齐 `mid_web/apps/web/src/visual-polish.css` 的深色薄荷配色：双栏连接与外观工作区、圆角卡片、明确的主操作、原生键盘下拉选择、主题配色示意及统一的图库和广告样式。修改仅涉及表现层，API 中转地址、模型 ID、密钥保存、配置/恢复、广告请求与启动流程保持原有逻辑。截图与验证说明见 [UI-REFRESH.md](UI-REFRESH.md)。

- 原生主题：保持当前、跟随系统、Codex 浅色/深色、Catppuccin、Dracula、Nord、Forest。应用到 Codex 本体的 `desktop.appearance*` 设置。
- QQ 风格：自有蓝银面板皮肤，参考 Codex-QQ-Skin 的本机样式注入思路；不是原项目的完整移植。保留 Codex 原生布局与操作，不包含其成长统计、音效、图片生成皮肤等额外功能。
- “浏览主题图库”打开用户指定的 CodexThemes 网站。内置主题可直接应用；第三方完整皮肤包、分享字符串导入不由本工具解析，按作者说明安装。
- 宠物：选择本机 Codex 已有的 9 个内置 ID。选择后在 Codex 输入 `/pet` 显示宠物，工具不修改宠物显隐开关。

QQ 模式必须由本工具“打开 Codex”，才会为该次运行启用 loopback 调试端口和皮肤宿主。成功提示以页面样式实际注入为条件。客户端更新造成页面结构不兼容时会报错；可以退出 Codex、选择原生界面并重新配置。

原生主题字段从本机 Codex `26.915.4065.0` 静态实现核实。宠物属于私有本地状态适配，仅允许已核实的 `26.915.*` 版本族，且要求现有状态结构匹配；其他版本需保留当前宠物并在 Codex 内自行选择。此限制不会阻止中转和主题配置。

## 配置边界与恢复

- 连接使用 `model_provider = "custom"`，provider 名称为 `Codex Relay`，使用 Responses 协议和 `experimental_bearer_token`。
- API Key 写入 Codex `auth.json` 和 `config.toml`，并额外通过 Windows DPAPI 加密保存；不会写入日志或环境变量。
- 用户数据保存在 `%LOCALAPPDATA%/MidWebCodex`；备份也使用当前 Windows 用户的 DPAPI 加密，不能直接搬到其他账号或电脑使用。
- Codex 配置目录尊重当前进程的 `CODEX_HOME`，没有设置时使用用户目录的 `.codex`。
- 写入前使用已安装的 Codex `app-server config/batchWrite` 在独立临时目录进行 TOML 修改与校验，再备份并替换正式配置；`auth.json` 与配置文件使用同一份加密备份清单。保留其他 provider、MCP、权限、项目、注释等设置。
- 宠物只合并 `.codex-global-state.json` 中 `electron-persisted-atom-state.selected-avatar-id`，保留其他字段。
- “恢复上次配置”还原最近一次一键配置前的内容。若用户或 Codex 已改动文件，会列出冲突文件名并要求确认；取消不修改配置。确认后先将当前内容加密备份到数据目录的 `backups`，再还原旧文件（包含旧登录信息）。确认期间再次改动配置或生成新备份会拒绝写入，原备份仍可重试。本工具当时未改动或已还原的文件会跳过，不覆盖之后的无关更改。
- 使用独立 profile、受组织强制管理或较旧且缺少配置服务的 Codex，可能拒绝自动配置；工具不会删除管理规则来绕过限制。

QQ 皮肤仅连接本机 `127.0.0.1` 端口，校验页面地址和 Codex DOM 标记。不修改官方安装目录或 `app.asar`。切回原生界面会停止自家皮肤宿主并移除自家样式；完全退出该次启动的 Codex 后调试端口关闭。

## 构建与验证

使用 Windows 自带 .NET Framework C# 编译器，不下载依赖；广告 HTTPS 请求需要 .NET Framework 4.7.2 或更高版本。广告客户端独立启用 TLS 1.2，保留正常证书校验，不修改系统或其他请求的 TLS 设置。加载失败会在空闲状态栏提示，并在下次定时刷新重试。

```powershell
powershell -NoProfile -File .\build.ps1
powershell -NoProfile -File .\build.ps1 -Test
```

可选皮肤协议模拟回归：`python tests/skin_mock.py`（仅开发测试需要 Python，正式程序不需要）。该测试启动独立本机模拟服务，覆盖非法目标拒绝、注入/移除、第三方样式保留和超时；不接触正在运行的 Codex。

输出：`bin/CodexCompanion.exe`。QQ CSS 嵌入主 EXE，不需要用户手工复制运行时；一键配置直接更新 Codex 的 `auth.json` 和 `config.toml`。

测试在全新的 `test-output` 子目录使用虚构配置与密钥，覆盖 URL 校验、原有配置保留、恢复与冲突保护、DPAPI、实际凭据子进程、宠物字段合并，以及本机 Codex 配置服务写入。不会访问真实账号、调用付费模型、修改真实 Codex 配置。

配置服务通过无 BOM 的 UTF-8 管道通信，支持中文 Windows 用户名和目录。回归测试覆盖“睿睿睿”路径、中文及其他 Unicode 配置值，并检查服务退出后的 staging 清理。

界面截图验证入口：

```powershell
.\bin\CodexCompanion.exe --preview D:\my_project\codex_app\.build\preview.png
```

预览模式不读取真实配置、不启用操作按钮。

## GitHub 发布与自动更新

首次安装从 [GitHub Releases](https://github.com/AhaRui-888/Codex-Companion/releases) 下载 `CodexCompanion-Windows.zip`，解压到可写目录并运行 `CodexCompanion.exe`。程序启动时会在后台检查公开的最新稳定版本；顶部“检查更新”也可手动触发。新版 EXE 下载并通过同一 Release 中的 SHA-256 校验后，按钮变为“重启并更新”。点击后助手退出，独立更新程序替换 EXE 并重新打开助手。失败时保留或恢复旧 EXE，不修改 Codex 配置、密钥或主题图库。若安装目录不可写，更新会失败；请将便携包解压到当前用户可写目录。

发布时先更新 `version.txt` 为 `major.minor.patch`，提交并推送代码，然后推送同版本的 `vmajor.minor.patch` 标签。GitHub Actions 在 Windows 上构建并运行更新校验与中转契约测试，生成 EXE、`CodexCompanion.exe.sha256` 和便携 ZIP，并上传 Release。UI 测试需要交互式 Windows 桌面，在本地运行。`assets/theme-gallery` 中的第三方二进制包与预览文件不进入源码仓库及便携包；在线主题图库仍可按需下载，已有本地图库不会被自动更新删除。

```powershell
powershell -NoProfile -File .\build.ps1 -Test -TestSuite update
git tag v0.1.0
git push origin v0.1.0
```

本工具只负责客户端配置，无法补足 mid-web 网关尚未实现的 Responses 协议能力。真实模型调用、支付和上游兼容性需要在 mid-web 单独验证。

## 部署

本目录为独立 Windows 应用，客户端本身不需要 Docker 镜像重建或容器重启。源码更新后重新运行 `build.ps1`，关闭旧版工具，再启动新 EXE。配套的 `mid_web` 广告子路由与管理页面需要在服务端项目执行 `docker compose up -d --build --no-deps control-api web` 后生效（数据库、Redis 等依赖须已运行）；本次广告改动无需数据库迁移。请先部署服务端子路由，再分发新版客户端。

参考：[Codex 自定义模型服务](https://learn.chatgpt.com/docs/config-file/config-advanced)、[主题设置](https://learn.chatgpt.com/docs/app/settings)、[宠物](https://learn.chatgpt.com/docs/pets)、[Codex-QQ-Skin](https://github.com/zhulin025/Codex-QQ-Skin)、[CodexThemes](https://codexthemes.ai/)。
