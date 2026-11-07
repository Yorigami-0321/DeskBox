# 性能重构计划（2026-11-07）

本计划承接 [内存归因与优化方案](memory-attribution-optimization-20261027.md) 的剩余项，目标是把已证明可行但结构受限的优化落到可维护的实现上，并为核显/低功耗场景建立可测量的优化路径。本文只定范围、设计、阶段与验收口径，不含工时/排期。

## 1. 当前基线与已落地改动

已提交：

| 提交 | 内容 | 实测 |
|---|---|---|
| `1f52f9c` | 设置页托管存储检查移出 UI 线程 | 设置页不再被磁盘/网络查询阻塞 |
| `d9ab66e` | AllRecreatable 下长隐藏释放内容树（P0-B） | 隐藏态 WS 270.9 → 52.1MB |
| `b472b5f` | 空闲重启 + 懒恢复（P0-C） | 空闲 Private 168.6 → 97.5–100.2MB（opt-in） |
| `b472b5f` | 启动激活排水不再跑交互兜底 | `--startup` 不再拉起设置宿主/强制显示格子 |
| `0b8a69f` | 胶囊内容睡眠（P0-A） | 3 胶囊 Private 189.3 → 167.6MB、WS 115 → 50MB |

部署现状：`D:\APP\DeskBox` 已覆盖为当前 AOT 构建；设置为 ResourceSaver + AllRecreatable + 空闲重启开启（30 分钟）；折叠行为仍为 Expanded（无胶囊），材质 AcrylicBase。

## 2. 工作流 A：compact 层抽取（P1-A）

### 2.1 目标

Expanded（永不折叠）窗口不再创建 `CollapsedChromeLayer`（125 个元素、73 个命名元素、占 WidgetShell XAML 的 62%），降低每个常驻窗口的固定 XAML 开销；胶囊窗口的行为、外观与延迟保持不变。

### 2.2 已验证的不可行路径

- `x:Load="{x:Bind ...}"` 绑定：`WidgetShell` 构造抛 `XamlParseException`。
- 同文件 `DataTemplate` + `FindName` 回填：同样在 `WidgetShell` 构造时抛 `XamlParseException`（模板内 17 个事件处理器、6 个 shell 属性 x:Bind 与自引用冲突）。

结论：必须把紧凑层及其代码一起移到独立控件，不能只延迟 XAML 子树。

### 2.3 设计

1. 新增 `WidgetCompactChrome : UserControl`，承载 `CollapsedChromeLayer` 整棵子树与对应的 compact 代码（预计迁移 `WidgetShell.xaml.cs` 中约 1.5k 行、123 个 compact 符号的引用）。
2. `WidgetShell` 保留一个布局占位 `CompactChromeHost` 和 `WidgetCompactChrome?`：
   - `SetCompactChromeEnabled(bool)`：Expanded → 不创建；Click/Smart → 创建并注入。
   - 壳只保留布局、内容宿主、标题栏和跨域状态；compact 交互全部由新控件负责。
3. API 边界：
   - 输入：`SetPresentation(WidgetCompactPresentation)`、`SetCollapsed(bool, contentMode)`、`SetInteractionMode(bool smart)`、`SetReorderEnabled(bool)`、`ApplyTheme/Size`、标题图标六项属性（当前 6 个 x:Bind 的 `TitleGlyph/TitleIconKind/TitleIconAccentColor/...`）。
   - 输出：17 个 XAML 处理器与壳级 compact 事件（PointerEntered/Moved/Exited/Pressed、Expansion/Action/MoveHandle、Drag/Drop、Previous/PrimaryAction/PlayPause/Next、CollapseRequested/ExpandRequested）由新控件以事件形式暴露，`WidgetShell`/窗口基类按原订阅关系转发。
4. 懒创建只由窗口基类的 `SupportsCompactChrome`（`EffectiveCollapseBehavior != Expanded`）驱动；运行时切换折叠行为时创建/销毁并重放当前 presentation。
5. 所有 compact 入口在控件不存在时做空守卫；Expanded 窗口的 init/主题/尺寸/内容切换路径不得触碰 compact 字段。

