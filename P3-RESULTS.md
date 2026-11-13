# P3 验证结果 — 交付

> 环境同前：ADOFAI `r150`（2026/11/03），Unity `6000.3.25f1`，Mono，UMM `0.32.5.0`。
> 原始输出：`Mods/PEV.Core/p3-results.txt`、`Mods/PEV.Core/pev-core.log`。

---

## 结果总览

| 检查 | 结果 |
|---|---|
| 撤销后事件绑定仍有效 | **PASS**：对象被替换后仍能找回数据 |
| 撤销后保存不丢事件 | **PASS**：15 个实例全部写出 |
| 重做 | **PASS**：放置的事件重新出现 |
| 缺失定义的注释原样保留 | **PASS**：逐字节一致 |
| ZIP 形式 `.event` 与目录形式并存 | **PASS**：`loaded=2, errors=0` |
| 打包工具校验 | **PASS**：两个示例包均 `OK` |
| 高密度谱面验收 | **PASS**：60 格 / 29 条 action / 15 个实例 |

### 高密度验收实测

```
decode: found=16, restored=15, malformed=0, missing=1, mismatch=0
runtime: 15 instance(s) armed
UNDO     PASS  attached 16 -> 15 (expected 15) after 2 undo step(s)
REDO     PASS  attached 15 -> 16
SAVE-AFTER-UNDO  PASS  written=16, attached instances=16, preserved(no definition)=1
MISSING-preserved PASS
```

一张 60 格、混有 `SetSpeed` / `Twirl` / 普通注释 / 缺失定义 / 15 个自定义事件的谱面，解码、撤销、重做、保存全部正确。

**说明**：这台机器上没有真实社区谱面，验收用的是按真实密度构造的合成谱面（每 4 格一个自定义事件）。这不等于"真实关卡验收"——真实谱面的参数面板布局、图标辨识度等体验问题仍未验证。

---

## 撤销/重做：一个真实的架构缺陷

这是本轮最重要的发现，也是唯一一个**会在用户毫不知情的情况下丢数据**的问题。

### 问题

编辑器的撤销不是"反向执行操作"，而是**整份 `LevelData` 快照回放**：

```csharp
public void SaveState(...)
{
    LevelData data = levelData.Copy();   // 整份拷贝
    undoStates.Add(new LevelState(data, ...));
}

public LevelData Copy()
{
    foreach (LevelEvent levelEvent in levelEvents)
        levelData.levelEvents.Add(levelEvent.Copy());   // 每个事件都是新对象
    ...
}
```

而我们的绑定表以**对象引用**为键：

```csharp
Dictionary<LevelEvent, EventInstanceData> Attached;
```

`Copy()` 之后，事件列表里全是新对象，绑定表里的键指向一批已经不在列表中的孤儿。后果是：**撤销一次再保存，所有自定义事件全部消失**——没有报错，没有提示，谱面看起来还是正常的，只是事件没了。

### 修复

把身份从"对象"改到"数据"，并把身份标记写进事件自身：

```csharp
private const string UidKey = "__pevUid";

public static bool TryGetData(LevelEvent ev, out EventInstanceData data)
{
    if (ByRef.TryGetValue(ev, out data)) return true;      // 快路径

    string uid = ev.ContainsKey(UidKey) ? ev.GetString(UidKey) : null;
    if (uid != null && ByUid.TryGetValue(uid, out data))   // 对象换了也能找回
    {
        ByRef[ev] = data;                                   // 顺手补回快路径
        return true;
    }
    return false;
}
```

关键点在于 `LevelEvent.Copy()` 会复制 `data` 字典：

```csharp
public LevelEvent Copy()
{
    return new LevelEvent(floor, eventType, info, data, disabled, ...);
}
```

所以藏在 `data` 里的 `__pevUid` 会随对象一起被复制过去。而这个键不会泄漏到文件或界面：`Encode` 只遍历 `info.propertiesInfo` 里声明过的键，`data` 里多出来的键会被跳过。

实测确认：撤销两次、重做之后，`SAVE-AFTER-UNDO` 写出 16 条 payload，与编辑器中的 16 个实例一致；另有一条缺失定义的注释按规则原样保留，不计入实例。

### 关于"需要撤销两次"

探针最初按一次 `Undo` 断言，失败了。栈诊断显示：

```
after place: undo=4, redo=0, events=30
after undo #1: undo=3, redo=1, rawEvents=30
after undo #2: undo=2, redo=2, rawEvents=29
```

`AddEventAtSelected` 内部有多层 `SaveStateScope`，一次放置产生多个快照。这是编辑器本身的行为，与我们的实现无关；测试改为循环撤销直到计数变化，并报告实际步数。

### 一个被误报的断言

验收测试第一次跑时 `SAVE-AFTER-UNDO` 报 FAIL：`pev comments written=17, attached instances=16`。

差的这一条是谱面里故意放的"缺失定义"注释——按 §6.3 不变式它必须原样保留，所以文件里有 17 条 `@PEV1` 注释，其中 16 条对应实例、1 条是保留数据。

**是断言写错了，不是产品出错。** 修正为只统计与已绑定 uid 匹配的注释后通过。这类误报值得记一笔：断言如果把"应该保留的数据"算成"应该写出的数据"，就会把正确行为判成缺陷。

---

## 本地化

快捷栏悬停原本显示 `editor.100001`。游戏的字符串表来自 Rewired 的 `LocalizedStringProvider`，它从 TextAsset 加载且没有公开的写入接口；直接改它的私有字典既脆弱又依赖版本。

改为拦截查询：

```csharp
[HarmonyPatch(typeof(RDString), "GetWithCheck")]
public static class GetWithCheckPatch
{
    public static void Postfix(string key, ref bool exists, ref string __result)
    {
        if (exists) return;
        // editor.<数字> -> 事件的 DisplayName
    }
}
```

