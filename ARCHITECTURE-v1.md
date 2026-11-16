# ADOFAI Programmable Events — Baseline v1.0

> 状态：工作基线。
> **冻结的是产品要求与验收标准；技术实现由验证结果决定。**
> 校准环境：本机 ADOFAI，Unity `6000.3.25f1`，build `r150`（2026/11/03），Mono 后端，UMM `0.32.5.0`。
> 依据：反编译 `Assembly-CSharp.dll` / `Assembly-CSharp-firstpass.dll`、解析 `resources.assets` 中的 `LevelEditorProperties`、实机运行日志。

---

## 0. 定义

**ADOFAI Programmable Events Core 是一个 UMM Mod。**

```
             ADOFAI / Unity
                   ▲
        ┌──────────┴──────────┐
        │      Game API       │  向脚本开放时间、轨道、对象、特效
        ├─────────────────────┤
        │   Script Executor   │  执行 .event 内的程序
        ├─────────────────────┤
        │   Editor Adapter    │  让事件在编辑器里出现、可放置、可编辑
        ├─────────────────────┤
        │  Event File Loader  │  扫描、校验、加载 .event
        └──────────┬──────────┘
                   │
              UMM（单个 Mod）
```

- Core 是**翻译层 + 执行器 + API 面**，不是插件生态，不是 Host，不是包管理器。
- `.event` 是作者交付的完整素材包；Core 是解释并执行它的工具。
- 效果本身由 `.event` 实现，Core 不负责特效。
- Core 的交付包不预装任何事件；事件文件单独分发、安装与卸载。事件专属的算法、状态规则、素材和设置属于事件包，Core 只提供不依赖具体事件的通用执行能力。已有实现若仍有特定事件的专属代码，属于待迁移债务，不能因更名或移出素材就称为已清理。

### 事件的全局设置

- 某个事件需要全局开关时，由它自己的 `.event/event.json` 声明设置、默认值和文案。Core 只提供通用的校验、UMM 面板、按事件 ID 隔离的保存与读取机制；不得为每个事件在 Core 的设置类或 UMM 界面里增加专属开关。
- 设置跟随事件包是否安装而显示；卸载事件包不删除已保存的值。设置属于本机，不写入谱面。事件脚本只读取本包声明的设置，不因此获得文件或其他包的访问权。
- Core 可以提供参数化、可复用的渲染等底层原语；事件选择、组合和控制原语的行为放在事件包里。仅为让某个效果可插拔，不应在 Core 按事件 ID 写入事件逻辑。现存兼容路径应逐步迁移并以回归测试保护旧数据。

### 非目标

- 不做独立 Host 或独立进程。
- 不做插件生命周期管理、模块依赖解析、版本仓库。
- 不为每个 `.event` 建独立 UMM Mod。UMM 只加载 Core 一个 DLL。
- 不做在线商店、账号、分发平台。
- 不做通用脚本能力（网络、文件系统、任意反射）。

---

## 1. 工程原则

本项目采用**结果优先**的工程原则。

- 不限定自定义事件必须通过何种内部机制注册，也不要求必须复用原生事件对象。
- 在满足验收目标的前提下，优先选择**实现简单、稳定、依赖少、易于维护、易于跟进游戏版本**的方案。
- 不为形式上的原生化而增加复杂度。
- 不把尚未验证的技术猜想提前冻结为架构约束。

**判断一个方案好坏，只看两件事**：它是否让三个验收目标更稳，以及它是否让下次游戏更新时我们要改的地方更少。

---

## 2. 三个硬性验收目标（冻结）

### A. 安装模组时：像原生事件一样使用

| 编号 | 要求 |
|---|---|
| A1 | 在编辑器快捷栏中显示，具有分类、名称、图标 |
| A2 | 可以放置、选择、移动、复制、删除，操作方式与原生事件一致或足够接近 |
| A3 | 可以通过图形化界面编辑参数，优先复用原生控件 |
| A4 | 保存后重新打开，事件与参数状态完全正确 |
| A5 | 实际游玩时完整执行自定义效果 |
| A6 | 多个同类事件实例互不干扰（参数、状态、生命周期各自独立） |

