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
        private static MelonPreferences_Entry<bool> _probe;
        private static MelonPreferences_Entry<string> _keyUp;
        private static MelonPreferences_Entry<string> _keyDown;
        private static MelonPreferences_Entry<float> _step;

        private vp_FPSController _controller;
        private float _lastJump = -99f;
        private bool _haveOriginalForce;
        private float _originalForce;
        private static float _oneOffForce = -1f;
        private static float _lastForceUsed;

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
            // V RATHER THAN SPACE, AND IT IS NOT A PREFERENCE.
            //
            // Space is the natural jump key and it is the wrong default here: The Long Dark uses it
            // for its context menu, so the key that jumps would also open something every time. The
            // game does not check modifiers either - that was established when three of another
            // mod's hotkeys turned out to be the game's own screenshot keys and Ctrl made no
            // difference - so Ctrl and Space would not have rescued it.
            //
            // Better a key that is only ours than a key that is nearly right.
            _key = _cfg.CreateEntry("Key", "Space",
                description: "The jump key. Space, because that is what a jump key is and it is what "
                    + "an on-screen keyboard offers first. The game has historically used Space for "
                    + "its context menu, so if something opens as well as jumping, set this to V - "
                    + "modifiers will not help, the game does not check them.");
            // MEASURED, twice, three seconds apart and repeatable to a centimetre:
            //
            //     force 0.25  ->  1.00m and 0.99m      the game's own value
            //     force 0.35  ->  1.55m and 1.54m
            //     force 0.50  ->  1.77m and 1.77m
            //     force 0.70  ->  1.77m and 1.77m      identical, so the controller clamps here
            //
            // 0.50 is the default because it is the most the engine will give: above it the number
            // changes and the jump does not. Anyone raising this past 0.5 is spending force on
            // nothing, which is worth knowing before spending an evening on it.
            _force = _cfg.CreateEntry("JumpForce", 0.50f,
                description: "How hard the jump pushes, in the controller's own units. 0.50 lifts "
                    + "about 1.77m, which is where the controller clamps - measured, and identical "
                    + "at 0.70, so there is nothing above this to reach for. The game's own value is "
                    + "0.25 and lifts about a metre; walking acceleration is around 0.03 for scale.");
            _useOwnForce = _cfg.CreateEntry("UseOwnForce", true,
                description: "Write JumpForce into the controller instead of using the game's own "
                    + "0.25. On, because 0.25 lifts a metre and 0.50 lifts the full 1.77m the engine "
                    + "allows. Turn it off for the jump exactly as Hinterland tuned it.");
            _cooldown = _cfg.CreateEntry("CooldownSeconds", 0.35f,
                description: "Least time between jumps. Stops a held key turning into a hover.");
            _requireGround = _cfg.CreateEntry("RequireGround", true,
                description: "Only jump with ground underfoot. On: a mid-air jump is a different "
                    + "feature and it is the one that makes a jump mod look like a cheat.");
            _report = _cfg.CreateEntry("ReportHeight", true,
                description: "Log how high each jump actually went. It is how you tell a working "
                    + "jump from one that lifts four centimetres.");
            _keyUp = _cfg.CreateEntry("KeyForceUp", "KeypadPlus",
                description: "Raise the jump force by ForceStep. On the numeric keypad by default, "
                    + "because the game claims the function row and the main row is mostly gameplay. "
                    + "Any Unity key name works.");
            _keyDown = _cfg.CreateEntry("KeyForceDown", "KeypadMinus",
                description: "Lower the jump force by ForceStep.");
            _step = _cfg.CreateEntry("ForceStep", 0.05f,
                description: "How much the two keys move the force per press. The useful range is "
                    + "narrow - 0.25 lifts a metre and 0.50 lifts the 1.77m ceiling - so the step is "
                    + "small on purpose.");
            _probe = _cfg.CreateEntry("Probe", true,
                description: "Report the first twelve key presses the mod sees, whatever they are. "
                    + "It exists to answer one question that a jump log cannot: whether the keyboard "
                    + "reaches the mod at all. Turn it off once that is settled.");
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

            KeyProbe();
            TriggerFile();
            ForceKeys();

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

        // ------------------------------------------------------------------------------------------
        // TUNING IT WHILE LOOKING AT IT
        //
        // The force was found by firing a ladder from outside the game and reading heights out of a
        // log. That worked, and it is not something anybody else can do. A number that can only be
        // changed by editing a file and restarting stays at whatever it was first set to.
        //
        // So two keys move it, the new value is announced ON SCREEN rather than only in the log, and
        // it is saved immediately - the next launch starts where the last one ended.
        private static float _shownUntil;
        private static string _shownText = "";

        private void ForceKeys()
        {
            try
            {
                if (Down(_keyUp, KeyCode.KeypadPlus)) NudgeForce(1);
                else if (Down(_keyDown, KeyCode.KeypadMinus)) NudgeForce(-1);
            }
            catch (System.Exception e)
            {
                Once("forcekeys", "the force keys could not be read: " + e.Message);
            }
        }

        private static bool Down(MelonPreferences_Entry<string> entry, KeyCode fallback)
        {
            KeyCode k;
            try { k = (KeyCode)System.Enum.Parse(typeof(KeyCode), entry.Value.Trim(), true); }
            catch (System.Exception) { k = fallback; }
            return Input.GetKeyDown(k);
        }

        private void NudgeForce(int direction)
        {
            // Clamped at 0.50 on the way up because that is where the controller stops caring -
            // measured at 0.50 and at 0.70, both giving an identical 1.77m. A dial that keeps moving
            // after the thing it controls has stopped is a dial that lies.
            float step = Mathf.Max(0.01f, _step.Value);
            float now = Mathf.Clamp(_force.Value + direction * step, 0.05f, 0.50f);

            _force.Value = now;
            _useOwnForce.Value = true;
            MelonPreferences.Save();

            string note = now >= 0.4999f
                ? "  (the ceiling - about 1.77m, and more force changes nothing)"
                : (now <= 0.0501f ? "  (the floor)" : "");
            _shownText = "jump force " + now.ToString("0.00") + note;
            _shownUntil = Time.realtimeSinceStartup + 2.5f;
            _log.Msg("jump force " + now.ToString("0.00") + note);
        }

        public override void OnGUI()
        {
            if (Time.realtimeSinceStartup > _shownUntil || _shownText.Length == 0) return;
            try
            {
                float w = 360f;
                Rect r = new Rect((Screen.width - w) * 0.5f, 60f, w, 26f);
                Color was = GUI.color;
                GUI.color = new Color(0f, 0f, 0f, 0.65f);
                GUI.Box(r, "");
                GUI.color = new Color(1f, 0.85f, 0.5f, 1f);
                GUI.Label(new Rect(r.x + 10f, r.y + 4f, r.width - 20f, 20f), _shownText);
                GUI.color = was;
            }
            catch (System.Exception) { }
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

                if (!_haveOriginalForce)
                {
                    _originalForce = _controller.MotorJumpForce;
                    _haveOriginalForce = true;
                    _log.Msg("jump: the game's own jump force is " + _originalForce.ToString("0.0000")
                        + (_originalForce <= 0.0001f
                            ? " - zero, which would be how jumping was taken out."
                            : " - a real tuned value, so jumping was removed by never binding a key."));
                }

                // A force from the trigger file wins, then the setting, then the game's own. The
                // one-off exists so a value can be tried without a restart; nothing about it is
                // remembered, so the next jump is back to normal unless it is asked for again.
                float useForce = _oneOffForce > 0f
                    ? _oneOffForce
                    : (_useOwnForce.Value ? Mathf.Max(0.001f, _force.Value) : _originalForce);
                _controller.MotorJumpForce = useForce;
                _lastForceUsed = useForce;

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

        // ------------------------------------------------------------------------------------------
        // A JUMP THAT NEEDS NO KEYBOARD
        //
        // Testing over a remote desktop from a phone puts a keyboard, an Android IME, a remote
        // session and a game between the intent and the code. When nothing happens, any one of those
        // four could be the reason, and the log cannot tell them apart.
        //
        // So the jump can also be asked for by a FILE. Create the file, and the next frame jumps and
        // deletes it. That removes every layer except the game itself: if a jump happens this way but
        // not on a key, the jump works and the keyboard is the problem; if it does not happen either
        // way, the keyboard was never the question.
        //
        // The file lives in the game folder rather than anywhere clever, so it can be made from a
        // terminal on the same machine with one command.
        private static string _triggerPath;
        private static float _nextTriggerCheck;

        private void TriggerFile()
        {
            float now = Time.realtimeSinceStartup;
            if (now < _nextTriggerCheck) return;
            _nextTriggerCheck = now + 0.2f;

            try
            {
                if (_triggerPath == null)
                {
                    _triggerPath = System.IO.Path.Combine(
                        System.IO.Directory.GetCurrentDirectory(), "jump-now.txt");
                    _log.Msg("a jump can also be asked for without any keyboard: create the file "
                        + _triggerPath + " and it jumps on the next frame.");
                }

                if (!System.IO.File.Exists(_triggerPath)) return;

                // THE FILE CAN CARRY A FORCE, and that is what makes tuning possible at all from
                // outside the game. Restarting once per guess is how a number like this never gets
                // found: each restart costs a minute, so three guesses cost the afternoon and the
                // person tuning gives up at "good enough". Writing 0.35 into the file jumps at 0.35
                // and reports the height, so the ladder can be climbed in one sitting.
                float oneOff = -1f;
                try
                {
                    string body = System.IO.File.ReadAllText(_triggerPath).Trim();
                    if (body.Length > 0)
                    {
                        float parsed;
                        if (float.TryParse(body, System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out parsed)
                            && parsed > 0f && parsed < 5f)
                        {
                            oneOff = parsed;
                        }
                    }
                }
                catch (System.Exception) { }

                try { System.IO.File.Delete(_triggerPath); } catch (System.Exception) { }
                _oneOffForce = oneOff;
                _log.Msg("jump asked for by file"
                    + (oneOff > 0f ? " at a one-off force of " + oneOff.ToString("0.000") : "")
                    + ".");
                TryJump();
                _oneOffForce = -1f;
            }
            catch (System.Exception e)
            {
                Once("trigger", "the jump trigger file could not be checked: " + e.Message
                    + " - the key still works.");
            }
        }

        // ------------------------------------------------------------------------------------------
        // DOES THE MOD SEE THE KEYBOARD AT ALL?
        //
        // "I tried V, nothing came of it" - and the log carried no jump line of any kind. Not a
        // refusal, not a failed jump: nothing. So the question is not whether the jump works, it is
        // whether the key press ever arrived, and those are answered in different places.
        //
        // This says so directly. It reports the first few keys the mod sees, whatever they are. If
        // pressing V produces a line here, the keyboard reaches us and the fault is further in. If
        // pressing anything at all produces nothing, the mod is not being given keys - which points
        // at how the session is being driven rather than at this code.
        private static KeyCode[] _allKeys;
        private static int _keysSeen;
        private static float _nextKeyLine;

        private void KeyProbe()
        {
            if (!_probe.Value || _keysSeen >= 12) return;
            try
            {
                if (!Input.anyKeyDown) return;
                float now = Time.realtimeSinceStartup;
                if (now < _nextKeyLine) return;
                _nextKeyLine = now + 0.2f;

                string typed = Input.inputString;
                string named = "";
                bool mouse = false;

                // EVERY KeyCode, not a shortlist. The shortlist version reported "a key down, not
                // one of the ones it names" nine times, which is the least useful sentence a probe
                // can produce: it proves something arrived and refuses to say what. The enum is
                // walked once and cached, so the cost is a loop over an array rather than any
                // reflection per frame.
                if (_allKeys == null)
                {
                    System.Array values = System.Enum.GetValues(typeof(KeyCode));
                    _allKeys = new KeyCode[values.Length];
                    for (int i = 0; i < values.Length; i++) _allKeys[i] = (KeyCode)values.GetValue(i);
                }

                for (int i = 0; i < _allKeys.Length; i++)
                {
                    KeyCode k = _allKeys[i];
                    if (!Input.GetKey(k)) continue;
                    if (k >= KeyCode.Mouse0 && k <= KeyCode.Mouse6) mouse = true;
                    if (named.Length < 60) named += (named.Length > 0 ? "+" : "") + k;
                }

                _keysSeen++;
                _log.Msg("key probe " + _keysSeen + "/12: "
                    + (named.Length > 0 ? named : "something was down but nothing reads as held")
                    + (mouse ? "  <- that is a MOUSE button, not a key" : "")
                    + (string.IsNullOrEmpty(typed) ? "" : ", typed '" + typed.Trim() + "'")
                    + ". Set Probe=false in the config to stop these.");
            }
            catch (System.Exception e)
            {
                Once("probe", "the key probe threw: " + e.Message);
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
                        + _lastForceUsed.ToString("0.000")
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
