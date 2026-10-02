# 图标素材来源与许可

本目录只放**静态图片素材**，不含可执行代码、字体或安装程序。

---

## 1. 盾牌图形 `shield-check.svg`

| 项 | 值 |
|---|---|
| 名称 | `lucide:shield-check` |
| 来源 | `https://cdn.jsdelivr.net/npm/lucide-static@0.545.0/icons/shield-check.svg` |
| 版本 | **固定 `0.545.0`**（不使用 `@latest`，避免上游内容变动导致产物不可复现） |
| 许可 | **ISC**（宽松许可，全文见 `LICENSE-lucide.txt`） |
| 大小 | 499 字节 |
| SHA-256 | `9838A6AC1E567F57EF07545FD75138568A30E21BFDD8A04895B2093636BFDAE8` |
| 复核方式 | 重新下载同一 URL，`Get-FileHash -Algorithm SHA256` 应与上值一致（本次已核对：一致） |

### 安全核验记录（下载前逐项确认）

| 风险项 | 结论 | 依据 |
|---|---|---|
| 代码执行 | **无** | 文件内只有两个 `<path d="…">`，无脚本；且本目录不做任何"渲染即执行"的操作 |
| 外链 / 追踪 | **无** | 无 `<script>`、`<image>`、`<use>`、`xlink:href`，无网络引用 |
| 格式可审计 | **是** | 纯文本 SVG，全文 499 字节，已逐行审阅 |
| 传输 | **HTTPS** | `cdn.jsdelivr.net`，HTTP 200 |
| 许可 | **宽松** | ISC，允许使用／修改／再分发，需保留版权与许可声明（声明已内嵌于 SVG 首行） |
| 内容完整性 | **已核对** | 本地文件与远端 SHA-256 一致 |

> **未做的事**：没有下载任何可执行文件、脚本、字体、压缩包；没有运行下载物；
> 没有引入需要联网才能工作的运行时依赖——图标在构建期已转为 `.ico` 并嵌入 exe。

---

## 2. 生成的产物

| 文件 | 说明 | 复现方式 |
|---|---|---|
| `appicon.html` | 图标版式（蓝底圆角 + 白色盾牌），不是运行时文件 | — |
| `dsh-guardian-app.ico` | 7 尺寸应用图标：16/24/32/48/64（BMP 条目）+ 128/256（PNG 条目） | ① Chrome 无头渲染 `appicon.html`（视口 256×256）得 PNG；② `make-ico.ps1 <png> <out.ico>` |
| `make-ico.ps1` | 把单张 PNG 组装成多尺寸 ICO 的脚本 | 直接运行，参数为源 PNG 与输出路径 |

生成后的 ICO：73,981 字节，
SHA-256 `265D826EC2A60C38961023F7176F3CD0465AC03FC1819274DD261DDD66469A0A`。

**为什么小尺寸用 BMP、大尺寸用 PNG**：Windows 10/11 两种都能读，
但部分旧版外壳代码在 128 以下只认 DIB 条目；两种都写不增加成本，兼容性最好。

---

## 3. 替换图标的方法

```powershell
# 1. 渲染（任意能出 256x256 PNG 的方式），或直接用本目录的 appicon.png 母图
# 2. 组装，直接输出到 app\：仓库里不存这份 .ico，它由母图生成，避免两份重复
powershell -File assets\icons\make-ico.ps1 assets\icons\appicon.png app\dsh-guardian-app.ico
# 3. 重新编译（build.ps1 只在图标缺失时才自动生成，存在则尊重现有文件）
powershell -File app\build.ps1
```

---

## 4. 透明背景修正（2026-10-03）

首版图标**没有透明背景**：ppicon.html 的透明区域被 Chrome 无头截图输出成了**不透明白底**
（实测源图是 Format24bppRgb，四角为纯白 255,255,255）。结果是"白方块里嵌一个蓝色圆角块"，
桌面和任务栏上都不对。

修正方式：对渲染结果做**边缘泛洪抠底**（容差 40），只把与边框相连的近白像素置为透明；
盾牌描边被蓝色包围、不与边框相连，因此完整保留（实测 6,029 个近白不透明像素）。
四角 A=0，边缘中点仍是蓝色 A=255。

| 文件 | 说明 | SHA-256（前 16 位） |
|---|---|---|
| ppicon.png | 透明母图，256×256 Format32bppArgb，18,515 B | $(D084E34703A97A5F5677EAD65168F8E731BE16072F4AEA2575DADE79B67A42A4.Substring(0,16)) |
| dsh-guardian-app.ico | 最终 7 尺寸图标，73,615 B | $(92D0754D8412E8D6227A016146BD9794B290CCDF66C486959F724A7FA8E8A70E.Substring(0,16)) |

**逐尺寸透明性实测**（角像素 A 值）：

`
16px 角A=0   24px 角A=0   32px 角A=0   48px 角A=0   64px 角A=0   128px 角A=0   256px 角A=0
`

**注意**：System.Drawing.Icon.ToBitmap() 对 128/256 这两个 **PNG 条目**会抛
Requested range extends past the end of the array。这是 .NET Icon 类不支持 PNG 压缩条目的
已知限制，**不是文件损坏**——手工解析 ICO 目录后，两个 PNG 条目都能正常解码为
Format32bppArgb，Windows 资源管理器本身也支持。