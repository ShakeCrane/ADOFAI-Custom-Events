# Programmable Events — 事件开发指南

> 适用于 PEV.Core 0.1.0 · 游戏版本 r150（ADOFAI 3.4+）
> 读者：事件作者，以及代为编写事件的 AI Agent。

---

## 1. 一个事件是什么

一个 `.event` 文件是一个 ZIP 包，里面有一个 `event.json` 描述它，一个 `main.js` 实现它。

```
CameraWarp.event
├── event.json      ← 元数据与参数声明
├── main.js         ← 执行逻辑
├── icon.png        ← 编辑器里的图标（强烈建议提供）
└── assets/         ← 任意附带资源
```

把它放进 `Mods/PEV.Core/Events/` 即可被加载。目录形式与打包成 ZIP 的形式都能用。

没有安装对应 `.event` 的谱面仍能打开，事件会显示为 **Missing Event** 占位。占位事件：

- 在轨道上可见，因此不会让人误以为事件丢了
- 参数面板显示缺失的 `id`、写入版本与实例 uid，且不可编辑
- **不执行**，也不会被改写——保存时原始数据原样写回

装上对应的事件包后重新打开谱面，事件自动恢复正常，参数完整保留。

---

## 2. `event.json`

```json
{
  "schemaVersion": 1,
  "id": "rhytropy.camera-warp",
  "version": "1.0.0",
  "apiVersion": 1,
  "entry": "main.js",
  "icon": "icon.png",
  "category": "VisualFx",
  "name": { "en": "Camera Warp", "zh-CN": "镜头扭曲" },
  "description": { "en": "Warps the camera." },
  "params": [
    { "name": "strength", "type": "Float", "default": 0.8, "min": 0, "max": 2 },
    { "name": "duration", "type": "Float", "default": 2, "min": 0, "max": 16, "unit": "beats" },
    { "name": "ease", "type": "Enum:Ease", "default": "OutQuad" },
    { "name": "color", "type": "Color", "default": "ffffff" },
    { "name": "target", "type": "Tile", "default": [0, "ThisTile"] }
  ]
}
```

### 字段

| 字段 | 必填 | 说明 |
|---|---|---|
| `schemaVersion` | 是 | manifest 格式版本，当前为 `1` |
| `id` | 是 | **身份**，与文件名无关。建议 `作者.事件名` |
| `version` | 是 | 事件定义版本，语义化版本 |
| `apiVersion` | 否 | 脚本 API 版本，当前为 `1` |
| `entry` | 是 | 脚本入口，通常是 `main.js` |
| `icon` | 否 | 图标路径。**缺失时编辑器会借用一个内置图标** |
| `category` | 否 | 见下文，默认 `VisualFx` |
| `name` / `description` | 否 | 字符串，或 `{ "en": "...", "zh-CN": "..." }` |
| `params` | 否 | 参数声明数组 |

### `category` 的合法值

`Gameplay` `TrackFx` `DecorationFx` `VisualFx` `FxModifiers` `Conveniences` `Jank` `Favorites`

只能取这些。写错会回退到 `VisualFx`。

---

## 3. 参数类型

`type` 必须是下列字面量之一。**写错不会报错，但控件不会出现**，作者只会看到空白。

| type | 编辑器控件 | 脚本里拿到的值 |
|---|---|---|
| `Float` | 数值输入框 | `number` |
| `Int` | 整数输入框 | `number` |
| `Bool` | 开关 | `boolean` |
| `String` | 单行文本 | `string` |
| `Text` | 多行文本 | `string` |
| `Color` | 取色器 | `string`，如 `"ffffff"` |
| `File` | 文件选择 | `string` |
| `Vector2` | 两个数 | `[x, y]` |
| `FloatPair` | 两个数 | `[a, b]` |
| `Tile` | 格选择 | `[floor, relativeTo]` |
| `Enum:<类型名>` | 下拉框 | `string`（成员名） |

### 枚举的两种写法

```json
{ "name": "ease", "type": "Enum:Ease", "default": "OutQuad" }
{ "name": "myMode", "type": "Enum:MyNamespace.MyMode, MyAssembly", "default": "A" }
```

