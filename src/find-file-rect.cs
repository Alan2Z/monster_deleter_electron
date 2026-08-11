// find-file-rect.cs —— 在资源管理器/桌面的文件列表里找文件图标屏幕坐标
// 用法:find-file-rect.exe <文件完整路径>
// 输出:"x y"(物理像素);找不到则无输出、退出码 1。
// 通道 1:SysListView32 远程内存(桌面视图 + 老式资源管理器)
// 通道 2:UIA UIItemsView(Win10 资源管理器文件夹窗口)
// 编译:csc /nologo /optimize+ /out:build\find-file-rect.exe
//       /r:UIAutomationClient.dll /r:UIAutomationTypes.dll src\find-file-rect.cs
//       (UIA 引用在 Framework 目录的 WPF 子目录,csc 用 /lib: 指过去)
// DPI 说明:不带 manifest 的 csc 程序默认是 DPI 非感知进程,高缩放屏(125%/150%)
// 上 ClientToScreen/UIA 返回的坐标会被系统虚拟化到 96 DPI 空间,整体缩小导致
// 瞄准偏差;所以 Main 一进来就主动声明 per-monitor DPI 感知(见 Dpi 类),
// 保证这里返回的真是物理像素,与 Electron 端按 scaleFactor 的换算一致。
using System;
using System.IO;
using System.Text;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Windows.Automation;

// DPI 感知声明:必须在创建任何窗口/调用任何坐标 API 之前执行。
// 依次尝试 PerMonitorV2(Win10 1703+)→ per-monitor(Win8.1+)→ system DPI aware(老系统),
// 全部成功后坐标 API 返回的都是真实物理像素,不再被系统虚拟化。
public static class Dpi
{
    [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("shcore.dll")] static extern int SetProcessDpiAwareness(int value);
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

    const int PROCESS_PER_MONITOR_DPI_AWARE = 2;

