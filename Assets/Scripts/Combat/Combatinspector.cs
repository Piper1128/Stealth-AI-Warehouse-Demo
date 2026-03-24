using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using StealthHuntAI.Combat;

namespace StealthHuntAI.Editor
{
    [CustomEditor(typeof(StandardCombat))]
    public class CombatInspector : UnityEditor.Editor
    {
        private SerializedProperty _animSlots;
        private SerializedProperty _animTransition;
        private string[] _clipNames = new string[0];
        private double _lastRefresh;

        // Fuzzy match keywords per trigger -- case insensitive
        private static readonly Dictionary<CombatAnimTrigger, string[]> Keywords
            = new Dictionary<CombatAnimTrigger, string[]>
        {
            { CombatAnimTrigger.MoveToCover,      new[] { "move to cover", "cover run", "run rifle", "run" } },
            { CombatAnimTrigger.TakeCover,        new[] { "take cover", "dive cover", "cover" } },
            { CombatAnimTrigger.CoverIdle,        new[] { "cover idle", "crouch idle", "hide idle", "rifle idle alert" } },
            { CombatAnimTrigger.Reposition,       new[] { "reposition", "change cover", "move cover" } },
            { CombatAnimTrigger.Advance,          new[] { "advance", "push forward", "rush" } },
            { CombatAnimTrigger.Flank,            new[] { "flank", "strafe", "side run" } },
            { CombatAnimTrigger.Vault,            new[] { "vault", "jump over", "hurdle", "climb over" } },
            { CombatAnimTrigger.Sprint,           new[] { "sprint", "full run", "fast run" } },
            { CombatAnimTrigger.PeekLeft,         new[] { "peek left", "lean left", "look left" } },
            { CombatAnimTrigger.PeekRight,        new[] { "peek right", "lean right", "look right" } },
            { CombatAnimTrigger.CoverFire,        new[] { "cover fire", "shoot cover", "firing rifle", "fire" } },
            { CombatAnimTrigger.Suppressing,      new[] { "suppress", "full auto", "spray", "suppressing" } },
            { CombatAnimTrigger.StandingFire,     new[] { "standing fire", "hip fire", "rifle aiming idle", "aim" } },
            { CombatAnimTrigger.KneelingFire,     new[] { "kneeling", "crouch fire", "crouch shoot" } },
            { CombatAnimTrigger.GoProne,          new[] { "go prone", "prone", "dive forward", "fall prone" } },
            { CombatAnimTrigger.ProneIdle,        new[] { "prone idle", "lying", "crawl idle" } },
            { CombatAnimTrigger.ProneFire,        new[] { "prone fire", "prone shoot", "ground fire" } },
            { CombatAnimTrigger.GetUp,            new[] { "get up", "stand up", "rise", "stand" } },
            { CombatAnimTrigger.ThrowGrenade,     new[] { "throw grenade", "grenade throw", "toss grenade", "grenade" } },
            { CombatAnimTrigger.ThrowGrenadeOver, new[] { "blind throw", "over throw", "throw over", "lob" } },
            { CombatAnimTrigger.HitReaction,      new[] { "hit reaction", "hit", "react", "flinch", "impact" } },
            { CombatAnimTrigger.Stagger,          new[] { "stagger", "heavy hit", "knockback" } },
            { CombatAnimTrigger.TakeCoverReaction,new[] { "cover react", "dodge cover", "emergency cover" } },
            { CombatAnimTrigger.Reload,           new[] { "reload", "reloading", "load" } },
            { CombatAnimTrigger.Melee,            new[] { "melee", "punch", "strike", "kick", "hit melee" } },
        };

        // Critical triggers -- shown as red if missing
        private static readonly HashSet<CombatAnimTrigger> Critical = new HashSet<CombatAnimTrigger>
        {
            CombatAnimTrigger.MoveToCover,
            CombatAnimTrigger.CoverIdle,
            CombatAnimTrigger.CoverFire,
        };

        private void OnEnable()
        {
            _animSlots = serializedObject.FindProperty("animSlots");
            _animTransition = serializedObject.FindProperty("animTransitionDuration");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            EditorGUI.BeginChangeCheck();

            DrawPropertiesExcluding(serializedObject, "animSlots", "animTransitionDuration");

            EditorGUILayout.Space(6);
            DrawAnimationSection();

            if (EditorGUI.EndChangeCheck())
                serializedObject.ApplyModifiedProperties();
        }

