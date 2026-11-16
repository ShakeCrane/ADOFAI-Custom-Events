# M0 结果：构建环境与取证链

> 执行日期：2026-11-15。基线提交：`46ae7a8`。计划：`_research/noisefield-plan/PLAN.md`。

## 结论

**M0 未通过，停在门槛处。** 两项交付物缺失，原因均为本机不存在对应工具链，不是实现选择：

| 交付物 | 结果 | 证据 |
|---|---|---|
| 匹配 Unity 6000.3.25f1 的 shader / compute shader 构建链 | **缺失** | `D:\Program Files (x86)\Unity\2022.3.3f1c1` 只含 `modules.json`，无 `Unity.exe`；全盘 `Unity.exe` 搜索为空 |
| 参考 renderer 的独立构建入口 | **缺失** | 无 Rust 工具链（`cargo` / `rustc` 不在 PATH，`~/.cargo`、`~/.rustup` 不存在） |
| 有效的 ADOFAI 离屏截图 | **已补齐** | 旧的 `ScreenCapture` 路径产生 5 张内容相同的纯白 PNG（sha256 `b50177e9…`）；已改为 RenderTexture 回读 + 空图判定，见 §3 |
| 构建日志、环境清单、参考提交与资产哈希 | **已补齐** | 见 §1、§2、§4 |

按计划 §4 的规则，此阶段不进入 M1/M2 的大规模移植，也不退回旧的最终像素烘焙方案。

## 1. 环境清单

| 项 | 值 |
|---|---|
| 游戏 | ADOFAI r150（2026/11/03） |
| Unity | `6000.3.25f1`（`Initialize engine version`，`globalgamemanagers` 内字符串一致） |
| 图形 API | Direct3D 11.0 level 11.1（`boot.config`：`force-d3d11-bitblt-model=`） |
| GPU | AMD Radeon RX 6750 GRE 10GB |
| 脚本运行时 | Mono |
| UMM | 0.32.5.0 |
| 渲染管线 | Built-in（`Managed` 下无 URP/HDRP 程序集） |
| 构建工具 | .NET SDK 10.0.301；`PEV.Core` 目标 `net48`，`dotnet build -c Release` 0 错误 0 警告 |
| 证据来源 | `%USERPROFILE%\AppData\LocalLow\7th Beat Games\A Dance of Fire and Ice\Player.log`、`boot.config`、`globalgamemanagers` |

运行时 API 实测（`pn1-results.txt`）：

- `Hidden/BlendModes/VisualDeco/Grab`：存在，`isSupported=true`，2 个 pass，queue 3000，暴露 `_MainTex _Color _Opacity _Tile`。
- `Sprites/Default`、`Legacy Shaders/Particles/Additive`：存在。
- `scrDecorationManager.CreateDecoration(LevelEvent, out bool, int)`：签名可用。
- 场景 SpriteRenderer sortingOrder 实际范围：`min=-4 max=940`。

## 2. 参考提交与资产

参考仓库 `C:/Users/Shake/AppData/Local/Temp/phirapro`，`git log -1` = `c8fb11c6a41faaf1233a5affabf87b0223e2edd8`（与计划固定值一致）。

固定提交下的 blob（`git ls-tree`）：

