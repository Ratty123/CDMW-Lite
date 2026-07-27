using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Cdmw.ArchiveLite.Standalone;

/// <summary>
/// Ties the extracted application - and every worker, decoder, and renderer it starts - to the
/// launcher process. Job membership is inherited, so closing this handle stops the whole tree and
/// force-closing the launcher cannot leave an invisible Archive Lite process behind.
/// </summary>
/// <remarks>
/// Every failure path degrades to "no job" rather than throwing: a teardown guarantee must never
/// stand between the user and a launch.
/// </remarks>
internal sealed class StandaloneJob : IDisposable
{
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private readonly SafeFileHandle _handle;

    private StandaloneJob(SafeFileHandle handle)
    {
        _handle = handle;
    }

    public static StandaloneJob? TryCreate(out Exception? failure)
    {
        var handle = CreateJobObjectW(IntPtr.Zero, null);
        if (handle.IsInvalid)
        {
            failure = new Win32Exception(Marshal.GetLastWin32Error(), "Could not create the launcher job object.");
            return null;
        }

        var limits = new JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation
            {
                LimitFlags = JobObjectLimitKillOnJobClose,
            },
        };
        if (!SetInformationJobObject(
                handle,
                JobObjectInformationClass.ExtendedLimitInformation,
                ref limits,
                (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>()))
        {
            failure = new Win32Exception(Marshal.GetLastWin32Error(), "Could not configure the launcher job object.");
            handle.Dispose();
            return null;
        }

        failure = null;
        return new StandaloneJob(handle);
    }

    public bool TryAdd(Process process, out Exception? failure)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (AssignProcessToJobObject(_handle, process.Handle))
        {
            failure = null;
            return true;
        }

        failure = new Win32Exception(
            Marshal.GetLastWin32Error(),
            "Could not assign the Archive Lite application to the launcher job object.");
        return false;
    }

    public void Dispose() => _handle.Dispose();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObjectW(IntPtr jobAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        SafeFileHandle job,
        JobObjectInformationClass informationClass,
        ref JobObjectExtendedLimitInformation information,
        uint informationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);

    private enum JobObjectInformationClass
    {
        ExtendedLimitInformation = 9,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }
}
