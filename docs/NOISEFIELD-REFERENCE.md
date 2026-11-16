# 噪域参考渲染器：运行记录

2026-11-15，Phira-Pro 固定提交 `c8fb11c6a41faaf1233a5affabf87b0223e2edd8`。参考渲染器在本机 AMD OpenGL 4.6 环境下已编译并运行；它是后续 Unity 移植的参考输出，不代表 Unity 移植已经完成。

## 结果及限制

- `block_area_gpu` 的生产渲染器输出了 39 张 PNG；连续两次运行的全部 PNG 文件 SHA-256 一致。已执行的原示例断言通过，覆盖普通域、反转洞、颜色、预警、触点、MSAA 和视口等案例。
- 另运行示例的视口矩阵模式，在 960×540、1023×767、1280×720 下均通过该模式自身的覆盖与颜色断言；日志为 `../matrix-<宽>x<高>.log`。矩阵模式会覆盖同名 PNG，分辨率各自的输出需运行时另存。
- 示例额外比较官方 shader 导出文件；文件缺失，**该比较被跳过**。示例末尾的真实谱面输入也未随仓库提供，**该部分被跳过**。这不是原示例的完整通过结果，也不能证明 Phira-Pro 与官方原始 shader 一致。
- 固定提交的示例有两处 `Zone` 初始化缺少 `projection`，原样不能编译。测试驱动补为生产默认值 `None`；生产渲染器文件没有修改。
- AMD 驱动将清屏色 `0.1` 量化为 26，而示例预期为 25。测试驱动把七处灰色清屏输入固定为 `25/255`；不改动效果算法或放宽断言。颜色基准应使用该明确输入。
- OpenGL 初始化输出 `WGL: failed to create 3.2 context, trying 2.1`，随后报告 `4.6.0 Compatibility Profile Context 26.8.1.260806`。输出反映此配置，不保证跨驱动逐字节一致；Unity D3D11 与 OpenGL 的最终颜色差异仍需测量。

## 本机位置

- 隔离副本：`_research/noisefield-plan/phirapro-reference`；生产源码来自固定提交。
- 输出：该副本下 `target/block-area-gpu/`；重复运行哈希记录 `../reference-first-run-hashes.json`；脚本驱动日志 `../reference-driver.log`。`_research/` 不纳入版本管理。
- 本地工具链：`_research/noisefield-plan/toolchains/`。Rust 为 `nightly-2026-01-01-x86_64-pc-windows-gnu`；GCC 为 WinLibs 16.2.0 UCRT，下载文件 SHA-256 `3c69305081e4a647144339a936244fcf7d3d9e558319e1ad7be2cd2886d4b6a1`，与发布信息一致。工具链不修改系统 PATH。

## 重建驱动

在固定提交的隔离副本中运行 `python tools/prepare_phira_reference.py <副本路径>`，脚本从 Git 读取原示例并生成新的 `prpr/examples/pev_block_area_gpu.rs`，不会覆盖已有文件；它检查提交及 `prpr/src/core` 是否有未提交改动。将 Rust/GCC 加入当前进程 PATH，设置 `PEV_SKIP_MISSING_OFFICIAL_REFERENCE=1`、`PEV_SKIP_MISSING_CHART=1`，在副本根目录执行 `cargo run --locked -p prpr --example pev_block_area_gpu`。未设置跳过开关时，缺失文件仍会导致运行失败。

GPU 示例的截图分辨率默认 960×540。示例内若干断言使用固定坐标，不能直接通过环境变量换分辨率后就当作等价验收。

## 仍未通过的门槛

匹配游戏的 Unity 6000.3.25f1 编辑器已安装在 `_research/noisefield-plan/toolchains/Unity-6000.3.25f1/`；安装包的官方 MD5 和 Unity 签名已验证。首次 batchmode 构建因缺少有效许可证以代码 198 退出。shader bundle、原生 depth 位置的场景采样、Unity 输出与参考的像素对比均未验证。用户要求暂不进行登录，相关构建和实测延后。