| 文件 | blob |
|---|---|
| `prpr/src/core/block_shader.rs` | `a7ad749e387204067451cb2ce4aaf15098e63710` |
| `prpr/src/core/block_mask.rs` | `e083956e8be91f565a21d134f0417d874159e5fc` |
| `prpr/src/core/block_shader_full.frag` | `17a144fd3e00eff1bc03b64fa8e344ceef9b6b1a` |
| `prpr/src/core/block_shader_full.vert` | `39f699344ec3e719f4902b4a5dfdd5c6a5eeeee3` |
| `prpr/src/core/block_touch.rs` | `b8212651fa7c9de1f08a28be9412f83627093262` |
| `prpr/src/core/chart.rs` | `184b0b9eb6777397cb74480689b95028efaa231f` |
| `prpr/src/core/rpen_skin.rs` | `ecf724778d70395768019e55b3749f0b5e962406` |
| `prpr/examples/block_area_gpu.rs` | `c8b3df1544a2b5680aab82b5cbc19bd67308bb37` |
| `prpr/examples/block_area_mask_gpu.rs` | `24abb6ebb483f44804e91e91370352ffcf68bf7c` |
| `prpr/examples/block_area_frames.rs` | `78b41563d46d847f980069f5da19f9ff06fb845b` |
| `assets/blockarea/BlockNoise1.png` | `c1740f87fb051564ca4ece46fabf9d33231e95f0` |
| `assets/blockarea/PointNoise.png` | `e3f276a84f7c99f0cbb198d88301973f9127233a` |
| `assets/blockarea/FD_Noise_00000.png` | `f65e29d1d170d6de324a851184696b3f5000d77a` |
| `assets/blockarea/TouchHover.png` | `4236129dfe1a84f24f1eeac07250d59074c81f24` |

文件 SHA-256：

| 文件 | sha256 |
|---|---|
| 参考 `BlockNoise1.png` | `b47e2dd828d7930919ec7c0e46e242a37ca5bed296bc294c31ff0f5815ffe318` |
| 参考 `PointNoise.png` | `dd4d5defae07f92b4e170b81e5ffd417f7e94cb573d495af534375f043363cfa` |
| 参考 `FD_Noise_00000.png` | `8e8afc8ae88d42716943c15bf241f749a49a84fde6498679b6b3291a16ba621f` |
| 参考 `TouchHover.png` | `7c1128464c57ebfd94989bece328bce5e70f7f87b53a2312b2532eeb0b0b5b6e` |
| 当前 `src/PEV.Core/Textures/BlockNoise1.png` | `da61ab2294ddb456314caece0565f24fbddccdf5fe7d98ec4a680c3f66033979` |
| 当前 `src/PEV.Core/Textures/PointNoise.png` | `fe1a54ef26f7d95d781f5d801a28ad82a7927c567218cda79e2a66a5988a7984` |
| 当前 `src/PEV.Core/Textures/FD_Noise.png` | `5f98b4bef6073a0190bc59d0fe64db569df1c32c457c7a178310a1fc5b793576` |

**文件哈希不同不代表像素不同**：PNG 编码参数不同即会得到不同文件。计划 §2 记录的是解码后像素哈希，本轮未重复计算；`FD_Noise.png` 与 `FD_Noise_00000.png` 是两张不同来源的图，计划已确认像素不同，实施时以参考目录的 `FD_Noise_00000.png` 为准，并随包固定。

## 3. 取证链（已修复）

问题：`ScreenCapture.CaptureScreenshot` 在本机产出 5 张内容完全相同的纯白 PNG（`p4-mid`、`p4-probe`、`p4-pulse-alive`、`pn1-editor-preview`、`pn1-fields` 均为 `b50177e9…`，18959 字节）。纯白图无法区分"效果缺失"与"截图失效"，这正是像素验收要排除的情形。

修复（`src/PEV.Core/Diagnostics/FrameCapture.cs`）：

- 用 `Camera.Render()` 渲染到自建 `RenderTexture`（ARGB32、24 位深度、`RenderTextureReadWrite.Default`），`ReadPixels` 回读一次，编码 PNG。
- 写入前删除同名旧文件：无效帧不会留下上一次的图冒充本次证据。
- 有效性判定：按 4 像素步长采样计算平均亮度、方差、不同颜色数；不同颜色数 < 8 判为空帧，报告失败且不落盘。
- 同时记录取证环境：`graphicsDeviceType`、`activeColorSpace`、MSAA、视口、设备名。
- 运行期渲染路径不使用回读；这是 harness 专用的一次性读取。

`P4Harness` 与 `PN1Harness` 的截图调用已切到该路径，结果行会给出 `distinct`、`mean`、`variance`。

**实测（2026-11-15，PN1 harness）：**

