using System.Collections.Generic;
using UnityEngine;

namespace Chroma.Lighting;

// Please let me delete this whole class
internal class LegacyLightHelper
{
    internal const int RGB_INT_OFFSET = 2000000000;

    internal LegacyLightHelper(IEnumerable<BasicBeatmapEventData> eventData)
    {
        foreach (BasicBeatmapEventData d in eventData)
        {
            if (d.value < RGB_INT_OFFSET)
            {
                continue;
            }

            if (!LegacyColorEvents.TryGetValue(d.basicBeatmapEventType, out List<(float, Color)> dictionaryID))
            {
                dictionaryID = [];
                LegacyColorEvents.Add(d.basicBeatmapEventType, dictionaryID);
            }

            dictionaryID.Add((d.time, ColorFromInt(d.value)));
        }
    }

    internal Dictionary<BasicBeatmapEventType, List<(float Time, Color Color)>> LegacyColorEvents { get; } = new();

    internal Color? GetLegacyColor(BasicBeatmapEventData beatmapEventData)
    {
        if (!LegacyColorEvents.TryGetValue(
                beatmapEventData.basicBeatmapEventType,
                out List<(float, Color)> dictionaryID))
        {
            return null;
        }

        for (int i = dictionaryID.Count - 1; i >= 0; i--)
        {
            if (dictionaryID[i].Item1 <= beatmapEventData.time)
            {
                return dictionaryID[i].Item2;
            }
        }

        return null;
    }

    private static Color ColorFromInt(int rgb)
    {
        rgb -= RGB_INT_OFFSET;
        int red = (rgb >> 16) & 0x0ff;
        int green = (rgb >> 8) & 0x0ff;
        int blue = rgb & 0x0ff;
        return new Color(red / 255f, green / 255f, blue / 255f);
    }
}
