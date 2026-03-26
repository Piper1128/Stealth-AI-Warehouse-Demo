#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using StealthHuntAI.Combat;
using StealthHuntAI.Combat.CQB;

namespace StealthHuntAI.Editor
{
    /// <summary>
    /// Real-time behavior debug overlay for StealthHuntAI.
    /// Shows all guards with their current state, roles, threat intel,
    /// Tactician scenario, CQB state and awareness.
    ///
    /// Open via: StealthHuntAI > Behavior Debug Overlay
    /// </summary>
    public class BehaviorDebugOverlay : EditorWindow
    {
        // ---------- Layout constants -----------------------------------------

        private const float ColGuard = 110f;
        private const float ColAlert = 82f;
        private const float ColRole = 100f;
        private const float ColScenario = 80f;
        private const float ColGoal = 80f;
        private const float ColConf = 52f;
        private const float ColLOS = 36f;
        private const float ColTSeen = 52f;
        private const float ColAware = 52f;
        private const float ColCQB = 72f;
        private const float ColCover = 48f;
        private const float ColWants = 52f;

        // ---------- Colors ---------------------------------------------------

        private static readonly Color ColHostile = new Color(1f, 0.35f, 0.35f);
        private static readonly Color ColSuspicion = new Color(1f, 0.82f, 0.25f);
        private static readonly Color ColPassive = new Color(0.55f, 0.85f, 0.55f);
        private static readonly Color ColSearch = new Color(0.9f, 0.9f, 0.3f);
        private static readonly Color ColApproach = new Color(0.4f, 0.9f, 0.4f);
        private static readonly Color ColAssault = new Color(1f, 0.4f, 0.4f);
        private static readonly Color ColCQBColor = new Color(0.4f, 0.8f, 1f);
        private static readonly Color ColWithdraw = new Color(0.7f, 0.4f, 1f);
        private static readonly Color ColHighConf = new Color(1f, 0.4f, 0.4f);
        private static readonly Color ColMidConf = new Color(1f, 0.82f, 0.25f);
        private static readonly Color ColLowConf = new Color(0.55f, 0.85f, 0.55f);
        private static readonly Color ColLOSTrue = new Color(1f, 0.35f, 0.35f);
        private static readonly Color ColLOSFalse = new Color(0.4f, 0.4f, 0.4f);

        // ---------- State ----------------------------------------------------

        private Vector2 _scroll;
        private bool _autoRefresh = true;
        private float _refreshRate = 0.1f;
        private double _lastRefresh;
        private bool _showDeadGuards = false;
        private int _filterSquad = -1; // -1 = all

        private GUIStyle _headerStyle;
        private GUIStyle _cellStyle;
        private GUIStyle _dimStyle;
        private bool _stylesInit;

        // ---------- Menu entry -----------------------------------------------

        [MenuItem("StealthHuntAI/Behavior Debug Overlay")]
        public static void Open()
        {
            var w = GetWindow<BehaviorDebugOverlay>("AI Debug");
            w.minSize = new Vector2(820f, 300f);
        }

        // ---------- Unity callbacks ------------------------------------------

        private void OnEnable()
        {
            EditorApplication.update += OnEditorUpdate;
        }

        private void OnDisable()
        {
            EditorApplication.update -= OnEditorUpdate;
        }

        private void OnEditorUpdate()
        {
            if (!_autoRefresh) return;
            if (EditorApplication.timeSinceStartup - _lastRefresh < _refreshRate) return;
            _lastRefresh = EditorApplication.timeSinceStartup;
            Repaint();
        }

        // ---------- GUI ------------------------------------------------------

        private void OnGUI()
        {
            InitStyles();
            DrawToolbar();

            if (!Application.isPlaying)
            {
                EditorGUILayout.HelpBox("Enter Play Mode to see live data.",
                    MessageType.Info);
                return;
            }

            var units = HuntDirector.AllUnits;
            if (units == null || units.Count == 0)
            {
                EditorGUILayout.HelpBox("No StealthHuntAI units registered.",
                    MessageType.Warning);
                return;
            }

            DrawSquadSummary(units);
            EditorGUILayout.Space(4);
            DrawTableHeader();

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            DrawRows(units);
            EditorGUILayout.EndScrollView();
        }

        // ---------- Toolbar --------------------------------------------------

        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            _autoRefresh = GUILayout.Toggle(_autoRefresh, "Auto Refresh",
                EditorStyles.toolbarButton, GUILayout.Width(90));

