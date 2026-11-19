using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace iTunes.SMTC.iTunes
{
    /// <summary>
    /// 在线封面兜底。
    /// iTunes 通过 COM（IITTrack.Artwork）拿不到 Apple Music 流媒体曲目的封面，
    /// 只能拿到空的 Artwork 集合，于是系统媒体弹窗只剩灰色音符占位图。
    /// 这里按 曲名 + 歌手 到 iTunes Search API 匹配封面，下载 600x600 的图并缓存到本地。
    /// </summary>
    internal static class ArtworkFallback
    {
        private const string ConfigFileName = "artwork-fallback.ini";
        private const string UserAgent = "iTunes.SMTC.ArtworkFallback/1.0";
        private const int MinScorePercent = 60;

        /// <summary>直连客户端：系统代理常把 Apple 接口拦成 403，直连反而通。</summary>
        private static readonly HttpClient HttpDirect;

        /// <summary>系统代理客户端：少数网络只有走代理才出得去。</summary>
        private static readonly HttpClient HttpProxy;

        private static readonly object CacheLock = new object();
        private static readonly Dictionary<string, bool> Misses = new Dictionary<string, bool>();
        private static readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);

        private static readonly bool FallbackEnabled;
        private static readonly bool DebugLogEnabled;
        private static readonly string[] Storefronts;

        /// <summary>直连和代理都失败时，短时间内不再重试，避免每条曲目都等满超时。</summary>
        private static DateTime _networkBlockedUntil = DateTime.MinValue;

        static ArtworkFallback()
        {
            HttpDirect = CreateClient(false);
            HttpProxy = CreateClient(true);

            FallbackEnabled = ReadBool("Enabled", true);
            DebugLogEnabled = ReadBool("DebugLog", true);
            Storefronts = ReadStorefronts();

            Log("init: storefronts=" + string.Join(",", Storefronts)
                + " enabled=" + FallbackEnabled);
        }

        private static HttpClient CreateClient(bool useProxy)
        {
            var handler = new HttpClientHandler
            {
                UseProxy = useProxy,
                AllowAutoRedirect = true
            };

            if (!useProxy)
            {
                handler.Proxy = null;
            }

            var client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(8)
            };

            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
            client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json,image/*,*/*");

            return client;
        }

        private static readonly object LogLock = new object();

        /// <summary>
        /// Appends one line to %LOCALAPPDATA%\iTunes.SMTC\artwork-fallback.log.
        /// Without a debugger this is the only way to see what COM returned for a track.
        /// </summary>
        internal static void Log(string message)
        {
            if (!DebugLogEnabled)
            {
                return;
            }

            try
            {
                lock (LogLock)
                {
                    Directory.CreateDirectory(BaseDirectory);

                    var path = Path.Combine(BaseDirectory, "artwork-fallback.log");
                    var info = new FileInfo(path);
                    if (info.Exists && info.Length > 512 * 1024)
                    {
                        File.Delete(path);
                    }

                    File.AppendAllText(
                        path,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message + Environment.NewLine,
                        Encoding.UTF8);
                }
            }
            catch
            {
            }
        }

        internal static string BaseDirectory
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "iTunes.SMTC");
            }
        }

        private static string CacheDirectory
        {
            get { return Path.Combine(BaseDirectory, "artcache"); }
        }

        /// <summary>
        /// 返回封面图片的原始字节；匹配不到或已禁用时返回 null。
        /// 命中缓存时不会发起网络请求。
        /// </summary>
        internal static async Task<byte[]> TryGetArtworkBytesAsync(string artist, string album, string title)
        {
            if (!FallbackEnabled)
            {
                return null;
            }

            // Catalog tracks occasionally lose the artist field when a COM property throws.
            // The title alone is still enough to search with, but then it must match exactly.
            if (string.IsNullOrWhiteSpace(title))
            {
                return null;
            }

            var key = BuildKey(artist, album, title);
            var cachePath = Path.Combine(CacheDirectory, key + ".jpg");

            try
            {
                if (File.Exists(cachePath))
                {
                    var cached = await File.ReadAllBytesAsync(cachePath);
                    if (cached != null && cached.Length > 0)
                    {
                        return cached;
                    }
                }
            }
            catch
            {
            }

            lock (CacheLock)
            {
                if (Misses.ContainsKey(key))
                {
                    return null;
                }
            }

            await Gate.WaitAsync();
            try
            {
                if (File.Exists(cachePath))
                {
                    var cached = await File.ReadAllBytesAsync(cachePath);
                    if (cached != null && cached.Length > 0)
                    {
                        return cached;
                    }
                }

                foreach (var storefront in Storefronts)
                {
                    var url = await FindArtworkUrlAsync(storefront, artist, album, title);
                    if (string.IsNullOrEmpty(url))
                    {
                        continue;
                    }

                    var bytes = await DownloadAsync(url);
                    if (bytes == null || bytes.Length == 0)
                    {
                        continue;
                    }

                    try
                    {
                        Directory.CreateDirectory(CacheDirectory);
                        await File.WriteAllBytesAsync(cachePath, bytes);
                    }
                    catch
                    {
                    }

                    return bytes;
                }

                lock (CacheLock)
                {
                    Misses[key] = true;
                }

                return null;
            }
            catch (Exception ex)
            {
                Log("fallback aborted: " + Describe(ex));

                lock (CacheLock)
                {
                    Misses[key] = true;
                }

                return null;
            }
            finally
            {
                Gate.Release();
            }
        }

        private static async Task<string> FindArtworkUrlAsync(string storefront, string artist, string album, string title)
        {
            var hasArtist = !string.IsNullOrWhiteSpace(artist);

            foreach (var query in BuildQueries(artist, album, title))
            {
                string json;

                try
                {
                    var url = "https://itunes.apple.com/search?term=" + Uri.EscapeDataString(query.Key)
                        + "&media=music&entity=" + query.Value + "&limit=25&country=" + storefront;

                    json = await GetStringAsync(url);
                }
                catch (Exception ex)
                {
                    // 之前这里静默失败，日志里连一行都不留，导致看起来像"搜索没命中"。
                    Log("search storefront=" + storefront + " term='" + query.Key + "' ERROR " + Describe(ex));
                    continue;
                }

                var results = ParseResults(json);
                if (results == null || results.Count == 0)
                {
                    Log("search storefront=" + storefront + " entity=" + query.Value
                        + " term='" + query.Key + "' results=0");
                    continue;
                }

                double bestScore;
                var bestUrl = PickBest(results, artist, album, title, hasArtist, out bestScore);

                Log(string.Format(
                    "search storefront={0} entity={1} term='{2}' results={3} best={4:F2} threshold={5:F2} {6}",
                    storefront, query.Value, query.Key, results.Count, bestScore, MinScorePercent / 100.0,
                    bestUrl != null && (bestScore * 100) >= MinScorePercent ? "hit" : "miss"));

                if (bestUrl != null && (bestScore * 100) >= MinScorePercent)
                {
                    return UpscaleArtworkUrl(bestUrl);
                }
            }

            return null;
        }

        /// <summary>
        /// 依次尝试的检索式。标题里常带 "[From GTAVI: Original Score]"、
        /// "(feat. ...)" 这类修饰，直接拿去搜会大幅降低命中率，所以先清洗再搜。
        /// </summary>
        private static List<KeyValuePair<string, string>> BuildQueries(string artist, string album, string title)
        {
            var queries = new List<KeyValuePair<string, string>>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var cleanTitle = CleanTitle(title);
            var hasArtist = !string.IsNullOrWhiteSpace(artist);

            Action<string, string> add = (term, entity) =>
            {
                if (string.IsNullOrWhiteSpace(term))
                {
                    return;
                }

                var trimmed = term.Trim();
                if (!seen.Add(entity + "\u0001" + trimmed))
                {
                    return;
                }

                queries.Add(new KeyValuePair<string, string>(trimmed, entity));
            };

            if (hasArtist && !string.IsNullOrWhiteSpace(cleanTitle))
            {
                add(cleanTitle + " " + artist, "song");
            }

            add(cleanTitle, "song");
            add(title, "song");

            if (hasArtist && !string.IsNullOrWhiteSpace(album))
            {
                add(album + " " + artist, "album");
            }
            else if (!string.IsNullOrWhiteSpace(album))
            {
                add(album, "album");
            }

            return queries;
        }

        private static string CleanTitle(string title)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                return title;
            }

            var value = Regex.Replace(title, @"\[[^\]]*\]", " ");
            value = Regex.Replace(
                value,
                @"\((?:feat|ft|with|from|remaster|remastered|deluxe|explicit|live|version)[^)]*\)",
                " ",
                RegexOptions.IgnoreCase);
            value = Regex.Replace(value, @"\s+", " ").Trim();

            return value.Length == 0 ? title : value;
        }

        private static JArray ParseResults(string json)
        {
            if (string.IsNullOrEmpty(json))
            {
                return null;
            }

            try
            {
                return JObject.Parse(json)["results"] as JArray;
            }
            catch (Exception ex)
            {
                Log("json parse failed: " + Describe(ex));
                return null;
            }
        }

        private static string PickBest(JArray results, string artist, string album, string title, bool hasArtist, out double bestScore)
        {
            string bestUrl = null;
            bestScore = 0;

            foreach (var item in results)
            {
                var candidateUrl = (string)item["artworkUrl100"];
                if (string.IsNullOrEmpty(candidateUrl))
                {
                    continue;
                }

                var artistScore = Score(artist, (string)item["artistName"]);
                var titleScore = Score(title, (string)item["trackName"]);
                var albumScore = Score(album, (string)item["collectionName"]);

                // Without an artist to cross-check, only an exact title match is trusted,
                // otherwise we would happily paste some other album's cover.
                if (hasArtist)
                {
                    if (artistScore <= 0)
                    {
                        continue;
                    }

                    if (titleScore <= 0 && albumScore <= 0)
                    {
                        continue;
                    }
                }
                else if (titleScore < 1.0)
                {
                    continue;
                }

                var total = hasArtist
                    ? (0.5 * artistScore) + (0.35 * Math.Max(titleScore, albumScore)) + (0.15 * albumScore)
                    : (0.65 * titleScore) + (0.35 * albumScore);

                if (total > bestScore)
                {
                    bestScore = total;
                    bestUrl = candidateUrl;
                }
            }

            return bestUrl;
        }

        /// <summary>
        /// 直连优先，失败再退系统代理。实测 clash 代理会把 itunes.apple.com 返回 403，
        /// 而 .NET 的 HttpClient 默认就走系统代理，于是整条兜底链路静默失效。
        /// </summary>
        private static async Task<string> GetStringAsync(string url)
        {
            if (DateTime.UtcNow < _networkBlockedUntil)
            {
                throw new InvalidOperationException("network temporarily blocked after repeated failures");
            }

            try
            {
                return await HttpDirect.GetStringAsync(url);
            }
            catch (Exception directError)
            {
                Log("http direct failed (" + Describe(directError) + "), retry via proxy");

                try
                {
                    var result = await HttpProxy.GetStringAsync(url);
                    _networkBlockedUntil = DateTime.MinValue;
                    return result;
                }
                catch (Exception proxyError)
                {
                    Log("http proxy failed (" + Describe(proxyError) + ")");
                    _networkBlockedUntil = DateTime.UtcNow.AddMinutes(2);
                    throw;
                }
            }
        }

        private static async Task<byte[]> DownloadAsync(string url)
        {
            foreach (var client in new[] { HttpDirect, HttpProxy })
            {
                try
                {
                    using (var response = await client.GetAsync(url))
                    {
                        if (!response.IsSuccessStatusCode)
                        {
                            Log("download " + (int)response.StatusCode + " via "
                                + (ReferenceEquals(client, HttpDirect) ? "direct" : "proxy"));
                            continue;
                        }

                        return await response.Content.ReadAsByteArrayAsync();
                    }
                }
                catch (Exception ex)
                {
                    Log("download error: " + Describe(ex));
                }
            }

            return null;
        }

        private static string Describe(Exception ex)
        {
            if (ex == null)
            {
                return "unknown";
            }

            var message = ex.GetType().Name + ": " + ex.Message;
            if (ex.InnerException != null)
            {
                message += " <- " + ex.InnerException.GetType().Name + ": " + ex.InnerException.Message;
            }

            message = message.Replace("\r", " ").Replace("\n", " ");
            return message.Length > 220 ? message.Substring(0, 220) + "..." : message;
        }

        private static string UpscaleArtworkUrl(string url)
        {
            if (string.IsNullOrEmpty(url))
            {
                return url;
            }

            return url.Replace("100x100bb", "600x600bb");
        }

        private static double Score(string expected, string actual)
        {
            if (string.IsNullOrWhiteSpace(expected) || string.IsNullOrWhiteSpace(actual))
            {
                return 0;
            }

            var left = Normalize(expected);
            var right = Normalize(actual);

            if (left.Length == 0 || right.Length == 0)
            {
                return 0;
            }

            if (string.Equals(left, right, StringComparison.Ordinal))
            {
                return 1.0;
            }

            if (left.Length >= 2 && right.Length >= 2 && (right.Contains(left) || left.Contains(right)))
            {
                return 0.8;
            }

            return 0;
        }

        private static string Normalize(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            // Apple 的曲库用繁体（周杰倫），本地标签多是简体（周杰伦），
            // 不先归一的话歌手永远对不上。用系统的 LCMapStringEx 做简繁转换。
            var folded = ToSimplified(value.Normalize(NormalizationForm.FormKC));
            var builder = new StringBuilder(folded.Length);

            foreach (var ch in folded)
            {
                if (char.IsLetterOrDigit(ch))
                {
                    builder.Append(char.ToLowerInvariant(ch));
                }
            }

            return builder.ToString();
        }

        private const uint LcmapSimplifiedChinese = 0x02000000;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int LCMapStringEx(
            string lpLocaleName,
            uint dwMapFlags,
            string lpSrcStr,
            int cchSrc,
            [Out] char[] lpDestStr,
            int cchDest,
            IntPtr lpVersionInformation,
            IntPtr lpReserved,
            IntPtr lpSortHandle);

        private static string ToSimplified(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return value;
            }

            try
            {
                var buffer = new char[value.Length + 16];
                var length = LCMapStringEx(
                    "zh-CN",
                    LcmapSimplifiedChinese,
                    value,
                    value.Length,
                    buffer,
                    buffer.Length,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero);

                if (length > 0)
                {
                    return new string(buffer, 0, length);
                }
            }
            catch
            {
            }

            return value;
        }

        private static string BuildKey(string artist, string album, string title)
        {
            var raw = Normalize(artist) + "|" + Normalize(album) + "|" + Normalize(title);
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));

            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        private static string ReadValue(string name)
        {
            try
            {
                var path = Path.Combine(BaseDirectory, ConfigFileName);
                if (!File.Exists(path))
                {
                    return null;
                }

                foreach (var rawLine in File.ReadAllLines(path))
                {
                    var line = rawLine.Trim();
                    if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";"))
                    {
                        continue;
                    }

                    var index = line.IndexOf('=');
                    if (index <= 0)
                    {
                        continue;
                    }

                    var key = line.Substring(0, index).Trim();
                    if (!string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    return line.Substring(index + 1).Trim();
                }
            }
            catch
            {
            }

            return null;
        }

        private static bool ReadBool(string name, bool fallback)
        {
            var value = ReadValue(name);
            if (string.IsNullOrEmpty(value))
            {
                return fallback;
            }

            if (value == "1")
            {
                return true;
            }

            if (value == "0")
            {
                return false;
            }

            return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "on", StringComparison.OrdinalIgnoreCase);
        }

        private static string[] ReadStorefronts()
        {
            var fallback = new[] { "tw", "hk", "us" };
            var value = ReadValue("Storefronts");
            if (string.IsNullOrEmpty(value))
            {
                return fallback;
            }

            var list = new List<string>();
            foreach (var part in value.Split(','))
            {
                var item = part.Trim().ToLowerInvariant();
                if (item.Length == 2)
                {
                    list.Add(item);
                }
            }

            return list.Count > 0 ? list.ToArray() : fallback;
        }
    }
}
