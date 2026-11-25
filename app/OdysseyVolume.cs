using System;
using System.ComponentModel;
using System.Drawing;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--check")
        {
            try
            {
                using (Headset.OpenButtons())
                    File.WriteAllText(args[1], "Headset buttons available. Windows volume: " +
                        Headset.WithVolume(volume => volume.GetMasterVolumeLevelScalar()).ToString("P0"));
                return 0;
            }
            catch (Exception error)
            {
                File.WriteAllText(args[1], error.ToString());
                return 1;
            }
        }

        using (var singleInstance = new Mutex(true, @"Local\OdysseyPlusVolume", out bool firstInstance))
        {
            if (!firstInstance)
                return 0;

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using (var steamVr = SteamVrSession.FindLauncher())
            using (var app = new VolumeTray(args.Length == 2 && args[0] == "--trace" ? args[1] : null, steamVr))
                Application.Run(app);
        }
        return 0;
    }
}

internal sealed class VolumeTray : ApplicationContext
{
    private const string AppName = "Odyssey+ Volume Buttons";
    private readonly NotifyIcon icon;
    private readonly Icon trayImage;
    private readonly ToolStripMenuItem statusItem;
    private readonly ToolStripMenuItem windowsStartupItem;
    private readonly ToolStripMenuItem steamVrStartupItem;
    private readonly object inputLock = new object();
    private readonly ManualResetEvent shutdown = new ManualResetEvent(false);
    private readonly Thread listener;
    private readonly System.Windows.Forms.Timer steamVrExitTimer;
    private SafeFileHandle activeInput;
    private volatile string status = "Waiting for Odyssey+";
    private volatile bool stopping;
    private bool disposed;

    private readonly string tracePath;