### B. 自定义事件能真正扩展游戏能力

| 编号 | 要求 |
|---|---|
| B1 | 通过独立 `.event` 文件导入并注册 |
| B2 | 文件可携带脚本、图标、参数定义、其他资源 |
| B3 | 开发者可调用模组开放的游戏接口 |
| B4 | 能实现官方事件无法实现的效果 |
| B5 | 未被当前关卡使用的 `.event` 同样注册，可供作者选择 |

### C. 不安装模组时：原版仍能打开关卡

| 编号 | 要求 |
|---|---|
| C1 | 不会因为自定义事件数据导致整张地图无法打开 |
| C2 | 自定义事件的数据采用原版能够容忍的存储形式 |
| C3 | （观察项，非硬性）原版打开并保存后，数据是否仍可被模组恢复 —— 需要实测并记录，但不作为 v1.0 的阻塞条件 |

> C 是**硬要求**，但只要求"能打开"。原版编辑后是否保留自定义事件，属实测观察项；实测结果决定我们要不要加提示或备份机制，不影响架构选型。

---

## 3. 技术候选与当前状态

> 状态更新：**Stage 0 已执行完毕**，结果见 [STAGE0-RESULTS.md](STAGE0-RESULTS.md)。下表中已落定的事项标注为「已实测」，其余仍开放。

| 决策点 | 候选 | 当前倾向 | 状态 | 由什么决定 |
|---|---|---|---|---|
| 事件注册方式 | 数字型事件类型名（利用 `Enum.TryParse` 接受数字串） | **采用** | **已实测：三条路径全部通过** | 无需 patch `ParseEnum` |
| | patch `ParseEnum` 调用点 | 备用 | 不需要 | — |
| | 完全自绘事件栏与面板 | 备用 | 不需要（v1） | 原生面板已可用 |
| 存储形式 | `EditorComment` + `@PEV1` 前缀 | **采用** | **已实测：原版可开、可回存、不误伤** | C1/C2 与 A4 全部满足 |
| | 其他原版可容忍形式 | 保留 | 不需要 | — |
| 参数控件 | 原生 `PropertyType` schema → 原生预制体 | **采用** | 机制已实测成立 | A3 |
| | 自绘控件 | 备用 | 未探索 | 仅在原生表达力不足时 |
| 枚举参数 | 全限定名 + 程序集名（`Enum:NS.Type, Asm`） | **采用** | **已实测可解析** | 见 STAGE0-RESULTS §V2 |
| 图标 | 必须提供，缺省借用内置 sprite | **必须** | **已实测：缺失会中断整个面板构建** | V3c |
| 脚本引擎 | Jint 4.17 + Acornima 1.7（net462） | **采用** | **已实测**：最差 0.028 ms/实例，五项沙箱检查全部拦截 | 见 P1-RESULTS |
| Harmony 使用量 | — | 越少越好 | 已落实：存储 2 处 + 注册 1 处 + 图标 1 处 | 维护成本 |

**原则**：任一候选在验证中被证伪时，直接换下一个，不改产品要求。

---

## 4. 已验证的游戏事实

这一节是**事实**，不是决策。它会长时间有效，是选型的输入。

