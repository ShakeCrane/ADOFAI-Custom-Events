# Phira-Pro 整帧参考样本

样本由固定提交 `c8fb11c6a41faaf1233a5affabf87b0223e2edd8` 的实际 `block_shader.rs`、`block_shader_full.frag` 与 `block_mask.rs` 生成。`tools/prepare_phira_frame.py` 在隔离副本中新建测试驱动，不修改生产源码；以 256×144、无多重采样、时间 0/1/9 绘制。除 `palette-` 外，背景为 RGB 25/255。

无前缀为默认红色激活域，`shifted-` 为偏移旋转的红色激活域，`disabled-` 为未激活材质，`ready-` 为预警的激活层，`ready-full-` 为预警的未激活层和激活层组合，`green-` 为绿色激活域，`hover-` 为双触点，`palette-` 为多色背景。`effect-*.bin` 和 `aux.bin` 是各帧对应的 64×36 RGBA8 原始掩膜；PNG 来自参考端的 OpenGL 帧缓冲读取。完整三态组合仍需在游戏内端到端验证。

`tools/test_noise_d3d11_frames.py` 在独立 D3D11 设备上使用相同掩膜、参考纹理和固定背景执行移植的片元代码，逐帧输出差异，并检查十种顶点/片元接口。`nocapture` 是将已授权的无场景采样近似模式与完整参考对比，数值有意不套用完整模式的回归阈值；脚本另验证远离噪域的边框保持原样。它使用简化全屏顶点及预制纹理，不覆盖 Unity 相机、GrabPass、动态 render targets 或最终色彩管线，因此数值不能替代游戏内视觉验收。脚本的差异上限只是跨 API 回归警报，并非用户认可的最终验收容差。
