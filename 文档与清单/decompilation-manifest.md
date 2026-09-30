# DogeDebugger 反编译基线

## 原始输入

- 文件：`DogeDebugger.dll`
- SHA256：`63947BAD80F59A5F5A0F17920E95177671DC89108A54225DD50706EE555AB3CA`
- 程序集：`DogeDebugger, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null`
- 模块 MVID：`14d79bd8-befb-483d-a18b-84f9049c8405`
- 入口点：`K8246CBA::N11621A0`
- 运行时：`.NET 10`
- 架构：`x64`
- 类型数量：`10586`
- 命名空间数量：`38`

## 原始依赖

- `Wpf.Ui 4.3.0.0`
- `AvalonDock 4.72.1.0`
- `AvalonDock.Themes.VS2013 4.72.1.0`
- `CommunityToolkit.Mvvm 8.4.0.0`
- `Iced 1.21.0.0`
- `ICSharpCode.AvalonEdit 6.3.1.120`
- `MdXaml 2.0.0.0`
- `MoonSharp.Interpreter 2.0.0.0`
- `Newtonsoft.Json 13.0.0.0`
- `Microsoft.Web.WebView2.Wpf 1.0.3537.50`
- `Microsoft.Web.WebView2.Core 1.0.3537.50`
- `protobuf-net 3.0.0.0`
- `protobuf-net.Core 3.0.0.0`
- `Reloaded.Memory 9.4.3.0`
- `Reloaded.Memory.Buffers 3.0.6.0`
- `DogeDebugger.PluginSdk 1.0.0.0`

程序集引用的 `Wpf.Ui`、`AvalonDock` 版本已同步到
`src/DogeDebugger/DogeDebugger.csproj`。

## 反编译输出

### ILSpy

- 路径：`analysis/original-project`
- C# 文件数：`8423`
- 项目文件：`DogeDebugger.csproj`
- 主窗口类型：`GD1C9922`
- 应用程序类型：`K8246CBA`
- 入口类导出：`analysis/decompiled-mainwindow.cs`
- 应用类导出：`analysis/decompiled-application.cs`

### dnSpy

- 路径：`analysis/dnspy-project-20260929`
- C# 文件数：`8424`
- 解决方案：`solution.sln`
- 项目文件：`DogeDebugger\DogeDebugger.csproj`

## 资源结论

原始程序集的 Manifest Resource 表为空，dnSpy 的 `get_resources` 也返回
`Module: DogeDebugger.dll` 且没有条目。因此：

- 不能从程序集直接解包 `mainwindow.xaml`、`app.xaml` 或 BAML。
- `InitializeComponent` 仍调用 `Application.LoadComponent`，但资源由运行时宿主或外部注入链提供。
- UI 必须依据已采集的真实视觉树、运行时布局、截图和反编译构造代码恢复。

## 当前构建状态

- `src` Release 构建：通过，`0` 警告、`0` 错误。
- ILSpy 反编译项目：已补依赖并完成首次构建，仍有反编译器生成代码的可编译性错误。
- dnSpy 反编译项目：已完成输出；存在 dnSpy 生成的非法编译器标识符和接口实现缺口，需在迁移时逐类修复。

## 下一步

1. 从 `GD1C9922` 提取主窗口字段、菜单、工具栏、AvalonDock 文档和事件处理链。
2. 用行为命名替换 `src` 中的占位实现，保持现有公开接口不变。
3. 按原始依赖版本逐模块迁移 UI、内存搜索、调试器、AI、插件和脚本功能。
4. 使用 dnSpy MCP 和运行中的原始程序持续核对方法逻辑、IL 与窗口行为。

## 主窗口命令映射

| 原始命令 | 语义 | 默认快捷键 | `src` 路由 |
|---|---|---|---|
| `debug.run` | 运行/继续 | `F9` | `DebuggerCommands.Run` |
| `debug.passException` | 不处理异常并继续 | `Shift+F9` | `DebuggerCommands.PassException` |
| `debug.runToCursor` | 运行到光标 | `F4` | `DebuggerCommands.RunToCursor` |
| `debug.pause` | 暂停 | `F12` | `DebuggerCommands.Pause` |
| `debug.stepInto` | 步入 | `F7` | `DebuggerCommands.StepInto` |
| `debug.stepOver` | 步过 | `F8` | `DebuggerCommands.StepOver` |
| `debug.stepOut` | 步出 | `Ctrl+F9` | `DebuggerCommands.StepOut` |
| `debug.toggleBreakpoint` | 切换断点 | `F2` | `DebuggerCommands.ToggleBreakpoint` |
| `debug.restart` | 重新开始 | `Ctrl+F2` | `DebuggerCommands.Restart` |
| `debug.detach` | 脱离 | `Ctrl+Alt+F2` | `DebuggerCommands.Detach` |

