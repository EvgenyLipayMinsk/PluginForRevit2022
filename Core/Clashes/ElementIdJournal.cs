using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;

namespace PluginForRevit2022.Core.Clashes
{
    /// <summary>
    /// Журнал замены ID лотков и коробов. Для исправления коллизии разделённые лотки соединяют и снова разделяют —
    /// Revit удаляет одни сегменты и создаёт другие, ID из отчёта Navisworks перестаёт существовать.
    /// Пока Revit открыт, запоминаем оси сегментов. Замена записывается, когда ось удалённого сегмента занял другой
    /// (соединение — в той же или в одной из следующих операций) или когда сегмент укоротился, а на освободившейся
    /// части его оси появился новый (разделение). Замены копятся в памяти и пишутся в файл при сохранении,
    /// синхронизации, закрытии, показе коллизии и выгрузке — только те, что подтверждает модель (отменённые
    /// операции отбрасываются). Файл на модель: %AppData%\AerBIM\idmap\&lt;модель&gt;.txt (старый ID, новые ID, когда, кто).
    /// Соединительные детали: удалённая деталь заменяется новой деталью того же семейства, подключённой к тем же
    /// лоткам (с учётом их замен), а если связи не помогли — новой деталью того же семейства в пределах 50 мм.
    /// </summary>
    internal static class ElementIdJournal
    {
        private static readonly BuiltInCategory[] Tracked = { BuiltInCategory.OST_CableTray, BuiltInCategory.OST_Conduit };
        private static readonly BuiltInCategory[] TrackedFittings = { BuiltInCategory.OST_CableTrayFitting, BuiltInCategory.OST_ConduitFitting };
        private const double FitNearFt = 50 / 304.8;     // деталь без общих лотков — замена только на месте (50 мм)
        private const double OnAxisFt = 5 / 304.8;      // на оси: отклонение не больше 5 мм
        private const double MinOverlapFt = 10 / 304.8;  // перекрытие хотя бы 10 мм
        private const int GraveyardOps = 30;             // удалённый сегмент ждёт соединения столько операций

        private sealed class Seg
        {
            public XYZ A, B;
            public long Category;
            public double Length => A.DistanceTo(B);
        }

        private sealed class Fit
        {
            public XYZ P;
            public long Category;
            public string Family;
            public HashSet<long> Trays = new HashSet<long>(); // лотки, к которым деталь была подключена
        }

        private sealed class Change
        {
            public long Old;
            public List<long> Now;
            public string When, User;
        }

        /// <summary>Состояние открытого проекта (по документу, а не по имени: копии одной модели не смешиваются).</summary>
        private sealed class State
        {
            public Document Doc;
            public string Key;
            public Dictionary<long, Seg> Axes = new Dictionary<long, Seg>();
            public Dictionary<long, (Seg seg, int op)> Graveyard = new Dictionary<long, (Seg, int)>();
            public List<Change> Pending = new List<Change>();
            public Dictionary<long, Fit> Fits = new Dictionary<long, Fit>();
            public Dictionary<long, (Fit fit, int op)> FitGraveyard = new Dictionary<long, (Fit, int)>();
            public HashSet<long> AddedFits = new HashSet<long>();
            public int Op;
        }

        private static readonly List<State> States = new List<State>();
        private static readonly Dictionary<string, (DateTime stamp, Dictionary<long, HashSet<long>> map, HashSet<string> users)> Read =
            new Dictionary<string, (DateTime, Dictionary<long, HashSet<long>>, HashSet<string>)>();
        private static string _user = "";

        public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AerBIM", "idmap");

        public static void Start(ControlledApplication app)
        {
            app.DocumentOpened += (s, e) => Safe(() => Remember(e.Document));
            app.DocumentCreated += (s, e) => Safe(() => Remember(e.Document));
            app.DocumentSaving += (s, e) => Safe(() => Flush(e.Document));
            app.DocumentSavingAs += (s, e) => Safe(() => Flush(e.Document));
            app.DocumentSynchronizingWithCentral += (s, e) => Safe(() => Flush(e.Document));
            app.DocumentClosing += (s, e) => Safe(() =>
            {
                Flush(e.Document);
                States.RemoveAll(x => Same(x.Doc, e.Document));
            });
            app.DocumentChanged += (s, e) => Safe(() => Changed(e));
        }

