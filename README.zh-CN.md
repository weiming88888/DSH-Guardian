# DSH Guardian

**给 DeepSeek Harness（DSH）*桌面版*用的崩溃自动回退工具。**

装个插件把 DSH 搞崩了、启动不起来？它把配置退回上一个能用的版本，重新对齐依赖，再把 DSH 拉起来。

> 中文 · [English](README.md) · [完整使用说明](docs/GUIDE-zh.md) · [图形界面说明](docs/GUI.md) · [日志怎么读](docs/LOG.md) · [技术文档](docs/TECHNICAL.md)

![DSH Guardian：装插件前点一下「打基线」，插件把 DSH 搞崩后自动退回去](docs/hero.png)

**Windows** · **DSH 桌面版** · **MIT** · **无计划任务、无开机自启**

---

## 我需要它吗

| | |
|---|---|
| **需要** | 你会给 DSH 装插件，而插件有可能让 DSH 起不来 |
| **不需要** | 你从不装插件 —— 它只防这一种情况 |
| **不适用** | 命令行安装的 DSH。只支持桌面版、`desktop` profile |

DSH 自己没有回退功能：插件直接改 `package.json` 和 `pnpm-lock.yaml`，一旦装坏，你连界面都进不去。

## 三步上手

1. **先装好 DSH 桌面版，并启动过一次。** 它需要一个"能正常工作"的状态来做副本。
2. **解压到固定位置，双击 `app\dsh-guardian.exe`。** 没有安装程序、不写注册表，也不会自动给你建快捷方式。
3. **点「打基线」记一个基线，再点「开关监视」开始盯着，窗口保持开着。** 然后放心装插件。

装完插件、DSH 还能正常启动 → 再点一次「打基线」记新基线 → 点「开关监视」停止 → 关窗口。整个流程就这些。

![主界面](docs/main-window.png)

## 界面上的 8 个按钮

| 按钮 | 作用 |
|---|---|
| 打基线 | 把当前状态记为一个"好版本" |
| 开关监视 | 开始 / 停止监视 |
| 回退 | 选一个版本：现在就回退，或只设为以后的目标 |
| 日志 | 错误日志，**出问题先点这个** |
| 目录 | 浏览 `data\`：快照、日志、诊断报告 |
| 刷新 | 重新读一遍状态 |
| 放大结果 | 收起上面的分区，让输出占满窗口 |
| 退出 | 退出（还在监视时会先提醒你） |

状态行是最该看的一行：`○ 未监视` / `● 监视中` / `◐ 已开启，监视器启动中…`。

## 窗口就是开关

这是硬性设计，不是"尽量"：

| | |
|---|---|
| 计划任务 | **没有** |
| 开机自启 / 注册表 Run 项 | **没有** |
| 关掉窗口后的后台进程 | **没有** |
| 什么时候开始监视 | 你点「开关监视」的那一刻 |
| 什么时候停止 | 再点一次「开关监视」，或者关掉窗口 |

监视器用 `-Resident -ParentPid <本窗口进程号>` 启动：窗口进程一消失，监视器跟着退出，日志里记
`resident: exiting (launcher closed)`。

**所以关掉窗口 = 停止监视**，它不会在你不知情时留在后台。
**去装插件之前，先确认窗口还开着。**

## 插件把 DSH 搞崩之后会发生什么

前提：窗口开着、监视是"开"。

1. 端口探测失败 → 重启 DSH
2. 一直失败 → 判定为**启动崩溃循环**（两个独立信号之一）：
   - `-CrashLoopThreshold` 次启动（默认 3）都在 `-StartGraceSeconds`（默认 45 秒）内死掉，**或**
   - 重启预算 `-BootRetryBudget`（默认 3）用尽，DSH 始终不响应
3. 抓崩溃现场：每次启动的 stdout+stderr，存在 `data\console\`
4. 你那套坏配置**另存**到 `data\snapshots\pre-restore-<时间>\`，不会丢
5. 恢复上一个好版本
6. 按恢复后的锁文件重新对齐 `node_modules`
7. 重启 DSH

正常情况下你只会看到 DSH 自己起来了。

## 它拒绝的时候比动手的时候多

| 闸门 | 效果 |
|---|---|
| 还没打过基线 | 拒绝回退并写明原因，不瞎猜 |
| 这套配置已经回退过 | **同样的配置绝不回退第二次** |
| 回退也没救回来 | 停止重启并告知一次，不陷入循环 |
| `-MaxAutoRollbacks`（默认 2） | 单次崩溃的硬上限 |

## 为什么要等约 90 秒

一次启动必须等满整个 `-BootWindowSeconds` 窗口（默认 90 秒）才会被算作"短命启动"——
"启动慢但没事"不能被误判成崩溃。所以一个瞬间就死的 DSH，在这 90 秒里日志仍然写
`DSH starting`，自动回退要再等一轮才触发。期间每 30 秒写一行"仍在启动窗口内，暂未下结论"，
不会看起来像卡住。

嫌慢就把 `-BootWindowSeconds` 调小（例如 `10`），前提是你的 DSH 正常情况下不需要那么久才能占住端口。

## 基线是什么

基线 = DSH 还能正常工作时，那六个配置文件的副本：`package.json`、`pnpm-lock.yaml`、
`cordis.patch.yml`、`cordis.yml`、`pnpm-workspace.yaml`、`compatibility.json`。

**可以存很多份。** 每点一次「打基线」新增一份，旧的**永不覆盖**。
**但自动回退只用其中一份**，就是状态行「回退目标」显示的那份。它指向哪里，决定了崩溃后你退到多远：

| 目标指向 | 回退后你得到 |
|---|---|
| 最新基线（装 B 之前） | **A 还在** —— 只想干掉 B |
| 更早的基线（装 A 之前） | **A、B 都没了** |

点「回退」把它们列出来挑：

![版本选择](docs/versions.png)

列表会标出当前生效的那份，然后你选**现在就回退**、**只设为以后的目标**、或**取消**。
写盘之前，当前配置会先另存到 `data\snapshots\pre-restore-<时间>\`，所以手动回退**永远可逆**。

> 快照名精确到秒；同一秒内打两次基线，第二份会带 `-2` 后缀。已存在的快照绝不覆盖。

## 安装与环境要求

1. 从 **[Releases](https://github.com/weiming88888/DSH-Guardian/releases/latest)** 下载压缩包
   （含 exe + 源码 + 文档），解压到固定位置。
2. 双击 `app\dsh-guardian.exe`。

| | |
|---|---|
| 需要 | 已安装 **DeepSeek Harness 桌面版**并有 `desktop` profile —— **不是**只有命令行的安装 |
| 平台 | **仅 Windows** |
| 还有 | Windows PowerShell 5.1（系统自带）。不需要 SDK、不需要安装程序、不写注册表 |
| 快捷方式 | **不会自动创建**。想要就运行一次 `dsh-guardian.exe shortcut` |
| 卸载 | 删掉文件夹即可 |

## 命令行（可选）

动词在 `dsh-guardian-console.exe` 上；`dsh-guardian.exe` 会把动词转发给它。

```
logs        看错误日志
baseline    打基线
rollback    选版本回退，或只设为目标
preview     只显示回退计划，什么都不改
arm / on    开始监视        disarm / off   停止监视
shortcut    创建桌面快捷方式（仅手动）
diagnostics 生成诊断报告
probe       探测 DSH 端口并报告
help        用法
```

`dsh-snapshot.ps1` 还支持
`-Action Create | List | Verify | Restore | Mark-Good | Promote | Delete`、
`-DryRun`、`-Force`。`-Action` 默认是 `Create`；不加 `-Force` 的 `Restore` 只打印计划，不写盘。

## 出问题了

1. 界面上的**「日志」** —— 最近的报错，尽量包含 DSH 自己的原始输出。
2. **诊断** —— 桌面「DSH Guardian 诊断」快捷方式，或 `app\dsh-guardian.exe diagnostics`。

![浏览 data 目录](docs/browser.png)

报告写在 `data\诊断报告\诊断报告-<日期>-<时间>.txt`，内容包括程序状态、监视器存活情况、
计划任务与自启项检查、DSH profile 状态、端口探测、全部日志、崩溃证据、快照清单。
**不含密码、密钥、账号信息。** 把报告贴到
[Issues](https://github.com/weiming88888/DSH-Guardian/issues) 即可。

## 包里有什么

```
app\                            程序本体（文件名刻意保持 ASCII）
  dsh-guardian.exe                图形界面 —— 双击这个
  dsh-guardian-console.exe        上面那些命令行动词
  dsh-watchdog.ps1                监视器：探测 / 判断 / 回退
  dsh-snapshot.ps1                快照、恢复、切换目标
  collect-diagnostics.ps1         诊断收集
  make-shortcut.ps1               写桌面快捷方式
  launch-diagnostics.cmd          诊断启动器
  Guardian.manifest               DPI 感知 + asInvoker
  Guardian.Win.cs / Guardian.exe.cs   源码
  build.ps1                       重新编译
  dsh-guardian-app.ico            图标
