using System.Collections.Generic;
using UnityEngine;

namespace SkateboardMomentumFix;

internal static class SkateboardMomentumFixService
{
	private enum TransitionPhase
	{
		None,
		Mount,
		Dismount
	}

	private readonly struct MountSample
	{
		internal MountSample(Vector3 flatVelocity, float capturedAt, bool hadSprintCarry)
		{
			FlatVelocity = flatVelocity;
			CapturedAt = capturedAt;
			HadSprintCarry = hadSprintCarry;
		}

		internal Vector3 FlatVelocity { get; }

		internal float CapturedAt { get; }

		internal bool HadSprintCarry { get; }
	}

	private readonly struct CameraPose
	{
		internal CameraPose(Vector3 position, Quaternion rotation)
		{
			Position = position;
			Rotation = rotation;
		}

		internal Vector3 Position { get; }

		internal Quaternion Rotation { get; }
	}

	private readonly struct DismountContext
	{
		internal DismountContext(Vector3 flatBoardVelocity, Vector3 flatPlayerForward)
		{
			FlatBoardVelocity = flatBoardVelocity;
			FlatPlayerForward = flatPlayerForward;
		}

		internal Vector3 FlatBoardVelocity { get; }

		internal Vector3 FlatPlayerForward { get; }
	}

	private const float SprintCarryWindow = 0.9f;
	private const float SprintMultiplierFloor = 1.9f;
	private const float MountSampleTtl = 1.25f;
	private const float MinMountTransferSpeed = 0.9f;
	private const float MaxMountTransferSpeed = 8.5f;
	private const float MountTransferMultiplier = 1.15f;
	private const float MaxMountDirectionAngle = 55f;
	private const float TransitionWindow = 0.45f;
	private const float MountCameraBlendTime = 0.11f;
	private const float MinDismountCameraLerp = 0.12f;
	private const float MinDismountFovLerp = 0.1f;
	private const float MinDismountResidualSpeed = 0.75f;
	private const float DismountResidualForcePerSpeed = 14f;
	private const float DismountResidualMinForce = 20f;
	private const float DismountResidualMaxForce = 130f;
	private const float DismountResidualDurationBase = 0.08f;
	private const float DismountResidualDurationPerSpeed = 0.01f;
	private const float DismountResidualDurationMin = 0.1f;
	private const float DismountResidualDurationMax = 0.18f;

	private static readonly Dictionary<int, MountSample> mountSamples = new();
	private static readonly Dictionary<int, CameraPose> mountCameraStartPoses = new();
	private static float sprintCarryUntil = -100f;
	private static TransitionPhase transitionPhase = TransitionPhase.None;
	private static float transitionUntil = -100f;
	private static bool hasDismountContext;
	private static DismountContext dismountContext;

	internal static void Initialize()
	{
		mountSamples.Clear();
		mountCameraStartPoses.Clear();
		sprintCarryUntil = -100f;
		transitionPhase = TransitionPhase.None;
		transitionUntil = -100f;
		hasDismountContext = false;
		dismountContext = default;
	}

	internal static void NotifySceneInitialized(string sceneName)
	{
		Initialize();
	}

	internal static void Tick()
	{
		float now = Time.timeSinceLevelLoad;
		PlayerMovement movement = GetPlayerMovement();
		if (movement != null && movement.IsSprinting)
		{
			sprintCarryUntil = now + SprintCarryWindow;
		}

		if (transitionPhase != TransitionPhase.None && now > transitionUntil)
		{
			transitionPhase = TransitionPhase.None;
		}

		if (mountSamples.Count <= 0)
		{
			return;
		}

		List<int>? staleKeys = null;
		foreach (KeyValuePair<int, MountSample> entry in mountSamples)
		{
			if (now - entry.Value.CapturedAt <= MountSampleTtl)
			{
				continue;
			}

			staleKeys ??= new List<int>();
			staleKeys.Add(entry.Key);
		}

		if (staleKeys == null)
		{
			return;
		}

		for (int i = 0; i < staleKeys.Count; i++)
		{
			mountSamples.Remove(staleKeys[i]);
		}
	}

