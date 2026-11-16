# Phira-Pro 默认噪域 fragment 移植草稿

`block_shader_full.hlsl` 是固定提交 `c8fb11c6a41faaf1233a5affabf87b0223e2edd8` 的 `prpr/src/core/block_shader_full.frag` 经 glslang 16.6.0 和 SPIRV-Cross（`57e4efcc84112abcd426dc5ec248b5339bee04f2`）生成的 HLSL 5.0，来源遵循 GPL-3.0-or-later。本仓库以 AGPL-3.0 发布；保留生成文件中的来源信息。

生成脚本为 `tools/translate_phira_shader.py`。它验证原文件 SHA-256，将 GLSL ES 100 语法升级到 ES 310 后编译为 OpenGL SPIR-V、生成 HLSL，并让 glslang 重新编译生成结果。转换过程保留原 fragment 算术，不验证 mediump 精度等价。

**这是未接入的草稿，不能用于交付。** Unity shader 包装、顶点及屏幕坐标、采样器与纹理资源绑定、色彩空间、精度、按 depth 采样、两种场景采样模式和所有逐像素对照均尚未验证。独立 HLSL 语法通过不能替代 Unity 打包与游戏内对照。

在 AMD OpenGL 环境做过一次 GLSL → SPIR-V → HLSL → SPIR-V → GLSL 的往返实验：39 张新输出中 35 张哈希相同，4 张明显不同，不能判定等价。参考 renderer 会按 GLSL 源码文本替换来特化关闭触摸分支；往返代码已将该函数内联，实验中加入的空占位函数只能避免测试驱动崩溃，不能等效恢复特化。因此差异原因目前尚未确认，也不能把这次实验当作 HLSL 精度误差的测量。

随后先按参考 `load_block_material` 对 disabled、hover、colored 进行特化，再分别往返转换并在参考 GPU 驱动中加载六个变体：**39/39 新输出 PNG 哈希一致**，驱动中其余未跳过的断言通过。`Variants/` 保存这六个经过独立语法编译的 HLSL，文件名三位依次表示 disabled、hover、colored；生成脚本的 `--variants-dir` 可重建，生成结果与实验中的 HLSL 逐字节一致。该实验使用 OpenGL 4.6，官方导出文件比较和缺失谱面仍被跳过，Unity 与 D3D11 结果未知。