| # | 事实 | 后果 |
|---|---|---|
| 1 | 事件数组 JSON 键为 `actions`；`levelEvents` 只是 C# 字段名 | 不要往文件写 `levelEvents` |
| 2 | 顶层键只有 `angleData`/`pathData`/`settings`/`actions`/`decorations` | 其它顶层键在保存时被丢弃 |
| 3 | `settings.version` 编码为 `19`；`>19` 直接拒绝加载 | 不要改 `version` |
| 4 | `EditorComment` 是原生事件类型，唯一属性 `comment`（长文本），`executionTime: Special`，`allowFirstFloor: true`，无字符数上限 | 首格可放置；无多字段 |
| 5 | **未知 `eventType` 会导致整关加载失败**：`Decode` 返回 `NoTypeFound` 使 `info == null`，随后在 `levelEvent.info.taroDLCCheck` 抛 NRE，外层无 try/catch | 自定义事件**不能**以未知 type 落盘 → 这是 C1 的直接来源 |
| 6 | `EditorComment` 保存无损：`comment` 原样进 `data`，Encode 对字符串类型直接赋值 | 注释是可行载体 |
| 7 | 保存时 `actions` 按 `floor` 稳定排序；`active == false` 的事件整条丢弃 | 不要用 `active:false` 存数据；顺序会变、内容不丢 |
| 8 | 所有存档路径共用 `LevelData.EncodeToDictionary()`（保存、backup、导出、上传前刷新） | 编码拦截点唯一 |
| 9 | 所有加载路径共用 `LevelData.Decode()` | 解码拦截点唯一 |
| 10 | 事件面板完全数据驱动：启动时读 `LevelEditorProperties` 填 `GCS.levelEventsInfo` / `levelEventTypeString`；`InspectorPanel.Init` 遍历该字典生成面板与页签 | 注册可能只是往字典塞条目，UI 白送 |
| 11 | 属性类型是**字符串字面量**，由 `PropertyInfo` 构造器 `switch` 解析。固定形式 18 个：`Array` `Bool` `Color` `Export` `File` `FilterProperties` `Float` `FloatPair` `Int` `List` `MinMaxGradient` `Note` `ParticlePlayback` `Rating` `String` `Text` `Tile` `Vector2`；另有 `"Enum:<TypeName>"`。写错不报错，静默不生成控件 | 参数类型必须走白名单 |
| 12 | `"Enum:X"` 的类型名走 `Type.GetType`，仅 `Ease`、`ParticleArcMode` 被硬编码特判 | 自定义枚举无法直接用作原生下拉 |
| 13 | `requiredMods` 判定为"非空即拒绝加载"，无可选依赖语义 | 不填 `requiredMods` |
| 14 | 全局时钟 `scrConductor.instance.songposition_minusi`；每格有 `scrFloor.entryTime` / `entryTimePitchAdj` | 时间系统的唯一依据 |
| 15 | 每种效果都有原生组件 `ffx*`（`ffxFlash`、`ffxShakeScreen`、`ffxBloom`、`ffxSetFilter`、`ffxScreenTile`、`ffxCamMove` 等） | 可直接复用为特效词汇表 |
| 16 | 运行环境为 Mono，非 IL2CPP | 托管脚本引擎无 AOT 障碍 |
| 17 | `LevelEventCategory` 是封闭枚举（8 个） | 自建分类需要额外工作 |

---

## 5. `.event` 规范

这一层是**产品契约**，因为文件要分发，冻结它对生态有利。

### 5.1 容器

ZIP，扩展名 `.event`。开发期同时支持解压目录，两种形态共用一个 `event.json`。

```
CameraWarp.event            (ZIP)
├── event.json
├── main.js
├── i18n/
│   ├── en.json
│   └── zh-CN.json
└── assets/
    └── noise.png
```

### 5.2 `event.json`

```json
{
  "schemaVersion": 1,
  "id": "rhytropy.camera-warp",
  "version": "1.0.0",
  "apiVersion": 1,
  "entry": "main.js",
  "icon": "assets/icon.png",
  "category": "VisualFx",
  "name": { "en": "Camera Warp", "zh-CN": "镜头扭曲" },
  "description": { "en": "…" },
  "params": [
    { "name": "strength", "type": "Float", "default": 0.8, "min": 0, "max": 2 },
    { "name": "duration", "type": "Float", "default": 2, "min": 0, "max": 16, "unit": "beats" },
    { "name": "ease",     "type": "Enum:Ease", "default": "OutQuad" },
    { "name": "color",    "type": "Color", "default": "ffffff" },
    { "name": "target",   "type": "Tile", "default": [0, "ThisTile"] }
  ]
}
```

