using UnityEngine;

namespace StealthHuntAI.Demo
{
    /// <summary>
    /// Place on a trigger collider at the exit point.
    /// When player enters, notifies GameManager of win condition.
    /// </summary>
    public class ExitTrigger : MonoBehaviour
    {
        [Tooltip("Tag of the player object.")]
        public string playerTag = "Player";

        private GameManager _gameManager;

        private void Awake()
        {
            _gameManager = FindFirstObjectByType<GameManager>();

            // Make sure collider is a trigger
            var col = GetComponent<Collider>();
            if (col != null) col.isTrigger = true;
        }

        private void OnTriggerEnter(Collider other)
        {
            if (!other.CompareTag(playerTag)) return;
            _gameManager?.OnPlayerReachedExit();
        }

#if UNITY_EDITOR
        private void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(0f, 1f, 0.3f, 0.3f);
            var col = GetComponent<BoxCollider>();
            if (col != null)
                Gizmos.DrawCube(transform.position + col.center, col.size);
        }
#endif
    }
}