// Dev Junk Cleaner - https://github.com/bhskr44/dev-junk-cleaner (MIT License)
// Frees space by deleting dev folders that can always be
// re-created (node_modules, Composer vendor, build outputs, tool caches) and
// by finding duplicate large files. Build with build.cmd (no install needed).
//
// Safety rules:
//  - a folder is only listed when it is clearly a dependency/cache folder
//    (node_modules next to package.json, vendor next to composer.json, ...)
//  - the skip list, junctions/symlinks, .git and system folders are never touched
//  - folders changed in the last N days are listed but NOT ticked
//  - nothing is deleted until you press Delete and confirm
//
// Speed: folders are listed with FindFirstFileEx (large fetch, no short names)
// on several threads, marker files (package.json, ...) are noted while listing
// the parent instead of checked one by one, and results appear while scanning.
// Written for the C# 5 compiler that ships with Windows (csc v4).

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

// Turns on modern path handling, so \\?\ long paths (deep node_modules) work.
[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8")]
[assembly: System.Reflection.AssemblyTitle("Dev Junk Cleaner")]
[assembly: System.Reflection.AssemblyProduct("Dev Junk Cleaner")]
[assembly: System.Reflection.AssemblyVersion("1.2.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.2.0.0")]
[assembly: System.Reflection.AssemblyCopyright("MIT License - https://github.com/bhskr44/dev-junk-cleaner")]

namespace DevJunkCleaner
{
    class Item
    {
        public string Kind, Path, Note;
        public long Bytes;
        public int Files;
        public bool Deletable, Checked, IsFile;
        // What "Apply ticked" does: delete, caches (delete several folders), move (to another drive + link),
        // wsl (move a Linux disk), temp (old temp files), recycle, admin (UAC command), info (nothing).
        public string Action = "delete";
        public string Target, Block, Extra;   // move destination / admin args; processes that must be closed; WSL name / exe
        public List<string> Paths;            // for "caches"
        public bool Junk;                     // found by the dev junk rules (re-checked before deleting)
    }

    // Totals of one folder tree, filled in by several threads.
    class Tree
    {
        public long Bytes, Newest;   // Newest = UTC file time of the latest change
        public int Files, Unsafe, Pending;
        public Action Done;
        public bool Safe { get { return Unsafe == 0; } }

        public void Add(long bytes, int files, long newest)
        {
            if (bytes != 0) Interlocked.Add(ref Bytes, bytes);
            if (files != 0) Interlocked.Add(ref Files, files);
            long cur;
            while (newest > (cur = Interlocked.Read(ref Newest)) && Interlocked.CompareExchange(ref Newest, newest, cur) != cur) { }
        }
    }

    class FileRec
    {
        public string Path, Quick, Full;
        public long Size, Write;
    }

    static class Native
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct FindData
        {
            public uint Attr;
            public uint CreateLow, CreateHigh, AccessLow, AccessHigh, WriteLow, WriteHigh;
            public uint SizeHigh, SizeLow, Reserved0, Reserved1;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Name;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string AltName;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr FindFirstFileExW(string name, int infoLevel, out FindData data, int searchOp, IntPtr filter, int flags);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool FindNextFileW(IntPtr h, out FindData data);
        [DllImport("kernel32.dll")]
        static extern bool FindClose(IntPtr h);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool DeleteFileW(string path);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool RemoveDirectoryW(string path);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool SetFileAttributesW(string path, uint attr);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern int SHEmptyRecycleBinW(IntPtr hwnd, string root, uint flags);

        public const uint Directory = 0x10, Reparse = 0x400, Normal = 0x80;

        // Delete a file, clearing read-only first if needed.
        public static bool DeleteFile(string path)
        {
            if (DeleteFileW(path)) return true;
            SetFileAttributesW(path, Normal);
            return DeleteFileW(path);
        }

        // Remove an empty folder - or, for a junction/symlink, only the link (never its target).
        public static bool RemoveDir(string path)
        {
            if (RemoveDirectoryW(path)) return true;
            SetFileAttributesW(path, Directory);
            return RemoveDirectoryW(path);
        }
        const int InfoBasic = 1, LargeFetch = 2;

        public delegate void Entry(string name, uint attr, long size, long writeTime);

        // Lists one folder; false when it can't be read.
        public static bool List(string dir, Entry entry)
        {
            FindData d;
            IntPtr h = FindFirstFileExW(dir.TrimEnd('\\') + "\\*", InfoBasic, out d, 0, IntPtr.Zero, LargeFetch);
            if (h == new IntPtr(-1)) return Marshal.GetLastWin32Error() == 2;
            try
            {
                do
                {
                    if (d.Name == "." || d.Name == "..") continue;
                    entry(d.Name, d.Attr, ((long)d.SizeHigh << 32) | d.SizeLow, ((long)d.WriteHigh << 32) | d.WriteLow);
                } while (FindNextFileW(h, out d));
            }
            finally { FindClose(h); }
            return true;
        }
    }

    // A small thread pool: Post work (work may post more), Run until all is done.
    class Pool
    {
        readonly ConcurrentStack<Action> work = new ConcurrentStack<Action>();
        readonly SemaphoreSlim signal = new SemaphoreSlim(0);
        int pending, threads;

        public void Post(Action a)
        {
            Interlocked.Increment(ref pending);
            work.Push(a);
            signal.Release();
        }

        public void Run(int count)
        {
            threads = Math.Max(1, count);
            if (Volatile.Read(ref pending) == 0) return;
            var list = new List<Thread>();
            for (int i = 0; i < threads; i++)
            {
                var t = new Thread(Loop) { IsBackground = true };
                list.Add(t);
                t.Start();
            }
            foreach (var t in list) t.Join();
        }

        void Loop()
        {
            while (true)
            {
                signal.Wait();
                Action a;
                if (!work.TryPop(out a)) return;   // everything is done
                try { a(); } catch { }
                if (Interlocked.Decrement(ref pending) == 0) signal.Release(threads);
            }
        }
    }

    static class Util
    {
        const string LongPrefix = @"\\?\";
        public static string Long(string p) { return p.StartsWith(LongPrefix) ? p : LongPrefix + p; }
        public static string Plain(string p) { return p.StartsWith(LongPrefix) ? p.Substring(LongPrefix.Length) : p; }

        // Full path without trailing "\" - except drive roots ("D:" alone means "current folder on D").
        public static string Full(string p)
        {
            string f = System.IO.Path.GetFullPath(Plain(p.Trim().Trim('"'))).TrimEnd('\\');
            if (Regex.IsMatch(f, "^[A-Za-z]:$")) f += "\\";
            return f;
        }

        public static bool Under(string path, string baseDir)
        {
            string p = Plain(path).TrimEnd('\\') + "\\";
            string b = baseDir.TrimEnd('\\') + "\\";
            return p.StartsWith(b, StringComparison.OrdinalIgnoreCase);
        }

        public static string Size(double b)
        {
            if (b >= 1L << 30) return (b / (1L << 30)).ToString("N2") + " GB";
            if (b >= 1L << 20) return (b / (1L << 20)).ToString("N1") + " MB";
            if (b >= 1L << 10) return (b / (1L << 10)).ToString("N0") + " KB";
            return b + " B";
        }

        public static bool IsReparse(string p)
        {
            try { return (File.GetAttributes(Long(p)) & FileAttributes.ReparsePoint) != 0; }
            catch { return true; }
        }

        public static List<string> Lines(string text)
        {
            var list = new List<string>();
            foreach (var raw in text.Split('\n'))
            {
                string t = raw.Trim().Trim('"');
                if (t.Length == 0 || t.StartsWith("#")) continue;
                list.Add(t);
            }
            return list;
        }
    }

    class Scanner
    {
        public List<string> Skip = new List<string>();
        public HashSet<string> Categories = new HashSet<string>();
        public int RecentDays = 14, Threads = 8;
        public long MinDupBytes = 100L << 20;
        public CancellationToken Token;
        public Action<string> Progress = delegate { };
        public Action<Item> Found = delegate { };
        public int Warnings;
        public bool Stopped;

        Pool pool;
        long cutoff;
        int scanned, lastTick;
        readonly ConcurrentBag<Item> found = new ConcurrentBag<Item>();

        public static readonly string[][] AllCategories =
        {
            new[] { "node_modules",   "node_modules  (next to package.json)" },
            new[] { "vendor",         "Composer vendor  (next to composer.json)" },
            new[] { "dart_tool",      "Flutter .dart_tool  (next to pubspec.yaml)" },
            new[] { "flutter_build",  "Flutter build output" },
            new[] { "gradle_build",   "Android / Gradle build, .gradle, .cxx" },
            new[] { "js_build_cache", ".next .nuxt .angular .turbo .parcel-cache ..." },
            new[] { "python_cache",   "Python __pycache__ / .pytest_cache ..." },
            new[] { "download_cache", "npm / Yarn / pip download caches" },
        };

        static readonly HashSet<string> SystemNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "$RECYCLE.BIN", "System Volume Information", "Windows", "Program Files", "Program Files (x86)",
            "ProgramData", "Recovery", "PerfLogs", "Config.Msi", "MSOCache", "$WinREAgent", ".git"
        };
        static readonly string[] GradleMarkers = { "build.gradle", "build.gradle.kts", "settings.gradle", "settings.gradle.kts" };
        static readonly HashSet<string> MarkerNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "package.json", "composer.json", "pubspec.yaml", "build.gradle", "build.gradle.kts", "settings.gradle", "settings.gradle.kts" };
        static readonly HashSet<string> JsCache = new HashSet<string> { ".next", ".nuxt", ".svelte-kit", ".angular", ".turbo", ".parcel-cache" };
        static readonly HashSet<string> PyCache = new HashSet<string> { "__pycache__", ".pytest_cache", ".mypy_cache", ".ruff_cache" };
        // Only folders with one of these names can match a rule; all others are just walked through.
        static readonly HashSet<string> CandidateNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "node_modules", "vendor", ".dart_tool", "build", ".gradle", ".cxx",
            ".next", ".nuxt", ".svelte-kit", ".angular", ".turbo", ".parcel-cache",
            "__pycache__", ".pytest_cache", ".mypy_cache", ".ruff_cache",
            "_cacache", "_npx", "cache", ".yarn-cache", "pip"
        };

        public bool Skipped(string p)
        {
            foreach (var s in Skip) if (Util.Under(p, s)) return true;
            return false;
        }

        // The category of a folder that is safe to delete, or null.
        // sibling(names) returns the first of names that exists next to the folder.
        static string Rule(string name, string fullPath, Func<string[], string> sibling, out string marker, out string restore)
        {
            marker = null; restore = null;
            if (name == "node_modules" && (marker = sibling(new[] { "package.json" })) != null) { restore = "npm install"; return "node_modules"; }
            if (name == "vendor" && (marker = sibling(new[] { "composer.json" })) != null) { restore = "composer install"; return "vendor"; }
            if (name == ".dart_tool" && (marker = sibling(new[] { "pubspec.yaml" })) != null) { restore = "flutter pub get"; return "dart_tool"; }
            if (name == "build" && (marker = sibling(new[] { "pubspec.yaml" })) != null) { restore = "re-created by the next flutter build"; return "flutter_build"; }
            if ((name == "build" || name == ".gradle" || name == ".cxx") && (marker = sibling(GradleMarkers)) != null) { restore = "re-created by the next Gradle build"; return "gradle_build"; }
            if (JsCache.Contains(name) && (marker = sibling(new[] { "package.json" })) != null) { restore = "re-created by npm run dev/build"; return "js_build_cache"; }
            if (PyCache.Contains(name)) { restore = "Python re-creates it"; return "python_cache"; }
            // Download caches: only well-known layouts, never a random folder called "cache".
            string lower = Util.Plain(fullPath).TrimEnd('\\').ToLowerInvariant();
            if ((name == "_cacache" || name == "_npx") && Regex.IsMatch(lower, @"\\(npm-cache|\.npm)\\[^\\]+$")) { restore = "npm downloads again when needed"; return "download_cache"; }
            if (Regex.IsMatch(lower, @"\\(yarn\\cache|\.yarn-cache|\.yarn\\berry\\cache)$")) { restore = "Yarn downloads again when needed"; return "download_cache"; }
            if (Regex.IsMatch(lower, @"\\(pip\\cache|\.cache\\pip)$")) { restore = "pip downloads again when needed"; return "download_cache"; }
            return null;
        }

        // Same rule, checking the disk directly (used again right before deleting).
        public static string RuleOnDisk(string dir)
        {
            string full = Util.Long(dir).TrimEnd('\\');
            string name = System.IO.Path.GetFileName(full), parent = System.IO.Path.GetDirectoryName(full);
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(parent)) return null;
            string m, r;
            return Rule(name, full, names => names.FirstOrDefault(n => File.Exists(System.IO.Path.Combine(parent, n))), out m, out r);
        }

        void Tick(string dir, string what)
        {
            int n = Interlocked.Increment(ref scanned);
            int now = Environment.TickCount, last = lastTick;
            if (now - last > 250 && Interlocked.CompareExchange(ref lastTick, now, last) == last)
                Progress(string.Format("{0}: {1:N0} folders, {2} found...  {3}", what, n, found.Count, Util.Plain(dir)));
        }

        // ── dev junk ──
        public List<Item> ScanJunk(IEnumerable<string> roots)
        {
            cutoff = DateTime.Now.AddDays(-RecentDays).ToFileTimeUtc();
            pool = new Pool();
            foreach (var root in roots)
            {
                if (!Directory.Exists(root)) continue;
                string r = Util.Long(root);
                pool.Post(() => ScanDir(r));
            }
            pool.Run(Threads);
            Stopped = Token.IsCancellationRequested;
            Progress(string.Format("Scanned {0:N0} folders.", scanned));
            return found.ToList();
        }

        void ScanDir(string dir)
        {
            if (Token.IsCancellationRequested) return;
            Tick(dir, "Scanning");
            var subdirs = new List<string>();
            Dictionary<string, long> markers = null;
            bool ok = Native.List(dir, (name, attr, size, wt) =>
            {
                if ((attr & Native.Directory) != 0)
                {
                    if ((attr & Native.Reparse) == 0 && !SystemNames.Contains(name)) subdirs.Add(name);
                }
                else if (MarkerNames.Contains(name))
                {
                    if (markers == null) markers = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                    markers[name] = wt;
                }
            });
            if (!ok) { Interlocked.Increment(ref Warnings); return; }

            string prefix = dir.TrimEnd('\\') + "\\";
            var mk = markers;
            foreach (var name in subdirs)
            {
                string child = prefix + name;
                if (Skipped(child)) continue;
                if (CandidateNames.Contains(name))
                {
                    string marker, restore;
                    string cat = Rule(name, child, names => mk == null ? null : names.FirstOrDefault(n => mk.ContainsKey(n)), out marker, out restore);
                    if (cat != null)
                    {
                        // Never walk into a match: what is inside is all part of it.
                        if (Categories.Contains(cat)) MeasureCandidate(child, cat, restore, marker != null ? mk[marker] : 0);
                        continue;
                    }
                }
                pool.Post(() => ScanDir(child));
            }
        }

        void MeasureCandidate(string dir, string cat, string restore, long markerTime)
        {
            var t = new Tree { Pending = 1 };
            t.Done = () =>
            {
                if (Token.IsCancellationRequested || t.Bytes == 0) return;
                var it = new Item { Kind = cat, Path = Util.Plain(dir), Bytes = t.Bytes, Files = t.Files, Junk = true };
                if (!t.Safe) it.Note = "kept: contains a link or unreadable file";
                else if (t.Newest > cutoff || markerTime > cutoff)
                {
                    it.Deletable = true;
                    it.Note = "changed in the last " + RecentDays + " days (probably in use) - restore: " + restore;
                }
                else { it.Deletable = true; it.Checked = true; it.Note = "restore: " + restore; }
                found.Add(it);
                Found(it);
            };
            pool.Post(() => MeasureDir(t, dir));
        }

        // Big trees (node_modules) are measured by all threads at once.
        void MeasureDir(Tree t, string dir)
        {
            if (!Token.IsCancellationRequested)
            {
                long bytes = 0, newest = 0; int files = 0;
                string prefix = dir.TrimEnd('\\') + "\\";
                bool ok = Native.List(dir, (name, attr, size, wt) =>
                {
                    if ((attr & Native.Reparse) != 0) { t.Unsafe = 1; return; }
                    if ((attr & Native.Directory) != 0)
                    {
                        Interlocked.Increment(ref t.Pending);
                        string sub = prefix + name;
                        pool.Post(() => MeasureDir(t, sub));
                    }
                    else { bytes += size; files++; if (wt > newest) newest = wt; }
                });
                if (!ok) t.Unsafe = 1;
                t.Add(bytes, files, newest);
            }
            if (Interlocked.Decrement(ref t.Pending) == 0) t.Done();
        }

        // One-thread measure, used right before deleting.
        public static Tree Measure(string dir)
        {
            var t = new Tree();
            var stack = new Stack<string>();
            stack.Push(Util.Long(dir));
            while (stack.Count > 0)
            {
                string d = stack.Pop();
                string prefix = d.TrimEnd('\\') + "\\";
                if (!Native.List(d, (name, attr, size, wt) =>
                {
                    if ((attr & Native.Reparse) != 0) t.Unsafe = 1;
                    else if ((attr & Native.Directory) != 0) stack.Push(prefix + name);
                    else t.Add(size, 1, wt);
                })) t.Unsafe = 1;
            }
            return t;
        }

        // ── duplicates ──
        public List<Item> ScanDuplicates(IEnumerable<string> roots)
        {
            var bySize = new ConcurrentDictionary<long, ConcurrentBag<FileRec>>();
            pool = new Pool();
            foreach (var root in roots)
            {
                if (!Directory.Exists(root)) continue;
                string r = Util.Long(root);
                pool.Post(() => DupDir(r, bySize));
            }
            pool.Run(Threads);
            var items = new List<Item>();
            if (Token.IsCancellationRequested) { Stopped = true; return items; }

            var groups = bySize.Values.Where(b => b.Count > 1).Select(b => b.ToList()).ToList();
            var files = groups.SelectMany(g => g).ToList();
            var po = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Threads), CancellationToken = Token };
            int done = 0;
            try
            {
                // 1) quick check: first and last 1 MB. Most same-size files differ already here.
                Parallel.ForEach(files, po, f =>
                {
                    f.Quick = QuickHash(f);
                    Progress(string.Format("Quick compare {0} of {1}: {2}", Interlocked.Increment(ref done), files.Count, Util.Plain(f.Path)));
                });
                // 2) full SHA-256 only for files whose quick check matched another file.
                var needFull = groups.SelectMany(g => g.Where(f => f.Quick != null).GroupBy(f => f.Quick).Where(q => q.Count() > 1).SelectMany(q => q)).ToList();
                done = 0;
                Parallel.ForEach(needFull, po, f =>
                {
                    f.Full = Hash(f.Path, 0, -1);
                    Progress(string.Format("Full compare {0} of {1}: {2}", Interlocked.Increment(ref done), needFull.Count, Util.Plain(f.Path)));
                });
                int group = 0;
                foreach (var same in needFull.Where(f => f.Full != null).GroupBy(f => f.Size + ":" + f.Full).Where(g => g.Count() > 1).OrderByDescending(g => g.First().Size))
                {
                    group++;
                    var ordered = same.OrderBy(f => f.Write).ToList();
                    string keep = Util.Plain(ordered[0].Path);
                    for (int i = 0; i < ordered.Count; i++)
                        items.Add(new Item
                        {
                            Kind = "duplicate #" + group, Path = Util.Plain(ordered[i].Path), Bytes = ordered[i].Size, Files = 1, IsFile = true,
                            Deletable = i > 0,
                            Note = i == 0 ? "KEEP - oldest copy" : "same content as " + keep,
                        });
                }
                Progress(string.Format("Found {0} duplicate groups.", group));
            }
            catch (OperationCanceledException) { Stopped = true; }
            return items;
        }

        void DupDir(string dir, ConcurrentDictionary<long, ConcurrentBag<FileRec>> bySize)
        {
            if (Token.IsCancellationRequested) return;
            Tick(dir, "Listing large files");
            string prefix = dir.TrimEnd('\\') + "\\";
            var subdirs = new List<string>();
            bool ok = Native.List(dir, (name, attr, size, wt) =>
            {
                if ((attr & Native.Reparse) != 0) return;
                if ((attr & Native.Directory) != 0)
                {
                    if (!SystemNames.Contains(name) && name != "node_modules") subdirs.Add(prefix + name);
                }
                else if (size >= MinDupBytes)
                    bySize.GetOrAdd(size, s => new ConcurrentBag<FileRec>()).Add(new FileRec { Path = prefix + name, Size = size, Write = wt });
            });
            if (!ok) { Interlocked.Increment(ref Warnings); return; }
            foreach (var sd in subdirs)
            {
                if (Skipped(sd)) continue;
                string s = sd;
                pool.Post(() => DupDir(s, bySize));
            }
        }

        string QuickHash(FileRec f)
        {
            const long chunk = 1L << 20;
            if (f.Size <= 2 * chunk) return Hash(f.Path, 0, -1);
            string a = Hash(f.Path, 0, chunk), b = Hash(f.Path, f.Size - chunk, chunk);
            return a == null || b == null ? null : a + b;
        }

        // SHA-256 of length bytes from offset (length -1 = to the end).
        string Hash(string path, long offset, long length)
        {
            try
            {
                using (var sha = SHA256.Create())
                using (var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan))
                {
                    s.Position = offset;
                    var buf = new byte[1 << 20];
                    long left = length < 0 ? long.MaxValue : length;
                    int n;
                    while (left > 0 && (n = s.Read(buf, 0, (int)Math.Min(buf.Length, left))) > 0)
                    {
                        if (Token.IsCancellationRequested) return null;
                        sha.TransformBlock(buf, 0, n, null, 0);
                        left -= n;
                    }
                    sha.TransformFinalBlock(buf, 0, 0);
                    return BitConverter.ToString(sha.Hash);
                }
            }
            catch { Interlocked.Increment(ref Warnings); return null; }
        }

        // ── deleting ──
        // All ticked items are deleted together by the thread pool: every folder
        // deletes its own files, its sub-folders run as separate work, and a folder
        // is removed once all its sub-folders are gone. Links inside are removed as
        // links (their targets are never touched), like rd /s does.
        class DelNode
        {
            public string Path;
            public DelNode Parent;
            public Action<bool> Done;   // set on the top folder only: true when it is gone
            public int Pending = 1;     // 1 for listing this folder + 1 per sub-folder
        }

        class Countdown { public int Left, Failed; }

        public readonly ConcurrentDictionary<Item, bool> Deleted = new ConcurrentDictionary<Item, bool>();   // items applied successfully
        public readonly ConcurrentBag<string> Errors = new ConcurrentBag<string>();
        int deletedFiles, itemsDone, itemsTotal;

        // Runs every ticked item: moves, WSL, admin commands etc. one by one, then all deletes in parallel.
        public void Apply(List<Item> items)
        {
            itemsTotal = items.Count;
            var running = RunningProcesses();
            bool hibernateOff = items.Any(i => i.Action == "admin" && i.Target == "/h off");
            var deletes = new List<Item>();
            foreach (var it in items)
            {
                string block = BlockedBy(it, running);
                if (block != null) { Errors.Add(it.Path + "  -  close " + block + " first, then try again"); ItemDone(); continue; }
                if (Skipped(it.Path)) { Errors.Add(it.Path + "  -  it is in the skip list"); ItemDone(); continue; }
                if (it.Action == "delete" || it.Action == "caches") { deletes.Add(it); continue; }
                if (Token.IsCancellationRequested) { Errors.Add(it.Path + "  -  stopped"); ItemDone(); continue; }
                if (hibernateOff && it.Target == "/h /type reduced") { Deleted[it] = true; ItemDone(); continue; }   // "off" covers it
                Progress("Working on: " + it.Kind + "  " + it.Path);
                try
                {
                    switch (it.Action)
                    {
                        case "move": MoveWithLink(it.Path, it.Target); break;
                        case "wsl": MoveWsl(it.Extra, it.Target); break;
                        case "temp": CleanTemp(it.Path, 2); break;
                        case "recycle": Native.SHEmptyRecycleBinW(IntPtr.Zero, SystemDrive + "\\", 7); break;
                        case "admin": RunAdmin(it.Extra, it.Target); break;
                        default: throw new Exception("nothing to do for this row");
                    }
                    Deleted[it] = true;
                }
                catch (Exception ex) { Errors.Add(it.Path + "  -  " + ex.Message); }
                ItemDone();
            }
            DeleteAll(deletes);
        }

        void DeleteAll(List<Item> items)
        {
            pool = new Pool();
            foreach (var it in items)
            {
                // Quick re-check right before deleting.
                string why = null;
                if (Util.IsReparse(it.Path)) why = "it is a link";
                else if (it.Junk && !it.IsFile && RuleOnDisk(it.Path) != it.Kind) why = "it no longer looks like a " + it.Kind + " folder";
                if (why != null) { Errors.Add(it.Path + "  -  " + why); ItemDone(); continue; }

                var item = it;
                if (it.IsFile)
                {
                    pool.Post(() =>
                    {
                        if (Native.DeleteFile(Util.Long(item.Path))) Deleted[item] = true;
                        else Errors.Add(item.Path + "  -  " + new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()).Message);
                        ItemDone();
                    });
                    continue;
                }
                var paths = it.Paths ?? new List<string> { it.Path };
                var count = new Countdown { Left = paths.Count };
                foreach (var p in paths)
                {
                    var node = new DelNode { Path = Util.Long(p).TrimEnd('\\') };
                    node.Done = ok =>
                    {
                        if (!ok) Interlocked.Increment(ref count.Failed);
                        if (Interlocked.Decrement(ref count.Left) > 0) return;
                        if (count.Failed == 0) Deleted[item] = true;
                        else if (Token.IsCancellationRequested) Errors.Add(item.Path + "  -  stopped");
                        else Errors.Add(item.Path + "  -  " + (item.Action == "caches"
                            ? "partly cleaned: some files are in use (close the app and run again to finish)"
                            : "some files could not be deleted (open in another program?)"));
                        ItemDone();
                    };
                    pool.Post(() => DelDir(node));
                }
            }
            pool.Run(Threads);
            Progress(string.Format("Deleted {0:N0} files.", deletedFiles));
        }

        void ItemDone()
        {
            int n = Interlocked.Increment(ref itemsDone);
            Progress(string.Format("Working... {0:N0} files deleted, {1} of {2} items done", deletedFiles, n, itemsTotal));
        }

        void DelDir(DelNode n)
        {
            if (!Token.IsCancellationRequested)
            {
                string prefix = n.Path + "\\";
                int gone = 0;
                Native.List(n.Path, (name, attr, size, wt) =>
                {
                    string p = prefix + name;
                    if ((attr & Native.Reparse) != 0)
                    {
                        if ((attr & Native.Directory) != 0) Native.RemoveDir(p); else Native.DeleteFile(p);
                    }
                    else if ((attr & Native.Directory) != 0)
                    {
                        var child = new DelNode { Path = p, Parent = n };
                        Interlocked.Increment(ref n.Pending);
                        pool.Post(() => DelDir(child));
                    }
                    else if (Native.DeleteFile(p)) gone++;
                });
                if (gone > 0)
                {
                    int total = Interlocked.Add(ref deletedFiles, gone);
                    int now = Environment.TickCount, last = lastTick;
                    if (now - last > 250 && Interlocked.CompareExchange(ref lastTick, now, last) == last)
                        Progress(string.Format("Working... {0:N0} files deleted, {1} of {2} items done", total, itemsDone, itemsTotal));
                }
            }
            // This folder is done listing; remove it (and parents) once nothing is left inside.
            while (n != null && Interlocked.Decrement(ref n.Pending) == 0)
            {
                Native.RemoveDir(n.Path);
                if (n.Parent == null) n.Done(!Directory.Exists(n.Path));
                n = n.Parent;
            }
        }

        // One-thread delete of a whole tree (links removed as links). True when it is gone.
        static bool DeleteTree(string dir)
        {
            string d = Util.Long(dir).TrimEnd('\\'), prefix = d + "\\";
            var subs = new List<string>();
            Native.List(d, (name, attr, size, wt) =>
            {
                string p = prefix + name;
                if ((attr & Native.Reparse) != 0) { if ((attr & Native.Directory) != 0) Native.RemoveDir(p); else Native.DeleteFile(p); }
                else if ((attr & Native.Directory) != 0) subs.Add(p);
                else Native.DeleteFile(p);
            });
            foreach (var s in subs) DeleteTree(s);
            return Native.RemoveDir(d) || !Directory.Exists(d);
        }

        // ── system drive (C:) actions ──
        public static readonly string SystemDrive = (Environment.GetEnvironmentVariable("SystemDrive") ?? "C:").TrimEnd('\\');
        static HashSet<string> RunningProcesses()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in Process.GetProcesses()) { try { set.Add(p.ProcessName); } catch { } finally { p.Dispose(); } }
            return set;
        }

        static string BlockedBy(Item it, HashSet<string> running)
        {
            if (string.IsNullOrEmpty(it.Block)) return null;
            foreach (var b in it.Block.Split(',')) if (running.Contains(b.Trim())) return b.Trim() + ".exe";
            return null;
        }

        static int RunHidden(string exe, string args, out string output, bool unicode = false)
        {
            var psi = new ProcessStartInfo(exe, args) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            if (unicode) psi.StandardOutputEncoding = System.Text.Encoding.Unicode;
            using (var p = Process.Start(psi))
            {
                var err = p.StandardError.ReadToEndAsync();
                output = p.StandardOutput.ReadToEnd();
                p.WaitForExit();
                output = (output + err.Result).Replace("\0", "").Trim();
                return p.ExitCode;
            }
        }

        // Copy to the other drive, delete the original, leave a junction so every program still finds it.
        void MoveWithLink(string src, string dest)
        {
            if (Util.IsReparse(src)) throw new Exception("it is already a link");
            if (Util.Under(dest, src)) throw new Exception("the destination is inside the folder");
            Directory.CreateDirectory(dest);
            string output;
            // /XO keeps newer files that are already at the destination.
            int code = RunHidden("robocopy.exe", "\"" + src + "\" \"" + dest + "\" /E /XO /XJ /COPY:DAT /DCOPY:DAT /R:1 /W:1 /MT:16 /NFL /NDL /NJH /NP", out output);
            if (code >= 8) throw new Exception("copy failed (robocopy code " + code + "), nothing was removed from C:");
            if (Token.IsCancellationRequested) throw new Exception("stopped after copying; the original was kept");
            if (!DeleteTree(src)) throw new Exception("copied to " + dest + ", but some original files are in use - close the program and run again (it continues where it stopped)");
            if (RunHidden("cmd.exe", "/d /c mklink /J \"" + src + "\" \"" + dest + "\"", out output) != 0 || !Util.IsReparse(src))
                throw new Exception("moved to " + dest + " but the link could not be made: " + output);
        }

        static void MoveWsl(string name, string dest)
        {
            string list;
            RunHidden("wsl.exe", "-l -v", out list, true);
            foreach (var line in list.Split('\n'))
            {
                var cols = line.Replace("*", " ").Split(new[] { ' ', '\t', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                if (cols.Length >= 2 && cols[0].Equals(name, StringComparison.OrdinalIgnoreCase) && cols[1].Equals("Running", StringComparison.OrdinalIgnoreCase))
                    throw new Exception(name + " is running - close its terminals and run \"wsl --shutdown\" first");
            }
            Directory.CreateDirectory(dest);
            string output;
            if (RunHidden("wsl.exe", "--manage \"" + name + "\" --move \"" + dest + "\"", out output, true) != 0)
                throw new Exception("wsl --manage --move failed: " + output);
        }

        static void RunAdmin(string exe, string args)
        {
            try
            {
                var psi = new ProcessStartInfo(exe, args) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
                using (var p = Process.Start(psi))
                {
                    p.WaitForExit();
                    if (p.ExitCode != 0) throw new Exception(exe + " " + args + " failed (code " + p.ExitCode + ")");
                }
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                if (ex.NativeErrorCode == 1223) throw new Exception("the admin (UAC) prompt was cancelled");
                throw;
            }
        }

        // Deletes what in the temp folder has not changed for `days` days; files in use are skipped.
        static void CleanTemp(string temp, int days)
        {
            long cut = DateTime.Now.AddDays(-days).ToFileTimeUtc();
            string d = Util.Long(temp).TrimEnd('\\'), prefix = d + "\\";
            var entries = new List<KeyValuePair<string, bool>>();
            Native.List(d, (name, attr, size, wt) =>
            {
                if ((attr & Native.Reparse) != 0) return;
                bool dir = (attr & Native.Directory) != 0;
                if (dir || wt < cut) entries.Add(new KeyValuePair<string, bool>(prefix + name, dir));
            });
            foreach (var e in entries)
            {
                if (!e.Value) { Native.DeleteFile(e.Key); continue; }
                var t = Measure(e.Key);
                if (t.Safe && t.Newest < cut) DeleteTree(e.Key);
            }
        }

        static IEnumerable<string> Dirs(string path)
        {
            try { return new DirectoryInfo(path).GetDirectories().Where(d => (d.Attributes & FileAttributes.ReparsePoint) == 0).Select(d => d.FullName).ToList(); }
            catch { return new string[0]; }
        }

        static readonly HashSet<string> AppCacheNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "Cache", "Code Cache", "GPUCache", "DawnCache", "DawnGraphiteCache", "DawnWebGPUCache", "GrShaderCache", "ShaderCache" };

        public string MoveRoot = "";   // empty = no "move" suggestions

        // Finds what can be freed on C: (nothing is changed here).
        public List<Item> ScanC()
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var running = RunningProcesses();
            var items = new List<Item>();
            Func<string, bool> usable = p => Directory.Exists(p) && !Util.IsReparse(p) && !Skipped(p);
            Action<Item> add = it => { if (it.Action == "info" || it.Action == "admin" || it.Paths != null || usable(it.Path)) items.Add(it); };

            string sys = SystemDrive;
            bool canMove = MoveRoot.Length > 2 && !MoveRoot.StartsWith(sys, StringComparison.OrdinalIgnoreCase);
            string moveKind = canMove ? "move to " + MoveRoot.Substring(0, 2) : null;
            Progress("Looking at " + sys + " ...");
            // 1. Safe to delete: re-created by the apps when needed.
            foreach (var d in Dirs(local))
            {
                if (!File.Exists(System.IO.Path.Combine(d, "Update.exe"))) continue;   // Squirrel-installed app (GitHub Desktop, Postman, ...)
                string app = System.IO.Path.GetFileName(d);
                var versions = Dirs(d).Select(a => new { Path = a, Name = System.IO.Path.GetFileName(a) })
                    .Where(a => a.Name.StartsWith("app-")).Select(a => { Version v; return new { a.Path, a.Name, V = Version.TryParse(a.Name.Substring(4), out v) ? v : null }; })
                    .Where(a => a.V != null).OrderByDescending(a => a.V).ToList();
                for (int i = 1; i < versions.Count; i++)
                    add(new Item { Kind = "old app version", Path = versions[i].Path, Block = app, Note = "older copy of " + app + " - the newest (" + versions[0].Name + ") is kept" });
            }
            add(new Item { Kind = "installer leftovers", Path = System.IO.Path.Combine(local, "SquirrelTemp"), Note = "temporary files from app installers" });
            add(new Item { Kind = "crash reports", Path = System.IO.Path.Combine(local, "CrashDumps"), Note = "crash dumps of programs" });
            add(new Item { Kind = "crash reports", Path = System.IO.Path.Combine(local, @"Microsoft\Windows\WER"), Note = "Windows error reports" });
            foreach (var root in new[] { local, roaming })
                foreach (var d in Dirs(root))
                {
                    add(new Item { Kind = "crash reports", Path = System.IO.Path.Combine(d, "Crashpad"), Note = "crash reports of " + System.IO.Path.GetFileName(d) });
                    foreach (var d2 in Dirs(d)) add(new Item { Kind = "crash reports", Path = System.IO.Path.Combine(d2, "Crashpad"), Note = "crash reports of " + System.IO.Path.GetFileName(d2) });
                }
            add(new Item { Kind = "shader cache", Path = System.IO.Path.Combine(local, "D3DSCache"), Note = "DirectX shader cache - rebuilt by games/apps" });
            add(new Item { Kind = "shader cache", Path = System.IO.Path.Combine(local, @"NVIDIA\DXCache"), Note = "NVIDIA shader cache - rebuilt automatically" });
            add(new Item { Kind = "shader cache", Path = System.IO.Path.Combine(local, @"NVIDIA\GLCache"), Note = "NVIDIA shader cache - rebuilt automatically" });
            add(new Item { Kind = "tool cache", Path = System.IO.Path.Combine(local, ".dartServer"), Block = "dart", Note = "Dart analyzer cache - rebuilt automatically (no downloads)" });
            add(new Item { Kind = "tool cache", Path = System.IO.Path.Combine(roaming, @"Code\CachedExtensionVSIXs"), Block = "Code", Note = "VS Code extension installers already installed" });
            add(new Item { Kind = "temp files", Path = System.IO.Path.Combine(local, "Temp"), Action = "temp", Note = "old temp folder - only things unchanged for 2+ days; files in use are skipped" });
            try
            {
                string sid = System.Security.Principal.WindowsIdentity.GetCurrent().User.Value;
                add(new Item { Kind = "recycle bin", Path = sys + @"\$Recycle.Bin\" + sid, Action = "recycle", Note = "empties the " + sys + " Recycle Bin" });
            }
            catch { }

            // Browser / Electron app caches: Cache, Code Cache, GPUCache ... next to a Preferences or Local State file.
            var cacheGroups = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var root in new[] { local, roaming })
            {
                var stack = new Stack<KeyValuePair<string, int>>();
                stack.Push(new KeyValuePair<string, int>(root, 0));
                while (stack.Count > 0)
                {
                    var cur = stack.Pop();
                    bool profile = File.Exists(System.IO.Path.Combine(cur.Key, "Preferences")) || File.Exists(System.IO.Path.Combine(cur.Key, "Local State"));
                    foreach (var d in Dirs(cur.Key))
                    {
                        string name = System.IO.Path.GetFileName(d);
                        if (profile && AppCacheNames.Contains(name))
                        {
                            var segs = d.Substring(root.Length).TrimStart('\\').Split('\\');
                            string label = segs[0];
                            if (segs.Length > 2 && (label == "Google" || label == "Microsoft" || label == "BraveSoftware" || label == "Mozilla")) label += "\\" + segs[1];
                            string key = System.IO.Path.Combine(root, label);
                            if (!cacheGroups.ContainsKey(key)) cacheGroups[key] = new List<string>();
                            cacheGroups[key].Add(d);
                        }
                        else if (cur.Value < 5 && name != "Packages" && name != "Temp" && name != "node_modules" && name != "Docker" && name != "wsl")
                            stack.Push(new KeyValuePair<string, int>(d, cur.Value + 1));
                    }
                }
            }
            foreach (var g in cacheGroups)
            {
                string block = g.Key.EndsWith(@"Google\Chrome") ? "chrome" : g.Key.EndsWith(@"Microsoft\Edge") ? "msedge"
                             : g.Key.Contains("BraveSoftware") ? "brave" : g.Key.EndsWith(@"Roaming\Code") ? "Code" : null;
                add(new Item { Kind = "app cache", Path = g.Key, Paths = g.Value.Where(p => !Skipped(p)).ToList(), Action = "caches", Block = block,
                    Note = g.Value.Count + " cache folders (Cache, Code Cache, GPUCache) - refilled as you use the app" + (block == null ? "; files in use are skipped" : "") });
            }

            // 2. Move to another drive and leave a link - nothing has to be downloaded again.
            var moves = new[]
            {
                new[] { home, ".gradle", "gradle", "java,studio64", "Gradle cache" },
                new[] { local, @"Pub\Cache", "pub", "dart,flutter", "Flutter/Dart packages" },
                new[] { local, "npm-cache", "npm-cache", "node", "npm cache" },
                new[] { roaming, "npm-cache", "npm-cache", "node", "npm cache" },
                new[] { home, @".nuget\packages", "nuget-packages", "devenv,dotnet", "NuGet packages" },
                new[] { local, @"pip\cache", "pip-cache", "python", "pip cache" },
                new[] { local, "Composer", "composer", "php", "Composer cache" },
                new[] { local, "uv", "uv", "uv,python", "uv (Python) cache" },
                new[] { local, "Yarn", "yarn", "node,yarn", "Yarn cache" },
                new[] { local, "ms-playwright", "ms-playwright", "node", "Playwright browsers" },
                new[] { home, @".android\avd", "android-avd", "qemu-system-x86_64,emulator,studio64", "Android emulators" },
                new[] { local, @"Android\Sdk", "android-sdk", "studio64,adb,java", "Android SDK" },
                new[] { home, @".vscode\extensions", @"vscode\extensions", "Code", "VS Code extensions" },
                new[] { roaming, "Code", @"vscode\user-data", "Code", "VS Code settings and data" },
                new[] { home, ".ollama", "ollama", "ollama,ollama app", "Ollama models" },
                new[] { home, @".cache\huggingface", "huggingface", "python", "Hugging Face models" },
            };
            foreach (var m in moves)
            {
                if (!canMove) break;
                string target = System.IO.Path.Combine(MoveRoot, m[2]);
                add(new Item { Kind = moveKind, Path = System.IO.Path.Combine(m[0], m[1]), Action = "move", Target = target, Block = m[3],
                    Note = m[4] + " -> " + target + " (a link is left here, so nothing is downloaded again)" });
            }
            try
            {
                using (var lxss = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Lxss"))
                    if (lxss != null)
                        foreach (var k in lxss.GetSubKeyNames())
                            using (var key = lxss.OpenSubKey(k))
                            {
                                string name = key.GetValue("DistributionName") as string, basePath = Util.Plain(key.GetValue("BasePath") as string ?? "");
                                if (name == null || name.StartsWith("docker-desktop") || !canMove || !basePath.StartsWith(sys, StringComparison.OrdinalIgnoreCase)) continue;
                                string target = System.IO.Path.Combine(MoveRoot, @"WSL\" + name);
                                add(new Item { Kind = moveKind, Path = basePath, Action = "wsl", Extra = name, Target = target,
                                    Note = "WSL Linux \"" + name + "\" -> " + target + " (wsl --manage --move; Linux must be stopped)" });
                            }
            }
            catch { }

            // 3. Needs admin, or done inside another app: shown with instructions.
            long hiber = 0, page = 0;
            Native.List(sys + "\\", (name, attr, size, wt) =>
            {
                if (name.Equals("hiberfil.sys", StringComparison.OrdinalIgnoreCase)) hiber = size;
                if (name.Equals("pagefile.sys", StringComparison.OrdinalIgnoreCase)) page = size;
            });
            if (hiber > 0)
            {
                items.Add(new Item { Kind = "admin", Path = sys + @"\hiberfil.sys", Bytes = hiber / 2, Action = "admin", Extra = "powercfg.exe", Target = "/h /type reduced",
                    Note = "Make the hibernation file smaller (keeps fast startup, no full hibernate). Asks for admin." });
                items.Add(new Item { Kind = "admin", Path = sys + @"\hiberfil.sys", Bytes = hiber, Action = "admin", Extra = "powercfg.exe", Target = "/h off",
                    Note = "Turn hibernation off (also turns off fast startup). Asks for admin." });
            }
            if (page > 4L << 30)
                items.Add(new Item { Kind = "info", Path = sys + @"\pagefile.sys", Bytes = page, Action = "info",
                    Note = "Windows swap file. To shrink: Settings > System > About > Advanced system settings > Performance Settings > Advanced > Virtual memory > Change: " + sys + " custom 4096-8192 MB (or none) and another drive System managed, then restart." });
            string docker = System.IO.Path.Combine(local, @"Docker\wsl");
            if (Directory.Exists(docker))
                items.Add(new Item { Kind = "info", Path = docker, Action = "info",
                    Note = "Docker disk. Move it in Docker Desktop > Settings > Resources > Advanced > Disk image location (choose a folder on another drive)." });
            string model = System.IO.Path.Combine(local, @"Google\Chrome\User Data\OptGuideOnDeviceModel");
            if (Directory.Exists(model))
                items.Add(new Item { Kind = "info", Path = model, Action = "info",
                    Note = "Chrome's on-device AI model. To remove: Chrome Settings > System > turn off \"On-device AI\" - Chrome then deletes it." });
            string downloads = System.IO.Path.Combine(home, "Downloads");
            if (Directory.Exists(downloads))
                items.Add(new Item { Kind = "info", Path = downloads, Action = "info", Note = "Your Downloads folder - check it for installers and files you no longer need." });

            // Sizes, measured in parallel; tiny entries are dropped.
            var po = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Threads), CancellationToken = Token };
            int done = 0;
            try
            {
                Parallel.ForEach(items, po, it =>
                {
                    Progress(string.Format("Measuring {0} of {1}: {2}", Interlocked.Increment(ref done), items.Count, it.Path));
                    if (it.Bytes > 0) return;
                    foreach (var p in it.Paths ?? new List<string> { it.Path })
                    {
                        var t = Measure(p);
                        it.Bytes += t.Bytes; it.Files += t.Files;
                    }
                });
            }
            catch (OperationCanceledException) { Stopped = true; }

            var result = new List<Item>();
            foreach (var it in items)
            {
                long min = it.Action == "move" || it.Action == "wsl" ? 50L << 20 : it.Action == "info" ? 200L << 20 : 1L << 20;
                if (it.Bytes < min) continue;
                string block = BlockedBy(it, running);
                it.Deletable = it.Action != "info";
                it.Checked = it.Deletable && it.Action != "admin" && block == null;
                if (block != null) it.Note = "Close " + block + " first. " + it.Note;
                result.Add(it);
            }
            Progress("Done looking at C:.");
            return result;
        }
    }

    class MainForm : Form
    {
        readonly string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        // Settings live in %LOCALAPPDATA% so the app also works from a read-only install folder.
        static readonly string DataDir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DevJunkCleaner");
        string SkipFile { get { return System.IO.Path.Combine(DataDir, "cleanup-skip.txt"); } }
        string SettingsFile { get { return System.IO.Path.Combine(DataDir, "cleanup-settings.ini"); } }
        static readonly string Version = typeof(MainForm).Assembly.GetName().Version.ToString(3);

        // Every fixed drive that is ready.
        static List<DriveInfo> FixedDrives()
        {
            return DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady).ToList();
        }

        // Default "move to": the non-system drive with the most free space, or none.
        static string DefaultMoveRoot()
        {
            var other = FixedDrives().Where(d => !d.Name.StartsWith(Scanner.SystemDrive, StringComparison.OrdinalIgnoreCase))
                                     .OrderByDescending(d => d.AvailableFreeSpace).FirstOrDefault();
            return other == null ? "" : System.IO.Path.Combine(other.Name, "DevCache");
        }

        TextBox txtRoots, txtSkip, txtMoveTo;
        NumericUpDown numDays, numThreads, numDupMb;
        CheckedListBox lstCats;
        ListView list;
        Button btnScan, btnDup, btnC, btnStop, btnDelete, btnAll, btnNone, btnClear;
        ToolStripStatusLabel status, totals;
        CancellationTokenSource cts;
        readonly ConcurrentQueue<Item> incoming = new ConcurrentQueue<Item>();
        readonly System.Windows.Forms.Timer flush = new System.Windows.Forms.Timer { Interval = 400 };
        string lastProgress;
        bool loading;

        public MainForm()
        {
            Text = "Dev Junk Cleaner " + Version;
            Font = new Font("Segoe UI", 9f);
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Size = new Size(1250, 760);
            MinimumSize = new Size(900, 560);
            StartPosition = FormStartPosition.CenterScreen;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            // ── left: settings ──
            var left = new Panel { Dock = DockStyle.Left, Width = 330, Padding = new Padding(10) };
            int y = 10;
            Func<string, int, int, Label> label = (s, x, w) => { var l = new Label { Text = s, Left = x, Top = y, Width = w, Height = 18 }; left.Controls.Add(l); return l; };

            label("Drives / folders to scan (one per line):", 10, 310); y += 20;
            txtRoots = new TextBox { Left = 10, Top = y, Width = 305, Height = 52, Multiline = true, ScrollBars = ScrollBars.Vertical };
            left.Controls.Add(txtRoots); y += 58;

            label("Skip these folders (one per line - paste here):", 10, 310); y += 20;
            txtSkip = new TextBox { Left = 10, Top = y, Width = 305, Height = 140, Multiline = true, ScrollBars = ScrollBars.Both, WordWrap = false, AcceptsReturn = true };
            left.Controls.Add(txtSkip); y += 144;
            var btnAddSkip = new Button { Text = "Add folder to skip...", Left = 10, Top = y, Width = 150, Height = 28 };
            btnAddSkip.Click += (s, e) => PickSkipFolder();
            var btnAddRoot = new Button { Text = "Add folder to scan...", Left = 165, Top = y, Width = 150, Height = 28 };
            btnAddRoot.Click += (s, e) => PickRootFolder();
            left.Controls.Add(btnAddSkip); left.Controls.Add(btnAddRoot); y += 36;

            label("Move " + Scanner.SystemDrive + " caches to (another drive):", 10, 310); y += 20;
            txtMoveTo = new TextBox { Left = 10, Top = y, Width = 305, Text = DefaultMoveRoot() };
            left.Controls.Add(txtMoveTo); y += 30;

            label("Recently used (days):", 10, 150);
            label("Parallel threads:", 165, 150); y += 20;
            numDays = new NumericUpDown { Left = 10, Top = y, Width = 80, Minimum = 0, Maximum = 3650, Value = 14 };
            numThreads = new NumericUpDown { Left = 165, Top = y, Width = 80, Minimum = 1, Maximum = 64, Value = Math.Min(16, Math.Max(4, Environment.ProcessorCount)) };
            left.Controls.Add(numDays); left.Controls.Add(numThreads); y += 32;

            label("What to look for:", 10, 310); y += 20;
            lstCats = new CheckedListBox { Left = 10, Top = y, Width = 305, Height = 150, CheckOnClick = true, IntegralHeight = false };
            foreach (var c in Scanner.AllCategories) lstCats.Items.Add(c[1], true);
            left.Controls.Add(lstCats); y += 156;

            label("Duplicates: only files of at least (MB):", 10, 310); y += 20;
            numDupMb = new NumericUpDown { Left = 10, Top = y, Width = 80, Minimum = 1, Maximum = 100000, Value = 100 };
            left.Controls.Add(numDupMb); y += 34;

            left.Controls.Add(new Label
            {
                Left = 10, Top = y, Width = 305, Height = 80, ForeColor = Color.DimGray,
                Text = "" +
                       "Grey rows are tips you do yourself. Orange rows were used recently, so they are not ticked. " +
                       "Right-click a row to skip or open it."
            });

            // ── top: buttons ──
            // Wraps to a second row instead of hiding buttons when the window is narrow or text is scaled up.
            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, Padding = new Padding(6, 6, 6, 4) };
            btnScan = Btn("Scan for dev junk", 140, (s, e) => Run(0));
            btnDup = Btn("Find duplicate files", 140, (s, e) => Run(1));
            btnC = Btn("Free up " + Scanner.SystemDrive + " drive", 130, (s, e) => Run(2));
            btnStop = Btn("Stop", 70, (s, e) => { if (cts != null) cts.Cancel(); });
            btnAll = Btn("Tick all", 80, (s, e) => SetAll(true));
            btnNone = Btn("Untick all", 80, (s, e) => SetAll(false));
            btnClear = Btn("Clear list", 85, (s, e) => ClearList());
            btnDelete = Btn("Apply ticked", 220, (s, e) => DeleteChecked());
            btnDelete.MinimumSize = new Size(220, 38);
            btnDelete.Font = new Font(Font, FontStyle.Bold);
            btnDelete.BackColor = Color.FromArgb(4, 120, 87);
            btnDelete.ForeColor = Color.White;
            btnDelete.FlatStyle = FlatStyle.Flat;
            // Bottom bar: the one button that changes anything, always visible.
            var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(6, 4, 6, 6) };
            actions.Controls.Add(btnDelete);
            actions.Controls.Add(new Label { AutoSize = true, Margin = new Padding(3, 12, 10, 3), ForeColor = Color.DimGray,
                Text = "1. Scan   2. Tick what you want (untick the rest)   3. Press Apply ticked" });
            btnStop.Enabled = false;
            bar.Controls.AddRange(new Control[] { btnScan, btnDup, btnC, btnStop, btnAll, btnNone, btnClear });

            // ── middle: results ──
            list = new ListView { Dock = DockStyle.Fill, View = View.Details, CheckBoxes = true, FullRowSelect = true, GridLines = true };
            list.Columns.Add("Size", 90, HorizontalAlignment.Right);
            list.Columns.Add("Type", 120);
            list.Columns.Add("Path", 520);
            list.Columns.Add("Note", 330);
            list.ItemCheck += (s, e) => { var it = (Item)list.Items[e.Index].Tag; if (!it.Deletable) e.NewValue = CheckState.Unchecked; };
            list.ItemChecked += (s, e) => { if (!loading) UpdateTotals(); };
            list.ColumnClick += (s, e) => SortBy(e.Column);
            var menu = new ContextMenuStrip();
            menu.Items.Add("Skip this folder from now on", null, (s, e) => SkipSelected());
            menu.Items.Add("Open in Explorer", null, (s, e) => OpenSelected());
            menu.Items.Add("Copy path", null, (s, e) => { if (list.SelectedItems.Count > 0) Clipboard.SetText(((Item)list.SelectedItems[0].Tag).Path); });
            list.ContextMenuStrip = menu;

            var strip = new StatusStrip();
            status = new ToolStripStatusLabel { Spring = true, TextAlign = ContentAlignment.MiddleLeft, Text = "Ready. Nothing changes until you press Apply ticked." };
            totals = new ToolStripStatusLabel { Text = "" };
            strip.Items.Add(status); strip.Items.Add(totals);

            Controls.Add(list);
            Controls.Add(actions);
            Controls.Add(bar);
            Controls.Add(left);
            Controls.Add(strip);

            // Results and progress reach the screen a few times a second, not once per folder.
            flush.Tick += (s, e) => FlushIncoming();

            LoadSettings();
            FormClosing += (s, e) => SaveSettings();
            ShowFreeSpace();
        }

        Button Btn(string text, int width, EventHandler click)
        {
            var b = new Button { Text = text, AutoSize = true, MinimumSize = new Size(width, 30) };
            b.Click += click;
            return b;
        }

        // ── settings ──
        void LoadSettings()
        {
            // First run of this version: bring over settings kept next to the exe by older versions.
            try
            {
                Directory.CreateDirectory(DataDir);
                foreach (var name in new[] { "cleanup-skip.txt", "cleanup-settings.ini" })
                {
                    string old = System.IO.Path.Combine(baseDir, name), now = System.IO.Path.Combine(DataDir, name);
                    if (File.Exists(old) && !File.Exists(now)) File.Copy(old, now);
                }
            }
            catch { }
            txtRoots.Text = string.Join("\r\n", FixedDrives().Select(d => d.Name));
            if (File.Exists(SkipFile)) txtSkip.Text = File.ReadAllText(SkipFile).Replace("\r\n", "\n").Replace("\n", "\r\n");
            else txtSkip.Text = "# One folder per line. Everything inside a listed folder is left alone.\r\n# Lines starting with # are comments.\r\n";
            if (!File.Exists(SettingsFile)) return;
            foreach (var line in File.ReadAllLines(SettingsFile))
            {
                int i = line.IndexOf('=');
                if (i < 0) continue;
                string k = line.Substring(0, i).Trim(), v = line.Substring(i + 1).Trim();
                int n;
                if (k == "roots" && v.Length > 0) txtRoots.Text = string.Join("\r\n", v.Split('|'));
                if (k == "days" && int.TryParse(v, out n)) numDays.Value = Math.Max(numDays.Minimum, Math.Min(numDays.Maximum, n));
                if (k == "moveto") txtMoveTo.Text = v;
                if (k == "threads" && int.TryParse(v, out n)) numThreads.Value = Math.Max(numThreads.Minimum, Math.Min(numThreads.Maximum, n));
                if (k == "dupmb" && int.TryParse(v, out n)) numDupMb.Value = Math.Max(numDupMb.Minimum, Math.Min(numDupMb.Maximum, n));
                if (k == "categories")
                {
                    var on = new HashSet<string>(v.Split(','));
                    for (int c = 0; c < Scanner.AllCategories.Length; c++) lstCats.SetItemChecked(c, on.Contains(Scanner.AllCategories[c][0]));
                }
            }
        }

        void SaveSettings()
        {
            try
            {
                File.WriteAllText(SkipFile, txtSkip.Text.TrimEnd() + "\r\n");
                var cats = new List<string>();
                for (int c = 0; c < Scanner.AllCategories.Length; c++) if (lstCats.GetItemChecked(c)) cats.Add(Scanner.AllCategories[c][0]);
                File.WriteAllLines(SettingsFile, new[]
                {
                    "roots=" + string.Join("|", Util.Lines(txtRoots.Text)),
                    "days=" + numDays.Value,
                    "threads=" + numThreads.Value,
                    "moveto=" + txtMoveTo.Text.Trim(),
                    "dupmb=" + numDupMb.Value,
                    "categories=" + string.Join(",", cats),
                });
            }
            catch (Exception ex) { status.Text = "Could not save settings: " + ex.Message; }
        }

        Scanner MakeScanner()
        {
            var sc = new Scanner { RecentDays = (int)numDays.Value, Threads = (int)numThreads.Value, MinDupBytes = (long)numDupMb.Value << 20 };
            try { sc.MoveRoot = txtMoveTo.Text.Trim().Length == 0 ? "" : Util.Full(txtMoveTo.Text); }
            catch { MessageBox.Show(this, "\"Move caches to\" is not a valid folder.", "Free up C:", MessageBoxButtons.OK, MessageBoxIcon.Warning); return null; }
            foreach (var s in Util.Lines(txtSkip.Text))
            {
                try { sc.Skip.Add(Util.Full(s)); }
                catch { MessageBox.Show(this, "This skip line is not a valid folder path:\n\n" + s, "Skip list", MessageBoxButtons.OK, MessageBoxIcon.Warning); return null; }
            }
            sc.Skip.Add(Util.Full(baseDir));   // never clean the tool's own folder
            for (int c = 0; c < Scanner.AllCategories.Length; c++) if (lstCats.GetItemChecked(c)) sc.Categories.Add(Scanner.AllCategories[c][0]);
            sc.Progress = msg => { lastProgress = msg; };
            return sc;
        }

        List<string> Roots()
        {
            var roots = new List<string>();
            foreach (var r in Util.Lines(txtRoots.Text))
            {
                try { roots.Add(Util.Full(r)); }
                catch { MessageBox.Show(this, "Not a valid folder to scan:\n\n" + r, "Scan", MessageBoxButtons.OK, MessageBoxIcon.Warning); return null; }
            }
            return roots;
        }

        void Busy(bool busy)
        {
            btnScan.Enabled = btnDup.Enabled = btnC.Enabled = btnDelete.Enabled = btnAll.Enabled = btnNone.Enabled = btnClear.Enabled = !busy;
            txtRoots.ReadOnly = txtSkip.ReadOnly = txtMoveTo.ReadOnly = busy;
            numThreads.Enabled = !busy;
            btnStop.Enabled = busy;
            UseWaitCursor = busy;
            lastProgress = null;
            if (busy) flush.Start(); else { flush.Stop(); FlushIncoming(); }
        }

        void FlushIncoming()
        {
            string p = lastProgress;
            if (p != null) status.Text = p;
            if (incoming.IsEmpty) return;
            loading = true;
            list.BeginUpdate();
            Item it;
            while (incoming.TryDequeue(out it)) list.Items.Add(MakeRow(it));
            list.EndUpdate();
            loading = false;
            UpdateTotals();
        }

        // ── scanning ──
        // mode 0 = dev junk, 1 = duplicates, 2 = free up C:
        async void Run(int mode)
        {
            bool duplicates = mode == 1, cdrive = mode == 2;
            var sc = MakeScanner();
            var roots = Roots();
            if (sc == null || roots == null) return;
            if (mode == 0 && sc.Categories.Count == 0) { status.Text = "Tick at least one thing to look for."; return; }
            SaveSettings();
            cts = new CancellationTokenSource();
            sc.Token = cts.Token;
            if (mode == 0) sc.Found = it => incoming.Enqueue(it);   // show junk as soon as it is found
            Item dropped;
            while (incoming.TryDequeue(out dropped)) { }
            list.Items.Clear(); UpdateTotals();
            Busy(true);
            var started = DateTime.Now;
            List<Item> items;
            try { items = await Task.Run(() => cdrive ? sc.ScanC() : duplicates ? sc.ScanDuplicates(roots) : sc.ScanJunk(roots)); }
            catch (Exception ex) { items = new List<Item>(); MessageBox.Show(this, ex.Message, "Scan failed"); }
            finally { Busy(false); }

            // C: list: things you can apply first, tips (grey) last.
            Fill(duplicates ? items : cdrive ? items.OrderBy(i => i.Action == "info").ThenByDescending(i => i.Bytes).ToList() : items.OrderByDescending(i => i.Bytes).ToList());
            long ticked = items.Where(i => i.Checked).Sum(i => i.Bytes);
            status.Text = string.Format("{0}{1} found in {2:N0} s{3}.",
                sc.Stopped ? "Stopped - partial results. " : "",
                duplicates ? items.Count + " duplicate files" : items.Count + (cdrive ? " things on C:" : " folders"),
                (DateTime.Now - started).TotalSeconds,
                sc.Warnings > 0 ? ", " + sc.Warnings + " folders could not be read" : "");
            if (!duplicates) status.Text += " Ready to free: " + Util.Size(ticked) + " (ticked).";
            ShowFreeSpace();
        }

        ListViewItem MakeRow(Item it)
        {
            var row = new ListViewItem(new[] { Util.Size(it.Bytes), it.Kind, it.Path, it.Note }) { Tag = it };
            if (!it.Deletable) row.ForeColor = Color.Gray;
            else if (!it.Checked && !it.IsFile) row.ForeColor = Color.DarkOrange;
            row.Checked = it.Checked && it.Deletable;
            return row;
        }

        void Fill(List<Item> items)
        {
            loading = true;
            list.BeginUpdate();
            list.Items.Clear();
            list.Items.AddRange(items.Select(MakeRow).ToArray());
            list.EndUpdate();
            loading = false;
            UpdateTotals();
        }

        void SortBy(int column)
        {
            var items = list.Items.Cast<ListViewItem>().Select(r => { var it = (Item)r.Tag; it.Checked = r.Checked; return it; }).ToList();
            if (column == 0) items = items.OrderByDescending(i => i.Bytes).ToList();
            else if (column == 1) items = items.OrderBy(i => i.Kind).ThenByDescending(i => i.Bytes).ToList();
            else if (column == 2) items = items.OrderBy(i => i.Path, StringComparer.OrdinalIgnoreCase).ToList();
            else items = items.OrderBy(i => i.Note).ToList();
            Fill(items);
        }

        // Empties the results list (nothing on disk is changed).
        void ClearList()
        {
            Item dropped;
            while (incoming.TryDequeue(out dropped)) { }
            list.Items.Clear();
            UpdateTotals();
            status.Text = "List cleared. Nothing on disk was changed.";
        }
        void SetAll(bool on)
        {
            loading = true;
            list.BeginUpdate();
            foreach (ListViewItem r in list.Items) if (((Item)r.Tag).Deletable) r.Checked = on;
            list.EndUpdate();
            loading = false;
            UpdateTotals();
        }

        void UpdateTotals()
        {
            long sum = 0; int n = 0;
            foreach (ListViewItem r in list.CheckedItems) { sum += ((Item)r.Tag).Bytes; n++; }
            totals.Text = string.Format("Ticked: {0} items, {1}", n, Util.Size(sum));
            btnDelete.Text = n == 0 ? "Apply ticked" : "Apply ticked (" + Util.Size(sum) + ")";
        }

        void ShowFreeSpace()
        {
            var parts = new List<string>();
            foreach (var d in DriveInfo.GetDrives())
                if (d.DriveType == DriveType.Fixed && d.IsReady) parts.Add(d.Name.TrimEnd('\\') + " " + Util.Size(d.AvailableFreeSpace) + " free");
            Text = "Dev Junk Cleaner " + Version + "   -   " + string.Join("   ", parts);
        }

        // ── deleting ──
        async void DeleteChecked()
        {
            var rows = list.CheckedItems.Cast<ListViewItem>().ToList();
            if (rows.Count == 0) { status.Text = "Nothing is ticked."; return; }
            long sum = rows.Sum(r => ((Item)r.Tag).Bytes);
            var ticked = rows.Select(r => (Item)r.Tag).ToList();
            var lines = new List<string>();
            int del = ticked.Count(i => i.Action == "delete" || i.Action == "caches" || i.Action == "temp" || i.Action == "recycle");
            int mov = ticked.Count(i => i.Action == "move" || i.Action == "wsl");
            int adm = ticked.Count(i => i.Action == "admin");
            if (del > 0) lines.Add("- permanently delete " + del + " item(s) - NOT to the Recycle Bin");
            if (mov > 0) lines.Add("- move " + mov + " folder(s) to " + txtMoveTo.Text.Trim() + " and leave a link in the old place (nothing is re-downloaded)");
            if (adm > 0) lines.Add("- run " + adm + " Windows command(s) as admin (a Windows permission prompt appears)");
            var answer = MessageBox.Show(this,
                string.Format("Apply {0} ticked items ({1})? This will:\n\n{2}\n\nDev folders come back with npm install / composer install / flutter pub get or a rebuild.", rows.Count, Util.Size(sum), string.Join("\n", lines)),
                "Apply", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes) return;

            var sc = MakeScanner();
            if (sc == null) return;
            cts = new CancellationTokenSource();
            sc.Token = cts.Token;
            Busy(true);
            var items = rows.Select(r => (Item)r.Tag).ToList();
            var started = DateTime.Now;
            try { await Task.Run(() => sc.Apply(items)); }
            catch (Exception ex) { sc.Errors.Add("Stopped: " + ex.Message); }
            finally { Busy(false); }
            var deleted = sc.Deleted;
            var errors = sc.Errors.ToList();
            long freed = deleted.Keys.Sum(i => i.Bytes);

            loading = true;
            list.BeginUpdate();
            foreach (var r in rows) if (deleted.ContainsKey((Item)r.Tag)) list.Items.Remove(r);
            list.EndUpdate();
            loading = false;
            UpdateTotals();
            ShowFreeSpace();
            status.Text = string.Format("Freed {0} in {1:N0} s: done {2}, not done {3}.", Util.Size(freed), (DateTime.Now - started).TotalSeconds, deleted.Count, errors.Count);
            if (errors.Count > 0)
                MessageBox.Show(this, "These were not done:\n\n" + string.Join("\n", errors.Take(25)) + (errors.Count > 25 ? "\n..." : ""), "Some items were kept", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // ── skip list helpers ──
        void PickSkipFolder()
        {
            using (var dlg = new FolderBrowserDialog { Description = "Folder to skip (everything inside it is left alone)" })
                if (dlg.ShowDialog(this) == DialogResult.OK) AddSkip(dlg.SelectedPath);
        }

        void PickRootFolder()
        {
            using (var dlg = new FolderBrowserDialog { Description = "Folder or drive to scan" })
                if (dlg.ShowDialog(this) == DialogResult.OK) txtRoots.AppendText((txtRoots.Text.EndsWith("\n") || txtRoots.Text.Length == 0 ? "" : "\r\n") + dlg.SelectedPath);
        }

        void AddSkip(string path)
        {
            txtSkip.AppendText((txtSkip.Text.EndsWith("\n") || txtSkip.Text.Length == 0 ? "" : "\r\n") + path + "\r\n");
            SaveSettings();
            // Drop rows that are now skipped.
            loading = true;
            list.BeginUpdate();
            foreach (var r in list.Items.Cast<ListViewItem>().ToList())
                if (Util.Under(((Item)r.Tag).Path, path)) list.Items.Remove(r);
            list.EndUpdate();
            loading = false;
            UpdateTotals();
            status.Text = "Skipping " + path;
        }

        void SkipSelected()
        {
            if (list.SelectedItems.Count == 0) return;
            var it = (Item)list.SelectedItems[0].Tag;
            AddSkip(it.IsFile ? System.IO.Path.GetDirectoryName(it.Path) : it.Path);
        }

        void OpenSelected()
        {
            if (list.SelectedItems.Count == 0) return;
            var it = (Item)list.SelectedItems[0].Tag;
            Process.Start("explorer.exe", it.IsFile ? "/select,\"" + it.Path + "\"" : "\"" + it.Path + "\"");
        }
    }

    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }
}
