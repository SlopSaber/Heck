using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ModestTree;

namespace Heck.Animation;

public interface IPointDefinition
{
    public int Count { get; }

    public bool HasBaseProvider { get; }
}

public abstract class PointDefinition<T> : IPointDefinition
    where T : struct
{
    private const int MIN_WORKER_VALUES = 128;
    private const int MAX_CAPTURE_DEPTH = 64;

    private readonly List<IPointData> _points = [];

    [SuppressMessage("ReSharper", "VirtualMemberCallInConstructor", Justification = "No instance variables used.")]
    protected PointDefinition(IReadOnlyCollection<object> list)
    {
        IEnumerable<List<object>> points = list.FirstOrDefault() is List<object>
            ? list.Cast<List<object>>()
            : new[] { list.Append(0).ToList() };
        PreparedPoints? prepared = TryPreparePoints(list, points);
        foreach (List<object> rawPoint in points)
        {
            Functions easing = Functions.easeLinear;
            Modifier<T>[]? modifiers = null;
            string[]? flags = null;
            IValues[]? values = null;
            foreach (PreparedGroup grouping in GetGroups(rawPoint, prepared))
            {
                object[] groupList = grouping.Items;
                switch (grouping.Key)
                {
                    case GroupType.Value:
                        values = groupList.DeserializeValues();
                        break;

                    case GroupType.Flag:
                        flags = groupList.Cast<string>().ToArray();
                        string? easingString = flags.FirstOrDefault(n => n.StartsWith("ease"));
                        if (easingString != null)
                        {
                            easing = (Functions)Enum.Parse(typeof(Functions), easingString);
                        }

                        break;

                    case GroupType.Modifier:
                        modifiers = groupList.Cast<List<object>>().Select(n => DeserializeModifier(n, prepared)).ToArray();
                        break;
                }
            }

            if (values == null)
            {
                throw new InvalidOperationException("No points found.");
            }

            _points.Add(CreatePointData(values, flags ?? [], modifiers ?? [], easing));
        }

        HasBaseProvider = _points.Any(n => n.HasBaseProvider);
    }

    private enum GroupType
    {
        Value,
        Flag,
        Modifier
    }

    protected interface IPointData
    {
        public Functions Easing { get; }

        public bool HasBaseProvider { get; }

        public T Point { get; }

        public float Time { get; }
    }

    public int Count => _points.Count;

    public bool HasBaseProvider { get; }

    public T Interpolate(float time)
    {
        return Interpolate(time, out _);
    }

    public T Interpolate(float time, out bool last)
    {
        last = false;
        if (Count == 0)
        {
            return default;
        }

        IPointData lastPoint = _points[Count - 1];
        if (lastPoint.Time <= time)
        {
            last = true;
            return lastPoint.Point;
        }

        IPointData firstPoint = _points[0];
        if (firstPoint.Time >= time)
        {
            return firstPoint.Point;
        }

        SearchIndex(time, out int l, out int r);
        IPointData pointL = _points[l];
        IPointData pointR = _points[r];

        float normalTime;
        float divisor = pointR.Time - pointL.Time;
        if (divisor != 0)
        {
            normalTime = (time - pointL.Time) / divisor;
        }
        else
        {
            normalTime = 0;
        }

        normalTime = Easings.Interpolate(normalTime, pointR.Easing);

        return InterpolatePoints(_points, l, r, normalTime);
    }

    public override string ToString()
    {
        return "{" +
               string.Join(
                   ", ",
                   _points.Select(
                       n =>
                       {
                           string result = n.ToString();
                           string added = $", {n.Time}" +
                                          (n.Easing != Functions.easeLinear ? ", " + n.Easing : string.Empty);
                           return result.Insert(result.Length - 1, added);
                       })) +
               "}";
    }

    protected abstract T InterpolatePoints(List<IPointData> points, int l, int r, float time);

    private protected abstract Modifier<T> CreateModifier(
        IValues[] values,
        Modifier<T>[] modifiers,
        Operation operation);

    private protected abstract IPointData CreatePointData(
        IValues[] values,
        string[] flags,
        Modifier<T>[] modifiers,
        Functions easing);

    private static IEnumerable<IGrouping<GroupType, object>> Group(IEnumerable<object> list)
    {
        return list.GroupBy(
            n =>
            {
                return n switch
                {
                    string s when !s.StartsWith("base") => GroupType.Flag,
                    List<object> => GroupType.Modifier,
                    _ => GroupType.Value
                };
            });
    }

    private static PreparedPoints? TryPreparePoints(IReadOnlyCollection<object> list, IEnumerable<List<object>> points)
    {
        if (Thread.CurrentThread.IsThreadPoolThread || list.GetType() != typeof(List<object>) ||
            CultureInfo.CurrentCulture.GetType() != typeof(CultureInfo) ||
            CultureInfo.CurrentUICulture.GetType() != typeof(CultureInfo))
        {
            return null;
        }

        List<object> root = (List<object>)list;
        if ((root.Count < MIN_WORKER_VALUES - 1 && root.All(n => n is not List<object>)) ||
            (root.Count == 1 && root[0] is List<object> single && single.GetType() == typeof(List<object>) &&
             single.Count < MIN_WORKER_VALUES && single.All(n => n is not List<object>)))
        {
            return null;
        }

        Dictionary<List<object>, CapturedList> captured = new();
        HashSet<List<object>> ancestors = [];
        List<List<object>> owned = [];
        int weight = 0;
        try
        {
            foreach (List<object> point in points)
            {
                if (!TryCaptureList(point, 0, captured, ancestors, out CapturedList copy))
                {
                    return null;
                }

                owned.Add(copy.Owned);
                weight = Math.Min(MIN_WORKER_VALUES, weight + copy.Weight);
            }
        }
        catch (InvalidCastException)
        {
            // Preserve malformed point enumeration and earlier caller effects.
            return null;
        }

        if (weight < MIN_WORKER_VALUES)
        {
            return null;
        }

        CultureInfo sourceCulture = CultureInfo.CurrentCulture;
        Tuple<List<object>[], CultureInfo, CultureInfo> state = Tuple.Create(
            owned.ToArray(),
            CultureInfo.ReadOnly((CultureInfo)sourceCulture.Clone()),
            CultureInfo.ReadOnly((CultureInfo)CultureInfo.CurrentUICulture.Clone()));
        Task<Dictionary<List<object>, PreparedGroup[]>> task;
        if (ExecutionContext.IsFlowSuppressed())
        {
            task = StartPreparation(state);
        }
        else
        {
            using (ExecutionContext.SuppressFlow())
            {
                task = StartPreparation(state);
            }
        }

        Dictionary<List<object>, PreparedGroup[]> groups = task.GetAwaiter().GetResult();
        Dictionary<List<object>, List<object>> originals = captured.ToDictionary(n => n.Value.Owned, n => n.Key);
        return new PreparedPoints(sourceCulture, captured, originals, groups);
    }

    private static bool TryCaptureList(
        List<object> source,
        int depth,
        Dictionary<List<object>, CapturedList> captured,
        HashSet<List<object>> ancestors,
        out CapturedList result)
    {
        result = null!;
        if (captured.TryGetValue(source, out CapturedList previous))
        {
            if (depth + previous.Height >= MAX_CAPTURE_DEPTH)
            {
                return false;
            }

            result = previous;
            return true;
        }

        if (source.GetType() != typeof(List<object>) || depth >= MAX_CAPTURE_DEPTH || !ancestors.Add(source))
        {
            return false;
        }

        try
        {
            object[] originalItems = source.ToArray();
            List<object> owned = new(originalItems.Length);
            int weight = Math.Min(MIN_WORKER_VALUES, originalItems.Length);
            int height = 0;
            foreach (object value in originalItems)
            {
                switch (value)
                {
                    case List<object> nested:
                        if (!TryCaptureList(nested, depth + 1, captured, ancestors, out CapturedList child))
                        {
                            return false;
                        }

                        owned.Add(child.Owned);
                        height = Math.Max(height, child.Height + 1);
                        weight = Math.Min(MIN_WORKER_VALUES, weight + child.Weight);
                        break;

                    case null:
                    case string:
                    case double:
                    case float:
                    case decimal:
                    case byte:
                    case sbyte:
                    case short:
                    case ushort:
                    case int:
                    case uint:
                    case long:
                    case ulong:
                    case bool:
                    case char:
                        owned.Add(value!);
                        break;

                    default:
                        return false;
                }
            }

            result = new CapturedList(owned, originalItems, weight, height);
            captured.Add(source, result);
            return true;
        }
        finally
        {
            ancestors.Remove(source);
        }
    }

    private static Task<Dictionary<List<object>, PreparedGroup[]>> StartPreparation(
        Tuple<List<object>[], CultureInfo, CultureInfo> state)
    {
        return Task.Factory.StartNew(
            PrepareWithCulture,
            state,
            CancellationToken.None,
            TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default);
    }

    private static Dictionary<List<object>, PreparedGroup[]> PrepareWithCulture(object? state)
    {
        Tuple<List<object>[], CultureInfo, CultureInfo> owned = (Tuple<List<object>[], CultureInfo, CultureInfo>)state!;
        CultureInfo previousCulture = CultureInfo.CurrentCulture;
        CultureInfo previousUICulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = owned.Item2;
            CultureInfo.CurrentUICulture = owned.Item3;
            Dictionary<List<object>, PreparedGroup[]> groups = new();
            foreach (List<object> point in owned.Item1)
            {
                PrepareGroups(point, groups);
            }

            return groups;
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
            CultureInfo.CurrentUICulture = previousUICulture;
        }
    }

    private static void PrepareGroups(List<object> point, Dictionary<List<object>, PreparedGroup[]> groups)
    {
        if (groups.ContainsKey(point))
        {
            return;
        }

        PreparedGroup[] prepared = Group(point).Select(n => new PreparedGroup(n.Key, n.ToArray())).ToArray();
        groups.Add(point, prepared);
        foreach (PreparedGroup group in prepared)
        {
            if (group.Key != GroupType.Modifier)
            {
                continue;
            }

            foreach (List<object> child in group.Items)
            {
                PrepareGroups(child, groups);
            }
        }
    }

    private static IEnumerable<PreparedGroup> GetGroups(List<object> source, PreparedPoints? prepared)
    {
        if (prepared != null && ReferenceEquals(CultureInfo.CurrentCulture, prepared.Culture) &&
            prepared.Captured.TryGetValue(source, out CapturedList captured) &&
            MatchesSource(source, captured.OriginalItems))
        {
            return prepared.Groups[captured.Owned].Select(n => n.Key == GroupType.Modifier
                ? new PreparedGroup(n.Key, n.Items.Select(child => (object)prepared.Originals[(List<object>)child]).ToArray())
                : n);
        }

        return Group(source).Select(n => new PreparedGroup(n.Key, n.ToArray()));
    }

    private static bool MatchesSource(List<object> source, object[] originalItems)
    {
        if (source.Count != originalItems.Length)
        {
            return false;
        }

        for (int i = 0; i < originalItems.Length; i++)
        {
            if (!ReferenceEquals(source[i], originalItems[i]))
            {
                return false;
            }
        }

        return true;
    }

    private Modifier<T> DeserializeModifier(List<object> list, PreparedPoints? prepared)
    {
        Modifier<T>[]? modifiers = null;
        Operation? operation = null;
        IValues[]? values = null;
        foreach (PreparedGroup grouping in GetGroups(list, prepared))
        {
            object[] groupList = grouping.Items;
            switch (grouping.Key)
            {
                case GroupType.Value:
                    values = groupList.DeserializeValues();
                    break;

                case GroupType.Flag:
                    Assert.IsEqual(1, groupList.Length, "Modifier must have one operation");
                    operation = (Operation)Enum.Parse(typeof(Operation), (string)groupList.First());
                    break;

                case GroupType.Modifier:
                    modifiers = groupList.Cast<List<object>>().Select(n => DeserializeModifier(n, prepared)).ToArray();
                    break;
            }
        }

        if (values == null)
        {
            throw new InvalidOperationException("No points found.");
        }

        if (operation == null)
        {
            throw new InvalidOperationException("No operation found.");
        }

        return CreateModifier(values, modifiers ?? [], operation.Value);
    }

    // Use binary search instead of linear search.
    private void SearchIndex(float time, out int l, out int r)
    {
        l = 0;
        r = Count;

        while (l < r - 1)
        {
            int m = (l + r) / 2;
            float pointTime = _points[m].Time;

            if (pointTime < time)
            {
                l = m;
            }
            else
            {
                r = m;
            }
        }
    }

    private readonly struct PreparedGroup(GroupType key, object[] items)
    {
        internal GroupType Key { get; } = key;

        internal object[] Items { get; } = items;
    }

    private sealed class CapturedList(List<object> owned, object[] originalItems, int weight, int height)
    {
        internal List<object> Owned { get; } = owned;

        internal object[] OriginalItems { get; } = originalItems;

        internal int Weight { get; } = weight;

        internal int Height { get; } = height;
    }

    private sealed class PreparedPoints(
        CultureInfo culture,
        Dictionary<List<object>, CapturedList> captured,
        Dictionary<List<object>, List<object>> originals,
        Dictionary<List<object>, PreparedGroup[]> groups)
    {
        internal CultureInfo Culture { get; } = culture;

        internal Dictionary<List<object>, CapturedList> Captured { get; } = captured;

        internal Dictionary<List<object>, List<object>> Originals { get; } = originals;

        internal Dictionary<List<object>, PreparedGroup[]> Groups { get; } = groups;
    }
}
