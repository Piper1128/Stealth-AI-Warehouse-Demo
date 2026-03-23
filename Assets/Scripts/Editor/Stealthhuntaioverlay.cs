using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Overlays;
using UnityEngine;
using UnityEngine.UIElements;

namespace StealthHuntAI.Editor
{
    [Overlay(typeof(SceneView), "StealthHuntAI", "StealthHuntAI Debug")]
    [Icon("Assets/StealthHuntAI/Editor/Icons/stealth_icon.png")]
    public class StealthHuntAIOverlay : Overlay, ITransientOverlay
    {
        // ---------- Visibility ------------------------------------------------

        // ITransientOverlay: only show when there are StealthHuntAI units in scene
        public bool visible => FindUnits().Count > 0 || Application.isPlaying;

        // ---------- Toggle state (persisted in EditorPrefs) -------------------

        private const string PrefFOV = "SHA_ShowFOV";
        private const string PrefHearing = "SHA_ShowHearing";
        private const string PrefLastKnown = "SHA_ShowLastKnown";
        private const string PrefSquadLines = "SHA_ShowSquadLines";
        private const string PrefAwareness = "SHA_ShowAwareness";
        private const string PrefLabels = "SHA_ShowLabels";

        private Toggle _togFOV;
        private Toggle _togHearing;
        private Toggle _togLastKnown;
        private Toggle _togSquadLines;
        private Toggle _togAwareness;
        private Toggle _togLabels;

        private Label _tensionLabel;
        private Label _unitCountLabel;
        private VisualElement _tensionBar;
        private VisualElement _tensionFill;

        private static readonly Color ColorPassive = new Color(0.25f, 0.75f, 0.35f);
        private static readonly Color ColorSuspicious = new Color(0.95f, 0.75f, 0.10f);
        private static readonly Color ColorHostile = new Color(0.90f, 0.20f, 0.15f);

        // ---------- UI build --------------------------------------------------

        public override VisualElement CreatePanelContent()
        {
            var root = new VisualElement();
            root.style.minWidth = 200;
            root.style.paddingLeft = 6;
            root.style.paddingRight = 6;
            root.style.paddingTop = 4;
            root.style.paddingBottom = 6;

            // Header
            var header = new Label("StealthHuntAI Monitor");
            header.style.unityFontStyleAndWeight = FontStyle.Bold;
            header.style.fontSize = 11;
            header.style.marginBottom = 4;
            root.Add(header);

            // Tension bar
            root.Add(BuildTensionBar());

            // Unit count
            _unitCountLabel = new Label("Units: --");
            _unitCountLabel.style.fontSize = 10;
            _unitCountLabel.style.marginBottom = 4;
            root.Add(_unitCountLabel);

            // Divider
            root.Add(BuildDivider());

            // Toggle header
            var toggleHeader = new Label("Gizmo Layers");
            toggleHeader.style.unityFontStyleAndWeight = FontStyle.Bold;
            toggleHeader.style.fontSize = 10;
            toggleHeader.style.marginTop = 4;
            toggleHeader.style.marginBottom = 2;
            root.Add(toggleHeader);

            // Toggles
            _togFOV = BuildToggle("FOV Cones", PrefFOV, root);
            _togHearing = BuildToggle("Hearing Radius", PrefHearing, root);
            _togLastKnown = BuildToggle("Last Known Markers", PrefLastKnown, root);
            _togSquadLines = BuildToggle("Squad Lines", PrefSquadLines, root);
            _togAwareness = BuildToggle("Awareness Bars", PrefAwareness, root);
            _togLabels = BuildToggle("State Labels", PrefLabels, root);

            // Refresh button
            var refreshBtn = new Button(() => SceneView.RepaintAll()) { text = "Refresh Scene" };
            refreshBtn.style.marginTop = 6;
            root.Add(refreshBtn);

            // Schedule live updates
            root.schedule.Execute(UpdateLiveData).Every(100);

            // Hook gizmo drawing
            SceneView.duringSceneGui -= OnSceneGUI;
            SceneView.duringSceneGui += OnSceneGUI;

            return root;
        }