- 游戏内置枚举（`Ease`、`TileRelativeTo`、`SpeedType`）直接写名字即可。
- **自定义枚举必须写全限定名加程序集名**。只写 `Enum:MyMode` 会解析失败，控件退化成空。
- 如果拿不准，**降级成 `String`**，在脚本里自己比较字符串。这是最稳的做法。

### 其他参数键

| 键 | 说明 |
|---|---|
| `default` | 默认值，必须与 `type` 匹配 |
| `min` / `max` | 数值范围 |
| `unit` | 显示单位，如 `"beats"`、`"x"` |
| `label` | 显示名，字符串或 `{ "en": ..., "zh-CN": ... }` |
| `group` | 属性面板分组（页签）。不写则整个事件是一列平铺的参数 |
| `order` | 同组内的排序位置，不写则按声明顺序 |
| `showIf` | 条件显示：`["shape", "Rect"]`，当另一属性等于该值时这一行才出现 |
| `hideIf` | 条件隐藏：同上，等于该值时这一行消失 |

`group` 是长参数事件的关键。原生事件都按页签分组；一个参数列表直接平铺出二十多个控件时，作者找不到自己想要的参数。

```json
{ "name": "size",   "type": "Vector2", "default": [4, 3], "group": "Geometry", "showIf": ["shape", "Rect"] },
{ "name": "radius", "type": "Float",   "default": 2, "group": "Geometry", "showIf": ["shape", "Circle"] }
```

`showIf` / `hideIf` 的值必须与目标的**存储形式**一致：枚举写成员名，`Bool` 写 `"true"` / `"false"`，数值写数字的字符串形式。

`default` 的类型很重要：`Float` 参数写 `2` 和写 `2.0` 都能用，但 `Tile` 必须写成 `[0, "ThisTile"]` 这样的数组。

---

## 4. 脚本接口

`main.js` 必须定义一个 `create` 函数。

```js
function create(ctx, params) {
  return {
    update: function (t) { /* 每帧 */ },
    dispose: function () { /* 清理 */ }
  };
}
```

### 生命周期

| 时机 | 调用 |
|---|---|
| 播放首次越过该事件所在格 | `create(ctx, params)` 一次 |
| 每帧（播放中） | `update(t)` |
| 暂停 | 不调用 |
| 时间回退（重开 / 检查点） | `dispose()`，之后重新 `create` |
| 退出关卡 | `dispose()` |

### `ctx` 里有什么

```js
ctx.time            // 当前歌曲时间（秒）
ctx.eventTime       // 本事件所在格的进入时间
ctx.t               // time - eventTime
ctx.beat            // 按 bpm 换算的拍数
ctx.bpm             // 关卡 BPM
ctx.secondsPerBeat  // 60 / bpm，把"拍"参数换算成秒时用它
ctx.floorSeq        // 所在格的序号
ctx.isPlaying
ctx.isPaused
```

`update(t)` 的参数 `t` 与 `ctx.t` 相同，单位是秒。

参数如果写的是"拍"，换算成秒不要写死 120 BPM：

```js
var seconds = params.duration * ctx.secondsPerBeat;
```

**`t` 是相对时间，不是累计帧时间。** 暂停会冻结它，重开会归零。不要自己累加 `deltaTime`，否则暂停和重开时行为会错。

---

## 5. 可用的效果

### 一次性效果

```js
fx.flash({ color: "ffffff", duration: 0.3, alpha: 0.6 });
fx.shake({ strength: 0.2, duration: 0.4 });
```

| 方法 | 参数 |
|---|---|
| `fx.flash` | `color`（十六进制，可带或不带 `#`）、`duration`（秒）、`alpha` |
| `fx.shake` | `strength`、`duration`（秒） |

### 持续型对象

要做出逐帧变化的图形，用这三个：

```js
fx.create(key, { color: "66ddff", scale: 0.3, alpha: 0 });   // 建一次
fx.set(key,    { x: 0.2, scale: 0.5, alpha: 0.8, angle: 45 }); // 每帧更新
fx.destroy(key);                                               // 结束时清理
```

