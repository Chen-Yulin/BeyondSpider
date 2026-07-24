using System;
using UnityEngine;
using UnityEngine.UI;

namespace BeyondSpiderAssembly
{
    // Top-left, collapse-to-the-left control panel. On the host (single-player or MP host) it shows the
    // world-override toggles — NO BOUNDARY and DEFOG — that drive SpaceBoundary.Instance and, in MP, get
    // streamed to clients by HostControlNet. On an MP client it turns into a read-only room-info panel
    // showing the host's current settings and the player count, since a client can't set them itself.
    //
    // Built procedurally with the BeyondSpiderUI helpers, same as BeyondSpiderInfoPanel. Collapse slides
    // the body horizontally off the left screen edge (the header stays put as the re-open handle) rather
    // than the info panel's vertical height animation.
    //
    // Labels are ASCII: the panels render with Unity's builtin Arial, which has no CJK glyphs (the same
    // reason the radar/info HUD text is English) — Chinese would come out as boxes.
    public class HostControlPanel : MonoBehaviour
    {
        private const float PanelWidth = 220f;
        private const float HeaderHeight = 26f;
        private const float DockMargin = 20f;
        private const float DockGap = 4f;
        private const float RowHeight = 24f;
        private const float SlideAnimationSeconds = 0.18f;

        private static readonly Color ToggleOnColor = new Color(0.3f, 0.85f, 0.95f, 0.45f);
        private static readonly Color ToggleOffColor = BeyondSpiderUI.BarBackgroundColor;

        public static bool PanelReady { get; private set; }

        private Toggle headerToggle;
        private Text headerText;
        private RectTransform body;

        private bool expanded = true;
        private float dockProgress = 1f;
        private float expandedX;
        private float collapsedX;

        // null until BuildBody runs for the current role; rebuilt when the host/client role changes.
        private bool builtAsHost;
        private bool hasBuiltBody;

        // Host controls.
        private Toggle boundaryToggle;
        private Image boundaryToggleBg;
        private Toggle defogToggle;
        private Image defogToggleBg;

        // Client read-outs.
        private Text roleText;
        private Text boundaryStateText;
        private Text defogStateText;
        private Text playersText;

        private void Start()
        {
            try
            {
                Build();
                PanelReady = true;
            }
            catch (Exception ex)
            {
                PanelReady = false;
                Debug.LogWarning("BeyondSpider: host control panel failed to build. " + ex);
            }
        }

        private void Build()
        {
            Transform canvas = BeyondSpiderUI.GetOrCreateRootCanvas();

            Text titleText;
            headerToggle = BeyondSpiderUI.CreateHeaderToggle(canvas, "BS Control Header", "CONTROL", out titleText);
            headerText = titleText;
            RectTransform headerRect = headerToggle.GetComponent<RectTransform>();
            headerRect.anchorMin = new Vector2(0f, 1f);
            headerRect.anchorMax = new Vector2(0f, 1f);
            headerRect.pivot = new Vector2(0f, 1f);
            headerRect.anchoredPosition = new Vector2(DockMargin, -DockMargin);
            headerRect.sizeDelta = new Vector2(PanelWidth, HeaderHeight);
            headerToggle.onValueChanged.AddListener(OnHeaderToggled);

            Image bodyImage = BeyondSpiderUI.CreatePanel(canvas, "BS Control Panel", BeyondSpiderUI.PanelColor);
            body = bodyImage.rectTransform;
            body.anchorMin = new Vector2(0f, 1f);
            body.anchorMax = new Vector2(0f, 1f);
            body.pivot = new Vector2(0f, 1f);
            expandedX = DockMargin;
            collapsedX = -(PanelWidth + DockMargin + 10f);
            body.anchoredPosition = new Vector2(expandedX, -(DockMargin + HeaderHeight + DockGap));
            body.sizeDelta = new Vector2(PanelWidth, RowHeight);
            BeyondSpiderUI.AddVerticalLayout(body.gameObject, 8, 4f);
            BeyondSpiderUI.AddAutoHeight(body.gameObject);

            RebuildBody(NetAuthority.IsAuthority);

            OnHeaderToggled(headerToggle.isOn);
            SetVisible(false);
        }

        // Rebuilds the body rows for host vs client. Cheap and only fires on a role change.
        private void RebuildBody(bool asHost)
        {
            for (int i = body.childCount - 1; i >= 0; i--)
            {
                Destroy(body.GetChild(i).gameObject);
            }
            boundaryToggle = null;
            defogToggle = null;
            roleText = null;
            boundaryStateText = null;
            defogStateText = null;
            playersText = null;

            if (asHost)
            {
                boundaryToggle = AddToggleRow("NoBoundary", "NO BOUNDARY", out boundaryToggleBg, OnBoundaryToggled);
                defogToggle = AddToggleRow("Defog", "DEFOG", out defogToggleBg, OnDefogToggled);
                SpaceBoundary boundary = SpaceBoundary.Instance;
                if (boundary != null)
                {
                    boundaryToggle.isOn = boundary.BoundaryOff;
                    defogToggle.isOn = boundary.DefogOn;
                }
                headerText.text = HeaderLabel("CONTROL");
            }
            else
            {
                roleText = AddInfoRow("Role", "ROLE: CLIENT");
                boundaryStateText = AddInfoRow("BoundaryState", "NO BOUNDARY: --");
                defogStateText = AddInfoRow("DefogState", "DEFOG: --");
                playersText = AddInfoRow("Players", "PLAYERS: --");
                headerText.text = HeaderLabel("ROOM INFO");
            }

            builtAsHost = asHost;
            hasBuiltBody = true;
        }

