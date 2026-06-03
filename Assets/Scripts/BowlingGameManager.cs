using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Manages the full 10-frame bowling game loop.
///
/// State machine transitions:
///   WaitingForThrow       →  BallInMotion          : ball speed exceeds ballInMotionSpeed
///   BallInMotion          →  PinsSettling           : ball speed drops below ballStoppedSpeed OR gutter event
///   PinsSettling          →  ResettingForNextBall   : first ball, not a strike
///   PinsSettling          →  ResettingForNextFrame  : strike, frame complete, or 10th-frame bonus done
///   PinsSettling          →  GameOver               : final ball of the game counted
///   ResettingForNextBall  →  WaitingForThrow        : after resetReadyDelay (second ball incoming)
///   ResettingForNextFrame →  WaitingForThrow        : after resetReadyDelay (new frame incoming)
///
/// The BallInMotion → PinsSettling transition fires from both Update() speed polling AND the
/// BallController callback, so the game advances even if the lane is not tagged "Lane".
///
/// Inspector wiring:
///   Ball Controller → BallController on the InteractableBall GameObject.
///   Pin Group       → the PinGroup root GameObject.
///   Bumper Objects  → LeftBumper, RightBumper (or any bumper root GameObjects).
/// </summary>
public class BowlingGameManager : MonoBehaviour
{
    // ── Game constants ─────────────────────────────────────────────────────────
    private const int TotalFrames  = 10;
    private const int TotalPins    = 10;
    /// <summary>Points awarded per knocked pin. Score = pinsKnocked × PointsPerPin.</summary>
    private const int PointsPerPin = 5;
    private const int MaxScore     = TotalFrames * TotalPins * PointsPerPin; // 500

    // ── Inspector — Scene References ───────────────────────────────────────────
    [Header("Scene References")]
    [Tooltip("BallController on the InteractableBall. Auto-found if not assigned.")]
    [SerializeField] private BallController ballController;

    [Tooltip("PinGroup that manages all bowling pins. Auto-found if not assigned.")]
    [SerializeField] private PinGroup pinGroup;

    [Tooltip("Root GameObjects for the left and right bumpers. Their colliders are toggled by enableBumpers.")]
    [SerializeField] private GameObject[] bumperObjects;

    // ── Inspector — Game Settings ──────────────────────────────────────────────
    [Header("Game Settings")]
    [Tooltip("When true bumpers are active (kids/beginner mode).")]
    [SerializeField] private bool enableBumpers = false;

    [Tooltip("Ball speed (m/s) above which the ball is considered in motion after a throw.")]
    [SerializeField] private float ballInMotionSpeed = 0.5f;

    [Tooltip("Ball speed (m/s) below which the ball is considered stopped. Must be less than ballInMotionSpeed.")]
    [SerializeField] private float ballStoppedSpeed = 0.3f;

    [Tooltip("Seconds to wait after the ball stops before counting knocked pins.")]
    [SerializeField] private float settleWaitTime = 3f;

    [Tooltip("Seconds after resetting ball or pins before accepting the next throw.")]
    [SerializeField] private float resetReadyDelay = 1f;

    // ── State machine ──────────────────────────────────────────────────────────
    private enum GameState
    {
        MainMenu,               // Showing the main menu; game not yet started.
        WaitingForThrow,        // Idle — ready for the player to throw.
        BallInMotion,           // Ball has been thrown and is rolling down the lane.
        PinsSettling,           // Ball stopped; settle coroutine counting down.
        ResettingForNextBall,   // Resetting ball only; player throws the second ball this frame.
        ResettingForNextFrame,  // Resetting ball and all pins; player starts a new frame.
        GameOver                // All 10 frames complete.
    }

    private GameState state = GameState.MainMenu;

    // ── Scoring data ───────────────────────────────────────────────────────────
    /// <summary>Flat list of pin counts per individual ball roll, in chronological order.</summary>
    private readonly List<int> ballRolls         = new List<int>();
    /// <summary>Index into ballRolls where each frame begins.</summary>
    private readonly List<int> frameStartIndices = new List<int>();