        // ---------- Live data update ------------------------------------------

        private void UpdateLiveData()
        {
            if (!Application.isPlaying)
            {
                _tensionLabel?.SetEnabled(false);
                if (_unitCountLabel != null) _unitCountLabel.text = "Units: (edit mode)";
                return;
            }

            var units = FindUnits();

            int passive = 0;
            int suspicious = 0;
            int hostile = 0;

            foreach (var u in units)
            {
                switch (u.CurrentAlertState)
                {
                    case AlertState.Passive: passive++; break;
                    case AlertState.Suspicious: suspicious++; break;
                    case AlertState.Hostile: hostile++; break;
                }
            }

            if (_unitCountLabel != null)
                _unitCountLabel.text =
                    "Units: " + units.Count +
                    "  |  P:" + passive +
                    " S:" + suspicious +
                    " H:" + hostile;

            // Tension
            var director = Object.FindFirstObjectByType<HuntDirector>();
            if (director != null && _tensionFill != null)
            {
                float t = director.TensionLevel;
                _tensionFill.style.width = Length.Percent(t * 100f);

                Color tc = Color.Lerp(ColorPassive, ColorHostile, t);
                _tensionFill.style.backgroundColor = new StyleColor(tc);

                SceneAlertLevel lvl = HuntDirector.AlertLevel;
                Color ac = lvl == SceneAlertLevel.Alert ? ColorHostile
                         : lvl == SceneAlertLevel.Caution ? ColorSuspicious
                         : ColorPassive;

                if (_tensionLabel != null)
                    _tensionLabel.text = "Tension: " + t.ToString("F2") +
                                         "  [" + lvl.ToString() + "]";
                if (_tensionLabel != null)
                    _tensionLabel.style.color = new StyleColor(ac);
            }

            SceneView.RepaintAll();
        }

        // ---------- Scene GUI (gizmo drawing) ---------------------------------

        private void OnSceneGUI(SceneView sceneView)
        {
            var units = FindUnits();
            if (units.Count == 0) return;

            bool showFOV = EditorPrefs.GetBool(PrefFOV, true);
            bool showHearing = EditorPrefs.GetBool(PrefHearing, true);
            bool showLastKnown = EditorPrefs.GetBool(PrefLastKnown, true);
            bool showSquadLines = EditorPrefs.GetBool(PrefSquadLines, true);
            bool showAwareness = EditorPrefs.GetBool(PrefAwareness, true);
            bool showLabels = EditorPrefs.GetBool(PrefLabels, true);

            foreach (var unit in units)
            {
                if (unit == null) continue;

                Color stateColor = unit.CurrentAlertState switch
                {
                    AlertState.Passive => ColorPassive,
                    AlertState.Suspicious => ColorSuspicious,
                    AlertState.Hostile => ColorHostile,
                    _ => Color.white
                };

                var sensor = unit.GetComponent<AwarenessSensor>();
                Transform origin = sensor != null ? unit.transform : unit.transform;

                // FOV cone
                if (showFOV && sensor != null)
                {
                    DrawFOVCone(unit.transform, sensor, sensor.sightRange, sensor.sightAngle,
                                new Color(stateColor.r, stateColor.g, stateColor.b, 0.12f));

                    // Peripheral ring
                    Handles.color = new Color(1f, 0.5f, 0f, 0.08f);
                    Handles.DrawWireDisc(unit.transform.position,
                                          Vector3.up, sensor.peripheralRange);
                }

                // Hearing radius
                if (showHearing && sensor != null)
                {
                    Handles.color = new Color(0.2f, 0.6f, 1f, 0.07f);
                    Handles.DrawWireDisc(unit.transform.position,
                                          Vector3.up, sensor.hearingRange);
                }

                // Last known position
                if (showLastKnown && Application.isPlaying && unit.LastKnownPosition.HasValue)
                {
                    Vector3 lkp = unit.LastKnownPosition.Value;
                    Handles.color = new Color(1f, 0.2f, 0.1f, 0.8f);
                    Handles.DrawWireCube(lkp + Vector3.up * 0.5f, Vector3.one * 0.35f);
                    Handles.DrawDottedLine(unit.transform.position, lkp, 4f);
                }

                // Awareness bar (billboard above unit)
                if (showAwareness && Application.isPlaying)
                {
                    DrawAwarenessBar(unit, stateColor);
                }

                // State label
                if (showLabels && Application.isPlaying)
                {
                    Handles.color = Color.white;
                    // Show Combat Pack state via ICombatBehaviour interface
                    string subStateLabel = unit.CurrentSubState.ToString();
                    var combat = unit.combatBehaviourOverride as ICombatBehaviour;
                    if (combat != null && combat.WantsControl)
                    {
                        // Use reflection to get state name without Combat assembly dependency
                        var prop = unit.combatBehaviourOverride?.GetType()
                                       .GetProperty("CurrentStateName");
                        string scState = prop?.GetValue(unit.combatBehaviourOverride) as string;
                        subStateLabel = "Combat" + (scState != null ? "/" + scState : "");
                    }

                    string label = unit.CurrentAlertState + " / " + subStateLabel
                                 + "\n" + unit.AwarenessLevel.ToString("F2")
                                 + "  [" + unit.ActiveRole + "]";
                    Handles.Label(unit.transform.position + Vector3.up * 3.4f, label);
                }
            }

            // Squad connection lines
            if (showSquadLines && Application.isPlaying)
                DrawSquadLines(units);
        }

