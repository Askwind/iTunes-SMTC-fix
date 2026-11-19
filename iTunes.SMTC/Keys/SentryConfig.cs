namespace iTunes.SMTC.Keys
{
    /// <summary>
    /// 原仓库未提交该文件（维护者的 Sentry DSN 属于私有信息），
    /// 缺少它源码无法编译。这里返回 null，Sentry SDK 会自行禁用，不会外发任何数据。
    /// </summary>
    internal static class SentryConfig
    {
        /// <summary>
        /// 返回空字符串表示禁用 Sentry。
        /// 注意：绝不能返回 null —— Sentry 5.x 的 SettingLocator.GetDsn 遇到 null 会抛
        /// ArgumentNullException("You must supply a DSN to use Sentry. To disable Sentry,
        /// pass an empty string: \"\"")，导致程序启动即崩溃。
        /// </summary>
        internal static string GetDsn()
        {
            return string.Empty;
        }
    }
}
