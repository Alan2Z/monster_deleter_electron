// find-file-rect.cs —— 在资源管理器/桌面的文件列表里找文件图标屏幕坐标
// 用法:find-file-rect.exe <文件完整路径>
// 输出:"x y"(物理像素);找不到则无输出、退出码 1。
// 通道 1:SysListView32 远程内存(桌面视图 + 老式资源管理器)
// 通道 2:UIA UIItemsView(Win10 资源管理器文件夹窗口)
// 编译:csc /nologo /optimize+ /out:build\find-file-rect.exe
//       /r:UIAutomationClient.dll /r:UIAutomationTypes.dll src\find-file-rect.cs
using System;
using System.IO;
using System.Text;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Windows.Automation;

public static class ShellWin
{
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hWnd, StringBuilder sb, int n);

    // 通道 2 只处理 Win10 文件夹窗口;桌面(Progman/WorkerW)由通道 1 的
    // SysListView32 远程内存覆盖,不进 UIA 全树搜索(那很慢)
    public static bool HasFileList(IntPtr hwnd)
    {
        var sb = new StringBuilder(64);
        GetClassName(hwnd, sb, 64);
        return sb.ToString() == "CabinetWClass";
    }

    public static void EachExplorerWindow(Action<IntPtr> act)
    {
        // 先拿 explorer PID 集合,避免对每个窗口调 GetProcessById(很慢)
        var explorerPids = new System.Collections.Generic.HashSet<int>();
        foreach (var p in Process.GetProcessesByName("explorer")) explorerPids.Add(p.Id);
        EnumWindows((hwnd, lp) =>
        {
            uint pid;
            GetWindowThreadProcessId(hwnd, out pid);
            if (!explorerPids.Contains((int)pid)) return true;
            act(hwnd);
            return true;
        }, IntPtr.Zero);
    }
}

