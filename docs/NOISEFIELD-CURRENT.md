# 噪域重做：当前进展与未完成事项

> **更新：**以下为授权前的历史记录，不再代表当前状态。Unity bundle 已构建并在游戏中实测，GPU 路径已接入；最新验证、剩余限制和安装说明见 [发布验证记录](NOISEFIELD-RELEASE-VALIDATION.md) 与 [安装说明](INSTALL-zh-CN.md)。

2026-11-16，本轮起始基线 `46ae7a8`。目标仍是复现 Phira-Pro 固定提交 `c8fb11c6a41faaf1233a5affabf87b0223e2edd8` 的默认噪域。用户要求暂不登录 Unity；本记录不把未运行的方案记为已完成。

## 当前结论

现有游戏仍运行旧的烘焙噪域。新的 GPU 渲染链路、三态和状态事件均为草稿，**没有接入正式游戏渲染**。目前并非只差 Unity 登录：登录后仍须实际构建、导入并验证真实层级采样、颜色、采样开关、资源生命周期、面板与旧谱面兼容；视结果还可能需要修复。不得将草稿包作为成品发布。

Unity 6000.3.25f1 编辑器已安装于 `_research/noisefield-plan/toolchains/Unity-6000.3.25f1/Editor/Unity.exe`；安装包已验证官方 MD5 与签名。没有适用于该版本的有效许可证，batchmode 曾以代码 198 退出。用户明确要求把登录留到最后，不能再为此打断用户。

## 已完成及验证