    public static string MakeAware()   // 返回命中的感知模式(诊断用)
    {
        if (SetProcessDpiAwarenessContext((IntPtr)(-4))) return "PerMonitorV2";   // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2
        if (SetProcessDpiAwareness(PROCESS_PER_MONITOR_DPI_AWARE) == 0) return "PerMonitor";
        if (SetProcessDPIAware()) return "SystemDPI";
        return "Unaware";
    }
}

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
    const uint LVIR_BOUNDS = 0, LVIR_ICON = 1;   // LVM_GETITEMRECT 的 wParam:整行 / 图标矩形
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

    // 桌面视图是 LVS_OWNERDATA 虚拟列表,explorer 的 ownerdata 处理器把文本按
    // 系统 ANSI 代码页写进缓冲区(中文系统=GBK,UTF-8 系统=UTF-8),不是 W 消息
    // 契约的 UTF-16。所以三个解码候选都要试:UTF-8 / UTF-16 / 系统 ANSI。
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
            return new string[] {
                StripBom(Encoding.UTF8.GetString(text, 0, nul)),
                StripBom(Encoding.Unicode.GetString(text, 0, nul)),
                StripBom(Encoding.Default.GetString(text, 0, nul)),   // 系统 ANSI 代码页(中文系统=GBK)
            };
        }
        finally { VirtualFreeEx(hProc, remote, 0, MEM_RELEASE); }
    }

    // 剥掉 UTF-8 BOM 残留的 U+FEFF(带 BOM 的 UTF-8 经 GetString 会留在开头,导致比对失败)
    static string StripBom(string s)
    {
        return s != null && s.Length > 0 && s[0] == '\uFEFF' ? s.Substring(1) : s;
    }

    public static string LastStatus = "";   // 失败阶段诊断(远程排查用)

    // 诊断用转义:引号/反斜杠/控制符换成可读形式,中文按 UTF-8 原样输出
    public static string Esc(string s)
    {
        if (s == null) return "null";
        var sb = new StringBuilder();
        foreach (char c in s)
        {
            if (c == '"') sb.Append("\\\"");
            else if (c == '\\') sb.Append("\\\\");
            else if (c == '\r') sb.Append("\\r");
            else if (c == '\n') sb.Append("\\n");
            else if (c < 0x20) sb.Append('.');   // 不可见控制符
            else sb.Append(c);
        }
        return sb.ToString();
    }

    // 读取第 item 项的图标中心(屏幕坐标,物理像素);失败返回 null。
    // LVM_GETITEMRECT 的怪癖:索引放在 RECT.left,不是 wParam!
    // (ListView_GetItemRect 宏就是先把 left=索引再发消息;wParam 是 LVIR_* 标志)
    static POINT? GetItemScreenRect(IntPtr lv, IntPtr hProc, IntPtr remote, int item)
    {
        byte[] zero = new byte[512];
        byte[] rb = new byte[16];
        int w, r;
        int left = 0, top = 0, right = 0, bottom = 0;
        bool iconRect = false;
        byte[] idx = new byte[512];
        BitConverter.GetBytes(item).CopyTo(idx, 0);
        WriteProcessMemory(hProc, remote, idx, idx.Length, out w);
        if (SendMessage(lv, LVM_GETITEMRECT, (IntPtr)LVIR_ICON, remote).ToInt32() != 0)
        {
            ReadProcessMemory(hProc, remote, rb, rb.Length, out r);
            left = BitConverter.ToInt32(rb, 0); top = BitConverter.ToInt32(rb, 4);
            right = BitConverter.ToInt32(rb, 8); bottom = BitConverter.ToInt32(rb, 12);
            if (right > left && bottom > top) iconRect = true;
        }
        if (!iconRect)   // 兜底:整行矩形(老系统/无图标矩形)
        {
            WriteProcessMemory(hProc, remote, zero, zero.Length, out w);
            SendMessage(lv, LVM_GETITEMRECT, (IntPtr)LVIR_BOUNDS, remote);
            ReadProcessMemory(hProc, remote, rb, rb.Length, out r);
            left = BitConverter.ToInt32(rb, 0); top = BitConverter.ToInt32(rb, 4);
            right = BitConverter.ToInt32(rb, 8); bottom = BitConverter.ToInt32(rb, 12);
        }
        POINT p1 = new POINT { X = left, Y = top };
        POINT p2 = new POINT { X = right, Y = bottom };
        ClientToScreen(lv, ref p1);
        ClientToScreen(lv, ref p2);
        int cx = (p1.X + p2.X) / 2, cy = (p1.Y + p2.Y) / 2;
        if (iconRect) return new POINT { X = cx, Y = cy };
        int view = GetWindowLong(lv, GWL_STYLE) & 0x0003;
        if (view == 1 || view == 3)   // 详情/列表视图:图标在第一列
        { return new POINT { X = p1.X + 20, Y = (p1.Y + p2.Y) / 2 }; }
        return new POINT { X = cx, Y = cy };
    }

    public static string Find(string fileName)
    {
        string withoutExt = fileName.LastIndexOf('.') > 0 ? fileName.Substring(0, fileName.LastIndexOf('.')) : fileName;
        string found = null;
        LastStatus = "no-explorer-window";

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
            if (defView == IntPtr.Zero) { if (LastStatus == "no-explorer-window") LastStatus = "no-defview"; return true; }
            IntPtr lv = FindWindowEx(defView, IntPtr.Zero, "SysListView32", null);
            if (lv == IntPtr.Zero) { LastStatus = "no-syslistview32"; return true; }

            IntPtr hProc = OpenProcess(PROCESS_VM_OPERATION | PROCESS_VM_READ | PROCESS_VM_WRITE, false, (int)pid);
            if (hProc == IntPtr.Zero) { LastStatus = "open-process-denied"; return true; }
            try
            {
                int count = SendMessage(lv, LVM_GETITEMCOUNT, IntPtr.Zero, IntPtr.Zero).ToInt32();
                if (count <= 0) { LastStatus = "list-empty"; return true; }
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
                        LastStatus = "found-item-" + i;
                        IntPtr remote = VirtualAllocEx(hProc, IntPtr.Zero, 512, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
                        if (remote != IntPtr.Zero)
                        {
                            try
                            {
                                // 对照:item0 与命中项各取一次矩形,若两者相同说明
                                // 这台机器忽略了索引(永远返回第 0 项),r0= 字段用于远程判断
                                var rect0 = GetItemScreenRect(lv, hProc, remote, 0);
                                var rect = GetItemScreenRect(lv, hProc, remote, i);
                                if (rect != null)
                                {
                                    LastStatus = "found-item-" + i +
                                        (rect0 != null ? " r0=" + rect0.Value.X + "," + rect0.Value.Y : " r0=null") +
                                        " ri=" + rect.Value.X + "," + rect.Value.Y;
                                    found = rect.Value.X + " " + rect.Value.Y;
                                }
                            }
                            finally { VirtualFreeEx(hProc, remote, 0, MEM_RELEASE); }
                        }
                        return false;
                    }
                }
                if (count > 0)
                {
                    // 没匹配时把现场dump进状态,下一条日志就能看出文本是空/乱码/编码问题
                    int style = GetWindowLong(lv, GWL_STYLE);
                    string dbg = "no-name-match count=" + count + " style=0x" + (style & 0xFFFF).ToString("x4");
                    if ((style & 0x1000) != 0) dbg += " OWNERDATA";   // LVS_OWNERDATA:虚拟列表
                    int dumpN = Math.Min(3, count);
                    for (int d = 0; d < dumpN; d++)
                    {
                        string[] nm = GetItemText(lv, hProc, d);
                        if (nm != null) dbg += " item" + d + "u16=\"" + Esc(nm[1]) + "\"u8=\"" + Esc(nm[0]) + "\"ansi=\"" + Esc(nm[2]) + "\"";
                        else dbg += " item" + d + "null";
                    }
                    LastStatus = dbg;
                }
            }
            catch { LastStatus = "listview-error"; }
            finally { CloseHandle(hProc); }
            return true;
        }, IntPtr.Zero);

        return found;
    }
}

