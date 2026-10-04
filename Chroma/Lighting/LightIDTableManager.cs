using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Chroma.EnvironmentEnhancement;
using Chroma.Settings;
using IPA.Utilities;
using Newtonsoft.Json;
using SiraUtil.Logging;
using Zenject;

namespace Chroma.Lighting;

internal class LightIDTableManager
{
    private static readonly Dictionary<int, Dictionary<int, int>> _defaultTable = new()
    {
        { 0, new Dictionary<int, int>() },
        { 1, new Dictionary<int, int>() },
        { 2, new Dictionary<int, int>() },
        { 3, new Dictionary<int, int>() },
        { 4, new Dictionary<int, int>() },
        { 5, new Dictionary<int, int>() },
        { 6, new Dictionary<int, int>() },
        { 7, new Dictionary<int, int>() },
        { 8, new Dictionary<int, int>() },
        { 9, new Dictionary<int, int>() }
    };

    private static readonly Lazy<Task<TableInitialization>> _tableInitialization = new(StartTableInitialization);

    private static int _initializationErrorLogged;

    private readonly HashSet<(int, int)> _failureLog = [];

    private readonly SiraLog _log;
    private readonly Config _config;
    private readonly EnvironmentOverrideChecker? _environmentOverrideChecker;
    private readonly Dictionary<int, Dictionary<int, int>> _activeTable;

    private LightIDTableManager(
        SiraLog log,
        Config config,
        EnvironmentSceneSetupData environmentSceneSetupData,
        [InjectOptional] EnvironmentOverrideChecker? environmentOverrideChecker)
    {
        _log = log;
        _config = config;
        _environmentOverrideChecker = environmentOverrideChecker;
        string environmentName = environmentSceneSetupData.environmentSerializedName;
        Dictionary<int, Dictionary<int, int>> loadedTable;
        TableInitialization initialization = _tableInitialization.Value.GetAwaiter().GetResult();
        if (initialization.Error != null && Interlocked.CompareExchange(ref _initializationErrorLogged, 1, 0) == 0)
        {
            _log.Error(initialization.Error);
        }

        if (initialization.Tables.TryGetValue(environmentName, out Dictionary<int, Dictionary<int, int>> selectedTable))
        {
            loadedTable = selectedTable;
        }
        else
        {
            loadedTable = _defaultTable;
            Plugin.Log.Warn($"Table not found for [{environmentName}]");
        }

        _activeTable = loadedTable.ToDictionary(n => n.Key, n => n.Value.ToDictionary(m => m.Key, m => m.Value));
    }

    internal static void InitTable()
    {
        _ = _tableInitialization.Value;
    }

    internal int? GetActiveTableValue(int lightID, int id)
    {
        if (_activeTable.TryGetValue(lightID, out Dictionary<int, int> dictioanry) &&
            dictioanry.TryGetValue(id, out int newId))
        {
            return newId;
        }

        // suppress error logs when no environment override is loaded
        if (_environmentOverrideChecker == null ||
            _environmentOverrideChecker.LoadedEnvironment == LoadedEnvironmentType.None)
        {
            return null;
        }

        (int, int) failure = new(lightID, id);
        if (_failureLog.Contains(failure))
        {
            return null;
        }

        _log.Error($"Unable to find value for light ID [{lightID}] and id [{id}], omitting future errors of same id...");
        _failureLog.Add(failure);

        return null;
    }

    internal int? GetActiveTableValueReverse(int lightID, int id)
    {
        if (!_activeTable.TryGetValue(lightID, out Dictionary<int, int> dictioanry))
        {
            return null;
        }

        foreach ((int key, int value) in dictioanry)
        {
            if (value == id)
            {
                return key;
            }
        }

        ////Plugin.Logger.Log($"Unable to find value for type [{type}] and id [{id}].", IPA.Logging.Logger.Level.Error);
        return null;
    }

    internal void RegisterIndex(int lightID, int index, int? requestedKey)
    {
        if (_activeTable.TryGetValue(lightID, out Dictionary<int, int> dictioanry))
        {
            int key;

            if (requestedKey.HasValue)
            {
                key = requestedKey.Value;
                while (dictioanry.ContainsKey(key))
                {
                    key++;
                }
            }
            else
            {
                if (dictioanry.Count != 0)
                {
                    key = dictioanry.Keys.Max() + 1;
                }
                else
                {
                    key = 0;
                }
            }

            dictioanry.Add(key, index);
            if (_config.PrintEnvironmentEnhancementDebug)
            {
                _log.Debug($"Registered key [{key}] to light ID [{lightID}]");
            }
        }
        else
        {
            _log.Warn($"Table does not contain light ID [{lightID}]");
        }
    }