规则：

1. **`id` 是身份，与文件名无关。**
2. **`params[].type` 必须取白名单形式**（§4 第 11、12 条）。枚举有两种写法：复用游戏内可解析的类型名，或降级为 `"String"` + `options` 由 Core 自行渲染。**具体走哪条，取决于验证结果，不预先冻结。**
3. **`schemaVersion` 与 `apiVersion` 分离**：前者约束 manifest，后者约束脚本 API。
4. `category` 在 v1 只用现有分类值。
5. `name`/`description` 支持字符串或 `{locale: string}`，至少要求 `en`。

### 5.3 身份与冲突

- 同 `id` 不同 `version` 视为同一事件类型的不同实现。
- 同 `id` 取版本最高者；同版本内容不同 → 保留先加载者，记录冲突并在设置界面报警，不静默覆盖。
- 实例记录写入时的 `version`；本地实现更新时按 §5.4 补齐参数后执行，不拒绝。

### 5.4 参数演进：只增不减

1. 实例保留**全部**原始参数字段，包括当前 manifest 中不存在的键。
2. 加载时用 `default` 补齐缺失键。
3. 保存时把所有原始键原样写回。

结果：升级或降级都不丢参数，最坏情况是脚本读到不认识的值。v1 不做迁移链。

### 5.5 缺失定义

找不到 `id` 时：数据原样保留；编辑器显示只读占位，展示 `id`、写入版本与原始参数。不删除、不改写。

---

## 6. 谱面内存储

### 6.1 必须满足的三条（冻结）

1. **原版能打开**（C1）。`EditorComment` 载体已实测通过；"写入未知 `eventType`"已实测失败（整关 actions 丢失）。
2. **模组能无损往返**（A4）。同一份数据解码后再编码，语义不变。
3. **可识别且不误伤**：只处理自己的命名空间，其他注释一字不碰。

### 6.2 当前方案（未冻结）

`EditorComment` 的 `comment` 字段承载：

```
@PEV1 {"id":"rhytropy.camera-warp","v":"1.0.0","uid":"a1b2c3","p":{"strength":0.8}}
```

- 前缀 `@PEV1`（不使用 `!`，该前缀在社区已被占用且有删除语义）。
- 单行紧凑；`floor` 用注释自身的；`p` 只存与默认值不同的键。
- `uid` 供编辑期定位与未来跨谱面复制。
- **绝不手工转义**：payload 作为普通字符串放入，交给游戏的 JSON 序列化器处理。

### 6.3 五条不变式（冻结，与载体无关）

1. 只识别自己的前缀，其它注释不处理。
2. 解析失败保留原文并记日志。
3. 不静默删除或覆盖任何注释。
4. 保存失败不能丢弃原始数据。
5. 版本不匹配不得静默错配。

> 若验证后更换载体，只需重新满足 §6.1 与 §6.3，产品要求不变。

### 6.4 往返观察矩阵

P0 阶段实测并记录（**不全部作为阻塞条件**）：

| 场景 | 期望 | 是否阻塞 |
|---|---|---|
| 模组存档 → 模组读档 | 完整恢复 | 是（A4） |
| 模组存档 → 纯原版打开 | 能打开 | 是（C1） |
| 模组存档 → 纯原版打开 → 原版保存 → 模组读档 | 内容不变（允许格式重排） | 否，观察项 |
| 原版删除该注释 → 模组读档 | 事件消失，无残留、不报错 | 是 |
| 缺 `.event` 文件 → 读档 | 占位显示，数据不变 | 是 |
| 补回 `.event` → 读档 | 恢复可编辑可执行 | 是 |

