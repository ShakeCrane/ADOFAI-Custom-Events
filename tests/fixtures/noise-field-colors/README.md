# 颜色归属参考数据

这些二进制样本由 Phira-Pro 固定提交 `c8fb11c6a41faaf1233a5affabf87b0223e2edd8` 的 `block_color.rs` 和 `block_mask.rs` 在隔离副本中生成。`tools/prepare_phira_colors.py` 只为复制模块添加 `raw_active` 的只读导出，不修改参考生产源码。

场景包含旋转、不同透明度、覆盖、反转、预警和不同颜色，分别以 256×144 与 255×143 视口计算。`raw-active` 是位移前激活颜色，`active-0/1/9` 是对应时刻位移后的颜色，`disabled` 是未激活颜色；数据为逐行 RGBA8。样本不构成 Unity 或 D3D11 的最终像素验收。
