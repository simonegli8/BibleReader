using Avalonia.Controls.Shapes;
using BibleMarkdown;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using static System.Net.Mime.MediaTypeNames;
using IO = System.IO;

namespace BibleReader.Library.Downloads
{
    public class BookArchive: InstallableBook
    {
        public string File { get; set; }
        public override string Origin => "Local File";

        // Words in capitals (LORD, GOD) become small caps. A word with Strong's attributes ("LORD_H3068_&lemma_%morph") is
        // matched as a whole, so its attributes are not mistaken for words in capitals (the morphology "HNcfsa" starts
        // with "HN"), and the small caps go around the whole word: "[Lord_H3068_&lemma_%morph]{.smallcaps}", which the
        // reader reads as a small caps run that holds a word with a Strong's number.
        static readonly Regex SmallCapsPattern = new(
            @"(?<!(^|\r?\n)#[^\r\n]*?)(?:(?<strongs>" + BookText.StrongsWord + @")|\p{Lu}{2,})",
            RegexOptions.Singleline | RegexOptions.Compiled);

        static readonly Regex StrongsAttributePart = new(@"^(?:[GH][0-9]+|&.+|%.+)$", RegexOptions.Compiled);

        static bool IsRomanNumberOrIsbn(string word) => Regex.IsMatch(word, "^(?:[IVXCD]+|ISBN)$", RegexOptions.Singleline);

        /// <summary>The first letter of the word and the rest in lower case: "LORD" becomes "Lord".</summary>
        static string Capitalize(string word) => word.Length < 2 ? word : word[0] + word[1..].ToLowerInvariant();

        /// <summary>
        /// A word with Strong's attributes: when all its words are in capitals (and at least one is not a roman number)
        /// it is returned as small caps around the whole word, else unchanged.
        /// </summary>
        static string SmallCapsStrongsWord(string token)
        {
            var parts = token.Split('_');
            var words = parts.Where(p => !StrongsAttributePart.IsMatch(p)).ToList();
            bool hasCapitals = words.Any(w => Regex.IsMatch(w, @"^\p{Lu}{2,}$") && !IsRomanNumberOrIsbn(w));
            if (!hasCapitals || words.Any(w => w.Any(char.IsLower))) return token; // no capitals, or mixed: not all small caps
            return "[" + string.Join('_', parts.Select(p => StrongsAttributePart.IsMatch(p) ? p : Capitalize(p))) + "]{.smallcaps}";
        }

        public static string PostprocessMarkdownSmallCaps(string text)
        {
            return SmallCapsPattern.Replace(text, m =>
            {
                if (m.Groups["strongs"].Success) return SmallCapsStrongsWord(m.Value);

                if (IsRomanNumberOrIsbn(m.Value))
                {
                    // is roman number
                    return m.Value;
                }

                var str = new StringBuilder("[");
                str.Append(m.Value[0]);
                for (int i = 1; i < m.Value.Length; i++) str.Append(Char.ToLower(m.Value[i]));
                str.Append("]{.smallcaps}");
                return str.ToString();
            });
        }

        public override async Task Install()
        {
            if (string.IsNullOrEmpty(File)) throw new InvalidOperationException("File is not set.");

            var path = ConfigPath;
            var src = IO.Path.Combine(path, "src");
            Directory.CreateDirectory(src);

            var ext = IO.Path.GetExtension(File).ToLower();
            if (ext == ".zip" || ext == ".7z")
            {
                Directory.Delete(src);
                Zip.UnzipZipFile(File, src);
            }
            else IO.File.Copy(File, IO.Path.Combine(src, IO.Path.GetFileName(File)), true);
            
            try {
                await Task.Run(() => {
                    var transform = new Transform(path, src);
                    transform.Import.Postprocess = PostprocessMarkdownSmallCaps;
                    transform.Import.All();
                    transform.Index.Create();
                    Directory.Delete(src, true);
                });
            }
            finally { }
        }
    }
}