// UIA 坐标空间自校准。
// 实测发现:UIA 的 BoundingRectangle 在某些机器上返回物理像素、另一些机器上
// 返回 DIP(虚拟化)坐标,与客户端 DPI 感知无关(provider 自身行为)。猜不可靠
// (启发式曾因此误伤),改为当场校准:物理虚拟屏尺寸 ÷ UIA 根矩形尺寸。
// 比值 ≈1 = 物理坐标(不乘),≈缩放倍数 = DIP 坐标(乘回去)。
public static class UiaCal
{
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
    const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77, SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;

    public static double factor = 1.0;

    public static void Calibrate()
    {
        factor = 1.0;
        try
        {
            int vw = GetSystemMetrics(SM_CXVIRTUALSCREEN), vh = GetSystemMetrics(SM_CYVIRTUALSCREEN);
            if (vw <= 0 || vh <= 0) return;
            var r = AutomationElement.RootElement.Current.BoundingRectangle;
            if (r.Width <= 0 || r.Height <= 0) return;
            double fx = vw / r.Width, fy = vh / r.Height;
            factor = (fx + fy) / 2.0;              // 两轴应一致,取平均
            if (factor < 0.9 || factor > 4.0) factor = 1.0;   // 异常值兜底
        }
        catch { factor = 1.0; }
    }

    // UIA 坐标 → 物理像素(UIA 空间中心点 × 因子)
    public static string Phys(double cx, double cy)
    {
        return ((int)(cx * factor)) + " " + ((int)(cy * factor));
    }
}

// 通道 1:桌面图标 —— UIA 桌面窗格搜索(首选,位置可靠)。
// 为什么不用 SysListView32 的 LVM_GETITEMRECT:实测返回的位置可能是过期层
// 的坐标(桌面有重复/隐藏列表层时,读到的矩形和真实图标位置对不上)。
// UIA 的 BoundingRectangle 来自 shell 自身的 IShellView,和屏幕显示一致。
// 桌面图标一般在:RootElement → "Program Manager"/"Desktop 1" Pane → List → ListItem
public static class DesktopUiaFinder
{
    public static string LastStatus = "";

    static string Center(AutomationElement item)
    {
        var r = item.Current.BoundingRectangle;
        if (r.Width <= 0 || r.Height <= 0) return null;
        return UiaCal.Phys((r.Left + r.Right) / 2.0, (r.Top + r.Bottom) / 2.0);
    }

    static string FindIn(AutomationElement container, string fileName, string withoutExt)
    {
        if (container == null) return null;
        foreach (string name in new string[] { fileName, withoutExt })
        {
            if (name == null) continue;
            var item = container.FindFirst(TreeScope.Children,
                new PropertyCondition(AutomationElement.NameProperty, name));
            if (item != null)
            {
                string c = Center(item);
                if (c != null) return c;
            }
        }
        return null;
    }