            EditorGUILayout.LabelField("Rate:", GUILayout.Width(36));
            _refreshRate = EditorGUILayout.Slider(_refreshRate, 0.05f, 1f,
                GUILayout.Width(100));

            GUILayout.Space(8);
            _showDeadGuards = GUILayout.Toggle(_showDeadGuards, "Show Dead",
                EditorStyles.toolbarButton, GUILayout.Width(72));

            GUILayout.Space(8);
            EditorGUILayout.LabelField("Squad:", GUILayout.Width(44));
            if (Application.isPlaying)
            {
                var squads = GetSquadIDs();
                var options = new List<string> { "All" };
                foreach (int id in squads) options.Add("Squad " + id);
                int cur = _filterSquad < 0 ? 0 : squads.IndexOf(_filterSquad) + 1;
                int sel = EditorGUILayout.Popup(cur, options.ToArray(),
                    GUILayout.Width(80));
                _filterSquad = sel == 0 ? -1 : squads[sel - 1];
            }

            GUILayout.FlexibleSpace();

            if (GUILayout.Button("Select All Hostile", EditorStyles.toolbarButton,
                GUILayout.Width(110)))
                SelectAllHostile();

            EditorGUILayout.EndHorizontal();
        }

        // ---------- Squad summary banners ------------------------------------

        private void DrawSquadSummary(IReadOnlyList<StealthHuntAI> units)
        {
            var squads = GetSquadIDs();
            EditorGUILayout.BeginHorizontal();

            foreach (int squadID in squads)
            {
                if (_filterSquad >= 0 && _filterSquad != squadID) continue;

                var brain = TacticalBrain.GetOrCreate(squadID);
                var tac = brain.Tactician;
                var threat = brain.Intel.Threat;
                int alive = 0;
                int total = 0;
                foreach (var u in units)
                {
                    if (u == null || u.squadID != squadID) continue;
                    total++;
                    if (!u.IsDead) alive++;
                }

                // Banner color by scenario
                Color bc = tac.CurrentScenario switch
                {
                    SquadTactician.TacticianScenario.Search => ColSearch,
                    SquadTactician.TacticianScenario.Approach => ColApproach,
                    SquadTactician.TacticianScenario.Assault => ColAssault,
                    SquadTactician.TacticianScenario.CQB => ColCQBColor,
                    SquadTactician.TacticianScenario.Withdraw => ColWithdraw,
                    _ => Color.grey,
                };

                var prev = GUI.backgroundColor;
                GUI.backgroundColor = bc * 0.6f;
                EditorGUILayout.BeginVertical("box", GUILayout.Width(200));
                GUI.backgroundColor = prev;

                GUI.color = bc;
                EditorGUILayout.LabelField($"Squad {squadID}  [{alive}/{total}]",
                    EditorStyles.boldLabel);
                GUI.color = Color.white;

                EditorGUILayout.LabelField(
                    $"Scenario: {tac.CurrentScenario}",
                    EditorStyles.miniLabel);
                EditorGUILayout.LabelField(
                    $"Strategy: {brain.Strategy?.Current}",
                    EditorStyles.miniLabel);

                float conf = threat?.Confidence ?? 0f;
                EditorGUI.BeginDisabledGroup(true);
                EditorGUILayout.Slider(conf, 0f, 1f);
                EditorGUI.EndDisabledGroup();

                EditorGUILayout.LabelField(
                    threat != null
                        ? $"LOS: {threat.HasLOS}  TSeen: {threat.TimeSinceSeen:F1}s"
                        : "No threat model",
                    EditorStyles.miniLabel);

                if (brain.CQB.IsActive)
                {
                    GUI.color = ColCQBColor;
                    EditorGUILayout.LabelField(
                        $"CQB: {brain.CQB.CurrentEntry}",
                        EditorStyles.boldLabel);
                    GUI.color = Color.white;
                }

                EditorGUILayout.EndVertical();
            }

            EditorGUILayout.EndHorizontal();
        }

        // ---------- Table header ---------------------------------------------

