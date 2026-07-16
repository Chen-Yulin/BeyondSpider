using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace BeyondSpiderAssembly
{
    // Left-docked, collapsible ship-status panel -- the Besiege-styled uGUI replacement for
    // SpaceCombatRuntime's old GUILayout debug window. Built procedurally in Start() using the
    // BeyondSpiderUI helpers; if construction throws for any reason, PanelReady stays false and
    // SpaceCombatRuntime.OnGUI (kept, gated on !PanelReady) is the automatic fallback.
    //
    // Docked bottom-left, mirroring CaptainRadarView's bottom-right dock exactly: a header fixed at
    // the corner, and a panel above it whose height animates via sizeDelta (not a nested
    // LayoutElement/ContentSizeFitter chain reacting to a parent layout group) -- the "Rows" child
    // always sizes itself to its own natural content height and sits bottom-anchored inside the
    // panel, so the panel's stencil Mask simply reveals more of it from the bottom up as it grows.
    public class BeyondSpiderInfoPanel : MonoBehaviour
    {
        private const float PanelWidth = 300f;
        private const float HeaderHeight = 26f;
        private const float DockMargin = 20f;
        private const float DockGap = 4f;
        private const float RowHeight = 20f;
        private const float SectionRowHeight = 16f;
        private const float BarLabelWidth = 72f;
        private const float SlideAnimationSeconds = 0.18f;

        public static bool PanelReady { get; private set; }

        private RectTransform panel;
        private GameObject body;
        private float bodyNaturalHeight;
        private float dockProgress = 1f;
        private bool expanded = true;
        private Toggle headerToggle;
        private Text headerText;

        // Ship tab strip (ADR-0011 multi-ship): one tab per local-player ship, plus the eye
        // button that orbits the camera onto the active ship. Rebuilt whenever the set of local
        // ships changes (partition ran, a ship was added mid-sim, sim stopped).
        private RectTransform tabRow;
        private readonly List<ShipState> localShips = new List<ShipState>();
        private readonly List<ShipState> tabShips = new List<ShipState>();
        private readonly List<Image> tabBackgrounds = new List<Image>();
        private readonly List<Text> tabTexts = new List<Text>();
        private static readonly Color ActiveTabColor = new Color(0.3f, 0.85f, 0.95f, 0.4f);

        private Text coreNameText;
        private Text captainIffText;
        private Text totalPowerText;
        private Text hullText;
        private Image powerShareArmorBar;
        private Image powerShareShieldBar;
        private Image powerShareWeaponBar;
        private Image capacitorArmorBar;
        private Image capacitorShieldBar;
        private Image capacitorWeaponBar;
        private Image capacitorUniversalBar;
        private Text tracksCountText;
        private Text shieldsCountText;
        private Text armorBlockCountText;
        private Image armorIntegrityBar;
        private Image armorStructuralBar;
        private Toggle showArmorHpToggle;
        private Text defenseTargetText;

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
                Debug.LogWarning("BeyondSpider: info panel failed to build, falling back to legacy HUD. " + ex);
            }
        }

        private void Build()
        {
            Transform canvas = BeyondSpiderUI.GetOrCreateRootCanvas();

            Text titleText;
            headerToggle = BeyondSpiderUI.CreateHeaderToggle(canvas, "BS Info Header", "▾ SHIP STATUS", out titleText);
            headerText = titleText;
            RectTransform headerRect = headerToggle.GetComponent<RectTransform>();
            headerRect.anchorMin = new Vector2(0f, 0f);
            headerRect.anchorMax = new Vector2(0f, 0f);
            headerRect.pivot = new Vector2(0f, 0f);
            headerRect.anchoredPosition = new Vector2(DockMargin, DockMargin);
            headerRect.sizeDelta = new Vector2(PanelWidth, HeaderHeight);

            Image panelImage = BeyondSpiderUI.CreatePanel(canvas, "BS Info Panel", BeyondSpiderUI.PanelColor);
            panelImage.raycastTarget = false;
            // Stencil Mask, NOT RectMask2D: an earlier RectMask2D here shrank the background but left
            // the rows fully visible overflowing above the collapsed panel (its cached clip rect never
            // followed the per-frame sizeDelta animation). Mask rewrites its stencil from the panel
            // Image's mesh every frame the rect changes, so the child rows always clip to the current
            // panel height. showMaskGraphic keeps the dark panel background itself drawn.
            Mask panelMask = panelImage.gameObject.AddComponent<Mask>();
            panelMask.showMaskGraphic = true;
            panel = panelImage.rectTransform;
            panel.anchorMin = new Vector2(0f, 0f);
            panel.anchorMax = new Vector2(0f, 0f);
            panel.pivot = new Vector2(0f, 0f);
            panel.anchoredPosition = new Vector2(DockMargin, DockMargin + HeaderHeight + DockGap);
            // Height starts at 0 -- UpdateDockAnimation() grows it toward bodyNaturalHeight once that's
            // measured below, sliding up from behind the header the same way CaptainRadarView's dock does.
            panel.sizeDelta = new Vector2(PanelWidth, 0f);

            body = BeyondSpiderUI.CreateRect(panel, "Rows").gameObject;
            RectTransform bodyRect = (RectTransform)body.transform;
            bodyRect.anchorMin = new Vector2(0f, 0f);
            bodyRect.anchorMax = new Vector2(1f, 0f);
            bodyRect.pivot = new Vector2(0.5f, 0f);
            bodyRect.anchoredPosition = Vector2.zero;
            bodyRect.sizeDelta = new Vector2(0f, 0f);
            BeyondSpiderUI.AddVerticalLayout(body, 8, 4f);
            BeyondSpiderUI.AddAutoHeight(body);

            tabRow = BeyondSpiderUI.CreateRect(body.transform, "ShipTabs");
            BeyondSpiderUI.SetRowHeight(tabRow.gameObject, RowHeight);
            HorizontalLayoutGroup tabLayout = tabRow.gameObject.AddComponent<HorizontalLayoutGroup>();
            tabLayout.spacing = 4f;
            tabLayout.childForceExpandWidth = true;
            tabLayout.childForceExpandHeight = true;

            coreNameText = AddTextRow("CoreName", "Core: --");
            captainIffText = AddTextRow("CaptainIff", "Captain IFF: --");
            totalPowerText = AddTextRow("TotalPower", "Total power MW: --");
            hullText = AddTextRow("Hull", "Hull: --");

            AddSectionLabel("POWER SHARE");
            powerShareArmorBar = AddBarRow("PowerShareArmor", "Armor");
            powerShareShieldBar = AddBarRow("PowerShareShield", "Shield");
            powerShareWeaponBar = AddBarRow("PowerShareWeapon", "Weapon");

            AddSectionLabel("CAPACITOR CHARGE");
            capacitorArmorBar = AddBarRow("CapArmor", "Armor");
            capacitorShieldBar = AddBarRow("CapShield", "Shield");
            capacitorWeaponBar = AddBarRow("CapWeapon", "Weapon");
            capacitorUniversalBar = AddBarRow("CapUniversal", "Universal");

            tracksCountText = AddTextRow("Tracks", "Tracks: --");
            shieldsCountText = AddTextRow("Shields", "Shields: --");

            AddSectionLabel("ARMOR");
            armorBlockCountText = AddTextRow("ArmorBlocks", "Armor blocks: --");
            armorIntegrityBar = AddBarRow("ArmorIntegrity", "Integrity");
            armorStructuralBar = AddBarRow("ArmorStructural", "Structural");

            showArmorHpToggle = AddToggleRow("ShowArmorHp", "Show Armor HP");
            showArmorHpToggle.isOn = SpaceCombatRuntime.ShowArmorHP;
            showArmorHpToggle.onValueChanged.AddListener(OnShowArmorHpChanged);

            defenseTargetText = AddTextRow("DefenseTarget", "Defense target: none");

            // Measure Rows' natural (all rows visible) height once via its own ContentSizeFitter, then
            // drive the panel's clip height from that -- Rows itself is never resized after this.
            LayoutRebuilder.ForceRebuildLayoutImmediate(bodyRect);
            bodyNaturalHeight = bodyRect.rect.height;
            if (bodyNaturalHeight <= 1f)
            {
                // Defensive fallback in case the immediate-rebuild measurement above ever comes back
                // empty (e.g. run before the canvas has laid out once) -- better an oversized panel
                // than one that collapses to nothing and never shows its rows again.
                bodyNaturalHeight = 520f;
            }

            headerToggle.onValueChanged.AddListener(OnHeaderToggled);
            OnHeaderToggled(headerToggle.isOn);

            // Starts hidden -- Update() sets the real visibility (whether a local ship exists) every
            // frame, but that hasn't run yet the instant Build() returns, so without this the panel
            // would show one frame of placeholder "--" text before its first real refresh.
            SetVisible(false);
        }

        private Text AddTextRow(string name, string initial)
        {
            Text text = BeyondSpiderUI.CreateLabel(body.transform, name, initial, 13, TextAnchor.MiddleLeft);
            BeyondSpiderUI.SetRowHeight(text.gameObject, RowHeight);
            return text;
        }

        private void AddSectionLabel(string title)
        {
            Text text = BeyondSpiderUI.CreateLabel(body.transform, title, title, 11, TextAnchor.MiddleLeft);
            text.color = BeyondSpiderUI.AccentColor;
            text.fontStyle = FontStyle.Bold;
            BeyondSpiderUI.SetRowHeight(text.gameObject, SectionRowHeight);
        }

        private Image AddBarRow(string name, string label)
        {
            RectTransform row = BeyondSpiderUI.CreateRect(body.transform, name + "Row");
            BeyondSpiderUI.SetRowHeight(row.gameObject, RowHeight);
            HorizontalLayoutGroup layout = row.gameObject.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = 6f;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = true;

            Text labelText = BeyondSpiderUI.CreateLabel(row, name + "Label", label, 12, TextAnchor.MiddleLeft);
            LayoutElement labelElement = labelText.gameObject.AddComponent<LayoutElement>();
            labelElement.preferredWidth = BarLabelWidth;

            Image fill = BeyondSpiderUI.CreateBarMeter(row, name + "Bar", BeyondSpiderUI.AccentColor);
            LayoutElement barElement = fill.transform.parent.gameObject.AddComponent<LayoutElement>();
            barElement.flexibleWidth = 1f;

            return fill;
        }

        private Toggle AddToggleRow(string name, string label)
        {
            Image background = BeyondSpiderUI.CreatePanel(body.transform, name + "Row", BeyondSpiderUI.BarBackgroundColor);
            RectTransform row = background.rectTransform;
            BeyondSpiderUI.SetRowHeight(row.gameObject, RowHeight);
            Toggle toggle = row.gameObject.AddComponent<Toggle>();
            toggle.targetGraphic = background;

            Text text = BeyondSpiderUI.CreateLabel(row, name + "Label", label, 12, TextAnchor.MiddleLeft);
            RectTransform textRect = text.rectTransform;
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = new Vector2(4f, 0f);
            textRect.offsetMax = Vector2.zero;

            return toggle;
        }

        private void OnHeaderToggled(bool isExpanded)
        {
            expanded = isExpanded;
            headerText.text = (isExpanded ? "▾ " : "▸ ") + "SHIP STATUS";
        }

        private void OnShowArmorHpChanged(bool value)
        {
            SpaceCombatRuntime.ShowArmorHP = value;
        }

        private void SetVisible(bool visible)
        {
            if (headerToggle != null)
            {
                headerToggle.gameObject.SetActive(visible);
            }
            if (panel != null)
            {
                panel.gameObject.SetActive(visible);
            }
        }

        // Sidebar-style slide instead of an instant pop: panel's height eases toward bodyNaturalHeight
        // (expanded) or 0 (collapsed) every frame -- unscaledDeltaTime so it keeps animating even if
        // gameplay time is paused/slowed, matching CaptainRadarView's dock.
        private void UpdateDockAnimation()
        {
            if (panel == null)
            {
                return;
            }
            float target = expanded ? 1f : 0f;
            float step = Time.unscaledDeltaTime / SlideAnimationSeconds;
            dockProgress = Mathf.MoveTowards(dockProgress, target, step);
            float eased = Mathf.SmoothStep(0f, 1f, dockProgress);
            panel.sizeDelta = new Vector2(PanelWidth, bodyNaturalHeight * eased);
        }

        private void Update()
        {
            if (!PanelReady)
            {
                return;
            }

            UpdateDockAnimation();

            SpaceCombatRegistry.GetShips(SpaceCombatRegistry.LocalPlayerId(), localShips);
            ShipState ship = SpaceCombatRegistry.ActiveLocalShip;
            bool hasShip = ship != null && ship.Core != null;
            SetVisible(hasShip);
            if (!hasShip)
            {
                return;
            }

            if (!SameShipsAsTabs())
            {
                RebuildTabs();
            }
            RefreshTabVisuals(ship);
            RefreshFrom(ship);
        }

        private bool SameShipsAsTabs()
        {
            if (localShips.Count != tabShips.Count)
            {
                return false;
            }
            for (int i = 0; i < localShips.Count; i++)
            {
                if (!ReferenceEquals(localShips[i], tabShips[i]))
                {
                    return false;
                }
            }
            return true;
        }

        private static string TabName(ShipState ship)
        {
            if (ship.Name != null && ship.Name.Length > 0)
            {
                return ship.Name;
            }
            return ship.Core != null ? ship.Core.DisplayName : "SHIP";
        }

        // One tab per local ship plus the eye button at the row's right end. Children of a
        // HorizontalLayoutGroup lay out in creation order, so the eye is simply added last.
        private void RebuildTabs()
        {
            for (int i = tabRow.childCount - 1; i >= 0; i--)
            {
                Destroy(tabRow.GetChild(i).gameObject);
            }
            tabBackgrounds.Clear();
            tabTexts.Clear();
            tabShips.Clear();
            tabShips.AddRange(localShips);

            for (int i = 0; i < tabShips.Count; i++)
            {
                ShipState tabShip = tabShips[i];
                Image background = BeyondSpiderUI.CreatePanel(tabRow, "Tab" + i, BeyondSpiderUI.BarBackgroundColor);
                Button button = background.gameObject.AddComponent<Button>();
                button.targetGraphic = background;
                button.onClick.AddListener(delegate { SpaceCombatRegistry.ActiveLocalShip = tabShip; });

                Text label = BeyondSpiderUI.CreateLabel(background.transform, "Tab" + i + "Label", TabName(tabShip), 11, TextAnchor.MiddleCenter);
                RectTransform labelRect = label.rectTransform;
                labelRect.anchorMin = Vector2.zero;
                labelRect.anchorMax = Vector2.one;
                labelRect.offsetMin = Vector2.zero;
                labelRect.offsetMax = Vector2.zero;

                tabBackgrounds.Add(background);
                tabTexts.Add(label);
            }

            Image eyeBackground = BeyondSpiderUI.CreatePanel(tabRow, "EyeButton", BeyondSpiderUI.BarBackgroundColor);
            LayoutElement eyeElement = eyeBackground.gameObject.AddComponent<LayoutElement>();
            eyeElement.preferredWidth = 26f;
            eyeElement.flexibleWidth = 0f;
            Button eyeButton = eyeBackground.gameObject.AddComponent<Button>();
            eyeButton.targetGraphic = eyeBackground;
            eyeButton.onClick.AddListener(OnEyeClicked);
            Text eyeLabel = BeyondSpiderUI.CreateLabel(eyeBackground.transform, "EyeLabel", "◉", 13, TextAnchor.MiddleCenter);
            RectTransform eyeLabelRect = eyeLabel.rectTransform;
            eyeLabelRect.anchorMin = Vector2.zero;
            eyeLabelRect.anchorMax = Vector2.one;
            eyeLabelRect.offsetMin = Vector2.zero;
            eyeLabelRect.offsetMax = Vector2.zero;
        }

        private void RefreshTabVisuals(ShipState active)
        {
            for (int i = 0; i < tabShips.Count; i++)
            {
                tabTexts[i].text = TabName(tabShips[i]);
                tabBackgrounds[i].color = ReferenceEquals(tabShips[i], active)
                    ? ActiveTabColor
                    : BeyondSpiderUI.BarBackgroundColor;
            }
        }

        // Eye button: orbit the game camera around the active ship — its captain when it has
        // one, its core otherwise.
        private void OnEyeClicked()
        {
            ShipState ship = SpaceCombatRegistry.ActiveLocalShip;
            if (ship == null)
            {
                return;
            }
            BlockBehaviour focus = null;
            if (ship.Captain != null)
            {
                focus = ship.Captain.BlockBehaviour;
            }
            if (focus == null && ship.Core != null)
            {
                focus = ship.Core.BlockBehaviour;
            }
            if (focus == null)
            {
                return;
            }
            MouseOrbit orbit = FindMouseOrbit();
            if (orbit != null)
            {
                orbit.FocusBlock(focus);
            }
        }

        private static MouseOrbit FindMouseOrbit()
        {
            Camera gameCamera = Camera.main;
            if (gameCamera != null)
            {
                MouseOrbit orbit = gameCamera.GetComponent<MouseOrbit>();
                if (orbit != null)
                {
                    return orbit;
                }
            }
            return UnityEngine.Object.FindObjectOfType(typeof(MouseOrbit)) as MouseOrbit;
        }

        private void RefreshFrom(ShipState ship)
        {
            coreNameText.text = "Ship: " + TabName(ship)
                + (ship.Cores.Count > 1 ? "  (" + ship.Cores.Count + " cores)" : "");
            hullText.text = "Hull: " + ship.HullSize.x.ToString("0") + "x"
                + ship.HullSize.y.ToString("0") + "x" + ship.HullSize.z.ToString("0")
                + " m  Vol " + ship.HullVolume.ToString("0");
            captainIffText.text = ship.Captain != null
                ? "Captain IFF: " + (ship.Captain.Iff.IsActive ? "on" : "off")
                : "Captain IFF: --";
            totalPowerText.text = "Total power MW: " + ship.Energy.ReactorOutput.ToString("0");

            float armorShare = 0f;
            float shieldShare = 0f;
            float weaponShare = 0f;
            if (ship.Core != null)
            {
                float armor = ship.Core.ArmorPowerShare.Value;
                float shield = ship.Core.ShieldPowerShare.Value;
                float weapon = ship.Core.WeaponPowerShare.Value;
                float total = Mathf.Max(0.001f, armor + shield + weapon);
                armorShare = armor / total;
                shieldShare = shield / total;
                weaponShare = weapon / total;
            }
            powerShareArmorBar.fillAmount = armorShare;
            powerShareShieldBar.fillAmount = shieldShare;
            powerShareWeaponBar.fillAmount = weaponShare;

            capacitorArmorBar.fillAmount = ship.Energy.ChargeLevel(EnergyBus.Armor);
            capacitorShieldBar.fillAmount = ship.Energy.ChargeLevel(EnergyBus.Shield);
            capacitorWeaponBar.fillAmount = ship.Energy.ChargeLevel(EnergyBus.Weapon);
            capacitorUniversalBar.fillAmount = ship.Energy.ChargeLevel(EnergyBus.Universal);

            tracksCountText.text = "Tracks: " + ship.Tracks.Count;
            shieldsCountText.text = "Shields: " + ship.Shields.Count;

            armorBlockCountText.text = "Armor blocks: " + ship.Armor.Count;
            float integritySum = 0f;
            float structuralSum = 0f;
            for (int i = 0; i < ship.Armor.Count; i++)
            {
                NanoArmorBehaviour armor = ship.Armor[i];
                if (armor == null)
                {
                    continue;
                }
                integritySum += armor.Integrity;
                structuralSum += armor.StructuralValue;
            }
            float armorCount = Mathf.Max(1, ship.Armor.Count);
            armorIntegrityBar.fillAmount = integritySum / armorCount;
            armorStructuralBar.fillAmount = structuralSum / armorCount;

            if (showArmorHpToggle.isOn != SpaceCombatRuntime.ShowArmorHP)
            {
                showArmorHpToggle.isOn = SpaceCombatRuntime.ShowArmorHP;
            }

            defenseTargetText.text = ship.DefensiveSolution.Target != null
                ? "Defense target: " + ship.DefensiveSolution.Target.Kind + "  TTI " + ship.DefensiveSolution.TimeToImpact.ToString("0.0") + "s"
                : "Defense target: none";
        }
    }
}
