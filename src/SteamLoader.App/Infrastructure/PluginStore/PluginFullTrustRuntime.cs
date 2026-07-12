using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using SteamLoader.App.Infrastructure.Steam;

namespace SteamLoader.App.Infrastructure.PluginStore;

public sealed class PluginFullTrustRuntime : IAsyncDisposable
{
    private const int MaxCapturedCharacters = 512 * 1024;
    private const int MaxRunTimeoutMs = 10 * 60 * 1000;

    private readonly PluginStoreService _pluginStoreService;
    private readonly SteamDevToolsClient _devToolsClient;
    private readonly ConcurrentDictionary<string, ManagedPluginProcess> _processes =
        new(StringComparer.OrdinalIgnoreCase);

    public PluginFullTrustRuntime(
        PluginStoreService pluginStoreService,
        SteamDevToolsClient devToolsClient)
    {
        _pluginStoreService = pluginStoreService;
        _devToolsClient = devToolsClient;
    }

    public Task<object> ExecuteSystemAsync(
        string pluginId,
        string operation,
        JsonElement? arguments,
        CancellationToken cancellationToken)
    {
        _pluginStoreService.EnsurePluginSdkPermission(pluginId, "native.full-trust");
        operation = operation.Trim().ToLowerInvariant();
        return operation switch
        {
            "getinfo" => Task.FromResult<object>(GetSystemInfo(pluginId)),
            "run" => RunAsync(pluginId, arguments, cancellationToken),
            "start" => StartAsync(pluginId, arguments, backend: false, cancellationToken),
            "startbackend" => StartAsync(pluginId, arguments, backend: true, cancellationToken),
            "list" => Task.FromResult<object>(List(pluginId)),
            "status" => Task.FromResult<object>(GetStatus(pluginId, GetRequiredString(arguments, "processId"))),
            "stop" => Task.FromResult<object>(Stop(pluginId, GetRequiredString(arguments, "processId"))),
            "stopall" => Task.FromResult<object>(StopAll(pluginId)),
            "call" => CallAsync(pluginId, arguments, cancellationToken),
            "open" => Task.FromResult(Open(pluginId, arguments)),
            _ => throw new InvalidOperationException($"Unknown full-trust system operation ({operation}).")
        };
    }

    public Task<object> ExecuteFileSystemAsync(
        string pluginId,
        string operation,
        JsonElement? arguments,
        CancellationToken cancellationToken)
    {
        _pluginStoreService.EnsurePluginSdkPermission(pluginId, "native.full-trust");
        cancellationToken.ThrowIfCancellationRequested();
        operation = operation.Trim().ToLowerInvariant();
        return Task.FromResult(operation switch
        {
            "paths" => GetPaths(pluginId),
            "stat" => Stat(pluginId, arguments),
            "list" => ListFiles(pluginId, arguments),
            "readtext" => ReadText(pluginId, arguments),
            "readbytes" => ReadBytes(pluginId, arguments),
            "writetext" => WriteText(pluginId, arguments),
            "writebytes" => WriteBytes(pluginId, arguments),
            "mkdir" => CreateDirectory(pluginId, arguments),
            "delete" => Delete(pluginId, arguments),
            "copy" => Copy(pluginId, arguments),
            "move" => Move(pluginId, arguments),
            _ => throw new InvalidOperationException($"Unknown full-trust filesystem operation ({operation}).")
        });
    }