    public VolumeTray(string tracePath, Process steamVr)
    {
        this.tracePath = tracePath;
        statusItem = new ToolStripMenuItem(status) { Enabled = false };
        windowsStartupItem = new ToolStripMenuItem("Start at Windows sign-in", null, ToggleWindowsStartup);
        steamVrStartupItem = new ToolStripMenuItem("Start with SteamVR");
        steamVrStartupItem.DropDownItems.Add("Enable", null, async (sender, args) => await SetSteamVrStartup(true));
        steamVrStartupItem.DropDownItems.Add("Disable", null, async (sender, args) => await SetSteamVrStartup(false));

        var menu = new ContextMenuStrip();
        menu.Opening += (sender, args) =>
        {
            statusItem.Text = status;
            try
            {
                windowsStartupItem.Checked = WindowsStartup.IsEnabled();
                windowsStartupItem.Enabled = true;
                windowsStartupItem.ToolTipText = "";
            }
            catch (Exception error) when (IsStartupError(error))
            {
                windowsStartupItem.Checked = false;
                windowsStartupItem.Enabled = false;
                windowsStartupItem.ToolTipText = error.Message;
            }
        };
        menu.Items.Add(statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(windowsStartupItem);
        menu.Items.Add(steamVrStartupItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (sender, args) => ExitThread());
        trayImage = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        icon = new NotifyIcon
        {
            Icon = trayImage,
            Text = AppName,
            ContextMenuStrip = menu,
            Visible = true
        };
        listener = new Thread(Listen) { IsBackground = true, Name = "Odyssey+ HID" };
        listener.Start();

        if (steamVr != null)
        {
            steamVrExitTimer = new System.Windows.Forms.Timer { Interval = 250 };
            steamVrExitTimer.Tick += (sender, args) =>
            {
                if (steamVr.HasExited)
                    ExitThread();
            };
            steamVrExitTimer.Start();
        }
    }

    private void ToggleWindowsStartup(object sender, EventArgs args)
    {
        try
        {
            bool enable = !WindowsStartup.IsEnabled();
            WindowsStartup.SetEnabled(enable);
            windowsStartupItem.Checked = enable;
        }
        catch (Exception error) when (IsStartupError(error))
        {
            MessageBox.Show("Could not change Windows autostart.\n\n" + error.Message,
                AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private async Task SetSteamVrStartup(bool enabled)
    {
        steamVrStartupItem.Enabled = false;
        try
        {
            await Task.Run(() => SteamVrStartup.SetEnabled(Application.ExecutablePath, enabled));
            if (!disposed)
                icon.ShowBalloonTip(3000, AppName, enabled ? "SteamVR autostart enabled." :
                    "SteamVR autostart disabled.", ToolTipIcon.Info);
        }
        catch (Exception error) when (IsStartupError(error))
        {
            if (!disposed)
                MessageBox.Show("Could not change SteamVR autostart.\n\n" + error.Message,
                    AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            if (!disposed)
                steamVrStartupItem.Enabled = true;
        }
    }

    private static bool IsStartupError(Exception error)
    {
        return error is IOException || error is Win32Exception || error is UnauthorizedAccessException ||
            error is SecurityException || error is InvalidOperationException || error is ArgumentException;
    }

    private void Listen()
    {
        while (!stopping)
        {
            try
            {
                using (var input = Headset.OpenButtons())
                {
                    lock (inputLock)
                    {
                        if (stopping)
                            return;
                        activeInput = input.SafeFileHandle;
                    }
                    try
                    {
                        status = "Odyssey+ connected";
                        ReadButtons(input);
                    }
                    finally
                    {
                        lock (inputLock)
                            activeInput = null;
                    }
                }
            }
            catch (OperationCanceledException) when (stopping)
            {
                return;
            }
            catch (Exception error) when (error is IOException || error is Win32Exception ||
                error is UnauthorizedAccessException)
            {
                status = error.Message;
            }

            shutdown.WaitOne(1500);
        }
    }

    private const int FirstRepeatDelayMs = 400;
    private const int RepeatIntervalMs = 110;

    private void ReadButtons(FileStream input)
    {
        var report = new byte[2];
        Task<int> pendingRead = null;
        byte heldButton = 0;
        var clock = Stopwatch.StartNew();
        long repeatAt = 0;

        while (!stopping)
        {
            if (pendingRead == null)
            {
                lock (inputLock)
                {
                    if (stopping)
                        break;
                    pendingRead = input.ReadAsync(report, 0, report.Length);
                }
            }

            int waitMs = heldButton == 0 ? Timeout.Infinite :
                (int)Math.Max(0, repeatAt - clock.ElapsedMilliseconds);
            if (!WaitForReport(pendingRead, waitMs) && !pendingRead.IsCompleted)
            {
                if (stopping)
                    break;
                AdjustVolume(heldButton);
                repeatAt = clock.ElapsedMilliseconds + RepeatIntervalMs;
                continue;
            }

            if (pendingRead.GetAwaiter().GetResult() != report.Length || stopping)
                break;
            pendingRead = null;

            if (report[0] != 1)
                continue;

            byte button = report[1] == 1 || report[1] == 2 ? report[1] : (byte)0;
            if (button == heldButton)
                continue;

            heldButton = button;
            if (heldButton != 0)
            {
                repeatAt = clock.ElapsedMilliseconds + FirstRepeatDelayMs;
                AdjustVolume(heldButton);
            }
        }

        if (!stopping)
            status = "Waiting for Odyssey+";
    }

    private static bool WaitForReport(Task<int> pendingRead, int waitMs)
    {
        try { return pendingRead.Wait(waitMs); }
        catch (AggregateException)
        {
            // Propagate the underlying I/O or cancellation error through the listener's normal path.
            pendingRead.GetAwaiter().GetResult();
            throw;
        }
    }

    private void AdjustVolume(byte button)
    {
        try
        {
            float level = Headset.WithVolume(volume =>
            {
                if (button == 1)
                    volume.VolumeStepUp(IntPtr.Zero);
                else
                    volume.VolumeStepDown(IntPtr.Zero);

                return volume.GetMasterVolumeLevelScalar();
            });
            status = "Windows volume " + level.ToString("P0");
            if (tracePath != null)
                File.AppendAllText(tracePath, DateTime.Now.ToString("HH:mm:ss.fff") + " " +
                    (button == 1 ? "+" : "-") + " " + status + Environment.NewLine);
        }
        catch (Exception error) when (error is COMException || error is IOException)
        {
            status = error.Message;
        }
    }

    private void StopListener()
    {
        lock (inputLock)
        {
            if (stopping)
                return;
            stopping = true;
            if (activeInput != null)
                Headset.CancelRead(activeInput);
        }
        shutdown.Set();
    }

    protected override void ExitThreadCore()
    {
        steamVrExitTimer?.Stop();
        StopListener();
        icon.Visible = false;
        base.ExitThreadCore();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !disposed)
        {
            disposed = true;
            steamVrExitTimer?.Dispose();
            StopListener();
            if (listener.Join(1000))
                shutdown.Dispose();
            var menu = icon.ContextMenuStrip;
            icon.Dispose();
            menu.Dispose();
            trayImage.Dispose();
        }
        base.Dispose(disposing);
    }
}

internal static class WindowsStartup
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "OdysseyVolume";

    public static bool IsEnabled()
    {
        using (var key = Registry.CurrentUser.OpenSubKey(RunKey))
            return key?.GetValue(ValueName) != null;
    }

    public static void SetEnabled(bool enabled)
    {
        if (enabled)
        {
            string command = "\"" + Application.ExecutablePath + "\"";
            if (command.Length > 260)
                throw new IOException("Move OdysseyVolume.exe to a shorter path and try again.");

            using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
                key.SetValue(ValueName, command, RegistryValueKind.String);
        }
        else
        {
            using (var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true))
                key?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}

internal static class Headset
{
    private const string ButtonDevice = @"\\?\HID#VID_04E8&PID_7312&COL02#";
    private static readonly Guid HidInterfaceClass = new Guid("4D1E55B2-F16F-11CF-88CB-001111000030");
    private static readonly Guid EnumeratorClass = new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E");

    public static FileStream OpenButtons()
    {
        Guid interfaceClass = HidInterfaceClass;
        uint result = CM_Get_Device_Interface_List_Size(out uint length, ref interfaceClass, IntPtr.Zero, 0);
        if (result != 0)
            throw new IOException("Windows could not list HID devices (" + result + ")");

        var paths = new char[length];
        result = CM_Get_Device_Interface_List(ref interfaceClass, IntPtr.Zero, paths, length, 0);
        if (result != 0)
            throw new IOException("The HID device list changed (" + result + ")");

        foreach (string path in new string(paths).Split(new[] { '\0' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!path.StartsWith(ButtonDevice, StringComparison.OrdinalIgnoreCase))
                continue;

            var handle = CreateFile(path, 0x80000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new Win32Exception(error);
            }
            try { return new FileStream(handle, FileAccess.Read, 2, true); }
            catch
            {
                handle.Dispose();
                throw;
            }
        }

        throw new IOException("Waiting for Odyssey+");
    }

    public static void CancelRead(SafeFileHandle handle)
    {
        // A completed or already disconnected read has no pending operation to cancel.
        CancelIoEx(handle, IntPtr.Zero);
    }

    public static float WithVolume(Func<IAudioEndpointVolume, float> action)
    {
        var enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(EnumeratorClass));
        try
        {
            // Resolve the default multimedia output on every press so output switches take effect immediately.
            var endpoint = enumerator.GetDefaultAudioEndpoint(0, 1);
            try
            {
                var volume = (IAudioEndpointVolume)endpoint.Activate(typeof(IAudioEndpointVolume).GUID, 23, IntPtr.Zero);
                try { return action(volume); }
                finally { Marshal.ReleaseComObject(volume); }
            }
            finally { Marshal.ReleaseComObject(endpoint); }
        }
        finally { Marshal.ReleaseComObject(enumerator); }
    }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_Interface_List_Size(out uint length, ref Guid interfaceClass,
        IntPtr deviceId, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_Interface_List(ref Guid interfaceClass, IntPtr deviceId,
        [Out] char[] paths, uint length, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CancelIoEx(SafeFileHandle handle, IntPtr overlapped);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share,
        IntPtr security, uint disposition, uint flags, IntPtr template);

}

[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    void EnumAudioEndpoints(int dataFlow, uint stateMask, out IntPtr endpoints);
    IMMDevice GetDefaultAudioEndpoint(int dataFlow, int role);
}

[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
    [return: MarshalAs(UnmanagedType.IUnknown)]
    object Activate([MarshalAs(UnmanagedType.LPStruct)] Guid iid, uint context, IntPtr parameters);
    void OpenPropertyStore(uint access, out IntPtr store);
    [return: MarshalAs(UnmanagedType.LPWStr)]
    string GetId();
}

[ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioEndpointVolume
{
    void RegisterControlChangeNotify(IntPtr callback);
    void UnregisterControlChangeNotify(IntPtr callback);
    uint GetChannelCount();
    void SetMasterVolumeLevel(float decibels, IntPtr context);
    void SetMasterVolumeLevelScalar(float level, IntPtr context);
    float GetMasterVolumeLevel();
    float GetMasterVolumeLevelScalar();
    void SetChannelVolumeLevel(uint channel, float decibels, IntPtr context);
    void SetChannelVolumeLevelScalar(uint channel, float level, IntPtr context);
    float GetChannelVolumeLevel(uint channel);
    float GetChannelVolumeLevelScalar(uint channel);
    void SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, IntPtr context);
    [return: MarshalAs(UnmanagedType.Bool)]
    bool GetMute();
    void GetVolumeStepInfo(out uint step, out uint count);
    void VolumeStepUp(IntPtr context);
    void VolumeStepDown(IntPtr context);
}
