# DeskBox 内存占用归因与优化方案（2026-10-27）

## 结论速览

**占用大头不是缓存、不是托管泄漏，而是「WinAppSDK/WinUI 底座 + 每格子一个独立 HWND/XAML 内容树」的 native 内存。**

- 全部图片/图标缓存在 12 格子真实配置下合计 **< 2MB**，可以排除。
- 托管堆两次 gcdump 差分仅 +1.5MB（29.6→31.1MB），无泄漏。
- **折叠（胶囊）态几乎不省内存**：12 格子全折叠 270.8MB vs 展开 288.3MB，只差 17.5MB（6%）。这是当前代码里最值得修的点。
- 用户日常的 300+MB（Debug 构建 411-452MB Private）中，约 **120-160MB 是 managed-jit 运行时开销**；AOT 零售同配置为 288MB Private。

## 测量方法

- 构建：`.artifacts/aot-retail` 的 Native AOT 零售产物，复制到 `%TEMP%\deskbox-aot-run` 运行（仓库路径下的进程会被 repo-owned 清理误杀）。
- 数据根：`%TEMP%\deskbox-memattr-data`（复制自生产 `%LOCALAPPDATA%\DeskBox` 的 12 格子真实配置），经 `DESKBOX_AOT_PREVIEW_DATA_ROOT` 隔离。
- 口径：`DESKBOX_PERF_LOG=1` 的应用内 `[Perf] MemorySample` + `Get-Process` 采样，启动后 ~50s 稳态读数。**Private（提交）为主口径**，WS 受工作集裁剪影响仅参考。
- 对照变量：同一构建、同一数据根，仅改 `widget-layout.json` 的 `layout.widgets`（注意不是顶层 `widgets`——顶层不存在该键）。
- 测量陷阱记录：
  1. `SettingsService.cs` 规范化会把所有非 Glance 格子的 `IsDisabled` 强制改回 `false`，靠禁用做对照无效；需**清空 widgets 列表 + 写入 deletedWidgetIds 墓碑**。
  2. 测试进程会在启动它的 shell 退出时被作业对象回收，启动/等待/采样必须在同一次调用内完成。

## 归因数据（AOT 零售，ResourceSaver + Small cacheBudget）

| 场景 | windows | WS(MB) | Private(MB) | 托管堆(MB) | private−GcHeap(MB) |
|---|---|---|---|---|---|
| **0 格子（地板值）** | 0 | 175.9 | **96.1** | 10.2 | 89.8 |
| 1 个 File 格子（另 2 个组占位窗） | 3 | 270.9 | 188.9 | 23.6 | 162.8 |
| 5 个 File 格子 | 5 | 297.5 | 217.3 | 27.2 | 191.2 |
| **9 格子全开（真实混合配置）** | 9 | 371.8 | **288.3** | 41.5 | 256.9 |
| 12 格子全折叠（胶囊态） | 9 | 352.5 | 270.8 | 35.8 | 241.8 |

派生结论：

| 成本项 | 数值 | 说明 |
|---|---|---|
| 平台底座（0 窗口） | **~96MB Private** | WinAppSDK/WinUI 初始化、托盘、热键、SettingsIpc、显示监听等；native ≈ 90MB |
| 每格子窗口边际成本 | **~15-20MB Private/窗口** | 5→9 窗口实测 (288.3−217.3)/4 ≈ 17.8MB；含 HWND、XAML 内容树、DirectComposition/D2D 图面 |
| 托管堆 | 10→42MB | 随格子数缓慢增长，健康 |
| 图标/位图/缩略图缓存 | **< 2MB** | iconCache 55 条、decodedBitmap 0.8MB、thumbCache 0——预算上限 32/48/32MB 实际远未触及 |
| Debug(JIT) 运行时溢价 | ~120-160MB | 用户 Debug 实例 Private 411-452MB vs AOT 同档 ~289MB |

组成员缓存树此前 P0 归因已证实只占稳态 2-3%（`residency-p0-attribution-20260926.md`），本次结论与之互补：**问题在每窗口固定成本与内容树驻留，不在缓存。**

