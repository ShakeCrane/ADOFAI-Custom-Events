# 噪域掩膜参考数据

这些原始字节来自 Phira-Pro 固定提交 `c8fb11c6a41faaf1233a5affabf87b0223e2edd8` 的 `prpr/src/core/block_mask.rs`。数据通过隔离副本调用 `Masks.render_displaced()` 生成；测试驱动仅添加只读 getter 以导出私有中间数组，生产算法未改动。输出尺寸分别为 64×36 和 62×34，即各测试视口每维 `max(1, floor(size/8))*2`。

两组测试场景的矩形、状态、透明度和旋转在 `tools/test_noise_coverage_gpu.py` 的 `AREAS` 和 `FADED_AREAS` 中固定；后者覆盖半透明重叠和三个反转区域。文件为逐行排列的无压缩 uint8，RGBA 文件按像素交错；`sources`、`base`、`aux-compose`、`effect-*` 是四通道，其余为单通道。`active-compose` 是位移之前的启用状态组合掩膜；`base` 的 A 和 `aux-compose` 分别包含未激活、预警组合。`effect-0`、`effect-1`、`effect-9` 包含位移、边缘和辉光。`BlockNoise1.png` 原样来自同一参考提交的 `assets/blockarea/`，用于位移采样。

运行脚本需提供 glslang 和 SPIRV-Cross 可执行文件，并安装 `moderngl`、`glfw`、`numpy`、`Pillow`；分别用 `--case normal` 和 `--case faded` 测试。脚本将仓库的 HLSL 计算阶段转换为 GLSL，在 OpenGL 4.3 以上的 GPU 执行，再与这些字节比较。两组场景、两种分辨率和三个时刻全部逐字节一致。该测试不覆盖 Unity、D3D11、透视投影、所有输入、纹理颜色或触点渲染。

通过 `tools/prepare_phira_reference.py` 和 `tools/prepare_phira_coverage.py` 可在固定提交的隔离副本中生成导出驱动；后者仅复制原始 mask 源码并加只读 getter，不修改参考仓库的生产源码。驱动生成的原始文件已与本目录先前捕获的数据逐字节核对。

`tools/test_noise_d3d11.py` 另在 Windows 硬件 D3D11 设备执行相同 HLSL 计算入口，并直接检查 R8 输出、触点以及颜色位移；运行需要 Pillow、MinGW g++ 和 Windows D3D11/D3DCompiler，不依赖 Unity 登录。它仍不覆盖 Unity 的资源导入和最终合成画面。
