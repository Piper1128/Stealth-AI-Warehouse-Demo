using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using System.Linq;

namespace StealthHuntAI.Editor
{
    /// <summary>
    /// Compact HUD overlay -- shows all guards as a live status list.
    /// Inspired by Blaze AI's unit overview panel.
    /// Open via: StealthHuntAI > Tactical HUD
    /// Dock it in a corner -- stays small and out of the way.
    /// </summary>
    public class TacticalHUD : EditorWindow
    {
        [MenuItem("StealthHuntAI/Tactical HUD")]
        public static void Open()
        {
            var win = GetWindow<TacticalHUD>("Tactical HUD");
            win.minSize = new Vector2(280, 100);
            win.maxSize = new Vector2(320, 800);
        }

        private Vector2 _scroll;

        // ---------- Colors ---------------------------------------------------

        private static readonly Color ColPassive = new Color(0.4f, 0.8f, 0.4f);
        private static readonly Color ColSuspicious = new Color(1f, 0.8f, 0.1f);
        private static readonly Color ColHostile = new Color(1f, 0.3f, 0.3f);
        private static readonly Color ColDead = new Color(0.4f, 0.4f, 0.4f);
        private static readonly Color ColBackground = new Color(0.15f, 0.15f, 0.15f);

        // ---------- Styles ---------------------------------------------------

        private GUIStyle _rowStyle;
        private GUIStyle _nameStyle;
        private GUIStyle _stateStyle;
        private GUIStyle _actionStyle;
        private bool _stylesInit;

        private void InitStyles()
        {
            if (_stylesInit) return;
            _stylesInit = true;

            _rowStyle = new GUIStyle(GUI.skin.box)
            {
                padding = new RectOffset(4, 4, 2, 2),
                margin = new RectOffset(2, 2, 1, 1),
            };

            _nameStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                fontStyle = FontStyle.Bold,
                fixedWidth = 80,
            };

            _stateStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                fixedWidth = 70,
                fontStyle = FontStyle.Bold,
            };

            _actionStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                fixedWidth = 100,
            };
        }

        private void OnGUI()
        {
            if (!Application.isPlaying)
            {
                EditorGUILayout.HelpBox("Enter Play Mode to see live data.", MessageType.Info);
                return;
            }

            InitStyles();

            var units = HuntDirector.AllUnits;
            if (units == null || units.Count == 0)
            {
                EditorGUILayout.LabelField("No units registered.", EditorStyles.miniLabel);
                return;
            }

            // Header
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            EditorGUILayout.LabelField("Unit", EditorStyles.toolbarButton, GUILayout.Width(80));
            EditorGUILayout.LabelField("State", EditorStyles.toolbarButton, GUILayout.Width(70));
            EditorGUILayout.LabelField("Action", EditorStyles.toolbarButton, GUILayout.Width(100));
            EditorGUILayout.LabelField("Conf", EditorStyles.toolbarButton, GUILayout.Width(36));
            EditorGUILayout.EndHorizontal();

            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            // Group by squadID
            var bySquad = units
                .Where(u => u != null)
                .GroupBy(u => u.squadID)
                .OrderBy(g => g.Key);

            foreach (var squad in bySquad)
            {
                // Squad header
                GUI.color = new Color(0.5f, 0.6f, 0.8f);
                EditorGUILayout.LabelField($"?? Squad {squad.Key} ??", EditorStyles.miniLabel);
                GUI.color = Color.white;

                foreach (var unit in squad.OrderBy(u => u.name))
                {
                    DrawUnitRow(unit);
                }
            }

            EditorGUILayout.EndScrollView();

            // Auto-repaint during play
            if (Application.isPlaying)
                Repaint();
        }

        private void DrawUnitRow(StealthHuntAI unit)
        {
            bool isDead = unit.IsDead;

            // Row color based on state
            Color stateColor = isDead ? ColDead :
                unit.CurrentAlertState switch
                {
                    AlertState.Hostile => ColHostile,
                    AlertState.Suspicious => ColSuspicious,
                    _ => ColPassive,
                };

            // Background tint
            var bg = new Color(stateColor.r * 0.15f, stateColor.g * 0.15f,
                               stateColor.b * 0.15f, 1f);

            var rect = EditorGUILayout.BeginHorizontal();
            EditorGUI.DrawRect(rect, bg);

            // State dot
            GUI.color = stateColor;
            EditorGUILayout.LabelField("?", GUILayout.Width(14));
            GUI.color = Color.white;

            // Name -- click to select
            if (GUILayout.Button(unit.name, _nameStyle, GUILayout.Width(80)))
            {
                Selection.activeGameObject = unit.gameObject;
                // Also select in TacticalInspector if open
                SceneView.lastActiveSceneView?.FrameSelected();
            }

            // Alert state
            GUI.color = stateColor;
            string stateLabel = isDead ? "Dead" :
                unit.CurrentAlertState switch
                {
                    AlertState.Hostile => "HOSTILE",
                    AlertState.Suspicious => "Suspicious",
                    _ => "Passive",
                };
            EditorGUILayout.LabelField(stateLabel, _stateStyle, GUILayout.Width(70));
            GUI.color = Color.white;

            // Current action
            var sc = unit.GetComponent<Combat.StandardCombat>();
            string action = sc != null && sc.WantsControl
                ? sc.CurrentStateName
                : unit.CurrentSubState.ToString();
            EditorGUILayout.LabelField(action, _actionStyle, GUILayout.Width(100));

            // Confidence bar
            float conf = 0f;
            if (sc != null)
            {
                var threat = GetThreat(sc);
                if (threat != null) conf = threat.Confidence;
            }
            DrawMiniBar(conf, stateColor);

            EditorGUILayout.EndHorizontal();
        }

        private void DrawMiniBar(float value, Color col)
        {
            var rect = GUILayoutUtility.GetRect(36, 10,
                GUILayout.Width(36), GUILayout.Height(10));

            EditorGUI.DrawRect(rect, new Color(0.1f, 0.1f, 0.1f));
            var fill = new Rect(rect.x, rect.y, rect.width * value, rect.height);
            EditorGUI.DrawRect(fill, new Color(col.r * 0.8f, col.g * 0.8f, col.b * 0.8f));
        }

        // ---------- Reflection helper ----------------------------------------

        private Combat.ThreatModel GetThreat(Combat.StandardCombat sc)
        {
            var field = typeof(Combat.StandardCombat)
                .GetField("_threat",
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance);
            return field?.GetValue(sc) as Combat.ThreatModel;
        }
    }
}