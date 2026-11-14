# 噪声域（Noise Field）事件 — 实现技术规范

> 读者：接手实现的工程师。本文假设你已读过 `ARCHITECTURE-v1.md` 与 `docs/EVENT-SDK.md`。
> 目标不是说服你，而是让你不必再自己调研一遍。
>
> 环境：ADOFAI `r150`（2026/11/03），Unity `6000.3.25f1`，Mono，UMM `0.32.5.0`。

---

## 接手第一步

不要从头读。按这个顺序：

1. **§0 证据等级** —— 30 秒，决定你信哪条、验哪条。
2. **§14 Q1** —— 半天。验 `SpriteRenderer` 的 UV 偏移是否生效。**这条不成立会改纹理生成器的形态**，所以必须先做。
3. **§11 P-N1** —— 1 天。做出第一块会动的噪声。做完这一步，剩下的都是工作量，不是风险。
4. **§5 + §10** —— 参数表与包结构，写代码时对照用。
5. 其余章节按需查。

如果只记一句话：**难点不在渲染，在坐标系、时钟和资源回收。**

---

## 0. 证据等级

本文所有结论标记来源。**不要跳过标记**——标 `[未知]` 的地方如果按 `[实测]` 对待，会返工。

| 标记 | 含义 | 可信度 |
|---|---|---|
| `[实测]` | 本仓库 P0–P4 跑出来的结果，见对应 `P*-RESULTS.md` | 最高，直接采信 |
| `[代码]` | 当前 `src/PEV.Core/` 代码里的事实，可直接读 | 高 |
| `[源码文档]` | `_research/7thRhythmDocuments/adofai/docs/` 的反编译文档。**注意：游戏源码目录 `7thRhythmSource/` 在本机是空的**，所以这是二手资料，有出错可能 | 中高，用前建议抽查 |
| `[推断]` | 由上述事实推出，未单独验证 | 中，按假设对待 |
| `[未知]` | 必须实测才能定，本文给出验证方法 | 无，不要假设 |

**交付前的最小要求**：所有 `[未知]` 至少被验证过一次，且结果回写到本文。

---

## 1. 目标与边界

### 1.1 做什么

在 ADOFAI 关卡里放置一个**可定位、可缩放、可移动的噪声区域**，视觉上复现 Phigros 第九章「噪声域」的观感：区域内出现滚动的红色噪声颗粒与横向噪线，随音乐时间演化，可淡入淡出。

### 1.2 不做什么（冻结）

| 不做 | 原因 |
|---|---|
| 判定拦截（区域内强制 miss / 变红） | 要动 `scrPlayer.Hit` / `scrMisc.GetHitMargin`，是全游戏判定的公共出口。失败表现是"所有谱面判定都错"，违反 §11.2 的 patch 纪律 |
| 触摸/输入屏蔽 | 同上，属于判定侧 |
| 音频低通 | 要动 `AudioListener` 滤镜链，且 ADOFAI 没有现成入口 |
| 复制 Phigros 的任何纹理、shader、代码 | 资源与代码版权均属原厂；PhiStudio-Rust / PhiraiAd 是 GPL。**只按原理重写** |
| 全屏效果 | 只做区域。全屏 glitch 用游戏自带的 `SetFilter` / `SetFilterAdvanced` 事件即可，不需要我们实现 |

### 1.3 验收标准

不是「看起来像 Phigros」，而是：

1. 同一噪声强度下，颗粒分布、密度、颜色接近原版。
2. 纹理运动速度、演化方式、采样密度接近原版。
3. 亮度、饱和度等参数变化时，响应规律接近原版。
4. 几何区域能正确贴合 ADOFAI 坐标系，不因区域大小或镜头缩放产生错误拉伸。
5. 暂停、重开、检查点跳转后，画面与声音时间一致。

### 1.4 一个必须先说清的预期

**编辑器里看不到噪声域。** 运行时实例只在 `LevelData.Decode` 与 `scnEditor.Play` 时创建（`Plugin.cs` 的 `DecodePatch` / `EditorPlayPatch`）`[代码]`。编辑器画布里放置、拖参数时，事件只是个图标，效果不会预览。

这不是缺陷，是与现有三个示例事件一致的行为。但作者第一次用会以为是坏的，所以：

- `.event` 的描述文本里要写明「按播放预览」。
- 如果要编辑器内预览，那是独立功能（在 `scnEditor` 里驱动一份不消耗歌曲时钟的实例），**不在本文范围内**。

评审时如果有人问「为什么放了没反应」，答案在这里。

---

## 2. 原版噪域的结构

调研结论（`[源码文档]` + `[推断]`）：噪域**不是单个 shader**，而是一套组合渲染系统。分三层：

### 2.1 区域本体：`BlockRender` 管线

这是区域的真实实现，也是这次要复现的部分：

```
六组 Camera Mask（约 1/8 画面分辨率）
        ↓
SubtractBlockBlender（普通区域与反转区域合成）
        ↓
BlockCompose（区域合成）
        ↓
EdgeMask Pass 1（边缘处理）
        ↓
DisabledBlock（Additive 混合）  /  ActiveBlock（Premultiplied Alpha）
        ↓
最终画面合成
```

对应 Unity 对象：`ActiveBlock`、`DisabledBlock`、`EdgeMask`、`BlockCompose`、`SubtractBlockBlender`、`BlockRender`、`PreviewBlockControl`。

关键事实：**区域是用低分辨率掩膜合成的**。这直接决定我们的实现形态——见 §4.3。

### 2.2 噪声纹理

原版纹理（`NoiseBgLines`、`FD_Noise_00000`、`PointNoise`、`BlockNoise1`、`Round10_Blur4`、`GlitchMap`、`Glitch4in1`）全部是原厂资源，**不能打包分发**。必须程序化生成。

### 2.3 后期与故障效果

`VignettePlus`（`noiseBrightness`、`noiseSaturation`、`noiseSampleRate`、`lineOpacity`、`center/radius/smoothness/darkness`）、`GlitchDog`（`noiseFrameCount`、`noiseMoveSpeed`、`noiseEvolveSpeed`、`noiseScale`、`noiseOffset`）、`GlitchSettings`（`glitchTex`、`glitchRange`、多层故障纹理、水平切片与 RGB 偏移）。

这一层是**全屏**的，不是区域。在 ADOFAI 里这一层有现成替代：游戏自带的 `SetFilter` / `SetFilterAdvanced` 事件，`CameraFilterPack` 里有 `NewGlitch1..7`、`TV_MovieNoise`、`Color_Chromatic_Plus` 等 `[源码文档]`。**我们不需要实现这一层**。

### 2.4 一个重要的参数解读

`GlitchDog` 的四个参数 `noiseFrameCount` / `noiseMoveSpeed` / `noiseEvolveSpeed` / `noiseScale` `[源码文档]`，含义几乎必然是：

- `noiseFrameCount`：噪声是**预生成的 K 帧序列**（翻页动画），不是每帧实时算的。
- `noiseEvolveSpeed`：翻页速率。
- `noiseMoveSpeed`：UV 滚动速率。
- `noiseScale`：UV 平铺密度。

`[推断]`，但这个推断极其关键——它把"每帧生成纹理"这个昂贵操作变成了"启动时生成 K 张，之后只换贴图和改 UV"。**整个实现方案的性能问题就此消失**。见 §4.3。

---

## 3. 可以复用的既有能力

这些已经在这个仓库里跑通了，不要重造。

| 能力 | 位置 | 状态 |
|---|---|---|
| `.event` 包扫描（ZIP / 目录） | `Loading/EventLoader.cs`，`ReadResource(path, name)` 读包内任意文件 | `[实测]` P3 |
| 事件注册到快捷栏 + 图标兜底 | `Registry/EventRegistry.cs` | `[实测]` Stage 0 V3/V3c |
| 自定义枚举作为原生下拉 | `Enum:NS.Type, Asm` 写法 | `[实测]` Stage 0 V2 |
| 参数类型转换双向 | `Storage/ParamCoercion.cs`（文件→编辑器）、`Storage/StorageValue.cs`（编辑器→文件） | `[实测]` P0 |
| 注释载体存储 + 五条不变式 | `Storage/LevelStorage.cs` | `[实测]` P0/P3 |
| 放置 / 复制 / 删除的编辑器同步 | `Editor/EditorIntegration.cs` | `[实测]` P2 |
| 撤销/重做后仍能绑定 | `__pevUid` 存在 `LevelEvent.data` 里 | `[实测]` P3 |
| 每实例独立 JS 引擎 + 沙箱 | `Scripting/ScriptHost.cs` | `[实测]` P1 |
| 歌曲时钟驱动（不累积 deltaTime） | `Scripting/RuntimeHost.cs` | `[实测]` P1/P4 |
| 持续型对象（create/set/destroy） | `Scripting/ContinuousEffects.cs` | `[实测]` P4 |
| 效果画布与相机解析 | `Scripting/EffectCanvas.cs`、`Scripting/GameCamera.cs` | `[实测]` P4 |

### 3.1 三个必须知道的历史坑

1. **`Camera.main` 是 null。** ADOFAI 不给主相机打 `MainCamera` 标签。必须走 `scrCamera.instance.camobj`。`GameCamera.Current` 已封装。
2. **对象不能挂在游戏相机下**，会被画在下面。用 `EffectCanvas.CreateObject`，layer 0 + 高 `sortingOrder`。
3. **`fx.set` 传入的坐标是"半屏归一化"**，`1.0` = 半个视口。噪声域需要世界坐标，不能直接复用这套坐标——见 §7.2。

