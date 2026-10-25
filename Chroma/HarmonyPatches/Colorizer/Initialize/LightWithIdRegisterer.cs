using System;
using System.Collections.Generic;
using Chroma.Colorizer;
using Chroma.Lighting;
using SiraUtil.Affinity;

namespace Chroma.HarmonyPatches.Colorizer.Initialize;

internal class LightWithIdRegisterer : IAffinity
{
    private readonly LightColorizerManager _colorizerManager;
    private readonly LightWithIdManager _lightWithIdManager;
    private readonly HashSet<ILightWithId> _needToRegister = [];
    private readonly Dictionary<ILightWithId, int> _requestedIds = new();
    private readonly LightIDTableManager _tableManager;

    private LightWithIdRegisterer(
        LightColorizerManager colorizerManager,
        LightWithIdManager lightWithIdManager,
        LightIDTableManager tableManager)
    {
        _colorizerManager = colorizerManager;
        _lightWithIdManager = lightWithIdManager;
        _tableManager = tableManager;
    }

    internal void ForceUnregister(ILightWithId lightWithId)
    {
        int lightId = lightWithId.lightId;
        ILightWithId[] lights = _lightWithIdManager._oldMapping[lightId].lightInstances ?? [];
        int index = Array.IndexOf(lights, lightWithId);
        if (index < 0)
        {
            return;
        }

        lights[index] = null!;
        _tableManager.UnregisterIndex(lightId, index);
        _colorizerManager.CreateLightColorizerContractByLightID(
            lightId,
            n => n.ChromaLightSwitchEventEffect.UnregisterLight(lightWithId));
        lightWithId.__SetIsUnRegistered();
    }

    internal void MarkForTableRegister(ILightWithId lightWithId)
    {
        _needToRegister.Add(lightWithId);
    }

    internal void SetRequestedId(ILightWithId lightWithId, int id)
    {
        _requestedIds[lightWithId] = id;
    }

    [AffinityPrefix]
    [AffinityPatch(typeof(LightWithIdManager), nameof(LightWithIdManager.UnregisterLight))]
    private bool DontClearList(LightWithIdManager __instance, ILightWithId lightWithId)
    {
        if (__instance != _lightWithIdManager || (lightWithId.groupId != -1 && lightWithId.elementId != -1))
        {
            return true;
        }

        lightWithId.__SetIsUnRegistered();
        return false;
    }

    [AffinityPrefix]
    [AffinityPatch(typeof(LightWithIdManager), nameof(LightWithIdManager.RegisterLight))]
    private bool PreserveRegistration(LightWithIdManager __instance, ILightWithId lightWithId)
    {
        if (__instance != _lightWithIdManager || (lightWithId.groupId != -1 && lightWithId.elementId != -1))
        {
            return true;
        }

        if (lightWithId.isRegistered)
        {
            return false;
        }

        int lightId = lightWithId.lightId;
        if (lightId < 0 || lightId >= __instance._oldMapping.Length ||
            Array.IndexOf(__instance._oldMapping[lightId]?.lightInstances ?? [], lightWithId) < 0)
        {
            return true;
        }

        // Reactivation keeps the stable mapping index and its existing color tween.
        lightWithId.__SetIsRegistered();
        return false;
    }

    [AffinityPostfix]
    [AffinityPatch(typeof(LightWithIdManager), nameof(LightWithIdManager.RegisterLight))]
    private void Postfix(LightWithIdManager __instance, ILightWithId lightWithId, bool __runOriginal)
    {
        if (!__runOriginal || __instance != _lightWithIdManager || !lightWithId.isRegistered ||
            (lightWithId.groupId != -1 && lightWithId.elementId != -1))
        {
            return;
        }

        int lightId = lightWithId.lightId;
        if (lightId < 0 || lightId >= __instance._oldMapping.Length)
        {
            return;
        }

        ILightWithId[] lights = __instance._oldMapping[lightId]?.lightInstances ?? [];
        int index = Array.IndexOf(lights, lightWithId);
        if (index < 0)
        {
            return;
        }

        if (_needToRegister.Remove(lightWithId))
        {
            int? tableId = _requestedIds.TryGetValue(lightWithId, out int value) ? value : null;
            _tableManager.RegisterIndex(lightId, index, tableId);
        }

        _colorizerManager.CreateLightColorizerContractByLightID(
            lightId,
            n =>
            {
                if (!n.ChromaLightSwitchEventEffect.ColorTweens.ContainsKey(lightWithId))
                {
                    n.ChromaLightSwitchEventEffect.RegisterLight(lightWithId, index);
                }
            });
        lightWithId.ColorWasSet(__instance.GetColorForId(lightId, true));
    }
}
