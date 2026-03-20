using System.Collections.Generic;
using UnityEngine;
using UnityEditor;

namespace StealthHuntAI.Editor
{
    [CustomEditor(typeof(StealthHuntAI))]
    public class StealthHuntAI_Inspector : UnityEditor.Editor
    {
        // ---------- Foldout state ---------------------------------------------

        private bool _foldPerception = true;
        private bool _foldThresholds = true;
        private bool _foldBehaviour = true;
        private bool _foldGuardZone = false;
        private bool _foldSquad = true;
        private bool _foldAnimation = false;
        private bool _foldEvents = false;
        private bool _foldMorale = false;
        private bool _foldCombat = false;
        private bool _foldAdvanced = false;

        // ---------- Styles ----------------------------------------------------

        private GUIStyle _headerStyle;
        private GUIStyle _subStateStyle;
        private bool _stylesBuilt;

        // ---------- Colors ----------------------------------------------------

        private static readonly Color ColPassive = new Color(0.25f, 0.75f, 0.35f);
        private static readonly Color ColSuspicious = new Color(0.95f, 0.75f, 0.10f);
        private static readonly Color ColHostile = new Color(0.90f, 0.20f, 0.15f);
        private static readonly Color ColBarBg = new Color(0.12f, 0.12f, 0.12f);
        private static readonly Color ColSection = new Color(0.20f, 0.20f, 0.20f);

        // ---------- Serialized properties ------------------------------------

        private SerializedProperty _sightRange;
        private SerializedProperty _sightAngle;
        private SerializedProperty _hearingRange;
        private SerializedProperty _preset;
        private SerializedProperty _combatOverride;
        private SerializedProperty _sightDetectionSpeed;
        private SerializedProperty _sightDecaySpeed;
        private SerializedProperty _suspicionThreshold;
        private SerializedProperty _hostileThreshold;
        private SerializedProperty _personality;
        private SerializedProperty _behaviourMode;
        private SerializedProperty _patrolPoints;
        private SerializedProperty _patrolPattern;
        private SerializedProperty _waypointWaitTime;
        private SerializedProperty _guardZoneCenter;
        private SerializedProperty _guardZoneRadius;
        private SerializedProperty _guardZoneWaypointCount;
        private SerializedProperty _guardZoneWaitTime;
        private SerializedProperty _searchDuration;
        private SerializedProperty _searchRadius;
        private SerializedProperty _searchPointCount;
        private SerializedProperty _searchHeightRange;
        private SerializedProperty _patrolSpeedMultiplier;
        private SerializedProperty _chaseSpeedMultiplier;
        private SerializedProperty _squadID;
        private SerializedProperty _manualRole;
        private SerializedProperty _movementProvider;
        private SerializedProperty _searchStrategyOverride;
        private SerializedProperty _visitedCellSize;
        private SerializedProperty _animator;
        private SerializedProperty _animClipAssignments;
        private SerializedProperty _animTransitionDuration;
        private string[] _animClipNames = new string[0];
        private double _lastClipRefresh;
        private SerializedProperty _onBecameSuspicious;
        private SerializedProperty _onBecameHostile;
        private SerializedProperty _onLostTarget;
        private SerializedProperty _onReturnedToPassive;
        private SerializedProperty _startingMorale;
        private SerializedProperty _persistMorale;

        private void OnEnable()
        {
            if (Application.isPlaying)
                EditorApplication.update += Repaint;

            _sightRange = serializedObject.FindProperty("sightRange");
            _sightAngle = serializedObject.FindProperty("sightAngle");
            _hearingRange = serializedObject.FindProperty("hearingRange");
            _preset = serializedObject.FindProperty("preset");
            _combatOverride = serializedObject.FindProperty("combatBehaviourOverride");
            _sightDetectionSpeed = serializedObject.FindProperty("sightDetectionSpeed");
            _sightDecaySpeed = serializedObject.FindProperty("sightDecaySpeed");
            _suspicionThreshold = serializedObject.FindProperty("suspicionThreshold");
            _hostileThreshold = serializedObject.FindProperty("hostileThreshold");
            _personality = serializedObject.FindProperty("personality");
            _behaviourMode = serializedObject.FindProperty("behaviourMode");
            _patrolPoints = serializedObject.FindProperty("patrolPoints");
            _patrolPattern = serializedObject.FindProperty("patrolPattern");
            _waypointWaitTime = serializedObject.FindProperty("waypointWaitTime");
            _guardZoneCenter = serializedObject.FindProperty("guardZoneCenter");
            _guardZoneRadius = serializedObject.FindProperty("guardZoneRadius");
            _guardZoneWaypointCount = serializedObject.FindProperty("guardZoneWaypointCount");
            _guardZoneWaitTime = serializedObject.FindProperty("guardZoneWaitTime");
            _searchDuration = serializedObject.FindProperty("searchDuration");
            _searchRadius = serializedObject.FindProperty("searchRadius");
            _searchPointCount = serializedObject.FindProperty("searchPointCount");
            _searchHeightRange = serializedObject.FindProperty("searchHeightRange");
            _patrolSpeedMultiplier = serializedObject.FindProperty("patrolSpeedMultiplier");
            _chaseSpeedMultiplier = serializedObject.FindProperty("chaseSpeedMultiplier");
            _squadID = serializedObject.FindProperty("squadID");
            _manualRole = serializedObject.FindProperty("manualRole");
            _movementProvider = serializedObject.FindProperty("movementProvider");
            _searchStrategyOverride = serializedObject.FindProperty("searchStrategyOverride");
            _visitedCellSize = serializedObject.FindProperty("visitedCellSize");
            _animator = serializedObject.FindProperty("animator");
            _animClipAssignments = serializedObject.FindProperty("animSlots");
            _animTransitionDuration = serializedObject.FindProperty("animTransitionDuration");
            _onBecameSuspicious = serializedObject.FindProperty("onBecameSuspicious");
            _onBecameHostile = serializedObject.FindProperty("onBecameHostile");
            _onLostTarget = serializedObject.FindProperty("onLostTarget");
            _onReturnedToPassive = serializedObject.FindProperty("onReturnedToPassive");
            _startingMorale = serializedObject.FindProperty("startingMorale");
            _persistMorale = serializedObject.FindProperty("persistMorale");
        }

