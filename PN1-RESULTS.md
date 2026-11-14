# P-N1 验证结果 — 噪声域

> 环境：ADOFAI `r150`（2026/11/03），Unity `6000.3.25f1`，Mono，UMM `0.32.5.0`。
> 原始输出：`Mods/PEV.Core/pn1-results.txt`、`pev-core.log`、`pn1-fields.png`、`pn1-editor-preview.png`。
> 测试谱面：`%USERPROFILE%\Documents\A Dance of Fire and Ice\Levels\PN1Test.adofai`（harness 自动打开）。
> 事件包：`src/Events/NoiseField.event`，36 个参数，分 Geometry / Appearance / Noise / Layers / Edge 五页。

---

## 结果

| 检查 | 结果 |
|---|---|
| 事件注册与参数 | **PASS**：`pev.noise-field v1.0.0 (36 params)`，5 个事件类型发布 |
| 砖块徽标 | **PASS**：3/3 格都有 `EventIndicator` |
| 属性分组 | **PASS**：`useGroups=True`，tab = Geometry, Appearance, Noise, Layers, Edge |
| 条件显示 | **PASS**：`size` 只在 `shape=Rect`、`radius`/`feather` 只在 `Circle` 时出现 |
| 编辑器内实时预览 | **PASS**：按播放前就有 3 个场；改参数后下一帧生效 |
| 三实例同时存活 | **PASS**：`live=3` |
| 同深度并集 | **PASS**：`LAYERS 2/3`，3 个场 → 2 个 quad（floor 4 与 floor 8 同为 depth 2） |
| 边缘参数生效 | **PASS**：`LAYER-BOXES` 显示 quad 已按 `edgeMargin` 撑开（5.4×3.2 / 18.6×12.9） |
| 逐帧动画 | **PASS**：frame 6→3，uv x 7.866→9.442 |
| **UV 偏移是否生效** | **PASS**：`mainTextureOffset` 在 `Sprites/Default` 上确实驱动滚动 |
| 空闲不重建 | **PASS**：`REGEN-IDLE` 2 秒内重建 0 次 |
| 退出清理 | **PASS**：两轮 `live=0, texSets=0` |
| 视觉可见 | **PASS**：截图见 `docs/images/pn1-play.png`、`pn1-editor.png` |
| 几何换算（纯函数断言） | **PASS**：9/9，见下表 |
| tag 驱动移动 | **PASS**：`targets=1`，偏移 (2.00,1.00)、旋转 15°、缩放 1.5 |
| tag 驱动状态 | **PASS**：`visibility=forced-hidden` 命中目标 |
| tag 清除还原 | **PASS**：偏移归零、缩放 1.0、可见性回到默认 |
| 未知 tag | **PASS**：匹配 0 个，不会误伤全场 |

### 几何断言明细

`Placement.Compute` 是纯函数，锚点方向这类最容易写反的东西可以精确断言，不必靠截图：

| 断言 | 期望 | 实测 |
|---|---|---|
| Center 锚点 | 中心在基准点 | `(0,0)` |
| TopLeft 锚点 | 中心在右下方 | `(1.00,-1.00)` |
| BottomRight 锚点 | 中心在左上方 | `(-1.00,1.00)` |
| 两角互为镜像 | `topLeft = -bottomRight` | **PASS** |
| `size` 单位 | 4×3 方块 @ tileSize 1.5 = 6×4.5 | `(6.00,4.50)` |
| `CameraRatio` | 0.5×0.25 @ 18×10 视口 = 9×2.5 | `(9.00,2.50)` |
| `lockSize` | 2×2 @ 2× 缩放 = 1×1 | `(1.00,1.00)` |
| Circle | 半径 3 @ tileSize 1 = 直径 6 | `(6.00,6.00)` |
| 旋转后的锚点偏移 | TopLeft 转 90° = (1,1) | `(1.00,1.00)` |

### 截图证据

- `docs/images/pn1-play.png`：播放中，红色细颗粒铺满矩形区，圆形区与镜头区同时存在。
- `docs/images/pn1-editor.png`：编辑器内，噪声域实时可见；右侧是原生的 Noise Field 面板，参数按分组显示；左上操作提示正常。

本轮（海岸线算法重写后）的现场截图在游戏目录：`pn1-fields.png`、`pn1-editor-preview.png`。
截图里可以看到三件事同时对：**几何框外有飘着的碎片**、**海岸线是粗台阶但描边只有一格宽**、
**内部基本实心**。这三条任意一条不对，就分别对应 §8 / §9 / 的带宽问题。

---

## 这个阶段暴露并修掉的缺陷

### 1. `Vector2` 参数让脚本初始化直接失败

```
JsonSerializationException: Self referencing loop detected for property 'normalized'
  with type 'UnityEngine.Vector2'. Path 'size.normalized'.
```

