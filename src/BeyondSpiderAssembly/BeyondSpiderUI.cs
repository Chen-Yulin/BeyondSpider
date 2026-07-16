using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace BeyondSpiderAssembly
{
    // Shared procedural uGUI construction helpers for BeyondSpider's HUD panels (the left ship-status
    // panel in BeyondSpiderInfoPanel.cs, and the radar's collapse dock in CaptainRadarView.cs). Ports
    // the dark/translucent Besiege-native panel look and the collapsible-header pattern directly into
    // plain UnityEngine.UI calls -- no dependency on any other mod's assemblies, and no serialized
    // layout file to load; every element is built by code, once, at startup.
    public static class BeyondSpiderUI
    {
        public static readonly Color PanelColor = new Color(0.0235f, 0.0235f, 0.0549f, 0.53f);
        public static readonly Color PanelColorOpaque = new Color(0.0235f, 0.0235f, 0.0549f, 0.92f);
        public static readonly Color BarBackgroundColor = new Color(0.0157f, 0.0157f, 0.0235f, 0.6f);
        public static readonly Color AccentColor = new Color(0.3f, 0.85f, 0.95f, 0.95f);
        public static readonly Color TextColor = new Color(0.85f, 0.95f, 1f, 1f);

        private static Transform rootCanvas;
        private static Sprite whiteSprite;

        // Always builds BeyondSpider's own dedicated ScreenSpaceOverlay canvas rather than trying to
        // find and parent into one of Besiege's own -- an earlier version searched for a "HUD"
        // GameObject / used FindObjectOfType<Canvas>() to inherit the game's own canvas, but Besiege
        // has multiple canvases in the scene and there's no reliable way to identify "the" full-screen
        // one from outside, so anchors ended up relative to the wrong parent (panels not actually
        // docked to the screen edges). A self-built ScreenSpaceOverlay canvas is always exactly
        // Screen.width x Screen.height, so anchoring to its corners is unambiguous. Parented under
        // Mod.Root so it shares that GameObject's DontDestroyOnLoad lifetime instead of needing its
        // own. Safe to call from multiple components -- whichever calls first wins, everyone else
        // reuses the same cached transform.
        public static Transform GetOrCreateRootCanvas()
        {
            if (rootCanvas != null)
            {
                return rootCanvas;
            }

            GameObject container = new GameObject("BeyondSpider UI Canvas");
            container.transform.SetParent(Mod.Root.transform, false);
            Canvas canvas = container.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 100;
            CanvasScaler scaler = container.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
            container.AddComponent<GraphicRaycaster>();

            // Always create our own, rather than only when Object.FindObjectOfType<EventSystem>()
            // finds none: Besiege's own simulation-mode input (MouseOrbit etc., see CaptainRadarView's
            // PauseGameCameraInput) is custom/non-uGUI, so there's no guarantee any EventSystem Besiege
            // itself has is active/appropriate during simulation -- clicks on our Toggles silently
            // doing nothing was traced to exactly this (skipped creating one because some other
            // EventSystem existed at the moment GetOrCreateRootCanvas() first ran, which then wasn't
            // reliably servicing our canvas). Unity tolerates multiple EventSystems in a scene (only
            // one is ever "current" at a time); a harmless duplicate is a much smaller risk than ours
            // never getting created.
            GameObject eventSystemObject = new GameObject("BeyondSpider EventSystem");
            eventSystemObject.transform.SetParent(Mod.Root.transform, false);
            eventSystemObject.AddComponent<EventSystem>();
            eventSystemObject.AddComponent<StandaloneInputModule>();

            rootCanvas = container.transform;
            return rootCanvas;
        }

        // A real 1x1 white Sprite, not just a null Image.sprite -- Image.Type.Filled's fill-mesh
        // generation on the version of UnityEngine.UI Besiege ships doesn't reliably honor fillAmount
        // without an actual sprite (bars rendered solid/full regardless of fillAmount otherwise).
        private static Sprite GetWhiteSprite()
        {
            if (whiteSprite == null)
            {
                Texture2D texture = new Texture2D(4, 4, TextureFormat.ARGB32, false);
                Color32[] pixels = new Color32[16];
                for (int i = 0; i < pixels.Length; i++)
                {
                    pixels[i] = new Color32(255, 255, 255, 255);
                }
                texture.SetPixels32(pixels);
                texture.Apply();
                whiteSprite = Sprite.Create(texture, new Rect(0f, 0f, 4f, 4f), new Vector2(0.5f, 0.5f), 4f);
            }
            return whiteSprite;
        }

        public static RectTransform CreateRect(Transform parent, string name)
        {
            GameObject go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return (RectTransform)go.transform;
        }

        public static Image CreatePanel(Transform parent, string name, Color color)
        {
            RectTransform rect = CreateRect(parent, name);
            Image image = rect.gameObject.AddComponent<Image>();
            image.sprite = GetWhiteSprite();
            image.color = color;
            image.type = Image.Type.Simple;
            return image;
        }

        public static Text CreateLabel(Transform parent, string name, string text, int fontSize, TextAnchor alignment)
        {
            RectTransform rect = CreateRect(parent, name);
            Text label = rect.gameObject.AddComponent<Text>();
            label.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            label.fontSize = fontSize;
            label.alignment = alignment;
            label.color = TextColor;
            label.text = text;
            label.horizontalOverflow = HorizontalWrapMode.Overflow;
            label.verticalOverflow = VerticalWrapMode.Overflow;
            label.raycastTarget = false;
            return label;
        }

        // Dim background + a bright foreground Image.Type.Filled bar; caller drives the returned
        // Image's fillAmount each frame. No Slider is used -- a read-only meter has no handle to drag.
        public static Image CreateBarMeter(Transform parent, string name, Color fillColor)
        {
            Image background = CreatePanel(parent, name + "Background", BarBackgroundColor);
            background.raycastTarget = false;

            RectTransform fillRect = CreateRect(background.transform, name + "Fill");
            fillRect.anchorMin = Vector2.zero;
            fillRect.anchorMax = Vector2.one;
            fillRect.offsetMin = Vector2.zero;
            fillRect.offsetMax = Vector2.zero;

            Image fill = fillRect.gameObject.AddComponent<Image>();
            fill.sprite = GetWhiteSprite();
            fill.color = fillColor;
            fill.type = Image.Type.Filled;
            fill.fillMethod = Image.FillMethod.Horizontal;
            fill.fillOrigin = (int)Image.OriginHorizontal.Left;
            fill.fillAmount = 0f;
            fill.raycastTarget = false;
            return fill;
        }

        // A clickable header row: a full-rect Toggle (targetGraphic gets Unity's normal built-in
        // hover/press color tinting for free) with a centered title label. titleText.text is the
        // caller's responsibility to update with a collapse/expand glyph.
        public static Toggle CreateHeaderToggle(Transform parent, string name, string title, out Text titleText)
        {
            Image background = CreatePanel(parent, name, PanelColorOpaque);
            Toggle toggle = background.gameObject.AddComponent<Toggle>();
            toggle.targetGraphic = background;
            toggle.isOn = true;

            titleText = CreateLabel(background.transform, name + "Text", title, 14, TextAnchor.MiddleCenter);
            RectTransform textRect = titleText.rectTransform;
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = Vector2.zero;
            textRect.offsetMax = Vector2.zero;

            return toggle;
        }

        public static VerticalLayoutGroup AddVerticalLayout(GameObject go, int padding, float spacing)
        {
            // This old bundled UnityEngine.UI has no childControlWidth/childControlHeight (added in a
            // later Unity UI release than the one Besiege ships) -- HorizontalOrVerticalLayoutGroup
            // here always sizes children from their own min/preferred/flexible size, so LayoutElement
            // on each row is all that's needed; childForceExpand only governs leftover space.
            VerticalLayoutGroup layout = go.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(padding, padding, padding, padding);
            layout.spacing = spacing;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            return layout;
        }

        public static ContentSizeFitter AddAutoHeight(GameObject go)
        {
            ContentSizeFitter fitter = go.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            return fitter;
        }

        public static LayoutElement SetRowHeight(GameObject go, float height)
        {
            LayoutElement element = go.AddComponent<LayoutElement>();
            element.preferredHeight = height;
            element.minHeight = height;
            return element;
        }
    }
}
