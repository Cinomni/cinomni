using System.Text.RegularExpressions;
using Cinomni.ReleaseParsing.Contracts;

namespace Cinomni.ReleaseParsing.Parsing;

/// <summary>Detects the structural quality of a release: source × resolution × modifier.</summary>
internal static class QualityDetector
{
    private static readonly Regex Res2160 = ParserRegex.Compile(@"\b(2160p|4k|uhd)\b");
    private static readonly Regex Res1080 = ParserRegex.Compile(@"\b1080[pi]\b");
    private static readonly Regex Res720 = ParserRegex.Compile(@"\b720p\b");
    private static readonly Regex Res576 = ParserRegex.Compile(@"\b576[pi]\b");
    private static readonly Regex Res480 = ParserRegex.Compile(@"\b480[pi]\b");

    private static readonly Regex Remux = ParserRegex.Compile(@"\bremux\b");
    private static readonly Regex Bluray = ParserRegex.Compile(@"\b(bluray|blu-ray|bdrip|brrip|bd25|bd50|bdremux|bdmv)\b");
    private static readonly Regex WebDl = ParserRegex.Compile(@"\b(web-?dl|webdl|amzn|nf|dsnp|hmax|atvp)\b");
    private static readonly Regex WebRip = ParserRegex.Compile(@"\b(web-?rip|webrip)\b");
    private static readonly Regex Web = ParserRegex.Compile(@"\bweb\b");
    private static readonly Regex Hdtv = ParserRegex.Compile(@"\b(hdtv|pdtv|sdtv|dsr)\b");
    private static readonly Regex Screener = ParserRegex.Compile(@"\b(bdscr|dvdscr|screener|scr)\b");
    private static readonly Regex Dvd = ParserRegex.Compile(@"\b(dvdrip|dvd-?r|dvd5|dvd9|dvd|xvid|divx)\b");
    private static readonly Regex Telesync = ParserRegex.Compile(@"\b(telesync|hdts|pdvd|predvd|ts)\b");
    private static readonly Regex Telecine = ParserRegex.Compile(@"\b(telecine|hdtc|tc)\b");
    private static readonly Regex Cam = ParserRegex.Compile(@"\b(cam-?rip|hdcam|cam)\b");

    public static Quality Detect(string scope) =>
        new(DetectSource(scope), DetectResolution(scope), DetectModifier(scope));

    private static QualityResolution DetectResolution(string s) =>
        Res2160.IsMatch(s) ? QualityResolution.R2160p
        : Res1080.IsMatch(s) ? QualityResolution.R1080p
        : Res720.IsMatch(s) ? QualityResolution.R720p
        : Res576.IsMatch(s) ? QualityResolution.R576p
        : Res480.IsMatch(s) ? QualityResolution.R480p
        : QualityResolution.Unknown;

    private static QualitySource DetectSource(string s)
    {
        // Remux implies a disc source; check the higher-fidelity sources first.
        if (Remux.IsMatch(s) || Bluray.IsMatch(s)) return QualitySource.Bluray;
        if (WebDl.IsMatch(s)) return QualitySource.WebDl;
        if (WebRip.IsMatch(s)) return QualitySource.WebRip;
        if (Web.IsMatch(s)) return QualitySource.WebDl; // bare WEB is treated as WEB-DL
        if (Hdtv.IsMatch(s)) return QualitySource.Hdtv;
        if (Screener.IsMatch(s)) return QualitySource.Screener;
        if (Dvd.IsMatch(s)) return QualitySource.Dvd;
        if (Telesync.IsMatch(s)) return QualitySource.Telesync;
        if (Telecine.IsMatch(s)) return QualitySource.Telecine;
        if (Cam.IsMatch(s)) return QualitySource.Cam;
        return QualitySource.Unknown;
    }

    private static QualityModifier DetectModifier(string s) =>
        Remux.IsMatch(s) ? QualityModifier.Remux : QualityModifier.None;
}