### 2.4 阶段与验收

| 阶段 | 内容 | 验收 |
|---|---|---|
| A1 | 抽出 UserControl，所有窗口仍创建（行为不变） | 全量测试绿；胶囊/悬停/拖拽/组切换/主题切换冒烟通过 |
| A2 | Expanded 窗口不创建 compact 层（守卫 + 懒创建） | Expanded 窗口视觉树无 compact 层；Click/Smart 胶囊正常；无空引用 |
| A3 | 运行时折叠行为切换 + 测量 | Expanded/Click/Smart 互切正常；AOT 每 Expanded 窗口 Private 下降（目标 2–3MB/窗，以实测为准）；首次胶囊 hover/expand 延迟不回归 |

**A1/A2/A3 结果（2026-11-07，分支 `refactor/performance-20261107`）**：

- A1 `2dafd36`：`WidgetCompactChrome` 独立控件；A2 `b5847be`：仅可折叠窗口创建；`f245ca7` 修复 AOT 警告门禁的 CS8604。
- 隐藏启动：Expanded `compactCreated=0`、Click `compactCreated=3`，均无异常；全量 5904/5904 绿。
- AOT 零售实测（3 个 File 窗口、同内容、Custom/Warm、无清理干扰、隐藏启动 75s）：

| 变体 | Private | WS | compact 控件 |
|---|---|---|---|
| Expanded（不创建） | 172.4MB | 41.4MB | 0 |
| Click + 展开（创建 3 个） | 185.5MB | 54.5MB | 3 |

差值 **13.1MB / 3 窗 ≈ 4.4MB/窗**（Private 与 WS 同幅），超过原 2–3MB/窗目标。按 5 个常驻窗口估算约 22MB，9–12 窗约 40–52MB。运行时行为切换通过设置宿主 IPC 路径生效（外部直接改 settings.json 不触发主进程重载，不能作为验证手段）；启动路径已验证。

**A2 回退（2026-11-07，`d67c7ab`）**：AOT 零售构建下，Expanded 窗口在**显示/唤起**时（应用自身启动显示路径）于 `Microsoft.UI.Windowing.dll` fail-fast（0xE0464645）；强制始终创建 compact 控件（A1 行为）后显示正常。shell 的四个 XAML 公开 compact 访问器已改为空守卫，但仍有未定位的显示路径空引用。当前保留 A1 抽取（可维护性），**A2 懒创建与 4.4MB/窗 的收益暂缓发布**；待定位显示路径空引用后再重试 A2。A3 的测量数据保留为试点结论。

### 2.5 风险与回滚

- 风险：跨控件事件顺序、hover/drag 状态、组宿主、标题图标绑定、主题/尺寸回调、tooltip/本地化、现有源码契约测试。
- 回滚：在独立分支 `perf/p1a-compact-chrome` 分阶段提交；A2 若出现空引用或交互回归，回到 A1 的“始终创建”实现，保留控件抽取本身（可维护性收益），不保留懒创建。

## 3. 工作流 B：核显/低功耗 GPU 优化（P3）

### 3.1 已测事实（本机）

- 双 GPU：AMD Radeon 780M（核显，驱动 1920×1280 显示）+ RTX 4060 Laptop（独显，驱动 2560×1440 主屏）。
- 安装版 `D:\APP\DeskBox\DeskBox.exe` 已在 Windows 图形设置中固定 `GpuPreference=1`（核显优先）；无该偏好的临时 AOT exe 会落到独显。
- AcrylicBase / Mica / Solid 三种材质（3 个可见 File 窗口、动画全关）：应用侧 Private 176.6 / 172.8 / 173.0MB，GPU 内存 36/40/50MB dedicated + ~18MB shared，3D 利用率 0%；材质差异在应用进程侧不显著。
- 亚克力模糊的合成开销在 DWM（DWM 独显 1.28GB dedicated；核显 146MB dedicated + 753MB shared）；每窗口约 14MB 合成表面由窗口尺寸决定，WinUI 不提供降采样接口。

### 3.2 设计