        private void OnDisable()
        {
            EditorApplication.update -= Repaint;
        }

        // ---------- Main draw -------------------------------------------------

        public override void OnInspectorGUI()
        {
            BuildStyles();
            serializedObject.Update();
            EditorGUI.BeginChangeCheck();

            var ai = (StealthHuntAI)target;

            DrawStateHeader(ai);
            EditorGUILayout.Space(4);

            // Preset section at top
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PropertyField(_preset, new GUIContent("Preset",
                "Drag a StealthAIPreset asset here to configure all settings at once."));
            GUI.enabled = _preset.objectReferenceValue != null;
            if (GUILayout.Button("Apply", GUILayout.Width(60)))
            {
                serializedObject.ApplyModifiedProperties();
                foreach (var t in targets)
                    ((StealthHuntAI)t).ApplyPreset();
            }
            GUI.enabled = true;
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();
            EditorGUILayout.Space(4);

            DrawSection("Perception", ref _foldPerception, () => DrawPerception(ai));
            DrawSection("Thresholds", ref _foldThresholds, () => DrawThresholds(ai));
            DrawSection("Behaviour", ref _foldBehaviour, () => DrawBehaviour(ai));
            DrawSection("Guard Zone", ref _foldGuardZone, () => DrawGuardZone());
            DrawSection("Squad", ref _foldSquad, () => DrawSquad(ai));
            DrawSection("Animation", ref _foldAnimation, () => DrawAnimation(ai));
            DrawSection("Events", ref _foldEvents, () => DrawEvents());
            DrawSection("Morale", ref _foldMorale, () => DrawMorale(ai));
            DrawSection("Combat Pack", ref _foldCombat, () => DrawCombat());
            DrawSection("Advanced", ref _foldAdvanced, () => DrawAdvanced());

            if (Application.isPlaying)
            {
                EditorGUILayout.Space(6);
                DrawTestButtons(ai);
            }

            if (EditorGUI.EndChangeCheck())
                serializedObject.ApplyModifiedProperties();

            if (Application.isPlaying)
                Repaint();
        }

        // ---------- State header ----------------------------------------------