---

## 4. 关键设计决策

### 4.1 事件形状：普通事件，不是 `decorations`

`[源码文档]`：`LevelEventInfo.isDecoration` 决定事件在 `LevelData.Decode` 时被分到 `decorations` 还是 `levelEvents`。装饰事件会自动获得编辑器里的列表项、选中、拖拽、pivot 手柄、`MoveDecorations` 按 tag 驱动。

看起来很诱人，但有两个硬约束：

**约束一（致命）**：`isDecoration = true` 的自定义类型如果被写进文件的 `decorations` 数组，原版读它时会去 `GCS.levelEventsInfo[数字串]` 查表，抛 `KeyNotFoundException`，**整关 `actions` 全部丢失**。`[实测]` Stage 0 反证实验拍下来过。这与硬性目标 C1 直接冲突。

**约束二**：装饰对象由 `scrDecorationManager.CreateDecoration` 创建，它面对一个不认识的 `LevelEventType` 走哪个分支 `[未知]`。可能是通用 `scrDecoration` 兜底，也可能是空引用。

**决定**：v1 走**普通事件**（`isDecoration = false`，即现状），"像一个装饰"由**参数**实现（相对对象、锚点、尺寸、旋转、深度、tag），不由"它在不在 decorations 数组里"实现。

**升级路径**（可选，P5 之后）：在编辑器里把实例注入 `levelData.decorations`，文件里仍然写注释。做法是 `LevelData.Decode` 后置补丁注入 + `EncodeToDictionary` 前置过滤。前置条件：

- 必须先验证 `scrDecorationManager.CreateDecoration` 对未知类型的行为（§14 Q1）。
- `UpdateDecorationObjects()` 会清空并按 `decorations` 重建，注入必须挂在它之后 `[源码文档]`。

**不要**在验证通过前把这条路径写进正式代码。

### 4.2 存储：装饰行载体

噪域现在存成一条**真的 `AddDecoration` 行**，不再是 `@PEV1` 注释。

| 字段 | 作用 |
|---|---|
| `eventType` | `AddDecoration`。不能换成自定义类型——`LevelEvent.Decode` 查不到 `info` 会立刻抛异常 |
| `decorationImage` | `pev/field`，标记。指向一个**故意不存在**的文件，原版加载到 null，不画、不报错 |
| `maskingTarget` | 载荷。`pev1:{json}`，装整个 `EventInstanceData` |
| `scale` / `position` / `rotation` / `color` / `opacity` / `tag` / `depth` / `parallax` | 原生字段，直接就是几何与外观 |

这样做的收益是**引擎自己的代码在替我们工作**：`scrDecorationManager` 建出真的 `scrDecoration`，编辑器 gizmo 能拖，`MoveDecorations` 按 tag 能找到它。

**为什么参数不单独成键**：`LevelEvent.Decode` 只遍历 `info.propertiesInfo` 里声明的键，未知键会被静默丢弃。参数若作为独立键写进装饰行，原版玩家打开再保存一次就没了。`maskingTarget` 是 `AddDecoration` 里唯一一个 `control: Hidden` 且 `encode: true` 的 String，所以它是唯一安全的容器。

**为什么参数还是在面板里**：`DecorationParams` 把清单参数注入 `AddDecoration.propertiesInfo`，带 `showIf: ["decorationImage", "pev/field"]`。`PropertyInfo.CheckIfShown` 每次选中都重新求值（`InspectorPanel.ShowPanel` → `PropertiesPanel.SetProperties`），所以这些行只出现在我们的装饰上，原生装饰一行都不多。注入项设 `encode: false`：它们的值只在载荷里，写进文件只会给原版留一堆它不认识的键。

**读取顺序**：`Restore()` 先扫 `level.decorations`（`RestoreDecorations` + `Adopt`），再扫 `levelEvents` 里的旧注释并把装饰型清单迁移成装饰行（`MigrateToDecoration`）。旧谱面因此不会丢事件。`Bake()` 不再处理它们；写回由 `EncodeToDictionary` 的前缀 `SyncDecorations()` 完成——必须在前缀，因为装饰数组是在 `EncodeToDictionary` 内部被序列化的。

### 4.3 渲染：不用 shader

这是整个方案里最重要的一个决定。

**为什么不能用 shader**：Unity 构建会剥掉没有被场景资产引用的 shader。Mod 里 `Shader.Find("PEV/Noise")` 会返回 `null`。要发自己的 shader，必须用 **Unity 6000.3.25f1 打一个 AssetBundle**，那是另一条完整的构建链，且与游戏版本强耦合。对一个 v1 效果来说不划算。

**用什么**：`SpriteRenderer` + 程序化生成的 `Texture2D`。

这条路**已经在游戏里跑通过**：P4 的 `ContinuousEffects` 用 `SpriteRenderer` + `SolidSprite.White` 渲染出了可见图形，截图确认 `[实测]` P4。所以 sprite 渲染通路本身不存在风险。

关于 `Sprites/Default`：`SpriteRenderer` 不显式赋材质时用的是内置 sprite 材质，sprite 系统自己在用，不会被剥掉 `[推断，但 P4 已间接佐证]`。**不要**用 `Shader.Find("Sprites/Default")` 去拿——多一次查找、多一个失败分支，而默认材质直接用就行。

**具体形态**：

```
实例
 ├─ GameObject "PEV.Field:<uid>"        （挂在 EffectCanvas.Root 下）
 ├─ SpriteRenderer                        （一张 quad）
 ├─ Sprite[] Sprites                      （K 个，与 Frames 一一对应，生成期一次性建好）
 └─ Texture2D[] Frames                    （K 张，翻页序列）
```

每帧开销 = `sr.sprite = Sprites[frame]` + 改材质 UV offset。**没有逐像素运算**。

**不要**用「一张 Sprite + 每帧换 `material.mainTexture`」的写法。`SpriteRenderer` 的材质绑定的是 sprite 自带的纹理，覆盖 `mainTexture` 在打包图集或批处理下不可靠——表现是画面显示第一帧后不再变化，且没有任何报错。K 个 `Sprite` 在生成期建好，换 `sprite` 引用是确定生效的。

UV 滚动用 `sr.material.mainTextureOffset`。**这一条是 `[未知]`，不是 `[实测]`**：

- `Sprite.Create` 会把 UV 直接烘进 mesh。`Sprites/Default` 是否在顶点着色器里乘 `_MainTex_ST`，取决于 Unity 版本里的 shader 源码——Unity 6000.3 的内置 sprite shader 大概率走 `TRANSFORM_TEX`，但**没有验证过**。
- 如果它不生效，表现是：翻页正常（换 sprite 生效），但滚动完全不动，没有报错。

**验证方法**（P-N1 的第一件事）：故意设一个很大的 `noiseMoveSpeed`，看画面有没有横向滚动。没滚动就是这条不成立。

**退路**（按代价排序）：

1. 把 offset 烘进 `Sprite.Create` 的 `rect`：生成一张比可视区宽 `K` 倍的纹理，每帧换 `rect` 的 x 偏移。滚动连续，代价是纹理更宽、每帧要重建 `Sprite`（比换引用贵，但仍然可接受）。
2. 换成 `MeshRenderer` + 手写 quad mesh，自己控制 UV 数组。绕开 sprite 系统，代价是失去 `sortingLayer`/`sortingOrder` 的便利（要自己算 `renderQueue` 或 `sortingLayerID`）。
3. 不做滚动，只用翻页。视觉上像「闪动的噪点」而不是「流动的噪点」，观感差一截。

先按方案 1 的形态设计 `NoiseTextureFactory`（生成时就能指定宽度），这样方案 0 不成立时不需要改结构。

### 4.4 遮罩：CPU 生成时就带上

不要在 GPU 侧做遮罩（那需要 shader 或 `SpriteMask` 的模板缓冲）。既然纹理是我们自己生成的，**生成时就把 alpha 按形状写好**。

- 矩形：宽高范围内 alpha=1，边缘按 `feather` 逐像素衰减。
- 圆形：半径内 alpha=1，边缘按 `feather` 衰减。

好处：形状、羽化、反转全部是生成参数，不需要额外组件；换形状只是重生成纹理。

代价：形状改变时要重生成（但形状是静态参数，只在参数变化时发生）。

`[源码文档]` 提过游戏自带 `SpriteAlphaMask` / `SpriteAlphaMaskUtils`（按 tag 绑定、把重叠 mask 合成到 RenderTexture）。那是给多装饰叠加遮罩用的；我们单 quad 自带 alpha，用不上，也不要用——它是装饰系统的内部结构。

### 4.5 时钟

**唯一时钟**：`scrConductor.instance.songposition_minusi`。已在 `RuntimeHost.Update()` 里，`EventInstance.Tick` 算出 `t = songTime - EntryTime`。

**绝对不能**用 `Time.deltaTime` / `Time.unscaledDeltaTime` 累加。

⚠️ 现存代码里的例外：`NativeEffects.EffectTicker` 用 `Time.unscaledDeltaTime` `[代码]`。那是给 0.3 秒闪屏用的，一次性、短命、无所谓。**噪声域不能用它**。

### 4.6 生命周期与多事件协作

Phigros 的噪域有时间维度：出现、激活、消失、多段激活、激活前白闪 `[源码文档]` RPE-Next 数据模型。ADOFAI 的装饰模型是"存在，然后被移动事件改"。