1. **B1 测量**：在 iGPU 路径上做 DWM 级 A/B（Acrylic vs Mica vs Solid），记录 `GPUEngine`/`GPUProcessMemory`（含 dwm）、电池放电/能耗、视觉差异；测试 exe 若需核显偏好，先征得同意再写 HKCU `UserGpuPreferences`。
2. **B2 低功耗材质模式**（opt-in，默认关闭）：检测**当前实际使用的 GPU**（DXGI 活动适配器/UMA 核显特征；不以电源状态代替），在核显路径上把 Acrylic/AcrylicBase 降为 Mica 或 Solid，切回独显后恢复用户选择；不修改用户持久化的材质设置，不静默改写系统 GPU 偏好。检测不到时保持用户原设置（不猜测）。
3. **B3 引导而非代写**：对便携/新装场景，提供跳转 Windows 图形设置的入口与说明（GPU 偏好是 Windows 按 exe 的系统设置）。
4. 不做：强行降采样窗口合成表面（无公开 API）、关闭 Mica 以外不相关的视觉效果、默认改变用户材质。

### 3.3 阶段与验收

| 阶段 | 内容 | 验收 |
|---|---|---|
| B1 | DWM/电池 A/B 测量 | 给出三种材质的 DWM GPU 内存/利用率/能耗差值与置信说明；确认是否值得做 B2 |
| B2 | 低功耗材质模式 | 核显路径上材质按预期变化、切回独显恢复；Private 不回退；设置可关闭；无闪烁/重建异常 |
| B3 | GPU 偏好引导 | 设置页入口可用；不写注册表；文档说明核显/独显选择的影响 |

**B1 结果（2026-11-07，安装版 AOT，GpuPreference=1，主屏空闲）**：

- 应用 GPU LUID 为 `0x000138AF`（核显）+ `0x00016100`（虚拟显示），确认走核显；3 个 Expanded File 窗口；每材质采样 6×2s。
- App Private：AcrylicBase 157.5MB、Mica 158.9MB、Solid 164.3MB（Solid 反而略高，属噪声/分配器差异）。
- DWM 核显（0x138AF）：三种材质均为 **ded=132.7MB / shared=826.8MB，完全一致**；DWM 3D 利用率 0/0.1%（静态空闲）。
- 结论：**静态空闲下材质的 DWM GPU 内存/利用率无可测差异**，亚克力→Mica/Solid 的自动降级（B2）缺少数据支持；电池放电 A/B 需要安静放电窗口，暂缓。建议 B2 不做，保留 B3 的说明性引导。

**B3 结果（2026-11-07）**：性能设置页新增“图形处理器”说明卡，按钮跳转 `ms-settings:display-advancedgraphics`；只打开系统页面，**不写注册表、不修改 GPU 偏好**；13 个语言的标题/说明/按钮文案齐全，设置搜索已收录。结合 B1 的“空闲无内存差异”结论，不提供自动材质降级。

## 4. 工作流 C：P2 长会话趋势告警（已实现，仅日志）

- 在现有 `privateMinusGcHeapEstimateMB` 采样序列上做趋势检测：连续 N 个样本单调上升且超过阈值时，记录一次 `PerformanceLogger` 事件并写 `[Memory] TrendWarning` 日志；**仅日志/性能标记，不弹 UI、不自动重启**（空闲重启已覆盖无人场景）。
- 已实现参数（`MemoryTrendPolicy`，`src/DeskBox/Services/MemoryTrendPolicy.cs`）：15 分钟滑动窗、首尾跨度 ≥10 分钟、至少 8 个样本、涨幅 ≥80MB 或 ≥30%、窗口内允许 1 次回落、命中后 6 小时冷却并清空窗口。
- 集成：`PerformanceLogger.SampleMemory` 每次采样调用 `MemoryTrendTracker.Observe`；命中写 `[Memory] TrendWarning privateMinusGcHeapMB=… windowMinutes=15` 并打 `MemoryTrendWarning` 性能标记。
- 验收：策略单测覆盖正常/异常序列（5 例：持续上升、平稳/振荡、短时大涨、1 次回落容忍与 2 次拒绝、冷却期内不重复）；全量 x64 套件 5909/5909 通过；正常使用不误报；仅有日志与性能标记。

