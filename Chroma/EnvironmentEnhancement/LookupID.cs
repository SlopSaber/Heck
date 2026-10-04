using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Chroma.EnvironmentEnhancement;

internal static class LookupID
{
    private const string LOOKUPDLL = "LookupID.dll";
    private const int MIN_WORKER_IDS = 128;

    private static bool _useFallback;
    private static bool _nativeReady;

    internal static List<GameObjectInfo> Get(
        List<GameObjectInfo> source,
        string[] gameObjectIds,
        string id,
        LookupMethod lookupMethod)
    {
        if (_useFallback)
        {
            return LookupID_Legacy(source, id, lookupMethod);
        }

        try
        {
            int[] arrayRes = GetNativeOrdinals(gameObjectIds, id, lookupMethod);
            List<GameObjectInfo> returnList = new(arrayRes.Length);
            returnList.AddRange(arrayRes.Select(index => source[index]));
            _nativeReady = true;
            return returnList;
        }
        catch (Exception e)
        {
            Plugin.Log.Error("Error running LookupID, falling back to managed code");
            Plugin.Log.Error("Expect long load times...");
            Plugin.Log.Error(e);

            _useFallback = true;
            return LookupID_Legacy(source, id, lookupMethod);
        }
    }

    // Preserve C string marshalling and native array writeback.
#pragma warning disable CA2101
    [DllImport(LOOKUPDLL, CallingConvention = CallingConvention.Cdecl)]
    private static extern void LookupID_internal(
        [In] [Out] string[] array,
        int size,
        out IntPtr returnArray,
        ref int returnSize,
        [MarshalAs(UnmanagedType.LPStr)] string id,
        LookupMethod method);
#pragma warning restore CA2101

    private static Func<string, bool> CreatePredicate(string id, LookupMethod lookupMethod)
    {
        switch (lookupMethod)
        {
            case LookupMethod.Regex:
                Regex regex = new(id, RegexOptions.CultureInvariant | RegexOptions.ECMAScript | RegexOptions.Compiled);
                return value => regex.IsMatch(value);

            case LookupMethod.Exact:
                return value => value == id;

            case LookupMethod.Contains:
                return value => value.Contains(id);

            case LookupMethod.StartsWith:
                return value => value.StartsWith(id);

            case LookupMethod.EndsWith:
                return value => value.EndsWith(id);

            default:
                throw new ArgumentOutOfRangeException(nameof(lookupMethod), "Invalid lookup method.");
        }
    }

    private static int[] GetNativeOrdinals(string[] ids, string id, LookupMethod lookupMethod)
    {
        if (!_nativeReady || ids.Length < MIN_WORKER_IDS || !TryGetCultures(out CultureInfo culture, out CultureInfo uiCulture))
        {
            return ReadNativeOrdinals(ids, id, lookupMethod);
        }

        LookupRequest request = new((string[])ids.Clone(), id, lookupMethod, true, culture, uiCulture);
        PreparedLookup result = PrepareOnWorker(request);
        Array.Copy(request.Ids, ids, ids.Length);
        result.Error?.Throw();
        return result.Ordinals!;
    }

    private static List<GameObjectInfo> LookupID_Legacy(
        IEnumerable<GameObjectInfo> source,
        string id,
        LookupMethod lookupMethod)
    {
        if (source is not List<GameObjectInfo> list || list.GetType() != typeof(List<GameObjectInfo>) ||
            list.Count < MIN_WORKER_IDS || !TryGetCultures(out CultureInfo culture, out CultureInfo uiCulture))
        {
            return LookupID_LegacyOnCaller(source, id, lookupMethod);
        }

        GameObjectInfo[] objects = list.ToArray();
        string[] ids = new string[objects.Length];
        for (int index = 0; index < objects.Length; index++)
        {
            ids[index] = objects[index].FullID;
        }

        PreparedLookup result = PrepareOnWorker(new LookupRequest(ids, id, lookupMethod, false, culture, uiCulture));
        result.Error?.Throw();
        List<GameObjectInfo> matches = new(result.Ordinals!.Length);
        foreach (int ordinal in result.Ordinals)
        {
            matches.Add(objects[ordinal]);
        }

        return matches;
    }

