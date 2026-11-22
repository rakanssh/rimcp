# RimMCP 本地化指南

[English](README.localization.md)

## 给翻译者

准备一份已构建的 RimMCP 模组，再用记事本或 VS Code 等纯文本编辑器，就能翻译并测试。请将 XML 文件保存为 UTF-8 编码。

### 新增或更新语言

1. 找到 RimMCP 模组文件夹，里面有 `About`、`Assemblies` 和 `Languages` 文件夹。如果用的是创意工坊版，请将模组复制到 RimWorld 的 `Mods` 文件夹，再编辑这份本地副本，以免创意工坊更新覆盖你的修改。在游戏的模组列表中只启用一份 RimMCP。
2. 打开 `Languages`。语言文件夹的名称必须与 RimWorld 使用的一致，例如法语是 `French`，简体中文是 `ChineseSimplified`。更新已有翻译时，直接在对应语言的 `Keyed` 文件夹中编辑。如果要新增法语翻译，则创建 `Languages/French/Keyed`。
3. 先试译一条。把 `Languages/English/Keyed/AgentActivity.xml` 复制到 `Languages/French/Keyed/AgentActivity.xml`，然后用下面这份完整的 XML 替换法语副本的内容。这会翻译智能体日志窗口的标题。

   ```xml
   <?xml version="1.0" encoding="utf-8"?>
   <LanguageData>
     <RiMCP.AgentActivity.LogTitle>Journal de l’agent</RiMCP.AgentActivity.LogTitle>
   </LanguageData>
   ```

4. 启动 RimWorld，在语言设置中选法语，根据游戏提示重新加载。确认 RimMCP 已启用后，载入一个测试殖民地，用 RimMCP 的快捷控制按钮打开智能体日志。窗口标题应显示 **Journal de l’agent**。本例中的其他 RimMCP 文案会回退到英文。切回英语，确认窗口恢复原有标题。
5. 要扩大翻译范围，可从英文 `Keyed` 文件中挑选更多条目来翻译。贡献译文时，只提交对应语言文件夹下改动的文件，并说明用哪种游戏语言检查了哪些界面。

### 哪些内容需要翻译

- 在 `Keyed` XML 文件中，编辑每对开始和结束标签之间的文字，保留标签名以及外层的 `LanguageData`。每种语言的同一个键只应出现在一个文件里。要在 XML 文本中写 `&` 和 `<`，请分别写作 `&amp;` 和 `&lt;`。
- 保留 `{0}`、`{NOTE}` 等占位符的拼写，可以根据语序移动或重复它们。例如，时间日志中的 `{0} minutes` 可以翻译为 `{0} 分钟`。在会进行格式化的模板中，`{{0}}` 会显示为字面文本 `{0}`。保留 RimWorld 的语法修饰符及其分支。
- 保留 `get_operation` 和 `requestId` 等工具名和参数名，以及描述工具行为的枚举值和数字限制。省略还没翻译好的条目，让游戏使用英文原文。
- 接受任务时的通知和《皇权》授予荣誉头衔前的确认提示使用当前游戏语言，译文由 RimWorld 的语言文件提供。
- **权限（Access）**选项卡中的工具说明使用 `RiMCP.Tool.get_game_log.Description` 一类的键。把想翻译的条目从 [ToolDescriptions.xml](RiMCP/Languages/English/Keyed/ToolDescriptions.xml) 复制到目标语言 `Keyed` 文件夹下的一份 XML 中，再翻译其中的文字。工具说明是纯文本，其中的花括号按字面显示。打开“权限”页，确认工具显示预期的译文，并用译文中的词搜索，确认能找到该工具。
- 快捷控制按钮的定义说明放在 `DefInjected` 中。把 `Languages/ChineseSimplified/DefInjected/MainButtonDef/RiMCP.xml` 复制到目标语言目录下相同的 `DefInjected/MainButtonDef` 路径，再翻译 `RiMCP_QuickControl.description` 里的文字。省略此条时，游戏会沿用按钮定义中的英文说明。

### 设置智能体日志的详情长度

在目标语言的 `Keyed/AgentActivity.xml` 中新增或修改 `RiMCP.AgentActivity.LogDetailLimit`，即可调整智能体日志每一行的详情预览长度。英语使用 `120`，简体中文使用 `60`。省略该键时使用英文资源的值。请填写大于或等于 `3` 的整数，无效值回退到内置默认值 `120`。

```xml
<RiMCP.AgentActivity.LogDetailLimit>100</RiMCP.AgentActivity.LogDetailLimit>
```

截断后末尾的三个点也计入长度上限。文字换行时，日志行仍可随之增高。将鼠标悬停在日志行上可看到完整详情，诊断 API 也会返回完整的记录内容。

### 设置留言长度上限

在目标语言的 `Keyed/AgentActivity.xml` 中新增或修改 `RiMCP.AgentActivity.NoteLengthLimit`，可设置 `tell_player` 的留言长度上限。英语使用 `280`，简体中文使用 `140`。省略该键时使用英文资源的值。请填写大于或等于 `1` 的整数，无效值回退到内置默认值 `280`。

```xml
<RiMCP.AgentActivity.NoteLengthLimit>320</RiMCP.AgentActivity.NoteLengthLimit>
```

长度按 UTF-16 代码单元计数，包括首尾空白。普通留言和通过 `attention: true` 发送的信件都使用此上限。超限时整条拒绝，不截断。智能体日志行内的详情预览使用独立的长度设置。

## 给开发者

下面的源码路径和命令适用于仓库源码。请在仓库根目录运行命令。

### 英文资源和生成的键

