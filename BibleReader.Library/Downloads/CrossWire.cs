using Avalonia.Remote.Protocol;
using BibleMarkdown;
using BibleReader.Library.Downloads;
using HtmlAgilityPack;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace BibleReader.Library
{
    /// <summary>
    /// A module of The SWORD Project, offered by crosswire.org. <see cref="GetBiblesAsync"/> reads the list of Bibles
    /// from its module page, and <see cref="Install"/> downloads the module and imports it like
    /// <see cref="eBible.Install"/> does.
    /// </summary>
    public class CrossWireBook: InstallableBook
    {
        internal const string Host = "https://crosswire.org";
        const string ListUrl = Host + "/sword/modules/ModDisp.jsp?modType=";

        public override string Origin => "crosswire.org";
        protected virtual string DetailsUrl { get; set; }
        protected virtual string CrossWireModuleUrl { get; set; }
        public override InstallableBook.Classes Class => InstallableBook.Classes.Bible;

        /// <summary>The Bibles of crosswire.org (the Bible texts and translations), by language.</summary>
        public static Task<List<CrossWireBook>> GetBiblesAsync() =>
            GetModulesAsync("Bibles", () => new CrossWireBook());

        /// <summary>
        /// The modules of a kind that crosswire.org lists. Its page has a table per language (the language is in the
        /// heading before the table), with the link to the details of a module, its name, its description and the
        /// link to download it. The name of the module is the <see cref="BookText.EnglishName"/> (it names the folder of
        /// the module), the description is the <see cref="BookText.Name"/>.
        /// </summary>
        protected static async Task<List<T>> GetModulesAsync<T>(string modType, Func<T> create) where T : CrossWireBook
        {
            var html = new HtmlAgilityPack.HtmlDocument();
            html.LoadHtml(await Download.DownloadStringAsync(ListUrl + modType));

            var modules = new List<T>();
            foreach (var table in html.DocumentNode.Descendants("table").Where(t => t.HasClass("moddisp")))
            {
                var language = LanguageOf(table);
                foreach (var row in table.Descendants("tr"))
                {
                    var cells = row.Elements("td").ToList(); // the header row has th cells
                    if (cells.Count < 4) continue;

                    var name = Text(cells[1]);
                    var info = Href(cells[0]);
                    var download = Href(cells[3]);
                    if (name.Length == 0 || download.Length == 0) continue;

                    var module = create();
                    module.EnglishName = name;
                    module.Name = Text(cells[2]) is { Length: > 0 } description ? description : name;
                    module.Language = language;
                    module.LanguageEnglishName = EnglishLanguageName(language);
                    module.DetailsUrl = info.Length > 0 ? Absolute(info, "/sword/modules/") : null;
                    module.CrossWireModuleUrl = Absolute(download, "/sword/modules/");
                    modules.Add(module);
                }
            }
            if (modules.Count == 0) throw new NotSupportedException("crosswire.org returned an invalid format.");
            return modules;
        }

        static string Text(HtmlNode node) => Regex.Replace(HtmlEntity.DeEntitize(node.InnerText), @"\s+", " ").Trim();

        static string Href(HtmlNode cell) =>
            HtmlEntity.DeEntitize(cell.Descendants("a").FirstOrDefault()?.GetAttributeValue("href", string.Empty) ?? string.Empty).Trim();

        /// <summary>An address from a page of crosswire.org as a complete address (the links are relative to the page).</summary>
        static string Absolute(string href, string folder) =>
            href.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? href
            : href.StartsWith('/') ? Host + href
            : Host + folder + href;

        /// <summary>The language code of the table: from the heading ("Language: de") that comes before it.</summary>
        static string LanguageOf(HtmlNode table)
        {
            for (var node = table.PreviousSibling; node != null; node = node.PreviousSibling)
            {
                if (node.Name == "b" && Regex.Match(node.InnerText, @"Language:\s*(\S+)") is { Success: true } heading)
                    return heading.Groups[1].Value;
                if (node.Name == "br" && node.Id.StartsWith("lang_", StringComparison.Ordinal))
                    return node.Id["lang_".Length..];
            }
            return string.Empty;
        }

        static string EnglishLanguageName(string code)
        {
            if (code.Length == 0) return code;
            try
            {
                var name = CultureInfo.GetCultureInfo(code).EnglishName;
                return name.StartsWith("Unknown", StringComparison.OrdinalIgnoreCase) ? code : name;
            }
            catch (CultureNotFoundException) { return code; }
        }

        /// <summary>
        /// Downloads the module (a zip with the folders mods.d and modules) into the src folder of the module, and
        /// imports it: a Bible is converted to markdown books, a Strong's dictionary to the database of its entries.
        /// </summary>
        public override async Task Install()
        {
            if (string.IsNullOrEmpty(CrossWireModuleUrl)) throw new InvalidOperationException("The address of the module is not set.");

            // The download link leads to a page that sends the browser on to the zip (and tells about the module).
            var html = new HtmlAgilityPack.HtmlDocument();
            html.LoadHtml(await Download.DownloadStringAsync(CrossWireModuleUrl));
            var zip = html.DocumentNode.Descendants("a")
                    .Select(a => HtmlEntity.DeEntitize(a.GetAttributeValue("href", string.Empty)))
                    .FirstOrDefault(href => href.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                ?? Regex.Match(html.Text, @"URL=([^""'>\s]+\.zip)", RegexOptions.IgnoreCase).Groups[1].Value;
            if (string.IsNullOrEmpty(zip)) throw new InvalidDataException("crosswire.org did not give the address of the module's zip file.");

            var path = ConfigPath;
            var srcpath = Path.Combine(path, "src");
            await Download.DownloadAndUnzip(Absolute(zip, "/sword/servlet/"), srcpath);

            await Task.Run(() =>
            {
                var transform = new Transform(path, srcpath);
                transform.Import.Postprocess = BookArchive.PostprocessMarkdownSmallCaps;
                transform.Import.FromSWORD(true);
                transform.Import.ImportBookNames();
                transform.Import.PostprocessMarkdown();
                transform.Index.Create(LanguageCode);
                Directory.Delete(srcpath, true);
            });
            if (!IsImported)
                throw new InvalidDataException("The SWORD module was downloaded, but nothing could be imported from it.");
        }

        /// <summary>Whether the folder of the module holds what the import makes of it.</summary>
        protected virtual bool IsImported => 
            Directory.EnumerateFiles(ConfigPath)
            .Any(file => file.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ||
                file.EndsWith(".db", StringComparison.OrdinalIgnoreCase));
    }

    public interface IStrongDictionary
    {
        public string? LookupStrong(string strong);
        /// <summary>Whether the dictionary is used. Changing it must call <see cref="StrongsDictionaries.Refresh"/>, which tells <see cref="StrongsDictionaries.Any"/> listeners.</summary>
        public bool IsEnabled { get; set; }
    }

    /// <summary>A Strong's dictionary of crosswire.org (StrongsGreek, StrongsHebrew and their translations).</summary>
    public class CrossWireDictionary: CrossWireBook, IStrongDictionary, IDisposable
    {
        const string Url = "https://crosswire.org/sword/modules";
        public override string Origin => "crosswire.org";
        public override InstallableBook.Classes Class => InstallableBook.Classes.Dictionary;
        public bool IsEnabled
        {
            get => field;
            set
            {
                if (field == value) return;
                field = value;
                StrongsDictionaries.Refresh();
            }
        }

        // The modules of crosswire.org that have a Strong's number as the key of their entries.
        static readonly Regex StrongsModule = new(@"Strongs(Greek|Hebrew)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>The Strong's dictionaries among the dictionaries of crosswire.org.</summary>
        public static async Task<List<CrossWireDictionary>> GetStrongsDictionariesAsync() =>
            (await GetModulesAsync("Dictionaries", () => new CrossWireDictionary()))
                .Where(d => StrongsModule.IsMatch(d.EnglishName))
                .ToList();

        string DatabaseFile => Path.Combine(ConfigPath, StrongsDatabase.FileName);
        StrongsDatabase database = null;
        StrongsDatabase Database
        {
            get => database ??= StrongsDatabase.Open(DatabaseFile);
            set => database = value;
        }

        protected override bool IsImported => File.Exists(DatabaseFile);

        public override async Task Install()
        {
            await base.Install();
            IsEnabled = true; // an installed dictionary is used at once; it is switched off in the Strongs list
        }

        public string? LookupStrong(string strong) => Database.Get(strong);
        public override async Task Uninstall()
        {
            IsEnabled = false; // a dictionary that is removed is switched off first, so Any is told
            Dispose();
            await base.Uninstall();
        }

        public void Dispose() => database?.Dispose();
    }
}