诚实映射成**一个事件**：

| 事件 | 职责 | 必要性 |
|---|---|---|
| `AddNoiseField`（即 `pev.noise-field`） | 创建实例，声明全部渲染参数 | 必须 |

早期设计拆成三个事件（Add / Move / SetState）。**这个拆分已经取消**，因为它与"像装饰一样"这个要求冲突：

噪域现在本身就是一条原生 `AddDecoration` 行。位置、大小、旋转、颜色、透明度、tag、depth 全部是装饰自身的属性，因此

- 拖动、缩放、旋转由编辑器的 gizmo 直接完成；
- `MoveDecorations` 按 tag 就能移动、改色、淡入淡出，**它就是那个"移动噪域"事件**；
- 可见性用原生 `visible`，无需另设状态事件。

用户的原话是"现在好像有好几种有关噪域的事件，合成一个『添加噪域』"——合成的方式不是把两个自定义事件合并，而是让噪域退回装饰本体，原生事件天然就是它的控制器。

**tag 索引**：`AddNoiseField` 的 `tag` 参数注册到 Core 内的 `Dictionary<string, List<NoiseFieldInstance>>`；Move/State 事件按空格拆 tag 查找（与原生 `MoveDecorations` 一致，空 tag 用 `NO TAG`）。

---

## 5. 参数表

全部在 `PropertyType` 白名单内——白名单本身是 `[源码文档]` §4 第 11–12 条 + `[实测]` Stage 0 V2。

**但要注意实测覆盖范围**：P0 实际建出控件并跑通往返的只有 `Float`、`Enum`、`Color`、`Tile` 四种 `[实测]` P0。本表用到的 `Vector2`、`Bool`、`Int`、`String` 属于白名单内但**本项目尚未逐个验证**的类型——`[源码文档]` §4 第 11 条表明它们都是 `PropertyInfo` 构造器直接支持的，风险低，但 `ParamCoercion` / `StorageValue` 对它们的处理是照 `PropertyType` 分支写的，没有实测过。

**所以 V-N1 不是走过场**：它是这四个类型在本项目里的第一次验证。如果 `Vector2` 的控件没出来，或者 `Bool` 存成了 `"True"` 字符串，问题就在这里，不在别处。

### 5.1 几何（全部由装饰本体承担）

几何**不再是清单参数**。噪域就是一条 `AddDecoration` 行，所以下面这些全部是游戏的属性，面板里就是装饰原本的那几行：

| 装饰属性 | 作用 | 备注 |
|---|---|---|
| `position` | 位置，单位：方块 | 相对 `floor` 所在的方块 |
| `scale` | 长宽，**百分比**（100 = 基础尺寸） | 圆形也走长宽——用户明确要求「圆形噪域也改成长宽控制」，实现为椭圆 |
| `rotation` | 度 | 与装饰一致 |
| `color` | 颜色 | 渲染器的基色直接取它，`MoveDecorations` 改色会立刻生效 |
| `opacity` | 0–100 | 与装饰一致 |
| `depth` / `parallax` | 层级与视差 | 与装饰一致 |
| `tag` | 供 `MoveDecorations` / `SetText` 等原生事件引用 | 由 `scrDecorationManager.taggedDecorations` 提供索引 |
| `visible` | 可见性 | 原生 |
| `decorationImage` | **固定 `pev/field`**，是标记 | 不要改，改了这条行就不再是噪域 |
| `maskingTarget` | 载荷 | 不要改，改了参数全部丢失 |

场景里唯一属于清单的几何项只剩 `shape` 与 `feather`（圆形羽化）。

**为什么这样更好**：位置、缩放、旋转、颜色、透明度、tag 都由编辑器 gizmo 与引擎自己的 tween 直接写入 `scrDecoration`，渲染器每帧从组件读取，所以「像装饰一样」不是模仿出来的，而是真的共用同一条代码路径。

### 5.2 噪声（沿用 Phigros 渲染变量）

不另造「风格参数」，每个都对应原版的实际渲染变量。

| 参数名 | 类型 | 默认 | 对应原版 | 说明 |
|---|---|---|---|---|
| `noiseBrightness` | `Float` | `1.0` | `VignettePlus.noiseBrightness` | 颗粒亮度倍数 |
| `noiseSaturation` | `Float` | `1.0` | `VignettePlus.noiseSaturation` | 饱和度倍数 |
| `noiseSampleRate` | `Float` | `1.0` | `VignettePlus.noiseSampleRate` | 采样密度；越大颗粒越细 |
| `lineOpacity` | `Float` | `0.35` | `VignettePlus.lineOpacity` | 横向噪线强度 |
| `noiseFrameCount` | `Int` | `8` | `GlitchDog.noiseFrameCount` | 预生成帧数，2–32 |
| `noiseEvolveSpeed` | `Float` | `12` | `GlitchDog.noiseEvolveSpeed` | 每秒翻页数 |
| `noiseMoveSpeed` | `Vector2` | `[0.15,0]` | `GlitchDog.noiseMoveSpeed` | UV 滚动速度，单位：贴图宽度/秒 |
| `noiseScale` | `Vector2` | `[1,1]` | `GlitchDog.noiseScale` | UV 平铺密度 |
| `glitchRange` | `Float` | `0` | `GlitchSettings.glitchRange` | 水平切片错位幅度；0 = 关闭 |
| `color` | `Color` | `ff2020` | — | 颗粒主色 |
| `feather` | `Float` | `0` | — | 边缘羽化宽度，单位：方块 |

### 5.3 边缘（海岸线形态）

区域边界不是几何轮廓的描边，而是一条**在几何范围附近游走的等值线**：覆盖判定是
`sdf(x) → 阈值 → 采样滚动噪声 → 是否在阈值以上`，即「随带符号距离滑动的噪声阈值」。
它决定了「剥离、分离、合并、碎片化」这些拓扑变化——这些是等值线的必然结果，
用顶点位移做不出来（见 §8.3）。因此下面这组参数调节的是这条等值线的形态，
不是某一条描边的粗细。

| 参数名 | 类型 | 默认 | 范围 | 说明 |
|---|---|---|---|---|
| `edgeDistance` | `Float` | `0.7` | 0–2 | 侵蚀带宽度，按场自身短边的比例。带内是海岸线；越大，缺口越深、内部越容易被啃出洞 |
| `edgeDetail` | `Float` | `8` | 1–32 | 短边上的噪声特征数。越大，海岸线越细碎、岛和湾越密 |
| `edgeThickness` | `Int` | `1` | 1–6 | 边框线厚度，单位为遮罩格。`1` 即参考图的单格描边 |
| `edgeMargin` | `Float` | `0.6` | 0–2 | 几何范围外多留的采样边距（按场短边比例）。碎片能在边界外飘多远就取决于它；0 = 一切都被裁在几何框内 |
| `edgeFragment` | `Float` | `1` | 0–1 | 场外碎片的存活量。0 时带外完全清空，边界干净但失去「纷飞」 |

`glitchRange`（§5.2）在这套算法下作用于整幅纹理的行位移，与 Phigros 的 `GlitchMap` 同义：
它是**观感层**的横向切片错位，不参与覆盖判定，因此不会让区域「断开」。

**带宽是按场算的，不是按合并后的包围盒**：同一深度上的一个小场和一个大场合并渲染时，
各自用自己的短边推 `Band`。否则小场会被大场的带宽整个吞掉，退化成碎片。

**参数只在重建纹理时生效**：这些值参与 `Fingerprint`，改动会重建纹理组（8 帧），
运行期改它们与改 `noiseScale` 同代价。跟随动画变化的量（位置、透明度、可见性）走
`fx.fieldMove` / `fx.fieldState`，不要塞进这里。

### 5.4 枚举定义

```csharp
namespace PEV.Scripting
{
    // 顺序即下拉顺序；名字一旦发布就不能改——文件里存的是名字（StorageValue.Canonical）。
    public enum NoiseFieldRelativeTo
    {
        Tile,        // 方块：以事件所在格为基准
        Camera,      // 镜头：跟相机，屏幕相对
        PlanetRed,   // 火之行星
        PlanetBlue,  // 冰之行星
        PlanetGreen, // 风之行星
        CameraRatio  // 镜头（比例）：屏幕相对，按视口比例而非世界单位
    }

    public enum NoiseFieldAnchor
    {
        Center, TopLeft, Top, TopRight,
        Left, Right,
        BottomLeft, Bottom, BottomRight
    }

    public enum NoiseFieldShape { Rect, Circle }
}
```

**注意**：`PropertyInfo` 的 `default` 必须是成员名，写错会得到「默认值 'A' 不是成员」的报错并退化成无控件 `[实测]` Stage 0 V2。`pack_event.py` 能校验类型名，但校验不了枚举成员名——**这一项只能靠人眼**。

---

## 6. 文件形态

注释里的内容：一行紧凑 JSON。

