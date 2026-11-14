# 交接文档 — 底层框架与 `.event` 事件

> 写于 2026-11-15，commit `e9f93cd` 之后。
> 对象：接手这个仓库的下一个 Agent。
> 目的：让你不必重新踩一遍已经踩过的坑，并且**不要把两层的边界搞混**。
>
> 前置阅读：`ARCHITECTURE-v1.md`（产品要求与验收标准，已冻结）、`docs/EVENT-SDK.md`（作者视角的 API）、
> `docs/NOISEFIELD-SPEC.md`（噪声域的完整技术细节）。本文是这三份之间的**连接件**：说清楚哪一层负责什么、
> 哪些结论是实测的、哪些地方看着能做其实做不了。

---

## 0. 一句话分层

**底层框架（PEV.Core，一个 UMM Mod）决定「能做什么」的上限；`.event` 事件包决定「用上限做出什么」。**

这个仓库里 90% 的坑来自把这两件事混在一起谈。判断标准很简单：

| 问题 | 属于哪一层 | 改哪里 |
|---|---|---|
| 「作者能不能在面板里拖动这个参数？」 | 框架 | `Registry/`、`Model/EventManifest.cs` |
| 「这个事件放到第二格会不会和第一格打架？」 | 框架 + 事件 | `Scripting/`、事件脚本的 key 生成 |
| 「这个效果的颜色对不对？」 | 事件 | 该事件的 `main.js` / 参数默认值 |
| 「烘焙会不会掉帧？」 | 框架 | `Scripting/NoiseTextureFactory.cs` |
| 「海岸线看起来乱不乱？」 | 事件 | `Scripting/NoiseComposer.cs`（但渲染入口是框架给的） |

最后一条是唯一的例外：噪声域的**算法**住在框架里，因为它需要主线程外的大块像素计算、纹理生命周期和
Material 控制，这些能力脚本层没有、也不该有。这是「事件需要一个框架级原语」的例子，见 §5.3。

---

## 1. 底层框架

### 1.1 目标是「翻译层 + 执行器 + API 面」

`ARCHITECTURE-v1.md` §0 定死了 Core 的定位：不是插件生态、不是 Host、不是包管理器。
它的全部职责是四件事，一件不能少、一件不能多：

```
   ADOFAI / Unity
        ▲
   Game API      把游戏能力整理成稳定、可白名单化的接口
        ▲
 Script Executor  Jint 沙箱，每个实例一个引擎
        ▲
 Editor Adapter  让事件出现在编辑器里、可放置、可编辑、可保存
        ▲
 Event File Loader 扫描、校验、加载 .event
```

### 1.2 模块地图（按依赖顺序）

| 目录 | 职责 | 关键约束 |
|---|---|---|
| `Loading/EventLoader.cs` | 扫描 `Events/*.event`（ZIP 或目录），读 manifest 与资源 | 只读，不写；`ReadResource` 是包内资源的唯一入口 |
| `Model/EventManifest.cs` | 解析 `event.json`，`ResolveLabel` 选当前语言 | **面板只读一次 customLabel**，所以语言必须在构造 PropertyInfo 前决定 |
| `Registry/EventRegistry.cs` | 把 manifest 注册进 `GCS.levelEventsInfo` | 必须早于编辑器建面板 |
| `Registry/DecorationParams.cs` | 装饰型事件的参数注入 AddDecoration | 必须早于 `PropertiesPanel.Init`（只走一遍） |
| `Registry/EnumLabels.cs` | 枚举选项名的本地化 | 只能写游戏自己的本地化表，见 §1.4 |
| `Storage/` | 关卡文件的读/写/迁移 | 只增不减；未知键原样保留 |
| `Scripting/RuntimeHost.cs` | 单例，按歌曲时钟驱动所有实例 | 时钟只来自 `songposition_minusi` |
| `Scripting/EventInstance.cs` | 一个已放置事件的阶段机 | 时间 = 歌曲时钟 − 该格 entryTime |
| `Scripting/ScriptHost.cs` | Jint 引擎 + `fx.*` 引导脚本 | 白名单，`Interop.Enabled = false` |
| `Scripting/EventContext.cs` | `GameBridge`：脚本能碰到的全部特效 | **这是 API 面的唯一出口** |
| `Scripting/EffectCanvas.cs` | 脚本创建对象的统一父节点 | 不建独立相机/图层 |
| `Editor/` | 编辑器集成与预览 | 补丁点极少，见 §1.3 |

