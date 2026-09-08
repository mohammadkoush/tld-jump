// tld-jump - a jump for The Long Dark.
//
// THE GAME ALREADY HAS ONE. IT NEVER CALLS IT.
//
// The player is driven by vp_FPSController, which is UFPS, and UFPS has jumping. The evidence is in
// the generated interop assembly rather than in anybody's opinion:
//
//     public bool Jump()                 the controller's own jump, public, returns whether it took
//     public float MotorJumpForce        how hard it pushes
//     public bool m_WasGroundedLastFrame whether there is ground under the feet
//     public bool IsFreeFalling()
//
// So this mod does not invent a jump. It calls the one that is already there, which is why it should
// look right: the arc, the landing, the fall damage and the footstep all belong to the game.
//
// THE OTHER JUMP MOD LOOKS WRONG FOR A REASON WORTH STATING. Shoving the character upward by writing
// to velocity or to the transform fights the controller instead of using it - the controller keeps
// its own idea of speed and grounding, so the result floats, snaps back, or lands without ever
// having fallen. Calling Jump() hands the whole problem back to the system that owns it.
//
// If MotorJumpForce turns out to be zero, that is almost certainly HOW jumping was removed from the
// game, and the force is set from a setting here instead - with the original remembered.

using MelonLoader;
using Il2Cpp;
using UnityEngine;

[assembly: MelonInfo(typeof(TldJump.JumpMod), "TLD Jump", "0.1.0", "mohammadkoush")]
[assembly: MelonGame("Hinterland", "TheLongDark")]

namespace TldJump
{
    public class JumpMod : MelonMod
    {
        private static MelonLogger.Instance _log;

        private static MelonPreferences_Category _cfg;
        private static MelonPreferences_Entry<bool> _enabled;
        private static MelonPreferences_Entry<string> _key;
        private static MelonPreferences_Entry<float> _force;
        private static MelonPreferences_Entry<bool> _useOwnForce;
        private static MelonPreferences_Entry<float> _cooldown;
        private static MelonPreferences_Entry<bool> _requireGround;
        private static MelonPreferences_Entry<bool> _report;

        private vp_FPSController _controller;
        private float _lastJump = -99f;
        private bool _haveOriginalForce;
        private float _originalForce;

        // Measurement, in the house style: how high the last jump actually went. A jump that "works"
        // and lifts four centimetres is a jump that does not work, and only a number can tell them
        // apart from inside a log.
        private bool _watching;
        private float _startY;
        private float _peakY;
        private float _watchUntil;

        public override void OnInitializeMelon()
        {
            _log = LoggerInstance;

            _cfg = MelonPreferences.CreateCategory("TLDJump", "TLD Jump");
            _enabled = _cfg.CreateEntry("Enabled", true, description: "Master switch.");
            _key = _cfg.CreateEntry("Key", "Space",
                description: "The jump key. Space is the natural one and it is the default, but The "
                    + "Long Dark has historically used Space for its context menu - if pressing it "
                    + "opens something as well as jumping, rebind to V here. The game does not check "
                    + "modifiers, so Ctrl and Space would not help.");
            _force = _cfg.CreateEntry("JumpForce", 0.14f,
                description: "How hard the jump pushes, in the controller's own units. The game's "
                    + "walking acceleration is around 0.03 for scale. Raise it a little at a time: "
                    + "this is a real physics push, and a big number sends the character over the "
                    + "roof rather than onto it.");
            _useOwnForce = _cfg.CreateEntry("UseOwnForce", true,
                description: "Write JumpForce into the controller before jumping. On, because the "
                    + "game ships with its jump force at zero - which is almost certainly HOW "
                    + "jumping was removed - and calling the jump with a zero force does nothing at "
                    + "all. Turn it off to use whatever the game itself has.");
            _cooldown = _cfg.CreateEntry("CooldownSeconds", 0.35f,
                description: "Least time between jumps. Stops a held key turning into a hover.");
            _requireGround = _cfg.CreateEntry("RequireGround", true,
                description: "Only jump with ground underfoot. On: a mid-air jump is a different "
                    + "feature and it is the one that makes a jump mod look like a cheat.");
            _report = _cfg.CreateEntry("ReportHeight", true,
                description: "Log how high each jump actually went. It is how you tell a working "
                    + "jump from one that lifts four centimetres.");
            MelonPreferences.Save();

            _log.Msg("TLD Jump 0.1.0 ready. Press " + _key.Value + " to jump.");
            _log.Msg("this calls the controller's own Jump(), so the arc, the landing and the fall "
                + "damage are the game's rather than ours.");
        }

        public override void OnSceneWasInitialized(int buildIndex, string sceneName)
        {
            // The handle is dropped so it is looked up fresh, but the ORIGINAL FORCE is not - it
            // describes a controller that outlives the scene, and re-capturing it after we have
            // written to it is how a value like this quietly runs away.
            _controller = null;
            _watching = false;
        }