```
decorations[] {
  "eventType": "AddDecoration",
  "floor": 12,
  "decorationImage": "pev/field",
  "scale": [100, 75],
  "position": [0, 0],
  "rotation": 0,
  "color": "ffffff",
  "opacity": 100,
  "depth": 0,
  "parallax": [100, 100],
  "tag": "wave",
  "maskingTarget": "pev1:{\"id\":\"pev.noise-field\",\"v\":\"1.0.0\",\"uid\":\"3f8a91c2\",\"p\":{\"shape\":\"Rect\",\"noiseBrightness\":1,\"noiseSaturation\":1,\"noiseSampleRate\":1,\"lineOpacity\":0.35,\"noiseFrameCount\":8,\"noiseEvolveSpeed\":12,\"noiseMoveSpeed\":[0.15,0],\"noiseScale\":[1,1],\"glitchRange\":0,\"fillOpacity\":0.667,\"edgeOpacity\":0.8,\"glowPasses\":5,\"displace\":0.35,\"edgeDistance\":0.7,\"edgeDetail\":8,\"edgeThickness\":1,\"edgeMargin\":0.6,\"edgeFragment\":1,\"preDuration\":0}}"
}
```

外层全部是原生字段——原版客户端打开这条谱面，看到的就是一个"图片缺失"的普通装饰，不崩、不显示。参数只在 `maskingTarget` 里，原版不认识但会原样保留，因为它在 `AddDecoration` 的属性表里是 `encode: true`。

枚举写名字、`Vector2` 写数组 `[x,y]`，由 `StorageValue.Canonical` 保证 `[实测]` P0。

**`scale` 的单位**：装饰的 `scale` 是百分比（100 = 原尺寸），而 `scrDecoration.scaleVec` 是倍数（1.0 = 原尺寸）。`MoveDecorations` 里能看到这个换算：事件值 `/100` 后写进 `targetScaleV2`，再原样传给 `SetScale`。读取时用哪一侧，单位必须对应——这里除错一次会让区域小 100 倍。

---

## 7. 运行时实现

### 7.1 为什么必须有原生模块

JS 沙箱里拿不到 `Texture2D`、没有 `System`、不能反射 `[实测]` P1。噪声生成与贴图上传**必须**在 C# 侧。

所以分工：

- `.event` 的 `main.js`：读参数、决定何时创建/销毁、每帧推一次状态。
- Core 的 `NoiseFieldRenderer.cs`：生成纹理、持有对象、响应 set。

### 7.2 新增脚本 API

在 `ScriptHost.Bootstrap` 里加：

```js
field: function (key, o) {
  var a = o || {}; a.key = key;
  return JSON.parse(__hostFx('field', JSON.stringify(a)));
},
fieldDestroy: function (key) {
  return JSON.parse(__hostFx('fieldDestroy', JSON.stringify({ key: key })));
}
```

在 `GameBridge.TryPlayNative` 里加两个 case：

```csharp
case "field":        return NoiseFieldRenderer.Set(Str(args, "key"), args, out error);
case "fielddestroy": return NoiseFieldRenderer.Destroy(Str(args, "key"), out error);
```

**`fx.field` 与 `fx.create` 的区别**：`fx.create` 的坐标是半屏归一化、只有 scale/alpha/angle；`fx.field` 收世界单位的几何参数，并且会生成纹理。两者不复用。

**实例上下文怎么传进 Core**：`ScriptHost.OnHostFx(name, argsJson)` 是**实例方法** `[代码]`，`_debugName` 就是 `"{id}#{uid}"`。所以在分发前把 uid 塞进 args：

```csharp
private int _floorSeq;   // 新增字段

private string OnHostFx(string name, string argsJson)
{
    try
    {
        var args = string.IsNullOrEmpty(argsJson)
            ? new Dictionary<string, object>()
            : JsonConvert.DeserializeObject<Dictionary<string, object>>(argsJson)
              ?? new Dictionary<string, object>();

        // 脚本给的 key 只在实例内部唯一；跨实例必须再叠一层身份，
        // 否则同一格放两个同 key 的对象会互相覆盖，且不会有任何报错。
        args["__instance"] = _debugName ?? "";
        args["__floor"]    = _floorSeq;

        bool ok = GameBridge.TryPlayNative(name, args, out string error);
        return JsonConvert.SerializeObject(new Dictionary<string, object>
        {
            { "ok", ok }, { "error", error }
        });
    }
    catch (Exception e)
    {
        return JsonConvert.SerializeObject(new Dictionary<string, object>
        {
            { "ok", false }, { "error", e.Message }
        });
    }
}
```

`_floorSeq` 的赋值位置有要求：必须在 `Initialize()` 里、**`_engine.Evaluate(create)` 之前**完成。因为 `create()` 可以在返回前就调 `fx.field`（比如一创建就摆好位置），那时 `OnHostFx` 已经会被调用。现有 `Initialize()` 的结构是：先 `SetValue("__ctx")` / `SetValue("__params")`，再 `Evaluate(create)`——把 `_floorSeq = context.FloorSeq;` 加在 `SetValue` 之前即可。`_debugName` 更早，在 `ScriptHost.Create()` 构造时就有了。

Core 侧：

```csharp
// 同一次调用里带过来的身份，用来把脚本的 key 限定到实例内
static string Scoped(Dictionary<string, object> args, string key)
{
    args.TryGetValue("__instance", out var inst);
    return (inst?.ToString() ?? "") + "/" + (key ?? "");
}
```

`NoiseFieldRenderer` 的所有 key 都过 `Scoped`。`Tile` 基准点需要的 `floorSeq` 也从同一份 args 里取（`__floor`），不必让脚本再传一次。

**`DestroyAll` 的清理顺序**：先遍历 `Entry` 释放 `Texture2D`（`Object.Destroy(tex)`），再销毁 `GameObject`。反过来写会先丢掉引用，纹理泄漏。

**挂在哪里**：`RuntimeHost.Teardown()` 调用 `GameBridge.DestroyAll()` `[代码]`，而 `GameBridge.DestroyAll()` 当前是：

```csharp
public static void DestroyAll()
{
    try { ContinuousEffects.DestroyAll(); } catch { }
    try { EffectCanvas.DestroyAll(); } catch { }
}
```

加一行：

```csharp
    try { NoiseFieldRenderer.DestroyAll(); } catch { }
```

**顺序要求**：必须在 `EffectCanvas.DestroyAll()` **之前**。它销毁的是所有 GameObject，纹理若还挂在已销毁的对象上，`Object.Destroy(tex)` 仍然有效，但先释放纹理逻辑更清晰、也便于断言计数。三个 `try` 各自独立，一个抛异常不影响其余——这是现有写法，保留。

### 7.3 `NoiseFieldRenderer`

```
public static class NoiseFieldRenderer
{
    // public：ByTag 要把它作为返回类型。嵌套在静态类里，不污染命名空间。
    public sealed class Entry
    {
        public GameObject     GameObject;
        public SpriteRenderer Renderer;
        public Sprite[]       Sprites;     // K 个，与 Frames 一一对应
        public Texture2D[]    Frames;      // K 张
        public string         ParamHash;   // 参数指纹，变化时重生成
        public string         Tag;
        public string         Scope;       // instance 前缀，解绑时用
        public int            FloorSeq;
        public float          Opacity;
        public bool           Visible;
        public int            SortingOrder;
    }

    public static bool Set(string key, Dictionary<string, object> args, out string error);
    public static bool Destroy(string key, out string error);
    public static void DestroyAll();
    public static IReadOnlyList<Entry> ByTag(string tag);
}
```

`Entry` 必须 `public`（或至少 `internal`）。私有嵌套类型不能作为公有方法的返回类型——写出来就编译不过，这是 C# 的可访问性规则。把它留在 `NoiseFieldRenderer` 内部，避免在 `PEV.Scripting` 命名空间里多一个顶层类型。

注意 `Entry` **不持有** `FrameCursor` / `UvOffset` / `ScrollSpeed`。这些由 JS 按 `t` 算好后每次传下来（§7.4），Core 只做应用。少一份状态就少一处暂停/重开不对齐的可能。

`DestroyAll()` 必须释放 `Texture2D`：`Object.Destroy(tex)`。`GameObject` 销毁不会连带释放贴图 `[代码事实：现有 EffectCanvas.DestroyAll 只销毁 GameObject]`。

> ⚠️ 这是本方案引入的**新泄漏源**：`Texture2D`、`Material` 不是 GameObject，`DestroyAll()` 现在回收不到。写 `Destroy` / `DestroyAll` 时逐个显式释放。

### 7.4 每帧流程

`EventInstance.Tick` → `Host.Update(t, stateJson)` → JS `update(t)` → `fx.field(key, {...})` → `NoiseFieldRenderer.Set`。

`Set` 内：

```
1. 若参数指纹变化 → 重生成 Sprites + Frames（释放旧的），更新 tiling/color/sortingOrder
2. 计算世界位置与尺寸（§7.5），写 transform
3. sr.sprite = Sprites[frame % K]
4. material.mainTextureOffset = uv
5. sr.color = tint * opacity * (visible ? 1 : 0)
```

**`frame` 与 `uv` 由 JS 从 `t` 算**，Core 不做时间推进：

```js
var frame = Math.floor(t * params.noiseEvolveSpeed) % K;
var uvx = t * params.noiseMoveSpeed[0];
var uvy = t * params.noiseMoveSpeed[1];
fx.field(key, { frame: frame, uv: [uvx, uvy], ... });
```

这样暂停、重开、检查点跳转全部自动正确，因为 `t` 来自歌曲时钟。**任何在 Core 侧累加的写法都会在跳转时错位。**

注：`K = max(2, round(params.noiseFrameCount))`，且生成期会把它写进参数指纹——改帧数要重生成。

### 7.5 几何计算

**这一节描述的是装饰自身的几何，不是我们要算的东西。** 噪域作为 `AddDecoration` 行存在时，位置、长宽、旋转由引擎写进 `scrDecoration`，我们只读：

