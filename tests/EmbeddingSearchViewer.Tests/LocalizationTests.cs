using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using EmbeddingSearchViewer.Core.Localization;
using EmbeddingSearchViewer.Core.Models;
using EmbeddingSearchViewer.Infrastructure.Ingestion;
using EmbeddingSearchViewer.Infrastructure.Library;
using EmbeddingSearchViewer.Infrastructure.Profiles;
using Xunit;

namespace EmbeddingSearchViewer.Tests;

/// <summary>The texts of the app exist in every language, with the same placeholders, and the code and the XAML use real keys.</summary>
public sealed class LocalizationTests : IDisposable
{
    private static readonly Regex Japanese = new(@"[\u3040-\u30ff\u4e00-\u9fff\uff00-\uffef]");
    private static readonly Regex Placeholder = new(@"\{(\d+)(?:[,:][^}]*)?\}");

    // Texts that deliberately contain both languages (the language setting names itself in both).
    private static readonly HashSet<string> MixedLanguageKeys = new() { "Settings.LanguageTitle.Text" };

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "esv_loc_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "version.txt"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("The repository root (version.txt) was not found.");
    }

    private static IEnumerable<string> SourceFiles(string pattern)
    {
        string src = Path.Combine(RepoRoot(), "src");
        return Directory.EnumerateFiles(src, pattern, SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                        !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));
    }

    // ---- choosing the language ----

    [Theory]
    [InlineData("ja", "en-US", "ja")]
    [InlineData("en", "ja-JP", "en")]
    [InlineData("auto", "ja-JP", "ja")]
    [InlineData("auto", "en-US", "en")]
    [InlineData("auto", "fr-FR", "en")]   // a language without texts: English
    [InlineData(null, "ja-JP", "ja")]
    [InlineData("klingon", "ja-JP", "ja")] // an unknown setting is treated as "auto"
    [InlineData("klingon", "de-DE", "en")]
    public void Resolve_FollowsTheSettingOrWindows(string? setting, string windows, string expected)
    {
        Assert.Equal(expected, Loc.Resolve(setting, CultureInfo.GetCultureInfo(windows)));
    }

    [Fact]
    public void Language_IsJapaneseUntilItIsSet_RegardlessOfTheMachine()
    {
        Assert.Equal("ja", Loc.Language);
        Assert.Equal("ライブラリ管理", Loc.T("Shell.NavigationViewItem02.Content"));
    }

    [Fact]
    public void UseLanguage_ChangesOnlyTheCodeThatRunsInsideIt()
    {
        using (Loc.UseLanguage("en"))
        {
            Assert.Equal("en", Loc.Language);
            Assert.Equal("Library", Loc.T("Shell.NavigationViewItem02.Content"));
        }
        Assert.Equal("ja", Loc.Language);
        Assert.Equal("ライブラリ管理", Loc.T("Shell.NavigationViewItem02.Content"));
    }

    [Fact]
    public async System.Threading.Tasks.Task UseLanguage_FlowsThroughAwaitAndTasks()
    {
        using var english = Loc.UseLanguage("en");
        await System.Threading.Tasks.Task.Delay(10);
        Assert.Equal("Stopped", Loc.T("Library.Stopped"));

        string other = await System.Threading.Tasks.Task.Run(() => Loc.T("Library.Stopped"));
        Assert.Equal("Stopped", other); // it flows into the code that this code starts
    }

    [Fact]
    public void T_FormatsWithTheNumberFormatOfTheLanguage()
    {
        using (Loc.UseLanguage("en"))
        {
            Assert.Equal("Video a.mp4: scene 1,234 / 5,678 (22%)", Loc.T("Ingest.VideoProgress", "a.mp4", 1234, 5678, 22));
            Assert.EndsWith(" or more", Loc.T("Search.MinSimilarity.AtLeast", 0.5f));
        }
        Assert.Equal("動画 a.mp4: 1,234 / 5,678 場面 (22%)", Loc.T("Ingest.VideoProgress", "a.mp4", 1234, 5678, 22));
        Assert.Equal("検索結果 (7 件)", Loc.T("Search.ResultsHeader", 7));
    }

    [Fact]
    public void T_ShowsAMissingKeyInBrackets()
    {
        Assert.Equal("[No.Such.Key]", Loc.T("No.Such.Key"));
    }

    [Fact]
    public void ForUid_ListsThePropertiesOfAnElement_InTheLanguageInUse()
    {
        var ja = Loc.ForUid("Settings.RestartNow").ToDictionary(p => p.Property, p => p.Value);
        Assert.Equal("今すぐ再起動", ja["Content"]);

        using (Loc.UseLanguage("en"))
        {
            var en = Loc.ForUid("Settings.RestartNow").ToDictionary(p => p.Property, p => p.Value);
            Assert.Equal("Restart now", en["Content"]);
        }

        Assert.Empty(Loc.ForUid("Settings.Restart")); // a longer uid is not a prefix match
    }

    // ---- the resources are complete ----

    [Fact]
    public void EveryJapaneseTextHasAnEnglishOne_AndTheOtherWayRound()
    {
        var ja = Loc.OwnTable("ja");
        var en = Loc.OwnTable("en");

        Assert.NotEmpty(ja);
        Assert.Empty(ja.Keys.Except(en.Keys).OrderBy(k => k));
        Assert.Empty(en.Keys.Except(ja.Keys).OrderBy(k => k));
        Assert.All(en, kv => Assert.False(string.IsNullOrWhiteSpace(kv.Value), $"{kv.Key} is empty in English"));
    }

    [Fact]
    public void BothLanguagesHaveTheSamePlaceholders_AndTheyFormat()
    {
        var ja = Loc.OwnTable("ja");
        var en = Loc.OwnTable("en");
        var problems = new List<string>();

        foreach (var (key, jaText) in ja)
        {
            var enText = en[key];
            var jaIndexes = Placeholder.Matches(jaText).Select(m => m.Groups[1].Value).Distinct().OrderBy(x => x).ToList();
            var enIndexes = Placeholder.Matches(enText).Select(m => m.Groups[1].Value).Distinct().OrderBy(x => x).ToList();
            if (!jaIndexes.SequenceEqual(enIndexes)) problems.Add($"{key}: ja has {{{string.Join(",", jaIndexes)}}}, en has {{{string.Join(",", enIndexes)}}}");

            // A broken format string only shows when it is used: try both, with every argument a number or a time.
            object?[] args = Enumerable.Range(0, 8).Select(_ => (object?)TimeSpan.FromSeconds(75)).ToArray();
            foreach (var (language, text) in new[] { ("ja", jaText), ("en", enText) })
            {
                try { string.Format(CultureInfo.GetCultureInfo(language), text, args); }
                catch (FormatException)
                {
                    // Number formats do not apply to a TimeSpan: retry with numbers, which is what the code passes there.
                    try { string.Format(CultureInfo.GetCultureInfo(language), text, Enumerable.Range(1, 8).Select(i => (object?)(i * 1.5)).ToArray()); }
                    catch (FormatException ex) { problems.Add($"{key} ({language}): {ex.Message}"); }
                }
            }
        }

        Assert.Empty(problems);
    }

    [Fact]
    public void TheEnglishTextsContainNoJapanese_ExceptWhereBothAreMeant()
    {
        var offenders = Loc.OwnTable("en")
            .Where(kv => Japanese.IsMatch(kv.Value) && !MixedLanguageKeys.Contains(kv.Key))
            .Select(kv => kv.Key)
            .OrderBy(k => k)
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void TheEnglishTextsAreActuallyTranslated()
    {
        var ja = Loc.OwnTable("ja");
        var en = Loc.OwnTable("en");
        // Same text in both is only fine when it has no words to translate (a separator, a bare placeholder).
        var untranslated = ja.Where(kv => Japanese.IsMatch(kv.Value) && en[kv.Key] == kv.Value).Select(kv => kv.Key).ToList();

        Assert.Empty(untranslated);
    }

    // ---- the code and the XAML use real keys ----

    [Fact]
    public void EveryKeyUsedInTheCode_Exists()
    {
        var keys = Loc.OwnTable("ja").Keys.ToHashSet();
        var missing = new List<string>();

        foreach (var file in SourceFiles("*.cs").Where(f => !f.Contains("Localization")))
        {
            foreach (Match m in Regex.Matches(File.ReadAllText(file), @"Loc\.T\(""([^""]+)"""))
            {
                if (!keys.Contains(m.Groups[1].Value)) missing.Add($"{Path.GetFileName(file)}: {m.Groups[1].Value}");
            }
        }

        Assert.Empty(missing);
    }

    [Fact]
    public void EveryUidInTheXaml_HasTextsForOneOfTheKnownProperties()
    {
        var table = Loc.OwnTable("ja");
        var known = new HashSet<string> { "Text", "Content", "Header", "PlaceholderText", "ToolTip", "OnContent", "OffContent", "Title", "Message" };
        var problems = new List<string>();
        int uids = 0;

        foreach (var file in SourceFiles("*.xaml"))
        {
            foreach (Match m in Regex.Matches(File.ReadAllText(file), @"loc:L\.Uid=""([^""]+)"""))
            {
                uids++;
                string uid = m.Groups[1].Value;
                var props = table.Keys.Where(k => k.StartsWith(uid + ".", StringComparison.Ordinal)).Select(k => k[(uid.Length + 1)..]).ToList();
                if (props.Count == 0) problems.Add($"{Path.GetFileName(file)}: {uid} has no texts");
                foreach (var p in props.Where(p => !known.Contains(p))) problems.Add($"{Path.GetFileName(file)}: {uid}.{p} is not a property the app sets");
            }
        }

        Assert.True(uids > 100, "the XAML should use many uids");
        Assert.Empty(problems);
    }

    [Fact]
    public void NoXamlKeyIsLeftWithoutAnElementThatUsesIt()
    {
        // A text for an element that no longer exists is dead weight (and hints at a renamed uid).
        var used = new HashSet<string>();
        foreach (var file in SourceFiles("*.xaml"))
        {
            foreach (Match m in Regex.Matches(File.ReadAllText(file), @"loc:L\.Uid=""([^""]+)""")) used.Add(m.Groups[1].Value);
        }

        var codeKeys = new HashSet<string>();
        foreach (var file in SourceFiles("*.cs").Where(f => !f.Contains("Localization")))
        {
            foreach (Match m in Regex.Matches(File.ReadAllText(file), @"Loc\.T\(""([^""]+)""")) codeKeys.Add(m.Groups[1].Value);
        }

        var unused = Loc.OwnTable("ja").Keys
            .Where(k => !codeKeys.Contains(k))
            .Where(k => !used.Any(u => k.StartsWith(u + ".", StringComparison.Ordinal) && k.IndexOf('.', u.Length + 1) < 0))
            .Where(k => k != "Profile.DefaultName") // used by the profile manager through a computed key
            .OrderBy(k => k)
            .ToList();

        Assert.Empty(unused);
    }

    // ---- messages built in the lower layers follow the language ----

    [Fact]
    public void MessagesFromTheCoreAndInfrastructure_FollowTheLanguage()
    {
        var item = new SearchResultItem { Chunk = new MediaChunk { Modality = ModalityType.VideoSegment }, Asset = new MediaAsset { MediaType = MediaType.Video } };
        item.Scenes.AddRange(new[] { item, item, item });
        var failures = new[] { new ImageFailure(@"C:\x\a.jpg", "corrupt"), new ImageFailure(@"C:\x\b.jpg", "corrupt") };

        Assert.Equal("一致した場面 3 件", item.GroupText);
        Assert.Contains("次回から", IngestionPipelineWorker.DescribeFailures(failures, willExclude: true));

        using (Loc.UseLanguage("en"))
        {
            Assert.Equal("3 matching scenes", item.GroupText);
            string text = IngestionPipelineWorker.DescribeFailures(failures, willExclude: true);
            Assert.Contains("a.jpg (corrupt), b.jpg (corrupt)", text);
            Assert.Contains("left out of the index", text);
            Assert.DoesNotContain("次回", text);
        }
    }

    [Fact]
    public void TheDefaultProfileIsNamedInTheLanguageOfTheFirstStart()
    {
        using (Loc.UseLanguage("en"))
        {
            Assert.Equal("Default", new ProfileManager(Path.Combine(_dir, "en")).Current.Name);
        }
        Assert.Equal("標準", new ProfileManager(Path.Combine(_dir, "ja")).Current.Name);
    }

    [Fact]
    public void ProfileErrors_AreInTheLanguageInUse()
    {
        var manager = new ProfileManager(Path.Combine(_dir, "p"));
        manager.Create("Work");

        using (Loc.UseLanguage("en"))
        {
            var ex = Assert.Throws<InvalidOperationException>(() => manager.Create("work"));
            Assert.Equal("A profile named \"work\" already exists.", ex.Message);
        }
        var exJa = Assert.Throws<InvalidOperationException>(() => manager.Create("work"));
        Assert.Equal("「work」という名前のプロファイルがすでにあります。", exJa.Message);
    }

    // ---- the language setting is stored ----

    [Fact]
    public void AppSettings_DefaultToAuto_AndKeepTheChoice()
    {
        var store = new AppSettingsStore(Path.Combine(_dir, "settings.json"));
        Assert.Equal("auto", store.Load().Language);

        store.Save(new AppSettings { Language = "en" });

        Assert.Equal("en", new AppSettingsStore(Path.Combine(_dir, "settings.json")).Load().Language);
    }

    [Fact]
    public void AppSettings_ABrokenFile_MeansAuto()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "settings.json"), "{ not json");

        Assert.Equal("auto", new AppSettingsStore(Path.Combine(_dir, "settings.json")).Load().Language);
    }
}