`LevelEvent` 里的 `Vector2` 是 Unity 结构体，`JsonConvert` 序列化它时撞上 `normalized` 的自引用。
**修正**：`ScriptHost.SanitizeParams` 在宿主边界把它摊平成 `[x,y]`，`Color` 摊平成 `[r,g,b,a]`。
这是所有含 `Vector2`/`Color` 参数的事件都会踩到的坑，不只是噪声域。

### 2. Jint 的墙钟预算把宿主工作也算进去

纹理生成发生在 `fx.field` 的宿主调用内部，但 Jint 的 `TimeoutInterval` 覆盖整个 `Evaluate`，
于是「生成 8 张纹理」被计入脚本耗时，三个实例里两个直接 `time budget exceeded` 而 fault。

**修正**：`MaxStatements(200_000)` 作为真正的防死循环手段，墙钟预算放宽到 2000 ms（仅作兜底）。

### 3. 自定义事件在砖块上不显示徽标

原版 `ShowEventIndicators` 会跳过 `data` 里没有 `angleOffset` 的事件。原生产生事件都带这个键，
我们的自定义参数里没有，于是放上去砖块是空的——这正是「和官方一样」要求的断点。

**修正**：`EditorIntegration.EnsureAngleOffset` 在放置/复制时补 `angleOffset=0`，
并立刻重画该格指示器（`AddEvent` 返回时那一轮绘制已经错过）。

### 4. 属性行标签显示成未解析的键

`Property.info` 的查找顺序是 `editor.{事件}.{属性}` → `editor.{属性}`，两者都不存在。
原补丁只认纯数字的 `editor.<type>`，于是 25 行标签全部退化成原始键或空白。

**修正**：`LocalizationPatch` 支持 `editor.{type}.{prop}`，把 `noiseSampleRate` 变成 `Noise Sample Rate`。

### 5. 参数面板是一整列 25 项，没有分组

原生事件都按 tab 分组。**修正**：manifest 新增 `group` / `order` / `showIf` / `hideIf`，
`EventRegistry` 在声明了 `group` 时打开 `useGroups` 并构造 `LevelEventInfo.groups`。
`size` 只在 `shape=Rect` 时显示，`radius`/`feather` 只在 `shape=Circle` 时显示。

### 6. 同深度噪域叠加会变亮

两个 50% 透明度的 quad 叠加得到 75%，而不是并集。**修正**：渲染单位从「事件」改成「层」，
同深度且包围盒重叠的事件合成为一张纹理，遮罩取 `max`。

**但整深度并集是错的**：第一次实现把第 4 格的矩形和第 8 格的圆强行并成一个横跨半屏的包围盒，
纹理被拉伸，颗粒反而消失。现在按包围盒重叠做贪心聚类——不相交的场永远不可能共享像素，
分开渲染才正确。

### 7. 颗粒太大

初始实现 `6 / sampleRate` 像素一块，看起来是马赛克；参考图是细颗粒。
**修正**：基准颗粒 2 px，密度从 0.55 降到 0.30，亮度分布向暗端偏斜（只有少量亮点）。

### 8. 边缘是「几何软边」而不是海岸线

最初的遮罩是「到几何边的距离 ÷ feather」，得到的是一圈均匀渐隐的软边。
参考视频里根本不是这个：边界是一条咬进去、切出岛、掉下碎片的**等值线**，
而且逐帧在变。软边永远做不出这个——它不是轮廓像不像的问题，是**拓扑会不会变**的问题。

真实机制在 Phira 的 `noise_area` 里读到了（`ActiveBlock` shader + `render.rs::mask_polygons`）：
覆盖判定是滚动噪声对**随带符号距离滑动的阈值**取等值线。

```csharp
float t   = clamp(-d / band, -1, 1);
float thr = 0.5f - (t > 0 ? swingIn : swingOut) * t;
bool  cover = noise > thr;
```

内部阈值趋近 0 → 实心；边界阈值回到中位数 → 等值线即海岸线；外部阈值抬高 → 只剩峰值，
也就是飘在外面的碎片。「脱落、分离、合并、碎片化」全是等值线的必然结果。

细节见 `docs/NOISEFIELD-SPEC.md` §8.3。

### 9. 碎片画不出来（quad 正好卡在几何框上）

阈值公式写对之后，带外的碎片仍然看不见：quad 的尺寸等于几何包围盒，框外的一切被裁掉了。
**修正**：`Request.Margin` 把采样域与 quad 在四边各撑开 `displace * edgeMargin * min(宽,高)`。
区域本身的世界位置不变，只有贴图变大。

### 10. 小场被大场的带宽吞掉

`band` 原本按合并后的包围盒短边算。同一深度上一个小矩形挨着一个大区域时，
小场的整个体积都落在带宽内，直接被侵蚀成碎片。**修正**：`band` 改成每个 `Piece` 自己的字段
（按该场自身短边），`SignedDistance` 返回最近使用者的带宽。合并仍然取距离的 `min`（并集），
但带宽各自独立。

