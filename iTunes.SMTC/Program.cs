using iTunes.SMTC.Keys;
using Sentry.Extensibility;
using Sentry.Protocol;
using System.Net;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Text.Json;

namespace iTunes.SMTC
{
    internal static class Program
    {
        private static Mutex _mutex = null;

        /// <summary>
        ///  The main entry point for the application.
        /// </summary>
        [STAThread]
        static void Main()
        {
            const string appName = "iTunes.SMTC";

            _mutex = new Mutex(true, appName, out bool createdNew);

            if (createdNew)
            {
                // Must run before anything touches Settings / ConfigurationManager.
                EnsureAppConfig();

                ApplicationConfiguration.Initialize();

                // Configure WinForms to throw exceptions so Sentry can capture them.
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);

                // Init the Sentry SDK.
                // This fork does not ship the author's private DSN, so the lookup returns an
                // empty string. Sentry 5.x throws ArgumentNullException if the DSN is null,
                // therefore an empty DSN must skip Init entirely.
                var sentryDsn = SentryConfig.GetDsn();

                if (string.IsNullOrWhiteSpace(sentryDsn))
                {
                    Application.Run(new SettingsUi());
                }
                else
                {
                    var sentryOptions = new SentryOptions()
                    {
                        // Tells which project in Sentry to send events to:
                        Dsn = sentryDsn,
#if DEBUG
                        // When configuring for the first time, to see what the SDK is doing:
                        Debug = true,
#endif
                    };

                    // Limit exceptions captured
                    sentryOptions.AddExceptionFilter(new ExceptionFilter());

                    using (SentrySdk.Init(sentryOptions))
                    {
                        Application.Run(new SettingsUi());
                    }
                }
            }
        }

        private const string DefaultConfigResourceName = "iTunes.SMTC.default.config";
        private const string ConfigFileName = "iTunes.SMTC.dll.config";

        /// <summary>
        ///  上游把 5 个用户设置的默认值放在 App.config（发布后叫 iTunes.SMTC.dll.config）。
        ///  这个文件不能省：Settings.Save() 要靠它里面的 configSections 声明，
        ///  缺了会抛 ConfigurationErrorsException（每次切开关都会保存）。
        ///
        ///  但它不必待在 exe 旁边。这里把它放到 %LOCALAPPDATA%\iTunes.SMTC\，
        ///  程序目录就只剩一个 exe，而且 Program Files 这类不可写目录也能正常工作，
        ///  exe 挪位置后设置也不会丢。
        ///
        ///  路径切换靠 AppDomain 的 APP_CONFIG_FILE，必须在任何 Settings /
        ///  ConfigurationManager 访问之前完成；设完之后再回问一次
        ///  ConfigurationManager 实际用的是哪个文件，不认就退回 exe 旁边，
        ///  保证任何情况下 Save() 都有 section 声明可用。
        /// </summary>
        private static void EnsureAppConfig()
        {
            try
            {
                var legacyPath = Path.Combine(AppContext.BaseDirectory, ConfigFileName);

                var configPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "iTunes.SMTC",
                    ConfigFileName);

                // 旧版本写在 exe 旁边：内容可能被用户改过默认值，所以是搬过去而不是重建
                if (!File.Exists(configPath) && File.Exists(legacyPath))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(configPath));
                    File.Copy(legacyPath, configPath);
                }

                WriteDefaultConfigIfMissing(configPath);

                AppDomain.CurrentDomain.SetData("APP_CONFIG_FILE", configPath);

                if (!IsAppConfigPath(configPath))
                {
                    // 系统没接受重定向：保持旧行为，确保 exe 旁边有一份可用的
                    WriteDefaultConfigIfMissing(legacyPath);
                    return;
                }

                // 只在这时才清掉旧文件，避免两头都没有
                TryDelete(legacyPath);
            }
            catch
            {
                // 自愈失败不能影响启动：读设置仍有编译期默认值兜底
            }
        }

        /// <summary>
        ///  ConfigurationManager 实际会读取的文件是不是 <paramref name="expected"/>。
        ///  这一步也会把配置系统初始化在当前路径上，所以必须先 SetData 再问。
        /// </summary>
        private static bool IsAppConfigPath(string expected)
        {
            try
            {
                var actual = global::System.Configuration.ConfigurationManager
                    .OpenExeConfiguration(global::System.Configuration.ConfigurationUserLevel.None)
                    .FilePath;

                return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static void WriteDefaultConfigIfMissing(string path)
        {
            if (File.Exists(path))
            {
                return;
            }

            using (var stream = typeof(Program).Assembly.GetManifestResourceStream(DefaultConfigResourceName))
            {
                if (stream == null)
                {
                    return;
                }

                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    var xml = reader.ReadToEnd();
                    var directory = Path.GetDirectoryName(path);

                    if (!string.IsNullOrEmpty(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }

                    File.WriteAllText(path, xml, new UTF8Encoding(false));
                }
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // 删不掉就留着，不影响功能
            }
        }

        private class ExceptionFilter : IExceptionFilter
        {
            public bool Filter(Exception ex)
            {
                // Don't filter unhandled exceptions of any type
                if (ex.Data is not null && ex.Data.Contains(Mechanism.HandledKey) && ex.Data[Mechanism.HandledKey] is false)
                {
                    return false;
                }

                if (ex is IOException && ex.Message?.Contains("HTTP") == true)
                {
                    return true;
                }

                if (ex is JsonException || ex is HttpRequestException ||
                    ex is WebException || ex is COMException ||
                    ex is FileNotFoundException ||
                    ex is TaskCanceledException || ex is TimeoutException)
                {
                    return true;
                }

                return false;
            }
        }
    }
}