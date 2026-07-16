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
			Root.AddComponent<SpaceFlakTurretNet>();
			Root.AddComponent<SpaceGunnerNet>();
			Root.AddComponent<SpaceGunnerHingeController>();
			Root.AddComponent<NanoArmorController>();
			Root.AddComponent<CaptainLockNet>();
			Root.AddComponent<MissileLauncherNet>();
			Root.AddComponent<CaptainRadarView>();
			Root.AddComponent<BeyondSpiderInfoPanel>();
			ModNetworking.Callbacks[SpaceFlakTurretNet.ActiveMsg] += SpaceFlakTurretNet.Instance.ActiveReceiver;
			ModNetworking.Callbacks[SpaceFlakTurretNet.StateMsg] += SpaceFlakTurretNet.Instance.StateReceiver;
			ModNetworking.Callbacks[SpaceFlakTurretNet.ShotMsg] += SpaceFlakTurretNet.Instance.ShotReceiver;
			ModNetworking.Callbacks[SpaceGunnerNet.ActiveMsg] += SpaceGunnerNet.Instance.ActiveReceiver;
			ModNetworking.Callbacks[CaptainLockNet.LockMsg] += CaptainLockNet.Instance.LockReceiver;
			ModNetworking.Callbacks[MissileLauncherNet.SpawnMsg] += MissileLauncherNet.Instance.SpawnReceiver;
			Debug.Log("BeyondSpider Space Combat loaded.");
		}
	}
}
