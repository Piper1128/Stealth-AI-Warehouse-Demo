using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using UnityEngine.SceneManagement;

namespace StealthHuntAI.Demo
{
    /// <summary>
    /// Manages win and lose conditions for the demo scene.
    /// Place on an empty GameObject in the scene.
    /// 
    /// Win:  Player reaches the exit trigger
    /// Lose: Player health reaches 0
    /// </summary>
    public class GameManager : MonoBehaviour
    {
        [Header("UI Panels")]
        [Tooltip("Panel shown when player wins.")]
        public GameObject winPanel;

        [Tooltip("Panel shown when player loses.")]
        public GameObject losePanel;

        [Tooltip("Panel shown during gameplay (HUD).")]
        public GameObject hudPanel;

        [Header("Restart")]
        [Tooltip("Seconds before restart option appears after win/lose.")]
        [Range(0.5f, 5f)] public float restartDelay = 2f;

        // ---------- Runtime ---------------------------------------------------

        public bool GameOver { get; private set; }
        public bool Won { get; private set; }

        // ---------- Unity lifecycle -------------------------------------------

        private void Start()
        {
            if (winPanel != null) winPanel.SetActive(false);
            if (losePanel != null) losePanel.SetActive(false);
            if (hudPanel != null) hudPanel.SetActive(true);

            // Lock cursor for first person
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }

        private void Update()
        {
            // Restart on R key after game over
            if (GameOver && Keyboard.current != null
             && Keyboard.current.rKey.wasPressedThisFrame)
                Restart();
        }

        // ---------- Public API ------------------------------------------------

        /// <summary>Called when player reaches exit trigger.</summary>
        public void OnPlayerReachedExit()
        {
            if (GameOver) return;
            GameOver = true;
            Won = true;

            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;

            if (hudPanel != null) hudPanel.SetActive(false);
            if (winPanel != null) winPanel.SetActive(true);
        }

        /// <summary>Called by PlayerHealth when health reaches 0.</summary>
        public void OnPlayerDied()
        {
            if (GameOver) return;
            GameOver = true;
            Won = false;

            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;

            if (hudPanel != null) hudPanel.SetActive(false);
            if (losePanel != null) losePanel.SetActive(true);
        }

        public void Restart()
        {
            SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex);
        }

        public void QuitToMenu()
        {
            SceneManager.LoadScene(0);
        }
    }
}