- 参考端：隔离副本中执行固定提交的 Phira-Pro GPU 示例；官方 shader 导出比较和缺失的谱面案例被跳过。参考程序运行条件、针对驱动测试的两处必要修正及局限见 [NOISEFIELD-REFERENCE.md](NOISEFIELD-REFERENCE.md)。
- 掩膜草稿：`src/UnityShaders/Draft/NoiseFieldCoverage.compute` 对普通、透明及多反转区域，分别在 256×144、255×143 视口与多个时间点对照固定提交生成的二进制样本；覆盖、组合、触点和效果数据在本机 AMD OpenGL 4.6 下逐字节一致。另在 AMD Radeon RX 6750 GRE 的独立 D3D11 硬件设备运行相同计算，覆盖、组合、触点、流动边缘、辉光及实际 R8 输出均逐字节一致。样本和来源见 `tests/fixtures/noise-field-coverage/README.md`；这仍不是 Unity 最终合成画面的结果。
- 多区域颜色：按参考的种子归属与两次 chamfer 扫描实现了独立 CPU 计算，封装单组最多一个后台任务并限制并发；两个视口下位移前的激活与未激活颜色均与参考逐字节一致。共享位移算法的 GPU 颜色采样在 OpenGL 和独立 D3D11 的两个视口、三个时刻也逐字节一致。颜色计算、工作线程和纹理上传已接入未启用的 coordinator 草稿，但 Unity 资源和画面尚未实测；样本来源见 `tests/fixtures/noise-field-colors/README.md`。
- 片元代码：先按参考运行时特化，再经 GLSL→SPIR-V→HLSL→SPIR-V→GLSL 往返，参考示例的 39 张 PNG 哈希一致。生成源和限制见 `src/UnityShaders/Reference/README.md`。Unity ShaderLab 包装为草稿，DXC 和 Windows D3DCompiler 均能编译十个片元变体、顶点和七个 compute 入口；Unity 导入及最终渲染未知。
- D3D11 独立整帧探针发现顶点/片元输入寄存器错位，已在生成器中修正并检查十种变体的接口；多色背景又发现抓取画面纵向反转，已按 Unity 的 `ComputeGrabScreenPos` 平台规则修正。256×144 的激活、偏移旋转、未激活、预警层及两层组合、绿色激活、双触点、多色背景在时间 0/1/9 对照 Phira-Pro OpenGL 原始帧，24 组中最多 276/36864 像素不同，最高平均通道差约 0.0375/255；部分时刻逐像素一致，个别差异像素最大 163/255。该对照采用简化全屏顶点和固定输入纹理，不证明 Unity 的相机、GrabPass 或 depth 正确；详情与样本来源见 `tests/fixtures/noise-field-frame/README.md`。
- 关闭场景采样的独立探针已检查周围背景保持不变，且生成的 shader 无 GrabPass。它以背景色代替真实场景采样，效果必然不同；多色测试中约 7334–7412/36864 像素与完整参考不同，整帧平均通道差约 14.2–15.5/255，不能宣称两种模式视觉等价，也尚未测游戏内性能。
- 独立性能基线：本机 AMD RX 6750 GRE，1600×900、六个区域、一层、100 个计时帧，D3D11 掩膜计算平均约 0.074 ms、P95 约 0.172 ms。此数字不包含 Unity、片元绘制、星球反馈、颜色、场景复制或多 depth。颜色归属在 .NET 8/10 独立测试中 400×224 六区域重算平均约 5.3 ms；改为工作线程复用双缓冲后，稳态数组分配从约 4 MB/次降至接近零。它不代表游戏 Mono、上传或 GC 的实测，不能据此认定高刷目标已通过。
- 框架：包内 `shaderBundle` 路径、包内读取、shader 资源缓存、固定参考纹理读取、区域规划、星球位置采集和触点历史均已实现或搭建草稿。状态事件的预警和状态选择已移到事件包脚本；Core 提供按标签应用状态、合并来源和撤销的字段原语。bundle 尚未实际构建，缓存尚未用有效 bundle 实测。`NoiseFieldGpuDriver` 仅在 `PEV_EXPERIMENTAL_NOISE_GPU=1` 时启用，默认不会替换旧路径，也不能认为已经端到端通过。
- 全局设置：噪域草稿现为 schema 3 / API 2，采样开关在事件包中声明，Core 根据声明通用绘制 UMM 设置并按事件 ID 保存；设置面板不依赖实验环境变量，但安装的正式旧包没有这个新声明。详细边界与遗留问题见 [Core 边界审计](CORE-BOUNDARY-AUDIT.md)。
- 颜色与过渡：草稿 GPU 路径读取原生装饰实时颜色，支持原生颜色变化；新装饰的 `ff5454` 默认色由事件包声明，已有行的原生颜色与旧参数不自动改写。参考区域是矩形，旧版圆形参数保留数据但在新路径不生效，不再导致整个 GPU 渲染器退出。分辨率、颜色模式或显示层发生变化时，新 GPU 分组先准备资源，旧分组保留至全部新分组就绪；被弃用的后台颜色任务会取消排队。分组替换、废弃与重复释放已有独立测试，但真实 Unity GPU 资源、画面过渡和动态颜色仍待有效 bundle 验证。
- Unity 构建脚本已在无需登录的条件下使用本机 Unity 6000.3.25f1 的 Editor 程序集做 C# 编译，零警告零错误。此项只检查 API 和类型引用，不能证明 Unity shader 导入或 bundle 构建成功。
- 兼容修复：按原生装饰排序，保留零尺寸、提前注册装饰、保留描述项的作者参数；无实时装饰时按原生规则回退 Unity layer，实时装饰则读取 SpriteRenderer 子对象的 layer（游戏的 `SetDepth` 修改的是它，而不是父对象），不再合并 Unity layer 不同的字段。触点亮斑均匀参数按参考代码的宽高比及时间修正；游戏实测星球和正交相机在同一深度平面，已修正此前把该投影误判为不可见的问题。普通事件也会注册枚举选项翻译，避免“噪域状态”的下拉选项漏翻译。
- 验证：Release 构建零错误零警告；Python 打包测试 20 项通过。实机自动启动游戏的 ReviewHarness 最近一次 73 项通过，包含全局设置隔离、损坏文件保护、脚本快照刷新、通用选项枚举、本地化隔离、事件声明资源与材质值、非字段装饰迁移、状态事件脚本及无效 bundle 回退；安装实验包后实际放置事件、打开状态面板并调用游戏控件切换状态。隐藏测试进程没有触发 UMM 的 `OnGUI`，全局设置面板实际绘制标记为 `SKIP`，未验证鼠标操作；也没有有效 bundle 以测试新画面。最新 PN1 的 `REGEN-IDLE`、`CLEANUP-0/1`、截图检查均通过，游戏播放时采集到两颗实际星球；没有现成原生装饰的测试谱面改用合成原生行，检查 27 个参数无泄漏。游戏原有事件包、DLL、纹理和测试标记在自动测试后已恢复，新增纹理诊断留在游戏模组目录的 `pn1-textures.pev-review-output*`。
- 游戏内离屏 probe 使用内置 `Hidden/BlendModes/VisualDeco/Grab` 的 Multiply 关键字和 `SortingGroup`：单层时背景颜色变化可被采样；双层时第二层读取第一层结果，前景装饰保持原色。测试证实新启用的 renderer 要等一帧进入相机列表，实验 renderer 已加入接管等待。内置 shader 的结果不能证明自定义 shader 的 GrabPass 已正确工作。

