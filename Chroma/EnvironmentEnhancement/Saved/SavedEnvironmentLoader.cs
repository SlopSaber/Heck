using System;
using System.Collections.Generic;
using System.IO;
using Chroma.Settings;
using IPA.Utilities;
using JetBrains.Annotations;
using SiraUtil.Logging;

namespace Chroma.EnvironmentEnhancement.Saved;

internal class SavedEnvironmentLoader
{
    private static readonly Version _currVer = new(1, 0, 0);

    private static readonly string _directory = Path.Combine(
        UnityGame.UserDataPath,
        ChromaController.ID,
        "Environments");

    private readonly Config _config;

    private readonly SiraLog _log;

    [UsedImplicitly]
    private SavedEnvironmentLoader(SiraLog log, Config config)
    {
        _log = log;
        _config = config;
        Init();
    }

    public SavedEnvironment? SavedEnvironment
    {
        get
        {
            string? name = _config.CustomEnvironment;
            if (name == null)
            {
                return null;
            }

            Environments.TryGetValue(name, out SavedEnvironment? result);
            return result;
        }
    }

    public Dictionary<string?, SavedEnvironment?> Environments { get; private set; } = new();

    internal void Init()
    {
        Environments = new Dictionary<string?, SavedEnvironment?>();

        using SavedEnvironmentReader reader = new(_directory, _currVer);
        while (reader.ReadNext() is { } file)
        {
            Exception? error = file.Error;
            if (file.Environment != null)
            {
                try
                {
                    _log.Trace($"Loaded [{file.File}]");
                    Environments.Add(file.FileName, file.Environment);
                }
                catch (Exception e)
                {
                    error = e;
                }
                finally
                {
                    error = reader.CompleteFile() ?? error;
                }
            }

            if (error != null)
            {
                _log.Error($"Encountered error deserializing [{file.File}]");
                _log.Error(error);
            }
        }
    }
}