### 1.3 Harmony 补丁点（尽量少，改动时优先考虑这里）

真正属于框架的补丁（不计各 `*Harness.cs`）：

| 目标 | 时机 | 为什么必须在这里 |
|---|---|---|
| `ADOStartup.SetupLevelEventsInfo` | Postfix | 注册必须在游戏建表之后、建面板之前 |
| `scnEditor.LoadEditorProperties` | Prefix | 图标与装饰参数必须在 `PropertiesPanel.Init` 之前就位 |
| `LevelData.Decode` | — | 所有加载路径的唯一入口 |
| `LevelData.EncodeToDictionary` | Prefix + Postfix | 所有保存路径的唯一入口；Prefix 必须写回 payload |
| `scnEditor.Play` | — | 重新武装运行时 |
| `scnEditor.Start` | — | 编辑器预览 attach |
| `RDString.GetWithCheck` | Postfix | 脚本/参数的运行时键回退 |
| `LevelEvent AddEvent/Copy/Remove`（`EditorIntegration`） | Postfix/Prefix | 放置、复制、删除时同步我们的数据 |
| `PropertiesPanel`（`PlaceholderReadOnlyPatch`） | Postfix | 占位事件只读 |

**新增补丁前先问：能不能通过数据层解决？** 目前全部编辑器行为都是数据驱动的
（`ARCHITECTURE-v1.md` §4 第 10 条），这是这个项目最大的可维护性资产。

### 1.4 三条容易被误判为「框架做不了」的事实

1. **枚举选项名不能走 customLabel。** 面板对真实 enum 强制 `localizeEnumStrings`，
   下拉项在绘制时问 `RDString.GetEnumValue("enum.<类型>.<值>")`，`customLabels` 被忽略。
   唯一的通道是往游戏自己的本地化表里写这个键——`Registry/EnumLabels.cs` 就是干这个的。
   表的 section 是公开可变字典，且一个会话只填充一次，所以写进去的条目会跨语言切换保留。

2. **装饰型事件的参数只能寄生在 AddDecoration 的面板里。** 没有「每行一个面板」这种东西；
   所有参数靠 `showIf decorationImage == "pev/field"` 挂在原生面板上，因此：
   - 与原生同名的参数（scale/rotation/color/opacity/depth/tag）**必须复用原生的**，不要重复声明；
   - `showIf` 必须是 `List<object>`，`string[]` 会让 `DecodeStringArray` 返回 null 并抛 NRE。

3. **`propertyInfo` 的 `encode` / `canBeDisabled` 有语义。** 装饰参数写 `encode = false`
   （值只在 payload 里存一份，避免给每个原生装饰添加无用键），`canBeDisabled = false`
   （关掉会显示默认值，那是无意义状态）。

### 1.5 存储：C 要求是硬约束

原版打开关卡时，**未知 eventType 会让整关加载失败**（`info == null` → NRE，外层无 try/catch）。
所以落盘形式只有两种：

- `EditorComment` + `@PEV1` 前缀（普通事件）；
- 真正的 `AddDecoration` 行 + `decorationImage = "pev/field"` 标记（装饰型事件）。

