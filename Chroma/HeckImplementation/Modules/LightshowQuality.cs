#if !PRE_V1_37_1
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace Chroma.Modules;

// Decides whether a difficulty carries a hand-made lightshow worth its own environment. Auto-lit maps
// only emit basic events, while authored shows add environment edits, per-light Chroma data or v3/v4
// event box groups. The files are scanned as text so large maps stay cheap to classify.
internal static class LightshowQuality
{
    private const int MinLightIds = 50;
    private const int MinGradients = 10;
    private const int MinColors = 300;
    private const int MinBoxGroups = 20;

    private static readonly Regex EnvironmentEdits = new(@"""_?environment""\s*:\s*\[\s*\{", RegexOptions.Compiled);
    private static readonly Regex LightIds = new(@"""_?lightID""\s*:", RegexOptions.Compiled);
    private static readonly Regex Gradients = new(@"""_?(lightGradient|gradient)""\s*:", RegexOptions.Compiled);
    private static readonly Regex Colors = new(@"""_?color""\s*:", RegexOptions.Compiled);
    private static readonly Regex V3BoxGroups = new(@"""lightColorEventBoxGroups""\s*:\s*\[\s*\{", RegexOptions.Compiled);
    private static readonly Regex V4BoxGroups = new(@"""eventBoxGroups""\s*:\s*\[\s*\{", RegexOptions.Compiled);
    private static readonly Regex BoxGroupEntries = new(@"""g""\s*:", RegexOptions.Compiled);

    private static readonly Dictionary<string, (DateTime Written, bool Quality)> Cache = new();

    internal static bool IsQuality(BeatmapLevel beatmapLevel, in BeatmapKey beatmapKey)
    {
        try
        {
            string? folder = GetFolder(beatmapLevel);
            if (folder == null)
            {
                return false;
            }

            BeatmapKey key = beatmapKey;
            return GetDifficultyFiles(folder, GetCharacteristicName(key.characteristic), key.difficulty.ToString())
                .Any(file => IsQualityFile(file, key));
        }
        catch (Exception ex)
        {
            Plugin.Log.Debug($"Could not classify the lightshow: {ex.Message}");
            return false;
        }
    }

    // Custom characteristics registered by SongCore have no enum name; those match any set.
    private static string? GetCharacteristicName(BeatmapCharacteristic characteristic) => characteristic switch
    {
        BeatmapCharacteristic.Degree360 => "360Degree",
        BeatmapCharacteristic.Degree90 => "90Degree",
        _ when Enum.IsDefined(typeof(BeatmapCharacteristic), characteristic) => characteristic.ToString(),
        _ => null,
    };

    private static string? GetFolder(BeatmapLevel beatmapLevel)
    {
        if (beatmapLevel.previewMediaData is not FileSystemPreviewMediaData media)
        {
            return null;
        }

        string? path = media._previewAudioClipPath ?? media._coverSpritePath;
        return string.IsNullOrEmpty(path) ? null : Path.GetDirectoryName(path);
    }

    private static IEnumerable<(string Path, bool Lightshow)> GetDifficultyFiles(string folder, string? characteristic, string difficulty)
    {
        string? infoPath = Directory.EnumerateFiles(folder)
            .FirstOrDefault(file => string.Equals(Path.GetFileName(file), "Info.dat", StringComparison.OrdinalIgnoreCase));
        if (infoPath == null)
        {
            yield break;
        }

        JObject info = JObject.Parse(File.ReadAllText(infoPath));
        if (info["_difficultyBeatmapSets"] is JArray sets)
        {
            foreach (JToken set in sets)
            {
                if ((characteristic != null && !string.Equals((string?)set["_beatmapCharacteristicName"], characteristic, StringComparison.OrdinalIgnoreCase)) ||
                    set["_difficultyBeatmaps"] is not JArray difficulties)
                {
                    continue;
                }

                foreach (JToken beatmap in difficulties)
                {
                    if (string.Equals((string?)beatmap["_difficulty"], difficulty, StringComparison.OrdinalIgnoreCase) &&
                        (string?)beatmap["_beatmapFilename"] is { } file)
                    {
                        yield return (Path.Combine(folder, file), false);
                    }
                }
            }
        }
        else if (info["difficultyBeatmaps"] is JArray beatmaps)
        {
            foreach (JToken beatmap in beatmaps)
            {
                if ((characteristic != null && !string.Equals((string?)beatmap["characteristic"], characteristic, StringComparison.OrdinalIgnoreCase)) ||
                    !string.Equals((string?)beatmap["difficulty"], difficulty, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if ((string?)beatmap["beatmapDataFilename"] is { } beatmapFile)
                {
                    yield return (Path.Combine(folder, beatmapFile), false);
                }

                if ((string?)beatmap["lightshowDataFilename"] is { } lightshowFile)
                {
                    yield return (Path.Combine(folder, lightshowFile), true);
                }
            }
        }
    }

    private static bool IsQualityFile((string Path, bool Lightshow) file, BeatmapKey beatmapKey)
    {
        string path = file.Path;
        if (!File.Exists(path))
        {
            return false;
        }

        DateTime written = File.GetLastWriteTimeUtc(path);
        if (Cache.TryGetValue(path, out (DateTime Written, bool Quality) cached) && cached.Written == written)
        {
            return cached.Quality;
        }

        string? text = ReadDifficulty(path, file.Lightshow, beatmapKey);
        if (text == null)
        {
            return false;
        }

        bool quality = EnvironmentEdits.IsMatch(text) ||
                       HasAtLeast(LightIds, text, MinLightIds) ||
                       HasAtLeast(Gradients, text, MinGradients) ||
                       HasAtLeast(Colors, text, MinColors) ||
                       ((V3BoxGroups.IsMatch(text) || V4BoxGroups.IsMatch(text)) &&
                        HasAtLeast(BoxGroupEntries, text, MinBoxGroups));
        Cache[path] = (written, quality);
        Plugin.Log.Debug($"Lightshow in [{Path.GetFileName(path)}] {(quality ? "meets" : "does not meet")} the quality threshold.");
        return quality;
    }

    // Counting a whole MatchCollection scans the full file and allocates every match; the thresholds
    // only need to know whether enough matches exist.
    private static bool HasAtLeast(Regex regex, string text, int count)
    {
        int found = 0;
        for (Match match = regex.Match(text); match.Success; match = match.NextMatch())
        {
            if (++found >= count)
            {
                return true;
            }
        }

        return false;
    }

    // SongCore rejects direct reads of cached difficulty files; the game's level data reader is the
    // supported path to their contents.
    private static string? ReadDifficulty(string path, bool lightshow, BeatmapKey beatmapKey)
    {
        FileSystemBeatmapLevelData levelData = new(
            string.Empty,
            string.Empty,
            string.Empty,
            new Dictionary<(BeatmapCharacteristic, BeatmapDifficulty), FileDifficultyBeatmap>
            {
                [(beatmapKey.characteristic, beatmapKey.difficulty)] = new(lightshow ? string.Empty : path, lightshow ? path : string.Empty),
            });
        return lightshow ? levelData.GetLightshowString(in beatmapKey) : levelData.GetBeatmapString(in beatmapKey);
    }
}
#endif