        private void DrawStateHeader(StealthHuntAI ai)
        {
            if (!Application.isPlaying)
            {
                EditorGUILayout.HelpBox(
                    "StealthHuntAI -- Enter Play Mode to see live state.",
                    MessageType.None);
                return;
            }

            Color stateColor;
            switch (ai.CurrentAlertState)
            {
                case AlertState.Suspicious: stateColor = ColSuspicious; break;
                case AlertState.Hostile: stateColor = ColHostile; break;
                default: stateColor = ColPassive; break;
            }

            Rect banner = GUILayoutUtility.GetRect(0, 50, GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(banner, stateColor * 0.55f);

            GUI.color = Color.white;
            GUI.Label(new Rect(banner.x + 8, banner.y + 5, banner.width - 16, 22),
                      ai.CurrentAlertState.ToString(), _headerStyle);
            GUI.Label(new Rect(banner.x + 8, banner.y + 28, banner.width - 16, 16),
                      ai.CurrentSubState.ToString() +
                      "  [" + ai.ActiveRole.ToString() + "]  " +
                      "Floor: " + ai.CurrentFloorID,
                      _subStateStyle);

            EditorGUILayout.Space(2);

            DrawBar("Awareness", ai.AwarenessLevel,
                    Color.Lerp(ColPassive, ColHostile, ai.AwarenessLevel));
            DrawThresholdTicks(ai.suspicionThreshold, ai.hostileThreshold);

            // Sight accumulator bar
            var sensor = ai.GetComponent<AwarenessSensor>();
            if (sensor != null)
            {
                DrawBar("Sight Exp.", sensor.SightAccumulator,
                        new Color(1f, 0.9f, 0.2f));
            }

            // Morale bar
            Color moraleColor;
            switch (ai.CurrentMorale)
            {
                case MoraleState.High: moraleColor = new Color(0.3f, 0.8f, 1.0f); break;
                case MoraleState.Medium: moraleColor = new Color(0.9f, 0.7f, 0.2f); break;
                default: moraleColor = new Color(0.8f, 0.3f, 0.8f); break;
            }
            DrawBar("Morale  [" + ai.CurrentMorale + "]", ai.MoraleLevel, moraleColor);
        }

        // ---------- Bar helpers -----------------------------------------------

        private void DrawBar(string label, float value, Color fill)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(label, GUILayout.Width(88));

            Rect bar = GUILayoutUtility.GetRect(0, 16, GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(bar, ColBarBg);
            EditorGUI.DrawRect(new Rect(bar.x, bar.y,
                               bar.width * Mathf.Clamp01(value), bar.height), fill);
            GUI.Label(new Rect(bar.x + 4, bar.y, 50, bar.height),
                      value.ToString("F2"), EditorStyles.miniLabel);

            EditorGUILayout.EndHorizontal();
        }

        private void DrawThresholdTicks(float suspicion, float hostile)
        {
            Rect area = GUILayoutUtility.GetRect(0, 8, GUILayout.ExpandWidth(true));
            float barStart = area.x + 92;
            float barWidth = area.width - 92;

            float sx = barStart + barWidth * suspicion;
            float hx = barStart + barWidth * hostile;

            EditorGUI.DrawRect(new Rect(sx - 1, area.y, 2, 8), ColSuspicious);
            EditorGUI.DrawRect(new Rect(hx - 1, area.y, 2, 8), ColHostile);

            GUI.color = ColSuspicious;
            GUI.Label(new Rect(sx - 4, area.y, 20, 8), "S", EditorStyles.miniLabel);
            GUI.color = ColHostile;
            GUI.Label(new Rect(hx - 4, area.y, 20, 8), "H", EditorStyles.miniLabel);
            GUI.color = Color.white;
        }

        // ---------- Section wrapper -------------------------------------------

        private void DrawSection(string title, ref bool foldout, System.Action draw)
        {
            Rect hdr = GUILayoutUtility.GetRect(0, 22, GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(hdr, ColSection);
            foldout = EditorGUI.Foldout(
                new Rect(hdr.x + 4, hdr.y + 3, hdr.width - 8, 16),
                foldout, title, true, EditorStyles.boldLabel);

            if (!foldout) return;

            EditorGUI.indentLevel++;
            EditorGUILayout.Space(2);
            draw();
            EditorGUI.indentLevel--;
            EditorGUILayout.Space(4);
        }

        // ---------- Perception ------------------------------------------------

        private void DrawPerception(StealthHuntAI ai)
        {
            EditorGUILayout.PropertyField(_sightRange,
                new GUIContent("Sight Range", "Max distance for cone detection"));
            EditorGUILayout.PropertyField(_sightAngle,
                new GUIContent("Sight Angle", "Full FOV angle in degrees"));
            EditorGUILayout.PropertyField(_hearingRange,
                new GUIContent("Hearing Range", "Radius for sound detection"));
            EditorGUILayout.PropertyField(_sightDetectionSpeed,
                new GUIContent("Detection Speed",
                    "How fast sight exposure builds. 1.5 = ~0.67s at full visibility. " +
                    "Hostile is 8x faster, Suspicious 3x."));
            EditorGUILayout.PropertyField(_sightDecaySpeed,
                new GUIContent("Sight Decay Speed",
                    "How fast sight exposure drains when target leaves FOV. " +
                    "Lower = exposure persists longer after brief cover."));

            EditorGUILayout.Space(4);

            var sensor = ai.GetComponent<AwarenessSensor>();

            if (sensor == null)
            {
                EditorGUILayout.HelpBox(
                    "AwarenessSensor not yet created. Press Play once to auto-configure.",
                    MessageType.Info);
                return;
            }

            var so = new SerializedObject(sensor);
            so.Update();

            EditorGUILayout.LabelField("Layer Masks", EditorStyles.boldLabel);

            var propBlockers = so.FindProperty("sightBlockers");
            var propTargets = so.FindProperty("targetLayers");

            EditorGUILayout.PropertyField(propBlockers,
                new GUIContent("Sight Blockers",
                    "Geometry layers that block line-of-sight raycasts. " +
                    "Do NOT include character or unit layers here."));

            EditorGUILayout.PropertyField(propTargets,
                new GUIContent("Target Layers",
                    "Layers used for hearing detection. " +
                    "Should include the layer your player is on."));

            bool allBlockers = propBlockers.intValue == -1;
            bool allTargets = propTargets.intValue == -1;

            if (allBlockers || allTargets)
            {
                EditorGUILayout.HelpBox(
                    "One or more masks are set to Everything. " +
                    "Use Auto-Configure to set recommended layers. " +
                    "Character layers in Sight Blockers will prevent detection.",
                    MessageType.Warning);
            }

            GUI.backgroundColor = new Color(0.4f, 0.8f, 1f);
            if (GUILayout.Button("Auto-Configure Layer Masks", GUILayout.Height(24)))
                RunAutoConfigureLayers(so);
            GUI.backgroundColor = Color.white;

            so.ApplyModifiedProperties();

            // Live sensor debug in play mode
            if (Application.isPlaying)
            {
                EditorGUILayout.Space(4);
                EditorGUILayout.LabelField("Live Sensor", EditorStyles.boldLabel);

                EditorGUI.BeginDisabledGroup(true);
                EditorGUILayout.Toggle("Can See Target", sensor.CanSeeTarget);
                EditorGUILayout.Toggle("Can Hear Target", sensor.CanHearTarget);
                EditorGUILayout.FloatField("Partial Visibility",
                    sensor.PartialVisibility);
                EditorGUILayout.FloatField("Stimulus Confidence",
                    sensor.StimulusConfidence);
                EditorGUILayout.FloatField("Uncertainty Radius",
                    sensor.PositionUncertaintyRadius);
                EditorGUI.EndDisabledGroup();
            }
        }

        // ---------- Auto-configure layers -------------------------------------

        private void RunAutoConfigureLayers(SerializedObject sensorSO)
        {
            // These are NEVER added to sightBlockers -- characters pass through sight
            string[] neverBlockNames = {
                "Ignore Raycast", "UI", "TransparentFX", "Water",
                "Trigger", "Triggers", "PostProcessing",
                "Player", "Unit", "Units", "Character", "Characters",
                "Enemy", "Enemies", "NPC", "Agent", "Pawn"
            };

            // Definitely geometry
            string[] geometryNames = {
                "Default", "Environment", "Terrain", "Ground",
                "Wall", "Walls", "Props", "Architecture",
                "Static", "Level", "World", "Obstacle", "Obstacles",
                "Building", "Structure", "Scenery"
            };

            // Character / target layers
            string[] characterNames = {
                "Player", "Unit", "Units", "Character", "Characters",
                "Enemy", "Enemies", "NPC", "Agent", "Pawn"
            };

            int blockers = 0;
            int targets = 0;

            bool foundChar = false;

            var blockerList = new System.Collections.Generic.List<string>();
            var targetList = new System.Collections.Generic.List<string>();
            var skippedList = new System.Collections.Generic.List<string>();

            for (int i = 0; i < 32; i++)
            {
                string ln = LayerMask.LayerToName(i);
                if (string.IsNullOrEmpty(ln)) continue;

                string lnLower = ln.ToLower();

                bool isNeverBlock = false;
                foreach (string n in neverBlockNames)
                {
                    if (lnLower.Contains(n.ToLower())) { isNeverBlock = true; break; }
                }

                if (isNeverBlock)
                {
                    skippedList.Add(ln);

                    foreach (string c in characterNames)
                    {
                        if (lnLower.Contains(c.ToLower()))
                        {
                            targets |= (1 << i);
                            foundChar = true;
                            targetList.Add(ln);
                            break;
                        }
                    }
                    continue;
                }

                bool isGeom = false;
                foreach (string g in geometryNames)
                {
                    if (lnLower.Contains(g.ToLower())) { isGeom = true; break; }
                }

                blockers |= (1 << i);

                blockerList.Add(ln + (isGeom ? "" : " (unknown)"));
            }

            // Always include Default layer
            if ((blockers & 1) == 0)
            {
                blockers |= 1;
                blockerList.Add("Default (added)");
            }

            sensorSO.FindProperty("sightBlockers").intValue = blockers;
            sensorSO.FindProperty("targetLayers").intValue =
                foundChar ? targets : (int)Physics.DefaultRaycastLayers;
            sensorSO.FindProperty("_layerMasksConfigured").boolValue = true;

            sensorSO.ApplyModifiedProperties();

            string bStr = blockerList.Count > 0 ? string.Join(", ", blockerList) : "none";
            string tStr = targetList.Count > 0 ? string.Join(", ", targetList) : "none -- using Default";
            string sStr = skippedList.Count > 0 ? string.Join(", ", skippedList) : "none";

            EditorUtility.DisplayDialog(
                "StealthHuntAI Layer Config",
                "Sight Blockers (geometry):\n" + bStr +
                "\n\nTarget Layers (characters):\n" + tStr +
                "\n\nSkipped (will not block sight):\n" + sStr +
                "\n\nReview and adjust manually if needed.",
                "OK");
        }

        // ---------- Thresholds ------------------------------------------------

        private void DrawThresholds(StealthHuntAI ai)
        {
            EditorGUILayout.PropertyField(_suspicionThreshold,
                new GUIContent("Suspicion", "Awareness that triggers Suspicious state"));
            EditorGUILayout.PropertyField(_hostileThreshold,
                new GUIContent("Hostile", "Awareness that triggers Hostile state"));

            if (_suspicionThreshold.floatValue >= _hostileThreshold.floatValue)
                EditorGUILayout.HelpBox(
                    "Suspicion threshold must be lower than Hostile threshold.",
                    MessageType.Warning);
        }

        // ---------- Behaviour -------------------------------------------------

        private void DrawBehaviour(StealthHuntAI ai)
        {
            EditorGUILayout.PropertyField(_personality, new GUIContent("Personality"));
            EditorGUILayout.PropertyField(_behaviourMode, new GUIContent("Behaviour Mode"));

            int mode = _behaviourMode.enumValueIndex;

            if (mode == (int)BehaviourMode.Patrol)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(_patrolPoints,
                    new GUIContent("Patrol Points", "Waypoints to follow in order"));
                EditorGUILayout.PropertyField(_patrolPattern,
                    new GUIContent("Pattern", "Loop or PingPong"));
                EditorGUILayout.PropertyField(_waypointWaitTime,
                    new GUIContent("Wait Time", "Seconds to wait at each waypoint"));
                EditorGUI.indentLevel--;
            }

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Speed", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(_patrolSpeedMultiplier,
                new GUIContent("Patrol Speed", "Speed multiplier when patrolling (0.55 = slower than chase)"));
            EditorGUILayout.PropertyField(_chaseSpeedMultiplier,
                new GUIContent("Chase Speed", "Speed multiplier when pursuing or lost target"));

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Search", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(_searchDuration,
                new GUIContent("Search Duration", "Seconds to search before giving up"));
            EditorGUILayout.PropertyField(_searchRadius,
                new GUIContent("Search Radius", "Max search area radius in metres"));
            EditorGUILayout.PropertyField(_searchPointCount,
                new GUIContent("Search Points", "Waypoints generated per search pass"));
            EditorGUILayout.PropertyField(_searchHeightRange,
                new GUIContent("Height Range", "Vertical NavMesh sampling range. 0 = auto (3m). Increase for tall multi-floor levels."));
        }

