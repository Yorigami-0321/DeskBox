# 性能优化第二轮计划（2026-11-08）

本文承接 [性能重构计划（2026-11-07）](performance-refactor-plan-20261107.md) 与 [内存占用归因与优化方案（2026-10-27）](memory-attribution-optimization-20261027.md)，登记本轮代码审查确认的剩余优化项、实施顺序、验收口径与暂缓项。分支 `refactor/performance`（原 `refactor/performance-20261107` 已去掉日期）。

本轮不重复已证伪或已否决的路径：A2 Expanded 懒创建 compact 层（AOT `Microsoft.UI.Windowing` fail-fast 0xE0464645）、P1-B 降低渲染窗口阈值、B2 自动降材质、Cold 关窗、单独周期重启、强制 GC、合并多格子为单窗口。

## 1. 结论速览

| 顺序 | 项目 | 根因 | 预期收益 | 风险 | 状态 |
|---|---|---|---|---|---|
| R1 | 备份调度顺序与线程归属 | 每分钟先全盘枚举/写探针，最后才判到期；归档管线同步段落在 UI 线程 | 消除每分钟隐藏 I/O，消除备份日 UI 卡顿 | 低 | 已实现（测试绿） |
| R2 | 启动显示沉降等待条件 | 当前屏集是上次真子集就固定等最多 5s，不检查缺失屏是否承载格子 | 拔屏/未停靠冷启动少等最多 5s | 低 | 已实现（测试绿） |
| R3 | 大目录水合域拆分 | 文件堆叠默认开启，`HydrationUniverseItems` 退回全部 `Items` | 1000+ 项目录不再全量取图标/计数/快捷方式 | 中 | 已实现（测试绿） |
| R4 | 静默启动懒建窗 | 启动仍全量创建隐藏窗口与 compact chrome | 静默启动恢复段约 1.0–1.2s | 中（产品取舍） | 待决策，暂不执行 |
| — | 搜索、动画、排序、图标路径、设置锁等 | 见 §6 | 中—小 | 中 | 候选/暂缓 |

## 2. R1：备份调度顺序与线程归属

### 2.1 现状与证据