	internal static void CaptureMountSample(Skateboard_Equippable equippable)
	{
		if (equippable == null)
		{
			return;
		}

		int key = equippable.GetInstanceID();
		if (equippable.IsRiding || !GameInput.GetButton(GameInput.ButtonCode.SkateboardMount))
		{
			mountSamples.Remove(key);
			return;
		}

		Vector3 measuredVelocity = MeasurePlayerFlatVelocity();
		if (measuredVelocity.sqrMagnitude <= 0.0001f)
		{
			return;
		}

		float now = Time.timeSinceLevelLoad;
		bool sprintCarry = now <= sprintCarryUntil;
		bool shouldUpdate = GameInput.GetButtonDown(GameInput.ButtonCode.SkateboardMount);
		if (!mountSamples.TryGetValue(key, out MountSample prior))
		{
			shouldUpdate = true;
		}
		else if (measuredVelocity.sqrMagnitude > prior.FlatVelocity.sqrMagnitude)
		{
			shouldUpdate = true;
			sprintCarry = sprintCarry || prior.HadSprintCarry;
		}
		else
		{
			sprintCarry = sprintCarry || prior.HadSprintCarry;
		}

		if (shouldUpdate)
		{
			mountSamples[key] = new MountSample(measuredVelocity, now, sprintCarry);
		}
	}

	internal static void BeginMount(Skateboard_Equippable equippable)
	{
		transitionPhase = TransitionPhase.Mount;
		transitionUntil = Time.timeSinceLevelLoad + TransitionWindow;
		CaptureMountSample(equippable);
	}

	internal static void ApplyMountMomentum(Skateboard_Equippable equippable)
	{
		if (equippable == null || equippable.ActiveSkateboard == null)
		{
			return;
		}

		Skateboard board = equippable.ActiveSkateboard;
		int key = equippable.GetInstanceID();

		Vector3 measured = MeasurePlayerFlatVelocity();
		bool sprintCarry = Time.timeSinceLevelLoad <= sprintCarryUntil;
		if (mountSamples.TryGetValue(key, out MountSample sample))
		{
			if (sample.FlatVelocity.sqrMagnitude > measured.sqrMagnitude)
			{
				measured = sample.FlatVelocity;
			}

			sprintCarry = sprintCarry || sample.HadSprintCarry;
		}
		mountSamples.Remove(key);

		Vector3 predicted = PredictInputFlatVelocity(sprintCarry);
		Vector3 chosenVelocity = measured;
		if (chosenVelocity.sqrMagnitude <= 0.0001f)
		{
			chosenVelocity = predicted;
		}
		else if (predicted.sqrMagnitude > chosenVelocity.sqrMagnitude && sprintCarry)
		{
			chosenVelocity = Vector3.Lerp(chosenVelocity, predicted, 0.45f);
		}

		float transferSpeed = chosenVelocity.magnitude;
		if (transferSpeed < MinMountTransferSpeed)
		{
			return;
		}

		Vector3 boardForward = Flatten(board.transform.forward);
		Vector3 direction = ResolveMountDirection(chosenVelocity, predicted, boardForward);
		if (direction.sqrMagnitude <= 0.0001f)
		{
			return;
		}

		float finalSpeed = Mathf.Clamp(transferSpeed * MountTransferMultiplier, MinMountTransferSpeed, MaxMountTransferSpeed);
		board.SetVelocity(direction * finalSpeed);
	}

	internal static void CaptureMountCameraPose(SkateboardCamera skateboardCamera)
	{
		if (skateboardCamera == null)
		{
			return;
		}

		PlayerCamera playerCamera = GetPlayerCamera();
		if (playerCamera == null)
		{
			return;
		}

		mountCameraStartPoses[skateboardCamera.GetInstanceID()] = new CameraPose(playerCamera.transform.position, playerCamera.transform.rotation);
	}