        // ---------- Guard Zone ------------------------------------------------

        private void DrawGuardZone()
        {
            int mode = _behaviourMode.enumValueIndex;

            if (mode != (int)BehaviourMode.GuardZone)
            {
                EditorGUILayout.HelpBox(
                    "Set Behaviour Mode to Guard Zone to activate these settings.",
                    MessageType.None);
                return;
            }

            EditorGUILayout.PropertyField(_guardZoneCenter,
                new GUIContent("Zone Center",
                    "Center of the guarded area. Leave empty to use spawn position."));
            EditorGUILayout.PropertyField(_guardZoneRadius,
                new GUIContent("Zone Radius", "Radius of the guarded area in metres"));
            EditorGUILayout.PropertyField(_guardZoneWaypointCount,
                new GUIContent("Waypoint Count",
                    "Auto-generated waypoints inside the zone. 0 = static guard."));
            EditorGUILayout.PropertyField(_guardZoneWaitTime,
                new GUIContent("Wait Time", "Seconds to wait at each zone waypoint"));
        }

        // ---------- Squad -----------------------------------------------------

        private void DrawSquad(StealthHuntAI ai)
        {
            EditorGUILayout.PropertyField(_squadID,
                new GUIContent("Squad ID", "0 = auto-assigned by HuntDirector"));
            EditorGUILayout.PropertyField(_manualRole,
                new GUIContent("Manual Role",
                    "Override dynamic role. Use for snipers, assassins etc."));

            if (Application.isPlaying)
            {
                EditorGUI.BeginDisabledGroup(true);
                EditorGUILayout.TextField("Active Role", ai.ActiveRole.ToString());
                EditorGUILayout.IntField("Squad (runtime)", ai.squadID);
                EditorGUI.EndDisabledGroup();
            }
        }