        private static void Safe(Action action)
        {
            try { action(); }
            catch (Exception ex) { PluginLog.Warn("idmap.error", ex.ToString()); }
        }

        private static bool Tracks(Document doc) => doc != null && doc.IsValidObject && !doc.IsFamilyDocument && !doc.IsLinked;

        private static bool Same(Document a, Document b)
        {
            try { return a != null && b != null && a.IsValidObject && b.IsValidObject && a.Equals(b); }
            catch (Exception) { return false; }
        }

        private static State StateOf(Document doc) => States.FirstOrDefault(x => Same(x.Doc, doc));

        /// <summary>Имя модели как в «Файл источника» отчёта: файл хранилища, иначе файл проекта.</summary>
        public static string ModelKey(Document doc)
        {
            string path = doc.PathName;
            try
            {
                if (doc.IsWorkshared)
                {
                    ModelPath central = doc.GetWorksharingCentralModelPath();
                    if (central != null) path = ModelPathUtils.ConvertModelPathToUserVisiblePath(central);
                }
            }
            catch (Exception) { /* облачная модель — берём локальный путь */ }
            return ClashRevit.Normalize(string.IsNullOrEmpty(path) ? doc.Title : path);
        }

        private static void Remember(Document doc)
        {
            if (!Tracks(doc)) return;
            _user = doc.Application.Username ?? "";
            States.RemoveAll(x => Same(x.Doc, doc) || !x.Doc.IsValidObject);
            var state = new State { Doc = doc, Key = ModelKey(doc) };
            var filter = new ElementMulticategoryFilter(Tracked.ToList());
            foreach (Element e in new FilteredElementCollector(doc).WherePasses(filter).WhereElementIsNotElementType())
            {
                Seg s = SegOf(e);
                if (s != null) state.Axes[RevitIds.IdOf(e.Id)] = s;
            }
            var fittings = new ElementMulticategoryFilter(TrackedFittings.ToList());
            foreach (Element e in new FilteredElementCollector(doc).WherePasses(fittings).WhereElementIsNotElementType())
            {
                Fit fit = FitOf(e);
                if (fit != null) state.Fits[RevitIds.IdOf(e.Id)] = fit;
            }
            States.Add(state);
        }

        private static Fit FitOf(Element e)
        {
            if (!(e is FamilyInstance fi) || e.Category == null) return null;
            long cat = RevitIds.IdOf(e.Category.Id);
            if (!TrackedFittings.Any(c => (long)c == cat)) return null;
            if (!(e.Location is LocationPoint lp)) return null;
            var fit = new Fit { P = lp.Point, Category = cat, Family = fi.Symbol?.Family?.Name ?? "" };
            try
            {
                ConnectorSet connectors = fi.MEPModel?.ConnectorManager?.Connectors;
                if (connectors != null)
                    foreach (Connector c in connectors)
                    {
                        if (!c.IsConnected) continue;
                        foreach (Connector other in c.AllRefs)
                            if (other.Owner is MEPCurve && other.Owner.Id != e.Id) fit.Trays.Add(RevitIds.IdOf(other.Owner.Id));
                    }
            }
            catch (Exception) { /* без связей — сопоставим по месту */ }
            return fit;
        }

        private static Seg SegOf(Element e)
        {
            if (!(e is MEPCurve) || e.Category == null) return null;
            long cat = RevitIds.IdOf(e.Category.Id);
            if (!Tracked.Any(c => (long)c == cat)) return null;
            if (!(e.Location is LocationCurve lc) || lc.Curve == null || !lc.Curve.IsBound) return null;
            return new Seg { A = lc.Curve.GetEndPoint(0), B = lc.Curve.GetEndPoint(1), Category = cat };
        }

