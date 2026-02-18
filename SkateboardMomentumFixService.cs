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

	private sealed class CustomCameraState
	{
		internal CustomCameraState(SkateboardCamera cameraComponent, Skateboard board, PlayerCamera playerCamera)
		{
			CameraComponent = cameraComponent;
			Board = board;
			CurrentPosition = playerCamera.transform.position;
			CurrentRotation = playerCamera.transform.rotation;
			MountStartPosition = CurrentPosition;
			MountStartRotation = CurrentRotation;
			MountBlend = 0f;
			ManualWeight = NeedsSecondaryClick() ? 0f : 1f;
			ManualWeightVelocity = 0f;
			TimeSinceManualInput = 100f;
			Distance = 2.5f;
			DistanceCurrent = 2.5f;
			DistanceVelocity = 0f;
			BaseFov = (playerCamera.Camera != null) ? playerCamera.Camera.fieldOfView : 80f;
			CurrentFovMultiplier = 1f;
			SmoothedForward = Vector3.forward;
			LocalBodyHidden = false;
			HideBodyUntilTime = 0f;
		}

		internal SkateboardCamera CameraComponent { get; }

		internal Skateboard Board { get; }

		internal Vector3 CurrentPosition;

		internal Quaternion CurrentRotation;

		internal Vector3 MountStartPosition;

		internal Quaternion MountStartRotation;

		internal float MountBlend;

		internal float Distance;

		internal float DistanceCurrent;

		internal float DistanceVelocity;

		internal float OrbitYaw;

		internal float OrbitPitch;

		internal float OrbitYawVelocity;

		internal float OrbitPitchVelocity;

		internal float ManualWeight;

		internal float ManualWeightVelocity;

		internal float TimeSinceManualInput;

		internal float BaseFov;

		internal float CurrentFovMultiplier;

		internal Vector3 SmoothedForward;

		internal bool LocalBodyHidden;

		internal float HideBodyUntilTime;
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
	private const float CameraMountBlendDuration = 2f;
	private const float CameraMountHideBodyDuration = 0.28f;
	private const float CameraPositionSharpness = 11.5f;
	private const float CameraRotationSharpness = 14.5f;
	private const float CameraDistanceSmoothTime = 2.6f;
	private const float CameraAutoYawSmoothTime = 0.2f;
	private const float CameraAutoPitchSmoothTime = 0.24f;
	private const float CameraForwardSharpness = 11f;
	private const float CameraManualReleaseDelay = 0.05f;
	private const float CameraManualEngageSmoothTime = 0.08f;
	private const float CameraManualReturnSmoothTime = 0.48f;
	private const float CameraPitchMin = -22f;
	private const float CameraPitchMax = 86f;
	private const float CameraMouseYawScale = 1.2f;
	private const float CameraMousePitchScale = 0.8f;
	private const float CameraCollisionPadding = 0.36f;
	private const float CameraCollisionRadius = 0.2f;
	private const float CameraMinDistance = 0.6f;
	private const float CameraFovSharpness = 10f;
	private const float MinDismountCameraLerp = 0.3f;
	private const float MinDismountFovLerp = 0.24f;
	private const float MinDismountResidualSpeed = 0.65f;
	private const float DismountResidualForcePerSpeed = 16f;
	private const float DismountResidualMinForce = 24f;
	private const float DismountResidualMaxForce = 155f;
	private const float DismountResidualDurationBase = 0.09f;
	private const float DismountResidualDurationPerSpeed = 0.012f;
	private const float DismountResidualDurationMin = 0.11f;
	private const float DismountResidualDurationMax = 0.22f;

	private static readonly Dictionary<int, MountSample> mountSamples = new();
	private static float sprintCarryUntil = -100f;
	private static TransitionPhase transitionPhase = TransitionPhase.None;
	private static float transitionUntil = -100f;
	private static bool hasDismountContext;
	private static DismountContext dismountContext;
	private static CustomCameraState? activeCameraState;
	private static int cameraCollisionMask = -1;

	internal static void Initialize()
	{
		mountSamples.Clear();
		sprintCarryUntil = -100f;
		transitionPhase = TransitionPhase.None;
		transitionUntil = -100f;
		hasDismountContext = false;
		dismountContext = default;
		ClearCustomCameraState(restoreVisibilityForMountState: true);
		cameraCollisionMask = -1;
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

	internal static bool BeginCustomSkateboardCamera(SkateboardCamera skateboardCamera, Skateboard skateboard)
	{
		if (skateboardCamera == null || skateboard == null)
		{
			return false;
		}

		PlayerCamera playerCamera = GetPlayerCamera();
		if (playerCamera == null || playerCamera.transform == null)
		{
			return false;
		}

		Transform originTransform = skateboardCamera.cameraOrigin;
		if (originTransform == null)
		{
			return false;
		}

		activeCameraState = new CustomCameraState(skateboardCamera, skateboard, playerCamera);
		Vector3 boardForward = NormalizeOrZero(Flatten(skateboard.transform.forward));
		if (boardForward.sqrMagnitude <= 0.0001f)
		{
			boardForward = Vector3.forward;
		}
		activeCameraState.SmoothedForward = boardForward;

		Vector3 autoOffset = ComputeAutoOffset(skateboardCamera, activeCameraState.SmoothedForward);
		activeCameraState.Distance = Mathf.Max(autoOffset.magnitude, CameraMinDistance);
		activeCameraState.DistanceCurrent = 0.02f;
		activeCameraState.DistanceVelocity = 0f;
		Vector3 euler = playerCamera.transform.rotation.eulerAngles;
		float pitch = euler.x;
		if (pitch > 180f)
		{
			pitch -= 360f;
		}
		activeCameraState.OrbitYaw = euler.y;
		activeCameraState.OrbitPitch = Mathf.Clamp(pitch, CameraPitchMin, CameraPitchMax);
		activeCameraState.OrbitYawVelocity = 0f;
		activeCameraState.OrbitPitchVelocity = 0f;
		HideLocalBodyDuringMountPullout(activeCameraState);
		return true;
	}

	internal static bool HasCustomSkateboardCamera(SkateboardCamera skateboardCamera)
	{
		if (activeCameraState == null || skateboardCamera == null)
		{
			return false;
		}

		return activeCameraState.CameraComponent == skateboardCamera;
	}

	internal static bool RunCustomSkateboardCamera(SkateboardCamera skateboardCamera)
	{
		if (!HasCustomSkateboardCamera(skateboardCamera) || activeCameraState == null)
		{
			return false;
		}

		PlayerCamera playerCamera = GetPlayerCamera();
		Transform originTransform = skateboardCamera.cameraOrigin;
		if (playerCamera == null || playerCamera.transform == null || originTransform == null)
		{
			ClearCustomCameraState(restoreVisibilityForMountState: true);
			return false;
		}
		if (activeCameraState.Board == null || !activeCameraState.Board.isActiveAndEnabled)
		{
			ClearCustomCameraState(restoreVisibilityForMountState: true);
			return false;
		}
		if (activeCameraState.LocalBodyHidden)
		{
			SetLocalBodyVisibility(visible: false);
		}

		float dt = Mathf.Max(Time.deltaTime, 0.0001f);
		Vector3 origin = originTransform.position;
		Vector3 boardForward = NormalizeOrZero(Flatten(activeCameraState.Board.transform.forward));
		if (boardForward.sqrMagnitude <= 0.0001f)
		{
			boardForward = activeCameraState.SmoothedForward;
		}
		if (boardForward.sqrMagnitude <= 0.0001f)
		{
			boardForward = Vector3.forward;
		}
		float forwardT = 1f - Mathf.Exp(0f - CameraForwardSharpness * dt);
		activeCameraState.SmoothedForward = NormalizeOrZero(Vector3.Slerp(activeCameraState.SmoothedForward, boardForward, forwardT));
		if (activeCameraState.SmoothedForward.sqrMagnitude <= 0.0001f)
		{
			activeCameraState.SmoothedForward = boardForward;
		}
		Vector3 autoOffset = ComputeAutoOffset(skateboardCamera, activeCameraState.SmoothedForward);
		activeCameraState.DistanceCurrent = Mathf.SmoothDamp(activeCameraState.DistanceCurrent, activeCameraState.Distance, ref activeCameraState.DistanceVelocity, CameraDistanceSmoothTime, Mathf.Infinity, dt);
		float distanceScale = (activeCameraState.Distance > 0.001f) ? activeCameraState.DistanceCurrent / activeCameraState.Distance : 1f;
		Vector3 cameraAnchor = skateboardCamera.transform != null ? skateboardCamera.transform.position : activeCameraState.Board.transform.position;
		Vector3 autoPosition = cameraAnchor + autoOffset * distanceScale;
		autoPosition = ResolveCameraCollision(origin, autoPosition);
		Vector3 desiredPosition;
		Quaternion desiredRotation;

		if (activeCameraState.MountBlend < 1f)
		{
			activeCameraState.MountBlend = Mathf.MoveTowards(activeCameraState.MountBlend, 1f, dt / CameraMountBlendDuration);
			desiredPosition = autoPosition;
			desiredRotation = activeCameraState.MountStartRotation;
		}
		else
		{
			Vector3 autoDirection = NormalizeOrZero(autoOffset);
			if (autoDirection.sqrMagnitude <= 0.0001f)
			{
				autoDirection = Vector3.back;
			}
			float autoYaw = DirectionToYaw(autoDirection);
			float autoPitch = Mathf.Clamp(DirectionToPitch(autoDirection), CameraPitchMin, CameraPitchMax);

			Vector2 mouseDelta = GameInput.MouseDelta;
			bool requiresSecondary = NeedsSecondaryClick();
			bool secondaryHeld = GameInput.GetButton(GameInput.ButtonCode.SecondaryClick);
			bool hasMouseInput = mouseDelta.sqrMagnitude > 0.000001f;
			bool manualControlActive = requiresSecondary ? secondaryHeld : hasMouseInput;
			bool manualLookInput = manualControlActive && hasMouseInput;
			float lookSensitivity = GetLookSensitivity();

			if (manualControlActive)
			{
				if (manualLookInput)
				{
					activeCameraState.OrbitYaw += mouseDelta.x * CameraMouseYawScale * lookSensitivity;
					activeCameraState.OrbitPitch = Mathf.Clamp(activeCameraState.OrbitPitch - mouseDelta.y * CameraMousePitchScale * lookSensitivity, CameraPitchMin, CameraPitchMax);
				}
				activeCameraState.TimeSinceManualInput = 0f;
				activeCameraState.ManualWeight = Mathf.SmoothDamp(activeCameraState.ManualWeight, 1f, ref activeCameraState.ManualWeightVelocity, CameraManualEngageSmoothTime, Mathf.Infinity, dt);
			}
			else
			{
				activeCameraState.TimeSinceManualInput += dt;
				if (activeCameraState.TimeSinceManualInput > CameraManualReleaseDelay)
				{
					activeCameraState.ManualWeight = Mathf.SmoothDamp(activeCameraState.ManualWeight, 0f, ref activeCameraState.ManualWeightVelocity, CameraManualReturnSmoothTime, Mathf.Infinity, dt);
				}
				activeCameraState.OrbitYaw = Mathf.SmoothDampAngle(activeCameraState.OrbitYaw, autoYaw, ref activeCameraState.OrbitYawVelocity, CameraAutoYawSmoothTime, Mathf.Infinity, dt);
				activeCameraState.OrbitPitch = Mathf.SmoothDamp(activeCameraState.OrbitPitch, autoPitch, ref activeCameraState.OrbitPitchVelocity, CameraAutoPitchSmoothTime, Mathf.Infinity, dt);
			}

			if (GameInput.GetButtonDown(GameInput.ButtonCode.VehicleResetCamera))
			{
				activeCameraState.ManualWeight = 0f;
				activeCameraState.ManualWeightVelocity = 0f;
				activeCameraState.TimeSinceManualInput = 100f;
				activeCameraState.OrbitYaw = autoYaw;
				activeCameraState.OrbitPitch = autoPitch;
				activeCameraState.OrbitYawVelocity = 0f;
				activeCameraState.OrbitPitchVelocity = 0f;
			}

			Quaternion orbitRotation = Quaternion.Euler(activeCameraState.OrbitPitch, activeCameraState.OrbitYaw, 0f);
			Vector3 manualPosition = origin + orbitRotation * Vector3.back * activeCameraState.DistanceCurrent;
			desiredPosition = Vector3.Lerp(autoPosition, manualPosition, activeCameraState.ManualWeight);
			desiredPosition = ResolveCameraCollision(origin, desiredPosition);
			desiredRotation = Quaternion.LookRotation(origin - desiredPosition, Vector3.up);
		}

		if (activeCameraState.LocalBodyHidden &&
		    Time.timeSinceLevelLoad >= activeCameraState.HideBodyUntilTime &&
		    (activeCameraState.MountBlend >= 0.35f || activeCameraState.DistanceCurrent >= activeCameraState.Distance * 0.3f))
		{
			SetLocalBodyVisibility(visible: true);
			activeCameraState.LocalBodyHidden = false;
		}

		float posT = 1f - Mathf.Exp(0f - CameraPositionSharpness * dt);
		float rotT = 1f - Mathf.Exp(0f - CameraRotationSharpness * dt);
		activeCameraState.CurrentPosition = Vector3.Lerp(activeCameraState.CurrentPosition, desiredPosition, posT);
		activeCameraState.CurrentRotation = Quaternion.Slerp(activeCameraState.CurrentRotation, desiredRotation, rotT);
		playerCamera.transform.position = activeCameraState.CurrentPosition;
		playerCamera.transform.rotation = activeCameraState.CurrentRotation;

		float speed01 = 0f;
		if (activeCameraState.Board.Rb != null)
		{
			speed01 = Mathf.Clamp01(activeCameraState.Board.Rb.velocity.magnitude / Mathf.Max(activeCameraState.Board.TopSpeed_Ms, 0.1f));
		}
		float targetFovMultiplier = Mathf.Lerp(skateboardCamera.FOVMultiplier_MinSpeed, skateboardCamera.FOVMultiplier_MaxSpeed, speed01);
		float fovT = 1f - Mathf.Exp(0f - Mathf.Max(CameraFovSharpness, skateboardCamera.FOVMultiplierChangeRate * 4f) * dt);
		activeCameraState.CurrentFovMultiplier = Mathf.Lerp(activeCameraState.CurrentFovMultiplier, targetFovMultiplier, fovT);
		if (playerCamera.Camera != null)
		{
			playerCamera.Camera.fieldOfView = activeCameraState.BaseFov * activeCameraState.CurrentFovMultiplier;
		}

		return true;
	}

	internal static void NotifySkateboardCameraDestroyed(SkateboardCamera skateboardCamera)
	{
		if (!HasCustomSkateboardCamera(skateboardCamera))
		{
			return;
		}

		ClearCustomCameraState(restoreVisibilityForMountState: true);
	}

	internal static void NotifyPlayerMountCompleted()
	{
		if (activeCameraState == null || !activeCameraState.LocalBodyHidden)
		{
			return;
		}

		SetLocalBodyVisibility(visible: false);
	}

	internal static void BeginDismount(Skateboard_Equippable equippable)
	{
		transitionPhase = TransitionPhase.Dismount;
		transitionUntil = Time.timeSinceLevelLoad + TransitionWindow;
		ClearCustomCameraState(restoreVisibilityForMountState: false);

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

	private static void HideLocalBodyDuringMountPullout(CustomCameraState state)
	{
		if (state == null)
		{
			return;
		}

		SetLocalBodyVisibility(visible: false);
		state.LocalBodyHidden = true;
		state.HideBodyUntilTime = Time.timeSinceLevelLoad + CameraMountHideBodyDuration;
	}

	private static void ClearCustomCameraState(bool restoreVisibilityForMountState)
	{
		if (activeCameraState != null && activeCameraState.LocalBodyHidden && restoreVisibilityForMountState)
		{
			SetLocalBodyVisibility(visible: true);
			activeCameraState.LocalBodyHidden = false;
		}

		activeCameraState = null;
	}

	private static void SetLocalBodyVisibility(bool visible)
	{
		Player local = Player.Local;
		if (local == null)
		{
			return;
		}

		local.SetVisibleToLocalPlayer(visible);
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

	private static Vector3 ComputeAutoOffset(SkateboardCamera skateboardCamera, Vector3 boardForward)
	{
		boardForward = NormalizeOrZero(boardForward);
		if (boardForward.sqrMagnitude <= 0.0001f)
		{
			boardForward = Vector3.forward;
		}
		Vector3 offset = boardForward * skateboardCamera.HorizontalOffset + Vector3.up * skateboardCamera.VerticalOffset;
		if (offset.sqrMagnitude <= 0.04f)
		{
			offset = boardForward * -2.4f + Vector3.up * 1.8f;
		}

		return offset;
	}

	private static bool NeedsSecondaryClick()
	{
		return GameInput.CurrentInputDevice == GameInput.InputDeviceType.KeyboardMouse;
	}

	private static float GetLookSensitivity()
	{
#if IL2CPP
		Il2CppScheduleOne.DevUtilities.Settings settings = Il2CppScheduleOne.DevUtilities.Singleton<Il2CppScheduleOne.DevUtilities.Settings>.Instance;
#else
		ScheduleOne.DevUtilities.Settings settings = ScheduleOne.DevUtilities.Singleton<ScheduleOne.DevUtilities.Settings>.Instance;
#endif
		if (settings == null)
		{
			return 1f;
		}

		return settings.LookSensitivity;
	}

	private static float DirectionToYaw(Vector3 direction)
	{
		if (direction.sqrMagnitude <= 0.0001f)
		{
			return 0f;
		}

		return Mathf.Atan2(direction.x, direction.z) * 57.29578f;
	}

	private static float DirectionToPitch(Vector3 direction)
	{
		if (direction.sqrMagnitude <= 0.0001f)
		{
			return 0f;
		}

		return Mathf.Asin(Mathf.Clamp(direction.y, -1f, 1f)) * 57.29578f;
	}

	private static Vector3 ResolveCameraCollision(Vector3 origin, Vector3 desiredPosition)
	{
		Vector3 delta = desiredPosition - origin;
		float distance = delta.magnitude;
		if (distance <= CameraMinDistance)
		{
			Vector3 fallbackDir = NormalizeOrZero(delta);
			if (fallbackDir.sqrMagnitude <= 0.0001f)
			{
				fallbackDir = Vector3.back;
			}
			return origin + fallbackDir * CameraMinDistance;
		}

		Vector3 direction = delta / distance;
		if (Physics.SphereCast(origin, CameraCollisionRadius, direction, out RaycastHit hit, distance, GetCameraCollisionMask(), QueryTriggerInteraction.Ignore))
		{
			float safeDistance = Mathf.Max(CameraMinDistance, hit.distance - CameraCollisionPadding);
			return origin + direction * safeDistance;
		}

		return desiredPosition;
	}

	private static int GetCameraCollisionMask()
	{
		if (cameraCollisionMask != -1)
		{
			return cameraCollisionMask;
		}

		int mask = 0;
		int defaultLayer = LayerMask.NameToLayer("Default");
		int terrainLayer = LayerMask.NameToLayer("Terrain");
		if (defaultLayer >= 0)
		{
			mask |= 1 << defaultLayer;
		}
		if (terrainLayer >= 0)
		{
			mask |= 1 << terrainLayer;
		}
		if (mask == 0)
		{
			mask = Physics.DefaultRaycastLayers;
		}

		cameraCollisionMask = mask;
		return cameraCollisionMask;
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