## 主窗口视图命令映射

| 原始命令 | `src` 路由 | 目标文档 |
|---|---|---|
| `view.memorySearch` | `ViewCommands.MemorySearch` | `MemorySearchDocument` |
| `view.disassembly` | `ViewCommands.Disassembly` | `DisassemblyDocument` |
| `view.hex` | `ViewCommands.Hex` | `DisassemblyDocument` |
| `view.registers` | `ViewCommands.Registers` | `DisassemblyDocument` |
| `view.callStack` | `ViewCommands.CallStack` | `DisassemblyDocument` |
| `view.modules` | `ViewCommands.Modules` | `ModulesDocument` |
| `view.threads` | `ViewCommands.Threads` | `ThreadsDocument` |
| `view.memoryMap` | `ViewCommands.MemoryMap` | `MemoryMapDocument` |
| `view.handles` | `ViewCommands.Handles` | `HandlesDocument` |
| `view.breakpoints` | `ViewCommands.Breakpoints` | `BreakpointsDocument` |
| `view.crossRef` | `ViewCommands.CrossReferences` | `CrossReferencesDocument` |
| `view.stringRefs` | `ViewCommands.StringReferences` | `StringReferencesDocument` |
| `view.rtti` | `ViewCommands.Rtti` | `RttiDocument` |
| `view.exceptions` | `ViewCommands.Exceptions` | `ExceptionsDocument` |
| `view.notes` | `ViewCommands.Notes` | `NotesDocument` |
| `view.comments` | `ViewCommands.Comments` | `CommentsDocument` |
| `view.customSymbols` | `ViewCommands.CustomSymbols` | `CustomSymbolsDocument` |
| `view.log` | `ViewCommands.Log` | `LogDocument` |
| `view.autoAssembler` | `ViewCommands.AutoAssembler` | `AutoAssemblerDocument` |
| `view.luaScript` | `ViewCommands.LuaScript` | `LuaDocument` |

## 主窗口文件和面板命令

| 原始命令 | `src` 路由 | 行为 |
|---|---|---|
| `file.openProcess` | `FileCommands.OpenProcess` | 打开原版结构的进程选择对话框并附加选中进程 |
| `file.attach` | `FileCommands.Attach` | 附加当前选中的目标进程 |
| `file.launch` | `FileCommands.Launch` | 使用当前启动命令行启动并调试 |
| `file.exit` | `FileCommands.Exit` | 关闭 DogeDebugger |
| `panel.next` | `PanelCommands.Next` | 当前文档组循环到下一个标签 |
| `panel.previous` | `PanelCommands.Previous` | 当前文档组循环到上一个标签 |
| `panel.close` | `PanelCommands.Close` | 按反编译逻辑隐藏当前面板，并可从视图菜单恢复 |
| `settings.open` | `SettingsCommands.Open` | 打开设置窗口的“常规”页 |
| `settings.shortcuts` | `SettingsCommands.Shortcuts` | 打开设置窗口的“快捷键”页 |
| `settings.resetLayout` | `SettingsCommands.ResetLayout` | 补齐默认文档并选中内存搜索 |

## 进程选择对话框

原始对话框运行时结构与 `src` 当前实现已逐项对照：

| 项目 | 原始程序 | `src` |
|---|---|---|
| 窗口尺寸 | `680×520` | `680×520` |
| 搜索框 | 相对 `(20,39)`，`444×32` | 相同 |
| 文档列表 | 相对 `(20,116)`，`640×345` | 相同 |
| 底部按钮 | 相对 `(512/602,471)` | 相同 |
| 图标码位 | `U+F690`、`U+F68F` | 相同 |

实现位于 `UI\Views\Dialogs\ProcessSelectionWindow.xaml`、对应代码隐藏和
`UI\Converters\ProcessIconConverter.cs`。

## 面板关闭与布局生命周期

- `panel.close` 对应反编译方法 `GD1C9922.LC93BC18`：
  - 使用 `D1B1BD96` 校验最后一个不可关闭文档。
  - 使用 `F00EBC25` 校验可隐藏面板；`SourceDebugPanel` 不可关闭。
  - 将 `ContentId` 加入隐藏集合 `L737D290`。
  - 通过 `DispatcherPriority.Background` 重排当前文档组。
  - 视图命令通过 `IF157580` 移除隐藏状态，并按原窗格索引恢复。
- 主窗口关闭保存对应 `GD1C9922.J63D941B`：
  - 保存 `WindowLayout`、`MainDockLayout`、`DisasmDockLayout`。
  - 保存前通过 `L519E482` 恢复会话内隐藏文档。
  - 使用原版 `JCAC69AE` 可选面板 ID 列表过滤主 Dock XML。