```csharp
var dec = scrDecorationManager.GetDecoration(ev);
Vector2 pivot = dec.pivotPosVec;     // 世界坐标的轴心
Vector2 scale = dec.scaleVec;        // 倍数，1.0 = 100%
float   rot   = dec.rotAngle;
Color   col   = dec.color;
float   opacity = dec.opacity;
```

`scaleVec` 是**倍数**，不是百分比。事件里的 `scale` 是百分比，`MoveDecorations` 会先 `/100` 再写进 `targetScaleV2` 交给 `SetScale`——所以在组件这一侧再除一次 100 会让区域小两个数量级。

**长宽到世界的换算**：

```
worldSize = (scaleVec.x * BaseSizeTiles.x * tileSize,
             scaleVec.y * BaseSizeTiles.y * tileSize)
BaseSizeTiles = (4, 3)
```

`BaseSizeTiles` 是装饰在 100% 时覆盖的方块数。装饰的尺寸本来来自它自己的 sprite，而噪域的标记图片**故意不存在**，引擎因此拿不到天然尺寸，这个常量就是那份「缺省的 sprite 尺寸」，也就是 scale 百分比与世界尺寸之间唯一的映射点。

**兜底路径**：`scrDecorationManager` 在解码之后才建对象，撤销、播放、重载时会重建。这些间隙里组件不存在，`DecorationBridge.TryReadEvent` 用同样一组字段直接从事件数据算——两条路径的公式一致，切换不可见，所以撤销一帧不会让区域闪掉。

**圆形**：不是半径，是**椭圆的半轴**。用户明确要求「圆形噪域也改成长宽控制」，于是圆形与矩形共用同一对 `scale` 手柄；`EllipseDistance` 用准欧氏近似（缩放成单位圆求距离再按短轴换回），对装饰能拉出的长宽比误差在 1% 以内，而精确椭圆距离是椭圆积分，逐像素算不起。

**基准点 `basePos`**：

| `relativeTo` | basePos |
|---|---|
| `Tile` | `floors[floorSeq].transform.position`（用 `ADOBase.lm.listFloors`） |
| `Camera` | `cam.transform.position`（跟随相机，屏幕相对） |
| `CameraRatio` | 同上，`size` 改按视口比例解释 |
| `PlanetRed` / `Blue` / `Green` | `ADOBase.controller.planetRed/Blue/Green.transform.position` `[源码文档]` `scrController.planetRed/planetBlue/planetGreen` |
| 行星不存在时 | 退回 `Tile` 基准，写一次日志 |

**本节其余部分（基准点、锚点、视差、`lockSize`）描述的是 `Placement.Compute`——纯函数路径，现在只用于测试与无装饰绑定的场合。** 噪域本体走装饰行，几何由引擎算出后从 `scrDecoration` 读取（见上一段）。保留这段是因为 `Placement` 仍被 harness 用来断言锚点符号，而且反射式摆放将来可能复用。

**深度**：装饰的 `depth` 直接走引擎自己的排序规则（`depth >= 0` → `Bg` 层、order `-depth`；`depth < 0` → `Default` 层）。我们的 quad 用 `sortingOrder = 900 + depth * 10` 覆盖在上面，因为噪域不是被轨道遮挡的对象，而是要盖住轨道的效果层。

---

## 8. 噪声纹理生成

### 8.1 分辨率

不要用全屏分辨率。原版区域掩膜就是 1/8 分辨率 `[源码文档]`。

实际实现（`NoiseTextureFactory.Generate`）：

```csharp
int    block   = Mathf.Clamp(Mathf.RoundToInt(GrainPixels / sampleRate), 1, 16); // GrainPixels = 1.5
int    pxW     = ResolutionBucket(req.TargetPixelWidth);                          // 96 px 一档
int    cellsX  = Mathf.Clamp(Mathf.RoundToInt(pxW / (float)block), MinCells, MaxSide / block);
int    texW    = cellsX * block;                                                  // MinCells = 8, MaxSide = 1024
while (cellsX * block * cellsY * block * frames > MaxTexelsPerSet && frames > 2) frames /= 2;
```

两点必须保持：

- **`TargetPixelWidth` 按 96 px 分档**。分辨率随场的屏幕尺寸连续变化时，镜头每缩放一点点就会
  换一个新指纹，纹理被反复重建——`REGEN-IDLE` 断言正是抓这个的。分档后只有跨过档位才重建。
- **texel 总量有上限**（`MaxTexelsPerSet = 2.6M`），超了就减帧数而不是减分辨率。
  减分辨率会把颗粒和海岸线一起糊掉；减帧数只是翻页慢一点。

块状（低分辨率 + 双线性放大）**是特征不是缺陷**——原版的颗粒就是能数得清方格的。

### 8.2 生成算法

对每一帧 `k ∈ [0, K)`、每个 cell `(cx, cy)`：

```
1. 确定性哈希 seed = Hash(cx, cy, k, instanceSeed)
2. 覆盖判定 = 阈值化的滚动噪声（§8.3）；未覆盖的 cell 整块透明
3. 覆盖时该 cell 是否点亮：r < density
4. 点亮时亮度 = 0.35 + 0.65 * Hash2(seed)   // 避免全同亮度
5. 颜色 = tint * 亮度，alpha = 覆盖 * 亮度
6. 横向噪线：若该行 r < lineOpacity，整行 alpha += lineOpacity * 行因子
7. 水平切片：该行整体位移 rowShift[y]（§8.3.5）
```

哈希用整数运算，不要用 `UnityEngine.Random`（不可控、有全局状态）：

```csharp
static uint Hash(uint a, uint b, uint c)
{
    uint h = a * 374761393u + b * 668265263u + c * 2147483647u;
    h = (h ^ (h >> 13)) * 1274126177u;
    return h ^ (h >> 16);
}
```

**覆盖判定要独立于颗粒点亮**：颗粒是「这个 cell 有多亮」，覆盖是「这个 cell 在不在区域里」。
早期版本把两者合在一起（未覆盖时就当成暗颗粒），结果碎片看起来像脏点而不是碎片——
碎片需要 1 cell 宽的**边框线**才读得出来是碎片（§8.3.3）。

### 8.3 遮罩

> **本节在 P-N1 实现期间被整体替换。** 原设计是「到几何边的距离 + 羽化斜坡」，那是**几何轮廓的软边**，
> 无论怎么调都不可能产生参考视频里那种「缺口咬进去、岛被切下来、碎片飘在外面」的形态——
> 那些是**等值线的拓扑变化**，顶点位移或距离斜坡在数学上做不到。真正的机制是从 Phira 的
> `noise_area`（`ActiveBlock` shader + `render.rs::mask_polygons`）读出来的：**随着带符号距离滑动的噪声阈值**。

#### 8.3.1 判定式

对每个采样点，先求它到整个场（同深度的并集，`pieces` 取 `min` 距离）的带符号距离 `d`，
再把它归一化到 `t ∈ [-1, 1]`，然后用 `t` **滑动**噪声阈值：

```csharp
float t   = Mathf.Clamp(-d / pieceBand, -1f, 1f);        // d<0 在内部，d>0 在外部
float n   = Fbm(wx * noiseFreq + drift, wy * noiseFreq, LoopPeriod, kseed);
float thr = 0.5f - (t > 0f ? swingIn : swingOut) * t;
bool  cover = n > thr;
```

三个区段的含义，这也正是观感来源：

| 位置 | `t` | `thr` | 结果 |
|---|---|---|---|
| 深内部 | `→ +1` | `0.5 − swingIn`（≈0.05） | 几乎全实心，只有极少数噪声低谷漏光 |
| 海岸带 | `≈ 0` | `0.5`（噪声中位数） | 噪声的等值线，走势即海岸线；**岛、湾、合并、分离都发生在这里** |
| 带外 | `→ −1` | `0.5 + swingOut`（≈0.82） | 只有噪声峰值存活 → 飘在外面的碎片 |

`swingIn = 0.45`，`swingOut = 0.32 * edgeFragment`。

**`pieceBand` 是每个场自己的**：`displace * edgeDistance * min(场宽, 场高)`。
用合并后包围盒的短边是错的——同深度上一个小场挨着一个大场时，小场会被大场的带宽整个吃掉，
只剩下碎片。因此 `Piece` 带自己的 `Band`，`SignedDistance` 返回最近使用者的带宽。

#### 8.3.2 周期性噪声

`Fbm` 是一层 `PeriodicNoise` 加一层半幅倍频，格点索引按 `LoopPeriod = 8` 取模：

```csharp
private const int LoopPeriod = 8;

private static float Fbm(float x, float y, int period, uint seed)
{
    float n = PeriodicNoise(x, y, period, seed);
    n += 0.5f * PeriodicNoise(x * 2f, y * 2f, period * 2, seed + 977u);
    return n / 1.5f;
}
```

格点取模保证 `x` 走满 8 格后噪声回到原值。于是**每一帧把采样坐标沿 x 平移 `LoopPeriod / frames`，
第 K 帧恰好接回第 0 帧**——8 帧循环是无缝的，翻页看不出跳变：

```csharp
float drift = k * (LoopPeriod / (float)frames);
```

这是「滚动」而不是「换一批随机噪声」，也解释了为什么参考里碎片的生灭是连续的。

#### 8.3.3 两级栅格与阶梯边

参考图的海岸线是**明显的阶梯状**（10–14 屏幕像素一级），不是光滑曲线。实现上分两级：

