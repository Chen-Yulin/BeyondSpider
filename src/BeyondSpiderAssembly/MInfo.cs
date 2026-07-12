using Modding.Mapper;
using UnityEngine;

namespace BeyondSpiderAssembly
{
    // Besiege's wrench menu has no built-in read-only widget -- AddKey/AddSlider/AddToggle/AddText/AddMenu/
    // AddLimits are all player-input controls. The base game does expose a real extension point for a custom
    // one, though (confirmed by decompiling Assembly-CSharp.dll and cross-checking against the BlockTransformValues
    // mod's MTransform/TransformSelector, which use this same mechanism to show a block's transform in the
    // wrench menu): a MapperType subclass that carries no player-facing control (Modding.Mapper.MCustom<T>),
    // paired with a Selector subclass that builds the widget's visuals (Modding.Mapper.CustomSelector<T,TMapper>),
    // registered once at mod load via Modding.Mapper.CustomMapperTypes.AddMapperType<T,TMapper,TSelector>().
    //
    // MInfo.Value's setter (inherited from MCustom<T>) fires the Changed event, which CustomSelector.Init()
    // subscribes to -- so setting .Value (or calling Set(), which skips the redundant fire when unchanged)
    // updates the on-screen label live while the wrench panel is open, and is a harmless no-op while it's
    // closed (no Selector exists yet to receive the event), same as the other M-types' SetValue().
    public sealed class MInfo : MCustom<string>
    {
        public MInfo(string displayName, string key, string initialValue = "")
            : base(displayName, key, initialValue)
        {
        }

        public void Set(string value)
        {
            if (Value != value)
            {
                Value = value;
            }
        }

        public override XData SerializeValue(string value)
        {
            return new XString(SerializationKey, "");
        }

        public override string DeSerializeValue(XData data)
        {
            return "";
        }
    }

    public sealed class InfoSelector : CustomSelector<string, MInfo>
    {
        private DynamicText valueLabel;

        protected override void CreateInterface()
        {
            Elements.MakeText(new Vector3(0f, 0.18f, 0f), CustomMapperType.DisplayName.ToUpper(), 0.13f);
            valueLabel = Elements.MakeText(new Vector3(0f, -0.12f, 0f), CustomMapperType.Value, 0.16f);
            UpdateInterface();
        }

        protected override void UpdateInterface()
        {
            valueLabel.SetText(CustomMapperType.Value);
        }
    }
}