---

## 7. 编辑器接入

### 7.1 路线（未冻结）

**优先级：先做原生注册，跑不通再自绘。** 判断标准不是"够不够原生"，而是 A1–A6 的达成成本与后续维护成本。

已验证的有利条件：面板由 `GCS.levelEventsInfo` 数据驱动，注册条目大概率直接产出按钮与页签。

### 7.2 关键接入点（已实测）

| 序 | 位置 | 作用 | 状态 |
|---|---|---|---|
| 1 | 事件元数据注册（`levelEventsInfo` 等字典） | 让事件出现在快捷栏与属性面板 | **P0 完成** |
| 2 | `LevelData.Decode` | 把存储数据还原为编辑器中的事件 | **P0 完成** |
| 3 | `LevelData.EncodeToDictionary` | 把事件改写回存储形式（唯一存档出口） | **P0 完成** |
| 4 | `scnEditor.AddEvent` | 放置：新建数据、生成 uid、写入默认值 | **P2 完成** |
| 5 | `scnEditor.CopyEvent` | 复制：深拷贝数据并**换新 uid** | **P2 完成** |
| 6 | `scnEditor.RemoveEvent` | 删除：解除绑定，Bake 自然不再写回 | **P2 完成** |
| 7 | 本地化键 | 显示名称 | P3 |
| 8 | 图标与分类 | 视觉对齐 | 图标 P0 完成，本地化 P3 |

### 7.3 时机要求

- 注册必须早于 `InspectorPanel.Init`（已由 P0 实机确认）。
- 图标必须在 `scnEditor.LoadEditorProperties` 之前可用——元数据阶段 `GCS.levelEventIcons` 仍为 null。
- 注入必须早于编辑器把 `levelData.levelEvents` 复制到自身 `events`。

### 7.4 两条实现纪律

1. **复制必须换 uid。** 编辑器用 `CopyShallow` 复制 `LevelEvent`，我们的数据以对象引用为键，副本天然未绑定；即使绑上同一份数据，共用 uid 也会让保存时两条注释互相覆盖。

2. **凡是写入 `LevelEvent` 的值都要经过 `ParamCoercion`。** P2 实测：manifest 的 `"default": 2` 解析成 int，直接存入后 `Get<float>` 转型失败返回 0，属性面板显示的也是 0。这条对"从文件恢复"和"新建放置"两条入口同等适用。

### 7.5 权威数据

持久化权威是磁盘数据；内存中的事件对象是视图。两者不一致时以磁盘为准。

---

## 8. 脚本执行

### 8.1 要求（冻结，与引擎无关）

| 编号 | 要求 |
|---|---|
| S1 | 每个事件实例拥有独立执行环境与独立状态，不共享可变对象 |
| S2 | 单实例异常不得影响其它实例；异常需带 `id`/`uid` 记录 |
| S3 | 必须有执行量上限，防止死循环卡死游戏 |
| S4 | 不允许脚本访问任意反射、文件系统、网络、线程 |
| S5 | 全部在主线程执行，不引入 async |
| S6 | 脚本创建的所有对象归 Core 统一托管，退出时可无条件回收 |

### 8.2 引擎（已实测）

**Jint 4.17.0（net462 构建）+ Acornima 1.7.0**。P1 实测结论见 [P1-RESULTS.md](P1-RESULTS.md)。

| 项 | 实测值 |
|---|---|
| 加载 | Mono 下正常，无需 AOT 处理 |
| 体积 | `Jint.dll` 2.6 MB + `Acornima.dll` 0.37 MB |
| 单次 `update()` | 最差 0.028 ms |
| 死循环 | 语句预算拦截，3 ms |
| 递归 | 深度上限拦截 |
| CLR 访问 | `System is not defined` |
| `eval` | 已禁用 |
| 跨脚本污染 | 独立引擎，不外溢 |

**沙箱配置**（缺一不可）：

