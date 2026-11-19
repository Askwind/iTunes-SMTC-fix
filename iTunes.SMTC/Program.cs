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

        /// <summary>
        /// 上游把 5 个用户设置的默认值放在 App.config（发布后叫 iTunes.SMTC.dll.config）。
        /// 单文件分发时这个文件很容易漏拷：读的时候有编译期默认值兜底不会出错，
        /// 但 Settings.Default.Save() 在缺少 section 声明时可能抛异常。
        /// 因此启动时检测一次，缺失就用内嵌的模板重建，做到"只拷 exe 也能用"。
        /// </summary>
        private static void EnsureAppConfig()
        {
            try
            {
                // 这是运行时真正会读取的配置文件路径，比自己拼路径可靠
                var configPath = AppContext.GetData("APP_CONFIG_FILE") as string;

                if (string.IsNullOrEmpty(configPath))
                {
                    configPath = Path.Combine(AppContext.BaseDirectory, "iTunes.SMTC.dll.config");
                }

                if (File.Exists(configPath))
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
                        var directory = Path.GetDirectoryName(configPath);

                        if (!string.IsNullOrEmpty(directory))
                        {
                            Directory.CreateDirectory(directory);
                        }

                        File.WriteAllText(configPath, xml, new UTF8Encoding(false));
                    }
                }
            }
            catch
            {
                // 自愈失败不能影响启动：缺文件时读取仍会走编译期默认值
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