- 主窗口启动恢复对应 `GD1C9922.O5B6E5A0`：
  - 恢复窗口位置、尺寸和最大化状态。
  - 使用 `XmlLayoutSerializer` 恢复主 Dock 与反汇编 Dock。
  - 按原版 `K7B7CA1A` 逻辑补回被过滤的可选面板。

运行验证：

- `Ctrl+W` 隐藏“笔记”后，文档组保留其余面板；通过“视图 → 调试 → 笔记”恢复后，
  “笔记”回到原索引并成为选中项。
- 选择“源码调试”执行 `Ctrl+W` 时显示原版提示“该面板不能关闭”，面板保持存在。
- 保存窗口 `300,220,920,640` 后重启，窗口恢复到同一坐标与尺寸；主 Dock 和
  反汇编 Dock XML 均写入设置文件。

## 搜索、Lua 与帮助命令

| 原始命令 | `src` 路由 | 反编译行为 |
|---|---|---|
| `memsearch.firstScan` | `MemorySearchCommands.FirstScan` | 调用 `I0BEFF29.ViewModel.FirstScanCommand` |
| `memsearch.nextScan` | `MemorySearchCommands.NextScan` | 调用 `I0BEFF29.ViewModel.NextScanCommand` |
| `memsearch.newTab` | `MemorySearchCommands.NewTab` | 创建搜索标签并复制当前标签选项 |
| `lua.run` | `LuaCommands.Run` | 加载脚本并保持激活，随后执行 `OnStart` |
| `lua.stop` | `LuaCommands.Stop` | 执行 `OnEnd` 并退出脚本激活状态 |
| `help.about` | `HelpCommands.About` | 打开 `AboutWindow` |
| `help.cheatSheet` | `HelpCommands.CheatSheet` | 切换主窗口快捷键速查覆盖层 |
| `tools.allocateMemory` | `ToolsCommands.AllocateMemory` | 调用 `VirtualAllocEx` 在目标进程分配内存 |
| `tools.createThread` | `ToolsCommands.CreateThread` | 调用 `CreateRemoteThread` 创建远程线程 |
| `tools.pointerScan` | `ToolsCommands.PointerScan` | 打开指针扫描窗口并调用 `PointerScanner` |
| `tools.signatureSearch` | `ToolsCommands.SignatureSearch` | 打开跨文件特征码窗口并调用 `AobSignatureGenerator` |

快捷键与反编译默认方案一致：

- `memsearch.firstScan`：`Ctrl+Shift+S`
- `memsearch.nextScan`：`Ctrl+Shift+N`
- `memsearch.newTab`：`Ctrl+T`
- `lua.run`：`Ctrl+Shift+F11`
- `lua.stop`：`Ctrl+Shift+F12`

运行验证：

- Lua 脚本执行 `OnStart` 后输出 `started`，停止执行 `OnEnd` 后追加 `stopped`；
  运行/停止命令状态按脚本激活状态切换。
- `Ctrl+T` 从“搜索 1”创建并切换到“搜索 2”，新标签复制当前扫描选项。
- About 窗口运行时尺寸为 `540×640`，品牌区、六个能力卡片、运行环境和版权区
  均已实际渲染；版本、.NET、Windows 和架构字段从当前进程读取。

## 设置窗口

原始 `AD048207` 设置窗口运行时结构已采集：

- 窗口尺寸：`1241×640`
- 左侧导航：常规、断点、调试、源码调试、异常处理、快捷键、外观、AI、MCP、外部 MCP
- 底部操作：保存、取消

当前 `SettingsWindow` 已迁移：

- “常规”页：
  - 打开新进程时提示清空列表
  - PDB 符号服务器
  - 保存主界面窗口位置和大小
  - 显示底部脚本栏
  - 数据锁定写入间隔
- “快捷键”页：
  - 读取当前程序实际注册的命令、默认快捷键和说明。
- “断点”页：
  - `DeleteBreakpointOnToggle`
  - 完整 `DebugEventSettings` 调试事件开关。
- “调试”页：
  - `RestrictStepToCurrentThread`
  - `SavedBreakpointRestoreMode`
  - `AutoEnableSavedBreakpointsOnLaunch`
  - `AutoAttachProcessNames`
  - `AutoAttachAllowWhenProcessSelected`
  - `AutoAttachIntervalMs`
  - `AutoAttachSavedAddressMode`
- “源码调试”页：
  - `SourceDebugging.Enabled`
  - `SourceDebugging.SourceRoot`
  - `SourceDebugging.PdbRoot`
