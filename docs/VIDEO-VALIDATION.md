# 视频事件验证记录

2026-11-19。环境：ADOFAI r150（2026-11-03）、Unity 6000.3.25f1、Windows Direct3D 11、AMD Radeon RX 6750 GRE 10GB、1600×900。所有结论来自实机运行，脚本见 `tools/run_video_test.ps1`，测试素材由 `tools/make_video_media.ps1` 生成。

## 实测能解码的格式

原生后端（`WindowsVideoMedia`）直接播放，无需任何外部依赖：

| 容器 | 编码 | 结果 |
|---|---|---|
| MP4 | H.264、HEVC、AV1、MPEG-4 Part 2 | 播放 |
| MP4 | VP9 | 播放 |
| MOV / M4V | H.264 | 播放 |
| AVI | H.264、MJPEG | 播放 |
| WMV | WMV2 | 播放 |
| WebM | VP8 | 播放 |

原生后端拒绝、经兼容层转换后可播放：MKV（H.264、HEVC）、FLV、MPEG-TS、OGG/Theora、ProRes、WebM/VP9。转换优先无损 remux，需要时再转码为 H.264 MP4，结果缓存在 `Mods/PEV.Core/video-cache/`。

测试矩阵 16/16 与预测一致（`FORMAT-MATRIX` 与 `FORMAT-NATIVE` 均通过）。

**透明视频不支持。** VP8 WebM 的 alpha 平面不会进入 RenderTexture，画面为不透明。这是后端限制，未做规避。

## 时间同步

`fx.video` 接收绝对媒体时间，宿主每帧向它收敛。实测通过的平台行为：

- 暂停后 `time` 保持不变（`pauseHolds=True`），暂停画面不漂移。
- 播放中与非播放状态设置 `time` 都能落位，`seekCompleted` 正常触发；乱序跳转 4/4 命中（`SEEK-SCRUB`）。
- 解码器可能报告"正在播放"但时钟不动；这种停摆会被检测并重启播放（`StallFrames`）。

包内计时（`MATH-*`）用真实 `main.js` 加合成时钟验证：起始前保持首帧、到达起始点开始播放、进度与歌曲时间一致、变速按倍数缩放、时间回退后回到早期位置。

## 性能与并发

- 1920×1080@60 解码时编辑器保持 ~120 fps，媒体时间与请求时间一致（`PERF-LOCKSTEP`：2.02s / 2.01s）。
- 4 个不同格式同时解码全部成功（`MULTI-INSTANCE`）。
- RenderTexture 在 `Prepare` 完成后按实际分辨率创建一次；作废的 player 延迟到不再准备时销毁，避免在原生解码器打开文件期间销毁对象导致进程崩溃。

## 编辑器

- 装饰行、`pev1:` 载荷、`pevEvents` 依赖写入与回读均通过（`PLACE-*`、`SAVE-*`）。
- 参数注入到原生 AddDecoration 面板（`PANEL-PARAMS`），`maskingTarget`/`maskingType` 在该事件面板隐藏（`PANEL-HIDDEN`），装饰列表显示 manifest 的 `decorationListName`。
- 空闲编辑器显示首帧，拖动改位置、改 `depth` 改排序层、改 `opacity` 同步到画面（`RENDER-*`）。
- 缺失文件与非法素材返回诊断文本而非异常（`DIAG-*`）。

## 未验证

- 真实社区谱面与社区视频素材。
- 其他 GPU、显卡驱动与 Windows 版本上的解码行为。
- 视频音频的听感验证：只确认音轨被识别、音量与静音参数写入 `AudioSource`，未做实听对比。
- 未安装模组时用原版打开含视频事件的谱面（数据形状与噪域相同，但未单独实测）。
- 编辑器关闭再重开的完整流程（只验证了编码/解码往返）。
