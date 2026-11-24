using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using BepInEx;
using HarmonyLib;
using InControl;
using UnityEngine;
using UnityEngine.SceneManagement;

[BepInPlugin("local.silksong.tas", "Silksong TAS Practice", "0.1.0")]
[DefaultExecutionOrder(-30000)]
public sealed class SilkPractice : BaseUnityPlugin
{
    private static string root;
    private static bool controlled;
    private static string keys = "";
    private static readonly Dictionary<string, char> Buttons = new Dictionary<string, char>();
    private readonly Queue<string> input = new Queue<string>();
    private bool frozen;
    private bool verbose;
    private int framesLeft = -1;
    private int frame;
    private int lastBossHP = -1;
    private int lastHeroHP = -1;
    private HealthManager boss;
    private PlayMakerFSM bossControl;
    private string trial;
    private readonly List<string> movie = new List<string>();
    private int trialStart;
    private int trialArmedFrame;
    private int endFrame;
    private int bestHp;
    private int maxFocus;
    private int changedState;
    private string lastState = "";
    private bool useRing;
    private bool useRed;
    private int lastRed;
    private bool ringPogo;
    private bool drill;
    private int drillHits;
    private int lastDrillHp;
    private int drillJump;
    private int drillInitHp;
    private float startTime;
    private static readonly System.Reflection.FieldInfo HunterState =
        AccessTools.Field(typeof(HeroController), "hunterUpgState");

    private void Awake()
    {
        root = Environment.GetEnvironmentVariable("SILK_PRACTICE_DIR");
        if (string.IsNullOrEmpty(root)) { enabled = false; return; }
        Application.runInBackground = true;
        QualitySettings.vSyncCount = 0;
        Application.targetFrameRate = 60;
        Time.captureFramerate = 60;
        new Harmony("local.silksong.tas").Patch(
            AccessTools.Method(typeof(PlayerAction), "UpdateBindings"),
            prefix: new HarmonyMethod(typeof(SilkPractice), "Feed"));
        SceneManager.activeSceneChanged += (oldScene, newScene) => {
            boss = null;
            bossControl = null;
            Note("SCENE " + newScene.name);
        };
        Note("READY " + root);
    }

    private static bool Feed(PlayerAction __instance, ulong __0, float __1)
    {
        if (!controlled || !(__instance.Owner is HeroActions)) return true;
        char key;
        float value = Buttons.TryGetValue(__instance.Name, out key) && keys.IndexOf(key) >= 0 ? 1f : 0f;
        __instance.CommitWithValue(value, __0, __1);
        return false;
    }

    private static void InitActions()
    {
        var manager = FindAnyObjectByType<InControlManager>();
        if (manager) manager.suspendInBackground = false;
        var a = GameManager.instance.inputHandler.inputActions;
        PlayerAction[] actions = { a.Left, a.Right, a.Up, a.Down, a.Attack, a.Jump, a.Dash,
            a.SuperDash, a.DreamNail, a.Cast, a.QuickCast, a.Evade, a.Taunt, a.MenuSubmit };
        const string codes = "LRUDAJSHNBCETM";
        for(int i = 0; i < actions.Length; ++i) Buttons[actions[i].Name] = codes[i];
    }

    private void Update()
    {
        Application.targetFrameRate = 120;
        Time.captureDeltaTime = 1f / 60f;
        string cmd = Path.Combine(root, "command.txt");
        if (File.Exists(cmd))
        {
            try
            {
                string[] words = File.ReadAllText(cmd).Trim().Split(' ');
                File.Delete(cmd);
                Command(words);
            }
            catch (Exception e) { Note("ERROR " + e); Freeze(0); }
        }
        if (frozen) Time.timeScale = 0f;
        if (!frozen && trial != null) DriveFight();
        if (framesLeft > 0 && input.Count > 0) keys = input.Dequeue();
    }

