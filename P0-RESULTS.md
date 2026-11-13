# P0 验证结果 — 存储闭环

> 环境同 Stage 0：ADOFAI `r150`（2026/11/03），Unity `6000.3.25f1`，Mono，UMM `0.32.5.0`。
> 工程：`src/PEV.Core/`，构建产物部署到 `Mods/PEV.Core/`。
> 原始输出：`Mods/PEV.Core/p0-results.txt`、`Mods/PEV.Core/pev-core.log`、`Mods/PEV.Core/p0-export.adofai`。

---

## 目标

打通 `.event` → 注册 → `EditorComment` 落盘 → 重开恢复，并证明数据无损。不含脚本执行。

---

## 结果

| 检查 | 结果 |
|---|---|
| 扫描 `.event` 包 | **PASS**：2 个包，0 错误 |
| 注册到编辑器 | **PASS**：2 个类型，快捷栏可用 |
| 从注释恢复实例 | **PASS**：3/3 恢复，参数完整 |
| 多实例独立 | **PASS**：同 id 两个实例 `0.8` vs `1.6` |
| 参数类型 | **PASS**：`Float`/`Enum`/`Color`/`Tile` 全部建出正确控件 |
| 重新编码 | **PASS**：`actions=6`，`unknownTypes=0` |
| 未知/损坏数据保留 | **PASS**：缺失定义的注释、格式错误的注释均原样保留 |
| 无关注释 | **PASS**：普通注释未被触碰 |

导出文件实测：

```
floor=7  an ordinary comment that must stay as-is
floor=8  @PEV1 {"id":"pev.does-not-exist","v":"1.0.0","uid":"zzz","p":{"x":1}}
floor=9  @PEV1 this is deliberately malformed
floor=2  @PEV1 {"id":"pev.camera-warp","v":"1.0.0","uid":"a1b2c3","p":{"strength":0.8,"duration":2.0,"ease":"OutQuad","color":"ffffff"}}
floor=4  @PEV1 {"id":"pev.camera-warp","v":"1.0.0","uid":"d4e5f6","p":{"strength":1.6,"duration":4.0,"ease":"OutQuad","color":"ffffff"}}
floor=6  @PEV1 {"id":"pev.screen-shake","v":"1.0.0","uid":"g7h8i9","p":{"strength":3.0,"duration":0.5,"target":[0,"ThisTile"]}}
```

注释格式写成了一行紧凑 JSON，枚举是名字，Tile 是数组，与手写形态一致。

---

## 实现过程中修掉的五个真实缺陷

每一个都是跑出来才发现的，且都会在正式使用中伤到作者。

### 1. 图标注册时机错误 → 编辑器整体损坏

在 `SetupLevelEventsInfo` 里发布元数据时，`GCS.levelEventIcons` 还是 `null`，图标兜底被跳过。结果是 Stage 0 已经确认过的那类崩溃重演：`LoadEditorProperties` 中断，`scnEditor.Update` 每帧空引用。

**修正**：元数据在 `SetupLevelEventsInfo` 发布，图标另在 `scnEditor.LoadEditorProperties` 前缀补一次——那时 sprite 已加载。

**教训**：注册不是一次原子操作，元数据与图标有各自的可用时机。

### 2. 按前缀无差别删除 → 静默丢数据

第一版编码时把所有 `@PEV1` 注释都删掉再重写，导致缺失定义和格式错误的注释被一并清除，`actions` 从 6 掉到 4。

**修正**：只删除 `uid` 与本次重写实例匹配的注释，其余一律保留。

**教训**：这条正是 ARCHITECTURE §6.3 第 3 条要求的，实现时很容易为了"幂等"而写过头。

### 3. Tile 参数默认值类型错误 → 控件退化为 String

manifest 里 `"default": [0, "ThisTile"]` 被解析成 `JArray` 直接传给 `PropertyInfo`，而游戏期望 `List<object>`，转型失败后落入兜底分支，控件变成文本框。

**修正**：`ToPlain` 对数组逐项转换并产出 `List<object>`。

### 4. 参数值类型不符 → 保存时抛异常

`PropertyInfo` 决定控件长什么样，但 `LevelEvent.Encode` 还会把值强转成具体类型（`Tile` → `Tuple<int, TileRelativeTo>`）。注入时直接塞 `List<object>`，保存瞬间 `InvalidCastException`。

**修正**：新增 `ParamCoercion`，按 `PropertyType` 把值转成编辑器内部表示。

**教训**：`PropertyInfo` 只解决"怎么显示"，不解决"怎么存"。两层都需要各自正确的类型。

### 5. 回写时丢失语义 → 枚举变数字、Tile 变对象

把编辑器里的活值直接序列化，枚举写成 `6`、Tile 写成 `{"Item1":0,"Item2":0}`。游戏能读，但作者不可读，且违背了"文件里应是手写形态"的约定。

**修正**：新增 `StorageValue`，按 `PropertyType` 反向转换——枚举写名字，复合类型写数组。

**教训**：`编辑器表示` ≠ `文件表示`，两个方向都要显式转换。

---

## 架构层面的确认

以下在 P0 中得到实证，可以写进实现规范：

1. **事件 id → 类型号用 FNV-1a 哈希**，稳定且跨机器一致。不用递增分配，避免安装顺序影响谱面可读性。
2. **`EditorComment` 载体对未知/损坏数据天然安全**：只要不解析成功，就不动它。这比"自定义 eventType"健壮一个数量级。
3. **编码阶段的删除必须精确到 uid**，不能按前缀。
4. **参数有两个表示层**：编辑器内部类型（`Tuple`、枚举对象）与文件类型（数组、字符串）。`ParamCoercion` 负责前者，`StorageValue` 负责后者。
5. **`PropertyInfo` 的可用时机**：`SetupLevelEventsInfo` 之后可用，但图标要等到 `scnEditor` 启动。

---

## 仍未覆盖

| 项 | 说明 |
|---|---|
| 顶点/快捷栏真实交互 | 仍只做过程序化验证，未手工点击放置 |
| 编辑后保存 | 未测"在编辑器改参数 → 保存 → 重开"的完整链路 |
| 复制 / 删除 / 撤销 | 未测 |
| 原版打开 P0 产物 | 未跑；Stage 0 已证明同类数据可行，但应在 P1 前补一次 |
| 脚本执行 | P1 |

---

## 工程结构

```
src/PEV.Core/
├── Plugin.cs                    入口、扫描、Harmony 注册
├── Model/EventManifest.cs       manifest 解析与默认值转换
├── Loading/EventLoader.cs       ZIP / 目录扫描
├── Registry/EventRegistry.cs    注册、版本裁决、图标兜底
├── Storage/EventInstanceData.cs 实例数据的持久化形态
├── Storage/LevelStorage.cs      编解码桥接、五条不变式
├── Storage/ParamCoercion.cs     文件值 → 编辑器值
├── Storage/StorageValue.cs      编辑器值 → 文件值
├── Util/StableId.cs             id → 类型号、uid 生成
└── Diagnostics/P0Harness.cs     仅在 p0-test.txt 存在时启用
```

`Diagnostics` 默认完全惰性，不会影响正常游玩。

---

## 下一步

P1 执行闭环：接入 Jint、建立 `EventContext`、做出一个官方没有的可见效果，并用两个同类实例验证互不干扰。
