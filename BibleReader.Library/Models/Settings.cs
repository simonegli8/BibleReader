using EstrellasDeEsperanza.AsyncLock;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Transactions;

namespace BibleReader.Library
{
    public class Settings
    {
        const string SettingsFile = "settings.json";
        public List<BookText> Bibles { get; set; }
        /// <summary>The <see cref="BookText.EnglishName"/> of the Bible that was open last; it is opened again at the start.</summary>
        public string? CurrentBible { get; set; }
        /// <summary>Where the reader was in <see cref="CurrentBible"/>: the number of the book (<c>Book.Index</c>), the chapter and the verse.</summary>
        public int CurrentBookIndex { get; set; }
        public int CurrentChapter { get; set; }
        public int CurrentVerse { get; set; }
        /// <summary>"System" (or null), "Light" or "Dark".</summary>
        public string? Theme { get; set; }
        /// <summary>A place in the history of positions (the back and forward buttons of the toolbar).</summary>
        public class HistoryEntry
        {
            public int BookIndex { get; set; }
            public int Chapter { get; set; }
            public int Verse { get; set; }
        }
        /// <summary>The positions the reader has been at, the oldest first. Kept in memory, saved by <see cref="SaveOnExit"/>.</summary>
        public List<HistoryEntry>? History { get; set; }
        /// <summary>The entry of <see cref="History"/> that the reader is at (earlier ones are back, later ones forward).</summary>
        public int HistoryIndex { get; set; } = -1;
        public bool? ShowWordsOfJesus { get; set; }
        /// <summary>The user confirmed that there is no persecution of Christians in their country; only then are online Bibles listed.</summary>
        public bool InMyCountryThereIsNoPersecution { get; set; }
        /// <summary>The language of the texts: "en", "de" ...; null for the language of the device. It is taken when the app starts.</summary>
        public string? Language { get; set; }
        /// <summary>The size of the text of the interface as a factor (1.25 is 25% larger); null for the size of the system (see <see cref="UiScale"/>).</summary>
        public double? UiSize { get; set; }
        /// <summary>Show the verse numbers in green (off when null).</summary>
        public bool? ShowVerseNumbersInGreen { get; set; }
        /// <summary>Split words at the end of a line (see <see cref="Hyphenation"/>); on when null.</summary>
        public bool? Hyphenate { get; set; }
        public double? FontSize { get; set; }
        public string FontFamily { get; set; } = "Bible Roman";
        public string ConfigPath
        {
            get
            {
                if (field == null)
                {
                    field = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CrossBible");
                    Directory.CreateDirectory(field);
                }
                return field;
            }
            set
            {
                field = value;
                Directory.CreateDirectory(field);
            }
        }

        [JsonIgnore]
        public static Settings Current { get; private set; } = new Settings();

        // A semaphore, not a lock: the asynchronous write waits inside it.
        private static readonly System.Threading.SemaphoreSlim WriteGate = new(1, 1);
        private static int _requested, _written;

        AsyncLock SaveLock = new AsyncLock();

        public void Save()
        {
            var json = Serialize();
            int number = ++_requested;
            var file = Path.Combine(ConfigPath, SettingsFile);
            using (SaveLock.Lock())
            {
                if (number < _written) return;
                _written = number;
                File.WriteAllText(file, json);
            }
        }

        /// <summary>
        /// Saves without waiting for the disk. The settings are turned into text here, on the calling thread (so nothing
        /// changes them meanwhile); the file is written asynchronously, off that thread. Writes never overlap, and a newer
        /// one is not overwritten by an older one that gets there later.
        /// </summary>
        public Task SaveAsync()
        {
            var json = Serialize();
            int number = ++_requested;
            var file = Path.Combine(ConfigPath, SettingsFile);
            return Task.Run(async () =>
            {
                using (await SaveLock.LockAsync())
                {
                    if (number < _written) return; // a newer state was already written
                    _written = number;
                    await File.WriteAllTextAsync(file, json);
                }
            });
        }

        /// <summary>The history is cut to its last this many entries when the settings are saved.</summary>
        public const int MaxHistoryOnSave = 250;

        private string Serialize()
        {
            TrimHistory();
            return JsonConvert.SerializeObject(this, new JsonSerializerSettings()
            {
                TypeNameHandling = TypeNameHandling.Auto,
                Converters = [ new StringEnumConverter(), new VersionConverter() ]
            });
        }

        /// <summary>Drops the oldest entries beyond <see cref="MaxHistoryOnSave"/>; <see cref="HistoryIndex"/> keeps pointing at the same entry.</summary>
        private void TrimHistory()
        {
            if (History is not { } history || history.Count <= MaxHistoryOnSave) return;
            int surplus = history.Count - MaxHistoryOnSave;
            history.RemoveRange(0, surplus);
            HistoryIndex = Math.Max(-1, HistoryIndex - surplus);
        }

        private static bool _saveOnExit, _loaded;

        /// <summary>
        /// Saves the settings when the app ends or crashes, so that things that change often (the history) need not be
        /// saved with every change. Never saves what was not read: a file that failed to load must not be overwritten.
        /// </summary>
        private static void SaveOnExit()
        {
            if (_saveOnExit) return;
            _saveOnExit = true;
            void Save()
            {
                if (!_loaded) return;
                try { Current.Save(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Could not save the settings: {ex}"); }
            }
            AppDomain.CurrentDomain.ProcessExit += (_, _) => Save();
            AppDomain.CurrentDomain.UnhandledException += (_, _) => Save(); // the process ends after this
        }

        public void Load()
        {
            SaveOnExit();
            var file = Path.Combine(ConfigPath, SettingsFile);
            if (!File.Exists(file)) { _loaded = true; Localizer.Apply(Language); return; }
            try
            {
                var json = File.ReadAllText(file);
                var settings = JsonConvert.DeserializeObject<Settings>(json, new JsonSerializerSettings()
                {
                    TypeNameHandling = TypeNameHandling.Auto,
                    Converters = [ new VersionConverter(), new StringEnumConverter() ],
                    Formatting = Formatting.Indented
                });
                Bibles = settings.Bibles;
                CurrentBible = settings.CurrentBible;
                CurrentBookIndex = settings.CurrentBookIndex;
                CurrentChapter = settings.CurrentChapter;
                CurrentVerse = settings.CurrentVerse;
                Theme = settings.Theme;
                History = settings.History;
                HistoryIndex = settings.HistoryIndex;
                ShowWordsOfJesus = settings.ShowWordsOfJesus;
                InMyCountryThereIsNoPersecution = settings.InMyCountryThereIsNoPersecution;
                ShowVerseNumbersInGreen = settings.ShowVerseNumbersInGreen;
                Hyphenate = settings.Hyphenate;
                FontSize = settings.FontSize;
                FontFamily = settings.FontFamily;
                ConfigPath = settings.ConfigPath;
                Language = settings.Language;
                UiSize = settings.UiSize;
                Localizer.Apply(Language);
                // The dictionaries were switched on or off while the file was read, before the list was replaced.
                BibleReader.Library.Downloads.StrongsDictionaries.Refresh();
                _loaded = true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load settings: {ex}");
            }
        }
    }
}