    public static string Find(string fileName, string withoutExt)
    {
        LastStatus = "no-desktop-pane";
        try
        {
            LastStatus = "step1";
            var root = AutomationElement.RootElement;
            LastStatus = "step2";
            var panes = root.FindAll(TreeScope.Children,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Pane));
            LastStatus = "step3";
            if (panes != null)
            {
                foreach (AutomationElement pane in panes)
                {
                    LastStatus = "step4";
                    string pn = null;
                    try { pn = pane.Current.Name; } catch { continue; }
                    bool isDesktop = pn != null && (pn.StartsWith("Desktop", StringComparison.OrdinalIgnoreCase) ||
                                                    pn.StartsWith("Program Manager", StringComparison.OrdinalIgnoreCase));
                    if (!isDesktop) continue;
                    LastStatus = "step5";
                    var list = pane.FindFirst(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.List));
                    LastStatus = "step6";
                    string r = FindIn(list, fileName, withoutExt);
                    if (r == null) r = FindIn(pane, fileName, withoutExt);
                    LastStatus = "step7";
                    if (r != null) { LastStatus = "found-desktop-pane"; return r; }
                }
            }
            LastStatus = "no-name-match";
        }
        catch (Exception ex) { LastStatus = "uia-error@" + LastStatus + ":" + (ex.Message.Length > 80 ? ex.Message.Substring(0, 80) : ex.Message); }
        return null;
    }
}

// 通道 2:Win10/Win11 资源管理器文件夹窗口 —— UIA
public static class UiaFinder
{
    public static string LastVia = "";       // 命中的容器类型(诊断用):UIItemsView / ListControl
    public static string LastStatus = "";    // 失败阶段诊断(远程排查用)
    // 在容器内按条目名找文件,命中返回屏幕中心坐标字符串(物理像素)
    static string FindNamedItem(AutomationElement container, string fileName, string withoutExt)
    {
        if (container == null) return null;
        foreach (string name in new string[] { fileName, withoutExt })
        {
            if (name == null) continue;
            var item = container.FindFirst(TreeScope.Children,
                new PropertyCondition(AutomationElement.NameProperty, name));
            if (item != null)
            {
                var r = item.Current.BoundingRectangle;
                return UiaCal.Phys((r.Left + r.Right) / 2.0, (r.Top + r.Bottom) / 2.0);
            }
        }
        return null;
    }

    public static string Find(string fileName)
    {
        string withoutExt = fileName.LastIndexOf('.') > 0 ? fileName.Substring(0, fileName.LastIndexOf('.')) : fileName;
        string found = null;
        LastStatus = "no-cabinet-window";

        ShellWin.EachExplorerWindow(hwnd =>
        {
            if (found != null) return;
            try
            {
                if (!ShellWin.HasFileList(hwnd)) return;   // 只搜真正的文件列表窗口
                LastStatus = "cabinet-no-list";
                var win = AutomationElement.FromHandle(hwnd);
                if (win == null) return;
                // 快路径:经典 UIItemsView(Win10 / 多数 Win11 都还是这个类名)
                var iv = win.FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ClassNameProperty, "UIItemsView"));
                if (iv != null)
                {
                    found = FindNamedItem(iv, fileName, withoutExt);
                    if (found != null) { LastVia = "UIItemsView"; return; }
                }
                // 通用路径:新版资源管理器的列表控件类名可能不同,按控件类型找
                // (FileList 是窗口里最大的 List/DataGrid,别的列表(导航栏等)都比它小)
                var listCond = new OrCondition(
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.List),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.DataGrid));
                var lists = win.FindAll(TreeScope.Descendants, listCond);
                if (lists != null && lists.Count > 0)
                {
                    AutomationElement best = null;
                    double maxArea = 0;
                    foreach (AutomationElement el in lists)
                    {
                        var r = el.Current.BoundingRectangle;
                        double area = r.Width * r.Height;
                        if (area > maxArea) { maxArea = area; best = el; }
                    }
                    found = FindNamedItem(best, fileName, withoutExt);
                    if (found != null) LastVia = "ListControl";
                    else LastStatus = "no-name-match";
                }
                else LastStatus = "no-list-control";
            }
            catch { LastStatus = "uia-error"; }
        });
        return found;
    }
}