- [DispatcherBackupTimer.cs:15](../src/DeskBox/Services/DispatcherBackupTimer.cs#L15) 每 1 分钟在 dispatcher 上触发一次 tick。
- `BackupRuntime.OnTick → CheckScheduleAsync → ExecuteAsync → work()` 直接调用 `BackupBackend.CreateSnapshotAsync`，没有 `Task.Run`；在 gate 无竞争时 `await _gate.WaitAsync` 同步完成，后续同步段落在 UI 线程执行。
- [DeskBoxDataBackupService.cs:294](../src/DeskBox/Services/DeskBoxDataBackupService.cs#L294) 在到期判断之前执行 `HasBackupSourceData()`，后者对数据目录做 `AllDirectories` 递归枚举（[:2484](../src/DeskBox/Services/DeskBoxDataBackupService.cs#L2484)）。
- 随后 [DeskBoxDataBackupService.cs:299](../src/DeskBox/Services/DeskBoxDataBackupService.cs#L299) 以 `probeWriteAccess: true` 解析目标目录，会 `Directory.CreateDirectory` 并写入/删除探针文件；到期判断直到 [:305](../src/DeskBox/Services/DeskBoxDataBackupService.cs#L305) 才发生。
- 注意：`HasBackupSourceData` 使用 `.Any`，遇到第一个可备份文件（通常是 `settings.json`）即短路；旧实现每分钟的成本主要是目录枚举调用 + 写探针，不是全树遍历。R1 仍去掉这两项，但收益量级应小于“全盘扫描”的直觉估算。
- 到期后 `CreateArchiveCoreAsync` 先全量复制到 staging（[:2509](../src/DeskBox/Services/DeskBoxDataBackupService.cs#L2509)），再压缩并逐文件 SHA-256（[:2934](../src/DeskBox/Services/DeskBoxDataBackupService.cs#L2934)、[:3358](../src/DeskBox/Services/DeskBoxDataBackupService.cs#L3358)）；没有 `ConfigureAwait(false)`，续体回到调用方上下文。

结果：每天 1440 次 tick，绝大多数返回 `Skipped`，但每次都已付出递归枚举与写探针；到期日的哈希/压缩同步段落落在 UI 线程。

### 2.2 设计

**A. 到期判断前置 + 内存到期时钟**

1. 用 `ResolveAutomaticSnapshotTarget(probeWriteAccess: false)`（已在 [:147](../src/DeskBox/Services/DeskBoxDataBackupService.cs#L147)、[:156](../src/DeskBox/Services/DeskBoxDataBackupService.cs#L156) 使用）做免探针解析。
2. 新增 `TryGetLatestAutomaticSnapshotTime`：目录不存在或枚举失败时返回“未知”，按到期处理；成功时返回最新 `DeskBox-Auto-*.zip` 的写入时间。
3. 本轮不做内存到期时钟：每次 tick 只做一次非递归目录枚举（`DeskBox-Auto-*.zip`），成本远小于递归源扫描与写探针，也避免外部新增/删除快照被缓存遮挡。若后续实测网络目录枚举显著，再单独加“最近快照时间”缓存。
4. 仅在到期时才执行 `HasBackupSourceData()` → `probeWriteAccess: true` → `CreateDirectory` → 归档。保留自定义目录不可用时的 fallback 语义：免探针枚举失败不直接失败，而是进入探测路径由现有逻辑回退；探针回退到默认目录后再复核一次该目录是否已有新快照。
5. `force`（“立即备份”）跳过间隔判断，但仍保留源数据检查与探针。

**B. 线程归属**

1. 在 `BackupBackend.CreateSnapshotAsync`（本地自动快照）边界包一层 `Task.Run`，把快照复制、压缩、哈希整体移出 UI 线程；`BackupRuntime.ExecuteAsync` 仍在 UI 上下文 `await`，完成通知/`Report` 不变。
2. 线程安全审计结论（已核对）：`DeskBoxDataBackupService` 不引用 dispatcher/settings UI；`App.Log` 是队列化线程安全；`App.MarkStartupProgress` 是 `Volatile.Write`；`AutomaticSnapshotFallbackDetected` 的订阅者 [App.xaml.cs:4276](../src/DeskBox/App.xaml.cs#L4276) 自身会 `TryEnqueue` 回 UI；`DesktopOrganizationTransaction.OperationGate` 与 `settingsWriteLock` 均为线程无关的同步原语。
3. 不同步改云备份路径；云路径仍走原线程模型。

**C. 可观测性**

- `PerformanceLogger.Measure("DataBackup.SourceDataScan")`、`Measure("DataBackup.Archive")`。
- `Skipped` 日志补充 `reason=interval`、`latestAgeMinutes`、`sourceScan=false`，便于证明未触盘。
- 保留现有 `App.MarkStartupProgress` 分块心跳。

### 2.3 验收

- 单测：未到期路径不调用源数据扫描/写探针（以可注入的计数或测试 seam 证明）；到期路径仍生成归档；自定义目录回退、force、恢复暂停三条分支保持原语义。
- 手工：附件目录含 N 个文件时，连续观察 10 个 tick，日志显示 `reason=interval` 且无 `SourceDataScan`；备份日 UI 帧间隔与空闲基线同量级。
- 全量 x64 测试 passed == total。
- AOT 零售 + 隔离数据根：记录备份日总耗时、UI 最大空档、staging/zip 文件写入量（改造前/后）。

### 2.4 风险与回滚

- 风险：自定义目录在慢速网络共享上，首次枚举仍可能慢；内存到期时钟与外部新增快照不同步。
- 处置：首次枚举可放在同一后台任务；外部新增快照最多延迟一个间隔被发现，可接受。
- 回滚：恢复到期判断顺序或移除 `Task.Run` 边界即可，不涉及备份格式与恢复协议。

## 3. R2：启动显示沉降等待条件

### 3.1 现状与证据

[App.xaml.cs:2225](../src/DeskBox/App.xaml.cs#L2225) 的 `WaitForStartupDisplaySettlingAsync` 在满足以下条件时最多等待 5 秒（500ms 步进）：

```csharp
IReadOnlyList<string> active = DisplayIdentityTokens.ActiveProfileSet(SettingsService.Settings);
...
if (!DisplayIdentityTokens.IsTrueSubset(current, active)) break;
await Task.Delay(500);
```

`IsTrueSubset` 只比较当前屏集是否为上次活动档案的真子集，不判断缺失屏上是否有任何持久化格子。拔掉扩展屏后启动、或扩展屏尚未被系统枚举时，会在建窗之前白等最多 5 秒。

### 3.2 设计

1. 新增纯策略 `StartupDisplaySettlingPolicy.ShouldWait(activeTokens, currentTokens, usedTokens)`：
   - `activeTokens` 为空 → 不等待（保持现状）。
   - `currentTokens` 不是 `activeTokens` 的真子集 → 不等待。
   - 缺失 token 中没有任何一个出现在 `usedTokens` → 不等待。
   - 缺失 token 被使用 → 等待，保持现有 5s 上限。
2. `usedTokens` 来源：
   - 已启用 `WidgetConfig` 与 `WidgetGroupConfig` 的 `PositionMonitorStableId`；
   - 活动拓扑档案 `WidgetTopologyLayoutProfile.Surfaces[*].PositionMonitorStableId`；
   - 统一用 `DisplayIdentityTokens.TokenFor` 的规则归一（稳定 ID 大写；退化身份走 `geo:WxH`）。
3. 退化身份（`DisplayPlacementResolver.IsDegenerateIdentity` 为真、没有稳定 ID）无法从配置还原宽高时，保守返回“等待”；本轮不引入按 `PositionMonitorDeviceName` 的模糊匹配。
4. 保持 500ms 轮询与 5s 上限；不新增唤醒源。
5. 纯策略单测覆盖：无活动档案、真子集但缺失屏无格子、缺失屏有格子、当前集为空、退化 token。

### 3.3 验收

- 拔掉扩展屏后启动：日志中 `display-startup-settling` 步骤时长为 0（或不再出现），格子落在当前屏。
- 停靠场景：缺失屏上有格子时仍等待并在屏幕出现后恢复。
- 全量 x64 测试 passed == total。

### 3.4 风险与回滚

- 风险：`PositionMonitorStableId` 在旧布局中可能为空，导致本可跳过的等待仍保留；这是保守方向，不会引入错位。
- 回滚：策略返回 `true` 即恢复原行为。

## 4. R3：大目录水合域拆分

### 4.1 现状与证据

- `FileStacksEnabled` 默认 `true`（[FileWidgetSettingsSlice.cs:45](../src/DeskBox/Models/FileWidgetSettingsSlice.cs#L45)）。
- `UsesStackProjection => FileStacksEnabled`（[WidgetViewModel.Stacks.cs:43](../src/DeskBox/ViewModels/WidgetViewModel.Stacks.cs#L43)）。
- [WidgetViewModel.Windowing.cs:79](../src/DeskBox/ViewModels/WidgetViewModel.Windowing.cs#L79)：

```csharp
internal IEnumerable<WidgetItem> HydrationUniverseItems => UsesStackProjection
    ? Items
    : RenderedItems;
```

- 四类水合全部使用该集合：图标（[ItemHydration.cs:556](../src/DeskBox/ViewModels/WidgetViewModel.ItemHydration.cs#L556)）、文件夹子项计数（[:685](../src/DeskBox/ViewModels/WidgetViewModel.ItemHydration.cs#L685)）、快捷方式目标（[:745](../src/DeskBox/ViewModels/WidgetViewModel.ItemHydration.cs#L745)）、ShellKind（[:791](../src/DeskBox/ViewModels/WidgetViewModel.ItemHydration.cs#L791)）。

渲染窗口的设计意图（[WidgetViewModel.Windowing.cs:8](../src/DeskBox/ViewModels/WidgetViewModel.Windowing.cs#L8)）是“大于阈值只渲染前缀，元数据水合同步跟随前缀”。默认堆叠开启后，该意图只对 XAML 容器生效，元数据/图标仍全量水合。

### 4.2 设计

1. 保留“ShellKind 全量”：堆叠分组读取每个成员的 `ShellKind`，这条水合继续使用全部 `Items`（`HydrationShellKindItems`）。
2. 新增“显示域”：`HydrationDisplayedItems` = `ComputeDisplayedIconKeepSet(RenderedItems)`（[ItemHydration.cs:470](../src/DeskBox/ViewModels/WidgetViewModel.ItemHydration.cs#L470)），即渲染前缀 + 每个折叠堆叠的前 4 个预览成员；剔除堆叠包装项本身（包装项渲染成员图标，自身没有可水合图标）。
3. 图标、文件夹子项计数改用 `HydrationDisplayedItems`；快捷方式目标在堆叠开启时保持全量（ShellKind 按目标分类，未水合 TargetPath 的 .lnk 会被按链接本身归类），堆叠关闭时跟随显示域。
4. `HealReleasedItemIconsIfIdle`（[:499](../src/DeskBox/ViewModels/WidgetViewModel.ItemHydration.cs#L499)）改为只扫描显示域，避免未渲染项的空图标触发全量水合。若当前已有 hydration pass 在跑，记录一次 follow-up，在 pass 结束时补一轮，避免“显示集在 pass 快照之后变化”导致漏补水合。
5. `HydrateIconsWithRetryAsync` 的重试退出条件（[:535](../src/DeskBox/ViewModels/WidgetViewModel.ItemHydration.cs#L535)）从“全部 `Items` 都有图标”改为“显示域都有图标”。
6. 渲染窗口增长路径已调用 `StartItemHydration()`（`EnsureRenderWindowCoversViewport`、`GrowRenderWindow`、`EnsureItemRendered`），新进入前缀的项会被增量补水合；无需新增触发源。
7. 堆叠弹层打开时调用 `SetHydrationPinnedItems(_stackPopoverMembers)`，把弹层一次展示的全部成员加入显示域；关闭时清空。弹层成员（尤其第 5 个及以后）不能只靠前 4 个预览成员覆盖。
8. 堆叠内联展开时，展开成员进入 `_stackDisplayItems` 并由渲染前缀/增长路径覆盖；这是 R3 的关键验收项。
9. 本轮不动 `ShellKind` 的获取方式；若后续实测其全量成本显著，再单独评估按扩展名/属性预分组或延迟到堆叠创建时。

### 4.3 验收

- 行为单测：>300 项目录、堆叠开启、`RenderedItems=30` 时，图标/计数/快捷方式请求集合等于“30 + 预览成员”，不等于全部项目。
- 现有 `RenderWindowBehaviorTests`、`RenderWindowContractTests`、`RenderWindowCoverageContractTests` 全绿。
- 堆叠分组正确性：ShellKind 全量水合后分组结果与改造前一致；展开堆叠后成员图标不为空。
- 全量 x64 测试 passed == total。
- AOT 零售 + 隔离数据根，1000+ 项目录：记录首屏时间、图标请求数、ShellKind 请求数、Private/WS；与改造前同场景对比。
- 小目录（≤300）行为与指标不回退。

### 4.4 风险与回滚

- 风险：堆叠预览成员数量 (`StackPreviewMemberKeepCount`) 与 XAML 绑定不一致会导致预览图标缺失；展开成员未进入显示域会导致空图标。
- 处置：把预览成员数量收敛为一个共享常量/纯函数，并在行为测试中固定。
- 回滚：恢复 `HydrationUniverseItems` 的单集合实现，保留新测试作为回归门禁。

## 5. R4：静默启动懒建窗（待决策）

### 5.1 现状与证据

- [WidgetManager.cs:941](../src/DeskBox/Services/WidgetManager.cs#L941) 在启动恢复前决定 `hideWidgetsAtStartup`，随后仍以 `keepPreparedForAnimation: true` 创建全部窗口并隐藏。
- 真正的懒恢复分支在 [WidgetManager.cs:913](../src/DeskBox/Services/WidgetManager.cs#L913)，目前只用于空闲回收重启的 `--lazy-restore`。
- 本机生产日志：三次 `OnLaunched` 为 0.34–2.0s，最慢单步均为 `tray-icon` 234–343ms；恢复段在历史测量中约 1.0–1.2s。

### 5.2 设计（若采纳）

1. 对“静默启动且策略判定应隐藏”的会话复用 `--lazy-restore` 的窗口懒创建：只做配置/拓扑归一化，不创建隐藏窗口；首次显式唤出走 `PrepareWidgetForBatchShowAsync`。
2. 可选折中：启动后空闲 N 秒再后台建窗，兼顾“首次唤出延迟”和“启动内存峰值”。
3. 不改变正常可见启动路径；不影响 A2 的 compact 层决策。

### 5.3 待确认

- 是否接受首次唤出多一次窗口创建（既有试点约 0.25–0.3s）？
- 若接受，选“立即懒创建”还是“空闲后预创建”？

## 6. 候选与暂缓项

以下项已核实，但本轮不做；保留证据供后续轮次选择。

| 项 | 证据 | 说明 |
|---|---|---|
| 搜索 Everything 等待 50ms 轮询 | `EverythingSearchService.cs:445` | 每键查询在未就绪时最多睡 50ms；可降 5–10ms 或改事件 |
| 搜索前 40 条重复 stat | `SearchPopupViewModel.cs:626`、`FileMetaService.cs:69` | Everything 已返回 size/mtime，实际只缺 CreatedAt/图标 |
| 搜索快照每秒重读 | `SearchEngineService.cs:15`、`:518` | 每秒重读 Todo JSON + 克隆 QuickCapture；可加 dirty/mtime 缓存 |
| 图标路径逐段 junction 解析 | `FileService.cs:984`、`FileService.PathResolution.cs:99` | 缓存命中也要付每段 `File.GetAttributes`；可缓存 resolvedPath |
| 全量刷新排序 O(N²) | `WidgetViewModel.SortingAndWatchers.cs:250` | `Items.IndexOf` + 每项 `Move`；增量 watcher 路径已较好 |
| 动画帧预算每帧重算 | `WidgetCompactAnimationCoordinator.cs:521`、`WidgetTrayBatchAnimationDriver.cs:267` | 每帧 MonitorFromWindow；可按动画开始缓存 |
| 过渡关键帧 12/24/24 | `WidgetShell.xaml.cs:2215`、`:2238`、`:2260` | 可用 cubic-bezier/更少采样替代 |
| DragOver 每事件 LINQ + 两次入队 | `ContentWidgetWindow.NativeDragDrop.cs:844` | 可按帧合并最新样本 |
| 设置全局锁 + 全图序列化 | `SettingsService.cs:513`、`:1640` | copy-on-write 收益中等但面大 |
| QuickCapture 全表规范化 | `QuickCaptureStore.cs:62` | 每次保存 GroupBy/OrderBy；Items 无上限 |
| ShellKind 全量水合 | AOT 两轮 `releasedShellKindEntries=1200` | 堆叠分组按目标分类需要全部 ShellKind；是下一项大目录成本，可评估扩展名/属性预分组 |
| `automatic-snapshot` 步骤耗时归因混淆 | `App.xaml.cs:1298` 步骤内 `await settingsLoadTask`；Debug 冒烟步骤 688ms，而 `DataBackup.DueCheck` 仅 1.0ms | 688ms 主要是设置加载等待，不是备份 I/O；步骤名/日志会误导后续优化。真实备份成本应在 due 日 + 大附件场景单独测 |

## 7. 测试与测量协议

- 测试命令（仓库约束）：`dotnet test .\tests\DeskBox.Tests\DeskBox.Tests.csproj --no-restore --verbosity:minimal -p:Platform=x64`；需要架构资产时补 `-p:RuntimeIdentifier=win-x64`。只有 passed == total 才算绿。
- Debug 只做功能冒烟；性能结论用 AOT 零售（`scripts/publish-aot-retail.ps1`，x64，Rust native）+ 隔离数据根 + 真实布局副本。
- 内存口径沿用既有协议：Private 为主，WS 参考；固定材质与动画变量；3 窗与 9–12 窗两档。
- 日志标记：`[Startup]`、`[DataBackup]`、`[FolderLoad]`、`[TrayAnimation]`、`[CompactAnimation]`；R1/R2/R3 各自新增结构化计数后再比较。
- 每阶段结束后复测同一场景，负收益或高风险回归按阶段回滚并记录。

## 8. 参考

- [Windows app performance and fundamentals overview - Microsoft Learn](https://learn.microsoft.com/en-us/windows/apps/develop/performance/)：测量基线 → 推迟非必要启动工作 → 优化 XAML 加载 → 保持 UI 线程响应 → ListView/GridView 虚拟化 → 复测与回归门禁。
- [Files - GitHub](https://github.com/files-community/Files)：WinUI 3 文件管理器，活跃维护，可作为大目录渲染/水合的同类参考实现。
- [PowerToys Run - Microsoft Learn](https://learn.microsoft.com/en-us/windows/powertoys/run)：输入平滑延迟、插件等待时间的可调设计，可作为搜索防抖/等待策略的参考。
- 工具：PerfView、Windows Performance Analyzer、SizeBench。

## 9. 明确不做

- 不重试 A2 Expanded 懒创建，除非先解释并消除 AOT `Microsoft.UI.Windowing` fail-fast。
- 不降低渲染窗口阈值，不削减图标缓存预算，不强制 GC，不合并多格子为单窗口。
- 不默认改变用户的折叠行为/材质，不静默改写系统 GPU 偏好。
- 不在本轮改设置持久化协议与云备份线程模型。

## 10. 本轮实施记录（2026-11-08）

- **R1**：`CreateAutomaticSnapshotResultAsync` 到期判断前置；新增 `IsAutomaticSnapshotFresh` 非递归目录枚举；`HasBackupSourceData` / `CreateArchiveCoreAsync` 加性能标记；`BackupBackend.CreateSnapshotAsync` 以 `Task.Run` 把本地自动快照移出 UI 线程；新增 `SourceDataScanCount` 供测试证明未到期不扫源。备份/运行时测试 43/43。
- **R2**：新增 `StartupDisplaySettlingPolicy`（纯等待策略 + 使用集合收集）；`App.WaitForStartupDisplaySettlingAsync` 改为“缺失屏承载格子才等待”，固定绑定与 FollowPrimary 计入使用集合，退化身份保守等待。策略测试 8/8。
- **R3**：拆分 `HydrationDisplayedItems`（渲染前缀 + 堆叠预览 + 弹层固定成员，剔除堆叠包装项）、`HydrationShortcutTargetItems` 与 `HydrationShellKindItems`（后两者在堆叠开启时保持全量，保证快捷方式按目标分类）；hydration pass 增加 follow-up 补跑；堆叠弹层打开注入、关闭清除 pinned 成员。渲染窗口/图标契约测试 39/39。
- 全量 x64：**5919/5919** 通过。
- Debug 冒烟（隔离数据根、`--startup`、`DESKBOX_PERF_LOG=1`）：`OnLaunched completed successfully`；`[Startup] Pipeline: 42 steps (5 critical), 0 degraded, 0 failed`；`[Perf] DataBackup.DueCheck elapsedMs=1.0`，无 `SourceDataScan`、未创建新快照。最慢单步 `automatic-snapshot` 688ms，但该步骤内部先 `await settingsLoadTask`，耗时主要是设置加载等待，不是备份 I/O（见 §6）。
- AOT 零售复测见 §11。

## 11. AOT 零售 A/B 复测（2026-11-08）

口径：基线 `357b3cc` AOT（141MB）vs 本轮 `a14e7be` AOT；同一 1200 文件目录、同一单 File 格子布局（自动堆叠关闭、堆叠开关开启）、各自隔离数据根（预置新鲜备份快照以跳过归档）、`--startup` 隐藏启动、`DESKBOX_PERF_LOG=1`；每边两轮。

| 指标 | 基线 `357b3cc` | 本轮 `a14e7be` |
|---|---|---|
| 大目录 `IconHelper.GetIcon` 请求 | 1216 / 1216 | 72 / 64 |
| `decodedBitmapLookupHits` | 1202 / 1206 | 69 / 59 |
| `releasedShellKindEntries` | 1200 / 1200 | 1200 / 1200 |
| `OnLaunched` | 956.1 / 758.4ms | 696.7 / 700.1ms |
| 30s Private（soft cleanup） | 178.0 / 169.7MB | 156.9 / 172.1MB |
| `DataBackup.DueCheck` | 无该标记 | 0.4 / 0.2ms |
| `DataBackup.SourceDataScan` | 无该标记 | 0 / 0 |

结论：

- **R3 图标水合下降约 94%**（1216 → 64–72），两轮一致；`decodedBitmap` 查询同步下降。`ShellKind` 仍为 1200（设计保留），是下一项大目录成本的明确候选。
- **R1 的结构性目标达成**：到期检查 0.2–0.4ms，未触发源扫描/写探针。但 `.Any` 短路意味着旧实现的每分钟源扫描本来较小；R1 的主要收益是去掉写探针与扫描调用，实测没有可归因的大幅启动/CPU 下降。
- `OnLaunched` 本轮两轮均低于基线约 58–259ms，但单机、单场景、两轮方差大，不能全部归因于本轮改动，不作为承诺。
- 30s Private 两轮区间重叠（基线 169.7–178.0 vs 本轮 156.9–172.1），本场景没有稳定内存差；图标请求下降主要降低瞬时工作与分配，不足以在 30s 稳定态形成可重复的 Private 下降。
- **R2 未在 AOT 侧复现**：本机双屏均在位，不满足“当前屏集是上次真子集”的触发条件；由 8 项策略单测覆盖。

## 12. R5：ShellKind 按需水合（2026-11-08）

### 12.1 证据

- AOT 两轮 `releasedShellKindEntries=1200`（1200 文件、`fileStackAutoStacking=false`）。
- `RebuildStackDisplayItems` 只在 `FileStackAutoStacking` 为真时调用 `WidgetStackGroupingService.Group`；关闭时所有项走 `Loose` 分组，`ShellKind` 没有消费者。
- 自动堆叠开启时，`ResolveCategory` 先看文件夹/ShellKind，未命中再按扩展名集合回退；因此常见类型并不完全依赖 ShellKind。

### 12.2 设计

1. 新增 `FileStackShellKindHydrationPolicy.RequiresFullUniverse(usesStackProjection, autoStacking)`：只有投影开启 **且** 自动堆叠开启时才需要全量 ShellKind。
2. `HydrationShellKindItems` 在该值为真时返回全部 `Items`，否则返回 `HydrationDisplayedItems`（渲染前缀 + 堆叠预览 + 弹层固定成员）。
3. `RebuildStackDisplayItems` 读取缓存的 `_fileStackAutoStacking`，避免重复读设置并保持与领域判断一致。
4. `ApplyStackSettings` 更新该缓存；从关闭→开启时调用 `StartItemHydration()` 补齐全量 ShellKind。
5. `SetShellKind` 只在“需要全量 ShellKind 且按 Kind 分组”时触发堆叠重建，避免关闭自动堆叠时每个 ShellKind 结果都排队重建。
6. 不改快捷方式目标域与自动堆叠开启时的全量 ShellKind；按扩展名预分组留作后续候选。

### 12.3 验收

- 策略单测覆盖 4 种组合。
- AOT 1200 文件、自动堆叠关闭：`releasedShellKindEntries` 从 1200 降到显示域量级（约 30）；大目录图标请求保持 64–72。
- 自动堆叠开启：分组结果与改造前一致，ShellKind 仍全量。
- 运行时开关关闭→开启：ShellKind 补齐，分组不缺失。
- 全量 x64 passed == total。

### 12.4 风险

- 手动堆叠不读取 ShellKind，但仍需在验收中确认；若未来有 UI 直接消费 ShellKind，需要重新评估显示域。
- 运行时切换依赖 `ApplyStackSettings` 的调用链；已确认 `SettingsChanged → ApplySettingsChangesAsync → ApplyStackSettings`。