    private void Command(string[] words)
    {
        Note("CMD " + string.Join(" ", words));
        switch (words[0])
        {
            case "load":
                controlled = false;
                Freeze(-1);
                GameManager.instance.LoadGameFromUI(2);
                break;
            case "warp":
                keys = "";
                Freeze(-1);
                GameManager.instance.ChangeToScene(words[1], words.Length > 2 ? words[2] : "", 0f);
                break;
            case "place":
                var h = HeroController.instance;
                var pos = new Vector2(float.Parse(words[1], CultureInfo.InvariantCulture), float.Parse(words[2], CultureInfo.InvariantCulture));
                var body = h.GetComponent<Rigidbody2D>();
                h.transform.position = new Vector3(pos.x,pos.y,h.transform.position.z);
                body.position = pos;
                body.linearVelocity = Vector2.zero;
                Physics2D.SyncTransforms();
                break;
            case "seed":
                UnityEngine.Random.InitState(int.Parse(words[1], CultureInfo.InvariantCulture));
                break;
            case "reset":
                trial = null;
                controlled = false;
                keys = "";
                Freeze(-1);
                GameManager.instance.ReturnToMainMenuNoSave();
                break;
            case "drill":
            case "trial":
                if (PlayerData.instance.atBench || !HeroController.instance.CanInput())
                    throw new InvalidOperationException("Wait for the bench load, then exit with step 150 RJ before starting a trial.");
                drill = words[0] == "drill";
                drillHits = 0;
                drillJump = -1000;
                lastDrillHp = 1800;
                InitActions();
                SetLoadout(words[2], words[3]);
                trial = words[1];
                useRing = words[3] == "rings";
                useRed = words[2] != "none";
                endFrame = int.Parse(words[4], CultureInfo.InvariantCulture) * 60;
                lastRed = -100000;
                trialStart = -1;
                trialArmedFrame = frame;
                bestHp = 1800;
                maxFocus = 0;
                keys = "";
                lastState = "";
                ringPogo = false;
                movie.Clear();
                controlled = true;
                UnityEngine.Random.InitState(int.Parse(words[5], CultureInfo.InvariantCulture));
                Freeze(-1);
                GameManager.instance.ChangeToScene("Abyss_Cocoon", "door_entry", 0f);
                break;
            case "loadout":
                SetLoadout(words[1], words[2]);
                break;
            case "save":
                GameManager.instance.SaveGame(done => Note("SAVED " + done));
                break;
            case "freeze":
                Freeze(0);
                break;
            case "release":
                controlled = false;
                Freeze(-1);
                break;
            case "step":
                InitActions();
                input.Clear();
                controlled = true;
                keys = words.Length > 2 ? words[2] : "";
                Freeze(int.Parse(words[1], CultureInfo.InvariantCulture));
                break;
            case "play":
                InitActions();
                controlled = true;
                input.Clear();
                foreach (string line in File.ReadAllLines(Path.Combine(root, "inputs.txt")))
                {
                    var p = line.Trim().Split(' ');
                    int count;
                    if (!int.TryParse(p[0], out count)) continue;
                    for (int i = 0; i < count; ++i) input.Enqueue(p.Length > 1 ? p[1] : "");
                }
                Freeze(input.Count);
                break;
            case "trace":
                verbose = words[1] == "on";
                break;
            case "inspect":
                Inspect();
                break;
            case "quit":
                Application.Quit();
                break;
        }
    }

    private void SetLoadout(string redMode, string blueMode)
    {
        string red = redMode == "brew" ? "Flea Brew" : redMode == "fire" ? "Flintstone" : "";
        string blue = blueMode == "rings" ? "Brolly Spike" : "";
        ToolItemManager.SetEquippedTools("Hunter_v3", new List<string> {"","","","Parry",red,"Longneedle","Barbed Wire"});
        ToolItemManager.SetExtraEquippedTool("Defend1", blue);
        Note("LOADOUT " + string.Join(",", ToolItemManager.GetEquippedToolsForCrest("Hunter_v3")
            .Where(t=>t).Select(t=>t.name).ToArray()) + " EXTRA=" + blue +
            " RED=" + BoundRed(AttackToolBinding.Neutral) + "/" + BoundRed(AttackToolBinding.Up) + "/" + BoundRed(AttackToolBinding.Down));
    }

    private static string BoundRed(AttackToolBinding binding)
    {
        var tool = ToolItemManager.GetBoundAttackTool(binding, (ToolEquippedReadSource)0);
        return tool ? tool.name : "-";
    }