// 通道 3:全局 UIA 搜索兜底(参考项目 tests/test_uiauto.py 的思路)。
// 通道 1/2 都失败时在整棵 UIA 树上找同名 ListItem——桌面整理软件(腾讯桌面
// 整理等)接管了桌面图标、新版资源管理器结构变动等情况,图标可能出现在
// 任何位置,全局搜一遍兜底。慢(数百 ms ~ 数秒),但只在前面都失败才跑。
public static class GlobalUiaFinder
{
    public static string LastStatus = "";

    // 收窄搜索:桌面窗格(Desktop pane)下的图标列表——全树搜索在 Win11 25H2
    // 上会抛 UIA 异常,小范围又快又稳。桌面图标一般在
    // RootElement → "Desktop 1" Pane → List → ListItem。
    static string FindInList(AutomationElement container, string fileName, string withoutExt)
    {
        if (container == null) return null;
        foreach (string name in new string[] { fileName, withoutExt })
        {
            if (name == null) continue;
            var item = container.FindFirst(TreeScope.Children,
                new PropertyCondition(AutomationElement.NameProperty, name));
            if (item != null)
            {
                var r = item.Current.BoundingRectangle;
                if (r.Width > 0 && r.Height > 0)
                {
                    return UiaCal.Phys((r.Left + r.Right) / 2.0, (r.Top + r.Bottom) / 2.0);
                }
            }
        }
        return null;
    }

    public static string Find(string fileName, string withoutExt)
    {
        LastStatus = "no-match";
        try
        {
            var root = AutomationElement.RootElement;
            // 1) 桌面窗格收窄搜索
            var panes = root.FindAll(TreeScope.Children,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Pane));
            if (panes != null)
            {
                foreach (AutomationElement pane in panes)
                {
                    string pn = null;
                    try { pn = pane.Current.Name; } catch { continue; }
                    if (pn == null || !pn.StartsWith("Desktop", StringComparison.OrdinalIgnoreCase)) continue;
                    var list = pane.FindFirst(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.List));
                    if (list == null) continue;
                    string r = FindInList(list, fileName, withoutExt);
                    if (r != null) { LastStatus = "found-desktop-pane"; return r; }
                }
            }
            // 2) 全树兜底(超时由 Main 的 5s join 兜住)
            foreach (string name in new string[] { fileName, withoutExt })
            {
                if (name == null) continue;
                var cond = new AndCondition(
                    new PropertyCondition(AutomationElement.NameProperty, name),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem));
                var item = root.FindFirst(TreeScope.Descendants, cond);
                if (item != null)
                {
                    var r = item.Current.BoundingRectangle;
                    if (r.Width > 0 && r.Height > 0)
                    {
                        LastStatus = "found-global";
                        return UiaCal.Phys((r.Left + r.Right) / 2.0, (r.Top + r.Bottom) / 2.0);
                    }
                }
            }
        }
        catch { LastStatus = "uia-error"; }
        return null;
    }
}

