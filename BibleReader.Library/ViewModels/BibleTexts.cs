using Avalonia.Controls;
using Avalonia.Controls.Platform;
using Avalonia.Media;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using IO = System.IO;

namespace BibleReader.Library;

public class Text
{
    public class Book
    {
        public string Name { get; set; }
        public string Abbrevation { get; set; }
        public string Description { get; set; }
        public string File { get; set; }
        public string Markdown { get => field ??= IO.File.ReadAllText(File, System.Text.Encoding.UTF8); set => field = value; }
    }
    public class VersePosition
    {
        public Book Book { get; set; }
        public int Chapter { get; set; }
        public int Verse { get; set; }
    }

    public class Run
    {
        public enum Classes
        {
            Text,
            Italic,
            Bold,
            SmallCaps,
            WordWithStrong,
            WordsOfJesus,
            Greek,
            Hebrew,
            Arabic,
            Link,
            Title,
            Book,
            Chaper,
            Verse,
            Footnote,
            Paragraph
        }
        public Classes Class { get; set; }
        public string Text { get; set; }
        public string Url { get; set; }
        public IEnumerable<Run> Runs { get; set; }
        public string StrongNumber { get; set; }
        public VersePosition Reference { get; set; }
        public Color MarkerColor { get; set; }
        public int Chapter { get; set; }
        public Run Parent { get; private set; }
        IEnumerable<Run> Append(IEnumerable<Run> children)
        {
            yield return this;
            foreach (var run in children)
            {
                if (run.Parent == null) run.Parent = this;
                yield return run;
            }
        }
        public IEnumerable<Run> All => Runs
            .SelectMany(run => run.Append(run.All));
    }

    public IEnumerable<Run> Runs { get; set; }
    public string Markdown
    {
        get => field;
        set
        {
            if (field != value)
            {
                field = value;
                Parse();
            }
        }
    }

    public IEnumerable<Run> ParseText(string text, Run.Classes textClass = Run.Classes.Text)
    {
        foreach (Match m in Regex.Matches(text, @"\*\*(?<bold>.*?)\*\*|\*(?<italic>.*?)\*|\^\[(?<footnote>.*?)\]|" +
            @"\[(?<smallcaps>.*?)\]\(.smallcaps\)|\[(?<wordsofjesus>.*?)\]\(.wj\)|" +
            @"\[(?<link>(.*?)\]\((?<linkurl>[a-zA-Z]+://.*?)\)|(?<normal>.*?"))
        {
            if (m.Groups["bold"].Success)
                yield return new Run
                {
                    Class = Run.Classes.Bold,
                    Text = m.Groups["bold"].Value,
                    Runs = ParseText(m.Groups["bold"].Value, Run.Classes.Bold).ToList()
                };
            else if (m.Groups["italic"].Success)
                yield return new Run
                {
                    Class = Run.Classes.Italic,
                    Text = m.Groups["bold"].Value,
                    Runs = ParseText(m.Groups["bold"].Value, Run.Classes.Italic).ToList()
                };
            else if (m.Groups["footnote"].Success)
                yield return new Run
                {
                    Class = Run.Classes.Footnote,
                    Text = m.Groups["footnote"].Value,
                    Runs = ParseText(m.Groups["footnote"].Value, Run.Classes.Text).ToList()
                };
            else if (m.Groups["smallcaps"].Success)
                yield return new Run
                {
                    Class = Run.Classes.SmallCaps,
                    Text = m.Groups["smallcaps"].Value,
                    Runs = ParseText(m.Groups["smallcaps"].Value, Run.Classes.SmallCaps).ToList()
                };
            else if (m.Groups["link"].Success)
                yield return new Run
                {
                    Class = Run.Classes.Link,
                    Text = m.Groups["link"].Value,
                    Url = m.Groups["linkurl"].Value,
                    Runs = Enumerable.Empty<Run>()
                };
            else if (m.Groups["wordsofjesus"].Success)
                yield return new Run
                {
                    Class = Run.Classes.WordsOfJesus,
                    Text = m.Groups["wordsofjesus"].Value,
                    Runs = ParseText(m.Groups["wordsofjesus"].Value, Run.Classes.SmallCaps).ToList()
                };
            else if (m.Groups["normal"].Success)
                yield return new Run
                {
                    Class = Run.Classes.Text,
                    Text = m.Groups["normal"].Value,
                    Runs = Enumerable.Empty<Run>()
                };
        }
    }
    IEnumerable<Run> ParseVerse(Match verse)
    {
        if (verse.Groups["title"].Success)
        {
            foreach (Run run in ParseText(verse.Groups["title"].Value, Run.Classes.Title)) yield return run;
        }
        else
        {
            foreach (Run run in ParseText(verse.Groups["title"].Value, Run.Classes.Title)) yield return run;
        }

    }
    IEnumerable<Run> ParseChapter(Match chapter)
    {
        yield return new Run
        {
            Class = Run.Classes.ChaperNumber,
            Chapter = int.Parse(chapter.Groups["chno"].Value),
            Text = chapter.Groups["chno"].Value
        };
        foreach (Run run in Regex.Matches(chapter.Groups["chbody"].Value, @"(^|\n)#\s+(?<title>.*?)\r?\n|@(?<verseno[0-9]+)(?=\s+)(?<versebody>.*?)(?:@[0-9]\s+|\r?\n#)")
            .Select(verse => ParseVerse(verse))) yield return run;
    }
    public IEnumerable<Run> Parse(string markdown)
    {
        // TODO strip comments
        var chapters = Regex.Matches(markdown, @"(^|\n)#\s+(?<chno>[0-9]+)\r?\n(?<chbody>.*?)(?:\n#\s+[0-9]+\r?\n|$)")
            .OfType<Match>();
        return chapters.SelectMany(chapter => ParseChapter(chapter));
    }
}