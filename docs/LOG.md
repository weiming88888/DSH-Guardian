# 日志怎么读

## 先看这一段：这是什么日志、该怎么看

**这是什么。** DSH Guardian 运行时会往 `data\` 目录写文件，记录它每一轮做了什么判断。
这些文件**不是程序崩溃才产生的**——只要监视开着，它就一直在写。所以打开 `data\`
看到一堆 `.log` 和 `.json` 是正常的，不代表出了问题。

**日志里没有你的密码、密钥、聊天内容。** 它只记录：端口探测结果、DSH 进程的启动输出、
六个配置文件的大小与修改时间、以及回退动作。诊断报告也是同样范围。

**该按什么顺序看**（从最省事到最细）：

| 顺序 | 看哪里 | 能看到什么 |
|---|---|---|
| 1 | 界面「执行结果」区 | 你刚点的那个按钮的原始输出，**先看这里** |
| 2 | `data\watchdog.log` | 监视器每一轮的判断过程——崩了先看这个 |
| 3 | `data\console\console-<时间>.log` | DSH 自己的报错原文（**要看"为什么起不来"就看它**） |
| 4 | `data\events.jsonl` | 结构化事件流，适合检索和贴给别人 |
| 5 | 其余文件 | 按第七节对照表按需查 |

**怎么读 `watchdog.log` 一行：**

```
2026-10-03 04:34:15 [INFO] DSH healthy (127.0.0.1:19387 listening, 6 matching process(es))
└──────── 时间 ────────┘ └级别┘ └──────────────── 内容 ────────────────┘
```

级别只有四种，**只关心后两种就够**：

| 级别 | 含义 |
|---|---|
| `[INFO]` | 正常过程记录，不需要管 |
| `[WARN]` | 提示性：某项保护生效了，或某个可选步骤被跳过 |
| `[ERROR]` | 某个动作失败了，但程序还在跑 |
| `[ALERT]` | **需要人工处理**。只有崩溃判定与回退相关的事情会用这个级别 |

---

## 一、文件清单：每个文件是什么

| 文件 | 是什么 | 什么时候看 |
|---|---|---|
| `watchdog.log` | **监视器主日志**，一轮一行 | 崩了先看这个 |
| `events.jsonl` | 结构化事件流，一行一个 JSON | 想检索"什么时候发生过什么" |
| `exe-trace.log` | 程序自身启动子进程的轨迹 | 监视器启动失败、按钮没反应 |
| `child-output.log` | 子进程输出里没被别处收走的部分 | 细查子进程行为 |
| `console\console-<时间>.log` | **DSH 进程的 stdout+stderr**，每次启动一个 | 要看 DSH 自己的报错原文 |
| `state.json` | 监视器的判据状态（失败计数等） | 想知道"它现在认为失败了几次" |
| `runtime.pid` | 正在运行的监视器进程号 | 确认监视器是否活着 |
| `last-tick.json` | 最近一次探测的结果快照 | 界面状态行的数据来源 |
| `last-known-good.json` | **回退目标指针** | 想知道"崩了会退到哪" |
| `port.txt` | 上次探测到的端口缓存 | DSH 已死时靠它记住端口 |
| `mode.json` | 监视开关状态（auto / paused） | 确认监视是否被真正关掉 |
| `gui-window.json` | 记住的窗口大小与位置 | 窗口开到屏幕外了 |
| `snapshots\snap-*\` | 各个"好版本"快照 | 想看有哪些可退的版本 |
| `snapshots\pre-restore-*\` | **每次回退前的坏配置留底** | 回退后想反悔、或想对比差异 |
| `rescue-*.json` | 回退的详细记录 | 回退发生过之后 |
| `crash-evidence-*.json` | 判定崩溃时的现场快照 | 想知道"凭什么判定崩了" |
| `诊断报告\诊断报告-<时间>.txt` | 一键收集的完整诊断包 | 要提 issue、或交材料 |
| `gui-clicks.log` | 界面收到的每次点击、几何实测 | 界面"点了没反应" |
| `gui-crash.log` | 图形界面的崩溃堆栈 | 窗口闪退（**只在崩溃时产生**） |
| `console-crash.log` | 命令行版的崩溃堆栈 | 动词执行失败（**只在崩溃时产生**） |
| `probe.log` | 早期鼠标输入实验的残留 | 一般不用看，见第七节 |

> `gui-crash.log`、`console-crash.log`、`runtime.pid` **只在特定情况下才存在**。
> 没看到它们不是异常。

---

## 二、`watchdog.log` 逐行含义

### 端口探测

```
probe port 19387 (source: live-process)
```

每一轮开头都探测 DSH 在哪个端口。`source` 有四种，**这是排查端口问题的关键**：

| source | 含义 |
|---|---|
| `parameter` | 命令行 `-Port` 指定，优先级最高 |
| `live-process` | 从**正在运行**的 DSH 进程实际占用的端口读出来的（最准） |
| `cached` | 复用 `data\port.txt` 里的上次结果——DSH 已死时靠它 |
| `default` | 以上全失败，退回文档默认端口 `3080` |

### 健康与失败

```
DSH healthy (127.0.0.1:19387 listening, 6 matching process(es))
```
**判定通过**：端口在应答。括号里的进程数是**取证信息，不是判据**——
`Win32_Process.CommandLine` 读不到时会降级匹配，数字里可能混进监视器自己的 powershell，
那种情况下日志会标 `(degraded)`，这个数字就不可信。

```
DSH starting: 3s since launch, process count 8, boot window 30s
```
我们**自己启动了** DSH，它还在引导窗内。**这行不是失败**，是"暂时不下结论"。
括号后那行还会提示：真需要更久就调大 `-BootWindowSeconds`。

```
Still inside the boot window: no verdict yet.
```
等待期间每 30 秒重复一次，**避免看起来像卡住**。

```
[WARN] DSH unhealthy (failure #2): 127.0.0.1:19387 refused connection
```
端口拒绝连接，记为第 N 次失败。**偶尔一两次是正常的**，只有累积成崩溃循环才会回退。

```
tick: alive=True port=19387 pid=22448
```
一轮心跳。`alive` 才是真正的判据（`alive=False` 就是没在应答）。
这行按间隔写一次，避免日志被刷爆。

```
Config plane changed (a plugin was most likely installed or removed)
```
六个配置文件的指纹变了——**基本等于"刚装了或卸了插件"**。这是回退的前置条件之一。

### 监视器自身的生命周期

```
resident: started (pid 3199, parent 17456, interval 55s, max 240 min)
```
常驻监视器启动。`parent 17456` 是**启动它的那个窗口进程**；`max 240 min` 是寿命上限，
到点自动退出，防止忘记关而永久驻留。

```
resident: exiting (launcher closed)
resident: stopped after 1 round(s)
```
监视器退出，原因是**启动它的窗口关了**。这是设计行为：窗口就是开关。

### 崩溃判定与回退

```
Startup crash loop proven: 3 failed launch attempts and DSH still does not answer.
```
**崩溃循环被证实**，这是自动回退的唯一触发条件。紧接着会列出：崩溃证据在哪、
DSH 的 stderr 在哪、以及三条人工排查命令。**这行的级别是 `[ALERT]`**。

```
[ALERT] ROLLBACK: restoring snapshot snap-20261003-032323-known-good (reason: startup crash loop)
```
正在把配置还原到那个快照。`reason` 说明为什么退。

```
  restore: Current state will be saved to: …\data\snapshots\pre-restore-20261003-032418
```
回退前先把**当前（坏掉的）配置留底**。想反悔，就用「回退」把这份留底选回来。

```
  restore: restored package.json / pnpm-lock.yaml / cordis.patch.yml / cordis.yml / pnpm-workspace.yaml
```
逐个还原的配置文件。**这是"退了什么"的最直接证据。**

```
[ALERT]   rescue record: D:\…\data\rescue-20261003-032418.json
[ALERT]   relaunched: True
```
救援记录写在哪；`relaunched: True` 表示回退后已把 DSH 重新拉起来。

```
[WARN] Auto-rollback suppressed: already rolled back 2 time(s) in this episode (cap 2). Human review required.
```
**同一轮故障里已经退过 2 次，触发上限，不再退。** 这是防"回退→还崩→再回退"的死循环。
出现这条说明**回退并没有解决问题**，要继续查根因，而不是等它再退。

```
[ALERT] Auto-rollback requested but no known-good snapshot pointer exists.
[ALERT] Create one BEFORE installing plugins: dsh-snapshot.ps1 -Action Create -Label known-good
```
需要回退，但**没有基线可退**（`last-known-good.json` 不存在或被删光了）。
这不是 bug，是拒绝瞎猜一个版本退回去。**先打一次基线。**

```
[ALERT] ACTION NEEDED: disabled by -InstallTimeoutSeconds 0
[WARN]   install: disabled by -InstallTimeoutSeconds 0
```
回退本身做完了，但**本该紧接着重装依赖**（`pnpm install`）却被参数关掉了。
所以 `node_modules` 现在**可能和还原后的 lockfile 不一致**，需要手动跑一次 `pnpm install`。
把 `-InstallTimeoutSeconds` 设成大于 0 的秒数就不会再看到这条。

---

## 三、判定"崩溃"的三条规则

日志里没有一句"结论"，因为判定是逐步积累的。规则本身是：

1. **启动宽限期**（`-StartGraceSeconds`）内进程就死掉，才算一次"短命启动"；
2. 连续 **`-CrashLoopThreshold` 次**短命启动，**或**重启预算 `-BootRetryBudget` 被耗尽
   = 崩溃循环成立；
3. 崩溃循环 **且** 配置相对基线发生了变化 = 才允许自动回退。

**只看一次失败不会回退。** 日志里出现一两次启动失败是正常的，不必紧张。

判定节奏：完整走过 `-BootWindowSeconds`（默认 **90 秒**）才会把一次启动算作"短命"，
所以一个瞬间就死的 DSH，日志在这 90 秒里仍然显示 `DSH starting`，
回退要再等一轮才触发。期间每 30 秒写一行"暂未下结论"。

> **轮次间隔必须大于引导窗**（`-IntervalSeconds > -BootWindowSeconds`）。
> 否则每轮都被判为"仍在引导窗内"，启动次数累积不起来，**崩溃循环永远无法成立**，
> 自动回退也就永远不会触发。这是实测踩过的坑。

---

## 四、其余文件里的关键行

### `last-tick.json`（界面状态行的数据来源）

```json
{ "at": "2026-10-03T04:34:15…", "alive": true, "port": 19387, "pid": 14572,
  "healthy": true, "configMovedSinceRollback": true, "lastAutoRollbackAt": "…" }
```

| 字段 | 含义 |
|---|---|
| `at` / `alive` / `healthy` | 最近一次探测的时间与结论——界面「上次检查」就是它 |
| `port` / `pid` | 探测到的端口和 DSH 进程号 |
| `configMovedSinceRollback` | 配置自上次回退后有没有再变。**true 才允许再次回退** |
| `lastAutoRollbackAt` | 上次自动回退的时间（没退过就是 null） |

### `state.json`（判据状态）

```json
{ "firstSeen": "…", "failStreak": 3, "shortLivedStarts": 3, "lastStartWasHealthy": false,
  "launches": 4, "autoRollbacks": 1, "relaunchStopped": false, … }
```

| 字段 | 含义 |
|---|---|
| `failStreak` | 连续失败轮数。达到 `-BootRetryBudget` 即判定崩溃循环 |
| `shortLivedStarts` | 连续"启动后立刻死"的次数。达到 `-CrashLoopThreshold` 即判定崩溃循环 |
| `launches` | 我们一共启动过几次 DSH |
| `autoRollbacks` | 本轮故障已自动回退次数，受 `-MaxAutoRollbacks` 限制 |
| `relaunchStopped` | 回退后仍然起不来 → 停止再重启，并在日志里说一次 |

**这个文件被写坏不影响使用**：程序会重建它（"状态文件损坏"是质检项之一）。

### `rescue-*.json`（回退记录）

```json
{ "rolledBack": true, "snapshotWasHealthy": true, "installOk": false, "error": null,
  "restoredFiles": [ "package.json", "pnpm-lock.yaml", … ], "at": "…",
  "snapshot": "snap-20261002-230452-pre-qc-live", "reason": "startup crash loop" }
```

`rolledBack` 是否真的退了 / `snapshot` 退到哪一个 / `restoredFiles` 还原了哪些文件 /
`installOk` 依赖是否重装成功 / `error` 失败原因（成功时为 null）。

### `crash-evidence-*.json`（崩溃现场）

```json
{ "path": "…\\console\\console-20261002-230743.log", "bytes": 98,
  "tail": "stub-dsh: booting\r\nstub-dsh: FATAL plugin @scope/broken failed to load\r\n…" }
```

判定崩溃那一刻，把 DSH 最后的输出**抓一份存下来**——因为回退会覆盖配置，
证据必须先留。`tail` 往往直接写着是哪个插件加载失败。

### `exe-trace.log`（程序自身轨迹）

```
2026-10-03 03:33:16.990 child output (dsh-snapshot.ps1 -Action Mark-Good): Snapshot to create: …
```

每次启动子脚本都记一行：**调了哪个脚本、它输出了什么**。按钮"点了没反应"时看这里。

> 这份文件里中文字符经常显示成 `����`：子进程输出是按系统 ANSI 代码页写进来的。
> **不影响功能**，命令与路径的 ASCII 部分仍然可读。

### `gui-clicks.log`（界面收到的点击）

```
04:35:46.196  window bounds restored: 1322,201 1142x898
04:35:47.334  selftest: entering RunAction(5)
```

| 行 | 含义 |
|---|---|
| `form loaded, action buttons=8` | 界面建好了，八个按钮都在 |
| `click: 打基线  busy=False actionsEnabled=True` | **点击确实到达了按钮** |
| `handler threw: …` | 到了按钮，但动作内部抛异常（含堆栈） |
| `window bounds restored: …` | 窗口位置被记忆并还原 |
| `window bounds ignored (off-screen)` | 记下的坐标已不在任何显示器上，**被忽略并回到默认位置** |

**判定方法**：没有 `click:` 行 → 输入没送到按钮；有 `click:` 但无后续 → 动作本身出错。

### `events.jsonl`（结构化事件流）

一行一个 JSON，便于检索：

```json
{"ts":"…","kind":"CRASH-LOOP-PROVEN", …}
{"ts":"…","kind":"ROLLBACK", …}
{"ts":"…","kind":"CONFIG-CHANGED","previous":"…"}
{"ts":"…","kind":"START-FAILED","error":"…"}
{"ts":"…","kind":"ROLLBACK-IMPOSSIBLE","reason":"no known-good snapshot pointer"}
```

`kind` 就是事件类型。**想快速回答"这台机器上到底回退过几次"，数 `"kind":"ROLLBACK"` 的行即可。**

---

## 五、目录里都有什么

| 目录 | 内容 |
|---|---|
| `snapshots\snap-<时间>-<标签>\` | 一个"好版本"：6 个配置文件的副本。**旧快照永不删除、永不覆盖** |
| `snapshots\pre-restore-<时间>\` | 每次回退前的**坏配置留底**。回退不该销毁证据，也可以用它反悔 |
| `console\` | 每次启动 DSH 一个 `console-<时间>.log`（stdout+stderr）。空目录 = 还没启动过 |
| `诊断报告\` | 诊断报告，一次运行一个文件 |

**快照名字是可以选的**：`snap-…-known-good`、`snap-…-pre-qc-live` 之类，
标签部分由打基线时的 `-Label` 决定。

---

## 六、出了事该交什么

一张清单，按顺序：

1. **`诊断报告\` 里最新那份**——一键收集，包含下面大部分内容，**优先交这个**
2. 界面「执行结果」区的完整输出
3. `watchdog.log`（整份，不用截取）
4. DSH 自己的报错：`console\` 里最新那份
5. 回退过的话：`rescue-*.json` 与对应的 `pre-restore-<时间>\` 目录名

**不含密码、密钥、账号信息**，可以直接贴。

---

## 七、看着吓人但其实正常的行

| 行 / 现象 | 为什么不慌 |
|---|---|
| `[WARN] DSH unhealthy (failure #1)` | 一次失败不回退，要累积成崩溃循环 |
| `DSH starting: …` / `Still inside the boot window: no verdict yet.` | 宽限期内**故意**不下结论，避免把慢启动误判成崩溃 |
| `matching process(es)` 数字忽大忽小 | 它是取证信息，不是判据；标了 `(degraded)` 时更不可信 |
| `resident: exiting (launcher closed)` | 启动它的窗口关了，属预期行为 |
| `Auto-rollback suppressed … (cap 2)` | 是**保护**生效，防止无限回退 |
| `[ALERT] Auto-rollback requested but no known-good snapshot pointer exists.` | 它拒绝瞎猜版本。打一次基线即可 |
| `[WARN]` 开头的任何行 | 提示性，不影响判定 |
| `exe-trace.log` 里的 `����` | 子进程输出的编码显示问题，功能正常 |
| `gui-crash.log` / `console-crash.log` **不存在** | 只在真崩溃时才产生 |
| `runtime.pid` **不存在** | 表示当前没在监视 |
| `probe.log` 里的 `MOUSE x=… btn=…` | 早期控制台版鼠标输入实验的残留文件，**现在没有任何代码写它**，可以忽略或删除 |
| `launch-captured-*.cmd` / `.vbs` | 早期启动包装器的历史文件，**现在不再生成**；旧的不影响使用 |

---

## 八、相关文档

| 想知道 | 看 |
|---|---|
| 界面上每个按钮做什么 | [图形界面说明](GUI.md) |
| 完整使用说明、常见问题 | [使用说明](GUIDE-zh.md) |
| 判定规则、编码陷阱、CLI | [技术文档](TECHNICAL.md) |