public static class Program
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHGetFolderPathW(IntPtr hwnd, int csidl, IntPtr token, uint flags, System.Text.StringBuilder path);
    const int CSIDL_DESKTOPDIRECTORY = 0x0010;
    const int CSIDL_COMMON_DESKTOPDIRECTORY = 0x0019;

    // 文件是否在桌面上(用户桌面含 OneDrive 重定向 + 公共桌面)。
    // 通道 1 只扫桌面图标列表,文件不在桌面上却先跑它,会误匹配到桌面上
    // 同名的图标(例如文件夹窗口里也叫"新建文件夹"的文件)——必须先做门禁。
    public static bool IsOnDesktop(string file)
    {
        try
        {
            string dir = Path.GetDirectoryName(file);
            if (dir == null) return false;
            foreach (int csidl in new int[] { CSIDL_DESKTOPDIRECTORY, CSIDL_COMMON_DESKTOPDIRECTORY })
            {
                var sb = new System.Text.StringBuilder(512);
                if (SHGetFolderPathW(IntPtr.Zero, csidl, IntPtr.Zero, 0, sb) == 0 &&
                    string.Equals(dir.TrimEnd('\\'), sb.ToString().TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch { }
        return false;
    }

    public static int Main(string[] args)
    {
        if (args.Length == 0) return 1;
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }   // 诊断行不乱码
        string dpiMode = Dpi.MakeAware();            // 必须先于任何坐标 API,见文件头注释
        string fileName = Path.GetFileName(args[0]); // 匹配的是列表条目名(只有文件名)
        string withoutExt = fileName.LastIndexOf('.') > 0 ? fileName.Substring(0, fileName.LastIndexOf('.')) : fileName;

        // 全局 UIA 搜索在某些机器(Win11 25H2 等)上会整进程卡死——主进程 8s 超时
        // 杀掉,日志表现为 exit=n/a。所以三通道全放后台线程 + 硬超时 5s:卡住就
        // 放弃、正常退出(后台线程不阻止进程退出),最多拖 5 秒而不是被杀。
        string result = null;
        string diag = "";
        var worker = new System.Threading.Thread(() =>
        {
            UiaCal.Calibrate();   // 校准与检索都在同一线程做 UIA,避免跨线程 UIA 树异常
            string s;
            bool onDesktop = IsOnDesktop(args[0]);
            if (onDesktop)
            {
                // 桌面文件:桌面窗格 UIA 首选 → 全局 UIA 兜底(窗格不暴露 List 时
                // 也能命中,位置可靠)→ SysListView32 最后兜底(编码兼容但坐标可能过期)
                result = DesktopUiaFinder.Find(fileName, withoutExt);
                s = "1:desktop-uia:" + DesktopUiaFinder.LastStatus;
                if (result == null)
                {
                    result = GlobalUiaFinder.Find(fileName, withoutExt);
                    s += "|2:global:" + GlobalUiaFinder.LastStatus;
                }
                if (result == null)
                {
                    result = ListViewFinder.Find(fileName);
                    s += "|3:listview:" + ListViewFinder.LastStatus;
                }
            }
            else
            {
                // 非桌面文件:桌面列表不参与(会误匹配桌面同名图标)
                ListViewFinder.LastStatus = "skipped-not-on-desktop";
                s = "1:listview:skipped-not-on-desktop";
            }
            if (result == null)
            {
                result = UiaFinder.Find(fileName);
                s += "|4:uia:" + UiaFinder.LastVia + ":" + UiaFinder.LastStatus;
            }
            if (result == null && !onDesktop)
            {
                result = GlobalUiaFinder.Find(fileName, withoutExt);
                s += "|5:global:" + GlobalUiaFinder.LastStatus;
            }
            diag = "via=" + s + " result=" + (result ?? "null");
        });
        worker.IsBackground = true;
        worker.Start();
        if (!worker.Join(5000)) diag = "via=timeout result=null";

        // 诊断行永远输出到 stderr(主进程写入 find-file-rect.log,远程排查用)
        Console.Error.WriteLine("dpi=" + dpiMode + " cal=" + Math.Round(UiaCal.factor, 3) +
            " os=" + RealOsVersion() + " " + diag + " file=" + fileName);
        if (result != null)
        {
            Console.WriteLine(result);
            Console.Out.Flush();
        }
        Console.Error.Flush();
        Environment.Exit(result != null ? 0 : 1);   // 强制退出,防 UIA 残留拖住进程
        return 1;   // 到不了这,保险
    }

    // 真实系统版本:无 manifest 的 csc 程序里 Environment.OSVersion 永远是假
    // 的 6.2(Win8),诊断没意义,改用 RtlGetVersion 拿真实版本号
    [StructLayout(LayoutKind.Sequential)]
    struct OSVERSIONINFOEXW
    {
        public int dwOSVersionInfoSize;
        public int dwMajorVersion;
        public int dwMinorVersion;
        public int dwBuildNumber;
        public int dwPlatformId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szCSDVersion;
    }
    [DllImport("ntdll.dll")] static extern int RtlGetVersion(ref OSVERSIONINFOEXW info);

    public static string RealOsVersion()
    {
        try
        {
            var info = new OSVERSIONINFOEXW();
            info.dwOSVersionInfoSize = Marshal.SizeOf(typeof(OSVERSIONINFOEXW));
            if (RtlGetVersion(ref info) == 0)
                return info.dwMajorVersion + "." + info.dwMinorVersion + "." + info.dwBuildNumber;
        }
        catch { }
        return Environment.OSVersion.VersionString;
    }
}
