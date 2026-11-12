using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using ARBM.CableJournalExporter.Models;
using Autodesk.Revit.DB;

namespace PluginForRevit2022.Core
{
    /// <summary>Настройка одного семейства. Публичный класс: на него напрямую привязана таблица окна.</summary>
    public sealed class FamilySetting
    {
        public string Name { get; set; }
        public string Bco { get; set; }
        /// <summary>Семейство применяется в линиях АПС.</summary>
        public bool Aps { get; set; }
        /// <summary>Семейство применяется в линиях СОТС.</summary>
        public bool Security { get; set; }
        /// <summary>Семейство применяется в линиях СОУЭ.</summary>
        public bool Souye { get; set; }

        public bool InSystem(string system)
        {
            switch (system)
            {
                case "aps": return Aps;
                case "security": return Security;
                case "souye": return Souye;
                default: return false;
            }
        }
    }

    /// <summary>Запасы кабеля для кабельного журнала.</summary>
    public sealed class JournalSettings
    {
        public double ApsPercent { get; set; } = 30;
        public double SecurityPercent { get; set; } = 30;
        public double SouyePercent { get; set; } = 40;
        /// <summary>Добавляется к каждой строке (кабельной линии) журнала, м.</summary>
        public double LineReserveM { get; set; }

        public void Validate()
        {
            if (ApsPercent < 0 || SecurityPercent < 0 || SouyePercent < 0 || LineReserveM < 0)
            {
                throw new ArgumentException("Запасы кабеля не могут быть отрицательными.");
            }
        }
    }

    /// <summary>Содержимое файла профиля. Файл можно передать другому пользователю; паролей и логинов нет.</summary>
    public sealed class MarkingAccount
    {
        public string Name { get; set; }
        public int Version { get; set; } = 1;
        public List<FamilySetting> Families { get; set; } = new List<FamilySetting>();
        /// <summary>Последние значения окна маркировки.</summary>
        public Dictionary<string, string> LastMarking { get; set; } = new Dictionary<string, string>();
        public JournalSettings Journal { get; set; } = new JournalSettings();
    }

    /// <summary>Запись в списке профилей этого компьютера.</summary>
    public sealed class ProfileEntry
    {
        public string Name { get; set; }
        public string FilePath { get; set; }
        public bool IsCurrent { get; set; }
    }

    /// <summary>Профили настроек маркировки: БЦО по семействам, участие в линии, последний формат.</summary>
    internal sealed class MarkingSettings
    {
        private const string DefaultProfile = "Основной";
        private readonly Dictionary<string, FamilySetting> _families;

        private MarkingSettings(IEnumerable<FamilySetting> families)
        {
            _families = Clean(families).ToDictionary(f => f.Name, StringComparer.CurrentCultureIgnoreCase);
        }

        private static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AerBIM", "PluginForRevit");
        private static string IndexPath => Path.Combine(Folder, "profiles.json");

        private static List<FamilySetting> Clean(IEnumerable<FamilySetting> families) =>
            families.Where(f => !string.IsNullOrWhiteSpace(f.Name))
                .GroupBy(f => f.Name.Trim(), StringComparer.CurrentCultureIgnoreCase)
                .Select(g => { FamilySetting last = g.Last(); last.Name = g.Key; return last; })
                .ToList();

        public IEnumerable<FamilySetting> Families => _families.Values;

        // ===================== Список профилей =====================

        public static List<ProfileEntry> Profiles()
        {
            List<ProfileEntry> list = File.Exists(IndexPath)
                ? new JavaScriptSerializer().Deserialize<List<ProfileEntry>>(File.ReadAllText(IndexPath)) ?? new List<ProfileEntry>()
                : new List<ProfileEntry>();
            if (list.Count == 0)
            {
                list.Add(new ProfileEntry { Name = DefaultProfile, FilePath = Path.Combine(Folder, "profiles", DefaultProfile + ".json"), IsCurrent = true });
                WriteIndex(list);
            }
            if (!list.Any(p => p.IsCurrent))
            {
                list[0].IsCurrent = true;
                WriteIndex(list);
            }
            return list;
        }

        private static void WriteIndex(List<ProfileEntry> list)
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(IndexPath, new JavaScriptSerializer().Serialize(list));
        }