- “异常处理”页：
  - `ExceptionHandling.Mode`
  - `ExceptionHandling.LogNonDebuggerExceptions`
  - `ExceptionHandling.CustomRules`
- “外观”页：
  - `ThemeMode`
  - `CodeFontFamily`、`CodeFontSize`
  - `UiFontFamily`、`UiFontSize`
- “AI”页：
  - API 地址、API 密钥、默认模型、推理强度、温度
  - 发送、推理显示、自动继续、内置指导和只读模式
  - 系统提示词
- “MCP”页：
  - `McpEnabled`、监听地址、端口
  - 隐私保护、输出配置
- “外部 MCP”页：
  - `ExternalMcpEnabled`
  - `McpServers` 真实服务器集合和传输、命令、URL、工作目录配置
- 保存按钮通过 `SettingsStore.Replace` 写入配置；取消不修改配置。

运行验证确认：

- `settings.open` 打开 `1241×640` 设置窗口并定位“常规”页。
- `settings.shortcuts` 直接定位“快捷键”页，显示 `F9`、`Shift+F9` 等当前命令。
- “断点、调试、源码调试、异常处理”页导航均启用，页面控件和模型字段实际加载。
- “外观、AI、MCP、外部 MCP”页导航均启用，页面控件和模型字段实际加载。

## 快捷键速查层

原始 `ShortcutCheatSheetOverlay` 已通过运行行为采集：

- 覆盖在主窗口内，顶部显示“快捷键速查”、当前方案和 `Esc` 提示。
- 内容按“调试、文件、面板、内存搜索、Lua 脚本”等分组。
- `Ctrl+Shift+/` 打开或关闭，`Esc` 关闭。

当前实现只列出程序实际注册的命令，不显示尚未迁移的追踪、工具或列表面板快捷键。
运行验证确认菜单命令可打开覆盖层，分组和快捷键正常显示，`Esc` 可关闭。

## 工具窗口

- “分配内存”窗口：
  - 首选地址、大小和保护属性。
  - 通过 `TargetProcess.AllocateMemory` 调用 `VirtualAllocEx`。
  - 支持通过 `VirtualFreeEx` 释放已分配内存。
- “创建线程”窗口：
  - 起始地址、参数和创建标志。
  - 通过 `TargetProcess.CreateRemoteThread` 调用 `CreateRemoteThread`。
- “指针扫描”窗口：
  - 目标地址、最大偏移、深度、对齐、仅可写选项。
  - 调用 `DebuggerSession.ScanPointers` 和 `PointerScanner`。
  - 结果展示基址、模块、模块偏移和偏移链。
- “跨文件特征码搜索”窗口：
  - 起始地址、模块、指令数、最大长度、唯一性和模块相对地址选项。
  - 调用 `DebuggerSession.GenerateSignature` 和 `AobSignatureGenerator`。
  - 生成结果可提交到现有内存搜索。

运行验证确认四个窗口均可从原“工具”菜单打开，输入控件和结果区域正常渲染。

## UnrealEngine 扩展

已按原始 `Plugins.UnrealEngine` 分层迁移模型、服务、界面和 MCP：

- FNamePool/GNames、Fixed/Chunked GObjects、自动特征扫描和偏移缓存。
- `UStruct/FField/UField` 成员、`UFunction/Exec`、继承链和 `UEnum` 三种名称布局。
- `GWorld/GEngine` 类层次和模块全局指针定位，以及 `UWorld.PersistentLevel/ULevel.Actors` 自动布局。
- 旧版反射布局自动探测，覆盖 `UStructSuperStruct/UFieldNext/UStructChildren/FProperty/UFunction`。
- `ue_inspect_object` 支持成员分页、过滤、值读取、字符串长度、数组预览和结构递归。
- 值读取覆盖 bool 位域、数值、`FName/FString/FText`、枚举、对象引用、`TArray`、内建结构和嵌套结构。
- 面板提供对象、包、类型、类型成员、函数、枚举、成员值、World Actors、Offsets 和诊断视图。

运行验证使用真实 `cmd.exe` 进程中的 64 位合成反射布局：

- 首次扫描识别 `UEnum::Names` 偏移 `0x40`、`NameInt64` 布局和 `ENetRole` 枚举项。
- 对象检查返回 `123456`、`1.25`、`Label`、`Hello UE`、`true`、`ROLE_AutonomousProxy (2)`。
- 数组返回 `10/20/30`，`FVector` 返回 `1.5/2.5/3.5`，对象引用返回目标对象名和地址。
- 世界布局自动识别 `PersistentLevel=0x30`、`Actors=0x98`，`ue_list_actors` 返回两个真实 Actor 对象。