在 `RiMCP/Languages/English/Keyed/` 里新增或修改普通的英文 UI 和活动文案。把相关的键放在一起，使用 `RiMCP.TimeLog.MinuteMany` 这类命名。`RiMCP.UI.Keys` 类定义在 [TextKeys.g.cs](RiMCP/Source/UI/TextKeys.g.cs) 中。CI 会检查该文件和英文工具说明是否分别与各自的来源保持同步。生成时会去掉 `RiMCP.` 前缀，把剩余的点替换为下划线，因此该例生成 `Keys.TimeLog_MinuteMany`。生成器会检查英文值，发现生成的成员名冲突时会报错。重命名或删除键时，也要更新使用它的 C# 代码。 [模组项目](RiMCP/Source/RiMCP.csproj) · [资源生成器](scripts/LocalizationCatalog/Program.cs)

增删或重命名英文键，或者修改英文工具说明时，在仓库根目录运行以下命令，同步 `TextKeys.g.cs` 和 `ToolDescriptions.xml`，并将变动的生成文件与对应的源文件一并提交。该命令无需 RimWorld 游戏程序集。

```sh
dotnet run --project scripts/LocalizationCatalog -- RiMCP/Languages/English/Keyed RiMCP/Source/UI/TextKeys.g.cs
```

要修改英文工具说明，请编辑 `Shared/ToolManifest.cs` 中对应工具的声明。这份清单同时提供 MCP 工具说明和游戏访问权限设置中的英文说明。构建模组时会根据工具清单刷新 `RiMCP/Languages/English/Keyed/ToolDescriptions.xml`。要覆盖某种非英语界面里的说明，在对应语言的 `Keyed` 资源中添加 `RiMCP.Tool.<tool_name>.Description`。缺失或空白时回退到清单中的英文。覆盖只影响游戏中的说明和搜索，MCP 客户端继续收到清单中的说明。 [工具清单](Shared/ToolManifest.cs) · [工具说明查找](RiMCP/Source/UI/UiText.cs)

### UI 文案和模板参数

面向玩家的标签、按钮、工具提示、通知、信件和对话框通过 `UiDisplay` 显示。使用 `Keys.SomeKey.Display()` 或 `Keys.SomeKey.Display(args)` 传入资源文本。日志等需要字符串的场合使用 `.Text(args)`。需要直接展示的数据和已经渲染过的字符串，用 `DisplayText.Raw(value)` 原样传入，避免再次翻译或格式化。地址等可编辑的值仍然直接传给 `Widgets.TextField`。

```csharp
UiDisplay.Label(rect, Keys.TimeLog_MinuteMany.Display(3));
UiDisplay.Label(rect, DisplayText.Raw("localhost"));
```

例如，新增模板 `Agent note: {NOTE} · {0} changes · {{0}}` 时，先传入数量，再将留言作为 `new NamedArgument(note, "NOTE")` 传入。如果新资源键叫 `RiMCP.AgentLog.Example`，调用就是 `Keys.AgentLog_Example.Text(3, new NamedArgument(note, "NOTE"))`。RimMCP 交由 Verse 解析模板符号，再原样插入字符串和 `TaggedString` 参数。留言中的花括号、小写文字或换行均会保留。

使用 RimWorld 的翻译键时，将整句和参数一次交给 Verse，例如 `"MessageQuestAccepted".Translate(pawn, quest.name)`。Verse 按当前语言的语法处理整句，再由 `UiDisplay.GameMessage` 显示包含角色姓名的通知。把游戏标签插入纯文本日志时，先对该标签调用 `StripTags()`。把 `tell_player` 留言、存档名、标识符和错误消息作为原文传入。 [TextKey](RiMCP/Source/UI/TextKey.cs) · [UI 文本入口](RiMCP/Source/UI/UiDisplay.cs)

### 活动日志和 API 边界

智能体日志在创建或更新条目时记录本地化的操作和相关详情。待处理的操作、时间推进和对话框完成时，可以更新同一条记录。切换语言后，新生成的文案和当前绘制的界面标签（例如结果标签）会使用新的语言。之前记录的操作文案保留原样，直到后续操作或完成事件更新这些文字。保存、载入或离开游戏成功时，日志显示实际使用的存档名。请求被拒绝时，日志保留请求中的名称。使用同一个 `requestId` 重试 `tell_player` 时，留言正文必须逐字一致，包括首尾空白。

HTTP 诊断端点 `/v1/agent-log` 返回已保存的 `line` 和 `detail`，以及 `tool`、`outcome` 等不随界面语言变化的结构化字段。详情预览按 UTF-16 代码单元计数，截断时保留完整代理对。长度限制只适用于简短预览。日志行按实际显示的文字计算高度，工具提示使用完整详情，诊断响应发送已记录的完整文本。`get_game_log` 提供游戏原始日志消息及可选的原始堆栈跟踪。MCP 工具名、参数定义、标识符和配置语法保持稳定。 [智能体日志](RiMCP/Source/Command/AgentLog.cs) · [日志窗口](RiMCP/Source/UI/Window_AgentLog.cs) · [诊断路由](RiMCP/Source/Read/AgentLogReadService.cs) · [游戏日志](RiMCP/Source/Read/GameLogReadService.cs)

### 验证本地化改动

在仓库根目录运行 `dotnet run --project tests/Localization`，检查译文是否缺失、模板占位符是否与英文一致。修改了显示文字的 C# 代码时，还应按[本地化检查说明](tests/Localization/README.md)检查界面调用。

在游戏里打开改动涉及的界面，用目标语言核对文案。例如，用译文中的词搜索工具，缩窄日志窗口检查换行，再悬停查看完整详情。模板包含存档名或留言时，用带花括号和标点的原文检查显示结果。最后切回英语，核对同一界面。
