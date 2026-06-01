using System.Collections;
using System.Reflection;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using Lodestone.Models;

namespace Lodestone.Services;

public sealed class SubmarineService : IDisposable
{
    private const long RefreshIntervalMilliseconds = 60_000;

    private readonly Plugin plugin;
    private readonly object returnLock = new();
    private List<SubmarineReturn> returns = [];
    private long lastRefresh;

    public SubmarineService(Plugin plugin)
    {
        this.plugin = plugin;
        Plugin.Framework.Update += OnFrameworkUpdate;
        Refresh(force: true);
    }

    public string Status { get; private set; } = "AutoRetainer submarine returns not checked yet.";
    public int Version { get; private set; }

    public IReadOnlyList<SubmarineReturn> Returns
    {
        get
        {
            lock (returnLock)
                return returns.ToArray();
        }
    }

    public IEnumerable<SubmarineReturn> GetReturnsForDay(DateTime day)
    {
        lock (returnLock)
        {
            return returns
                .Where(r => r.ReturnAt.Date == day.Date)
                .OrderBy(r => r.ReturnAt)
                .ThenBy(r => r.VesselName)
                .ToArray();
        }
    }

    public void Refresh(bool force = false)
    {
        if (!plugin.Configuration.ShowSubmarineReturns)
        {
            SetReturns([], "AutoRetainer submarine returns hidden.");
            return;
        }

        if (!force && Environment.TickCount64 - lastRefresh < RefreshIntervalMilliseconds)
            return;

        lastRefresh = Environment.TickCount64;
        try
        {
            var loaded = LoadReturns().ToList();
            var status = loaded.Count == 0
                ? "No AutoRetainer submarine returns found."
                : $"{loaded.Count} AutoRetainer submarine return{(loaded.Count == 1 ? string.Empty : "s")} loaded.";
            SetReturns(loaded, status);
        }
        catch (Exception ex)
        {
            Plugin.Log.Verbose(ex, "Unable to read AutoRetainer submarine return data.");
            SetReturns([], "AutoRetainer submarine data unavailable.");
        }
    }

    private IEnumerable<SubmarineReturn> LoadReturns()
    {
        var offlineType = FindType("AutoRetainerAPI.Configuration.OfflineCharacterData");
        if (offlineType == null)
            yield break;

        var subscriber = CreateGetOfflineCharacterDataSubscriber(offlineType);
        if (subscriber == null)
            yield break;

        List<ulong> cids;
        try
        {
            cids = Plugin.PluginInterface.GetIpcSubscriber<List<ulong>>("AutoRetainer.GetRegisteredCIDs").InvokeFunc() ?? [];
        }
        catch
        {
            yield break;
        }

        foreach (var cid in cids.Distinct())
        {
            object? character;
            try
            {
                character = InvokeOfflineCharacterData(subscriber, cid);
            }
            catch
            {
                continue;
            }

            if (character == null)
                continue;

            foreach (var submarineReturn in ReadCharacterSubmarineReturns(cid, character))
                yield return submarineReturn;
        }
    }

    private IEnumerable<SubmarineReturn> ReadCharacterSubmarineReturns(ulong cid, object character)
    {
        var characterName = ReadString(character, "Name");
        var world = ReadString(character, "CurrentWorld");
        if (string.IsNullOrWhiteSpace(world))
            world = ReadString(character, "World");

        var workshopEnabled = ReadBool(character, "WorkshopEnabled");
        var excluded = ReadBool(character, "ExcludeWorkshop");
        var waitForAll = ReadBool(character, "MultiWaitForAllDeployables");
        var enabledSubs = ReadStringSet(character, "EnabledSubs");
        var additional = ReadDictionary(character, "AdditionalSubmarineData");

        foreach (var vessel in ReadEnumerable(character, "OfflineSubmarineData"))
        {
            var vesselName = ReadString(vessel, "Name");
            var returnTime = ReadUInt(vessel, "ReturnTime");
            if (string.IsNullOrWhiteSpace(vesselName) || returnTime == 0)
                continue;

            var add = additional.TryGetValue(vesselName, out var data) ? data : null;
            var returnAt = DateTimeOffset.FromUnixTimeSeconds(returnTime).LocalDateTime;
            var id = $"{cid}:{vesselName}:{returnTime}";
            yield return new SubmarineReturn
            {
                Id = id,
                CharacterId = cid,
                CharacterName = characterName,
                World = world,
                VesselName = vesselName,
                ReturnUnixSeconds = returnTime,
                ReturnAt = returnAt,
                WorkshopEnabled = workshopEnabled,
                CharacterExcluded = excluded,
                EnabledInAutoRetainer = enabledSubs.Contains(vesselName),
                WaitForAllDeployables = waitForAll,
                Level = ReadInt(add, "Level"),
                CurrentExp = ReadUInt(add, "CurrentExp"),
                NextLevelExp = ReadUInt(add, "NextLevelExp"),
                Behavior = ReadString(add, "VesselBehavior"),
                SelectedPointPlan = ReadString(add, "SelectedPointPlan"),
                SelectedUnlockPlan = ReadString(add, "SelectedUnlockPlan"),
                Points = ReadByteArray(add, "Points")
            };
        }
    }