    private void DriveFight()
    {
        if (trialStart < 0 && frame - trialArmedFrame > 2400)
        {
            Note("SETUP_TIMEOUT " + trial);
            trial = null;
            keys = "";
            Freeze(0);
            return;
        }
        var hero = HeroController.instance;
        if (!hero || !boss || boss.gameObject.scene.name != "Abyss_Cocoon")
        {
            keys = "";
            return;
        }
        if (!bossControl) bossControl = boss.GetComponents<PlayMakerFSM>().First(f=>f.FsmName=="Control");
        var state = bossControl.ActiveStateName ?? "";
        var pd = PlayerData.instance;
        bool onArenaFloor = hero.cState.onGround && hero.transform.position.y < 8f;
        bool started = onArenaFloor && (drill ? state == "Idle" : state.StartsWith("Start", StringComparison.Ordinal) || boss.hp < 1800);
        if (trialStart < 0 && hero.CanInput() && started)
        {
            trialStart = frame;
            Note((drill ? "DRILL_START " : "TRIAL_START ") + trial + " " + frame);
            startTime = Time.time;
            if (drill)
            {
                bossControl.enabled = false;
                var bossBody = boss.GetComponent<Rigidbody2D>();
                if (bossBody)
                {
                    bossBody.linearVelocity = Vector2.zero;
                    bossBody.constraints |= RigidbodyConstraints2D.FreezePosition;
                }
                foreach (var hazard in boss.GetComponentsInChildren<DamageHero>(true)) hazard.enabled = false;
                HunterState.SetValue(hero, new HeroController.HunterUpgCrestStateInfo { CurrentMeterHits = 12 });
                var hbody = hero.GetComponent<Rigidbody2D>();
                var hx = boss.transform.position.x - (useRing ? 2.35f : 2.05f);
                hbody.position = new Vector2(hx, hero.transform.position.y);
                hero.transform.position = new Vector3(hx, hero.transform.position.y, hero.transform.position.z);
                hbody.linearVelocity = Vector2.zero;
                hero.FaceRight();
                drillInitHp = boss.hp;
                lastDrillHp = boss.hp;
                Physics2D.SyncTransforms();
            }
        }
        bestHp = Math.Min(boss.hp, bestHp);
        maxFocus = Math.Max(maxFocus, hero.HunterUpgState.CurrentMeterHits);
        if (drill && trialStart >= 0 && boss.hp < lastDrillHp)
        {
            ++drillHits;
            Note("DRILL_HIT " + trial + " FRAME=" + (frame - trialStart) + " DAMAGE=" + (lastDrillHp - boss.hp) +
                " HP=" + boss.hp + " FOCUS=" + hero.HunterUpgState.CurrentMeterHits + " FIRE=" + hero.NailImbuement.CurrentElement + " BREW=" + hero.IsUsingQuickening + " INPUT=" + keys);
            lastDrillHp = boss.hp;
        }
        if (trialStart >= 0 && (pd.health <= 0 || boss.hp <= 0 || frame - trialStart >= endFrame))
        {
            File.WriteAllLines(Path.Combine(root, trial + ".inputs.txt"), movie
                .Select((input, i) => (i == 0 || movie[i-1] != input ? i.ToString(CultureInfo.InvariantCulture) + " " + input : null))
                .Where(line => line != null).ToArray());
            Note((drill ? "DRILL_END " : "TRIAL_END ") + trial + " FRAMES=" + (frame - trialStart) +
                " GAME_SECONDS=" + (Time.time - startTime).ToString("F3", CultureInfo.InvariantCulture) +
                " BOSS_HP=" + bestHp + " HERO_HP=" + pd.health + " MAX_FOCUS=" + maxFocus +
                (drill ? " DAMAGE=" + (drillInitHp - bestHp) + " HITS=" + drillHits : ""));
            trial = null;
            keys = "";
            Freeze(0);
            return;
        }
        if (state != lastState)
        {
            changedState = frame;
            if (trialStart >= 0) Note("MOVE " + trial + " " + (frame - trialStart) + " " + state);
            lastState = state;
        }
        string previous = keys;
        keys = "";
        if (!hero.CanInput())
        {
            movie.Add(keys);
            return;
        }
        float dx = boss.transform.position.x - hero.transform.position.x;
        float dy = boss.transform.position.y - hero.transform.position.y;
        float abs = Mathf.Abs(dx);
        bool ground = hero.cState.onGround;
        bool facing = hero.cState.facingRight == (dx >= 0);
        string toward = dx < 0 ? "L" : "R";
        string away = dx < 0 ? "R" : "L";
        bool towardRightWall = hero.transform.position.x > 56f;
        bool towardLeftWall = hero.transform.position.x < 18f;
        if (drill && trialStart >= 0)
        {
            int f = frame - trialStart;
            if (useRed && f < 14 && !hero.IsUsingQuickening && hero.NailImbuement.CurrentElement.ToString() == "None")
            {
                keys = f % 7 >= 2 && f % 7 <= 4 ? "DC" : "D";
            }
            else if (!useRing)
            {
                if (abs > 3.0f) keys = toward;
                if (abs > 1.2f && abs < 3.55f && frame % 4 < 2)
                    keys += (facing ? "" : toward) + "A";
            }
            else if (useRing)
            {
                if (ringPogo && hero.CanAttack())
                {
                    keys = toward + "DA";
                    ringPogo = false;
                }
                else if (ground && f - drillJump > 12 && previous.IndexOf('J') < 0 &&
                    (!useRed || f > 32))
                {
                    keys = "J";
                    drillJump = f;
                }
                else if (!ground && dy < -0.8f && dy > -5.1f && abs < 4f && hero.CanDoubleJump(false) &&
                    hero.CanAttack() && previous.IndexOf('J') < 0 && f - drillJump > 3)
                {
                    keys = toward + "DJ";
                    ringPogo = true;
                    drillJump = f;
                }
                else if (!ground && abs > 3.7f)
                    keys = toward;
                else if (ground && abs > 3.5f)
                    keys = toward;
                else if (ground && abs < 1.65f)
                    keys = away;
            }
            movie.Add(keys);
            return;
        }
        bool dodge = abs < 8.0f && (state.Contains("Charge") || state.Contains("Combo") ||
            state.Contains("Dash") || state.Contains("Riposte") || state.Contains("Counter") ||
            state.Contains("Tendril G") || state.Contains("Tendril A") || state.Contains("Lunge") || state.Contains("J Slash") ||
            state.Contains("Crossup") || state.Contains("RapidSlash") || state.Contains("Slash Antic"));
        bool vanished = state.Contains("Dive") && !state.Contains("Land");
        bool intro = state == "Dormant" || state.StartsWith("Start");
        bool preBuff = state == "Start Pause" && frame - changedState < 12 && lastRed < 0;
        if (useRed && (preBuff || (!intro && !dodge && frame - changedState < 20)) && frame - lastRed > 510 &&
            ground && (abs >= 4.8f || state.Contains("Roar") || state.Contains("Stun")) &&
            previous.IndexOf('C') < 0 && hero.CanThrowTool(false))
        {
            keys = "DC";
            lastRed = frame;
        }
        else if (ringPogo && dy < -0.6f && dy > -6.5f && abs < 5f)
        {
            keys = toward + "DA";
            ringPogo = false;
        }
        else if (dodge)
        {
            if (ground)
            {
                int since = frame - changedState;
                bool delaying = state == "Charge Antic" || (state == "Charge Break" && since < 22) ||
                    (state == "J Slash M Antic" && since < 17) ||
                    (state == "ComboSlash 1" && since < 23) ||
                    (state == "Tendril G Antic" && since < 20);
                bool airTendril = state.Contains("Tendril A");
                if (airTendril)
                    keys = (abs > 6f && abs < 7.7f) ? toward : (abs < 3f ? "" : toward);
                else
                    keys = (abs < 8f ? away : "") +
                        (!delaying && previous.IndexOf('J') < 0 && abs < 8f ? "J" : "");
            }
            else
            {
                var vertical = hero.GetComponent<Rigidbody2D>().linearVelocity.y;
                bool stayUp = vertical < 1f && hero.CanDoubleJump(false) && previous.IndexOf('J') < 0 &&
                    abs < 8f && (state.Contains("Combo") || state.Contains("Tendril") || state.Contains("Charge"));
                if (stayUp)
                    keys = away + "J";
                else if (dy < -1.5f && abs <= 3.35f && hero.CanAttack() && previous.IndexOf('A') < 0 &&
                    (state.EndsWith("End", StringComparison.Ordinal) || state.Contains("Recover")))
                    keys = toward + "DA";
                else
                    keys = abs > 7.5f ? "" : away;
            }
        }
        else if (!vanished && !state.Contains("Parry") && !state.Contains("Counter"))
        {
            if (!ground && useRing && dy < -1.3f && dy > -5.5f && abs < 3.7f &&
                hero.CanDoubleJump(false) && hero.CanAttack() && previous.IndexOf('J') < 0)
            {
                keys = toward + "DJ";
                ringPogo = true;
            }
            else if (hero.CanAttack() && previous.IndexOf('A') < 0 && !intro)
            {
                if (dy > 1.05f && dy < 4.3f && abs < 2.15f) keys = "UA";
                else if (dy < -1.1f && dy > -5.6f && abs < 4f) keys = toward + "DA";
                else if (Mathf.Abs(dy) < 1.55f && abs <= 3.85f && abs > 1.05f)
                    keys = (facing ? "" : toward) + "A";
            }
            if (keys.Length == 0)
            {
                if (abs > (intro ? 3.05f : 3.25f) && (Mathf.Abs(dy) < 4.8f || ground)) keys = toward;
                else if (abs < 1.65f && !(towardLeftWall && away == "L") && !(towardRightWall && away == "R")) keys = away;
            }
        }
        if (ground && pd.health <= 2 && pd.silk >= 9 && (vanished || abs > 9f) && previous.IndexOf('B') < 0)
            keys = "JB";
        movie.Add(keys);
    }

