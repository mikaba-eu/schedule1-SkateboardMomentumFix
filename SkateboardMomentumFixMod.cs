using System.Reflection;
using HarmonyLib;
using MelonLoader;

namespace SkateboardMomentumFix;

public sealed class SkateboardMomentumFixMod : MelonMod
{
	internal const string HarmonyId = "skateboardmomentumfix";

	public override void OnInitializeMelon()
	{
		var harmony = new HarmonyLib.Harmony(HarmonyId);
		harmony.PatchAll(Assembly.GetExecutingAssembly());

		SkateboardMomentumFixService.Initialize();
		MelonLogger.Msg("SkateboardMomentumFix initialized.");
	}

	public override void OnSceneWasInitialized(int buildIndex, string sceneName)
	{
		base.OnSceneWasInitialized(buildIndex, sceneName);
		SkateboardMomentumFixService.NotifySceneInitialized(sceneName);
	}

	public override void OnUpdate()
	{
		SkateboardMomentumFixService.Tick();
	}
}