        // ---------- Gizmo helpers ---------------------------------------------

        private void DrawFOVCone(Transform root, AwarenessSensor sensor,
                                   float range, float angle, Color color)
        {
            // Use SightOrigin forward if followHeadBone is enabled
            Vector3 fwd = (sensor != null && sensor.sightFollowHeadBone
                        && sensor.SightOrigin != null)
                ? sensor.SightOrigin.forward
                : root.forward;

            Vector3 pos = sensor != null && sensor.SightOrigin != null
                ? sensor.SightOrigin.position
                : root.position + Vector3.up * 1.4f;

            Handles.color = color;
            int segments = 32;
            float halfAngle = angle * 0.5f;

            Vector3 prev = Vector3.zero;
            for (int i = 0; i <= segments; i++)
            {
                float t = (float)i / segments;
                float a = Mathf.Lerp(-halfAngle, halfAngle, t);
                Vector3 dir = Quaternion.Euler(0, a, 0) * fwd;
                Vector3 pt = pos + dir * range;

                if (i > 0) Handles.DrawLine(prev, pt);
                if (i == 0 || i == segments)
                    Handles.DrawLine(pos, pt);

                prev = pt;
            }

            // Solid fill
            Handles.color = new Color(color.r, color.g, color.b, color.a * 0.5f);
            Handles.DrawSolidArc(pos, Vector3.up,
                Quaternion.Euler(0, -halfAngle, 0) * fwd,
                angle, range);
        }

        private void DrawAwarenessBar(StealthHuntAI unit, Color stateColor)
        {
            Vector3 barPos = unit.transform.position + Vector3.up * 2.6f;
            float awareness = unit.AwarenessLevel;
            float barWidth = 1.2f;
            float barHeight = 0.15f;

            // Background
            Handles.color = new Color(0.1f, 0.1f, 0.1f, 0.6f);
            Handles.DrawSolidRectangleWithOutline(
                BuildBillboardRect(barPos, barWidth, barHeight, SceneView.lastActiveSceneView),
                new Color(0.1f, 0.1f, 0.1f, 0.5f),
                new Color(0.3f, 0.3f, 0.3f, 0.6f));

            // Fill
            if (awareness > 0.01f)
            {
                Vector3 fillPos = barPos + Vector3.left * (barWidth * 0.5f * (1f - awareness));
                Handles.color = stateColor;
                Handles.DrawSolidRectangleWithOutline(
                    BuildBillboardRect(fillPos, barWidth * awareness, barHeight,
                                        SceneView.lastActiveSceneView),
                    new Color(stateColor.r, stateColor.g, stateColor.b, 0.7f),
                    Color.clear);
            }
        }

