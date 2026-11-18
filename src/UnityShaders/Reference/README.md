# 噪域着色器来源与构建

`block_shader_full.hlsl` 与 `Variants/` 来源于 Phira-Pro 固定提交 `c8fb11c6a41faaf1233a5affabf87b0223e2edd8` 的 `prpr/src/core/block_shader_full.frag`，遵循 GPL-3.0-or-later。翻译使用 glslang 16.6.0 和 SPIRV-Cross 提交 `57e4efcc84112abcd426dc5ec248b5339bee04f2`。`tools/translate_phira_shader.py --help` 列出重建原始 HLSL 和特化变体所需参数。

运行 `python tools/generate_noise_unity_shaders.py` 可由特化变体重新生成 [Unity 运行时着色器](../Runtime/Generated/)。计算遮罩的源码位于 [NoiseFieldCoverage.compute](../Runtime/NoiseFieldCoverage.compute)。普通 Core 打包使用事件包内现有的 shader bundle。修改着色器后，可用 Unity 6000.3.25f1 重建 Windows bundle：

```powershell
python tools/build_noise_shader_bundle.py --unity <Unity.exe> --project <新建工作目录> --output <输出目录>
```

将实测通过的 `noise-field.bundle` 放入 `src/Events/NoiseField.event/assets/` 后，用 `tools/pack_event.py` 重新打包。源码生成和离线编译无法替代 Unity 运行验证。[参考渲染复现](../../../docs/NOISEFIELD-REFERENCE.md)与[实测记录](../../../docs/NOISEFIELD-RELEASE-VALIDATION.md)说明了对照范围。