1. **粗格**：`maskDiv = 11 / block`（每个粗格覆盖约 11 个纹理块），每个粗格取**一格样本**判定覆盖；
2. **细格**：每格纹理就近继承所属粗格的判定（最近格继承，不做插值）。

覆盖边界因此是沿粗格网格走的直角台阶，正是要的观感。但**描边必须在细格上勾勒**：

```csharp
int thickness = Mathf.Clamp(req.EdgeThickness, 1, 6);
// 对一个已覆盖的细格，检查它到「未覆盖」的距离是否 < thickness（Chebyshev 邻域）
for (int oy = -thickness + 1; oy < thickness && !nearOutside; oy++)
for (int ox = -thickness + 1; ox < thickness; ox++)
    if (!inside[ny * cellsX + nx]) { nearOutside = true; break; }
```

若在粗格上勾勒，线宽会跟着粗格一起变成十几个像素；在细格上勾勒，轮廓台阶粗、
**线本身细**——参考图正是这样。越界的细格算作「外部」，所以贴边处的线不会断。

#### 8.3.4 边距（碎片）

几何框外的碎片必须画得出来，否则它们会被 quad 直接裁掉。`edgeMargin` 把采样域和 quad
在四边各撑开 `margin = displace * edgeMargin * min(场宽, 场高)`：

```csharp
float totalW = req.WidthWorld  + margin * 2f;
float totalH = req.HeightWorld + margin * 2f;
...
layer.GameObject.transform.localScale = new Vector3(
    (size.x + margin * 2f) / Mathf.Max(1e-4f, natural.x),
    (size.y + margin * 2f) / Mathf.Max(1e-4f, natural.y), 1f);
```

**区域本身的世界坐标不变**：只有贴图与 quad 变大，几何位置仍按 `size` 计算。这一点容易写错，
写成 `size + margin` 会让整个场随边距漂移。

#### 8.3.5 水平切片（`glitchRange`）

每帧为每一行生成一个位移量，命中概率随 `glitchRange` 上升，位移量环绕（`sx %= texW`）以保持无缝：

```csharp
int span = Mathf.Max(1, Mathf.RoundToInt(glitch * texW * 0.18f));
rowShift[y] = Mathf.RoundToInt(hashFrac * 2f - 1f) * span;
```

这是**观感层**，在像素写入时应用，不影响覆盖判定，所以不会让场「断开」或让碎片重新连上。

### 8.4 上传

```csharp
var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
tex.wrapMode = TextureWrapMode.Repeat;      // noiseMoveSpeed 依赖它
tex.filterMode = FilterMode.Bilinear;       // 低分辨率放大要双线性
tex.SetPixels32(buffer);
tex.Apply(false);                            // 不生成 mipmap
```

`WrapMode.Repeat` 是必须的：`noiseMoveSpeed` 靠 `mainTextureOffset` 滚动，`Clamp` 会让边缘出现一条静止带。

### 8.5 共享与缓存

多个实例参数相同时共享同一组 `Frames`。缓存键 = 影响纹理的全部参数（`noiseBrightness`、`noiseSaturation`、`noiseSampleRate`、`lineOpacity`、`noiseFrameCount`、`color`、`shape`、`size`/`radius`、`feather`、纹理分辨率）的字符串指纹。

引用计数：最后一个使用者销毁时释放。**没有引用计数的共享缓存会变成内存泄漏**。

### 8.6 成本

```
128×72 texel × 8 帧 ≈ 9,216 × 8 = 73,728 texel
每 texel：1 次哈希 + 1 次遮罩 + 写 4 字节
```

在 Mono 上单帧生成一组大概 5–15 ms——**只在参数变化时发生**，不是每帧。每帧只做一次 `sr.sprite = Sprites[i]` 和一次 `mainTextureOffset` 赋值，成本可忽略。

如果连续拖动参数滑块导致卡顿，加防抖：参数变化后 150 ms 才重生成。`[推断]`，看实测。

---

## 9. 呈现与混合

### 9.1 混合模式

`SpriteRenderer` 默认材质 `Sprites/Default` 是 alpha 混合、无光照。对深色背景上的亮红颗粒，alpha 混合的观感已经足够接近加色。

**想更接近原版**（原版 `DisabledBlock` 用 Additive、`ActiveBlock` 用 Premultiplied Alpha）可以用加色材质：

```csharp
var sh = Shader.Find("Legacy Shaders/Particles/Additive");
if (sh != null) mat = new Material(sh);
```

`[未知]`：该 shader 是否被游戏构建保留。**必须有 fallback**——`Shader.Find` 返回 null 时静默用 `Sprites/Default`。不要因为拿不到加色材质就抛异常。

可以尝试的候选（按优先级）：`Legacy Shaders/Particles/Additive`、`Particles/Standard Unlit`、`Sprites/Default`。加载期各试一次并缓存结果，日志里写清楚用了哪个。

### 9.2 排序

`sortingOrder` 见 §7.5。多个噪声域之间用 `depth` 区分。

### 9.3 水平切片（`glitchRange`）

原版 `GlitchSettings` 有「水平切片与 RGB 偏移」`[源码文档]`。

**已实现，不需要额外 SpriteRenderer**：行位移在生成纹理时直接烘进像素，与覆盖判定分开。
每帧（8 帧循环里的每一帧）逐行抽一个位移量：

```csharp
int span = Mathf.Max(1, Mathf.RoundToInt(glitch * texW * 0.18f));
rowShift[y] = Mathf.RoundToInt(hashFrac * 2f - 1f) * span;   // 命中概率 ∝ glitch
```

写像素时按行环绕，保证贴图仍然无缝：

```csharp
int sx = x + shift;
if (shift != 0) { sx %= texW; if (sx < 0) sx += texW; }
```

之所以做成生成期而不是渲染期：渲染期只能位移整张 quad 或其 UV，位移不到「某些行」；
按行切 Sprite 又会把 8 帧 × 行数变成上百个对象。烘进纹理的代价是零——总 texel 预算不变。

RGB 偏移未做：需要单独的通道副本，收益小于成本。

**注意**：`glitchRange > 0` 会让纹理指纹变化，因此也参与重建判定，和改 `noiseScale` 同代价。
它不应该被逐帧驱动（那等于每帧重建）；脚本里写常量，或在事件级参数里改。

---

## 10. `.event` 包

### 10.1 `main.js`

```js
function create(ctx, params) {
  // key 只在实例内部唯一即可：Core 会再叠一层 __instance 前缀，
  // 所以同一格放两个噪声域也不会互相覆盖。
  var key = "noisefield";
  var alive = false;

  function push(t) {
    var frames = Math.max(2, Math.round(params.noiseFrameCount));
    var frame = Math.floor(t * params.noiseEvolveSpeed) % frames;
    if (frame < 0) frame += frames;

    // 只发观感参数。位置、长宽、旋转、颜色、透明度、tag、depth 全部是装饰
    // 自己的属性，宿主每帧从 scrDecoration 读——脚本再发一遍会与引擎打架：
    // 作者拖出来的值会在下一帧被读到的旧值覆盖。
    fx.field(key, {
      shape: params.shape,
      feather: params.feather,

      frame: frame,
      uv: [t * params.noiseMoveSpeed[0], t * params.noiseMoveSpeed[1]],
      brightness: params.noiseBrightness,
      saturation: params.noiseSaturation,
      sampleRate: params.noiseSampleRate,
      lineOpacity: params.lineOpacity,
      tiling: params.noiseScale,

      // 层与海岸线参数。宿主只读调用参数，不读 manifest：漏传一项就静默换成
      // 宿主默认值，最典型的表现是 displace 丢失后整个场变成一块平直矩形。
      fillOpacity: params.fillOpacity,
      edgeColor: params.edgeColor,
      edgeOpacity: params.edgeOpacity,
      sparkColor: params.sparkColor,
      sparkOpacity: params.sparkOpacity,
      glowColor: params.glowColor,
      glowOpacity: params.glowOpacity,
      glowPasses: params.glowPasses,
      displace: params.displace,
      pre: t < params.preDuration,

      edgeDistance: params.edgeDistance,
      edgeDetail: params.edgeDetail,
      edgeThickness: params.edgeThickness,
      edgeMargin: params.edgeMargin,
      edgeFragment: params.edgeFragment,
      glitch: params.glitchRange
    });
  }

  return {
    update: function (t) {
      if (t < 0) return;
      if (!alive) { alive = true; }
      push(t);
    },
    dispose: function () {
      if (alive) fx.fieldDestroy(key);
      alive = false;
    }
  };
}
```

要点：

1. **`key` 不需要全局唯一**。Core 在分发时会叠 `__instance`（`"{id}#{uid}"`）前缀，所以写死 `"noisefield"` 也不会和别的实例撞。

   这是刻意的双层设计：脚本层可以偷懒，宿主层兜底。代价只是 `GameBridge` 里每个 key 都要过一次 `Scoped()`，成本可忽略。

   历史上 `fx.create` 的这类撞 key 问题（P4 已证）是让作者自己负责的；新 API 不要重蹈覆辙。
2. **时间全部来自 `t`**，不用累加。
3. **`dispose` 里也要销毁**。宿主会兜底，但显式更干净。
4. **不要发几何参数**。发了也会被忽略，但更糟的是它会让人以为几何归脚本管，改装饰反而看不到效果。

### 10.2 `event.json`