```
CAPTURE     PASS  editor-preview: 1600x900 bytes=5761403 distinct=2915 mean=0.0670 variance=0.024056
CAPTURE     PASS  fields:         1600x900 bytes=5761403 distinct=2816 mean=0.0700 variance=0.032265
CAPTURE-ENV INFO  api=Direct3D11 colorSpace=Gamma msaa=0 viewport=1600x900
                  device=AMD Radeon RX 6750 GRE 10GB
```

两张图内容哈希不同（`1579948a…` / `bb52ad41…`），不再是此前 5 张完全相同的纯白图。

由此确认的环境事实：**颜色空间是 Gamma，不是 Linear**；MSAA 关闭；实际视口 1600×900（非 1920×1080）。后续像素比较必须以 Gamma + 1600×900 为基线。

## 4. 参考端不可运行的部分

现有 `block_area_gpu` 示例依赖缺失的外部官方 shader 导出文件（计划 §4 M0 已注明），本机也无 Rust 工具链，因此：

- **没有**参考端 golden 数据产出；
- 生产 GPU 路径与参考的逐字节比较（M2 通过条件）**无法在本轮进行**。

不以 C# 现有实现冒充参考。`NoiseComposer` 是待替换的 CPU 近似，不是基准。

## 5. 未决与下一步

| 项 | 状态 |
|---|---|
| Unity 6000.3.25f1 编辑器（含 Windows Build Support） | **需要用户提供**；没有它无法编译 shader / compute shader 并用 AssetBundle 打包 |
| Rust 工具链 + 参考示例的 shader 导出文件 | **需要用户提供**；没有它无法产出 golden |
| 离屏截图有效性 | **已实测通过**（`distinct≈2800–2900`，两图哈希不同） |
| 原生 depth / sorting 读取 | **已实测通过**：`SORTING-native PASS`，全部字段落在 `scrVisualDecoration.SetDepth` 会用的层与序上 |
| 零尺寸 / 隐藏语义 | **已实测通过**：`ZERO-scale PASS`（`renderable=False`，无 GameObject），还原后 `renderable=True` |
| 关卡初始化即注册 | **已实测通过**：`REGISTER-init PASS`（首帧 3 个描述项），`REGISTER-tag PASS`（未开播即可按 tag 找到 2 个） |
| 回归（ReviewHarness） | **32/32 PASS**：存储、脚本隔离、纹理所有权、`FIELD-transitive-union` / `FIELD-hidden-member` / `FIELD-cleanup` 均未受本轮渲染分组改动影响 |

## 6. 本轮代码改动（已构建、已部署、已实测）

| 文件 | 改动 |
|---|---|
| `Diagnostics/FrameCapture.cs` | 新增：相机 RenderTexture 回读、空帧判定、取证环境记录 |
| `Diagnostics/PN1Harness.cs` | 截图切到 FrameCapture；新增 `REGISTER-init`、`REGISTER-tag`、`SORTING-layers`、`SORTING-native`、`ZERO-scale`、`ZERO-scale-restore` 断言 |
| `Diagnostics/P4Harness.cs` | 截图切到 FrameCapture |
| `Scripting/DecorationBridge.cs` | `Live` 增加 `SortingLayerID` / `SortingOrder` / `UnityLayer` / `Renderable`；零缩放不再被替换成默认尺寸；`NativeSorting` 复现 `SetDepth` 的层与序 |
| `Scripting/NoiseFieldRenderer.cs` | 渲染分组改按「排序层 + 排序序」；按绘制序分组后组内并集；`Describe` 关卡初始化注册；`Refresh` 未接管项跟随原生装饰；`DropDescribed` 供编辑器预览接管；丢弃 `900 + depth×10` |
| `Scripting/RuntimeHost.cs` | `Rebuild(level, describeFields)`；Play 路径显式注册；每帧 `Refresh()` |
| `Editor/EditorPreview.cs` | 空闲时丢弃已描述项，避免与预览重复绘制 |

**注意**：`ScreenCapture` 路径的旧截图证明此功能此前**从未有过有效画面证据**；本轮第一次拿到了非空帧（见 §3）。