## 阻塞与下一步

1. 草稿 manifest 单独缺少脚本、图标和 bundle；`tools/stage_noise_draft.py` 可在拿到 bundle 后组装实验包，但不验证 bundle 是否真的能在 Unity 中加载。状态事件、噪域面板和旧谱面保留数据已有代码和局部检查，仍须在有效 bundle 下做完整交互和播放验证。
2. 用户有空登录并激活许可证后，以 `tools/build_noise_shader_bundle.py` 在隔离工程构建包，再用 `tools/stage_noise_draft.py --bundle <包路径> --output <噪域.event> --state-output <状态.event>` 组装两个实验事件包。构建成功仍不等于完成；成品要求 bundle 内置 `.event`，不得要求玩家安装 Unity 或另下依赖。
3. 在游戏中验证深度插入点：前后原生装饰、相同与不同 depth、多个采样层、关闭采样时零复制。`GrabPass`、相机与屏幕 UV、D3D11 颜色空间、采样纹理和排序都还没有实测。
4. 将 Unity 帧与参考数据逐项比较，覆盖 Disabled/Ready/Active、重叠与反转、多个颜色、旋转、淡入淡出、星球触点、暂停及回退；验证颜色、资源释放、性能与编辑器交互。测试未完成前不得称作严格复现。

## 复现现有检查

```powershell
dotnet build src/PEV.Core/PEV.Core.csproj -c Release --no-restore
python -B -m unittest discover -s tools -p 'test_*.py'
dotnet run --project tests/noise-color/NoiseColorProbe.csproj -- tests/fixtures/noise-field-colors
dotnet build tools/unity-noise-bundle-check/EditorApiCheck.csproj -p:UnityManaged=<Unity的Editor/Data/Managed/UnityEngine目录>
python -B tools/test_unity_shader_draft.py --dxc _research/noisefield-plan/toolchains/dxc/bin/x64/dxc.exe
python -B tools/test_noise_d3dcompile.py
$env:PYTHONPATH = (Resolve-Path '_research/noisefield-plan/toolchains/python-libs').Path
$env:PATH = (Resolve-Path '_research/noisefield-plan/toolchains/mingw64/bin').Path + ';' + $env:PATH
python -B tools/test_noise_d3d11.py --cxx _research/noisefield-plan/toolchains/mingw64/bin/g++.exe
python -B tools/test_noise_d3d11_frames.py --cxx _research/noisefield-plan/toolchains/mingw64/bin/g++.exe
python -B tools/test_noise_coverage_gpu.py --glslang _research/noisefield-plan/toolchains/bin/glslang.exe --spirv-cross _research/noisefield-plan/toolchains/spirv-cross.exe --case normal
python -B tools/test_noise_coverage_gpu.py --glslang _research/noisefield-plan/toolchains/bin/glslang.exe --spirv-cross _research/noisefield-plan/toolchains/spirv-cross.exe --case faded
```

两种游戏 harness 的 `AutoOpen` 会互相冲突，不能同时启用。实机自动测试要备份并恢复游戏的 DLL、测试标记及结果，不应干扰已有游戏进程；使用相机截图而非把全白帧误当作有效图像。历史环境和基线见 [NOISEFIELD-HANDOFF.md](NOISEFIELD-HANDOFF.md)。
