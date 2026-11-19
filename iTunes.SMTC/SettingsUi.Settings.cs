#if UNPACKAGEDDEBUG || UNPACKAGEDRELEASE
using Microsoft.Win32.TaskScheduler;
using System.Reflection;
using System.Security.Principal;
#elif DEBUG || RELEASE
using Windows.ApplicationModel;
#endif

namespace iTunes.SMTC
{
    partial class SettingsUi
    {
        private const string TASK_NAME = "iTunes.SMTC";

        /// <summary>
        /// Guards against the CheckedChanged handler re-entering itself when it
        /// has to put the startup switch back to its previous state.
        /// </summary>
        private bool _suppressStartupToggle;

#if UNPACKAGEDDEBUG || UNPACKAGEDRELEASE
        private static string GetTaskName()
        {
            return $"{TASK_NAME} {WindowsIdentity.GetCurrent().Name.Replace(@"\", "")}";
        }

        /// <summary>
        /// Absolute path of the executable to put in the logon task.
        /// </summary>
        /// <remarks>
        /// Do not build this from the assembly name: in a single-file publish
        /// Assembly.Location is empty and the exe can be renamed by the user, so
        /// the old "BaseDirectory + assemblyName.exe" guess produced a path that
        /// did not exist and the logon task silently failed to start.
        /// Environment.ProcessPath reports the real image path, including for
        /// single-file bundles. It points at dotnet.exe when launched via
        /// "dotnet run", hence the fallback.
        /// </remarks>
        private static string GetExecutablePath()
        {
            var processPath = Environment.ProcessPath;

            if (!string.IsNullOrEmpty(processPath)
                && processPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(Path.GetFileName(processPath), "dotnet.exe", StringComparison.OrdinalIgnoreCase)
                && File.Exists(processPath))
            {
                return processPath;
            }

            var exeName = Assembly.GetExecutingAssembly().GetName().Name;
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, exeName + ".exe");
        }
#endif

        private void InitializeSettings()
        {
            TrackNotificationSwitch.Checked = Settings.ShowTrackToast;
            TrackNotificationSwitch.CheckedChanged += TrackNotificationSwitch_CheckedChanged;
            StartupSwitch.Checked = Settings.OpenOnStartup;
            StartupSwitch.CheckedChanged += StartupSwitch_CheckedChanged;
            iTunesSwitch.Checked = Settings.EnableiTunesController;
            iTunesSwitch.CheckedChanged += iTunesSwitch_CheckedChanged;
            AppleMusicSwitch.Checked = Settings.EnableAppleMusicController;
            AppleMusicSwitch.CheckedChanged += AppleMusicSwitch_CheckedChanged;
        }

#if UNPACKAGEDDEBUG || UNPACKAGEDRELEASE
        private void StartupSwitch_CheckedChanged(object sender, EventArgs e)
#else
        private async void StartupSwitch_CheckedChanged(object sender, EventArgs e)
#endif
        {
            if (_suppressStartupToggle) return;

            var oldValue = Settings.OpenOnStartup;
            var newValue = !oldValue;

#if UNPACKAGEDDEBUG || UNPACKAGEDRELEASE
            var ts = TaskService.Instance;

            var userId = WindowsIdentity.GetCurrent().Name;
            var taskName = GetTaskName();

            var task = ts.GetTask(taskName);
            if (task != null)
            {
                ts.RootFolder.DeleteTask(taskName);
            }

            if (newValue)
            {
                var exePath = GetExecutablePath();

                if (!File.Exists(exePath))
                {
                    // Registering a task that points nowhere would fail silently at
                    // every logon while the switch still looked "on".
                    MessageBox.Show(
                        $"Cannot find the program file:{Environment.NewLine}{exePath}{Environment.NewLine}{Environment.NewLine}Startup was not enabled.",
                        TASK_NAME, MessageBoxButtons.OK, MessageBoxIcon.Warning);

                    _suppressStartupToggle = true;
                    StartupSwitch.Checked = oldValue;
                    _suppressStartupToggle = false;

                    return;
                }

                var td = ts.NewTask();
                td.Triggers.Add(new LogonTrigger()
                {
                    UserId = userId
                });

                // WorkingDirectory matters: a logon task runs in system32 by default.
                td.Actions.Add(new ExecAction(exePath, null, Path.GetDirectoryName(exePath)));

                ts.RootFolder.RegisterTaskDefinition(taskName, td);
            }

            Settings.OpenOnStartup = newValue;
#else
            StartupTask startupTask = await StartupTask.GetAsync(TASK_NAME);

            if (newValue)
            {
                await startupTask.RequestEnableAsync();
            }
            else
            {
                startupTask.Disable();
            }

            Settings.OpenOnStartup = newValue;
#endif
        }

        private void TrackNotificationSwitch_CheckedChanged(object sender, EventArgs e)
        {
            Settings.ShowTrackToast = !Settings.ShowTrackToast;
        }

        private void iTunesSwitch_CheckedChanged(object sender, EventArgs e)
        {
            var newValue = Settings.EnableiTunesController = !Settings.EnableiTunesController;
            var key = iTunesSwitch.Tag as string;
            EnableController(key, newValue);
        }

        private void AppleMusicSwitch_CheckedChanged(object sender, EventArgs e)
        {
            var newValue = Settings.EnableAppleMusicController = !Settings.EnableAppleMusicController;
            var key = AppleMusicSwitch.Tag as string;
            EnableController(key, newValue);
        }
    }
}