```
MaxStatements(200_000)          // 主防线：确定性，不受预热/GC 影响
TimeoutInterval(50ms)           // 兜底：8 ms 会误杀新引擎的首次调用
LimitRecursion(64)
Strict()
Interop.Enabled = false
Interop.AllowWrite = false
Host.StringCompilationAllowed = false
StackOverflowGuard = true       // 不加会静默杀进程
```

**边界设计**：不向脚本投影任何 CLR 对象。脚本只看到 JSON 解析出的普通值；它请求效果时通过宿主函数传 JSON 字符串。这让 `Interop.Enabled = false` 名副其实，而不是"理论上关掉了"。

### 8.3 生命周期接口

```js
function create(ctx, params) {
  return { update(t) {}, dispose() {} };
}
```

已实现并验证。`t` 是相对该事件所在格的秒数。

### 8.4 请求效果的脚本接口

```js
fx.flash({ color: "ff8844", duration: 0.5, alpha: 0.3 });
fx.shake({ strength: 0.2, duration: 0.4 });
```

未列入白名单的名称会返回 `{ ok:false, error:"unsupported effect ..." }`，而不是静默失败。

---

## 9. 游戏 API 边界

分三层。**第 0、1 层为必须；第 2 层性价比最高，应优先实现。**

### 第 0 层：只读世界

```js
ctx.time;      // 当前歌曲时间（秒）
ctx.eventTime; // 本事件所在格 entryTime
ctx.t;         // ctx.time - ctx.eventTime
ctx.beat;
ctx.params;    // 冻结的参数快照
ctx.floorSeq;
ctx.isPlaying;
ctx.isPaused;

game.floor(i);   // { seqID, entryTime, angle, posX, posY, isCCW, speed }
game.planet(i);  // { angle, radius, speed }
```

### 第 1 层：特效与对象

```js
fx.create({ parent: "world" });   // 返回由 Core 托管生命周期的句柄
obj.transform; obj.sprite; obj.renderer; obj.particle;
fx.camera({...}); fx.shake({...}); fx.screenColor({...});
```

### 第 2 层：借用原生效果

```js
fx.playNative("Flash", { duration: 0.3, color: "ffffff" });
```

游戏自带一整套 `ffx*` 效果组件。把这套词汇表暴露给脚本，等于让作者直接获得官方全部特效能力，而 Core 只需实例化并赋参。**这是让 B4（实现官方没有的效果）最快达成的路径。**

### 不开放

任意 C# 反射、`System.IO`、网络、`Thread`、直接持有游戏内部对象。

---

## 10. 时间与生命周期

### 10.1 唯一时钟

```csharp
songTime := scrConductor.instance.songposition_minusi
t         := songTime - floor.entryTime
```

效果时间一律由 `t` 计算，**不累积 `deltaTime`**。由此自动覆盖暂停、重开、检查点恢复、关卡内跳转、变速变调。

### 10.2 状态机

| 条件 | 动作 |
|---|---|
| `songTime < entryTime` | 未创建 |
| 首次越过 `entryTime` | `create(ctx, params)` |
| 播放中 | `update(t)` |
| 暂停 | 不调用 `update`（`t` 冻结） |
| `t` 回退 | `dispose()`，标记未创建 |
| 退出关卡 | 全部 `dispose()` |

### 10.3 清理兜底

Core 维护自有根 `GameObject`，脚本对象挂其下；退出时除 `dispose()` 外**无条件销毁根节点**。即使脚本异常、忘记释放或被执行上限中断，也不污染下一关。

---

## 11. 维护策略

### 11.1 收敛纪律

所有对游戏内部的访问收敛到 `GameBridge` 单个文件，对外只暴露纯数据与委托：

```csharp
float      SongTime();
int        FloorCount();
FloorInfo  GetFloor(int seqID);   // struct，仅 float/int/bool
void       SpawnNative(string effect, ParamBag p);
```