        private static void Changed(DocumentChangedEventArgs e)
        {
            Document doc = e.GetDocument();
            if (!Tracks(doc)) return;
            State st = StateOf(doc);
            if (st == null) { Remember(doc); return; } // прежние оси неизвестны — начинаем с текущего состояния
            st.Op++;

            var current = new Dictionary<long, Seg>();
            var added = new HashSet<long>();
            foreach (ElementId id in e.GetAddedElementIds())
            {
                Seg s = SegOf(doc.GetElement(id));
                if (s == null) continue;
                long v = RevitIds.IdOf(id);
                current[v] = s;
                added.Add(v);
            }
            foreach (ElementId id in e.GetModifiedElementIds())
            {
                Seg s = SegOf(doc.GetElement(id));
                if (s != null) current[RevitIds.IdOf(id)] = s;
            }
            List<long> deleted = e.GetDeletedElementIds().Select(RevitIds.IdOf).Where(st.Axes.ContainsKey).ToList();

            // Детали: связи копим (лоток могли удалить раньше самой детали), удалённые ждут замены до Flush
            foreach (ElementId id in e.GetAddedElementIds().Concat(e.GetModifiedElementIds()))
            {
                Fit fit = FitOf(doc.GetElement(id));
                if (fit == null) continue;
                long v = RevitIds.IdOf(id);
                if (st.Fits.TryGetValue(v, out Fit before)) fit.Trays.UnionWith(before.Trays);
                st.Fits[v] = fit;
                if (e.Operation == UndoOperation.TransactionCommitted && e.GetAddedElementIds().Contains(id)) st.AddedFits.Add(v);
            }
            foreach (long d in e.GetDeletedElementIds().Select(RevitIds.IdOf).Where(st.Fits.ContainsKey).ToList())
            {
                if (e.Operation == UndoOperation.TransactionCommitted) st.FitGraveyard[d] = (st.Fits[d], st.Op);
                st.Fits.Remove(d);
            }

            if (e.Operation == UndoOperation.TransactionCommitted)
            {
                string when = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
                // Соединение в одной операции: сегмент удалён, его ось занял удлинённый или новый сегмент
                foreach (long d in deleted)
                {
                    List<long> now = current.Where(c => Overlaps(st.Axes[d], c.Value)).Select(c => c.Key).OrderBy(x => x).ToList();
                    if (now.Count > 0) st.Pending.Add(new Change { Old = d, Now = now, When = when, User = _user });
                    else st.Graveyard[d] = (st.Axes[d], st.Op); // ждём: конец соседнего лотка вытянут следующей операцией
                }
                // Соединение в несколько операций: ось удалённого раньше сегмента занята сейчас
                foreach (long g in st.Graveyard.Keys.Where(k => !deleted.Contains(k)).ToList())
                {
                    List<long> now = current.Where(c => Overlaps(st.Graveyard[g].seg, c.Value)).Select(c => c.Key).OrderBy(x => x).ToList();
                    if (now.Count == 0) continue;
                    st.Pending.Add(new Change { Old = g, Now = now, When = when, User = _user });
                    st.Graveyard.Remove(g);
                }
                // Разделение: сегмент укоротился, а на освободившейся части его прежней оси появился новый
                foreach (long m in current.Keys.Where(k => !added.Contains(k) && st.Axes.ContainsKey(k)).ToList())
                {
                    Seg before = st.Axes[m], after = current[m];
                    if (after.Length > before.Length - MinOverlapFt) continue;
                    List<long> now = added.Where(a => Overlaps(before, current[a]) && !Overlaps(after, current[a])).OrderBy(x => x).ToList();
                    if (now.Count > 0) st.Pending.Add(new Change { Old = m, Now = new[] { m }.Concat(now).ToList(), When = when, User = _user });
                }
            }
            // Отмена/повтор: записи не добавляем; уже накопленные проверит Flush по фактическому состоянию модели

            foreach (long d in e.GetDeletedElementIds().Select(RevitIds.IdOf)) st.Axes.Remove(d);
            foreach (var c in current) st.Axes[c.Key] = c.Value;
            foreach (long g in st.Graveyard.Where(x => st.Op - x.Value.op > GraveyardOps).Select(x => x.Key).ToList()) st.Graveyard.Remove(g);
        }

        /// <summary>Новый сегмент лежит на оси старого и перекрывает его.</summary>
        private static bool Overlaps(Seg old, Seg now)
        {
            if (old.Category != now.Category) return false;
            XYZ dir = old.B - old.A;
            double len = dir.GetLength();
            if (len < 1e-6) return false;
            dir = dir / len;
            XYZ dirNow = now.B - now.A;
            if (dirNow.GetLength() < 1e-6 || dir.CrossProduct(dirNow.Normalize()).GetLength() > 1e-3) return false;
            foreach (XYZ p in new[] { now.A, now.B })
            {
                XYZ v = p - old.A;
                if ((v - dir * v.DotProduct(dir)).GetLength() > OnAxisFt) return false;
            }
            double t1 = (now.A - old.A).DotProduct(dir), t2 = (now.B - old.A).DotProduct(dir);
            double overlap = Math.Min(len, Math.Max(t1, t2)) - Math.Max(0, Math.Min(t1, t2));
            return overlap > MinOverlapFt;
        }