## 代码问题判定

1. **【主因】折叠/胶囊态不释放内容树**（`WidgetShell.xaml.cs` / `WidgetWindowBase.Collapse.cs`）
   `SetCollapsed` 只切换 chrome 层，`ShellContentPresenter.Visibility = Collapsed` —— 仅隐藏不卸载，整棵 XAML 内容树 + ViewModel + 图面全量驻留。实测 12 格子全折叠只比展开省 17.5MB。
2. **【放大器】长隐藏回收被 `Visible` 门槛锁死**（`ContentWidgetWindow.ContentSwitching.cs:210`）
   `ReleaseLongHiddenContentResources()` 首行 `if (Visible || IsClosing) return 0;`。胶囊态窗口仍 `Visible=true`，该回收路径**永远不会触发**；且该方法刻意保留 `CurrentContent` 只清缓存组员（注释：避免下次展开重建）。结果：隐藏/折叠格子的 XAML 树无任何释放路径。
3. **【架构固有】一组一窗、一格子一 HWND**——多格子架构（保持不变）的必然成本。WinUI 3 每窗口 1080p GDI 重定向图面约 7.9MB（roadmap #11523），外加每棵内容树。折叠时 HWND bounds 会缩到胶囊尺寸（图面随之缩小），这解释了折叠残留的 ~17.5MB 降幅。
4. **【轻微】File 格子整目录物化**：5 个 File 格子 90 个 FileItemSurface 全量驻留；`RenderedItems` 渲染窗口阈值 300 起作用，用户目录均 <300 项故全部物化。单条目成本低，不是大头。
5. **【行为怪异，非内存问题】** `SettingsService.cs` 规范化强制 `IsDisabled=false`（非 Glance）——「禁用」语义实际只对 Glance 生效，属产品语义问题，建议另行澄清。
6. **【观察项】** 长会话 Private 缓慢爬升（Debug 90min 337→452MB）而托管堆平稳，是 native 侧缓慢增长（D2D/XAML/碎片），未见泄漏形态；54→63 线程波动属线程池正常范围。

## 优化方案（保持多格子 widget 架构不变）

### P0-A：胶囊/折叠态卸载内容树（治本核心，预期收益最大）

- 新增「内容休眠」状态，与「窗口可见」解耦：折叠为胶囊（或组内非活动成员超时）后，卸载 `ShellContentPresenter` 内容树、释放 content ViewModel 与图面，只保留 HWND + 壳 chrome + 胶囊 UI。
- 展开时重建（预算 100-300ms 一次性重建，可复用现有 `CompactWarmup` 通道做预热）。
- **预期**：12 格子全折叠从 270MB → 约 130-160MB；日常「常驻几个胶囊」场景每格子驻留从 ~18MB 降到 ~5-8MB（壳 + HWND）。

**P0-A 落地（2026-11-07）**：胶囊折叠 30 秒后释放 `CurrentContent`（保留 HWND + 壳 + 胶囊静态面），hover/展开/胶囊按钮动作时经现有 wake factory 重建（`CompactContentSleepPolicy`、`ContentWidgetWindow.OnTargetCollapsedChanged` / `TrySleepCompactContent`）。AOT 零售实测（3 个胶囊内容释放）：Private **189.3 → 167.6MB**、WS **115 → 50MB**；副屏（DISPLAY1）用 `SW_SHOWNOACTIVATE` 复核，释放前后胶囊标题/图标/摘要保持完整，展开按钮触发 `WidgetContentWakeCompleted` 并重新加载内容。代价：首次展开重建约百毫秒级；组内切换成员时按新内容重建胶囊面。

### P0-B：补全长隐藏释放路径

- 修 `ReleaseLongHiddenContentResources` 的 `Visible` 门槛：胶囊可见但内容隐藏时也应允许释放 `CurrentContent`（现在刻意保活）。
- 与现有 `hiddenCacheCleanupDelaySeconds` 节拍对齐：隐藏 N 分钟后整树释放，重建走冷路径。
- **预期**：深夜/长时间不用时 Private 可回落到底座 + 少量壳；工作集裁减从「必开」降为「可选锦上添花」。

