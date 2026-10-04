using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Chroma.Lighting;

internal static class LightEventPreparation
{
    internal static KeyValuePair<int, int>[]?[] PrepareLinks(
        (int Type, int[]? IDs, bool Valid, bool NeedsNext)[] rows)
    {
        return RunOwned(rows, ComputeLinks);
    }

    internal static (float[] Red, float[] Green, float[] Blue, int[] Matches) PrepareLegacyColors(
        (int Type, float Time, int Value)[] rows)
    {
        return RunOwned(rows, ComputeLegacyColors);
    }

    private static KeyValuePair<int, int>[]?[] ComputeLinks(
        (int Type, int[]? IDs, bool Valid, bool NeedsNext)[] rows)
    {
        KeyValuePair<int, int>[]?[] result = new KeyValuePair<int, int>[]?[rows.Length];
        Dictionary<int, Dictionary<int, int>> types = new();
        for (int i = rows.Length - 1; i >= 0; i--)
        {
            if (!rows[i].Valid)
            {
                continue;
            }

            if (!types.TryGetValue(rows[i].Type, out Dictionary<int, int>? next))
            {
                types[rows[i].Type] = next = new Dictionary<int, int>();
            }

            if (rows[i].NeedsNext)
            {
                result[i] = next.ToArray();
            }

            int[]? ids = rows[i].IDs;
            if (ids == null)
            {
                // Global events replace every known light ID, including -1.
                next[-1] = i;
                foreach (int key in next.Keys.ToArray())
                {
                    next[key] = i;
                }
            }
            else
            {
                foreach (int id in ids)
                {
                    next[id] = i;
                }
            }
        }

        return result;
    }

    private static (float[] Red, float[] Green, float[] Blue, int[] Matches) ComputeLegacyColors(
        (int Type, float Time, int Value)[] rows)
    {
        float[] red = new float[rows.Length];
        float[] green = new float[rows.Length];
        float[] blue = new float[rows.Length];
        int[] matches = new int[rows.Length];
        Dictionary<int, List<int>> colors = new();
        for (int i = 0; i < rows.Length; i++)
        {
            if (rows[i].Value < LegacyLightHelper.RGB_INT_OFFSET)
            {
                continue;
            }

            if (!colors.TryGetValue(rows[i].Type, out List<int>? definitions))
            {
                colors[rows[i].Type] = definitions = new List<int>();
            }

            definitions.Add(i);
            int rgb = rows[i].Value - LegacyLightHelper.RGB_INT_OFFSET;
            red[i] = ((rgb >> 16) & 0x0ff) / 255f;
            green[i] = ((rgb >> 8) & 0x0ff) / 255f;
            blue[i] = (rgb & 0x0ff) / 255f;
        }

        for (int i = 0; i < rows.Length; i++)
        {
            matches[i] = -1;
            if (!colors.TryGetValue(rows[i].Type, out List<int>? definitions))
            {
                continue;
            }

            for (int j = definitions.Count - 1; j >= 0; j--)
            {
                int ordinal = definitions[j];
                if (rows[ordinal].Time <= rows[i].Time)
                {
                    matches[i] = ordinal;
                    break;
                }
            }
        }

        return (red, green, blue, matches);
    }

    private static TResult RunOwned<TSnapshot, TResult>(TSnapshot snapshot, Func<TSnapshot, TResult> prepare)
    {
        if (Thread.CurrentThread.IsThreadPoolThread)
        {
            return prepare(snapshot);
        }

        Tuple<TSnapshot, Func<TSnapshot, TResult>> request = Tuple.Create(snapshot, prepare);
        Func<object?, TResult> execute = static state =>
        {
            Tuple<TSnapshot, Func<TSnapshot, TResult>> owned =
                (Tuple<TSnapshot, Func<TSnapshot, TResult>>)state!;
            return owned.Item2(owned.Item1);
        };

        Task<TResult> task;
        if (ExecutionContext.IsFlowSuppressed())
        {
            task = Task.Factory.StartNew(
                execute, request, CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);
        }
        else
        {
            using (ExecutionContext.SuppressFlow())
            {
                task = Task.Factory.StartNew(
                    execute, request, CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);
            }
        }

        return task.GetAwaiter().GetResult();
    }
}
