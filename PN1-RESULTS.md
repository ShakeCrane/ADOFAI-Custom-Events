# P-N1 验证结果 — 噪声域

> 环境：ADOFAI `r150`（2026/11/03），Unity `6000.3.25f1`，Mono，UMM `0.32.5.0`。
> 原始输出：`Mods/PEV.Core/pn1-results.txt`、`pev-core.log`、`pn1-fields.png`、`pn1-editor-preview.png`。
> 测试谱面：`%USERPROFILE%\Documents\A Dance of Fire and Ice\Levels\PN1Test.adofai`（harness 自动打开）。
> 事件包：`src/Events/NoiseField.event`，25 个参数，分 Geometry / Appearance / Noise 三页。

---

## 结果

| 检查 | 结果 |
|---|---|
| 事件注册与参数 | **PASS**：`pev.noise-field v1.0.0 (25 params)`，5 个事件类型发布 |
| 砖块徽标 | **PASS**：3/3 格都有 `EventIndicator` |
| 属性分组 | **PASS**：`useGroups=True`，tab = Geometry, Appearance, Noise |
| 条件显示 | **PASS**：`size` 只在 `shape=Rect`、`radius`/`feather` 只在 `Circle` 时出现 |
| 编辑器内实时预览 | **PASS**：按播放前就有 3 个场；改参数后下一帧生效 |
| 三实例同时存活 | **PASS**：`live=3` |
| 同深度并集 | **PASS**：3 个场 → 2 个 quad（floor 4 与 floor 8 同为 depth 2） |
| 逐帧动画 | **PASS**：frame 6→3，uv x 7.866→9.442 |
| **UV 偏移是否生效** | **PASS**：`mainTextureOffset` 在 `Sprites/Default` 上确实驱动滚动 |
| 退出清理 | **PASS**：两轮 `live=0, texSets=0` |
| 视觉可见 | **PASS**：截图见 `docs/images/pn1-play.png`、`pn1-editor.png` |
| 几何换算（纯函数断言） | **PASS**：9/9，见下表 |

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

## 仍未覆盖

| 项 | 说明 |
|---|---|
| 真实鼠标操作 | 徽标/面板/预览都是程序化断言，没有模拟真实点击与拖拽 |
| 不同 `relativeTo` 的几何正确性 | 已验证 Tile / Camera 两种；行星三种、CameraRatio、九锚点未逐个截图 |
| `lockSize` 的缩放行为 | 参数已接通，未在 0.5× / 2× 缩放下逐帧确认 |
| 参考图观感对齐 | 颗粒尺寸已调整，密度/线条/速度仍需对着视频逐参数比 |
| 长时运行 | 只跑了十几秒；3 分钟连续播放未做 |