```json
{
  "schemaVersion": 1,
  "id": "pev.noise-field",
  "version": "1.0.0",
  "apiVersion": 1,
  "entry": "main.js",
  "icon": "icon.png",
  "category": "DecorationFx",
  "decoration": true,
  "name": { "en": "Noise Field", "zh-CN": "噪声域" },
  "description": {
    "en": "A rectangular or circular region of scrolling red noise, placed as a decoration.",
    "zh-CN": "矩形或圆形的滚动红色噪声区，作为装饰放置。"
  },
  "params": [
    { "name": "shape",            "type": "Enum:PEV.Scripting.NoiseFieldShape, PEV.Core", "default": "Rect", "group": "Geometry" },
    { "name": "feather",          "type": "Float", "default": 0, "min": 0, "max": 8, "group": "Geometry", "showIf": ["shape", "Circle"] },

    { "name": "fillOpacity",      "type": "Float", "default": 0.667, "min": 0, "max": 1, "group": "Layers" },
    { "name": "edgeColor",        "type": "Color", "default": "ff5454", "group": "Layers" },
    { "name": "edgeOpacity",      "type": "Float", "default": 0.8,  "min": 0, "max": 1, "group": "Layers" },
    { "name": "glowColor",        "type": "Color", "default": "ff5454", "group": "Layers" },
    { "name": "glowOpacity",      "type": "Float", "default": 0.8,  "min": 0, "max": 1, "group": "Layers" },
    { "name": "glowPasses",       "type": "Float", "default": 5,    "min": 0, "max": 6, "group": "Layers" },
    { "name": "sparkColor",       "type": "Color", "default": "ff5a5a", "group": "Layers" },
    { "name": "sparkOpacity",     "type": "Float", "default": 1,    "min": 0, "max": 3, "group": "Layers" },
    { "name": "displace",         "type": "Float", "default": 0.35, "min": 0, "max": 1, "group": "Layers" },

    { "name": "noiseBrightness",  "type": "Float", "default": 1.0,  "min": 0, "max": 3, "group": "Noise" },
    { "name": "noiseSaturation",  "type": "Float", "default": 1.0,  "min": 0, "max": 3, "group": "Noise" },
    { "name": "noiseSampleRate",  "type": "Float", "default": 1.0,  "min": 0.25, "max": 4, "group": "Noise" },
    { "name": "lineOpacity",      "type": "Float", "default": 0.35, "min": 0, "max": 1, "group": "Noise" },
    { "name": "noiseFrameCount",  "type": "Int",   "default": 8,    "min": 2, "max": 32, "group": "Noise" },
    { "name": "noiseEvolveSpeed", "type": "Float", "default": 12,   "min": 0, "max": 60, "unit": "fps", "group": "Noise" },
    { "name": "noiseMoveSpeed",   "type": "Vector2", "default": [0.15, 0], "group": "Noise" },
    { "name": "noiseScale",       "type": "Vector2", "default": [1, 1],    "group": "Noise" },
    { "name": "glitchRange",      "type": "Float", "default": 0,    "min": 0, "max": 1, "group": "Noise" },

    { "name": "edgeDistance",     "type": "Float", "default": 0.7,  "min": 0, "max": 2,  "group": "Edge" },
    { "name": "edgeDetail",       "type": "Float", "default": 8,    "min": 1, "max": 32, "group": "Edge" },
    { "name": "edgeThickness",    "type": "Int",   "default": 1,    "min": 1, "max": 6,  "group": "Edge" },
    { "name": "edgeMargin",       "type": "Float", "default": 0.6,  "min": 0, "max": 2,  "group": "Edge" },
    { "name": "edgeFragment",     "type": "Float", "default": 1,    "min": 0, "max": 1,  "group": "Edge" },
    { "name": "preDuration",      "type": "Float", "default": 0,    "min": 0, "max": 5,  "group": "Edge" }
  ]
}
```

实际包里的 `event.json` 每项还带 `order` 与 `label`（中英）。这里省略以保持可读。

**`decoration: true` 是要害。** 它让这个包走装饰行载体（§4.2）而不是注释载体。少写这一行，事件会回到"看不见、点不到"的注释形态。

**`group` 在这里几乎不起作用**，因为这些参数是注入到 `AddDecoration` 面板里的，会被并入原生面板已有的分组。写 `group` 的意义在于将来若原生面板开启分组，新页签能按这里的名字创建。

**`displace` 是总开关。** 它为 0 时海岸带宽度为 0，整个场退化成一块边缘平直的纯几何——这不是 bug，
是「关掉噪域形态」的语义。但它必须**每次调用都传下去**：宿主读的是调用参数而不是 manifest，
少传一个就静默换成宿主默认值。

**枚举 `default` 必须是成员名。** 写 `"center"`（小写）或 `"0"` 都不可靠：`ParamCoercion.ToEnum` 会 `Enum.Parse(..., true)` 忽略大小写，但 `PropertyInfo` 构造期就要校验默认值，报错后控件直接不出现。**一律用精确成员名**。

`category` 必须取 `LevelEventCategory` 的 8 个成员之一 `[源码文档]`：`Gameplay` `TrackFx` `DecorationFx` `VisualFx` `FxModifiers` `Conveniences` `Jank` `Favorites`。噪声域用 `DecorationFx` 最贴切。

---

## 11. 实现顺序

按「哪一步最便宜地退掉最大的风险」排，不按依赖排。

### P-N1：静态噪声方块（约 1 天）

**目标**：屏幕上出现一块会滚动的红色噪声。

- `NoiseTextureFactory`：生成 K 张纹理（矩形遮罩先写死全 1）。
- `NoiseFieldRenderer`：`Set` / `Destroy` / `DestroyAll`，贴到相机前的 quad，按 `size` 定世界尺寸。
- `fx.field` 白名单接线。
- 一个最小 `.event`（只有 `size`、`color`、`noiseEvolveSpeed` 三个参数）。
- **不接遮罩、不接锚点、不接相对对象。**

**验收**：截图里能看到红色噪声在动；`liveEffectCount` 在退出关卡后归零；连续开关 20 次无内存增长（任务管理器看）。

**退掉的风险**：纹理生成是否可行、`SpriteRenderer` 是否在正确层级、`mainTextureOffset` 是否生效。

### P-N2：几何与遮罩（约 1–2 天）

- 几何全部交由装饰本体：位置、长宽、旋转、颜色、透明度、tag、depth、visible。
- 形状只剩矩形与椭圆（圆形走同一对长宽手柄）。
- `lockSize` / `lockRotation` / `relativeTo` / `anchor` 作为**前置概念**退役——它们原本要解决的问题（区域怎么跟随镜头与轨道）在原版装饰里已由 `parallax` 与 `depth` 承担。

**验收**：
- 放置后立刻出现一条可选中的装饰，`scrDecorationManager.GetDecoration(row)` 非空。
- 拖动 gizmo 改位置、拉伸手柄改长宽、旋转，区域每帧跟随。
- 改装饰颜色后填充与边框同色变化；`MoveDecorations` 按 tag 改色同样生效。
- 圆形在拉成长宽不等时呈椭圆，边缘无方形残留。
- 保存后重开，几何与参数都不变（`ENCODE` 断言）。
- 原版客户端打开这条谱面不崩、不显示、不丢行。

**退掉的风险**：装饰组件生命周期（撤销/播放时重建）、`scaleVec` 单位、`maskingTarget` 是否真的落盘。

### P-N3：生命周期与多实例（约 1 天）

- 移动与显隐由**原生** `MoveDecorations` 完成（按 tag），不再单独提供事件。
- tag 索引由 `scrDecorationManager` 提供：装饰行天然进入 `taggedDecorations`。
- 同包多实例互不干扰。

**验收**：同一谱面放 3 个噪声域，参数不同，同时存在且互不覆盖；暂停时画面静止；重开后与原时间点一致。

### P-N4：观感调优（不定）

- 对着 Phigros 参考视频逐参数比。
- 决定是否需要加色混合、是否要做水平切片。

**这一步无法排期**，因为「像不像」是主观的。建议先做完 P-N1 再评估。

---

## 12. 风险登记

| 风险 | 概率 | 影响 | 应对 |
|---|---|---|---|
| 装饰标记图片不存在导致引擎不建对象 | 中 | 无法拖动、tag 找不到 | `TextureManager.LoadTexture` 对缺失文件返回 null，且 `ResizeCollider` / `UpdateHitbox` / `GetDecorationWorldSize` 全部 null 安全 `[源码文档]`；harness 的 `OBJECT-floor{n}` 断言实测 |
| 自定义参数被原版保存时丢弃 | 高 | 换客户端打开即丢参数 | 参数只进 `maskingTarget`（`AddDecoration` 里唯一隐藏且 `encode: true` 的 String），不单独成键 |
| `maskingTarget` 因 disabled 被 `Encode` 跳过 | 中 | 载荷静默不落盘 | `SyncDecorations` 每次写入后同时 `disabled["maskingTarget"] = false` |
| `scaleVec` 单位搞错 | 中 | 尺寸差 100 倍 | 组件侧是倍数（1.0 = 100%），事件侧是百分比；两处读取都已按各自单位处理并写在注释里 |
| `SpriteRenderer` 被轨道遮住 | 中 | 效果不可见 | 用 `sortingOrder ≥ 900` 并截图验证；P4 已证明 32000 可见 |
| 找不到加色 shader | 高 | 观感偏灰 | fallback 到 `Sprites/Default`；用高亮度弥补 |
| `Texture2D` 泄漏 | 高 | 反复进出关卡内存增长 | `Destroy`/`DestroyAll` 里显式 `Object.Destroy(tex)`，并加一个 live 计数供测试断言 |
| 参数滑块拖动导致卡顿 | 中 | 编辑器体验差 | 参数指纹 + 150ms 防抖 |
| 多实例共享纹理时释放过早 | 中 | 后创建的黑块 | 引用计数 |
| 枚举成员改名 | 低 | 旧谱面参数读不出来 | 发布后不改；`StorageValue` 写的是名字 |
| 与 `fx.create` 的 key 命名空间冲突 | 低 | 互相覆盖 | 两套表完全分开（`ContinuousEffects` / `NoiseFieldRenderer`），且都叠 `__instance` 前缀 |

