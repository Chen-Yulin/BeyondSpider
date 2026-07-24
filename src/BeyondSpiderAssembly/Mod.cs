using System;
using Modding;
using Modding.Mapper;
using UnityEngine;

namespace BeyondSpiderAssembly
{
	public class Mod : ModEntryPoint
	{
		public static GameObject Root;

		public override void OnLoad()
		{
			CustomMapperTypes.AddMapperType<string, MInfo, InfoSelector>();
			CustomMapperTypes.AddMapperType<int, MFireChannel, FireChannelSelector>();
			Root = new GameObject("BeyondSpider Space Combat");
			UnityEngine.Object.DontDestroyOnLoad(Root);
			Root.AddComponent<SpaceCombatRuntime>();
			Root.AddComponent<SpaceBoundary>();
			Root.AddComponent<HostControlNet>();
			Root.AddComponent<HostControlPanel>();
			Root.AddComponent<ShipPoseNet>();
			Root.AddComponent<SpaceFlakTurretNet>();
			Root.AddComponent<SpaceGunnerNet>();
			Root.AddComponent<SpaceGunnerHingeController>();
			Root.AddComponent<NanoArmorController>();
			Root.AddComponent<CaptainLockNet>();
			Root.AddComponent<MissileLauncherNet>();
			Root.AddComponent<RailgunNet>();
			Root.AddComponent<LaserNet>();
			Root.AddComponent<NanoArmorNet>();
			Root.AddComponent<ShieldNet>();
			Root.AddComponent<ShipEnergyNet>();
			Root.AddComponent<SubsystemDetonationNet>();
			Root.AddComponent<ThrusterNet>();
			Root.AddComponent<CaptainRadarView>();
			Root.AddComponent<BeyondSpiderInfoPanel>();
			ModNetworking.Callbacks[SpaceFlakTurretNet.ActiveMsg] += SpaceFlakTurretNet.Instance.ActiveReceiver;
			ModNetworking.Callbacks[SpaceFlakTurretNet.StateMsg] += SpaceFlakTurretNet.Instance.StateReceiver;
			ModNetworking.Callbacks[SpaceFlakTurretNet.ShotMsg] += SpaceFlakTurretNet.Instance.ShotReceiver;
			ModNetworking.Callbacks[SpaceGunnerNet.ActiveMsg] += SpaceGunnerNet.Instance.ActiveReceiver;
			ModNetworking.Callbacks[CaptainLockNet.LockMsg] += CaptainLockNet.Instance.LockReceiver;
			ModNetworking.Callbacks[CaptainLockNet.FilterMsg] += CaptainLockNet.Instance.FilterReceiver;
			ModNetworking.Callbacks[CaptainLockNet.Channel0ListMsg] += CaptainLockNet.Instance.Channel0ListReceiver;
			ModNetworking.Callbacks[MissileLauncherNet.SpawnMsg] += MissileLauncherNet.Instance.SpawnReceiver;
			ModNetworking.Callbacks[MissileLauncherNet.StateBatchMsg] += MissileLauncherNet.Instance.StateBatchReceiver;
			ModNetworking.Callbacks[MissileLauncherNet.DetonateMsg] += MissileLauncherNet.Instance.DetonateReceiver;
			ModNetworking.Callbacks[RailgunNet.ShotMsg] += RailgunNet.Instance.ShotReceiver;
			ModNetworking.Callbacks[LaserNet.FireMsg] += LaserNet.Instance.FireReceiver;
			ModNetworking.Callbacks[NanoArmorNet.BatchMsg] += NanoArmorNet.Instance.BatchReceiver;
			ModNetworking.Callbacks[ShieldNet.StateMsg] += ShieldNet.Instance.StateReceiver;
			ModNetworking.Callbacks[ShipEnergyNet.EnergyMsg] += ShipEnergyNet.Instance.EnergyReceiver;
			ModNetworking.Callbacks[SubsystemDetonationNet.DetonateMsg] += SubsystemDetonationNet.Instance.DetonateReceiver;
			ModNetworking.Callbacks[ThrusterNet.DriveMsg] += ThrusterNet.Instance.DriveReceiver;
			ModNetworking.Callbacks[ThrusterNet.KeyMsg] += ThrusterNet.Instance.KeyReceiver;
			ModNetworking.Callbacks[ThrusterNet.OutputMsg] += ThrusterNet.Instance.OutputReceiver;
			ModNetworking.Callbacks[HostControlNet.StateMsg] += HostControlNet.Instance.StateReceiver;
			ModNetworking.Callbacks[ShipPoseNet.PoseMsg] += ShipPoseNet.Instance.PoseReceiver;
			Debug.Log("BeyondSpider Space Combat loaded.");
		}
	}
}