后者能成立，是因为标记指向一个**不存在的文件**：原版加载得到 null sprite，什么也不画，也不抛错；
而 `scrDecorationManager` 仍然索引这一行，于是 `MoveDecorations` 按 tag 找得到它、编辑器 gizmo 拖得动它——
**这条路径里没有我们的一行代码**。这就是「像装饰一样」的实现方式，不是模仿。

配套的两条经验：

- 新建字段的装饰必须带一个非空 tag。否则 `scrDecoration.Setup` 在 `ResetDecorations` 之后
  会去删 `"NO TAG"` 键，查不到就抛异常，播放直接中断。
- 参数演进**只增不减**：`EventInstanceData.Raw` 保留全部原始键，缺的用 default 补上，
  存的时候全写回。

### 1.6 时间模型（最容易写错的地方）

- 唯一时间源：`scrConductor.instance.songposition_minusi`。
- 实例的 `t = 歌曲时钟 − 该格 entryTime`，`entryTime` **在实例开始那一帧才解析**，
  不能在建实例时抓——那时路径还没重建，取到的是 NaN。
- 暂停 = 时钟冻结，不是枚举状态。
- 回退检测有阈值（实测抖动几毫秒），触发后 `dispose()` 再重新 `create()`。
- **绝对不要累加 deltaTime。** 暂停、检查点、中途跳转全都会错。

### 1.7 沙箱的真实边界

`Scripting/ScriptHost.cs` 的实际配置（不是文档愿景）：

- `Interop.Enabled = false`，`Host.StringCompilationAllowed = false` → 没有 .NET 类型、没有 `eval`。
- `LimitRecursion(64)`，`StackOverflowGuard = true`（脚本栈溢出会杀进程，这个守卫不是可选项）。
- `TimeoutInterval(2000ms)` —— 这个数字**故意很宽松**，因为 Jint 把 host delegate 的时间也算进去：
  一次字段纹理构建被计成脚本时间，50ms 会让三个实例里两个直接失效。
  真正的守卫是语句数预算（`MaxStatements`），它精确且与主机调用无关。
- 每个实例一个 `Engine`，所以原型污染不跨实例；代价是不能靠全局变量共享状态。

---

## 2. `.event` 事件层

### 2.1 契约与实现的分界

`.event` 是**产品契约**（要分发，冻结它有利于生态）；包里的 `main.js` 是**实现**。
判断一个新能力该放哪边：**如果第三方作者需要用它，它就得是契约**；如果只有我们自己的事件用，它可以是内部原语。

| 契约（对作者稳定） | 内部（可随时改） |
|---|---|
| `event.json` 字段与参数类型白名单 | `fx.field` / `fx.fieldMove` / `fx.fieldState` |
| `create(ctx, params) → {update, dispose}` | `NoiseComposer` 的任何常量 |
| `fx.flash` / `fx.shake` / `fx.create` / `fx.set` / `fx.destroy` | `DecorationCarrier` 的标记字符串约定 |
| `ctx` 字段 | 参数注入的 group / order 细节 |

注意 `fx.field*` 在 `EventContext.cs` 里已经明确注释为「host primitive, not part of the
author-facing SDK」。保持这个区分——否则一次重构会变成一次破坏性变更。

### 2.2 目前能做的事（一个脚本实际能调用的全部）

```
fx.flash({color, duration, alpha})        一次性闪光
fx.shake({strength, duration})            一次性震屏
fx.create(key, {color, scale, alpha, sortingOrder})   建一个白色 SpriteRenderer
fx.set(key, {x, y, scale, alpha, angle, color})       每帧更新
fx.destroy(key)                                       清理

fx.field(key, {...})        噪声域（内部原语，27 个参数）
fx.fieldMove / fx.fieldState  按 tag 改别的实例的 offset/rotation/scale/alpha/visibility
```

`fx.create` 画的是**纯色矩形**（`SolidSprite.White` 的 SpriteRenderer），不是任意图形。
想画复杂图形，只有两条路：框架里加一个合成器原语（像噪声域那样），或者用很多个小矩形拼。