---

## 13. 验证清单

交付前逐条跑。每条都要有可粘贴进日志的断言，不接受"看起来对"。

| # | 检验 | 通过条件 |
|---|---|---|
| V-N1 | 参数类型全部建出控件 | 属性面板里每个参数都有对应控件，没有一个退化成文本框 |
| V-N2 | 枚举下拉可用 | 三个自定义枚举显示为下拉，选项为成员名 |
| V-N3 | 落盘可读 | 注释里枚举是名字，`size` 是 `[4,3]` |
| V-N4 | 原版可开 | 不装模组打开含噪声域事件的谱面，`actions` 完整 |
| V-N5 | 撤销后保存不丢 | 放置 → 撤销 → 重做 → 保存，实例数一致 |
| V-N6 | 复制换 uid | 复制后两个实例 uid 不同，参数独立 |
| V-N7 | 暂停冻结 | 暂停 2 秒，`fx.field` 调用数为 0 |
| V-N8 | 重开对齐 | 重开后同一时刻的 `frame` 与首次播放一致 |
| V-N9 | 检查点跳转 | 跳到中途，区域立即出现在正确位置，不残留旧对象 |
| V-N10 | 退出清理 | 退出关卡后 live 实例数 = 0，纹理数 = 0 |
| V-N11 | 多实例独立 | 3 个实例同时存在，参数互不污染 |
| V-N12 | 视觉可见 | 截图里能看到，且不在 UI 之下 |
| V-N13 | 尺寸正确 | 4 方块宽的区域恰好覆盖 4 格轨道宽度 |
| V-N14 | 长时运行 | 连续播放 3 分钟，帧率无下降，纹理数不增长 |
| V-N15 | 空闲不重建 | 稳定播放 2 秒内纹理组重建次数 = 0（分辨率分档与指纹稳定） |
| V-N16 | 边缘参数生效 | 改 `edgeMargin` 后合成四边形的世界尺寸随之增大；改 `edgeDistance` 后场从实心变为带缺口的形态 |
| V-N17 | 碎片不被裁 | `edgeMargin > 0` 时，几何框外能看到孤立的噪域碎块 |

---

## 14. 必须实测才能定的未知

按影响排序。**这些结论会改变设计，不是细节**。

### Q1. `SpriteRenderer` 的材质 UV 偏移是否生效（**最高优先级**）—— **已实测：生效**

`[实测]` P-N1：`mainTextureOffset` 在 `Sprites/Default` 上确实驱动滚动，采样日志里 uv 从 3.098 推进到 5.725。
退路方案 1/2/3 都不需要。

原始描述保留如下：

见 §4.3。设很大的 `noiseMoveSpeed`，看画面有没有横向滚动。不滚动就走退路。

**为什么排第一**：它决定 `NoiseTextureFactory` 的纹理宽度形态。先验证再写生成器，能省一轮返工。半天以内能验完。

### Q2. `scrDecorationManager.CreateDecoration` 对未知类型的反应

只有走「装饰升级路径」才需要。影响：决定 P5 能不能白送一套编辑器交互。

方法：注册一个 `isDecoration=true` 的类型，调用 `CreateDecoration(type)`，看返回值和异常。

### Q3. 轨道与装饰的 `sortingOrder` 实际范围

影响：`depth` 映射公式。取值不当会让噪声域盖住 UI 或被轨道盖住。

方法：遍历场景里 `Renderer` 的 `sortingLayerName` / `sortingOrder` 打表。

### Q4. `ADOBase.controller.tileSize` 的实测值 —— **已实测：1.5**

`[实测]` P-N1：`size=[6,4]` 的场世界尺寸为 `9.0 x 6.0`，即 1 方块 = 1.5 世界单位。

原始描述保留如下：

影响：全部尺寸换算。

方法：读一次，并与 `floors[0]` 到 `floors[1]` 的世界距离对照。

### Q5. 加色 shader 是否可用

影响：观感，不影响功能。

方法：`Shader.Find` 各候选，打日志。

### Q6. `SpriteRenderer` 在 `EffectCanvas` 的层下是否受相机 `cullingMask` 影响

P4 已间接证明可见，但 P4 用的是同一路径、同样的 layer 0。基本可以认为已解决，**不需要重复验证**。

### 不建议现在验证的

- 装饰升级路径（Q2）：不影响 v1，且结论只影响「能不能省 P5 的工程量」。
- 水平切片 / RGB 偏移：先看主体像不像，不像再谈。

---

## 15. 交付物

### 15.1 代码

| 文件 | 状态 | 说明 |
|---|---|---|
| `src/PEV.Core/Scripting/NoiseFieldRenderer.cs` | 新增 | 实例管理、每帧应用 |
| `src/PEV.Core/Scripting/NoiseTextureFactory.cs` | 新增 | 纹理生成、遮罩、共享缓存 |
| `src/PEV.Core/Scripting/NoiseFieldParams.cs` | 新增 | 三个枚举 |
| `src/PEV.Core/Scripting/Placement.cs` | 新增 | 锚点、相对对象、世界坐标换算 |
| `src/PEV.Core/Scripting/EventContext.cs` | 修改 | `GameBridge.TryPlayNative` 白名单加 `field` / `fieldDestroy`（`GameBridge` 在这个文件里，不单独存在） |
| `src/PEV.Core/Scripting/ScriptHost.cs` | 修改 | Bootstrap 加 `fx.field` / `fx.fieldDestroy`；`OnHostFx` 注入 `__instance` / `__floor` |
| `src/PEV.Core/Scripting/EffectCanvas.cs` | 修改 | `DestroyAll` 增加纹理回收钩子 |
| `src/Events/NoiseField.event/` | 新增 | `event.json` / `main.js` / `icon.png` |
| `src/PEV.Core/Diagnostics/PN1Harness.cs` | 新增 | 按现有 harness 风格 |

### 15.2 构建与部署

**环境事实（已核实）**：

| 路径 | 内容 |
|---|---|
| `D:\Program Files (x86)\Steam\steamapps\common\A Dance of Fire and Ice` | **完整安装**，含 `A Dance of Fire and Ice_Data\Managed\Assembly-CSharp.dll` |
| `E:\SteamLibrary\steamapps\common\A Dance of Fire and Ice` | 只有 `User` 目录（存档/关卡数据），**没有程序集** |

`csproj` 的 `GameDir` 指向 D 盘，是**正确**的，不要改。改到 E 盘会立刻在引用解析阶段失败。

```bash
# 编译前确认引用存在
ls "D:/Program Files (x86)/Steam/steamapps/common/A Dance of Fire and Ice/A Dance of Fire and Ice_Data/Managed/Assembly-CSharp.dll"
```

部署目标（模组目录、`Levels` 目录）在 E 盘。两边是分开的：**D 盘编译，E 盘运行**。

```bash
E="/e/SteamLibrary/steamapps/common/A Dance of Fire and Ice"

# 编译并打包
python tools/build_release.py --version 0.1.0      # 产出 dist/PEV.Core-<version>.zip

# 开发期更快：直接拷 DLL 和事件包
cp src/PEV.Core/bin/Release/PEV.Core.dll      "$E/Mods/PEV.Core/"
cp -r src/Events/NoiseField.event             "$E/Mods/PEV.Core/Events/"
```

改一行就要重拷一次，建议写个 watch 脚本。

**日志**：`$E/Mods/PEV.Core/pev-core.log`，游戏每次启动会被清空（`Plugin.TryResetLog`）。所有 `Plugin.Log` 都写这里。排查「效果不可见」时第一件事是看这个文件，第二件是把 `EffectCanvas.Describe()` 的输出打出来——相机列表、`cullingMask`、`sortingOrder` 全在那行。

### 15.3 测试

- 一张 `NoiseFieldTest.adofai`：3 个实例，覆盖矩形/圆形/两种 `relativeTo`。
- V-N1…V-N14 的输出写进 `PN1-RESULTS.md`，格式照 `P4-RESULTS.md`。

### 15.3 文档

- 本文更新：`[未知]` 的结论回填。
- `docs/EVENT-SDK.md` 增加 `fx.field` 一节。
- `docs/NOISEFIELD.md`：面向作者的参数说明与配方（怎么做出「区域内变红」的观感）。

---

## 16. 一句话总结

这个效果的难点不在渲染，在**几何与生命周期**。噪声本身是低分辨率的程序化纹理，预生成若干帧、靠 UV 滚动和翻页演化，成本可以忽略；真正需要小心的是坐标系（方块 / 镜头 / 行星 × 九宫格锚点 × 缩放锁定）、时间（必须来自歌曲时钟，任何增量累加都会在检查点跳转时错位）、以及资源回收（纹理不是 GameObject，现有清理路径回收不到）。