## 5. 建议顺序与依赖

1. **A1 → A2 → A3**（P1-A，独立分支；A1 合并后立即推进 A2/A3，不停顿观察）。
2. **B1 → B2 → B3**（核显；B1 是决策前置，数据为负则只保留 B3 的说明；B2 以“当前使用核显”为触发）。
3. **C**（P2，小改动，可与 B 并行）。

依赖：A 与 B 互相独立；B2 依赖 B1 数据；C 独立。所有阶段共用同一套 AOT 零售 + 隔离数据根测量协议。

## 6. 测试与测量协议

- 构建：AOT 零售（x64，Rust native），隔离数据根，真实布局副本；Debug 仅做功能冒烟。
- 场景：3 个 File 窗口（1 组 + 2 独立）与 9-12 窗口折叠/展开两档；固定材质与动画变量。
- 指标：Private 为主，WS 仅参考；GPU 采 per-LUID `GPUProcessMemory` + `GPUEngine`（含 dwm）；必要时加电池放电。
- 门禁：每阶段全量 x64 测试 passed == total；A2/A3、B2 完成后做 AOT 实测；负收益或高风险回归按分支回滚。

## 7. 明确不做

- 合并多格子为单 widget；强制 GC；削减 IconHelper 缓存预算；为省内存默认改变用户的折叠行为/材质；静默改写系统 GPU 偏好。

## 8. 待确认决策

1. P1-A：已确认 A1 合并后立即推进 A2/A3（懒创建）。
2. B2：已确认以“当前使用核显”为触发（DXGI 活动适配器/UMA 特征），不以电池状态代替；检测不到时保持原设置。**B1 数据为负，B2 不做**，只保留 B3 系统入口。
3. P2 的提示形式：已确认**仅日志/性能标记**，不弹 UI。
4. 呼出流畅度：已确认在“滑动会跨越不同 DPI 显示器”时改用合成器淡入/淡出，保留同 DPI 环境的原滑动。

## 9. 视觉呼出流畅度（2026-11-08）

- **帧率结论**：应用没有固定帧率上限。XAML 动画交给合成器；窗口位移动画使用自适应步进（`WidgetAnimationFramePacingPolicy`），目标是显示器刷新周期（本机 144Hz → 6.9ms），仅当提交耗时连续超过预算 70% 时降低提交频率。呼出卡顿来自窗口路径，不是人为限帧。
- **GPU A/B（无改善）**：同一安装、3 个 File 窗口、4 组 show/hide、隔离数据根。核显 show 回调 15/15/17、最大空档 63–73ms；独显 show 8–16、最大 82–98ms。已恢复安装版 `GpuPreference=1`。
- **根因**：SlideFade 把窗口完整停到屏外；本机主屏 125%、左侧邻屏 150%，停靠位置跨屏导致 XAML 在动画中途按 1.5→1.25 重栅格化。实测 show 仅 7–17 个 UI 回调、最大空档 63–316ms；把同一设置的动画改成 Fade（不移动窗口）后为 35 个回调、0 丢帧、最大 8.7ms。
- **修复**：`WidgetTrayAnimationController.CreateProfile` 在停靠中心落到不同 DPI 的显示器时（`SlideCrossesDpiBoundary`）把该窗口降级为合成器淡入/淡出，保留时长与缓动；同 DPI 环境仍使用原滑动。托盘动画期间的瞬态 `xaml-root-scale`/`widget-window-message` 拓扑恢复请求被抑制，管理器在批量动画运行中对恢复请求退避。
- **AOT 零售复测**（3 窗、隔离数据根、4 组）：show 回调 33–36、丢帧 0–2、最大 7.8–13.5ms；hide 28–35、丢帧 0–7、最大 7.8–22.7ms；切换期间无 XamlRoot 缩放变化和拓扑恢复。
- **试过并放弃**：在批量 show 前等待首帧呈现（预滚）。屏外窗口不产生合成帧，等待会超时并给每次呼出增加约 150ms，掉帧不变。
