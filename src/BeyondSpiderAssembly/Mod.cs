using System;
using Modding;
using UnityEngine;

namespace BeyondSpiderAssembly
{
	public class Mod : ModEntryPoint
	{
		public static GameObject Root;

		public override void OnLoad()
		{
			Root = new GameObject("BeyondSpider Space Combat");
			UnityEngine.Object.DontDestroyOnLoad(Root);
			Root.AddComponent<SpaceCombatRuntime>();
			Root.AddComponent<SpaceFlakTurretNet>();
			Root.AddComponent<NanoArmorController>();
			ModNetworking.Callbacks[SpaceFlakTurretNet.ActiveMsg] += SpaceFlakTurretNet.Instance.ActiveReceiver;
			ModNetworking.Callbacks[SpaceFlakTurretNet.StateMsg] += SpaceFlakTurretNet.Instance.StateReceiver;
			ModNetworking.Callbacks[SpaceFlakTurretNet.ShotMsg] += SpaceFlakTurretNet.Instance.ShotReceiver;
			Debug.Log("BeyondSpider Space Combat loaded.");
		}
	}
}