        private void DrawTableHeader()
        {
            EditorGUILayout.BeginHorizontal(_headerStyle);
            GUILayout.Label("Guard", _headerStyle, GUILayout.Width(ColGuard));
            GUILayout.Label("Alert", _headerStyle, GUILayout.Width(ColAlert));
            GUILayout.Label("Role", _headerStyle, GUILayout.Width(ColRole));
            GUILayout.Label("Scenario", _headerStyle, GUILayout.Width(ColScenario));
            GUILayout.Label("Goal", _headerStyle, GUILayout.Width(ColGoal));
            GUILayout.Label("Conf", _headerStyle, GUILayout.Width(ColConf));
            GUILayout.Label("LOS", _headerStyle, GUILayout.Width(ColLOS));
            GUILayout.Label("TSeen", _headerStyle, GUILayout.Width(ColTSeen));
            GUILayout.Label("Aware", _headerStyle, GUILayout.Width(ColAware));
            GUILayout.Label("CQB", _headerStyle, GUILayout.Width(ColCQB));
            GUILayout.Label("Cover", _headerStyle, GUILayout.Width(ColCover));
            GUILayout.Label("Wants", _headerStyle, GUILayout.Width(ColWants));
            EditorGUILayout.EndHorizontal();
        }

        // ---------- Table rows -----------------------------------------------

        private void DrawRows(IReadOnlyList<StealthHuntAI> units)
        {
            int row = 0;
            foreach (var u in units)
            {
                if (u == null) continue;
                if (_filterSquad >= 0 && u.squadID != _filterSquad) continue;
                if (u.IsDead && !_showDeadGuards) continue;

                var sc = u.GetComponent<StandardCombat>();
                var brain = TacticalBrain.GetOrCreate(u.squadID);
                var threat = brain.Intel.Threat;

                // Row background
                var rowBg = row % 2 == 0
                    ? new Color(0.18f, 0.18f, 0.18f)
                    : new Color(0.22f, 0.22f, 0.22f);
                if (u.IsDead) rowBg = new Color(0.12f, 0.12f, 0.12f);

                var prevBg = GUI.backgroundColor;
                GUI.backgroundColor = rowBg;
                EditorGUILayout.BeginHorizontal("box");
                GUI.backgroundColor = prevBg;

                // Guard name -- click to select
                if (GUILayout.Button(u.name, _cellStyle, GUILayout.Width(ColGuard)))
                    Selection.activeGameObject = u.gameObject;

                // Alert state
                Color alertCol = u.CurrentAlertState switch
                {
                    AlertState.Hostile => ColHostile,
                    AlertState.Suspicious => ColSuspicion,
                    _ => ColPassive,
                };
                GUI.color = u.IsDead ? Color.grey : alertCol;
                GUILayout.Label(
                    u.IsDead ? "DEAD" : u.CurrentAlertState.ToString(),
                    _cellStyle, GUILayout.Width(ColAlert));
                GUI.color = Color.white;

                // Role
                string roleName = sc != null ? sc.CurrentStateName : "-";
                Color roleCol = GetRoleColor(roleName);
                GUI.color = roleCol;
                GUILayout.Label(roleName, _cellStyle, GUILayout.Width(ColRole));
                GUI.color = Color.white;

                // Scenario
                var scenario = brain.Tactician.CurrentScenario;
                Color scenCol = scenario switch
                {
                    SquadTactician.TacticianScenario.Search => ColSearch,
                    SquadTactician.TacticianScenario.Approach => ColApproach,
                    SquadTactician.TacticianScenario.Assault => ColAssault,
                    SquadTactician.TacticianScenario.CQB => ColCQBColor,
                    SquadTactician.TacticianScenario.Withdraw => ColWithdraw,
                    _ => Color.white,
                };
                GUI.color = scenCol;
                GUILayout.Label(scenario.ToString(), _cellStyle,
                    GUILayout.Width(ColScenario));
                GUI.color = Color.white;

                // Goal
                string goal = sc != null ? sc.CurrentGoal.ToString() : "-";
                GUILayout.Label(goal, _cellStyle, GUILayout.Width(ColGoal));

                // Confidence
                float conf = threat?.Confidence ?? 0f;
                GUI.color = conf > 0.6f ? ColHighConf
                          : conf > 0.25f ? ColMidConf
                          : ColLowConf;
                GUILayout.Label(conf.ToString("F2"), _cellStyle,
                    GUILayout.Width(ColConf));
                GUI.color = Color.white;

                // LOS
                bool los = threat?.HasLOS ?? false;
                GUI.color = los ? ColLOSTrue : ColLOSFalse;
                GUILayout.Label(los ? "YES" : "no", _cellStyle,
                    GUILayout.Width(ColLOS));
                GUI.color = Color.white;

                // Time since seen
                float tseen = threat?.TimeSinceSeen ?? 999f;
                GUI.color = tseen < 3f ? ColHighConf
                          : tseen < 10f ? ColMidConf
                          : ColLowConf;
                GUILayout.Label(tseen >= 999f ? "--" : tseen.ToString("F1") + "s",
                    _cellStyle, GUILayout.Width(ColTSeen));
                GUI.color = Color.white;

                // Awareness
                float aware = u.Sensor?.AwarenessLevel ?? 0f;
                GUI.color = aware > 0.7f ? ColHighConf
                          : aware > 0.3f ? ColMidConf
                          : ColLowConf;
                GUILayout.Label(aware.ToString("F2"), _cellStyle,
                    GUILayout.Width(ColAware));
                GUI.color = Color.white;

                // CQB role
                string cqbRole = "-";
                if (brain.CQB.IsActive)
                {
                    var cqbR = brain.CQB.GetRole(u);
                    if (cqbR.HasValue)
                    {
                        cqbRole = cqbR.Value.IsBreacher ? "Breach"
                                : cqbR.Value.IsFollower ? "Follow"
                                : cqbR.Value.IsHolder ? "Hold"
                                : "?";
                    }
                    GUI.color = ColCQBColor;
                }
                GUILayout.Label(cqbRole, _cellStyle, GUILayout.Width(ColCQB));
                GUI.color = Color.white;

                // In cover
                bool inCover = sc?.IsInCover ?? false;
                GUI.color = inCover ? ColApproach : ColLOSFalse;
                GUILayout.Label(inCover ? "YES" : "no", _cellStyle,
                    GUILayout.Width(ColCover));
                GUI.color = Color.white;

                // WantsControl
                bool wants = sc?.WantsControl ?? false;
                GUI.color = wants ? ColApproach : ColLOSFalse;
                GUILayout.Label(wants ? "YES" : "no", _cellStyle,
                    GUILayout.Width(ColWants));
                GUI.color = Color.white;

                EditorGUILayout.EndHorizontal();
                row++;
            }
        }

