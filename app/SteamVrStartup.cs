using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Web.Script.Serialization;
using Microsoft.Win32;

internal static class SteamVrStartup
{
    private const string AppKey = "odysseyvolume.volumebuttons";
    private const string ApplicationsInterface = "FnTable:IVRApplications_008";
    private const int UtilityApplication = 4;
    private const int NoManifest = 101;
    private const int InvalidManifest = 107;

    private static string ManifestPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OdysseyVolume", "OdysseyVolume.vrmanifest");

    public static bool HasManifest => File.Exists(ManifestPath);

    public static bool IsEnabled()
    {
        // With no app manifest there is no need to start the OpenVR utility runtime.
        return HasManifest && WithApplications(applications =>
        {
            byte[] key = Utf8(AppKey);
            return Slot<TestFlag>(applications, 2)(key) && Slot<TestFlag>(applications, 18)(key);
        });
    }

    public static void SetEnabled(string executable, bool enabled)
    {
        WithApplications(applications =>
        {
            UpdateManifest(applications, executable, ManifestPath, enabled);
            return true;
        });
    }

    private static T WithApplications<T>(Func<IntPtr, T> action)
    {
        const uint searchDllDirectory = 0x00000100;
        const uint searchSystem32 = 0x00000800;
        IntPtr library = LoadLibraryEx(FindOpenVrLibrary(), IntPtr.Zero, searchDllDirectory | searchSystem32);
        if (library == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error());

        try
        {
            var init = Export<Init>(library, "VR_InitInternal");
            var shutdown = Export<Shutdown>(library, "VR_ShutdownInternal");
            var getInterface = Export<GetInterface>(library, "VR_GetGenericInterface");
            init(out int error, UtilityApplication);
            CheckResult(error, "connect");
            try
            {
                IntPtr applications = getInterface(ApplicationsInterface, out error);
                CheckResult(error, "open the applications interface");
                if (applications == IntPtr.Zero)
                    throw new InvalidOperationException("The SteamVR applications interface is unavailable.");

                return action(applications);
            }
            finally
            {
                shutdown();
            }
        }
        finally
        {
            FreeLibrary(library);
        }
    }

    private static void UpdateManifest(IntPtr applications, string executable, string manifestPath, bool enabled)
    {
        // Slot indices from Valve's VR_IVRApplications_FnTable for IVRApplications_008.
        var addManifest = Slot<ChangeFlag>(applications, 0);
        var removeManifest = Slot<PathOperation>(applications, 1);
        var isInstalled = Slot<TestFlag>(applications, 2);
        var setAutoLaunch = Slot<ChangeFlag>(applications, 17);
        byte[] path = Utf8(manifestPath);
        byte[] key = Utf8(AppKey);

        if (!enabled)
        {
            if (isInstalled(key))
                CheckResult(setAutoLaunch(key, false), "disable autostart");
            RemoveManifest(removeManifest, path);
            if (File.Exists(manifestPath))
                File.Delete(manifestPath);
            return;
        }

        string manifest = new JavaScriptSerializer().Serialize(new
        {
            applications = new[]
            {
                new
                {
                    app_key = AppKey,
                    launch_type = "binary",
                    binary_path_windows = executable,
                    is_dashboard_overlay = true,
                    strings = new
                    {
                        en_us = new { name = "Odyssey+ Volume Buttons" },
                    },
                },
            },
        });

        if (!File.Exists(manifestPath) || File.ReadAllText(manifestPath) != manifest)
        {
            // Refresh the registered executable path if the user moved the app.
            if (isInstalled(key))
                RemoveManifest(removeManifest, path);
            Directory.CreateDirectory(Path.GetDirectoryName(manifestPath));
            File.WriteAllText(manifestPath, manifest);
        }

        CheckResult(addManifest(path, false), "register the app");
        CheckResult(setAutoLaunch(key, true), "enable autostart");
    }

    private static void RemoveManifest(PathOperation removeManifest, byte[] path)
    {
        int error = removeManifest(path);
        // SteamVR returns InvalidManifest when the path has not been registered.
        if (error != NoManifest && error != InvalidManifest)
            CheckResult(error, "unregister the app");
    }

    private static string FindOpenVrLibrary()
    {
        const string uninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Steam App 250820";
        string architecture = Environment.Is64BitProcess ? "win64" : "win32";
        foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using (var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
            using (var steamVr = machine.OpenSubKey(uninstallKey))
            {
                string location = steamVr?.GetValue("InstallLocation") as string;
                if (string.IsNullOrEmpty(location))
                    continue;

                string library = Path.Combine(location, "bin", architecture, "openvr_api.dll");
                if (File.Exists(library))
                    return library;
            }
        }
        throw new InvalidOperationException("Install SteamVR through Steam and try again.");
    }

    private static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value + "\0");

    private static void CheckResult(int error, string action)
    {
        if (error != 0)
            throw new InvalidOperationException("SteamVR could not " + action + " (error " + error + ").");
    }

    private static T Export<T>(IntPtr library, string name) where T : Delegate
    {
        IntPtr address = GetProcAddress(library, name);
        if (address == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return Marshal.GetDelegateForFunctionPointer<T>(address);
    }

    private static T Slot<T>(IntPtr table, int index) where T : Delegate
    {
        return Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(table, index * IntPtr.Size));
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint Init(out int error, int applicationType);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void Shutdown();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr GetInterface(string name, out int error);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int PathOperation(byte[] path);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ChangeFlag(byte[] pathOrKey, [MarshalAs(UnmanagedType.I1)] bool value);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool TestFlag(byte[] key);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr module, string name);

    [DllImport("kernel32.dll")]
    private static extern bool FreeLibrary(IntPtr module);
}
