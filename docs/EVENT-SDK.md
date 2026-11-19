# ADOFAI `.event` 开发指南

`.event` 是可独立分发的 ZIP 包。它定义编辑器里的事件入口、可编辑参数和执行效果的 JavaScript。谱面记录事件实例，PEV.Core 负责加载包、接入原生编辑器并按歌曲时间执行。开发、校验和打包普通事件只需要本仓库与 Python 3。在编辑器和游戏内验收时需要 ADOFAI、Unity Mod Manager 和启用的 PEV.Core，见[安装说明](INSTALL-zh-CN.md)。

本文以仓库当前的 schema 1–4、API 1–2 为准。项目的实机校准环境是 ADOFAI r150、Unity 6000.3.25f1、UMM 0.32.5.0。分发时注明实测的 Core 与游戏版本，尤其是使用内部渲染接口的事件。

## 1. 从构想选到实现

| 要做什么 | 从哪里开始 |
|---|---|
| 到指定格时闪屏或震屏 | 普通事件，在 `update(t)` 中触发一次。见本页最小示例和 [ScreenShake](../src/Events/ScreenShake.event/main.js) |
| 随时间移动、淡入淡出或循环 | 普通事件，用 `fx.create/set/destroy` 管理图形，以 `t` 求出当前状态。见本页连续动画示例和 [Pulse](../src/Events/Pulse.event/main.js) |
| 可用原生工具拖拽、变换、赋标签的区域 | 采用装饰载体。跟随装饰的渲染需要对应宿主原语。见[装饰与渲染](#6-装饰多入口和高级渲染) |
| 同一包提供多个编辑器按钮 | 用 schema 4 的 `includedEvents`。每个入口有自己的 ID、参数和脚本 |
| 需要宿主尚未开放的游戏数据或操作 | 先查 [GameBridge 的调用分发](../src/PEV.Core/Scripting/EventContext.cs)。需要新能力时，为 Core 增加可复用原语，再由包组合它，见第 6 节 |

动手前确定事件何时开始、何时结束、输入的默认值和单位，以及暂停、重开、从中途开始时应看到什么。把每个参数追到一次计算或宿主调用。分类只决定按钮位置，例如 `Gameplay` 分类本身不会赋予改动判定的权限。

## 2. 十分钟做出第一个包

在仓库根目录新建 `BeatFlash/`，将以下两个文件用 UTF-8 保存进去。示例省略图标，Core 会借用游戏内置图标。

**`BeatFlash/event.json`**

```json
{
  "schemaVersion": 1,
  "apiVersion": 1,
  "id": "example.beat-flash",
  "version": "1.0.0",
  "entry": "main.js",
  "category": "VisualFx",
  "name": { "en": "Beat Flash", "zh-CN": "节拍闪光" },
  "params": [
    { "name": "color", "type": "Color", "default": "66ddff",
      "label": { "en": "Color", "zh-CN": "颜色" } },
    { "name": "duration", "type": "Float", "default": 0.5,
      "min": 0.1, "max": 4, "unit": "beats",
      "label": { "en": "Duration", "zh-CN": "持续拍数" } }
  ]
}
```

**`BeatFlash/main.js`**

```js
function create(ctx, params) {
  var fired = false;

  return {
    update: function (t) {
      if (t < 0 || fired) return;
      fired = true;

      var beats = Math.max(0.1, Math.min(4, Number(params.duration) || 0.5));
      var seconds = beats * ctx.secondsPerBeat;
      if (t >= seconds) return;

      var result = fx.flash({
        color: params.color,
        duration: seconds - t,
        alpha: 0.6 * (1 - t / seconds)
      });
      if (!result.ok) throw new Error(result.error || "fx.flash failed");
    }
  };
}
```

在仓库根目录依次校验源码、打包并列出归档内容。

```bash
python tools/pack_event.py --check BeatFlash
python tools/pack_event.py BeatFlash -o BeatFlash.event
python -m zipfile -l BeatFlash.event
```

`--check` 输入源码目录。打包器默认在当前目录输出 `<id>.event`，`-o` 可以指定输出路径和文件名。归档顶层应直接出现 `event.json` 和 `main.js`。将产出的 **文件** `BeatFlash.event` 放在一张已保存的 `.adofai` 旁，重新打开谱面，在 VisualFx 类别选择“节拍闪光”，放置、改色并试玩。120 BPM 下，默认半拍对应 0.25 秒。若从事件结束之后开始播放，这个示例会跳过闪光。

加图标时，把可解码的 PNG 放入源码目录，并在 manifest 加入 `"icon": "icon.png"`。仓库样例目录名带 `.event` 后缀，也照样作为源码目录传给打包器，例如 `python tools/pack_event.py src/Events/Pulse.event -o Pulse.event`。

## 3. Manifest 与参数

### 版本、路径和名称

| 字段 | 值和用途 |
|---|---|
| `schemaVersion` | 必填整数。普通脚本从 `1` 开始。Schema 2 起可声明内部原语的 `shaderBundle`。Schema 3 起支持 `Choice`、全局设置、装饰默认值和迁移。Schema 4 加入多入口、装饰面板定制与当前内部渲染资源声明 |
| `apiVersion` | 可选整数，默认为 `1`。读取 `ctx.settings` 用 `2` |
| `id` | 必填，大小写敏感。建议用 `作者.事件名` 形式，例如 `team-rhytropy.beat-flash`，并保持稳定 |
| `version` | 必填，使用三段数字，例如 `1.2.0`。Core 读取旧实例时用主版本号判断是否过新，完整的兼容性由作者验收 |
| `entry` | 必填，入口脚本在包根目录下的相对路径。例 `scripts/main.js` |
| `icon` | 可选，图标在包内的相对路径。缺失或解码失败会使用内置图标 |
| `name`、`description` | 字符串或语言对象，例如 `{ "en": "Beat Flash", "zh-CN": "节拍闪光" }`。常用语言键还有 `zh-TW`、`ja`、`ko` |
| `category` | 默认为 `VisualFx`。打包器允许 `Gameplay`、`TrackFx`、`DecorationFx`、`VisualFx`、`FxModifiers`、`Conveniences`、`Jank`、`Favorites` |
| `params` | 可选，参数对象数组。名称在一个 manifest 内唯一，`__pevUid` 由 Core 保留 |

包内路径统一用 `/`，以包根目录为基准，使用与文件一致的拼写，避开绝对路径、盘符和 `..`。源码目录只放要分发的文件。打包器会递归收集其中的文件，跳过以 `.event` 结尾的文件及输出文件自身。包里可以附素材和许可，但素材须有对应的宿主消费者才会在游戏里生效。`main.js` 作为一段脚本执行。有多份 JS 源码时，在打包前合并或构建成一个入口文件。

### 控件和脚本形状

每项写 `name`、`type` 和匹配类型的 `default`。下表给出常用类型的默认值与脚本形状。

| `type` | JSON 默认值举例 | `params[name]` 的形状 |
|---|---|---|
| `Float`、`Int`、`Rating` | `0.8`、`3`、`5` | 数字 |
| `Bool` | `true` | 布尔值 |
| `String`、`Text` | `"hello"` | 字符串，分别用单行与多行控件 |
| `Color` | `"66ddff"` | 颜色字符串。传给 `fx` 时使用六位 RGB |
| `File` | `""` | 文件选择器返回的字符串。JS 没有读取文件的接口。声明 `"fileType": "Video"`（或 `Audio`、`Image`）选择浏览器的筛选类别，不写时按音频处理 |
| `Vector2`、`FloatPair` | `[1.0, 0.5]` | 两个数字组成的数组 |
| `Tile` | `[0, "ThisTile"]` | `[格位整数, 相对位置枚举名]`。解析到游戏对象需要宿主能力 |
| `Enum:Ease` 等 | `"OutQuad"` | 枚举成员名字符串。内置类型用 `Enum:<类型名>`，宿主已加载的自定义枚举用 `Enum:<命名空间.类型名>, <程序集>` |
| `Choice` | `"Normal"` | 包定义的选项名字符串，要求 schema 3 或 4 |

打包器还接受 `Array`、`Export`、`FilterProperties`、`List`、`MinMaxGradient`、`Note`、`ParticlePlayback` 等原生类型字面量，并对 `Enum:...` 检查格式。使用这些专用控件时，以目标游戏构建的 `PropertyInfo` 和实机结果为准，逐一确认默认值、控件、回存和脚本形状。打包通过本身无法验证这些行为。

```json
[
  {
    "name": "mode", "type": "Choice", "default": "Normal",
    "options": ["Normal", "Strong"],
    "optionLabels": { "Normal": { "en": "Normal", "zh-CN": "普通" },
                      "Strong": { "en": "Strong", "zh-CN": "强烈" } },
    "group": "Effect", "order": 0
  },
  {
    "name": "strength", "type": "Float", "default": 0.8, "min": 0, "max": 1,
    "label": { "en": "Strength", "zh-CN": "强度" },
    "group": "Effect", "order": 1, "showIf": ["mode", "Strong"]
  }
]
```

把这段数组写入 `params`，同时把 `schemaVersion` 改为 `3` 或 `4`。`Choice` 要有 1–64 个唯一选项，每项是最长 64 字符的标识符（字母或下划线开头，后续可含数字，排除 `value__`）。默认值须是其中一项。给自定义下拉选项用 `Choice`，避免依赖游戏类型。选项名和参数名随谱面存储，翻译只改显示。

- `min`、`max`、`unit` 用于编辑器输入。脚本仍要校验会影响时长、循环、分配和范围的值。标注 `"beats"` 不会自动单位换算。
- `label` 设置显示名，`group` 建立普通事件的面板页签，`order` 指定排序值。启用分组时建议每个参数都声明组名，首个声明的组为默认页签。
- `showIf` 或 `hideIf` 使用成对条件，例如 `["mode", "Strong"]`。需要合并条件时继续追加键和值。值写成控件匹配的字符串，如 `"Strong"`、`"true"`、`"2"`。在实机上验证显示与隐藏，脚本自行决定是否使用隐藏参数。

### 全局设置

作者放置的每个实例用 `params` 调节。本机统一开关使用 schema 3 或 4 加 API 2，并在 manifest 顶层声明 `globalSettings`。

```json
{
  "globalSettings": [
    { "name": "enabled", "type": "Bool", "default": true,
      "label": { "en": "Enable effect", "zh-CN": "启用效果" } }
  ]
}
```

当前只支持 `Bool`。名称不可重复，请避开 `__proto__`、`constructor` 和 `prototype`。Core 为已加载定义绘制 UMM 开关，按事件 ID 和设置名存入模组目录的 `event-settings.json`。值跟随本机，卸载事件包后仍保留。脚本每次 `update` 从 `ctx.settings.enabled` 读取当前的只读快照。持续效果关闭时主动清除已创建对象。重新开启时按当下的 `t` 恢复。一次性效果应明确重新开启时是否补触发。

## 4. 脚本生命周期和效果接口

入口在 Jint 的严格模式运行，定义全局函数 `create(ctx, params)` 并返回对象。只需要初始化工作时可以返回 `{}`。`update(t)` 和 `dispose()` 均可选。把可变状态放在 `create` 的闭包里。每个放置实例有独立引擎和参数快照。

| 时机 | 行为 |
|---|---|
| 歌曲时间到达或越过该格 | Core 初始化脚本并调用 `create`。从中间开始播放时，首个 `t` 可能大于零 |
| 歌曲时间向前推进且已到该格 | 每帧调用 `update(t)`，参数 `t` 是相对该格的秒数。暂停时停止调用 |
| 重开、检测到明显回退、退出关卡 | 宿主释放实例，正常释放时调用 `dispose()`。回退后根据时间重新初始化 |
| 脚本抛错或超限 | Core 记录该实例错误并回收它的宿主资源。故障时可能跳过 `dispose()` |

`create` 可用 `params`、`ctx.eventTime`、`ctx.floorSeq` 和 BPM 信息做初始化。使用首个 `update` 里的播放时间计算起始画面。`create` 时的 `ctx.time`、`ctx.t`、`ctx.beat` 与播放标志还是初始值。

| `ctx` 字段 | 值 |
|---|---|
| `time`、`eventTime`、`t` | 歌曲时间、所在格进入时间、两者的差。单位均为秒 |
| `bpm`、`secondsPerBeat` | 当前 BPM 与 `60 / BPM`。固定时长可在首帧换算。需要跟随变速时单独设计时间逻辑 |
| `beat` | 当前实现为 `t * bpm / 60`。跨变速时，它不能表示累计经过的精确拍数 |
| `floorSeq` | 实例所在格的序号，可用于定位和诊断，同格实例可相同 |
| `isPlaying`、`isPaused` | 播放标志，实际收到的 `update` 处于播放中 |
| `settings` | API 2 的当前事件设置快照 |

使用 `t` 求当前状态，避免按帧累加时间。为动画定义终点并主动回收资源，宿主会继续调用仍处于运行态的实例。入口是同步的，请在当前回调中完成效果调用。只有 JSON 数据和白名单效果跨过 JS 与宿主的边界。无浏览器、Node、文件、网络或 CLR 接口，字符串编译（`eval`、`new Function`）被禁用。当前 `ScriptHost` 配置的故障保护上限是 200,000 条语句、2 秒超时和 64 层递归。每帧尽量少分配、少调用宿主，把纯数据预处理放在初始化或打包阶段。

### 通用 `fx` 方法

| 调用 | 参数与行为 |
|---|---|
| `fx.flash({color, duration, alpha})` | 全屏闪光并淡出。默认白色、0.3 秒、透明度 0.6 |
| `fx.shake({strength, duration})` | 镜头震动并衰减。默认幅度 0.2、0.4 秒。同一镜头共用震动控制器，重叠时后一次调用接管 |
| `fx.create(key, props)` | 创建一个纯色方形图形。可设 `x`、`y`、`scale`、`angle`、`color`、`alpha`，建对象时可设 `sortingOrder`（默认 1000） |
| `fx.set(key, props)` | 更新传入的绘制属性，省略的沿用上次状态。键不存在时按传入参数创建 |
| `fx.destroy(key)` | 删除本实例的图形。同一键可以重复清理 |

`x`、`y` 以画面中心为原点，`±1` 约对应画面边缘。`angle` 用度，`alpha` 用 0–1。键只需在**本实例**内唯一，例如 `"ring"` 和 `"core"`。不同实例可以复用同一个键，包括放在同一格的两个实例。每次调用返回 `{ ok, error }`（成功时 `error` 通常是 `null`）。检查必要调用的返回值，避免把宿主错误当作成功。

下面是一个可直接替换第 2 节 `main.js` 的连续效果，复用它的 `color` 和 `duration`。发布成第二个包时给 manifest 新的 ID 和显示名。

```js
function create(ctx, params) {
  var key = "pulse";
  var duration;
  var visible = false;

  function expect(result) {
    if (!result.ok) throw new Error(result.error || "Pulse effect failed");
  }

  function remove() {
    if (!visible) return;
    expect(fx.destroy(key));
    visible = false;
  }

  return {
    update: function (t) {
      if (t < 0) return;
      if (duration === undefined) {
        var beats = Math.max(0.1, Math.min(4, Number(params.duration) || 0.5));
        duration = beats * ctx.secondsPerBeat;
      }
      if (t >= duration) {
        remove();
        return;
      }
      if (!visible) {
        expect(fx.create(key, { color: params.color, alpha: 0 }));
        visible = true;
      }

      var p = t / duration;
      expect(fx.set(key, { x: p - 0.5, scale: 0.3 + p, alpha: 1 - p }));
    },
    dispose: remove
  };
}
```

## 5. 安装、依赖和升级

- Core 扫描当前谱面的目录顶层，加载其中的 `.event` 文件。首次试用时先保存谱面。新增或替换包后重新打开谱面，脚本和注册信息会重新加载。
- Core 在保存时写入谱面根对象的 `pevEvents`，条目形如 `{"id":"example.beat-flash","version":"1.0.0","file":"BeatFlash.event"}`。已有 `file` 声明时按文件名匹配，因此用过的包应保持文件名。谱面目录下多个文件定义同一 ID 时，若声明无法唯一选定文件，Core 会报告冲突。发布前通过编辑器导出并检查依赖文件也在归档里。
- 普通事件用原生 `EditorComment` 保存 `@PEV1` 数据，装饰型事件用原生 `AddDecoration` 行。Core 管理载体、实例 UID 和依赖，谱面可在原版游戏打开。
- Core 启用但缺包时，普通事件显示只读的 Missing Event 占位，原始注释保留。对应装饰保留为原生装饰行。装回匹配的文件再重开，检查两种载体都能恢复。
- 升级时沿用 ID、参数名、类型和选项值。新增参数提供兼容默认值，旧实例缺该值时用默认值补齐。重命名字段或改变值含义需要显式兼容逻辑，Core 没有通用 JS 迁移回调。较低主版本的包无法执行由较高主版本写入的实例。新版的包仍应自己兼容旧谱面的值。用旧谱面副本验收，再分发新版。

## 6. 装饰、多入口和高级渲染

### 装饰载体

顶层 `"decoration": true` 为事件使用原生装饰行。作者能用编辑器工具移动、调整大小、改标签。装饰载体提供这些编辑与存储能力。要使渲染效果跟随拖动、`MoveDecorations`、视差或深度，须用能读取原生装饰实时状态的宿主原语。通用 `fx.create` 生成的是屏幕图形。

| 字段 | 怎么用 |
|---|---|
| `decorationDefaults` | Schema 3 起可设新建装饰的 `color`（六位 RGB）与 `tag`（`""` 表示新装饰无标签） |
| `decorationMigration` | Schema 3 起支持从旧注释实例迁入原生装饰属性。每项为 `{ "source": "旧键", "target": "原生键", "conversion": "转换名" }`，最多 16 项，目标不可重复 |
| `hiddenDecorationProperties` | Schema 4 可列出最多 32 个原生属性名，只在该事件面板隐藏 |
| `decorationListName` | Schema 4 可指定装饰列表名称，长度至多 128。可搭配 `icon` 自定义列表图标 |

迁移支持 `scale/tilesToScale`、`position/vector2`、`rotation/float`、`tag/string`、`color/string`、`depth/int`、`opacity/percent`。原生已有字段如 `scale`、`rotation`、`opacity`、`depth`、`tag` 由原生面板负责。额外控件进入共享的 AddDecoration 面板，使用有事件前缀的自定义参数名以减少不同装饰包之间的定义冲突。装饰面板采用原生页签布局。

### 一个包注册多个入口

主 `event.json` 使用 schema 4，加上 `"includedEvents": ["included/control.json"]`。在同一归档放入 `included/control.json` 和它声明的入口脚本。子 manifest 的 `entry`、`icon` 也相对包根，例如 `"entry": "included/control.js"`。支持 1–16 个子 manifest，每个入口的 ID 唯一。子定义可使用适合自己的 schema/API。子 manifest 无效或嵌套包含，以及入口脚本文件缺失，都会使整包注册失败。可按 [NoiseField 主入口](../src/Events/NoiseField.event/event.json)和[状态入口](../src/Events/NoiseField.event/included/state.json)对照目录与字段。

### 视频装饰

[`Video.event`](../src/Events/Video.event/)是视频的参考实现：`"decoration": true` 加 `"hostPrimitive": "fx.video"`，作者新建后得到一条原生装饰行，拖动、缩放、旋转、透明度、深度和标签都由原生工具负责。脚本只决定**当前应该显示哪一帧**。

```js
fx.video("video", {
  file: "clip.mp4",           // 相对谱面目录，随谱面一起分发
  time: 12.5,                 // 绝对媒体时间（秒），不是增量
  playing: true, loop: false,
  speed: 1, trimStart: 0, trimEnd: 0,
  volume: 1, muted: false, fit: "Fit"
});
fx.videoDestroy("video");
```

`time` 写绝对值，暂停、重开、检查点跳转和编辑器拖动都由宿主对齐到该值，不需要脚本自己累加时间。`trimEnd` 为 0 表示到片尾；返回的 `{ ok, error, ready, duration, width, height }` 里 `duration` 只有解码器准备好以后才非零，可用于把循环区间裁到片长。宿主自动换算 `fit`（`Fit` 完整显示、`Fill` 填满裁切、`Stretch` 拉伸）与装饰框的宽高比。

格式：原生后端直接解码 MP4/MOV/M4V（H.264、HEVC、AV1、MPEG-4）、AVI（H.264、MJPEG）、WMV 和 VP8-in-WebM。其余容器（MKV、FLV、TS、OGG/Theora、ProRes、VP9-in-WebM）由兼容层自动转换一次并缓存到 `Mods/PEV.Core/video-cache/`；转换使用机器上的 FFmpeg，可从模组目录、谱面目录或 PATH 找到，没有 FFmpeg 时日志和返回值给出明确诊断而不是静默黑屏。视频素材放在谱面目录，路径写相对值，未安装本模组时原版仍能打开谱面。

### 自定义区域、GPU 与包内状态[NoiseField 源码](../src/Events/NoiseField.event/)展示了可运行的装饰和 GPU 包。以下是该版本 Core 的内部接口。基于它们开发时固定 Core 构建和事件包版本，并对目标 Unity、图形后端和设备实测。

1. 从 manifest 和 [`main.js`](../src/Events/NoiseField.event/main.js) 跟踪一个参数，例如 `animationSpeed`。manifest 定义输入，入口逐帧传给 `fx.field(key, props)`，宿主处理与原生装饰的绑定，结束时用 `fx.fieldDestroy(key)`。`hostPrimitive: "fx.field"` 和 `fieldPreview` 将编辑态预览接到同一类原语，预览映射需和实际播放的参数一致。拖拽、缩放等原生实时属性交给装饰行提供。
2. 把着色器、纹理和运算声明随包分发。用 `shaderBundle` 指向包内 bundle，用 `shaderAssets` 指定 bundle 内的资源角色，用 `imageAssets` 描述图片及采样。当前包的 `computeGraphs`、`vectorPrograms`、`keySpecs` 和 `pixelResources` 声明计算图、受限后台脚本、缓存键与像素输入。`runtimeBindings` 将宿主需要的角色绑定到声明。按参考包的 [pipelines](../src/Events/NoiseField.event/pipelines/) 和 [workers](../src/Events/NoiseField.event/workers/) 追踪输入、输出及 CPU 回退。要接入渲染，资源和角色须由宿主识别与消费。
3. 需要另一个入口驱动状态时，参照[状态脚本](../src/Events/NoiseField.event/included/state.js)向宿主发布 `fx.tagValue({tag, key, value})`。当前字段渲染管线按同包、标签和键消费字符串状态，按事件时间和顺序选值。普通脚本没有通用的读值接口。`fx.fieldMove({tag, offset, rotation, scale, alpha, visibility, clear})` 是当前另一个按标签覆盖字段的内部方法。`visibility` 为 0 时采用字段自己的可见性，1 强制可见，2 强制隐藏。结束时可用 `{tag, clear: true}` 还原。
4. 只改参数和脚本时重新校验打包即可。改了着色器时按[源码和 bundle 构建说明](../src/UnityShaders/Reference/README.md)重建，再验证渲染、预览、回退、重叠和资源回收。复用素材时随包保留所需许可与来源。

如果设计需要目前没有的能力，例如读取星球坐标供任意脚本使用、直接改变轨道或调用新的渲染操作，把它当作 Core 扩展。先定义可复用操作的输入、输出、单位和生命周期，再从 [ScriptHost](../src/PEV.Core/Scripting/ScriptHost.cs) 的 JS 边界和 [GameBridge](../src/PEV.Core/Scripting/EventContext.cs) 的分发接入，给参数做类型及范围校验，按实例管理资源，覆盖回退、故障和 teardown。事件的算法、专属参数和素材仍放进包内。若变更是新的 manifest 字段，同步检查 [EventManifest](../src/PEV.Core/Model/EventManifest.cs) 和[打包器](../tools/pack_event.py)。构建 Core 按[仓库说明](../README.md#build-from-source)操作。

## 7. 验收与排障

| 阶段 | 验收 |
|---|---|
| 离线 | 用 `pack_event.py --check` 校验源码，再打包并检查 ZIP 根目录、入口及所有声明资源。JS 可用 `node --check` 检查语法，但宿主兼容性以实际 Jint 和游戏为准 |
| 编辑器 | 在已保存谱面旁加载包，核对分类、图标、标签、控件、默认值和条件。修改参数后直接试玩，再保存重开。检查同格与异格的多个实例、复制、撤销、重做和删除 |
| 播放 | 确认开始与结束时点、极值、重叠、暂停继续、死亡重开、检查点或从中段播放。确认退出和结束后没有残留。装饰型事件还要验证拖拽、缩放、改标签和 `MoveDecorations` |
| 兼容分发 | 用谱面副本验证缺包后仍可打开、装回可恢复，关闭 Core 后原版能打开。用旧谱面检查升级，用原生导出核对已声明包确实被包含 |
| 交付 | 提供打包后的 `.event`、源码、使用方式、实测环境及结果。有素材时附许可。仅完成离线检查时，标明实机项目待测 |

打包器会检查主要 manifest 规则和受检资源路径，但无法确认 JS 是否定义了可运行的入口、所有参数默认值是否可被原生控件采用、或素材是否会成功渲染。

| 现象 | 先查什么 |
|---|---|
| 打包失败 | 找到 `--check` 报出的文件和字段，确认传入的是含 `event.json` 的目录，并检查声明路径 |
| 快捷栏缺入口 | 查看包是否与已保存谱面同级、ZIP 根目录是否有 manifest，再看日志中的 schema/API、重复 ID、资源路径和加载问题 |
| 参数没显示或不生效 | 检查类型拼写、枚举解析、分组、显示条件和装饰共享属性名。再沿 `params` 查到脚本的真正消费点 |
| Missing Event | 核对 `pevEvents` 的文件名、manifest ID 和主版本。确认正确包就位以后重新打开 |
| 游戏里没有效果 | 检查事件激活状态、当前 `t`、单位、`fx` 返回值。日志里的 `create failed`、`init failed`、`instance faulted` 可定位阶段 |
| 重开后状态不对 | 检查脚本是否从 `t` 重算，用同格两个实例确认对象键作用域，检查 `dispose` 与正常结束路径 |

Core 日志在 `Mods/PEV.Core/pev-core.log`。排查时记录包 ID、实例 UID（日志可见）、目标格、参数、游戏/Core 版本和最短复现步骤。

需要查实现时，面板和存档字段以 [EventManifest](../src/PEV.Core/Model/EventManifest.cs) 与 [LevelStorage](../src/PEV.Core/Storage/LevelStorage.cs) 为准，调用和时间以 [ScriptHost](../src/PEV.Core/Scripting/ScriptHost.cs) 与 [EventInstance](../src/PEV.Core/Scripting/EventInstance.cs) 为准。