        private Toggle AddToggleRow(string name, string label, out Image background, UnityEngine.Events.UnityAction<bool> onChanged)
        {
            Image bg = BeyondSpiderUI.CreatePanel(body, name + "Row", ToggleOffColor);
            BeyondSpiderUI.SetRowHeight(bg.gameObject, RowHeight);
            Toggle toggle = bg.gameObject.AddComponent<Toggle>();
            toggle.targetGraphic = bg;
            // We drive the row colour from isOn every frame (ON = accent, OFF = dim); leave the
            // built-in ColorTint out of it so it doesn't overwrite that on hover/press.
            toggle.transition = Selectable.Transition.None;
            toggle.isOn = false;

            Text text = BeyondSpiderUI.CreateLabel(bg.transform, name + "Label", label, 13, TextAnchor.MiddleCenter);
            RectTransform textRect = text.rectTransform;
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = Vector2.zero;
            textRect.offsetMax = Vector2.zero;

            toggle.onValueChanged.AddListener(onChanged);
            background = bg;
            return toggle;
        }

        private Text AddInfoRow(string name, string initial)
        {
            Text text = BeyondSpiderUI.CreateLabel(body, name, initial, 13, TextAnchor.MiddleLeft);
            BeyondSpiderUI.SetRowHeight(text.gameObject, RowHeight);
            return text;
        }

        private void OnBoundaryToggled(bool value)
        {
            if (SpaceBoundary.Instance != null)
            {
                SpaceBoundary.Instance.BoundaryOff = value;
            }
        }

        private void OnDefogToggled(bool value)
        {
            if (SpaceBoundary.Instance != null)
            {
                SpaceBoundary.Instance.DefogOn = value;
            }
        }

        private void OnHeaderToggled(bool isExpanded)
        {
            expanded = isExpanded;
            headerText.text = HeaderLabel(builtAsHost ? "CONTROL" : "ROOM INFO");
        }

        private string HeaderLabel(string title)
        {
            return (expanded ? "◀ " : "▶ ") + title;
        }

        private void SetVisible(bool visible)
        {
            if (headerToggle != null)
            {
                headerToggle.gameObject.SetActive(visible);
            }
            if (body != null)
            {
                body.gameObject.SetActive(visible);
            }
        }

        // Slides the body horizontally toward expandedX / collapsedX. Off the left screen edge when
        // collapsed, so the screen itself clips it — no mask needed. The header stays put as the handle.
        private void UpdateDockAnimation()
        {
            float target = expanded ? 1f : 0f;
            float step = Time.unscaledDeltaTime / SlideAnimationSeconds;
            dockProgress = Mathf.MoveTowards(dockProgress, target, step);
            float eased = Mathf.SmoothStep(0f, 1f, dockProgress);
            float x = Mathf.Lerp(collapsedX, expandedX, eased);
            body.anchoredPosition = new Vector2(x, body.anchoredPosition.y);
        }

        private void Update()
        {
            if (!PanelReady)
            {
                return;
            }

            bool visible = !StatMaster.isMainMenu;
            SetVisible(visible);
            if (!visible)
            {
                return;
            }

            bool asHost = NetAuthority.IsAuthority;
            if (!hasBuiltBody || asHost != builtAsHost)
            {
                RebuildBody(asHost);
            }

            UpdateDockAnimation();

            SpaceBoundary boundary = SpaceBoundary.Instance;
            if (boundary == null)
            {
                return;
            }

            if (asHost)
            {
                // Host owns the toggles; only recolour to reflect their state (Unity's Toggle has no
                // built-in on/off tint without a checkmark graphic).
                if (boundaryToggle != null)
                {
                    boundaryToggleBg.color = boundaryToggle.isOn ? ToggleOnColor : ToggleOffColor;
                }
                if (defogToggle != null)
                {
                    defogToggleBg.color = defogToggle.isOn ? ToggleOnColor : ToggleOffColor;
                }
            }
            else
            {
                if (roleText != null)
                {
                    roleText.text = "ROLE: CLIENT";
                    boundaryStateText.text = "NO BOUNDARY: " + OnOff(boundary.BoundaryOff);
                    defogStateText.text = "DEFOG: " + OnOff(boundary.DefogOn);
                    playersText.text = "PLAYERS: " + StatMaster.activePlayerCount;
                }
            }
        }

        private static string OnOff(bool value)
        {
            return value ? "ON" : "OFF";
        }
    }
}