        /// <summary>Все открытые проекты (перед показом коллизии и выгрузкой). Только в контексте Revit API.</summary>
        public static void FlushAll()
        {
            foreach (State st in States.ToList()) Safe(() => Flush(st.Doc));
        }

        /// <summary>
        /// Записывает накопленные замены, которые подтверждает модель: новые ID существуют, а старый удалён
        /// (или сохранился сам при разделении). Отменённые операции так отсеиваются; повтор (Redo) вернёт те же ID.
        /// </summary>
        private static void Flush(Document doc)
        {
            State st = StateOf(doc);
            if (st == null || !doc.IsValidObject || (st.Pending.Count == 0 && st.FitGraveyard.Count == 0)) return;
            Func<long, bool> exists = id => doc.GetElement(RevitIds.ToElementId(id)) != null;
            var confirmed = new List<Change>();
            foreach (Change c in st.Pending)
            {
                bool selfKept = c.Now.Contains(c.Old);
                if (!selfKept && exists(c.Old)) continue;               // соединение отменили — старый сегмент вернулся
                List<long> now = c.Now.Where(exists).ToList();
                if (now.Count == 0 || (now.Count == 1 && now[0] == c.Old)) continue; // разделение отменили
                confirmed.Add(new Change { Old = c.Old, Now = now, When = c.When, User = c.User });
            }
            st.Pending.Clear();
            Write(st.Key, confirmed);

            // Детали — после лотков: их замены уже в журнале
            Dictionary<long, HashSet<long>> map = MapFor(st.Key);
            string when = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
            var fits = new List<Change>();
            foreach (long g in st.FitGraveyard.Keys.ToList())
            {
                Fit old = st.FitGraveyard[g].fit;
                if (exists(g)) { st.FitGraveyard.Remove(g); continue; } // удаление отменили
                // Лотки, к которым была подключена деталь, — какими они стали сейчас
                var trays = new HashSet<long>();
                foreach (long tray in old.Trays)
                {
                    if (exists(tray)) trays.Add(tray);
                    else if (map != null) trays.UnionWith(ResolveIn(map, tray).Where(exists));
                }
                var candidates = st.AddedFits.Where(exists)
                    .Select(id => (id, fit: FitOf(doc.GetElement(RevitIds.ToElementId(id)))))
                    .Where(x => x.fit != null && x.fit.Category == old.Category && x.fit.Family == old.Family)
                    .ToList();
                int need = Math.Min(2, trays.Count);
                var best = candidates
                    .Select(x => (x.id, shared: need == 0 ? 0 : x.fit.Trays.Count(trays.Contains), dist: x.fit.P.DistanceTo(old.P)))
                    .Where(x => (need > 0 && x.shared >= need) || x.dist <= FitNearFt)
                    .OrderByDescending(x => x.shared).ThenBy(x => x.dist)
                    .Select(x => (long?)x.id).FirstOrDefault();
                if (best != null)
                {
                    fits.Add(new Change { Old = g, Now = new List<long> { best.Value }, When = when, User = _user });
                    st.AddedFits.Remove(best.Value); // одна новая деталь заменяет одну удалённую
                    st.FitGraveyard.Remove(g);
                }
                else if (st.Op - st.FitGraveyard[g].op > GraveyardOps) st.FitGraveyard.Remove(g);
            }
            Write(st.Key, fits);
        }