    internal void UnregisterIndex(int lightID, int index)
    {
        if (_activeTable.TryGetValue(lightID, out Dictionary<int, int> dictioanry))
        {
            foreach ((int key, int value) in dictioanry)
            {
                if (value != index)
                {
                    continue;
                }

                dictioanry.Remove(key);
                if (_config.PrintEnvironmentEnhancementDebug)
                {
                    _log.Debug($"Unregistered key [{key}] from light ID [{lightID}]");
                }

                return;
            }

            _log.Warn($"Could not find key to unregister for light ID [{lightID}] and index [{index}]");
        }
        else
        {
            _log.Warn($"Table does not contain light ID [{lightID}]");
        }
    }

    private static Task<TableInitialization> StartTableInitialization()
    {
        Tuple<CultureInfo, CultureInfo> cultures = Tuple.Create(
            CultureInfo.ReadOnly((CultureInfo)CultureInfo.CurrentCulture.Clone()),
            CultureInfo.ReadOnly((CultureInfo)CultureInfo.CurrentUICulture.Clone()));
        if (ExecutionContext.IsFlowSuppressed())
        {
            return StartTableInitialization(cultures);
        }

        using (ExecutionContext.SuppressFlow())
        {
            return StartTableInitialization(cultures);
        }
    }

    private static Task<TableInitialization> StartTableInitialization(Tuple<CultureInfo, CultureInfo> cultures)
    {
        return Task.Factory.StartNew(
            ReadTablesWithCulture,
            cultures,
            CancellationToken.None,
            TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default);
    }

    private static TableInitialization ReadTablesWithCulture(object? state)
    {
        Tuple<CultureInfo, CultureInfo> cultures = (Tuple<CultureInfo, CultureInfo>)state!;
        CultureInfo previousCulture = CultureInfo.CurrentCulture;
        CultureInfo previousUICulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = cultures.Item1;
            CultureInfo.CurrentUICulture = cultures.Item2;
            return ReadTables();
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
            CultureInfo.CurrentUICulture = previousUICulture;
        }
    }

    private static TableInitialization ReadTables()
    {
        const string tableNamespace = "Chroma.LightIDTables.";
        Dictionary<string, Dictionary<int, Dictionary<int, int>>> tables = new();
        try
        {
            Assembly assembly = typeof(LightIDTableManager).Assembly;
            IEnumerable<string> tableNames = assembly.GetManifestResourceNames().Where(n => n.StartsWith(tableNamespace));
            foreach (string tableName in tableNames)
            {
                using StreamReader stream = new(
                    assembly.GetManifestResourceStream(tableName) ??
                    throw new InvalidOperationException($"Failed to retrieve {tableName}"));
                using JsonReader reader = new JsonTextReader(stream);
                Dictionary<int, Dictionary<int, int>> typeTable = new();
                JsonSerializer serializer = new();
                Dictionary<string, Dictionary<string, int>> rawDict =
                    serializer.Deserialize<Dictionary<string, Dictionary<string, int>>>(reader) ??
                    throw new InvalidOperationException($"Failed to deserialize ID table [{tableName}]");
                foreach ((string key, Dictionary<string, int> value) in rawDict)
                {
                    typeTable[int.Parse(key)] = value.ToDictionary(n => int.Parse(n.Key), n => n.Value);
                }

                string tableNameWithoutExtension = Path.GetFileNameWithoutExtension(tableName.Remove(
                    tableName.IndexOf(tableNamespace, StringComparison.Ordinal),
                    tableNamespace.Length));
                tables.Add(tableNameWithoutExtension, typeTable);
            }

            return new TableInitialization(tables, null);
        }
        catch (Exception error)
        {
            // Keep tables read before failure available to the original fallback path.
            return new TableInitialization(tables, error);
        }
    }

    private sealed class TableInitialization(
        Dictionary<string, Dictionary<int, Dictionary<int, int>>> tables,
        Exception? error)
    {
        internal Dictionary<string, Dictionary<int, Dictionary<int, int>>> Tables { get; } = tables;

        internal Exception? Error { get; } = error;
    }
}