        public override void OnUpdate()
        {
            if (!_enabled.Value) return;

            Measure();

            KeyCode key;
            try { key = (KeyCode)System.Enum.Parse(typeof(KeyCode), _key.Value.Trim(), true); }
            catch (System.Exception)
            {
                key = KeyCode.Space;
                Once("bad-key", "'" + _key.Value + "' is not a Unity key name - using Space.");
            }

            bool pressed;
            try { pressed = Input.GetKeyDown(key); }
            catch (System.Exception e)
            {
                Once("input", "reading the keyboard threw: " + e.Message + " - jumping is unavailable "
                    + "this session, and nothing else is affected.");
                return;
            }
            if (!pressed) return;

            TryJump();
        }

        /// <summary>
        /// Every refusal says why. A jump key that sometimes does nothing and never explains is a
        /// key nobody trusts, and the log is the only place the answer can live.
        /// </summary>
        private void TryJump()
        {
            float now = Time.realtimeSinceStartup;

            if (now - _lastJump < Mathf.Max(0.05f, _cooldown.Value))
            {
                return;                       // silent: a fast double press is not worth a line
            }

            try
            {
                if (GameManager.IsMainMenuActive() || GameManager.GetPlayerTransform() == null)
                {
                    _log.Msg("jump: not in a world.");
                    return;
                }
            }
            catch (System.Exception) { }

            if (_controller == null)
            {
                try
                {
                    GameObject player = GameManager.GetPlayerObject();
                    if (player != null) _controller = player.GetComponentInChildren<vp_FPSController>();
                    if (_controller == null) _controller = Object.FindObjectOfType<vp_FPSController>();
                }
                catch (System.Exception) { }

                if (_controller == null)
                {
                    Once("no-controller", "jump: no vp_FPSController on the player yet. It is looked "
                        + "up again on the next press, so this is usually just a menu.");
                    return;
                }
                Forget("no-controller");
            }

            try
            {
                if (_requireGround.Value && !_controller.m_WasGroundedLastFrame)
                {
                    _log.Msg("jump: no ground underfoot.");
                    return;
                }

                if (_useOwnForce.Value)
                {
                    if (!_haveOriginalForce)
                    {
                        _originalForce = _controller.MotorJumpForce;
                        _haveOriginalForce = true;
                        _log.Msg("jump: the game's own jump force is " + _originalForce.ToString("0.0000")
                            + (_originalForce <= 0.0001f
                                ? " - zero, which is how jumping was taken out. Using JumpForce instead."
                                : " - overriding it with JumpForce."));
                    }
                    _controller.MotorJumpForce = Mathf.Max(0.001f, _force.Value);
                }

                float yBefore = 0f;
                Transform t = GameManager.GetPlayerTransform();
                if (t != null) yBefore = t.position.y;

                bool took = _controller.Jump();
                _lastJump = now;

                if (!took)
                {
                    // THE INTERESTING REFUSAL. The controller was asked and said no - that is a
                    // different thing from the mod not trying, and it is the line worth reporting.
                    _log.Warning("jump: the controller refused. Grounded says "
                        + _controller.m_WasGroundedLastFrame + ", force is "
                        + _controller.MotorJumpForce.ToString("0.0000")
                        + ". If this is every press, the controller's own conditions are the thing to "
                        + "look at rather than the key.");
                    return;
                }

                if (_report.Value)
                {
                    _watching = true;
                    _startY = yBefore;
                    _peakY = yBefore;
                    _watchUntil = now + 3f;
                }
            }
            catch (System.Exception e)
            {
                // The handle is dropped, not the intent: the next press looks the controller up
                // again rather than giving up on jumping for the session.
                _controller = null;
                Once("jump-threw", "jump: the call threw: " + e.Message
                    + " - the controller is looked up again on the next press.");
            }
        }

        /// <summary>How high it actually went, measured rather than assumed.</summary>
        private void Measure()
        {
            if (!_watching) return;
            try
            {
                Transform t = GameManager.GetPlayerTransform();
                if (t == null) { _watching = false; return; }

                float y = t.position.y;
                if (y > _peakY) _peakY = y;

                if (Time.realtimeSinceStartup >= _watchUntil)
                {
                    _watching = false;
                    float rise = _peakY - _startY;
                    _log.Msg("jump: rose " + rise.ToString("0.00") + "m at a force of "
                        + _force.Value.ToString("0.000")
                        + (rise < 0.05f
                            ? ". That is not a jump - raise JumpForce, or turn UseOwnForce on."
                            : "."));
                }
            }
            catch (System.Exception) { _watching = false; }
        }

        // ---- small logging helpers, same shape as the other mod --------------------------------
        private static readonly System.Collections.Generic.HashSet<string> _said =
            new System.Collections.Generic.HashSet<string>();

        private static void Once(string key, string message)
        {
            if (!_said.Add(key)) return;
            _log.Warning(message);
        }

        private static void Forget(string key) { _said.Remove(key); }
    }
}