        private void DrawAnimationSection()
        {
            var sc = (StandardCombat)target;

            // Refresh clip names every 2s
            if (EditorApplication.timeSinceStartup - _lastRefresh > 2.0)
            {
                _lastRefresh = EditorApplication.timeSinceStartup;
                _clipNames = GetClipNames(sc);
            }

            // Summary + Auto Assign
            int assigned = 0;
            for (int i = 0; i < _animSlots.arraySize; i++)
                if (HasClip(_animSlots.GetArrayElementAtIndex(i))) assigned++;

            EditorGUILayout.BeginHorizontal();
            Color prev = GUI.color;
            int total = _animSlots.arraySize;
            GUI.color = assigned == total
                ? new Color(0.35f, 0.9f, 0.4f)
                : assigned > total / 2
                    ? new Color(1f, 0.85f, 0.2f)
                    : new Color(1f, 0.35f, 0.35f);

            EditorGUILayout.LabelField(
                "Combat Animations  " + assigned + "/" + total + " assigned",
                EditorStyles.boldLabel);
            GUI.color = prev;

            GUI.enabled = _clipNames.Length > 0;
            if (GUILayout.Button("Auto Assign", GUILayout.Width(90)))
                AutoAssign(sc);
            GUI.enabled = true;
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.PropertyField(_animTransition,
                new GUIContent("Transition", "CrossFade duration between combat anims"));

            if (_clipNames.Length == 0)
            {
                EditorGUILayout.HelpBox(
                    "No Animator Controller found. Assign one to the Animator component.",
                    MessageType.Info);
            }
            else
            {
                EditorGUILayout.HelpBox(
                    "Drag clips into Animator (no transitions needed). " +
                    "Auto Assign matches by name. Add extra keywords per slot for custom clips.",
                    MessageType.None);
            }

            EditorGUILayout.Space(4);

            // Draw each slot
            for (int i = 0; i < _animSlots.arraySize; i++)
            {
                var slot = _animSlots.GetArrayElementAtIndex(i);
                var trigProp = slot.FindPropertyRelative("trigger");
                var clipsProp = slot.FindPropertyRelative("clips");
                var nameProp = slot.FindPropertyRelative("customName");
                var kwProp = slot.FindPropertyRelative("extraKeywords");

                bool isCustom = trigProp.enumValueIndex == (int)CombatAnimTrigger.Custom;
                int status = GetStatus(slot);
                Color dotCol = status == 2
                    ? new Color(0.35f, 0.9f, 0.4f)
                    : status == 1
                        ? new Color(1f, 0.85f, 0.2f)
                        : new Color(1f, 0.35f, 0.35f);

                // Slot header
                EditorGUILayout.BeginHorizontal();

                GUI.color = dotCol;
                EditorGUILayout.LabelField(status == 2 ? "o" : status == 1 ? "!" : "X",
                    new GUIStyle(EditorStyles.boldLabel) { normal = { textColor = dotCol } },
                    GUILayout.Width(14));
                GUI.color = Color.white;

                EditorGUILayout.PropertyField(trigProp, GUIContent.none,
                    GUILayout.Width(isCustom ? 80 : 110));

                if (isCustom)
                    nameProp.stringValue = EditorGUILayout.TextField(
                        nameProp.stringValue, GUILayout.Width(80));

                if (GUILayout.Button("+", GUILayout.Width(22)))
                {
                    clipsProp.InsertArrayElementAtIndex(clipsProp.arraySize);
                    clipsProp.GetArrayElementAtIndex(clipsProp.arraySize - 1).stringValue = "";
                }
                if (GUILayout.Button("x", GUILayout.Width(22)))
                {
                    _animSlots.DeleteArrayElementAtIndex(i);
                    break;
                }
                EditorGUILayout.EndHorizontal();

                // Clip dropdowns
                for (int j = 0; j < clipsProp.arraySize; j++)
                {
                    var clipProp = clipsProp.GetArrayElementAtIndex(j);
                    EditorGUILayout.BeginHorizontal();
                    GUILayout.Space(isCustom ? 176 : 128);

                    if (_clipNames.Length > 0)
                    {
                        string cur = clipProp.stringValue;
                        string[] opts = BuildOptions();
                        int idx = System.Array.IndexOf(opts, cur);
                        if (idx < 0) idx = 0;
                        int newIdx = EditorGUILayout.Popup(idx, opts);
                        string newClip = newIdx == 0 ? "" : opts[newIdx];
                        if (newClip != cur) clipProp.stringValue = newClip;
                    }
                    else
                        clipProp.stringValue = EditorGUILayout.TextField(clipProp.stringValue);

                    if (GUILayout.Button("-", GUILayout.Width(22)))
                    {
                        clipsProp.DeleteArrayElementAtIndex(j);
                        break;
                    }
                    EditorGUILayout.EndHorizontal();
                }

                // Random label
                if (clipsProp.arraySize > 1)
                {
                    EditorGUILayout.BeginHorizontal();
                    GUILayout.Space(isCustom ? 176 : 128);
                    var s = new GUIStyle(EditorStyles.miniLabel);
                    s.normal.textColor = new Color(0.5f, 0.8f, 0.5f);
                    EditorGUILayout.LabelField(
                        "[random: " + clipsProp.arraySize + " clips]", s);
                    EditorGUILayout.EndHorizontal();
                }

                // Extra keywords foldout
                string kwLabel = "Keywords (" + kwProp.arraySize + ")";
                EditorGUILayout.BeginHorizontal();
                GUILayout.Space(14);
                bool kwFold = EditorPrefs.GetBool("CombatKW_" + i, false);
                bool newFold = EditorGUILayout.Foldout(kwFold, kwLabel, true,
                    EditorStyles.miniLabel);
                if (newFold != kwFold) EditorPrefs.SetBool("CombatKW_" + i, newFold);
                if (GUILayout.Button("+ Keyword", EditorStyles.miniButton,
                    GUILayout.Width(80)))
                {
                    kwProp.InsertArrayElementAtIndex(kwProp.arraySize);
                    kwProp.GetArrayElementAtIndex(kwProp.arraySize - 1).stringValue = "";
                }
                EditorGUILayout.EndHorizontal();

                if (newFold)
                {
                    for (int k = 0; k < kwProp.arraySize; k++)
                    {
                        EditorGUILayout.BeginHorizontal();
                        GUILayout.Space(28);
                        kwProp.GetArrayElementAtIndex(k).stringValue =
                            EditorGUILayout.TextField(
                                kwProp.GetArrayElementAtIndex(k).stringValue);
                        if (GUILayout.Button("-", GUILayout.Width(22)))
                        {
                            kwProp.DeleteArrayElementAtIndex(k);
                            break;
                        }
                        EditorGUILayout.EndHorizontal();
                    }
                }

                EditorGUILayout.Space(2);
            }

            EditorGUILayout.Space(4);

            // Add buttons
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("+ Trigger Slot"))
            {
                _animSlots.InsertArrayElementAtIndex(_animSlots.arraySize);
                var e = _animSlots.GetArrayElementAtIndex(_animSlots.arraySize - 1);
                e.FindPropertyRelative("trigger").enumValueIndex = 0;
                e.FindPropertyRelative("customName").stringValue = "";
                var c = e.FindPropertyRelative("clips"); c.ClearArray();
                c.InsertArrayElementAtIndex(0);
                c.GetArrayElementAtIndex(0).stringValue = "";
            }
            if (GUILayout.Button("+ Custom Slot"))
            {
                _animSlots.InsertArrayElementAtIndex(_animSlots.arraySize);
                var e = _animSlots.GetArrayElementAtIndex(_animSlots.arraySize - 1);
                e.FindPropertyRelative("trigger").enumValueIndex =
                    (int)CombatAnimTrigger.Custom;
                e.FindPropertyRelative("customName").stringValue = "MyAnim";
                var c = e.FindPropertyRelative("clips"); c.ClearArray();
                c.InsertArrayElementAtIndex(0);
                c.GetArrayElementAtIndex(0).stringValue = "";
            }
            EditorGUILayout.EndHorizontal();
        }

