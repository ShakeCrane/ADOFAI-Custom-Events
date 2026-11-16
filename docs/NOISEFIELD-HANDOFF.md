# 噪声域重做 — 接手说明

> **后续进展：**本文件保留 11 月 15 日的历史记录；Unity 编辑器及参考端现已取得，其余状态和未完成事项以 [NOISEFIELD-CURRENT.md](NOISEFIELD-CURRENT.md) 为准。当前不能把“仅需登录打包”当作完成条件。

> 写于 2026-11-15，分支 `noise-field-as-decoration`，基线提交 `46ae7a8`（本轮改动**尚未提交**）。
> 上一份框架级交接见 `docs/HANDOFF.md`；噪域的技术规范见 `docs/NOISEFIELD-SPEC.md`；
> 计划与门槛见 `_research/noisefield-plan/PLAN.md` 与 `_research/noisefield-plan/M0-RESULTS.md`（`_research/` 被 gitignore，内容要点本文已复述）。

---

## 0. 一句话

目标是按 Phira-Pro 默认原生噪域（固定提交 `c8fb11c6a41faaf1233a5affabf87b0223e2edd8`）重做噪域：实时 GPU 掩膜/合成、严格服从原生 depth、三态（Disabled/Ready/Active）、tag 状态事件、星球触点反馈。
**当前卡在 M0：本机拿不到匹配 Unity 6000.3.25f1 的编辑器，因此没有任何自定义 shader**；
不依赖 shader 的框架部分已经完成并实测通过。

---

## 1. 已实测的环境事实（可直接引用，不要重新猜）

| 项 | 值 | 来源 |
|---|---|---|
| 游戏 | ADOFAI r150，Unity **6000.3.25f1**（changeset `e1dba0a9aba4`） | `Player.log`、`globalgamemanagers` |
| 图形 | Direct3D 11.0 level 11.1，**Gamma** 色彩空间，**MSAA 0** | `FrameCapture.DescribeEnvironment()` 实测 |
| 视口 | **1600×900**（不是 1920×1080） | 同上 |
| GPU | AMD Radeon RX 6750 GRE 10GB | 同上 |
| 管线 | Built-in（`Managed` 下无 URP/HDRP 程序集） | 目录检查 |
| 运行时 shader 编译 | **不存在**：`CoreModule` 中 `CreateGPUProgram` / `CompilePass` / `SetSourceComputeShader` 命中数均为 0 | DLL 字符串统计 |
| 可用的内置 shader | `Hidden/BlendModes/VisualDeco/Grab`（2 pass，queue 3000，属性 `_MainTex _Color _Opacity _Tile`）、`Sprites/Default`、`Legacy Shaders/Particles/Additive` | `pn1-results.txt` |
| 排序层 | `Bg`=1320933907，`Default`=0；完整表见 `SortingLayer.layers` | `pn1-results.txt` |
| 原生 depth 映射 | `depth ≥ 0` → 层名 `Bg`、GameObject layer 9；否则 `Default`、layer 7；两种都 `sortingOrder = -depth`（**depth 越大越靠后**） | `scrVisualDecoration.SetDepth` 反编译 |
| 参考提交 | `c8fb11c6…` 已在 `C:/Users/Shake/AppData/Local/Temp/phirapro` | `git log -1` |

构建工具状态：`.NET SDK 10.0.301` 可用；**无** Unity 编辑器可执行文件（`D:\Program Files (x86)\Unity\2022.3.3f1c1` 只有 `modules.json`）；**无** Rust 工具链；**无** MSVC linker / Windows SDK；`winget` 可安装 VS BuildTools 与 LLVM。

---

## 2. 本轮已完成并实测通过（工作树未提交）