### P1-A：每窗口固定成本瘦身

- 压缩每格子 XAML 树深度：chrome 元素（把手、边框、按钮层）用 `x:DeferLoadStrategy`/按需加载；共享 Style/DWM 资源已共享的部分继续保持。
- 评估长隐藏格子直接**销毁 HWND**（roadmap 的 Cold 档）：彻底消除 7.9MB 图面 + 窗口过程开销，唤起时重建窗口。作为 P0-A 的激进版，可做成设置项（如「深度休眠」）。

**P1-A 评估（2026-11-07）**：Cold 销毁 HWND 已在 B1 试点（AOT 零售负收益，已回滚），现由 P0-C 的进程级懒恢复替代。XAML 树瘦身量化：shell 共 201 个元素，其中 `CollapsedChromeLayer` 子树 **125 个**（62%），`WidgetShell.xaml.cs` 有 **379 处、123 个** compact 符号引用。两条直接延迟创建路径均失败（`x:Load` 绑定、同文件 `DataTemplate` + `FindName`，均让 WidgetShell 构造抛 `XamlParseException`）。最终完成独立控件抽取（A1，`2dafd36`）；A2 懒创建在 AOT 下显示 Expanded 窗口时触发 `Microsoft.UI.Windowing` fail-fast，已回退为始终创建（`d67c7ab`）。A3 试点实测 compact 层成本 **约 4.4MB Private/WS 每窗口**（3 窗 172.4→185.5MB），但该收益暂缓发布；待定位显示路径空引用后重试 A2。分支 `refactor/performance-20261107`。

### P1-B：File 格子渲染窗口下移

- `RenderedItems` 初始 30 / 增长块 200 的机制保持；对 <300 项目录也启用窗口化（如 60 项），减少一次性 90 个 ItemSurface 类的全量物化。
- 图标解码维持现有预算与 LRU（实测使用率极低，无需扩）。

### P2：长会话 native 爬升治理

- 在 `MemorySample` 已有字段上加趋势告警（`privateMinusGcHeapEstimateMB` 连续上涨超阈值时触发一次全量重建或提示重启）。
- 继续用 gcdump 差分巡检托管侧；native 侧若持续膨胀，再定向排查 D2D/DWrite 位面缓存。

### P3（待评估）：核显 / 低功耗 GPU 优化

本机为 AMD Radeon 780M（核显）+ RTX 4060 Laptop（独显）双 GPU；安装版 `D:\APP\DeskBox\DeskBox.exe` 已在 Windows 图形设置中固定 `GpuPreference=1`（核显优先），临时 AOT 测试 exe 无该偏好时会落到独显。实测 AcrylicBase / Mica / Solid 三种材质（3 个可见 File 窗口、动画全关）：

| 材质 | Private | 应用 GPU 内存（dedicated/shared） | 3D 利用率 |
|---|---|---|---|
| AcrylicBase | 176.6MB | 36 / 18.3MB | 0% |
| Mica | 172.8MB | 40 / 18.2MB | 0% |
| Solid | 173.0MB | 50 / 18.3MB | 0% |

结论：**应用进程侧 Private/GPU 内存在材质间无有意义差异；亚克力模糊的合成开销在 DWM**（DWM 独显 1.28GB dedicated、核显 146MB dedicated + 753MB shared），应用进程 GPU 引擎计数看不到。每窗口约 14MB 合成表面由窗口尺寸决定，WinUI 不提供降采样接口。

待评估方案（未实现）：opt-in「核显/省电材质」——在电池/省电模式下把 Acrylic 自动降为 Mica 或 Solid 并关闭 rich backdrop 模糊；用 DWM GPU 计数 + 电池放电做 A/B，数据为正再决定默认值。GPU 偏好属于 Windows 按 exe 的系统设置，应用不应静默改写注册表；安装版已由用户设置。重构计划见 [performance-refactor-plan-20261107.md](performance-refactor-plan-20261107.md)。

