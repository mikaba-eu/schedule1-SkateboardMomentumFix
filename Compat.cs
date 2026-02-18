#if IL2CPP
global using GameInput = Il2CppScheduleOne.GameInput;
global using Player = Il2CppScheduleOne.PlayerScripts.Player;
global using PlayerCamera = Il2CppScheduleOne.PlayerScripts.PlayerCamera;
global using PlayerMovement = Il2CppScheduleOne.PlayerScripts.PlayerMovement;
global using Skateboard = Il2CppScheduleOne.Skating.Skateboard;
global using SkateboardCamera = Il2CppScheduleOne.Skating.SkateboardCamera;
global using Skateboard_Equippable = Il2CppScheduleOne.Skating.Skateboard_Equippable;
global using SmoothedVelocityCalculator = Il2CppScheduleOne.Tools.SmoothedVelocityCalculator;
#else
global using GameInput = ScheduleOne.GameInput;
global using Player = ScheduleOne.PlayerScripts.Player;
global using PlayerCamera = ScheduleOne.PlayerScripts.PlayerCamera;
global using PlayerMovement = ScheduleOne.PlayerScripts.PlayerMovement;
global using Skateboard = ScheduleOne.Skating.Skateboard;
global using SkateboardCamera = ScheduleOne.Skating.SkateboardCamera;
global using Skateboard_Equippable = ScheduleOne.Skating.Skateboard_Equippable;
global using SmoothedVelocityCalculator = ScheduleOne.Tools.SmoothedVelocityCalculator;
#endif