	internal static void BlendMountCamera(SkateboardCamera skateboardCamera)
	{
		if (skateboardCamera == null)
		{
			return;
		}

		int key = skateboardCamera.GetInstanceID();
		if (!mountCameraStartPoses.TryGetValue(key, out CameraPose startPose))
		{
			return;
		}
		mountCameraStartPoses.Remove(key);

		PlayerCamera playerCamera = GetPlayerCamera();
		if (playerCamera == null || transitionPhase != TransitionPhase.Mount)
		{
			return;
		}

		Vector3 targetPosition = playerCamera.transform.position;
		Quaternion targetRotation = playerCamera.transform.rotation;
		if ((targetPosition - startPose.Position).sqrMagnitude <= 0.0001f &&
		    Quaternion.Angle(targetRotation, startPose.Rotation) <= 0.05f)
		{
			return;
		}

		playerCamera.transform.position = startPose.Position;
		playerCamera.transform.rotation = startPose.Rotation;
		playerCamera.OverrideTransform(targetPosition, targetRotation, MountCameraBlendTime);
	}

	internal static void BeginDismount(Skateboard_Equippable equippable)
	{
		transitionPhase = TransitionPhase.Dismount;
		transitionUntil = Time.timeSinceLevelLoad + TransitionWindow;

		hasDismountContext = false;
		if (equippable == null)
		{
			return;
		}

		Player local = Player.Local;
		if (local == null)
		{
			return;
		}

		Vector3 boardVelocity = Vector3.zero;
		if (equippable.ActiveSkateboard != null && equippable.ActiveSkateboard.Rb != null)
		{
			boardVelocity = Flatten(equippable.ActiveSkateboard.Rb.velocity);
		}

		Vector3 playerForward = Flatten(local.transform.forward);
		dismountContext = new DismountContext(boardVelocity, playerForward);
		hasDismountContext = true;
	}

	internal static void EndDismount()
	{
		hasDismountContext = false;
	}

	internal static void AdjustDismountResidual(ref Vector3 dir, ref float force, ref float time)
	{
		if (!hasDismountContext)
		{
			return;
		}

		Vector3 forward = NormalizeOrZero(dismountContext.FlatPlayerForward);
		Vector3 velocityDir = NormalizeOrZero(dismountContext.FlatBoardVelocity);
		Vector3 resolvedDir = forward;

		if (velocityDir.sqrMagnitude > 0.0001f && forward.sqrMagnitude > 0.0001f)
		{
			float alignment01 = Mathf.Clamp01((Vector3.Dot(forward, velocityDir) + 1f) * 0.5f);
			float velocityWeight = Mathf.Lerp(0.2f, 0.65f, alignment01);
			resolvedDir = Vector3.Slerp(forward, velocityDir, velocityWeight).normalized;
		}
		else if (velocityDir.sqrMagnitude > 0.0001f)
		{
			resolvedDir = velocityDir;
		}
		else if (resolvedDir.sqrMagnitude <= 0.0001f)
		{
			resolvedDir = NormalizeOrZero(Flatten(dir));
		}

		if (resolvedDir.sqrMagnitude <= 0.0001f)
		{
			return;
		}

		float speed = dismountContext.FlatBoardVelocity.magnitude;
		if (speed < MinDismountResidualSpeed)
		{
			dir = resolvedDir;
			force = 0f;
			time = 0f;
			return;
		}

		dir = resolvedDir;
		force = Mathf.Clamp(speed * DismountResidualForcePerSpeed, DismountResidualMinForce, DismountResidualMaxForce);
		time = Mathf.Clamp(DismountResidualDurationBase + speed * DismountResidualDurationPerSpeed, DismountResidualDurationMin, DismountResidualDurationMax);
	}

	internal static void SmoothDismountCamera(ref float lerpTime)
	{
		if (transitionPhase != TransitionPhase.Dismount)
		{
			return;
		}

		lerpTime = Mathf.Max(lerpTime, MinDismountCameraLerp);
	}

	internal static void SmoothDismountFov(ref float lerpTime)
	{
		if (transitionPhase != TransitionPhase.Dismount)
		{
			return;
		}

		lerpTime = Mathf.Max(lerpTime, MinDismountFovLerp);
	}

	private static Vector3 MeasurePlayerFlatVelocity()
	{
		Player local = Player.Local;
		if (local == null)
		{
			return Vector3.zero;
		}

		Vector3 best = Vector3.zero;
		SmoothedVelocityCalculator velocityCalculator = local.VelocityCalculator;
		if (velocityCalculator != null)
		{
			best = Flatten(velocityCalculator.Velocity);
		}

		PlayerMovement movement = GetPlayerMovement();
		if (movement != null)
		{
			Vector3 move = Flatten(movement.Movement);
			if (move.sqrMagnitude > best.sqrMagnitude)
			{
				best = move;
			}
		}

		return best;
	}

