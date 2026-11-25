using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

internal static class SteamVrSession
{
    public static Process FindLauncher()
    {
        using (var current = Process.GetCurrentProcess())
        {
            int parentId = FindParentId(current.Id);
            if (parentId == 0)
                return null;

            Process parent = null;
            try
            {
                parent = Process.GetProcessById(parentId);
                // SteamVR launches the registered executable directly from vrserver.
                if (string.Equals(parent.ProcessName, "vrserver", StringComparison.OrdinalIgnoreCase) &&
                    parent.SessionId == current.SessionId && parent.StartTime <= current.StartTime)
                {
                    // Keep a handle to this instance even if Windows later reuses its process ID.
                    parent.EnableRaisingEvents = true;
                    return parent;
                }
            }
            catch (Exception error) when (error is ArgumentException || error is InvalidOperationException ||
                error is Win32Exception)
            {
                // The parent may already have exited before we could open it.
            }

            parent?.Dispose();
            return null;
        }
    }

    private static int FindParentId(int processId)
    {
        const uint snapshotProcesses = 0x00000002;
        using (var snapshot = CreateToolhelp32Snapshot(snapshotProcesses, 0))
        {
            if (snapshot.IsInvalid)
                return 0;

            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf(typeof(ProcessEntry)) };
            if (Process32First(snapshot, ref entry))
            {
                do
                {
                    if (entry.ProcessId == processId)
                        return entry.ParentProcessId;
                } while (Process32Next(snapshot, ref entry));
            }
            return 0;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size;
        public uint Usage;
        public int ProcessId;
        public UIntPtr DefaultHeapId;
        public uint ModuleId;
        public uint ThreadCount;
        public int ParentProcessId;
        public int BasePriority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string Executable;
    }

    [DllImport("kernel32.dll")]
    private static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(SafeFileHandle snapshot, ref ProcessEntry entry);

    [DllImport("kernel32.dll", EntryPoint = "Process32NextW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(SafeFileHandle snapshot, ref ProcessEntry entry);
}
