# iTunes-SMTC · Artwork Fix

Album art for Apple Music streaming tracks in the Windows media flyout.
让 Windows 媒体弹窗为 Apple Music 流媒体曲目显示专辑封面。

**[English](#english)** · **[中文](#中文)**

---

## English

### What this is

A patched fork of [thewizrd/iTunes-SMTC](https://github.com/thewizrd/iTunes-SMTC) (MIT, v1.3.3).

Upstream bridges iTunes to the Windows **System Media Transport Controls** (SMTC). Once running, iTunes shows up in the volume flyout, responds to media keys, and third-party apps that read SMTC — BetterLyrics, Lyricify, ModernFlyouts, Stream Deck widgets, Last.fm scrobblers — can see what is playing.

One gap remains in upstream: album art only works for tracks in the **local library**. Tracks streamed from Apple Music's *Listen Now* / *Browse* tabs, or streamed from the library without a local file, render a gray placeholder in the flyout.

Upstream has three issues describing exactly this ([#5](https://github.com/thewizrd/iTunes-SMTC/issues/5), [#8](https://github.com/thewizrd/iTunes-SMTC/issues/8), [#10](https://github.com/thewizrd/iTunes-SMTC/issues/10)), the oldest from April 2025, with no maintainer reply. The last commit is June 2025. This fork fixes it.

### Root cause

| Where the track comes from | `CurrentTrack` over COM | Artwork |
| --- | --- | --- |
| Local file | `IITFileOrCDTrack` | works |
| Apple Music track already in the library | catalog object, cached | empty collection → fallback needed |
| Apple Music track from *Listen Now* / *Browse* | transient catalog object | **non-null but unusable** (writes nothing, or iTunes' own gray placeholder) |

Upstream had exactly one source of artwork, in `iTunes/Extensions/iTunesAppExtensions.cs`:

```csharp
metadata.Artwork = track?.Artwork?.Cast<IITArtwork>()?.FirstOrDefault();
```

There is no HTTP client anywhere in upstream. If that returns `null`, `SaveArtwork()` writes the built-in `no_artwork.png` and stops — there is nothing to fall back to.

A second defect made it worse. `GetMetadata()` read nine COM properties inside a single `try` block, so one property throwing on a streaming track silently discarded `Artist`, `Album` and `Artwork` along with it.

### What this fork changes

| File | Change |
| --- | --- |
| `iTunes.SMTC/iTunes/ArtworkFallback.cs` | **New.** Online cover lookup via the public iTunes Search API, with caching, scoring and diagnostics. |
| `iTunes.SMTC/iTunes/iTunesController.cs` | Validates what COM actually wrote (exists, ≥1 KB, not byte-identical to the placeholder); artwork refresh is async and sequence-guarded; never blocks the flyout. |
| `iTunes.SMTC/iTunes/Extensions/iTunesAppExtensions.cs` | Each COM property read in its own `try`/`catch`, so one failure no longer drops the rest. |
| `iTunes.SMTC/Keys/SentryConfig.cs` | **New.** Upstream never committed this file (the author's DSN is private), so the repo did not compile. Returns an empty string — Sentry is disabled, nothing is reported. |
| `iTunes.SMTC/Program.cs` | Skips `SentrySdk.Init` entirely when the DSN is empty; `EnsureAppConfig()` rebuilds the config file from an embedded resource. |
| `iTunes.SMTC/iTunes.SMTC.csproj` | Embeds `App.config` as `iTunes.SMTC.default.config` for the self-healing step above. |
| `build.bat` | One-click publish. Framework-dependent + compressed (~15–22 MB instead of ~85 MB self-contained). |

### How the fallback works

```
COM returns usable artwork ──► save it, done            (zero network requests)
                            │
COM empty / unusable ───────┴─► write placeholder now    (flyout never shows the previous cover)
                                 │
                                 └─► background lookup: cleaned title + artist
                                       │
                                       ├─ hit  ──► download 600×600, overwrite, refresh flyout
                                       └─ miss ──► keep the placeholder
```

Design decisions, all of them driven by measurements rather than guesses:

- **Simplified/Traditional normalization.** Apple's catalog answers with `周杰倫`, local tags say `周杰伦`. Comparing raw strings scored 4/8 on a test set; normalizing through `LCMapStringEx` scored 8/8.
- **Storefront order `tw → hk → us`.** Measured: the `cn` storefront returns `resultCount: 0` for every query, including `beatles`. It is unusable.
- **Direct connection first, system proxy second.** `.NET HttpClient` follows the system proxy by default. On a machine running a local proxy (Clash, etc.) requests to `itunes.apple.com` came back **HTTP 403**, and the exception was silently swallowed. `HttpDirect` (`UseProxy = false`) returns 200; `HttpProxy` stays as a fallback for networks that need it.
- **Query ladder.** `[From ...]`, `(feat. ...)`, `(Remastered)` suffixes are stripped before searching. Order: cleaned title + artist → cleaned title → raw title → album + artist.
- **Strict matching.** The artist must match; the composite score must reach 60/100. With no artist available, only an exact title match is accepted. A wrong cover is worse than a placeholder.
- **Sequence guard.** A late network reply for a track you already skipped past is discarded instead of overwriting the current cover.
- **Disk cache.** `%LOCALAPPDATA%\iTunes.SMTC\artcache\<hash>.jpg`. Misses are cached too, so the same dead query is not retried. After both direct and proxy fail, lookups pause for 2 minutes to avoid timeout storms.

### Build

Requirements: **.NET 8 SDK**. Visual Studio is *not* required for the unpackaged build.

```
build.bat
```

or manually:

```
dotnet publish iTunes.SMTC\iTunes.SMTC.csproj -c UnpackagedRelease -r win-x64 --self-contained false -p:Platform=x64 -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o out
```

Output: `out\iTunes.SMTC.exe`, a single framework-dependent file.

Two things that will bite you:

- **Do not pass `-p:DefineConstants="TRACE;UNPACKAGEDRELEASE"`.** The SDK already derives `UNPACKAGEDRELEASE` from the configuration name (`ImplicitConfigurationDefine`). The quotes are eaten by the shell, MSBuild splits on `;` into two properties, the second has no `=`, and the build dies with `MSB1006: Invalid property`.
- **Do not try to build `iTunes.SMTC (Package)`.** That `.wapproj` requires Visual Studio + the Windows SDK. The unpackaged configuration is the one you want.

### Run

Requirements on the machine running it: **.NET 8 Desktop Runtime** and **ASP.NET Core 8 Runtime** (the app hosts a small local HTTP server). Both are included in the .NET 8 SDK.

Want a build that runs anywhere without installing runtimes? Publish with `--self-contained true` instead — same command, roughly 85 MB.

Only `iTunes.SMTC.exe` is needed next to your install. If `iTunes.SMTC.dll.config` is missing, the app rebuilds it on startup from an embedded copy.

### Configuration

Optional. Drop `artwork-fallback.ini` into `%LOCALAPPDATA%\iTunes.SMTC\` and restart the app:

```ini
[Artwork]
; 0 disables the online fallback (back to upstream behaviour: placeholder only)
Enabled=1
; Storefronts, comma separated. Measured: cn returns no results, do not add it.
Storefronts=tw,hk,us
; 1 appends one diagnostic line per track change to artwork-fallback.log
DebugLog=1
```

### Diagnostics

`%LOCALAPPDATA%\iTunes.SMTC\artwork-fallback.log` gets one block per track change:

```
init: storefronts=tw,hk,us enabled=True
track name='Starting From Zero [From GTAVI: Original Score]' artist='Justice & Michael McDonald' album='...' comArtworkObject=False comArtworkUsable=False fileBytes=5447
search storefront=tw entity=song term='Starting From Zero Justice & Michael McDonald' results=24 best=0.97 threshold=0.60 hit
fallback: got 64222 bytes for 'Starting From Zero [From GTAVI: Original Score]'
```

`fileBytes=5447` is the size of the built-in placeholder — that number appearing means COM gave you nothing and the fallback is doing the work. No `search` line at all means the HTTP request threw before it could log.

### Limitations

- The fallback needs network access. Offline, you get the placeholder.
- Covers come from Apple's public catalog at 600×600. Tracks not sold in any of the configured storefronts will not be found.
- Local files still go through COM unchanged. This fork adds a fallback; it does not replace the original path.
- Windows 10 1809+ only, since SMTC is what the whole thing is built on.
- Upstream's Apple Music (Store app) controller is untouched and untested here — this fork targets the iTunes controller.

### Credits

Upstream: [thewizrd/iTunes-SMTC](https://github.com/thewizrd/iTunes-SMTC), MIT. All original code and the license notice are retained; this fork only adds and modifies the files listed above.

Sentry telemetry is disabled in this build. Nothing leaves your machine except the cover lookups to `itunes.apple.com`.

---

## 中文

### 这是什么

这是 [thewizrd/iTunes-SMTC](https://github.com/thewizrd/iTunes-SMTC)（MIT，v1.3.3）的修补分支。

原项目把 iTunes 桥接到 Windows 的**系统媒体传输控制**（SMTC）。跑起来之后，iTunes 会出现在音量弹窗里、响应媒体键，而那些读取 SMTC 的第三方软件——BetterLyrics、Lyricify、ModernFlyouts、Stream Deck 挂件、Last.fm 记录工具——也都能看到 iTunes 正在放什么。

原项目还剩一个缺口：专辑封面只在**本地资料库曲目**上有效。从 Apple Music 的「为你推荐 / 浏览」标签页播放的曲目，或者没有本地文件的流媒体曲目，弹窗里只显示一张灰色占位图。

原仓库有三个 issue 正是在讲这件事（[#5](https://github.com/thewizrd/iTunes-SMTC/issues/5)、[#8](https://github.com/thewizrd/iTunes-SMTC/issues/8)、[#10](https://github.com/thewizrd/iTunes-SMTC/issues/10)），最早的是 2025 年 4 月，维护者没有回复；仓库最后一次提交在 2025 年 6 月。本分支修复了它。

### 根因

| 曲目来源 | COM 里的 `CurrentTrack` | 封面 |
| --- | --- | --- |
| 本地文件 | `IITFileOrCDTrack` | 正常 |
| 已入库的 Apple Music 曲目 | 目录对象，有缓存 | 空集合 → 需要兜底 |
| 「为你推荐 / 浏览」的曲目 | 临时目录对象 | **非空但无效**（写不出图，或只写出 iTunes 自己的灰色占位图） |

原项目获取封面只有一条路径，在 `iTunes/Extensions/iTunesAppExtensions.cs`：

```csharp
metadata.Artwork = track?.Artwork?.Cast<IITArtwork>()?.FirstOrDefault();
```

整个项目里没有任何 HTTP 客户端。这一行取到 `null` 之后，`SaveArtwork()` 就写内置的 `no_artwork.png` 然后结束——没有任何后备方案。

第二个缺陷让它更糟：`GetMetadata()` 把九个 COM 属性读在**同一个 `try` 块**里。流媒体曲目上只要有一个属性抛异常，`Artist`、`Album`、`Artwork` 会一起丢掉。

### 这个分支改了什么

| 文件 | 改动 |
| --- | --- |
| `iTunes.SMTC/iTunes/ArtworkFallback.cs` | **新增。** 通过公开的 iTunes Search API 在线找封面，带缓存、评分与诊断日志。 |
| `iTunes.SMTC/iTunes/iTunesController.cs` | 校验 COM 到底写出了什么（文件存在、≥1 KB、字节不等于占位图）；封面刷新改为异步且带序号校验；不阻塞弹窗。 |
| `iTunes.SMTC/iTunes/Extensions/iTunesAppExtensions.cs` | 每个 COM 属性单独 `try`/`catch`，一个失败不再连累其他。 |
| `iTunes.SMTC/Keys/SentryConfig.cs` | **新增。** 原仓库没提交这个文件（作者 DSN 是私有的），所以直接 clone 下来编译不过。这里返回空字符串——Sentry 关闭，不会有任何数据外发。 |
| `iTunes.SMTC/Program.cs` | DSN 为空时完全不进入 `SentrySdk.Init`；`EnsureAppConfig()` 从内嵌资源重建配置文件。 |
| `iTunes.SMTC/iTunes.SMTC.csproj` | 把 `App.config` 以 `iTunes.SMTC.default.config` 为名嵌入程序集，供上面那步自愈使用。 |
| `build.bat` | 一键发布。依赖框架 + 压缩（约 15–22 MB，而不是自包含的 85 MB）。 |

### 兜底是怎么走的

```
COM 给出可用封面 ──► 直接存盘，结束        （零网络请求）
                  │
COM 为空 / 无效 ───┴─► 先写占位图           （弹窗不会残留上一首的封面）
                        │
                        └─► 后台检索：清洗后的曲名 + 歌手
                              │
                              ├─ 命中 ──► 下载 600×600，覆盖并刷新弹窗
                              └─ 未命中 ──► 保持占位图
```

几个设计点，都是先实测再定，不是猜的：

- **简繁归一。** Apple 曲库返回的是繁体 `周杰倫`，本地标签是简体 `周杰伦`。直接比对在测试集上只有 4/8；用 `LCMapStringEx` 归一后是 8/8。
- **区服顺序 `tw → hk → us`。** 实测 `cn` 区服对任何关键词都返回 `resultCount: 0`，连 `beatles` 都是 0，完全不能用。
- **直连优先，系统代理兜底。** `.NET HttpClient` 默认走系统代理。在开了本地代理（Clash 之类）的机器上，请求 `itunes.apple.com` 会返回 **HTTP 403**，而且异常被静默吞掉。`HttpDirect`（`UseProxy = false`）返回 200，`HttpProxy` 留给必须走代理的网络。
- **检索式阶梯。** 搜索前先剥掉 `[From ...]`、`(feat. ...)`、`(Remastered)` 这类后缀。顺序：清洗标题+歌手 → 清洗标题 → 原题 → 专辑+歌手。
- **严格匹配。** 歌手必须对上，综合分要到 60/100。拿不到歌手时只接受曲名完全一致。贴错封面比不显示更糟。
- **序号校验。** 你已切歌后才返回的网络结果会被丢弃，不会覆盖当前封面。
- **本地缓存。** `%LOCALAPPDATA%\iTunes.SMTC\artcache\<hash>.jpg`，未命中也会记住，不反复请求。直连和代理都失败后，暂停查询 2 分钟，避免超时风暴。

### 构建

需要 **.NET 8 SDK**。非打包版本**不需要** Visual Studio。

```
build.bat
```

或者手动：

```
dotnet publish iTunes.SMTC\iTunes.SMTC.csproj -c UnpackagedRelease -r win-x64 --self-contained false -p:Platform=x64 -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o out
```

产物：`out\iTunes.SMTC.exe`，依赖框架的单文件。

两个会踩的坑：

- **不要传 `-p:DefineConstants="TRACE;UNPACKAGEDRELEASE"`。** SDK 已经根据配置名自动生成 `UNPACKAGEDRELEASE`（`ImplicitConfigurationDefine`）。引号会被 shell 吃掉，MSBuild 按 `;` 拆成两个属性，第二个没有 `=`，构建直接挂在 `MSB1006: 属性无效`。
- **不要试图构建 `iTunes.SMTC (Package)`。** 那个 `.wapproj` 需要 Visual Studio + Windows SDK。你要的是非打包配置。

### 运行

目标机器需要 **.NET 8 桌面运行时** 和 **ASP.NET Core 8 运行时**（程序内置了一个小的本地 HTTP 服务）。两者都包含在 .NET 8 SDK 里。

想要「拷过去双击就能用、不用装运行时」？把 `--self-contained true` 传进去，命令其余不变，体积约 85 MB。

安装目录里只需要 `iTunes.SMTC.exe` 一个文件。`iTunes.SMTC.dll.config` 缺失时，程序启动会从内嵌副本重建。

### 配置

可选。把 `artwork-fallback.ini` 放到 `%LOCALAPPDATA%\iTunes.SMTC\`，重启程序生效：

```ini
[Artwork]
; 0 = 关闭在线兜底，退回原始行为（只显示占位图）
Enabled=1
; 区服顺序，逗号分隔。实测 cn 无结果，不要加
Storefronts=tw,hk,us
; 1 = 每次切歌往 artwork-fallback.log 追加一行诊断
DebugLog=1
```

### 排障

`%LOCALAPPDATA%\iTunes.SMTC\artwork-fallback.log`，每次切歌写一组：

```
init: storefronts=tw,hk,us enabled=True
track name='Starting From Zero [From GTAVI: Original Score]' artist='Justice & Michael McDonald' album='...' comArtworkObject=False comArtworkUsable=False fileBytes=5447
search storefront=tw entity=song term='Starting From Zero Justice & Michael McDonald' results=24 best=0.97 threshold=0.60 hit
fallback: got 64222 bytes for 'Starting From Zero [From GTAVI: Original Score]'
```

`fileBytes=5447` 就是内置占位图的大小——出现这个数字，说明 COM 什么都没给，正在靠兜底。如果连 `search` 行都没有，说明 HTTP 请求在写日志之前就抛异常了。

### 已知限制

- 兜底需要联网。离线时只有占位图。
- 封面来自 Apple 公开曲库，600×600。在所有已配置区服都未上架的曲目搜不到。
- 本地文件仍走原来的 COM 路径不变。这个分支是**加**兜底，不是替换原有逻辑。
- 仅支持 Windows 10 1809 以上，因为整套东西建立在 SMTC 之上。
- 原项目的 Apple Music（商店版）控制器没有改动，也没有测试——本分支只针对 iTunes 控制器。

### 致谢

上游：[thewizrd/iTunes-SMTC](https://github.com/thewizrd/iTunes-SMTC)，MIT 协议。原始代码与许可证声明全部保留；本分支只新增和修改了上面列出的文件。

本构建已关闭 Sentry 遥测。除了向 `itunes.apple.com` 查询封面，不会有任何数据离开你的机器。

---

## License

MIT — see [LICENSE](LICENSE). Original work © Dave Antoine / thewizrd.
