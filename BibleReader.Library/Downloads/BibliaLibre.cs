using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;
using System.Globalization;
using System.Text.RegularExpressions;
using BibleMarkdown;

namespace BibleReader.Library.Downloads
{
    /// <summary>
    /// The Bibles that the Biblia del Pueblo project publishes at https://biblia-del-pueblo.github.io/books: the output of
    /// <c>bibmark -publish</c> (see <c>BibleMarkdown.Publisher</c>), an index.json that lists a zip for every Bible, with the
    /// info.json that is in the zip. The zip holds the markdown books that the reader reads, so installing is
    /// unzipping it; nothing is converted.
    /// </summary>
    public class BibliaLibreBible : InstallableBook
    {
        public const string Url = "https://biblia-del-pueblo.github.io/books";
        public const string ImagesUrl = "https://biblia-del-pueblo.github.io/book-covers";

        const string IndexUrl = Url + "/index.json";
        const string ImagesIndexUrl = ImagesUrl + "/index.json";

        public override string Origin => "BibliaLibre";
        protected virtual string DetailsUrl { get; set; }

        /// <summary>
        /// The Bibles of the index. Every entry is <c>{ "File": "Name.zip", "Info": { ... } }</c>; the info is an
        /// <see cref="InstallableBook"/> (name, language, country ...). A Bible whose info lacks a name takes the one of its zip.
        /// </summary>
        public static async Task<IEnumerable<InstallableBook>> GetBiblesAsync()
        {
            var index = JArray.Parse(await Download.DownloadStringAsync(IndexUrl));
            var serializer = JsonSerializer.Create(new JsonSerializerSettings { Converters = { new StringEnumConverter() } });

            var bibles = new List<BibliaLibreBible>();
            foreach (var entry in index.OfType<JObject>())
            {
                var file = (string?)entry["File"];
                if (string.IsNullOrWhiteSpace(file)) continue;

                // A bad info is no reason to leave the Bible out: it is listed with what the zip's name tells.
                BibliaLibreBible bible;
                try { bible = (entry["Info"] as JObject)?.ToObject<BibliaLibreBible>(serializer) ?? new BibliaLibreBible(); }
                catch (JsonException) { bible = new BibliaLibreBible(); }

                var name = Path.GetFileNameWithoutExtension(file);
                if (string.IsNullOrWhiteSpace(bible.EnglishName)) bible.EnglishName = name;
                if (string.IsNullOrWhiteSpace(bible.Name)) bible.Name = bible.EnglishName;
                if (bible.Class == Classes.All) bible.Class = Classes.Bible; // "All" is the filter, not a kind of module
                bible.DetailsUrl = Url + "/" + Uri.EscapeDataString(file); // the address of the zip
                bibles.Add(bible);
            }
            return bibles;
        }

