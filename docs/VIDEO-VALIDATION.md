# 视频事件验证记录

2026-11-19。环境：ADOFAI r150（2026-11-03）、Unity 6000.3.25f1、Windows Direct3D 11、AMD Radeon RX 6750 GRE 10GB、1600×900。结论全部来自实机运行：`tools/run_video_test.ps1` 在真实编辑器里跑完一份随附谱面，`tools/test_video_vanilla.ps1` 检查数据形状与原版启动，素材由 `tools/make_video_media.ps1` 生成。

## 实测格式

原生后端（`WindowsVideoMedia`）直接解码，不依赖任何外部程序：

| 容器 | 编码 | 结果 |
|---|---|---|
| MP4 | H.264、HEVC、AV1、MPEG-4 Part 2、VP9 | 播放 |
| MOV / M4V | H.264 | 播放 |
| AVI | H.264、MJPEG | 播放 |
| WMV | WMV2 | 播放 |
| WebM | VP8 | 播放 |

原生拒绝、经兼容层转换后可播放：MKV（H.264、HEVC）、FLV、MPEG-TS、OGG/Theora、ProRes、WebM/VP9。转换优先无损 remux，不行再转码为 H.264 MP4，结果缓存在 `Mods/PEV.Core/video-cache/`，按路径、大小和修改时间做键。

两个矩阵都是 16/16：`FORMAT-MATRIX`（开启兼容层，全部可播放）与 `FORMAT-NATIVE`（关闭兼容层，逐项符合上表）。

**透明视频不支持。** VP8 WebM 的 alpha 平面不会进入 RenderTexture，画面是不透明的。这是后端限制，本版未规避。

## 时间同步

`fx.video` 收绝对媒体时间，宿主每帧向它收敛。

- 播放推进：按下编辑器的播放键后媒体时间随歌曲时钟前进（`PLAY-ADVANCES`）。
- 暂停保持：暂停 45 帧后画面停在原处（`PLAY-HOLDS`），不会继续解码。
- 恢复继续：取消暂停后从歌曲当前时间接着走（`PLAY-RESUMES`）。
- 跳转落位：乱序跳转 2.00 → 0.40 → 2.60 → 0.05，4/4 命中（`SEEK-SCRUB`）。
- 包内计时（`MATH-*`）用真实 `main.js` 加合成时钟验证：起始前保持首帧、到达起始点开始播放、进度与歌曲时间一致、变速按倍数缩放、时间回退后回到早期位置。

跳转期间解码器会短暂报告"正在播放"而时钟不动。这种停摆会被检测并按上限重启，而不是让画面停在旧位置。检测只在确实应播放时生效，否则空闲编辑器会被误判。

## 性能与并发

- 1920×1080@60 解码时编辑器保持约 120 fps，媒体时间与请求时间一致（2.02s / 2.01s）。
- 4 种不同格式同时解码全部成功。
- RenderTexture 在 `Prepare` 完成后按实际分辨率创建一次。作废的播放器延迟到不再准备时才销毁——原生解码器打开文件期间销毁对象会让进程直接退出，这不是可选的加固。

## 编辑器与存储

- 装饰行、`pev1:` 载荷、`pevEvents` 依赖写入与回读通过（`PLACE-*`、`SAVE-*`）。
- 参数注入原生 AddDecoration 面板（`PANEL-PARAMS`），`maskingTarget`/`maskingType` 在该事件面板隐藏（`PANEL-HIDDEN`），装饰列表显示 manifest 的 `decorationListName`。
- 空闲编辑器显示首帧；改位置移动画面、改 `depth` 换排序层、改 `opacity` 同步到画面（`RENDER-*`）。
- 编码后重新打开，行、uid 和渲染都恢复（`REOPEN-*`）。
- 缺失文件、非法素材返回诊断文本而不是异常（`DIAG-*`）。
- 音频：带音轨的文件报告 1 条音轨，音量与静音写入 `AudioSource`（`AUDIO-*`）。

## 原版兼容

谱面把视频存成原生 `AddDecoration` 行：所有键都是原生属性，事件体在 `maskingTarget` 里，标记图指向一个故意不存在的文件。`test_video_vanilla.ps1` 检查这两点，并在移除 `Mods/PEV.Core` 后确认游戏仍能正常启动、日志中没有 Core 加载记录。

## 未验证

- 真实社区谱面与社区视频素材。
- 其他 GPU、显卡驱动与 Windows 版本上的解码行为。
- 视频音频的听感：只确认音轨被识别、参数写入 `AudioSource`，没有做实听对比。
- 在原版编辑器里打开含视频事件的谱面：游戏没有命令行开谱面入口，自动化只能验证数据形状与原版启动，不能代跑它的文件对话框。
- 多种分辨率/帧率的组合压测（只单独测了 1080p60 与 4K 的解码与 320×180 的格式矩阵）。