| 方法 | 参数 |
|---|---|
| `fx.create` | `key`（你自己起的名字）、`color`、`scale`、`alpha`、`sortingOrder` |
| `fx.set` | `key`，以及 `x` `y` `scale` `alpha` `angle` `color` 中任意几个 |
| `fx.destroy` | `key` |

坐标说明：`x` / `y` 是相对画面中心的归一化坐标，`1.0` 约等于半屏。这样换分辨率或缩放时位置不会跑偏。

**`key` 必须每个实例不同。** 最常见的是拼上 `ctx.floorSeq`：

```js
var key = "pulse:" + ctx.floorSeq;
```

同一个事件放两次而 `key` 相同，两个实例会抢同一个对象，后写的覆盖先写的。

### 完整示例：持续动画

`Pulse.event` 是随模组分发的参考实现，核心部分：

```js
function create(ctx, params) {
  var key = "pulse:" + ctx.floorSeq;
  var started = false;
  var duration = params.duration > 0 ? params.duration : 1;

  return {
    update: function (t) {
      if (t < 0) return;

      // 过了生命周期就收掉，否则对象会一直留在画面上
      if (t > duration) {
        if (started) { started = false; fx.destroy(key); }
        return;
      }

      if (!started) {
        started = true;
        fx.create(key, { color: params.color, scale: params.size, alpha: 0 });
      }

      var p = t / duration;                          // 0..1
      var wave = Math.sin(p * params.cycles * Math.PI * 2);

      fx.set(key, {
        scale: params.size * (1 + wave * 0.25),
        alpha: 0.35 * (0.6 + 0.4 * wave),
        x: Math.sin(p * Math.PI * 2) * params.drift,
        angle: p * 360
      });
    },

    dispose: function () {
      if (started) fx.destroy(key);
      started = false;
    }
  };
}
```

三个要点：

1. **用 `started` 标志保证 `create` 只跑一次**，`set` 每帧跑。
2. **自己判断生命周期**。`update(t)` 不会因为过了某个时长就停止调用，脚本要自己决定何时收尾。
3. **`dispose` 里也要 `destroy`**。重开、检查点跳转、退出关卡都会触发 `dispose`；宿主也会兜底回收，但显式清理更干净。

所有方法都返回 `{ ok: boolean, error: string }`。**不在白名单里的效果会返回 `ok: false` 而不是静默失败**，调试时值得检查。

---

### 按 tag 控制别的实例

`fx.field` 创建的对象可以带 `tag`，另一个事件用 `fx.fieldMove` / `fx.fieldState` 按 tag 改它。
这在「先摆好区域，再按节奏改它」的分层编排里是必需的——移动事件不该知道区域是怎么创建的。

```js
// 在创建方：
fx.field("key", { tag: "wave", /* ... */ });

// 在控制方：写的是绝对值，脚本每帧按 t 重新算，暂停和跳转自动正确
fx.fieldMove({ tag: "wave", offset: [2 * k, 0], rotation: 15 * k, scale: 1 + 0.5 * k, alpha: 1 });
fx.fieldState({ tag: "wave", alpha: 0.5, visibility: 1 });

// 事件结束时还原，否则区域会停在最后一个位置
fx.fieldMove({ tag: "wave", clear: true });
```

`visibility`：`0` 不改、`1` 强制可见、`2` 强制隐藏。tag 可以是空格分隔的多个，命中任意一个即可。

> 这些是宿主原语，与 `fx.create/set/destroy` 一样**不在对外 SDK 的兼容承诺内**；
> 对外承诺的是 `.event` 包格式（zip + metadata + 脚本约定）。

---

## 6. 沙箱边界

脚本运行在隔离引擎里，以下都不可用：

- `System` / 任意 .NET 类型
- `eval`、`new Function`
- 文件、网络、线程
- Promise / async（脚本全部在主线程同步执行）

超出语句预算（约 20 万条）或时间预算的脚本会被中断，该实例标记为失效，**其它事件不受影响**。

每个实例有独立的引擎，所以：

- 你改 `Array.prototype` 不会影响别人，别人也影响不到你
- 但你也不能靠全局变量在实例之间共享状态——同一个事件放两次，`create` 会跑两次，各自持有自己的闭包

---

## 7. 常见写法

### 一次性的效果

