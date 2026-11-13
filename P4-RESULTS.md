# P4 验证结果 — 持续型效果

> 环境同前：ADOFAI `r150`（2026/11/03），Unity `6000.3.25f1`，Mono，UMM `0.32.5.0`。
> 原始输出：`Mods/PEV.Core/p4-results.txt`、`Mods/PEV.Core/pev-core.log`。
> 示例事件：`src/Events/Pulse.event`；测试谱面：`Levels/P4Test.adofai`。

---

## 结果

| 检查 | 结果 |
|---|---|
| 两个实例同时存活 | **PASS**：floor 2 与 floor 5 各自持有对象 |
| 实例互不干扰 | **PASS**：`scale 0.322 vs 0.351`，`x 7.24 vs 8.29` |
| 逐帧驱动 | **PASS**：单实例 `fx.set` 调用 300+ 次 |
| 脚本 `update()` 被调用 | **PASS**：344 次，无异常 |
| 生命周期结束后自清理 | **PASS**：`effects alive after lifetime=0` |

采样显示数值确实在逐帧变化：

```
SAMPLE  t0:  calls=154 scale=0.348 alpha=0.300 x=6.89
SAMPLE  t5:  calls=204 scale=0.325 alpha=0.257 x=5.75
SAMPLE  t10: calls=254 scale=0.226 alpha=0.072 x=6.37
```

---

## 这个测试暴露的三个真实缺陷

前三个阶段的验证都是"触发一次、看结果"，所以这些缺陷一直藏着。持续型效果要求每帧都正确，才把它们逼出来。

### 1. `update(t)` 从来没收到过 `t`

脚本里所有派生值都是 `NaN`：

```
fx.set args: {"color":"66ddff","x":null,"y":null,"scale":null,"alpha":null,"angle":null}
```

`null` 是 `NaN` 经 `JSON.stringify` 的结果。原因是直接对函数对象调用 `Invoke`：

```csharp
_engine.Invoke(_updateFn, _instance, t);   // Jint 按自己的重载解释这些参数
```

Jint 把后面几个参数当成自己的选项，`update` 实际收到的是 `undefined`。

**修正**：改走一个显式包装函数，参数列表不再有歧义：

```csharp
var call = _engine.Evaluate("(function(fn, self, t){ return fn.call(self, t); })");
_engine.Invoke(call, _updateFn, _instance, t);
```

**为什么之前没发现**：CameraWarp 和 ScreenShake 的 `update` 只在第一帧触发一次副作用，不依赖 `t` 的数值；`t < 0` 对 `undefined` 求值为 `false`，所以它们"看起来正常"。

### 2. 脚本拿到的是加载时的参数快照，不是作者刚改的值

```
init pev.pulse#...: params={"duration":3.0,...}   ← 作者改后的值
```

但编辑器里设的值没进脚本。原因是 `Begin()` 用 `Data.Effective`（解码/放置时的快照）而不是事件当前值。

**修正**：`ReadLiveParams()` 优先读 `LevelEvent` 的当前值，快照只作兜底。

**用户可见后果**：作者改完参数按播放，看到的是旧参数的效果，会以为"改了没用"。

### 3. 编辑器中放置的事件不会执行

运行时实例只在 `LevelData.Decode` 时创建，而放置发生在解码之后。作者放一个事件按播放，什么都不会发生。

**修正**：`scnEditor.Play` 前缀重建运行时。

这是三个里最严重的一个——它让整个编辑器工作流走不通。

### 附带：`entryTime` 的 NaN

`Rebuild` 在播放前取 `floors[i].entryTime`，此时路径还没重算，拿到 NaN，传播进每一帧。改为在实例启动时惰性解析，并拒绝 NaN。

---

## 对 API 的补充

脚本此前无法把"拍"换算成秒，只能写死 120 BPM。`ctx` 现在提供：

```js
ctx.bpm             // 关卡 BPM
ctx.secondsPerBeat  // 60 / bpm
```

`Pulse.event` 已作为可持续动画的参考实现随包分发。

---

## 仍未覆盖

| 项 | 说明 |
|---|---|
| 视觉效果本身 | 只验证了数值正确、对象存在，没有肉眼确认画面对不对 |
| 真实鼠标点击快捷栏 | 仍是程序化调用 |
| 属性面板控件交互 | 直接写 `LevelEvent` 值 |
| 真实关卡验收 | 全部验证都在合成谱面上 |