        private static void Write(string key, List<Change> changes)
        {
            if (changes.Count == 0) return;
            var sb = new StringBuilder();
            foreach (Change c in changes)
                sb.Append(c.Old).Append('\t').Append(string.Join(",", c.Now)).Append('\t').Append(c.When).Append('\t').Append(c.User).Append("\r\n");
            byte[] bytes = new UTF8Encoding(false).GetBytes(sb.ToString());
            string path = Path.Combine(Folder, key + ".txt");
            Directory.CreateDirectory(Folder);
            // Файл может читать или дописывать другая сессия Revit — общий доступ и короткие повторы
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    using (var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                        fs.Write(bytes, 0, bytes.Length);
                    break;
                }
                catch (IOException) when (attempt < 5) { Thread.Sleep(100); }
            }
            Read.Remove(key); // перечитать при следующем запросе
            PluginLog.Info("idmap.change", key + " | " + string.Join("; ", changes.Select(c => c.Old + "→" + string.Join(",", c.Now))));
        }

        /// <summary>
        /// Текущие ID элемента из отчёта по журналу (с учётом цепочек: соединили, разделили, снова соединили).
        /// Пусто, если элемент не менялся. Сам ID входит в ответ, если сегмент сохранился после разделения.
        /// </summary>
        public static List<long> Resolve(string reportFile, long id)
        {
            Dictionary<long, HashSet<long>> map = MapFor(ClashRevit.Normalize(reportFile));
            return map == null ? new List<long>() : ResolveIn(map, id);
        }

        private static List<long> ResolveIn(Dictionary<long, HashSet<long>> map, long id)
        {
            if (!map.ContainsKey(id)) return new List<long>();
            var result = new SortedSet<long>();
            var seen = new HashSet<long>();
            var stack = new Stack<long>();
            stack.Push(id);
            while (stack.Count > 0)
            {
                long x = stack.Pop();
                if (!seen.Add(x)) continue;
                if (!map.TryGetValue(x, out HashSet<long> next)) { result.Add(x); continue; }
                foreach (long n in next)
                {
                    if (n == x) result.Add(x);
                    else stack.Push(n);
                }
            }
            return result.SequenceEqual(new[] { id }) ? new List<long>() : result.ToList();
        }

        /// <summary>Журнал для файла из отчёта. Точное имя, иначе — NWC выгружен из локальной копии «модель_пользователь»:
        /// журнал «модель», если хвост — имя пользователя, который правил эту модель (или текущий).</summary>
        private static Dictionary<long, HashSet<long>> MapFor(string file)
        {
            if (string.IsNullOrEmpty(file)) return null;
            var exact = Load(file);
            if (exact != null) return exact.Value.map;
            if (!Directory.Exists(Folder)) return null;
            foreach (string path in Directory.GetFiles(Folder, "*.txt").OrderByDescending(p => p.Length))
            {
                string key = Path.GetFileNameWithoutExtension(path);
                if (!file.StartsWith(key + "_")) continue;
                string tail = file.Substring(key.Length + 1);
                var loaded = Load(key);
                if (loaded == null) continue;
                if (loaded.Value.users.Contains(tail) || string.Equals(tail, _user, StringComparison.OrdinalIgnoreCase)) return loaded.Value.map;
            }
            return null;
        }

        private static (Dictionary<long, HashSet<long>> map, HashSet<string> users)? Load(string key)
        {
            string path = Path.Combine(Folder, key + ".txt");
            try
            {
                if (!File.Exists(path)) return null;
                DateTime stamp = File.GetLastWriteTimeUtc(path);
                if (Read.TryGetValue(key, out var cached) && cached.stamp == stamp) return (cached.map, cached.users);
                string text;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(fs, Encoding.UTF8))
                    text = reader.ReadToEnd();
                var map = new Dictionary<long, HashSet<long>>();
                var users = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string line in text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string[] f = line.Split('\t');
                    if (f.Length < 2 || !long.TryParse(f[0], out long old)) continue;
                    if (f.Length >= 4 && f[3].Trim().Length > 0) users.Add(f[3].Trim().ToLowerInvariant());
                    var now = new HashSet<long>(f[1].Split(',').Select(x => long.TryParse(x, out long v) ? v : -1).Where(v => v >= 0));
                    if (!map.TryGetValue(old, out HashSet<long> set)) map[old] = set = new HashSet<long>();
                    // Записи по порядку: если позже сегмент удалили, он больше не «сохранился сам»
                    if (!now.Contains(old)) set.Remove(old);
                    set.UnionWith(now);
                }
                Read[key] = (stamp, map, users);
                return (map, users);
            }
            catch (Exception ex)
            {
                PluginLog.Warn("idmap.read", path + " | " + ex.Message);
                return null;
            }
        }
    }
}