        // ---------- Auto Assign ----------------------------------------------

        private void AutoAssign(StandardCombat sc)
        {
            // Animation slots removed from new StandardCombat architecture
        }

        private string FindBestMatch(List<string> keywords)
        {
            foreach (var kw in keywords)
            {
                string kwLower = kw.ToLower();
                foreach (var clip in _clipNames)
                {
                    // Case insensitive contains
                    if (clip.ToLower().Contains(kwLower))
                        return clip;
                }
            }
            return null;
        }

        // ---------- Helpers --------------------------------------------------

        private string[] GetClipNames(StandardCombat sc)
        {
            var anim = sc.GetComponentInChildren<Animator>();
            if (anim == null || anim.runtimeAnimatorController == null)
                return new string[0];

            var names = new List<string> { "(none)" };
            foreach (var clip in anim.runtimeAnimatorController.animationClips)
                if (clip != null && !names.Contains(clip.name))
                    names.Add(clip.name);

            names.Sort(1, names.Count - 1, System.StringComparer.OrdinalIgnoreCase);
            return names.ToArray();
        }

        private string[] BuildOptions() => _clipNames.Length > 0
            ? _clipNames
            : new[] { "(none)" };

        private bool HasClip(SerializedProperty slot)
        {
            var clips = slot.FindPropertyRelative("clips");
            for (int i = 0; i < clips.arraySize; i++)
                if (!string.IsNullOrEmpty(clips.GetArrayElementAtIndex(i).stringValue))
                    return true;
            return false;
        }

        private int GetStatus(SerializedProperty slot)
        {
            if (HasClip(slot)) return 2;
            var t = (CombatAnimTrigger)slot.FindPropertyRelative("trigger").enumValueIndex;
            return Critical.Contains(t) ? 0 : 1;
        }
    }
}