// GXWorks3Automation.cs
// প্রথম ওয়ার্কিং প্রোটোটাইপ — MELSOFT GX Works3 UI Automation
//
// NuGet packages লাগবে:
//   Install-Package FlaUI.UIA3
//   Install-Package FlaUI.Core
//
// এই প্রোটোটাইপ যা করে:
//   1. GX Works3 প্রসেসে অ্যাটাচ করে (already running থাকতে হবে)
//   2. ST editor window ফোকাস করে
//   3. Clipboard দিয়ে টেস্ট কোড পেস্ট করে
//   4. Rebuild All ট্রিগার করে (Shift+Alt+F4)
//   5. Output DataGrid থেকে error rows পড়ে ফেরত দেয়
//
// গুরুত্বপূর্ণ: এই কোড ইন্টারেক্টিভ Windows session-এ, GUI ভিজিবল অবস্থায় চালাতে হবে।
// Windows Service হিসেবে চালালে কাজ করবে না (Session 0 isolation)।

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms; // Clipboard-এর জন্য (System.Windows.Forms.dll reference লাগবে)
using FlaUICoreApp = FlaUI.Core.Application;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace GXWorks3Bridge
{
    /// <summary>
    /// GX Works3-এর সাথে UI Automation দিয়ে যোগাযোগ করার মূল ক্লাস।
    /// প্রতিটা পাবলিক মেথড একেকটা MCP tool-এর ব্যাকএন্ড হবে।
    /// </summary>
    public class GXWorks3Controller : IDisposable
    {
        private readonly UIA3Automation _automation;
        private FlaUICoreApp _app;
        private Window _mainWindow;

        /// <summary>
        /// একই সময়ে দুটো MCP tool call যেন UI নাড়াচাড়া (keystroke/click) না মেশায়,
        /// সব পাবলিক অপারেশন এই lock-এর ভেতরে চলে।
        /// </summary>
        public readonly object SyncRoot = new object();

        /// <summary>
        /// preview→confirm write-safety store: safetyToken → pending write তথ্য।
        /// সব access SyncRoot lock-এর ভেতরে হয় (প্রতিটা tool lock ধরে চলে), তাই plain Dictionary যথেষ্ট।
        /// </summary>
        private readonly Dictionary<string, PendingWrite> _pendingWrites = new();
        private static readonly TimeSpan PreviewTtl = TimeSpan.FromMinutes(10);

        public GXWorks3Controller()
        {
            _automation = new UIA3Automation();
        }

        /// <summary>
        /// প্রসেস আইডি দিয়ে ইতিমধ্যে চলমান GX Works3-এ অ্যাটাচ করে।
        /// প্রথমে Task Manager থেকে GX Works3-এর PID যাচাই করে নাও, অথবা
        /// FindProcessByName() হেল্পার ব্যবহার করো।
        /// </summary>
        public bool AttachToProcess(int processId)
        {
            try
            {
                _app = FlaUICoreApp.Attach(processId);
                _mainWindow = _app.GetMainWindow(_automation, TimeSpan.FromSeconds(5));
                return _mainWindow != null;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Attach failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// টুল কল-এর শুরুতে ডাকা হয়: এখনো attach না থাকলে চলমান GX Works3 খুঁজে
        /// নিজে থেকে attach করে। ফলে "আগে attach করুন" জাতীয় অর্ডার চাপাতে হয় না।
        /// GX Works3 চালু না থাকলে readable exception ছোঁড়ে।
        /// </summary>
        public void EnsureAttached()
        {
            if (_mainWindow != null) return;

            var pid = FindProcessByName()
                ?? throw new InvalidOperationException(
                    "GX Works3 চলমান পাওয়া যায়নি — আগে GX Works3 ওপেন করে একটা প্রজেক্ট লোড করুন।");

            if (!AttachToProcess(pid))
                throw new InvalidOperationException("GX Works3-এ attach ব্যর্থ হয়েছে।");
        }

        /// <summary>
        /// টাইটেলে "GX Works3" থাকা প্রসেস খুঁজে বের করে PID রিটার্ন করে।
        /// Attach করার আগে এটা কল করে PID পাওয়া যায়।
        /// </summary>
        public static int? FindProcessByName()
       {
        var processes = System.Diagnostics.Process.GetProcessesByName("GXW3");
        return processes.Length > 0 ? processes[0].Id : (int?)null;
         }
                /// <summary>
        /// বর্তমানে খোলা সব child Window-এর টাইটেল রিটার্ন করে (POU নাম আবিষ্কারের জন্য)।
        /// </summary>
        public List<string> ListPouWindows()
        {
            if (_mainWindow == null)
                throw new InvalidOperationException("আগে AttachToProcess/EnsureAttached কল করো।");

            var childWindows = _mainWindow.FindAllDescendants(cf =>
                cf.ByControlType(ControlType.Window));

            return childWindows
                .Select(el => el.AsWindow()?.Title)
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Select(t => t!)
                .ToList();
        }

        /// <summary>
        /// POU window খুঁজে বের করে ফোকাস করে।
        /// GX Works3-এ প্রতিটা খোলা POU একটা আলাদা child Window হিসেবে থাকে,
        /// টাইটেলে POU-এর নাম থাকে (যেমন "ProgPou [PRG] [ST]").
        /// </summary>
        public Window FindAndFocusPouWindow(string pouNameHint)
{
    if (_mainWindow == null)
        throw new InvalidOperationException("আগে AttachToProcess কল করো।");

    var childWindows = _mainWindow.FindAllDescendants(cf =>
        cf.ByControlType(ControlType.Window));

    Console.Error.WriteLine($"মোট {childWindows.Length} টা Window পাওয়া গেছে:");
    foreach (var el in childWindows)
    {
        var w = el.AsWindow();
        Console.Error.WriteLine($"  - Title: '{w?.Title}'");
    }

    foreach (var el in childWindows)
    {
        var win = el.AsWindow();
        if (win?.Title != null &&
            win.Title.IndexOf(pouNameHint, StringComparison.OrdinalIgnoreCase) >= 0)
        {
            win.Focus();
            Thread.Sleep(300);
            return win;
        }
    }

        throw new Exception($"'{pouNameHint}' নামে কোনো POU window পাওয়া যায়নি।");
    }

        /// <summary>
        /// ST editor-এ পুরনো কোড মুছে নতুন কোড ক্লিপবোর্ড দিয়ে পেস্ট করে।
        /// UIA ValuePattern কাজ করে না (আগের discovery-তে কনফার্ম হয়েছে),
        /// তাই এটাই একমাত্র নির্ভরযোগ্য পথ।
        /// </summary>
        public void WriteSTCode(string pouNameHint, string stCode)
        {
            var pouWindow = FindAndFocusPouWindow(pouNameHint);

            // এডিটর এরিয়ার ভেতরে ক্লিক করে cursor বসানো (safety measure)
            // pouWindow-এর center point এ ক্লিক করছি; দরকার হলে নির্দিষ্ট
            // editor bounding rectangle অনুযায়ী কোঅর্ডিনেট টিউন করো।
            var bounds = pouWindow.BoundingRectangle;
            var clickPoint = new System.Drawing.Point(
                bounds.X + bounds.Width / 2,
                bounds.Y + bounds.Height / 2);
            Mouse.Click(clickPoint);
            Thread.Sleep(200);

            // পুরনো কোড সিলেক্ট ও ডিলিট
            Keyboard.Press(VirtualKeyShort.CONTROL);
            Keyboard.Type(VirtualKeyShort.KEY_A);
            Keyboard.Release(VirtualKeyShort.CONTROL);
            Thread.Sleep(100);
            Keyboard.Press(VirtualKeyShort.DELETE);
            Keyboard.Release(VirtualKeyShort.DELETE);
            Thread.Sleep(100);

            // ক্লিপবোর্ডে কোড বসিয়ে পেস্ট
            SetClipboardTextSafely(stCode);
            Keyboard.Press(VirtualKeyShort.CONTROL);
            Keyboard.Type(VirtualKeyShort.KEY_V);
            Keyboard.Release(VirtualKeyShort.CONTROL);
            Thread.Sleep(300);
        }

        /// <summary>
        /// বর্তমান POU-এর সম্পূর্ণ ST কোড ক্লিপবোর্ডের মাধ্যমে পড়ে।
        /// </summary>
        public string ReadSTCode(string pouNameHint)
        {
            var pouWindow = FindAndFocusPouWindow(pouNameHint);

            Keyboard.Press(VirtualKeyShort.CONTROL);
            Keyboard.Type(VirtualKeyShort.KEY_A);
            Keyboard.Release(VirtualKeyShort.CONTROL);
            Thread.Sleep(100);
            Keyboard.Press(VirtualKeyShort.CONTROL);
            Keyboard.Type(VirtualKeyShort.KEY_C);
            Keyboard.Release(VirtualKeyShort.CONTROL);
            Thread.Sleep(200);

            return GetClipboardTextSafely();
        }

        /// <summary>
        /// Rebuild All ট্রিগার করে (Shift+Alt+F4)।
        /// মূল উইন্ডো ফোকাস থাকা অবস্থায় শর্টকাট পাঠায়।
        /// </summary>
        public void TriggerRebuildAll()
        {
            _mainWindow.Focus();
            Thread.Sleep(200);

            Keyboard.Press(VirtualKeyShort.SHIFT);
            Keyboard.Press(VirtualKeyShort.ALT);
            Keyboard.Type(VirtualKeyShort.F4);
            Keyboard.Release(VirtualKeyShort.ALT);
            Keyboard.Release(VirtualKeyShort.SHIFT);
        }

        /// <summary>
        /// Compile শেষ হওয়া পর্যন্ত অপেক্ষা করে — Output DataGrid-এর row count
        /// স্থির (আর বাড়ছে না) হওয়া পর্যন্ত পোল করে, timeout সহ।
        /// </summary>
       public bool WaitForCompileComplete(int timeoutSeconds = 30)
{
    var deadline = DateTime.Now.AddSeconds(timeoutSeconds);
    int lastCount = -1;
    int stableChecks = 0;

    while (DateTime.Now < deadline)
    {
        int currentCount = TryGetRowCount();

        if (currentCount >= 0 && currentCount == lastCount)
        {
            stableChecks++;
            if (stableChecks >= 3)
                return true;
        }
        else
        {
            stableChecks = 0;
            lastCount = currentCount;
        }

        Thread.Sleep(500);
    }

    return false;
}

private int TryGetRowCount()
{
    try
    {
        var grid = FindOutputGrid();
        if (grid == null) return -1;
        return grid.Patterns.Grid.Pattern.RowCount;
    }
    catch (Exception)
    {
        return -1;
    }
}

        /// <summary>
        /// Output ট্যাবের ভেতরের DataGrid element খুঁজে বের করে।
        /// AutomationId "1003" আগের discovery থেকে কনফার্মড।
        /// </summary>
        private AutomationElement FindOutputGrid()
        {
            return _mainWindow.FindFirstDescendant(cf =>
                cf.ByAutomationId("1003").And(cf.ByControlType(ControlType.Custom)))
                ?? _mainWindow.FindFirstDescendant(cf => cf.ByAutomationId("1003"));
        }

        /// <summary>
        /// Compile error/warning রো-গুলো structured ফরম্যাটে রিটার্ন করে।
        /// GridPattern ব্যবহার করছে — টেক্সট স্ক্র্যাপিং লাগছে না।
        /// </summary>
        public List<CompileMessage> GetCompileErrors()
{
    var result = new List<CompileMessage>();

    try
    {
        var grid = FindOutputGrid();
        if (grid == null || !grid.Patterns.Grid.IsSupported)
        {
            Console.Error.WriteLine("Output grid পাওয়া যায়নি বা Grid pattern সাপোর্ট করে না।");
            return result;
        }

        var gridPattern = grid.Patterns.Grid.Pattern;
        int rowCount = gridPattern.RowCount;
        int colCount = gridPattern.ColumnCount;

        for (int row = 0; row < rowCount; row++)
        {
            var cells = new string[colCount];
            for (int col = 0; col < colCount; col++)
            {
                var item = gridPattern.GetItem(row, col);
                cells[col] = item?.Name ?? "";
            }

            result.Add(new CompileMessage
            {
                RowIndex = row,
                ResultType = cells.Length > 1 ? cells[1] : "",
                Detail = cells.Length > 2 ? cells[2] : "",
                Message = cells.Length > 3 ? cells[3] : string.Join(" | ", cells)
            });
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Compile error পড়তে সমস্যা হয়েছে: {ex.Message}");
        Console.Error.WriteLine("GridPattern কাজ করছে না — বিকল্প হিসেবে TreeWalker দিয়ে child element পড়ার চেষ্টা করা লাগতে পারে।");
    }

    return result;
}
        // --- Clipboard helper: STA thread lাগে, তাই wrapper দরকার ---

        private void SetClipboardTextSafely(string text)
        {
            var thread = new Thread(() => Clipboard.SetText(text));
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
        }

        private string GetClipboardTextSafely()
        {
            string result = "";
            var thread = new Thread(() =>
            {
                if (Clipboard.ContainsText())
                    result = Clipboard.GetText();
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            return result;
        }

        // --- preview→confirm write-safety ---

        /// <summary>
        /// একটা write preview তৈরি করে: বর্তমান POU কোড পড়ে, নতুন কোডের সাথে তুলনা করে,
        /// currentStateHash + একবার-ব্যবহার্য safetyToken রিটার্ন করে।
        /// আসল write হয় না — শুধু plan তৈরি হয়। Token ~১০ মিনিট পরে expire হয়।
        /// </summary>
        public PreviewResult CreateWritePreview(string pouName, string newCode, bool forCompile, int? timeoutSeconds)
        {
            string currentCode = ReadSTCode(pouName);
            string currentHash = HashHelper.ComputeSha256(currentCode);
            string newHash = HashHelper.ComputeSha256(newCode);

            // মেয়াদোত্তীর্ণ token গুলো ঝেড়ে ফেলি
            PurgeExpiredTokens();

            string token = Guid.NewGuid().ToString("N");
            _pendingWrites[token] = new PendingWrite
            {
                PouName = pouName,
                PendingCode = newCode,
                ExpectedHash = currentHash,
                Expiry = DateTime.Now.Add(PreviewTtl),
                IsForCompile = forCompile,
                TimeoutSeconds = timeoutSeconds
            };

            var diff = BuildLineDiff(currentCode, newCode);

            return new PreviewResult
            {
                SafetyToken = token,
                CurrentStateHash = currentHash,
                NewStateHash = newHash,
                NoChange = string.Equals(currentHash, newHash, StringComparison.Ordinal),
                CurrentLineCount = CountLines(currentCode),
                NewLineCount = CountLines(newCode),
                Diff = diff,
                ExpiresAt = _pendingWrites[token].Expiry
            };
        }

        /// <summary>
        /// safetyToken যাচাই করে ও consume করে (single-use)। বৈধ না হলে readable exception ছোঁড়ে।
        /// এখনো write হয় না — শুধু pending তথ্য ফেরত দেয়, hash-drift চেক করে।
        /// </summary>
        public PendingWrite ConsumeToken(string token, string pouName)
        {
            PurgeExpiredTokens();

            if (string.IsNullOrWhiteSpace(token))
                throw new InvalidOperationException(
                    "safetyToken দেওয়া হয়নি। আগে preview_* টুল কল করে safetyToken নিন, তারপর confirm=true সহ সেই token পাঠান।");

            if (!_pendingWrites.TryGetValue(token, out var pending))
                throw new InvalidOperationException(
                    "safetyToken অবৈধ বা ইতিমধ্যে ব্যবহৃত/মেয়াদোত্তীর্ণ। নতুন করে preview_* কল করে fresh token নিন।");

            // Token single-use — সাথে সাথে সরিয়ে ফেলি (সফল হোক বা ব্যর্থ)
            _pendingWrites.Remove(token);

            if (DateTime.Now > pending.Expiry)
                throw new InvalidOperationException("safetyToken মেয়াদোত্তীর্ণ (১০ মিনিট পার)। নতুন করে preview_* কল করুন।");

            if (!string.Equals(pending.PouName, pouName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"এই safetyToken '{pending.PouName}' POU-এর জন্য তৈরি, কিন্তু write করা হচ্ছে '{pouName}'-এ। মিল নেই।");

            // hash-drift চেক: preview-এর পর কেউ POU বদলে ফেলেছে কিনা
            string currentCode = ReadSTCode(pouName);
            string currentHash = HashHelper.ComputeSha256(currentCode);
            if (!string.Equals(currentHash, pending.ExpectedHash, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "POU-এর কোড preview-এর পর থেকে বদলে গেছে (hash mismatch)। নিরাপত্তার জন্য write বাতিল। " +
                    "আবার preview_* কল করে বর্তমান state যাচাই করুন।");

            return pending;
        }

        /// <summary>
        /// write করার পর read-back করে যাচাই করে কোড আসলেই বসেছে (truncation guard)।
        /// প্রত্যাশিত hash-এর সাথে না মিললে exception ছোঁড়ে।
        /// </summary>
        public void VerifyWrittenCode(string pouName, string expectedCode)
        {
            string actual = ReadSTCode(pouName);
            string actualHash = HashHelper.ComputeSha256(NormalizeForCompare(actual));
            string expectedHash = HashHelper.ComputeSha256(NormalizeForCompare(expectedCode));
            if (!string.Equals(actualHash, expectedHash, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "write verify ব্যর্থ: read-back করা কোড পাঠানো কোডের সাথে মেলেনি — সম্ভবত paste truncate হয়েছে। " +
                    $"(পাঠানো লাইন: {CountLines(expectedCode)}, read-back লাইন: {CountLines(actual)})");
            }
        }

        private void PurgeExpiredTokens()
        {
            var now = DateTime.Now;
            var expired = _pendingWrites.Where(kv => now > kv.Value.Expiry).Select(kv => kv.Key).ToList();
            foreach (var key in expired) _pendingWrites.Remove(key);
        }

        private static int CountLines(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            return s.Replace("\r\n", "\n").Split('\n').Length;
        }

        /// <summary>
        /// read-back তুলনার আগে trailing whitespace / line-ending স্বাভাবিক করে,
        /// যাতে এডিটরের যোগ করা শেষ newline জাতীয় সামান্য পার্থক্যে verify মিথ্যা-fail না করে।
        /// </summary>
        private static string NormalizeForCompare(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var lines = s.Replace("\r\n", "\n").Split('\n').Select(l => l.TrimEnd());
            return string.Join("\n", lines).TrimEnd();
        }

        /// <summary>
        /// সরল line-by-line diff — preview-তে দেখানোর জন্য (+ যোগ, - বাদ, সমান লাইন বাদ)।
        /// </summary>
        private static List<string> BuildLineDiff(string oldCode, string newCode)
        {
            var oldLines = (oldCode ?? "").Replace("\r\n", "\n").Split('\n');
            var newLines = (newCode ?? "").Replace("\r\n", "\n").Split('\n');
            var diff = new List<string>();
            int max = Math.Max(oldLines.Length, newLines.Length);
            for (int i = 0; i < max; i++)
            {
                string o = i < oldLines.Length ? oldLines[i] : null;
                string n = i < newLines.Length ? newLines[i] : null;
                if (o == n) continue;
                if (o != null) diff.Add($"- {o}");
                if (n != null) diff.Add($"+ {n}");
            }
            if (diff.Count == 0) diff.Add("(কোনো পরিবর্তন নেই)");
            return diff;
        }

        public void Dispose()
        {
            _automation?.Dispose();
        }
    }

    /// <summary>
    /// preview_* টুলের ফলাফল — safetyToken, hash ও diff সহ।
    /// </summary>
    public class PreviewResult
    {
        public string SafetyToken { get; set; }
        public string CurrentStateHash { get; set; }
        public string NewStateHash { get; set; }
        public bool NoChange { get; set; }
        public int CurrentLineCount { get; set; }
        public int NewLineCount { get; set; }
        public List<string> Diff { get; set; }
        public DateTime ExpiresAt { get; set; }
    }

    public class CompileMessage
    {
        public int RowIndex { get; set; }
        public string ResultType { get; set; } // "Error", "Warning" ইত্যাদি
        public string Detail { get; set; }     // POU নাম
        public string Message { get; set; }    // এরর বার্তা
    }

    /// <summary>
    /// preview→confirm safety store-এ রাখা pending write-এর তথ্য।
    /// </summary>
    public class PendingWrite
    {
        public string PouName { get; set; }
        public string PendingCode { get; set; }
        public string ExpectedHash { get; set; }  // preview সময়ের POU state-এর hash
        public DateTime Expiry { get; set; }
        public bool IsForCompile { get; set; }    // true → write_compile_and_get_errors, false → write_st_code
        public int? TimeoutSeconds { get; set; }  // compile-এর জন্য timeout (IsForCompile=true হলে)
    }

    /// <summary>
    /// ST কোডের SHA256 hash compute করার helper।
    /// </summary>
    internal static class HashHelper
    {
        public static string ComputeSha256(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            using var sha256 = SHA256.Create();
            var bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(text));
            return Convert.ToHexString(bytes);
        }
    }

    /// <summary>
    /// MCP stdio server-এর এন্ট্রি পয়েন্ট।
    /// stdout = JSON-RPC protocol channel, তাই সব log stderr-এ পাঠানো হয়।
    /// GXWorks3Controller singleton — attach-state সব tool call-এর মধ্যে টিকে থাকে।
    /// </summary>
    public class Program
    {
        public static async Task Main(string[] args)
        {
            var builder = Host.CreateApplicationBuilder(args);

            // গুরুত্বপূর্ণ: stdio MCP-তে stdout প্রোটোকলের জন্য সংরক্ষিত।
            // সব log অবশ্যই stderr-এ যেতে হবে, নইলে JSON-RPC ভেঙে যাবে।
            builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

            builder.Services.AddSingleton<GXWorks3Controller>();

            builder.Services
                .AddMcpServer()
                .WithStdioServerTransport()
                .WithToolsFromAssembly();

            await builder.Build().RunAsync();
        }
    }

    /// <summary>
    /// GX Works3-এর ক্ষমতাগুলো MCP tool হিসেবে expose করে।
    /// Controller singleton method-parameter injection দিয়ে DI থেকে আসে;
    /// বাকি প্যারামিটার Claude পাঠায়। প্রতিটা টুল SyncRoot lock-এ চলে যাতে
    /// একই সময়ে দুটো কল UI নাড়াচাড়া না মেশায়।
    /// </summary>
    [McpServerToolType]
    public static class GXWorks3Tools
    {
        private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

        // --- preview tools (write-safety pattern) ---

        [McpServerTool, Description("write_st_code-এর preview: বর্তমান POU কোড পড়ে, নতুন কোডের সাথে diff দেখায়, currentStateHash ও safetyToken ফেরত দেয়। আসল write হয় না। তারপর write_st_code-এ confirm=true + safetyToken পাঠিয়ে apply করতে হবে।")]
        public static string preview_write_st_code(
            GXWorks3Controller controller,
            [Description("POU window টাইটেলের অংশ, যেমন ProgPou")] string pouName,
            [Description("যে ST কোড বসাতে চাও (preview শুধু দেখায়, লেখে না)")] string stCode)
        {
            lock (controller.SyncRoot)
            {
                try
                {
                    controller.EnsureAttached();
                    var preview = controller.CreateWritePreview(pouName, stCode, forCompile: false, timeoutSeconds: null);
                    return JsonSerializer.Serialize(preview, JsonOpts);
                }
                catch (Exception ex)
                {
                    return $"ERROR: {ex.Message}";
                }
            }
        }

        [McpServerTool, Description("write_compile_and_get_errors-এর preview: বর্তমান POU কোড পড়ে, নতুন কোডের সাথে diff দেখায়, currentStateHash ও safetyToken ফেরত দেয়। আসল write হয় না। তারপর write_compile_and_get_errors-এ confirm=true + safetyToken পাঠিয়ে apply করতে হবে।")]
        public static string preview_write_compile_and_get_errors(
            GXWorks3Controller controller,
            [Description("POU window টাইটেলের অংশ, যেমন ProgPou")] string pouName,
            [Description("যে ST কোড বসাতে চাও (preview শুধু দেখায়, লেখে না)")] string stCode,
            [Description("কম্পাইল শেষ হওয়ার জন্য সর্বোচ্চ কত সেকেন্ড অপেক্ষা (preview-তে শুধু store হয়, এখনো compile হয় না)")] int timeoutSeconds = 30)
        {
            lock (controller.SyncRoot)
            {
                try
                {
                    controller.EnsureAttached();
                    var preview = controller.CreateWritePreview(pouName, stCode, forCompile: true, timeoutSeconds: timeoutSeconds);
                    return JsonSerializer.Serialize(preview, JsonOpts);
                }
                catch (Exception ex)
                {
                    return $"ERROR: {ex.Message}";
                }
            }
        }

        // --- ordinary tools (now gated with confirm + safetyToken) ---

        [McpServerTool, Description("চলমান GX Works3-এ attach করে এবং বর্তমানে খোলা সব window/POU-এর টাইটেল তালিকা দেয়। POU-এর সঠিক নাম জানতে প্রথমে এটা কল করুন।")]
        public static string attach_and_list_pous(GXWorks3Controller controller)
        {
            lock (controller.SyncRoot)
            {
                try
                {
                    controller.EnsureAttached();
                    var titles = controller.ListPouWindows();
                    return JsonSerializer.Serialize(new { attached = true, windows = titles }, JsonOpts);
                }
                catch (Exception ex)
                {
                    return $"ERROR: {ex.Message}";
                }
            }
        }

        [McpServerTool, Description("নির্দিষ্ট POU-তে ST কোড লেখে — পুরনো কোড সম্পূর্ণ মুছে নতুন কোড বসায়। নিরাপত্তার জন্য আগে preview_write_st_code কল করে safetyToken নিতে হবে, তারপর এখানে confirm=true + সেই safetyToken পাঠাতে হবে। লেখার পর read-back করে যাচাই করা হয় (truncation guard)।")]
        public static string write_st_code(
            GXWorks3Controller controller,
            [Description("POU window টাইটেলের অংশ, যেমন ProgPou")] string pouName,
            [Description("যে ST কোড বসাতে হবে")] string stCode,
            [Description("অবশ্যই true হতে হবে — নিশ্চিত করে যে তুমি preview দেখে write অনুমোদন করছ")] bool confirm = false,
            [Description("preview_write_st_code থেকে পাওয়া safetyToken")] string safetyToken = "")
        {
            lock (controller.SyncRoot)
            {
                try
                {
                    controller.EnsureAttached();

                    if (!confirm)
                        return "ERROR: confirm=false। write বাতিল। আগে preview_write_st_code কল করে diff দেখুন, তারপর confirm=true + safetyToken সহ আবার কল করুন।";

                    var pending = controller.ConsumeToken(safetyToken, pouName);

                    // Token-এর pending কোড আর tool-এ পাঠানো কোড একই কিনা — নাহলে token অন্য কোডের জন্য
                    if (!string.Equals(pending.PendingCode, stCode, StringComparison.Ordinal))
                        return "ERROR: safetyToken যে কোডের জন্য preview করা হয়েছিল, এই stCode তার সাথে মেলে না। একই কোড দিয়ে আবার preview করুন।";

                    controller.WriteSTCode(pouName, stCode);
                    controller.VerifyWrittenCode(pouName, stCode);
                    return $"OK: '{pouName}'-এ ST কোড লেখা ও যাচাই সম্পন্ন হয়েছে।";
                }
                catch (Exception ex)
                {
                    return $"ERROR: {ex.Message}";
                }
            }
        }

        [McpServerTool, Description("নির্দিষ্ট POU-এর বর্তমান সম্পূর্ণ ST কোড পড়ে ফেরত দেয়।")]
        public static string read_st_code(
            GXWorks3Controller controller,
            [Description("POU window টাইটেলের অংশ, যেমন ProgPou")] string pouName)
        {
            lock (controller.SyncRoot)
            {
                try
                {
                    controller.EnsureAttached();
                    return controller.ReadSTCode(pouName);
                }
                catch (Exception ex)
                {
                    return $"ERROR: {ex.Message}";
                }
            }
        }

        [McpServerTool, Description("Rebuild All চালায়, কম্পাইল শেষ হওয়া পর্যন্ত অপেক্ষা করে, তারপর সব compile error/warning JSON হিসেবে ফেরত দেয়।")]
        public static string compile_and_get_errors(
            GXWorks3Controller controller,
            [Description("কম্পাইল শেষ হওয়ার জন্য সর্বোচ্চ কত সেকেন্ড অপেক্ষা")] int timeoutSeconds = 30)
        {
            lock (controller.SyncRoot)
            {
                try
                {
                    controller.EnsureAttached();
                    controller.TriggerRebuildAll();
                    bool done = controller.WaitForCompileComplete(timeoutSeconds);
                    var errors = controller.GetCompileErrors();
                    return JsonSerializer.Serialize(new { compileCompleted = done, count = errors.Count, results = errors }, JsonOpts);
                }
                catch (Exception ex)
                {
                    return $"ERROR: {ex.Message}";
                }
            }
        }

        [McpServerTool, Description("এক ধাপে: POU-তে ST কোড লেখে → Rebuild All চালায় → compile error/warning JSON-এ ফেরত দেয়। নিরাপত্তার জন্য আগে preview_write_compile_and_get_errors কল করে safetyToken নিতে হবে, তারপর এখানে confirm=true + সেই safetyToken পাঠাতে হবে। লেখার পর read-back করে যাচাই করা হয় (truncation guard)।")]
        public static string write_compile_and_get_errors(
            GXWorks3Controller controller,
            [Description("POU window টাইটেলের অংশ, যেমন ProgPou")] string pouName,
            [Description("যে ST কোড বসাতে হবে")] string stCode,
            [Description("কম্পাইল শেষ হওয়ার জন্য সর্বোচ্চ কত সেকেন্ড অপেক্ষা")] int timeoutSeconds = 30,
            [Description("অবশ্যই true হতে হবে — নিশ্চিত করে যে তুমি preview দেখে write অনুমোদন করছ")] bool confirm = false,
            [Description("preview_write_compile_and_get_errors থেকে পাওয়া safetyToken")] string safetyToken = "")
        {
            lock (controller.SyncRoot)
            {
                try
                {
                    controller.EnsureAttached();

                    if (!confirm)
                        return "ERROR: confirm=false। write বাতিল। আগে preview_write_compile_and_get_errors কল করে diff দেখুন, তারপর confirm=true + safetyToken সহ আবার কল করুন।";

                    var pending = controller.ConsumeToken(safetyToken, pouName);

                    if (!pending.IsForCompile)
                        return "ERROR: এই safetyToken write_st_code-এর জন্য তৈরি, write_compile_and_get_errors-এর জন্য নয়। সঠিক preview_* টুল দিয়ে আবার শুরু করুন।";

                    if (!string.Equals(pending.PendingCode, stCode, StringComparison.Ordinal))
                        return "ERROR: safetyToken যে কোডের জন্য preview করা হয়েছিল, এই stCode তার সাথে মেলে না। একই কোড দিয়ে আবার preview করুন।";

                    controller.WriteSTCode(pouName, stCode);
                    controller.VerifyWrittenCode(pouName, stCode);
                    controller.TriggerRebuildAll();
                    bool done = controller.WaitForCompileComplete(pending.TimeoutSeconds ?? timeoutSeconds);
                    var errors = controller.GetCompileErrors();
                    return JsonSerializer.Serialize(new { wrote = true, verified = true, compileCompleted = done, count = errors.Count, results = errors }, JsonOpts);
                }
                catch (Exception ex)
                {
                    return $"ERROR: {ex.Message}";
                }
            }
        }
    }
}