### 不建议做的事

- ❌ 把多格子合并成单 widget（违反基础需求）。
- ❌ 大幅削减 IconHelper 三缓存预算（实测使用 <2MB，削减无收益）。
- ❌ 强制 GC 作为常规手段（托管堆只占 10-42MB，收益极小）。
- ⭕ 工作集裁减保留默认关闭即可——P0 落地后它只剩装饰性收益。

## 预期总账（日常「几个胶囊常驻」场景）

| 状态 | 现状 (AOT) | P0 落地后（估） |
|---|---|---|
| 9-12 格子全展开 | ~288MB | ~250-270MB（P1 树瘦身） |
| 9-12 格子全折叠 | ~271MB | **~130-160MB** |
| 1-2 格子展开 + 其余折叠 | ~250MB | **~150-180MB** |
| Debug 构建同上 | +120-160MB | 建议日常用 AOT 零售构建 |

核心判断：**代码没有严重 bug 或泄漏，但「折叠不释放、隐藏回收被 Visible 锁死」两个设计决策叠加，让多格子架构的每窗口成本在用户最常见的胶囊常驻场景下全额驻留**——这是 300MB 体感的直接来源。P0-A/B 落地即可治本，且完全保持多格子架构。

## 后续进展（2026-11-07 补记）

### P0 已落地

- `1f52f9c`：设置页托管存储检查移出 UI 线程。
- `d9ab66e`：`AllRecreatable` 深度清理阶段释放长隐藏窗口的内容树（`ReleaseLongHiddenInactiveContent`，含 `WidgetShellContentHost` 释放与视觉活动契约测试）。
- AOT 零售实测（ResourceSaver、3 个 File 窗口：1 组 + 2 独立）：隐藏清理后 Private ~189MB、**WS 降至 ~52MB**；1.6.0 AOT 主进程 Private 已比 1.5.9 低（186 vs 225MB）。

### A2：GPU/DirectComposition trace 确认残留块归因

提权抓取 GPU + DesktopComposition + VirtualAllocation + XAMLActivity trace（约 3.36GB ETL，已清理），交叉确认隐藏释放后仍驻留的三个 ~14–15MB 私有块：

- `Microsoft-Windows-Direct3D11`：DeskBox 进程创建 **1920×1920×4B（≈14.1MB）、1904×1904×4B（≈13.8MB）、1902×1902×4B（≈13.8MB）** 的 Texture2D，尺寸/数量与残留私有块吻合。
- `Microsoft-Windows-XAML`：1904×1904 的 `SystemMemorySurfaceAllocate` 有对应 `SystemMemorySurfaceFree`（随内容树释放）。
- DComp `CAP_RELEASE_SURFACE` 无 14MB 级表面（最大 1024×1024）。

**结论：残留块是窗口级 D3D11/Composition 纹理，不是内容树或普通 native heap；内容树释放无法回收它们。**

### B1：长隐藏独立 File 窗口 Cold 关闭试点——负收益，已回滚

试点内容：`AllRecreatable` 深度清理阶段关闭完全隐藏、非组的独立 File 窗口（`_fileWidgets`），唤出走现有重建路径。Debug 口径功能验证通过（冷关闭 2 窗、托盘唤出重建成功、全量测试绿），但 **AOT 零售口径为负收益**：

| 口径 | Private(MB) | WS(MB) |
|---|---|---|
| B1 初始（3 窗口） | 187.7 | 248.6 |
| B1 隐藏 60s 冷关闭 2 窗后（30s 稳态） | **189.4（不降）** | 74.8 |
| 仅 P0（同场景对照） | 189.3 | **52.1** |

原因：关窗确实释放了 composition 对象（`ownedCompositionReleased=2`），但 Windows heap/D3D 分配器保留提交内存，native commit 没有还给 OS；WS 反而比仅 P0 更差，且引入滚动/导航状态丢失与窗口重建风险。

