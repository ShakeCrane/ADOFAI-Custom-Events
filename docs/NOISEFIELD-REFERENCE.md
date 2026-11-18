# 噪域参考实现与复现

视觉参考为 Phira-Pro 提交 `c8fb11c6a41faaf1233a5affabf87b0223e2edd8`。事件使用的参考纹理来自该提交的 `assets/blockarea/`，来源和许可随包保存。移植用的 HLSL、生成步骤及 Unity bundle 构建方法见 [着色器源码说明](../src/UnityShaders/Reference/README.md)。

要重新生成参考帧，在该提交的独立 Phira-Pro 副本上运行 `python tools/prepare_phira_reference.py <副本路径>`。它检查提交并单独生成测试入口。构建记录使用 Rust `nightly-2026-01-01-x86_64-pc-windows-gnu`、MinGW 工具链和 OpenGL 4.6。在参考仓库根目录运行 `cargo run --locked -p prpr --example pev_block_area_gpu`。仅在官方导出文件或示例谱面确实缺失时，分别设置 `PEV_SKIP_MISSING_OFFICIAL_REFERENCE=1` 和 `PEV_SKIP_MISSING_CHART=1`。

参考程序连续两次生成的 39 张生产渲染器输出哈希一致。已执行的覆盖、组合、颜色、预警、触点和视口断言通过。官方 shader 导出及未提供的谱面没有参与对照。仓库保留可复跑的 [基准样本](../tests/fixtures/) 与 [游戏实测结果](NOISEFIELD-RELEASE-VALIDATION.md)。
