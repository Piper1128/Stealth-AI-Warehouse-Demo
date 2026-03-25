using System.Collections.Generic;
using UnityEngine;

namespace StealthHuntAI
{
    /// <summary>
    /// Handles animation state transitions and aim IK for a guard.
    /// Attach alongside StealthHuntAI. Reads alert state and substate
    /// from StealthHuntAI -- writes nothing back.
    /// </summary>
    [RequireComponent(typeof(StealthHuntAI))]
    public class StealthAnimator : MonoBehaviour
    {
        // ---------- Inspector -------------------------------------------------

        [Range(0f, 0.5f)] public float transitionDuration = 0.15f;

        [Header("Aim IK")]
        public bool enableAimIK = true;
        [Range(0f, 1f)] public float bodyWeight = 0.3f;
        [Range(0f, 1f)] public float headWeight = 0.7f;
        [Range(0f, 1f)] public float eyesWeight = 0f;
        [Range(0f, 2f)] public float aimHeightOffset = 0.8f;

        // ---------- References ------------------------------------------------

        private StealthHuntAI _ai;
        private Animator _animator;
        private bool _hasAnimator;
        private string _currentState;
        private float _aimWeight;

        // ---------- Unity lifecycle -------------------------------------------

        private void Awake()
        {
            _ai = GetComponent<StealthHuntAI>();
            _animator = GetComponentInChildren<Animator>();
            _hasAnimator = _animator != null;
        }

        private void LateUpdate()
        {
            if (!_hasAnimator) return;
            if (_ai.GetCombat() != null && _ai.GetCombat().WantsControl) return;
            TickAnimator();
        }

        private void OnAnimatorIK(int layer)
        {
            if (!_hasAnimator || !enableAimIK) return;
            if (_ai.CurrentAlertState == AlertState.Passive) return;

            Vector3 aimPos;
            var sensor = _ai.Sensor;
            var target = _ai.GetTarget();

            if (sensor != null && sensor.CanSeeTarget && target != null)
                aimPos = target.Position + Vector3.up * aimHeightOffset;
            else if (_ai.HasLastKnown)
                aimPos = _ai.LastKnownPosition.GetValueOrDefault() + Vector3.up * aimHeightOffset;
            else
                aimPos = transform.position + transform.forward * 5f
                       + Vector3.up * aimHeightOffset;

            float targetWeight = _ai.CurrentAlertState switch
            {
                AlertState.Hostile => 1.0f,
                AlertState.Suspicious => 0.4f,
                _ => 0f,
            };

            _aimWeight = Mathf.MoveTowards(_aimWeight, targetWeight, 2f * Time.deltaTime);

            _animator.SetLookAtWeight(_aimWeight,
                bodyWeight * _aimWeight, headWeight * _aimWeight,
                eyesWeight * _aimWeight, 0.5f);
            _animator.SetLookAtPosition(aimPos);
        }

        // ---------- Animation ------------------------------------------------

        private void TickAnimator()
        {
            var movement = GetComponent<IStealthMovement>();
            bool moving = movement != null && movement.ActualSpeed > 0.1f;

            string clip = GetTargetClip(moving);
            if (string.IsNullOrEmpty(clip)) return;

            if (clip != _currentState)
            {
                _currentState = clip;
                try { _animator.CrossFade(clip, transitionDuration); } catch { }
                return;
            }

            var info = _animator.GetCurrentAnimatorStateInfo(0);
            if (info.normalizedTime >= 0.95f && !info.loop)
                try { _animator.CrossFade(clip, 0f); } catch { }
        }

        private string GetTargetClip(bool moving)
        {
            AnimTrigger trigger;
            switch (_ai.CurrentAlertState)
            {
                case AlertState.Hostile:
                    trigger = _ai.CurrentSubState == SubState.Shooting
                        ? AnimTrigger.Shooting
                        : (_ai.CurrentSubState == SubState.Pursuing
                           || _ai.CurrentSubState == SubState.Flanking)
                        ? AnimTrigger.Pursuing
                        : AnimTrigger.LostTarget;
                    break;
                case AlertState.Suspicious:
                    trigger = _ai.CurrentSubState == SubState.Investigating
                        ? AnimTrigger.Investigate
                        : _ai.CurrentSubState == SubState.Searching
                        ? AnimTrigger.Search
                        : AnimTrigger.Alerted;
                    break;
                default:
                    trigger = _ai.CurrentSubState == SubState.Returning
                        ? AnimTrigger.Return
                        : moving ? AnimTrigger.Walk : AnimTrigger.Idle;
                    break;
            }
            return _ai.GetClip(trigger) ?? string.Empty;
        }

        // ---------- Public API -----------------------------------------------

        public void PlayState(string customName, float duration = -1f)
        {
            if (!_hasAnimator) return;
            string clip = _ai.GetCustomClip(customName);
            if (string.IsNullOrEmpty(clip)) return;
            float d = duration >= 0f ? duration : transitionDuration;
            try { _animator.CrossFade(clip, d); } catch { }
        }

        public void PlayDeath()
        {
            if (!_hasAnimator) return;
            string clip = _ai.GetClip(AnimTrigger.Death);
            if (!string.IsNullOrEmpty(clip))
                try { _animator.CrossFade(clip, 0.1f); } catch { }
        }
    }
}