        // ---------- Helpers --------------------------------------------------

        private Color GetRoleColor(string role) => role switch
        {
            "Advance" => new Color(1f, 0.6f, 0.2f),
            "Flank" => new Color(0.8f, 0.4f, 1f),
            "Suppress" => new Color(1f, 0.35f, 0.35f),
            "Cover" => new Color(0.4f, 0.8f, 0.4f),
            "Cautious" => new Color(0.9f, 0.9f, 0.3f),
            "Reposition" => new Color(0.6f, 0.8f, 1f),
            "Search" => new Color(0.9f, 0.9f, 0.3f),
            "Overwatch" => new Color(0.4f, 0.8f, 1f),
            "RearSecurity" => new Color(0.6f, 0.4f, 1f),
            "Breach" => new Color(1f, 0.35f, 0.35f),
            "Follow" => new Color(1f, 0.6f, 0.2f),
            "Withdraw" => new Color(0.7f, 0.4f, 1f),
            "CQB" => ColCQBColor,
            _ => Color.white,
        };

        private List<int> GetSquadIDs()
        {
            var ids = new List<int>();
            var seen = new HashSet<int>();
            if (!Application.isPlaying) return ids;
            var units = HuntDirector.AllUnits;
            if (units == null) return ids;
            foreach (var u in units)
            {
                if (u == null) continue;
                if (seen.Add(u.squadID)) ids.Add(u.squadID);
            }
            ids.Sort();
            return ids;
        }

        private void SelectAllHostile()
        {
            if (!Application.isPlaying) return;
            var units = HuntDirector.AllUnits;
            if (units == null) return;
            var gos = new List<GameObject>();
            foreach (var u in units)
                if (u != null && u.CurrentAlertState == AlertState.Hostile)
                    gos.Add(u.gameObject);
            Selection.objects = gos.ToArray();
        }

        private void InitStyles()
        {
            if (_stylesInit) return;
            _stylesInit = true;

            _headerStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 10,
                alignment = TextAnchor.MiddleLeft,
                normal = { textColor = new Color(0.7f, 0.7f, 0.7f) },
            };

            _cellStyle = new GUIStyle(EditorStyles.label)
            {
                fontSize = 10,
                alignment = TextAnchor.MiddleLeft,
                normal = { textColor = Color.white },
            };

            _dimStyle = new GUIStyle(EditorStyles.label)
            {
                fontSize = 10,
                alignment = TextAnchor.MiddleLeft,
                normal = { textColor = new Color(0.4f, 0.4f, 0.4f) },
            };
        }
    }
}
#endif