**处置：B1 两处改动（`App.xaml.cs`、`WidgetManager.Memory.cs`）已回滚，工作区回到 `d9ab66e`；不建议沿此路径做 B2（连组窗口销毁），证据不支持继续 Cold。**

若仍要压 Private commit，需换层级：进程级隔离/重启回收，或深入 WinUI/D3D 分配器治理，须单独设计。当前确定收益 = 设置页修复 + P0（隐藏态 WS ~52MB）。

### P0-C：空闲进程重启试点——稳态零收益（2026-11-07）

思路：全部格子隐藏且连续空闲达到阈值后，提前安排进程重启，把 WinUI/D3D 保留的 native commit 还给 OS。实现包含：性能设置开关（默认关闭）、后台清理新增 `IdleProcessRecycle` 阶段、更新下载/Private 下限（150MB）/6 小时间隔三重阳门、重启走 `AppRelaunchService` 并带 `--startup` 保持无人值守启动。

AOT 零售实测（11 格子真实配置，3 个 File 窗口隐藏，阈值 60s，重启后观察 75s 稳态）：

| 口径 | 重启前 Private | 重启后 Private | 结论 |
|---|---|---|---|
| ResourceSaver / AllRecreatable | 167.3MB（WS 33MB） | 167.5MB（WS 56MB） | 0 收益 |
| Custom / Warm（用户日常配置） | 171.6MB（WS 42MB） | 170.7MB（WS 39MB） | ≈0 收益 |

原因：重启后的新实例会立即恢复全部已启用格子（即使隐藏），内容树与窗口基线约 170MB；96MB 的「底座」是 0 格子状态，永远到不了。也就是说，**稳态隐藏内存 ≈ 新进程基线**，周期性重启没有可回收的增量；B1 的分配器保留结论在这里同样成立。

**附带的真实回归与修复**：`feat/160-kickoff`（#589）在启动排水路径无条件调用 `HandleExternalActivationAsync()`，其「无激活」分支会执行交互式兜底（恢复全部格子 + 打开设置）。实测 `--startup` 启动后所有格子被重新显示、设置进程被拉起，**静默启动完全失效**。已修复：启动排水在 `--startup` 场景跳过交互兜底；空闲重启的重启参数也显式带 `--startup`（updater 新增 `--app-arg` 透传）。

**判定（周期性重启单独）**：稳态零收益，不能只靠重启。

### P0-C 续：重启 + 懒恢复试点——有效（2026-11-07）

在重启的基础上增加「懒恢复」：`--lazy-restore` 启动时只做配置/拓扑归一化，**不创建任何隐藏格子窗口**；首次显式唤出（托盘/热键/桌面双击）走已有的 `PrepareWidgetForBatchShowAsync` 按配置创建窗口。AOT 零售实测（真实 11 格子配置、Warm、3 个 File 窗口、阈值 60s）：

| 阶段 | Private | WS |
|---|---|---|
| 懒恢复前（隐藏稳态） | 168.6MB | 36MB |
| 重启后（零窗口懒恢复） | **97.5–100.2MB** | 140–146MB |
| 首次托盘唤出后 | 174MB | 240MB→裁剪后 4–6MB |

- **空闲 Private 净省约 68MB**（168.6 → 100.2MB），窗口在首次唤出时创建，日志确认 `Raise prepared` + `RestoreWidgetPositions` + `LoadFolderContents` 全部执行。
- 懒恢复实例 Private < 150MB 下限，不会再次触发回收（无重启循环）；6 小时间隔闸门继续防止频繁重启。
- 代价：回收后的**首次**唤出多一次窗口创建/内容加载（实测 ~0.3–1s 量级），之后与正常实例一致。
- 启动激活回归修复（上节）是懒恢复可用的前提：重启实例必须走无人值守启动，不能弹设置/强制显示。

**结论：保留「空闲重启 + 懒恢复」，默认关闭（用户在性能设置中开启）；周期性重启单独使用没有意义。**