	private static Vector3 PredictInputFlatVelocity(bool forceSprintFloor)
	{
		Player local = Player.Local;
		PlayerMovement movement = GetPlayerMovement();
		if (local == null || movement == null)
		{
			return Vector3.zero;
		}

		Vector2 input = GameInput.MotionAxis;
		Vector3 localInput = new Vector3(input.x, 0f, input.y);
		if (localInput.sqrMagnitude <= 0.0001f)
		{
			return Vector3.zero;
		}
		if (localInput.sqrMagnitude > 1f)
		{
			localInput.Normalize();
		}

		Vector3 worldInput = NormalizeOrZero(Flatten(local.transform.TransformDirection(localInput)));
		if (worldInput.sqrMagnitude <= 0.0001f)
		{
			return Vector3.zero;
		}

		float sprintMultiplier = movement.CurrentSprintMultiplier;
		if (forceSprintFloor)
		{
			sprintMultiplier = Mathf.Max(sprintMultiplier, SprintMultiplierFloor);
		}

		float crouchMultiplier = movement.IsCrouched ? 1f - 0.4f * (1f - movement.StandingScale) : 1f;
		float speed = PlayerMovement.WalkSpeed * sprintMultiplier * crouchMultiplier * PlayerMovement.StaticMoveSpeedMultiplier * movement.MoveSpeedMultiplier;
		if (local.IsTased)
		{
			speed *= 0.5f;
		}

		return worldInput * speed;
	}

	private static Vector3 ResolveMountDirection(Vector3 measuredVelocity, Vector3 predictedVelocity, Vector3 boardForward)
	{
		Vector3 boardDir = NormalizeOrZero(boardForward);
		Vector3 measuredDir = NormalizeOrZero(measuredVelocity);
		Vector3 predictedDir = NormalizeOrZero(predictedVelocity);

		Vector3 chosen = measuredDir;
		if (chosen.sqrMagnitude <= 0.0001f)
		{
			chosen = predictedDir;
		}
		else if (predictedDir.sqrMagnitude > 0.0001f && Vector3.Dot(chosen, predictedDir) > 0f)
		{
			chosen = Vector3.Slerp(chosen, predictedDir, 0.25f).normalized;
		}
		if (chosen.sqrMagnitude <= 0.0001f)
		{
			chosen = boardDir;
		}
		if (chosen.sqrMagnitude <= 0.0001f)
		{
			return Vector3.zero;
		}
		if (boardDir.sqrMagnitude <= 0.0001f)
		{
			return chosen;
		}

		float signedAngle = Vector3.SignedAngle(boardDir, chosen, Vector3.up);
		float clampedAngle = Mathf.Clamp(signedAngle, 0f - MaxMountDirectionAngle, MaxMountDirectionAngle);
		Vector3 limited = (Quaternion.AngleAxis(clampedAngle, Vector3.up) * boardDir).normalized;
		if (Vector3.Dot(limited, boardDir) < 0f)
		{
			return boardDir;
		}

		return limited;
	}

	private static Vector3 Flatten(Vector3 vector)
	{
		vector.y = 0f;
		return vector;
	}

	private static Vector3 NormalizeOrZero(Vector3 vector)
	{
		if (vector.sqrMagnitude <= 0.0001f)
		{
			return Vector3.zero;
		}

		return vector.normalized;
	}

	private static PlayerMovement GetPlayerMovement()
	{
#if IL2CPP
		return Il2CppScheduleOne.DevUtilities.PlayerSingleton<PlayerMovement>.Instance;
#else
		return ScheduleOne.DevUtilities.PlayerSingleton<PlayerMovement>.Instance;
#endif
	}

	private static PlayerCamera GetPlayerCamera()
	{
#if IL2CPP
		return Il2CppScheduleOne.DevUtilities.PlayerSingleton<PlayerCamera>.Instance;
#else
		return ScheduleOne.DevUtilities.PlayerSingleton<PlayerCamera>.Instance;
#endif
	}
}
