# Stage 0 验证结果

> 执行环境：本机 ADOFAI，Unity `6000.3.25f1`，build `r150`（2026/11/03），Mono，UMM `0.32.5.0`。
> 探针：`probe/Probe/`（`PEVStage0.dll`），部署到 `Mods/PEVStage0/`。原始输出见 `Mods/PEVStage0/stage0-results.txt`。
> 结论标记：**已实测** = 本机跑出来的；**推断** = 由实测行为推出但未单独隔离验证。

---

## 总览

| 项 | 结论 | 对架构的影响 |
|---|---|---|
| V1 数字型事件名 | **通过（三条路径全部）** | 事件命名可直接用数字串，无需 patch `ParseEnum` |
| V2 枚举参数 | **部分可用，需程序集限定名** | 自定义枚举可用，但必须写成 `Enum:Type, Assembly` |
| V3 原生注册 | **通过** | 事件真的出现在快捷栏，可放置 |
| V3c 图标 | **硬依赖**（新发现） | 缺图标会中断整个面板构建，不是可选装饰 |
| V4 虚拟事件 | **通过** | 注入、选中、开面板、参数独立全部正常 |
| V5 / C1 原版可打开 | **通过** | `EditorComment` 载体满足硬性要求 |
| C1 反证：未知 eventType | **确认会毁掉整关事件** | 印证"不能写未知类型"这条约束 |

---

## V1 — 数字型事件名：通过

注册键 `"100001"`，三条转换路径全部把字符串正确还原为 `(LevelEventType)100001`：

```
V1-ParseEnum    PASS   RDUtils.ParseEnum("100001") = 100001
V1b-EnumParse   PASS   Enum.Parse = 100001
V1c-EnumTryParse PASS  TryParse=True, value=100001
```

**结论**：`LevelEventInfo.name` 用数字串时，"名字 ↔ 枚举"天然自洽。原先预估需要替换的十几处 `ParseEnum` 调用点**不需要 patch**。这是本次最大的一笔维护成本节省。

---

## V2 — 枚举参数：可用，但必须程序集限定

之前的判断"自定义枚举无法用作原生下拉"**是错的**，需要更正。实测：

| 写法 | 结果 | 说明 |
|---|---|---|
| `Enum:Ease` | 解析成功（硬编码特判） | 游戏对 `Ease` 有专门分支 |
| `Enum:TileRelativeTo` | **解析成功** | 报错变成"默认值 'A' 不是成员"，说明 `enumType` 已拿到 |
| `Enum:SpeedType` | **解析成功** | 同上 |
| `Enum:Probe.ProbeMode` | 失败，`enumType == null` | 无程序集名时 `Type.GetType` 搜不到 |
| `Enum:Probe.ProbeMode, PEVStage0` | **成功** | 解析到 `Probe.ProbeMode (enum)` |

**结论**：`Type.GetType` 在游戏程序集里执行时只搜索 `Assembly-CSharp` 与核心库。自定义枚举只要写成 `Enum:命名空间.类型名, 程序集名` 就能解析。

**给 `.event` 规范的约束**：枚举参数支持两种写法，按可靠性排序——

1. 复用游戏已有枚举（`TileRelativeTo`、`SpeedType` 等），直接写类型名；
2. 自定义枚举写全限定名 + 程序集名；
3. 或者降级为 `String` + `options`，由 Core 渲染。

注意 `Ease` 来自 DOTween，不在 `Assembly-CSharp` 里，但游戏对它有硬编码特判，所以只写 `Enum:Ease` 即可。

---

## V3 — 原生注册：通过

```
V3a-InfoPresent        PASS  GCS.levelEventsInfo 有我们的键
V3b-TypeStringPresent  PASS  GCS.levelEventTypeString 有映射
V3d-PaletteButtons     1     快捷栏里为我们的类型建出了 1 个按钮
V4e-ShowPanel          ok    ShowPanel(customType) 无异常
```

**结论**：往 `GCS.levelEventsInfo` / `GCS.levelEventTypeString` 塞条目就够了，面板与按钮由游戏自行生成。自绘 UI 在 v1 没有必要。

---

## V3c — 图标是硬依赖（重要新发现）

第一次运行时没有注册图标，结果：

```
KeyNotFoundException: The given key '100001' was not present in the dictionary.
  at ADOFAI.LevelEventButton.Init_Patch0(...)
  at scnEditor.LoadEditorProperties_Patch0(scnEditor)
```

后果不是"图标空白"，而是**`LoadEditorProperties` 整个中断**：快捷栏只建到一半，随后编辑器每帧在 `scnEditor.Update` / `HandleKeyboardActions` 里抛 `NullReferenceException`，界面基本不可用。

补一个占位图标后一切正常。

**结论**：`GCS.levelEventIcons[type]` 是**必须存在的键**。注册流程必须包含图标，缺省时借用一个内置 sprite 兜底。这条要写进实现规范——否则任何一个 `.event` 忘记带图标，玩家看到的就是一个坏掉的编辑器。

---

## V4 — 虚拟事件：通过