docs\                           使用说明与截图
data\                           运行时生成，不随包分发
  snapshots\   console\   诊断报告\   以及状态与日志文件
```

发布包（418 KB）只含 `app\` 和 `docs\`。`data\` 里有你本机的路径和插件清单，所以被 git 忽略。
宣传图及其 HTML 渲染源留在仓库里，但不进压缩包。

## 重新编译

只用 Windows 自带的 .NET Framework `csc.exe`，**不需要装 SDK**：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File "app\build.ps1"
```

## 已知限制

- **端口是探测出来的，不是假设的。** 顺序：`-Port` → 运行中 DSH 进程实际监听的端口 →
  `data\port.txt` 里上次成功探测的缓存（崩溃后 DSH 已经不在了，靠它续上）→ 3080。
  假设一个端口会让它把没崩的 DSH 判成崩溃，再去"救"一个好好的 DSH。
- **监视只在窗口开着时有效。** 这是"平时不运行"的必然代价，两者不可兼得。
- 探测方式是 **TCP 连接**（默认 3 秒超时），不是 HTTP 请求；只能判断"端口是否响应"，
  看不出进程内部的偶发错误。
- 只写上面那六个配置文件。**不读**你的会话与凭据，也**不删** `node_modules`
  （只按恢复后的版本重新对齐）。
- `dsh plugin --profile <名字> install` 会被 DSH CLI 拒绝（该 profile 由 Electron 应用独占管理），
  所以对齐依赖时直接调用应用自带的 pnpm
  （`resources\runtime\pnpm\dist\pnpm.mjs` + 运行时自带的 `node.exe`）。
- 常驻监视有存活上限（`-MaxResidentMinutes`，默认 240 分钟），忘记关窗口也不会永久占用。
- **改代码的人注意**：`.ps1` 必须保持纯 ASCII（或带 UTF-8 BOM）。PowerShell 5.1 读取无 BOM 的脚本时
  按 ANSI 代码页解码，源码里直接写中文会让解析失败；需要输出中文时用转义存储、运行时解码。
  详见 [docs/TECHNICAL.md](docs/TECHNICAL.md)。

## 许可

MIT。见 [LICENSE](LICENSE)。