// 通道 1:Win10 桌面 / 老式资源管理器列表视图 —— 远程内存法(跨进程指针必须在目标进程内)
public static class ListViewFinder
{
    delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr hWnd, ref POINT pt);
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr VirtualAllocEx(IntPtr proc, IntPtr addr, int size, uint type, uint protect);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool WriteProcessMemory(IntPtr proc, IntPtr addr, byte[] buf, int size, out int written);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool ReadProcessMemory(IntPtr proc, IntPtr addr, byte[] buf, int size, out int read);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool VirtualFreeEx(IntPtr proc, IntPtr addr, int size, uint type);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

    struct POINT { public int X, Y; }
    struct RECT { public int Left, Top, Right, Bottom; }

    const uint LVM_FIRST = 0x1000;
    const uint LVM_GETITEMCOUNT = LVM_FIRST + 4;
    const uint LVM_GETITEMTEXTW = LVM_FIRST + 45;
    const uint LVM_GETITEMRECT = LVM_FIRST + 14;
    const int GWL_STYLE = -16;
    const uint PROCESS_VM_OPERATION = 0x0008, PROCESS_VM_READ = 0x0010, PROCESS_VM_WRITE = 0x0020;
    const uint MEM_COMMIT = 0x1000, MEM_RESERVE = 0x2000, MEM_RELEASE = 0x8000;
    const uint PAGE_READWRITE = 0x04;

    // LVITEM x64 布局关键字段偏移
    const int OFF_IITEM = 4, OFF_PSZTEXT = 24, OFF_CCHTEXTMAX = 32;
    const int LVITEM_SIZE = 88;

    static byte[] MakeLvitemBytes(int iItem, IntPtr pszTextRemote, int cchTextMax)
    {
        byte[] b = new byte[LVITEM_SIZE];
        BitConverter.GetBytes((uint)1).CopyTo(b, 0);                      // LVIF_TEXT
        BitConverter.GetBytes(iItem).CopyTo(b, OFF_IITEM);
        BitConverter.GetBytes(pszTextRemote.ToInt64()).CopyTo(b, OFF_PSZTEXT);
        BitConverter.GetBytes(cchTextMax).CopyTo(b, OFF_CCHTEXTMAX);
        return b;
    }

    // 桌面视图返回 UTF-8 字节,普通窗口是 UTF-16:两种候选都返回
    static string[] GetItemText(IntPtr lv, IntPtr hProc, int i)
    {
        IntPtr remote = VirtualAllocEx(hProc, IntPtr.Zero, 4096, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
        if (remote == IntPtr.Zero) return null;
        try
        {
            IntPtr buf = new IntPtr(remote.ToInt64() + 512);
            byte[] lvi = MakeLvitemBytes(i, buf, 256);
            int written;
            WriteProcessMemory(hProc, remote, lvi, lvi.Length, out written);
            SendMessage(lv, LVM_GETITEMTEXTW, (IntPtr)i, remote);
            byte[] text = new byte[512];
            int read;
            ReadProcessMemory(hProc, buf, text, text.Length, out read);
            int nul = Array.IndexOf(text, (byte)0);
            if (nul < 0) nul = read;
            return new string[] { Encoding.UTF8.GetString(text, 0, nul), Encoding.Unicode.GetString(text, 0, nul) };
        }
        finally { VirtualFreeEx(hProc, remote, 0, MEM_RELEASE); }
    }

    public static string Find(string fileName)
    {
        string withoutExt = fileName.LastIndexOf('.') > 0 ? fileName.Substring(0, fileName.LastIndexOf('.')) : fileName;
        string found = null;

        // 一次拿 explorer 的 PID 集合,回调里先比 PID(避免对每个窗口调 GetProcessById,那很慢)
        var explorerPids = new System.Collections.Generic.HashSet<int>();
        foreach (var p in Process.GetProcessesByName("explorer")) explorerPids.Add(p.Id);

        EnumWindows((hwnd, lp) =>
        {
            if (found != null) return false;
            uint pid;
            GetWindowThreadProcessId(hwnd, out pid);
            if (!explorerPids.Contains((int)pid)) return true;
            Process proc;
            try { proc = Process.GetProcessById((int)pid); } catch { return true; }
            if (proc.ProcessName != "explorer") return true;

            IntPtr defView = FindWindowEx(hwnd, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (defView == IntPtr.Zero) return true;
            IntPtr lv = FindWindowEx(defView, IntPtr.Zero, "SysListView32", null);
            if (lv == IntPtr.Zero) return true;

            IntPtr hProc = OpenProcess(PROCESS_VM_OPERATION | PROCESS_VM_READ | PROCESS_VM_WRITE, false, (int)pid);
            if (hProc == IntPtr.Zero) return true;
            try
            {
                int count = SendMessage(lv, LVM_GETITEMCOUNT, IntPtr.Zero, IntPtr.Zero).ToInt32();
                for (int i = 0; i < count; i++)
                {
                    string[] names = GetItemText(lv, hProc, i);
                    bool matched = false;
                    if (names != null)
                    {
                        foreach (string name in names)
                        {
                            if (string.Equals(name, fileName, StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(name, withoutExt, StringComparison.OrdinalIgnoreCase))
                            { matched = true; break; }
                        }
                    }
                    if (matched)
                    {
                        IntPtr remote = VirtualAllocEx(hProc, IntPtr.Zero, 512, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
                        if (remote != IntPtr.Zero)
                        {
                            try
                            {
                                byte[] zero = new byte[512];
                                int w;
                                WriteProcessMemory(hProc, remote, zero, zero.Length, out w);
                                SendMessage(lv, LVM_GETITEMRECT, (IntPtr)i, remote);
                                byte[] rb = new byte[16];
                                int r;
                                ReadProcessMemory(hProc, remote, rb, rb.Length, out r);
                                int left = BitConverter.ToInt32(rb, 0), top = BitConverter.ToInt32(rb, 4);
                                int right = BitConverter.ToInt32(rb, 8), bottom = BitConverter.ToInt32(rb, 12);
                                POINT p1 = new POINT { X = left, Y = top };
                                POINT p2 = new POINT { X = right, Y = bottom };
                                ClientToScreen(lv, ref p1);
                                ClientToScreen(lv, ref p2);
                                int view = GetWindowLong(lv, GWL_STYLE) & 0x0003;
                                int cx, cy;
                                if (view == 1 || view == 3)   // 详情/列表视图:图标在第一列
                                { cx = p1.X + 20; cy = (p1.Y + p2.Y) / 2; }
                                else                          // 图标视图:取图标中心
                                { cx = (p1.X + p2.X) / 2; cy = (p1.Y + p2.Y) / 2; }
                                found = cx + " " + cy;
                            }
                            finally { VirtualFreeEx(hProc, remote, 0, MEM_RELEASE); }
                        }
                        return false;
                    }
                }
            }
            catch { }
            finally { CloseHandle(hProc); }
            return true;
        }, IntPtr.Zero);

        return found;
    }
}

// 通道 2:Win10 资源管理器文件夹窗口 —— UIA UIItemsView
public static class UiaFinder
{
    public static string Find(string fileName)
    {
        string withoutExt = fileName.LastIndexOf('.') > 0 ? fileName.Substring(0, fileName.LastIndexOf('.')) : fileName;
        string found = null;
        var lvCond = new PropertyCondition(AutomationElement.ClassNameProperty, "UIItemsView");

        ShellWin.EachExplorerWindow(hwnd =>
        {
            if (found != null) return;
            try
            {
                if (!ShellWin.HasFileList(hwnd)) return;   // 只搜真正的文件列表窗口
                var win = AutomationElement.FromHandle(hwnd);
                if (win == null) return;
                // FindFirst 匹配到即停,不深入虚拟列表子树(FindAll 会逐个物化项,很慢)
                var iv = win.FindFirst(TreeScope.Descendants, lvCond);
                if (iv == null) return;
                foreach (string name in new string[] { fileName, withoutExt })
                {
                    if (name == null) continue;
                    var item = iv.FindFirst(TreeScope.Children,
                        new PropertyCondition(AutomationElement.NameProperty, name));
                    if (item != null)
                    {
                        var r = item.Current.BoundingRectangle;
                        found = ((int)((r.Left + r.Right) / 2)).ToString() + " " +
                                ((int)((r.Top + r.Bottom) / 2)).ToString();
                        return;
                    }
                }
            }
            catch { }
        });
        return found;
    }
}

public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length == 0) return 1;
        string fileName = Path.GetFileName(args[0]);   // 匹配的是列表条目名(只有文件名)
        string result = ListViewFinder.Find(fileName);
        if (result == null) result = UiaFinder.Find(fileName);
        if (result != null) { Console.WriteLine(result); return 0; }
        return 1;
    }
}
