# ADOFAI `.event` 开发指南

`.event` 是可独立分发的 ZIP 包。它定义编辑器里的事件入口、可编辑参数和执行效果的 JavaScript。谱面记录事件实例，PEV.Core 负责加载包、接入原生编辑器并按歌曲时间执行。开发、校验和打包普通事件只需要本仓库与 Python 3。在编辑器和游戏内验收时需要 ADOFAI、Unity Mod Manager 和启用的 PEV.Core，见[安装说明](INSTALL-zh-CN.md)。

本文以仓库当前的 schema 1–5、API 1–2 为准。项目的实机校准环境是 ADOFAI r150、Unity 6000.3.25f1、UMM 0.32.5.0。分发时注明实测的 Core 与游戏版本，高级区域管线需要同时注明目标图形后端。

## 1. 从构想选到实现

| 要做什么 | 从哪里开始 |
|---|---|
| 到指定格时闪屏或震屏 | 普通事件，在 `update(t)` 中触发一次。见本页最小示例和 [ScreenShake](../src/Events/ScreenShake.event/main.js) |
| 随时间移动、淡入淡出或循环 | 普通事件，用 `fx.create/set/destroy` 管理图形，以 `t` 求出当前状态。见本页连续动画示例和 [Pulse](../src/Events/Pulse.event/main.js) |
| 可用原生工具拖拽、变换、赋标签的区域 | 采用装饰载体。跟随装饰的渲染需要对应宿主原语。见[装饰与渲染](#6-装饰多入口和高级渲染) |
| 按歌曲时钟播放视频，或给完整画面加后处理 | 用 `fx.video` 或包声明的 `screenEffect`。见[视频](#视频)和[全屏屏幕效果](#全屏屏幕效果) |
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
| `schemaVersion` | 必填整数。支持 `1`–`5`。各字段标注的是最低 schema，更高 schema 继承其能力 |
| `apiVersion` | 可选整数，默认为 `1`。读取 `ctx.settings` 用 `2` |
| `id` | 必填，大小写敏感。建议用 `作者.事件名` 形式，例如 `team-rhytropy.beat-flash`，并保持稳定 |
| `version` | 必填，使用三段数字，例如 `1.2.0`。Core 读取旧实例时用主版本号判断是否过新，完整的兼容性由作者验收 |
| `entry` | 必填，入口脚本在包根目录下的相对路径。例 `scripts/main.js` |
| `icon` | 可选，图标在包内的相对路径。缺失或解码失败会使用内置图标 |
| `name`、`description` | 字符串或语言对象，例如 `{ "en": "Beat Flash", "zh-CN": "节拍闪光" }`。常用语言键还有 `zh-TW`、`ja`、`ko` |
| `category` | 默认为 `VisualFx`。打包器允许 `Gameplay`、`TrackFx`、`DecorationFx`、`VisualFx`、`FxModifiers`、`Conveniences`、`Jank`、`Favorites` |
| `params` | 可选，参数对象数组。名称在一个 manifest 内唯一，`__pevUid` 由 Core 保留 |

包内路径统一用 `/`，以包根目录为基准，使用与文件一致的拼写，避开绝对路径、盘符和 `..`。源码目录只放要分发的文件。打包器会递归收集其中的文件，跳过以 `.event` 结尾的文件及输出文件自身。包里可以附素材和许可，但素材须有对应的宿主消费者才会在游戏里生效。`main.js` 作为一段脚本执行。有多份 JS 源码时，在打包前合并或构建成一个入口文件。

### 宿主能力声明

| 最低 schema | 字段 | 作用 |
|---|---|---|
| 1 | `decoration` | 布尔值，默认 `false`。为 `true` 时用原生装饰行保存和编辑 |
| 2 | `shaderBundle` | 包内 Unity AssetBundle 相对路径，渲染原语选择其中的资产，最大 64 MiB |
| 3 | `hostPrimitive` | `fx.field` 或 `fx.video`。选择装饰接入及预览方式 |
| 3 | `globalSettings` | 声明按事件 ID 持久化的本机设置，需要 API 2 |
| 3 | `decorationDefaults`、`decorationMigration` | 装饰默认值及旧载体迁移，见[装饰载体](#装饰载体) |
| 3 | `fieldPreview` | 预览键及参数到字段原语的映射，见[区域与计算管线](#区域与计算管线) |
| 4 | `includedEvents` | 同一 ZIP 注册多个入口，见[多入口](#一个包注册多个入口) |
| 4 | `decorationMarker`、`decorationListName`、`hiddenDecorationProperties` | 装饰行标记、列表名称及面板字段 |
| 4 | `shaderAssets`、`imageAssets`、`vectorPrograms`、`computeGraphs`、`keySpecs`、`runtimeBindings`、`pixelResources` | 按名称声明渲染资源与运行角色，见[区域与计算管线](#区域与计算管线) |
| 5 | `clock` | `tile` 在格位启动，`level` 在歌曲起点启动。省略时视频原语沿用歌曲时钟，其余实例采用格位时钟 |
| 5 | `videoPreview` | 给编辑器静态画面配置参数映射，见[视频](#视频) |
| 5 | `screenEffect`、`editorPanel` | 全屏 shader 阶段及浮动输入面板，见[全屏屏幕效果](#全屏屏幕效果) |

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
| `Choice` | `"Normal"` | 包定义的选项名字符串，要求 schema 3 及以上 |

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

把这段数组写入 `params`，同时把 `schemaVersion` 改为 `3` 或更高版本。`Choice` 要有 1–64 个唯一选项，每项是最长 64 字符的标识符（字母或下划线开头，后续可含数字，排除 `value__`）。默认值须是其中一项。给自定义下拉选项用 `Choice`，避免依赖游戏类型。选项名和参数名随谱面存储，翻译只改显示。

- `min`、`max`、`unit` 用于编辑器输入。脚本仍要校验会影响时长、循环、分配和范围的值。标注 `"beats"` 不会自动单位换算。
- `label`（兼容别名 `customLabel`）设置显示名，`group` 建立普通事件的面板页签，`order` 指定排序值。启用分组时建议每个参数都声明组名，首个声明的组为默认页签。
- `showIf` 或 `hideIf` 使用成对条件，例如 `["mode", "Strong"]`。也接受对象写法，如 `{"mode":"Strong"}`。需要合并条件时继续追加键和值。值写成控件匹配的字符串，如 `"Strong"`、`"true"`、`"2"`。在实机上验证显示与隐藏，脚本自行决定是否使用隐藏参数。

### 全局设置

作者放置的每个实例用 `params` 调节。本机统一开关使用 schema 3 及以上加 API 2，并在 manifest 顶层声明 `globalSettings`。

```json
{
  "globalSettings": [
    { "name": "enabled", "type": "Bool", "default": true,
      "label": { "en": "Enable effect", "zh-CN": "启用效果" } }
  ]
}
```

当前只支持 `Bool`。名称不可重复，请避开 `__proto__`、`constructor` 和 `prototype`。Core 为已加载定义绘制 UMM 开关，按事件 ID 和设置名存入模组目录的 `event-settings.json`。值跟随本机，卸载事件包后仍保留。可选 `binding` 是宿主消费者识别的命名绑定，普通脚本直接按设置名访问即可。脚本每次 `update` 从 `ctx.settings.enabled` 读取当前的只读快照。持续效果关闭时主动清除已创建对象。重新开启时按当下的 `t` 恢复。一次性效果应明确重新开启时是否补触发。

## 4. 脚本生命周期和效果接口

入口在 Jint 的严格模式运行，定义全局函数 `create(ctx, params)` 并返回对象。只需要初始化工作时可以返回 `{}`。`update(t)` 和 `dispose()` 均可选。把可变状态放在 `create` 的闭包里。每个放置实例有独立引擎和参数快照。

| 时机 | 行为 |
|---|---|
| 歌曲时间到达或越过时钟原点 | Core 初始化脚本并调用 `create`。从中间开始播放时，首个 `t` 可能大于零 |
| 歌曲时间向前推进 | 启动后每帧调用 `update(t)`。暂停时停止调用 |
| 重开、检测到明显回退、退出关卡 | 宿主释放实例，正常释放时调用 `dispose()`。回退后根据时间重新初始化 |
| 脚本抛错或超限 | Core 记录该实例错误并回收它的宿主资源。故障时可能跳过 `dispose()` |

`create` 可用 `params`、`ctx.eventTime`、`ctx.floorSeq` 和 BPM 信息做初始化。使用首个 `update` 里的播放时间计算起始画面。`create` 时的 `ctx.time`、`ctx.t`、`ctx.beat` 与播放标志还是初始值。

| `ctx` 字段 | 值 |
|---|---|
| `time`、`eventTime`、`t` | 歌曲时间、实例时钟原点、两者的差，单位为秒。原点为所在格进入时间或歌曲零点 |
| `tileTime` | 实例所在格的歌曲时间，歌曲时钟的实例也可据此定位格位 |
| `bpm`、`secondsPerBeat` | 播放运行期使用歌曲基础 BPM 及 `60 / bpm` |
| `tileBpm`、`tileSecondsPerBeat` | 实例所在格的局部 BPM 及每拍秒数，包含该格速度倍率。适合把相对该格的拍数转换为秒 |
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
| `fx.video(key, props)` / `fx.videoDestroy(key)` | 解码视频、渲染视频表面并可输出音轨，见[视频](#视频) |
| `fx.screen(props)` / `fx.screenClear()` | 启用、更新或释放实例的全屏阶段，见[全屏屏幕效果](#全屏屏幕效果) |
| `fx.field(key, props)` / `fx.fieldDestroy(key)` | 声明、更新和释放区域，见[区域与计算管线](#区域与计算管线) |
| `fx.fieldMove(props)` | 按标签覆盖现有区域的变换和可见性 |
| `fx.tagValue({tag, key, value})` | 发布同包、同标签的字符串状态供宿主消费者读取 |

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

## 5. 归档、安装、存档与升级

```text
Example.event              ZIP，扩展名 .event
├── event.json             主 manifest，必须位于 ZIP 根目录
├── main.js                主入口，实际位置由 entry 指定
├── icon.png               可选
├── included/              可选子 manifest 及其脚本
└── assets/                包内资源及其许可
```

- 源文件使用 UTF-8，manifest 使用 JSON。包内路径相对 ZIP 根目录并用 `/`，主、子入口使用相同根目录。用唯一的路径和精确大小写。把包目录的**内容**打进 ZIP 根目录。
- 主、子 manifest 各限 2 MiB，入口 JS 各限 4 MiB，图标限 8 MiB。其他资源由对应消费者限制。图标使用可解码 PNG，加载失败时 Core 使用内置图标。
- 先运行 `python tools/pack_event.py --check <源码目录>`，再运行 `python tools/pack_event.py <源码目录> -o Example.event`。校验失败返回非零。打包递归收集普通文件，跳过输出自身与扩展名为 `.event` 的文件。只在源码目录保留要分发的素材、脚本与许可。
- 谱面目录顶层放成品 `.event` 文件。首次试用先保存谱面。新增或替换包后重新打开，Core 会刷新此谱面的定义与脚本缓存。
- 谱面根字段 `pevEvents` 记录依赖，例如 `[{"id":"example.beat-flash","version":"1.0.0","file":"Example.event"}]`。`file` 是文件名，用于在同 ID 候选之间选包。用过的包宜保持文件名。游戏导出会加入已声明的包和已加载事件通过 `File` 参数引用的谱面同级文件。原生 ZIP 只存文件名，引用了子目录文件时请使用保留目录的归档方式。通用 `String` 或脚本推导的路径需要由作者另外打包。检查最终 ZIP，确认所有包外资源随谱面分发。

### 载体和兼容性

两种载体都使用 `id`、`v`、`uid`、`p`。它们分别是事件 ID、实例写入时的包版本、实例唯一标识、作者输入的参数对象。复制事件时 Core 分配新 UID。测试工具生成数据时按对应前缀写入紧凑 JSON。

| 载体 | 位置 | 示例 |
|---|---|---|
| 普通事件 | `actions` 下 `EditorComment.comment` | `@PEV1 {"id":"example.beat-flash","v":"1.0.0","uid":"flash01","p":{"duration":0.5}}` |
| 装饰型事件 | `decorations` 下 `AddDecoration.maskingTarget`，`decorationImage` 存标记 | `pev1:{"id":"pev.video","v":"1.1.0","uid":"video01","p":{"file":"clip.mp4"}}` |

Core 启用时，缺包的普通事件显示只读占位，装饰保留为原生行。装回匹配的文件并重开即可恢复。未启用 Core 时可以用原版打开谱面。升级时沿用 ID、参数名、类型和选项值。新增参数用默认值补齐，已存参数和未知键保留。较低主版本无法执行由较高主版本写入的实例，其他版本兼容性由包作者在旧谱面上验收。可声明装饰字段迁移。

## 6. 装饰、多入口和高级渲染

### 装饰载体

顶层 `"decoration": true` 为事件使用原生装饰行。作者能用编辑器工具移动、调整大小、改标签。装饰载体提供这些编辑与存储能力。要使渲染效果跟随拖动、`MoveDecorations`、视差或深度，须用能读取原生装饰实时状态的宿主原语。通用 `fx.create` 生成的是屏幕图形。

| 字段 | 怎么用 |
|---|---|
| `decorationDefaults` | Schema 3 起可设新建装饰的 `color`（六位 RGB）与 `tag`（`""` 表示新装饰无标签） |
| `decorationMigration` | Schema 3 起支持从旧注释实例迁入原生装饰属性。每项为 `{ "source": "旧键", "target": "原生键", "conversion": "转换名" }`，最多 16 项，目标不可重复 |
| `hiddenDecorationProperties` | Schema 4 可列出最多 32 个原生属性名，只在该事件面板隐藏 |
| `decorationListName` | Schema 4 可指定装饰列表名称，长度至多 128。可搭配 `icon` 自定义列表图标 |
| `decorationMarker` | Schema 4 可指定本包装饰行的载体标记，形如 `pev/<小写路径>`，缺省为共享的 `pev/field`。同时安装多个装饰包时要各用各的标记，否则面板会把别人的参数显示在你的装饰上 |

迁移支持 `scale/tilesToScale`、`position/vector2`、`rotation/float`、`tag/string`、`color/string`、`depth/int`、`opacity/percent`。原生已有字段如 `scale`、`rotation`、`opacity`、`depth`、`tag` 由原生面板负责。额外控件进入共享的 AddDecoration 面板，使用有事件前缀的自定义参数名以减少不同装饰包之间的定义冲突。装饰面板采用原生页签布局。

### 一个包注册多个入口

主 `event.json` 使用 schema 4，加上 `"includedEvents": ["included/control.json"]`。在同一归档放入 `included/control.json` 和它声明的入口脚本。子 manifest 的 `entry`、`icon` 也相对包根，例如 `"entry": "included/control.js"`。支持 1–16 个子 manifest，每个入口的 ID 唯一。子定义可使用适合自己的 schema/API。子 manifest 无效或嵌套包含，以及入口脚本文件缺失，都会使整包注册失败。可按 [NoiseField 主入口](../src/Events/NoiseField.event/event.json)和[状态入口](../src/Events/NoiseField.event/included/state.json)对照目录与字段。

### 视频

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

`time` 写绝对值，暂停、重开、检查点跳转和编辑器拖动都由宿主对齐到该值，不需要脚本自己累加时间。`trimEnd` 为 0 表示到片尾；返回的 `{ ok, error, ready, duration, width, height }` 里 `duration` 只有解码器准备好以后才非零，可用于把循环区间裁到片长。宿主自动换算 `fit`（`Fit` 完整显示、`Fill` 填满裁切、`Stretch` 拉伸）与装饰框的宽高比。`visible` 为 `false` 时宿主撤下画面但保留装饰行；`Video.event` 用它在「不保持末帧」时让视频在片尾消失。

格式：原生后端直接解码格式取决于当前 Unity 与系统解码器。Windows 实测 MP4/MOV/M4V、AVI、WMV 和 VP8-in-WebM 有可直接解码的组合。MKV、传输流、Theora、ProRes 和 VP9-in-WebM 等组合可通过本机 FFmpeg 兼容，先尝试重封装，必要时重编码。转换完成后缓存于 `Mods/PEV.Core/video-cache/`。FFmpeg 放在模组目录或本机 PATH 中。源视频须在谱面目录内并使用相对路径。通过 `File` 参数选择同级视频可随游戏导出，其他路径按第 5 节分发。详见[视频实测记录](VIDEO-VALIDATION.md)。

| 属性 | 合约 |
|---|---|
| `file` | 谱面内相对文件路径，省略时沿用已打开文件，空字符串关闭源。路径变化时重新准备 |
| `time`、`trimStart`、`trimEnd` | 绝对媒体秒数，默认 0。终点为 0 或不晚于起点时使用媒体末尾 |
| `playing`、`loop`、`speed` | 是否尝试随歌曲时钟推进、是否循环裁剪区间、解码播放速率，默认 `true`、`false`、`1` |
| `visible`、`fit` | 画面开关（默认 `true`）及缩放模式（初始为 `Stretch`）。`Fit` 保持比例放入框内，`Fill` 在原框内居中裁切 |
| `volume`、`muted` | 0–1 的音量和静音开关，默认 `1` 和 `false`。视频透明通道受解码后端限制 |
| `compat` | 默认允许 FFmpeg 转换。传 `false` 可要求原生解码 |
| 无装饰绑定时 | `x`、`y`、`width`、`height` 为世界单位，`rotation` 为度，`alpha` 为 0–1，可设 `color`（RGB）及 `sortingOrder`。宽高须为正数才绘制 |
| 返回值 | `ready` 表示解码器准备完成，`duration` 为秒，`width`、`height` 为像素，`converting` 表示兼容转换中。准备完成与首帧出画是两个时点。路径或解码失败时检查 `ok` 与 `error` |

空闲预览通过 schema 5 的 `videoPreview.parameterMap` 声明。键是已声明的参数名，值是原语参数，可用 `file`（必映射）、`time`、`trimStart`、`trimEnd`、`fit`、`compat`、`visible`，每个目标只映射一次。预览静音并停在 `time`、裁剪起点或首帧。比如 `{"videoPreview":{"parameterMap":{"clipFile":"file","scaling":"fit"}}}`。Schema 3–4 的视频装饰沿用旧 `file`、`trimStart`、`trimEnd`、`mode` 约定。

### 全屏屏幕效果

`fx.field` 和 `fx.video` 画的是世界里的对象；要对**整个画面**做后处理（调色、变形、雾化……），用屏幕效果。

它挂在游戏实际出画面的那台摄像机上：编辑器空闲时世界相机直接出屏，播放时同一台相机先渲染到内部 RT 再由叠加四边形显示，两种情况都拿到合成后的完整轨道、装饰、背景、粒子与视频。UI 在相机之后绘制，**不会被处理**——这是有意的边界。

```json
{
  "schemaVersion": 5,
  "shaderBundle": "assets/look.bundle",
  "shaderAssets": { "look": "MyScreenShader" },
  "screenEffect": { "shader": "look", "pass": 0 }
}
```

```js
fx.screen({
  enabled: true,
  weight: 1,              // 0..1，宿主用来做淡入淡出；材质里是 _PEVWeight
  mode: 'replace',        // 'replace' 覆盖旧调色层，'stack' 叠加
  order: 0,               // 同 order 时按创建顺序
  fade: 0.5,              // 秒；替换时旧层用这个时长淡出
  floats:   { "_Exposure": 0.4 },
  vectors:  { "_Tint": [1, 0.5, 0.2, 0] },
  colors:   { "_Wash": "ff8800" },
  vectorArrays: { "_Bands": [[0,0,0,1],[0,0,0,1]] },
  curves:   { "_CurveTex": { "master": [0,0,1,1], "red": [0,0,1,1] } },
  lut:      { "enabled": true, "path": "assets/luts/look.cube" }
});
fx.screenClear();
```

| 名称 | 说明 |
|---|---|
| 材质名 | 一律以 `_` 开头，只允许字母、数字、下划线，前导下划线之后最多 32 字符 |
| 保留名 | `_MainTex`、`_LutTex`、`_LutTex2D`、`_LutSize`、`_LutMode`、`_LutEnabled`、`_LutDomainMin`、`_LutDomainMax`、`_PEVLinearProject`、`_PEVWeight` 由宿主写入，包不能覆盖 |
| `curves` | 每个键是一个角色名，值里的四组控制点烘成 256×1 的 RGBA 查找表：R/G/B 是分通道曲线，A 是主曲线。控制点写成 `[x0,y0,x1,y1,…]`，0..1，最多 16 点，用单调三次插值（不过冲） |
| `lut` | `.cube` 文件，先找包内、再找谱面旁边；支持 `LUT_3D_SIZE`（2–64）与 `LUT_1D_SIZE`（2–4096）。着色器里用 `_LutTex`（3D）或 `_LutTex2D`（1D）采样，`_LutSize` 是格数，`_LutMode` 为 0/1 |
| `fade` / `weight` | `weight` 由脚本按自己的时钟算好；旧层被替换时由宿主用 `fade` 平滑淡出 |
| `mode` | `replace` 只显示最新的层，`stack` 让后加的层叠加在前面的结果上 |

`screenEffect.pass` 为 0–8，默认 0，必须在 shader 中存在。每实例持有一个阶段，最多同时登记 8 层。`enabled` 默认开启，`weight` 默认 1，`order` 限于 −1024 至 1024。向量为四分量，向量数组最多 32 项，曲线纹理最多四个角色。颜色写 RGB 或 RGBA 十六进制。设 `mode: "stack"` 保持并行层，`replace` 只替换较早的 replace 层。旧层淡出使用实时时钟，范围 0–60 秒。旧实例在淡出结束后保持被替换状态，直到清理或切换为 stack。

包决定每个 uniform 的语义。Core 负责校验、上传、曲线与 LUT 资源、排序和清理。

编辑器里的实时预览不需要另写：编辑器空闲时会用**同一个脚本、同一套参数、同一个材质**跑一遍，所以「调参数时看到的画面」和「播放时看到的画面」出自同一条代码路径。

`editorPanel` 声明一个浮动面板，用来放原生属性面板表达不了的输入：

```json
"editorPanel": {
  "title": { "en": "Color Grading", "zh-CN": "调色" },
  "curves": { "master": "curveMaster", "red": "curveRed" },
  "hueCurves": { "hue": "hueVsHue", "saturation": "hueVsSat", "luma": "hueVsLuma" },
  "wheels": [ { "label": { "en": "Lift" }, "xy": "liftXY", "luma": "liftL" } ],
  "lut": { "preset": "lutPreset", "file": "lutFile", "strength": "lutStrength" },
  "compare": "enabled",
  "composition": "mode",
  "hsl": { "parameter": "bands", "bands": ["Red", "Cyan"] },
  "curvePresets": [ { "label": "Contrast", "value": "0,0;0.5,0.4;1,1" } ],
  "scopes": true,
  "summary": { "en": "Applied before other stacked stages." }
}
```

面板写回普通声明参数，沿用谱面的存取与复制机制。各组选填，面板从已声明控件生成页签。

| 字段 | 绑定或说明 |
|---|---|
| `title`、`summary` | 本地化窗口标题和 Info 页签说明 |
| `compare`、`composition` | 绑定 `Bool` 用于开关与 A/B 对照，或绑定 `Choice` 提供组合选项。选项及文案读参数定义 |
| `curves`、`hueCurves` | 绑定 `String` / `Text` 参数。标准通道为 `master/red/green/blue`，色相通道为 `hue/saturation/luma` |
| `curvePresets` | 标准曲线的预设按钮，最多 8 个 `{label,value}`，值采用曲线文本格式 |
| `wheels` | 1–8 个可选本地化标签的色轮，每轮 `xy` 绑定 `Vector2` 或 `FloatPair`，`luma` 绑定 `Float` |
| `lut` | `preset` 绑定 `Choice`，`file` 绑定 `String` / `Text` / `File`，`strength` 绑定 `Float`（0–1）。可选本地化 `note` |
| `hsl` | `parameter` 指向 `String` / `Text`，`bands` 提供 1–16 个分区标签。参数格式为以分号分隔的 `色相角,饱和度,亮度`，范围分别为 −60–60、−1–1、−1–1 |
| `scopes` | 是否提供示波器页签，默认开启 |

曲线参数用 `"0,0;0.25,0.3;1,1"` 这样的文本保存，可以直接读懂并手工编辑。面板还提供直方图、RGB Parade、波形与矢量示波器；它们读取全屏阶段的输入（192×108 副本，每秒读回四次）。离开 Scopes 页签或进入播放时停止采样。

可运行的参考实现是 [ColorGrading.event](../src/Events/ColorGrading.event/)，着色器源码在 [src/UnityShaders/ColorGrading](../src/UnityShaders/ColorGrading/ColorGrading.shader)。

### 区域与计算管线

[NoiseField 源码](../src/Events/NoiseField.event/)展示了可运行的装饰和 GPU 包。以下是该版本 Core 的内部接口。基于它们开发时固定 Core 构建和事件包版本，并对目标 Unity、图形后端和设备实测。

#### 区域与标签

| 调用或声明 | 用法 |
|---|---|
| `fx.field(key, props)` | 设置该键的区域和当前帧输入。装饰型使用原生变换、色彩、透明度、标签、可见性及 depth。独立字段可用 `relativeTo`（`Tile`、`Camera`、`PlanetRed`、`PlanetBlue`、`PlanetGreen`、`CameraRatio`）、九点 `anchor`、`size`、`radius`、`position`、`rotation`、`lockSize`、`lockRotation`、`parallax`、`color`、`opacity`、`tag`、`depth`、`visible`。位置和大小用格单位，`CameraRatio` 用视口比例，角度用度，透明度 0–1 |
| `fx.fieldDestroy(key)` | 释放该实例的区域及其画面资源 |
| `fieldPreview` | `{"key":"region","parameterMap":{"authorSpeed":"evolveSpeed"}}`。预览键与播放脚本使用同一个键，映射支持最多 64 项，目标名唯一。空对象可直接使用同名字段 |
| `fx.fieldMove({tag,offset,rotation,scale,alpha,visibility,clear})` | 为匹配标签的区域设置绝对覆盖量。`offset`、`rotation`、`scale`、`alpha` 默认为 `[0,0]`、0、1、1。`visibility` 可用 0（当前可见性）、1（显示）、2（隐藏）。用 `{tag,clear:true}` 还原 |
| `fx.tagValue({tag,key,value})` | 同包入口可用它发布字符串值。字段宿主按包、标签、键以及事件时序读取。普通脚本当前只有写接口 |

常用渲染输入还包括帧索引 `frame`、纹理偏移 `uv` 及合成分量 `tiling`，其余参数由包的渲染计划消费。区域可对接场景采样、深度分组、星球位置与接触输入、GPU 计算和 CPU 像素回退。星球相关输入由对应管线提供。

#### 包内资源和角色

| 声明 | 内容与消费方 |
|---|---|
| `shaderBundle`、`shaderAssets` | Bundle 文件及角色到 bundle 资产名的映射（1–32）。使用目标游戏兼容的 Unity 编辑器与平台构建 |
| `imageAssets` | 1–16 个图片角色。每项为 `{path,wrap,filter,flipY,linear}`，`wrap` 为 `clamp` / `repeat` / `mirror`，`filter` 为 `point` / `linear`，后两项为布尔值。单图文件限 8 MiB |
| `vectorPrograms` | 1–16 个包内 JS 程序。按绑定角色用受限 worker 执行纯数据变换，单文件限 256 KiB |
| `computeGraphs` | 1–16 个包内 JSON 计算图。字段宿主校验外部输入、纹理、记录缓冲、计算 kernel、dispatch 与输出，单图限 128 KiB |
| `pixelResources` | 1–16 个包内图片角色。解码为供 CPU 像素任务读取的资源，单文件限 8 MiB |
| `keySpecs` | 1–16 个包内 JSON 缓存键定义。声明字段和量化方式，宿主据此复用已烘焙帧，单文件限 32 KiB |
| `runtimeBindings` | 1–16 个角色映射。值必须是已在 `vectorPrograms`、`computeGraphs` 或 `keySpecs` 声明的名称 |

字段管线识别的角色为 `renderGraph`（GPU 图）、`renderPlan`（实例渲染计划）、`renderGroup`（分组与绘制命令）、`inputController`（输入与接触控制）、`vectorProgram`（向量纹理）、`framePlan` / `frameProgram` / `frameKey` / `frameRequest`（CPU 回退、帧生成、缓存键和帧请求）。声明角色后需提供匹配该阶段的输入输出。完整例子见 [NoiseField manifest](../src/Events/NoiseField.event/event.json)、[计算图与缓存键](../src/Events/NoiseField.event/pipelines/) 和 [worker](../src/Events/NoiseField.event/workers/)。这些高级角色绑定当前 Core build，改动时用目标 Unity、图形后端及设备回归。

打包器保留 `fieldShaders`、`fieldTextures`、`fieldUniforms`、`fieldWaves`、`legacyFieldTextures` 和 `legacyFieldProfile` 的历史静态校验。现行 Core 字段管线使用命名资源和运行角色。迁移旧包时按当前参考包重建渲染声明，同时保留谱面 ID 和参数兼容性。

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