    public async Task<object> ExecuteSteamAsync(
        string pluginId,
        string operation,
        JsonElement? arguments,
        CancellationToken cancellationToken)
    {
        _pluginStoreService.EnsurePluginSdkPermission(pluginId, "native.full-trust");
        operation = operation.Trim().ToLowerInvariant();
        _ = _pluginStoreService.GetPluginInstallationDirectory(pluginId);
        if (operation == "targets")
        {
            var targets = await _devToolsClient.GetTargetsAsync(cancellationToken);
            return targets.Select(target => new
            {
                target.Id,
                target.Title,
                target.Type,
                target.Url
            }).ToArray();
        }

        if (operation is "evaluate" or "inject")
        {
            var targetId = GetRequiredString(arguments, "targetId");
            var expression = GetRequiredString(arguments, "expression", trim: false);
            var targets = await _devToolsClient.GetTargetsAsync(cancellationToken);
            var target = targets.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, targetId, StringComparison.Ordinal));
            if (target is null)
            {
                throw new InvalidOperationException("The requested Steam DevTools target is no longer available.");
            }

            var result = await _devToolsClient.EvaluateAsync(
                target.WebSocketDebuggerUrl,
                expression,
                cancellationToken);
            return new
            {
                result.Success,
                result.Value,
                result.ErrorMessage,
                target.Id,
                target.Title,
                target.Url
            };
        }

        throw new InvalidOperationException($"Unknown full-trust Steam operation ({operation}).");
    }

    public object StopAll(string pluginId)
    {
        var stopped = new List<string>();
        foreach (var item in _processes.Where(item =>
                     string.Equals(item.Value.PluginId, pluginId, StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            if (_processes.TryRemove(item.Key, out var managed))
            {
                managed.Stop();
                managed.Dispose();
                stopped.Add(item.Key);
            }
        }

        return new { pluginId, stoppedProcessIds = stopped, count = stopped.Count };
    }

    public ValueTask DisposeAsync()
    {
        foreach (var item in _processes.ToArray())
        {
            if (_processes.TryRemove(item.Key, out var managed))
            {
                managed.Stop();
                managed.Dispose();
            }
        }
        return ValueTask.CompletedTask;
    }

    private object GetSystemInfo(string pluginId)
    {
        var paths = GetPaths(pluginId);
        return new
        {
            os = Environment.OSVersion.ToString(),
            osArchitecture = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(),
            processArchitecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            machineName = Environment.MachineName,
            userName = Environment.UserName,
            processorCount = Environment.ProcessorCount,
            framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            environment = Environment.GetEnvironmentVariables()
                .Cast<System.Collections.DictionaryEntry>()
                .ToDictionary(entry => String(entry.Key), entry => String(entry.Value), StringComparer.OrdinalIgnoreCase),
            paths
        };
    }

    private object GetPaths(string pluginId)
    {
        return new
        {
            plugin = _pluginStoreService.GetPluginInstallationDirectory(pluginId),
            data = _pluginStoreService.GetPluginDataDirectory(pluginId),
            temp = Path.GetTempPath(),
            app = AppContext.BaseDirectory,
            desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
            localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            roamingAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
        };
    }

    private async Task<object> RunAsync(
        string pluginId,
        JsonElement? arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = BuildStartInfo(pluginId, arguments, backend: false, capture: true);
        using var process = new Process { StartInfo = startInfo };
        var timeoutMs = Math.Clamp(GetInt32(arguments, "timeoutMs", 60_000), 1_000, MaxRunTimeoutMs);
        if (!process.Start())
        {
            throw new InvalidOperationException("The requested process could not be started.");
        }

        var standardInput = GetOptionalString(arguments, "stdin", trim: false);
        if (standardInput is not null)
        {
            await process.StandardInput.WriteAsync(standardInput.AsMemory(), cancellationToken);
        }
        process.StandardInput.Close();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(timeoutMs);
        try
        {
            var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var output = Limit(await outputTask);
            var error = Limit(await errorTask);
            return new
            {
                processId = process.Id.ToString(),
                exitCode = process.ExitCode,
                success = process.ExitCode == 0,
                output,
                error,
                timedOut = false
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            return new
            {
                processId = process.Id.ToString(),
                exitCode = -1,
                success = false,
                output = string.Empty,
                error = $"Process exceeded the {timeoutMs} ms timeout.",
                timedOut = true
            };
        }
    }

    private Task<object> StartAsync(
        string pluginId,
        JsonElement? arguments,
        bool backend,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var startInfo = BuildStartInfo(pluginId, arguments, backend, capture: true);
        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var managedId = Guid.NewGuid().ToString("N");
        var managed = new ManagedPluginProcess(managedId, pluginId, process, backend);
        if (!_processes.TryAdd(managedId, managed))
        {
            managed.Dispose();
            throw new InvalidOperationException("Unable to allocate a managed plugin process.");
        }

        try
        {
            managed.Start();
            return Task.FromResult<object>(managed.Snapshot());
        }
        catch
        {
            _processes.TryRemove(managedId, out _);
            managed.Dispose();
            throw;
        }
    }

    private ProcessStartInfo BuildStartInfo(
        string pluginId,
        JsonElement? arguments,
        bool backend,
        bool capture)
    {
        var fileName = backend
            ? GetRequiredString(arguments, "entryPoint")
            : GetRequiredString(arguments, "fileName");
        var packageRelative = backend || GetBoolean(arguments, "packageRelative", false) ||
            fileName.StartsWith("./", StringComparison.Ordinal) ||
            fileName.StartsWith(".\\", StringComparison.Ordinal);
        string? packageEntryPoint = null;
        if (packageRelative)
        {
            var pluginRoot = _pluginStoreService.GetPluginInstallationDirectory(pluginId);
            var candidate = Path.GetFullPath(Path.Combine(pluginRoot, fileName.TrimStart('.', '/', '\\')));
            EnsureWithin(candidate, pluginRoot);
            if (!File.Exists(candidate))
            {
                throw new FileNotFoundException("The plugin backend or executable was not found.", candidate);
            }
            fileName = candidate;
            packageEntryPoint = candidate;
        }

        var runtime = backend ? (GetOptionalString(arguments, "runtime") ?? "executable").ToLowerInvariant() : "executable";
        var runtimeArguments = new List<string>();
        if (backend && runtime != "executable")
        {
            var runtimeExecutable = GetOptionalString(arguments, "runtimeExecutable");
            switch (runtime)
            {
                case "powershell":
                    fileName = runtimeExecutable ?? "powershell.exe";
                    runtimeArguments.AddRange(["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", packageEntryPoint!]);
                    break;
                case "python":
                    fileName = runtimeExecutable ?? "python.exe";
                    runtimeArguments.Add(packageEntryPoint!);
                    break;
                case "node":
                    fileName = runtimeExecutable ?? "node.exe";
                    runtimeArguments.Add(packageEntryPoint!);
                    break;
                default:
                    throw new InvalidOperationException("Backend runtime must be executable, powershell, python, or node.");
            }
        }

        var workingDirectory = GetOptionalString(arguments, "workingDirectory");
        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            workingDirectory = backend
                ? Path.GetDirectoryName(packageEntryPoint ?? fileName)
                : _pluginStoreService.GetPluginDataDirectory(pluginId);
        }
        else if (!Path.IsPathRooted(workingDirectory))
        {
            workingDirectory = Path.GetFullPath(Path.Combine(
                _pluginStoreService.GetPluginInstallationDirectory(pluginId),
                workingDirectory));
        }

        var info = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory ?? AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = GetBoolean(arguments, "createNoWindow", true),
            WindowStyle = GetBoolean(arguments, "createNoWindow", true)
                ? ProcessWindowStyle.Hidden
                : ProcessWindowStyle.Normal,
            RedirectStandardOutput = capture,
            RedirectStandardError = capture,
            RedirectStandardInput = capture
        };
        foreach (var argument in runtimeArguments.Concat(GetStringArray(arguments, "arguments")))
        {
            info.ArgumentList.Add(argument);
        }
        foreach (var (key, value) in GetStringDictionary(arguments, "environment"))
        {
            info.Environment[key] = value;
        }
        foreach (var (environmentName, secretKey) in GetStringDictionary(arguments, "secretEnvironment"))
        {
            info.Environment[environmentName] = _pluginStoreService.GetPluginSecretForFullTrust(pluginId, secretKey);
        }
        info.Environment["TFS_PLUGIN_ID"] = pluginId;
        info.Environment["TFS_PLUGIN_DIR"] = _pluginStoreService.GetPluginInstallationDirectory(pluginId);
        info.Environment["TFS_PLUGIN_DATA_DIR"] = _pluginStoreService.GetPluginDataDirectory(pluginId);
        return info;
    }

    private object List(string pluginId)
    {
        return new
        {
            processes = _processes.Values
                .Where(process => string.Equals(process.PluginId, pluginId, StringComparison.OrdinalIgnoreCase))
                .Select(process => process.Snapshot())
                .ToArray()
        };
    }

    private object GetStatus(string pluginId, string processId)
    {
        var managed = GetOwnedProcess(pluginId, processId);
        return managed.Snapshot();
    }

    private object Stop(string pluginId, string processId)
    {
        var managed = GetOwnedProcess(pluginId, processId);
        _processes.TryRemove(processId, out _);
        managed.Stop();
        var snapshot = managed.Snapshot();
        managed.Dispose();
        return snapshot;
    }

    private object Open(string pluginId, JsonElement? arguments)
    {
        _ = _pluginStoreService.GetPluginInstallationDirectory(pluginId);
        var target = GetRequiredString(arguments, "target", trim: false);
        var info = new ProcessStartInfo
        {
            FileName = target,
            UseShellExecute = true,
            Verb = GetBoolean(arguments, "runAsAdministrator", false) ? "runas" : string.Empty,
            WorkingDirectory = GetOptionalString(arguments, "workingDirectory") ?? string.Empty
        };
        foreach (var argument in GetStringArray(arguments, "arguments"))
        {
            info.ArgumentList.Add(argument);
        }
        using var process = Process.Start(info)
            ?? throw new InvalidOperationException("Windows could not open the requested target.");
        return new { opened = true, target, processId = process.Id };
    }

    private async Task<object> CallAsync(
        string pluginId,
        JsonElement? arguments,
        CancellationToken cancellationToken)
    {
        var managed = GetOwnedProcess(pluginId, GetRequiredString(arguments, "processId"));
        if (!managed.Backend)
        {
            throw new InvalidOperationException("RPC calls are only available for managed plugin backends.");
        }
        var method = GetRequiredString(arguments, "method");
        var callArguments = GetProperty(arguments, "arguments");
        var timeoutMs = Math.Clamp(GetInt32(arguments, "timeoutMs", 30_000), 500, 120_000);
        return await managed.CallAsync(method, callArguments, timeoutMs, cancellationToken);
    }

    private object Stat(string pluginId, JsonElement? arguments)
    {
        var path = ResolveFileSystemPath(pluginId, arguments, "path");
        if (File.Exists(path))
        {
            var file = new FileInfo(path);
            return FileSnapshot(file.FullName, false, file.Length, file.LastWriteTimeUtc);
        }
        if (Directory.Exists(path))
        {
            var directory = new DirectoryInfo(path);
            return FileSnapshot(directory.FullName, true, 0, directory.LastWriteTimeUtc);
        }
        return new { path, exists = false };
    }

    private object ListFiles(string pluginId, JsonElement? arguments)
    {
        var path = ResolveFileSystemPath(pluginId, arguments, "path", allowEmpty: true);
        var recursive = GetBoolean(arguments, "recursive", false);
        var entries = Directory.EnumerateFileSystemEntries(
                path,
                "*",
                recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
            .Take(10_000)
            .Select(entry =>
            {
                if (Directory.Exists(entry))
                {
                    var directory = new DirectoryInfo(entry);
                    return FileSnapshot(directory.FullName, true, 0, directory.LastWriteTimeUtc);
                }
                var file = new FileInfo(entry);
                return FileSnapshot(file.FullName, false, file.Length, file.LastWriteTimeUtc);
            })
            .ToArray();
        return new { path, entries };
    }

    private object ReadText(string pluginId, JsonElement? arguments)
    {
        var path = ResolveFileSystemPath(pluginId, arguments, "path");
        return new { path, content = File.ReadAllText(path), encoding = "utf8", size = new FileInfo(path).Length };
    }

    private object ReadBytes(string pluginId, JsonElement? arguments)
    {
        var path = ResolveFileSystemPath(pluginId, arguments, "path");
        var bytes = File.ReadAllBytes(path);
        return new { path, content = Convert.ToBase64String(bytes), encoding = "base64", size = bytes.LongLength };
    }

    private object WriteText(string pluginId, JsonElement? arguments)
    {
        var path = ResolveFileSystemPath(pluginId, arguments, "path");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var content = GetOptionalString(arguments, "content", trim: false) ?? string.Empty;
        if (GetBoolean(arguments, "append", false)) File.AppendAllText(path, content);
        else File.WriteAllText(path, content);
        return StatByPath(path);
    }

    private object WriteBytes(string pluginId, JsonElement? arguments)
    {
        var path = ResolveFileSystemPath(pluginId, arguments, "path");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = Convert.FromBase64String(GetRequiredString(arguments, "content", trim: false));
        if (GetBoolean(arguments, "append", false))
        {
            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            stream.Write(bytes);
        }
        else File.WriteAllBytes(path, bytes);
        return StatByPath(path);
    }

    private object CreateDirectory(string pluginId, JsonElement? arguments)
    {
        var path = ResolveFileSystemPath(pluginId, arguments, "path", allowEmpty: true);
        Directory.CreateDirectory(path);
        return StatByPath(path);
    }

    private object Delete(string pluginId, JsonElement? arguments)
    {
        var path = ResolveFileSystemPath(pluginId, arguments, "path");
        if (Directory.Exists(path)) Directory.Delete(path, GetBoolean(arguments, "recursive", false));
        else if (File.Exists(path)) File.Delete(path);
        return new { path, exists = File.Exists(path) || Directory.Exists(path) };
    }

    private object Copy(string pluginId, JsonElement? arguments)
    {
        var source = ResolveFileSystemPath(pluginId, arguments, "sourcePath");
        var destination = ResolveFileSystemPath(pluginId, arguments, "destinationPath");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(source, destination, GetBoolean(arguments, "overwrite", false));
        return StatByPath(destination);
    }

    private object Move(string pluginId, JsonElement? arguments)
    {
        var source = ResolveFileSystemPath(pluginId, arguments, "sourcePath");
        var destination = ResolveFileSystemPath(pluginId, arguments, "destinationPath");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (GetBoolean(arguments, "overwrite", false) && File.Exists(destination)) File.Delete(destination);
        File.Move(source, destination);
        return StatByPath(destination);
    }

    private string ResolveFileSystemPath(
        string pluginId,
        JsonElement? arguments,
        string name,
        bool allowEmpty = false)
    {
        var path = GetOptionalString(arguments, name, trim: false) ?? string.Empty;
        if (!allowEmpty && string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException($"Full-trust filesystem argument '{name}' is required.");
        }
        var scope = GetOptionalString(arguments, "scope")?.ToLowerInvariant() ?? "data";
        if (Path.IsPathRooted(path) || scope == "absolute") return Path.GetFullPath(path);
        var root = scope switch
        {
            "plugin" or "package" => _pluginStoreService.GetPluginInstallationDirectory(pluginId),
            "data" => _pluginStoreService.GetPluginDataDirectory(pluginId),
            "app" => AppContext.BaseDirectory,
            "temp" => Path.GetTempPath(),
            _ => throw new InvalidOperationException("Filesystem scope must be data, plugin, app, temp, or absolute.")
        };
        return Path.GetFullPath(Path.Combine(root, path));
    }

    private ManagedPluginProcess GetOwnedProcess(string pluginId, string processId)
    {
        if (!_processes.TryGetValue(processId, out var managed) ||
            !string.Equals(managed.PluginId, pluginId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The requested managed process does not belong to this plugin.");
        }
        return managed;
    }

    private static object FileSnapshot(string path, bool isDirectory, long size, DateTime modifiedUtc) =>
        new { path, name = Path.GetFileName(path), exists = true, isDirectory, size, modifiedUtc };

    private static object StatByPath(string path)
    {
        if (Directory.Exists(path))
        {
            var directory = new DirectoryInfo(path);
            return FileSnapshot(path, true, 0, directory.LastWriteTimeUtc);
        }
        var file = new FileInfo(path);
        return FileSnapshot(path, false, file.Exists ? file.Length : 0, file.LastWriteTimeUtc);
    }

    private static void EnsureWithin(string path, string root)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(path).StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("A package-relative executable must stay inside the plugin package.");
        }
    }

    private static string GetRequiredString(JsonElement? arguments, string name, bool trim = true)
    {
        var value = GetOptionalString(arguments, name, trim);
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"Full-trust argument '{name}' is required.")
            : value;
    }

    private static string? GetOptionalString(JsonElement? arguments, string name, bool trim = true)
    {
        if (arguments is null || arguments.Value.ValueKind != JsonValueKind.Object ||
            !arguments.Value.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return null;
        }
        var value = property.GetString();
        return trim ? value?.Trim() : value;
    }

    private static bool GetBoolean(JsonElement? arguments, string name, bool defaultValue)
    {
        if (arguments is not null && arguments.Value.ValueKind == JsonValueKind.Object &&
            arguments.Value.TryGetProperty(name, out var property))
        {
            if (property.ValueKind == JsonValueKind.True) return true;
            if (property.ValueKind == JsonValueKind.False) return false;
        }
        return defaultValue;
    }

    private static int GetInt32(JsonElement? arguments, string name, int defaultValue)
    {
        return arguments is not null && arguments.Value.ValueKind == JsonValueKind.Object &&
               arguments.Value.TryGetProperty(name, out var property) && property.TryGetInt32(out var value)
            ? value
            : defaultValue;
    }

    private static IReadOnlyList<string> GetStringArray(JsonElement? arguments, string name)
    {
        return arguments is not null && arguments.Value.ValueKind == JsonValueKind.Object &&
               arguments.Value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Array
            ? property.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString() ?? string.Empty).ToArray()
            : [];
    }

    private static IReadOnlyDictionary<string, string> GetStringDictionary(JsonElement? arguments, string name)
    {
        if (arguments is null || arguments.Value.ValueKind != JsonValueKind.Object ||
            !arguments.Value.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.Object)
        {
            return new Dictionary<string, string>();
        }
        return property.EnumerateObject()
            .Where(item => item.Value.ValueKind == JsonValueKind.String)
            .ToDictionary(item => item.Name, item => item.Value.GetString() ?? string.Empty, StringComparer.OrdinalIgnoreCase);
    }

    private static JsonElement? GetProperty(JsonElement? arguments, string name)
    {
        return arguments is not null && arguments.Value.ValueKind == JsonValueKind.Object &&
               arguments.Value.TryGetProperty(name, out var property)
            ? property.Clone()
            : null;
    }

    private static string String(object? value) => Convert.ToString(value) ?? string.Empty;
    private static string Limit(string value) => value.Length <= MaxCapturedCharacters
        ? value
        : value[^MaxCapturedCharacters..];
    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
    }

    private sealed class ManagedPluginProcess : IDisposable
    {
        private readonly object _outputGate = new();
        private readonly StringBuilder _output = new();
        private readonly StringBuilder _error = new();
        private readonly Process _process;
        private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _rpcPending =
            new(StringComparer.Ordinal);

        public ManagedPluginProcess(string id, string pluginId, Process process, bool backend)
        {
            Id = id;
            PluginId = pluginId;
            Backend = backend;
            _process = process;
        }

        public string Id { get; }
        public string PluginId { get; }
        public bool Backend { get; }

        public void Start()
        {
            _process.OutputDataReceived += (_, args) =>
            {
                Append(_output, args.Data);
                TryCompleteRpc(args.Data);
            };
            _process.ErrorDataReceived += (_, args) => Append(_error, args.Data);
            if (!_process.Start()) throw new InvalidOperationException("The requested process could not be started.");
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
        }

        public async Task<object> CallAsync(
            string method,
            JsonElement? arguments,
            int timeoutMs,
            CancellationToken cancellationToken)
        {
            if (_process.HasExited)
            {
                throw new InvalidOperationException("The plugin backend is not running.");
            }
            var requestId = Guid.NewGuid().ToString("N");
            var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_rpcPending.TryAdd(requestId, completion))
            {
                throw new InvalidOperationException("Unable to allocate a backend RPC request.");
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(timeoutMs);
            using var registration = timeout.Token.Register(() =>
                completion.TrySetCanceled(timeout.Token));
            try
            {
                var payload = JsonSerializer.Serialize(new
                {
                    tfsRpcId = requestId,
                    method,
                    arguments
                });
                await _process.StandardInput.WriteLineAsync(payload.AsMemory(), timeout.Token);
                await _process.StandardInput.FlushAsync(timeout.Token);
                var response = await completion.Task;
                if (response.ValueKind == JsonValueKind.Object &&
                    response.TryGetProperty("error", out var error) &&
                    error.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(error.GetString()))
                {
                    throw new InvalidOperationException(error.GetString());
                }
                return response.ValueKind == JsonValueKind.Object && response.TryGetProperty("result", out var result)
                    ? result.Clone()
                    : response.Clone();
            }
            finally
            {
                _rpcPending.TryRemove(requestId, out _);
            }
        }

        public object Snapshot()
        {
            lock (_outputGate)
            {
                var running = false;
                int? exitCode = null;
                try
                {
                    running = !_process.HasExited;
                    if (!running) exitCode = _process.ExitCode;
                }
                catch { }
                return new
                {
                    processId = Id,
                    osProcessId = SafeProcessId(),
                    pluginId = PluginId,
                    backend = Backend,
                    running,
                    exitCode,
                    output = Limit(_output.ToString()),
                    error = Limit(_error.ToString()),
                    fileName = _process.StartInfo.FileName,
                    startedAtUtc = SafeStartTime()
                };
            }
        }

        public void Stop()
        {
            foreach (var completion in _rpcPending.Values)
            {
                completion.TrySetException(new InvalidOperationException("The plugin backend was stopped."));
            }
            _rpcPending.Clear();
            TryKill(_process);
        }
        public void Dispose() => _process.Dispose();

        private void Append(StringBuilder builder, string? line)
        {
            if (line is null) return;
            lock (_outputGate)
            {
                builder.AppendLine(line);
                if (builder.Length > MaxCapturedCharacters * 2)
                {
                    builder.Remove(0, builder.Length - MaxCapturedCharacters);
                }
            }
        }

        private void TryCompleteRpc(string? line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object ||
                    !root.TryGetProperty("tfsRpcId", out var idProperty) ||
                    idProperty.ValueKind != JsonValueKind.String)
                {
                    return;
                }
                var requestId = idProperty.GetString();
                if (!string.IsNullOrWhiteSpace(requestId) && _rpcPending.TryGetValue(requestId, out var completion))
                {
                    completion.TrySetResult(root.Clone());
                }
            }
            catch
            {
            }
        }

        private int? SafeProcessId() { try { return _process.Id; } catch { return null; } }
        private DateTimeOffset? SafeStartTime() { try { return _process.StartTime.ToUniversalTime(); } catch { return null; } }
    }
}