| 文件 | 改动 | 实测证据 |
|---|---|---|
| `Diagnostics/FrameCapture.cs`（新增） | 相机 → 自建 RenderTexture → 单次回读 → PNG；写入前删除旧文件；空帧判定（采样不同颜色数 < 8 判失败） | `CAPTURE PASS distinct≈2900`，两图哈希不同 |
| `Diagnostics/PN1Harness.cs` | 截图改用 FrameCapture；新增 `REGISTER-init`、`REGISTER-tag`、`SORTING-layers`、`SORTING-native`、`ZERO-scale`、`ZERO-scale-restore` | 全部 PASS |
| `Diagnostics/P4Harness.cs` | 截图改用 FrameCapture | 编译通过 |
| `Scripting/DecorationBridge.cs` | `Live` 增加 `SortingLayerID/SortingOrder/UnityLayer/Renderable`；零缩放不再被替换成默认尺寸；`NativeSorting` 复现 `SetDepth` 的层/序/layer | `SORTING-native PASS`、`ZERO-scale PASS` |
| `Scripting/NoiseFieldRenderer.cs` | 渲染分组由 `depth` 改为「排序层 + 排序序」；组内再按包围盒并集；`Describe` 在关卡初始化注册；`Refresh` 让未接管的描述项跟随原生装饰；`DropDescribed` 供编辑器预览接管；删除 `900 + depth×10` | `REGISTER-init PASS`（首帧 3 个描述项）、`REGISTER-tag PASS` |
| `Scripting/RuntimeHost.cs` | `Rebuild(level, describeFields)`；Play 路径显式注册；每帧 `Refresh()` | 同上 |
| `Editor/EditorPreview.cs` | 空闲时丢弃已描述项，避免与预览重复绘制 | `PREVIEW-idle 3`、`LIVE-before-play 3` |

回归：**ReviewHarness 32/32 PASS**（含 `FIELD-transitive-union`、`FIELD-hidden-member`、`FIELD-cleanup`）。
构建：`dotnet build -c Release` 0 错误 0 警告；DLL 已部署到 `Mods/PEV.Core/`。

### 2.1 为什么改 depth

旧实现自己发明 `sortingOrder = 900 + depth × 10`，永远在默认层最上面——depth 设成什么都画在轨道前面，等于没有 depth。
现在读承载装饰的真实 `SpriteRenderer.sortingLayerID/sortingOrder`，`syncFloorDepth` 自动被继承（SetDepth 走该分支时已写在 renderer 上）。

### 2.2 为什么改零尺寸

旧 `TryReadDecoration` 在尺寸 ≤1e-4 时返回 false，随后回落到脚本默认几何 4×3，把作者压到零的装饰重新画出来。
现在零缩放保留在表里但 `Renderable=false`，不生成 GameObject；缩放还原后自动恢复。这是计划 §3.4「零缩放不能被替换成默认可见值」的硬要求。

### 2.3 为什么改注册时机

装饰行在关卡加载时就存在，但旧实现要等歌曲时钟走到所在格、脚本 `update()` 首次运行才建场，于是「放置格」偷偷变成了延迟出现条件，time 0 的状态事件找不到后面的场。
现在关卡初始化即 `Describe`，脚本接管前每帧跟随原生装饰。

---

## 3. 当前阻塞（按影响排序）

### 3.1 Unity 6000.3.25f1 编辑器拿不到

- 全球 CDN `download.unity3d.com` 按出口 IP 302 到 `download.unitychina.cn`，而该 CDN **没有** `e1dba0a9aba4`（404）；国内镜像只有 Tuanjie 2020/2021/2022.3。
- 本机代理（`xray`，`127.0.0.1:10808`）走 CERNET（`42.247.94.24`），不是境外出口；**用户已说明把 v2rayN 改成全局即可**，接手后先确认这一点是否已生效。
- 已有许可证 `C:\ProgramData\Unity\Unity_lic.ulf` 绑定 Tuanjie `2022.3.3f1c1`（2025-02），**不能**直接激活 6000.x；需要 Unity Hub 登录账号激活 Personal。

一旦拿到编辑器：安装 Windows Build Support（StandaloneWindows64 默认随编辑器安装），用 batchmode 构建 shader bundle。

### 3.2 Rust / 参考端

- 参考 `block_area_mask_gpu` 依赖缺失的官方 shader 导出文件（`PHIRA_OFFICIAL_SRC/…/Unlit_*.p0.txt`），本机没有；`block_area_gpu` 不需要这些，但它自己依赖 GL 上下文（macroquad headless）。
- 本机无 linker：装 `Microsoft.VisualStudio.2022.BuildTools`（winget 有）或 LLVM 后才能 `cargo build`。
- 不能拿 `NoiseComposer`（C# CPU 近似）冒充 golden——这是计划明令禁止的。