        public static ProfileEntry Current() => Profiles().First(p => p.IsCurrent);

        public static void Select(string name)
        {
            List<ProfileEntry> list = Profiles();
            if (!list.Any(p => Same(p.Name, name))) throw new ArgumentException($"Профиль «{name}» не найден.");
            foreach (ProfileEntry profile in list) profile.IsCurrent = Same(profile.Name, name);
            WriteIndex(list);
        }

        /// <summary>Новый пустой профиль в указанном файле; он становится текущим.</summary>
        public static void Create(string path)
        {
            string name = Path.GetFileNameWithoutExtension(path);
            AddEntry(name, path);
            WriteFile(path, new MarkingAccount { Name = name });
        }

        /// <summary>Подключает полученный файл профиля; он становится текущим.</summary>
        public static void AddFromFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) throw new ArgumentException($"Файл не найден: {path}");
            MarkingAccount account = Parse(path);
            string name = string.IsNullOrWhiteSpace(account.Name) ? Path.GetFileNameWithoutExtension(path) : account.Name.Trim();
            AddEntry(name, path);
        }

        private static void AddEntry(string name, string path)
        {
            List<ProfileEntry> list = Profiles();
            if (list.Any(p => Same(p.Name, name))) throw new InvalidOperationException($"Профиль «{name}» уже есть в списке.");
            foreach (ProfileEntry profile in list) profile.IsCurrent = false;
            list.Add(new ProfileEntry { Name = name, FilePath = Path.GetFullPath(path), IsCurrent = true });
            WriteIndex(list);
        }

        /// <summary>Убирает профиль из списка. Файл удаляется, только если лежит в папке плагина.</summary>
        public static void Delete(string name)
        {
            List<ProfileEntry> list = Profiles();
            ProfileEntry target = list.FirstOrDefault(p => Same(p.Name, name)) ?? throw new ArgumentException($"Профиль «{name}» не найден.");
            if (list.Count == 1) throw new InvalidOperationException("Нельзя удалить единственный профиль.");
            list.Remove(target);
            if (target.IsCurrent) list[0].IsCurrent = true;
            WriteIndex(list);
            if (Path.GetFullPath(target.FilePath).StartsWith(Path.GetFullPath(Folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && File.Exists(target.FilePath))
            {
                File.Delete(target.FilePath);
            }
        }

        private static bool Same(string a, string b) => string.Equals(a, b?.Trim(), StringComparison.CurrentCultureIgnoreCase);

        // ===================== Файл профиля =====================

        public static MarkingAccount ReadAccount()
        {
            ProfileEntry current = Current();
            return File.Exists(current.FilePath) ? Parse(current.FilePath) : new MarkingAccount { Name = current.Name };
        }

        private static MarkingAccount Parse(string path)
        {
            try
            {
                MarkingAccount account = new JavaScriptSerializer().Deserialize<MarkingAccount>(File.ReadAllText(path));
                if (account?.Families == null) throw new FormatException("нет списка семейств");
                if (account.LastMarking == null) account.LastMarking = new Dictionary<string, string>();
                if (account.Journal == null) account.Journal = new JournalSettings();
                return account;
            }
            catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException || ex is FormatException)
            {
                throw new InvalidOperationException($"Файл профиля не прочитан: {path}. {ex.Message}", ex);
            }
        }

        private static void WriteFile(string path, MarkingAccount account)
        {
            string folder = Path.GetDirectoryName(Path.GetFullPath(path));
            Directory.CreateDirectory(folder);
            account.Families = Clean(account.Families);
            File.WriteAllText(path, new JavaScriptSerializer().Serialize(account));
        }

        public static void Export(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Укажите путь к файлу.");
            WriteFile(path, ReadAccount());
        }

        public static void Save(IEnumerable<FamilySetting> families, JournalSettings journal)
        {
            journal.Validate();
            MarkingAccount account = ReadAccount();
            account.Families = families.ToList();
            account.Journal = journal;
            WriteFile(Current().FilePath, account);
        }

        public static void SaveLastMarking(Dictionary<string, string> values)
        {
            MarkingAccount account = ReadAccount();
            account.LastMarking = values;
            WriteFile(Current().FilePath, account);
        }

        // ===================== Запасы кабеля =====================

        /// <summary>Коэффициент запаса для расчёта длин выбранной системы.</summary>
        public static double CableFactor(string system)
        {
            JournalSettings journal = ReadAccount().Journal;
            switch (system)
            {
                case "aps": return 1 + journal.ApsPercent / 100;
                case "souye": return 1 + journal.SouyePercent / 100;
                default: return 1 + journal.SecurityPercent / 100;
            }
        }

        /// <summary>Запасы кабеля текущего профиля (копия).</summary>
        public static JournalSettings CurrentJournal()
        {
            JournalSettings j = ReadAccount().Journal ?? new JournalSettings();
            return new JournalSettings { ApsPercent = j.ApsPercent, SecurityPercent = j.SecurityPercent, SouyePercent = j.SouyePercent, LineReserveM = j.LineReserveM };
        }

        /// <summary>Запас на линию из профиля, м (добавляется к каждой строке журнала).</summary>
        public static double LineReserveM() => Math.Max(0, ReadAccount().Journal.LineReserveM);

        /// <summary>Добавляет к длине каждой строки журнала запас на линию из профиля.</summary>
        public static void ApplyLineReserve(IEnumerable<CableRecord> records)
        {
            double reserve = ReadAccount().Journal.LineReserveM;
            if (reserve <= 0) return;
            foreach (CableRecord record in records)
            {
                record.Length = (double.Parse(record.Length) + reserve).ToString("F2");
            }
        }

        // ===================== Применение =====================

        public static MarkingSettings Load() => new MarkingSettings(ReadAccount().Families);

        /// <summary>В линии системы участвуют только семейства с галочкой этой системы.</summary>
        public bool IsInLine(string family, string system) => _families.TryGetValue(family ?? "", out FamilySetting setting) && setting.InSystem(system);

        public bool AnyInLine(string system) => _families.Values.Any(f => f.InSystem(system));

        public string BcoOf(string family) =>
            _families.TryGetValue(family ?? "", out FamilySetting setting) && !string.IsNullOrWhiteSpace(setting.Bco) ? setting.Bco.Trim() : null;

        /// <summary>Строки настройки: сохранённые значения плюс семейства устройств из открытого проекта.</summary>
        public static List<FamilySetting> ForDocument(Document doc)
        {
            MarkingSettings saved = Load();
            var categories = new List<BuiltInCategory> { BuiltInCategory.OST_DataDevices, BuiltInCategory.OST_FireAlarmDevices, BuiltInCategory.OST_NurseCallDevices };
            IEnumerable<string> names = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol))
                .WherePasses(new ElementMulticategoryFilter(categories)).Cast<FamilySymbol>().Select(s => s.FamilyName);
            return names.Concat(saved.Families.Select(f => f.Name))
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase)
                .Select(n => saved._families.TryGetValue(n, out FamilySetting s)
                    ? new FamilySetting { Name = n, Bco = s.Bco, Aps = s.Aps, Security = s.Security, Souye = s.Souye }
                    : new FamilySetting { Name = n })
                .ToList();
        }

        public static Dictionary<string, object> ToResult(Document doc) => new Dictionary<string, object>
        {
            ["profile"] = Current().Name,
            ["journal"] = ReadAccount().Journal,
            ["profiles"] = Profiles().Select(p => new Dictionary<string, object> { ["name"] = p.Name, ["file"] = p.FilePath, ["current"] = p.IsCurrent }).ToList(),
            ["families"] = ForDocument(doc).Select(f => new Dictionary<string, object> { ["name"] = f.Name, ["bco"] = f.Bco, ["aps"] = f.Aps, ["security"] = f.Security, ["souye"] = f.Souye }).ToList(),
        };

        /// <summary>Обновляет только переданные семейства текущего профиля.</summary>
        public static Dictionary<string, object> Merge(Document doc, IEnumerable<FamilySetting> changes, JournalSettings journal = null)
        {
            MarkingAccount account = ReadAccount();
            if (journal != null)
            {
                journal.Validate();
                account.Journal = journal;
            }
            foreach (FamilySetting change in changes)
            {
                account.Families.RemoveAll(f => string.Equals(f.Name, change.Name, StringComparison.CurrentCultureIgnoreCase));
                account.Families.Add(change);
            }
            WriteFile(Current().FilePath, account);
            return ToResult(doc);
        }
    }
}
