using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using System.Linq;
using System.IO;
using BibleReader.Library.Downloads;


namespace BibleReader.Library;


public class eBible : InstallableBook
{
    const string Url = "https://ebible.org/find";

    public override string Origin => "ebible.org";
    public override Classes Class { get => InstallableBook.Classes.Bible; }
    protected virtual string DetailsUrl { get; set; }
    static async Task<string> GetListAsync() => await Download.DownloadStringAsync(Url);

    public static async Task<IEnumerable<eBible>> GetBiblesAsync()
    {
        var html = new HtmlAgilityPack.HtmlDocument();
        var htmlText = await GetListAsync();
        html.LoadHtml(htmlText);
        var table = html.DocumentNode.Descendants("table").LastOrDefault();
        var rows = table.Descendants("tr").Skip(1); // Skip header row
        return rows
            .Select(row =>
            {
                var cells = row.Descendants("td").ToList();
                if (cells.Count >= 5)
                {
                    var linkNode = cells[0].Descendants("a").FirstOrDefault();
                    if (linkNode != null)
                    {
                        var countryCode = linkNode.GetAttributeValue("href", string.Empty).Split('=').Skip(1).FirstOrDefault();
                        var countryName = linkNode.InnerText.Trim();
                        var languageName = cells[1].Descendants("a").FirstOrDefault()?.InnerText.Trim() ?? "";
                        var languageEnglishName = cells[2].Descendants("a").FirstOrDefault()?.InnerText.Trim() ?? "";
                        var englishName = cells[4].Descendants("a").FirstOrDefault()?.InnerText.Trim();
                        var name = cells[3].Descendants("a").FirstOrDefault()?.InnerText.Trim();
                        var detailsUrl = cells[3].Descendants("a").FirstOrDefault().GetAttributeValue("href", string.Empty);
                        return new eBible
                        {
                            CountryName = countryName,
                            CountryCode = countryCode,
                            Name = name,
                            EnglishName = englishName,
                            DetailsUrl = Url + "/" + detailsUrl,
                            Language = languageName,
                            LanguageEnglishName = languageEnglishName,
                        };
                    }
                }
                throw new NotSupportedException("ebible.org returned an invalid format.");
            })
            .ToList();
    }

    public override async Task Install()
    {
        var details = await Download.DownloadStringAsync(DetailsUrl);
        var html = new HtmlAgilityPack.HtmlDocument();
        html.LoadHtml(details);
        var swordUrl = html.DocumentNode
            .Descendants("a")
            .Select(a => a.GetAttributeValue("href", string.Empty))
            .FirstOrDefault(url => url.Contains("/sword/zip/"));
        var path = ConfigPath;
        var srcpath = Path.Combine(path, "src");
        await Download.DownloadAndUnzip(swordUrl, srcpath);
        // ImportFromSWORD(mdpath, srcpath) writes the markdown books to mdpath, and does nothing unless FromSource is set.
        await Task.Run(() => {
            var transform = new BibleMarkdown.Transform(path, srcpath);
            transform.Import.Postprocess = BookArchive.PostprocessMarkdownSmallCaps;
            transform.Import.FromSWORD(true);
            transform.Import.ImportBookNames();
            transform.Import.PostprocessMarkdown();
            transform.Index.Create(LanguageCode);

            Directory.Delete(srcpath, true);
        });
    if (!Directory.EnumerateFiles(path, "*.md").Any())
            throw new InvalidDataException("The SWORD module was downloaded, but no books could be imported from it.");
    }
}
