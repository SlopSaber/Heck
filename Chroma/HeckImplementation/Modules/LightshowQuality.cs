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

            return GetDifficultyFiles(folder, GetCharacteristicName(beatmapKey.characteristic), beatmapKey.difficulty.ToString())
                .Any(IsQualityFile);
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

    private static IEnumerable<string> GetDifficultyFiles(string folder, string? characteristic, string difficulty)
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
                        yield return Path.Combine(folder, file);
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
                    yield return Path.Combine(folder, beatmapFile);
                }

                if ((string?)beatmap["lightshowDataFilename"] is { } lightshowFile)
                {
                    yield return Path.Combine(folder, lightshowFile);
                }
            }
        }
    }

    private static bool IsQualityFile(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        DateTime written = File.GetLastWriteTimeUtc(path);
        if (Cache.TryGetValue(path, out (DateTime Written, bool Quality) cached) && cached.Written == written)
        {
            return cached.Quality;
        }

        string text = File.ReadAllText(path);
        bool hasBoxGroups = V3BoxGroups.IsMatch(text) || V4BoxGroups.IsMatch(text);
        bool quality = EnvironmentEdits.IsMatch(text) ||
                       LightIds.Matches(text).Count >= MinLightIds ||
                       Gradients.Matches(text).Count >= MinGradients ||
                       Colors.Matches(text).Count >= MinColors ||
                       (hasBoxGroups && BoxGroupEntries.Matches(text).Count >= MinBoxGroups);
        Cache[path] = (written, quality);
        Plugin.Log.Debug($"Lightshow in [{Path.GetFileName(path)}] {(quality ? "meets" : "does not meet")} the quality threshold.");
        return quality;
    }
}
#endif
