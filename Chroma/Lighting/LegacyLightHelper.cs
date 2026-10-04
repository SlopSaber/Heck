using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace Chroma.Lighting;

internal class LegacyLightHelper
{
    internal const int RGB_INT_OFFSET = 2000000000;

    private readonly Dictionary<BasicBeatmapEventType, List<(float Time, Color Color)>> _legacyColorEvents = new();
    private readonly Dictionary<BasicBeatmapEventData, (int Type, float Time, Color? Color)> _preparedColors =
        new(EventReferenceComparer.Instance);

    private bool _colorEventsExposed;

    internal LegacyLightHelper(IEnumerable<BasicBeatmapEventData> eventData)
    {
        List<BasicBeatmapEventData> events = new();
        List<(int Type, float Time, int Value)> rows = new();
        bool hasLegacyColors = false;
        foreach (BasicBeatmapEventData d in eventData)
        {
            int value = d.value;
            events.Add(d);
            rows.Add(((int)d.basicBeatmapEventType, d.time, value));
            hasLegacyColors |= value >= RGB_INT_OFFSET;
        }

        if (!hasLegacyColors)
        {
            return;
        }

        (float[] red, float[] green, float[] blue, int[] matches) =
            LightEventPreparation.PrepareLegacyColors(rows.ToArray());
        for (int i = 0; i < rows.Count; i++)
        {
            (int type, float time, int value) = rows[i];
            if (value < RGB_INT_OFFSET)
            {
                continue;
            }

            BasicBeatmapEventType eventType = (BasicBeatmapEventType)type;
            if (!_legacyColorEvents.TryGetValue(eventType, out List<(float, Color)>? dictionaryID))
            {
                dictionaryID = [];
                _legacyColorEvents.Add(eventType, dictionaryID);
            }

            dictionaryID.Add((time, new Color(red[i], green[i], blue[i])));
        }

        for (int i = 0; i < rows.Count; i++)
        {
            int match = matches[i];
            Color? color = match < 0 ? null : new Color(red[match], green[match], blue[match]);
            _preparedColors[events[i]] = (rows[i].Type, rows[i].Time, color);
        }
    }

    internal Dictionary<BasicBeatmapEventType, List<(float Time, Color Color)>> LegacyColorEvents
    {
        get
        {
            _colorEventsExposed = true;
            return _legacyColorEvents;
        }
    }

    internal Color? GetLegacyColor(BasicBeatmapEventData beatmapEventData)
    {
        BasicBeatmapEventType type = beatmapEventData.basicBeatmapEventType;
        if (!_colorEventsExposed &&
            _preparedColors.TryGetValue(beatmapEventData, out (int Type, float Time, Color? Color) prepared) &&
            prepared.Type == (int)type &&
            prepared.Time.Equals(beatmapEventData.time))
        {
            return prepared.Color;
        }

        if (!_legacyColorEvents.TryGetValue(
                type,
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

    private sealed class EventReferenceComparer : IEqualityComparer<BasicBeatmapEventData>
    {
        internal static EventReferenceComparer Instance { get; } = new();

        public bool Equals(BasicBeatmapEventData? x, BasicBeatmapEventData? y)
        {
            return ReferenceEquals(x, y);
        }

        public int GetHashCode(BasicBeatmapEventData obj)
        {
            return RuntimeHelpers.GetHashCode(obj);
        }
    }
}
