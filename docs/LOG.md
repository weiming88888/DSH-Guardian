# 日志怎么读

DSH Guardian 会写几种日志。**看日志的顺序**：先看界面里「执行结果」区 → 再看 `data\watchdog.log` → 需要细节才看 `data\console\console-*.log`。

---

## 一、文件在哪、分别是什么

| 文件 | 内容 | 什么时候看 |
|---|---|---|
| `data\watchdog.log` | 监视器的主日志，一轮一行 | **崩了先看这个** |
| `data\console\console-<时间>.log` | 被启动的 DSH 进程的 stdout+stderr | 要看 DSH 自己的报错原文 |
| `data\crash-evidence-<时间>.json` | 判定为崩溃时的现场快照 | 想知道"凭什么判定崩了" |
| `data\rescue-<时间>.json` | 自动回退的救援记录 | 回退发生过之后 |
| `data\exe-trace.log` | 程序自身的动作轨迹 | 监视器启动失败时 |
| `data\gui-clicks.log` | 界面收到的每次点击、布局实测 | 界面"点了没反应"时 |

---

## 二、`watchdog.log` 逐行含义

### 启动与探测

```
probe port 19387 (source: live-process)
```
探测 DSH 在哪个端口。`source` 有四种，**这是排查端口问题的关键**：

| source | 含义 |
|---|---|
| `parameter` | 命令行 `-Port` 指定，优先级最高 |
| `live-process` | 从**正在运行**的 DSH 进程实际占用的端口读出来的（最准） |
| `cached` | 上次探测结果缓存在 `data\port.txt`，本次复用了它 |
| `default` | 以上全失败，退回 DSH 文档默认端口 3080 |

```
DSH healthy (127.0.0.1:19387 listening, 6 matching process(es))
```
**健康判定通过**：端口在监听。括号里的进程数是**取证信息，不是判据**——
这台机器上 `Win32_Process.CommandLine` 被拒绝访问，进程匹配会降级，
数字里可能混进监视器自己的 powershell。所以日志会标 `(degraded)`，那种时候这个数字不可信。

```
tick: alive=True port=19387 pid=22448
```
一轮心跳。`alive` 才是真正的判据。这行每约 30 分钟写一次，避免日志被刷爆。

### 监视器自身的生命周期

```
resident: started (pid 3199, parent 17456, interval 55s, max 240 min)
```
常驻监视器启动。`interval 55s` 是探测间隔；`max 240 min` 是**寿命上限**，
到点自动退出，防止忘记关而永久驻留。

```
resident: exiting (launcher closed)
resident: stopped after 1 round(s)
```
监视器退出，原因是**启动它的窗口关了**——这是设计行为：
监视器归启动它的进程管。想让它继续跑，就别关那个窗口，或用界面里的「开关监视」重新拉起常驻实例。

### 崩溃与回退

```
[ALERT] ACTION NEEDED: disabled by -InstallTimeoutSeconds 0
```
**唯一的"需要人工处理"级别**。这里指：检测到崩溃并回退后，
本来应该重装依赖（`pnpm install`），但该功能被参数 `-InstallTimeoutSeconds 0` 关掉了。
所以 `node_modules` 现在**可能和还原后的 lockfile 不一致**，需要手动跑一次 `pnpm install`。
不想看到这条，就把 `-InstallTimeoutSeconds` 设成大于 0 的秒数。

```
[ALERT] AUTO-ROLLBACK BLOCKED / Auto-rollback suppressed: already rolled back 2 time(s) in this episode (cap 2)
```
**同一轮故障里已经回退过 2 次，触发上限，不再回退。**
这是防"回退→还崩→再回退"的死循环：上限默认 2 次（`cap 2`）。
出现这条说明**回退没有解决问题**，要继续查根因，而不是等它再退。

```
rescue record: D:\...\data\rescue-20261002-230804.json
```
回退的详细记录写在那个文件里：退了哪个版本、还原了哪些文件、是否重新拉起。

```
relaunched: True
```
回退后 DSH 已重新启动成功。

```
Launched (stderr captured to D:\...\data\console\console-20261002-230804.log)
```
启动 DSH 时把它的 stderr 存到了那个文件。
**要看 DSH 自己的报错原文，就打开它。**

```
broken config preserved: D:\...\data\snapshots\pre-restore-20261002-230804
```
**回退前的坏配置被完整保留了**，路径就在日志里。
这是有意为之：回退不该销毁证据，你可以对比坏配置和好配置的差异。

### 结尾

```
Human-reviewed.
```
有人看过这份日志并做了处置。出现这行表示这次事件已经收尾。

---

## 三、判定"崩溃"的三条规则

日志里看不到简短的结论，因为判定是逐步积累的。规则本身是：

1. **启动宽限期**（默认 45 秒）内进程死掉，才算"短命启动"；
2. 连续 **3 次**（`CrashLoopThreshold`）短命启动 = 崩溃循环；
3. 崩溃循环 + 配置与基线不同 = 才允许自动回退。

**只看一次失败不会回退。** 所以日志里出现一两次启动失败是正常的，不必紧张。

---

## 四、常见的"看起来吓人但其实正常"的行

| 行 | 为什么不慌 |
|---|---|
| `matching process(es)` 数字忽大忽小 | 它是取证信息，不是判据；`(degraded)` 时更不可信 |
| `resident: exiting (launcher closed)` | 启动它的窗口关了，属预期 |
| `Auto-rollback suppressed` | 是**保护**生效，防止无限回退 |
| `Still inside the boot window: no verdict yet.` | 启动宽限期内不做判定，避免把慢启动误判成崩溃 |
| `[WARN]` | 提示性，不影响判定 |
