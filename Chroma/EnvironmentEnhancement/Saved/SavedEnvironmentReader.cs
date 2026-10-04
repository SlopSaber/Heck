using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace Chroma.EnvironmentEnhancement.Saved;

internal sealed class SavedEnvironmentReader : IDisposable
{
    private readonly string _directory;

    private readonly Version _version;

    private IEnumerator<string>? _files;

    private JsonReader? _jsonReader;

    private JsonSerializerSettings? _serializerSettings;

    private StreamReader? _streamReader;

    internal SavedEnvironmentReader(string directory, Version version)
    {
        _directory = directory;
        _version = version;
    }

    public void Dispose()
    {
        Run(static reader => reader.DisposeOwned());
    }

    internal Exception? CompleteFile()
    {
        return Run(static reader => reader.CompleteFileOwned());
    }

    internal (string File, string? FileName, SavedEnvironment? Environment, Exception? Error)? ReadNext()
    {
        return Run(static reader => reader.ReadNextOwned());
    }

    private Exception? CompleteFileOwned()
    {
        try
        {
            DisposeFileOwned();
            return null;
        }
        catch (Exception e)
        {
            return e;
        }
    }

    private void DisposeFileOwned()
    {
        try
        {
            ((IDisposable?)_jsonReader)?.Dispose();
        }
        finally
        {
            try
            {
                _streamReader?.Dispose();
            }
            finally
            {
                _jsonReader = null;
                _streamReader = null;
            }
        }
    }

    private int DisposeOwned()
    {
        try
        {
            DisposeFileOwned();
        }
        finally
        {
            _files?.Dispose();
            _files = null;
        }

        return 0;
    }

    private (string File, string? FileName, SavedEnvironment? Environment, Exception? Error)? ReadNextOwned()
    {
        if (_files == null)
        {
            if (!Directory.Exists(_directory))
            {
                Directory.CreateDirectory(_directory);
            }

            _serializerSettings = new JsonSerializerSettings
            {
                MissingMemberHandling = MissingMemberHandling.Error
            };
            _files = Directory.EnumerateFiles(_directory, "*.dat").GetEnumerator();
        }

        if (!_files.MoveNext())
        {
            return null;
        }

        string file = _files.Current;
        try
        {
            _streamReader = new StreamReader(file);
            _jsonReader = new JsonTextReader(_streamReader);
            JsonSerializer serializer = JsonSerializer.Create(_serializerSettings);
            SavedEnvironment environment = serializer.Deserialize<SavedEnvironment>(_jsonReader) ??
                                           throw new InvalidOperationException("Deserializing returned null.");
            if (environment.Version != _version)
            {
                throw new InvalidOperationException(
                    $"Unhandled version: [{environment.Version}], must be [{_version}].");
            }

            string fileName = Path.GetFileName(file);

            // Keep readers open until owner publication; disposal faults follow Trace/Add.
            return (file, fileName, environment, null);
        }
        catch (Exception e)
        {
            return (file, null, null, CompleteFileOwned() ?? e);
        }
    }

    private TResult Run<TResult>(Func<SavedEnvironmentReader, TResult> operation)
    {
        if (Thread.CurrentThread.IsThreadPoolThread)
        {
            return operation(this);
        }

        CultureInfo culture = CultureInfo.ReadOnly((CultureInfo)CultureInfo.CurrentCulture.Clone());
        CultureInfo uiCulture = CultureInfo.ReadOnly((CultureInfo)CultureInfo.CurrentUICulture.Clone());
        Tuple<SavedEnvironmentReader, Func<SavedEnvironmentReader, TResult>, CultureInfo, CultureInfo> request =
            Tuple.Create(this, operation, culture, uiCulture);
        Func<object, TResult> execute = static state =>
        {
            var work = (Tuple<SavedEnvironmentReader, Func<SavedEnvironmentReader, TResult>, CultureInfo, CultureInfo>)state;
            Thread thread = Thread.CurrentThread;
            CultureInfo previousCulture = thread.CurrentCulture;
            CultureInfo previousUiCulture = thread.CurrentUICulture;
            try
            {
                thread.CurrentCulture = work.Item3;
                thread.CurrentUICulture = work.Item4;
                return work.Item2(work.Item1);
            }
            finally
            {
                thread.CurrentCulture = previousCulture;
                thread.CurrentUICulture = previousUiCulture;
            }
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