### 2.3 噪声域这一条线的完整工作历程

这部分值得单独记，因为它几乎把「猜 → 抄 → 量」三种工作方式都走了一遍。

**用户的原始要求**（`ARCHITECTURE-v1.md` §1 的「结果优先」原则的具体化）：
做一个放在谱面里的噪声区域效果，观感对齐 Phira-Pro 的 noise skin。用户反复强调的只有一句：
**「明明有开源实现，你非得自己瞎猜。」**

**实际走过的五个阶段**：

1. **凭感觉调参**（`b8aeb7c` 前后）——用 FBM + 距离场拼凑。结果「一点都不像」。
   反思：这一步唯一的价值是暴露了「噪声域不是噪声纹理，是三层合成」这个认知缺口，代价是几轮无效迭代。

2. **照着手册重写**（`79a52a3`）——读了 `render.rs::mask_polygons` 的二手描述，
   做了「带符号距离 + 滑动噪声阈值」。能出缺口和碎片，但观感仍然不对。
   这条路后来被整体删除（385 行死代码），教训见下面第 5 条。

3. **逐 pass 移植**（`4a8c775`）——直接读 `composeActive.glsl` / `edge.glsl` / `glow.glsl` / `main.glsl`，
   常数逐个抄。这一步才真正对上。关键认知：
   - 覆盖是**矩形并集**，不是噪声等值线；噪声只位移「采样点」；
   - 边框 = 1/4 目标对 1/8 目标做 9 点 max 再减原值；
   - 辉光是 5 遍 ping-pong，权重逐遍衰减；
   - 合成是预乘 alpha，`Sprites/Default` 是 SrcAlpha，所以要除回去。
   纹理也必须原样加载（`NoiseTextures.cs`）——海岸的碎块分布由那几张具体像素值决定，
   合成替代品必然是另一个效果。

4. **架构改造**（`02d2fd3`）——从 EditorComment 改成真正的 AddDecoration 行，
   于是 MoveDecorations / gizmo / 原生 tween 全部白送。

5. **冻结内部、修复观感**（`e9f93cd`）——用户说「边缘已经有点像了，但 Phira 是流动，你像杂乱随机」。
   量化后定位到两件事：
   - **网格基准错了**：参考的 `maskPixelScale = 8` 是**屏幕**的 1/8，我按**纹理**的 1/8 取；
     纹理只占屏幕一小部分，导致台阶粗 4–5 倍，同一套位移把海岸甩成整块矩形。改用 `ScreenPixelWidth` 后，
     帧间海岸变化从均值 13% 降到 1.58%，峰值从 3.77% 降到 2.36%。
   - **烘焙卡顿**：实测 92ms（后因细网格涨到约 300ms）全在主线程，拆成后台计算 + 分帧上传后
     主线程最坏 5.6ms（预算 120ms）。

**如果我能重来**：第一件事应该是**打开参考的 shader 文件**，而不是打开参考的视频。
整个 2 到 3 之间浪费的轮次，根因都是「用观感反推实现」而不是「读实现」。

### 2.4 事件层自己的坑

1. **`key` 必须每个实例唯一。** 同一事件放两次而 key 相同，两个实例抢同一个对象，后写的覆盖先写的。
   惯用写法是拼 `ctx.floorSeq`。
2. **参数类型写错不报错，控件静默消失。** 必须取 §4 第 11 条的白名单字面量。
3. **枚举参数**：`"Enum:<全限定名>, <程序集名>"` 才能被 `Type.GetType` 解析，
   且 `Type.GetType` 只硬编码特判了 `Ease` / `ParticleArcMode`。
4. **图标缺失会中断整个面板构建**，不是「这个事件没图标」而已。
5. **`t` 是相对时间**，不是累计帧时间；暂停冻结、重开归零。

---

## 3. 能力上限：诚实回答「最极限能做到什么」

