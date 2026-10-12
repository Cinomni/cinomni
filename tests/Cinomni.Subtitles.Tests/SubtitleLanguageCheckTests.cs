using Cinomni.Subtitles.Application;

namespace Cinomni.Subtitles.Tests;

/// <summary>
/// The alphabet check on a downloaded file. A provider filed a Hebrew subtitle as English and it was
/// played as the English one; the check refuses that, and refuses nothing it cannot be sure of.
/// </summary>
public sealed class SubtitleLanguageCheckTests
{
    private const string English =
        "1\n00:01:47,250 --> 00:01:50,500\n<i>This blade has a dark past.</i>\n\n2\n00:01:51,800 --> 00:01:55,800\n"
        + "It has shed much innocent blood.\n\n3\n00:01:58,000 --> 00:02:01,450\nYou're a fool for traveling alone, so ill-equipped.\n\n"
        + "4\n00:02:01,750 --> 00:02:04,800\nYou're lucky your blood's still flowing.\n";

    private const string Hebrew =
        "1\n00:01:47,250 --> 00:01:50,500\n.ללהב הזה יש עבר אפל\n\n2\n00:01:51,800 --> 00:01:55,800\n.הוא שפך כמויות רבות של דם\n\n"
        + "3\n00:01:58,000 --> 00:02:01,450\nאתה טיפש שאתה מטייל לבד, כל כך לא מצויד\n\n4\n00:02:01,750 --> 00:02:04,800\n"
        + "יש לך מזל שהדם שלך עדיין זורם. אלו אדמות של דרקונים, סינטל. את קרובה יותר משנדמה לך\n";

    private const string Russian =
        "1\n00:01:47,250 --> 00:01:50,500\nУ этого клинка тёмное прошлое.\n\n2\n00:01:51,800 --> 00:01:55,800\n"
        + "Он пролил много невинной крови.\n\n3\n00:01:58,000 --> 00:02:01,450\nТы глупец, раз путешествуешь один и так плохо снаряжён.\n\n"
        + "4\n00:02:01,750 --> 00:02:04,800\nТебе повезло, что твоя кровь всё ещё течёт. Это земли драконов.\n";

    [Fact]
    public void A_hebrew_file_labelled_english_is_refused()
    {
        Assert.False(SubtitleLanguageCheck.IsPlausible("en", Hebrew));
    }

    [Theory]
    [InlineData("en")]
    [InlineData("es")]
    [InlineData("pt-BR")]
    public void A_latin_file_passes_for_any_language_written_in_latin_letters(string language)
    {
        Assert.True(SubtitleLanguageCheck.IsPlausible(language, English));
    }

    [Fact]
    public void Each_alphabet_is_checked_both_ways()
    {
        Assert.True(SubtitleLanguageCheck.IsPlausible("ru", Russian));
        Assert.False(SubtitleLanguageCheck.IsPlausible("ru", English));
        Assert.False(SubtitleLanguageCheck.IsPlausible("en", Russian));
        Assert.True(SubtitleLanguageCheck.IsPlausible("he", Hebrew));
    }

    [Fact]
    public void Too_little_text_or_an_unlisted_language_always_passes()
    {
        Assert.True(SubtitleLanguageCheck.IsPlausible("en", "1\n00:00:01,000 --> 00:00:02,000\nשלום\n"));
        Assert.True(SubtitleLanguageCheck.IsPlausible("xx", Hebrew));
    }

    [Fact]
    public void Markup_is_not_counted_as_text()
    {
        var tagged = string.Concat(Enumerable.Repeat("<font color=\"#ffffff\"><i>{\\an8}</i></font>", 40)) + Hebrew;

        Assert.True(SubtitleLanguageCheck.IsPlausible("he", tagged));
    }
}