        public static async Task UpdateBookCoversAsync()
        {
            var coversPath = Settings.Current.BookCoversPath;
            Directory.CreateDirectory(coversPath);

            var newest = new DirectoryInfo(coversPath).EnumerateFiles("*.*", SearchOption.AllDirectories)
                .Select(file => (DateTime?)file.LastWriteTimeUtc)
                .Max() ?? DateTime.MinValue;

            string[] index = Array.Empty<string>();
            try
            {
                index = JsonConvert.DeserializeObject<string[]>(await Download.DownloadStringAsync(ImagesIndexUrl));
                var zips = index
                    .Select(file => new
                    {
                        Time = DateTime.ParseExact(
                            Regex.Match(file, @"^covers\.since\.(?<time>[0-9]+-[0-9]+-[0-9]+_[0-9]+-[0-9]+-[0-9]+).zip$").Groups["time"].Value,
                                "yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture),
                        File = ImagesUrl + "/" + file
                    })
                    .Where(file => file.Time >= newest); // only select new zips that we have not downloaded yet
                var tempPath = Path.Combine(coversPath, ".temp");
                var downloaders = zips
                    .Select(zip => Download.DownloadFileAndUnzipAsync(zip.File, Path.GetTempFileName() + ".zip", Path.Combine(tempPath, $"covers.since.{zip.Time.ToString("yyyy-MM-dd_HH-mm-ss")}")));
                await Task.WhenAll(downloaders);
                foreach (var zipdir in Directory.EnumerateDirectories(tempPath)
                    .OrderBy(path => path))
                {
                    var files = Directory.EnumerateFiles(zipdir, "*.*", SearchOption.AllDirectories);
                    foreach (var file in files)
                    {
                        var relativePath = Path.GetRelativePath(zipdir, file);
                        var name = Path.GetFileName(file);
                        if (name.StartsWith("~"))
                        {
                            var fileToDelete = Path.Combine(coversPath, Path.GetDirectoryName(relativePath), name.Substring(1));
                            if (File.Exists(fileToDelete)) File.Delete(fileToDelete);
                        }
                        else
                        {
                            var fileToCopy = Path.Combine(coversPath, relativePath);
                            File.Copy(file, fileToCopy, true);
                        }
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        public static async Task GetImagesAsync(IEnumerable<InstallableBook> books)
        {
            await UpdateBookCoversAsync();

            var coversPath = Settings.Current.BookCoversPath;
            var folders = Directory.EnumerateDirectories(coversPath)
                .Select(dir => new
                {
                    Path = dir,
                    Name = Path.GetFileName(dir)
                })
                .OrderBy(f => f.Name.Length)
                .ThenBy(f => f.Name)
                .ToArray();

            foreach (var book in books)
            {
                var bfolders = folders
                .Where(f => book.Language.Contains(f.Name) ||
                    book.EnglishName.Contains(f.Name) ||
                    book.LanguageCode.Contains(f.Name) ||
                    Regex.IsMatch(f.Name, @"[a-z]{2,3}-[A-Z]{2,3}") && f.Name.Contains(book.LanguageCode));
                var folder = bfolders
                    .FirstOrDefault();
                if (folder != null)
                {
                    int bookyear = 0, bookyearen = 0;
                    var m = Regex.Match(book.Name, "[0-9]{4}");
                    var men = Regex.Match(book.EnglishName, "[0-9]{4}");
                    if (m.Success) bookyear = int.Parse(m.Value);
                    if (men.Success) bookyearen = int.Parse(men.Value);
                    bookyear = Math.Max(bookyear, bookyearen);

                    book.Image = Directory.EnumerateFiles(folder.Path, "*:*", SearchOption.AllDirectories)
                        .Select(img =>
                        {
                            var filename = Path.GetFileNameWithoutExtension(img);
                            var name = Regex.Replace(filename, "[0-9]+$", "");
                            int year = 0, bookyear = 0, bookyearen = 0;
                            int.TryParse(Regex.Replace(filename, ".?(?=[0-9]+$)", ""), out year);

                            if (book.EnglishName.Contains(name) || book.Name.Contains(name))
                            {
                                return new
                                {
                                    Image = img,
                                    Year = year,
                                };
                            }
                            else return null;
                        })
                        .Where(img => img != null)
                        .OrderBy(img => img.Year)
                        .FirstOrDefault(img => img.Year >= bookyear)
                        ?.Image;
                }
                else book.Image = null;
            }
        }

        /// <summary>Downloads the zip into the folder of the Bible; its files are the books, ready to be read.</summary>
        public override async Task Install()
        {
            if (string.IsNullOrEmpty(DetailsUrl)) throw new InvalidOperationException("The address of the Bible is not set.");
            var srcpath = Path.Combine(ConfigPath, "src");
            await Task.Run(async () =>
            {
                await Download.DownloadAndUnzip(DetailsUrl, srcpath);
                var transform = new Transform(ConfigPath, srcpath);
                transform.Import.All();
                if (!Directory.EnumerateFiles(ConfigPath, "*.md").Any() &&
                    !Directory.EnumerateFiles(ConfigPath, "*.db").Any())
                    throw new InvalidDataException("The Bible was downloaded, but it has no books.");
                transform.Index.Create();
                Directory.Delete(srcpath, true);
            });
        }
    }
}