    private static List<GameObjectInfo> LookupID_LegacyOnCaller(
        IEnumerable<GameObjectInfo> source,
        string id,
        LookupMethod lookupMethod)
    {
        Func<string, bool> predicate = CreatePredicate(id, lookupMethod);
        return source.Where(value => predicate(value.FullID)).ToList();
    }

    private static PreparedLookup Prepare(LookupRequest request)
    {
        CultureInfo previousCulture = CultureInfo.CurrentCulture;
        CultureInfo previousUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = request.Culture;
            CultureInfo.CurrentUICulture = request.UiCulture;
            int[] ordinals = request.Native
                ? ReadNativeOrdinals(request.Ids, request.Id, request.Method)
                : PrepareManagedOrdinals(request.Ids, request.Id, request.Method);
            return new PreparedLookup(ordinals, null);
        }
        catch (Exception exception)
        {
            return new PreparedLookup(null, ExceptionDispatchInfo.Capture(exception));
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
            CultureInfo.CurrentUICulture = previousUiCulture;
        }
    }

    private static int[] PrepareManagedOrdinals(string[] ids, string id, LookupMethod lookupMethod)
    {
        Func<string, bool> predicate = CreatePredicate(id, lookupMethod);
        List<int> matches = new();
        for (int ordinal = 0; ordinal < ids.Length; ordinal++)
        {
            if (predicate(ids[ordinal]))
            {
                matches.Add(ordinal);
            }
        }

        return matches.ToArray();
    }

    private static PreparedLookup PrepareOnWorker(LookupRequest request)
    {
        Task<PreparedLookup> task;
        if (ExecutionContext.IsFlowSuppressed())
        {
            task = StartPreparation(request);
        }
        else
        {
            using (ExecutionContext.SuppressFlow())
            {
                task = StartPreparation(request);
            }
        }

        return task.GetAwaiter().GetResult();
    }

    private static int[] ReadNativeOrdinals(string[] ids, string id, LookupMethod lookupMethod)
    {
        int length = ids.Length;
        LookupID_internal(ids, length, out IntPtr buffer, ref length, id, lookupMethod);
        int[] ordinals = new int[length];
        Marshal.Copy(buffer, ordinals, 0, length);
        Marshal.FreeCoTaskMem(buffer);
        return ordinals;
    }

    private static Task<PreparedLookup> StartPreparation(LookupRequest request)
    {
        return Task.Factory.StartNew(
            static state => Prepare((LookupRequest)state!),
            request,
            CancellationToken.None,
            TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default);
    }

    private static bool TryGetCultures(out CultureInfo culture, out CultureInfo uiCulture)
    {
        CultureInfo currentCulture = CultureInfo.CurrentCulture;
        CultureInfo currentUiCulture = CultureInfo.CurrentUICulture;
        if (Thread.CurrentThread.IsThreadPoolThread || currentCulture.GetType() != typeof(CultureInfo) ||
            currentUiCulture.GetType() != typeof(CultureInfo))
        {
            culture = null!;
            uiCulture = null!;
            return false;
        }

        culture = CultureInfo.ReadOnly((CultureInfo)currentCulture.Clone());
        uiCulture = CultureInfo.ReadOnly((CultureInfo)currentUiCulture.Clone());
        return true;
    }

    private sealed class LookupRequest(
        string[] ids,
        string id,
        LookupMethod method,
        bool native,
        CultureInfo culture,
        CultureInfo uiCulture)
    {
        internal string[] Ids { get; } = ids;

        internal string Id { get; } = id;

        internal LookupMethod Method { get; } = method;

        internal bool Native { get; } = native;

        internal CultureInfo Culture { get; } = culture;

        internal CultureInfo UiCulture { get; } = uiCulture;
    }

    private sealed class PreparedLookup(int[]? ordinals, ExceptionDispatchInfo? error)
    {
        internal int[]? Ordinals { get; } = ordinals;

        internal ExceptionDispatchInfo? Error { get; } = error;
    }
}