        // ---------- Animation -------------------------------------------------

        private void DrawAnimation(StealthHuntAI ai)
        {
            EditorGUILayout.PropertyField(_animator,
                new GUIContent("Animator", "Auto-detected if left empty"));

            EditorGUILayout.PropertyField(_animTransitionDuration,
                new GUIContent("Transition Duration", "CrossFade blend time between states"));

            // Refresh clip list from Animator Controller every 2 seconds
            if (EditorApplication.timeSinceStartup - _lastClipRefresh > 2.0)
            {
                _lastClipRefresh = EditorApplication.timeSinceStartup;
                _animClipNames = GetAnimatorClipNames(ai);
            }

            EditorGUILayout.Space(6);

            // Summary bar
            int assigned = 0, total = _animClipAssignments.arraySize;
            for (int i = 0; i < total; i++)
                if (GetSlotStatus(_animClipAssignments.GetArrayElementAtIndex(i)) == 2)
                    assigned++;

            // Header row with summary and Auto Assign
            EditorGUILayout.BeginHorizontal();
            Color prevColor = GUI.color;

            string summary = assigned + "/" + total + " slots assigned";
            Color summaryColor = assigned == total
                ? new Color(0.35f, 0.9f, 0.4f)
                : assigned > total * 0.5f
                    ? new Color(1f, 0.85f, 0.2f)
                    : new Color(1f, 0.35f, 0.35f);

            GUI.color = summaryColor;
            EditorGUILayout.LabelField("Clip Assignments  " + summary, EditorStyles.boldLabel);
            GUI.color = prevColor;

            GUI.enabled = _animClipNames.Length > 0;
            if (GUILayout.Button("Auto Assign", GUILayout.Width(88)))
                AutoAssign((StealthHuntAI)target);
            GUI.enabled = true;
            EditorGUILayout.EndHorizontal();

            if (_animClipNames.Length == 0)
            {
                EditorGUILayout.HelpBox(
                    "No Animator Controller found. Assign an Animator with clips above.",
                    MessageType.Info);
            }
            else
            {
                EditorGUILayout.HelpBox(
                    "Drag clips into Animator (no transitions needed). " +
                    "Use Auto Assign to match clips automatically, then adjust.",
                    MessageType.None);
            }

            EditorGUILayout.Space(4);

            // Draw each AnimSlot row
            for (int i = 0; i < _animClipAssignments.arraySize; i++)
            {
                SerializedProperty slot = _animClipAssignments.GetArrayElementAtIndex(i);
                SerializedProperty triggerProp = slot.FindPropertyRelative("trigger");
                SerializedProperty clipsProp = slot.FindPropertyRelative("clips");
                SerializedProperty nameProp = slot.FindPropertyRelative("customName");

                bool isCustom = triggerProp.enumValueIndex == (int)AnimTrigger.Custom;

                // Slot header row with status dot
                EditorGUILayout.BeginHorizontal();

                // Status dot
                int slotStatus = GetSlotStatus(slot);
                Color dotColor = StatusColor(slotStatus);
                string dotLabel = slotStatus == 2 ? "o" : slotStatus == 1 ? "!" : "X";
                GUI.color = dotColor;
                EditorGUILayout.LabelField(dotLabel,
                    new GUIStyle(EditorStyles.boldLabel) { normal = { textColor = dotColor } },
                    GUILayout.Width(14));
                GUI.color = Color.white;

                EditorGUILayout.PropertyField(triggerProp, GUIContent.none,
                    GUILayout.Width(isCustom ? 76 : 96));

                if (isCustom)
                    nameProp.stringValue = EditorGUILayout.TextField(
                        nameProp.stringValue, GUILayout.Width(76));

                // Add clip to this slot
                if (GUILayout.Button("+", GUILayout.Width(22)))
                {
                    clipsProp.InsertArrayElementAtIndex(clipsProp.arraySize);
                    clipsProp.GetArrayElementAtIndex(clipsProp.arraySize - 1)
                              .stringValue = "";
                }

                // Remove entire slot
                if (GUILayout.Button("x", GUILayout.Width(22)))
                {
                    _animClipAssignments.DeleteArrayElementAtIndex(i);
                    break;
                }

                EditorGUILayout.EndHorizontal();

                // Clip rows for this slot
                for (int j = 0; j < clipsProp.arraySize; j++)
                {
                    SerializedProperty clipProp = clipsProp.GetArrayElementAtIndex(j);

                    EditorGUILayout.BeginHorizontal();
                    GUILayout.Space(isCustom ? 162 : 114);

                    if (_animClipNames.Length > 0)
                    {
                        string current = clipProp.stringValue;
                        string[] options = new string[_animClipNames.Length + 1];
                        options[0] = "(none)";
                        int idx = 0;
                        for (int k = 0; k < _animClipNames.Length; k++)
                        {
                            options[k + 1] = _animClipNames[k];
                            if (_animClipNames[k] == current) idx = k + 1;
                        }
                        int newIdx = EditorGUILayout.Popup(idx, options);
                        string newClip = newIdx == 0 ? "" : options[newIdx];
                        if (newClip != current)
                            clipProp.stringValue = newClip;
                    }
                    else
                    {
                        clipProp.stringValue = EditorGUILayout.TextField(clipProp.stringValue);
                    }

                    // Remove clip
                    if (GUILayout.Button("-", GUILayout.Width(22)))
                    {
                        clipsProp.DeleteArrayElementAtIndex(j);
                        break;
                    }

                    EditorGUILayout.EndHorizontal();
                }

                // Show "random" label if multiple clips
                if (clipsProp.arraySize > 1)
                {
                    EditorGUILayout.BeginHorizontal();
                    GUILayout.Space(isCustom ? 162 : 114);
                    GUIStyle small = new GUIStyle(EditorStyles.miniLabel);
                    small.normal.textColor = new Color(0.5f, 0.8f, 0.5f);
                    EditorGUILayout.LabelField(
                        "[random: " + clipsProp.arraySize + " clips]",
                        small);
                    EditorGUILayout.EndHorizontal();
                }

                EditorGUILayout.Space(2);
            }

            EditorGUILayout.Space(4);

            // Add buttons
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("+ Trigger Slot"))
            {
                _animClipAssignments.InsertArrayElementAtIndex(_animClipAssignments.arraySize);
                var e = _animClipAssignments.GetArrayElementAtIndex(
                    _animClipAssignments.arraySize - 1);
                e.FindPropertyRelative("trigger").enumValueIndex = 0;
                e.FindPropertyRelative("customName").stringValue = "";
                var clips = e.FindPropertyRelative("clips");
                clips.ClearArray();
                clips.InsertArrayElementAtIndex(0);
                clips.GetArrayElementAtIndex(0).stringValue = "";
            }
            if (GUILayout.Button("+ Custom Slot"))
            {
                _animClipAssignments.InsertArrayElementAtIndex(_animClipAssignments.arraySize);
                var e = _animClipAssignments.GetArrayElementAtIndex(
                    _animClipAssignments.arraySize - 1);
                e.FindPropertyRelative("trigger").enumValueIndex =
                    (int)AnimTrigger.Custom;
                e.FindPropertyRelative("customName").stringValue = "MyAnim";
                var clips = e.FindPropertyRelative("clips");
                clips.ClearArray();
                clips.InsertArrayElementAtIndex(0);
                clips.GetArrayElementAtIndex(0).stringValue = "";
            }
            EditorGUILayout.EndHorizontal();
        }

        private string[] GetAnimatorClipNames(StealthHuntAI ai)
        {
            Animator anim = ai.animator;
            if (anim == null)
                anim = ai.GetComponentInChildren<Animator>();
            if (anim == null) return new string[0];

            var controller = anim.runtimeAnimatorController;
            if (controller == null) return new string[0];

            var clips = controller.animationClips;
            var names = new List<string>();
            foreach (var clip in clips)
            {
                if (clip != null && !names.Contains(clip.name))
                    names.Add(clip.name);
            }
            names.Sort();
            return names.ToArray();
        }

        // ---------- Auto Assign ----------------------------------------------

        // Fuzzy match table: trigger -> keywords that indicate a match
        private static readonly Dictionary<AnimTrigger, string[]> _triggerKeywords
            = new Dictionary<AnimTrigger, string[]>
        {
            { AnimTrigger.Idle,        new[] { "idle", "stand", "rifle idle" } },
            { AnimTrigger.Walk,        new[] { "walk", "walking", "patrol" } },
            { AnimTrigger.Alerted,     new[] { "alert", "alert idle", "rifle idle alert", "look" } },
            { AnimTrigger.Investigate, new[] { "investigate", "crouch walk", "sneak" } },
            { AnimTrigger.Search,      new[] { "search", "look around", "scan" } },
            { AnimTrigger.Pursuing,    new[] { "chase", "pursue", "run hostile", "rifle run", "run", "sprint", "jog" } },
            { AnimTrigger.Shooting,    new[] { "shoot", "fire", "firing", "firing rifle", "aim" } },
            { AnimTrigger.LostTarget,  new[] { "lost", "confused", "look around" } },
            { AnimTrigger.Return,      new[] { "return", "walk back", "walking" } },
            { AnimTrigger.Death,       new[] { "death", "die", "dying", "dead", "fall" } },
        };

        // Custom slot keyword matches
        private static readonly Dictionary<string, string[]> _customKeywords
            = new Dictionary<string, string[]>
        {
            { "HitReaction", new[] { "hit", "hit reaction", "react", "flinch" } },
            { "Reload",      new[] { "reload", "reloading" } },
            { "TakeCover",   new[] { "take cover", "cover", "dive" } },
            { "PeekLeft",    new[] { "peek left", "lean left" } },
            { "PeekRight",   new[] { "peek right", "lean right" } },
        };

        private string FuzzyMatch(string clipName, string[] keywords)
        {
            string lower = clipName.ToLower();
            foreach (var kw in keywords)
                if (lower.Contains(kw.ToLower())) return clipName;
            return null;
        }

        private void AutoAssign(StealthHuntAI ai)
        {
            if (_animClipNames.Length == 0) return;

            Undo.RecordObject(ai, "Auto Assign Animations");

            // Ensure default slots exist first
            ai.EnsureDefaultAnimAssignmentsPublic();

            // Match trigger slots -- access via index so we modify actual list objects
            foreach (var kv in _triggerKeywords)
            {
                for (int i = 0; i < ai.animSlots.Count; i++)
                {
                    if (ai.animSlots[i].trigger != kv.Key) continue;

                    // Find best matching clip
                    string bestClip = null;
                    foreach (var clipName in _animClipNames)
                    {
                        if (FuzzyMatch(clipName, kv.Value) != null)
                        {
                            bestClip = clipName;
                            break;
                        }
                    }

                    if (bestClip == null) continue;

                    var slot = ai.animSlots[i];
                    if (slot.clips.Count == 0)
                        slot.clips.Add(bestClip);
                    else if (string.IsNullOrEmpty(slot.clips[0]))
                        slot.clips[0] = bestClip;
                    // Already assigned -- don't overwrite
                }
            }

            // Match custom slots
            foreach (var kv in _customKeywords)
            {
                foreach (var clipName in _animClipNames)
                {
                    if (FuzzyMatch(clipName, kv.Value) == null) continue;

                    bool found = false;
                    for (int i = 0; i < ai.animSlots.Count; i++)
                    {
                        if (ai.animSlots[i].trigger != AnimTrigger.Custom) continue;
                        if (ai.animSlots[i].customName != kv.Key) continue;

                        var slot = ai.animSlots[i];
                        if (slot.clips.Count == 0) slot.clips.Add(clipName);
                        else if (string.IsNullOrEmpty(slot.clips[0])) slot.clips[0] = clipName;
                        else if (!slot.clips.Contains(clipName)) slot.clips.Add(clipName);
                        found = true;
                        break;
                    }

                    if (!found)
                        ai.animSlots.Add(new AnimSlot
                        {
                            trigger = AnimTrigger.Custom,
                            customName = kv.Key,
                            clips = new List<string> { clipName }
                        });
                    break;
                }
            }

            EditorUtility.SetDirty(ai);
            serializedObject.Update();
        }

        // Return status: 0=missing critical, 1=missing optional, 2=ok
        private int GetSlotStatus(SerializedProperty slot)
        {
            var clips = slot.FindPropertyRelative("clips");
            bool hasClip = false;
            for (int i = 0; i < clips.arraySize; i++)
                if (!string.IsNullOrEmpty(clips.GetArrayElementAtIndex(i).stringValue))
                { hasClip = true; break; }

            if (hasClip) return 2;

            // Check if critical
            var trigger = (AnimTrigger)slot.FindPropertyRelative("trigger").enumValueIndex;
            bool critical = trigger == AnimTrigger.Idle || trigger == AnimTrigger.Walk
                         || trigger == AnimTrigger.Death;
            return critical ? 0 : 1;
        }

        private static Color StatusColor(int status) => status switch
        {
            0 => new Color(1f, 0.35f, 0.35f),
            1 => new Color(1f, 0.85f, 0.2f),
            _ => new Color(0.35f, 0.9f, 0.4f)
        };

        // ---------- Events ----------------------------------------------------

        private void DrawEvents()
        {
            EditorGUILayout.PropertyField(_onBecameSuspicious,
                new GUIContent("On Became Suspicious"));
            EditorGUILayout.PropertyField(_onBecameHostile,
                new GUIContent("On Became Hostile"));
            EditorGUILayout.PropertyField(_onLostTarget,
                new GUIContent("On Lost Target"));
            EditorGUILayout.PropertyField(_onReturnedToPassive,
                new GUIContent("On Returned To Passive"));
        }

        // ---------- Morale ----------------------------------------------------

        private void DrawMorale(StealthHuntAI ai)
        {
            EditorGUILayout.PropertyField(_startingMorale,
                new GUIContent("Starting Morale",
                    "Initial morale when the scene loads. 1 = fully confident."));
            EditorGUILayout.PropertyField(_persistMorale,
                new GUIContent("Persist Between Sessions",
                    "Save morale to PlayerPrefs so it carries over. " +
                    "Uses the GameObject name as key -- ensure names are unique."));

            if (_persistMorale.boolValue)
                EditorGUILayout.HelpBox(
                    "Persistence enabled. Morale saves on destroy and loads on Awake.",
                    MessageType.Info);

            if (Application.isPlaying)
            {
                EditorGUI.BeginDisabledGroup(true);
                EditorGUILayout.EnumPopup("Current State", ai.CurrentMorale);
                EditorGUILayout.FloatField("Morale Level", ai.MoraleLevel);
                EditorGUI.EndDisabledGroup();

                EditorGUILayout.Space(2);

                GUI.backgroundColor = new Color(0.8f, 0.4f, 1f);
                if (GUILayout.Button("Reset Morale", GUILayout.Height(24)))
                    ai.ResetMorale();
                GUI.backgroundColor = Color.white;
            }
        }

        // ---------- Advanced --------------------------------------------------

        private void DrawCombat()
        {
            EditorGUILayout.PropertyField(_combatOverride, new GUIContent(
                "Combat Behaviour Override",
                "Assign a MonoBehaviour implementing ICombatBehaviour to override " +
                "default Hostile behaviour. Requires Combat Pack or custom implementation."));

            if (_combatOverride.objectReferenceValue == null)
            {
                EditorGUILayout.HelpBox(
                    "No combat behaviour assigned. Guard will use default Pursuing/Shooting states. " +
                    "Assign StandardCombat component for cover-based combat (Combat Pack required).",
                    MessageType.Info);
            }

            // Runtime suppression status
            if (!Application.isPlaying) return;

            var ai = (StealthHuntAI)target;

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Runtime Status", EditorStyles.boldLabel);

            // Alert state
            Color alertCol = ai.CurrentAlertState switch
            {
                AlertState.Hostile => new Color(1f, 0.2f, 0.2f),
                AlertState.Suspicious => new Color(1f, 0.85f, 0.1f),
                _ => new Color(0.4f, 0.9f, 0.4f)
            };
            GUI.color = alertCol;
            string alertLabel = ai.CurrentAlertState + "  /  " + ai.CurrentSubState;
            EditorGUILayout.LabelField(alertLabel, EditorStyles.boldLabel);
            GUI.color = Color.white;

            // Awareness bar
            EditorGUI.BeginDisabledGroup(true);
            var sensor = ai.GetComponent<AwarenessSensor>();
            if (sensor != null)
                EditorGUILayout.Slider(
                    new GUIContent("Awareness"), sensor.AwarenessLevel, 0f, 1f);
            EditorGUI.EndDisabledGroup();

            // Suppression
            var supp = ai.GetComponent<ISuppressionHandler>();
            if (supp != null && supp.IsSuppressed)
            {
                GUI.color = new Color(1f, 0.6f, 0.1f);
                EditorGUILayout.LabelField("SUPPRESSED", EditorStyles.boldLabel);
                GUI.color = Color.white;
            }
        }

        private void DrawAdvanced()
        {
            EditorGUILayout.PropertyField(_movementProvider,
                new GUIContent("Movement Provider",
                    "Custom IStealthMovement implementation. " +
                    "Leave empty to use NavMeshMovement (auto-added)."));

            EditorGUILayout.PropertyField(_searchStrategyOverride,
                new GUIContent("Search Strategy",
                    "Custom ISearchStrategy implementation. " +
                    "Leave empty to use ReachabilitySearch (auto-added)."));

            EditorGUILayout.PropertyField(_visitedCellSize,
                new GUIContent("Visited Cell Size",
                    "Cell size in metres for search memory. " +
                    "Smaller = more precise, more memory."));
        }

        // ---------- Test buttons ----------------------------------------------

        private void DrawTestButtons(StealthHuntAI ai)
        {
            EditorGUILayout.LabelField("Debug Controls", EditorStyles.boldLabel);
            EditorGUILayout.BeginHorizontal();

            GUI.backgroundColor = ColHostile;
            if (GUILayout.Button("Force Hostile", GUILayout.Height(28)))
                ai.ForceAlert(ai.transform.position + ai.transform.forward * 5f, 1f);

            GUI.backgroundColor = ColSuspicious;
            if (GUILayout.Button("Force Suspicious", GUILayout.Height(28)))
                ai.ForceAlert(ai.transform.position + ai.transform.forward * 5f, 0.35f);

            GUI.backgroundColor = ColPassive;
            if (GUILayout.Button("Reset Awareness", GUILayout.Height(28)))
            {
                var sensor = ai.GetComponent<AwarenessSensor>();
                if (sensor != null) sensor.DebugReset();
            }

            GUI.backgroundColor = Color.white;
            EditorGUILayout.EndHorizontal();
        }

        // ---------- Style builder ---------------------------------------------

        private void BuildStyles()
        {
            if (_stylesBuilt) return;

            _headerStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 14,
                alignment = TextAnchor.MiddleLeft
            };
            _headerStyle.normal.textColor = Color.white;

            _subStateStyle = new GUIStyle(EditorStyles.label)
            {
                fontSize = 11,
                alignment = TextAnchor.MiddleLeft
            };
            _subStateStyle.normal.textColor = new Color(0.82f, 0.82f, 0.82f);

            _stylesBuilt = true;
        }
    }
}