    private int  currentFrame;
    private bool isGutterBall;
    private int  pinsStandingAtFrameStart;
    private int  pinsStandingBeforeThisRoll;

    // ── Bumper colliders ───────────────────────────────────────────────────────
    private Collider[] bumperColliders;

    // ── GUI styles ─────────────────────────────────────────────────────────────
    private GUIStyle hudStyle;
    private GUIStyle titleStyle;
    private GUIStyle actionStyle;

    // ──────────────────────────────────────────────────────────────────────────
    // Unity lifecycle
    // ──────────────────────────────────────────────────────────────────────────

    private void Start()
    {
        // Auto-locate references when not assigned in the Inspector.
        if (ballController == null)
        {
            var ballGO = GameObject.Find("InteractableBall");
            if (ballGO != null)
                ballController = ballGO.GetComponent<BallController>();
            if (ballController == null)
                Debug.LogWarning("[BowlingGameManager] BallController not found — assign it in the Inspector.");
        }

        if (pinGroup == null)
        {
            pinGroup = FindAnyObjectByType<PinGroup>();
            if (pinGroup == null)
                Debug.LogWarning("[BowlingGameManager] PinGroup not found — assign it in the Inspector.");
        }

        CollectBumperColliders();
        SetBumpersEnabled(enableBumpers);
        InitScoringState();
    }

