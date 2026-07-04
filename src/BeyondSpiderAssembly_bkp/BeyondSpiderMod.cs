using Modding;
using UnityEngine;

namespace BeyondSpiderAssembly
{
    public class BeyondSpiderMod : ModEntryPoint
    {
        public static GameObject Root;

        public override void OnLoad()
        {
            Root = new GameObject("BeyondSpider Space Combat");
            Object.DontDestroyOnLoad(Root);
            Root.AddComponent<SpaceCombatRuntime>();
            Debug.Log("BeyondSpider Space Combat loaded.");
        }
    }
}
