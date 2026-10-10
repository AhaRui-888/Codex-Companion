# 桌面 UI 更新

## 当前验证：窗口自适应与账户栏（2026-10-10，Asia/Shanghai）

保留现有浅色背景、白色面板和蓝色操作按钮。主窗口现在允许最大化，默认客户区为 930 × 504，最小客户区为 660 × 504。调整窗口大小时使用可用宽度重新排布，而不是缩放文字。

- 窄窗口将品牌与顶部操作分成两行，并将“保存设置后重新启动 Codex”的说明和打开 / 恢复按钮分成两行；API Key 输入与主操作仍相邻。
- 连接区提示和底部操作状态按真实文字测量行高，长消息自动换行。窗口高度不足时使用 Windows 原生纵向滚动条，操作和状态均可到达，不需要横向滚动。
- 宽窗口的内容列最大为 1230 px，水平居中；高窗口在完整内容组上下分配留白，状态紧随连接区，不再由状态行占据大块空白。
- 网站账户栏位于品牌下方，显示登录入口或账户、可用余额及刷新 / 退出入口；与配置操作的忙碌状态关联。截图中的已登录账户与余额使用隔离 fixture，不代表实际用户数据。
- 保留广告展示 / 隐藏时的窗口高度调整、键盘导航、轮播与最大化恢复行为。

隔离 UI 套件与广告套件通过。UI 断言覆盖默认、最小、660 × 700 窄窗、1240 × 860 高窗、1600 × 900 宽窗、广告、未登录与已登录 fixture、短窗滚动可达、字号稳定、账户控件边界和长状态消息。测试没有读取实际登录会话、修改真实配置、退出真实 Codex 或调用模型。

本次截图目录：`test-output/20261010-193303-6180/ui/`。主窗口截图包括 `desktop-default.png`、`desktop-account.png`、`desktop-minimum.png`、`desktop-short-scrolled.png`、`desktop-narrow.png`、`desktop-expanded.png`、`desktop-wide.png`、`desktop-advertisement.png`。构建目录：`.build-check/responsive-ui/bin/`。广告验收输出：`.build-check/responsive-ui/advertisement-check/`。

最终交付构建为 `bin/CodexCompanion.exe`，便携包为 `dist/CodexCompanion-Windows.zip`。完整隔离回归、更新校验、账户协议与中转契约均通过；最终报告及截图保存于 `test-output/account-ui-delivery/`。真实官网表单加载与取消后浏览器资料清理已验证，成功登录和余额读取使用模拟会话及响应，真实账户由用户自行登录验证。

完整回归期间发现原有 Windows 临时目录清理及托盘测试等待时间不足：staging 清理重试上限由 5 秒延长为 15 秒，托盘测试等待预算覆盖原有扫描时长并记录观测信息，配置内容和菜单动作断言保留。修正后完整套件通过。

## 历史验证（2026-10-02，Asia/Shanghai）

默认构建为 `bin/CodexCompanion.exe`。本次全量隔离回归通过，图库测试按实际可见卡片验证滚动加载，并验证离页后释放原图缓存；更新并发、配置恢复、广告和 UI 检查通过；修正皮肤模拟脚本的编译依赖后，skin_mock 回归通过。日志：`.build-check/fix-20261002-validation.log`。本轮未修改真实配置、未退出或重启真实 Codex，也未调用付费模型。下方记录属于各自日期的历史构建，不代表当前验证结果。


## 历史记录：柔和浅色界面（2026-09-30，Asia/Shanghai）

新程序：`bin/CodexCompanionFinal/CodexCompanion.exe`。关闭旧版助手窗口后运行此文件；此次没有关闭或重启用户正在使用的 Codex。窗口标题和顶部品牌均为 **Codex Companion**，不显示 Response/model 标签或 MID-WEB/CODEX COMPANION 品牌脚注。

依据用户提供的 CC Switch 截图学习浅色背景、淡蓝强调、圆角边界和留白，不复制服务商列表的完整排版。保留 WinForms / .NET Framework，因为现有自绘控件足以实现此效果，且可以直接保留 AppController 调用链，无需新增 WebView 桥接层。

- 减少嵌套边框，连接目标独立呈现，凭据与外观设置保持直接可操作。
- 统一按钮、下拉框、主题图库、搜索框、推广横幅及详情窗口的浅色样式。
- 按钮增加短暂的悬停色彩过渡，遵循 Windows 客户区动画开关；保留键盘焦点、按下与禁用状态。
- 保留主题 / 皮肤的真实 ID 绑定、原生下拉交互、密钥遮挡及忙碌锁定。
- 修复主题预览裁切，新增预览边界和应用名称断言；广告布局测试改用稳定控件名称定位。

### 验证结果

