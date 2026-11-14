// SPDX-License-Identifier: GPL-3.0-or-later
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace MaPasserelle.App;

/// <summary>
/// Point d'entrée. Le Store refuse une deuxième application cachée dans le paquet : l'alias
/// « mapasserelle » appartient donc à l'application principale. Lancée avec une commande
/// (discover, mirror…), elle passe la main à la ligne de commande mapasserelle.exe du même dossier,
/// avec les mêmes entrées et sorties, et rend son code de sortie. Sans commande, c'est l'application.
/// </summary>
public static class Program
{
    private static readonly string[] CliCommands =
        ["discover", "pair", "unpair", "screens", "windows", "mirror", "version", "help", "--help", "-h"];

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 0 && CliCommands.Contains(args[0], StringComparer.OrdinalIgnoreCase))
            return CliRelay.Run(args);

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Microsoft.UI.Xaml.Application.Start(p =>
        {
            var context = new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(
                Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            new App();
        });
        return 0;
    }
}

/// <summary>Lance mapasserelle.exe avec nos flux standard et attend sa fin.</summary>
internal static class CliRelay
{
    public static int Run(string[] args)
    {
        string exe = Path.Combine(AppContext.BaseDirectory, "mapasserelle.exe");
        string commandLine = Quote(exe) + " " + string.Join(" ", args.Select(Quote));

        // Flux redirigés (Mon Estrade, tube, fichier) : on les transmet tels quels, sans console.
        // Sinon, lancé depuis une invite de commandes : on rejoint sa console, que l'enfant partage.
        IntPtr stdin = GetStdHandle(StdInput), stdout = GetStdHandle(StdOutput), stderr = GetStdHandle(StdError);
        bool redirected = IsUsable(stdout);
        var startup = new StartupInfo { cb = Marshal.SizeOf<StartupInfo>() };
        uint flags = CreateSuspended;
        IntPtr self = GetCurrentProcess();
        var duplicated = new List<IntPtr>();
        if (redirected)
        {
            startup.dwFlags = StartfUseStdHandles;
            startup.hStdInput = Inheritable(self, stdin, duplicated);
            startup.hStdOutput = Inheritable(self, stdout, duplicated);
            startup.hStdError = IsUsable(stderr) ? Inheritable(self, stderr, duplicated) : startup.hStdOutput;
            flags |= CreateNoWindow;
        }
        else
        {
            AttachConsole(AttachParentProcess);
        }

        // Job « tuer à la fermeture » : si ce relais est arrêté, la ligne de commande l'est aussi.
        IntPtr job = CreateJobObject(IntPtr.Zero, null);
        var limits = new JobExtendedLimitInformation();
        limits.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;
        int size = Marshal.SizeOf<JobExtendedLimitInformation>();
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(limits, buffer, false);
            SetInformationJobObject(job, JobObjectExtendedLimitInformation, buffer, (uint)size);
        }
        finally { Marshal.FreeHGlobal(buffer); }

        if (!CreateProcess(exe, commandLine, IntPtr.Zero, IntPtr.Zero, redirected, flags, IntPtr.Zero,
                Environment.CurrentDirectory, ref startup, out var info))
        {
            int error = Marshal.GetLastWin32Error();
            Console.Error.WriteLine($"mapasserelle.exe introuvable ou impossible à lancer : {new Win32Exception(error).Message}");
            return 1;
        }
        foreach (var h in duplicated) CloseHandle(h);
        AssignProcessToJobObject(job, info.hProcess);
        ResumeThread(info.hThread);
        CloseHandle(info.hThread);

        WaitForSingleObject(info.hProcess, Infinite);
        GetExitCodeProcess(info.hProcess, out uint code);
        CloseHandle(info.hProcess);
        CloseHandle(job);
        return (int)code;
    }

    private static bool IsUsable(IntPtr handle) =>
        handle != IntPtr.Zero && handle != new IntPtr(-1) && GetFileType(handle) != FileTypeUnknown;

    private static IntPtr Inheritable(IntPtr process, IntPtr handle, List<IntPtr> duplicated)
    {
        if (!IsUsable(handle)) return IntPtr.Zero;
        if (!DuplicateHandle(process, handle, process, out IntPtr copy, 0, true, DuplicateSameAccess)) return handle;
        duplicated.Add(copy);
        return copy;
    }

    /// <summary>Guillemets selon les règles de CommandLineToArgvW.</summary>
    private static string Quote(string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny([' ', '\t', '"']) < 0) return arg;
        var sb = new System.Text.StringBuilder("\"");
        int backslashes = 0;
        foreach (char c in arg)
        {
            if (c == '\\') { backslashes++; continue; }
            if (c == '"') sb.Append('\\', backslashes * 2 + 1);
            else sb.Append('\\', backslashes);
            backslashes = 0;
            sb.Append(c);
        }
        sb.Append('\\', backslashes * 2).Append('"');
        return sb.ToString();
    }

    private const int StdInput = -10, StdOutput = -11, StdError = -12;
    private const uint AttachParentProcess = 0xFFFFFFFF, Infinite = 0xFFFFFFFF;
    private const uint CreateSuspended = 0x4, CreateNoWindow = 0x08000000, StartfUseStdHandles = 0x100;
    private const uint DuplicateSameAccess = 0x2, FileTypeUnknown = 0;
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb; public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute;
        public uint dwFlags; public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobBasicLimitInformation
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit; public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize; public uint ActiveProcessLimit;
        public UIntPtr Affinity; public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters { public ulong a, b, c, d, e, f; }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobExtendedLimitInformation
    {
        public JobBasicLimitInformation BasicLimitInformation; public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GetStdHandle(int n);
    [DllImport("kernel32.dll")] private static extern uint GetFileType(IntPtr h);
    [DllImport("kernel32.dll")] private static extern bool AttachConsole(uint pid);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DuplicateHandle(IntPtr sp, IntPtr sh, IntPtr tp, out IntPtr th, uint access, bool inherit, uint options);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcess(string app, string cmd, IntPtr pa, IntPtr ta, bool inherit, uint flags,
        IntPtr env, string dir, ref StartupInfo si, out ProcessInformation pi);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateJobObject(IntPtr a, string? name);
    [DllImport("kernel32.dll")] private static extern bool SetInformationJobObject(IntPtr job, int cls, IntPtr info, uint len);
    [DllImport("kernel32.dll")] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll")] private static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr h, uint ms);
    [DllImport("kernel32.dll")] private static extern bool GetExitCodeProcess(IntPtr h, out uint code);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
}
