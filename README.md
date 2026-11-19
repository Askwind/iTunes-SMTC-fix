# iTunes-SMTC-fix

**[English](#english)** · **[中文](#中文)**

---

## English

A patched fork of [thewizrd/iTunes-SMTC](https://github.com/thewizrd/iTunes-SMTC) (MIT, v1.3.3).

The original project bridges **iTunes** to the Windows **System Media Transport Controls** (SMTC), so the volume flyout, media keys, lyrics apps and desktop widgets can read what iTunes is playing.

### What this fork changes

Upstream shows album art only for tracks in the local library. Apple Music streaming and catalog tracks (*Listen Now* / *Browse*) fall back to a gray placeholder — see upstream issues [#5](https://github.com/thewizrd/iTunes-SMTC/issues/5), [#8](https://github.com/thewizrd/iTunes-SMTC/issues/8), [#10](https://github.com/thewizrd/iTunes-SMTC/issues/10), unresolved.

| Change | File |
| --- | --- |
| Online cover lookup via the public iTunes Search API when COM returns nothing usable | `iTunes/ArtworkFallback.cs` *(new)* |
| Validates what COM actually wrote before trusting it; refresh is async and sequence-guarded | `iTunes/iTunesController.cs` |
| Each COM property read in its own `try`/`catch`, so one failure no longer drops the rest | `iTunes/Extensions/iTunesAppExtensions.cs` |
| Added the missing `Keys/SentryConfig.cs` — upstream never committed it, so a clean clone did not compile. DSN is empty, Sentry is off | `Keys/SentryConfig.cs` *(new)* |
| Rebuilds `iTunes.SMTC.dll.config` from an embedded copy, so the single-file exe works alone | `Program.cs`, `.csproj` |
| Build script: framework-dependent + compressed (≈15–22 MB instead of ≈85 MB) | `build.bat` |

Local files still go through the original COM path — zero network requests. The fallback only runs when iTunes returns an empty or unusable artwork object.

### Build

```
build.bat
```

or manually:

```
dotnet publish iTunes.SMTC\iTunes.SMTC.csproj -c UnpackagedRelease -r win-x64 --self-contained false -p:Platform=x64 -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o out
```

Needs the **.NET 8 SDK** to build (no Visual Studio required), and the **.NET 8 Desktop Runtime + ASP.NET Core 8 Runtime** to run. Output is `out\iTunes.SMTC.exe`.

Optional: copy `artwork-fallback.ini` to `%LOCALAPPDATA%\iTunes.SMTC\` to tweak or disable the fallback. Diagnostics go to `artwork-fallback.log` in the same folder.

### License

MIT — see [LICENSE](LICENSE). Original work © Dave Antoine / thewizrd.

---

## 中文

[thewizrd/iTunes-SMTC](https://github.com/thewizrd/iTunes-SMTC)（MIT，v1.3.3）的修补分支。

原项目把 **iTunes** 桥接到 Windows 的**系统媒体传输控制**（SMTC），让音量弹窗、媒体键、歌词软件和桌面挂件都能读到 iTunes 正在播放的内容。

### 这个分支改了什么

原项目只对本地资料库曲目显示专辑封面。Apple Music 的流媒体和目录曲目（「为你推荐」「浏览」）会退化成一张灰色占位图——对应上游 issue [#5](https://github.com/thewizrd/iTunes-SMTC/issues/5)、[#8](https://github.com/thewizrd/iTunes-SMTC/issues/8)、[#10](https://github.com/thewizrd/iTunes-SMTC/issues/10)，一直没有解决。

| 改动 | 文件 |
| --- | --- |
| COM 拿不到可用封面时，改走公开的 iTunes Search API 在线查找 | `iTunes/ArtworkFallback.cs`（新增） |
| 先校验 COM 到底写出了什么再决定是否采用；刷新改为异步且带序号校验 | `iTunes/iTunesController.cs` |
| 每个 COM 属性单独 `try`/`catch`，一个失败不再连累其余 | `iTunes/Extensions/iTunesAppExtensions.cs` |
| 补齐缺失的 `Keys/SentryConfig.cs`——上游从未提交，导致 clone 下来编译不过；DSN 为空，Sentry 关闭 | `Keys/SentryConfig.cs`（新增） |
| 从内嵌副本重建 `iTunes.SMTC.dll.config`，单文件 exe 可独立运行 | `Program.cs`、`.csproj` |
| 构建脚本：依赖框架 + 压缩（约 15–22 MB，而不是 85 MB） | `build.bat` |

本地文件仍走原来的 COM 路径，零网络请求。兜底只在 iTunes 返回空封面或无效封面时才会触发。

### 构建

```
build.bat
```

或者手动：

```
dotnet publish iTunes.SMTC\iTunes.SMTC.csproj -c UnpackagedRelease -r win-x64 --self-contained false -p:Platform=x64 -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o out
```

构建需要 **.NET 8 SDK**（不需要 Visual Studio），运行需要 **.NET 8 桌面运行时 + ASP.NET Core 8 运行时**。产物为 `out\iTunes.SMTC.exe`。

可选：把 `artwork-fallback.ini` 复制到 `%LOCALAPPDATA%\iTunes.SMTC\` 可以调整或关闭兜底；诊断日志写在同一目录的 `artwork-fallback.log`。

### 许可证

MIT，见 [LICENSE](LICENSE)。原始作品版权归 Dave Antoine / thewizrd 所有。
