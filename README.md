# CE C# 版本功能与落地说明（zh-CN）

## 1. 定位

本文档描述 Cheat Engine 功能语义在 DogeDebugger C#/.NET 10/WPF 架构中的落地方式。

约束如下：

- 使用现有 C# 项目结构和 WPF 布局。
- 不使用反编译工具生成的混淆标识符作为新代码命名。
- 不新增 `Test` 类型。
- CE 的底层行为、数据含义和实时状态作为实现基线。
- 不复制 CE 的品牌、可识别文案、视觉特征或外部发布信息。
- 原版 `DogeDebugger.dll` 的运行行为只用于校验，不把旧文档当作实现依据。

## 2. 分层结构

| 层级 | C# 实现 | 职责 |
| --- | --- | --- |
| WPF 外壳 | `MainWindow`、`UI/Views`、`UI/ViewModels` | 菜单、AvalonDock 布局、面板、对话框和实时绑定 |
| 会话层 | `DebuggerSession` | 统一暴露目标进程、调试器、断点、内存、RTTI、Mono 和指令访问状态 |
| 调试器层 | `UserModeDebugger` | Windows 调试事件循环、暂停、继续、单步、异常和中断 |
| 断点层 | `SoftwareBreakpointManager`、`HardwareBreakpointManager` | 软件断点、硬件断点、命中规范化、恢复和重新武装 |
| 访问监视层 | `InstructionAccessWatchManager`、`InstructionAccessWatch` | `WatchId` 生命周期、记录队列、地址聚合和实时快照 |
| MCP 层 | `Core/AI/DebuggerMcpTools.cs` | 把同一会话状态暴露为可查询、可执行工具 |

## 3. CE 功能映射

| CE 侧参考 | C# 侧实现 | 语义 |
| --- | --- | --- |
| `debughelper.pas` | `BreakpointEntry`、`SoftwareBreakpointManager` | 断点注册、原始字节、命中恢复、重新安装 |
| `debugeventhandler.pas` | `UserModeDebugger` | 调试事件、异常、断点、单步和继续 |
| `FoundCodeUnit.pas` | `InstructionAccessWatchManager`、`InstructionAccessWatchViewModel` | 指令访问记录、值读取、计数和界面状态 |
| `frmChangedAddresses.pas` | `InstructionAccessWatchWindow` | 指令访问窗口及实时列表 |
| `MemoryBrowserFormUnit.pas` | `DisassemblyView` | 反汇编右键菜单和指令访问入口 |
| `LuaHandler.pas`、CE MCP bridge | `DebuggerMcpTools` | 调试状态和操作的程序化入口 |

映射时保留行为语义，但不直接复用 CE 的过程名、类名、控件名或品牌标识。

## 4. 指令访问实时状态链

### 4.1 创建

1. 反汇编视图从当前指令读取内存操作数。
2. `InstructionMemoryOperand` 保存操作数索引、显示文本、默认值大小和 Mono 基址寄存器。
3. `InstructionAccessWatchManager.Start` 创建稳定 `WatchId`。
4. 监听使用内部软件断点，并保存指令原始字节。

### 4.2 命中

1. `UserModeDebugger` 收到软件断点异常。
2. 按异常地址解析真实断点，内部监听断点不进入普通用户断点流程。
3. `InstructionAccessWatchManager` 解析操作数最终访问地址。
4. 读取目标内存并生成 `InstructionAccessRecord`。
5. 记录按 `WatchId` 进入队列，由排空计时器合并为 UI 快照。

记录字段包括：

- `WatchId`
- 指令地址
- 访问地址
- 值大小
- 读取是否成功
- 值
- Mono 基址寄存器与地址
- 线程号
- 首次出现时间和最后出现时间
- 命中次数

### 4.3 UI 更新

`InstructionAccessWatchViewModel` 不直接读取调试器内部字典，只消费 `InstructionAccessWatchSnapshot`：

- 摘要数量来自快照的 `AddressCount`
- 行地址、值、次数来自快照记录
- 16 进制与 10 进制切换只改变显示，不改变底层值
- 值大小切换清空旧结果并更新监听过滤
- 选中行变化同步刷新复制、RTTI、数据视图和 Mono 菜单状态
- 窗口关闭或停止时移除内部断点并恢复原始字节

## 5. 布局与界面状态规则

- 窗口和面板尺寸使用稳定的 `Width`、`Height`、`MinWidth`、`MinHeight`。
- 表格列宽使用固定宽度或 `*`，避免实时值变化引起布局跳动。
- 字体样式使用项目现有 UI 字体资源；代码和地址列使用 `Consolas` 或 `Cascadia Code`。
- 行选择优先使用控件选择和模式 API，不依赖屏幕坐标。
- 右键菜单必须处理真实鼠标所在行，再刷新菜单可用状态。
- 实时列表只允许从快照更新，不允许 UI 直接修改断点内部状态。

## 6. 调试器生命周期要求

- 必须打开目标进程后才能执行附加。
- 必须附加调试器后才能创建指令访问监听。
- 监听创建、命中、停止、重新附加和进程切换都必须回到同一个状态源。
- 暂停状态、当前线程、IP 和寄存器必须来自调试器实际上下文，不能在 UI 层猜测。
- 调试事件循环退出时，必须同步清理监听、断点和窗口状态。

## 7. 验证要求

每次改动后至少执行：

```powershell
& 'D:\Microsoft Visual Studio2026\Community\MSBuild\Current\Bin\MSBuild.exe' `
  'src\DogeDebugger.sln' /m /t:Build /p:Configuration=Release /p:Platform=x64 /v:minimal
```

运行验证必须覆盖：

1. 启动目标进程。
2. 打开并附加目标进程。
3. 在带内存操作数的指令上创建监听。
4. 观察到 `totalRecordCount`、`addressCount`、值和次数增长。
5. 在监听窗口中观察到同一地址行实时变化。
6. 停止监听后确认断点移除、原始字节恢复、UI 状态变为停止。

只通过编译、只看到窗口、或只得到空记录，都不能作为功能完成证明。