### 11. 脚本没转发参数，宿主静默用默认值

两轮同一类问题：`displace` 没进 `p` 对象、`edgeColor`/`fillOpacity` 进了 `p` 却没进 `push()` 的
`fx.field(...)` 载荷。宿主读的是**调用参数**，缺一个就换成宿主默认值，不报错。
`displace` 缺失时带宽为 0，整个场退化成一块纯几何矩形——看起来像渲染坏了，实际是参数没到。

**修正**：`main.js` 的 `p` 与 `push()` 载荷逐项对齐；`LAYER-BOXES` 断言能发现这类问题
（quad 尺寸没跟着 margin 变，就说明参数没传到位）。

---

## 对 spec 的回填

| 未知项 | 结论 |
|---|---|
| Q1 `mainTextureOffset` 是否生效 | **生效**。`Sprites/Default` 确实走 `TRANSFORM_TEX`，退路方案不需要 |
| Q3 轨道/装饰的 sortingOrder 范围 | 场景内 sprite 实测 `min=-2, max=940`；我们的层在 900+，不与轨道冲突 |
| Q4 `tileSize` 实测值 | **1.5**（`size=[6,4]` 的场世界尺寸为 `9.0 x 6.0`） |
| Q5 加色 shader | 未测，alpha 混合 + 提高亮度已足够；留作观感项 |
| Q6 层与相机 cullingMask | 不再需要单独验证：layer 0 + 高 sortingOrder 已实测可见 |

---

## 参考实现调研（这条路能抄什么、不能抄什么）

用户要求「先看现有开源实现再动手」，实际查下来结论必须先说清楚：

| 来源 | 结论 |
|---|---|
| ADOFAI 社区（`_research/` 下的 Iridium、CustomEvents、Litematica、Petal 等 20 余个仓库） | **不存在噪域/噪声区域类效果**。最接近的是它们自定义事件的元数据构造（`CustomEvents` 的 `groups`/`useGroups` 写法）与装饰贴图加载 |
| Phira（`extra.json` 的 `effects` + 自定义 shader） | 效果模型是「按时间段给整屏挂 shader」。ADOFAI 打包会剥离未被场景资产引用的 shader，**shader 这条路在本项目不可用**，只能走 `SpriteRenderer` + 程序化纹理 |
| **Phira `src/noise_area/`（`ActiveBlock` shader + `render.rs`）** | **直接对上了代码**。覆盖判定是滚动噪声对随带符号距离滑动的阈值取等值线；`mask_polygons` 用真二维布尔差集合并同深度区域。本实现的遮罩算法与同深度并集都来自这里，不是猜的 |
| Phigros 客户端资源拆解（第三方整理，非官方） | 确认了纹理分工：`FD_Noise_*` 红色颗粒、`NoiseBgLines` 横向线噪（alpha 叠加）、`GlitchMap` 水平切片位移；强度由时间轴逐层启用。**本实现的纹理结构按这个分工对齐** |

结论按来源分开说：

- **纹理**（颗粒哈希、行噪线、低分辨率放大）：从 Phigros 的资源分工推出，没有可复制代码。
- **遮罩形态**（海岸线、碎片、同深度并集）：Phira `noise_area` 里有真实实现，读了源码后照做。
- **编辑器元数据**（分组 / 顺序 / 条件显示）：`CustomEvents` 提供了写法参考。

关键的一次认知修正：**边缘不是被"变形"出来的**。在读到 Phira 源码之前，我一直在调「轮廓怎么扭」，
而参考实现里根本没有轮廓这个对象——只有一张阈值化的噪声图。扭轮廓永远调不出碎片，
因为碎片是等值线断开后留下的连通分量。

## 仍未覆盖

| 项 | 说明 |
|---|---|
| 真实鼠标操作 | 徽标/面板/预览都是程序化断言，没有模拟真实点击与拖拽 |
| 不同 `relativeTo` 的几何正确性 | 已验证 Tile / Camera 两种；行星三种、CameraRatio、九锚点未逐个截图 |
| `lockSize` 的缩放行为 | 参数已接通，未在 0.5× / 2× 缩放下逐帧确认 |
| 五个边缘参数的逐档观感 | 默认值（0.7 / 8 / 1 / 0.6 / 1）已截图确认；0、极端值未扫 |
| `glitchRange` | 已接通（行位移 + 环绕），未截图确认观感 |
| 参考图观感对齐 | 颗粒尺寸、海岸线形态已调整，密度/线条/速度仍需对着视频逐参数比 |
| 长时运行 | 只跑了十几秒；3 分钟连续播放未做 |
| 背景扭曲 | 参考里噪域会扭曲底下的图案；目前只有叠加与水平切片，未接 `Hidden/BlendModes/VisualDeco/Grab` |
