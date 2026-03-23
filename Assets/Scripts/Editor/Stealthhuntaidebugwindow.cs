using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace StealthHuntAI.Editor
{
    public class StealthHuntAIDebugWindow : EditorWindow
    {
        // ---------- Colors ----------------------------------------------------

        private static readonly Color ColorPassive = new Color(0.25f, 0.75f, 0.35f);
        private static readonly Color ColorSuspicious = new Color(0.95f, 0.75f, 0.10f);
        private static readonly Color ColorHostile = new Color(0.90f, 0.20f, 0.15f);
        private static readonly Color ColorRowEven = new Color(0.18f, 0.18f, 0.18f);
        private static readonly Color ColorRowOdd = new Color(0.22f, 0.22f, 0.22f);
        private static readonly Color ColorHeader = new Color(0.14f, 0.14f, 0.14f);

        // ---------- UI elements -----------------------------------------------

        private VisualElement _unitListContainer;
        private Label _tensionLabel;
        private Label _unitCountLabel;
        private VisualElement _tensionFill;
        private Label _statusLabel;

        private IVisualElementScheduledItem _updateSchedule;

        // ---------- Menu item -------------------------------------------------

        [MenuItem("Window/StealthHuntAI/Debug Monitor")]
        public static void Open()
        {
            var window = GetWindow<StealthHuntAIDebugWindow>();
            window.titleContent = new GUIContent("SHA Monitor");
            window.minSize = new Vector2(480, 300);
            window.Show();
        }

        // ---------- Window lifecycle ------------------------------------------

        private void CreateGUI()
        {
            BuildUI();
            _updateSchedule = rootVisualElement.schedule
                .Execute(Refresh)
                .Every(100);
        }

        private void OnDestroy()
        {
            _updateSchedule?.Pause();
        }

        // ---------- UI construction -------------------------------------------

        private void BuildUI()
        {
            rootVisualElement.Clear();
            rootVisualElement.style.flexDirection = FlexDirection.Column;
            rootVisualElement.style.backgroundColor = new StyleColor(new Color(0.16f, 0.16f, 0.16f));

            rootVisualElement.Add(BuildTopBar());
            rootVisualElement.Add(BuildTableHeader());
            rootVisualElement.Add(BuildScrollView());
            rootVisualElement.Add(BuildStatusBar());
        }

        private VisualElement BuildTopBar()
        {
            var bar = new VisualElement();
            bar.style.flexDirection = FlexDirection.Row;
            bar.style.alignItems = Align.Center;
            bar.style.paddingLeft = 8;
            bar.style.paddingRight = 8;
            bar.style.paddingTop = 6;
            bar.style.paddingBottom = 6;
            bar.style.backgroundColor = new StyleColor(ColorHeader);

            var title = new Label("StealthHuntAI Debug Monitor");
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.fontSize = 12;
            title.style.flexGrow = 1;
            bar.Add(title);

            _unitCountLabel = new Label("-- units");
            _unitCountLabel.style.fontSize = 10;
            _unitCountLabel.style.marginRight = 12;
            bar.Add(_unitCountLabel);

            // Tension area
            var tensionContainer = new VisualElement();
            tensionContainer.style.flexDirection = FlexDirection.Row;
            tensionContainer.style.alignItems = Align.Center;
            tensionContainer.style.width = 160;

            _tensionLabel = new Label("Tension: --");
            _tensionLabel.style.fontSize = 10;
            _tensionLabel.style.width = 80;
            _tensionLabel.style.marginRight = 4;
            tensionContainer.Add(_tensionLabel);

            var alertLabel = new Label("");
            alertLabel.name = "alertLabel";
            alertLabel.style.fontSize = 10;
            alertLabel.style.width = 60;
            alertLabel.style.marginLeft = 8;
            tensionContainer.Add(alertLabel);

            var tensionBg = new VisualElement();
            tensionBg.style.flexGrow = 1;
            tensionBg.style.height = 10;
            tensionBg.style.backgroundColor = new StyleColor(new Color(0.1f, 0.1f, 0.1f));

            _tensionFill = new VisualElement();
            _tensionFill.style.height = 10;
            _tensionFill.style.width = Length.Percent(0);
            _tensionFill.style.backgroundColor = new StyleColor(ColorPassive);
            tensionBg.Add(_tensionFill);
            tensionContainer.Add(tensionBg);

            bar.Add(tensionContainer);

            // Refresh button
            var btn = new Button(Refresh) { text = "Refresh" };
            btn.style.marginLeft = 8;
            btn.style.height = 22;
            bar.Add(btn);

            return bar;
        }

        private VisualElement BuildTableHeader()
        {
            var row = BuildRow(ColorHeader);
            row.style.borderBottomColor = new StyleColor(new Color(0.4f, 0.4f, 0.4f));
            row.style.borderBottomWidth = 1;

            row.Add(BuildHeaderCell("Name", 120));
            row.Add(BuildHeaderCell("Squad", 44));
            row.Add(BuildHeaderCell("Role", 72));
            row.Add(BuildHeaderCell("State", 90));
            row.Add(BuildHeaderCell("Sub-State", 90));
            row.Add(BuildHeaderCell("Awareness", 120));
            row.Add(BuildHeaderCell("", 50));  // ping button column

            return row;
        }

        private ScrollView BuildScrollView()
        {
            var scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.style.flexGrow = 1;

            _unitListContainer = new VisualElement();
            _unitListContainer.style.flexDirection = FlexDirection.Column;
            scroll.Add(_unitListContainer);

            return scroll;
        }

        private VisualElement BuildStatusBar()
        {
            var bar = new VisualElement();
            bar.style.backgroundColor = new StyleColor(ColorHeader);
            bar.style.paddingLeft = 8;
            bar.style.paddingTop = 3;
            bar.style.paddingBottom = 3;

            _statusLabel = new Label("Not playing");
            _statusLabel.style.fontSize = 9;
            _statusLabel.style.color = new StyleColor(new Color(0.55f, 0.55f, 0.55f));
            bar.Add(_statusLabel);

            return bar;
        }

        // ---------- Data refresh ----------------------------------------------

        private void Refresh()
        {
            _unitListContainer.Clear();

            if (!Application.isPlaying)
            {
                if (_statusLabel != null) _statusLabel.text = "Enter Play Mode to see live data.";
                if (_unitCountLabel != null) _unitCountLabel.text = "-- units";
                return;
            }

            var units = FindAllUnits();
            if (_unitCountLabel != null) _unitCountLabel.text = units.Count + " unit" + (units.Count != 1 ? "s" : "");

            // Update tension bar
            var director = Object.FindFirstObjectByType<HuntDirector>();
            if (director != null)
            {
                float t = director.TensionLevel;
                if (_tensionFill != null)
                {
                    _tensionFill.style.width = Length.Percent(t * 100f);
                    Color tc = Color.Lerp(ColorPassive, ColorHostile, t);
                    _tensionFill.style.backgroundColor = new StyleColor(tc);
                }
                if (_tensionLabel != null) _tensionLabel.text = "Tension: " + t.ToString("F2");
            }

            // Build unit rows
            for (int i = 0; i < units.Count; i++)
            {
                var unit = units[i];
                if (unit == null) continue;

                Color rowBg = i % 2 == 0 ? ColorRowEven : ColorRowOdd;
                var row = BuildUnitRow(unit, rowBg, i);
                _unitListContainer.Add(row);
            }

            // Status
            int passive = 0, suspicious = 0, hostile = 0;
            foreach (var u in units)
            {
                switch (u.CurrentAlertState)
                {
                    case AlertState.Passive: passive++; break;
                    case AlertState.Suspicious: suspicious++; break;
                    case AlertState.Hostile: hostile++; break;
                }
            }

            if (_statusLabel != null)
                _statusLabel.text =
                    "Passive: " + passive +
                    "  Suspicious: " + suspicious +
                    "  Hostile: " + hostile +
                    "  |  Refreshing at 10 Hz";
        }

        private VisualElement BuildUnitRow(StealthHuntAI unit, Color bg, int index)
        {
            Color stateColor = unit.CurrentAlertState switch
            {
                AlertState.Passive => ColorPassive,
                AlertState.Suspicious => ColorSuspicious,
                AlertState.Hostile => ColorHostile,
                _ => Color.white
            };

            var row = BuildRow(bg);

            // Left accent stripe by state
            var stripe = new VisualElement();
            stripe.style.width = 3;
            stripe.style.backgroundColor = new StyleColor(stateColor);
            row.Insert(0, stripe);

            // Name (clickable)
            var nameLabel = new Label(unit.gameObject.name);
            nameLabel.style.fontSize = 10;
            nameLabel.style.width = 117;
            nameLabel.style.color = new StyleColor(Color.white);
            nameLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            row.Add(nameLabel);

            // Squad
            row.Add(BuildCell(unit.squadID.ToString(), 44, Color.white));

            // Role
            Color roleColor = unit.ActiveRole == SquadRole.Dynamic
                ? new Color(0.5f, 0.5f, 0.5f)
                : new Color(0.7f, 0.9f, 1f);
            row.Add(BuildCell(unit.ActiveRole.ToString(), 72, roleColor));

            // State
            row.Add(BuildCell(unit.CurrentAlertState.ToString(), 90, stateColor));

            // SubState
            row.Add(BuildCell(unit.CurrentSubState.ToString(), 90,
                              new Color(0.75f, 0.75f, 0.75f)));

            // Awareness mini bar
            row.Add(BuildAwarenessCell(unit.AwarenessLevel, stateColor, 120));

            // Ping button
            var capturedUnit = unit;
            var pingBtn = new Button(() => PingUnit(capturedUnit)) { text = "Ping" };
            pingBtn.style.height = 18;
            pingBtn.style.fontSize = 9;
            pingBtn.style.width = 44;
            pingBtn.style.marginLeft = 3;
            row.Add(pingBtn);

            return row;
        }

        private VisualElement BuildAwarenessCell(float value, Color fill, float width)
        {
            var container = new VisualElement();
            container.style.flexDirection = FlexDirection.Row;
            container.style.alignItems = Align.Center;
            container.style.width = width;
            container.style.paddingLeft = 4;
            container.style.paddingRight = 4;

            var bg = new VisualElement();
            bg.style.flexGrow = 1;
            bg.style.height = 8;
            bg.style.backgroundColor = new StyleColor(new Color(0.1f, 0.1f, 0.1f));

            var filledPart = new VisualElement();
            filledPart.style.height = 8;
            filledPart.style.width = Length.Percent(value * 100f);
            filledPart.style.backgroundColor = new StyleColor(fill);
            bg.Add(filledPart);

            container.Add(bg);

            var valueLabel = new Label(value.ToString("F2"));
            valueLabel.style.fontSize = 9;
            valueLabel.style.width = 28;
            valueLabel.style.marginLeft = 4;
            valueLabel.style.color = new StyleColor(new Color(0.7f, 0.7f, 0.7f));
            container.Add(valueLabel);

            return container;
        }

        // ---------- Row / cell builders ---------------------------------------

        private VisualElement BuildRow(Color bg)
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.height = 24;
            row.style.backgroundColor = new StyleColor(bg);
            row.style.paddingLeft = 0;
            return row;
        }

        private Label BuildHeaderCell(string text, float width)
        {
            var label = new Label(text);
            label.style.fontSize = 9;
            label.style.width = width;
            label.style.color = new StyleColor(new Color(0.6f, 0.6f, 0.6f));
            label.style.unityFontStyleAndWeight = FontStyle.Bold;
            label.style.paddingLeft = 4;
            label.style.paddingRight = 4;
            return label;
        }

        private Label BuildCell(string text, float width, Color color)
        {
            var label = new Label(text);
            label.style.fontSize = 10;
            label.style.width = width;
            label.style.color = new StyleColor(color);
            label.style.paddingLeft = 4;
            label.style.overflow = Overflow.Hidden;
            return label;
        }

        // ---------- Actions ---------------------------------------------------

        private void PingUnit(StealthHuntAI unit)
        {
            if (unit == null) return;
            Selection.activeGameObject = unit.gameObject;
            SceneView.lastActiveSceneView?.FrameSelected();
            EditorGUIUtility.PingObject(unit.gameObject);
        }

        // ---------- Utility ---------------------------------------------------

        private static List<StealthHuntAI> FindAllUnits()
        {
            var list = new List<StealthHuntAI>();
            var all = Object.FindObjectsByType<StealthHuntAI>(FindObjectsSortMode.None);
            list.AddRange(all);
            list.Sort((a, b) =>
            {
                int sq = a.squadID.CompareTo(b.squadID);
                return sq != 0 ? sq : string.Compare(a.name, b.name,
                    System.StringComparison.Ordinal);
            });
            return list;
        }
    }
}