不泄漏游戏类型。游戏更新时只改一个文件。

### 11.2 Harmony 使用原则

- 能少用就少用；优先选择"数据驱动接口"而非"IL 改写"。
- 每一处 patch 必须能说明：如果这处被游戏更新破坏，失败表现是什么。
- patch 集中在少数文件，便于版本升级时快速定位。

---

## 12. 验证清单

Stage 0、P0、P1 均已执行，结果见 [STAGE0-RESULTS.md](STAGE0-RESULTS.md)、[P0-RESULTS.md](P0-RESULTS.md)、[P1-RESULTS.md](P1-RESULTS.md)。

| # | 验证 | 结果 | 影响 |
|---|---|---|---|
| V1 | 数字型事件名能否免除大量 transpiler | **通过**：`ParseEnum` / `Enum.Parse` / `TryParse` 三条路径均正确 | 无需 patch `ParseEnum` |
| V2 | 枚举参数能否被解析 | **通过（需限定名）**：`Enum:NS.Type, Asm` 可解析；裸类型名不可 | 枚举参数写法有约束 |
| V3 | 注册后快捷栏与面板能否正确重建 | **通过**：建出按钮，`ShowPanel` 无异常 | 无需自绘 |
| V3c | 图标是否必需 | **必需**：缺失导致 `KeyNotFoundException`，面板构建中断 | 注册必须带图标 |
| V4 | 虚拟事件注入与实例隔离 | **通过**：注入成功、可选中、参数互不干扰 | 方案成立 |
| V5 | 纯原版能否打开含自定义数据的关卡 | **通过**：事件全保留，无关注释未受影响 | C1 满足 |
| V6 | 原版保存后数据是否保留 | **通过**：原版重新编码后 payload 仍在 | 强于 C3 要求 |
| V7 | Jint 在 Mono 上的逐帧开销与沙箱 | **通过**：最差 0.028 ms；五项沙箱检查全部拦截 | 引擎选型确定 |

补充反证：未知 `eventType` 会让整关 `actions` 全部丢失（几何尚在）。两条失败路径——非数字名触发 NRE，数字名触发 `KeyNotFoundException`——都必须避免。

---

## 13. 实现顺序

**Stage 0 — 验证（已完成）**
结果见 [STAGE0-RESULTS.md](STAGE0-RESULTS.md)。V1–V6 全部通过，V7（Jint）留给 P1。
产物：`probe/Probe/`，可复用的最小探针，含注册、注入、编解码、原版对照四种能力。

**P0 — 存储闭环（已完成）**
结果见 [P0-RESULTS.md](P0-RESULTS.md)。`.event` 读取 → 注册 → 写盘 → 重开恢复全部打通，多实例独立、未知数据保留均已实测。
产物：正式工程 `src/PEV.Core/`，以及示例包 `src/Events/`。

**P1 — 执行闭环（已完成）**
结果见 [P1-RESULTS.md](P1-RESULTS.md)。Jint 接入、`EventContext`、实例隔离、沙箱五项、暂停与重开全部实测通过。
产物：`src/PEV.Core/Scripting/`，以及两个可运行的示例事件。

**P2 — 编辑器闭环（已完成）**
结果见 [P2-RESULTS.md](P2-RESULTS.md)。放置、编辑、复制、删除、编码、重新解码全部实测通过；复制换 uid、放置默认值类型转换两处已落地。
产物：`src/PEV.Core/Editor/EditorIntegration.cs`。

**P3 — 交付（已完成主体）**
结果见 [P3-RESULTS.md](P3-RESULTS.md)。撤销/重做缺陷已修复并验证；本地化、错误提示、打包工具、SDK 文档均已落地。
产物：`src/PEV.Core/Editor/`、`tools/pack_event.py`、`docs/EVENT-SDK.md`。

**未完成，按影响排序**