    private void Freeze(int count)
    {
        framesLeft = count;
        frozen = count == 0;
        Time.timeScale = frozen ? 0f : 1f;
    }

    private void LateUpdate()
    {
        if (frozen) return;
        ++frame;
        var game = GameManager.instance;
        var hero = HeroController.instance;
        if (game && hero && PlayerData.instance != null)
        {
            string scene = game.GetSceneNameString();
            if (!boss && scene.StartsWith("Abyss", StringComparison.Ordinal) && frame % 10 == 0)
                boss = FindObjectsByType<HealthManager>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                    .FirstOrDefault(m => m.gameObject.scene.name == scene &&
                        m.name.IndexOf("lace", StringComparison.OrdinalIgnoreCase) >= 0);
            int hp = boss ? boss.hp : -1;
            int own = PlayerData.instance.health;
            if (verbose || hp != lastBossHP || own != lastHeroHP || frame % 90 == 0)
                ReadState(scene, hero);
            lastBossHP = hp;
            lastHeroHP = own;
        }
        if (framesLeft > 0 && --framesLeft == 0)
        {
            Freeze(0);
            Note("STOP " + frame);
        }
    }

    private void ReadState(string scene, HeroController hero)
    {
        var pd = PlayerData.instance;
        var h = hero.transform.position;
        var b = boss ? boss.transform.position : Vector3.zero;
        string fsm = boss ? string.Join("|", boss.GetComponents<PlayMakerFSM>()
            .Select(f => f.FsmName + "=" + f.ActiveStateName).ToArray()) : "-";
        Note(string.Format(CultureInfo.InvariantCulture,
            "F {0} {1} H {2:F2} {3:F2} {4} {5} {6} {7} {8} {9} L {10:F2} {11:F2} {12} {13} {14} INPUT={15} FOCUS={16}",
            frame, scene, h.x, h.y, pd.health, pd.silk, hero.cState.onGround ? 1 : 0,
            hero.cState.facingRight ? 1 : 0, hero.CanInput() ? 1 : 0, hero.CanAttack() ? 1 : 0,
            b.x, b.y, boss ? boss.hp : -1, boss && boss.gameObject.activeInHierarchy ? 1 : 0, fsm, keys, hero.HunterUpgState.CurrentMeterHits));
    }