- 最终应用编译、EXE 只读预览和 UI 检查通过，截图覆盖默认 / 最小 / 放大 / 广告状态及图库最小窗口。
- 图库 9 项回归、广告协议 / 轮播 / 输入 / 布局检查通过。
- 核心回归 14/15 通过，包含中转 provider、配置保留和恢复冲突、DPAPI、凭据子进程、Responses 成功与错误映射。中文路径测试仍报告 staging 子目录未清理，未宣称完整回归通过。
- 16 项托管重启编排及图库交互检查通过。单独的原生托盘测试在沙箱中触发 UIAutomation 错误，用户上下文复测出现有界等待超时；此项保留为未通过。未修改退出 / 重启后端以规避测试。
- 与改版前本地备份比较，ApplyAsync、OpenCodex、Restore、OpenSkinGallery、官网 / 充值入口、LoadAdvertisementsAsync、SetBusy、LoadInitialState 和 SetOperationsEnabled 均一致（仅比较业务函数，忽略空白）。
- 匿名只读检查：`/v1/models` HTTP 200 且包含当前配置模型，`/api/advertisements/companion` HTTP 200 且返回 1 条推广。此结果仅证明公开接口可达；没有使用真实 API Key 执行模型推理或修改真实配置。

验证记录：`output/ui-cc-switch/validation.json`、`core-regression.json`、`managed-workflows.json`、`gallery-results.txt`、`event-handler-comparison.json`。

```powershell
.\build.ps1 -OutputDirectory bin\CodexCompanionFinal
.\build.ps1 -OutputDirectory bin\CodexCompanionFinal -Test -TestSuite ui
# 可选套件：all / ui / advertisements / gallery / restart
```

`desktop-default.png` 展示隔离测试数据下的按钮可用态；`application-preview.png` 来自最终 EXE 的只读预览模式，配置操作按设计禁用。图库截图使用离线色块 fixture，不代表在线图片加载结果。

![当前主界面](output/ui-cc-switch/desktop-default.png)

---

## 历史记录：此前品牌文案构建

窗口标题和顶部品牌统一为 **Codex Companion**，移除连接区的协议/模型说明与底部品牌脚注。原 EXE 正在运行且被占用，因此本次新构建位于 `bin/CodexCompanion/CodexCompanion.exe`；关闭旧版助手后打开此路径。接口、模型配置及后端代码未改动。

本次编译、隔离 UI 检查和最终 EXE 截图核对通过。全量回归在已有的中文路径 staging 目录清理检查处失败，未据此宣称本次全量通过；详情记录于 `output/ui-refresh/branding-validation.json`。下文的全量通过记录对应此前 UI 改造构建。

本次改动面向 Mid-web / Codex Windows 助手。视觉参考同级 `mid_web/apps/web/src/visual-polish.css` 当前的深色薄荷样式，没有修改正在开发的 mid_web 前端或服务端。

## 界面

- 背景 `#070908`、卡片 `#111513`、主按钮 `#e2f8ea`、强调色 `#b7d5ca`、正文 `#e9eeeb`、辅助文字 `#98a39c`。配色集中在 `src/DesktopVisuals.cs`。
- 顶部品牌和轨道装饰、连接与外观双栏、独立操作区和状态栏。轨道仅作装饰，不表示连接成功。
- 主题与皮肤保留原生下拉和键盘行为；按钮保留焦点、禁用态及原来的事件绑定。
- 密钥显示切换、主题配色示意、图库卡片、筛选与分页、广告横幅和详情采用一致样式。
- 修复品牌不显示、自绘文本缩放、图库文本裁切和最小窗口分页操作溢出；测试程序采用与 EXE 相同的 DPI manifest。

## 接口与行为验证

既有 `ApplyAsync`、`OpenCodex`、`Restore`、官网/充值入口、图库入口、广告请求、初始配置读取和选项 ID 绑定均已与修改前备份对照。仅广告布局计算的基准高度随新布局调整。

- 核心回归：15 项通过，包含 provider 配置、原有配置保留、恢复冲突、DPAPI、凭据子进程、中文路径及 Responses 成功/错误映射。
- 图库回归：9 项通过，覆盖分页、离线缓存、安装包校验和预览缓存等。
- 退出/重启隔离测试、广告请求/取消/轮播/键盘/布局测试及新增 UI 测试通过。
- UI 测试覆盖密钥遮挡、空密钥拦截、选项 ID、原生下拉、操作锁、默认/最小/放大/广告布局及图库最小窗口。测试目录使用模拟密钥与独立配置路径。
- 本轮已只读检查官方公开接口：`/v1/models` 和 `/api/advertisements/companion` 均返回 HTTP 200；模型列表包含当前配置的 `gpt-5.6-sol`。

这些检查确认现有协议和公开接口可用；没有使用真实 API Key 发起付费模型调用，也没有退出或重启用户当前 Codex。真实账号鉴权及上游模型端到端调用仍需实际环境验证。既有自动退出候选实现的限制继续见 README。

## 运行与预览

构建文件：`bin/CodexCompanion.exe`。关闭旧版助手后手动打开新 EXE。无需安装新依赖。

```powershell
.\build.ps1
.\build.ps1 -Test
# 已编译回归程序的独立 UI 检查；目标目录必须不存在
.\.build\Regression.exe .\test-output\my-new-ui-check --ui
```

最终预览保存在 `output/ui-refresh/`。`desktop-default.png` 为隔离测试数据下的可用按钮示意；`application-preview.png` 与 `application-gallery-preview.png` 由最终 EXE 的只读预览入口生成，按设计禁用写入操作。图库的色块为离线 fixture，并非在线主题图片加载失败。

![主界面](output/ui-refresh/desktop-default.png)

![主题图库](output/ui-refresh/gallery.png)
