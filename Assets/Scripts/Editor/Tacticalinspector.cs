using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace StealthHuntAI.Editor
{
    /// <summary>
    /// Tactical Inspector -- live debug window for the tactical pipeline.
    /// Shows scorer breakdown, intel feed, candidate spots and squad status.
    /// Open via: StealthHuntAI > Tactical Inspector
    /// </summary>
    public class TacticalInspector : EditorWindow
    {
        [MenuItem("StealthHuntAI/Tactical Inspector")]
        public static void Open()
        {
            var win = GetWindow<TacticalInspector>("Tactical Inspector");
            win.minSize = new Vector2(380, 500);
        }

        // ---------- State ----------------------------------------------------

        private StealthHuntAI _selected;
        private Combat.TacticalSystem _sys;
        private Vector2 _scroll;
        private int _tab;
        private bool _followCam;
        private bool _showGizmos = true;
        private bool _showHeat;
        private readonly List<string> _feed = new List<string>();
        private double _lastFeedTime;

        private static readonly string[] TabLabels = { "Unit", "Squad", "Scorers", "Feed" };

        // ---------- Lifecycle ------------------------------------------------

        private void OnEnable()
        {
            EditorApplication.update += Repaint;
            SceneView.duringSceneGui += OnSceneGUI;
        }

        private void OnDisable()
        {
            EditorApplication.update -= Repaint;
            SceneView.duringSceneGui -= OnSceneGUI;
        }

        // ---------- GUI ------------------------------------------------------

        private void OnGUI()
        {
            if (!Application.isPlaying)
            {
                EditorGUILayout.HelpBox("Enter Play mode to use Tactical Inspector.", MessageType.Info);
                return;
            }

            _sys = Combat.TacticalSystem.Instance;

            DrawHeader();
            DrawUnitSelector();

            if (_selected == null)
            {
                EditorGUILayout.HelpBox("Select a guard with StandardCombat to inspect.", MessageType.None);
                return;
            }

            _tab = GUILayout.Toolbar(_tab, TabLabels);
            EditorGUILayout.Space(4);

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            switch (_tab)
            {
                case 0: DrawUnitTab(); break;
                case 1: DrawSquadTab(); break;
                case 2: DrawScorersTab(); break;
                case 3: DrawFeedTab(); break;
            }
            EditorGUILayout.EndScrollView();

            DrawControls();
        }

        // ---------- Header ---------------------------------------------------

        private void DrawHeader()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            GUILayout.Label("Tactical Inspector", EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();
            if (_sys != null)
            {
                GUILayout.Label("Queue: " + _sys.QueueDepth, EditorStyles.miniLabel);
                GUILayout.Space(8);
                GUILayout.Label("Providers: " + (_sys.Providers?.Count ?? 0), EditorStyles.miniLabel);
                GUILayout.Space(8);
                GUILayout.Label("Scorers: " + (_sys.Scorers?.Count ?? 0), EditorStyles.miniLabel);
            }
            else
            {
                GUILayout.Label("No TacticalSystem in scene", EditorStyles.miniLabel);
            }
            EditorGUILayout.EndHorizontal();
        }

        // ---------- Unit selector --------------------------------------------

        private void DrawUnitSelector()
        {
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("Guard", GUILayout.Width(40));

            var units = ThreatModelExtensions.GetAllUnits();
            if (units.Count == 0) { EditorGUILayout.EndHorizontal(); return; }

            // Build names array
            string[] names = new string[units.Count];
            int currentIdx = 0;
            for (int i = 0; i < units.Count; i++)
            {
                names[i] = units[i].name;
                if (units[i] == _selected) currentIdx = i;
            }

            int newIdx = EditorGUILayout.Popup(currentIdx, names);
            if (newIdx != currentIdx || _selected == null)
                _selected = units[Mathf.Clamp(newIdx, 0, units.Count - 1)];

            if (GUILayout.Button("Select", GUILayout.Width(52)))
                Selection.activeGameObject = _selected?.gameObject;

            EditorGUILayout.EndHorizontal();
            EditorGUILayout.Space(2);
        }

        // ---------- Unit tab -------------------------------------------------

        private void DrawUnitTab()
        {
            if (_selected == null) return;

            var sc = _selected.GetComponent<Combat.StandardCombat>();

            // Alert state
            DrawSection("Alert State", () =>
            {
                Color stateColor = _selected.CurrentAlertState switch
                {
                    AlertState.Hostile => new Color(1f, 0.25f, 0.25f),
                    AlertState.Suspicious => new Color(1f, 0.85f, 0.1f),
                    _ => new Color(0.4f, 0.9f, 0.4f)
                };

                GUI.color = stateColor;
                string combatState = sc != null && sc.WantsControl
                    ? "Combat / " + sc.CurrentStateName
                    : _selected.CurrentSubState.ToString();
                EditorGUILayout.LabelField(
                    _selected.CurrentAlertState + " / " + combatState,
                    EditorStyles.boldLabel);
                GUI.color = Color.white;

                EditorGUI.BeginDisabledGroup(true);
                EditorGUILayout.Slider("Awareness", _selected.AwarenessLevel, 0f, 1f);
                EditorGUI.EndDisabledGroup();

                EditorGUILayout.LabelField("Role", _selected.ActiveRole.ToString());
            });

            // Threat model
            if (sc != null)
            {
                var threat = GetThreat(sc);
                if (threat != null)
                {
                    DrawSection("Threat Model", () =>
                    {
                        EditorGUI.BeginDisabledGroup(true);
                        EditorGUILayout.Slider("Confidence", threat.Confidence, 0f, 1f);
                        EditorGUI.EndDisabledGroup();
                        EditorGUILayout.LabelField("Time Since Seen",
                            threat.TimeSinceSeen.ToString("F1") + "s");
                        EditorGUILayout.LabelField("Has LOS", threat.HasLOS.ToString());
                        EditorGUILayout.LabelField("Has Intel", threat.HasIntel.ToString());
                        EditorGUILayout.LabelField("Estimated Pos",
                            threat.EstimatedPosition.ToString("F1"));
                    });
                }
            }

            // Last spot
            if (sc?.CurrentSpot != null)
            {
                DrawSection("Current Spot", () =>
                {
                    var spot = sc.CurrentSpot;
                    EditorGUILayout.LabelField("Provider", spot.ProviderTag);
                    EditorGUILayout.LabelField("Score", spot.Score.ToString("F3"));
                    EditorGUILayout.LabelField("Position", spot.Position.ToString("F1"));
                    EditorGUILayout.LabelField("Has Cover", spot.HasHardCover.ToString());
                    EditorGUILayout.LabelField("In Shadow", spot.IsInShadow.ToString());
                    EditorGUILayout.LabelField("Dist To Threat",
                        spot.DistanceToThreat.ToString("F1") + "m");
                });
            }
        }

        // ---------- Squad tab ------------------------------------------------

        private void DrawSquadTab()
        {
            var units = ThreatModelExtensions.GetAllUnits();

            DrawSection("Squad Members (" + units.Count + ")", () =>
            {
                foreach (var u in units)
                {
                    if (u == null) continue;
                    bool isCurrent = u == _selected;

                    EditorGUILayout.BeginHorizontal();
                    if (isCurrent) GUI.color = new Color(0.6f, 0.9f, 1f);

                    EditorGUILayout.LabelField(u.name, GUILayout.Width(80));
                    GUI.color = u.CurrentAlertState == AlertState.Hostile
                        ? new Color(1f, 0.4f, 0.4f)
                        : Color.white;
                    EditorGUILayout.LabelField(u.CurrentAlertState.ToString(),
                        GUILayout.Width(70));
                    GUI.color = Color.white;

                    var sc = u.GetComponent<Combat.StandardCombat>();
                    string goalLabel = sc != null && sc.WantsControl
                        ? sc.CurrentStateName : "-";
                    EditorGUILayout.LabelField(goalLabel, GUILayout.Width(80));

                    // Awareness mini bar
                    Rect barRect = GUILayoutUtility.GetRect(60, 14);
                    EditorGUI.DrawRect(barRect, new Color(0.2f, 0.2f, 0.2f));
                    Rect fill = new Rect(barRect.x, barRect.y,
                        barRect.width * u.AwarenessLevel, barRect.height);
                    Color fillCol = u.AwarenessLevel > 0.7f
                        ? new Color(1f, 0.3f, 0.3f)
                        : u.AwarenessLevel > 0.3f
                            ? new Color(1f, 0.8f, 0.1f)
                            : new Color(0.3f, 0.8f, 0.3f);
                    EditorGUI.DrawRect(fill, fillCol);

                    EditorGUILayout.EndHorizontal();
                }
            });
        }

        // ---------- Scorers tab ----------------------------------------------

        private void DrawScorersTab()
        {
            if (_sys == null)
            {
                EditorGUILayout.HelpBox("No TacticalSystem found.", MessageType.Warning);
                return;
            }

            var sc = _selected?.GetComponent<Combat.StandardCombat>();
            var spot = sc?.CurrentSpot;

            DrawSection("Scorer Breakdown" + (spot != null ? " (current spot)" : " (no spot)"), () =>
            {
                if (_sys.Scorers == null) return;

                float totalWeight = 0f;
                foreach (var s in _sys.Scorers)
                    if (s.IsEnabled) totalWeight += s.Weight;

                foreach (var scorer in _sys.Scorers)
                {
                    EditorGUILayout.BeginHorizontal();

                    // Enabled toggle
                    bool enabled = EditorGUILayout.Toggle(scorer.IsEnabled, GUILayout.Width(16));
                    if (enabled != scorer.IsEnabled) scorer.IsEnabled = enabled;

                    // Name
                    EditorGUILayout.LabelField(scorer.Name, GUILayout.Width(160));

                    // Weight slider
                    float w = EditorGUILayout.Slider(scorer.Weight, 0f, 3f, GUILayout.Width(120));
                    if (!Mathf.Approximately(w, scorer.Weight)) scorer.Weight = w;

                    // Score bar if spot available
                    if (spot != null && spot.ScoreBreakdown.TryGetValue(scorer.Name, out float val))
                    {
                        Rect barRect = GUILayoutUtility.GetRect(60, 14);
                        EditorGUI.DrawRect(barRect, new Color(0.15f, 0.15f, 0.15f));
                        Color barCol = val > 0.6f
                            ? new Color(0.2f, 0.8f, 0.3f)
                            : val > 0.3f
                                ? new Color(0.9f, 0.7f, 0.1f)
                                : new Color(0.9f, 0.2f, 0.2f);
                        Rect fill = new Rect(barRect.x, barRect.y,
                            barRect.width * val, barRect.height);
                        EditorGUI.DrawRect(fill, barCol);
                        EditorGUILayout.LabelField(val.ToString("F2"), GUILayout.Width(36));
                    }
                    else
                    {
                        GUILayout.FlexibleSpace();
                    }

                    EditorGUILayout.EndHorizontal();
                }

                EditorGUILayout.Space(4);
                if (spot != null)
                {
                    EditorGUILayout.LabelField(
                        "Final Score: " + spot.Score.ToString("F3"),
                        EditorStyles.boldLabel);
                    if (!string.IsNullOrEmpty(spot.RejectionReason))
                        EditorGUILayout.LabelField(
                            "Worst scorer: " + spot.RejectionReason,
                            EditorStyles.miniLabel);
                }
            });

            DrawSection("All Candidates (last request)", () =>
            {
                var candidates = _sys.LastCandidates;
                if (candidates == null || candidates.Count == 0)
                {
                    EditorGUILayout.LabelField("No candidates yet.", EditorStyles.miniLabel);
                    return;
                }

                EditorGUILayout.LabelField(
                    candidates.Count + " candidates -- showing top 8",
                    EditorStyles.miniLabel);

                int shown = Mathf.Min(8, candidates.Count);
                for (int i = 0; i < shown; i++)
                {
                    var c = candidates[i];
                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField((i + 1) + ".", GUILayout.Width(20));
                    EditorGUILayout.LabelField(c.ProviderTag, GUILayout.Width(130));
                    EditorGUILayout.LabelField(c.Score.ToString("F3"), GUILayout.Width(48));
                    EditorGUILayout.LabelField(c.Position.ToString("F0"),
                        EditorStyles.miniLabel);
                    EditorGUILayout.EndHorizontal();
                }
            });
        }

        // ---------- Feed tab -------------------------------------------------

        private void DrawFeedTab()
        {
            if (_sys != null && _sys.LastRequest != null)
            {
                var req = _sys.LastRequest;
                if (req.IsComplete)
                {
                    string entry = "[" + req.Context.Unit?.name + "] "
                        + (req.BestSpot?.ProviderTag ?? "none")
                        + " score=" + (req.BestSpot?.Score.ToString("F2") ?? "0")
                        + " (" + req.ElapsedMs.ToString("F1") + "ms)";

                    if (_feed.Count == 0 || _feed[_feed.Count - 1] != entry)
                        _feed.Add(entry);

                    if (_feed.Count > 40) _feed.RemoveAt(0);
                }
            }

            DrawSection("Intel Feed (" + _feed.Count + " entries)", () =>
            {
                if (_feed.Count == 0)
                {
                    EditorGUILayout.LabelField("Waiting for requests...", EditorStyles.miniLabel);
                    return;
                }

                for (int i = _feed.Count - 1; i >= Mathf.Max(0, _feed.Count - 20); i--)
                    EditorGUILayout.LabelField(_feed[i], EditorStyles.miniLabel);
            });

            if (GUILayout.Button("Clear Feed"))
                _feed.Clear();
        }

        // ---------- Controls -------------------------------------------------

        private void DrawControls()
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            bool newFollow = GUILayout.Toggle(_followCam, "Follow Cam",
                EditorStyles.toolbarButton, GUILayout.Width(80));
            if (newFollow != _followCam)
            {
                _followCam = newFollow;
                if (_followCam && _selected != null)
                    SceneView.lastActiveSceneView?.LookAt(_selected.transform.position);
            }

            bool newGizmos = GUILayout.Toggle(_showGizmos, "Gizmos",
                EditorStyles.toolbarButton, GUILayout.Width(60));
            _showGizmos = newGizmos;

            bool newHeat = GUILayout.Toggle(_showHeat, "Heatmap",
                EditorStyles.toolbarButton, GUILayout.Width(65));
            _showHeat = newHeat;

            GUILayout.FlexibleSpace();

            if (GUILayout.Button("Rebuild Pipeline",
                EditorStyles.toolbarButton, GUILayout.Width(110)))
            {
                _sys?.SendMessage("BuildDefaultPipeline",
                    SendMessageOptions.DontRequireReceiver);
            }

            EditorGUILayout.EndHorizontal();
        }

        // ---------- Scene gizmos ---------------------------------------------

        private void OnSceneGUI(SceneView sv)
        {
            if (!Application.isPlaying || !_showGizmos) return;

            var candidates = _sys?.LastCandidates;
            if (candidates == null) return;

            for (int i = 0; i < candidates.Count; i++)
            {
                var spot = candidates[i];
                bool isBest = (spot == _sys?.LastBestSpot);

                float t = Mathf.Clamp01(spot.Score);
                Color col = Color.Lerp(
                    new Color(0.9f, 0.2f, 0.2f, 0.6f),
                    new Color(0.2f, 0.9f, 0.3f, 0.8f),
                    t);

                if (isBest) col = new Color(0.1f, 0.6f, 1f, 1f);

                Handles.color = col;
                Handles.SphereHandleCap(0, spot.Position,
                    Quaternion.identity, isBest ? 0.4f : 0.25f, EventType.Repaint);

                if (isBest || i < 5)
                {
                    Handles.Label(spot.Position + Vector3.up * 0.5f,
                        spot.ProviderTag.Replace("Provider", "") +
                        " " + spot.Score.ToString("F2"),
                        EditorStyles.miniLabel);
                }
            }

            // Draw line from selected unit to best spot
            if (_selected != null && _sys?.LastBestSpot != null)
            {
                Handles.color = new Color(0.1f, 0.6f, 1f, 0.5f);
                Handles.DrawDottedLine(_selected.transform.position,
                    _sys.LastBestSpot.Position, 4f);
            }

            // Follow cam
            if (_followCam && _selected != null)
                sv.LookAt(_selected.transform.position,
                    sv.rotation, sv.size, false, false);

            // Heatmap
            if (_showHeat) DrawHeatmap();
        }

        private void DrawHeatmap()
        {
            // Sample heat in a grid around selected unit
            if (_selected == null) return;
            Vector3 center = _selected.transform.position;
            float range = 15f;
            int res = 8;

            for (int x = 0; x < res; x++)
                for (int z = 0; z < res; z++)
                {
                    float wx = center.x - range + x * (range * 2f / res);
                    float wz = center.z - range + z * (range * 2f / res);
                    var pos = new Vector3(wx, center.y, wz);
                    float heat = HuntDirector.GetHeat(pos);
                    if (heat < 0.05f) continue;

                    Handles.color = new Color(1f, 0.3f, 0.1f, heat * 0.5f);
                    Handles.DrawSolidRectangleWithOutline(
                        new[]
                        {
                        pos + new Vector3(-1f, 0.05f, -1f),
                        pos + new Vector3( 1f, 0.05f, -1f),
                        pos + new Vector3( 1f, 0.05f,  1f),
                        pos + new Vector3(-1f, 0.05f,  1f),
                        },
                        new Color(1f, 0.3f, 0.1f, heat * 0.4f),
                        Color.clear);
                }
        }

        // ---------- Helpers --------------------------------------------------

        private void DrawSection(string title, System.Action content)
        {
            EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
            EditorGUI.indentLevel++;
            content();
            EditorGUI.indentLevel--;
            EditorGUILayout.Space(6);
        }

        private Combat.ThreatModel GetThreat(Combat.StandardCombat sc)
        {
            // Access via reflection -- ThreatModel is private
            var field = typeof(Combat.StandardCombat)
                .GetField("_threat",
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance);
            return field?.GetValue(sc) as Combat.ThreatModel;
        }
    }

    /// <summary>
    /// Helper to get all units without Combat assembly dependency in Editor.
    /// </summary>
    internal static class ThreatModelExtensions
    {
        public static List<StealthHuntAI> GetAllUnits()
        {
            var result = new List<StealthHuntAI>();
            var units = HuntDirector.AllUnits;
            for (int i = 0; i < units.Count; i++)
                if (units[i] != null) result.Add(units[i]);
            return result;
        }
    }
}