    private void Update()
    {
        // ── Main menu: any key or mouse click starts the game ─────────────────
        if (state == GameState.MainMenu)
        {
            if (Input.anyKeyDown || Input.GetMouseButtonDown(0))
                StartGame();
            return;
        }

        // ── R: restart the game at any time ───────────────────────────────────
        if (Input.GetKeyDown(KeyCode.R))
        {
            RestartGame();
            return;
        }

        // ── Q: quit (GameOver overlay only) ───────────────────────────────────
        if (state == GameState.GameOver && Input.GetKeyDown(KeyCode.Q))
        {
            QuitGame();
            return;
        }

        // ── Speed polling — primary driver for WaitingForThrow and BallInMotion ─
        // This makes state transitions independent of whether the lane is tagged "Lane".
        float ballSpeed = ballController != null ? ballController.CurrentSpeed : 0f;

        switch (state)
        {
            // ── WaitingForThrow → BallInMotion ─────────────────────────────────
            // Ball must exceed ballInMotionSpeed to register as a valid throw.
            case GameState.WaitingForThrow:
                if (ballSpeed > ballInMotionSpeed)
                    EnterBallInMotion();
                break;

            // ── BallInMotion → PinsSettling ────────────────────────────────────
            // Detects stop via polling; also triggered by BallController callbacks.
            case GameState.BallInMotion:
                if (ballSpeed < ballStoppedSpeed)
                    EnterPinsSettling();
                break;

            // PinsSettling, ResettingForNextBall, ResettingForNextFrame, GameOver
            // are all driven by coroutines — no polling action required here.
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // State entry helpers — one clean method per transition target
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>Transition: WaitingForThrow → BallInMotion.</summary>
    private void EnterBallInMotion()
    {
        isGutterBall = false;
        state        = GameState.BallInMotion;
        Debug.Log("[BowlingGameManager] → BallInMotion");
    }

    /// <summary>
    /// Transition: BallInMotion → PinsSettling.
    /// Starts the settle coroutine which always completes — no stuck states.
    /// </summary>
    private void EnterPinsSettling()
    {
        state = GameState.PinsSettling;
        Debug.Log($"[BowlingGameManager] → PinsSettling (waiting {settleWaitTime} s)");
        // Stop any stale coroutine before starting fresh — guarantees exactly one settle coroutine.
        StopAllCoroutines();
        StartCoroutine(SettleAndCount());
    }

    /// <summary>
    /// Transition: PinsSettling → ResettingForNextBall.
    /// Knocked pins stay down; only the ball resets. Player throws the second ball.
    /// </summary>
    private void EnterResettingForNextBall()
    {
        state = GameState.ResettingForNextBall;
        Debug.Log("[BowlingGameManager] → ResettingForNextBall");
        StartCoroutine(DoResetNextBall());
    }

    /// <summary>
    /// Transition: PinsSettling → ResettingForNextFrame.
    /// Ball and all pins reset. Player starts a new frame (or bonus ball).
    /// </summary>
    private void EnterResettingForNextFrame()
    {
        state = GameState.ResettingForNextFrame;
        Debug.Log("[BowlingGameManager] → ResettingForNextFrame");
        StartCoroutine(DoResetNextFrame());
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Coroutines
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Waits settleWaitTime seconds for pins to finish wobbling, then counts and advances.
    /// This coroutine always completes — the game can never get stuck in PinsSettling.
    /// </summary>
    private IEnumerator SettleAndCount()
    {
        yield return new WaitForSeconds(settleWaitTime);
        RecordRollAndAdvance(); // Always called — no early exit.
    }

    /// <summary>Resets the ball to spawn, waits resetReadyDelay, then returns to WaitingForThrow.</summary>
    private IEnumerator DoResetNextBall()
    {
        ballController?.TeleportToSpawn();
        yield return new WaitForSeconds(resetReadyDelay);
        // → WaitingForThrow: second ball of the same frame.
        state = GameState.WaitingForThrow;
        Debug.Log("[BowlingGameManager] → WaitingForThrow (2nd ball)");
    }

    /// <summary>Resets ball and all pins, waits resetReadyDelay, then returns to WaitingForThrow.</summary>
    private IEnumerator DoResetNextFrame()
    {
        ballController?.TeleportToSpawn();
        pinGroup?.ResetPositions();
        // Pin counts reset to full — DoResetNextFrame always sets up a fresh frame.
        pinsStandingAtFrameStart   = TotalPins;
        pinsStandingBeforeThisRoll = TotalPins;
        yield return new WaitForSeconds(resetReadyDelay);
        // → WaitingForThrow: first ball of the new frame (or bonus ball in the 10th).
        state = GameState.WaitingForThrow;
        Debug.Log("[BowlingGameManager] → WaitingForThrow (new frame)");
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Backward-compatible callbacks from BallController
    // These fire the same transitions as Update() speed polling, whichever arrives first.
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Called by BallController when the ball is released.
    /// Acts as an immediate WaitingForThrow → BallInMotion trigger alongside speed polling.
    /// </summary>
    public void OnBallThrown(float speed)
    {
        if (state == GameState.WaitingForThrow)
            EnterBallInMotion();
    }

    /// <summary>
    /// Called by BallController when the ball stops naturally on the lane.
    /// Acts as an immediate BallInMotion → PinsSettling trigger alongside speed polling.
    /// </summary>
    public void OnBallStopped()
    {
        if (state == GameState.BallInMotion)
            EnterPinsSettling();
    }

    /// <summary>
    /// Called by BallController when the ball enters a gutter trigger.
    /// Marks the roll as a gutter ball and immediately starts the settle sequence.
    /// </summary>
    public void OnGutterBall()
    {
        if (state != GameState.BallInMotion) return;
        isGutterBall = true;
        EnterPinsSettling();
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Pin counting and frame advancement
    // ──────────────────────────────────────────────────────────────────────────

    private void RecordRollAndAdvance()
    {
        int pinsNowStanding     = pinGroup != null ? pinGroup.CountStanding() : pinsStandingBeforeThisRoll;
        int pinsKnockedThisRoll = Mathf.Max(0, pinsStandingBeforeThisRoll - pinsNowStanding);

        // Gutter ball scores zero regardless of any physics edge cases.
        if (isGutterBall)
            pinsKnockedThisRoll = 0;

        ballRolls.Add(pinsKnockedThisRoll);
        isGutterBall = false;

        int rollInFrame = RollIndexInFrame();
        Debug.Log($"[BowlingGameManager] Frame {currentFrame + 1}, Roll {rollInFrame + 1}: " +
                  $"{pinsKnockedThisRoll} knocked (was {pinsStandingBeforeThisRoll} standing). " +
                  $"Running score: {CalculateTotalScore()}");

        // Route to the appropriate handler.
        if (currentFrame < TotalFrames - 1)
            AdvanceNormalFrame(pinsKnockedThisRoll, pinsNowStanding, rollInFrame);
        else
            AdvanceTenthFrame(pinsNowStanding, rollInFrame);
    }

    /// <summary>Returns the 0-based roll index within the current frame (0 = first ball, 1 = second, etc.).</summary>
    private int RollIndexInFrame()
    {
        if (frameStartIndices.Count == 0) return 0;
        return ballRolls.Count - 1 - frameStartIndices[currentFrame];
    }

    /// <summary>Handles advancement for frames 1–9 (indices 0–8). Standard strike / spare / open logic.</summary>
    private void AdvanceNormalFrame(int pinsKnockedThisRoll, int pinsNowStanding, int rollInFrame)
    {
        bool isStrike  = rollInFrame == 0 && pinsKnockedThisRoll == TotalPins;
        bool frameOver = isStrike || rollInFrame >= 1; // Frame ends on a strike OR after ball 2.

        if (frameOver)
        {
            // Frame complete — advance counter and reset everything for the next frame.
            currentFrame++;
            frameStartIndices.Add(ballRolls.Count);
            // DoResetNextFrame sets pinsStanding* = TotalPins internally.
            EnterResettingForNextFrame();
        }
        else
        {
            // Ball 1 was not a strike — keep knocked pins down, reset ball only.
            pinsStandingBeforeThisRoll = pinsNowStanding;
            EnterResettingForNextBall();
        }
    }

    /// <summary>
    /// Handles advancement for the 10th frame (up to 3 balls).
    ///   Roll 0 strike  → reset all 10 pins for roll 1.
    ///   Roll 0 no-strike → keep remaining pins for roll 1.
    ///   Roll 1 open    → no bonus ball, game over.
    ///   Roll 1 strike or spare → bonus ball (roll 2) earned.
    ///   Roll 2         → game always ends.
    /// </summary>
    private void AdvanceTenthFrame(int pinsNowStanding, int rollInFrame)
    {
        int frameStart = frameStartIndices[currentFrame];

        switch (rollInFrame)
        {
            case 0:
            {
                bool roll0Strike = ballRolls[frameStart] == TotalPins;
                if (roll0Strike)
                {
                    // Strike on ball 1: reset full 10 pins for ball 2.
                    // DoResetNextFrame also resets pinsStanding* to TotalPins.
                    EnterResettingForNextFrame();
                }
                else
                {
                    // No strike: keep remaining pins standing for ball 2.
                    pinsStandingBeforeThisRoll = pinsNowStanding;
                    EnterResettingForNextBall();
                }
                break;
            }

            case 1:
            {
                int  roll0       = ballRolls[frameStart];
                int  roll1       = ballRolls[frameStart + 1];
                bool roll0Strike = roll0 == TotalPins;
                bool roll1Strike = roll1 == TotalPins;
                bool spareEarned = !roll0Strike && (roll0 + roll1 == TotalPins);
                bool bonusEarned = roll0Strike || spareEarned;

                if (!bonusEarned)
                {
                    // Open 10th frame — no bonus ball, game ends here.
                    EndGame();
                    break;
                }

                if (roll0Strike && roll1Strike)
                {
                    // Double strike: reset full 10 pins for ball 3.
                    EnterResettingForNextFrame();
                }
                else if (roll0Strike)
                {
                    // Strike then non-strike: keep remaining pins for ball 3.
                    pinsStandingBeforeThisRoll = pinsNowStanding;
                    EnterResettingForNextBall();
                }
                else
                {
                    // Spare: reset full 10 pins for ball 3.
                    EnterResettingForNextFrame();
                }
                break;
            }

            default:
                // Ball 3 complete — game always ends after the third ball of the 10th.
                EndGame();
                break;
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Score calculation — simple per-pin scoring
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Simple scoring: each knocked pin is worth PointsPerPin (5) points.
    /// Total score = total pins knocked across all rolls × PointsPerPin.
    /// </summary>
    private int CalculateTotalScore()
    {
        int score = 0;
        foreach (int pins in ballRolls)
            score += pins * PointsPerPin;
        return score;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Game lifecycle
    // ──────────────────────────────────────────────────────────────────────────

    private void InitScoringState()
    {
        ballRolls.Clear();
        frameStartIndices.Clear();
        frameStartIndices.Add(0); // Frame 0 begins at ball-roll index 0.

        currentFrame               = 0;
        isGutterBall               = false;
        pinsStandingAtFrameStart   = pinGroup != null ? pinGroup.CountStanding() : TotalPins;
        pinsStandingBeforeThisRoll = pinsStandingAtFrameStart;
    }

    private void EndGame()
    {
        ballController?.TeleportToSpawn();
        state = GameState.GameOver;
        Debug.Log($"[BowlingGameManager] Game over! Final score: {CalculateTotalScore()} / {MaxScore}");
    }

    /// <summary>Transitions from the main menu into the first frame. Called by the Play button or any key press.</summary>
    public void StartGame()
    {
        StopAllCoroutines();

        if (ballController != null)
            ballController.TeleportToSpawn();

        if (pinGroup != null)
            pinGroup.ResetPositions();

        InitScoringState();
        state = GameState.WaitingForThrow;
        Debug.Log("[BowlingGameManager] Game started — Frame 1, Ball 1.");
    }

    /// <summary>
    /// Fully restarts the game: resets all 10 frames, score, pins, and ball.
    /// Called when the player presses R at any time, or from a UI button.
    /// </summary>
    public void RestartGame()
    {
        StopAllCoroutines();

        if (ballController != null)
            ballController.TeleportToSpawn();
        else
            Debug.LogWarning("[BowlingGameManager] RestartGame: ballController is not assigned — ball position will not reset.");

        if (pinGroup != null)
            pinGroup.ResetPositions();
        else
            Debug.LogWarning("[BowlingGameManager] RestartGame: pinGroup is not assigned — pins will not reset.");

        InitScoringState();
        state = GameState.WaitingForThrow;
        Debug.Log("[BowlingGameManager] Game restarted — Frame 1, Ball 1.");
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Public reset helpers — wire to spatial Reset Ball / Reset Pins buttons
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Resets the ball to its spawn position.
    /// Wire this to the Reset Ball spatial button event.
    /// </summary>
    public void ResetBall()
    {
        ballController?.TeleportToSpawn();
        // Cancel any settle coroutine so the game doesn't count after a manual reset.
        if (state == GameState.BallInMotion || state == GameState.PinsSettling)
        {
            StopAllCoroutines();
            state = GameState.WaitingForThrow;
        }
    }

    /// <summary>
    /// Resets all pins to their initial positions.
    /// Wire this to the Reset Pins spatial button event.
    /// </summary>
    public void ResetPins()
    {
        pinGroup?.ResetPositions();
        pinsStandingAtFrameStart   = TotalPins;
        pinsStandingBeforeThisRoll = TotalPins;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Bumper management
    // ──────────────────────────────────────────────────────────────────────────

    private void CollectBumperColliders()
    {
        var collected = new List<Collider>();

        if (bumperObjects != null)
        {
            foreach (GameObject obj in bumperObjects)
            {
                if (obj == null) continue;
                collected.AddRange(obj.GetComponentsInChildren<Collider>(true));
            }
        }

        // Fallback: tag-based discovery. Wrapped in try-catch because
        // FindGameObjectsWithTag throws if "Bumper" is not declared in Tags & Layers.
        try
        {
            foreach (GameObject obj in GameObject.FindGameObjectsWithTag("Bumper"))
            {
                if (obj.TryGetComponent<Collider>(out Collider col))
                    collected.Add(col);
            }
        }
        catch (UnityException)
        {
            // "Bumper" tag not defined — assign bumpers via the Bumper Objects Inspector array.
        }

        bumperColliders = collected.ToArray();
    }

    /// <summary>Enables or disables all bumper colliders. true = kids mode; false = open gutters.</summary>
    public void SetBumpersEnabled(bool enabled)
    {
        enableBumpers = enabled;
        if (bumperColliders == null) return;

        foreach (Collider col in bumperColliders)
        {
            if (col != null)
                col.enabled = enabled;
        }

        Debug.Log($"[BowlingGameManager] Bumpers {(enabled ? "ENABLED" : "DISABLED")}.");
    }

    // ──────────────────────────────────────────────────────────────────────────
    // HUD — immediate-mode GUI (debug/desktop overlay)
    // ──────────────────────────────────────────────────────────────────────────

    private void OnGUI()
    {
        InitGuiStyles();

        if (state == GameState.MainMenu)
        {
            DrawMainMenuOverlay();
            return;
        }

        DrawScoreHUD();

        if (state == GameState.GameOver)
            DrawGameOverOverlay();
    }

    private void InitGuiStyles()
    {
        if (hudStyle != null) return; // Already initialised.

        hudStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize  = 16,
            alignment = TextAnchor.MiddleLeft,
        };
        hudStyle.normal.textColor = Color.white;

        titleStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize  = 26,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleCenter,
        };

        actionStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize  = 20,
            alignment = TextAnchor.MiddleCenter,
        };
        actionStyle.normal.textColor = new Color(0.85f, 0.85f, 0.85f);
    }

    private void DrawScoreHUD()
    {
        const float W = 420f;
        const float H = 60f;

        GUI.Box(new Rect(10f, 10f, W, H), "");

        string frameLabel = state == GameState.GameOver
            ? "GAME OVER"
            : $"Frame {Mathf.Min(currentFrame + 1, TotalFrames)} / {TotalFrames}";

        GUI.Label(new Rect(20f, 16f, W - 20f, 24f),
            $"{frameLabel}    Score: {CalculateTotalScore()}", hudStyle);
        GUI.Label(new Rect(20f, 38f, W - 20f, 20f),
            $"Bumpers: {(enableBumpers ? "ON" : "OFF")}    [{state}]    [R] Restart", hudStyle);
    }

    private void DrawGameOverOverlay()
    {
        int  totalScore = CalculateTotalScore();
        bool isPerfect  = totalScore >= MaxScore;

        const float W = 460f;
        const float H = 200f;
        float x = (Screen.width  - W) * 0.5f;
        float y = (Screen.height - H) * 0.5f;

        GUI.Box(new Rect(x, y, W, H), "");

        titleStyle.normal.textColor = isPerfect ? Color.yellow : Color.white;
        string headline = isPerfect
            ? $"PERFECT GAME!  {MaxScore} / {MaxScore}"
            : $"Game Over — Score: {totalScore} / {MaxScore}";

        GUI.Label(new Rect(x, y + 20f,  W, 50f), headline,            titleStyle);
        GUI.Box(new Rect(x + 20f, y + 80f, W - 40f, 2f), "");
        GUI.Label(new Rect(x, y + 100f, W, 38f), "[ R ]  Play again", actionStyle);
        GUI.Label(new Rect(x, y + 140f, W, 38f), "[ Q ]  Quit",       actionStyle);
    }

    private void DrawMainMenuOverlay()
    {
        const float W = 460f;
        const float H = 260f;
        float x = (Screen.width  - W) * 0.5f;
        float y = (Screen.height - H) * 0.5f;

        GUI.Box(new Rect(x, y, W, H), "");

        titleStyle.normal.textColor = new Color(0.4f, 0.9f, 1f); // Cyan-ish
        GUI.Label(new Rect(x, y + 20f, W, 50f), "VR Bowling", titleStyle);

        titleStyle.normal.textColor = Color.white;
        GUI.Label(new Rect(x, y + 80f,  W, 30f), $"Each pin knocked = {PointsPerPin} points", actionStyle);
        GUI.Label(new Rect(x, y + 115f, W, 30f), $"Max score: {MaxScore}  |  10 Frames", actionStyle);
        GUI.Label(new Rect(x, y + 155f, W, 30f), "Press any key or click Play to begin", actionStyle);

        if (GUI.Button(new Rect(x + 130f, y + 200f, 200f, 42f), "PLAY"))
            StartGame();
    }

    private static void QuitGame()
    {
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#else
        Application.Quit();
#endif
    }
}
