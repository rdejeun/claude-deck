using System.Runtime.InteropServices;
using System.Text;

namespace ClaudeDeck.App;

/// <summary>Appels Win32 utilisés pour identifier le processus client et mettre sa fenêtre au premier plan.</summary>
internal static class Native
{
    // ───────────── Table TCP (iphlpapi) ─────────────

    private const int AF_INET = 2;
    private const int TCP_TABLE_OWNER_PID_ALL = 5;
    private const uint ERROR_INSUFFICIENT_BUFFER = 122;

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_TCPROW_OWNER_PID
    {
        public uint State;
        public uint LocalAddr;
        public uint LocalPort;
        public uint RemoteAddr;
        public uint RemotePort;
        public uint OwningPid;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int pdwSize, bool bOrder, int ulAf, int tableClass, uint reserved);

    /// <summary>
    /// Retrouve le PID propriétaire de la connexion IPv4 dont le port local est <paramref name="clientPort"/>
    /// et le port distant <paramref name="serverPort"/> : c'est le processus qui s'est connecté à notre serveur.
    /// </summary>
    public static int? FindTcpClientPid(int clientPort, int serverPort)
    {
        var size = 0;
        var ret = GetExtendedTcpTable(IntPtr.Zero, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                ret = GetExtendedTcpTable(buffer, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);
                if (ret == ERROR_INSUFFICIENT_BUFFER) continue;
                if (ret != 0) return null;

                var count = Marshal.ReadInt32(buffer);
                var rowPtr = buffer + 4;
                var rowSize = Marshal.SizeOf<MIB_TCPROW_OWNER_PID>();
                for (var i = 0; i < count; i++)
                {
                    var row = Marshal.PtrToStructure<MIB_TCPROW_OWNER_PID>(rowPtr + i * rowSize);
                    if (Port(row.LocalPort) == clientPort && Port(row.RemotePort) == serverPort)
                        return (int)row.OwningPid;
                }
                return null;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        return null;
    }

    /// <summary>Les ports sont stockés en ordre réseau dans les 16 bits de poids faible.</summary>
    private static int Port(uint raw) => (int)(((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF));

    // ───────────── Processus (Toolhelp) ─────────────

    private const uint TH32CS_SNAPPROCESS = 0x00000002;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32FirstW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32NextW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    public readonly record struct ProcEntry(int Pid, int ParentPid, string ExeName);

    /// <summary>Instantané de tous les processus : PID → (parent, exécutable).</summary>
    public static Dictionary<int, ProcEntry> SnapshotProcesses()
    {
        var result = new Dictionary<int, ProcEntry>();
        var snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return result;
        try
        {
            var e = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            if (!Process32FirstW(snap, ref e)) return result;
            do
            {
                result[(int)e.th32ProcessID] = new ProcEntry((int)e.th32ProcessID, (int)e.th32ParentProcessID, e.szExeFile);
            } while (Process32NextW(snap, ref e));
        }
        finally
        {
            CloseHandle(snap);
        }
        return result;
    }

    // ───────────── Fenêtres ─────────────

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLengthW(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);

    private const uint GW_OWNER = 4;
    private const int DWMWA_CLOAKED = 14;

    public readonly record struct WindowInfo(IntPtr Handle, int Pid, string Title);

    /// <summary>Fenêtres principales visibles (ordre Z : la plus récemment active d'abord).</summary>
    public static List<WindowInfo> TopLevelWindows()
    {
        var list = new List<WindowInfo>();
        EnumWindowsProc callback = (h, _) =>
        {
            if (!IsWindowVisible(h) || GetWindow(h, GW_OWNER) != IntPtr.Zero) return true;
            var len = GetWindowTextLengthW(h);
            if (len == 0) return true;
            if (DwmGetWindowAttribute(h, DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
            var sb = new StringBuilder(len + 1);
            GetWindowTextW(h, sb, sb.Capacity);
            GetWindowThreadProcessId(h, out var pid);
            list.Add(new WindowInfo(h, (int)pid, sb.ToString()));
            return true;
        };
        EnumWindows(callback, IntPtr.Zero);
        GC.KeepAlive(callback);
        return list;
    }

    // ───────────── Premier plan ─────────────

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    private const int SW_RESTORE = 9;

    /// <summary>
    /// Met une fenêtre au premier plan malgré les restrictions de Windows sur le vol de focus
    /// (un processus en arrière-plan n'y a normalement pas droit) : essai direct, puis rattachement à la file
    /// d'entrée du premier plan. Pas de saisie clavier synthétique (heuristique « keylogger » des antivirus) :
    /// en cas de refus, Windows fait clignoter le bouton de la fenêtre dans la barre des tâches.
    /// </summary>
    public static bool Activate(IntPtr hwnd)
    {
        if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
        if (SetForegroundWindow(hwnd) && GetForegroundWindow() == hwnd) return true;

        var fg = GetForegroundWindow();
        var fgThread = GetWindowThreadProcessId(fg, out _);
        var me = GetCurrentThreadId();
        if (fgThread != 0 && fgThread != me && AttachThreadInput(me, fgThread, true))
        {
            try
            {
                BringWindowToTop(hwnd);
                SetForegroundWindow(hwnd);
            }
            finally
            {
                AttachThreadInput(me, fgThread, false);
            }
            if (GetForegroundWindow() == hwnd) return true;
        }
        return false;
    }

    // ───────────── Console (commandes en ligne d'une application WinExe) ─────────────

    [DllImport("kernel32.dll")]
    public static extern bool AttachConsole(int dwProcessId);

    public const int ATTACH_PARENT_PROCESS = -1;
}
