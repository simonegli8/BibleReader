using Avalonia.Markup.Xaml.Templates;
using BibleMarkdown;
using BibleReader.Library.Downloads;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace BibleReader.Library;


/// <summary>
/// The Bibles that the Biblia del Pueblo project publishes at https://biblia-del-pueblo.github.io/books: the output of
/// <c>bibmark -publish</c> (see <c>BibleMarkdown.Publisher</c>), an index.json that lists a zip for every Bible, with the
/// info.json that is in the zip. The zip holds the markdown books that the reader reads, so installing is
/// unzipping it; nothing is converted.
/// </summary>
public class BibliaLibreBible : InstallableBook
{
    public const string Url = "https://biblia-del-pueblo.github.io/BibliaLibre/books";
    public const string ImagesUrl = "https://biblia-del-pueblo.github.io/BibliaLibre/book-covers";

    const string IndexUrl = Url + "/index.json";
    const string ImagesIndexUrl = ImagesUrl + "/index.json";

    public override string Origin => "Biblia Libre";
    protected virtual string DetailsUrl { get; set; }

    public BibliaLibreBible() { }
    public BibliaLibreBible(InstallableBook source): base(source)
    {
        if (source is BibliaLibreBible blbible) DetailsUrl = blbible.DetailsUrl;
    }

    /// <summary>
    /// The Bibles of the index. Every entry is <c>{ "File": "Name.zip", "Info": { ... } }</c>; the info is an
    /// <see cref="InstallableBook"/> (name, language, country ...). A Bible whose info lacks a name takes the one of its zip.
    /// </summary>
    public static async Task<IEnumerable<InstallableBook>> GetBooksAsync()
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
            InstallableBook book;
            try { book = (entry["Info"] as JObject)?.ToObject<InstallableBook>(serializer) ?? new InstallableBook(); }
            catch (JsonException) { book = new BibliaLibreBible(); }

            if (book.Class != Classes.Dictionary) bible = new BibliaLibreBible(book);
            else bible = new BibliaLibreDictionary(book);

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

        var tempPath = Path.Combine(coversPath, ".temp");
        try
        {
            if (Directory.Exists(tempPath)) Directory.Delete(tempPath, true);

            var newest = new DirectoryInfo(coversPath).EnumerateFiles("*.*", SearchOption.AllDirectories)
                .Select(file => (DateTime?)file.LastWriteTimeUtc)
                .Max() ?? DateTime.MinValue;

            var indexJson = await Download.DownloadStringAsync(ImagesIndexUrl);
            var zips = JsonConvert.DeserializeObject<string[]>(indexJson)
                .Select(file =>
                {
                    if (Publisher.CoverZipName.Match(file) is { Success: true } match)
                    {
                        var time = Publisher.ParseCoverDate(match.Groups["time"].Value);
                        return new
                        {
                            Time = time,
                            File = ImagesUrl + "/" + file,
                            TempDir = Path.Combine(tempPath, $"covers.upto.{time.ToString("yyyy-MM-dd_HH-mm-ss")}")
                        };
                    }
                    else return null;
                })
                .Where(file => file != null && file.Time >= newest)
                .OrderBy(z => z.Time).ToList(); // only select new zips that we have not downloaded yet
            var downloaders = zips
                .Select(zip => Download.DownloadFileAndUnzipAsync(zip.File, Path.GetTempFileName() + ".zip", zip.TempDir));
            await Task.WhenAll(downloaders);
            foreach (var zip in zips)
            {
                var files = Directory.EnumerateFiles(zip.TempDir, "*.*", SearchOption.AllDirectories);
                foreach (var file in files)
                {
                    var relativePath = Path.GetRelativePath(zip.TempDir, file);
                    var name = Path.GetFileName(file);
                    if (name.StartsWith("~"))
                    {
                        var fileToDelete = Path.Combine(coversPath, Path.GetDirectoryName(relativePath)!, name.Substring(1));
                        if (File.Exists(fileToDelete)) File.Delete(fileToDelete);
                    }
                    else
                    {
                        var fileToCopy = Path.Combine(coversPath, relativePath);
                        Directory.CreateDirectory(Path.GetDirectoryName(fileToCopy)!);
                        File.Copy(file, fileToCopy, true);
                        File.SetLastWriteTimeUtc(fileToCopy, zip.Time);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempPath)) Directory.Delete(tempPath, true);
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
            }
        }
    }
    static readonly Regex CodeFolder = new(@"^[a-z]{2,3}(?:-[A-Z]{2,3})?$");
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
            var lang = book.LanguageCode?.Split('-')[0];   // read once per book

            var bfolders = folders.Where(f => (CodeFolder.IsMatch(f.Name) && lang != null)
                ? f.Name.Split('-')[0].Equals(lang, StringComparison.OrdinalIgnoreCase)
                : f.Name.Equals(book.LanguageEnglishName, StringComparison.OrdinalIgnoreCase) ||
                  f.Name.Equals(book.Language, StringComparison.OrdinalIgnoreCase));

            foreach (var folder in bfolders)
            {
                int bookyear = 0, bookyearen = 0;
                var m = Regex.Match(book.Name, "[0-9]{4}");
                var men = Regex.Match(book.EnglishName, "[0-9]{4}");
                if (m.Success) bookyear = int.Parse(m.Value);
                if (men.Success) bookyearen = int.Parse(men.Value);
                bookyear = Math.Max(bookyear, bookyearen);

                book.Image = Directory.EnumerateFiles(folder.Path, "*.*", SearchOption.AllDirectories)
                    .Select(img =>
                    {
                        var filename = Path.GetFileNameWithoutExtension(img);
                        var name = Regex.Replace(filename, @"-|\s*[0-9]{4}$", "");
                        // TryParse sets year to 0 on failure, so a file without a year must be kept as "any year" explicitly.
                        int year = int.TryParse(Regex.Replace(filename, @"^.*?(?=[0-9]{4}$)", ""), out var parsed) ? parsed : int.MaxValue;

                        // Compare composed (NFC) so "ü" matches whether the file name stores it as one char or u + diaeresis.
                        name = name.Normalize(NormalizationForm.FormC);
                        if (book.EnglishName.Normalize(NormalizationForm.FormC).Contains(name) ||
                            book.Name.Normalize(NormalizationForm.FormC).Contains(name))
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
                    .OrderBy(img => img!.Year)
                    .FirstOrDefault(img => img!.Year >= bookyear)
                    ?.Image;
                if (book.Image != null) break;
            }

            // no cover found: one is made (an SVG as a data address), the same one for the same book
            if (book.Image == null || Path.GetFileNameWithoutExtension(book.Image).StartsWith("-")) book.Image = CoverFallback.Create(book);
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
            transform.Index.Create(LanguageCode);
            Directory.Delete(srcpath, true);
        });
    }
}

public class BibliaLibreDictionary: BibliaLibreBible, IStrongDictionary
{
    public BibliaLibreDictionary() { }
    public BibliaLibreDictionary(InstallableBook source) : base(source) { }
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
    string DatabaseFile => Path.Combine(ConfigPath, StrongsDatabase.FileName);
    StrongsDatabase database = null;
    StrongsDatabase Database
    {
        get => database ??= StrongsDatabase.Open(DatabaseFile);
        set => database = value;
    }

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