```js
function create(ctx, params) {
  var fired = false;
  return {
    update: function (t) {
      if (t < 0 || fired) return;
      fired = true;
      fx.flash({ color: params.color, duration: 0.2 });
    },
    dispose: function () { fired = false; }
  };
}
```

### 持续一段时间的效果

```js
function create(ctx, params) {
  var started = false;
  var seconds = params.duration * ctx.secondsPerBeat;   // 拍 -> 秒，跟随关卡 BPM

  return {
    update: function (t) {
      if (t < 0 || started || t > seconds) return;
      started = true;
      fx.shake({ strength: params.strength, duration: seconds });
    },
    dispose: function () { started = false; }
  };
}
```

### 用参数控制分支

```js
function create(ctx, params) {
  return {
    update: function (t) {
      if (t < 0) return;
      if (params.variant === "Red") fx.flash({ color: "ff0000", duration: 0.2 });
      else                        fx.flash({ color: "00ff00", duration: 0.2 });
    }
  };
}
```

---

## 8. 打包与测试

```bash
python tools/pack_event.py MyEvent.event        # 校验并打包
python tools/pack_event.py --check MyEvent.event # 只校验
```

校验会检查必填字段、脚本是否存在、参数类型是否合法、分类是否有效。

测试流程：

1. 把目录（或打包好的 `.event`）放进 `Mods/PEV.Core/Events/`
2. 启动游戏，打开编辑器
3. 事件应出现在快捷栏对应分类下
4. 放置、改参数、保存
5. 播放，观察效果

---

## 9. 排障

| 现象 | 原因 |
|---|---|
| 快捷栏里没有这个事件 | `id` 为空、`event.json` 不是合法 JSON、`schemaVersion` 过高。看 `Mods/PEV.Core/pev-core.log` |
| 有按钮但点击没反应 | 少见；通常是 `category` 写错导致归类异常 |
| 参数面板是空的 | `params[].type` 写错。写错不报错，只是控件不出现 |
| 某个参数显示 0 而不是默认值 | `default` 类型与 `type` 不匹配 |
| 事件显示为"缺失" | `.event` 不在 `Events/` 下，或 `id` 与谱面里记录的不一致 |
| 播放时没有效果 | 脚本报错。检查日志里带 `uid` 的那行 |
| 改了 `.event` 但行为没变 | 脚本在加载时缓存。重启游戏 |
| 参数滑块拖了但画面没变化 | 最常见的原因是**脚本没把参数传下去**。宿主只读脚本调用里带的参数，缺一项就用宿主默认值，不报错。在 `create` 里把 `params.x` 存进对象、在每帧的调用里逐项写出来；两边漏一个都会静默失效 |
| 效果看起来像"参数全丢了" | 同上，且通常是**总开关类参数**（例如噪域的 `displace`）没传：一个参数为 0 会让整组参数都不显现 |

日志位置：`Mods/PEV.Core/pev-core.log`。

---

## 10. 给 AI Agent 的要点

如果由 Agent 生成事件，按这个顺序做：

1. 从 `tools/pack_event.py` 里的 `VALID_TYPES` / `VALID_CATEGORIES` 取合法值，不要臆造类型名。
2. `id` 用 `作者.事件名` 形式，小写、连字符分隔，**不要用中文或空格**。
3. `default` 的类型必须和 `type` 一致：`Float` → 数字，`Color` → 六位十六进制字符串，`Tile` → `[int, "ThisTile"]`。
4. 枚举优先写成 `Enum:<游戏内置类型>`；不确定就降级成 `String`，并在脚本里比较字符串。
5. `create` 必须返回对象；不需要的行为可以省略 `update` 或 `dispose`。
6. **不要假设 `t` 会单调递增**——重开时它会归零。
7. 不要在实例之间共享可变状态；同一个事件放两次是两份独立闭包。
8. 打包前跑 `--check`。

常见错误，按出现频率排序：

- 参数类型写成 `Number` / `Integer` / `Boolean`（应为 `Float` / `Int` / `Bool`）
- `default` 写成字符串 `"0.8"` 而不是数字 `0.8`
- 忘记提供 `icon`，导致编辑器借用别的图标
- 在 `update` 里无条件触发效果，导致每帧都触发