1. 真实关卡验收 —— 现有验证全部在合成谱面上
2. 持续型效果案例 —— 验证 `update(t)` 的逐帧演算路径
3. 真实鼠标输入与属性面板控件交互
4. UMM 设置面板（`EditorReport.Details()` 已预留）

---

## 14. 风险登记

| 风险 | 概率 | 影响 | 应对 |
|---|---|---|---|
| 原生注册需要大量 IL 改写 | **已排除** | — | V1 实测通过，无需 patch `ParseEnum` |
| 图标缺失导致编辑器损坏 | 高（易犯） | 编辑器整屏报错 | 注册流程强制兜底图标 |
| 游戏更新改内部字段/结构 | 高 | 需改代码 | 全部收敛到 `GameBridge` |
| 原版用户编辑保存后数据损坏 | 中 | 用户数据丢失 | 实测（V6）后决定加提示或备份；非阻塞 |
| 参数类型名写错导致控件不显示 | 中 | 作者困惑 | SDK 白名单 + 加载期校验并报错 |
| 参数值类型与编码期强转不符 | **已发生** | 保存时 `InvalidCastException` | `ParamCoercion` 按 `PropertyType` 转换（P0 已修） |
| 回写时丢失语义（枚举变数字） | **已发生** | 文件不可读、跨版本易错 | `StorageValue` 反向转换（P0 已修） |
| 按前缀无差别清理注释 | **已发生** | 静默丢用户数据 | 只删除 uid 匹配的条目（P0 已修） |
| 逐帧脚本开销 | **已排除** | — | 实测最差 0.028 ms/实例 |
| 脚本死循环 | 中 | 卡死 | 语句数 + 超时 + 递归上限 + 栈保护（P1 已实测拦截） |
| 超时预算误杀正常脚本 | **已发生** | 实例被无端判死 | 主防线改为 `MaxStatements`，墙钟仅兜底（P1 已修） |
| 时钟抖动被误判为重绕 | **已发生** | 实例每秒被重置多次 | 阈值 50 ms + 触发后重锚基线（P1 已修） |
| 误以为 `paused` 会冻结时钟 | **已发生** | 暂停期间脚本仍在跑 | 改用时钟增量判定播放状态（P1 已修） |
| 复制后共用 uid | **已防住** | 两条注释互相覆盖 | `CopyEvent` 钩子强制换 uid（P2 落地） |
| 放置时默认值未经类型转换 | **已发生** | 面板显示 0 而非默认值 | 放置路径也走 `ParamCoercion`（P2 已修） |
| 撤销/重做后绑定丢失 | **已发生并修复** | 撤销一次再保存，自定义事件全部消失 | 绑定改为 uid 键 + 隐藏键随身携带（P3 已修） |
| 错误只写日志、作者看不到 | **已修** | 缺失/损坏事件无任何提示 | 开档时汇总弹一次通知（P3 落地） |
| 自定义类型悬停显示 `editor.100001` | **已修** | 界面出现原始键名 | 拦截 `RDString.GetWithCheck` 补名称（P3 落地） |
| 缺失事件在轨道上不可见 | **已修** | 作者以为事件丢了 | `pev.missing` 占位，原字节写回（P3 落地） |
| 占位事件被改写导致数据丢失 | **已防住** | 装上事件包后无法恢复 | 占位保存时返回原始注释，不重建（P3 落地） |
| 与其他模组前缀冲突 | 低 | 数据互删 | 命名空间前缀，只处理自身前缀 |
| 版本错配 | 中 | 参数错乱 | 只增不减策略 |

---

## 附：本地工件

- `_research/`：调研阶段克隆的参考仓库（约 143 MB），纯参考，可随时删除。
- `Mods\CustomEventsProbe\`：第三方库与一个探针。**不构成依赖。** 其运行日志记录了一个有价值的事实：多个实例共享同一事件对象会导致参数互相覆盖——这正是验收项 A6 的反面案例。目录可随时删除。