    private void SetReturns(List<SubmarineReturn> loaded, string status)
    {
        lock (returnLock)
        {
            returns = loaded
                .OrderBy(r => r.ReturnAt)
                .ThenBy(r => r.VesselName)
                .ToList();
            Version++;
        }

        Status = status;
    }

    private static Type? FindType(string fullName)
        => AppDomain.CurrentDomain
            .GetAssemblies()
            .Select(assembly =>
            {
                try
                {
                    return assembly.GetType(fullName, throwOnError: false);
                }
                catch
                {
                    return null;
                }
            })
            .FirstOrDefault(type => type != null);

    private static object? CreateGetOfflineCharacterDataSubscriber(Type offlineType)
    {
        var method = FindGenericMethod(Plugin.PluginInterface.GetType(), "GetIpcSubscriber", 2);
        method ??= FindGenericMethod(typeof(Dalamud.Plugin.IDalamudPluginInterface), "GetIpcSubscriber", 2);
        if (method == null)
            return null;

        return method.MakeGenericMethod(typeof(ulong), offlineType)
            .Invoke(Plugin.PluginInterface, ["AutoRetainer.GetOfflineCharacterData"]);
    }

    private static MethodInfo? FindGenericMethod(Type type, string name, int genericArgumentCount)
        => type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(method => method.Name == name
                                      && method.IsGenericMethodDefinition
                                      && method.GetGenericArguments().Length == genericArgumentCount
                                      && method.GetParameters().Length == 1
                                      && method.GetParameters()[0].ParameterType == typeof(string));

    private static object? InvokeOfflineCharacterData(object subscriber, ulong cid)
    {
        var method = subscriber.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == "InvokeFunc" && m.GetParameters().Length == 1);
        return method?.Invoke(subscriber, [cid]);
    }

    private static IEnumerable<object> ReadEnumerable(object? source, string name)
    {
        if (ReadValue(source, name) is not IEnumerable enumerable)
            yield break;

        foreach (var item in enumerable)
        {
            if (item != null)
                yield return item;
        }
    }

    private static HashSet<string> ReadStringSet(object? source, string name)
        => ReadEnumerable(source, name)
            .Select(item => item.ToString() ?? string.Empty)
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, object> ReadDictionary(object? source, string name)
    {
        var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        if (ReadValue(source, name) is not IDictionary dictionary)
            return result;

        foreach (DictionaryEntry entry in dictionary)
        {
            if (entry.Key is string key && entry.Value != null)
                result[key] = entry.Value;
        }

        return result;
    }

    private static object? ReadValue(object? source, string name)
    {
        if (source == null)
            return null;

        var type = source.GetType();
        return type.GetField(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(source)
               ?? type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(source);
    }

    private static string ReadString(object? source, string name)
        => ReadValue(source, name)?.ToString()?.Trim() ?? string.Empty;

    private static bool ReadBool(object? source, string name)
        => ReadValue(source, name) is bool value && value;

    private static int ReadInt(object? source, string name)
        => ReadValue(source, name) switch
        {
            int value => value,
            byte value => value,
            short value => value,
            uint value when value <= int.MaxValue => (int)value,
            _ => 0
        };

    private static uint ReadUInt(object? source, string name)
        => ReadValue(source, name) switch
        {
            uint value => value,
            int value when value >= 0 => (uint)value,
            byte value => value,
            short value when value >= 0 => (uint)value,
            _ => 0
        };

    private static byte[] ReadByteArray(object? source, string name)
        => ReadValue(source, name) as byte[] ?? [];

    private void OnFrameworkUpdate(IFramework framework)
        => Refresh(force: false);

    public void Dispose()
    {
        Plugin.Framework.Update -= OnFrameworkUpdate;
    }
}
