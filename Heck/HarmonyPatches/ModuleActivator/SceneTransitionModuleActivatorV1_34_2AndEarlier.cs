#if PRE_V1_37_1
using Heck.Module;
using SiraUtil.Affinity;

namespace Heck.HarmonyPatches.ModuleActivator;

internal class SceneTransitionModuleActivator : IAffinity
{
    private readonly ModuleManager _moduleManager;

    internal SceneTransitionModuleActivator(ModuleManager moduleManager)
    {
        _moduleManager = moduleManager;
    }

    [AffinityPrefix]
    [AffinityPatch(
        typeof(MissionLevelScenesTransitionSetupData),
        nameof(MissionLevelScenesTransitionSetupData.Init))]
    private void MissionPrefix(IDifficultyBeatmap difficultyBeatmap)
    {
        OverrideEnvironmentSettings? overrideEnvironmentSettings = null;
        _moduleManager.Activate(difficultyBeatmap, LevelType.Mission, ref overrideEnvironmentSettings);
    }

    [AffinityPrefix]
    [AffinityPatch(
        typeof(MultiplayerLevelScenesTransitionSetupData),
        nameof(MultiplayerLevelScenesTransitionSetupData.Init))]
    private void MultiplayerPrefix(IDifficultyBeatmap difficultyBeatmap)
    {
        OverrideEnvironmentSettings? overrideEnvironmentSettings = null;
        _moduleManager.Activate(difficultyBeatmap, LevelType.Multiplayer, ref overrideEnvironmentSettings);
    }

    [AffinityPrefix]
    [AffinityPatch(
        typeof(StandardLevelScenesTransitionSetupData),
        nameof(StandardLevelScenesTransitionSetupData.Init))]
    private void StandardPrefix(
        IDifficultyBeatmap difficultyBeatmap,
        ref OverrideEnvironmentSettings? overrideEnvironmentSettings)
    {
        _moduleManager.Activate(difficultyBeatmap, LevelType.Standard, ref overrideEnvironmentSettings);
    }

    [AffinityPrefix]
    [AffinityPatch(
        typeof(TutorialScenesTransitionSetupData),
        nameof(TutorialScenesTransitionSetupData.Init))]
    private void TutorialPrefix()
    {
        OverrideEnvironmentSettings? overrideEnvironmentSettings = null;
        _moduleManager.Activate(null, LevelType.Standard, ref overrideEnvironmentSettings);
    }
}
#endif
