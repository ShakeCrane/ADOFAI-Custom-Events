# P2 验证结果 — 编辑器闭环

> 环境同前：ADOFAI `r150`（2026/11/03），Unity `6000.3.25f1`，Mono，UMM `0.32.5.0`。
> 工程：`src/PEV.Core/Editor/EditorIntegration.cs`。
> 原始输出：`Mods/PEV.Core/p2-results.txt`、`Mods/PEV.Core/pev-core.log`。
> 测试谱面：`Documents/A Dance of Fire and Ice/Levels/P2Test.adofai`（初始 1 个已存在实例，9 格空位）。

---

## 结果总览

| 检查 | 结果 |
|---|---|
| 从快捷栏放置新实例 | **PASS**：floor 3，默认值正确写入 |
| 编辑参数 | **PASS**：strength 1.25 / duration 6 生效 |
| 复制到其它格 | **PASS**：floor 5，参数随复制保留 |
| 复制后 uid 不同 | **PASS**：`68db6b19` vs `ee70c561` |
| 删除实例 | **PASS**：解除绑定且从事件列表移除 |
| 编码后无未知类型 | **PASS**：`actions=2, pevComments=2, unknownTypes=0` |
| 保存文件后重新解码 | **PASS**：`Successful`，2 个实例完整恢复 |
| 无残留 payload 注释 | **PASS**：0 条未被消费 |

实测日志：

```
placed  'pev.camera-warp' uid=68db6b19 at floor 3
copied  'pev.camera-warp' -> uid=ee70c561 at floor 5
removed 'pev.camera-warp' uid=68db6b19
encode: written=2, removedStale=2
```

---

## 三个接入点

编辑器的放置、复制、删除都不会经过我们的存储层，需要在三处接上：

| 编辑器动作 | 接入点 | 动作 |
|---|---|---|
| 放置 | `scnEditor.AddEvent(int, LevelEventType)` | 新建数据、生成新 uid、绑定、写入默认值 |
| 复制 | `scnEditor.CopyEvent(LevelEvent, int)` | 深拷贝数据、**换新 uid**、绑定到副本 |
| 删除 | `scnEditor.RemoveEvent(LevelEvent, bool)` | 解除绑定 |

关键点是 **`CopyShallow` 复制的是 `LevelEvent`，而我们的数据表以对象引用为键**。副本天然没有绑定，必须显式接上；而且必须换 uid——两个实例共用一个 uid 会在保存时写同一条注释，后写的覆盖先写的，属于静默数据丢失。

删除时之所以只需"解除绑定"，是因为 `Bake()` 只遍历仍然绑定的实例。这比在删除时直接改文件安全得多：万一编辑器撤销删除，事件对象还在，只是没绑定，重新绑上即可。

---

## 修掉的一个真实缺陷

### 放置时默认值类型未转换 → 参数读出来是 0

第一次运行的输出：

```
PLACE  PASS  floor 3: pev->camera-warp uid=b4e69199 strength=0.8 duration=0
```

`duration` 应该是 2。原因是 manifest 里写的是 `"default": 2`，JSON 解析成 `int`，原样塞进事件后，`LevelEvent.Get<float>` 走 `TryGenericCast` 做 `(float)(object)int` 转型失败，返回默认值 0。

这不是只有探针才会踩到：属性面板读的也是同一个值，所以**作者放一个事件，看到的 duration 会是 0 而不是 2**。

**修正**：放置时走已有的 `ParamCoercion.Coerce`，按 `PropertyType` 转成正确类型。

**教训**：`ParamCoercion` 原来只用在"从文件恢复"这条路径上，放置是第二条入口，同样需要它。**凡是把值写进 `LevelEvent` 的地方都要经过类型转换**，这条应当作为实现纪律固定下来。

值得记一笔的是：P0 的往返测试没能发现它，因为那条路径上的值本来就来自文件、已经是正确类型。只有"新建一个实例"才会暴露。

---

## 已确认的端到端链路

```
快捷栏点击
  → AddEvent → 绑定数据 + 默认值
  → 属性面板改值
  → CopyEvent → 深拷贝 + 新 uid
  → RemoveEvent → 解绑
  → EncodeToDictionary → Bake 写回注释
  → 落盘
  → Decode → Restore 恢复实例
```

其中"重新解码"是在内存中构造新的 `LevelData` 并调用 `Decode`，走的是与真实开档相同的代码路径，但没有重载场景（否则会销毁跑测的协程）。真实开档在 P0 已验证过。

---

## 仍未覆盖

| 项 | 说明 |
|---|---|
| 真实鼠标点击快捷栏 | 走的是同一个 `AddEventAtSelected`，但未用真实输入验证 |
| 属性面板控件交互 | 直接写 `LevelEvent` 值，未点面板控件 |
| 撤销 / 重做 | 未测。`SaveStateScope` 会记录快照，但我们的数据表不在快照里 |
| 多选复制 / 拖动 | 未测 |
| Missing Event 占位 UI | 未做，仍只在日志里报告 |
| 脚本加载失败的用户提示 | 未做 |

**撤销/重做是其中最值得注意的一项**：编辑器的撤销依赖事件对象快照，而我们的数据绑定在对象引用上。撤销后对象被换掉，绑定关系可能丢失。需要在 P3 之前单独验证。

---

## 下一步

P3 交付：Missing Event 占位、图标与本地化、`.event` 打包工具、SDK 文档、真实关卡验收。另需补上撤销/重做与真实输入交互的验证。