---

## 4. 已确定的架构决策（不要推翻）

1. **成品零外部依赖**（用户明确要求）：shader bundle **打包进 `.event` 包**，由 Core 从包内加载；用户安装即用，不额外下载任何东西。
   - 当前 `.event` 资源读取入口是 `EventLoader.ReadResource(packagePath, relative)`，目录与 ZIP 两种形态都支持，已拒绝 `..`/绝对路径/含 `:` 的路径——bundle 走同一入口。
   - 需要在 `EventManifest` 增加包内 bundle 字段（建议 `shaderBundle`），并在 `pack_event.py` 的 `validate()` 里检查该文件存在。
   - `PEV.Core.csproj` 目前**未**引用 `UnityEngine.AssetBundleModule`；`AssetBundle.LoadFromMemory` 需要它（或反射）。加引用时注意 net48 与 Unity 模块的兼容性。
2. 三态与状态事件分离显隐：Disabled 是参考的未激活材质，**不等于隐藏**；默认 Active。
3. 状态按歌曲绝对时间求值，时间基准是事件格时刻；「预警后激活」事件时刻 = 预警开始，默认 0.5 s。
4. 触点用星球实时位置投影到统一视口判定，不拦截输入、不影响判定。
5. 各中间纹理的 filter/wrap/色彩空间分别规定；颜色空间实测是 **Gamma**，不要按 Linear 写。
6. 不新增原版不认识的 eventType；继续用 `AddDecoration` + `markingTarget` 载荷。

---

## 5. 下一步（按顺序）

1. **确认 v2rayN 全局生效** → 下载并安装 Unity 6000.3.25f1 + Windows Build Support → 激活许可证。
2. 建最小 shader 工程，先只做 M1 的纯色测试材质：
   - 后方测试图 → 噪域 → 前方高对比装饰；前景完全不被采样。
   - 第二层噪域验证能看到第一层结果。
   - 关闭采样时**场景复制次数为 0**（计数证明确实没有 GrabPass/复制）。
   - 深度语义不成立就停在这里，不要继续调噪声。
3. M2：按参考逐 pass 对齐（mask 量化 → 反转 → compose 位移 → edge → glow → touch），用固定时刻的 golden 比对；**整数掩膜要逐字节一致**，颜色差异逐项说明精度来源。
4. bundle 加载链路：`EventManifest.shaderBundle` → `EventLoader.ReadResource` → `AssetBundle.LoadFromMemory` → 缓存 `Shader` 对象；包内缺 bundle 时记录一条明确日志并回退。
5. M3：三态材质、状态事件、面板重整（旧参数标弃用但保留 Raw 数据）。
6. M4：星球触点；M5：性能与回归。

---

## 6. 验证闭环（改渲染前先读）

- 开关文件：`Mods/PEV.Core/pn1-test.txt`（PN1 harness）、`review-test.txt`（ReviewHarness）。**同一时间只放一个**——两个 harness 的 `AutoOpen` 会同时抢加载关卡，结果文件为空且 `scrPlayer.Update` 持续 NRE。
- 结果文件：`Mods/PEV.Core/pn1-results.txt`、`review-results.txt`，每次启动会清空。
- 重启游戏才会加载新 DLL；部署前先 `taskkill` 游戏进程。
- 截图：`pn1-fields.png`、`pn1-editor-preview.png`（RenderTexture 回读，已能产出非空画面）。
- 历史教训：旧的 `ScreenCapture` 路径在本机产出 5 张内容完全相同的纯白 PNG（sha256 `b50177e9…`）——以后看到「截图都在但看不出问题」，先怀疑取证链本身。

---

## 7. 已知未验证 / 风险

| 项 | 状态 |
|---|---|
| shader bundle 在 6000.3.25f1 的加载格式与变体 | 未验证（需要编辑器） |
| 原生排序位置执行 GrabPass 的可行性与成本 | 未验证 |
| 缩放下像素桶滞后是否与新的排序分组冲突 | 未验证（PN1 里 `LAYER-BOXES` 显示层对象存在但尺寸读取为 `gone`，属纹理未上传时的正常表现） |
| 旧谱面的参数迁移 | 未做（M3 范围） |
| 本轮改动 | **未提交**；工作树 6 改 1 新增 |