    private void Inspect()
    {
        var game = GameManager.instance;
        var hero = HeroController.instance;
        Note("INSPECT " + (game ? game.GetSceneNameString() : "-") + " " + Platform.Current.UserDataDirectory);
        Note("TIME " + Time.deltaTime + " " + Time.timeScale + " " + Time.fixedDeltaTime + " " + Time.captureFramerate);
        foreach (var bench in FindObjectsByType<RestBench>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (bench.gameObject.scene.name != SceneManager.GetActiveScene().name) continue;
            Note("BENCH " + bench.name + " " + bench.transform.position + " " + bench.gameObject.activeInHierarchy);
        }
        if (hero)
        {
            var pd = PlayerData.instance;
            Note("HERO " + hero.transform.position + " " + pd.CurrentCrestID + " HP=" + pd.health + " SILK=" + pd.silk + " RESPAWN=" + pd.respawnScene + "/" + pd.respawnMarkerName + " SEATED=" + pd.atBench + " CAP=" + Time.captureDeltaTime + " CTRL=" + hero.CanInput() + " ONGROUND=" + hero.cState.onGround);
        }
        foreach (var m in FindObjectsByType<HealthManager>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (m.gameObject.scene.name != SceneManager.GetActiveScene().name) continue;
            Note("ENEMY " + m.name + " " + m.hp + " " + m.transform.position + " " + m.gameObject.activeInHierarchy);
        }
        foreach (var t in FindObjectsByType<TransitionPoint>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (t.gameObject.scene.name != SceneManager.GetActiveScene().name) continue;
            Note("GATE " + t.name + " " + t.transform.position + " " + t.targetScene);
        }
    }

    private static void Note(string message)
    {
        File.AppendAllText(Path.Combine(root, "telemetry.txt"), message + Environment.NewLine);
    }
}