分三层回答，因为「能做到什么」在不同层的答案差别很大。

### 3.1 事件脚本层（第三方作者写的 `main.js`）

**上限约为 Unity 的「摆放矩形 + 改颜色 + 全屏闪光/震动」子集。**

具体：能逐帧控制任意多个白色 SpriteRenderer 的位置、缩放、旋转、颜色、透明度和排序。
够做粒子雨、色块、条形频谱、准星、遮罩式的闪烁——所有**由矩形拼得出来**的东西。

**明确做不到**（不是没实现，是这层的设计边界）：

- 自定义 shader / 材质 —— 没有 `Material` 访问，只有 `SpriteRenderer.color`；
- 任意贴图 —— 没有从事件包里读图给脚本用的接口（`ReadResource` 目前只服务图标与入口脚本）；
- 访问其他 GameObject / 组件 —— 没有 reflection，没有 scene 查询；
- 读写关卡/文件/网络 —— 被沙箱禁止，这是产品要求（`ARCHITECTURE-v1.md` 非目标）；
- 异步 / 协程 —— 全部在主线程同步执行。

### 3.2 框架层（我们自己的事件能用什么）

框架层**没有架构上的能力上限**，只有工程代价上限。原因是它是 Unity Mono 环境 + Harmony，
理论上可以：

- 直接 `AddComponent` 任意 Unity 组件；
- 自己写 shader 并 `Material` 绑定（噪声域就是 CPU 端口，但 GPU 路径同样可行）；
- 读写任意游戏内部状态（需要接受版本升级带来的维护成本）。

**真正的约束只有三条**：

1. **版本跟进成本。** 每碰一处游戏内部类型，就是一处需要在游戏更新时复查的地方。
   目前的补丁点（§1.3）刻意保持少。
2. **C 要求。** 落盘形式必须是原版能容忍的，这排除了「自定义 eventType」这类干净方案。
3. **性能预算。** 主线程每帧只有那么多；噪声域的烘焙之所以要拆线程，就是这条。

所以「能不能做到 Unity 理论上能做的一切」——**框架层：能，但每加一项都要付维护费。
脚本层：不能，也不该。** 脚本层的限制本身就是产品要求（沙箱、非目标）。

### 3.3 中间地带的判断准则

当作者需要一个脚本层做不了的视觉效果时，正确做法**不是**放宽沙箱，而是：

1. 判断它是**一个原语**（一条能力，参数化后可供多个事件复用）还是**一个效果**；
2. 若是原语 → 加到 `GameBridge`，并在 `EVENT-SDK.md` 里写清它是内部还是契约；
3. 若是效果 → 用现有原语在事件里实现，或者把这个效果整体做成一个事件包。

噪声域是反例也是范例：它被做成「原语（`fx.field`）+ 自己的事件包」的组合，
因为这个效果需要大块像素计算和纹理生命周期，脚本层给不了，而它本身又要有 27 个可调参数。

---

## 4. 开放了多少接口

| 层 | 接口 | 稳定度 |
|---|---|---|
| 包格式 | `event.json` schema + 参数类型白名单 | 契约，改动需升 `schemaVersion` |
| 脚本 | `create/update/dispose` + `ctx` 字段 | 契约，改动需升 `apiVersion` |
| 特效 | `fx.flash` / `fx.shake` / `fx.create` / `fx.set` / `fx.destroy` | 契约 |
| 特效 | `fx.field` / `fx.fieldMove` / `fx.fieldState` | **内部原语**，可随时改 |
| 存储 | `@PEV1` payload 格式 | 契约（数据要能被旧版本读回） |

**新加接口前先分类**：作者会不会用？会 → 契约，要写进 `EVENT-SDK.md` 并考虑版本；
不会 → 内部，可以自由改。混淆这两者是这个项目最容易引入的长期债务。

---

## 5. 给下一个 Agent 的接手建议

### 5.1 先建立验证闭环，再改代码

