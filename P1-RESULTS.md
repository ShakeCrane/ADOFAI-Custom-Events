# P1 验证结果 — 执行闭环

> 环境同前：ADOFAI `r150`（2026/11/03），Unity `6000.3.25f1`，Mono，UMM `0.32.5.0`。
> 工程：`src/PEV.Core/`；依赖 Jint 4.17.0（net462）+ Acornima 1.7.0。
> 原始输出：`Mods/PEV.Core/p1-results.txt`、`Mods/PEV.Core/pev-core.log`。
> 测试谱面：`Documents/A Dance of Fire and Ice/Levels/P1Test.adofai`（4 个实例，2 种事件）。

---

## 结果总览

| 检查 | 结果 |
|---|---|
| Jint 在 Mono 上加载并执行 | **PASS** |
| 每个实例独立引擎 | **PASS**：4 个实例互不干扰 |
| `create(ctx, params)` / `update(t)` / `dispose()` | **PASS**：update 累计调用 578 次 |
| 沙箱：死循环 | **PASS**：语句预算拦截，耗时 3 ms |
| 沙箱：递归 | **PASS**：递归深度上限拦截 |
| 沙箱：CLR 访问 | **PASS**：`System is not defined` |
| 沙箱：`eval` | **PASS**：字符串编译被禁用 |
| 沙箱：跨脚本污染 | **PASS**：独立引擎，污染不外溢 |
| 暂停冻结 | **PASS**：2 秒窗口内 0 次调用 |
| 重开/回退重置 | **PASS**：两次真实重绕，实例正确重置 |
| 单次 update 开销 | **0.028 ms**（最差） |

---

## 关键实测数据

### 单帧开销

```
UPDATES  pev.camera-warp#aa11   calls=335, lastMs=0.028, faulted=False
UPDATES  pev.camera-warp#bb22   calls=193, lastMs=0.008, faulted=False
UPDATES  pev.screen-shake#cc33  calls=50,  lastMs=0.005, faulted=False
UPDATE-TOTAL  578
UPDATE-COST   0.028 ms
```

最差单次 `update()` 0.028 ms，四个实例合计每帧不到 0.1 ms，相对 16.7 ms 的帧预算可以忽略。即使按 50 个活跃实例估算也在 0.5 ms 量级。

### 沙箱

```
SANDBOX-loop       PASS (contained)   3 ms, statement budget exceeded
SANDBOX-recursion  PASS (contained)   recursion limit hit
SANDBOX-clr        PASS (blocked)     JS error: System is not defined
SANDBOX-eval       PASS (blocked)     String compilation has been disabled
SANDBOX-isolation  PASS (separate engines)
```

五个都是"脚本抛异常、宿主存活、其它实例不受影响"的形态。

### 暂停

```
PAUSE  PASS  calls during 2s paused window: 0
```

### 重开

```
rewind detected (songTime 4.604 -> 4.177); reset 2 instance(s)
rewind detected (songTime 6.166 -> -0.749); reset 4 instance(s)
```

两条都是真实事件：前者是掉格回退，后者是整关重开。

---

## 实现过程中修掉的四个真实缺陷

### 1. 8 ms 超时误杀首次调用

`TimeoutInterval(8ms)` 在第一帧就把实例打成 Faulted：

```
instance faulted: [pev.camera-warp#aa11] time budget exceeded
```

原因是新引擎的首次 `Invoke` 要付解释器预热成本，与脚本本身复杂度无关。这会把"脚本写得对但第一次跑得慢"误判成"脚本超预算"。

**修正**：超时放宽到 50 ms，把主防线换成 `MaxStatements`——它是确定性的，且不惩罚首次调用。超时退居兜底。

**教训**：**逐帧执行器的预算必须区分"预热"与"病态"**。语句数上限是确定性约束，墙钟是软约束，两者定位不同，不能混用。

### 2. `scrController.paused` 不冻结时钟

暂停测试第一次失败：暂停 2 秒内仍产生 3 次调用。追下去发现 `paused` 只做三件事——切输入映射、更新 UI 可见性、改 `_paused` 标志。而 `songposition_minusi` 由 `AudioSettings.dspTime` 推导：

```csharp
songposition_minusi = (float)(dspTime - dspTimeSong - calibration_i) * song.pitch - addoffset;
```

真正停住这条时钟的是 `AudioListener.pause`（`scrController.audioPaused`）。

**修正**：运行时用"时钟是否推进"判断播放状态，不碰 `paused`。

**教训**：**"暂停"在游戏里不是一个属性，而是若干独立的行为**。用时钟增量判定播放状态，自动绕开了这个坑，也顺带让重开、检查点跳转无需特殊处理。

### 3. 重绕阈值过小导致每帧误判

最初用 1 ms 作为回退阈值，结果时钟的音频抖动被当成重绕。加日志后暴露得很清楚：

```
rewind detected (songTime 4.627 -> 4.209)   ← 真实重绕
rewind detected (songTime 4.627 -> 4.219)   ← 同一次重绕的后续帧
rewind detected (songTime 4.627 -> 4.230)
...
```

1188 条日志，实例被反复重置，`update()` 一次都没跑成。

**修正**：阈值提到 50 ms。

### 4. 重绕后未重置基线

修了阈值还不够。重绕发生后 `_lastSongTime` 仍停在旧峰值（4.627），而实际时钟在 4.2 附近推进，于是**之后的每一帧都被判定为又一次重绕**，形成死循环。

**修正**：重绕时把基线重置为新位置。

**教训**：**单调基线 + 阈值判定** 这个组合里，基线在触发后必须重新锚定，否则一次真实事件会永久毒化判定。

---

## 架构确认

1. **每实例一个引擎是正确的**。沙箱隔离测试直接证明：一个脚本改写 `Array.prototype.push`，另一个引擎完全不受影响。
2. **不传 CLR 对象是可行的**。脚本侧只见 JSON 解析出的普通值，宿主侧只暴露一个 `Func<string,string,string>`。`Interop.Enabled = false` 因此名副其实。
3. **时钟驱动比状态枚举可靠**。`States.PlayerControl` 之类的枚举与"时间在不在走"并非等价，用时钟增量判定更省事也更准确。
4. **`MaxStatements` 是主防线**。它是确定性的，不受预热、GC、机器性能影响。

---

## 仍未覆盖

| 项 | 说明 |
|---|---|
| 效果是否视觉正确 | 只验证了 `fx.flash` / `fx.shake` 被调用且不抛异常，未做视觉确认 |
| 持续型效果 | 两个示例事件都是瞬时触发，没有逐帧演算的案例 |
| 资源释放 | `dispose()` 被调用，但没有验证资源确实回收 |
| 多 `.event` 包并发 | 只用了 2 个包、4 个实例 |
| 脚本加载失败的用户提示 | 目前只写日志，编辑器内无提示 |
| 编辑器内改参数后执行 | 未测；属于 P2 |

---

## 工程结构增量

```
src/PEV.Core/Scripting/
├── ScriptHost.cs       单实例引擎、沙箱配置、JSON 边界
├── EventContext.cs     脚本可见状态、GameBridge 白名单
├── EventInstance.cs    实例生命周期与时钟
├── RuntimeHost.cs      全关卡调度器
└── NativeEffects.cs    flash / shake 的参考实现
```

Jint 相关 DLL 随模组分发：`Jint.dll` 2.6 MB + `Acornima.dll` 0.37 MB。

---

## 下一步

P2 编辑器闭环：快捷栏点击放置、参数面板同步、复制/删除/移动，以及在编辑器改参数后保存再重开的完整链路。
