using Modding.Mapper;
using UnityEngine;

namespace BeyondSpiderAssembly
{
    // 火控通道选择 mapper: four channel buttons laid out horizontally in the wrench menu, one
    // per fire channel, each tinted with its channel color (see FireChannels.Colors). Value is
    // a bitmask (bit c = channel c enabled), so a weapon may subscribe to any combination —
    // effectively four MToggles in a row, built on the same MCustom<T>/CustomSelector extension
    // point as MInfo (see MInfo.cs and the agent guide's "扳手菜单里的只读信息：MInfo" section
    // for the full mechanism; unlike MInfo this one is interactive and persisted to the save).
    public sealed class MFireChannel : MCustom<int>
    {
        public MFireChannel(string displayName, string key, int defaultMask)
            : base(displayName, key, defaultMask & FireChannels.AllMask)
        {
        }

        public bool IsChannelEnabled(int channel)
        {
            return FireChannels.Contains(Value, channel);
        }

        public void ToggleChannel(int channel)
        {
            Value = (Value ^ (1 << channel)) & FireChannels.AllMask;
        }

        public override XData SerializeValue(int value)
        {
            return new XInteger(SerializationKey, value);
        }

        public override int DeSerializeValue(XData data)
        {
            XInteger stored = data as XInteger;
            return stored != null ? stored.Value & FireChannels.AllMask : FireChannels.AllMask;
        }
    }

    public sealed class FireChannelSelector : CustomSelector<int, MFireChannel>
    {
        private const float ButtonSpacing = 0.4f;
        private const float ButtonRowY = -0.2f;
        private const float LabelRowY = 0.0f;
        private static readonly Vector2 ButtonSize = new Vector2(0.32f, 0.28f);

        private readonly MeshRenderer[] buttons = new MeshRenderer[FireChannels.Count];

        // Solid-color button textures, generated once and shared by every selector instance:
        // enabled = the channel's full color, disabled = the same hue dimmed nearly to black.
        // Textures (not tinted materials) because Elements.MakeTexture keeps the wrench UI's
        // own template material and only swaps mainTexture — no assumption about its shader
        // having a settable _Color.
        private static readonly Texture2D[] onTextures = new Texture2D[FireChannels.Count];
        private static readonly Texture2D[] offTextures = new Texture2D[FireChannels.Count];

        private static Texture2D ButtonTexture(int channel, bool enabled)
        {
            Texture2D[] cache = enabled ? onTextures : offTextures;
            if (cache[channel] == null)
            {
                Color color = FireChannels.Colors[channel] * (enabled ? 1f : 0.18f);
                color.a = 1f;
                Texture2D texture = new Texture2D(2, 2, TextureFormat.ARGB32, false);
                for (int y = 0; y < 2; y++)
                {
                    for (int x = 0; x < 2; x++)
                    {
                        texture.SetPixel(x, y, color);
                    }
                }
                texture.Apply();
                cache[channel] = texture;
            }
            return cache[channel];
        }

        protected override void CreateInterface()
        {
            Elements.MakeText(new Vector3(0f, 0.18f, 0f), CustomMapperType.DisplayName.ToUpper(), 0.13f);
            for (int i = 0; i < FireChannels.Count; i++)
            {
                // Copy the loop variable before capturing it — the Click closure below must
                // bind its own channel index, not the shared post-loop value.
                int channel = i;
                float x = (channel - (FireChannels.Count - 1) * 0.5f) * ButtonSpacing;
                Elements.MakeText(new Vector3(x, LabelRowY, 0f), channel.ToString(), 0.1f);
                buttons[channel] = Elements.MakeTexture(new Vector3(x, ButtonRowY, 0f), ButtonSize, ButtonTexture(channel, false));
                UIButton button = Elements.AddButton(buttons[channel].transform);
                button.Click += delegate { CustomMapperType.ToggleChannel(channel); };
            }
            UpdateInterface();
        }

        protected override void UpdateInterface()
        {
            for (int channel = 0; channel < FireChannels.Count; channel++)
            {
                if (buttons[channel] != null)
                {
                    buttons[channel].material.mainTexture = ButtonTexture(channel, CustomMapperType.IsChannelEnabled(channel));
                }
            }
        }
    }
}