仓库里每个功能都有一套 `*Harness.cs`，靠 `pn1-test.txt` 之类的开关启用，结果写进
`Mods/PEV.Core/pn*-results.txt`，另外会 dump 纹理和截图。这个闭环是整个项目最值钱的部分——
改任何渲染相关的东西之前，先确认「你怎么知道它变好了」。

实测流程（本机）：

```bash
# 构建 + 部署
cd src/PEV.Core && dotnet build -c Release
cp bin/Release/PEV.Core.dll "D:/Program Files (x86)/Steam/steamapps/common/A Dance of Fire and Ice/Mods/PEV.Core/"

# 跑 harness（游戏必须重启才能加载新 DLL）
cd "D:/Program Files (x86)/Steam/steamapps/common/A Dance of Fire and Ice"
rm -f "Mods/PEV.Core/pn1-results.txt" && ./"A Dance of Fire and Ice.exe"
# 等 pn1-results.txt 出现 DONE 那一行
```

### 5.2 具体的技术建议

1. **先看参考实现，再看观感。** 见 §2.3 的第 5 阶段。
2. **量化「像不像」。** 相邻帧差异、海岸轮廓变化率这类数字比截图更容易定位问题。
   `_research/frames_*.png` 就是当初用来做连续帧对比的接触表。
3. **烘焙/生成这类工作永远不要留在主线程。** 现在的形态：后台算像素（限一半核心）+ 分帧上传 +
   新集合就绪前继续显示旧集合。任何新的重计算原语都应该照抄这个形状。
4. **补丁点只在无处可去时才加。** 优先级：数据层 > 生命周期钩子 > Harmony。
5. **装饰型事件的参数**：先查 `AddDecoration` 有没有同名原生属性，有就复用。
6. **改完记得跑完整 harness**，不只是自己那一项。`REGEN-IDLE`、`CLEANUP`、`PANEL-NATIVE`
   这几项专门用来抓「结构没变却每帧重算」和「资源泄漏」这类退步。

### 5.3 已知的债（按优先级）

| 项 | 说明 | 建议 |
|---|---|---|
| `PN1-RESULTS.md` 过期 | 早于多次重写 | 下次跑完 harness 后整体重写 |
| `docs/EVENT-SDK.md` 缺 `decoration: true` | 装饰型事件的 manifest 字段没写进作者文档 | 补一节，说明它会把参数注入 AddDecoration 面板 |
| `NoiseFieldRelativeTo` / `NoiseFieldAnchor` | 枚举已定义但渲染器不再使用 | 要么接上，要么删掉 |
| 事件包不能携带运行期资源 | `ReadResource` 存在但只给图标和入口脚本用 | 若要做「作者自带贴图」，这是要开的口子；开之前想清楚沙箱语义 |
| `dist/NoiseField.event/` 空目录 | 打包残留 | 删 |
| 枚举选项名依赖运行时写表 | 见 §1.4 第 1 条；游戏若改成不可变表就会失效 | 在 `EnumLabels` 里留了回读自检，升级时看 harness 的 `LABEL-ENUM` 行 |

### 5.4 不要做的事

- **不要**为了「看起来更像原生」而加补丁——`ARCHITECTURE-v1.md` §1 明确反对。
- **不要**把 `fx.field*` 写进面向作者的文档并承诺稳定。
- **不要**在脚本层开 reflection / 文件 / 网络，即使是「为了做一个很酷的效果」。
- **不要**把 `.event` 的默认参数当成框架逻辑——那属于事件，改它不需要碰框架。
- **不要**在 `LevelData.Decode` / `EncodeToDictionary` 之外再找存储入口；只有这两个。

### 5.5 一句话总结

**框架少而稳（补丁点少、数据驱动、只增不减）；事件厚而自由（参数、脚本、包，随便改）。**
把新能力往正确的那一层放，是这个项目唯一需要长期坚持的纪律。
