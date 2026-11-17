// The UI Automation driver every desktop flow shares on Windows: the twin of ax-driver.swift.
// The actions live here, the engine's names for its controls live in its profile
// (src/server/native/profiles/<id>.json, read the same way as on macOS, with a "windows" section for
// what differs there), and whether a message really went out is decided by the engine's own record,
// back in JS.
//
// Compiled by src/server/core/desktop-automation.ts with the csc.exe of .NET Framework 4.x that every
// Windows 10/11 ships (C# 5), into the person's cache, once per source version: nothing is downloaded
// and nothing has to be signed. UIA is reached through its COM interfaces (UIA3, as Narrator reaches
// it), declared by hand below. The helper is run as
//   AX_PROFILE=<profile.json> <helper> <command> [args…]
// and prints one JSON object. The commands so far only look; none presses anything:
//   check  → {"trusted":…, "running":…}: the app has a window, and this process may act on it
//   probe  → which of the profile's locators the page shows right now: found / ways / differ / drift / several
//   inspect [<locator>…]  → what the controls those locators find go by besides their names
//   warm [check]  → reads the page once so the desktop keeps its accessibility tree; with `check`,
//            also whether the profile still matches that page
// Any other command answers `not_ported` until its flow is ported.
//
// The three things ax-driver.swift holds for every flow hold here too, written once below:
// "one control, or none" (Sole), "a control found by its place" (Confirmed, against the profile's
// menuSigns), and every answer naming the controls found by another way than their first (`via`).
//
// Nothing here may take the foreground or send global input (skills/add-desktop-engine/
// references/philosophy.md). UIA's SetFocus takes the foreground, and keys can reach Chromium only
// through the foreground, so neither is used: see probes/windows/PLAN.md for what that leaves.
//
// The COM declarations of IUIAutomation, IUIAutomationElement, IUIAutomationTreeWalker,
// IUIAutomationCacheRequest and IUIAutomationElementArray follow vercel-labs/agent-desktop's
// probes/windows (Apache-2.0), which checked them against the Windows SDK's UIAutomationClient.h.
// Each interface is declared only as far as its last method used (a COM vtable may be declared short
// at its end, never with a gap).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace Frontdesk.Driver
{
    // ---- UI Automation, as COM ----
    [ComImport, Guid("352ffba8-0973-437c-a61f-f64cafd81df9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IUIAutomationCondition { }

    [ComImport, Guid("14314595-b4bc-4055-95f2-58f2e42c9855"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IUIAutomationElementArray { int GetLength(); IUIAutomationElement GetElement(int index); }

    [ComImport, Guid("b32a92b5-bc25-4078-9c08-d7ee95c48e03"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IUIAutomationCacheRequest
    {
        void AddProperty(int propertyId);
        void AddPattern(int patternId);
        IUIAutomationCacheRequest Clone();
        int GetTreeScope();
        void SetTreeScope(int scope);
        IUIAutomationCondition GetTreeFilter();
        void SetTreeFilter(IUIAutomationCondition filter);
    }

    [ComImport, Guid("4042c624-389c-4afc-a630-9df854a541fc"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IUIAutomationTreeWalker
    {
        IUIAutomationElement GetParentElement(IUIAutomationElement element);
        IUIAutomationElement GetFirstChildElement(IUIAutomationElement element);
        IUIAutomationElement GetLastChildElement(IUIAutomationElement element);
        IUIAutomationElement GetNextSiblingElement(IUIAutomationElement element);
    }

    [ComImport, Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IUIAutomationElement
    {
        void SetFocus();
        [return: MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_I4)]
        int[] GetRuntimeId();
        IUIAutomationElement FindFirst(int scope, IUIAutomationCondition condition);
        IUIAutomationElementArray FindAll(int scope, IUIAutomationCondition condition);
        void Slot5FindFirstBuildCache();
        IUIAutomationElementArray FindAllBuildCache(int scope, IUIAutomationCondition condition, IUIAutomationCacheRequest cacheRequest);
        IUIAutomationElement BuildUpdatedCache(IUIAutomationCacheRequest cacheRequest);
        [return: MarshalAs(UnmanagedType.Struct)]
        object GetCurrentPropertyValue(int propertyId);
        void Slot9GetCurrentPropertyValueEx();
        [return: MarshalAs(UnmanagedType.Struct)]
        object GetCachedPropertyValue(int propertyId);
        void Slot11GetCachedPropertyValueEx();
        void Slot12GetCurrentPatternAs();
        void Slot13GetCachedPatternAs();
        [return: MarshalAs(UnmanagedType.IUnknown)]
        object GetCurrentPattern(int patternId);
        void Slot15GetCachedPattern();
        void Slot16GetCachedParent();
        IUIAutomationElementArray GetCachedChildren();
    }

    [ComImport, Guid("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IUIAutomation
    {
        int CompareElements(IUIAutomationElement el1, IUIAutomationElement el2);
        void Slot2CompareRuntimeIds();
        IUIAutomationElement GetRootElement();
        IUIAutomationElement ElementFromHandle(IntPtr hwnd);
        void Slot5ElementFromPoint();
        IUIAutomationElement GetFocusedElement();
        void Slot7RootBuildCache();
        void Slot8FromHandleBuildCache();
        void Slot9FromPointBuildCache();
        void Slot10FocusedBuildCache();
        IUIAutomationTreeWalker CreateTreeWalker(IUIAutomationCondition condition);
        IUIAutomationTreeWalker GetControlViewWalker();
        IUIAutomationTreeWalker GetContentViewWalker();
        IUIAutomationTreeWalker GetRawViewWalker();
        IUIAutomationCondition GetRawViewCondition();
        IUIAutomationCondition GetControlViewCondition();
        IUIAutomationCondition GetContentViewCondition();
        IUIAutomationCacheRequest CreateCacheRequest();
    }

    [ComImport, Guid("a5cd92ff-29be-454c-8d04-d82879fb3f1b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IVirtualDesktopManager { [PreserveSig] int IsWindowOnCurrentVirtualDesktop(IntPtr topLevelWindow, out int onCurrentDesktop); }

    public static class Native
    {
        public delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);
        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc proc, IntPtr lParam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);
        [DllImport("user32.dll")] public static extern int GetWindowTextLength(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hwnd);
        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
        [DllImport("kernel32.dll")] public static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll")] public static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);
        [DllImport("advapi32.dll")] public static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
        [DllImport("advapi32.dll")] public static extern bool GetTokenInformation(IntPtr token, int infoClass, IntPtr info, int length, out int returned);
        [DllImport("advapi32.dll")] public static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);
        [DllImport("advapi32.dll")] public static extern IntPtr GetSidSubAuthority(IntPtr sid, int index);
    }

    // One element of the page, as read once (ax-driver.swift's `known`: everything a search looks at,
    // fetched together, in one round trip for the whole page).
    public class Node
    {
        public int Index, End, Parent = -1, ControlType;
        public string Name = "", Accel = "", AutomationId = "", ClassName = "", AriaRole = "", AriaProps = "", Value = "", Help = "", FullDescription = "", Framework = "";
        public bool IsDialog, PExpand, PValue, PText;
        public IUIAutomationElement Element;
        public bool HasPopupMark { get { return Regex.IsMatch(AriaProps, @"(^|;)\s*haspopup=(?!false)", RegexOptions.IgnoreCase); } }
        public bool ValueIsAddress { get { return Regex.IsMatch(Value, @"^(https?|file|about|chrome|app):", RegexOptions.IgnoreCase); } }
        public List<string> Classes { get { return ClassName.Split(new[] { ' ', '\t', '\n' }, StringSplitOptions.RemoveEmptyEntries).ToList(); } }
    }

    // A locator of the profile, read as ax-driver.swift reads it (see its "A locator names one kind of
    // control"): the Mac roles (AXButton…) are mapped to what Chromium shows on Windows in RoleIs.
    public class Loc
    {
        public string Key, Role, Name, Prefix, Contains, After, Inside, Has, Keys, Id, Subrole, Url;
        public List<string> Roles = new List<string>(), Classes = new List<string>();
        public bool Named; public int? Index; public bool? Popup;
        public bool Worded { get { return Name != null || Prefix != null || Contains != null; } }
        public bool Placed { get { return Index.HasValue || After != null; } }
        public string Kind { get { return Worded ? "name" : Placed ? "place" : "mark"; } }
        public static Loc From(string key, Dictionary<string, object> spec)
        {
            Loc l = new Loc(); l.Key = key;
            l.Role = Str(spec, "role"); l.Name = Str(spec, "name"); l.Prefix = Str(spec, "prefix"); l.Contains = Str(spec, "contains");
            l.After = Str(spec, "after"); l.Inside = Str(spec, "inside"); l.Has = Str(spec, "has"); l.Keys = Str(spec, "keys");
            l.Id = Str(spec, "id"); l.Subrole = Str(spec, "subrole"); l.Url = Str(spec, "url");
            object v;
            if (spec.TryGetValue("roles", out v) && v is object[]) l.Roles = ((object[])v).Select(o => Convert.ToString(o, CultureInfo.InvariantCulture)).ToList();
            if (spec.TryGetValue("class", out v)) { if (v is string) l.Classes.Add((string)v); else if (v is object[]) l.Classes = ((object[])v).Select(o => Convert.ToString(o, CultureInfo.InvariantCulture)).ToList(); }
            if (spec.TryGetValue("named", out v) && v is bool) l.Named = (bool)v;
            if (spec.TryGetValue("popup", out v) && v is bool) l.Popup = (bool)v;
            if (spec.TryGetValue("index", out v) && v != null) l.Index = Convert.ToInt32(v, CultureInfo.InvariantCulture);
            return l;
        }
        static string Str(Dictionary<string, object> spec, string key) { object v; return spec.TryGetValue(key, out v) && v is string ? (string)v : null; }
    }

    public static partial class Helper
    {
        // ---- the ids UIAutomationClient.h gives ----
        const int P_ControlType = 30003, P_Name = 30005, P_Accel = 30006, P_AutomationId = 30011, P_ClassName = 30012, P_Help = 30013,
            P_Framework = 30024, P_IsExpand = 30028, P_IsText = 30040, P_IsValue = 30043, P_Value = 30045,
            P_AriaRole = 30101, P_AriaProps = 30102, P_FullDescription = 30159, P_IsDialog = 30174;
        const int CT_Button = 50000, CT_CheckBox = 50002, CT_ComboBox = 50003, CT_Edit = 50004, CT_Hyperlink = 50005, CT_Image = 50006, CT_List = 50008,
            CT_Menu = 50009, CT_MenuItem = 50011, CT_RadioButton = 50013, CT_Slider = 50015, CT_Text = 50020, CT_Custom = 50025, CT_Group = 50026,
            CT_Document = 50030, CT_Pane = 50033;
        static readonly Dictionary<int, string> ControlTypeNames = new Dictionary<int, string> {
            {50000,"Button"},{50002,"CheckBox"},{50003,"ComboBox"},{50004,"Edit"},{50005,"Hyperlink"},{50006,"Image"},{50007,"ListItem"},{50008,"List"},
            {50009,"Menu"},{50011,"MenuItem"},{50013,"RadioButton"},{50015,"Slider"},{50018,"Tab"},{50019,"TabItem"},{50020,"Text"},{50025,"Custom"},
            {50026,"Group"},{50030,"Document"},{50032,"Window"},{50033,"Pane"} };
        // The Mac subroles the profiles use, as the ARIA role Chromium reports on Windows.
        static readonly Dictionary<string, string> SubroleAria = new Dictionary<string, string> {
            {"AXDocumentArticle","article"},{"AXApplicationGroup","group"},{"AXApplicationStatus","status"},{"AXApplicationDialog","dialog"},
            {"AXApplicationAlertDialog","alertdialog"},{"AXApplicationAlert","alert"},{"AXLandmarkMain","main"},{"AXLandmarkNavigation","navigation"},
            {"AXLandmarkRegion","region"},{"AXTabPanel","tabpanel"} };

        static IUIAutomation uia;
        static readonly Stopwatch clock = Stopwatch.StartNew();
        static int reads;
        static readonly bool tracing = Environment.GetEnvironmentVariable("AX_TRACE") != null;
        static void Trace(string what) { if (tracing) Console.Error.WriteLine("[uia +" + clock.ElapsedMilliseconds + "ms " + reads + "r] " + what); }

        // ---- the command line ----
        static List<string> args = new List<string>();
        static Dictionary<string, string> wanted = new Dictionary<string, string>();
        static string command { get { return args.Count > 0 ? args[0] : ""; } }

        // ---- the answer ----
        // What the command learned about its own footing (see "one control, or none" and "a control
        // found by its place"), added to every answer by Annotate.
        static Dictionary<string, int> via = new Dictionary<string, int>(), refused = new Dictionary<string, int>();
        static List<string> unconfirmed = new List<string>();
        static Dictionary<Node, KeyValuePair<string, int>> origin = new Dictionary<Node, KeyValuePair<string, int>>();
        static HashSet<Node> soleNow = new HashSet<Node>();
        class Done : Exception { public readonly int Code; public Done(int code) { Code = code; } }
        static void Out(Dictionary<string, object> answer)
        {
            Annotate(answer);
            answer["ms"] = (int)clock.ElapsedMilliseconds; answer["reads"] = reads;
            JavaScriptSerializer json = new JavaScriptSerializer(); json.MaxJsonLength = int.MaxValue; json.RecursionLimit = 100;
            Console.WriteLine(json.Serialize(answer));
            object ok;
            throw new Done(answer.TryGetValue("ok", out ok) && ok is bool && !(bool)ok ? 1 : 0);
        }
        static void Fail(string code, string message) { Out(new Dictionary<string, object> { { "ok", false }, { "code", code }, { "error", message } }); }

        [MTAThread]
        public static int Main(string[] argv)
        {
            try { Console.OutputEncoding = new UTF8Encoding(false); } catch { }
            for (int i = 0; i < argv.Length; i++)
            {
                string a = argv[i];
                if ((a == "--model" || a == "--effort" || a == "--mode" || a == "--space") && i + 1 < argv.Length) { wanted[a.Substring(2)] = argv[++i]; continue; }
                if (a == "--previous" && i + 1 < argv.Length) { i++; continue; }
                if (a == "--stay") continue;
                args.Add(a);
            }
            try { Run(); return 0; }
            catch (Done done) { return done.Code; }
            catch (Exception e)
            {
                Console.WriteLine(new JavaScriptSerializer().Serialize(new Dictionary<string, object> { { "ok", false }, { "code", "crashed" }, { "error", e.GetType().Name + ": " + e.Message }, { "ms", (int)clock.ElapsedMilliseconds } }));
                return 1;
            }
        }

        // ---- the profile: the engine's names for things, as data ----
        static Dictionary<string, object> profile = new Dictionary<string, object>();
        static Dictionary<string, object> windowsSpec = new Dictionary<string, object>();
        static Dictionary<string, List<Loc>> locators = new Dictionary<string, List<Loc>>();
        static string Setting(string key)
        {
            object v;
            if (windowsSpec.TryGetValue(key, out v) && v is string) return (string)v;
            return profile.TryGetValue(key, out v) && v is string ? (string)v : "";
        }
        static List<string> Settings(string key)
        {
            object v;
            if (!windowsSpec.TryGetValue(key, out v)) profile.TryGetValue(key, out v);
            return v is object[] ? ((object[])v).Select(o => Convert.ToString(o, CultureInfo.InvariantCulture)).ToList() : new List<string>();
        }
        static string appName { get { string n = Setting("name"); return n.Length > 0 ? n : "桌面版"; } }
        static List<Loc> LocatorList(string key, object value)
        {
            List<Loc> ways = new List<Loc>();
            if (value is Dictionary<string, object>) ways.Add(Loc.From(key, (Dictionary<string, object>)value));
            else if (value is object[]) foreach (object o in (object[])value) { if (o is Dictionary<string, object>) ways.Add(Loc.From(key, (Dictionary<string, object>)o)); }
            return ways;
        }
        // The profile's locators, with the "windows" section's own for the controls the Windows app
        // draws elsewhere (Claude's mode switch sits in the window's top bar, not in its sidebar): a
        // locator named there replaces the one of the same name, and may be new.
        static void LoadProfile()
        {
            string path = Environment.GetEnvironmentVariable("AX_PROFILE");
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
            profile = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(path, Encoding.UTF8)) as Dictionary<string, object> ?? new Dictionary<string, object>();
            object raw;
            if (profile.TryGetValue("windows", out raw) && raw is Dictionary<string, object>) windowsSpec = (Dictionary<string, object>)raw;
            foreach (Dictionary<string, object> source in new[] { profile, windowsSpec })
            {
                if (!source.TryGetValue("locators", out raw) || !(raw is Dictionary<string, object>)) continue;
                foreach (KeyValuePair<string, object> kv in (Dictionary<string, object>)raw) locators[kv.Key] = LocatorList(kv.Key, kv.Value);
            }
        }

        // ---- the app ----
        static IntPtr window = IntPtr.Zero;
        static int IntegrityOf(IntPtr process)
        {
            IntPtr token;
            if (!Native.OpenProcessToken(process, 0x0008, out token)) return -1;
            try
            {
                int size; Native.GetTokenInformation(token, 25, IntPtr.Zero, 0, out size);
                IntPtr buffer = Marshal.AllocHGlobal(size);
                try
                {
                    if (!Native.GetTokenInformation(token, 25, buffer, size, out size)) return -1;
                    IntPtr sid = Marshal.ReadIntPtr(buffer);
                    int count = Marshal.ReadByte(Native.GetSidSubAuthorityCount(sid));
                    return Marshal.ReadInt32(Native.GetSidSubAuthority(sid, count - 1));
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            finally { Native.CloseHandle(token); }
        }
        static int IntegrityOfPid(uint pid)
        {
            IntPtr process = Native.OpenProcess(0x1000, false, pid);
            if (process == IntPtr.Zero) return -1;
            try { return IntegrityOf(process); } finally { Native.CloseHandle(process); }
        }
        static string ExeOf(uint pid)
        {
            IntPtr process = Native.OpenProcess(0x1000, false, pid);
            if (process == IntPtr.Zero) return "";
            try { StringBuilder sb = new StringBuilder(1024); int size = sb.Capacity; return Native.QueryFullProcessImageName(process, 0, sb, ref size) ? Path.GetFileName(sb.ToString()) : ""; }
            finally { Native.CloseHandle(process); }
        }
        static string ClassOf(IntPtr hwnd) { StringBuilder sb = new StringBuilder(256); Native.GetClassName(hwnd, sb, sb.Capacity); return sb.ToString(); }
        static bool Cloaked(IntPtr hwnd) { int cloaked; return Native.DwmGetWindowAttribute(hwnd, 14, out cloaked, 4) == 0 && cloaked != 0; }
        // The app's own window: a titled Chromium window of the profile's exe ("windows": {"exe": …}).
        // A visible one before a hidden one (Codex keeps a hidden window titled like its main one),
        // then a restored one before a minimized one (whose rect is only its caption), then the largest.
        static IntPtr FindWindow()
        {
            string exe = Setting("exe");
            if (exe.Length == 0) return IntPtr.Zero;
            List<IntPtr> found = new List<IntPtr>();
            Native.EnumWindows(delegate (IntPtr hwnd, IntPtr l)
            {
                uint pid; Native.GetWindowThreadProcessId(hwnd, out pid);
                if (ClassOf(hwnd).StartsWith("Chrome_WidgetWin", StringComparison.Ordinal) && Native.GetWindowTextLength(hwnd) > 0 && string.Equals(ExeOf(pid), exe, StringComparison.OrdinalIgnoreCase)) found.Add(hwnd);
                return true;
            }, IntPtr.Zero);
            return found.OrderByDescending(h => Native.IsWindowVisible(h)).ThenByDescending(h => !Native.IsIconic(h))
                .ThenByDescending(h => { Native.RECT r; Native.GetWindowRect(h, out r); return (long)(r.Right - r.Left) * (r.Bottom - r.Top); })
                .FirstOrDefault();
        }
        // On screen: shown, not minimized, not cloaked (another virtual desktop, or hidden by the shell).
        static bool OnScreen()
        {
            if (window == IntPtr.Zero || !Native.IsWindowVisible(window) || Native.IsIconic(window) || Cloaked(window)) return false;
            try
            {
                IVirtualDesktopManager m = (IVirtualDesktopManager)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("aa509086-5ca9-4c25-8f95-589d3c07b48a")));
                int on; return m.IsWindowOnCurrentVirtualDesktop(window, out on) != 0 || on != 0;
            }
            catch { return true; }
        }

        // ---- reading the page ----
        // Every property read is a round trip to the desktop's process, so the whole page is read in
        // one: a cache request over the raw view (Stale() drops it after anything that could change it).
        static List<Node> nodes = new List<Node>();
        static bool pageRead;
        static readonly int[] WalkProps = { P_ControlType, P_Name, P_Accel, P_AutomationId, P_ClassName, P_Help, P_Framework, P_IsExpand, P_IsText, P_IsValue, P_Value, P_AriaRole, P_AriaProps, P_FullDescription, P_IsDialog };
        static void Stale() { pageRead = false; soleNow.Clear(); }
        static object ReadProp(IUIAutomationElement e, int id)
        {
            try { object v = e.GetCachedPropertyValue(id); return v != null && Marshal.IsComObject(v) ? null : v; } catch { return null; }
        }
        static string S(object v) { return v as string ?? ""; }
        static bool B(object v) { return v is bool && (bool)v; }
        static void Add(IUIAutomationElement e, int parent, int depth)
        {
            if (nodes.Count >= 20000 || depth > 90) return;
            Node n = new Node { Element = e, Parent = parent, Index = nodes.Count };
            n.ControlType = ReadProp(e, P_ControlType) is int ? (int)ReadProp(e, P_ControlType) : 0;
            n.Name = S(ReadProp(e, P_Name)); n.Accel = S(ReadProp(e, P_Accel)); n.AutomationId = S(ReadProp(e, P_AutomationId));
            n.ClassName = S(ReadProp(e, P_ClassName)); n.Help = S(ReadProp(e, P_Help)); n.Framework = S(ReadProp(e, P_Framework));
            n.Value = S(ReadProp(e, P_Value)); n.AriaRole = S(ReadProp(e, P_AriaRole)); n.AriaProps = S(ReadProp(e, P_AriaProps));
            n.FullDescription = S(ReadProp(e, P_FullDescription)); n.IsDialog = B(ReadProp(e, P_IsDialog));
            n.PExpand = B(ReadProp(e, P_IsExpand)); n.PValue = B(ReadProp(e, P_IsValue)); n.PText = B(ReadProp(e, P_IsText));
            nodes.Add(n);
            IUIAutomationElementArray kids = null;
            try { kids = e.GetCachedChildren(); } catch { }
            if (kids != null) { int count = kids.GetLength(); for (int i = 0; i < count; i++) Add(kids.GetElement(i), n.Index, depth + 1); }
            n.End = nodes.Count - 1;
        }
        static void ReadPage()
        {
            if (pageRead) return;
            nodes = new List<Node>(); origin.Clear();
            reads++;
            IUIAutomationCacheRequest cache = uia.CreateCacheRequest();
            foreach (int id in WalkProps) { try { cache.AddProperty(id); } catch { } }
            cache.SetTreeScope(7);                                  // the element and everything under it
            cache.SetTreeFilter(uia.GetRawViewCondition());          // the raw view, like the Mac tree
            try { Add(uia.ElementFromHandle(window).BuildUpdatedCache(cache), -1, 0); } catch (Exception e) { Trace("read failed: " + e.Message); }
            popupMarked = nodes.Any(n => n.AriaProps.IndexOf("haspopup=", StringComparison.OrdinalIgnoreCase) >= 0);
            pageRead = true;
        }
        static IEnumerable<Node> Within(Node box) { for (int i = box.Index; i <= box.End && i < nodes.Count; i++) yield return nodes[i]; }

        // ---- finding a control ----
        // Whether Chromium puts haspopup into AriaProperties on this machine; when it does not, a button
        // that opens something is told by the ExpandCollapse pattern instead.
        static bool popupMarked;
        // A Mac role, as Chromium shows it on Windows (measured: probes/windows/PLAN.md).
        static HashSet<string> unmappedRoles = new HashSet<string>();
        static bool RoleIs(Node n, string ax)
        {
            bool opens = popupMarked ? n.HasPopupMark : n.PExpand;
            switch (ax)
            {
                case "AXButton": return n.ControlType == CT_Button && !opens;
                case "AXPopUpButton": return n.ControlType == CT_ComboBox || (n.ControlType == CT_Button && opens);
                case "AXMenuItem": return n.ControlType == CT_MenuItem || n.AriaRole == "menuitem" || n.AriaRole == "menuitemradio" || n.AriaRole == "menuitemcheckbox";
                case "AXGroup": return (n.ControlType == CT_Group || n.ControlType == CT_Custom || n.ControlType == CT_Pane) && n.AriaRole != "heading" && n.AriaRole != "radiogroup";
                case "AXTextArea": return n.ControlType == CT_Edit || (n.ControlType != CT_Document && n.PValue && n.PText && !n.ValueIsAddress);
                case "AXTextField": return n.ControlType == CT_Edit;
                case "AXHeading": return n.AriaRole == "heading";
                case "AXWebArea": return n.ControlType == CT_Document;
                case "AXRadioGroup": return n.AriaRole == "radiogroup";
                case "AXList": return n.ControlType == CT_List;
                case "AXStaticText": return n.ControlType == CT_Text && n.AriaRole != "heading";
                case "AXCheckBox": return n.ControlType == CT_CheckBox;
                case "AXRadioButton": return n.ControlType == CT_RadioButton;
                case "AXLink": return n.ControlType == CT_Hyperlink;
                case "AXImage": return n.ControlType == CT_Image;
                case "AXSlider": return n.ControlType == CT_Slider;
                case "AXMenu": return n.ControlType == CT_Menu;
                default: unmappedRoles.Add(ax); return false;
            }
        }
        static bool HasClass(Node n, string want)
        {
            List<string> has = n.Classes;
            return want.EndsWith("*", StringComparison.Ordinal) ? has.Any(c => c.StartsWith(want.Substring(0, want.Length - 1), StringComparison.Ordinal)) : has.Contains(want);
        }
        // A shortcut the page declares, as Windows writes it: Chromium reports aria-keyshortcuts' "Meta"
        // as "Control" here ("Meta+n" is "Control+n"), so a profile's keys are compared that way.
        static string WindowsKeys(string keys) { return keys.Replace("Meta", "Control"); }
        // What the element itself is; where it is ("inside", "after", "index") is Matches.
        static bool Fits(Node n, Loc l)
        {
            if (l.Role != null && !RoleIs(n, l.Role)) return false;
            if (l.Roles.Count > 0 && !l.Roles.Any(r => RoleIs(n, r))) return false;
            if (l.Subrole != null) { string aria; if (!SubroleAria.TryGetValue(l.Subrole, out aria)) { unmappedRoles.Add(l.Subrole); return false; } if (n.AriaRole != aria) return false; }
            if (l.Id != null && n.AutomationId != l.Id) return false;
            if (l.Keys != null && n.Accel != WindowsKeys(l.Keys)) return false;
            foreach (string want in l.Classes) if (!HasClass(n, want)) return false;
            if (l.Popup.HasValue && (n.HasPopupMark || n.PExpand) != l.Popup.Value) return false;
            if (l.Url != null) { if (n.ControlType != CT_Document) return false; string own = n.Value; if (l.Url.EndsWith("*", StringComparison.Ordinal) ? !own.StartsWith(l.Url.Substring(0, l.Url.Length - 1), StringComparison.Ordinal) : own != l.Url) return false; }
            if (!l.Worded && !l.Named) return true;
            string[] names = { n.Name, Label(n) };
            if (l.Name != null && !names.Contains(l.Name)) return false;
            if (l.Prefix != null && !names.Any(s => s.StartsWith(l.Prefix, StringComparison.Ordinal))) return false;
            if (l.Contains != null && !names.Any(s => s.IndexOf(l.Contains, StringComparison.OrdinalIgnoreCase) >= 0)) return false;
            if (l.Named && names.All(s => s.Length == 0)) return false;
            return true;
        }
        // A control's label: its name and its description, once when they say the same.
        static string Label(Node n)
        {
            string more = n.FullDescription.Length > 0 ? n.FullDescription : n.Help;
            return more.Length == 0 || more == n.Name ? n.Name : (n.Name + " " + more).Trim();
        }
        // Everything one way of finding a control finds, in the order the page lists it. (`depth`: a
        // locator placed by another, placed by another…: a profile that goes round in a circle finds nothing.)
        static List<Node> Matches(Loc l, int depth)
        {
            ReadPage();
            if (depth >= 5 || nodes.Count == 0) return new List<Node>();
            Node scope = nodes[0];
            if (l.Inside != null) { scope = Find(l.Inside, depth + 1); if (scope == null) return new List<Node>(); }
            List<Node> hits;
            if (l.After != null)
            {
                Node mark = Find(l.After, depth + 1);
                if (mark == null) return new List<Node>();
                hits = Within(scope).Where(n => n.Index > mark.Index && Fits(n, l)).ToList();
            }
            else hits = Within(scope).Where(n => Fits(n, l)).ToList();
            if (l.Has != null)
            {
                List<Node> held = FindAll(l.Has, depth + 1);
                hits = hits.Where(box => held.Any(part => part != box && part.Index > box.Index && part.Index <= box.End)).ToList();
            }
            if (!l.Index.HasValue) return hits;
            int at = l.Index.Value < 0 ? hits.Count + l.Index.Value : l.Index.Value;
            return at >= 0 && at < hits.Count ? new List<Node> { hits[at] } : new List<Node>();
        }
        static List<Node> FindAll(string key, int depth = 0)
        {
            List<Loc> ways; if (!locators.TryGetValue(key, out ways)) return new List<Node>();
            foreach (Loc l in ways) { List<Node> hits = Matches(l, depth); if (hits.Count > 0) return hits; }
            return new List<Node>();
        }
        // (Which locator found the control, and by which of its ways, is kept with it: `origin`, for
        // the check before anything is done to it; `via`, for the answer.)
        static Node Find(string key, int depth = 0)
        {
            List<Loc> ways; if (!locators.TryGetValue(key, out ways)) return null;
            for (int way = 0; way < ways.Count; way++)
            {
                List<Node> hits = Matches(ways[way], depth);
                if (hits.Count == 0) continue;
                origin[hits[0]] = new KeyValuePair<string, int>(key, way); via[key] = way;
                return hits[0];
            }
            return null;
        }

        // ---- one control, or none ----
        // As in ax-driver.swift: before anything is done to a control a locator found, the way that
        // found it is asked for everything it finds. Exactly one: go on. Several: another of the
        // locator's ways may still single out this very control; if none does, nothing is done and the
        // answer says why ("ambiguous"). The box it was looked for in, and the control it was counted
        // from, have to be the only ones too.
        static bool Sole(Node e, int depth = 0)
        {
            KeyValuePair<string, int> from; List<Loc> ways;
            if (depth >= 5 || !origin.TryGetValue(e, out from) || !locators.TryGetValue(from.Key, out ways) || from.Value >= ways.Count || soleNow.Contains(e)) return true;
            Loc l = ways[from.Value];
            if (!l.Placed)
            {
                List<Node> hits = Matches(l, 0);
                if (hits.Count > 1)
                {
                    bool singled = Enumerable.Range(0, ways.Count).Any(j => j != from.Value && !ways[j].Placed && Matches(ways[j], 0).Count == 1 && Matches(ways[j], 0)[0] == e);
                    if (!singled) { refused[from.Key] = hits.Count; Trace("held back: " + hits.Count + " controls answer to " + from.Key); return false; }
                }
            }
            foreach (string anchor in new[] { l.Inside, l.After }.Where(a => a != null)) { Node box = Find(anchor, depth + 1); if (box != null && !Sole(box, depth + 1)) return false; }
            origin[e] = from;
            soleNow.Add(e);
            return true;
        }

        // ---- a control found by its place ----
        // A menu button found by its place has to open the menu the profile describes for it
        // ("menuSigns": a locator spec one of that menu's rows answers to). Called with the menu open;
        // a menu without such a row is not taken, and the answer says so ("unconfirmed").
        static Dictionary<string, object> MenuSigns()
        {
            object raw;
            if (windowsSpec.TryGetValue("menuSigns", out raw) && raw is Dictionary<string, object>) return (Dictionary<string, object>)raw;
            return profile.TryGetValue("menuSigns", out raw) && raw is Dictionary<string, object> ? (Dictionary<string, object>)raw : new Dictionary<string, object>();
        }
        static bool Confirmed(Node button)
        {
            KeyValuePair<string, int> from; List<Loc> ways; object spec;
            if (!origin.TryGetValue(button, out from) || !locators.TryGetValue(from.Key, out ways) || from.Value >= ways.Count || !ways[from.Value].Placed || !MenuSigns().TryGetValue(from.Key, out spec)) return true;
            List<Loc> signs = LocatorList(from.Key, spec);
            ReadPage();
            bool holds = nodes.Any(row => RoleIs(row, "AXMenuItem") && signs.Any(sign => Fits(row, sign)));
            if (!holds) { unconfirmed.Add(from.Key); Trace("the menu " + from.Key + " opened is not the one the profile describes"); }
            return holds;
        }

        // ---- what the answer says about its footing ----
        static void Annotate(Dictionary<string, object> answer)
        {
            if (!new[] { "warm", "probe", "inspect", "check" }.Contains(command))
            {
                Dictionary<string, object> other = new Dictionary<string, object>();
                foreach (KeyValuePair<string, int> kv in via) { List<Loc> ways; if (kv.Value > 0 && locators.TryGetValue(kv.Key, out ways) && kv.Value < ways.Count) other[kv.Key] = ways[kv.Value].Kind; }
                if (other.Count > 0) answer["via"] = other;
            }
            object okValue; bool failed = answer.TryGetValue("ok", out okValue) && okValue is bool && !(bool)okValue;
            Action<string, string> because = (code, message) => { if (failed) { object step; if (answer.TryGetValue("code", out step)) answer["step"] = step; answer["code"] = code; answer["error"] = message; } };
            if (refused.Count > 0)
            {
                KeyValuePair<string, int> first = refused.OrderBy(kv => kv.Key, StringComparer.Ordinal).First();
                answer["ambiguous"] = refused.ToDictionary(kv => kv.Key, kv => (object)kv.Value);
                because("ambiguous", appName + "的页面上有 " + first.Value + " 个控件都对得上「" + first.Key + "」，分不清该动哪一个，所以没有动手（桌面版可能改过界面，这个控件的认法要更新）");
            }
            else if (unconfirmed.Count > 0)
            {
                answer["unconfirmed"] = unconfirmed;
                because("unconfirmed", appName + "里按位置找到的「" + unconfirmed[0] + "」打开的不是预期的菜单，所以什么都没有选（桌面版可能改过界面，这个控件的认法要更新）");
            }
        }

        // ---- does the profile still match the desktop? ----
        // ax-driver.swift's profileCheck: found / ways / differ / drift / several, the same fields.
        static Dictionary<string, object> ProfileCheck()
        {
            Dictionary<string, object> found = new Dictionary<string, object>(), waysOut = new Dictionary<string, object>(), several = new Dictionary<string, object>();
            List<string> differ = new List<string>(), drift = new List<string>();
            foreach (string key in locators.Keys.OrderBy(k => k, StringComparer.Ordinal))
            {
                List<Loc> list = locators[key];
                List<List<Node>> each = list.Select(l => Matches(l, 0)).ToList();
                int at = each.FindIndex(h => h.Count > 0);
                found[key] = at >= 0 ? each[at].Count : 0;
                if (at >= 0 && !list[at].Placed && each[at].Count > 1)
                {
                    bool singled = Enumerable.Range(0, list.Count).Any(j => j != at && !list[j].Placed && each[j].Count == 1 && each[j][0] == each[at][0]);
                    if (!singled) several[key] = each[at].Count;
                }
                if (each.Count < 2) continue;
                waysOut[key] = each.Select(h => (object)h.Count).ToList();
                List<Node> firsts = each.Where(h => h.Count > 0).Select(h => h[0]).ToList();
                if (firsts.Count > 1 && firsts.Any(f => f != firsts[0])) differ.Add(key);
                if (at > 0)
                {
                    Node hit = each[at][0];
                    bool lost = Enumerable.Range(0, at).Any(j =>
                    {
                        Loc way = list[j];
                        if (way.Kind != "mark" || (way.Role != null && !RoleIs(hit, way.Role)) || (way.Roles.Count > 0 && !way.Roles.Any(r => RoleIs(hit, r)))) return false;
                        if (way.Inside == null) return true;
                        Node box = Find(way.Inside); return box != null && hit.Index > box.Index && hit.Index <= box.End;
                    });
                    if (lost) drift.Add(key);
                }
            }
            return new Dictionary<string, object> { { "found", found }, { "ways", waysOut }, { "differ", differ }, { "drift", drift }, { "several", several } };
        }
        // Which conversation is on screen: the page's title, as ax-driver.swift's current() reads it.
        static string Current()
        {
            string suffix = Setting("titleSuffix");
            List<string> blank = Settings("blankTitles");
            ReadPage();
            foreach (Node page in nodes.Where(n => n.ControlType == CT_Document))
            {
                string title = page.Name;
                if (suffix.Length > 0) { if (title.EndsWith(suffix, StringComparison.Ordinal)) return title.Substring(0, title.Length - suffix.Length); continue; }
                return blank.Contains(title) ? "" : title;
            }
            return "";
        }
        // What a control goes by besides its name (inspect).
        static Dictionary<string, object> Facts(Node n)
        {
            Dictionary<string, object> f = new Dictionary<string, object> { { "role", ControlTypeNames.ContainsKey(n.ControlType) ? ControlTypeNames[n.ControlType] : n.ControlType.ToString(CultureInfo.InvariantCulture) } };
            if (n.AriaRole.Length > 0) f["ariaRole"] = n.AriaRole;
            if (n.Name.Length > 0) f["name"] = n.Name.Length > 80 ? n.Name.Substring(0, 80) : n.Name;
            if (n.AutomationId.Length > 0) f["id"] = n.AutomationId;
            if (n.ClassName.Length > 0) f["class"] = n.Classes;
            if (n.Accel.Length > 0) f["keys"] = n.Accel;
            return f;
        }

        // ---- the commands ----
        static void Run()
        {
            LoadProfile();
            window = FindWindow();
            int self = IntegrityOf(Native.GetCurrentProcess());
            uint pid = 0; if (window != IntPtr.Zero) Native.GetWindowThreadProcessId(window, out pid);
            int app = window == IntPtr.Zero ? -1 : IntegrityOfPid(pid);
            // Windows has no accessibility permission to ask for. What it has is integrity levels (UIPI):
            // a process may read a window of a higher level's process but not act on it.
            bool trusted = app < 0 || self >= app;
            if (command == "check") Out(new Dictionary<string, object> { { "ok", true }, { "trusted", trusted }, { "running", window != IntPtr.Zero }, { "integrity", self }, { "appIntegrity", app } });
            if (Setting("exe").Length == 0) Fail("usage", "缺少桌面版在 Windows 上的配置（AX_PROFILE 的 windows.exe）");
            if (window == IntPtr.Zero) Fail("not_running", appName + "没有在运行");
            if (!trusted) Fail("not_trusted", appName + "以更高的权限运行（例如以管理员身份），工作台读得到它的界面但不能操作：请用相同的权限运行两者");
            try { uia = (IUIAutomation)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("e22ad333-b25f-460c-83d0-0581107395c9"), true)); }
            catch { uia = (IUIAutomation)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("ff48dba4-60ef-4201-aa87-54103eef594e"), true)); }

            // Chromium builds its page's tree once a UIA client asks, starting from a shell of a dozen
            // nodes; until it has, every control would read as missing. So wait for one of the profile's
            // "refreshAnchors" (up to three seconds; warm only looks), as ax-driver.swift does.
            List<string> anchors = Settings("refreshAnchors");
            double patience = command == "warm" ? 0 : 3.0;
            bool ready;
            while (true)
            {
                Stale(); ReadPage();
                ready = anchors.Count == 0 ? nodes.Count >= 40 : anchors.Any(a => FindAll(a).Count > 0);
                if (ready || clock.Elapsed.TotalSeconds >= patience) break;
                Thread.Sleep(150);
            }
            bool pageEmpty = !ready && nodes.Count < 40;
            Trace("page " + (ready ? "ready" : pageEmpty ? "empty" : "not ready") + " (" + nodes.Count + " elements)");

            if (command == "warm")
            {
                Dictionary<string, object> answer = new Dictionary<string, object> { { "ok", true }, { "ready", ready } };
                if (args.Contains("check") && ready) { Dictionary<string, object> state = ProfileCheck(); answer["checked"] = true; answer["drift"] = state["drift"]; answer["differ"] = state["differ"]; }
                answer["elements"] = nodes.Count;
                Out(answer);
            }
            if (command == "probe")
            {
                Dictionary<string, object> state = ProfileCheck();
                state["ok"] = true; state["onScreen"] = OnScreen(); state["current"] = Current();
                if (unmappedRoles.Count > 0) state["unmappedRoles"] = unmappedRoles.OrderBy(r => r, StringComparer.Ordinal).ToList();
                Out(state);
            }
            if (command == "inspect")
            {
                Dictionary<string, object> report = new Dictionary<string, object>();
                IEnumerable<string> asked = args.Count > 1 ? args.Skip(1) : locators.Keys.OrderBy(k => k, StringComparer.Ordinal);
                foreach (string key in asked)
                {
                    report[key] = FindAll(key).Take(3).Select(hit =>
                    {
                        Dictionary<string, object> own = Facts(hit);
                        List<object> boxes = new List<object>();
                        for (int up = hit.Parent, i = 0; up >= 0 && i < 12; up = nodes[up].Parent, i++)
                        {
                            Dictionary<string, object> box = Facts(nodes[up]);
                            if (box.Count > 1) boxes.Add(box);
                            if (nodes[up].ControlType == CT_Document) break;
                        }
                        own["in"] = boxes;
                        return (object)own;
                    }).ToList();
                }
                Out(new Dictionary<string, object> { { "ok", true }, { "current", Current() }, { "controls", report } });
            }
            if (pageEmpty) Fail("page_empty", OnScreen()
                ? appName + "的页面没有接进 UI Automation，工作台读不到它的界面"
                : appName + "的窗口不在屏幕上（最小化或在另一个桌面），页面没有接进 UI Automation：把窗口还原后再试");
            Fail("not_ported", "Windows 上的桌面代发还在移植中，" + appName + "暂时不能通过窗口操作「" + command + "」");
        }
    }
}