```
V4-DecodeInject        PASS  从注释恢复 2 个虚拟事件
V4a-InEditorList       PASS  两个都出现在 editor.events
V4b-Instance           info  floor=2, strength=0.25
V4b-Instance           info  floor=4, strength=1.75
V4c-InstanceIndependence PASS 0.25 vs 1.75
V4d-Select             ok    选中 floor 2
V4e-ShowPanel          ok    打开属性面板无异常
```

**结论**：`LevelData.Decode` postfix 注入虚拟事件可行；注入后编辑器把它当作普通事件对待——可选中、可开面板、参数各自独立。这正是 A6 要求的实例隔离。

---

## V5 / C1 — 原版可打开：通过

纯原版路径（不注册任何类型、不挂任何存储补丁）打开含 `@PEV1` 注释的谱面：

```
C1-Floors           10   几何正常解码
C1-EventCount       3    三条 action 全部存活
C1-Comments         3    EditorComment 条目完整
C1-PEVComments      2    我们的两条 payload 原样保留
C1-ForeignComments  1    无关的普通注释未被触碰
C3-ReEncodePEV      2    原版重新编码后 payload 仍在
C3-UnknownTypes     0    没有未知类型
```

**结论**：`EditorComment` 载体满足 C1（原版能打开）与 C3（原版保存后数据仍在）。这比硬性要求更强——原版不仅没崩，还没吃掉数据。

---

## C1 反证 — 未知 `eventType` 会毁掉整关

对照谱面里放了一条 `"eventType": "100001"`，纯原版路径打开：

```
C1-Floors       11   几何仍然解码成功
C1-EventCount   0    所有 action 全部丢失
```

日志：

```
Error loading level file at ...\UnknownTypeControl.adofai:
The given key '100001' was not present in the dictionary.
  at ADOFAI.LevelEvent.Decode
  at ADOFAI.LevelEvent..ctor
  at ADOFAI.LevelData.Decode
  at ADOFAI.LevelData.LoadLevel
  at scnGame.LoadLevel
  at scnEditor+<OpenLevelCo>d__600.MoveNext
```

**机制上有两处需要更正此前的理解：**

1. **不是 NullReferenceException，而是 KeyNotFoundException。** 因为 `"100001"` 能被 `Enum.TryParse` 解析成合法枚举值，所以 `eventType != None`，流程走到 `GCS.levelEventsInfo[text]` 这一步才炸。
2. **两种未知类型的失败点不同**：
   - 非数字名（如 `"MyMod.Event"`）→ `ParseEnum` 返回 `None` → `NoTypeFound` → `info` 保持 null → 后续 `info.taroDLCCheck` NRE。
   - 数字名（能被解析成 int）→ 解析"成功" → 字典查找抛 `KeyNotFoundException`。

两条路径都会让**整关的 `actions` 全部丢失**（几何还在，事件没了）。这是 C1 不可妥协的直接理由。

---

## 对 Baseline 的影响

### 可以确定的技术选型

| 决策点 | 结论 | 依据 |
|---|---|---|
| 事件命名 | 数字串（如 `"100001"`） | V1 三条路径全通过 |
| 注册方式 | 塞 `levelEventsInfo` + `levelEventTypeString` + **`levelEventIcons`** | V3、V3c |
| 编辑器 UI | 复用原生面板，v1 不自绘 | V3d、V4e |
| 存储载体 | `EditorComment` + `@PEV1` | V5 / C1 / C3 |
| 编解码拦截点 | `LevelData.Decode` / `EncodeToDictionary` 各一处 | V4、V5 |
| 枚举参数 | 复用游戏枚举，或全限定名 + 程序集名 | V2 |

### 仍未验证

| 项 | 原因 | 下一步 |
|---|---|---|
| 脚本引擎逐帧开销与沙箱 | Stage 0 未涉及 | P1 接入 Jint 时测 |
| `LimitMemory` 在 Mono 上是否生效 | 同上 | P1 一行代码验证 |
| 事件从快捷栏拖放到轨道的完整交互 | 只验证了按钮存在与程序化选中 | P2 手工操作一遍 |
| 复制/删除/撤销 | 未测 | P2 |
| 多个 `.event` 同时注册 | 只注册了一个类型 | P2 |

---

## 实现规范中必须固化的两条

1. **注册必须同时提供图标**，缺省时借内置 sprite 兜底。否则不是图标空白，而是编辑器整体损坏。
2. **绝不把自定义事件写成未知 `eventType`。** 两条失败路径都会清空整关事件列表。

---

## 附带说明

- 探针产物：`probe/Probe/`，构建后部署到 `Mods/PEVStage0/`，通过 `vanilla-mode.txt` 与 `level.txt` 两个标记文件切换运行模式。这两个文件现已删除，模组回到默认的完整模式。
- 测试谱面在 `Documents/A Dance of Fire and Ice/Levels/`：`Stage0Probe.adofai`、`PEV-vanilla-test.adofai`、`UnknownTypeControl.adofai`、`PEV-roundtrip*.adofai`。
- 原先位于 `Mods/CustomEventsProbe/` 的第三方库在切换 Mods 目录时被移除。该库与本项目无依赖关系，且用户已明确表示可以删除。