        private Vector3[] BuildBillboardRect(Vector3 center, float width, float height,
                                              SceneView sv)
        {
            if (sv == null) return new Vector3[4];

            Vector3 right = sv.camera.transform.right * width * 0.5f;
            Vector3 up = sv.camera.transform.up * height * 0.5f;

            return new Vector3[]
            {
                center - right - up,
                center + right - up,
                center + right + up,
                center - right + up
            };
        }

        private void DrawSquadLines(List<StealthHuntAI> units)
        {
            // Group by squadID and draw lines between members
            var squads = new Dictionary<int, List<StealthHuntAI>>();

            foreach (var unit in units)
            {
                if (!squads.ContainsKey(unit.squadID))
                    squads[unit.squadID] = new List<StealthHuntAI>();
                squads[unit.squadID].Add(unit);
            }

            foreach (var pair in squads)
            {
                var members = pair.Value;
                if (members.Count < 2) continue;

                for (int i = 0; i < members.Count; i++)
                {
                    for (int j = i + 1; j < members.Count; j++)
                    {
                        Handles.color = new Color(0.4f, 0.6f, 1f, 0.25f);
                        Handles.DrawDottedLine(
                            members[i].transform.position + Vector3.up * 0.5f,
                            members[j].transform.position + Vector3.up * 0.5f,
                            6f);
                    }
                }
            }
        }

        // ---------- UI helpers ------------------------------------------------

        private VisualElement BuildTensionBar()
        {
            var container = new VisualElement();
            container.style.marginBottom = 4;

            _tensionLabel = new Label("Tension: --");
            _tensionLabel.style.fontSize = 10;
            container.Add(_tensionLabel);

            _tensionBar = new VisualElement();
            _tensionBar.style.height = 8;
            _tensionBar.style.backgroundColor = new StyleColor(new Color(0.15f, 0.15f, 0.15f));
            _tensionBar.style.marginTop = 2;

            _tensionFill = new VisualElement();
            _tensionFill.style.height = 8;
            _tensionFill.style.width = Length.Percent(0);
            _tensionFill.style.backgroundColor = new StyleColor(ColorPassive);
            _tensionBar.Add(_tensionFill);

            container.Add(_tensionBar);
            return container;
        }

        private Toggle BuildToggle(string label, string prefKey, VisualElement parent)
        {
            var tog = new Toggle(label);
            tog.value = EditorPrefs.GetBool(prefKey, true);
            tog.style.fontSize = 10;
            tog.style.marginTop = 1;
            tog.RegisterValueChangedCallback(evt =>
            {
                EditorPrefs.SetBool(prefKey, evt.newValue);
                SceneView.RepaintAll();
            });
            parent.Add(tog);
            return tog;
        }

        private VisualElement BuildDivider()
        {
            var div = new VisualElement();
            div.style.height = 1;
            div.style.marginTop = 4;
            div.style.marginBottom = 4;
            div.style.backgroundColor = new StyleColor(new Color(0.35f, 0.35f, 0.35f));
            return div;
        }

        // ---------- Utility ---------------------------------------------------

        private static List<StealthHuntAI> FindUnits()
        {
            var found = new List<StealthHuntAI>();
            var all = Object.FindObjectsByType<StealthHuntAI>(FindObjectsSortMode.None);
            found.AddRange(all);
            return found;
        }

        public override void OnWillBeDestroyed()
        {
            SceneView.duringSceneGui -= OnSceneGUI;
        }
    }
}