只处理 `editor.` 前缀加纯数字键，其它查询原样放行。

---

## 错误可见性

原来所有问题只写进 `pev-core.log`，而谱面作者不会知道这个文件存在。现在解码阶段收集问题，编辑器就绪后弹一次通知：

```
Programmable Events: MissingEvent x2, Malformed x1
```

分类：

| 类型 | 含义 |
|---|---|
| `MissingEvent` | 引用了未安装的 `.event`，注释会原样保留 |
| `Malformed` | 注释格式损坏，未被解析也未被改动 |
| `VersionMismatch` | 由更新版本的事件包写入 |

选择"每次开档提示一次"而不是"每个事件提示一次"，避免打开一个含几十个缺失事件的谱面时被通知淹没。

---

## 打包工具

`tools/pack_event.py`：

```bash
python tools/pack_event.py --check MyEvent.event   # 只校验
python tools/pack_event.py MyEvent.event           # 校验并打包
```

校验项：必填字段、入口脚本存在、图标存在、参数类型在白名单内、分类合法。

参数类型这项校验是有价值的——**类型写错时游戏不会报错，只是控件不出现**，作者会以为是自己没写对。工具在打包前就拦下来。

实测：两个示例包 `OK`，`CameraWarp.event` 打包后 1247 字节，内容为 `event.json` / `icon.png` / `main.js`，与目录形式并列加载无冲突（`loaded=2, errors=0`）。

---

## SDK 文档

`docs/EVENT-SDK.md`，面向人和 AI Agent 两种读者：

- 完整字段表与参数类型表（含"写错不报错"的警告）
- 枚举的两种写法与降级建议
- 生命周期表格，明确 `t` 是相对时间、不单调
- 沙箱边界：什么不可用，以及"实例之间不能共享状态"
- 三个可直接套用的写法模板
- 排障表：现象 → 原因
- 给 Agent 的检查清单与高频错误排序

---

## Missing Event 占位

### 问题

此前缺失包的处理是"注释原样保留 + 开档提示"，但**轨道上什么都不显示**。作者打开一张别人做的谱面，看到事件位置是空的，会以为自己建的谱面坏了，或者以为对方的事件丢了。

数据一直是安全的——注释在文件里好好的——只是不可见。可见性和完整性是两件事，之前只做了后者。

### 做法

内置一个 `pev.missing` 事件类型，把三个降级分支（缺包、版本过高、payload 损坏）都转成它：

```
PLACEHOLDER-item  pev.not-installed uid=ghost  floor=41 reason=package 'pev.not-installed' is not installed
```

占位事件带三个描述性字段：缺失的 id、写入时的版本、实例 uid。参数面板里它们被强制 `SetEnabled(false)`——不能编辑，因为编辑了也不会写回（占位事件保存时返回原始字节）。

要点是**占位实例不参与执行**：

```
PLACEHOLDER-not-armed  PASS  placeholder instances armed for execution=0, total armed=15
```

### 关键设计：占位事件写回原始字节

```csharp
public string ToComment()
{
    if (IsPlaceholder && !string.IsNullOrEmpty(OriginalComment))
        return OriginalComment;   // 不重建，原样返回
    ...
}
```

不这样做的话，占位事件会用自己的三个描述字段重写 payload，把 `p` 里的原始参数全部抹掉——一旦装了对应的事件包，事件就再也恢复不出来了。

实测确认字节级一致：

```
MISSING-preserved  PASS  payload with no installed definition must round-trip byte-identical
PAYLOAD  @PEV1 {"id":"pev.not-installed","v":"1.0.0","uid":"ghost","p":{"x":1}}  floor 41
```

### 一个连带影响

占位事件现在也计入"已绑定实例"，所以 `SAVE-AFTER-UNDO` 的计数从"16 实例 + 1 保留注释"变成"17 实例 + 0 保留"。断言相应改为按 uid 比对，而不是按注释前缀统计。

### 面板验证

```
PLACEHOLDER-count      1     visible placeholders
PLACEHOLDER-item       pev.not-installed uid=ghost  floor=41
PLACEHOLDER-registered PASS  pev.missing 是已注册的编辑器类型
PLACEHOLDER-not-armed  PASS  armed for execution=0, total armed=15
PLACEHOLDER-panel      PASS  ShowPanel 在 floor 41 正常打开
PLACEHOLDER-readonly   PASS  fields disabled 3/3
```

`SetEnabled(false)` 不只是改颜色：它会把控件内所有 `Selectable.interactable` 置 false 并同步 `propertyInfo.isEnabled`，所以字段确实不可输入，而不是"看起来灰了但仍能编辑"。

---

## 仍未完成

| 项 | 状态 |
|---|---|
| Missing Event 的编辑器内占位 UI | **已完成**，见下文 |
| 真实鼠标点击快捷栏 | 走同一 `AddEventAtSelected`，未用真实输入验证 |
| 属性面板控件交互 | 直接写 `LevelEvent` 值，未点击控件 |
| 持续型视觉效果 | 两个示例都是瞬时触发 |
| 真实关卡验收 | **仅用合成谱面**：本机无真实社区谱面，验收谱面按真实密度构造，非真实作品 |
| UMM 设置面板 | 未做，`EditorReport.Details()` 已预留 |

---

## 下一步建议

按影响排序：

1. **真实关卡验收** —— 目前所有验证都在合成谱面上，一张真实谱面会暴露参数面板布局、多事件密度、图标辨识度等体验问题。
2. **持续型效果案例** —— 证明 `update(t)` 的逐帧演算路径在实际效果中可用。
3. **真实输入验证** —— 真实鼠标点击快捷栏、真实点击参数面板控件。
