using HarmonyLib;

namespace SkateboardMomentumFix.Patches;

[HarmonyPatch(typeof(Skateboard_Equippable), "Update")]
internal static class SkateboardEquippable_Update_Patch
{
	private static void Postfix(Skateboard_Equippable __instance)
	{
		SkateboardMomentumFixService.CaptureMountSample(__instance);
	}
}

[HarmonyPatch(typeof(Skateboard_Equippable), nameof(Skateboard_Equippable.Mount))]
internal static class SkateboardEquippable_Mount_Patch
{
	private static void Prefix(Skateboard_Equippable __instance)
	{
		SkateboardMomentumFixService.BeginMount(__instance);
	}

	private static void Postfix(Skateboard_Equippable __instance)
	{
		SkateboardMomentumFixService.ApplyMountMomentum(__instance);
	}
}

[HarmonyPatch(typeof(Skateboard_Equippable), nameof(Skateboard_Equippable.Dismount))]
internal static class SkateboardEquippable_Dismount_Patch
{
	private static void Prefix(Skateboard_Equippable __instance)
	{
		SkateboardMomentumFixService.BeginDismount(__instance);
	}

	private static void Postfix()
	{
		SkateboardMomentumFixService.EndDismount();
	}
}

[HarmonyPatch(typeof(PlayerMovement), nameof(PlayerMovement.SetResidualVelocity))]
internal static class PlayerMovement_SetResidualVelocity_Patch
{
	private static void Prefix(ref UnityEngine.Vector3 dir, ref float force, ref float time)
	{
		SkateboardMomentumFixService.AdjustDismountResidual(ref dir, ref force, ref time);
	}
}

[HarmonyPatch(typeof(SkateboardCamera), "OnPlayerMountedSkateboard")]
internal static class SkateboardCamera_OnPlayerMountedSkateboard_Patch
{
	private static bool Prefix(SkateboardCamera __instance, Skateboard skateboard)
	{
		bool handled = SkateboardMomentumFixService.BeginCustomSkateboardCamera(__instance, skateboard);
		return !handled;
	}
}

[HarmonyPatch(typeof(SkateboardCamera), "Update")]
internal static class SkateboardCamera_Update_Patch
{
	private static bool Prefix(SkateboardCamera __instance)
	{
		return !SkateboardMomentumFixService.HasCustomSkateboardCamera(__instance);
	}
}

[HarmonyPatch(typeof(SkateboardCamera), "LateUpdate")]
internal static class SkateboardCamera_LateUpdate_Patch
{
	private static bool Prefix(SkateboardCamera __instance)
	{
		bool handled = SkateboardMomentumFixService.RunCustomSkateboardCamera(__instance);
		return !handled;
	}
}

[HarmonyPatch(typeof(SkateboardCamera), "OnDestroy")]
internal static class SkateboardCamera_OnDestroy_Patch
{
	private static void Prefix(SkateboardCamera __instance)
	{
		SkateboardMomentumFixService.NotifySkateboardCameraDestroyed(__instance);
	}
}

[HarmonyPatch(typeof(PlayerCamera), nameof(PlayerCamera.StopTransformOverride))]
internal static class PlayerCamera_StopTransformOverride_Patch
{
	private static void Prefix(ref float lerpTime)
	{
		SkateboardMomentumFixService.SmoothDismountCamera(ref lerpTime);
	}
}

[HarmonyPatch(typeof(PlayerCamera), nameof(PlayerCamera.StopFOVOverride))]
internal static class PlayerCamera_StopFOVOverride_Patch
{
	private static void Prefix(ref float lerpTime)
	{
		SkateboardMomentumFixService.SmoothDismountFov(ref lerpTime);
	}
}
