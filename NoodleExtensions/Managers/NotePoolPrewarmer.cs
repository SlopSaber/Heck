using System;
using System.Collections.Generic;
using CustomJSONData.CustomBeatmap;
using Heck.Deserialize;
using JetBrains.Annotations;
using Zenject;

namespace NoodleExtensions.Managers;

[UsedImplicitly]
internal class NotePoolPrewarmer : IInitializable
{
    private const int MAX_PREWARM = 256;

    private readonly BeatmapCallbacksController.InitData _initData;
    private readonly DeserializedData _deserializedData;
    private readonly SpawnDataManager _spawnDataManager;
    private readonly GameNoteController.Pool? _notes;
    private readonly GameNoteController.Pool? _heads;
    private readonly BombNoteController.Pool? _bombs;

    private NotePoolPrewarmer(
        BeatmapCallbacksController.InitData initData,
        [Inject(Id = NoodleController.ID)] DeserializedData deserializedData,
        SpawnDataManager spawnDataManager,
        [InjectOptional(Id = NoteData.GameplayType.Normal)] GameNoteController.Pool? notes,
        [InjectOptional(Id = NoteData.GameplayType.BurstSliderHead)] GameNoteController.Pool? heads,
        [InjectOptional] BombNoteController.Pool? bombs)
    {
        _initData = initData;
        _deserializedData = deserializedData;
        _spawnDataManager = spawnDataManager;
        _notes = notes;
        _heads = heads;
        _bombs = bombs;
    }

    public void Initialize()
    {
        List<(float Time, int Delta)> notes = [];
        List<(float Time, int Delta)> heads = [];
        List<(float Time, int Delta)> bombs = [];
        foreach (BeatmapObjectData item in ((CustomBeatmapData)_initData.beatmapData).beatmapObjectDatas)
        {
            if (item is not NoteData note || note.time < _initData.startFilterTime ||
                !_deserializedData.Resolve(note, out NoodleObjectData? data))
            {
                continue;
            }

            List<(float Time, int Delta)>? intervals = note.gameplayType switch
            {
                NoteData.GameplayType.Normal when _notes != null => notes,
                NoteData.GameplayType.BurstSliderHead when _heads != null => heads,
                NoteData.GameplayType.Bomb when _bombs != null => bombs,
                _ => null
            };
            if (intervals == null)
            {
                continue;
            }

            float aheadTime = _spawnDataManager.GetSpawnAheadTime(data.Njs, data.SpawnOffset);
            if (aheadTime < 0 || float.IsNaN(aheadTime) || float.IsInfinity(aheadTime) ||
                float.IsNaN(note.time) || float.IsInfinity(note.time))
            {
                continue;
            }

            // Retain uncut notes beyond the beat; the extra floor-movement time is conservative.
            intervals.Add((note.time - aheadTime, 1));
            intervals.Add((note.time + aheadTime, -1));
        }

        Prewarm(_notes, notes);
        Prewarm(_heads, heads);
        Prewarm(_bombs, bombs);
    }

    private static void Prewarm<T>(MemoryPoolBase<T>? pool, List<(float Time, int Delta)> intervals)
    {
        if (pool == null || intervals.Count == 0)
        {
            return;
        }

        intervals.Sort(static (left, right) =>
        {
            int time = left.Time.CompareTo(right.Time);
            return time != 0 ? time : right.Delta.CompareTo(left.Delta);
        });
        int active = 0;
        int peak = 0;
        foreach ((float _, int delta) in intervals)
        {
            active += delta;
            peak = Math.Max(peak, active);
        }

        // Bound loading-time allocation; larger or dynamically held notes keep normal pool growth.
        int target = Math.Min(peak, MAX_PREWARM);
        if (target > pool.NumTotal)
        {
            int previous = pool.NumTotal;
            pool.ExpandBy(target - pool.NumTotal);
            Plugin.Log.Debug($"Prewarmed [{typeof(T).Name}] pool from [{previous}] to [{pool.NumTotal}].");
        }
    }
}
