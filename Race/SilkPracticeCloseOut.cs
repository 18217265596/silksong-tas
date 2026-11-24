using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection.Emit;
using System.Collections.Generic;
using BepInEx;
using HarmonyLib;
using InControl;
using UnityEngine;
using UnityEngine.SceneManagement;

[BepInPlugin("local.silksong.tas", "Silksong Wanderer TAS", "0.2.0")]
[DefaultExecutionOrder(-30000)]
public sealed class SilkPractice : BaseUnityPlugin
{
    private static string root;
    private static readonly System.Text.StringBuilder pendingTelemetry=new System.Text.StringBuilder();
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

    private HashSet<string> dumpedCue=new HashSet<string>();
    private void SaveRaceCue(AudioClip clip)
    {
        string n=clip.name;
        if(n!="flint_state_ambient_effect_option_loop"&&n!="flame_damage_ignite"&&n!="zap_burst_hit")return;
        if(!dumpedCue.Add(n))return;
        var samples=new float[clip.samples*clip.channels];
        var reader=typeof(AudioClip).GetMethod("GetData",new[]{typeof(float[]),typeof(int)});
        if(reader==null||!(bool)reader.Invoke(clip,new object[]{samples,0})){Note("CUE_UNREADABLE "+n);return;}
        string dir=Path.Combine(root,"native-cues");
        Directory.CreateDirectory(dir);
        using(var w=new BinaryWriter(File.Create(Path.Combine(dir,n+".wav"))))
        {
            w.Write(new[]{'R','I','F','F'});w.Write(36+samples.Length*2);
            w.Write(new[]{'W','A','V','E','f','m','t',' '});w.Write(16);w.Write((short)1);
            w.Write((short)clip.channels);w.Write(clip.frequency);w.Write(clip.frequency*clip.channels*2);
            w.Write((short)(clip.channels*2));w.Write((short)16);w.Write(new[]{'d','a','t','a'});w.Write(samples.Length*2);
            foreach(float x in samples)w.Write((short)(Mathf.Clamp(x,-1f,1f)*32767f));
        }
        Note("CUE_DUMP "+n);
    }
    private sealed class SoundCursor
    {
        public int clipId;
        public int position;
    }
    private readonly Dictionary<int, SoundCursor> soundCursors=new Dictionary<int, SoundCursor>();
    private void ObserveRaceSounds()
    {
        if(!raceRoar || (frame%3)!=0 || raceEndFrame>=0)return;
        var active=new HashSet<int>();
        foreach(var source in FindObjectsByType<AudioSource>(FindObjectsSortMode.None))
        {
            if(!source || !source.isPlaying || !source.clip)continue;
            var clip=source.clip; SaveRaceCue(clip);
            int id=source.GetInstanceID(), clipId=clip.GetInstanceID(), pos=source.timeSamples;
            active.Add(id);
            SoundCursor cursor;
            bool first=!soundCursors.TryGetValue(id,out cursor);
            if(first){cursor=new SoundCursor();soundCursors[id]=cursor;}
            if(first || cursor.clipId!=clipId || pos+256<cursor.position || frame%60==0)
            {
                var mix=source.outputAudioMixerGroup;
                string row=string.Join("\t",new[]{raceName,frame.ToString(),(raceStart<0?-1:frame-raceStart).ToString(),
                    id.ToString(),source.name,clip.name,pos.ToString(),clip.frequency.ToString(),clip.samples.ToString(),
                    source.pitch.ToString("R",CultureInfo.InvariantCulture),source.volume.ToString("R",CultureInfo.InvariantCulture),
                    source.loop?"1":"0",source.mute?"1":"0",mix?mix.name:""});
                Note("SOUND_SOURCE "+row);
            }
            cursor.clipId=clipId;cursor.position=pos;
        }
        foreach(int id in soundCursors.Keys.Where(k=>!active.Contains(k)).ToArray())
        {
            Note("SOUND_STOP "+raceName+" "+frame+" "+id);
            soundCursors.Remove(id);
        }
    }

    private void Awake()
    {
        root = Environment.GetEnvironmentVariable("SILK_PRACTICE_DIR");
        if (string.IsNullOrEmpty(root)) { enabled = false; return; }
        Application.runInBackground = true;
        AudioListener.volume = 0f;
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
        InstallGeometry();
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
        if (AudioListener.volume != 0f) AudioListener.volume = 0f;
        Time.captureDeltaTime = 1f / 60f;
        FlushNotes();
        string cmd = Path.Combine(root, "command.txt");
        if (File.Exists(cmd))
        {
            string line = null;
            try
            {
                line = File.ReadAllText(cmd);
                File.Delete(cmd);
            }
            catch (IOException) { line = null; }
            if (line != null)
            {
                try { Command(line.Trim().Split(' ')); }
                catch (Exception e) { Note("ERROR " + e); Freeze(0); }
            }
        }
        if (frozen) Time.timeScale = 0f;
        if (!frozen && fetchingCorpse) DriveCorpseRecovery();
        if (!frozen && trial != null) DriveFight();
        if (!frozen && raceName != null) {
            DriveRace();
            if(raceName!=null)ObserveRaceSounds();
            if(raceEndFrame<0&&raceStart>=0&&flowAttack&&keys.IndexOf('A')<0&&
               (flowAttach||keys.IndexOf('J')>=0))
                TryFlowNeedle(HeroController.instance,raceState,frame-raceStateSince,keys);
            if(raceEndFrame<0&&wanderRace&&frame<pressureLiftUntil&&
               keys.IndexOf('J')<0&&keys.IndexOf('S')<0&&keys.IndexOf('C')<0)
                keys+="J";
            if(raceEndFrame<0&&raceStart>=0&&raceBuild.Contains("reflex")&&
                (!raceBuild.Contains("reflexlate")||(boss&&boss.hp<1450)||raceBuild.Contains("reflexall")||
                 raceBuild.Contains("bodyguard")))
                GuardNextInput();
            if (raceName != null)
            {
                if(raceEndFrame<0&&raceStart>=0&&raceFirstFloor&&raceBuild.Contains("tempokeep")&&
                   !keys.Contains("A")&&!keys.Contains("C")&&!keys.Contains("S")&&!keys.Contains("H"))
                    TryFlowNeedle(HeroController.instance,raceState,frame-raceStateSince,keys,true);
                if(wanderRace&&raceBuild.Contains("strikefirst")&&raceEndFrame<0&&
                   raceStart>=0&&raceFirstFloor)
                    TryLastOpening(HeroController.instance);
                if(wanderRace&&raceStart>=0&&raceEndFrame<0&&branchAfterRoar&&
                   raceBuild.Contains("landlevel")&&raceState=="Bounce Back"&&keys.Contains("J")&&
                   HeroController.instance&&HeroController.instance.cState.onGround){
                    keys=keys.Replace("J","");
                    Note("LEVEL_RETURN "+raceName+" "+(frame-raceStart)+" input=["+keys+"]");
                }
                if(wanderRace&&raceEndFrame<0&&raceStart>=0&&branchAfterRoar&&
                   raceBuild.Contains("pogoalign"))
                    AlignReturnPogo(HeroController.instance);
                if(wanderRace&&raceBuild.Contains("chargenet")&&raceEndFrame<0)
                    KeepNeedleCharge();
                if(wanderRace&&raceEndFrame<0&&raceStart>=0&&raceBuild.Contains("counterready")&&
                   keys.Contains("A")&&CounterReadyForNeedle())
                {
                    keys=keys.Replace("A","");
                    Note("COUNTER_READY_CUT "+raceName+" "+(frame-raceStart));
                }
                if(wanderRace&&keys.Contains("C"))
                {
                    var pd=PlayerData.instance;
                    string use=keys.Contains("U")?"UC":keys.Contains("D")?"DC":"C";
                    bool spell=raceBuild.Contains("wspell")&&pd.silk-pd.SilkSkillCost>=(freeSilk?0:10);
                    if(use!=ToolInput("Flintstone")&&!spell)
                        keys=keys.Replace("C","");
                }
                if(traceAttack&&raceStart>=0&&raceEndFrame<0)
                {
                    ApplyAuditReplay();
                    TraceAttackFrame();
                }
                raceTape.Add(keys);
                previousRaceKeys=keys;
            }
        }
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
            case "race":
                ArmRace(words);
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
            case "recover":
                BeginCorpseRecovery();
                break;
            case "inspect":
                if (PlayerData.instance!=null)
                    Note("CORPSE_CHECK "+(string.IsNullOrEmpty(PlayerData.instance.HeroCorpseScene)?"none":
                        PlayerData.instance.HeroCorpseScene+" at="+PlayerData.instance.HeroDeathScenePos));
                Inspect();
                break;
            case "quit":
                Application.Quit();
                break;
        }
    }

    private void BeginCorpseRecovery()
    {
        if(raceName!=null)throw new InvalidOperationException("Wait until the stopped race has flushed its recording.");
        var pd=PlayerData.instance;
        if(string.IsNullOrEmpty(pd.HeroCorpseScene))
        {
            Note("RECOVERY_CLEAR no death cocoon");
            return;
        }
        InitActions();
        fetchingCorpse=true;
        fetchStart=frame;
        fetchAttack=fetchJump=-999;
        fetchApproach=false;
        controlled=true;
        keys="";
        Freeze(-1);
        string scene=pd.HeroCorpseScene;
        Note("RECOVERY_BEGIN "+scene+" position="+pd.HeroDeathScenePos);
        if(SceneManager.GetActiveScene().name!=scene)
            GameManager.instance.ChangeToScene(scene,scene=="Abyss_Cocoon"?"door_entry":"",0f);
    }

    private void DriveCorpseRecovery()
    {
        var pd=PlayerData.instance;
        if(string.IsNullOrEmpty(pd.HeroCorpseScene))
        {
            fetchingCorpse=false;
            keys="";
            Freeze(0);
            GameManager.instance.SaveGame(done=>Note("RECOVERY_CLEAR native cocoon retrieved saved="+done));
            return;
        }
        if(frame-fetchStart>2500)
        {
            fetchingCorpse=false;
            keys="";
            Freeze(0);
            Note("RECOVERY_BLOCKED cocoon still present "+pd.HeroCorpseScene);
            return;
        }
        var hero=HeroController.instance;
        if(!hero || !hero.CanInput() || SceneManager.GetActiveScene().name!=pd.HeroCorpseScene)
        {
            keys="";
            return;
        }
        var target=pd.HeroDeathScenePos;
        var guid=pd.HeroCorpseMarkerGuid;
        var marker=guid!=null&&guid.Length==16?HeroCorpseMarker.GetByGuid(guid):null;
        if(marker&&marker.gameObject.scene.name==pd.HeroCorpseScene)target=marker.Position;
        var pos=hero.transform.position;
        if(!fetchApproach && frame-fetchStart>=85 && (Mathf.Abs(pos.y-target.y)>9f || frame-fetchStart>245))
        {
            fetchApproach=true;
            var approach=new Vector2(target.x-2.15f,target.y+.45f);
            var rb=hero.GetComponent<Rigidbody2D>();
            hero.transform.position=new Vector3(approach.x,approach.y,pos.z);
            rb.position=approach;
            rb.linearVelocity=Vector2.zero;
            Physics2D.SyncTransforms();
            Note("RECOVERY_POSITION setup-only target="+target+" marker="+(marker?marker.name:"none"));
            keys="";
            return;
        }
        float dx=target.x-pos.x,dy=target.y-pos.y;
        string toward=dx<0?"L":"R";
        keys=Mathf.Abs(dx)>2.3f?toward:"";
        if(dy>1.45f && Mathf.Abs(dx)<5.5f && hero.CanJump() && frame-fetchJump>18)
        {
            fetchJump=frame;
            keys+= "J";
            return;
        }
        if(Mathf.Abs(dx)<3.5f && Mathf.Abs(dy)<4.2f && hero.CanAttack() && frame-fetchAttack>12)
        {
            fetchAttack=frame;
            string face=hero.cState.facingRight==(dx>=0)?"":toward;
            keys=(dy>1.8f?"U":dy< -1.4f?"D":face)+"A";
        }
        else if(Mathf.Abs(dx)<=2.3f) keys=toward;
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
        pendingTelemetry.AppendLine(message);
        FlushNotes();
    }
    private static void FlushNotes()
    {
        if(pendingTelemetry.Length==0)return;
        try
        {
            using(var file=new FileStream(Path.Combine(root,"telemetry.txt"), FileMode.Append, FileAccess.Write,
                                          FileShare.ReadWrite|FileShare.Delete))
            using(var writer=new StreamWriter(file))
                writer.Write(pendingTelemetry.ToString());
            pendingTelemetry.Length=0;
        }
        catch(IOException) {}
    }

    private string raceName;
    private string raceBuild;
    private bool wanderRace, alignCrit, wanderLongneedle, stockAttack, sustainAttack, freeSilk, sharpIntercept;
    private int quickRollLimit;
    private int sharpTried, sharpLanded, sharpThroughLanded, sharpLastPlan, sharpPlanMasks, sharpSpent;
    private bool sharpPlanThrough;
    private int wanderHits, wanderEligible, wanderCrits, wanderCapHits, wanderCapCrits, wanderSkips, wanderRolls;
    private int wanderDamage, wanderLastRollFrame, wanderLastRollSilk;
    private int lastWanderNeedle, nativeStuns, quickNeedles, nativeParries, pinClashFrame;
    private static readonly System.Reflection.FieldInfo CanClashNeedle =
        AccessTools.Field(typeof(DamageHero),"canClashTink");
    private static readonly System.Reflection.FieldInfo PreventClashNeedle =
        AccessTools.Field(typeof(DamageHero),"preventClashTink");
    private static readonly System.Reflection.FieldInfo NativeAttackCooldown =
        AccessTools.Field(typeof(HeroController),"attack_cooldown");
    private static readonly System.Reflection.FieldInfo WanderDashCombo =
        AccessTools.Field(typeof(HeroController),"wandererDashComboWindowTimer");
    private int pressureSwings, pressureWaits, pressureStartWait, pressureWaitUntil, pressureAttackFrame, pressureNote, pressureWaitDraws, pressureLiftUntil;
    private bool pressureHullsReady, haveRngObservation;
    private int previousRngDraws, stableRngFrames;
    private UnityEngine.Random.State previousRngState;
    private Collider2D[] pressureHulls;
    private static readonly System.Reflection.FieldInfo[] NativeSlashFields=new[]{
        "normalSlashDamager","alternateSlashDamager","upSlashDamager","altUpSlashDamager","downSlashDamager","altDownSlashDamager"
    }.Select(n=>AccessTools.Field(typeof(HeroController),n)).ToArray();

    private int racePolicy;
    private int raceLimit;
    private int raceArmed;
    private int raceStart;
    private int raceEndFrame;
    private int raceSilkLast;
    private int raceActualCasts;
    private int raceSilkSpent;
    private bool fetchingCorpse;
    private int fetchStart;
    private int fetchAttack;
    private int fetchJump;
    private bool fetchApproach;
    private int raceMinHp;
    private int raceLastHp;
    private int raceLastHero;
    private int raceStateSince;
    private string raceState;
    private bool raceRoar;
    private bool raceVideo;
    private bool raceRing;
    private bool raceFirstFloor;
    private bool raceOverlap;
    private int raceAttack;
    private int raceJump;
    private int raceDouble;
    private int raceDash;
    private int raceClaw;
    private int raceCast;
    private int raceFire;
    private int raceBrew;
    private int raceAuxTool;
    private int firstWaveApproach;
    private int freeToolStarted;
    private string freeToolName;
    private string freeToolState;
    private bool flintObserved;
    private int flintObservedAt;
    private static readonly System.Reflection.FieldInfo ChargeTimer=
        AccessTools.Field(typeof(HeroController),"nailChargeTimer");
    private static readonly System.Reflection.MethodInfo ChargeRequired=
        AccessTools.PropertyGetter(typeof(HeroController),"CurrentNailChargeTime");
    private bool chargedHold;
    private int chargeHoldAt, chargePlayableAt;
    private string chargedFrom;
    private int lastPlainImpact;
    private int lastHookWeave, lastHookImpact;
    private int lastTaunt;
    private float airWhipFacing;
    private string moulinetTravel="";
    private float raceStateNativeAge;
    private static readonly System.Reflection.FieldInfo TauntTime =
        AccessTools.Field(typeof(HeroController),"silkTauntEffectTimeLeft");
    private int ringPrimer;
        private string previousRaceKeys="";
    private string raceHeld = "";
    private int raceHoldUntil;
    private bool repairOpenings, preferAirUp, isolateLateAir, lateAirReady;
    private bool traceAttack, flowAttack, flowPhase, flowStrict, flowMeasured, flowAttach;
    private int flowSwingAt, flowSwings, flowLanded, flowPhaseSwings, flowPhaseLanded, flowJumpAttack;
    private bool flowSwingInPhase;
    private StreamWriter attackTrace;
    private string[] auditKeys, auditStates;
    private int[] auditHealth;
    private int auditDrift;
    private int shiftExitLogged, shiftVaultAt, airSwingAt;
    private int raceCaptureFrame;
    private bool raceStartedCapture;
    private StreamWriter raceVideoLog;
    private RenderTexture raceTarget;
    private Texture2D racePixels;
    private string raceVideoDir;
    private int raceVideoIndex;
    private int raceRetreatUntil;
    private string raceRetreatDirection = "";
    private readonly List<string> raceTape = new List<string>();

    private void ArmRace(string[] w)
    {
        var hero = HeroController.instance;
        if (PlayerData.instance.atBench || !hero.CanInput())
            throw new InvalidOperationException("Use step 150 RJ after the bench animation settles.");
        if (w[1].IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("Invalid run name.");
        if(!string.IsNullOrEmpty(PlayerData.instance.HeroCorpseScene))
            throw new InvalidOperationException("Retrieve the death cocoon before arming the race.");
        InitActions();
        InputManager.SuspendInBackground = false;
        raceName = w[1];
        raceBuild = w[2];
        wanderRace=raceBuild.StartsWith("wander-",StringComparison.Ordinal);
        stockAttack=wanderRace&&raceBuild.Contains("stockattack");
        sustainAttack=wanderRace&&raceBuild.Contains("sustain");
        freeSilk=wanderRace&&raceBuild.Contains("freesilk");
        sharpIntercept=freeSilk&&raceBuild.Contains("sharpintercept");
        repairOpenings=wanderRace&&raceBuild.Contains("airwindows");
        preferAirUp=repairOpenings&&raceBuild.Contains("upwindow");
        isolateLateAir=repairOpenings&&raceBuild.Contains("lateair");
        lateAirReady=false;
        traceAttack=wanderRace&&raceBuild.Contains("traceattack");
        flowAttack=wanderRace&&raceBuild.Contains("flowattack");
        flowPhase=flowAttack&&raceBuild.Contains("groundphase");
        flowStrict=flowAttack&&raceBuild.Contains("flowstrict");
        flowMeasured=flowAttack&&raceBuild.Contains("flowmeasured");
        flowAttach=flowAttack&&raceBuild.Contains("flowattach");
        flowSwingAt=-10000;
        flowSwings=flowLanded=flowPhaseSwings=flowPhaseLanded=flowJumpAttack=0;
        auditKeys=null;
        auditDrift=0;
        if(traceAttack&&raceBuild.Contains("auditreplay")) LoadAuditReplay();
        shiftExitLogged=shiftVaultAt=airSwingAt=-10000;
        sharpTried=sharpSpent=sharpLanded=sharpThroughLanded=0;
        sharpLastPlan=-10000;sharpPlanMasks=0;sharpPlanThrough=false;
        alignCrit=wanderRace&&!stockAttack&&raceBuild.Contains("critbank");
        quickRollLimit=wanderRace&&!stockAttack&&raceBuild.Contains("quickroll")?16:0;
        wanderHits=wanderEligible=wanderCrits=wanderCapHits=wanderCapCrits=wanderSkips=wanderRolls=wanderDamage=0;
        wanderLastRollFrame=-10000; wanderLastRollSilk=0;
        pressureSwings=pressureWaits=pressureWaitDraws=nativeStuns=quickNeedles=0;
        lastWanderNeedle=pinClashFrame=-10000;
        nativeParries=0;
        pressureStartWait=pressureWaitUntil=pressureAttackFrame=pressureNote=pressureLiftUntil=-10000;
        previousRngDraws=stableRngFrames=0;
        pressureHulls=null;
        pressureHullsReady=haveRngObservation=false;
        racePolicy = int.Parse(w[3], CultureInfo.InvariantCulture);
        raceLimit = int.Parse(w[4], CultureInfo.InvariantCulture) * 60;
        raceVideo = w.Length > 6 && w[6] == "rec";
        raceRing = raceBuild.Contains("ring");
        raceArmed = frame;
        raceStart = -1;
        raceEndFrame = -1;
        raceSilkLast=-1;
        raceActualCasts=0;
        raceSilkSpent=0;
        raceState = "";
        raceStateSince = frame;
        raceMinHp = 1800;
        raceLastHp = 1800;
        raceLastHero = PlayerData.instance.health;
        raceRoar = false;
        raceFirstFloor = false;
        raceOverlap = false;
        raceStartedCapture = false;
        raceVideoIndex = 0;
        raceCaptureFrame = -1;
        raceHeld = "";
        previousRaceKeys="";
        raceHoldUntil = frame;
        raceAttack = raceJump = raceDouble = raceDash = raceClaw = raceCast =
            raceFire = raceBrew = raceAuxTool = ringPrimer = raceRetreatUntil = -10000;
        firstWaveApproach=freeToolStarted=-10000;
        freeToolName=freeToolState="";
        flintObserved=false;
        flintObservedAt=-10000;
        chargedHold=false;
        chargedFrom="";
        moulinetTravel="";
        chargeHoldAt=chargePlayableAt=-10000;
        lastPlainImpact=lastHookWeave=lastHookImpact=lastTaunt=-10000;
        raceTape.Clear();
        combatSeed=int.Parse(w[5],CultureInfo.InvariantCulture);
        choiceTapeCursor=0;
        branchAfterRoar=seenReturnBounce=firstReturnAttack=false;
        returnBias=raceBuild.Contains("returnleft")?-1.55f:raceBuild.Contains("returnright")?1.55f:0f;
        shiftedSpell=false;
        branchStun=null;
        branchOptionsAt=-1;
        branchOptionsState="";
        branchProfile=raceBuild.Contains("branchlab")?(w.Length>7?w[7]:"observe"):null;
        choiceTape=raceBuild.Contains("choiceplayback")?LoadChoiceTape():null;
        InitGeometry();
        if(wanderRace) ConfigureWanderer(); else ConfigureRace();
        controlled = true;
        keys = "";
        Freeze(-1);
        if(!stockAttack) UnityEngine.Random.InitState(combatSeed);
        GameManager.instance.ChangeToScene("Abyss_Cocoon", "door_entry", 0f);
        Note("RACE_ARM " + raceName + " " + raceBuild + " policy=" + racePolicy + " seed=" + w[5]);
    }

    private void ConfigureWanderer()
    {
        var pd=PlayerData.instance;
        var crest=GlobalSettings.Gameplay.WandererCrest;
        if(!crest||pd.CurrentCrestID!=crest.name)
            throw new InvalidOperationException("Slot 2 must already use its Wanderer crest.");
        var gear=ToolItemManager.GetEquippedToolsForCrest(crest.name);
        Note("WANDER_SAVE_GEAR "+crest.name+" "+string.Join(",",gear.Select(it=>it?it.name:"-").ToArray()));
        var chosen=raceBuild.Contains("savedgear")?"":raceBuild.Contains("toolbrew")?"Flea Brew":
                   raceBuild.Contains("toolfire")?"Flintstone":"";
        string skill=raceBuild.Contains("wspellpale")?"Silk Boss Needle":
                     raceBuild.Contains("wspellsharp")?"Silk Charge":
                     raceBuild.Contains("wspellcross")?"Parry":"";
        if(chosen.Length>0||skill.Length>0)
        {
            var red=Array.FindIndex(crest.Slots.ToArray(),slot=>slot.Type.ToString()=="Red"||slot.Type.ToString()=="Attack");
            if(red<0||red>=gear.Count)throw new InvalidOperationException("Could not find Wanderer's red slot.");
            if(chosen.Length>0&&!ToolItemManager.GetUnlockedTools().Any(t=>t&&t.name==chosen))
                throw new InvalidOperationException("The requested red tool is not unlocked.");
            var names=gear.Select(it=>it?it.name:"").ToList();
            if(chosen.Length>0)names[red]=chosen;
            if(raceBuild.Contains("pinbelt"))
            {
                int magnet=names.IndexOf("Rosary Magnet");
                var slots=crest.Slots.ToArray();
                int openBlue=Enumerable.Range(0,names.Count)
                    .Where(i=>slots[i].Type.ToString()=="Blue"&&string.IsNullOrEmpty(names[i]))
                    .DefaultIfEmpty(-1).First();
                var unlocked=ToolItemManager.GetUnlockedTools();
                if(magnet>=0&&unlocked.Any(t=>t&&t.name=="Weighted Anklet"))names[magnet]="Weighted Anklet";
                if(openBlue>=0&&unlocked.Any(t=>t&&t.name=="Longneedle"))names[openBlue]="Longneedle";
            }
            if(skill.Length>0)
            {
                var silkSlot=Array.FindIndex(crest.Slots.ToArray(),slot=>slot.Type.ToString()=="Skill");
                if(silkSlot<0)
                    throw new InvalidOperationException("Wanderer slot types: "+string.Join("/",crest.Slots.Select(slot=>slot.Type.ToString()).ToArray()));
                if(!ToolItemManager.GetUnlockedTools().Any(t=>t&&t.name==skill))
                    throw new InvalidOperationException("The requested skill is not unlocked: "+skill);
                names[silkSlot]=skill;
                Note("WANDER_SPELL_CONFIG "+raceName+" "+skill+" slot="+silkSlot);
            }
            ToolItemManager.SetEquippedTools(crest.name,names);
            gear=ToolItemManager.GetEquippedToolsForCrest(crest.name);
        }
        if(raceBuild.Contains("savedgear"))
        {
            var extra=pd.ExtraToolEquips;
            var extraNames=extra.GetValidNames(null);
            Note("WANDER_EXTRA_GEAR "+raceName+" "+
                 string.Join("|",extraNames.Select(name=>name+"="+extra.GetData(name).EquippedTool).ToArray())+
                 " volt="+ToolItemManager.IsToolEquipped("Zap Imbuement")+
                 " belt="+ToolItemManager.IsToolEquipped("Weighted Anklet")+
                 " longclaw="+ToolItemManager.IsToolEquipped("Longneedle"));
        }
        wanderLongneedle=ToolItemManager.IsToolEquipped("Longneedle");
        Note("WANDER_RULE "+raceName+" chance="+GlobalSettings.Gameplay.WandererCritChance+
             " luck="+HeroController.instance.GetLuckModifier()+" multiplier="+GlobalSettings.Gameplay.WandererCritMultiplier+
             " silkMinAfterCast="+(freeSilk?0:10)+" maxSilk="+pd.CurrentSilkMax+" rng="+(alignCrit?"advance":quickRollLimit>0?"same-frame-bounded":"stock")+
             " stockAttack="+stockAttack+" sustain="+sustainAttack);
        Note("WANDER_GEAR "+raceName+" "+crest.name+" "+string.Join(",",gear.Select(it=>it?it.name:"-").ToArray())+
             " bind="+BoundRed(AttackToolBinding.Neutral)+"/"+BoundRed(AttackToolBinding.Up)+"/"+BoundRed(AttackToolBinding.Down));
    }

    private void ConfigureRace()
    {
        bool guard = !raceBuild.Contains("max");
        string skill = raceBuild.Contains("cross") ? "Parry" : raceBuild.Contains("sharp") ? "Silk Charge" :
            raceBuild.Contains("nails") ? "Silk Boss Needle" : "Silk Charge";
        bool noWisp = raceBuild.Contains("nowisp") || raceBuild.Contains("crossreact");
        string extra = !noWisp && raceBuild.Contains("lantern") ? "Wisp Lantern" : "Zap Imbuement";
        string crest = "Hunter_v3";
        var crestAsset=ToolItemManager.GetCrestByName(crest);
        Note("HUNTER_SLOTS " + string.Join(";",crestAsset.Slots.Select((slot,index)=>
            index + ":" + slot.Type + ":" + slot.AttackBinding + ":defaultLocked=" + slot.IsLocked).ToArray()));
        string upper = raceBuild.Contains("dual") || raceBuild.Contains("brew") ? "Flea Brew" :
            raceBuild.Contains("fly") ? "Cogwork Flier" : "";
        string lower = raceBuild.Contains("dual") || raceBuild.Contains("fire") ? "Flintstone" :
            raceBuild.Contains("fly") && raceBuild.Contains("both") ? "Flintstone" : "";
        if(raceBuild.Contains("cog"))
        {
            upper="Cogwork Flier";
            lower=raceBuild.Contains("cogbrew")?"Flea Brew":"Flintstone";
        }
        var gear = new List<string> {upper,"Longneedle","Weighted Anklet",skill,lower,
            raceRing ? "Brolly Spike" : "Zap Imbuement", "Barbed Wire"};
        if (!raceRing && extra=="Zap Imbuement") extra=noWisp ? "" : "Wisp Lantern";
        if(noWisp)
        {
            var owned=ToolItemManager.GetUnlockedTools().Where(t=>t).ToArray();
            if(raceBuild.Contains("egg") && owned.Any(t=>t.name=="Flea Charm")) extra="Flea Charm";
            else if(!raceRing && owned.Any(t=>t.name=="White Ring")) extra="White Ring";
        }
        ToolItemManager.SetEquippedTools(crest, gear);
        ToolItemManager.SetEquippedCrest(crest);
        if(raceBuild.Contains("steady") && raceBuild.Contains("egg"))
            extra="Flea Charm";
        ToolItemManager.SetExtraEquippedTool("Defend1", extra);
        bool saveSprintSilk=raceBuild.Contains("nosilkspeed");
        ToolItemManager.SetExtraEquippedTool("Explore1", saveSprintSilk?"":"Sprintmaster");
        if(noWisp)
        {
            var extraSlots=PlayerData.instance.ExtraToolEquips;
            foreach(var slot in extraSlots.GetValidNames(null))
                if(extraSlots.GetData(slot).EquippedTool=="Wisp Lantern" ||
                   saveSprintSilk && extraSlots.GetData(slot).EquippedTool=="Sprintmaster")
                    ToolItemManager.SetExtraEquippedTool(slot,"");
        }
        Note("RACE_GEAR " + crest + " " + string.Join(",", ToolItemManager.GetEquippedToolsForCrest(crest)
            .Where(t=>t).Select(t=>t.name).ToArray()) + " extra=" + extra +
            " bind=" + BoundRed(AttackToolBinding.Neutral) + "/" + BoundRed(AttackToolBinding.Up) + "/" + BoundRed(AttackToolBinding.Down));
    }

    private static float Dist(float x) { return Mathf.Abs(x); }

    private void RacePress(string input, int duration)
    {
        keys = input;
        raceHeld = input;
        raceHoldUntil = frame + duration;
        if(!raceBuild.Contains("edgekeys"))return;

        // InControl needs one released tick before a held action can be pressed again.
        const string edges="JSAHC";
        keys=new string(input.Where(c=>edges.IndexOf(c)<0||previousRaceKeys.IndexOf(c)<0).ToArray());
        if(keys!=input)
        {
            ++raceHoldUntil;
            if(raceStart>=0)Note("KEY_REPRESS "+raceName+" "+(frame-raceStart)+" release="+keys+" resume="+input+" state="+raceState);
        }
    }

    private string ToolInput(string item)
    {
        var bindings = new[]{AttackToolBinding.Down,AttackToolBinding.Up,AttackToolBinding.Neutral};
        string[] inputs = {"DC","UC","C"};
        for(int i=0;i<bindings.Length;i++)
        {
            var tool = ToolItemManager.GetBoundAttackTool(bindings[i], ToolEquippedReadSource.Active);
            if (tool && tool.name == item && !tool.IsEmpty) return inputs[i];
        }
        return null;
    }

    private bool UseRaceTool(HeroController hero, string item)
    {
        if (frame < raceHoldUntil || !hero.CanThrowTool(false)) return false;
        string action = ToolInput(item);
        if (action == null) return false;
        RacePress(action, 3);
        if(wanderRace)Note("WANDER_TOOL "+raceName+" frame="+(raceStart<0?-1:frame-raceStart)+" "+item+" via="+action);
        return true;
    }

    private void TrackFreeTool(string item,string state)
    {
        freeToolStarted=frame;
        freeToolState=state;
        freeToolName=item;
        Note("FREE_TOOL "+raceName+" "+(raceStart<0?-1:frame-raceStart)+" item="+item+" state="+state);
    }

    private bool TryPulseReentry(HeroController hero,float dx,bool grounded)
    {
        if(!hero.CanInput())return false;
        float gap=Mathf.Abs(dx);
        float facing=Mathf.Sign(dx);
        string toward=facing<0?"L":"R";
        float settle=returnBias!=0f?.36f:raceBuild.Contains("centerwave")?
            (raceBuild.Contains("centertwo")?1.65f:1.2f):raceBuild.Contains("flinttangent")?.72f:3.2f;
        string drift=gap>settle?toward:"";
        if(gap<=settle){keys="";return true;}
        bool direct=raceBuild.Contains("returndirect");
        if(hero.cState.dashing)
        {
            if(direct)keys=toward;
            return direct;
        }
        var box=hero.heroBox?hero.heroBox.GetComponent<Collider2D>():null;
        var rigid=hero.GetComponent<Rigidbody2D>();
        if(!box||!rigid)return false;
        if(!direct&&!grounded&&hero.transform.position.y<10.2f)return false;
        ReadHitboxes(hero);
        AddFlailForecast(hero.transform.position);
        float floor=grounded?box.bounds.center.y:currentFloor+(box.bounds.center.y-hero.transform.position.y);
        string hazard;
        if(gap>8.0f&&hero.CanDash()&&frame-raceDash>24&&!previousRaceKeys.Contains("S")&&
           Forecast(box.bounds,rigid.linearVelocity,floor,facing*hero.DASH_SPEED,0f,0,true,!grounded,out hazard,13)<1.4f)
        {
            raceDash=frame;
            RacePress(toward+"S",5);
            Note("WAVE_REENTRY_DASH "+raceName+" "+(frame-raceStart)+" "+keys+" gap="+
                 gap.ToString("F2",CultureInfo.InvariantCulture));
        }
        else
        {
            if(!grounded&&previousRaceKeys.Contains("J")&&rigid.linearVelocity.y>0.7f)drift+="J";
            keys=drift;
        }
        return true;
    }

    private void KeepNeedleCharge()
    {
        var hero=HeroController.instance;
        if(!hero||!boss||ChargeTimer==null||ChargeRequired==null||!hero.Config.CanNailCharge)return;
        float required=(float)ChargeRequired.Invoke(hero,null);
        bool intro=raceStart<0&&(raceState=="Start"||raceState=="Start Pause");
        bool interval=raceStart>=0&&(
            raceState=="Antic Wave"||raceState=="P3 Dive Out"||raceState=="P3 Roar"||
            raceState=="CS Thwip Out"&&raceBuild.Contains("chargelate")||
            raceState=="P3 Roar End"||raceState=="P3 Start");
        bool ready=(float)ChargeTimer.GetValue(hero)>=required-.018f;
        if(intro&&!raceBuild.Contains("chargeopen"))intro=false;
        if(interval&&!raceBuild.Contains("chargemid")&&raceState!="CS Thwip Out")interval=false;
        if(intro||interval)
        {
            if(keys.Contains("C")||frame-raceFire<53&&!chargedHold)return;
            if(!chargedHold)
            {
                chargedHold=true;
                chargeHoldAt=frame;
                chargePlayableAt=-10000;
                chargedFrom=raceState;
                Note("NEEDLE_HOLD "+raceName+" "+(raceStart<0?-1:frame-raceStart)+" from="+chargedFrom+
                     " chargeTime="+required.ToString("F3",CultureInfo.InvariantCulture));
            }
            if(!keys.Contains("A"))keys+="A";
            return;
        }
        if(!chargedHold)return;
        if(!hero.CanInput())
        {
            if(!keys.Contains("A"))keys+="A";
            return;
        }
        if(chargePlayableAt<0)chargePlayableAt=frame;
        int defer=frame-chargePlayableAt;
        float dx=boss.transform.position.x-hero.transform.position.x;
        float dy=boss.transform.position.y-hero.transform.position.y;
        if(!boss.IsInvincible&&hero.cState.onGround&&Mathf.Abs(dy)<1.48f&&
           Mathf.Abs(dx)>1.45f&&Mathf.Abs(dx)<7.4f&&ready&&
           (raceState=="Idle"||raceState=="Dive Out G"||raceState=="Land"||raceState=="Downstab Land"||
            raceState=="Stunned"||raceState=="ComboSlash 1"&&frame-raceStateSince<9))
        {
            var toward=dx<0?"L":"R";
            keys=toward;
            raceHoldUntil=frame;
            chargedHold=false;
            Note("NEEDLE_RELEASE "+raceName+" "+(frame-raceStart)+" from="+chargedFrom+" state="+raceState+
                 " held="+(frame-chargeHoldAt)+" dx="+dx.ToString("F2",CultureInfo.InvariantCulture));
            return;
        }
        if(boss.IsInvincible&&defer<=10||ready&&defer<=5&&!keys.Contains("C")&&!keys.Contains("S"))
        {
            if(!keys.Contains("A"))keys+="A";
            return;
        }
        chargedHold=false;
        keys=keys.Replace("A","");
        Note("NEEDLE_RESUME "+raceName+" "+(frame-raceStart)+" from="+chargedFrom+" charged="+ready+
             " elapsed="+(frame-chargeHoldAt)+" native="+((float)ChargeTimer.GetValue(hero)).ToString("F3",CultureInfo.InvariantCulture));
    }

    private bool TryWaveChallenge(HeroController hero,int age,float dx)
    {
        if(age<38||age>69||frame-lastTaunt<220||Mathf.Abs(dx)>2.0f||
           hero.cState.dashing||PlayerData.instance.silk<10||
           (float)TauntTime.GetValue(hero)>0f)
            return false;
        var actor=hero.heroBox?hero.heroBox.GetComponent<Collider2D>():null;
        var rb=hero.GetComponent<Rigidbody2D>();
        if(!actor||!rb)return false;
        ReadHitboxes(hero);
        AddFlailForecast(hero.transform.position);
        string hazard;
        if(Forecast(actor.bounds,rb.linearVelocity,actor.bounds.center.y,
           0f,0f,0,false,false,out hazard,23)>1.1f)return false;
        lastTaunt=frame;
        RacePress("T",1);
        Note("WAVE_CHALLENGE "+raceName+" "+(frame-raceStart)+" age="+age);
        return true;
    }

    private bool TryFireWindow(HeroController hero,string state,int age,float dx)
    {
        if(flintObserved||frame-raceFire<155||hero.cState.dashing||!hero.CanThrowTool(false)||
           ToolInput("Flintstone")==null)return false;
        const int recovery=53;
        int spare=0;
        float x=hero.transform.position.x;
        bool lateCenter=raceBuild.Contains("flintcenter");
        bool tangent=raceBuild.Contains("flinttangent");
        float gap=Mathf.Abs(dx);
        if(state=="Antic Wave"&&raceBuild.Contains("flintlate"))
        {
            bool applies=tangent?age>=109&&age<=120&&(gap<=1.22f||age>=119&&gap<=3.7f):
                         lateCenter?age>=98&&age<=120&&(gap<=3.35f||age>=116&&gap<=4.6f):
                         age>=63&&age<=104&&x>=23.3f&&x<=34.9f;
            if(applies)spare=127-age+17+121;
        }
        if(state=="CS Thwip Out"&&age>=31&&age<=53)spare=109-age;
        if(state=="Tendril Emerge Wait"&&age<=13&&Mathf.Abs(dx)>=7.0f)spare=91-age;
        if(state=="Stun Recover Pause"&&age<=3&&Mathf.Abs(dx)>=7.1f)spare=56-age;
        if(spare<recovery)return false;
        Bounds actorShape,bossBox;
        if(state=="Tendril Emerge Wait"&&ReadPinnedTarget(hero,out actorShape,out bossBox))return false;
        var actor=hero.heroBox?hero.heroBox.GetComponent<Collider2D>():null;
        var rigid=hero.GetComponent<Rigidbody2D>();
        if(!actor||!rigid)return false;
        ReadHitboxes(hero);
        AddFlailForecast(hero.transform.position);
        string hazard;
        if(Forecast(actor.bounds,rigid.linearVelocity,actor.bounds.center.y,0f,0f,0,false,false,out hazard,23)>1.55f)
            return false;
        if(!UseRaceTool(hero,"Flintstone"))return false;
        raceFire=frame;
        TrackFreeTool("Flintstone",state);
        Note("FLINT_WINDOW "+raceName+" "+(frame-raceStart)+" state="+state+
             " age="+age+" spare="+spare+" gap="+Mathf.Abs(dx).ToString("F2",CultureInfo.InvariantCulture));
        return true;
    }

    private void DriveRace()
    {
        if (raceEndFrame >= 0)
        {
            keys = "";
            return;
        }
        if (raceStart < 0 && frame - raceArmed > 2500)
        {
            EndRace("SETUP");
            return;
        }
        var hero = HeroController.instance;
        if (!hero || !boss || boss.gameObject.scene.name != "Abyss_Cocoon")
        {
            keys = "";
            return;
        }
        if (!bossControl) bossControl = boss.GetComponents<PlayMakerFSM>().First(f=>f.FsmName=="Control");
        var pd = PlayerData.instance;
        string state = bossControl.ActiveStateName ?? "";
        string previousState = raceState;
        bool changed = state != previousState;
        if (changed)
        {
            raceState = state;
            raceStateSince = frame;
            raceStateNativeAge=0f;
        }
        else raceStateNativeAge+=Time.deltaTime*60f;
        Vector3 heroPos = hero.transform.position;
        Vector3 bossPos = boss.transform.position;
        float dx = bossPos.x-heroPos.x;
        float dy = bossPos.y-heroPos.y;
        float abs = Dist(dx);
        bool ground = hero.cState.onGround;
        bool hasControl = hero.CanInput();
        if(changed&&state=="Tendril A Antic")
            airWhipFacing=Mathf.Abs(dx)>.2f?-Mathf.Sign(dx):Mathf.Sign(boss.transform.localScale.x);
        if(wanderRace&&raceBuild.Contains("flintwindow"))
        {
            bool active=hero.NailImbuement.CurrentElement.ToString()=="Fire";
            if(active!=flintObserved)
            {
                Note("FLINT_STATE "+raceName+" "+(raceStart<0?-1:frame-raceStart)+
                     " active="+active+" fromInput="+(frame-raceFire)+
                     " duration="+(active?0:frame-flintObservedAt)+" state="+state);
                flintObserved=active;
                if(active)flintObservedAt=frame;
            }
        }
        if(isolateLateAir&&boss.hp<=900&&state=="Dive Out A")lateAirReady=true;
        if(changed&&raceStart>=0&&raceBuild.Contains("centerwave")&&
           (state=="Set Roar Pos"||state=="Antic Wave"||state=="P3 Dive Out"||
            state=="P3 Roar"||previousState=="P3 Roar"))
            Note("RETURN_POS "+raceName+" "+(frame-raceStart)+" "+state+" hx="+
                 heroPos.x.ToString("F3",CultureInfo.InvariantCulture)+" bx="+
                 bossPos.x.ToString("F3",CultureInfo.InvariantCulture)+" silk="+pd.silk);
        if (state.StartsWith("P2 Shift",StringComparison.Ordinal)) shiftedPhase=true;
        if (state.StartsWith("Summon ",StringComparison.Ordinal) || state=="Summon")
        {
            summonUntil=frame+85;
            if(state=="Summon Antic"&&changed) summonStarted=frame;
            if (state=="Summon Thwip"&&changed) summonAimX=heroPos.x;
        }
        if(state=="Charge") lastChargeSeen=frame;
        if(state.StartsWith("Vomit",StringComparison.Ordinal)) lastVomit=frame;
        if(state.StartsWith("Bullet S ",StringComparison.Ordinal)) lastBulletAt=frame;
        if(state=="Tendril Emerge Wait"&&changed) emergingStarted=frame;
        if(branchProfile!=null&&changed)
        {
            if(state=="P3 Roar")branchAfterRoar=true;
            if(branchAfterRoar&&(state=="Bounce Back"||state=="Bounce Back ToDive"))seenReturnBounce=true;
            if(state=="Stop"&&!branchAfterRoar||branchAfterRoar&&
               (previousState=="P3 Roar"||state=="Stun Start"||state=="Bullet S Ground 1"))
                LogBranchStun(state);
        }
        if(state=="Wave L"&&changed&&previousState=="Silk Fall")
            firstWaveApproach=frame;
        if(freeToolName.Length>0 && frame-freeToolStarted>=5 && hero.CanAttack() && hero.CanCast() && hero.CanInput())
        {
            Note("FREE_TOOL_READY "+raceName+" "+(raceStart<0?-1:frame-raceStart)+" item="+freeToolName+
                " elapsed="+(frame-freeToolStarted)+" state="+state+" from="+freeToolState);
            freeToolName="";
        }
        if (ground&&heroPos.y>5f&&heroPos.y<8f) currentFloor=heroPos.y;
        if (boss.hp<1350 || shiftedPhase) lateRisk=true;
        if (state == "Start Pause") raceRoar = true;
        if (ground && heroPos.y < 9f && heroPos.y > 4f) raceFirstFloor = true;
        if (raceStart < 0 && raceRoar && raceFirstFloor && !state.StartsWith("Start", StringComparison.Ordinal) &&
            state != "Dormant" && state.Length > 0 && hasControl)
        {
            if(raceBuild.Contains("empty"))
            {
                if(!string.IsNullOrEmpty(pd.HeroCorpseScene))
                {
                    EndRace("CORPSE");
                    return;
                }
                int pre= pd.silk;
                if(pre>0) hero.TakeSilk(pre);
                if(pd.silk!=0)
                {
                    Note("START_LOAD_FAILED "+raceName+" silk="+pd.silk);
                    EndRace("SETUP");
                    return;
                }
                bool wisp=ToolItemManager.IsToolEquipped("Wisp Lantern");
                if(raceBuild.Contains("nowisp")&&wisp)
                {
                    Note("START_LOAD_FAILED "+raceName+" Wisp Lantern still active");
                    EndRace("SETUP");
                    return;
                }
                if(raceBuild.Contains("tightstart"))
                    Note("OPEN_POSITION "+raceName+" gap="+abs.ToString("F2",CultureInfo.InvariantCulture)+" ground="+ground);
                Note("START_LOAD "+raceName+" silk=0 corpse=none setupSpool="+pre+" wisp="+wisp+
                    " skillCost="+pd.SilkSkillCost+" silkspeed="+ToolItemManager.IsToolEquipped("Sprintmaster"));
            }
            raceStart = frame;
            if(raceBuild.Contains("choiceaudit"))
                Note("PHYS_START "+raceName+" "+frame+" step="+
                     (Time.timeAsDouble-Time.fixedTimeAsDouble).ToString("F6",CultureInfo.InvariantCulture)+
                     " fixed="+Time.fixedDeltaTime.ToString("F6",CultureInfo.InvariantCulture));
            raceSilkLast=pd.silk;
            if(!stockAttack&&raceBuild.Contains("anchor")) UnityEngine.Random.InitState(combatSeed);
            Note("RACE_START " + raceName + " frame=" + frame + " state=" + state + " hp=" + boss.hp);
            if(raceBuild.Contains("fsmfacts"))WriteBossRoutes();
            if(traceAttack)
            {
                attackTrace=new StreamWriter(Path.Combine(root,raceName+".opportunities.tsv"),false);
                attackTrace.WriteLine("frame\tstate\tage\tboss\tmasks\tsilk\tkeys\tprevious\tground\tcontrol\tready\tcooldown\tvulnerable\tdx\tdy\trvx\trvy\tneedle\toverlap\trisk\tthreat\tplan");
            }
        }
        if (raceVideo && raceRoar && !raceStartedCapture) StartRaceCapture();
        if (!raceOverlap && hero.IsUsingQuickening && hero.NailImbuement.CurrentElement.ToString()=="Fire")
        {
            raceOverlap=true;
            Note("RACE_OVERLAP " + raceName + " " + (raceStart<0 ? -1 : frame-raceStart) + " hp=" + boss.hp);
        }
        racePeakFocus=Math.Max(racePeakFocus,hero.HunterUpgState.CurrentMeterHits);
        if (raceStart >= 0)
        {
            int t = frame - raceStart;
            if(wanderRace&&!stockAttack&&raceBuild.Contains("rngtempo"))ObserveFrameRng();
            if(t==0)
            {
                Note("PRACTICE_AUDIO "+raceName+" volume="+AudioListener.volume+" background="+Application.runInBackground);
                if(wanderRace&&raceBuild.Contains("pinlace"))
                {
                    var meter=boss.GetComponents<PlayMakerFSM>().FirstOrDefault(f=>f.FsmName=="Stun Control");
                    if(meter)Note("WANDER_STUN_METER "+raceName+" "+
                         string.Join("|",meter.FsmVariables.FloatVariables.Select(v=>v.Name+"="+v.Value).ToArray()));
                    Note("WANDER_ATTACK_CADENCE "+raceName+" cooldown="+hero.Config.AttackCooldownTime+
                         " recover="+hero.Config.AttackRecoveryTime);
                }
                if(raceBuild.Contains("scout"))
                {
                    Note("BOSS_STATES "+string.Join("|",bossControl.FsmStates.Select(s=>s.Name).ToArray()));
                    foreach(var fs in bossControl.FsmStates.Where(v=>v.Name=="Summon Thwip"||v.Name=="Tendril Emerge"||
                        v.Name=="Tendril Emerge Wait"||v.Name=="CS Summon"||v.Name=="Wave L"||v.Name=="Wave R"))
                    {
                        Note("BOSS_ACTIONS "+fs.Name+" "+string.Join("|",fs.Actions.Select(a=>a.GetType().Name).ToArray()));
                    }
                }
            }
            if(wanderRace&&changed&&state=="Stun Start")nativeStuns++;
            if (changed)
                Note("RSTATE " + raceName + " " + t + " " + state + " bx=" + bossPos.x.ToString("F2",CultureInfo.InvariantCulture) +
                      " hx=" + heroPos.x.ToString("F2",CultureInfo.InvariantCulture));
            if(raceSilkLast>=0 && raceSilkLast!=pd.silk)
            {
                int used=raceSilkLast-pd.silk;
                bool cast=used==pd.SilkSkillCost && frame-raceCast>=0 && frame-raceCast<=32;
                if(used>0)raceSilkSpent+=used;
                if(cast)
                {
                    raceActualCasts++;
                    if(sharpIntercept&&frame-sharpLastPlan<=32)
                    {
                        sharpSpent++;
                        Note("SHARP_COMMIT "+raceName+" "+t+" through="+sharpPlanThrough+
                             " silk="+raceSilkLast+">"+pd.silk);
                    }
                    if(wanderRace)Note("WANDER_CAST_RESERVE "+raceName+" "+t+" silk="+raceSilkLast+">"+pd.silk);
                }
                Note("R_SILK "+raceName+" "+t+" "+raceSilkLast+">"+pd.silk+
                    " used="+Math.Max(0,used)+" nativeSkill="+cast+" state="+state);
                raceSilkLast=pd.silk;
            }
            bool tookDamage = pd.health < raceLastHero;
            if(wanderRace&&tookDamage)
            {
                wanderDamage+=raceLastHero-pd.health;
                Note("WANDER_HURT "+raceName+" "+t+" masks="+pd.health+" loss="+wanderDamage+" state="+state);
            }
            if (boss.hp != raceLastHp || pd.health != raceLastHero)
            {
                Note("RHP " + raceName + " " + t + " boss=" + boss.hp + " hornet=" + pd.health + " silk=" + pd.silk +
                    " focus=" + hero.HunterUpgState.CurrentMeterHits + " fire=" + hero.NailImbuement.CurrentElement +
                    " brew=" + hero.IsUsingQuickening + " in=" + keys + " state=" + state);
                raceLastHero = pd.health;
                raceLastHp = boss.hp;
                if (boss.hp < raceMinHp)
                {
                    foreach (int split in new[]{1350,900,540,0})
                        if (raceMinHp >= split && boss.hp < split)
                            Note("RSPLIT " + raceName + " " + t + " below=" + split + " hp=" + boss.hp);
                    raceMinHp = boss.hp;
                }
            }
            if(t==600)
                Note("OPEN10 "+raceName+" damage="+(1800-boss.hp)+" masks="+pd.health+
                     " focus="+hero.HunterUpgState.CurrentMeterHits+" casts="+raceActualCasts+
                   (wanderRace?" hits="+wanderHits+" eligible="+wanderEligible+" crits="+wanderCrits:""));
            if(raceBuild.Contains("trimfront")&&(t==600&&boss.hp>1280||t==865&&boss.hp>=900))
            {
                EndRace("LIMIT");
                return;
            }
            if(raceBuild.Contains("quickfront")&&(t==600&&boss.hp>1260||t==805&&boss.hp>=900))
            {
                EndRace("LIMIT");
                return;
            }
            if(raceBuild.Contains("speedgate")&&(t==600&&boss.hp>1410||t==1090&&boss.hp>=900))
            {
                EndRace("LIMIT");
                return;
            }
            if(flowMeasured&&raceBuild.Contains("flowselect")&&t==600&&(boss.hp>1415||pd.health<8))
            {
                EndRace("LIMIT");
                return;
            }
            if(isolateLateAir&&t==600&&(boss.hp!=1295||pd.health!=8))
            {
                EndRace("LIMIT");
                return;
            }
            if(sharpIntercept&&raceBuild.Contains("screenfast")&&t==600&&
               (boss.hp>1340||pd.health<8))
            {
                EndRace("LIMIT");
                return;
            }
            if(t>=600 && state=="Counter Hit" && frame-raceStateSince>=260 && boss.hp>1690)
            {
                EndRace("STALL");
                return;
            }
            bool stopOnHit=!(wanderRace&&raceBuild.Contains("finish"));
            if (boss.hp < 0 || tookDamage&&stopOnHit || pd.health <= 0 || t >= raceLimit || (boss.isDead && boss.hp == 0))
            {
                EndRace(tookDamage&&stopOnHit ? "HIT" : boss.hp < 0 ? (wanderDamage==0?"WIN":"CLEAR") :
                    boss.isDead && boss.hp == 0 ? "ZERO" : pd.health <= 0 ? "DEAD" : "LIMIT");
                return;
            }
        }

        bool merge = raceBuild.Contains("merge");
        int raceAge = frame-raceStart;
        int openFrames=480+(racePolicy%6)*24;
        if (merge && raceStart>=0 && !mergeOpened &&
            (raceAge>=openFrames || (boss.hp<=1430 && raceAge>=430) || hero.HunterUpgState.CurrentMeterHits>=12))
        {
            mergeOpened=true;
            Note("MERGE_SWITCH "+raceName+" "+raceAge+" hp="+boss.hp+" focus="+hero.HunterUpgState.CurrentMeterHits+" state="+state);
        }
        bool earlyRush=merge && !mergeOpened;
        bool wideSwing=raceBuild.Contains("max")||earlyRush;
        if(wanderRace&&raceStart>=0&&hasControl&&state=="P2 Shift 2"&&
           (raceBuild.Contains("shiftshort")||raceBuild.Contains("shiftcross"))&&
           TryCompactShift(hero,state,NeedleWindowAge(state,frame-raceStateSince),dx,dy,ground))
            return;
        if(wanderRace&&raceStart>=0&&branchAfterRoar&&state=="Stunned"&&
           (raceBuild.Contains("endthread")||raceBuild.Contains("endline"))&&
           TryStaggerFinish(hero,state,hasControl))
            return;
        if(wanderRace&&raceBuild.Contains("wavechallenge")&&raceStart>=0&&hasControl&&ground&&
           state=="Antic Wave"&&TryWaveChallenge(hero,frame-raceStateSince,dx))
            return;
        if(wanderRace&&raceBuild.Contains("aircoat")&&raceStart>=0&&hasControl&&ground&&
           frame-raceStart>=113&&frame-raceStart<=144&&frame-raceFire>125&&
           (state=="Dive In 1"||state=="Dive In 2")&&abs>=4.4f&&UseRaceTool(hero,"Flintstone"))
        {
            raceFire=frame;
            auditKeys=null;
            TrackFreeTool("Flintstone",state);
            Note("OPEN_RECOAT "+raceName+" "+(frame-raceStart)+" state="+state);
            return;
        }
        if(wanderRace&&raceBuild.Contains("flintwindow")&&raceStart>=0&&hasControl&&ground&&
           TryFireWindow(hero,state,frame-raceStateSince,dx))
            return;
        if(wanderRace&&raceBuild.Contains("whipflank")&&raceStart>=0&&hasControl&&raceFirstFloor&&
           (state=="Tendril A Antic"||state=="Tendril A Whip")&&
           TryAirWhipFlank(hero,state,frame-raceStateSince,ground))
            return;
        if(wanderRace&&(raceBuild.Contains("hookdrop")||raceBuild.Contains("hookchase"))&&
           raceStart>=0&&hasControl&&raceFirstFloor&&!ground&&
           TryHookDrop(hero,state))
            return;
        if(wanderRace&&branchAfterRoar&&raceBuild.Contains("finishpin")&&hasControl&&
           boss.hp>0&&boss.hp<=145&&(boss.hp<=44||boss.hp>=105)&&
           (state=="Stunned"||state=="Stun Air"||state=="Stun Start"||state=="Dive In 3"||
            state=="Bullet S Ground 1"||state=="Bullet S Ground 2")&&TryFinishPin(hero,state))
            return;
        if(wanderRace&&raceBuild.Contains("landcut")&&raceStart>=0&&hasControl&&!ground&&
           (state=="Idle"&&raceAge>=320&&raceAge<=393||state=="Bullet S Ground 1"||state=="Bullet S Ground 2")&&
           TryLandingCut(hero,state))
        {
            auditKeys=null;
            return;
        }
        if(wanderRace&&branchAfterRoar&&hasControl&&ground&&raceBuild.Contains("stunmeet")&&
           (state=="Stun Air"||state=="Stunned")&&TryStunMeet(hero,state))
            return;
        if(wanderRace&&raceBuild.Contains("summoncut")&&!branchAfterRoar&&hasControl&&ground&&
           state=="Summon Thwip"&&frame-raceStateSince<=4&&boss.hp>=930&&boss.hp<=1130&&
           PlayerData.instance.silk>=12&&hero.CanAttack()&&!previousRaceKeys.Contains("C")&&
           abs>=4.5f&&abs<=8.35f&&TrySharpdart(hero,state,frame-raceStateSince,dx,dy,false))
            return;
        if(wanderRace&&branchAfterRoar&&raceBuild.Contains("combocut")&&hasControl&&ground&&state=="ComboSlash 1")
        {
            int native=Mathf.FloorToInt(raceStateNativeAge+.05f);
            int inputAge=frame-raceStateSince;
            if(hero.CanAttack()&&!previousRaceKeys.Contains("C")&&native>=9&&native<=20&&abs>=3.15f&&abs<=7.8f&&Mathf.Abs(dy)<=1.50f&&
               TrySharpdart(hero,state,native,dx,dy,true))
            {
                pressureLiftUntil=-10000;
                Note("RETURN_COMBO_CAST "+raceName+" "+(frame-raceStart)+" native="+native+
                     " inputAge="+inputAge+" dx="+dx.ToString("F2",CultureInfo.InvariantCulture));
                return;
            }
        }
        if(wanderRace&&branchAfterRoar&&raceBuild.Contains("landpursue")&&hasControl&&ground&&
           (state=="Land"||state=="Idle")&&boss.hp>=425&&boss.hp<=815&&
           TryLandRecoilDash(hero,state,frame-raceStateSince))return;
        if(wanderRace&&boss.hp<900&&raceBuild.Contains("tailclock")&&raceStart>=0&&hasControl&&
           raceFirstFloor&&state=="ComboSlash 1"&&TryP3ComboPressure(hero,frame-raceStateSince))
            return;
        if(returnBias!=0f&&hasControl&&state=="P3 Dive Out"&&TryPulseReentry(hero,dx+returnBias,ground))
            return;
        if(wanderRace&&boss.hp<900&&raceBuild.Contains("latecling")&&
           raceStart>=0&&hasControl&&raceFirstFloor)
        {
            if(state=="Counter End"&&TryFlowNeedle(hero,state,frame-raceStateSince,null,true))
                return;
            if(ground&&TryKeepCounterPosition(hero,state,dx,ground))
                return;
            if(ground&&(state=="Bullet S Ground 1"||state=="Bullet S Ground 2")&&
               TryOpportunityPressure(hero,state,frame-raceStateSince))
                return;
        }
        if(wanderRace&&raceBuild.Contains("inwardp3")&&raceStart>=0&&hasControl&&raceFirstFloor&&
           boss.hp<900&&TryKeepInsideAfterCharge(hero,state,frame-raceStateSince))
            return;
        if(wanderRace&&raceBuild.Contains("fastdrop")&&raceStart>=0&&hasControl&&raceFirstFloor&&!ground&&
           TryFastDrop(hero,state,frame-raceStateSince))
            return;
        if(wanderRace&&raceBuild.Contains("reboundcut")&&raceStart>=0&&hasControl&&raceFirstFloor&&
           boss.hp<900&&(state=="Bounce Back ToDive"||state=="Bounce Back")&&
           TryPressureRebound(hero,state,frame-raceStateSince,ground))
            return;
        if(wanderRace&&raceBuild.Contains("hookchase")&&raceStart>=0&&hasControl&&raceFirstFloor&&
           TryClawChase(hero,state,frame-raceStateSince))
            return;
        if(wanderRace&&raceBuild.Contains("windowpick")&&raceStart>=0&&hasControl&&raceFirstFloor&&
           TryOpportunityPressure(hero,state,frame-raceStateSince))
            return;
        if(wanderRace&&raceBuild.Contains("stunpress")&&raceStart>=0&&hasControl&&raceFirstFloor&&
           (state=="Stun Air"||state=="Stunned")&&TryStunPress(hero,state,frame-raceStateSince,ground))
            return;
        if(wanderRace&&hasControl&&raceFirstFloor&&raceStart>=0)
        {
            if(raceBuild.Contains("needleall")&&TryFlowNeedle(hero,state,frame-raceStateSince,null,true))return;
            if(raceBuild.Contains("counterstay")&&TryKeepCounterPosition(hero,state,dx,ground))return;
            if(raceBuild.Contains("laneadvance")&&TryAdvanceToNeedle(hero,state,frame-raceStateSince,ground))return;
        }
        if(raceBuild.Contains("comboedge")&&raceStart>=0&&hasControl&&raceFirstFloor&&
           state=="ComboSlash 1"&&TryOpeningCombo(hero,frame-raceStateSince,ground))
            return;
        if(wanderRace&&raceBuild.Contains("tempoearly")&&raceStart>=0&&hasControl&&raceFirstFloor&&
           TryFlowNeedle(hero,state,frame-raceStateSince,null,true))
            return;
        if(flowAttack&&raceBuild.Contains("combopress")&&raceStart>=0&&hasControl&&
            TryComboRelease(hero,state,frame-raceStateSince))
            return;
        if(flowMeasured&&!raceBuild.Contains("combopress")&&raceBuild.Contains("latechain")&&raceStart>=0&&hasControl&&
           boss.hp<=900&&state=="ComboSlash 3"&&TryLateComboLane(hero,frame-raceStateSince,ground))
            return;
        if(flowMeasured&&FlowSelection(state)&&raceBuild.Contains("noemptycut")&&
           state=="Stun Recover Pause"&&hasControl)
        {
            keys=heroPos.x<9.0f?"R":heroPos.x>50.5f?"L":"";
            return;
        }
        if(flowMeasured&&FlowSelection(state)&&raceBuild.Contains("columnaware")&&raceStart>=0&&hasControl&&
           frame<summonUntil&&frame-summonStarted>=(raceBuild.Contains("columnbrake")?14:27)&&
           GetColumnExit(hero,state,frame-raceStateSince,ground,heroPos.x<30f?"R":"L",true))
            return;
        if(flowAttack&&raceBuild.Contains("columnfour")&&state=="P4 Shift 2"&&
            raceStart>=0&&hasControl&&ground&&TryFourthShiftPressure(hero,frame-raceStateSince))
            return;
        if(flowAttack&&(!flowAttach||state=="P2 Shift 2")&&raceStart>=0&&hasControl&&
           raceFirstFloor&&heroPos.y<26f&&TryFlowNeedle(hero,state,frame-raceStateSince,null))
            return;
        if(wanderRace&&sharpIntercept&&raceBuild.Contains("shiftcast")&&!shiftedSpell&&
           raceStart>=0&&hasControl&&ground&&state=="P2 Shift 2"&&boss.hp>=990&&boss.hp<=1130)
        {
            int native=NeedleWindowAge(state,frame-raceStateSince);
            int start=raceBuild.Contains("shiftcastlate")?49:43;
            if((!raceBuild.Contains("phasesync")||hero.CanAttack()&&!previousRaceKeys.Contains("C"))&&native>=start&&native<=(raceBuild.Contains("phasesync")?57:55)&&abs>=4.8f&&abs<=(raceBuild.Contains("phasesync")?8.15f:7.5f)&&Mathf.Abs(dy)<1.25f&&
               TrySharpdart(hero,state,native,dx,dy,true))
            {
                shiftedSpell=true;
                auditKeys=null;
                return;
            }
        }
        if(flowPhase&&raceBuild.Contains("shiftlock")&&raceStart>=0&&hasControl&&ground&&
           (state=="P2 Shift 1"||state=="P2 Shift 2")&&
           TrySecondShiftLock(hero,state,NeedleWindowAge(state,frame-raceStateSince),dx))
            return;
        if(wanderRace&&raceBuild.Contains("actualdive")&&raceStart>=0&&hasControl&&raceFirstFloor&&
           state=="Downstab"&&ground&&TryDownstabFlank(hero,frame-raceStateSince))
            return;
        if(wanderRace&&raceBuild.Contains("meetrise")&&raceStart>=0&&hasControl&&raceFirstFloor&&
           TryMeetRise(hero,state,frame-raceStateSince,ground))
            return;
        if(sharpIntercept&&raceStart>=0&&hasControl&&raceFirstFloor&&CanWanderSpendSilk()&&
           TrySharpThroughAttack(hero,state,frame-raceStateSince,dx,dy))
            return;
        if(repairOpenings&&raceBuild.Contains("catchclear")&&!raceBuild.Contains("groundmoulinet")&&
           raceStart>=0&&hasControl&&state=="Stomp Antic"&&TryAvoidMoulinetCatch(hero,frame-raceStateSince,dx))
            return;
        if(sharpIntercept&&raceBuild.Contains("crossmove")&&raceStart>=0&&hasControl&&
           state=="Do CrossSlash"&&TryMoveAcrossCircles(hero,frame-raceStateSince))
            return;
        if(stockAttack&&!flowStrict&&raceStart>=0&&hasControl&&raceFirstFloor&&heroPos.y<28f&&
           TryPressSwing(hero,state,frame-raceStateSince,ground))
            return;
        if(wanderRace&&!sharpIntercept&&raceBuild.Contains("sharpdeflect")&&raceStart>=0&&hasControl&&raceFirstFloor&&
           CanWanderSpendSilk()&&TrySharpThroughAttack(hero,state,frame-raceStateSince,dx,dy))
            return;
        if(flowAttack&&(raceBuild.Contains("flowhook")||raceBuild.Contains("hookdrop"))&&
           raceStart>=0&&hasControl&&raceFirstFloor&&TryPressureClaw(hero,state,frame-raceStateSince))
            return;
        if(raceBuild.Contains("hookweave")&&raceStart>=0&&hasControl&&raceFirstFloor&&
            TryWeaveClaw(hero,state))
            return;
        if(wanderRace&&raceBuild.Contains("sprintroute")&&raceStart>=0&&hasControl&&raceFirstFloor&&
           TrySprintNeedle(hero,state,ground))
            return;
        if(wanderRace&&!flowStrict&&!stockAttack&&raceBuild.Contains("attackzones")&&raceStart>=0&&hasControl&&raceFirstFloor&&
           heroPos.y<28f&&TryPressSwing(hero,state,frame-raceStateSince,ground))
            return;
        if(sharpIntercept&&raceBuild.Contains("orbitcut")&&raceStart>=0&&hasControl&&
           TryOrbitCircle(hero,state,frame-raceStateSince,dx,ground))
            return;
        if(wanderRace&&raceBuild.Contains("pinclash")&&raceStart>=0&&hasControl&&
           TryPinClash(hero,state,ground))
            return;
        if(wanderRace&&raceBuild.Contains("contactdash")&&raceStart>=0&&hasControl&&raceFirstFloor&&
            TryContactDash(hero,state,frame-raceStateSince,ground))
            return;
        if(repairOpenings&&(!isolateLateAir||lateAirReady)&&raceBuild.Contains("airlane")&&
           raceStart>=0&&hasControl&&raceFirstFloor&&
           TryCloseAirLane(hero,state,frame-raceStateSince,ground))
            return;
        if(flowAttack&&raceBuild.Contains("reacquire")&&raceStart>=0&&hasControl&&
           heroPos.y<24f&&TryRecoverNeedlePosition(hero,state,frame-raceStateSince,ground))
            return;
        bool beforeGuard=UseAirNeedleWindow(state,frame-raceStateSince)||
            flowAttack&&raceBuild.Contains("flowstep")&&CanFlowClose(state,frame-raceStateSince)&&
            (!flowMeasured||ProvenFlowWindow(state))&&FlowSelection(state);
        if(beforeGuard&&raceStart>=0&&hasControl&&raceFirstFloor&&heroPos.y<26f&&
           (frame>=raceHoldUntil||raceHeld=="L"||raceHeld=="R"||raceHeld=="")&&
           TryWanderApproach(hero,state,frame-raceStateSince,ground))
            return;
        if (frame < raceHoldUntil)
        {
            if(sustainAttack&&raceStart>=0&&hasControl&&raceFirstFloor&&heroPos.y<28f&&
               (raceHeld=="L"||raceHeld=="R"||raceHeld=="")&&TryWanderApproach(hero,state,frame-raceStateSince,ground))
            {
                if(keys!=raceHeld)
                    Note("WANDER_REAPPROACH "+raceName+" "+raceAge+" "+state+" input="+keys);
                return;
            }
            keys = raceHeld;
            return;
        }
        keys = "";
        if (!hasControl || !raceFirstFloor || heroPos.y > 28f) return;
        int age = frame - raceStateSince;
        int silk = pd.silk;
        float vy = hero.GetComponent<Rigidbody2D>().linearVelocity.y;
        string toward = dx < 0f ? "L" : "R";
        string away = dx < 0f ? "R" : "L";
        string center = heroPos.x < 37f ? "R" : "L";
        string face = hero.cState.facingRight == (dx >= 0) ? "" : toward;
        bool steady = raceBuild.Contains("steady") &&
            (!raceBuild.Contains("steadyafter") || raceStart>=0 && (boss.hp<=1440 || shiftedPhase));
        bool accurateEdge=raceBuild.Contains("arenaedges");
        bool roomEdge = (heroPos.x < (accurateEdge?7.7f:steady?10f:19f) && away=="L") ||
                        (heroPos.x > (accurateEdge?51.8f:steady?53f:56f) && away=="R");
        bool vanished = state.Contains("Dive In") || state.Contains("Splash") || state.Contains(" TeleOut") ||
            bossPos.y < 3f || !boss.gameObject.activeInHierarchy;
        bool intro = raceStart < 0;
        bool counter = state.Contains("Counter") || state.Contains("RapidSlash");
        bool wave = state.IndexOf("Wave", StringComparison.OrdinalIgnoreCase)>=0 || state.Contains("Void Tide") || state.Contains("Flood");
        bool pause = state.Contains("Laugh") || state.Contains("Roar") || state.Contains("Stun") || state.Contains("Title");
        bool recovering = state.Contains("Recover") || state.EndsWith(" End", StringComparison.Ordinal) || state=="Idle" ||
            state=="Land" || state=="Hop" || state.Contains("Downstab Land") || state.Contains("Evade");
        bool combo = state.Contains("ComboSlash") || state.Contains("Combo Strike") || state.Contains("Quick Slash");
        bool charge = state.Contains("Charge") || state.Contains("Crossup");
        bool tendrilG = state.Contains("Tendril G");
        bool tendrilA = state.Contains("Tendril A");
        bool slam = (state.Contains("Downstab") || state.Contains("Dstab")) && !state.Contains("Land");
        bool saw = state.Contains("J Slash");
        bool danger = (combo || charge || tendrilG || tendrilA || saw || slam || counter || wave) && !recovering;
        int style = merge ? 1 : racePolicy % 5;
        bool guard2 = raceBuild.Contains("guard2") || earlyRush;
        // The mid-fight pause is long enough to coat the needle before Lace returns.
        if(wanderRace&&raceBuild.Contains("toolfire")&&!raceBuild.Contains("flintlate")&&
           state=="Wave Pause"&&age<=5&&ground&&
           hero.NailImbuement.CurrentElement.ToString()=="None"&&frame-raceFire>100&&
           UseRaceTool(hero,"Flintstone"))
        {
            raceFire=frame;
            TrackFreeTool("Flintstone",state);
            return;
        }
        if(returnBias!=0f&&boss.hp<990&&
           (state=="Set Roar Pos"||state=="Wave Pause"||state=="Antic Wave"||state=="P3 Dive Out")&&
           TryPulseReentry(hero,dx+returnBias,ground))
            return;
        if(wanderRace&&boss.hp<900&&raceBuild.Contains("centerwave")&&state=="Set Roar Pos"&&
           age>=3&&TryPulseReentry(hero,dx,ground))
            return;
        if(wanderRace&&raceBuild.Contains("flinttangent")&&state=="Wave Pause"&&
           age>=(raceBuild.Contains("returndirect")?0:10)&&TryPulseReentry(hero,dx,ground))
            return;
        if(wanderRace&&raceBuild.Contains("reenter")&&state=="Antic Wave"&&age>=8)
        {
            if(raceBuild.Contains("reenterpulse")&&age>=(raceBuild.Contains("centerwave")?8:
               raceBuild.Contains("flinttangent")?8:26)&&TryPulseReentry(hero,dx,ground))
                return;
            if(heroPos.x<26f||heroPos.x>33f)
                keys=(heroPos.x<26f?"R":"L")+(ground?"S":"");
            return;
        }
        if(wanderRace&&raceBuild.Contains("wavewall")&&boss.hp<=900&&TryWaveWall(hero,state,age,ground))
            return;
        if(wanderRace&&raceBuild.Contains("middleafterwave")&&boss.hp<=900&&
           (state=="P3 Dive Out"||state=="P3 Roar"||state=="P3 Roar End"||state=="P3 Start"))
        {
            if(heroPos.x<26f||heroPos.x>35f)keys=(heroPos.x<26f?"R":"L")+(ground?"S":"");
            return;
        }
        bool canBuff = (intro && (state=="Start Pause" || state=="Start" || state=="Dormant")) || (vanished && abs>=4.4f) || (recovering && abs>=8f) || pause;
        if(merge && raceBuild.Contains("buff") && !intro && !danger && !shiftedPhase &&
            (state=="Counter Stance" || (state=="Dive Out A" && abs>=6f) || (state=="Stunned"&&abs>5f)))
            canBuff=true;
        if(steady && lateRisk && !intro && state=="Tendril Emerge Wait") canBuff=false;
        bool freeOnly=raceBuild.Contains("freebuff");
        if(freeOnly)
        {
            bool inactiveTarget=boss.IsInvincible || bossPos.y<2.75f || !boss.gameObject.activeInHierarchy;
            bool tighter=raceBuild.Contains("tightstart");
            bool advanceCoat=raceBuild.Contains("frontcoat")&&intro&&!flintObserved&&frame-raceFire>100;
            if(advanceCoat&&state=="Start"&&ground&&hasControl&&abs>=7.9f&&hero.CanDash()&&frame-raceDash>=30)
            {
                raceDash=frame;
                RacePress(toward+"S",5);
                return;
            }
            if(advanceCoat&&hero.cState.dashing){keys=toward;return;}
            bool approach=intro&&(state=="Dormant"||state=="Start"||state=="Start Pause")&&
                (advanceCoat&&state=="Start Pause"||abs>(tighter?3.50f:5.2f));
            if(tighter&&state=="Start"&&ground&&hasControl&&!flintObserved&&abs>=5.35f&&abs<7.25f)
            {
                keys=toward;
                return;
            }
            bool afterStun=state=="Stun Recover Pause" && age<=7 && inactiveTarget;
            bool hiddenTendrils=state=="Tendril Emerge Wait" && age<=7 && inactiveTarget && abs>4.0f;
            bool longWave=state=="Wave L" && firstWaveApproach==raceStateSince &&
                age>=1 && age<53 && heroPos.x>=18f && heroPos.x<=44f;
            canBuff=approach||afterStun||hiddenTendrils||longWave;
        }
        if (canBuff && ground)
        {
            if ((!raceBuild.Contains("firefirst") || frame-raceFire<95 || ToolInput("Flintstone")==null) && !hero.IsUsingQuickening && frame-raceBrew>100 && UseRaceTool(hero,"Flea Brew"))
            {
                raceBrew=frame;
                if(freeOnly) TrackFreeTool("Flea Brew",state);
                return;
            }
            if ((!raceBuild.Contains("flintwindow")||intro) &&
                hero.NailImbuement.CurrentElement.ToString()=="None" && frame-raceFire>100 && UseRaceTool(hero,"Flintstone"))
            {
                raceFire=frame;
                if(freeOnly) TrackFreeTool("Flintstone",state);
                return;
            }
            if(!intro && frame-raceAuxTool>370 && !counter && UseRaceTool(hero,"Cogwork Flier"))
            {
                raceAuxTool=frame;
                Note("COG_RELEASE "+raceName+" "+(frame-raceStart)+" "+state);
                return;
            }
        }
        if (intro)
        {
            if(sharpIntercept&&raceBuild.Contains("introdash")&&state=="Start Pause"&&
               abs>=7.85f&&abs<=10.0f&&ground&&hero.CanDash()&&
               frame-raceDash>=34&&frame-raceFire>=50)
            {
                raceDash=frame;
                RacePress(toward+"S",raceBuild.Contains("startstride")?16:6);
                Note("RACE_CLOSE_INTRO "+raceName+" gap="+abs.ToString("F2",CultureInfo.InvariantCulture));
                return;
            }
            float opening=raceBuild.Contains("tightstart")?2.45f:
                raceBuild.Contains("openfar")?6.0f:raceBuild.Contains("opennear")?(wanderRace?2.55f:2.9f):style==4?4.4f:3.6f;
            if (abs>opening) keys=toward;
            return;
        }

        if(wanderRace&&raceBuild.Contains("wspell")&&CanWanderSpendSilk())
        {
            if(raceBuild.Contains("wspellpale")&&TryRangedPale(hero,state,age,dx,dy,silk,face)||
               raceBuild.Contains("wspellsharp")&&TrySharpdart(hero,state,age,dx,dy)||
               raceBuild.Contains("wspellcross")&&TryCrossStitch(hero,state,age,dx,dy,0f,""))
                return;
        }

        if(wanderRace&&raceBuild.Contains("sticklace")&&state.StartsWith("Multihit",StringComparison.Ordinal)&&abs<8.8f)
        {
            if(ground&&hero.CanJump()&&frame-raceJump>11)
            {
                raceJump=frame;
                RacePress((roomEdge?center:away)+"J",12);
                return;
            }
            if(hero.CanDash()&&frame-raceDash>24&&!roomEdge)
            {
                raceDash=frame;
                RacePress(away+"S",7);
                return;
            }
            keys=(roomEdge?center:away)+(previousRaceKeys.Contains("J")?"J":"");
            return;
        }
        if(sustainAttack&&TryWanderApproach(hero,state,age,ground))
            return;
        if(wanderRace&&raceBuild.Contains("pinlace")&&TryPinLace(hero,state,age,ground))
            return;
        if(wanderRace&&!sustainAttack&&raceBuild.Contains("sticklace")&&TryWanderApproach(hero,state,age,ground))
            return;

        if(wanderRace&&raceBuild.Contains("whipflee")&&state=="Tendril G Antic"&&
           age>=8&&abs<15.5f&&!roomEdge&&heroPos.y<10.0f)
        {
            float room=away=="R"?54.69f-heroPos.x:heroPos.x-3.73f;
            if(room>=8.3f&&age<=30&&abs<12.8f&&hero.CanDash()&&frame-raceDash>25)
            {
                raceDash=frame;RacePress(away+"S",11);return;
            }
            if(abs>=12.8f){keys=abs<14.7f?away+"S":"";return;}
        }
        if(wanderRace&&raceBuild.Contains("whipflee")&&state=="Bounce Back ToDive"&&
           abs<5.3f&&dy>.25f&&dy<4.0f)
        {
            keys=abs<3.2f?(roomEdge?center:away):"";
            return;
        }
        if(wanderRace&&raceBuild.Contains("closecontact")&&TryCloseContact(hero,state,age,dx,dy,ground))
            return;
        if(wanderRace&&raceBuild.Contains("attackzones")&&CloseForWanderSwing(hero,state,age,dx,dy,ground))
            return;
        if(wanderRace&&raceBuild.Contains("presspogo")&&JumpForWanderPogo(hero,state,age,dx,dy,ground))
            return;
        if(wanderRace&&raceBuild.Contains("lateguard")&&boss.hp<=900&&state=="ComboSlash 1"&&
           age>=17&&abs<=6.4f&&ground&&hero.CanJump()&&frame-raceJump>17)
        {
            raceJump=frame;RacePress((roomEdge?center:away)+"J",11);return;
        }

        if(wanderRace&&raceBuild.Contains("needlepress")&&DriveWanderPunishes(hero,state,age,dx,dy,ground))
            return;

        if(!wanderRace&&raceBuild.Contains("paleaim")&&TryRangedPale(hero,state,age,dx,dy,silk,face))
            return;

        if(raceBuild.Contains("aircounter") && GuardAerialPass(hero,state,age,dx,dy,ground))
            return;

        if((raceBuild.Contains("comboexit") || raceBuild.Contains("comboover")) && GuardChainDistance(hero,state,age,dx,ground))
            return;

        if (state.StartsWith("P2 Shift", StringComparison.Ordinal) ||
            state.StartsWith("P3 Shift", StringComparison.Ordinal) ||
            state.StartsWith("P4 Shift", StringComparison.Ordinal))
        {
            bool blast = state.EndsWith("2",StringComparison.Ordinal) || state.EndsWith("3",StringComparison.Ordinal);
            if(flowPhase&&state=="P2 Shift 2"&&TryGroundShiftPressure(hero,age,dx,ground))
                return;
            int margin = raceBuild.Contains("late") ? 29 : 20;
            if(wanderRace&&raceBuild.Contains("pinwindow")&&state=="P2 Shift 2"&&
               age<=ShiftAttackDeadline(hero)&&!boss.IsInvincible&&abs<8.4f&&Mathf.Abs(dy)<2.1f)
            {
                if(age<=5&&ground&&abs>=6.2f&&abs<=8.4f&&hero.CanDash()&&frame-raceDash>26)
                {
                    Bounds actor,victim;
                    if(ReadPinnedTarget(hero,out actor,out victim))
                    {
                        ReadHitboxes(hero);
                        string hazard;
                        float stride=dx>0f?hero.DASH_SPEED:-hero.DASH_SPEED;
                        if(Forecast(actor,hero.GetComponent<Rigidbody2D>().linearVelocity,actor.center.y,
                            stride,0f,0,true,false,out hazard,9)<2.0f)
                        {
                            raceDash=frame;
                            RacePress(toward+"S",5);
                            return;
                        }
                    }
                }
                keys=abs>3.38f?toward:abs<1.86f?away:"";
                if(age==13||age==25||age==33)
                    Note("SHIFT_ATTACK_WINDOW "+raceName+" "+(frame-raceStart)+" age="+age+
                         " last="+ShiftAttackDeadline(hero)+" gap="+abs.ToString("F2",CultureInfo.InvariantCulture)+
                         " ready="+hero.CanAttack()+" input="+keys);
                return;
            }
            if(repairOpenings&&raceBuild.Contains("shiftvault")&&state=="P2 Shift 2"&&
               TryVaultShift(hero,age,dx,ground))
                return;
            if(repairOpenings&&!raceBuild.Contains("aironly")&&state=="P2 Shift 2"&&age>=12&&
               TryShiftBoundary(hero,age,dx,ground))
                return;
            if (raceBuild.Contains("p2") && blast && abs<13f)
            {
                float outwardClearance=away=="L"?bossPos.x-3.73f:54.69f-bossPos.x;
                if(wanderRace&&raceBuild.Contains("pinlace")&&outwardClearance>11.5f&&age>=12)
                {
                    if(abs<10.85f&&hero.CanDash()&&frame-raceDash>25)
                    {
                        raceDash=frame;
                        RacePress(away+"S",6);
                    }
                    else if(abs<11.2f)keys=away;
                    return;
                }
                string escape=bossPos.x<25f?"R":bossPos.x>48f?"L":away;
                if(age>=6 && abs<4.75f && ((escape=="R")!=(dx>0)) && hero.CanDash() && frame-raceDash>20)
                {
                    raceDash=frame;
                    RacePress(escape+"S",9);
                    return;
                }
                if (age>=13)
                {
                    if (hero.CanDash() && frame-raceDash>30 && ((escape=="R"&&heroPos.x<54f)||(escape=="L"&&heroPos.x>21f)))
                    {
                        raceDash=frame;
                        RacePress(escape+"S",9);
                    }
                    else keys=escape;
                    return;
                }
            }
            if (blast && age>=margin)
            {
                if (abs<11f && frame-raceDash>=26 && hero.CanDash() && !roomEdge)
                {
                    raceDash=frame;
                    RacePress(away+"S",10);
                    return;
                }
                if (ground && age>=42 && abs<12f && frame-raceJump>19)
                {
                    raceJump=frame;
                    RacePress((roomEdge?center:away)+"J",16);
                    return;
                }
                if (!ground && age>=55 && hero.CanDoubleJump(false) && frame-raceDouble>22)
                {
                    raceDouble=frame;
                    RacePress("J",8);
                    return;
                }
                if (abs<11.5f) keys=roomEdge?"":away;
                return;
            }
        }

        if(wanderRace&&raceBuild.Contains("moulinetlane")&&boss.hp<540&&TryHoldMoulinetLane(hero,state,age))
            return;
        if(raceBuild.Contains("finalguard") && GuardLatePhases(hero,state,age,ground))
            return;

        if(raceBuild.Contains("counterexit") && GuardRiposte(hero,state,age,dx,ground))
            return;

        if(raceBuild.Contains("chargeheight") && GuardChargeHeight(hero,state,age,dx,ground))
            return;

        if(raceBuild.Contains("positionguard") && TryPositionGuard(hero,state,age,dx,dy,ground))
            return;

        if(raceBuild.Contains("safecast") && (shiftedPhase || raceBuild.Contains("combosafe")) &&
            (state.StartsWith("ComboSlash ",StringComparison.Ordinal)||state.StartsWith("Quick Slash",StringComparison.Ordinal)) &&
            abs<9.4f && !hero.cState.invulnerable)
        {
            int launch=state=="ComboSlash 1"?21:state=="Quick Slash 1"?0:2;
            string escape=roomEdge?center:away;
            if(ground && age>=launch && hero.CanJump() && frame-raceJump>15)
            {
                raceJump=frame;
                RacePress(escape+"J",14);
                return;
            }
            if(!ground && abs<6.4f && vy<3.5f && hero.CanDoubleJump(false) &&
                frame-raceDouble>18 && frame-raceJump>12)
            {
                raceDouble=frame;
                RacePress(escape+"J",10);
                return;
            }
        }

        if(lateRisk && raceBuild.Contains("lanes") &&
            (state=="Tendril Emerge Wait" || state=="Summon Antic" || state=="Summon Thwip" ||
             state=="Summon Turn" || frame<summonUntil) && GetColumnExit(hero,state,age,ground,center))
            return;

        if(raceBuild.Contains("crossreact") && TryCrossStitch(hero,state,age,dx,dy,0f,""))
            return;

        if(raceBuild.Contains("ringburst") && !boss.IsInvincible && !counter &&
            (state=="Downstab Land"||state=="Stunned"||state=="Summon Turn"||
             state=="Evade Recover"||state=="Tendril G Whip End"||state=="Charge Recover") &&
            TryRingBurst(hero,age,dx,dy,ground,toward))
            return;

        if(raceBuild.Contains("sharp") && TrySharpdart(hero,state,age,dx,dy))
            return;
        if(steady && DriveSteady(hero,state,age,dx,dy,ground,counter))
            return;
        if(merge && !raceBuild.Contains("plain") &&
            GuardTelegraphedStrike(hero,state,age,dx,dy,ground,away,center,roomEdge))
            return;

        if(merge && (shiftedPhase || frame-lastChargeSeen<54 || raceBuild.Contains("allguard") ||
            (raceBuild.Contains("midguard") && mergeOpened)) &&
            GuardMovingHits(hero,state,age,dx,dy,ground,away,center))
            return;
        if (guard2 && ((tendrilG && state.Contains("Antic")) || (saw && state.Contains("Antic"))) && abs<13f)
        {
            if (saw && age<13 && abs<7.5f && frame-raceDash>24 && hero.CanDash() && !roomEdge)
            {
                raceDash=frame;
                RacePress(away+"S",7);
                return;
            }
            if (age<24)
            {
                if (abs<9.4f && !roomEdge) keys=away;
                return;
            }
        }

        if ((raceBuild.Contains("box") || (shiftedPhase && raceBuild.Contains("post"))) && !geometryFault)
        {
            try { if (DriveGeometry(hero,state,age,dx,dy,ground,danger,vanished,counter)) return; }
            catch(Exception e) { geometryFault=true; Note("BOX_ERROR "+raceName+" "+e); }
        }
        int comboDelay = state=="ComboSlash 1" ? 15 + ((merge?6:racePolicy)%7)*2 : 0;
        int chargeDelay = state=="Charge Break" ? 18+((merge?6:racePolicy)%4)*2 :
            guard2 && state=="Crossup Antic" ? 19 + (merge?6:racePolicy)%8 :
            state=="Charge Antic" ? 200 : 0;
        int avoidAge = combo ? comboDelay : charge ? chargeDelay : tendrilG && state.Contains("Antic") ? (guard2?25:19) :
            saw && state.Contains("Antic") ? 19 : 0;
        bool jumpThreat = wave || (danger && !tendrilA && !slam && abs < (tendrilG||charge?14f:9.25f) && age>=avoidAge);
        bool enoughSilk = !wanderRace && silk >= pd.SilkSkillCost;
        bool strictCast=raceBuild.Contains("safecast");
        bool recoveryCast=(state=="Idle"||state=="Charge Recover"||state=="Downstab Land"||
            state=="Tendril G Whip End"||state=="Bounce Back") && abs>5.4f && age<21;
        if(raceBuild.Contains("ample") && !counter && frame-raceCast>45)
        {
            bool distanceForCast=abs>3.95f && abs<10f && Mathf.Abs(dy)<2.3f;
            recoveryCast|=distanceForCast &&
                ((state=="Downstab Land"&&age<9) || (state=="Evade Recover"&&age<7&&abs>5.4f) ||
                 (state=="Summon Antic"&&age<9) || (state=="Charge Recover"&&age<7) ||
                 (state=="Tendril G Whip End"&&age<7));
        }
        if ((!steady || pause || recoveryCast) && !raceBuild.Contains("sharp") && !raceBuild.Contains("crossreact") && enoughSilk && !vanished && frame-raceCast>=25 && hero.CanCast() &&
            (abs<10.0f && Dist(dy)<2.3f) &&
            (pause || (strictCast?recoveryCast:
                (style!=0 && jumpThreat && abs<7.1f && age>=avoidAge) || (style==2 && abs>4f && !counter))))
        {
            raceCast=frame;
            RacePress(face+"C",3);
            return;
        }
        if (wave)
        {
            string wall=heroPos.x<37f?"L":"R";
            if (ground && frame-raceJump>12)
            {
                raceJump=frame;
                RacePress(wall+"J", 10);
            }
            else if (!ground && vy<8f && hero.CanDoubleJump(false) && frame-raceDouble>17)
            {
                raceDouble=frame;
                RacePress(wall+"J", 10);
            }
            else keys=wall;
            return;
        }
        if ((tendrilA || slam) && danger)
        {
            if (abs<(tendrilA?9.05f:5.8f) && hero.CanDash() && frame-raceDash>25 && age>5)
            {
                raceDash=frame;
                RacePress((tendrilA && dy>4.7f && age<24 ? toward : roomEdge ? center : away)+"S",6);
                return;
            }
            if (tendrilA && abs<5.9f && age<18 && dy>4.1f)
                keys=toward;
            else if (abs<7.8f) keys=roomEdge?"":away;
            else if (abs>8f && dy<9f) keys=style==1 ? toward : "";
            return;
        }

        bool safeAttack = (recovering || pause || !danger || hero.cState.invulnerable ||
            (age + (style<=1?9:5) < avoidAge)) && !vanished && !boss.IsInvincible;
        float needleReach=wanderRace?(wanderLongneedle?4.30f:3.83f):(wideSwing?4.15f:3.70f);
        if (safeAttack && !(wanderRace&&raceBuild.Contains("attackzones")&&pressureHullsReady) &&
            frame-raceAttack>(wanderRace?2:6) && hero.CanAttack())
        {
            if (Dist(dy)<1.65f && abs <= needleReach && abs>1.05f)
            {
                raceAttack=frame;
                RacePress(face+"A",3);
                return;
            }
            if (dy>1.4f && dy<4.6f && abs<2.25f)
            {
                raceAttack=frame;
                RacePress("UA",3);
                return;
            }
            if (!ground && dy < -1.1f && dy>-5.4f && abs<3.55f)
            {
                if (raceRing && frame-raceDouble>15 && hero.CanDoubleJump(false) && frame-raceJump>4)
                {
                    raceDouble=frame;
                    RacePress(toward+"DJ",1);
                    return;
                }
                raceAttack=frame;
                RacePress(toward+"DA",3);
                return;
            }
        }
        if (ground && jumpThreat && frame-raceJump>14)
        {
            raceJump=frame;
            raceRetreatUntil=frame+14;
            raceRetreatDirection=roomEdge ? toward : away;
            RacePress(raceRetreatDirection+"J", 12+((merge?6:racePolicy)%5));
            return;
        }
        if (!ground && jumpThreat && vy<8.0f && frame-raceJump>11 && frame-raceDouble>16 && hero.CanDoubleJump(false))
        {
            raceDouble=frame;
            RacePress((roomEdge?center:away)+"J",8);
            return;
        }
        if (frame<raceRetreatUntil) { keys=raceRetreatDirection; return; }
        if (vanished)
        {
            if (heroPos.x<22f || heroPos.x>52f) keys=center;
            return;
        }
        float goal = wanderRace?(wanderLongneedle?3.05f:2.78f):wideSwing ? 3.0f : (style == 4 ? 2.45f : 2.85f);
        if(accurateEdge && state=="Idle" && abs>5.5f && (heroPos.x>51.5f||heroPos.x<8.3f))
        {
            keys=center;
            return;
        }
        if (counter)
        {
            if (abs < 4.3f) keys=roomEdge ? center : away;
            return;
        }
        if (danger && (guard2 || abs<(style==0?7.5f:6.1f)))
        {
            if (abs<3.4f || ground) keys=roomEdge ? "" : away;
            return;
        }
        bool reserveClaw=raceBuild.Contains("reserveclaw");
        bool clawWindow=!reserveClaw || (silk>pd.SilkSkillCost && abs<11.5f && Mathf.Abs(dy)<1.5f &&
            (state=="Downstab Land"||state=="Stunned"||state=="Charge Recover"||state=="Tendril G Whip End") &&
            frame-lastVomit>160 && frame-lastBulletAt>155);
        if (!wanderRace && clawWindow && (!steady || (state=="Stunned"&&abs>9f)) &&
            !danger && abs>7f && abs<15f && Dist(dy)<3.0f && style>0 && hero.CanHarpoonDash() && frame-raceClaw>65)
        {
            raceClaw=frame;
            RacePress(toward+"H",3);
            return;
        }
        if (abs>goal && (Dist(dy)<7f || ground)) keys=toward;
        else if (abs<1.9f && Dist(dy)<1.5f) keys=roomEdge?center:away;
    }

    private void EndRace(string reason)
    {
        raceEndFrame=frame;
        if(attackTrace!=null)
        {
            attackTrace.Dispose();
            attackTrace=null;
        }
        if(flowAttack)
            Note("FLOW_TOTAL "+raceName+" scheduled="+flowSwings+" landed="+flowLanded+
                 " phase="+flowPhaseSwings+" phaseHits="+flowPhaseLanded+" jumpAttacks="+flowJumpAttack);
        if(sharpIntercept)
            Note("SHARP_TOTAL "+raceName+" attempted="+sharpTried+" spent="+sharpSpent+" landed="+sharpLanded+" through="+sharpThroughLanded);
        if(wanderRace)
            Note("WANDER_TOTAL "+raceName+" hits="+wanderHits+" eligible="+wanderEligible+" crits="+wanderCrits+
                 " capHits="+wanderCapHits+" capCrits="+wanderCapCrits+" rolls="+wanderRolls+" skipped="+wanderSkips+
                 " pressureSwings="+pressureSwings+" waits="+pressureWaits+
                 " stuns="+nativeStuns+" quickNeedles="+quickNeedles+" parries="+nativeParries);
        int elapsed = raceStart<0 ? -1 : Math.Max(0,frame-raceStart);
        Note("RACE_END " + raceName + " " + reason + " frames=" + elapsed + " hp=" + raceLastHp +
              " min=" + raceMinHp + " hero=" + raceLastHero + " focus=" + racePeakFocus +
              " casts=" + raceActualCasts + " silkSpent=" + raceSilkSpent);
        var path = Path.Combine(root,raceName+".race-inputs.txt");
        var encoded = new List<string>();
        int i=0;
        while (i<raceTape.Count)
        {
            int end=i+1;
            while (end<raceTape.Count && raceTape[end]==raceTape[i]) ++end;
            encoded.Add((end-i).ToString(CultureInfo.InvariantCulture)+" "+raceTape[i]);
            i=end;
        }
        File.WriteAllLines(path,encoded);
        if (!raceStartedCapture)
        {
            raceName=null;
            Freeze(0);
        }
    }

    private void StartRaceCapture()
    {
        raceStartedCapture=true;
        raceVideoDir=Path.Combine(root,"video-"+raceName);
        Directory.CreateDirectory(raceVideoDir);
        raceVideoLog=new StreamWriter(Path.Combine(raceVideoDir,"frames.csv"),false);
        raceVideoLog.WriteLine("image,frame,timed,boss,hero,silk,focus,fire,brew");
        StartCoroutine(RecordRace());
    }

    private System.Collections.IEnumerator RecordRace()
    {
        var endOfFrame=new WaitForEndOfFrame();
        while (raceName!=null && raceStartedCapture)
        {
            yield return endOfFrame;
            if (raceName==null || !raceStartedCapture) break;
            if (raceCaptureFrame!=frame && (frame%2==0 || raceEndFrame>=0))
            {
                raceCaptureFrame=frame;
                var image=RenderArena();
                if (image)
                {
                    int index=++raceVideoIndex;
                    File.WriteAllBytes(Path.Combine(raceVideoDir, index.ToString("D6",CultureInfo.InvariantCulture)+".jpg"),
                        ImageConversion.EncodeToJPG(image,86));
                    var pd=PlayerData.instance;
                    var hero=HeroController.instance;
                    raceVideoLog.WriteLine(index+","+frame+","+(raceStart<0 ? -1 : (raceEndFrame>=0 ? raceEndFrame : frame)-raceStart)+","+
                        (boss?boss.hp:raceLastHp)+","+pd.health+","+pd.silk+","+
                        (hero?hero.HunterUpgState.CurrentMeterHits:0)+","+
                        (hero && hero.NailImbuement.CurrentElement.ToString()!="None" ? 1 : 0)+","+
                        (hero && hero.IsUsingQuickening ? 1 : 0));
                }
            }
            if (raceEndFrame>=0)
            {
                raceVideoLog.Dispose();
                raceVideoLog=null;
                Note("RACE_VIDEO " + raceName + " " + raceVideoIndex + " " + raceVideoDir);
                raceStartedCapture=false;
                raceName=null;
                Freeze(0);
                break;
            }
        }
    }

    private Texture2D RenderArena()
    {
        if (!raceTarget)
        {
            raceTarget=new RenderTexture(960, 540, 24, RenderTextureFormat.ARGB32);
            raceTarget.Create();
            racePixels=new Texture2D(960,540,TextureFormat.RGB24,false);
            Note("RCAM " + string.Join("|", Camera.allCameras.OrderBy(c=>c.depth)
                .Select(c=>c.name + "@" + c.depth + "/" + c.clearFlags).ToArray()));
        }
        var previous=RenderTexture.active;
        try
        {
            RenderTexture.active=raceTarget;
            GL.Clear(true,true,Color.black);
            foreach (var cam in Camera.allCameras.OrderBy(c=>c.depth))
            {
                if (!cam.enabled || cam.targetTexture) continue;
                var target=cam.targetTexture;
                cam.targetTexture=raceTarget;
                try { cam.Render(); }
                finally { cam.targetTexture=target; }
            }
            RenderTexture.active=raceTarget;
            racePixels.ReadPixels(new Rect(0,0,960,540),0,0,false);
            return racePixels;
        }
        finally { RenderTexture.active=previous; }
    }
    private struct BoxThreat
    {
        public Bounds Shape;
        public Vector2 Velocity;
        public Vector2 Acceleration;
        public bool MovingProjectile;
        public string Label;
        public int WaitFrames;
        public int Duration;
    }

    private sealed class TrackedBox
    {
        public DamageHero Damage;
        public Collider2D Collider;
        public Vector3 PriorPosition;
        public int PriorFrame;
        public Vector2 Velocity;
        public bool SeenActive;
    }

    private static readonly System.Reflection.FieldInfo DamageCollider =
        AccessTools.Field(typeof(DamageHero), "collider");
    private static readonly System.Reflection.FieldInfo LaceCollider =
        AccessTools.Field(typeof(HealthManager), "boxCollider");
    private static SilkPractice geometryOwner;
    private readonly Dictionary<int, TrackedBox> track = new Dictionary<int, TrackedBox>();
    private readonly List<BoxThreat> threats = new List<BoxThreat>();
    private string shapedScene;
    private int nextShapeScan;
    private int lastGeometryLog;
    private int racePeakFocus;
    private int aimJump;
    private int aimDash;
    private int geomHoldJump;
    private int geomHoldDash;
    private string aimName = "";


    private int ringPogoFrame;
    private int ringJumpFrame;
    private bool shiftedPhase;
    private int combatSeed;
    private bool mergeOpened;
    private int summonUntil;
    private float summonAimX;
    private int lastChargeSeen;
    private string guardedMove;
    private string exitSide;
    private int exitAt;
    private bool lateRisk;
    private float currentFloor;
    private float columnLane;
    private int columnPlanFrame;
    private int columnLogFrame;
    private int columnThreatCount;
    private int columnBrakeLogged;
    private int summonStarted;
    private int emergingStarted;
    private int columnWaitLog;
    private int lastReflex;
    private int lastWaveDodge;
    private string positioningMove="", positioningDirection="";
    private int positioningSince=-9000, positioningDash=-9000;
    private int crossTelegraph=-9000, chargeHeightNote=-9000;
    private bool priorCrossChain;
    private int nextWaveScan;
    private int waveLogFrame;
    private readonly List<TrackedBox> liveWaves=new List<TrackedBox>();
    private int lastVomit, lastBulletAt;
    private int lastFlailScan;
    private readonly List<TrackedBox> flailTrack = new List<TrackedBox>();
    private string waveTravel, waveWallSide, moulinetSide;
    private int lastMoulinetFrame;
    private int lastGeometrySample;
    private static readonly System.Reflection.FieldInfo DamageReadyAt =
        AccessTools.Field(typeof(DamageHero),"damageAllowedTime");
    private Bounds[] columnHull = new Bounds[0];
    private int lastFlowDodge;
    private bool geometryFault;
    private bool geometryEntered;

    private void InstallGeometry()
    {
        geometryOwner=this;
        var harmony=new Harmony("local.silksong.tas.geometry");
        harmony.Patch(AccessTools.Method(typeof(HutongGames.PlayMaker.Actions.SendRandomEventV4),"OnEnter"),
            prefix:new HarmonyMethod(typeof(SilkPractice),"NoteBossChoiceBefore"),
            postfix:new HarmonyMethod(typeof(SilkPractice),"NoteBossChoiceAfter"),
            transpiler:new HarmonyMethod(typeof(SilkPractice),"InstrumentBranchDraw"));
        harmony.Patch(AccessTools.Method(typeof(HutongGames.PlayMaker.Actions.RandomFloat),"OnEnter"),
            prefix:new HarmonyMethod(typeof(SilkPractice),"SeekNativeTeleport"));
        harmony.Patch(AccessTools.Method(typeof(HeroBox), "TakeDamageFromDamager", new[]{typeof(DamageHero),typeof(GameObject)}),
            prefix: new HarmonyMethod(typeof(SilkPractice),"ObserveHit"));
        harmony.Patch(AccessTools.Method(typeof(HeroController),"TakeSilk",new[]{typeof(int),typeof(SilkSpool.SilkTakeSource)}),
            prefix:new HarmonyMethod(typeof(SilkPractice),"ObserveSpend"));
        harmony.Patch(AccessTools.Method(typeof(DamageEnemies),"DoDamage",new[]{typeof(GameObject),typeof(bool)}),
            transpiler:new HarmonyMethod(typeof(SilkPractice),"AtWandererRoll"));
        harmony.Patch(AccessTools.Method(typeof(HeroController),"NailParry"),
            postfix:new HarmonyMethod(typeof(SilkPractice),"CountNeedleParry"));
        harmony.Patch(AccessTools.Method(typeof(HeroController),"DoAttack",Type.EmptyTypes),
            prefix:new HarmonyMethod(typeof(SilkPractice),"ObserveAttackInput"));
        harmony.Patch(AccessTools.Method(typeof(HealthManager),"TakeDamage",new[]{typeof(HitInstance)}),
            prefix:new HarmonyMethod(typeof(SilkPractice),"NoteBossHealthBefore"),
            postfix:new HarmonyMethod(typeof(SilkPractice),"ObserveBossHit"));
    }

    private static readonly System.Reflection.FieldInfo[] UnityRandomWords=
        typeof(UnityEngine.Random.State).GetFields(System.Reflection.BindingFlags.Instance|
            System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.NonPublic);
    private static string RandomWords()
    {
        object state=UnityEngine.Random.state;
        return string.Join(",",UnityRandomWords.Select(f=>Convert.ToString(f.GetValue(state),CultureInfo.InvariantCulture)).ToArray());
    }

    private struct RecordedChoice
    {
        public int Frame;
        public string State;
        public UnityEngine.Random.State Random;
    }

    private struct ChoiceAudit
    {
        public string State;
        public bool Replayed;
        public UnityEngine.Random.State Ambient;
    }

    private RecordedChoice[] choiceTape;
    private int choiceTapeCursor;

    private RecordedChoice[] LoadChoiceTape()
    {
        return File.ReadAllLines(Path.Combine(root,raceBuild.Contains("floorcopy")?"floor-choices.tsv":"choice-reference.tsv")).Select(line=>
        {
            string[] fields=line.Split('\t');
            string[] words=fields[2].Split(',');
            if(words.Length!=UnityRandomWords.Length)throw new InvalidDataException("Unity RNG state width changed.");
            object box=new UnityEngine.Random.State();
            for(int i=0;i<words.Length;i++)
                UnityRandomWords[i].SetValue(box,int.Parse(words[i],CultureInfo.InvariantCulture));
            return new RecordedChoice {
                Frame=int.Parse(fields[0],CultureInfo.InvariantCulture),
                State=fields[1],
                Random=(UnityEngine.Random.State)box
            };
        }).ToArray();
    }

    private static void NoteBossChoiceBefore(HutongGames.PlayMaker.Actions.SendRandomEventV4 __instance,
                                              out ChoiceAudit __state)
    {
        __state=new ChoiceAudit();
        var m=geometryOwner;
        if(m==null||m.raceStart<0||m.raceEndFrame>=0||m.boss==null||m.bossControl==null||
           !m.raceBuild.Contains("choiceaudit")||__instance.Owner!=m.boss.gameObject||
           __instance.Fsm!=m.bossControl.Fsm)return;
        __state.State=m.bossControl.ActiveStateName;
        int t=m.frame-m.raceStart;
        if(m.choiceTape!=null&&m.choiceTapeCursor<m.choiceTape.Length)
        {
            bool flexible=m.raceBuild.Contains("choiceflex");
            int cursor=m.choiceTapeCursor;
            int slack=flexible?16:5;
            if(flexible)
            {
                int limit=Math.Min(m.choiceTape.Length,cursor+4);
                for(int i=cursor;i<limit;i++)
                {
                    if(m.choiceTape[i].State!=__state.State||Math.Abs(m.choiceTape[i].Frame-t)>slack)
                        continue;
                    cursor=i;
                    break;
                }
            }
            var expected=m.choiceTape[cursor];
            if(expected.State==__state.State&&Math.Abs(expected.Frame-t)<=slack)
            {
                __state.Replayed=true;
                __state.Ambient=UnityEngine.Random.state;
                UnityEngine.Random.state=expected.Random;
                m.choiceTapeCursor=cursor+1;
                Note("CHOICE_REPLAY "+m.raceName+" "+t+" "+__state.State+" source="+expected.Frame+
                     " draw="+m.choiceTapeCursor+"/"+m.choiceTape.Length);
            }
            else
            {
                Note("CHOICE_DRIFT "+m.raceName+" "+t+" "+__state.State+" expected="+expected.Frame+" "+expected.State);
                if(!flexible||t>m.choiceTape[m.choiceTape.Length-1].Frame+slack)m.choiceTape=null;
            }
        }
        var hero=HeroController.instance;
        Note("CHOICE_BEG "+m.raceName+" "+t+" "+__state.State+" rng="+RandomWords()+" step="+
             (Time.timeAsDouble-Time.fixedTimeAsDouble).ToString("F6",CultureInfo.InvariantCulture)+
             " hx="+(hero?hero.transform.position.x.ToString("F3",CultureInfo.InvariantCulture):"-"));
    }

    private static void NoteBossChoiceAfter(ChoiceAudit __state)
    {
        if(__state.State==null)return;
        var m=geometryOwner;
        var next=RandomWords();
        if(__state.Replayed)UnityEngine.Random.state=__state.Ambient;
        Note("CHOICE_END "+m.raceName+" "+(m.frame-m.raceStart)+" "+__state.State+
             " next="+m.bossControl.ActiveStateName+" rng="+next);
    }


    private bool branchAfterRoar, seenReturnBounce, firstReturnAttack;
    private float returnBias;
    private bool shiftedSpell;
    private PlayMakerFSM branchStun;
    private int branchOptionsAt;
    private string branchOptionsState;
    private string branchProfile;
    private static readonly System.Reflection.FieldInfo BranchRepeats=
        AccessTools.Field(typeof(HutongGames.PlayMaker.Actions.SendRandomEventV4),"trackingInts");
    private static readonly System.Reflection.FieldInfo BranchMisses=
        AccessTools.Field(typeof(HutongGames.PlayMaker.Actions.SendRandomEventV4),"trackingIntsMissed");

    private static IEnumerable<CodeInstruction> InstrumentBranchDraw(IEnumerable<CodeInstruction> incoming)
    {
        var weighted=AccessTools.Method(typeof(HutongGames.PlayMaker.ActionHelpers),"GetRandomWeightedIndex",
            new[]{typeof(HutongGames.PlayMaker.FsmFloat[])});
        var send=AccessTools.Method(typeof(HutongGames.PlayMaker.Fsm),"Event",new[]{typeof(HutongGames.PlayMaker.FsmEvent)});
        var draw=AccessTools.Method(typeof(SilkPractice),"DrawLaceBranch");
        var observed=AccessTools.Method(typeof(SilkPractice),"SendObservedBranch");
        foreach(var instruction in incoming)
        {
            bool isDraw=instruction.Calls(weighted), isEvent=instruction.Calls(send);
            if(!isDraw&&!isEvent){yield return instruction;continue;}
            var load=new CodeInstruction(OpCodes.Ldarg_0);
            load.labels.AddRange(instruction.labels);
            load.blocks.AddRange(instruction.blocks);
            yield return load;
            yield return new CodeInstruction(OpCodes.Call,isDraw?draw:observed);
        }
    }

    private bool BranchInScope(HutongGames.PlayMaker.Actions.SendRandomEventV4 action)
    {
        return raceName!=null&&raceStart>=0&&raceEndFrame<0&&branchProfile!=null&&
            boss&&bossControl&&action.Owner==boss.gameObject&&action.Fsm==bossControl.Fsm;
    }

    private void LogBranchStun(string state)
    {
        if(!branchStun)branchStun=boss.GetComponents<PlayMakerFSM>().FirstOrDefault(f=>f.FsmName=="Stun Control");
        if(!branchStun||raceStart<0)return;
        var values=branchStun.FsmVariables.FloatVariables;
        var relevant=values.Where(v=>v.Name=="Hits Total"||v.Name=="Combo Counter"||v.Name=="Stun Hit Max");
        Note("LACE_STUN "+raceName+" "+(frame-raceStart)+" state="+state+" hp="+boss.hp+" "+string.Join("|",
            relevant.Select(v=>v.Name+"="+v.Value.ToString("F2",CultureInfo.InvariantCulture)).ToArray()));
    }


    private static void SeekNativeTeleport(HutongGames.PlayMaker.Actions.RandomFloat __instance)
    {
        var m=geometryOwner;
        if(m==null||m.branchProfile==null||m.raceStart<0||m.raceEndFrame>=0||
           !m.boss||!m.bossControl||__instance.Owner!=m.boss.gameObject||
           __instance.Fsm!=m.bossControl.Fsm||m.bossControl.ActiveStateName!="Tele Pos"||
           __instance.storeResult.Name!="Tele Offset")
            return;
        var hero=HeroController.instance;
        if(!hero)return;

        int t=m.frame-m.raceStart;
        float min=__instance.min.Value, max=__instance.max.Value;
        bool anchor=m.raceBuild.Contains("anchorroute")&&t<=33&&min<1f&&max>=5.3f;
        bool close=m.raceBuild.Contains("teleclose")&&m.branchAfterRoar&&max<=14f&&
            (min>=4f||m.raceBuild.Contains("quickreturn")&&min<1f&&max>=5.4f);
        bool second=m.raceBuild.Contains("anchorboth")&&t>=114&&t<=154&&min>=5.9f&&min<=6.1f&&max>=7.9f;
        bool shift=m.raceBuild.Contains("anchorboth")&&t>=420&&t<=510&&min>=6.9f&&min<=7.1f&&max>=12.9f;
        bool floorAnchor=m.raceBuild.Contains("floorcopy")&&t>=243&&t<=272&&min<=5.37f&&max>=5.39f;
        if(!anchor&&!close&&!second&&!shift&&!floorAnchor)return;

        float h=hero.transform.position.x, b=m.boss.transform.position.x;
        bool carry=close&&m.raceBuild.Contains("edgecarry")&&min>=6.8f&&max>8.5f;
        float minOffset=floorAnchor?5.20f:anchor?5.10f:second?6.29f:shift?10.08f:Mathf.Max(min,5.10f);
        float maxOffset=floorAnchor?5.47f:anchor?5.31f:second?6.52f:shift?10.40f:Mathf.Max(minOffset+.35f,min+(max-min)*.22f);
        var ambient=UnityEngine.Random.state;
        int drawLimit=second||shift?64:48;
        for(int skipped=0;skipped<=drawLimit;skipped++)
        {
            var start=UnityEngine.Random.state;
            float d=UnityEngine.Random.Range(min,max);
            var after=UnityEngine.Random.state;
            bool left=UnityEngine.Random.value>=.5f;
            float predicted=h+(left?-d:d);
            if(d>=minOffset&&d<=maxOffset&&(!(anchor||second||shift||floorAnchor)||left)&&
               predicted>=6.15f&&predicted<=50.85f&&(carry?left==(b<h):Mathf.Abs(predicted-b)>=4.1f))
            {
                UnityEngine.Random.state=start;
                Note("LACE_TELE_SEARCH "+m.raceName+" "+t+" side="+(left?"L":"R")+
                     " dx="+d.ToString("F3",CultureInfo.InvariantCulture)+" target="+
                     predicted.ToString("F3",CultureInfo.InvariantCulture)+" skipped="+skipped);
                return;
            }
            UnityEngine.Random.state=after;
        }
        UnityEngine.Random.state=ambient;
    }

    private string BranchPreference(string state)
    {
        if(!branchAfterRoar)
        {
            if(raceBuild.Contains("floorstart")&&frame-raceStart>66&&boss.hp>1000)
            {
                if(state=="Attack Choice")return "BULLET SUMMON";
                if(state=="Set Bullets"||state=="Set J Slash"||state=="Combo Interrupt?")return "@default";
            }
            if(raceBuild.Contains("lowjump")&&frame-raceStart>=95&&frame-raceStart<=145&&
               state=="Set J Slash"&&boss.hp>1600)return "@default";
            if(raceBuild.Contains("anchorroute")&&frame-raceStart<=12)
            {
                if(state=="Evade Type")return "TELE";
                if(state=="Attack Choice")return "COMBO";
            }
            return null;
        }
        switch(state)
        {
            case "Evade Type":
                if(!firstReturnAttack&&raceBuild.Contains("quickreturn"))return "TELE";
                break;
            case "Attack Choice":
                if(branchProfile=="bullet"||branchProfile=="divebullet")return "BULLET SUMMON";
                if(branchProfile=="charge")return "CHARGE";
                if(branchProfile=="combo")return "COMBO";
                if(branchProfile=="combobullet")return boss.hp>=730?"COMBO":"BULLET SUMMON";
                if(branchProfile=="jslash")return "J SLASH";
                if(branchProfile=="spit")return "VOMIT";
                break;
            case "Set Bullets":
                if(branchProfile=="bullet"||branchProfile=="divebullet"||branchProfile=="combobullet")return "@default";
                break;
            case "Bounce Back":
                if(!seenReturnBounce&&raceBuild.Contains("groundreturn"))return "@default";
                if(branchProfile=="divebullet")return "TELE";
                break;
            case "Combo Interrupt?":
                if(branchProfile=="combo"||branchProfile=="combobullet")return "@default";
                break;
        }
        return null;
    }

    private static int DrawLaceBranch(HutongGames.PlayMaker.FsmFloat[] weights,
                                      HutongGames.PlayMaker.Actions.SendRandomEventV4 action)
    {
        int first=HutongGames.PlayMaker.ActionHelpers.GetRandomWeightedIndex(weights);
        var m=geometryOwner;
        if(m==null||!m.BranchInScope(action))return first;
        string state=m.bossControl.ActiveStateName, wanted=m.BranchPreference(state);
        if(m.branchAfterRoar&&(m.branchOptionsAt!=m.frame||m.branchOptionsState!=state))
        {
            m.branchOptionsAt=m.frame;
            m.branchOptionsState=state;
            var options=action.events.Select((evt,i)=>i>=weights.Length?null:
                  (evt!=null&&!string.IsNullOrEmpty(evt.Name)?evt.Name:"(none)")+":"+
                  weights[i].Value.ToString("F2",CultureInfo.InvariantCulture)).Where(s=>s!=null);
            Note("LACE_OPTIONS "+m.raceName+" "+(m.frame-m.raceStart)+" state="+state+" "+string.Join("|",options.ToArray()));
        }
        if(wanted==null)return first;
        int target=Array.FindIndex(action.events,e=>wanted=="@default"?
            e==null||string.IsNullOrEmpty(e.Name)||e.Name=="FINISHED":e!=null&&e.Name==wanted);
        if(target<0||target>=weights.Length||weights[target].Value<=0f)return first;
        var repeats=(int[])BranchRepeats.GetValue(action);
        var misses=(int[])BranchMisses.GetValue(action);
        if(repeats[target]>=action.eventMax[target].Value||
           misses.Where((value,i)=>value>=action.missedMax[i].Value).Any())
            return first;

        var afterFirst=UnityEngine.Random.state;
        int picked=first, extra=0;
        while(picked!=target&&extra<24)
        {
            picked=HutongGames.PlayMaker.ActionHelpers.GetRandomWeightedIndex(weights);
            ++extra;
        }
        if(picked!=target)
        {
            UnityEngine.Random.state=afterFirst;
            return first;
        }
        int t=m.frame-m.raceStart;
        string initial=first>=0&&first<action.events.Length&&action.events[first]!=null?action.events[first].Name:"-";
        Note("LACE_DRAW "+m.raceName+" "+t+" state="+state+" original="+initial+" choice="+wanted+" extra="+extra+
             " hp="+m.boss.hp);
        return picked;
    }

    private static void SendObservedBranch(HutongGames.PlayMaker.Fsm fsm,HutongGames.PlayMaker.FsmEvent evt,
                                         HutongGames.PlayMaker.Actions.SendRandomEventV4 action)
    {
        var m=geometryOwner;
        if(m!=null&&m.BranchInScope(action))
        {
            if(m.branchAfterRoar&&m.bossControl.ActiveStateName=="Attack Choice"&&evt!=null&&
               !string.IsNullOrEmpty(evt.Name))m.firstReturnAttack=true;
            var h=HeroController.instance;
            float dx=h?m.boss.transform.position.x-h.transform.position.x:0f;
            int t=m.frame-m.raceStart;
            Note("LACE_EVENT "+m.raceName+" "+t+" state="+m.bossControl.ActiveStateName+" event="+(evt!=null&&!string.IsNullOrEmpty(evt.Name)?evt.Name:"(none)")+
                 " phase="+(m.branchAfterRoar?"return":"front")+" hp="+m.boss.hp+
                 " dx="+dx.ToString("F2",CultureInfo.InvariantCulture));
        }
        fsm.Event(evt);
    }

    private static IEnumerable<CodeInstruction> AtWandererRoll(IEnumerable<CodeInstruction> incoming)
    {
        var instructions=incoming.ToList();
        var chance=AccessTools.PropertyGetter(typeof(GlobalSettings.Gameplay),"WandererCritChance");
        var range=AccessTools.Method(typeof(UnityEngine.Random),"Range",new[]{typeof(float),typeof(float)});
        var align=AccessTools.Method(typeof(SilkPractice),"AlignWandererRoll");
        int idx=instructions.FindIndex(code=>code.Calls(chance));
        if(idx<=0||!instructions[idx-1].Calls(range))
            throw new InvalidOperationException("Could not locate the stock Wanderer RNG call.");
        var load=new CodeInstruction(OpCodes.Ldarg_1);
        load.labels.AddRange(instructions[idx-1].labels);
        instructions[idx-1].labels.Clear();
        instructions.Insert(idx-1,load);
        instructions.Insert(idx,new CodeInstruction(OpCodes.Call,align));
        return instructions;
    }

    private static void AlignWandererRoll(GameObject target)
    {
        var self=geometryOwner;
        if(self==null||self.raceName==null||!self.wanderRace||self.raceStart<0||!self.boss||!target)return;
        var bt=self.boss.transform;
        if(target!=self.boss.gameObject&&!target.transform.IsChildOf(bt)&&!bt.IsChildOf(target.transform))return;
        if(self.boss.IsInvincible)return;
        ++self.wanderRolls;
        self.wanderLastRollFrame=self.frame;
        self.wanderLastRollSilk=PlayerData.instance.silk;
        float cutoff=GlobalSettings.Gameplay.WandererCritChance*HeroController.instance.GetLuckModifier();
        int skipped=0;
        float roll=-1;
        var entry=UnityEngine.Random.state;
        bool advance=self.alignCrit||self.quickRollLimit>0;
        int maxRolls=self.alignCrit?4096:self.quickRollLimit>0?self.quickRollLimit+1:1;
        for(int i=0;i<maxRolls;i++)
        {
            var before=UnityEngine.Random.state;
            roll=UnityEngine.Random.Range(0f,1f);
            if(!advance||roll<=cutoff)
            {
                UnityEngine.Random.state=before;
                skipped=i;
                break;
            }
            if(i==maxRolls-1)
            {
                UnityEngine.Random.state=entry;
                roll=UnityEngine.Random.Range(0f,1f);
                UnityEngine.Random.state=entry;
            }
        }
        self.wanderSkips+=skipped;
        Note("WANDER_ROLL "+self.raceName+" "+(self.frame-self.raceStart)+" skipped="+skipped+
             " roll="+roll.ToString("F7",CultureInfo.InvariantCulture)+" cutoff="+cutoff.ToString("F7",CultureInfo.InvariantCulture)+
             " silk="+PlayerData.instance.silk+" mode="+(self.alignCrit?"advance":self.quickRollLimit>0?"same-frame-bounded":"stock")+" target="+target.name);
    }

    private static void NoteBossHealthBefore(HealthManager __instance,out int __state)
    {
        __state=__instance.hp;
    }

    private static void ObserveBossHit(HealthManager __instance,HitInstance __0,int __state)
    {
        var self=geometryOwner;
        if(self==null||self.raceName==null||self.raceStart<0||self.boss!=__instance||
           __instance.hp>=__state)return;
        if(self.wanderRace && __0.AttackType==AttackTypes.Nail)
        {
            if(__0.Source&&(__0.Source.name=="Slash"||__0.Source.name=="AltSlash"||
               __0.Source.name=="UpSlash"||__0.Source.name=="DownSlash"))self.lastPlainImpact=self.frame;
            var pd=PlayerData.instance;
            int silk=pd.silk;
            bool rolled=self.frame-self.wanderLastRollFrame<=1;
            int before=rolled?self.wanderLastRollSilk:-1;
            self.wanderLastRollFrame=-10000;
            ++self.wanderHits;
            if(self.frame-self.lastWanderNeedle<=24)self.quickNeedles++;
            self.lastWanderNeedle=self.frame;
            if(self.flowAttack&&self.frame-self.flowSwingAt<=4)
            {
                ++self.flowLanded;
                if(self.flowSwingInPhase)++self.flowPhaseLanded;
                self.flowSwingAt=-10000;
                Note("FLOW_LANDED "+self.raceName+" "+(self.frame-self.raceStart)+
                     " source="+(__0.Source?__0.Source.name:"-")+" damage="+(__state-__instance.hp)+
                     " masks="+pd.health+" state="+self.raceState);
            }
            if(self.frame-self.airSwingAt<=4)
            {
                self.airSwingAt=-10000;
                Note("AIR_WINDOW_HIT "+self.raceName+" "+(self.frame-self.raceStart)+
                     " source="+(__0.Source?__0.Source.name:"-")+" damage="+(__state-__instance.hp)+
                     " masks="+pd.health+" state="+self.raceState);
            }
            if(rolled){++self.wanderEligible;if(__0.CriticalHit)++self.wanderCrits;}
            if(before>=pd.CurrentSilkMax){++self.wanderCapHits;if(__0.CriticalHit)++self.wanderCapCrits;}
            Note("WANDER_HIT "+self.raceName+" "+(self.frame-self.raceStart)+" silk="+silk+
                 " beforeRollSilk="+before+" damage="+(__state-__instance.hp)+" crit="+__0.CriticalHit+" fire="+__0.NailElement);
        }
        if(self.sharpIntercept&&__0.Source&&__0.Source.name=="Silk Charge Damager"&&
           __0.StunDamage>=.9f&&self.frame-self.sharpLastPlan<=38)
        {
            ++self.sharpLanded;
            if(self.sharpPlanThrough)++self.sharpThroughLanded;
            Note("SHARP_PRIMARY "+self.raceName+" "+(self.frame-self.raceStart)+" damage="+
                 (__state-__instance.hp)+" elapsed="+(self.frame-self.sharpLastPlan)+
                 " through="+self.sharpPlanThrough+" masks="+self.sharpPlanMasks+">"+PlayerData.instance.health);
            self.sharpLastPlan=-10000;
        }
        if(__0.Source&&__0.Source.name=="Harpoon Dash Damager")self.lastHookImpact=self.frame;
        Note("NATIVE_BOSS_HIT "+self.raceName+" "+(self.frame-self.raceStart)+" "+
             "delta="+(__state-__instance.hp)+" source="+(__0.Source?__0.Source.name:"-")+
             " attack="+__0.AttackType+" element="+__0.NailElement+" stunDamage="+__0.StunDamage);
    }

    private static void ObserveSpend(int __0,SilkSpool.SilkTakeSource __1)
    {
        if(geometryOwner==null||geometryOwner.raceName==null||geometryOwner.raceStart<0||__0<=0)return;
        string from="";
        if(__0==1&&__1==SilkSpool.SilkTakeSource.Normal)
        {
            var frames=new System.Diagnostics.StackTrace(1,false).GetFrames();
            if(frames!=null) from=" via="+string.Join("/",frames.Take(6).Select(x=>x.GetMethod().Name).ToArray());
        }
        Note("NATIVE_SPEND "+geometryOwner.raceName+" "+(geometryOwner.frame-geometryOwner.raceStart)+
             " amount="+__0+" reason="+__1+" wisp="+ToolItemManager.IsToolEquipped("Wisp Lantern")+from);
    }

    private static void ObserveHit(DamageHero __0, GameObject __1)
    {
        if (geometryOwner==null || geometryOwner.raceName==null || geometryOwner.raceStart<0) return;
        string source=__0?__0.name:__1?__1.name:"-";
        if(OwnHitbox(source)) return;
        Transform parent = __0 ? __0.transform.parent : null;
        string spatial="";
        if(__0&&(source=="Damager"||source=="Charge Hit"||source=="Downstab Hit"))
        {
            var collider=(Collider2D)DamageCollider.GetValue(__0);
            if(collider)
            {
                var b=collider.bounds;
                spatial=" min="+b.min+" max="+b.max;
            }
        }
        Note("BOX_CONTACT " + geometryOwner.raceName + " frame=" +
            (geometryOwner.frame-geometryOwner.raceStart) + " source=" + source+
            " parent="+(parent?parent.name:"-")+spatial);
    }

    private void InitGeometry()
    {
        geometryFault=geometryEntered=false;
        track.Clear();
        threats.Clear();
        shapedScene=null;
        nextShapeScan=0;
        lastGeometryLog=-200;
        racePeakFocus=0;
        aimJump=aimDash=ringPogoFrame=ringJumpFrame=-999;
        shiftedPhase=mergeOpened=false;
        summonUntil=lastFlowDodge=lastChargeSeen=columnBrakeLogged=-9999;
        summonAimX=0f;
        guardedMove=exitSide="";
        exitAt=-900;
        lateRisk=false;
        currentFloor=6.37f;
        columnLane=float.NaN;
        columnPlanFrame=columnLogFrame=-900;
        columnThreatCount=0;
        lastBulletAt=lastVomit=lastFlailScan=lastWaveDodge=lastReflex=lastGeometrySample=nextWaveScan=waveLogFrame=-9000;
        flailTrack.Clear();
        liveWaves.Clear();
        waveTravel=waveWallSide=moulinetSide=circleTravel="";
        lastMoulinetFrame=-10000;
        positioningMove=positioningDirection="";
        positioningSince=positioningDash=crossTelegraph=chargeHeightNote=-9000;
        priorCrossChain=false;
        summonStarted=emergingStarted=columnWaitLog=-9000;
        columnHull=new Bounds[0];
        geomHoldJump=geomHoldDash=-999;
        aimName="";

    }

    private static bool OwnHitbox(string source)
    {
        return source=="Slash" || source=="AltSlash" || source=="UpSlash" || source=="DownSlash" ||
           source.StartsWith("Hero ",StringComparison.Ordinal) || source.StartsWith("Harpoon ",StringComparison.Ordinal);
    }

    private void ReadHitboxes(HeroController hero)
    {
        if(frame==lastGeometrySample)return;
        lastGeometrySample=frame;
        string activeScene = SceneManager.GetActiveScene().name;
        if (activeScene!=shapedScene)
        {
            shapedScene=activeScene;
            track.Clear();
            nextShapeScan=0;
        }
        if (frame>=nextShapeScan)
        {
            nextShapeScan=frame+(raceBuild.Contains("fastscan")||raceBuild.Contains("merge")?1:4);
            foreach (var dead in track.Where(e=>!e.Value.Damage || !e.Value.Collider).Select(e=>e.Key).ToArray())
                track.Remove(dead);
            bool seeTelegraphs=raceBuild.Contains("merge") && (shiftedPhase||frame-lastChargeSeen<54);
            var visibility=seeTelegraphs&&frame%3==0?FindObjectsInactive.Include:FindObjectsInactive.Exclude;
            foreach (var obj in FindObjectsByType<DamageHero>(visibility, FindObjectsSortMode.None))
            {
                if (track.ContainsKey(obj.GetInstanceID()) ||
                    obj.transform.IsChildOf(hero.transform) ||
                    (obj.gameObject.scene.name!="Abyss_Cocoon" && obj.gameObject.scene.name!="DontDestroyOnLoad")) continue;
                var hit = (Collider2D)DamageCollider.GetValue(obj);
                if (!hit) hit = obj.GetComponent<Collider2D>();
                var holder=obj.transform.parent;
                if(!obj.isActiveAndEnabled && (!holder||!holder.gameObject.activeInHierarchy ||
                    !(holder.name.StartsWith("Lost_Lace_tendril_set",StringComparison.Ordinal)||
                      holder.name.StartsWith("Lost_Lace_Wake_Tendrils",StringComparison.Ordinal)))) continue;
                if (hit)
                    track.Add(obj.GetInstanceID(),new TrackedBox {Damage=obj, Collider=hit});
            }
        }
        threats.Clear();
        foreach (var obj in track.Values)
        {
            if (!obj.Damage || !obj.Collider) continue;
            var holder=obj.Damage.transform.parent;
            bool column=holder && holder.gameObject.activeInHierarchy &&
                (holder.name.StartsWith("Lost_Lace_tendril_set",StringComparison.Ordinal) ||
                 holder.name.StartsWith("Lost_Lace_Wake_Tendrils",StringComparison.Ordinal));
            if (!obj.Damage.isActiveAndEnabled || !obj.Collider.isActiveAndEnabled)
            {
                obj.SeenActive=false;
                if(!column||!raceBuild.Contains("merge")) continue;
                var telegraph=obj.Collider as BoxCollider2D;
                if(!telegraph)continue;
                var bro=obj.Collider.transform;
                var point=bro.TransformPoint(telegraph.offset);
                var physical=bro.TransformVector(telegraph.size);
                if(Mathf.Abs(point.x-hero.transform.position.x)>22f) continue;
                threats.Add(new BoxThreat {
                    Shape=new Bounds(point,new Vector3(Mathf.Abs(physical.x),Mathf.Abs(physical.y),1f)),
                    Label=holder.name, WaitFrames=5, Duration=13
                });
                continue;
            }
            var b=obj.Collider.bounds;
            if (b.size.x<.02f || b.size.y<.02f) continue;
            var body=obj.Collider.attachedRigidbody;
            Vector2 v=Vector2.zero;
            string source=obj.Damage.name;
            if (OwnHitbox(source) || source=="Enemy Damager" || source.StartsWith("Silk Charge",StringComparison.Ordinal) ||
                (raceBuild.Contains("reflex") && obj.Damage.damageDealt<=0 && !obj.Damage.AlwaysSendDamaged)) continue;
            if (obj.SeenActive && frame>obj.PriorFrame)
            {
                v=(Vector2)(b.center-obj.PriorPosition)*(60f/(frame-obj.PriorFrame));
                if (v.sqrMagnitude>10000f) v=Vector2.zero;
                obj.Velocity=Vector2.Lerp(obj.Velocity,v,.62f);
            }
            else obj.Velocity=body?body.linearVelocity:Vector2.zero;
            obj.SeenActive=true;
            obj.PriorPosition=b.center;
            obj.PriorFrame=frame;
            if (Mathf.Abs(b.center.x-hero.transform.position.x)>b.extents.x+24f ||
                Mathf.Abs(b.center.y-hero.transform.position.y)>b.extents.y+19f) continue;
            int wait=0;
            if(raceBuild.Contains("reflex")&&!obj.Damage.CanCauseDamage&&DamageReadyAt!=null)
                wait=Mathf.CeilToInt((float)((double)DamageReadyAt.GetValue(obj.Damage)-Time.timeAsDouble)*60f);
            if(wait>15)continue;
            string threatLabel=column?holder.name:obj.Damage.name;
            if(raceBuild.Contains("finalguard"))
            {
                for(var parent=holder;parent;parent=parent.parent)
                {
                    if(parent.name.IndexOf("wave",StringComparison.OrdinalIgnoreCase)<0 &&
                       parent.name.IndexOf("cross slash",StringComparison.OrdinalIgnoreCase)<0 &&
                       parent.name.IndexOf("circle slash",StringComparison.OrdinalIgnoreCase)<0)continue;
                    threatLabel=parent.name;break;
                }
            }
            bool projectile=raceBuild.Contains("blobflight")&&source.StartsWith("Abyss Bullet",StringComparison.Ordinal);
            Vector2 velocity=projectile&&body&&body.linearVelocity.sqrMagnitude>.1f?body.linearVelocity:obj.Velocity;
            Vector2 acceleration=projectile&&body&&body.bodyType==RigidbodyType2D.Dynamic?
                Physics2D.gravity*body.gravityScale:Vector2.zero;
            threats.Add(new BoxThreat {Shape=b, Velocity=velocity, Acceleration=acceleration, MovingProjectile=projectile,
                Label=threatLabel, WaitFrames=wait, Duration=18});
        }
    }

    private bool ReadDiveLanding(out float x,out int frames)
    {
        x=0f;frames=0;
        if(!boss)return false;
        var body=boss.GetComponent<Rigidbody2D>();
        var pos=boss.transform.position;
        if(!body||body.linearVelocity.y>= -4f||pos.y<6.1f)return false;
        Vector2 v=body.linearVelocity;
        float dt=Mathf.Clamp((pos.y-6.4f)/-v.y,0f,.32f);
        x=Mathf.Clamp(pos.x+v.x*dt,4.0f,54.2f);
        frames=Mathf.CeilToInt(dt*60f);
        return true;
    }

    private bool TryDownstabFlank(HeroController hero,int age)
    {
        if(age>19||hero.cState.dashing)return false;
        float x;
        int wait;
        if(!ReadDiveLanding(out x,out wait)||wait<2||wait>14)return false;
        var actor=hero.heroBox?hero.heroBox.GetComponent<Collider2D>():null;
        var body=hero.GetComponent<Rigidbody2D>();
        if(!actor||!body)return false;
        float here=actor.bounds.center.x;
        float side=here<x?-1f:1f;
        float gap=Mathf.Abs(here-x);
        if(gap<3.12f||gap>8.5f)return false;
        float distance=gap-3.85f;
        float travel=Mathf.Abs(distance)>.32f?(distance>0f?-side:side)*hero.GetWalkSpeed():0f;
        float expected=here+travel*(wait/60f);
        if(expected<4.35f||expected>54.15f||Mathf.Abs(expected-x)<3.40f)return false;
        ReadHitboxes(hero);
        AddFlailForecast(hero.transform.position);
        string hazard;
        if(Forecast(actor.bounds,body.linearVelocity,actor.bounds.center.y,travel,0f,0,false,false,
            out hazard,Mathf.Min(10,wait+3))>1.35f)return false;
        keys=travel<0?"L":travel>0?"R":"";
        if(age==2||age==6)
            Note("DIVE_FLANK "+raceName+" "+(frame-raceStart)+" input="+keys+" x="+
                 x.ToString("F2",CultureInfo.InvariantCulture)+" eta="+wait);
        return true;
    }

    private bool TryMeetRise(HeroController hero,string state,int age,bool grounded)
    {
        if(state!="Dive Out A"||age>=14||hero.cState.dashing||!grounded)return false;
        NeedleGeometry g;
        if(!ReadNeedleGeometry(hero,out g))return false;
        float dx=g.Target.center.x-g.Actor.center.x;
        float gap=Mathf.Abs(dx);
        float dy=g.Target.center.y-g.Actor.center.y;
        if(gap<3.55f||gap>10.3f||dy<0.19f||dy>6.85f)return false;
        var destination=bossControl.FsmVariables.GetFsmFloat("Tele Y");
        if(destination==null||destination.Value-hero.transform.position.y<3.55f)return false;
        ReadHitboxes(hero);
        AddFlailForecast(hero.transform.position);
        string hazard;
        float side=Mathf.Sign(dx);
        string toward=side<0?"L":"R";
        float travel=gap-hero.DASH_SPEED*.17f;
        if(gap>=5.95f&&travel>1.25f&&travel<4.55f&&age<10&&hero.CanDash()&&frame-raceDash>25&&
           !previousRaceKeys.Contains("S")&&
           Forecast(g.Actor,g.HeroVelocity,g.Floor,side*hero.DASH_SPEED,0,0,true,false,out hazard,12)<1.35f)
        {
            raceDash=frame;
            RacePress(toward+"S",5);
            Note("MEET_RISE "+raceName+" "+(frame-raceStart)+" "+keys+" age="+age+" gap="+
                 gap.ToString("F2",CultureInfo.InvariantCulture));
            return true;
        }
        return false;
    }

    private void PredictTelegraph(string state, int age, Bounds enemy, Bounds player, float dx)
    {
        int onset=-1, duration=13;
        float halfWidth=0f, halfHeight=0f, ahead=0f, yLift=0f;
        if (state=="Tendril G Antic") {onset=39; halfWidth=7.0f; halfHeight=1.55f; ahead=6.1f;}
        else if (state=="Tendril G Dash") {onset=4; halfWidth=7.0f; halfHeight=1.55f; ahead=6.1f;}
        else if (state=="Tendril G Whip") {onset=0; halfWidth=7.0f; halfHeight=1.55f; ahead=6.1f; duration=26;}
        else if (state=="J Slash M Antic") {onset=36; halfWidth=3.15f; halfHeight=3.35f; ahead=1.25f; yLift=1.9f; duration=17;}
        else if (state=="ComboSlash 1") {onset=35; halfWidth=3.35f; halfHeight=1.15f; ahead=1.9f; duration=15;}
        else if (state=="Combo Strike") {onset=0; halfWidth=3.35f; halfHeight=1.15f; ahead=1.9f; duration=16;}
        else if (state=="Downstab Antic") {onset=27; halfWidth=2.7f; halfHeight=1.25f; ahead=Mathf.Abs(dx); yLift=5.99f-enemy.center.y; duration=19;}
        else if (state=="Downstab") {onset=9; halfWidth=3.1f; halfHeight=1.2f; ahead=Mathf.Abs(dx); yLift=5.99f-enemy.center.y; duration=13;}
        else if (state=="Crossup Antic") {onset=34; halfWidth=5.6f; halfHeight=1.75f; ahead=4.3f; duration=16;}
        else if (state=="Charge Break") {onset=27; halfWidth=5.6f; halfHeight=1.75f; ahead=4.3f; duration=16;}
        else if (state=="P2 Shift 2") {age=NeedleWindowAge(state,age); onset=64; halfWidth=8.4f; halfHeight=4.35f; ahead=0f; duration=38;}
        float centerX=enemy.center.x-Mathf.Sign(dx)*ahead;
        if(raceBuild.Contains("actualwhip")&&boss&&
           (state=="Tendril G Antic"||state=="Tendril G Dash"||state=="Tendril G Whip"||
            state=="ComboSlash 1"||state=="Combo Strike"))
            centerX=enemy.center.x+Mathf.Sign(boss.transform.localScale.x)*ahead;
        float landing;
        int untilLand;
        if(state=="Downstab"&&raceBuild.Contains("actualdive")&&ReadDiveLanding(out landing,out untilLand))
        {
            centerX=landing;
            onset=age+untilLand;
            halfWidth=2.76f;
            duration=15;
        }
        if (onset<0 || onset-age>19 || age-onset>=duration) return;
        if (enemy.size.x<.01f) return;
        var b=new Bounds(new Vector3(centerX, enemy.center.y+yLift, 0),
                         new Vector3(2f*halfWidth,2f*halfHeight,1));
        threats.Add(new BoxThreat {
            Shape=b,Velocity=Vector2.zero, Label="telegraph:"+state,
            WaitFrames=Math.Max(0,onset-age), Duration=duration-Math.Max(0,age-onset)
        });
    }

    private float Forecast(Bounds hb, Vector2 heroVelocity, float floorY, float sx, float sy, int jumpHold,
                           bool dash, bool aerial, out string hitName, int lookahead=19)
    {
        const float dt=1f/60f;
        Vector3 center=hb.center;
        float vx=heroVelocity.x, vy=heroVelocity.y;
        if (sy>0f) vy=sy;
        float cost=0f;
        hitName="";
        for(int t=1;t<=lookahead;t++)
        {
            vx=Mathf.Lerp(vx,sx,dash?.88f:.42f);
            bool airborne = aerial || sy>0f || center.y>floorY+.10f;
            if (airborne && !dash)
            {
                vy-= (t<=jumpHold&&vy>0?34f:57f)*dt;
            }
            else if (!airborne || dash) vy=0f;
            center.x+=vx*dt;
            center.y+=vy*dt;
            if(center.y<=floorY && vy<=0f){center.y=floorY;vy=0f;}
            float minX=raceBuild.Contains("arenaedges")?3.85f:15.5f;
            float maxX=raceBuild.Contains("arenaedges")?54.5f:59.1f;
            if(center.x<minX||center.x>maxX) cost+=.17f;
            foreach (var obj in threats)
            {
                if(t<obj.WaitFrames || t>=obj.WaitFrames+obj.Duration) continue;
                float horizon=dt*Mathf.Min(t,obj.MovingProjectile?18:8);
                var hc=obj.Shape.center+(Vector3)(obj.Velocity*horizon+obj.Acceleration*(.5f*horizon*horizon));
                var he = obj.Shape.extents;
                if(obj.MovingProjectile){he.x+=.18f;he.y+=.16f;}
                float ax=Mathf.Abs(center.x-hc.x)-hb.extents.x-he.x-.15f;
                float ay=Mathf.Abs(center.y-hc.y)-hb.extents.y-he.y-.13f;
                float separation=Mathf.Max(ax,ay);
                if(separation<0f)
                {
                    cost+=obj.WaitFrames==0?2.25f:1.15f;
                    cost+=Mathf.Min(1f,-separation*.65f);
                    if(hitName.Length==0)hitName=obj.Label;
                }
                else if(separation<.38f) cost+=(.38f-separation)*.21f;
            }
        }
        return cost;
    }

    private void TrackWaveCollider(DamageHero damage)
    {
        if(!damage||liveWaves.Any(w=>w.Damage==damage))return;
        var collider=(Collider2D)DamageCollider.GetValue(damage);
        if(!collider)collider=damage.GetComponent<Collider2D>();
        if(collider)liveWaves.Add(new TrackedBox{Damage=damage,Collider=collider,PriorFrame=-900});
    }

    private bool LocateAbyssWave(Vector3 player,string state,out Bounds shape,out float motion)
    {
        shape=new Bounds(); motion=0f;
        if(frame>=nextWaveScan)
        {
            nextWaveScan=frame+5;
            liveWaves.RemoveAll(w=>!w.Damage||!w.Collider||!w.Damage.transform.parent||
                !w.Damage.transform.parent.gameObject.activeInHierarchy);
            var native=bossControl.FsmStates.FirstOrDefault(v=>v.Name==state);
            if(native!=null && (state=="Wave L" || state=="Wave R"))
            {
                foreach(var launch in native.Actions.OfType<HutongGames.PlayMaker.Actions.ActivateGameObject>())
                {
                    var slot=launch.gameObject;
                    var target=slot!=null&&slot.GameObject!=null?slot.GameObject.Value:null;
                    if(!target||!target.activeInHierarchy)continue;
                    foreach(var damage in target.GetComponentsInChildren<DamageHero>(true))
                        TrackWaveCollider(damage);
                }
            }
            foreach(var damage in FindObjectsByType<DamageHero>(FindObjectsInactive.Include,FindObjectsSortMode.None))
            {
                var holder=damage.transform.parent;
                if(!holder||!holder.gameObject.activeInHierarchy||
                    holder.name.IndexOf("abyss_wave",StringComparison.OrdinalIgnoreCase)<0)continue;
                TrackWaveCollider(damage);
            }
        }
        float priority=float.PositiveInfinity;
        foreach(var wave in liveWaves)
        {
            if(!wave.Damage || !wave.Collider)continue;
            var b=WorldColliderShape(wave.Collider);
            if(b.size.x<.50f)continue;
            float sample=wave.PriorFrame==frame-1?(b.center.x-wave.PriorPosition.x)*60f:wave.Velocity.x;
            if(Mathf.Abs(sample)<.4f||Mathf.Abs(sample)>150f)
                sample=wave.Velocity.x;
            if(Mathf.Abs(sample)<.4f)sample=state=="Wave R"?-26f:26f;
            wave.PriorFrame=frame;
            wave.PriorPosition=b.center;
            wave.Velocity=new Vector2(sample,0f);
            float side=sample>0f?1f:-1f;
            if((player.x-b.center.x)*side<-b.extents.x-1.6f)continue;
            float range= (player.x-b.center.x)*side-b.extents.x;
            float score=Mathf.Max(0f,range)/Mathf.Max(9f,Mathf.Abs(sample));
            if(score>=priority)continue;
            priority=score;shape=b;motion=sample;
        }
        return priority<float.PositiveInfinity;
    }

    private bool TryWaveWall(HeroController hero,string state,int age,bool ground)
    {
        bool approach=state=="Lock End"||state=="Silk Scream"||state=="Silk Fall"||
                      state=="Abyss Wave Start"||state=="Abyss Wave Start Init"||state=="Antic Wave";
        bool wave=state=="Wave L"||state=="Wave R"||state=="Abyss Return"||
                  state=="Abyss Wave Wait"||state=="Wave Pause"||state=="Wave Check";
        if(!approach&&!wave){waveWallSide="";return false;}
        var pos=hero.transform.position;
        var rb=hero.GetComponent<Rigidbody2D>();
        if(waveWallSide.Length==0)
        {
            waveWallSide=pos.x<16f?"L":"R";
            Note("WALL_WAVE "+raceName+" "+(frame-raceStart)+" "+state+" wall="+waveWallSide+" start="+pos);
        }
        string side=waveWallSide;
        float gap=side=="R"?54.69f-pos.x:pos.x-3.73f;
        if(gap>3.3f&&!hero.cState.wallSliding)
        {
            if(wave&&age>=121&&gap>10f)return false;
            keys=side+"S";
            return true;
        }
        keys=side;
        if((ground&&hero.CanJump()||hero.cState.wallSliding&&pos.y<17.6f) && frame-raceJump>17)
        {
            raceJump=frame;RacePress(side+"J",9);
            Note("WALL_ASCEND "+raceName+" "+(frame-raceStart)+" "+state+" at="+pos+
                 " wall="+hero.cState.wallSliding+" vy="+rb.linearVelocity.y);
            return true;
        }
        if(!ground&&!hero.cState.wallSliding&&rb.linearVelocity.y<2.7f&&pos.y<13.9f&&
           hero.CanDoubleJump(false)&&frame-raceDouble>18&&frame-raceJump>11)
        {
            raceDouble=frame;RacePress(side+"J",9);return true;
        }
        if(!ground&&pos.y>15.1f&&gap<5.5f&&rb.linearVelocity.y<1.0f)
            keys=side+"J";
        return true;
    }

    private string circleTravel="";
    private bool TryOrbitCircle(HeroController hero,string state,int age,float dx,bool ground)
    {
        bool windup=state=="J Slash M Antic";
        if(!windup&&state!="J Slash Multi"&&state!="J Slash Multihit")return false;
        float gap=Mathf.Abs(dx);
        float y=hero.transform.position.y;
        if(gap>9.25f)return false;
        string toward=dx<0f?"L":"R";
        if(ground&&windup&&age>=12&&age<=25&&hero.CanJump()&&frame-raceJump>13)
        {
            raceJump=frame;
            RacePress(gap>=4.25f?toward+"J":"J",10);
            Note("ORBIT_CIRCLE "+raceName+" "+(frame-raceStart)+" "+state+" age="+age+" "+keys);
            return true;
        }
        if(!ground&&y<12.65f&&frame-raceJump>=12&&frame-raceDouble>=19&&
           hero.CanDoubleJump(false)&&(!windup||age>=25))
        {
            raceDouble=frame;
            RacePress(gap>=4.65f?toward+"J":"J",10);
            Note("ORBIT_CIRCLE "+raceName+" "+(frame-raceStart)+" "+state+" age="+age+" "+keys);
            return true;
        }
        return false;
    }

    private bool TryAvoidMoulinetCatch(HeroController hero,int age,float dx)
    {
        if(age>12||Mathf.Abs(dx)>7.25f)return false;
        float x=hero.transform.position.x;
        string side=dx>=0f?"L":"R";
        float room=side=="L"?x-3.73f:54.69f-x;
        if(room<5.25f)
        {
            if(!flowMeasured)return false;
            // Carry outward motion at the edge through the catch. The ordinary
            // wall route can turn back into Lace as the grab collider starts.
            keys=side;
            if(Mathf.Abs(dx)<5.25f&&hero.CanJump()&&age<6&&frame-raceJump>15)
            {
                raceJump=frame;
                RacePress(side+"J",8);
            }
            return true;
        }
        if(Mathf.Abs(dx)<5.85f&&hero.CanDash()&&frame-raceDash>23&&age<7)
        {
            raceDash=frame;
            RacePress(side+"S",5);
            Note("MOULINET_CATCH_EXIT "+raceName+" "+(frame-raceStart)+" age="+age+
                 " gap="+Mathf.Abs(dx).ToString("F2",CultureInfo.InvariantCulture)+" input="+keys);
        }
        else keys=Mathf.Abs(dx)<6.85f?side:"";
        return true;
    }

    private bool TryMoveAcrossCircles(HeroController hero,int age)
    {
        float x=hero.transform.position.x;
        if(age==0||circleTravel.Length==0)circleTravel=x<30f?"R":"L";
        if(x<8.5f)circleTravel="R";else if(x>49.5f)circleTravel="L";
        float room=circleTravel=="R"?54.69f-x:x-3.73f;
        if((age<=9||age>=30)&&room>8.3f&&hero.CanDash()&&frame-raceDash>26)
        {
            raceDash=frame;
            RacePress(circleTravel+"S",6);
            Note("SHARP_ROUTE_CIRCLE "+raceName+" "+(frame-raceStart)+" age="+age+" input="+keys);
        }
        else keys=circleTravel;
        return true;
    }

    private bool TryHoldMoulinetLane(HeroController hero,string state,int age)
    {
        bool sequence=state.StartsWith("CS ",StringComparison.Ordinal)||state=="Do CrossSlash"||
           state=="Thread Roar"||state=="Thread Roar Antic"||state=="Stomp Antic"||
           state=="Stomp"||state=="Slam";
        if(!sequence)return false;
        float x=hero.transform.position.x;
        if(frame-lastMoulinetFrame>15||moulinetSide.Length==0)
        {
            moulinetSide=boss.transform.position.x>=x?"L":"R";
            Note("MOULINET_LANE "+raceName+" "+(frame-raceStart)+" state="+state+
                 " side="+moulinetSide+" x="+x+" boss="+boss.transform.position.x);
        }
        lastMoulinetFrame=frame;
        if(state=="CS Flip Back"&&age>=9)return false;
        if(raceBuild.Contains("moulinettravel")&&
           (state=="CS Thwip Out"||state=="Stomp Antic"||state=="Stomp"||state=="Slam"))
            return TryMoulinetTraverse(hero,state,age,x);
        float toWall=moulinetSide=="R"?54.69f-x:x-3.73f;
        if(raceBuild.Contains("groundmoulinet"))
        {
            bool edge=raceBuild.Contains("moulinetedge");
            float limit=moulinetSide=="R"?(edge?52.5f:43.0f):(edge?6.1f:15.8f);
            float room=moulinetSide=="R"?limit-x:x-limit;
            if(room>1.0f)
            {
                if(room>4.75f&&hero.CanDash()&&frame-raceDash>25&&!previousRaceKeys.Contains("S"))
                {
                    raceDash=frame;
                    RacePress(moulinetSide+"S",5);
                    return true;
                }
                keys=moulinetSide;
            }
            else keys="";
            if(state=="Stomp Antic"&&age==10)
                Note("MOULINET_GROUND "+raceName+" "+(frame-raceStart)+" x="+
                     x.ToString("F2",CultureInfo.InvariantCulture)+" input="+keys);
            return true;
        }
        if(raceBuild.Contains("p4inside"))
        {
            float limit=moulinetSide=="R"?43.0f:15.8f;
            float room=moulinetSide=="R"?limit-x:x-limit;
            if(room>1.10f)
                keys=moulinetSide+(room>5.3f?"S":"");
            else if(room< -1.2f)
                keys=moulinetSide=="R"?"L":"R";
            else
                keys="";
            if(state=="Stomp Antic"&&age>=8&&age<=16&&hero.cState.onGround&&
               hero.CanJump()&&frame-raceJump>15)
            {
                raceJump=frame;
                RacePress(keys.Replace("S","")+"J",11);
                Note("MOULINET_INSIDE "+raceName+" "+(frame-raceStart)+" x="+x+" "+keys);
            }
            return true;
        }
        keys=toWall>1.05f?moulinetSide+"S":"";
        return true;
    }

    private bool TryMoulinetTraverse(HeroController hero,string state,int age,float x)
    {
        if(moulinetTravel.Length==0||state=="CS Thwip Out"&&age==0)
        {
            moulinetTravel=x<29.0f?"R":"L";
            Note("MOULINET_TRAVERSE "+raceName+" "+(frame-raceStart)+" x="+
                 x.ToString("F2",CultureInfo.InvariantCulture)+" side="+moulinetTravel);
        }
        if(state=="CS Thwip Out"&&age<8){keys="";return true;}
        float room=moulinetTravel=="R"?53.95f-x:x-4.55f;
        if(raceBuild.Contains("moulinetvault")&&state=="Stomp Antic"&&age>=11&&age<=17&&
           hero.cState.onGround&&hero.CanJump()&&frame-raceJump>17)
        {
            var actor=hero.heroBox?hero.heroBox.GetComponent<Collider2D>():null;
            var body=hero.GetComponent<Rigidbody2D>();
            if(actor&&body)
            {
                ReadHitboxes(hero);
                AddFlailForecast(hero.transform.position);
                string hazard;
                float vx=room>.65f?(moulinetTravel=="R"?1f:-1f)*hero.GetWalkSpeed():0f;
                if(Forecast(actor.bounds,body.linearVelocity,actor.bounds.center.y,
                    vx,hero.JUMP_SPEED,13,false,false,out hazard,12)<1.8f)
                {
                    raceJump=frame;
                    RacePress((room>.65f?moulinetTravel:"")+"J",13);
                    Note("MOULINET_FLOOR_JUMP "+raceName+" "+(frame-raceStart)+" age="+age);
                    return true;
                }
            }
        }
        if(room>5.3f&&hero.CanDash()&&frame-raceDash>25&&!previousRaceKeys.Contains("S"))
        {
            raceDash=frame;
            RacePress(moulinetTravel+"S",6);
            return true;
        }
        keys=room>.65f?moulinetTravel:"";
        if(state=="Slam"&&age>=7)return false;
        return true;
    }

    private bool GuardLatePhases(HeroController hero, string state, int age, bool ground)
    {
        bool wave=state.IndexOf("Wave",StringComparison.OrdinalIgnoreCase)>=0&&!state.Contains("Wave Check");
        bool cross=state.StartsWith("CS ",StringComparison.Ordinal)||state=="Do CrossSlash"||
                   state=="Thread Roar"||state=="Thread Roar Antic"||state=="Stomp Antic"||state=="Stomp";
        if(!wave&&!cross&&state!="P3 Start"&&state!="P3 Roar"&&state!="P3 Roar End")return false;
        var pos=hero.transform.position;
        var body=hero.GetComponent<Rigidbody2D>();
        if(!body)return false;
        bool canJump=ground&&hero.CanJump()&&frame-raceJump>15;
        bool canAgain=!ground&&hero.CanDoubleJump(false)&&frame-raceDouble>18&&frame-raceJump>10;
        bool canDash=hero.CanDash()&&frame-raceDash>20;
        string towardsMid=pos.x<30f?"R":"L";
        if(state=="P3 Start"||state=="P3 Roar"||state=="P3 Roar End")
        {
            if (pos.x<22f||pos.x>40f)keys=towardsMid;
            return true;
        }
        ReadHitboxes(hero);
        if(wave)
        {
            Bounds bodyWave;
            float waveSpeed;
            if(LocateAbyssWave(pos,state,out bodyWave,out waveSpeed))
            {
                float sign=waveSpeed>0f?1f:-1f;
                float gap=(pos.x-bodyWave.center.x)*sign-bodyWave.extents.x;
                float speed=Mathf.Max(10f,Mathf.Abs(waveSpeed));
                float eta=gap/speed*60f;
                string forward=sign>0f?"R":"L";
                string backward=sign>0f?"L":"R";
                bool near=eta<28f;
                float fromTop=pos.y-bodyWave.max.y;
                if(frame-waveLogFrame>=17)
                {
                    waveLogFrame=frame;
                    Note("WAVE_GEOM "+raceName+" "+(frame-raceStart)+" age="+age+
                        " eta="+eta.ToString("F1",CultureInfo.InvariantCulture)+" top="+bodyWave.max.y.ToString("F2",CultureInfo.InvariantCulture)+
                        " floor="+currentFloor.ToString("F2",CultureInfo.InvariantCulture)+" actor="+pos);
                }
                if(canJump&&near)
                {
                    raceJump=lastWaveDodge=frame;
                    RacePress("J",14);
                    return true;
                }
                if(canAgain&&near&&body.linearVelocity.y<4.0f)
                {
                    raceDouble=lastWaveDodge=frame;
                    RacePress("J",15);
                    return true;
                }
                if(!ground&&canDash&&fromTop>1.62f&&eta<=11f&&eta> -25f)
                {
                    raceDash=frame;
                    RacePress(backward+"SJ",8);
                    return true;
                }
                if(!ground&&near&&frame-raceDouble<40)
                    keys="J";
                else if(eta>30f&&eta<48f && ((sign>0f&&pos.x<48f)||(sign<0f&&pos.x>12f)))
                    keys=forward;
                return true;
            }
            bool central=pos.x>=22f&&pos.x<=40f;
            if(state=="Wave L")
            {
                int launch=pos.x<26f?130:pos.x>36f?145:139;
                if(canJump&&age>=launch&&age<=launch+19&&frame-lastWaveDodge>45)
                {
                    raceJump=lastWaveDodge=frame;RacePress("J",15);return true;
                }
                if(canAgain&&age>=launch+14&&age<launch+37&&body.linearVelocity.y<4.6f)
                {
                    raceDouble=frame;RacePress("J",18);return true;
                }
                if(!ground&&frame-raceDouble<40)keys="J";
                else if(!central&&age<95)keys=towardsMid;
            }
            else if(!central) keys=towardsMid;
            return true;
        }

        if(age==0 || waveTravel=="" || pos.x<7.4f || pos.x>54.5f)
            waveTravel=pos.x<30f?"R":"L";
        if(state=="CS Flip Back"&&age>5)return false;
        float closest=20f;
        foreach(var box in threats)
            if(box.Label.IndexOf("circle",StringComparison.OrdinalIgnoreCase)>=0 ||
               box.Label.IndexOf("cross slash",StringComparison.OrdinalIgnoreCase)>=0 ||
               box.Label.IndexOf("slash catch",StringComparison.OrdinalIgnoreCase)>=0)
            {
                if(Mathf.Abs(pos.y-box.Shape.center.y)>box.Shape.extents.y+2.5f)continue;
                closest=Mathf.Min(closest,Mathf.Abs(pos.x-box.Shape.center.x)-box.Shape.extents.x);
            }
        if(canDash && ((closest<2.5f && (state!="Thread Roar Antic"||age>3)) ||
            (state=="CS Thwip Out"&&age>3&&age<9) || (state=="CS Slam"&&age<=8)))
        {
            raceDash=frame;RacePress(waveTravel+"S",8);return true;
        }
        keys=waveTravel;
        return true;
    }

    private static Bounds WorldColliderShape(Collider2D child)
    {
        var real=child.bounds;
        if(real.size.x>.1f&&real.size.y>.2f)return real;
        Vector2 offset=child.offset, size=Vector2.zero;
        var box=child as BoxCollider2D;
        var poly=child as PolygonCollider2D;
        var capsule=child as CapsuleCollider2D;
        var circle=child as CircleCollider2D;
        if(box)size=box.size;
        else if(capsule)size=capsule.size;
        else if(circle)size=Vector2.one*circle.radius*2f;
        else if(poly)
        {
            var points=poly.points;
            if(points.Length>0)
            {
                var lo=points[0];var hi=points[0];
                foreach(var at in points){lo=Vector2.Min(lo,at);hi=Vector2.Max(hi,at);}
                size=hi-lo;
                offset+=(hi+lo)*.5f;
            }
        }
        if(size.sqrMagnitude<.01f)return new Bounds(child.transform.position,Vector3.zero);
        var pt=child.transform.TransformPoint(offset);
        var tx=child.transform.TransformVector(new Vector3(size.x,0,0));
        var ty=child.transform.TransformVector(new Vector3(0,size.y,0));
        return new Bounds(pt,new Vector3(Mathf.Abs(tx.x)+Mathf.Abs(ty.x),Mathf.Abs(tx.y)+Mathf.Abs(ty.y),1f));
    }

    private static bool FlailIsLive(Transform hit)
    {
        var parent=hit.parent;
        if(!parent)return false;
        if(parent.gameObject.activeInHierarchy)return true;
        var root=parent.parent;
        return root&&root.gameObject.activeInHierarchy&&parent.name.IndexOf("Ball Sprite",StringComparison.OrdinalIgnoreCase)>=0;
    }

    private void AddFlailForecast(Vector3 heroPos)
    {
        if(frame-lastVomit>250||!raceBuild.Contains("preview"))return;
        if(frame-lastFlailScan>=5)
        {
            lastFlailScan=frame;
            flailTrack.RemoveAll(t=>!t.Damage||!t.Collider||!FlailIsLive(t.Damage.transform));
            foreach(var hit in FindObjectsByType<DamageHero>(FindObjectsInactive.Include,FindObjectsSortMode.None))
            {
                if(!hit.name.Equals("Flail Damager",StringComparison.OrdinalIgnoreCase)||
                    !FlailIsLive(hit.transform) || flailTrack.Any(t=>t.Damage==hit))continue;
                var c=(Collider2D)DamageCollider.GetValue(hit);
                if(!c)c=hit.GetComponent<Collider2D>();
                if(c)flailTrack.Add(new TrackedBox{Damage=hit,Collider=c,PriorFrame=-1000});
            }
        }
        foreach(var t in flailTrack)
        {
            var shape=WorldColliderShape(t.Collider);
            var velocity=t.PriorFrame==frame-1?(Vector2)(shape.center-t.PriorPosition)*60f:Vector2.zero;
            t.PriorFrame=frame;t.PriorPosition=shape.center;
            var body=t.Damage.GetComponentInParent<Rigidbody2D>(true);
            if(body && body.linearVelocity.sqrMagnitude>4f)velocity=body.linearVelocity;
            bool active=t.Collider.isActiveAndEnabled&&t.Damage.isActiveAndEnabled;
            if(!active && shape.center.y>currentFloor+3.7f && velocity.y< -2f)
            {
                float gravity=Mathf.Max(14f,body?Mathf.Abs(Physics2D.gravity.y*body.gravityScale):28f);
                float height=Mathf.Max(0f,shape.center.y-currentFloor);
                float duration=(velocity.y+Mathf.Sqrt(velocity.y*velocity.y+2f*gravity*height))/gravity;
                float landing=shape.center.x+velocity.x*duration;
                if(duration<.42f&&Mathf.Abs(landing-heroPos.x)<14f)
                    threats.Add(new BoxThreat{
                        Shape=new Bounds(new Vector3(landing,currentFloor+1.35f,0f),new Vector3(6.4f,5.5f,1f)),
                        Velocity=Vector2.zero, Label="Void blob landing",WaitFrames=Mathf.Clamp(Mathf.RoundToInt(duration*60f),0,17), Duration=25
                    });
                continue;
            }
            if(Mathf.Abs(shape.center.x-heroPos.x)>15f||Mathf.Abs(shape.center.y-heroPos.y)>12f)continue;
            if(shape.center.y<currentFloor+3.55f)
                shape=new Bounds(new Vector3(shape.center.x,currentFloor+1.35f,0f),
                    new Vector3(Mathf.Max(6.4f,shape.size.x),Mathf.Max(5.5f,shape.size.y),1f));
            if(shape.size.x<.12f)continue;
            threats.Add(new BoxThreat {Shape=shape,Velocity=active?velocity:Vector2.zero,Label="Void blob preview",
                WaitFrames=active?0:3,Duration=25});
        }
    }

    private void GuardNextInput()
    {
        if(raceName==null||!boss||!bossControl) return;
        var hero=HeroController.instance;
        if(!hero || !hero.CanInput() || hero.cState.invulnerable || hero.cState.dashing) return;
        if(wanderRace&&(pinClashFrame==frame||
           (raceBuild.Contains("wspellcross")||raceBuild.Contains("sharpdeflect"))&&
           frame-raceCast<=2&&keys.Contains("C")))
            return;
        var player=hero.heroBox?hero.heroBox.GetComponent<BoxCollider2D>():null;
        if (!player||!player.isActiveAndEnabled) return;
        var body=hero.GetComponent<Rigidbody2D>();
        if(!body)return;
        bool ground=hero.cState.onGround;
        var pos=hero.transform.position;
        if(pos.y>23f||frame-lastReflex<5)return;
        var hurtbox=player.bounds;
        ReadHitboxes(hero);
        AddFlailForecast(pos);
        string state=bossControl.ActiveStateName??"";
        int age=frame-raceStateSince;
        string plan=keys;
        float floor=ground?hurtbox.center.y:currentFloor+(hurtbox.center.y-pos.y);
        if(plan.IndexOf('C')>=0||plan.IndexOf('A')>=0)
        {
            var bodyBox=boss.GetComponent<Collider2D>();
            if(bodyBox&&bodyBox.isActiveAndEnabled)
                PredictTelegraph(state,age,bodyBox.bounds,hurtbox,boss.transform.position.x-pos.x);
        }
        if(threats.Count==0)return;
        bool wakeLanding=raceBuild.Contains("wakeheight");
        bool firstJump=hero.CanJump()&&ground&&frame-raceJump>10;
        bool secondJump=!ground&&hero.CanDoubleJump(false)&&frame-raceDouble>15;
        if(wakeLanding&&previousRaceKeys.Contains("J"))firstJump=secondJump=false;
        bool canDash=hero.CanDash()&&frame-raceDash>19;
        float speed=Mathf.Max(4f,hero.GetWalkSpeed());
        float dashSpeed=Mathf.Max(18f,hero.DASH_SPEED);
        float jumpSpeed=Mathf.Max(14f,hero.JUMP_SPEED);
        int intended=plan.Contains("L")?-1:plan.Contains("R")?1:0;
        bool priorDash=plan.Contains("S")&&hero.CanDash();
        bool priorJump=plan.Contains("J") &&
            (!raceBuild.Contains("bodytangent")||!previousRaceKeys.Contains("J"))&&
            (ground?hero.CanJump():hero.CanDoubleJump(false));
        string reason;
        int horizon=wanderRace&&pressureAttackFrame==frame?7:19;
        if(wanderRace&&raceBuild.Contains("centerwave")&&state=="Antic Wave"&&ground&&!priorDash&&!priorJump&&
           intended!=0&&intended*(boss.transform.position.x-pos.x)>0f)
        {
            float remaining=Mathf.Max(0f,Mathf.Abs(boss.transform.position.x-pos.x)-1.3f);
            horizon=Mathf.Clamp(Mathf.CeilToInt(remaining*60f/speed)+1,4,19);
        }
        float risk=Forecast(hurtbox,body.linearVelocity,floor,intended*(priorDash?dashSpeed:speed),
            priorJump?(ground||!raceBuild.Contains("bodytangent")?jumpSpeed:17.2f):0f,
            priorJump||raceBuild.Contains("bodytangent")&&!ground&&plan.Contains("J")?12:0,
            priorDash,!ground,out reason,horizon);
        bool blobAlert=raceBuild.Contains("blobflight")&&reason.StartsWith("Abyss Bullet",StringComparison.Ordinal);
        bool tangential=raceBuild.Contains("bodytangent");
        bool bodyAlert=(tangential||raceBuild.Contains("bodyguard"))&&reason=="Lost Lace Boss";
        bool wakeAlert=sharpIntercept&&raceBuild.Contains("crossmove")&&
            reason.StartsWith("Lost_Lace_Wake_Tendrils",StringComparison.Ordinal);
        if(wakeLanding&&wakeAlert&&!ground&&!priorDash&&body.linearVelocity.y>-1.0f&&
            ThreatClearance(hurtbox,reason)>.28f&&plan.Contains("J"))return;
        bool columnAlert=flowMeasured&&FlowSelection(state)&&raceBuild.Contains("columnaware")&&
            reason.StartsWith("Lost_Lace_tendril_set",StringComparison.Ordinal);
        if(raceBuild.Contains("bodyguard")&&raceBuild.Contains("reflexlate")&&boss.hp>=1450&&
           !raceBuild.Contains("reflexall")&&!bodyAlert)return;
        if(risk<(bodyAlert?2.65f:blobAlert||columnAlert?3.15f:wakeAlert?4.6f:9.0f))return;
        if(raceBuild.Contains("crossreact") && state!="Tendril Emerge Wait" && !state.Contains("Wave") &&
            !reason.StartsWith("Lost_Lace_tendril",StringComparison.OrdinalIgnoreCase) &&
            TryCrossStitch(hero,state,age,boss.transform.position.x-pos.x,boss.transform.position.y-pos.y,risk,reason))
            return;
        string best=plan;
        float bdx=boss.transform.position.x-pos.x;
        bool preserveReach=(sustainAttack||tangential)&&!boss.IsInvincible&&Mathf.Abs(bdx)<13f&&
            !state.Contains("Wave")&&!state.Contains("Shift")&&!state.StartsWith("CS ");
        var enemyBody=preserveReach?boss.GetComponent<Rigidbody2D>():null;
        float targetX=bdx+(enemyBody?Mathf.Clamp(enemyBody.linearVelocity.x*.13f,-3.5f,3.5f):0f);
        float directionCost=preserveReach?Mathf.Max(0f,Mathf.Abs(targetX-intended*(priorDash?dashSpeed:speed)*.13f)-3f)*.68f:0f;
        float cost=risk+directionCost;
        float bestClear=risk;
        foreach(string candidate in new[]{"","L","R","J","LJ","RJ","LS","RS"})
        {
            if(candidate.Length==0&&!blobAlert&&!bodyAlert&&!preserveReach&&!wakeAlert)continue;
            int dir=candidate.Contains("L")?-1:candidate.Contains("R")?1:0;
            bool jump=candidate.Contains("J"),dash=candidate.Contains("S");
            if(jump&&!firstJump&&!secondJump || dash&&!canDash || candidate==plan)continue;
            if(pos.x<6.8f&&dir<0||pos.x>54.4f&&dir>0)continue;
            if(tangential&&dash&&bodyAlert&&dir*bdx>0f&&Mathf.Abs(bdx)<7.0f&&
               pos.y-boss.transform.position.y<4.65f)continue;
            if(wakeLanding&&wakeAlert&&!ground&&dash&&dir*bdx>0f)continue;
            if(wanderRace&&raceBuild.Contains("jumpcut")&&dash&&pos.y<10.4f&&
               (state=="Charge Break"||state=="Charge Antic"||state=="Crossup Antic"||
                state=="Tendril G Antic"||state=="Tendril G Dash"))
            {
                float inFront=dir*(boss.transform.position.x-pos.x);
                if(inFront>0f&&inFront<dashSpeed*.21f+2.2f)continue;
            }
            string leftover;
            float found=Forecast(hurtbox,body.linearVelocity,floor,dir*(dash?dashSpeed:speed),
                jump?(wakeLanding&&!ground?17.2f:jumpSpeed):0f,jump?12:0,dash,!ground,out leftover,horizon);
            float reachCost=preserveReach?Mathf.Max(0f,Mathf.Abs(targetX-dir*(dash?dashSpeed:speed)*.13f)-3f)*.68f:0f;
            if(preserveReach&&found>risk)continue;
            if(preserveReach&&bestClear<2.75f&&found>=2.75f)continue;
            float wakeCost=wakeAlert&&!jump?(dash?1.75f:4.4f):0f;
            float newCost=found+(jump?1.0f:dash?2.2f:.35f)+(dir!=intended?.35f:0f)+reachCost+wakeCost;
            if(newCost+.7f<cost){cost=newCost;best=candidate;bestClear=found;}
        }
        bool urgent=blobAlert||bodyAlert||wakeAlert||columnAlert;
        if(best==plan || (bestClear>=(urgent?3.25f:7f) && risk-bestClear<(urgent?4f:9f)))return;
        lastReflex=frame;
        if(best.Contains("S"))raceDash=frame;
        if(best.Contains("J"))
        {
            if(ground)raceJump=frame;
            else raceDouble=frame;
        }
        Note("REFLEX "+raceName+" "+(frame-raceStart)+" "+state+" from="+plan+" to="+best+
             " threat="+reason+" "+risk.ToString("F1",CultureInfo.InvariantCulture)+"/"+bestClear.ToString("F1",CultureInfo.InvariantCulture));
        string output=best;
        if(wanderRace&&raceBuild.Contains("sticklace")&&pressureAttackFrame==frame&&plan.Contains("A")&&
           !best.Contains("S")&&(!best.Contains("L")&&!best.Contains("R")||
             plan.Contains("U")||plan.Contains("D")||best[0]==plan[0]))
            output+=plan.Contains("U")?"UA":plan.Contains("D")?"DA":"A";
        if(wanderRace&&raceBuild.Contains("pinwindow")&&plan.Contains("A")&&!output.Contains("A"))
            Note("WANDER_CUT_OVERRIDE "+raceName+" "+(frame-raceStart)+" "+state+
                 " from="+plan+" to="+output+" cause="+reason);
        RacePress(output,best.Contains("S")?6:best.Contains("J")?10:4);
    }

    private float ThreatClearance(Bounds body,string label)
    {
        float clearance=30f;
        foreach(var item in threats)
        {
            if(item.Label!=label)continue;
            var box=item.Shape;
            float dx=Mathf.Abs(body.center.x-box.center.x)-body.extents.x-box.extents.x;
            float dy=Mathf.Abs(body.center.y-box.center.y)-body.extents.y-box.extents.y;
            clearance=Mathf.Min(clearance,Mathf.Max(dx,dy));
        }
        return clearance;
    }

    private bool TryRingBurst(HeroController hero,int age,float dx,float dy,bool grounded,string toward)
    {
        float gap=Mathf.Abs(dx);
        if(grounded && age<13 && gap>1.7f && gap<3.8f && Mathf.Abs(dy)<.9f &&
            hero.CanJump()&&frame-raceJump>21)
        {
            raceJump=frame;
            RacePress("J",5);
            return true;
        }
        if(grounded || gap>4.5f || dy>-.65f || dy< -5.8f)return false;
        if(frame-ringPrimer>=1&&frame-ringPrimer<=7&&hero.CanAttack()&&frame-raceAttack>6)
        {
            raceAttack=frame;
            RacePress(toward+"DA",2);
            return true;
        }
        if(hero.CanDoubleJump(false)&&frame-raceJump>5&&frame-raceDouble>13&&gap<3.0f&&dy> -4.85f)
        {
            raceDouble=ringPrimer=frame;
            RacePress(toward+"J",1);
            return true;
        }
        if(hero.CanAttack()&&frame-raceAttack>7&&gap<3.25f&&dy< -1.55f)
        {
            raceAttack=frame;
            RacePress(toward+"DA",2);
            return true;
        }
        return false;
    }

    private bool CanWanderSpendSilk()
    {
        var pd=PlayerData.instance;
        int reserve=freeSilk?0:10;
        if(raceBuild.Contains("needlelast")&&branchAfterRoar&&raceState!="ComboSlash 1")return false;
        if(raceBuild.Contains("holdhalf")&&!branchAfterRoar&&boss&&boss.hp<1180&&pd.silk<=12&&
           raceState.StartsWith("Summon",StringComparison.Ordinal))return false;
        if(raceBuild.Contains("tailbank")&&branchAfterRoar&&pd.silk>8&&raceState!="ComboSlash 1")reserve=9;
        if(raceBuild.Contains("bankfirst")&&raceStart>=0&&frame-raceStart<480&&boss&&boss.hp>1350)
            reserve=9;
        if(raceBuild.Contains("bankall")&&boss&&boss.hp>900)reserve=9;
        if(raceBuild.Contains("needlebank")&&boss&&boss.hp>1170&&pd.silk<9)return false;
        if(raceBuild.Contains("bankphase")&&boss&&boss.hp<1330&&boss.hp>540)reserve=9;
        if(raceBuild.Contains("flamebank")&&boss&&boss.hp<900&&flintObserved&&
           (raceState=="Charge Break"||raceState=="Crossup Antic"||raceState=="J Slash M Antic")&&
           Mathf.Abs(boss.transform.position.x-29.1f)>12.6f)
            reserve=9;
        if(raceBuild.Contains("banksummon")&&raceState.StartsWith("Summon",StringComparison.Ordinal)&&
           pd.silk>=9)reserve=9;
        return frame-raceCast>4 && pd.silk-pd.SilkSkillCost>=reserve && ToolInput("Flintstone")!="C";
    }

    private bool TryP3ComboPressure(HeroController hero,int inputAge)
    {
        int age=NeedleWindowAge("ComboSlash 1",inputAge);
        if(age>23||hero.cState.dashing||frame<raceHoldUntil&&raceHeld.Length>1)
            return false;
        NeedleGeometry g;
        if(!ReadNeedleGeometry(hero,out g)||!g.Ground)return false;
        float dx=g.Target.center.x-g.Actor.center.x;
        float dy=g.Target.center.y-g.Actor.center.y;
        float side=Mathf.Sign(dx),gap=Mathf.Abs(dx);
        if(gap>6.9f||gap<2.0f||Mathf.Abs(dy)>1.10f)return false;
        if(TryFlowNeedle(hero,"ComboSlash 1",inputAge,null,true))return true;
        if(age>19||gap<=2.95f&&hero.CanAttack())return false;

        string toward=side<0?"L":"R";
        float step=gap>3.15f?side*hero.GetWalkSpeed():0f;
        ReadHitboxes(hero);
        AddFlailForecast(hero.transform.position);
        PredictTelegraph("ComboSlash 1",age,g.Target,g.Actor,dx);
        string hazard;
        if(Forecast(g.Actor,g.HeroVelocity,g.Floor,step,0f,0,false,false,out hazard,10)>1.25f)
            return false;
        keys=step!=0f?toward:"";
        if(inputAge-age>=5&&inputAge%6==0)
            Note("P3_COMBO_FOLLOW "+raceName+" "+(frame-raceStart)+" render="+inputAge+" native="+age+
                 " gap="+gap.ToString("F2",CultureInfo.InvariantCulture));
        return true;
    }

    private bool IsSpitCutWindow(string state,int age)
    {
        return raceBuild.Contains("spitcut")&&boss.hp<900&&
               (state=="Vomit Start"&&age>=15&&age<=28||state=="Vomit Fire"&&age<=3);
    }

    private bool TrySharpThroughAttack(HeroController hero,string state,int age,float dx,float dy)
    {
        // Start before the live blade. The wind-up still has a hurtbox.
        age=NeedleWindowAge(state,age);
        if(raceBuild.Contains("earlycombo")&&state=="ComboSlash 1"&&choiceTape!=null&&choiceTapeCursor==13&&
           frame-raceStart>=235&&frame-raceStart<276&&PlayerData.instance.silk<8)
            return false;
        bool charge=state=="Charge Break"&&age>=9&&age<=12||
                    state=="Crossup Antic"&&age>=12&&age<=17;
        bool combo=state=="ComboSlash 1"&&age>=16&&age<=21;
        bool whip=state=="Tendril G Antic"&&age>=16&&age<=21;
        bool circle=state=="J Slash M Antic"&&age>=13&&age<=18;
        bool summon=state=="Summon Antic"&&age>=5&&age<=11||state=="Summon Thwip"&&age<=4;
        bool spit=IsSpitCutWindow(state,age);
        if(sharpIntercept)
        {
            charge|=state=="Charge Break"&&age>=5&&age<=17||
                    state=="Crossup Antic"&&age>=8&&age<=21;
            combo|=state=="ComboSlash 1"&&age>=11&&age<=24;
            whip|=state=="Tendril G Antic"&&age>=12&&age<=23;
            circle|=state=="J Slash M Antic"&&age>=9&&age<=20;
            if(raceBuild.Contains("orbitcut")&&state=="J Slash M Antic"&&age<14&&Mathf.Abs(dx)>7.95f)
                circle=false;
        }
        if(!charge&&!combo&&!whip&&!circle&&!summon&&!spit)return false;
        if(spit&&(Mathf.Abs(dx)<3.30f||Mathf.Abs(dx)>6.60f||
           Mathf.Abs(dy)>1.68f||PlayerData.instance.silk<12))return false;
        float gap=Mathf.Abs(dx);
        if(gap<(sharpIntercept?3.05f:charge||summon?3.25f:4.45f)||gap>8.4f||Mathf.Abs(dy)>1.65f)return false;
        if(!TrySharpdart(hero,state,age,dx,dy,true))return false;
        Note("SHARP_THROUGH "+raceName+" "+(frame-raceStart)+" "+state+" age="+age+
             " gap="+gap.ToString("F2",CultureInfo.InvariantCulture));
        return true;
    }

    private bool TrySharpdart(HeroController hero, string state, int age, float dx, float dy, bool through=false)
    {
        if (!hero.CanCast() || PlayerData.instance.silk<PlayerData.instance.SilkSkillCost ||
            frame-raceCast<(sharpIntercept?39:52) || hero.cState.invulnerable || boss.IsInvincible ||
            wanderRace&&(!raceBuild.Contains("wspellsharp")||!CanWanderSpendSilk()))
            return false;
        bool earlySummon=wanderRace&&raceBuild.Contains("sharpdeflect")&&
            (state=="Summon Antic"&&age>=5&&age<=11||state=="Summon Thwip"&&age<=4);
        if(state.Contains("Counter")&&state!="Counter End"||state.Contains("Rapid")||state.Contains("Dive In")||
           state.Contains("Shift")&&!(through&&state=="P2 Shift 2"&&raceBuild.Contains("shiftcast"))||
           state=="Tendril Emerge Wait"||state.Contains("Summon")&&!earlySummon)
            return false;
        float gap=Mathf.Abs(dx);
        float x=hero.transform.position.x;
        int edge=dx>0f?1:-1;
        bool intoWall=x+edge*10f<4f||x+edge*10f>57f;
        bool wallTarget=wanderRace&&raceBuild.Contains("savedgear")&&gap<7.2f&&
            (edge<0?boss.transform.position.x<9.5f:boss.transform.position.x>48.5f);
        if(gap>8.65f||gap<1.3f||Mathf.Abs(dy)>2.0f||intoWall&&!wallTarget)
            return false;
        bool safeDamage=(state=="Downstab Land" && age>=3&&age<=10)||
            (state=="Counter End"&&age<=5)||(state=="Stunned"&&age<32)||state=="Charge Recover"||
            wanderRace&&((state=="Dive Out G"&&age>=5&&age<=13&&gap>4.6f)||
                (state=="Evade Recover"&&age<=8&&gap>4.0f)||
                (state=="Tendril G Whip End"&&age<=5&&gap>4.0f));
        bool dodge=(state=="ComboSlash 1"&&age>=15&&age<=21)||
            (state=="Charge Break"&&age>=7&&age<=12)||(state=="Crossup Antic"&&age>=11&&age<=17)||
            (state=="Tendril G Antic"&&age>=16&&age<=21)||
            (state=="J Slash M Antic"&&age>=12&&age<=18);
        if(sharpIntercept)
        {
            if(raceBuild.Contains("sharpnarrow")&&through)
            {
                if(state=="Charge Break"&&age>12||state=="Crossup Antic"&&age<11||
                   state=="J Slash M Antic"&&age>15||state=="Summon Thwip")
                    return false;
            }
            Bounds caster, target;
            if(!ReadPinnedTarget(hero,out caster,out target))return false;
            var enemyBody=boss.GetComponent<Rigidbody2D>();
            var enemySpeed=enemyBody?enemyBody.linearVelocity:Vector2.zero;
            float predictedY=target.center.y+Mathf.Clamp(enemySpeed.y*.18f,-2.6f,2.6f);
            if(raceBuild.Contains("sharpnarrow")&&through&&Mathf.Abs(enemySpeed.y)>3.6f&&
               Mathf.Abs(predictedY-caster.center.y)>1.45f)return false;
            float predictedX=edge*(target.center.x-caster.center.x+Mathf.Clamp(enemySpeed.x*.21f,-3.2f,3.2f));
            if(predictedX<.85f||predictedX>9.5f||
               Mathf.Abs(predictedY-caster.center.y)>2.15f)return false;
            if(!through)
            {
                if(gap<4.2f&&hero.CanAttack()&&frame-raceAttack>2)return false;
                safeDamage|=gap>=4.35f&&age<13&&
                    (state=="Idle"||state=="Land"||state=="Hop Recover"||
                     state=="Bullet S Ground 1"||state=="Bullet S Ground 2"||
                     state=="Tendril A End"||state=="Tendril G Whip End");
                if(raceBuild.Contains("throughonly")&&!dodge&&!earlySummon)return false;
            }
            else dodge=true;
        }
        if(!safeDamage&&!dodge&&!earlySummon)return false;
        raceCast=frame;
        string face=hero.cState.facingRight==(dx>=0f)?"":dx<0f?"L":"R";
        if(sharpIntercept)
        {
            sharpTried++;
            sharpLastPlan=frame;
            sharpPlanMasks=PlayerData.instance.health;
            sharpPlanThrough=through;
        }
        Note("SHARP_CAST "+raceName+" "+(frame-raceStart)+" "+state+" age="+age+" through="+through);
        RacePress(face+"C",3);
        return true;
    }

    private bool GetColumnExit(HeroController hero, string state, int age, bool grounded, string mid, bool requireBox=false)
    {
        if(frame>=columnPlanFrame+5)
        {
            columnPlanFrame=frame;
            var colliders=new List<Bounds>();
            int activeRoots=0;
            foreach(var tr in FindObjectsByType<Transform>(FindObjectsInactive.Include,FindObjectsSortMode.None))
            {
                if(!tr.gameObject.activeInHierarchy ||
                   tr.name.IndexOf("Lost_Lace_tendril_set",StringComparison.OrdinalIgnoreCase)<0) continue;
                activeRoots++;
                var children=tr.GetComponentsInChildren<Collider2D>(true);
                if(frame-columnWaitLog>=40)
                {
                    columnWaitLog=frame;
                    Note("COLUMN_ROOT "+raceName+" "+(frame-raceStart)+" scene="+tr.gameObject.scene.name+
                        " at="+tr.position+" boxes="+string.Join(",",children.Select(c=>c.name+"/"+c.GetType().Name).ToArray()));
                }
                foreach(var child in children)
                {
                    if(!child.GetComponent<DamageHero>() &&
                       child.name.IndexOf("damager",StringComparison.OrdinalIgnoreCase)<0) continue;
                    var shape=WorldColliderShape(child);
                    if(shape.size.x>.15f && shape.size.y>.4f) colliders.Add(shape);
                }
            }
            if(raceBuild.Contains("preview"))
            {
                var marks=FindObjectsByType<Transform>(FindObjectsInactive.Exclude,FindObjectsSortMode.None)
                    .Where(t=>t.name.Equals("tendril antic",StringComparison.OrdinalIgnoreCase)&&
                              t.position.y>1f && t.position.y<13f).Select(t=>t.position).ToArray();
                foreach(var mark in marks)
                {
                    if(colliders.Any(h=>Mathf.Abs(h.center.x-mark.x)<2.0f))continue;
                    colliders.Add(new Bounds(new Vector3(mark.x,9.8f,0f),new Vector3(6.5f,12.5f,1f)));
                }
            }
            columnThreatCount=activeRoots;
            columnHull=colliders.ToArray();
        }
        if(columnHull.Length==0)
        {
            if(requireBox)return false;
            int since=state=="Tendril Emerge Wait"?frame-emergingStarted:frame-summonStarted;
            bool escapeWindow=state=="Tendril Emerge Wait"?since>=14&&since<=77:since>=30&&since<=113;
            if(!escapeWindow)return false;
            if(frame-columnLogFrame>=20)
            {
                columnLogFrame=frame;
                Note("COLUMN_SIGHT "+raceName+" "+(frame-raceStart)+" "+state+" roots="+columnThreatCount+" shapes=0");
                if(raceBuild.Contains("scout"))
                {
                    var marks=FindObjectsByType<Transform>(FindObjectsInactive.Exclude,FindObjectsSortMode.None)
                        .Where(t=>t.position.y<12f&&t.position.y>2f&&t.position.x>3f&&t.position.x<58f)
                        .Where(t=>t.name.IndexOf("Tendril",StringComparison.OrdinalIgnoreCase)>=0 ||
                                  t.name.IndexOf("Antic",StringComparison.OrdinalIgnoreCase)>=0 ||
                                  t.name.IndexOf("Rumble",StringComparison.OrdinalIgnoreCase)>=0 ||
                                  t.name.IndexOf("Dust",StringComparison.OrdinalIgnoreCase)>=0)
                        .Select(t=>t.name+"@"+t.position.x.ToString("F2",CultureInfo.InvariantCulture)).Distinct().Take(25);
                    Note("COLUMN_PREVIEW "+raceName+" "+(frame-raceStart)+" "+string.Join("|",marks.ToArray()));
                }
            }
            if(!raceBuild.Contains("lanes"))return false;
            float heroX=hero.transform.position.x;
            float[] gaps=state=="Tendril Emerge Wait"
                ? new[]{19.3f,31.6f,44.6f}
                : new[]{8.5f,18.5f,30.4f,42.6f,53.6f};
            float lane=gaps.OrderBy(k=>Mathf.Abs(k-heroX)).First();
            if(Mathf.Abs(lane-heroX)>.5f)
            {
                keys=heroX<lane?"R":"L";
                return true;
            }
            return state=="Tendril Emerge Wait"||state.StartsWith("Summon ",StringComparison.Ordinal)||
                   state.StartsWith("Dive In ",StringComparison.Ordinal);
        }
        var player=hero.transform.position;
        float x=player.x;
        float current=16f;
        foreach(var box in columnHull)
            current=Mathf.Min(current,Mathf.Abs(x-box.center.x)-box.extents.x);
        if (frame-columnLogFrame>=14)
        {
            columnLogFrame=frame;
            Note("COLUMN_SIGHT "+raceName+" "+(frame-raceStart)+" "+state+" roots="+columnThreatCount+
                " shapes="+string.Join(",",columnHull.Select(h=>h.center.x.ToString("F2",CultureInfo.InvariantCulture)+
                 "/"+h.extents.x.ToString("F2",CultureInfo.InvariantCulture)).ToArray()));
        }
        if(current>=1.35f)
        {
            if(requireBox&&raceBuild.Contains("columnbrake"))
            {
                var rb=hero.GetComponent<Rigidbody2D>();
                float predicted=x+(rb?Mathf.Clamp(rb.linearVelocity.x*.17f,-2.1f,2.1f):0f);
                float next=16f;
                foreach(var box in columnHull)
                    next=Mathf.Min(next,Mathf.Abs(predicted-box.center.x)-box.extents.x);
                if(next<.95f)
                {
                    keys="";
                    raceHoldUntil=frame;
                    if(frame-columnBrakeLogged>=8)
                    {
                        columnBrakeLogged=frame;
                        Note("FLOW_COLUMN_BRAKE "+raceName+" "+(frame-raceStart)+" "+state+
                             " now="+current.ToString("F2",CultureInfo.InvariantCulture)+
                             " next="+next.ToString("F2",CultureInfo.InvariantCulture));
                    }
                    return true;
                }
            }
            return state=="Tendril Emerge Wait";
        }
        float best=float.NegativeInfinity;
        columnLane=x;
        for(float lane=Mathf.Max(7f,x-16f);lane<=Mathf.Min(55f,x+16f);lane+=.36f)
        {
            float clearance=16f;
            foreach(var box in columnHull)
                clearance=Mathf.Min(clearance,Mathf.Abs(lane-box.center.x)-box.extents.x);
            float score=Mathf.Min(2.85f,clearance)*4.3f-Mathf.Abs(lane-x)*.39f;
            if(requireBox&&!boss.IsInvincible)
                score-=Mathf.Max(0f,Mathf.Abs(lane-boss.transform.position.x)-3.8f)*.35f;
            if(score>best){best=score;columnLane=lane;}
        }
        if(Mathf.Abs(columnLane-x)<.34f) return false;
        string move=columnLane>x?"R":"L";
        if(Mathf.Abs(columnLane-x)>4.4f&&hero.CanDash()&&frame-raceDash>22)
        {
            raceDash=frame;
            RacePress(move+"S",7);
        }
        else keys=move;
        return true;
    }

    private bool DriveSteady(HeroController hero, string state, int age, float dx, float dy, bool ground, bool counter)
    {
        var pos=hero.transform.position;
        var rb=hero.GetComponent<Rigidbody2D>();
        float gap=Mathf.Abs(dx), y=pos.y-currentFloor;
        string toward=dx<0f?"L":"R", away=dx<0f?"R":"L";
        string mid=pos.x<30f?"R":"L";
        bool atSide=pos.x<10f||pos.x>54f;
        string face=hero.cState.facingRight==(dx>=0f)?"":toward;
        bool slashing=state.StartsWith("ComboSlash",StringComparison.Ordinal)||
                      state.StartsWith("Quick Slash",StringComparison.Ordinal)||
                      state.StartsWith("Combo Strike",StringComparison.Ordinal);
        bool downstab=state=="Downstab Antic"||state=="Downstab"||state=="Dstab Multihit"||
                      state=="Dstab Strike";
        bool counterRunning=(counter&&state!="Counter End")||state.StartsWith("Multihit",StringComparison.Ordinal);
        bool whipping=state=="Tendril G Whip"||state=="Tendril G Dash"||state=="Tendril G Antic";
        bool charge=state=="Charge"||state=="Crossup Antic"||state=="Charge Break";
        bool bigSlash=state=="J Slash M Antic"||state=="J Slash Multi"||state=="J Slash Multihit";
        string group=slashing?"combo":downstab?"stab":counterRunning?"counter":
            whipping?"whip":charge?"charge":bigSlash?"saw":"";
        if(group!=guardedMove)
        {
            guardedMove=group;
            exitAt=frame;
            exitSide=atSide?mid:away;
        }
        bool pushOff=hero.CanDash()&&frame-raceDash>=22;
        bool jumpOff=ground&&hero.CanJump()&&frame-raceJump>=17;
        bool liftMore=!ground&&hero.CanDoubleJump(false)&&frame-raceDouble>=18&&frame-raceJump>=12;
        bool favored=hero.CanAttack()&&frame-raceAttack>7&&!boss.IsInvincible&&gap>1.35f&&
            gap<3.95f&&Mathf.Abs(dy)<1.75f;

        if (state=="Tendril Emerge Wait" || state.Contains("Void Wave") || state.Contains("Tsunami"))
        {
            ReadHitboxes(hero);
            var boxes=threats.Where(t=>t.Label.IndexOf("tendril_set",StringComparison.OrdinalIgnoreCase)>=0 ||
                                       t.Label.IndexOf("wave",StringComparison.OrdinalIgnoreCase)>=0).ToArray();
            float savePos=pos.x, quality=float.NegativeInfinity;
            for(float lane=Mathf.Max(8f,pos.x-12f);lane<=Mathf.Min(55f,pos.x+12f);lane+=.45f)
            {
                float clearance=boxes.Length>0?15f:2f;
                foreach(var hit in boxes)
                    clearance=Mathf.Min(clearance,Mathf.Abs(lane-hit.Shape.center.x)-hit.Shape.extents.x);
                float q=Mathf.Min(3.1f,clearance)*4f-Mathf.Abs(lane-pos.x)*.24f-Mathf.Abs(lane-31f)*.02f;
                if(q>quality) {quality=q;savePos=lane;}
            }
            if(boxes.Length>0)
            {
                if(age%15==0) Note("STEADY_COLUMNS "+raceName+" "+(frame-raceStart)+" count="+boxes.Length+
                    " x="+pos.x.ToString("F2",CultureInfo.InvariantCulture)+" target="+savePos.ToString("F2",CultureInfo.InvariantCulture));
                if(Mathf.Abs(savePos-pos.x)>.55f) keys=savePos>pos.x?"R":"L";
                else if (jumpOff && boxes.Any(x=>x.Shape.extents.y<3.3f && Mathf.Abs(x.Shape.center.x-pos.x)<6f))
                {
                    raceJump=frame;
                    RacePress("J",12);
                }
                return true;
            }
            if (state=="Tendril Emerge Wait")
            {
                if(age<34||age>58) keys=mid;
                else if(age>=42 && jumpOff){raceJump=frame;RacePress(mid+"J",14);}
                else keys=mid;
                return true;
            }
        }

        if (counterRunning)
        {
            if (!state.Contains("Hit") && !state.Contains("Loop") && !state.Contains("Tele") &&
                !state.Contains("Rapid") && !state.Contains("Multihit"))
            {
                if(gap<4.8f) keys=exitSide;
                else if (pos.x<11f||pos.x>53f) keys=mid;
                return true;
            }
            if (jumpOff && (gap<10f||state=="Counter Hit"))
            {
                raceJump=frame;RacePress(exitSide+"J",15);return true;
            }
            if (liftMore && rb.linearVelocity.y<4f && gap<9f)
            {
                raceDouble=frame;RacePress(exitSide+"J",10);return true;
            }
            if (pushOff&&gap<5f&&y>2.3f)
            {
                raceDash=frame;RacePress(exitSide+"S",6);return true;
            }
            keys=exitSide;
            return true;
        }

        if (downstab)
        {
            if (state=="Downstab Antic")
            {
                if(age<=9 && Mathf.Abs(dy)>4.6f && gap<8f && atSide) exitSide=mid;
                if(gap<10f || age>=14) keys=exitSide;
                if (age>=20 && age<=24 && pushOff && gap<7.1f && atSide && dy>3.5f)
                {
                    raceDash=frame;RacePress(exitSide+"S",7);
                }
                return true;
            }
            float vel=boss.GetComponent<Rigidbody2D>().linearVelocity.x;
            if (age<=4 && gap<9f && (exitSide=="R"?vel>13f:vel< -13f) && Mathf.Abs(dy)>3.2f &&
                !atSide)
                exitSide=vel>0?"L":"R";
            if(gap<7.2f && pushOff && dy>1.4f)
            {
                raceDash=frame;RacePress(exitSide+"S",7);return true;
            }
            keys=exitSide;
            return true;
        }

        if (slashing)
        {
            int first=state=="ComboSlash 1"?32:state=="Quick Slash 1"?1:3;
            if (favored && state=="ComboSlash 1" && age<=10)
            {
                raceAttack=frame;RacePress(face+"A",2);return true;
            }
            if (gap<10.0f && age>=first-12 && jumpOff)
            {
                raceJump=frame;RacePress(exitSide+"J",12);return true;
            }
            if (liftMore && gap<8f && rb.linearVelocity.y<3f && (y<4.2f||frame-exitAt>45))
            {
                raceDouble=frame;RacePress(exitSide+"J",11);return true;
            }
            if (gap<4.6f && y>2.1f && pushOff)
            {
                raceDash=frame;RacePress(exitSide+"S",7);return true;
            }
            if(gap<12.2f) keys=exitSide;
            return true;
        }

        if (charge)
        {
            int ant=state=="Crossup Antic"?20:state=="Charge Break"?19:0;
            if (age>=ant && gap<13f && jumpOff)
            {
                raceJump=frame;RacePress(exitSide+"J",14);return true;
            }
            if (age>=ant+3 && gap<8.6f && liftMore && rb.linearVelocity.y<4.0f)
            {
                raceDouble=frame;RacePress(exitSide+"J",11);return true;
            }
            if (state=="Charge" && y>2.9f && gap<5f && pushOff)
            {
                raceDash=frame;RacePress(exitSide+"S",6);return true;
            }
            if(gap<10f) keys=exitSide;
            return true;
        }

        if (bigSlash)
        {
            if(state=="J Slash M Antic" && age<6 && favored)
            {
                raceAttack=frame;RacePress(face+"A",2);return true;
            }
            if (age>=17 && gap<11f && pushOff)
            {
                raceDash=frame;RacePress(exitSide+"S",8);return true;
            }
            if(gap<11.3f) keys=exitSide;
            return true;
        }

        if (whipping)
        {
            if(state=="Tendril G Antic"&&favored&&age<12)
            {
                raceAttack=frame;RacePress(face+"A",2);return true;
            }
            if(gap<13.3f)
            {
                int warm=state=="Tendril G Antic"?31:0;
                if(age>=warm && jumpOff)
                {
                    raceJump=frame;RacePress(exitSide+"J",14);return true;
                }
                if((state=="Tendril G Whip"||state=="Tendril G Dash")&&liftMore &&
                    rb.linearVelocity.y<3.8f&&y<5.0f)
                {
                    raceDouble=frame;RacePress(exitSide+"J",10);return true;
                }
                keys=exitSide;
            }
            return true;
        }

        if (state=="Idle" && gap>5.3f && gap<13f && Mathf.Abs(dy)<3.3f &&
            hero.CanCast() && frame-raceCast>=85 && PlayerData.instance.silk>=PlayerData.instance.SilkSkillCost &&
            !raceBuild.Contains("nocast") && !raceBuild.Contains("sharp"))
        {
            raceCast=frame;
            RacePress(face+"C",3);
            return true;
        }
        if (lateRisk && (state=="Dive In 1"||state=="Dive In 2") && (pos.x<12f||pos.x>52f))
        {
            keys=mid;
            return true;
        }
        return false;
    }

    private void ObserveFrameRng()
    {
        var live=UnityEngine.Random.state;
        int draws=-1;
        if(haveRngObservation)
        {
            try
            {
                UnityEngine.Random.state=previousRngState;
                for(int n=0;n<=12;n++)
                {
                    if(UnityEngine.Random.state.Equals(live)){draws=n;break;}
                    UnityEngine.Random.Range(0f,1f);
                }
            }
            finally { UnityEngine.Random.state=live; }
        }
        haveRngObservation=true;
        previousRngState=live;
        stableRngFrames=draws>=0&&draws==previousRngDraws?stableRngFrames+1:0;
        previousRngDraws=draws;
    }

    private bool ReadPressureHulls(HeroController hero)
    {
        if(pressureHulls!=null)return pressureHullsReady;
        pressureHulls=new Collider2D[NativeSlashFields.Length];
        for(int i=0;i<NativeSlashFields.Length;i++)
        {
            var damager=NativeSlashFields[i]==null?null:NativeSlashFields[i].GetValue(hero) as DamageEnemies;
            if(!damager)continue;
            pressureHulls[i]=damager.GetComponentsInChildren<Collider2D>(true)
                .OrderByDescending(c=>{var b=WorldColliderShape(c);return b.size.x*b.size.y;}).FirstOrDefault();
        }
        pressureHullsReady=pressureHulls[0];
        Note("PRESSBOX_HULL "+raceName+" "+
            string.Join("|",pressureHulls.Select(h=>h?h.name+" "+WorldColliderShape(h).size:"-").ToArray()));
        return pressureHullsReady;
    }

    private void LoadAuditReplay()
    {
        string path=Path.Combine(root,raceBuild.Contains("floorcopy")?"floor-inputs.tsv":raceBuild.Contains("bestopening")?"retime-opening.tsv":raceBuild.Contains("audittrim")?"audit-clean.tsv":"audit-reference.tsv");
        var rows=File.ReadAllLines(path).Select(line=>line.Split('\t')).ToArray();
        auditStates=rows.Select(r=>r[0]).ToArray();
        auditHealth=rows.Select(r=>int.Parse(r[1],CultureInfo.InvariantCulture)).ToArray();
        auditKeys=rows.Select(r=>r[2]).ToArray();
    }

    private void ApplyAuditReplay()
    {
        if(auditKeys==null)return;
        int t=frame-raceStart;
        if(t>=auditKeys.Length){auditKeys=null;return;}
        bool fade=raceBuild.Contains("auditburn");
        if(fade&&t>=(raceBuild.Contains("floorcopy")?425:raceBuild.Contains("floorstart")?107:515)){auditKeys=null;return;}
        int slack=fade?5:2;
        int prior=Math.Max(0,t-slack), after=Math.Min(auditKeys.Length-1,t+slack);
        bool stateMatches=false, hpMatches=false;
        for(int n=prior;n<=after;n++)
        {
            stateMatches|=auditStates[n]==raceState;
            hpMatches|=Math.Abs(auditHealth[n]-boss.hp)<=(raceBuild.Contains("bestopening")?13:fade?5:0);
        }
        auditDrift=stateMatches&&hpMatches?0:auditDrift+1;
        if(auditDrift>=4)
        {
            Note("AUDIT_REPLAY_BREAK "+raceName+" "+t+" actual="+boss.hp+" expected="+auditHealth[t]+
                 " state="+raceState+" expectedState="+auditStates[t]);
            auditKeys=null;
            return;
        }
        keys=auditKeys[t];
    }

    private static void ObserveAttackInput(HeroController __instance)
    {
        var m=geometryOwner;
        if(m==null||!m.traceAttack||m.raceName==null||m.raceStart<0||m.raceEndFrame>=0||
           __instance!=HeroController.instance)return;
        Note("AUDIT_ATTACK "+m.raceName+" "+(m.frame-m.raceStart)+" input="+keys+" state="+m.raceState);
    }

    private struct NeedleGeometry
    {
        public Bounds Actor, Target;
        public Vector2 HeroVelocity, TargetVelocity;
        public bool Ground, FacingRight;
        public float HeroX, Floor;
        public int InputDelay;
    }

    private bool ReadNeedleGeometry(HeroController hero,out NeedleGeometry geometry)
    {
        geometry=new NeedleGeometry();
        if(!ReadPinnedTarget(hero,out geometry.Actor,out geometry.Target)||!ReadPressureHulls(hero))
            return false;
        var actorBody=hero.GetComponent<Rigidbody2D>();
        if(!actorBody)return false;
        var targetBody=boss.GetComponent<Rigidbody2D>();
        geometry.HeroVelocity=actorBody.linearVelocity;
        geometry.TargetVelocity=targetBody?targetBody.linearVelocity:Vector2.zero;
        geometry.Ground=hero.cState.onGround;
        geometry.FacingRight=hero.cState.facingRight;
        geometry.HeroX=hero.transform.position.x;
        geometry.Floor=geometry.Ground?geometry.Actor.center.y:
            currentFloor+geometry.Actor.center.y-hero.transform.position.y;
        return true;
    }

    private Bounds NeedleShape(HeroController hero,NeedleGeometry g,int family,bool faceRight)
    {
        bool alternate=hero.cState.altAttack&&frame-raceAttack<47;
        var collider=pressureHulls[family*2+(alternate?1:0)]??pressureHulls[family*2];
        if(!collider)return new Bounds();
        var shape=WorldColliderShape(collider);
        if(family==0&&faceRight!=g.FacingRight)
        {
            var c=shape.center;
            c.x=2f*g.HeroX-c.x;
            shape.center=c;
        }
        return shape;
    }

    private float NeedleContact(NeedleGeometry g,Bounds shape,float walk,bool keepJump)
    {
        if(shape.size.x<.08f||shape.size.y<.08f)return -20f;
        var relative=g.TargetVelocity-g.HeroVelocity;
        var projected=g.Target;
        float best=-20f;
        for(int i=1+g.InputDelay;i<=2+g.InputDelay;i++)
        {
            float dt=i/60f;
            projected.center=g.Target.center+(Vector3)(relative*dt);
            projected.center+=new Vector3((g.HeroVelocity.x-walk)*dt*.42f,
                g.Ground?0f:(keepJump?17f:28.5f)*dt*dt,0);
            best=Mathf.Max(best,PressureOverlap(shape,projected));
        }
        return best;
    }

    private bool FlowSelection(string state)
    {
        if(raceBuild.Contains("phaseonly"))return state.StartsWith("P2 Shift",StringComparison.Ordinal)||
            raceBuild.Contains("columnfour")&&state.StartsWith("P4 Shift",StringComparison.Ordinal);
        return !raceBuild.Contains("lateonly")||boss.hp<=900;
    }

    private bool TryComboRelease(HeroController hero,string state,int age)
    {
        if(age>11||(state!="ComboSlash 3"&&state!="ComboSlash 5")||
           hero.cState.dashing||hero.cState.invulnerable)return false;
        NeedleGeometry g;
        if(!ReadNeedleGeometry(hero,out g))return false;
        float dx=g.Target.center.x-g.Actor.center.x;
        float side=Mathf.Sign(dx),outward=-side;
        float gap=Mathf.Abs(dx);
        if(gap>8.65f)return false;
        // The lance has already started closing in before the damage box enables.
        float dt=Mathf.Max(2,12-age)/60f;
        float closing=Mathf.Clamp(outward*g.TargetVelocity.x,0f,35f);
        float forwardGap=gap+(outward*g.HeroVelocity.x*.65f-closing)*dt;
        float y=g.Actor.center.y-g.Target.center.y;
        float nextY=y+g.HeroVelocity.y*dt-(g.Ground?0f:25f*dt*dt);
        if(forwardGap>5.2f||nextY>3.8f)return false;

        float overlap;
        string needle=ReadAttackEnvelope(hero,g,out overlap);
        if(g.Ground&&age<=1&&hero.CanAttack()&&overlap>=.40f&&
           needle.Length>0&&TryPressSwing(hero,state,age,true))return true;
        if(Mathf.Abs(y)<1.45f&&gap>=3.05f&&gap<=7.1f&&age<=4&&
            TrySharpdart(hero,state,age,dx,-y,true))return true;

        bool rise=!previousRaceKeys.Contains("J")&&
            (g.Ground?hero.CanJump()&&frame-raceJump>12:
              hero.CanDoubleJump(false)&&frame-raceDouble>18&&frame-raceJump>10);
        if(rise&&age<=8&&nextY>-.7f)
        {
            if(g.Ground)raceJump=frame;
            else raceDouble=frame;
            string drift=gap>2.7f?"":outward<0f?"L":"R";
            RacePress(drift+"J",8);
            Note("PRESSURE_COMBO_LIFT "+raceName+" "+(frame-raceStart)+" "+state+" age="+age+
                 " gap="+gap.ToString("F2",CultureInfo.InvariantCulture));
            return true;
        }

        float room=outward>0f?54.69f-g.HeroX:g.HeroX-3.73f;
        if(room>4.5f&&age>=4&&nextY<2.95f&&hero.CanDash()&&frame-raceDash>21)
        {
            raceDash=frame;
            RacePress((outward<0f?"L":"R")+"S",5);
            Note("PRESSURE_COMBO_SLIDE "+raceName+" "+(frame-raceStart)+" "+state+" age="+age+
                 " gap="+gap.ToString("F2",CultureInfo.InvariantCulture));
            return true;
        }
        return false;
    }

    private bool TryKeepInsideAfterCharge(HeroController hero,string state,int age)
    {
        bool windup=state=="J Slash M Antic"&&age<=13;
        bool transition=state=="Crossup Antic"&&age<=23;
        if(!windup&&!transition||hero.cState.dashing||frame<raceHoldUntil&&raceHeld.Length>1||
           frame<summonUntil)return false;
        NeedleGeometry g;
        if(!ReadNeedleGeometry(hero,out g)||!g.Ground)return false;
        float edge=g.HeroX>49f?1f:g.HeroX<9.2f?-1f:0f;
        if(edge==0f)return false;
        float gap=edge*(g.Actor.center.x-g.Target.center.x);
        if(gap<4.7f||gap>18.0f||Mathf.Abs(g.Target.center.y-g.Actor.center.y)>1.32f)return false;
        if(TryFlowNeedle(hero,state,age,null,true))return true;
        ReadHitboxes(hero);
        AddFlailForecast(hero.transform.position);
        PredictTelegraph(state,age,g.Target,g.Actor,-edge*gap);
        string risk;
        string inward=edge>0f?"L":"R";
        float dash=hero.DASH_SPEED;
        float projectedGap=gap+Mathf.Clamp(-edge*g.TargetVelocity.x*.16f,-2.6f,2.6f);
        if(transition&&hero.CanDash()&&frame-raceDash>25&&!previousRaceKeys.Contains("S")&&
           projectedGap>=6.9f&&Forecast(g.Actor,g.HeroVelocity,g.Floor,-edge*dash,0,0,true,false,out risk,12)<.9f)
        {
            raceDash=frame;
            RacePress(inward+"S",5);
            Note("P3_INWARD_DASH "+raceName+" "+(frame-raceStart)+" gap="+
                gap.ToString("F2",CultureInfo.InvariantCulture));
            return true;
        }
        if(windup&&gap<=8.1f&&age<=8&&hero.CanJump()&&frame-raceJump>13&&
           !previousRaceKeys.Contains("J")&&Forecast(g.Actor,g.HeroVelocity,g.Floor,-edge*hero.GetWalkSpeed(),
               hero.JUMP_SPEED,12,false,false,out risk,14)<.80f)
        {
            raceJump=frame;
            RacePress(inward+"J",9);
            Note("P3_INWARD_HOP "+raceName+" "+(frame-raceStart)+" gap="+
                gap.ToString("F2",CultureInfo.InvariantCulture));
            return true;
        }
        if(Forecast(g.Actor,g.HeroVelocity,g.Floor,-edge*hero.GetWalkSpeed(),0,0,false,false,out risk,9)>.82f)
            return false;
        keys=inward;
        return true;
    }

    private void TryLastOpening(HeroController hero)
    {
        if(!hero||!hero.CanInput()||keys.Contains("A")||keys.Contains("C")||keys.Contains("H")||
           hero.cState.dashing||frame<raceHoldUntil-1&&keys.Contains("S"))return;
        int age=frame-raceStateSince;
        string state=raceState;
        bool whipTail=state=="Tendril G Whip"&&age>=30&&!hero.cState.onGround;
        bool recovery=state=="Land"||state=="Evade Recover"||state=="Charge Recover"||
            state=="Dive Out G"||state=="Downstab Land"||state=="Counter End"||
            state=="Stun Air"||state=="Stunned"||state=="Tendril A End"||state=="Tendril G Whip End"||
            raceBuild.Contains("spitcut")&&boss.hp<900&&(state=="Vomit Cooldown"||state=="Fall");
        if(!whipTail&&!recovery)return;
        NeedleGeometry g;
        if(!ReadNeedleGeometry(hero,out g))return;
        float dx=g.Target.center.x-g.Actor.center.x,dy=g.Target.center.y-g.Actor.center.y;
        if(Mathf.Abs(dx)>4.75f||Mathf.Abs(dy)>4.9f)return;
        if(keys.Contains("J")&&g.Ground&&previousRaceKeys.IndexOf('J')<0)return;
        if(frame<summonUntil)return;
        if(TryFlowNeedle(hero,state,age,null,true))
            Note("PRESSURE_LATE_NEEDLE "+raceName+" "+(frame-raceStart)+" "+state+" "+keys);
    }

    private static bool PressurePreparation(string state,int age)
    {
        return state=="J Slash M Antic"&&age<=21||state=="Downstab Antic"&&age<=13||
               state=="Tendril G Antic"&&age<=24||state=="Crossup Antic"&&age<=17||
               state=="Charge Break"&&age<=15;
    }

    private bool TryLandRecoilDash(HeroController hero,string state,int age)
    {
        if(hero.cState.dashing||!hero.CanDash()||frame-raceDash<25||previousRaceKeys.Contains("S")||
           boss.IsInvincible||Mathf.Abs((float)NativeAttackCooldown.GetValue(hero)-.14f)>.08f)return false;
        NeedleGeometry g;
        if(!ReadNeedleGeometry(hero,out g))return false;
        float dx=g.Target.center.x-g.Actor.center.x;
        float gap=Mathf.Abs(dx), side=Mathf.Sign(dx);
        if(gap<6.05f||gap>8.4f||Mathf.Abs(g.Target.center.y-g.Actor.center.y)>.72f||
           gap-hero.DASH_SPEED*.157f<g.Target.extents.x+g.Actor.extents.x+.18f)return false;
        ReadHitboxes(hero);AddFlailForecast(hero.transform.position);
        PredictTelegraph(state,age,g.Target,g.Actor,dx);
        string threat;
        if(Forecast(g.Actor,g.HeroVelocity,g.Floor,side*hero.DASH_SPEED,0f,0,true,false,out threat,12)>.88f)return false;
        raceDash=frame;RacePress((dx<0?"L":"R")+"S",4);
        Note("LAND_RECOIL_DASH "+raceName+" "+(frame-raceStart)+" "+state+" gap="+gap.ToString("F2",CultureInfo.InvariantCulture));
        return true;
    }
    private bool TryOpportunityPressure(HeroController hero,string state,int age)
    {
        if(hero.cState.dashing||!hero.CanInput()||frame<raceHoldUntil&&raceHeld.Length>1)return false;
        bool stagger=state=="Stun Air"||state=="Stunned";
        bool column=state=="P2 Shift 1"||state=="P2 Shift 2"&&NeedleWindowAge(state,age)<=31;
        bool opening=PressurePreparation(state,age);
        bool recover=state=="Counter End"||state=="Land"||state=="Dive Out G"||
             state=="Downstab Land"||state=="Charge Recover"||state=="Evade Recover"||
             state=="Tendril G Whip End"||state=="Tendril A End"||
             state=="Bullet S Ground 1"||state=="Bullet S Ground 2";
        bool whip=state=="Tendril G Whip"&&age>=22;
        bool bulletLane=raceBuild.Contains("latecling")&&boss.hp<900&&
            (state=="Bullet S Ground 1"||state=="Bullet S Ground 2");
        if(raceBuild.Contains("staggeronly")&&!stagger&&!column&&!bulletLane)return false;
        if(!stagger&&!column&&!opening&&!recover&&!whip)return false;

        NeedleGeometry g;
        if(!ReadNeedleGeometry(hero,out g))return false;
        float dx=g.Target.center.x-g.Actor.center.x;
        float dy=g.Target.center.y-g.Actor.center.y;
        float gap=Mathf.Abs(dx);
        if(gap>15.3f||Mathf.Abs(dy)>9.0f)return false;

        if(TryFlowNeedle(hero,state,age,null,true))return true;
        if(!g.Ground||dy>1.06f)
        {
            if(g.Ground&&stagger&&dy>1.12f&&gap<4.25f&&hero.CanJump()&&frame-raceJump>11&&
               !previousRaceKeys.Contains("J"))
            {
                ReadHitboxes(hero);
                AddFlailForecast(hero.transform.position);
                string threat;
                float direction=gap>1.6f?Mathf.Sign(dx):0f;
                if(Forecast(g.Actor,g.HeroVelocity,g.Floor,direction*hero.GetWalkSpeed(),
                            hero.JUMP_SPEED,4,false,false,out threat,10)<1f)
                {
                    raceJump=frame;
                    RacePress((direction<0f?"L":direction>0f?"R":"")+"J",4);
                    Note("PRESSURE_STAGGER_RISE "+raceName+" "+(frame-raceStart)+" "+state);
                    return true;
                }
            }
            if(!column&&TryWanderApproach(hero,state,age,g.Ground))return true;
            return false;
        }

        if(dy< -1.58f)return false;
        float side=Mathf.Sign(dx);
        float demand=bulletLane&&raceBuild.Contains("bulletclose")?1.95f:opening?3.45f:column?2.85f:2.75f;
        float projected=gap+Mathf.Clamp(side*g.TargetVelocity.x*.14f,-2.7f,2.7f);
        float minimum=g.Actor.extents.x+g.Target.extents.x+.65f;
        if(projected<minimum||gap<minimum)return false;
        if(column&&state=="P2 Shift 2"&&age>40)return false;

        ReadHitboxes(hero);
        AddFlailForecast(hero.transform.position);
        PredictTelegraph(state,age,g.Target,g.Actor,dx);
        string hazard;
        string toward=side<0f?"L":"R";
        float stride=hero.DASH_SPEED*.157f;
        bool pursueRecoil=false;
        if(raceBuild.Contains("bulletpunish")&&branchAfterRoar&&state=="Bullet S Ground 1"&&
           age>=22&&age<=70&&gap>=6.15f&&gap<=7.55f&&Mathf.Abs(dy)<.7f)
        {
            float remaining=(float)NativeAttackCooldown.GetValue(hero);
            // Native dash ends just as the previous fire needle recovers. Leave body clearance.
            pursueRecoil=remaining>=.065f&&remaining<=.205f&&Mathf.Abs(g.TargetVelocity.x)<5f&&
                         gap-stride>g.Target.extents.x+g.Actor.extents.x+.22f;
        }
        if(!whip&&(pursueRecoil||projected>=stride+demand+.29f)&&
           hero.CanDash()&&frame-raceDash>24&&!previousRaceKeys.Contains("S")&&
           (!opening||age<=10)&&state!="Counter End"&&
           Forecast(g.Actor,g.HeroVelocity,g.Floor,side*hero.DASH_SPEED,0f,0,true,false,out hazard,12)<.88f)
        {
            raceDash=frame;
            RacePress(toward+"S",4);
            Note((pursueRecoil?"BULLET_RECOIL_DASH ":"PRESSURE_WINDOW_DASH ")+raceName+" "+
                 (frame-raceStart)+" "+state+" input="+keys+" gap="+
                 gap.ToString("F2",CultureInfo.InvariantCulture));
            return true;
        }
        float speed=projected>demand+.17f&&gap>minimum+.42f?side*hero.GetWalkSpeed():0f;
        if(whip&&speed==0f||opening&&speed==0f&&age>13)return false;
        if(Forecast(g.Actor,g.HeroVelocity,g.Floor,speed,0f,0,false,false,out hazard,12)>.90f)
            return false;
        keys=speed==0f?"":toward;
        return true;
    }

    private bool TryClawChase(HeroController hero,string state,int age)
    {
        bool stagger=state=="Stun Air"||state=="Stunned";
        bool recoil=state=="Evade Recover"||state=="Evade"&&age>=8||
                    state=="P2 Shift 1"&&age<15;
        bool recovery=state=="Charge Recover"||state=="Dive Out G"||state=="Downstab Land"||
                      state=="Land"||state=="Tendril G Whip End"||
                      state=="Bullet S Ground 1"||state=="Bullet S Ground 2";
        if(!stagger&&!recoil&&(!recovery||raceBuild.Contains("hookstun")))return false;
        if(raceBuild.Contains("savedsilkchase")&&boss.hp>=1350)return false;
        if(!hero.CanInput()||hero.cState.dashing||!hero.CanHarpoonDash()||
           frame-raceClaw<27||PlayerData.instance.silk<1||frame<summonUntil||
           previousRaceKeys.Contains("H"))return false;
        if(raceHoldUntil>frame&&raceHeld.Length!=0&&raceHeld!="L"&&raceHeld!="R")return false;

        NeedleGeometry g;
        if(!ReadNeedleGeometry(hero,out g))return false;
        float dx=g.Target.center.x-g.Actor.center.x;
        float dy=g.Target.center.y-g.Actor.center.y;
        float side=Mathf.Sign(dx);
        float gap=Mathf.Abs(dx);
        if(branchAfterRoar&&raceBuild.Contains("shortfeet")&&gap<11.5f)return false;
        float t=(4f+.49f*gap)/60f;
        float futureX=g.Target.center.x+g.TargetVelocity.x*t;
        float projectedGap=(futureX-g.Actor.center.x)*side;
        float projectedY=dy+g.TargetVelocity.y*t;
        float max=raceBuild.Contains("hookwide")?13.5f:11.65f;
        if(gap<6.35f||projectedGap<5.9f||projectedGap>max||
           Mathf.Abs(projectedY)>1.05f||Mathf.Abs(dy)>1.65f||
           futureX<7f||futureX>51.8f)return false;

        float cooldown=(float)NativeAttackCooldown.GetValue(hero)*60f;
        float walkSpeed=hero.GetWalkSpeed();
        float directSpeed=Mathf.Max(2f,walkSpeed-side*g.TargetVelocity.x);
        float direct=(gap-3.7f)*60f/directSpeed;
        if(hero.CanDash())
        {
            float dashSpeed=hero.DASH_SPEED-side*g.TargetVelocity.x;
            float dashRemaining=gap-3.7f-dashSpeed*.16f;
            if(dashRemaining<=0f)direct=Mathf.Min(direct,(gap-3.7f)*60f/dashSpeed+4f);
            else direct=Mathf.Min(direct,11f+dashRemaining*60f/directSpeed);
        }
        float seize=t*60f+4f;
        if(raceBuild.Contains("savedsilkchase"))seize+=7f;
        if(Mathf.Max(direct,cooldown)<seize)return false;

        ReadHitboxes(hero);
        AddFlailForecast(hero.transform.position);
        string hazard;
        if(Forecast(g.Actor,g.HeroVelocity,g.Floor,side*hero.DASH_SPEED,0f,0,true,!g.Ground,
           out hazard,14)>1.1f)return false;

        raceClaw=frame;
        RacePress((side<0f?"L":"R")+"H",2);
        Note("HOOK_CHASE "+raceName+" "+(frame-raceStart)+" "+state+" gap="+
            gap.ToString("F2",CultureInfo.InvariantCulture)+" approach="+
            direct.ToString("F1",CultureInfo.InvariantCulture));
        return true;
    }

    private bool TryPressureClaw(HeroController hero,string state,int age)
    {
        bool extended=raceBuild.Contains("hookdrop")&&
             (state=="Bullet S Ground 1"||state=="Bullet S Ground 2"||state=="Dive Out G"&&age<=9);
        if(age>15||frame-raceClaw<50||!hero.CanHarpoonDash()||
           PlayerData.instance.silk<(raceBuild.Contains("hookdrop")&&boss.hp<900?10:1)||frame<summonUntil||
           !extended&&(state!="Stunned"&&state!="Downstab Land"&&state!="Charge Recover"&&state!="Evade Recover"))
            return false;
        if(raceHoldUntil>frame&&raceHeld.Length!=0&&raceHeld!="L"&&raceHeld!="R")return false;
        NeedleGeometry g;
        if(!ReadNeedleGeometry(hero,out g))return false;
        float dx=g.Target.center.x-g.Actor.center.x;
        float side=Mathf.Sign(dx);
        float gap=Mathf.Abs(dx);
        float projectedY=g.Target.center.y-g.Actor.center.y+g.TargetVelocity.y*.12f;
        if(gap<6.0f||gap>10.6f||Mathf.Abs(projectedY)>1.0f||
           Mathf.Abs(g.TargetVelocity.x)>7.5f||
           g.Target.center.x<7.1f||g.Target.center.x>50.8f)return false;
        ReadHitboxes(hero);
        AddFlailForecast(hero.transform.position);
        string hazard;
        if(Forecast(g.Actor,g.HeroVelocity,g.Floor,side*hero.DASH_SPEED,0f,0,true,!g.Ground,
           out hazard,12)>1.4f)return false;
        raceClaw=frame;
        RacePress((side<0?"L":"R")+"H",2);
        Note("PRESSURE_CLAW "+raceName+" "+(frame-raceStart)+" "+state+" gap="+
             gap.ToString("F2",CultureInfo.InvariantCulture));
        return true;
    }

    private bool TryLateComboLane(HeroController hero,int age,bool ground)
    {
        if(age>10||hero.cState.dashing||hero.cState.invulnerable)return false;
        NeedleGeometry g;
        if(!ReadNeedleGeometry(hero,out g))return false;
        float dx=g.Target.center.x-g.Actor.center.x;
        float gap=Mathf.Abs(dx);
        float outward=-Mathf.Sign(dx);
        float closing=g.TargetVelocity.x*outward;
        if(gap<2.20f||gap>8.2f||closing<6.0f)return false;
        int framesToStrike=Math.Max(3,13-age);
        float dt=framesToStrike/60f;
        float futureGap=gap-closing*dt+outward*g.HeroVelocity.x*dt*.45f;
        float relativeBottom=g.Actor.min.y-g.Target.center.y;
        float futureBottom=relativeBottom+g.HeroVelocity.y*dt-(ground?0f:28.5f*dt*dt);
        if(futureGap>5.45f||futureBottom>1.75f)return false;
        if(hero.CanAttack()&&gap<4.55f&&relativeBottom<.55f&&framesToStrike>=11)
        {
            float overlap;
            ReadAttackEnvelope(hero,g,out overlap);
            if(overlap>=.15f)return false;
        }
        string away=outward>0?"R":"L";
        string toward=outward>0?"L":"R";
        if(!ground&&hero.CanDoubleJump(false)&&frame-raceDouble>23&&frame-raceJump>13&&framesToStrike>=5)
        {
            raceDouble=frame;
            RacePress("J",10);
            Note("FLOW_CHAIN_LIFT "+raceName+" "+(frame-raceStart)+" age="+age+" gap="+
                 gap.ToString("F2",CultureInfo.InvariantCulture));
            return true;
        }
        float room=outward>0f?54.69f-g.HeroX:g.HeroX-3.73f;
        if(!hero.CanDash()||frame-raceDash<=23)return false;
        bool over=raceBuild.Contains("chainover")&&!ground&&relativeBottom>2.15f&&gap<6.8f&&
                  g.HeroX+(outward>0?-5.4f:5.4f)>4.7f&&g.HeroX+(outward>0?-5.4f:5.4f)<53.7f;
        if(!over&&room<4.4f)return false;
        raceDash=frame;
        RacePress((over?toward:away)+"S",6);
        Note("FLOW_CHAIN_DASH "+raceName+" "+(frame-raceStart)+" "+keys+" age="+age+" projectedGap="+
             futureGap.ToString("F2",CultureInfo.InvariantCulture)+" over="+over);
        return true;
    }

    private bool TryAirWhipFlank(HeroController hero,string state,int age,bool ground)
    {
        if(airWhipFacing==0f||hero.cState.dashing||boss.IsInvincible)return false;
        bool winding=state=="Tendril A Antic";
        NeedleGeometry g;
        if(!ReadNeedleGeometry(hero,out g))return false;
        float dx=g.Target.center.x-g.Actor.center.x,dy=g.Target.center.y-g.Actor.center.y;
        float front=-airWhipFacing*dx;
        float flank=front+2.16f;
        float desiredX=g.Actor.center.x-airWhipFacing*flank;
        if(desiredX<4.6f||desiredX>53.9f||Mathf.Abs(dx)>9.5f||dy<2.15f||dy>8.35f)return false;
        bool set=front<=-1.6f;
        if(set&&TryFlowNeedle(hero,state,age,null,true))return true;
        ReadHitboxes(hero);
        AddFlailForecast(hero.transform.position);
        string hazard;
        float side=-Mathf.Sign(flank)*airWhipFacing;
        string move=Mathf.Abs(flank)>.40f?(side>0f?"R":"L"):"";
        float vx=move.Length>0?side*hero.GetWalkSpeed():0f;
        if(!set)
        {
            if(g.Actor.max.y+.54f>g.Target.min.y||!winding&&age>6)return false;
            float stride=hero.DASH_SPEED*.17f;
            if(front>=1.95f&&front+1.45f<stride&&winding&&age<=20&&hero.CanDash()&&
               frame-raceDash>25&&!previousRaceKeys.Contains("S")&&
               Forecast(g.Actor,g.HeroVelocity,g.Floor,side*hero.DASH_SPEED,0f,0,true,!ground,out hazard,12)<1.05f)
            {
                raceDash=frame;
                RacePress(move+"S",5);
                Note("AIR_WHIP_FLANK_DASH "+raceName+" "+(frame-raceStart)+" dir="+move+
                     " x="+front.ToString("F2",CultureInfo.InvariantCulture));
                return true;
            }
            if(Forecast(g.Actor,g.HeroVelocity,g.Floor,vx,0f,0,false,!ground,out hazard,12)>1.05f)return false;
            keys=move;
            return true;
        }
        if(ground&&dy>3.4f&&hero.CanJump()&&!previousRaceKeys.Contains("J")&&
           (!winding||age>=8)&&Forecast(g.Actor,g.HeroVelocity,g.Floor,vx,hero.JUMP_SPEED,
             8,false,false,out hazard,11)<1.05f)
        {
            raceJump=frame;
            RacePress(move+"J",5);
            Note("AIR_WHIP_FLANK_JUMP "+raceName+" "+(frame-raceStart)+" state="+state+
                 " x="+front.ToString("F2",CultureInfo.InvariantCulture));
            return true;
        }
        if(Forecast(g.Actor,g.HeroVelocity,g.Floor,vx,0f,0,false,!ground,out hazard,10)>1.05f)return false;
        keys=move+(!ground&&g.HeroVelocity.y>0f&&previousRaceKeys.Contains("J")&&frame-raceJump<=9?"J":"");
        return true;
    }

    private bool TryHookDrop(HeroController hero,string state)
    {
        int since=frame-lastHookImpact;
        if(since<3||since>19||hero.cState.dashing||!hero.CanDash()||
           previousRaceKeys.Contains("S")||state.StartsWith("P2 Shift")||state.StartsWith("P4 Shift"))
            return false;
        NeedleGeometry g;
        if(!ReadNeedleGeometry(hero,out g))return false;
        float dx=g.Target.center.x-g.Actor.center.x,dy=g.Target.center.y-g.Actor.center.y;
        float gap=Mathf.Abs(dx);
        if(gap<g.Actor.extents.x+g.Target.extents.x+.62f||gap>6.8f||
           dy> -1.52f||dy< -5.8f)return false;
        if(TryFlowNeedle(hero,state,frame-raceStateSince,null,true))return true;
        ReadHitboxes(hero);
        AddFlailForecast(hero.transform.position);
        string hazard;
        if(Forecast(g.Actor,new Vector2(0f,-32f),g.Floor,0f,0f,0,false,true,out hazard,10)>1.05f)
            return false;
        raceDash=frame;
        RacePress("DS",4);
        Note("HOOK_DROP "+raceName+" "+(frame-raceStart)+" since="+since+" cd="+
             ((float)NativeAttackCooldown.GetValue(hero)).ToString("F3",CultureInfo.InvariantCulture));
        return true;
    }

    private bool TryFastDrop(HeroController hero,string state,int age)
    {
        bool landing=state=="Tendril G Whip End"||state=="Tendril A End"||
           state=="Charge Recover"||state=="Downstab Land"&&age>=3||
           state=="Land"||state=="Stunned"||state=="Summon Turn"&&age>=14;
        if(!landing||hero.cState.dashing||!hero.CanDash()||frame-raceDash<=25||
            previousRaceKeys.Contains("S"))return false;
        NeedleGeometry g;
        if(!ReadNeedleGeometry(hero,out g))return false;
        float dx=g.Target.center.x-g.Actor.center.x,dy=g.Target.center.y-g.Actor.center.y;
        if(Mathf.Abs(dx)<2.65f||Mathf.Abs(dx)>5.95f||dy> -2.7f||dy< -7.2f||
           g.HeroVelocity.y>3.5f||g.HeroVelocity.y< -20f)
            return false;
        if(TryFlowNeedle(hero,state,age,null,true))return true;
        ReadHitboxes(hero);
        AddFlailForecast(hero.transform.position);
        string hazard;
        if(Forecast(g.Actor,new Vector2(0f,-31f),g.Floor,0f,0f,0,false,true,out hazard,11)>.75f)
            return false;
        raceDash=frame;
        RacePress("DS",5);
        Note("FAST_DROP "+raceName+" "+(frame-raceStart)+" state="+state+
             " x="+Mathf.Abs(dx).ToString("F2",CultureInfo.InvariantCulture));
        return true;
    }

    private bool TryKeepCounterPosition(HeroController hero,string state,float dx,bool ground)
    {
        if(state!="Counter Antic"&&state!="Counter Stance"&&state!="Counter End")return false;
        if(hero.cState.dashing||!ground||Mathf.Abs(dx)<1.9f)return false;
        var hurt=hero.heroBox?hero.heroBox.GetComponent<Collider2D>():null;
        if(!hurt||!hurt.enabled)return false;
        Vector2 velocity=hero.GetComponent<Rigidbody2D>().linearVelocity;
        float side=Mathf.Sign(dx);
        float gap=Mathf.Abs(dx);
        string toward=side>0f?"R":"L";
        string away=side>0f?"L":"R";
        float walk=gap>3.50f?side*hero.GetWalkSpeed():gap<2.75f?-side*hero.GetWalkSpeed():0f;
        ReadHitboxes(hero);
        AddFlailForecast(hero.transform.position);
        string hazard;
        if(Forecast(hurt.bounds,velocity,hurt.bounds.center.y,walk,0,0,false,false,out hazard,10)>1.15f)
            return false;
        keys=gap>3.50f?toward:gap<2.75f?away:"";
        return true;
    }

    private bool TryAdvanceToNeedle(HeroController hero,string state,int age,bool ground)
    {
        if(!ground||hero.cState.dashing||boss.IsInvincible||frame-raceAttack<4)return false;
        bool whip=state=="Tendril G Antic"&&age<=15;
        bool slash=state=="J Slash M Antic"&&age<=12;
        bool combo=state=="ComboSlash 1"&&age<=12;
        bool returnFromMove=state=="Dive Out G"||state=="Downstab Land"&&age<15||
            state=="Tendril G Whip End"||state=="Evade Recover"||state=="Charge Recover"||
            state=="Land"&&!hero.cState.isSprinting||state=="Tendril A End";
        if(!whip&&!slash&&!combo&&!returnFromMove)return false;
        NeedleGeometry g;
        if(!ReadNeedleGeometry(hero,out g))return false;
        float dx=g.Target.center.x-g.Actor.center.x,dy=g.Target.center.y-g.Actor.center.y;
        float gap=Mathf.Abs(dx),side=Mathf.Sign(dx);
        if(gap>18f||gap<4.7f||Mathf.Abs(dy)>1.38f)return false;
        if(TryFlowNeedle(hero,state,age,null,true))return true;
        ReadHitboxes(hero);
        AddFlailForecast(hero.transform.position);
        PredictTelegraph(state,age,g.Target,g.Actor,dx);
        string hazard;
        string toward=side>0f?"R":"L";
        float projected=gap+Mathf.Clamp(side*g.TargetVelocity.x*.16f,-2.7f,2.7f);
        float stride=hero.DASH_SPEED*.17f;
        if(projected>stride+(returnFromMove?2.9f:3.7f)&&
           hero.CanDash()&&frame-raceDash>25&&!previousRaceKeys.Contains("S")&&
           Forecast(g.Actor,g.HeroVelocity,g.Floor,side*hero.DASH_SPEED,0,0,true,false,out hazard,13)<1.15f)
        {
            raceDash=frame;
            RacePress(toward+"S",4);
            Note("NEEDLE_LANE_DASH "+raceName+" "+(frame-raceStart)+" "+state+" age="+age+
                 " gap="+gap.ToString("F2",CultureInfo.InvariantCulture));
            return true;
        }
        if(gap>=4.7f&&projected>3.55f&&
           Forecast(g.Actor,g.HeroVelocity,g.Floor,side*hero.GetWalkSpeed(),0,0,false,false,out hazard,10)<1.15f)
        {
            keys=toward;
            return true;
        }
        return false;
    }

    private bool TryPressureRebound(HeroController hero,string state,int age,bool ground)
    {
        if(age>27||hero.cState.dashing||boss.IsInvincible)return false;
        if(TryFlowNeedle(hero,state,age,null,true))return true;
        NeedleGeometry g;
        if(!ReadNeedleGeometry(hero,out g))return false;
        float dx=g.Target.center.x-g.Actor.center.x;
        float dy=g.Target.center.y-g.Actor.center.y;
        if(Mathf.Abs(dx)>5.6f||dy<2.3f)return false;
        ReadHitboxes(hero);
        AddFlailForecast(hero.transform.position);
        float future=dx+Mathf.Clamp(g.TargetVelocity.x*.12f,-1.05f,1.05f);
        string toward=future>0f?"R":"L";
        string move=Mathf.Abs(future)>.85f?toward:"";
        float walk=move.Length>0?Mathf.Sign(future)*hero.GetWalkSpeed():0f;
        string threat;
        float jumpReach=raceBuild.Contains("bouncemeet")?4.25f:2.8f;
        if(ground&&age<=16&&Mathf.Abs(future)<jumpReach&&hero.CanJump()&&!previousRaceKeys.Contains("J")&&
           Forecast(g.Actor,g.HeroVelocity,g.Floor,walk,hero.JUMP_SPEED,7,false,false,out threat,10)<1.15f)
        {
            raceJump=frame;
            RacePress(move+"J",5);
            Note("REBOUND_APPROACH "+raceName+" "+(frame-raceStart)+" gap="+
                 Mathf.Abs(dx).ToString("F2",CultureInfo.InvariantCulture));
            return true;
        }
        if(Forecast(g.Actor,g.HeroVelocity,g.Floor,walk,0,0,false,!ground,out threat,10)>1.0f)return false;
        keys=move+(!ground&&g.HeroVelocity.y>0f&&previousRaceKeys.Contains("J")?"J":"");
        return true;
    }

    private bool TryFinishPin(HeroController hero,string state)
    {
        if(!hero.CanHarpoonDash()||hero.cState.dashing||boss.IsInvincible||PlayerData.instance.silk<11||
           previousRaceKeys.Contains("H")||frame-raceClaw<9)return false;
        NeedleGeometry g;
        if(!ReadNeedleGeometry(hero,out g))return false;
        float dx=g.Target.center.x-g.Actor.center.x;
        float gap=Mathf.Abs(dx),dy=g.Target.center.y-g.Actor.center.y;
        if(gap<3.55f||gap>9.5f||Mathf.Abs(dy)>1.45f)return false;
        if(hero.CanAttack()&&gap<4.45f&&TryFlowNeedle(hero,state,frame-raceStateSince,null,true))return true;
        ReadHitboxes(hero);
        AddFlailForecast(hero.transform.position);
        string hazard;
        if(Forecast(g.Actor,g.HeroVelocity,g.Floor,Mathf.Sign(dx)*hero.DASH_SPEED,0,0,true,!g.Ground,out hazard,10)>1.05f)
            return false;
        raceClaw=frame;
        RacePress((dx<0?"L":"R")+"H",2);
        Note("FINISH_PIN "+raceName+" "+(frame-raceStart)+" state="+state+" hp="+boss.hp);
        return true;
    }

    private bool TryLandingCut(HeroController hero,string state)
    {
        if(hero.cState.dashing||!hero.CanDash()||frame-raceDash<=24||previousRaceKeys.Contains("S")||
           boss.IsInvincible)return false;
        NeedleGeometry g;
        if(!ReadNeedleGeometry(hero,out g))return false;
        float gap=Mathf.Abs(g.Target.center.x-g.Actor.center.x);
        float height=g.Actor.center.y-g.Target.center.y;
        if(gap<1.85f||gap>4.75f||height<3.85f||height>7.6f||g.HeroVelocity.y< -16f)return false;
        if(TryFlowNeedle(hero,state,frame-raceStateSince,null,true))return true;
        ReadHitboxes(hero);
        AddFlailForecast(hero.transform.position);
        string hazard;
        if(Forecast(g.Actor,new Vector2(0f,-31f),g.Floor,0f,0f,0,false,true,out hazard,11)>.82f)
            return false;
        raceDash=frame;
        RacePress("DS",4);
        Note("LAND_CUT "+raceName+" "+(frame-raceStart)+" state="+state);
        return true;
    }

    private bool TryStunMeet(HeroController hero,string state)
    {
        float cooldown=(float)NativeAttackCooldown.GetValue(hero);
        if(cooldown<.055f||cooldown>.255f||hero.cState.dashing||!hero.CanDash()||
           frame-raceDash<=24||previousRaceKeys.Contains("S")||boss.IsInvincible)return false;
        NeedleGeometry g;
        if(!ReadNeedleGeometry(hero,out g))return false;
        float dx=g.Target.center.x-g.Actor.center.x;
        float side=Mathf.Sign(dx), gap=Mathf.Abs(dx);
        float dy=g.Target.center.y-g.Actor.center.y;
        float future=gap+Mathf.Clamp(side*g.TargetVelocity.x*.13f,-1.0f,1.9f);
        float clearance=g.Actor.extents.x+g.Target.extents.x+.56f;
        if(gap<4.65f||gap>10.5f||dy<-.80f||dy>2.55f||future-hero.DASH_SPEED*.155f<clearance)
            return false;
        ReadHitboxes(hero);
        AddFlailForecast(hero.transform.position);
        string threat;
        if(Forecast(g.Actor,g.HeroVelocity,g.Floor,side*hero.DASH_SPEED,0,0,true,false,out threat,12)>1.02f)
            return false;
        raceDash=frame;
        RacePress((side<0?"L":"R")+"S",4);
        Note("STUN_MEET "+raceName+" "+(frame-raceStart)+" state="+state+" gap="+gap.ToString("F2",CultureInfo.InvariantCulture));
        return true;
    }

    private bool TryStunPress(HeroController hero,string state,int age,bool ground)
    {
        if(hero.cState.dashing||boss.IsInvincible)return false;
        if(TryFlowNeedle(hero,state,age,null,true))return true;
        NeedleGeometry g;
        if(!ReadNeedleGeometry(hero,out g))return false;
        float dx=g.Target.center.x-g.Actor.center.x,dy=g.Target.center.y-g.Actor.center.y;
        float side=Mathf.Sign(dx),gap=Mathf.Abs(dx);
        if(gap>13.0f||dy< -2.3f||dy>5.0f)return false;
        float near=g.Actor.extents.x+g.Target.extents.x+.68f;
        float targetGap=Mathf.Max(near,2.65f);
        string toward=side<0f?"L":"R";
        ReadHitboxes(hero);
        AddFlailForecast(hero.transform.position);
        string threat;
        float stride=hero.DASH_SPEED*.159f;
        float futureGap=gap+Mathf.Clamp(side*g.TargetVelocity.x*.16f,-2.1f,2.2f);
        if(ground&&Mathf.Abs(dy)<3.0f&&futureGap>targetGap+stride+.17f&&
           (gap>5.45f||state=="Stun Air"&&gap>4.65f)&&hero.CanDash()&&frame-raceDash>24&&
           !previousRaceKeys.Contains("S")&&
           Forecast(g.Actor,g.HeroVelocity,g.Floor,side*hero.DASH_SPEED,0,0,true,false,out threat,12)<1.35f)
        {
            raceDash=frame;
            RacePress(toward+"S",4);
            Note("STUN_PRESS_DASH "+raceName+" "+(frame-raceStart)+" gap="+
                 gap.ToString("F2",CultureInfo.InvariantCulture)+" state="+state);
            return true;
        }
        if(futureGap<near-.35f)return false;
        string move=futureGap>targetGap+.15f?toward:"";
        float walk=move.Length>0?side*hero.GetWalkSpeed():0f;
        if(Forecast(g.Actor,g.HeroVelocity,g.Floor,walk,0,0,false,!ground,out threat,10)>1.1f)return false;
        keys=move;
        return true;
    }

    private bool TryRecoverNeedlePosition(HeroController hero,string state,int age,bool ground)
    {
        if(!ground||!FlowSelection(state)||hero.cState.dashing||boss.IsInvincible)return false;
        bool stun=state=="Stunned"||state=="Stun Air";
        bool recovery=state=="Downstab Land"||state=="Evade Recover"||state=="Charge Recover"||
                      (state=="Dive Out G"&&age>=2&&age<17);
        if(!stun&&!recovery)return false;
        NeedleGeometry g;
        if(!ReadNeedleGeometry(hero,out g))return false;
        float dx=g.Target.center.x-g.Actor.center.x;
        float dy=g.Target.center.y-g.Actor.center.y;
        float gap=Mathf.Abs(dx);
        if(gap<3.15f||gap>12.3f||Mathf.Abs(dy)>1.45f)return false;
        float side=Mathf.Sign(dx);
        string toward=side>0f?"R":"L";
        ReadHitboxes(hero);
        AddFlailForecast(hero.transform.position);
        string threat;
        float future=gap+Mathf.Clamp(side*g.TargetVelocity.x*.17f,-2.4f,2.4f);
        float stride=hero.DASH_SPEED*.159f;
        if(future-stride>1.55f&&future-stride<5.35f&&gap>=5.75f&&
           hero.CanDash()&&frame-raceDash>24&&
           Forecast(g.Actor,g.HeroVelocity,g.Floor,side*hero.DASH_SPEED,0,0,true,false,out threat,12)<1.8f)
        {
            raceDash=frame;
            RacePress(toward+"S",4);
            Note("FLOW_CLOSE "+raceName+" "+(frame-raceStart)+" "+keys+" state="+state+
                 " gap="+gap.ToString("F2",CultureInfo.InvariantCulture));
            return true;
        }
        if((stun||gap<7.1f)&&Forecast(g.Actor,g.HeroVelocity,g.Floor,side*hero.GetWalkSpeed(),
            0,0,false,false,out threat,12)<1.65f)
        {
            keys=toward;
            return true;
        }
        return false;
    }

    private static bool ProvenFlowWindow(string state)
    {
        return state.StartsWith("P2 Shift",StringComparison.Ordinal)||
               state.StartsWith("P4 Shift",StringComparison.Ordinal)||
               state=="Stun Air"||state=="Stunned"||state=="Stun Start"||
               state=="Downstab Land"||state=="Charge Recover"||state=="Evade Recover"||
               state=="Land"||state=="Tendril G Whip End"||state=="Tendril A End"||
               state.StartsWith("Bullet S Air",StringComparison.Ordinal)||
               state.StartsWith("Bullet S Ground",StringComparison.Ordinal)||
               state=="Summon Antic"||state=="Summon Thwip"||state=="Summon Turn";
    }

    private static bool CanFlowClose(string state,int age)
    {
        return state=="ComboSlash 1"&&age<22||
               state=="Tendril G Antic"&&age<24||
               state=="Tendril A Antic"&&age<18||
               state=="J Slash M Antic"&&age<18||
               state=="Downstab Antic"&&age<13||
               state=="Summon Antic"||state=="Summon Thwip"||state=="Summon Turn"||
               state=="Stunned"||state=="Stun Air"||state=="Stun Recover Pause"||
               state=="Land"||state=="Downstab Land"||state=="Charge Recover"||
               state=="Evade Recover"||state.StartsWith("Bullet S Ground",StringComparison.Ordinal);
    }

    private bool TryFourthShiftPressure(HeroController hero,int age)
    {
        if(age>35)return false;
        NeedleGeometry g;
        if(!ReadNeedleGeometry(hero,out g))return false;
        if(age>=8&&GetColumnExit(hero,"P4 Shift 2",age,true,g.HeroX<30f?"R":"L",true))
            return true;
        if(TryFlowNeedle(hero,"P4 Shift 2",age,null))return true;

        float dx=g.Target.center.x-g.Actor.center.x;
        if(Mathf.Abs(dx)>7.2f||Mathf.Abs(g.Target.center.y-g.Actor.center.y)>1.4f)
            return false;
        ReadHitboxes(hero);
        float vx=Mathf.Abs(dx)>3.40f?Mathf.Sign(dx)*hero.GetWalkSpeed():0f;
        bool tracked=threats.Any(h=>h.Label.StartsWith("Lost_Lace_tendril_set",StringComparison.Ordinal));
        if(age>=11&&!tracked)return false;
        string hazard;
        if(Forecast(g.Actor,g.HeroVelocity,g.Floor,vx,0f,0,false,false,out hazard,12)>.85f)
            return false;
        keys=vx>0f?"R":vx<0f?"L":"";
        return true;
    }

    private bool TrySecondShiftLock(HeroController hero,string state,int age,float dx)
    {
        if(hero.cState.dashing||frame<raceHoldUntil&&raceHeld.Contains("S"))return false;
        float gap=Mathf.Abs(dx);
        if(gap>12.7f)return false;
        bool pressure=state=="P2 Shift 1"||age<33;
        float side=dx>=0f?-1f:1f;
        string toward=side<0f?"R":"L";
        string away=side<0f?"L":"R";
        float safeX=boss.transform.position.x+side*9.45f;
        if((safeX<4.15f||safeX>54.25f)&&state=="P2 Shift 2"&&age>10)return false;
        if(!pressure)
        {
            if(raceBuild.Contains("shiftreturn")&&age>=80&&gap>=9.45f)
            {
                Bounds actor,target;
                if(!ReadPinnedTarget(hero,out actor,out target))return false;
                ReadHitboxes(hero);
                string hazard;
                var rb=hero.GetComponent<Rigidbody2D>();
                if(rb&&Forecast(actor,rb.linearVelocity,actor.center.y,-side*hero.GetWalkSpeed(),
                    0f,0,false,false,out hazard,10)<.55f)
                {
                    keys=toward;
                    return true;
                }
            }
            return TryGroundShiftPressure(hero,age,dx,true);
        }

        if(gap>=6.25f&&age<=9&&hero.CanDash()&&frame-raceDash>24&&!previousRaceKeys.Contains("S"))
        {
            Bounds actor,target;
            var rb=hero.GetComponent<Rigidbody2D>();
            float arrive=gap-hero.DASH_SPEED*.167f;
            if(rb&&arrive>=1.9f&&arrive<=5.20f&&ReadPinnedTarget(hero,out actor,out target))
            {
                ReadHitboxes(hero);
                string hazard;
                if(Forecast(actor,rb.linearVelocity,actor.center.y,-side*hero.DASH_SPEED,
                    0f,0,true,false,out hazard,10)<1.35f)
                {
                    raceDash=frame;
                    RacePress(toward+"S",5);
                    Note("SHIFT_LOCK_CLOSE "+raceName+" "+(frame-raceStart)+" age="+age+" state="+state);
                    return true;
                }
            }
        }
        keys=gap>3.38f?toward:gap<2.16f?away:"";
        return true;
    }

    private bool TryGroundShiftPressure(HeroController hero,int age,float dx,bool ground)
    {
        if(!ground||age<33||age>79||Mathf.Abs(dx)>11.7f)return false;
        float side=dx>=0f?-1f:1f;
        float safeX=boss.transform.position.x+side*9.45f;
        if(safeX<4.15f||safeX>54.25f)return false;
        float gap=Mathf.Abs(dx);
        string exit=side<0f?"L":"R";
        string toward=side<0f?"R":"L";
        if(gap>=9.45f)
        {
            keys="";
            return true;
        }
        bool tight=raceBuild.Contains("phasebudget");
        int dashBy=GroundShiftDashDeadline(hero,gap);
        if(age>=dashBy-(tight?0:1))
        {
            if(age<=70&&gap<8.92f&&hero.CanDash()&&frame-raceDash>23)
            {
                raceDash=frame;
                RacePress(exit+"S",5);
                Note("FLOW_SHIFT_EXIT "+raceName+" "+(frame-raceStart)+" age="+age+" gap="+
                     gap.ToString("F2",CultureInfo.InvariantCulture)+" input="+keys);
            }
            else keys=exit;
            return true;
        }
        if(tight)
        {
            float cd=(float)NativeAttackCooldown.GetValue(hero);
            int ready=Mathf.CeilToInt(Mathf.Max(0f,cd)*60f);
            int lockFrames=Mathf.CeilToInt(hero.Config.AttackRecoveryTime*60f);
            bool nextCut=age+ready+lockFrames<=dashBy;
            keys=nextCut?(gap>3.12f?toward:gap<2.03f?exit:""):(age>=dashBy-4?exit:"");
        }
        else keys=age<=dashBy-4&&gap>4.03f?toward:"";
        return true;
    }

    private int GroundShiftDashDeadline(HeroController hero,float gap)
    {
        bool tight=raceBuild.Contains("phasebudget");
        int dashFrames=Mathf.CeilToInt(60f*Mathf.Max(0f,9.45f-gap)/
            Mathf.Max(14f,hero.DASH_SPEED*(tight?.96f:.84f)));
        return 64-dashFrames-(tight?2:4);
    }

    private int NeedleWindowAge(string state,int inputAge)
    {
        bool useNative=state=="P2 Shift 2"&&raceBuild.Contains("shiftclock")||
            raceBuild.Contains("tailclock")&&boss&&boss.hp<900&&
                (state=="ComboSlash 1"||state=="Vomit Start"||state=="Vomit Fire"||state=="J Slash M Antic");
        return useNative?Mathf.FloorToInt(raceStateNativeAge+.05f):inputAge;
    }

    private bool TryFlowNeedle(HeroController hero,string state,int age,string movement,bool tempo=false)
    {
        age=NeedleWindowAge(state,age);
        if(!hero||!hero.CanInput()||hero.cState.dashing||frame-raceAttack<=2||
           raceBuild.Contains("counterready")&&CounterReadyForNeedle()||previousRaceKeys.Contains("A")||state=="Counter Stance"||state=="Counter Hit"||
           state=="Counter Antic"&&age>=2||state.Contains("TeleOut")||
           state.Contains("Wave")||state=="Stomp Antic"||state=="Do CrossSlash"||
           state=="CS Slam"||state=="P4 Shift 2"&&age>(raceBuild.Contains("columnfour")?20:10))return false;
        bool joining=movement!=null;
        bool earlyWindup=raceBuild.Contains("windupcut")&&
            (state=="ComboSlash 1"&&age>=5&&age<=17||state=="Downstab Antic"&&age<10||
            raceBuild.Contains("windupmore")&&
              (state=="Charge Break"&&age<10||state=="Tendril G Antic"&&age<=14||
               state=="Crossup Antic"&&age<=9));
        bool freeCut=raceBuild.Contains("needleall")&&!joining&&!state.Contains("Counter")&&
            !state.Contains("Dive In")&&!state.StartsWith("CS ",StringComparison.Ordinal)&&
            !state.Contains("RapidSlash")&&!state.StartsWith("Multihit",StringComparison.Ordinal)&&
            state!="ComboSlash 2"&&state!="ComboSlash 3"&&state!="ComboSlash 4"&&state!="Charge Strike";
        bool measuredOpening=raceBuild.Contains("windowpick")&&
            (PressurePreparation(state,age)||state=="Tendril G Whip"&&age>=22||
             state=="Counter End"||state=="Tendril A End"||state=="Charge Recover");
        measuredOpening|=raceBuild.Contains("strikefirst")&&
             (state=="Tendril G Whip"&&age>=30&&!hero.cState.onGround||
              state=="Counter End"||state=="Tendril A End"||state=="Charge Recover"||
              state=="Stun Air");
        measuredOpening|=raceBuild.Contains("latecling")&&boss.hp<900&&state=="Counter End";
        measuredOpening|=raceBuild.Contains("spitcut")&&boss.hp<900&&
                         (state=="Vomit Cooldown"||state=="Fall");
        bool tempoWindow=tempo&&
            (measuredOpening||freeCut||state=="Idle"||state=="Hop"||state=="Hop Cancel"||state=="Land"||state=="Downstab Land"||
             state=="Dive Out G"||state=="Dive Out A"||state=="Stunned"||state=="Evade Recover"||
             state=="Summon Turn"||state=="Summon Thwip"||state=="Tendril A Whip"&&age>=7||
             state=="Crossup Antic"&&age<10||state=="ComboSlash 1"&&age<=17||
             state=="Charge Break"&&age<=12||state=="Tendril G Antic"&&age<=16||
             raceBuild.Contains("groundmoulinet")&&state=="Slam"&&age>=8||
             raceBuild.Contains("whipflank")&&state=="Tendril A Antic"&&age<=24||
             raceBuild.Contains("reboundcut")&&(state=="Bounce Back ToDive"||state=="Bounce Back")&&age<=27||
             raceBuild.Contains("stunpress")&&state=="Stun Air");
        if(!tempoWindow&&(!FlowSelection(state)||flowMeasured&&!ProvenFlowWindow(state)&&!earlyWindup))return false;
        if(joining&&(movement.IndexOf('S')>=0||movement.IndexOf('C')>=0||movement.IndexOf('H')>=0))
            return false;
        float cooldown=(float)NativeAttackCooldown.GetValue(hero);
        bool buffer=!hero.CanAttack();
        if(buffer&&(cooldown<=0f||cooldown>.031f||frame-raceAttack<12||joining))return false;
        NeedleGeometry g;
        if(!ReadNeedleGeometry(hero,out g))return false;
        float dx=g.Target.center.x-g.Actor.center.x,dy=g.Target.center.y-g.Actor.center.y;
        if(Mathf.Abs(dx)>6.8f||Mathf.Abs(dy)>6.1f)return false;
        if(earlyWindup&&g.Ground&&Mathf.Abs(dy)>2.5f)return false;
        bool phase=state=="P2 Shift 2";
        bool fourth=state=="P4 Shift 2";
        bool phaseClamp=phase&&raceBuild.Contains("phaseclamp");
        if(phaseClamp&&age<28&&Mathf.Abs(dx)>3.77f)return false;
        int phaseLock=raceBuild.Contains("phasebudget")?Mathf.CeilToInt(hero.Config.AttackRecoveryTime*60f):4;
        if(phase&&age>37&&g.Ground&&
           (!flowPhase||age+(buffer?1:0)+phaseLock>GroundShiftDashDeadline(hero,Mathf.Abs(dx))))
            return false;
        if(phase&&age>=63&&g.Actor.min.y<g.Target.center.y+4.45f&&Mathf.Abs(dx)<9.0f)return false;
        g.InputDelay=buffer?1:0;
        ReadHitboxes(hero);
        AddFlailForecast(hero.transform.position);
        bool seenColumn=threats.Any(h=>h.Label.StartsWith("Lost_Lace_tendril_set",StringComparison.Ordinal));
        if(fourth&&age>10&&(!g.Ground||!seenColumn))return false;
        int count=threats.Count;
        if(!threats.Any(h=>h.Label=="telegraph:"+state))
            PredictTelegraph(state,age,g.Target,g.Actor,dx);
        bool rise=joining&&movement.Contains("J")&&!previousRaceKeys.Contains("J")&&
            (g.Ground?hero.CanJump():hero.CanDoubleJump(false));
        bool carry=(joining&&movement.Contains("J"))||(!g.Ground&&g.HeroVelocity.y>0&&previousRaceKeys.Contains("J"));
        int fixedDirection=joining?(movement.Contains("L")?-1:movement.Contains("R")?1:0):0;
        float best=float.NegativeInfinity, bestHit=-20f, bestRisk=0f;
        string answer="";
        float speed=hero.GetWalkSpeed();
        for(int dir=-1;dir<=1;dir++)
        {
            if(joining&&dir!=fixedDirection)continue;
            if(g.Actor.center.x<=4.4f&&dir<0||g.Actor.center.x>=54.0f&&dir>0)continue;
            bool facing=dir==0?g.FacingRight:dir>0;
            float walk=dir*speed;
            float jump=rise?(g.Ground?hero.JUMP_SPEED:17.2f):0f;
            string hazard;
            float risk=Forecast(g.Actor,g.HeroVelocity,g.Floor,walk,jump,carry?12:0,
                false,!g.Ground,out hazard,fourth&&age>10?12:tempoWindow?10:joining?7:buffer?9:8);
            if(!hero.cState.invulnerable&&risk>(fourth&&age>10?.85f:freeCut?.92f:tempoWindow?1.55f:flowMeasured?2.65f:flowStrict?2.9f:4.1f))
                continue;
            var limb=g;
            if(rise)limb.HeroVelocity.y=jump*.82f;
            for(int family=0;family<3;family++)
            {
                if(family==2&&g.Ground)continue;
                var shape=NeedleShape(hero,g,family,facing);
                float hit=NeedleContact(limb,shape,walk,carry);
                float limit=family==0?(g.Ground?(phaseClamp?.13f:-.18f):.14f):.06f;
                bool lowWhip=raceBuild.Contains("strikefirst")&&state=="Tendril G Whip"&&age>=30&&family==2;
                if(lowWhip)limit=.02f;
                if(hit<limit||buffer&&hit<.23f||joining&&hit<.1f||
                   tempoWindow&&hit<(lowWhip?.02f:freeCut?.23f:.18f))continue;
                bool chaseStun=raceBuild.Contains("stunpress")&&state=="Stunned"&&Mathf.Abs(dx)>3.25f;
                float score=hit-(hero.cState.invulnerable?0f:.32f*risk)
                    +(dir==0?.12f:dir*dx>0?(chaseStun?.42f:.07f):0f)+(family==1&&dy>1.25f?.14f:0f);
                if(score<=best)continue;
                best=score;bestHit=hit;bestRisk=risk;
                answer=(dir<0?"L":dir>0?"R":"")+(family==1?"U":family==2?"D":"")+"A";
            }
        }
        if(threats.Count>count)threats.RemoveRange(count,threats.Count-count);
        if(answer.Length==0)return false;
        if(carry)answer+="J";
        if(joining)
        {
            // Keep the selected jump and its fixed movement interval.
            keys=answer;
        }
        else
        {
            RacePress(answer,buffer?3:1);
            if(carry&&!keys.Contains("J"))keys+="J";
        }
        raceAttack=pressureAttackFrame=flowSwingAt=frame;
        flowSwingInPhase=phase;
        ++pressureSwings;
        ++flowSwings;
        if(phase)++flowPhaseSwings;
        if(joining&&movement.Contains("J"))++flowJumpAttack;
        if(tempoWindow)Note("TEMPO_CUT "+raceName+" "+(frame-raceStart)+" "+keys+" state="+state);
        Note("FLOW_NAIL "+raceName+" "+(frame-raceStart)+" "+keys+" age="+age+
             " overlap="+bestHit.ToString("F2",CultureInfo.InvariantCulture)+
             " danger="+bestRisk.ToString("F2",CultureInfo.InvariantCulture)+
             " combined="+joining+" state="+state);
        if(phase&&raceBuild.Contains("shiftclock"))
            Note("SHIFT_NATIVE_CUT "+raceName+" "+(frame-raceStart)+" age="+age+
                 " inputAge="+(frame-raceStateSince));
        return true;
    }

    private void AlignReturnPogo(HeroController hero)
    {
        // Keep the defensive down-cut against the blade in ComboSlash 2.
        // The next crossing is short enough that swinging above the body consumes it.
        bool close=raceBuild.Contains("pogoclose")&&raceState=="ComboSlash 1";
        if(!close&&raceState!="ComboSlash 5")return;
        if(!hero||!boss||boss.IsInvincible||!hero.CanInput()||hero.cState.onGround||
           hero.cState.dashing||keys.Contains("S")||keys.Contains("C")||keys.Contains("H"))return;
        NeedleGeometry g;
        if(!ReadNeedleGeometry(hero,out g))return;
        float dx=g.Target.center.x-g.Actor.center.x;
        float height=g.Actor.center.y-g.Target.center.y;
        int toward=dx<0f?-1:1;
        bool hold=keys.Contains("J");
        int side=keys.Contains("L")?-1:keys.Contains("R")?1:0;
        if(close)
        {
            if(side==toward||keys.Contains("A")||Mathf.Abs(dx)<4.35f||Mathf.Abs(dx)>7.8f||
               height<1.25f||height>3.9f)return;
            ReadHitboxes(hero);
            AddFlailForecast(hero.transform.position);
            string cause;
            float risk=Forecast(g.Actor,g.HeroVelocity,g.Floor,toward*hero.GetWalkSpeed(),0f,
                hold?8:0,false,true,out cause,10);
            if(risk>1.1f)return;
            keys=(toward<0?"L":"R")+keys.Replace("L","").Replace("R","");
            Note("POGO_ALIGN_DRIFT "+raceName+" "+(frame-raceStart)+" "+keys);
            return;
        }
        if(height<1.4f||height>5.6f||Mathf.Abs(dx)>4.5f||!hero.CanAttack()||
           previousRaceKeys.Contains("A")||keys.Contains("U"))return;
        // Other valid swing families retain priority.
        if(keys.Contains("A")&&!keys.Contains("D"))return;
        Bounds shape=NeedleShape(hero,g,2,g.FacingRight);
        if(shape.size.x<.08f||shape.size.y<.08f)return;
        float walk=side*hero.GetWalkSpeed();
        float overlap=NeedleContact(g,shape,walk,hold);
        if(overlap<.02f)
        {
            if(keys.Contains("A"))
            {
                keys=keys.Replace("D","").Replace("A","");
                Note("POGO_ALIGN_WAIT "+raceName+" "+(frame-raceStart)+" overlap="+
                     overlap.ToString("F2",CultureInfo.InvariantCulture));
            }
            return;
        }
        if(keys.Contains("A"))return;
        ReadHitboxes(hero);
        AddFlailForecast(hero.transform.position);
        string threat;
        float danger=Forecast(g.Actor,g.HeroVelocity,g.Floor,walk,0f,hold?8:0,
            false,true,out threat,10);
        if(danger>1.35f)return;
        keys=keys.Replace("D","")+"DA";
        raceAttack=frame;
        Note("POGO_ALIGN_CUT "+raceName+" "+(frame-raceStart)+" "+keys+" overlap="+
             overlap.ToString("F2",CultureInfo.InvariantCulture)+" danger="+
             danger.ToString("F2",CultureInfo.InvariantCulture));
    }

    private string ReadAttackEnvelope(HeroController hero,NeedleGeometry g,out float overlap)
    {
        overlap=-20f;
        string best="";
        bool right=g.Target.center.x>=g.Actor.center.x;
        string toward=right?"R":"L";
        float speed=(right?1f:-1f)*hero.GetWalkSpeed();
        bool holding=previousRaceKeys.Contains("J");
        for(int family=0;family<3;family++)
        {
            if(family==2&&g.Ground)continue;
            var shape=NeedleShape(hero,g,family,right);
            float hit=NeedleContact(g,shape,family==0?speed:0f,holding);
            if(hit<=overlap)continue;
            overlap=hit;
            best=family==0?toward+"A":family==1?"UA":"DA";
        }
        return best;
    }

    private void TraceAttackFrame()
    {
        var hero=HeroController.instance;
        if(attackTrace==null||!hero||!boss)return;
        var pd=PlayerData.instance;
        NeedleGeometry g;
        bool grounded=hero.cState.onGround;
        bool control=hero.CanInput(),ready=control&&hero.CanAttack();
        bool target=ReadNeedleGeometry(hero,out g);
        Vector2 gap=boss.transform.position-hero.transform.position;
        Vector2 velocity=Vector2.zero;
        float cooldown=(float)NativeAttackCooldown.GetValue(hero);
        float overlap=-20f,risk=-1f;
        string strike="",cause="";
        if(target)
        {
            gap=g.Target.center-g.Actor.center;
            velocity=g.TargetVelocity-g.HeroVelocity;
            if(Mathf.Abs(gap.x)<=8f&&Mathf.Abs(gap.y)<=7f)
            {
                strike=ReadAttackEnvelope(hero,g,out overlap);
                if(ready&&overlap>=-.36f)
                {
                    if(lastGeometrySample!=frame)
                    {
                        ReadHitboxes(hero);
                        AddFlailForecast(hero.transform.position);
                    }
                    int previousCount=threats.Count;
                    if(!threats.Any(t=>t.Label=="telegraph:"+raceState))
                        PredictTelegraph(raceState,frame-raceStateSince,g.Target,g.Actor,gap.x);
                    float walk=strike.StartsWith("R")?hero.GetWalkSpeed():strike.StartsWith("L")?-hero.GetWalkSpeed():0f;
                    risk=Forecast(g.Actor,g.HeroVelocity,g.Floor,walk,0f,previousRaceKeys.Contains("J")?9:0,
                        false,!g.Ground,out cause,10);
                    if(threats.Count>previousCount)threats.RemoveRange(previousCount,threats.Count-previousCount);
                }
            }
        }
        attackTrace.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "{0}\t{1}\t{2}\t{3}\t{4}\t{5}\t{6}\t{7}\t{8}\t{9}\t{10}\t{11:F4}\t{12}\t{13:F2}\t{14:F2}\t{15:F2}\t{16:F2}\t{17}\t{18:F2}\t{19:F2}\t{20}\t{21}",
            frame-raceStart,raceState,frame-raceStateSince,boss.hp,pd.health,pd.silk,keys,previousRaceKeys,
            grounded?1:0,control?1:0,ready?1:0,cooldown,target?1:0,gap.x,gap.y,velocity.x,velocity.y,
            strike,overlap,risk,cause,auditKeys!=null?"replay":"policy"));
    }

    private static float PressureOverlap(Bounds attack, Bounds target)
    {
        float x=attack.extents.x+target.extents.x-Mathf.Abs(attack.center.x-target.center.x);
        float y=attack.extents.y+target.extents.y-Mathf.Abs(attack.center.y-target.center.y);
        return Mathf.Min(x,y);
    }

    private int ChooseCritWait(HeroController hero,Bounds strike,Bounds victim,Vector2 relative)
    {
        if(stockAttack||!raceBuild.Contains("rngtempo")||alignCrit||quickRollLimit>0||!hero.IsWandererLucky||
           HeroController.instance.WandererState.CriticalHitsLocked||previousRngDraws<=0||stableRngFrames<4)
            return 0;
        var config=hero.Config;
        float cooldown=hero.IsUsingQuickening?config.QuickAttackCooldownTime:config.AttackCooldownTime;
        int cadence=Mathf.Max(5,Mathf.CeilToInt(60f*cooldown));
        // Compare both plans over two stock attack intervals.
        int limit=2*cadence-1;
        int horizon=cadence*2;
        float cutoff=GlobalSettings.Gameplay.WandererCritChance*hero.GetLuckModifier();
        var observed=UnityEngine.Random.state;
        float[] forecasts=new float[limit+1];
        try
        {
            for(int n=0;n<=(limit+1)*previousRngDraws;n++)
            {
                float value=UnityEngine.Random.Range(0f,1f);
                if(n>=previousRngDraws&&n%previousRngDraws==0)
                    forecasts[n/previousRngDraws-1]=value;
            }
        }
        finally { UnityEngine.Random.state=observed; }
        if(forecasts[0]<=cutoff)return 0;
        var afterOne=victim;
        afterOne.center+=(Vector3)(relative*(cadence/60f));
        int immediate=1+(PressureOverlap(strike,afterOne)>.08f?1:0);
        for(int n=1;n<=limit;n++)
        {
            if(forecasts[n]>cutoff)continue;
            var duringWait=victim;
            duringWait.center+=(Vector3)(relative*(n/60f));
            if(PressureOverlap(strike,duringWait)<.30f)continue;
            // The immediate strike can recover in time for this window, including the critical opportunity.
            if(n>=cadence)continue;
            var next=victim;
            next.center+=(Vector3)(relative*((n+cadence)/60f));
            int delayed=3+(n+cadence<horizon&&PressureOverlap(strike,next)>.08f?1:0);
            if(delayed>immediate)return n;
        }
        return 0;
    }

    private int compactShiftSince=-1,compactCast=-10000,compactJump=-10000,compactDouble=-10000;
    private int compactSilk;
    private bool compactDebited;
    private string compactAction="";

    private bool CompactInput(string action,int age,float gap)
    {
        if(action!=compactAction)
        {
            compactAction=action;
            Note("COMPACT_SHIFT "+raceName+" "+(frame-raceStart)+" "+action+" native="+age+
                 " gap="+gap.ToString("F2",CultureInfo.InvariantCulture)+" input=["+keys+"]"+
                 " silk="+PlayerData.instance.silk);
        }
        return true;
    }

    private bool TryCompactShift(HeroController hero,string state,int age,float dx,float dy,bool ground)
    {
        // Keep the measured early needle sequence through native 42, including the knockback.
        if(age<43)return false;
        if(compactShiftSince!=raceStateSince)
        {
            compactShiftSince=raceStateSince;
            compactAction="";
            compactCast=compactJump=compactDouble=-10000;
            compactDebited=false;
        }
        float gap=Mathf.Abs(dx);
        string toward=dx<0f?"L":"R",away=dx<0f?"R":"L";
        auditKeys=null;
        if(raceBuild.Contains("shiftcross"))
        {
            var pd=PlayerData.instance;
            if(compactCast>=0&&!compactDebited&&pd.silk<compactSilk)
            {
                compactDebited=true;
                Note("COMPACT_DEBIT "+raceName+" "+(frame-raceStart)+" native="+age+" silk="+pd.silk);
            }
            if(frame-compactCast<3)
            {
                keys=raceHeld;
                return CompactInput("cross-cast-hold",age,gap);
            }
            if(compactDebited)
            {
                // Native spell movement supplies the crossing. Only join a proven needle after it.
                keys=gap>3.3f?toward:"";
                if(TryFlowNeedle(hero,state,frame-raceStateSince,keys))
                    return CompactInput("cross-needle",age,gap);
                return CompactInput("cross-land",age,gap);
            }
            bool hasSilk=pd.silk>=pd.SilkSkillCost;
            if(ground&&compactCast<0&&hasSilk&&age>=53&&age<=58&&gap>=4.4f&&
               Mathf.Abs(dy)<1.45f&&TrySharpdart(hero,state,age,dx,dy,true))
            {
                // Both verified pre-blast casts explicitly faced the target.
                compactCast=frame;
                compactSilk=pd.silk;
                shiftedSpell=true;
                RacePress(toward+"C",3);
                return CompactInput("cross-cast",age,gap);
            }
            if(ground&&compactJump<0&&(!hasSilk||age>=58)&&hero.CanJump())
            {
                raceJump=compactJump=frame;
                RacePress(toward+"J",9);
                return CompactInput("cross-vault",age,gap);
            }
            if(!ground&&compactJump>=0&&frame-compactJump>=14&&age>=55&&
               compactDouble<0&&hero.transform.position.y<12.35f&&hero.CanDoubleJump(false))
            {
                raceDouble=compactDouble=frame;
                RacePress(toward+"J",10);
                return CompactInput("cross-double",age,gap);
            }
            if(compactJump>=0)
            {
                keys=(gap>2.3f?toward:"")+(frame-compactJump<9||frame-compactDouble<10?"J":"");
                if(!ground&&TryFlowNeedle(hero,state,frame-raceStateSince,keys))
                    return CompactInput("cross-air-needle",age,gap);
                return CompactInput("cross-air",age,gap);
            }
            keys=gap>6.0f?toward:"";
            return CompactInput("cross-align",age,gap);
        }

        // The radial blast starts at native 64 with a measured 8.4 horizontal radius.
        // Start walking early so residual needle recoil helps attain the edge without a dash.
        if(ground&&age>=56&&age<64&&compactJump<0&&gap+(64-age)*.1375f<8.85f&&hero.CanJump())
        {
            raceJump=compactJump=frame;
            RacePress(away+"J",10);
            return CompactInput("short-vault",age,gap);
        }
        float edge=age>=73?8.87f:9.05f;
        keys=gap<edge?away:gap>edge+.2f?toward:"";
        if(frame-compactJump<10)keys+="J";
        return CompactInput(keys.Contains(away)?"short-out":keys.Contains(toward)?"short-return":"short-edge",age,gap);
    }

    private int endRaceStart=-1,endAttempt=-10000,endInitialSilk,endTries;
    private bool endSpent;

    private bool TryStaggerFinish(HeroController hero,string state,bool control)
    {
        if(endRaceStart!=raceStart)
        {
            endRaceStart=raceStart;
            endAttempt=-10000;
            endTries=0;
            endSpent=false;
        }
        var pd=PlayerData.instance;
        if(endTries>0&&!endSpent&&pd.silk<endInitialSilk)
        {
            endSpent=true;
            Note("END_RESOURCE "+raceName+" "+(frame-raceStart)+" before="+endInitialSilk+" now="+pd.silk);
        }
        if(!control||boss.IsInvincible||hero.cState.dashing)return false;
        int age=frame-raceStateSince;
        if(endSpent)
        {
            // Spell and Clawline determine their own movement. Seek an ordinary needle on recovery.
            if(hero.CanAttack()&&TryFlowNeedle(hero,state,age,null,true))
            {
                Note("END_FOLLOW_NEEDLE "+raceName+" "+(frame-raceStart)+" input="+keys);
                return true;
            }
            return false;
        }
        if(boss.hp<105||boss.hp>160||!hero.cState.onGround||pd.silk<11||age<25)return false;
        float cd=(float)NativeAttackCooldown.GetValue(hero);
        bool thread=raceBuild.Contains("endthread");
        if(endTries==0)
        {
            // 149 HP at 2881. The .2667 cooldown is frozen for 14 frames of hitstop.
            if(cd<=.04f||cd>(thread?.185f:.235f))return false;
        }
        else if(endTries>=3||frame-endAttempt<4||cd<.012f)return false;
        NeedleGeometry g;
        if(!ReadNeedleGeometry(hero,out g))return false;
        float dx=g.Target.center.x-g.Actor.center.x,dy=g.Target.center.y-g.Actor.center.y;
        float gap=Mathf.Abs(dx);
        if(gap<3.7f||gap>7.65f||Mathf.Abs(dy)>1.2f||Mathf.Abs(g.TargetVelocity.y)>2.5f)return false;
        string toward=dx<0f?"L":"R";
        if(thread)
        {
            if(previousRaceKeys.Contains("C")||!hero.CanCast())return false;
            if(endTries==0)
            {
                if(!TrySharpdart(hero,state,age,dx,dy,true))return false;
            }
            else
            {
                // Retry only an input that spent no silk and is still legal to cast.
                if(pd.silk!=endInitialSilk)return false;
                raceCast=sharpLastPlan=frame;
                sharpPlanMasks=pd.health;
                sharpPlanThrough=true;
                ++sharpTried;
            }
            RacePress(toward+"C",2);
        }
        else
        {
            if(previousRaceKeys.Contains("H")||!hero.CanHarpoonDash())return false;
            ReadHitboxes(hero);
            AddFlailForecast(hero.transform.position);
            string hazard;
            if(Forecast(g.Actor,g.HeroVelocity,g.Floor,Mathf.Sign(dx)*hero.DASH_SPEED,0f,0,true,false,
                        out hazard,10)>1.05f)return false;
            raceClaw=frame;
            RacePress(toward+"H",2);
        }
        if(endTries==0)endInitialSilk=pd.silk;
        endAttempt=frame;
        ++endTries;
        Note("END_STAGGER "+raceName+" "+(frame-raceStart)+" type="+(thread?"silk":"clawline")+
             " hp="+boss.hp+" cd="+cd.ToString("F3",CultureInfo.InvariantCulture)+" input="+keys+
             " gap="+gap.ToString("F2",CultureInfo.InvariantCulture)+" try="+endTries);
        return true;
    }

    private bool TryVaultShift(HeroController hero,int age,float dx,bool ground)
    {
        if(age>=38&&age<=46&&ground&&hero.CanJump()&&
           frame-raceJump>18&&Mathf.Abs(dx)<6.6f)
        {
            raceJump=shiftVaultAt=frame;
            RacePress("J",10);
            Note("SHIFT_VAULT "+raceName+" "+(frame-raceStart)+" first age="+age);
            return true;
        }
        if(frame-shiftVaultAt>49)return false;
        if(ground&&frame-shiftVaultAt>15)return false;
        float y=hero.transform.position.y;
        if(!ground&&age>=55&&frame-shiftVaultAt>=14&&hero.CanDoubleJump(false)&&
           frame-raceDouble>23&&y<12.35f)
        {
            raceDouble=frame;
            RacePress("J",11);
            Note("SHIFT_VAULT "+raceName+" "+(frame-raceStart)+" second age="+age+
                 " y="+y.ToString("F2",CultureInfo.InvariantCulture));
            return true;
        }
        keys=(frame-raceJump<17||frame-raceDouble<15)?"J":"";
        return true;
    }

    private bool TryShiftBoundary(HeroController hero,int age,float dx,bool ground)
    {
        float hx=hero.transform.position.x;
        float bx=boss.transform.position.x;
        float sign=dx>=0f?-1f:1f;
        float edge=bx+sign*9.65f;
        if(edge<4.25f||edge>54.15f||!ground)return false;
        float deficit=sign*(edge-hx);
        if(deficit<=0f)
        {
            keys="";
            if(frame-shiftExitLogged>=9)
            {
                shiftExitLogged=frame;
                Note("SHIFT_BOUNDARY "+raceName+" "+(frame-raceStart)+" age="+age+
                     " gap="+Mathf.Abs(dx).ToString("F2",CultureInfo.InvariantCulture)+" input=hold");
            }
            return true;
        }
        string side=sign<0f?"L":"R";
        int timeToWalk=Mathf.CeilToInt(deficit*60f/Mathf.Max(4f,hero.GetWalkSpeed()))+5;
        if(age+timeToWalk>=62&&deficit>2.0f&&hero.CanDash()&&frame-raceDash>24)
        {
            raceDash=frame;
            RacePress(side+"S",5);
            Note("SHIFT_BOUNDARY "+raceName+" "+(frame-raceStart)+" age="+age+" input="+keys);
        }
        else keys=side;
        return true;
    }

    private bool TryCloseAirLane(HeroController hero,string state,int age,bool ground)
    {
        if(!ground||state!="Dive Out A"||age>9||hero.cState.dashing||
           frame-raceDash<=27||!hero.CanDash())return false;
        Bounds actor,victim;
        if(!ReadPinnedTarget(hero,out actor,out victim))return false;
        var enemy=boss.GetComponent<Rigidbody2D>();
        var own=hero.GetComponent<Rigidbody2D>();
        if(!enemy||!own)return false;
        float dx=victim.center.x-actor.center.x;
        float gap=Mathf.Abs(dx);
        float rise=victim.center.y-actor.center.y;
        if(gap<6.05f||gap>9.35f||rise<.65f||rise>4.55f||enemy.linearVelocity.y<5.0f)return false;
        float sign=Mathf.Sign(dx);
        ReadHitboxes(hero);
        AddFlailForecast(hero.transform.position);
        string threat;
        if(Forecast(actor,own.linearVelocity,actor.center.y,sign*hero.DASH_SPEED,0,0,true,false,out threat,13)>1.4f)
            return false;
        raceDash=frame;
        RacePress(sign<0f?"LS":"RS",4);
        Note("AIR_LANE_CLOSE "+raceName+" "+(frame-raceStart)+" "+keys+" age="+age+
             " gap="+gap.ToString("F2",CultureInfo.InvariantCulture)+
             " rise="+rise.ToString("F2",CultureInfo.InvariantCulture));
        return true;
    }

    private bool UseAirNeedleWindow(string state,int age)
    {
        return repairOpenings&&(!isolateLateAir||lateAirReady)&&
             (state=="Dive Out A"||state.StartsWith("Bullet S Air",StringComparison.Ordinal)||
              state=="Bounce Back ToDive"&&age<29||state=="Tendril A Antic"&&age<27||
              state=="Tendril A Whip"&&age<14);
    }

    private int ShiftAttackDeadline(HeroController hero)
    {
        float bx=boss.transform.position.x;
        float hx=hero.transform.position.x;
        float room=hx<bx?bx-3.73f:54.69f-bx;
        if(room<11.55f||Mathf.Abs(hero.transform.position.y-boss.transform.position.y)>2.45f)return 12;
        float gap=Mathf.Min(3.35f,Mathf.Abs(bx-hx));
        float exitSpeed=frame-raceDash>16?hero.DASH_SPEED*.84f:hero.GetWalkSpeed();
        int exitFrames=Mathf.CeilToInt((11.0f-gap)*60f/Mathf.Max(5f,exitSpeed));
        // Leave enough time for attack recovery and a full step outside the blast.
        return Mathf.Clamp(64-7-exitFrames,12,40);
    }

    private bool TryPressSwing(HeroController hero,string state,int age,bool ground)
    {
        bool nativeReady=hero.CanAttack();
        bool buffered=!nativeReady&&sharpIntercept&&raceBuild.Contains("needlequeue")&&
            frame-raceAttack>=12&&!hero.cState.dashing&&!previousRaceKeys.Contains("A")&&
            Mathf.Abs(hero.transform.position.x-boss.transform.position.x)<5.5f;
        float cooldown=buffered?(float)NativeAttackCooldown.GetValue(hero):0f;
        buffered=buffered&&cooldown>0f&&cooldown<=.031f;
        if(!nativeReady&&!buffered||frame-raceAttack<=2||boss.IsInvincible||!boss.gameObject.activeInHierarchy||
           state=="Counter Stance"||state=="Counter Hit"||
           raceBuild.Contains("bodyguard")&&state=="Counter Antic"&&age>=2||state.Contains("Wave")||
           state.StartsWith("P2 Shift",StringComparison.Ordinal)&&age>
             (raceBuild.Contains("pinwindow")&&state=="P2 Shift 2"?ShiftAttackDeadline(hero):12)||
           state.StartsWith("P4 Shift",StringComparison.Ordinal)&&age>10)
            return false;
        if(!ReadPressureHulls(hero))return false;
        var target=LaceCollider.GetValue(boss) as Collider2D;
        if(!target||!target.isActiveAndEnabled)
            target=boss.GetComponents<Collider2D>().FirstOrDefault(c=>c.isActiveAndEnabled&&c.bounds.size.x>.25f);
        var self=hero.heroBox?hero.heroBox.GetComponent<Collider2D>():null;
        if(!target||!self||!target.isActiveAndEnabled||!self.isActiveAndEnabled)
        {
            if(frame-pressureNote>=60&&raceBuild.Contains("inspectzone"))
            {
                pressureNote=frame;
                Note("PRESSBOX_CANDIDATE "+raceName+" "+(frame-raceStart)+" missing target="+(bool)target+" own="+(bool)self+" state="+state);
            }
            return false;
        }
        var victim=target.bounds;
        var own=self.bounds;
        if(victim.size.x<.25f||victim.center.y<3.2f||
           Mathf.Abs(own.center.x-victim.center.x)>6.6f||
           Mathf.Abs(own.center.y-victim.center.y)>5.4f)return false;
        var hb=hero.GetComponent<Rigidbody2D>();
        var tb=boss.GetComponent<Rigidbody2D>();
        Vector2 v=hb.linearVelocity;
        Vector2 relative=(tb?tb.linearVelocity:Vector2.zero)-v;
        bool airWindow=UseAirNeedleWindow(state,age);
        var expected=victim;
        expected.center+=(Vector3)(relative*((buffered?4f:2f)/60f));
        bool right=victim.center.x>=hero.transform.position.x;
        bool same=hero.cState.facingRight==right;
        string action="", direction=right?"R":"L";
        float best=airWindow?.06f:raceBuild.Contains("sticklace")?-.23f:-.13f;
        float chosenOverlap=0f;
        Bounds selected=new Bounds();
        bool alt=hero.cState.altAttack&&frame-raceAttack<47;
        for(int family=0;family<3;family++)
        {
            if(family==2&&ground)continue;
            int i=family*2+(alt?1:0);
            var collider=pressureHulls[i]?pressureHulls[i]:pressureHulls[family*2];
            if(!collider)continue;
            var shape=WorldColliderShape(collider);
            if(shape.size.x<.1f||shape.size.y<.1f)continue;
            if(family==0&&!same)
            {
                var center=shape.center; center.x=2*hero.transform.position.x-center.x; shape.center=center;
            }
            float current=PressureOverlap(shape,victim);
            float next=PressureOverlap(shape,expected);
            float overlap=raceBuild.Contains("sticklace")&&current>.24f&&next>-.40f?
                current:Mathf.Min(current,next);
            if(airWindow)
            {
                // Follow the hurtbox through attack startup. A corner of the slash's AABB
                // alone does not support a hit on a vertically moving target.
                var middle=victim;
                middle.center+=(Vector3)(relative*((buffered?3f:1f)/60f));
                float projected=Mathf.Min(PressureOverlap(shape,middle),next);
                float margin=family==0?.25f:.08f;
                if(projected<margin)continue;
                overlap=projected;
                if(family==1&&victim.center.y>own.center.y+1.1f)
                    overlap+=preferAirUp?.62f:.32f;
            }
            else if(family==2&&victim.center.y<own.center.y-.35f)overlap+=.17f;
            if(overlap<=best)continue;
            best=overlap;
            chosenOverlap=airWindow?Mathf.Min(PressureOverlap(shape,victim),next):overlap;
            action=family==0?(same&&raceBuild.Contains("longedge")?"A":direction+"A"):family==1?"UA":"DA";
            selected=shape;
        }
        if(frame-pressureNote>=16&&raceBuild.Contains("inspectzone")&&Mathf.Abs(victim.center.x-own.center.x)<4.4f)
        {
            pressureNote=frame;
            var raw=WorldColliderShape(pressureHulls[0]);
            Note("PRESSBOX_CANDIDATE "+raceName+" "+(frame-raceStart)+" state="+state+" player="+own.center+
                 " victim="+victim.center+" reach="+raw.center+" best="+best+" action="+action+
                 " facing="+hero.cState.facingRight);
        }
        if(action.Length==0||buffered&&best<.28f)return false;
        ReadHitboxes(hero);
        AddFlailForecast(hero.transform.position);
        PredictTelegraph(state,age,victim,own,victim.center.x-own.center.x);
        float floor=ground?own.center.y:currentFloor+(own.center.y-hero.transform.position.y);
        bool force=raceBuild.Contains("presshard");
        bool desperate=raceBuild.Contains("whipflee")&&PlayerData.instance.health<=4;
        string threat;
        float walk=action.Length==2&&(action[0]=='R'||action[0]=='L')?
            (action[0]=='R'?1f:-1f)*hero.GetWalkSpeed():0f;
        float gap=Mathf.Abs(victim.center.x-own.center.x);
        bool liftWithCut=raceBuild.Contains("jumpcut")&&ground&&hero.CanJump()&&frame-raceJump>17&&
            ((state=="Charge Break"&&age>=16&&gap<7.3f)||
             (state=="ComboSlash 1"&&age>=20&&gap<5.8f)||
             (state=="Tendril G Antic"&&age>=22&&gap<10f));
        float risk=Forecast(own,v,floor,walk,liftWithCut?hero.JUMP_SPEED:0f,liftWithCut?10:0,
            false,!ground,out threat,force?6:9);
        if(!hero.cState.invulnerable&&risk>(raceBuild.Contains("stickmax")?10.8f:desperate?3.8f:force?7.9f:4.6f))
        {
            if(raceBuild.Contains("inspectzone")&&frame==pressureNote)
                Note("PRESSBOX_VETO "+raceName+" "+(frame-raceStart)+" risk="+risk+" "+threat);
            return false;
        }
        if(raceBuild.Contains("pinwindow")&&(state=="Tendril A Whip"||state=="P2 Shift 2"&&age>12))
            Note("WANDER_SKILL_PUNISH "+raceName+" "+(frame-raceStart)+" "+state+" age="+age+
                 " input="+action+" overlap="+best.ToString("F2",CultureInfo.InvariantCulture));
        bool sameOpportunity=frame-pressureStartWait<2*Mathf.Max(5,Mathf.CeilToInt(60f*hero.Config.AttackCooldownTime));
        if(!liftWithCut&&!sameOpportunity&&best>.25f&&relative.sqrMagnitude<100f&&risk<1.1f)
        {
            int wait=ChooseCritWait(hero,selected,victim,relative);
            if(wait>0)
            {
                pressureStartWait=frame;pressureWaitUntil=frame+wait;pressureWaits++;
                pressureWaitDraws=previousRngDraws;
                Note("PRESSBOX_WAIT "+raceName+" "+(frame-raceStart)+" delay="+wait+
                     " stableDraws="+previousRngDraws+" overlap="+best+" maxAttackIntervals=2");
            }
        }
        if(frame<pressureWaitUntil&&best>.16f&&risk<1.3f&&
           previousRngDraws==pressureWaitDraws&&stableRngFrames>=4)
        {
            keys=action.Length==2&&(action[0]=='L'||action[0]=='R')?direction:"";
            raceHoldUntil=frame;
            return true;
        }
        int held=pressureWaitUntil>-1000?frame-pressureStartWait:0;
        pressureWaitUntil=-10000;
        raceAttack=pressureAttackFrame=frame;
        ++pressureSwings;
        if(liftWithCut)
        {
            action+="J";raceJump=frame;pressureLiftUntil=frame+12;
        }
        RacePress(action,buffered?3:1);
        if(buffered)
            Note("NEEDLE_BUFFER "+raceName+" "+(frame-raceStart)+" cooldown="+
                cooldown.ToString("F4",CultureInfo.InvariantCulture)+" "+action+" "+state);
        bool keepJump=airWindow&&v.y>0f&&(frame-raceJump<=19||frame-raceDouble<=15);
        if(!ground&&(frame-raceJump<14||keepJump)&&previousRaceKeys.Contains("J"))keys+="J";
        if(airWindow)
        {
            airSwingAt=frame;
            Note("AIR_WINDOW_SWING "+raceName+" "+(frame-raceStart)+" "+keys+" state="+state+
                 " overlap="+chosenOverlap.ToString("F2",CultureInfo.InvariantCulture)+
                 " dx="+(victim.center.x-own.center.x).ToString("F2",CultureInfo.InvariantCulture)+
                 " dy="+(victim.center.y-own.center.y).ToString("F2",CultureInfo.InvariantCulture));
        }
        Note("PRESSBOX_SWING "+raceName+" "+(frame-raceStart)+" "+action+" overlap="+best+
             " danger="+risk+" held="+held+" state="+state);
        return true;
    }

    private bool ReadPinnedTarget(HeroController hero,out Bounds actor,out Bounds victim)
    {
        actor=new Bounds();
        victim=new Bounds();
        if(boss.IsInvincible||!boss.gameObject.activeInHierarchy)return false;
        var target=LaceCollider.GetValue(boss) as Collider2D;
        if(!target||!target.isActiveAndEnabled)
            target=boss.GetComponents<Collider2D>().FirstOrDefault(c=>c.isActiveAndEnabled&&c.bounds.size.x>.25f);
        var hurt=hero.heroBox?hero.heroBox.GetComponent<Collider2D>():null;
        if(!target||!hurt||!hurt.isActiveAndEnabled)return false;
        actor=hurt.bounds;
        victim=target.bounds;
        return victim.center.y>3.5f&&victim.size.x>.25f;
    }

    private bool TrySprintNeedle(HeroController hero,string state,bool ground)
    {
        float follow=(float)WanderDashCombo.GetValue(hero);
        bool sprintPair=raceBuild.Contains("sprintpair")&&follow>.01f;
        if((!ground&&!sprintPair)||(!hero.CanAttack()&&!sprintPair)||frame-raceAttack<=(sprintPair?3:2)||
           previousRaceKeys.Contains("A")&&sprintPair||state.Contains("Counter")||
           state.Contains("Wave")||state.Contains("Shift")||state.Contains("Dive In"))return false;
        if(sprintPair&&frame-raceAttack>11&&!hero.CanAttack())return false;
        bool running=hero.cState.isSprinting;
        if(!running&&follow<=0f)return false;
        Bounds actor,victim;
        if(!ReadPinnedTarget(hero,out actor,out victim))return false;
        float dx=victim.center.x-actor.center.x;
        float gap=Mathf.Abs(dx);
        if(gap<(follow>0f?.85f:1.8f)||gap>(follow>0f?6.8f:5.0f)||
           Mathf.Abs(actor.center.y-victim.center.y)>1.7f||
           running&&follow<=0f&&hero.cState.facingRight!=(dx>0f))return false;
        if(follow<=0f)
        {
            // The initial sprint attack needs physical clearance. Don't sprint into a live lunge.
            ReadHitboxes(hero);
            string source;
            var velocity=hero.GetComponent<Rigidbody2D>().linearVelocity;
            var victimBody=boss.GetComponent<Rigidbody2D>();
            if(victimBody&&Mathf.Sign(dx)*victimBody.linearVelocity.x< -7f)return false;
            if(!hero.cState.invulnerable&&Forecast(actor,velocity,actor.center.y,
               Mathf.Sign(dx)*Mathf.Max(hero.GetWalkSpeed(),Mathf.Abs(velocity.x)),0,0,false,false,out source,5)>5.8f)
                return false;
        }
        string direction=dx<0f?"L":"R";
        raceAttack=pressureAttackFrame=frame;
        pressureSwings++;
        RacePress(follow>0f?direction+"A":direction+"SA",sprintPair?2:1);
        Note("WANDER_SPRINT_STRIKE "+raceName+" "+(frame-raceStart)+" "+keys+
             " follow="+(follow>0f)+" gap="+gap.ToString("F2",CultureInfo.InvariantCulture)+" "+state);
        return true;
    }

    private bool TryOpeningCombo(HeroController hero,int age,bool ground)
    {
        if(age>29||hero.cState.dashing)return false;
        NeedleGeometry g;
        if(!ReadNeedleGeometry(hero,out g))return false;
        float dx=g.Target.center.x-g.Actor.center.x,dy=g.Target.center.y-g.Actor.center.y;
        if(Mathf.Abs(dx)>6.0f||Mathf.Abs(dy)>4.65f)return false;
        if(age<=25&&TryFlowNeedle(hero,"ComboSlash 1",age,null,true))return true;
        if(!ground||Mathf.Abs(dy)>.7f)return false;
        string toward=dx<0f?"L":"R";
        if(age<20)
        {
            keys=Mathf.Abs(dx)>3.72f?toward:"";
            return true;
        }
        if(age>=24&&hero.CanJump()&&frame-raceJump>15&&!previousRaceKeys.Contains("J"))
        {
            raceJump=frame;
            RacePress(Mathf.Abs(dx)>4.0f?toward+"J":"J",9);
            Note("COMBO_EDGE_LIFT "+raceName+" "+(frame-raceStart)+" age="+age+" "+keys);
            return true;
        }
        return false;
    }

    private bool TryWeaveClaw(HeroController hero,string state)
    {
        int since=frame-lastPlainImpact;
        if(since<3||since>10||!hero.CanHarpoonDash()||frame-lastHookWeave<48||
           PlayerData.instance.silk<11||frame-raceClaw<32||
           state!="Downstab Land"&&state!="Charge Recover"&&state!="Stunned"&&
           state!="Evade Recover"&&state!="Tendril G Whip End")return false;
        NeedleGeometry g;
        if(!ReadNeedleGeometry(hero,out g))return false;
        float dx=g.Target.center.x-g.Actor.center.x;
        if(Mathf.Abs(dx)<4.65f||Mathf.Abs(dx)>8.5f||
           Mathf.Abs(g.Target.center.y-g.Actor.center.y)>1.15f||
           Mathf.Abs(g.TargetVelocity.x)>6.8f)return false;
        ReadHitboxes(hero);
        AddFlailForecast(hero.transform.position);
        string hazard;
        float face=Mathf.Sign(dx);
        if(Forecast(g.Actor,g.HeroVelocity,g.Floor,face*hero.DASH_SPEED,0,0,true,!g.Ground,out hazard,11)>1.0f)
            return false;
        string toward=face<0?"L":"R";
        raceClaw=lastHookWeave=frame;
        RacePress(toward+"H",2);
        Note("HOOK_WEAVE "+raceName+" "+(frame-raceStart)+" gap="+Mathf.Abs(dx).ToString("F2",CultureInfo.InvariantCulture)+
             " nextNeedle="+Mathf.CeilToInt((float)NativeAttackCooldown.GetValue(hero)*60f)+" state="+state);
        return true;
    }

    private bool TryContactDash(HeroController hero,string state,int age,bool ground)
    {
        bool stand=state=="Dive Out G"||state=="Stunned"||state=="Downstab Land"||
            state=="Evade Recover"||state=="Charge Recover"||state=="Tendril G Whip End"||
            state=="Idle"||state=="Hop Recover"||state=="Land";
        bool windup=state=="Summon Turn"||state=="Summon Thwip"||
            state=="Tendril G Antic"&&age<=13||state=="ComboSlash 1"&&age<=12||
            state=="Charge Break"&&age<=8;
        if(!stand&&!windup||hero.cState.dashing||hero.transform.position.y>13.9f||
           frame<raceHoldUntil&&raceHeld!=""&&raceHeld!="L"&&raceHeld!="R")return false;
        NeedleGeometry g;
        if(!ReadNeedleGeometry(hero,out g))return false;
        float dx=g.Target.center.x-g.Actor.center.x;
        float dy=g.Target.center.y-g.Actor.center.y;
        float sign=Mathf.Sign(dx),gap=Mathf.Abs(dx);
        if(gap<3.85f||gap>13.0f||dy>2.15f||dy< -2.5f||sign*g.TargetVelocity.x< -11.0f)
            return false;
        if(!ground&&dy< -1.65f&&!stand)return false;

        ReadHitboxes(hero);
        AddFlailForecast(hero.transform.position);
        PredictTelegraph(state,age,g.Target,g.Actor,dx);
        string hazard;
        float moved=gap+Mathf.Clamp(sign*g.TargetVelocity.x*.16f,-2.4f,2.4f)-hero.DASH_SPEED*.17f;
        string toward=sign<0?"L":"R";
        if(ground&&gap>=7.1f&&moved>2.45f&&moved<6.3f&&Mathf.Abs(dy)<1.2f&&
           hero.CanDash()&&frame-raceDash>24&&!previousRaceKeys.Contains("S")&&
           Forecast(g.Actor,g.HeroVelocity,g.Floor,sign*hero.DASH_SPEED,0f,0,true,false,out hazard,12)<1.45f)
        {
            raceDash=frame;
            RacePress(toward+"S",5);
            Note("CONTACT_DASH "+raceName+" "+(frame-raceStart)+" "+keys+
                 " gap="+gap.ToString("F2",CultureInfo.InvariantCulture)+" state="+state);
            return true;
        }
        if(gap>7.65f&&!stand)return false;
        if(Forecast(g.Actor,g.HeroVelocity,g.Floor,sign*hero.GetWalkSpeed(),0f,
            !ground&&previousRaceKeys.Contains("J")?8:0,false,!ground,out hazard,12)>1.55f)
            return false;
        keys=toward;
        if(!ground&&g.HeroVelocity.y>2.3f&&previousRaceKeys.Contains("J"))keys+="J";
        return true;
    }

    private void WriteBossRoutes()
    {
        using(var trace=new StreamWriter(Path.Combine(root,raceName+".boss-routes.txt"),false))
        {
            foreach(var machine in boss.GetComponents<PlayMakerFSM>())
            {
                if(machine.FsmName!="Control"&&machine.FsmName!="Stun Control"&&
                   machine.FsmName!="Death Control"&&machine.FsmName!="Circle Slash Catch")continue;
                trace.WriteLine("FSM "+machine.FsmName);
                foreach(var fs in machine.FsmStates)
                {
                    trace.WriteLine("STATE "+fs.Name+" -> "+string.Join(", ",fs.Transitions
                        .Select(t=>t.EventName+":"+t.ToState).ToArray()));
                    foreach(var action in fs.Actions)
                    {
                        var fields=new List<string>();
                        foreach(var member in action.GetType().GetFields())
                        {
                            var v=member.GetValue(action);
                            var f=v as HutongGames.PlayMaker.FsmFloat;
                            var i=v as HutongGames.PlayMaker.FsmInt;
                            var b=v as HutongGames.PlayMaker.FsmBool;
                            if(f!=null)fields.Add(member.Name+"="+f.Name+":"+f.Value);
                            if(i!=null)fields.Add(member.Name+"="+i.Name+":"+i.Value);
                            if(b!=null)fields.Add(member.Name+"="+b.Name+":"+b.Value);
                        }
                        trace.WriteLine("  "+action.GetType().Name+" "+string.Join(" ",fields.ToArray()));
                    }
                }
            }
        }
        Note("BOSS_ROUTES "+raceName);
    }

    private bool TryPinLace(HeroController hero,string state,int age,bool ground)
    {
        if(state.Contains("Wave")||state.Contains("Counter")||state.Contains("Rapid")||
           state.Contains("Dive In")||state.StartsWith("P2")||state.StartsWith("P3")||
           state.StartsWith("P4")||state.StartsWith("CS ")||state.StartsWith("Multihit")||
           state=="Charge"||state=="Tendril G Dash"||state=="Tendril A Whip"||
           state=="Downstab"||state.StartsWith("Dstab")||state.StartsWith("Quick Slash")||
           state.StartsWith("J Slash")||state=="Downstab Antic"||state=="Tendril A Antic"||
           state=="Crossup Antic"&&age>=12||state=="Tendril G Antic"&&age>=18||
           state.StartsWith("Combo")&&(state!="ComboSlash 1"||age>=13))
            return false;
        Bounds actor,victim;
        if(!ReadPinnedTarget(hero,out actor,out victim))return false;
        float dx=victim.center.x-actor.center.x;
        float dy=victim.center.y-actor.center.y;
        float gap=Mathf.Abs(dx);
        bool stun=state=="Stunned"||state.StartsWith("Stun ",StringComparison.Ordinal);
        if(gap>13.4f||Mathf.Abs(dy)>(stun?4.8f:2.1f))return false;
        float x=victim.center.x;
        bool rim=x>47.5f&&dx>0f||x<11f&&dx<0f;
        bool recent=frame-lastWanderNeedle<29;
        bool open=state=="Idle"||state=="Land"||state=="Downstab Land"||state=="Charge Recover"||
                  state=="Evade Recover"||state.StartsWith("Bullet S Ground")||
                  state.StartsWith("Hop Recover")||state=="Tendril G Whip End"||
                  state=="Summon Antic"||state=="Summon Thwip"||state=="Summon Turn"||
                  state=="Dive Out G"&&age>0;
        bool windup=state=="Charge Break"&&age<14||state=="Tendril G Antic"&&age<19||
                    state=="ComboSlash 1"&&age<15||state=="Crossup Antic"&&age<13;
        if(!stun&&!open&&!windup&&!(rim&&recent&&dy<1.4f))return false;
        var ownBody=hero.GetComponent<Rigidbody2D>();
        var enemyBody=boss.GetComponent<Rigidbody2D>();
        Vector2 velocity=ownBody.linearVelocity;
        Vector2 enemy=enemyBody?enemyBody.linearVelocity:Vector2.zero;
        string toward=dx<0f?"L":"R", away=dx<0f?"R":"L";
        float sign=dx<0f?-1f:1f;
        float nominal=sustainAttack?(stun?2.45f:rim?2.68f:2.95f):(stun?2.75f:rim?3.0f:3.27f);
        float nearEdge=actor.extents.x+victim.extents.x+.55f;
        float targetGap=gap+Mathf.Clamp(sign*(enemy.x-velocity.x)*.06f,-2.2f,2.2f);
        string move=targetGap>nominal+.12f?toward:targetGap<nearEdge?away:"";
        float floor=ground?actor.center.y:currentFloor+(actor.center.y-hero.transform.position.y);
        if(raceBuild.Contains("sprintroute")&&ground&&hero.cState.dashing&&
           previousRaceKeys.Contains(toward)&&previousRaceKeys.Contains("S")&&gap>3.35f&&
           Mathf.Abs(dy)<1.6f&&sign*enemy.x> -3f)
        {
            keys=toward+"S";
            return true;
        }
        bool sprint=raceBuild.Contains("sprintroute")&&ground&&move==toward&&gap>=6.1f&&
            gap<=12.4f&&Mathf.Abs(dy)<1.25f&&(stun||open||windup&&age<=7)&&sign*enemy.x>= -2f&&
            (hero.cState.isSprinting||hero.CanDash()&&frame-raceDash>=26);
        ReadHitboxes(hero);
        AddFlailForecast(hero.transform.position);
        PredictTelegraph(state,age,victim,actor,dx);
        string threat;
        float vx=move==toward?sign*hero.GetWalkSpeed():move==away?-sign*hero.GetWalkSpeed():0f;
        if(sprint)
        {
            float reach=(hero.DASH_SPEED+Mathf.Max(0f,-sign*enemy.x))*.165f+nearEdge;
            if(gap>reach&&!hero.cState.isSprinting&&
               Forecast(actor,velocity,floor,sign*hero.DASH_SPEED,0,0,true,false,out threat,9)<2.8f)
            {
                raceDash=frame;
                RacePress(toward+"S",4);
                return true;
            }
            if(hero.cState.isSprinting&&targetGap>4.55f&&
               (hero.cState.invulnerable||Forecast(actor,velocity,floor,sign*Mathf.Max(hero.GetWalkSpeed(),
                   Mathf.Abs(velocity.x)),0,0,false,false,out threat,6)<3.8f))
            {
                keys=toward+"S";return true;
            }
        }
        if(!hero.cState.invulnerable&&Forecast(actor,velocity,floor,vx,0,0,false,!ground,out threat,stun?6:9)>5.8f)
            return false;
        // Remain on the inside of the wall and move only enough to preserve the next slash.
        keys=move;
        return true;
    }

    private bool TryPinClash(HeroController hero,string state,bool ground)
    {
        if(!hero.CanAttack()||frame-raceAttack<=2||!ReadPressureHulls(hero)||
           state.Contains("Counter")||state.Contains("Wave")||state.StartsWith("CS ")||
           Mathf.Abs(boss.transform.position.x-hero.transform.position.x)>10f)
            return false;
        var hurt=hero.heroBox?hero.heroBox.GetComponent<Collider2D>():null;
        if(!hurt||!hurt.isActiveAndEnabled)return false;
        ReadHitboxes(hero);
        Bounds player=hurt.bounds;
        Vector2 speed=hero.GetComponent<Rigidbody2D>().linearVelocity;
        string input="", source="";
        float best=.21f;
        foreach(var weapon in track.Values)
        {
            if(!weapon.Damage||!weapon.Collider||!weapon.Damage.isActiveAndEnabled||
               !weapon.Collider.isActiveAndEnabled||!weapon.Damage.transform.IsChildOf(boss.transform)||
               !(bool)CanClashNeedle.GetValue(weapon.Damage)||
               (bool)PreventClashNeedle.GetValue(weapon.Damage))continue;
            Bounds immediate=weapon.Collider.bounds;
            var now=immediate;
            Vector3 move=(Vector3)((weapon.Velocity-speed)*(2f/60f));
            now.center+=move;
            var approaching=now;
            approaching.center+=move;
            float danger=Mathf.Max(PressureOverlap(now,player),PressureOverlap(approaching,player));
            if(danger<-.6f||danger>1.1f)continue;
            bool right=now.center.x>=player.center.x;
            bool same=hero.cState.facingRight==right;
            for(int family=0;family<3;family++)
            {
                if(family==2&&ground||!pressureHulls[family*2])continue;
                var sweep=WorldColliderShape(pressureHulls[family*2]);
                if(family==0&&!same)
                {
                    var mirrored=sweep.center;
                    mirrored.x=2*hero.transform.position.x-mirrored.x;
                    sweep.center=mirrored;
                }
                float overlap=Mathf.Max(PressureOverlap(sweep,now),PressureOverlap(sweep,approaching));
                if(overlap<=best)continue;
                best=overlap;
                input=family==1?"UA":family==2?"DA":(right?"RA":"LA");
                source=weapon.Damage.name;
            }
        }
        if(input.Length==0)return false;
        raceAttack=pressureAttackFrame=pinClashFrame=frame;
        pressureSwings++;
        RacePress(input,1);
        Note("PIN_CLASH_INPUT "+raceName+" "+(frame-raceStart)+" "+input+" "+source+" state="+state);
        return true;
    }

    private static void CountNeedleParry()
    {
        var owner=geometryOwner;
        if(owner==null||!owner.wanderRace||owner.raceName==null||owner.raceStart<0)return;
        owner.nativeParries++;
        Note("WANDER_NATIVE_PARRY "+owner.raceName+" "+(owner.frame-owner.raceStart)+" "+owner.raceState);
    }

    private bool TryWanderApproach(HeroController hero,string state,int age,bool ground)
    {
        bool orbit=raceBuild.Contains("orbitcut");
        bool airWindow=UseAirNeedleWindow(state,age);
        bool flowClose=flowAttack&&raceBuild.Contains("flowstep")&&CanFlowClose(state,age)&&
            (!flowMeasured||ProvenFlowWindow(state));
        bool closeOpening=airWindow||flowClose||sustainAttack&&
            (state=="Downstab Antic"&&age<15||state=="Tendril A Antic"&&age<14||
             state=="J Slash M Antic"&&age<14||state=="J Slash Multi"||
             state=="Bullet S Air"||state=="Dive Out A"||
             !ground&&(state.StartsWith("ComboSlash ")||state.Contains("Combo Strike")||
               state=="Charge"||state=="Downstab"||state=="Tendril G Dash"));
        bool attackSide=closeOpening||raceBuild.Contains("pinwindow")&&
            (state=="Tendril A Whip"||state=="Tendril G Whip"&&age>=8||
             state=="J Slash Multi"&&age>=4);
        if(boss.IsInvincible||!boss.gameObject.activeInHierarchy||!ReadPressureHulls(hero)||
           state.StartsWith("P2")||state.StartsWith("P3")||state.StartsWith("P4")||
           state.StartsWith("CS ")||state.Contains("Wave")||state.Contains("Counter")||
           state.Contains("Rapid")||state.Contains("Dive In")||state=="Tendril Emerge Wait"||
           state.StartsWith("Dstab")||
           (state=="Downstab"||state=="Downstab Antic"||state=="Tendril A Antic"||state.StartsWith("J Slash"))&&!attackSide||
           state.Contains("Multihit")||state=="Tendril A Whip"&&!attackSide||
           state=="Tendril G Antic"&&age>=22||state=="Tendril G Dash"&&!closeOpening||state=="Tendril G Whip"&&age<26&&!attackSide||
           state=="Charge Break"&&age>=19||state=="Crossup Antic"&&age>=19||
           state=="Charge"&&!closeOpening||state.StartsWith("Quick Slash")||
           state.StartsWith("ComboSlash")&&(state!="ComboSlash 1"||age>=17)&&!closeOpening||
           state.Contains("Combo Strike")&&!closeOpening)
            return false;
        var target=LaceCollider.GetValue(boss) as Collider2D;
        if(!target||!target.isActiveAndEnabled)
            target=boss.GetComponents<Collider2D>().FirstOrDefault(c=>c.isActiveAndEnabled&&c.bounds.size.x>.25f);
        var hurt=hero.heroBox?hero.heroBox.GetComponent<Collider2D>():null;
        if(!target||!hurt||!hurt.isActiveAndEnabled)return false;
        var victim=target.bounds;
        var own=hurt.bounds;
        var self=hero.transform.position;
        var body=hero.GetComponent<Rigidbody2D>();
        var enemy=boss.GetComponent<Rigidbody2D>();
        Vector2 en=enemy?enemy.linearVelocity:Vector2.zero;
        Vector2 me=body.linearVelocity;
        float gap=victim.center.x-own.center.x;
        float rise=victim.center.y-own.center.y;
        if(Mathf.Abs(gap)>16f||victim.center.y<4.2f||Mathf.Abs(rise)>12.5f)return false;
        float floor=ground?own.center.y:currentFloor+(own.center.y-self.y);
        bool lift=ground?rise>(airWindow?1.35f:flowClose&&state=="Stun Air"?1.35f:sustainAttack?1.20f:1.65f)&&
            hero.CanJump()&&frame-raceJump>10:
            hero.CanDoubleJump(false)&&frame-raceDouble>16&&frame-raceJump>13&&
            rise>(airWindow?2.55f:3.65f)&&me.y<(airWindow?9f:7f);
        bool hold=!ground&&previousRaceKeys.Contains("J")&&frame-raceJump<18;
        bool slide=ground&&Mathf.Abs(gap)>(orbit?6.7f:sustainAttack?7.4f:8.5f)&&rise<2.0f&&rise> -2.0f&&
            hero.CanDash()&&frame-raceDash>27&&Mathf.Sign(gap)*en.x> -6f;
        ReadHitboxes(hero);
        AddFlailForecast(self);
        PredictTelegraph(state,age,victim,own,gap);
        float best=float.PositiveInfinity;
        int bestMode=-1, bestBearing=0, bestMeet=-1, bestFamily=-1;
        var strike=new Bounds[3];
        for(int k=0;k<3;k++)strike[k]=pressureHulls[k*2]?WorldColliderShape(pressureHulls[k*2]):new Bounds();
        int recentlyHit=frame-raceAttack;
        int ready=hero.CanAttack()?1:Mathf.Clamp(Mathf.CeilToInt(60f*hero.Config.AttackCooldownTime)-recentlyHit,2,17);
        int ahead=24;
        bool reckless=raceBuild.Contains("stickmax")&&PlayerData.instance.health>=6;
        for(int mode=0;mode<3;mode++)
        {
            if(mode==1&&!lift||mode==2&&!slide)continue;
            for(int bearing=-1;bearing<=1;bearing++)
            {
                if(mode==2&&bearing!=(gap>0?1:-1))continue;
                float speed=bearing*(mode==2?hero.DASH_SPEED:hero.GetWalkSpeed());
                float jump=mode==1?(ground?hero.JUMP_SPEED:17.2f):0f;
                string hazard;
                float risk=Forecast(own,me,floor,speed,jump,mode==1?14:hold?8:0,mode==2,!ground,
                    out hazard,mode==2?10:15);
                if(!hero.cState.invulnerable&&risk>(attackSide?2.7f:reckless?11.5f:6.9f))continue;
                var pos=own.center;
                float vx=me.x,vy=jump>0f?jump:me.y;
                float closest=50f;
                int meet=-1, family=-1;
                for(int t=1;t<=ahead;t++)
                {
                    float demand=mode==2&&t>9?bearing*hero.GetWalkSpeed():speed;
                    vx=Mathf.Lerp(vx,demand,mode==2&&t<=9?.83f:.42f);
                    if(mode==2&&t<=9)vy=0;
                    else if(mode==1||!ground||pos.y>floor+.1f)
                        vy-=(vy>0f&&(mode==1&&t<=14||hold)?34f:57f)/60f;
                    pos.x+=vx/60f;
                    pos.y=Mathf.Max(floor,pos.y+vy/60f);
                    if(pos.x<4.0f||pos.x>54.65f)break;
                    if(t<ready)continue;
                    var moved=victim;
                    moved.center+=new Vector3(Mathf.Clamp(en.x*t/60f,-5.2f,5.2f),
                        Mathf.Clamp(en.y*t/60f,-4.3f,4.3f),0f);
                    bool face=moved.center.x>=pos.x;
                    for(int k=0;k<3;k++)
                    {
                        if(strike[k].size.x<.15f||k==2&&pos.y<floor+.5f)continue;
                        var sweep=strike[k];
                        var offset=sweep.center-own.center;
                        if(k==0&&face!=hero.cState.facingRight)offset.x=-offset.x;
                        sweep.center=pos+offset;
                        float proximity=PressureOverlap(sweep,moved);
                        float need=airWindow?(k==0?.34f:k==1?.12f:.22f):.11f;
                        if(proximity>need&&(meet<0||airWindow&&k==1&&family==0&&
                           t-meet<=(preferAirUp?4:2)&&moved.center.y>pos.y+1.2f))
                        {
                            meet=t;family=k;
                        }
                        closest=Mathf.Min(closest,Mathf.Max(0f,(airWindow?need:0f)-proximity)+t*.060f);
                    }
                }
                if(flowClose&&meet<0)continue;
                float score=(meet>=0?meet*(sustainAttack||flowClose?.36f:.24f):6.2f+closest*1.27f)+
                    (hero.cState.invulnerable?.11f:reckless?.17f:.42f)*risk+
                    (mode==1?(ground?.20f:.80f):mode==2?.68f:0f);
                if(airWindow&&family==1)score-=preferAirUp?1.35f:.62f;
                if(mode==0&&bearing==0)score-=.14f;
                if(score>=best)continue;
                best=score;bestMode=mode;bestBearing=bearing;bestMeet=meet;bestFamily=family;
            }
        }
        if(bestMode<0||bestMeet<0&&(attackSide||bestMode==1||best>11.4f))return false;
        if(orbit&&bestMode==0&&bestMeet<0&&Mathf.Abs(gap)>7.5f&&
           (state=="Stunned"||state=="Dive Out G"||state=="Evade Recover"||state=="Charge Recover")&&
           hero.CanDash()&&frame-raceDash>=26&&Mathf.Abs(rise)<1.6f&&
           Mathf.Sign(gap)*en.x>=-.5f)
        {
            string hazard;
            if(Forecast(own,me,floor,Mathf.Sign(gap)*hero.DASH_SPEED,0,0,true,!ground,out hazard,10)<2.5f)
            {
                raceDash=frame;
                RacePress((gap>0?"R":"L")+"S",5);
                return true;
            }
        }
        string direction=bestBearing<0?"L":bestBearing>0?"R":"";
        if(bestMode==1)
        {
            if(ground)raceJump=frame;else raceDouble=frame;
            RacePress(direction+"J",ground?10:8);
            if(airWindow)
                Note("AIR_WINDOW_LIFT "+raceName+" "+(frame-raceStart)+" "+keys+" state="+state+
                     " reach="+bestMeet+" family="+bestFamily);
            Note("WANDER_CHASE "+raceName+" "+(frame-raceStart)+" "+keys+" reach="+bestMeet+" state="+state+
                 " rise="+rise.ToString("F2",CultureInfo.InvariantCulture)+" skillWindow="+attackSide);
        }
        else if(bestMode==2)
        {
            raceDash=frame;RacePress(direction+"S",5);
        }
        else keys=direction+(hold?"J":"");
        return true;
    }

    private bool TryCloseContact(HeroController hero,string state,int age,float dx,float dy,bool ground)
    {
        if(state.StartsWith("P2",StringComparison.Ordinal)||state.StartsWith("P3",StringComparison.Ordinal)||
           state.StartsWith("P4",StringComparison.Ordinal)||state.StartsWith("CS ",StringComparison.Ordinal)||
           state.Contains("Counter")||state.Contains("Wave")||state.Contains("Tendril Emerge")||
           state.Contains("Downstab")&&!state.Contains("Land")||state.StartsWith("Dstab",StringComparison.Ordinal)||
           state.StartsWith("J Slash",StringComparison.Ordinal)||state.StartsWith("Tendril A",StringComparison.Ordinal)||
           state=="Crossup Antic"||state=="Charge Strike"||state=="Charge"||
           (state=="Charge Break"&&age>=17)||
           (state.StartsWith("ComboSlash ",StringComparison.Ordinal)&&(state!="ComboSlash 1"||age>=18))||
           (state=="Tendril G Antic"&&age>=19)||state=="Tendril G Dash"||
           state=="Tendril G Whip"&&age<30)
            return false;
        var pos=hero.transform.position;
        if(!hero.heroBox||!boss.gameObject.activeInHierarchy||boss.transform.position.y<4.5f||
           Mathf.Abs(dx)>19f)return false;
        var pc=hero.heroBox.GetComponent<Collider2D>();
        var hc=LaceCollider.GetValue(boss) as Collider2D;
        if(!hc||!hc.isActiveAndEnabled)hc=boss.GetComponents<Collider2D>().FirstOrDefault(c=>c.isActiveAndEnabled&&c.bounds.size.x>.25f);
        if(!pc||!hc||!pc.isActiveAndEnabled)return false;
        var player=pc.bounds;
        var victim=hc.bounds;
        var rb=hero.GetComponent<Rigidbody2D>();
        var enemy=boss.GetComponent<Rigidbody2D>();
        float floor=ground?player.center.y:currentFloor+(player.center.y-pos.y);
        Vector2 velocity=rb.linearVelocity;
        Vector2 opponent=enemy?enemy.linearVelocity:Vector2.zero;
        bool edge=raceBuild.Contains("longedge");
        float desired=Mathf.Max(edge?3.65f:2.6f,player.extents.x+victim.extents.x+.65f);
        float speed=hero.GetWalkSpeed();
        bool canDash=hero.CanDash()&&frame-raceDash>25&&Mathf.Abs(dx)>5.2f;
        bool jump=ground&&hero.CanJump()&&frame-raceJump>17;
        ReadHitboxes(hero);
        AddFlailForecast(pos);
        PredictTelegraph(state,age,victim,player,dx);
        string bestInput="";
        float lowest=float.PositiveInfinity;
        bool quick=raceBuild.Contains("hardcontact");
        for(int a=0;a<8;a++)
        {
            int bearing=a==0||a==3?0:a%3==1?-1:1;
            bool lift=a>=3&&a<=5;
            bool dash=a>=6;
            if(dash&&!canDash||lift&&!jump)continue;
            if(a==6)bearing=-1;
            if(a==7)bearing=1;
            float vx=bearing*(dash?hero.DASH_SPEED:speed);
            if(dash&&bearing*(victim.center.x-player.center.x)>0)
            {
                float closing=Mathf.Max(0f,-bearing*opponent.x);
                float required=(hero.DASH_SPEED+closing)*.185f+player.extents.x+victim.extents.x+.55f;
                if(Mathf.Abs(victim.center.x-player.center.x)<required&&player.center.y<victim.max.y+1.0f)
                    continue;
            }
            float targetX=player.center.x+vx*(dash?.135f:.195f);
            float targetY=player.center.y+(lift?2.7f:ground?0f:velocity.y*.105f);
            if(targetX<4.35f||targetX>54.1f)continue;
            string danger;
            float risk=Forecast(player,velocity,floor,vx,lift?hero.JUMP_SPEED:0f,lift?11:0,dash,!ground,out danger,12);
            float bossX=victim.center.x+Mathf.Clamp(opponent.x*.135f,-2.7f,2.7f);
            float bossY=victim.center.y+Mathf.Clamp(opponent.y*.085f,-1.7f,1.7f);
            float gap=Mathf.Abs(bossX-targetX);
            float dy0=bossY-targetY;
            float attackDistance=Mathf.Abs(gap-desired);
            if(lift&&dy0< -1.8f&&dy0> -4.3f)attackDistance=Mathf.Abs(gap-1.25f)*.68f;
            float riskWeight=hero.cState.invulnerable?.19f:quick?.46f:.72f;
            if(raceBuild.Contains("whipflee")&&PlayerData.instance.health<=4&&!hero.cState.invulnerable)riskWeight=.94f;
            float score=riskWeight*risk+attackDistance+
                Mathf.Max(0,Mathf.Abs(dy0)-1.8f)*.46f;
            float minimum=edge?2.28f:1.45f;
            if(gap<minimum&&Mathf.Abs(dy0)<1.8f)score+=(minimum-gap)*4.1f;
            if(lift)score+=.60f;
            if(dash)score+=quick?.40f:1.15f;
            if(a==0)score-=.12f;
            if(score>=lowest)continue;
            lowest=score;
            bestInput=(bearing<0?"L":bearing>0?"R":"")+(lift?"J":dash?"S":"");
        }
        if(float.IsInfinity(lowest))return false;
        if(bestInput.IndexOf('S')>=0)
        {
            raceDash=frame;RacePress(bestInput,5);
        }
        else if(bestInput.IndexOf('J')>=0)
        {
            raceJump=frame;RacePress(bestInput,8);
        }
        else keys=bestInput;
        return true;
    }

    private bool JumpForWanderPogo(HeroController hero,string state,int age,float dx,float dy,bool ground)
    {
        bool combo=state=="ComboSlash 1";
        bool charge=state=="Charge Break";
        bool whip=state=="Tendril G Antic";
        bool transit=state=="ComboSlash 2"||state=="ComboSlash 3"||state=="Charge"||state=="Tendril G Dash"||
            raceBuild.Contains("pinlace")&&(state.StartsWith("ComboSlash ")||state.StartsWith("Combo Strike"));
        if(!combo&&!charge&&!whip&&!transit)return false;
        float gap=Mathf.Abs(dx);
        if(gap<1.6f||gap>7.2f||dy>2f)return false;
        string toward=dx<0f?"L":"R";
        if(ground&&hero.CanJump()&&frame-raceJump>20&&
          (combo&&age>=16&&age<=28||charge&&age>=20&&age<=27||whip&&age>=21&&age<=32))
        {
            raceJump=frame;
            int hold=raceBuild.Contains("pinlace")?13:9;
            RacePress((gap>5.35f?toward:"")+"J",hold);
            if(raceBuild.Contains("pinlace"))pressureLiftUntil=frame+hold;
            return true;
        }
        var body=hero.GetComponent<Rigidbody2D>();
        bool comboMiddle=state=="ComboSlash 2"||state=="ComboSlash 3"||
                         state=="ComboSlash 4"||state=="ComboSlash 5";
        if(raceBuild.Contains("pinlace")&&comboMiddle&&gap<7.1f&&
           hero.transform.position.y-currentFloor<3.7f)
        {
            bool vault=ground&&hero.CanJump()&&frame-raceJump>18;
            bool liftAgain=!ground&&body.linearVelocity.y<1.4f&&hero.CanDoubleJump(false)&&
                frame-raceDouble>18&&frame-raceJump>13;
            if(vault||liftAgain)
            {
                if(vault)raceJump=frame;else raceDouble=frame;
                RacePress((gap>4.9f?toward:"")+"J",12);
                Note("WANDER_COMBO_VAULT "+raceName+" "+(frame-raceStart)+" "+state+" input="+keys);
                return true;
            }
        }
        if(!ground&&dy< -2.65f&&dy> -6.1f&&body.linearVelocity.y<3f&&
           frame-raceJump<=26&&gap>2.1f)
        {
            keys=toward;
            return true;
        }
        return false;
    }

    private bool CloseForWanderSwing(HeroController hero,string state,int age,float dx,float dy,bool ground)
    {
        float gap=Mathf.Abs(dx);
        bool recovery=state=="Downstab Land"||state=="Charge Recover"||state=="Evade Recover"||
                      state=="Tendril A End"||state=="Tendril G Whip End"||state=="Stunned";
        bool emerging=state=="Dive Out G"&&age<=15;
        bool openings=recovery||emerging||state=="Land"||state=="Idle"||
                      (state.StartsWith("Bullet S Ground",StringComparison.Ordinal)&&age<20);
        if(!openings||Mathf.Abs(dy)>(emerging?2.8f:1.8f))return false;
        bool edge=raceBuild.Contains("longedge");
        if(gap<(edge?3.72f:3.20f))
        {
            if(edge&&gap<2.25f&&Mathf.Abs(dy)<1.25f)
                keys=dx>0?"L":"R";
            return edge;
        }
        string toward=dx<0f?"L":"R";
        if(raceBuild.Contains("chasehook")&&recovery&&ground&&gap>=5.7f&&gap<=10.8f&&
           Mathf.Abs(dy)<1.15f&&(!boss.IsInvincible||state=="Stunned")&&PlayerData.instance.silk>=11&&
           frame-lastVomit>170&&frame-lastBulletAt>155&&frame-raceClaw>48&&hero.CanHarpoonDash())
        {
            var hood=hero.heroBox?hero.heroBox.GetComponent<Collider2D>():null;
            if(hood)
            {
                string hz;
                ReadHitboxes(hero);
                float aim=dx<0?-1f:1f;
                float safety=Forecast(hood.bounds,hero.GetComponent<Rigidbody2D>().linearVelocity,
                    hood.bounds.center.y,aim*hero.DASH_SPEED,0f,0,true,false,out hz,8);
                if(safety<1.3f)
                {
                    raceClaw=frame;RacePress(toward+"H",3);
                    Note("WANDER_CHASE_HOOK "+raceName+" "+(frame-raceStart)+" gap="+gap+" "+state);
                    return true;
                }
            }
        }
        if(ground&&gap>=(emerging?8.8f:7.2f)&&gap<=12.6f&&(age<=9||state=="Stunned")&&
           frame-raceDash>26&&hero.CanDash())
        {
            var h=hero.heroBox?hero.heroBox.GetComponent<Collider2D>():null;
            if(h)
            {
                ReadHitboxes(hero);
                AddFlailForecast(hero.transform.position);
                string threat;
                float dir=dx<0?-1f:1f;
                float risk=Forecast(h.bounds,hero.GetComponent<Rigidbody2D>().linearVelocity,
                    h.bounds.center.y,dir*hero.DASH_SPEED,0f,0,true,false,out threat,8);
                if(risk<4.25f)
                {
                    raceDash=frame;RacePress(toward+"S",4);return true;
                }
            }
        }
        keys=toward;
        return true;
    }

    private bool DriveWanderPunishes(HeroController hero,string state,int age,float dx,float dy,bool ground)
    {
        float gap=Mathf.Abs(dx);
        bool rise=state=="Dive Out G"&&age<14;
        bool landing=state=="Downstab Land"&&age<22;
        bool rest=(state=="Evade Recover"&&age<10)||(state=="Charge Recover"&&age<13)||
                  (state=="Tendril A End"&&age<8)||(state=="Stunned");
        bool bullets=(state=="Bullet S Ground 1"||state=="Bullet S Ground 2")&&age<20;
        bool idle=state=="Idle"&&age<7;
        bool windup=raceBuild.Contains("earlypunish")&&
            ((state=="ComboSlash 1"&&age<12)||(state=="Tendril G Antic"&&age<9)||
             (state=="J Slash M Antic"&&age<10)||(state=="Charge Antic"&&age<5));
        if(!rise&&!landing&&!rest&&!bullets&&!idle&&!windup)return false;
        if(Mathf.Abs(dy)>(rise?2.8f:1.5f))return false;
        string direction=dx<0?"L":"R";
        if(!boss.IsInvincible&&gap>1.05f&&gap<(wanderLongneedle?4.20f:3.83f)&&
           hero.CanAttack()&&frame-raceAttack>2)
        {
            raceAttack=frame;
            RacePress(direction+"A",2);
            return true;
        }
        if(windup)return false;
        if((rise&&age>=3&&age<8||landing&&age<=8||rest&&age<=5||bullets&&age<=5)&&
            ground&&gap>4.3f&&gap<8.2f&&hero.CanDash()&&frame-raceDash>27)
        {
            raceDash=frame;RacePress(direction+"S",5);return true;
        }
        if(gap>2.95f)keys=direction;
        return gap>2.95f;
    }

    private bool TryRangedPale(HeroController hero,string state,int age,float dx,float dy,int silk,string face)
    {
        if(boss.IsInvincible||silk<PlayerData.instance.SilkSkillCost||frame-raceCast<48||!hero.CanCast())return false;
        float gap=Mathf.Abs(dx);
        if(gap<(wanderRace?3.6f:4.45f)||gap>14.2f||Mathf.Abs(dy)>5.75f)return false;
        bool window=(state=="Downstab Land"&&age<14)||
            (state=="Tendril G Whip End"&&age<7)||
            (state=="Tendril A End"&&age<8)||
            (state=="Charge Recover"&&age<9)||
            (state=="Counter End"&&age<6&&gap>=6.2f)||
            (state=="J Slash Multi"&&age<5&&gap>=10.0f&&dy>1.4f)||
            (state=="Dive Out A"&&age<6&&gap>=9.5f)||
            (raceBuild.Contains("bulletcast")&&age<15&&
             (state=="Bullet S Ground 1"||state=="Bullet S Ground 2")&&gap>=5.0f&&gap<=11.2f)||
            (state=="Stunned"&&age<36)||
            wanderRace&&((state=="Bullet S Air"&&age<16&&dy>2.5f)||
                (state=="Tendril G Antic"&&age<9&&gap>5.0f)||
                (state=="Land"&&age<11&&gap>5.2f));
        if(!window)return false;
        raceCast=frame;RacePress(face+"C",3);
        Note("PALE_WINDOW "+raceName+" "+(frame-raceStart)+" "+state+" age="+age+" dx="+dx+" dy="+dy+" silk="+silk);
        return true;
    }

    private bool GuardAerialPass(HeroController hero,string state,int age,float dx,float dy,bool ground)
    {
        bool stab=state=="Downstab Antic";
        bool whip=state=="Tendril A Antic";
        if((!stab&&!whip)||age>10||dy<4.3f||Mathf.Abs(dx)>6.1f||!ground)return false;
        float x=hero.transform.position.x;
        string travel=dx>0f?"R":"L";
        if((travel=="R"&&x>48f)||(travel=="L"&&x<10f))return false;
        if(hero.CanDash()&&frame-raceDash>25)
        {
            raceDash=frame;
            positioningMove=stab?"stab":"aerial";
            positioningDirection=travel;
            positioningSince=frame;
            positioningDash=frame;
            RacePress(travel+"S",10);
            return true;
        }
        return false;
    }

    private bool CounterReadyForNeedle()
    {
        if(raceState!="Idle"||!bossControl)return false;
        var ready=bossControl.FsmVariables.GetFsmBool("Counter Ready");
        var range=bossControl.FsmVariables.GetFsmBool("Counter Range");
        return ready!=null&&ready.Value&&range!=null&&range.Value;
    }

    private bool GuardRiposte(HeroController hero,string state,int age,float dx,bool ground)
    {
        bool triggered=state=="Counter Hit"||state=="Counter TeleOut";
        bool teleport=state=="Counter TeleIn";
        bool airCombo=state=="RapidSlashAir Antic"||state=="RapidSlash Air";
        bool groundCombo=state=="RapidSlash Charge"||state=="RapidSlash Loop";
        if(!triggered&&!teleport&&!airCombo&&!groundCombo)return false;

        float gap=Mathf.Abs(dx);
        float pos=hero.transform.position.x;
        string away=dx>0f?"L":"R";
        string towards=dx>0f?"R":"L";
        if((pos<9f&&away=="L")||(pos>50f&&away=="R"))away=towards;
        string center=pos<29f?"R":"L";
        bool dash=hero.CanDash()&&frame-raceDash>=23;
        if(raceBuild.Contains("counterhop")&&ground&&gap<10.2f&&hero.CanJump()&&
           frame-raceJump>16&&!previousRaceKeys.Contains("J")&&
           (state=="Counter Hit"&&age>=25||groundCombo&&age<=5))
        {
            raceJump=frame;
            RacePress((pos<10f||pos>49f?center:away)+"J",8);
            Note("COUNTER_HOP "+raceName+" "+(frame-raceStart)+" state="+state+" age="+age);
            return true;
        }
        if(triggered)
        {
            // Keep an exit lane before the teleport chooses a side.
            keys=pos<22f||pos>38f?center:gap<10.5f?away:"";
            return true;
        }
        keys=gap<13f?away:"";
        if(dash&&gap<11.7f)
        {
            raceDash=frame; RacePress(away+"S",10);
            Note("RIPOSTE_EXIT "+raceName+" "+(frame-raceStart)+" "+state+" gap="+gap+" "+away);
            return true;
        }
        if(ground&&hero.CanJump()&&frame-raceJump>20&&gap<6.3f&&groundCombo)
        {
            raceJump=frame;RacePress(away+"J",12);return true;
        }
        if(!ground&&hero.CanDoubleJump(false)&&frame-raceDouble>18&&gap<6.3f&&
           (groundCombo||airCombo&&hero.transform.position.y>9.1f))
        {
            raceDouble=frame;RacePress(away+"J",10);return true;
        }
        return true;
    }

    private bool GuardChainDistance(HeroController hero,string state,int age,float dx,bool ground)
    {
        bool telegraph=state=="ComboSlash 1"||state=="Quick Slash 1";
        bool chain=state.StartsWith("ComboSlash ",StringComparison.Ordinal)||
                   state.StartsWith("Quick Slash ",StringComparison.Ordinal)||state=="Combo Strike";
        if(!chain)return false;
        float x=hero.transform.position.x;
        float y=hero.transform.position.y;
        float gap=Mathf.Abs(dx);
        bool inCorner=x<9f||x>50f;
        string retreat=inCorner?(x<30f?"R":"L"):dx>0f?"L":"R";
        if(telegraph&&age<8&&gap>3.1f&&!inCorner)return false;
        if(raceBuild.Contains("comboover")&&!ground&&y>9.75f&&gap<6.5f&&
           hero.CanDash()&&frame-raceDash>26)
        {
            raceDash=frame;RacePress((dx>0f?"R":"L")+"S",8);return true;
        }
        if(!ground&&hero.CanDoubleJump(false)&&frame-raceDouble>26&&frame-raceJump>12&&gap<6.0f&&
            y<9.9f&&hero.GetComponent<Rigidbody2D>().linearVelocity.y<2.4f)
        {
            raceDouble=frame;RacePress(retreat+"J",8);return true;
        }
        if(ground&&hero.CanJump()&&frame-raceJump>17&&gap<6.0f&&
           (!telegraph||state=="Quick Slash 1"||age>=15))
        {
            raceJump=frame;RacePress(retreat+"J",inCorner?17:12);return true;
        }
        if(!inCorner&&hero.CanDash()&&frame-raceDash>26&&gap<7.5f&&telegraph&&
           (state=="Quick Slash 1"||age>=4&&age<=15))
        {
            raceDash=frame;RacePress(retreat+"S",10);return true;
        }
        keys=gap<10f?retreat:"";
        return true;
    }

    private bool GuardChargeHeight(HeroController hero,string state,int age,float dx,bool ground)
    {
        if(state=="Crossup Antic"&&age==0)
        {
            priorCrossChain=frame-crossTelegraph<94;
            crossTelegraph=frame;
        }
        bool cross=state=="Crossup Antic"||(state=="Charge Strike"&&frame-crossTelegraph<54)||
                   (state=="Charge"&&frame-crossTelegraph<=45);
        bool first=state=="Charge Break"||state=="Charge"||state=="Charge Antic";
        if(!cross&&!first)return false;
        float x=hero.transform.position.x;
        float y=hero.transform.position.y;
        float vy=hero.GetComponent<Rigidbody2D>().linearVelocity.y;
        float gap=Mathf.Abs(dx);
        string away=dx>0f?"L":"R";
        string towards=dx>0f?"R":"L";
        if((x<10f&&away=="L")||(x>50f&&away=="R"))away=towards;
        bool grounded=ground&&hero.CanJump()&&frame-raceJump>15;
        bool airJump=!ground&&hero.CanDoubleJump(false)&&frame-raceDouble>20&&frame-raceJump>=10;
        if(wanderRace&&raceBuild.Contains("pinwake")&&airJump&&vy<1.8f&&y<10.4f&&gap<9.1f&&
           (state=="Charge"&&age>=5||state=="Crossup Antic"&&age<=13&&frame-lastChargeSeen<20))
        {
            raceDouble=frame;
            RacePress("J",12);
            Note("WAKE_LIFT "+raceName+" "+(frame-raceStart)+" "+state+" y="+
                y.ToString("F2",CultureInfo.InvariantCulture)+" gap="+gap.ToString("F2",CultureInfo.InvariantCulture));
            return true;
        }
        if(cross)
        {
            int crossAge=frame-crossTelegraph;
            bool leave=raceBuild.Contains("chargeescape")&&priorCrossChain&&
                ((away=="R"&&dx<0&&x<48.5f)||(away=="L"&&dx>0&&x>9.0f));
            int liftAt=(racePolicy%3==0?14:17);
            if(leave)
            {
                if(state=="Crossup Antic"&&crossAge>=23&&gap<14.5f&&hero.CanDash()&&frame-raceDash>32)
                {
                    raceDash=frame;RacePress(away+"S",10);
                    Note("CHARGE_EXIT "+raceName+" "+(frame-raceStart)+" age="+crossAge+" gap="+gap+" "+away);
                    return true;
                }
                if(crossAge<30)
                {
                    keys=gap<16.5f?away:"";return true;
                }
            }
            if(gap>17f&&state=="Crossup Antic"){keys="";return true;}
            if(wanderRace&&raceBuild.Contains("groundpress")&&!leave&&state=="Crossup Antic"&&grounded&&
               crossAge<13&&gap>4.05f&&gap<9.5f)
            {
                keys=towards;
                return true;
            }
            if((grounded&&crossAge>=liftAt&&gap<18f)||
               (airJump&&crossAge>=liftAt+4&&vy<4.0f&&gap<18f))
            {
                if(grounded)raceJump=frame; else raceDouble=frame;
                string input=(y>8.8f&&!leave&&(!raceBuild.Contains("chargeescape")||gap<4.75f)?towards:away)+"J";
                RacePress(input,raceBuild.Contains("chargehold")?16:12);
                if(frame-chargeHeightNote>5){chargeHeightNote=frame;Note("CHARGE_HEIGHT "+raceName+" "+(frame-raceStart)+" crossAge="+crossAge+" y="+y+" vy="+vy+" "+input);}
                return true;
            }
            bool canCross=y>9.35f&&gap<(raceBuild.Contains("chargeescape")?4.95f:11f)&&!leave;
            keys=canCross?towards+"J":gap<(raceBuild.Contains("chargeescape")?14f:9f)?away:"";
            if(raceBuild.Contains("chargehold")&&!ground&&frame-raceDouble<21&&!keys.Contains("J"))keys+="J";
            return true;
        }
        if(state=="Charge Antic")
        {
            if(wanderRace&&raceBuild.Contains("middleafterwave")&&boss.hp<=900&&
               (x<10f||x>48f)&&gap>=5.5f)
            {
                string middle=x<10f?"R":"L";
                if(middle==away&&hero.CanDash()&&frame-raceDash>28)
                {
                    raceDash=frame;RacePress(middle+"S",8);
                }
                else if(middle==away)keys=middle;
                else if(grounded)
                {
                    raceJump=frame;RacePress(middle+"J",10);
                }
                else keys=middle;
                return true;
            }
            return false;
        }
        bool longLift=raceBuild.Contains("chargehold");
        if(raceBuild.Contains("wakeheight")&&state=="Charge"&&!ground&&y<11.1f&&
           age>=7&&age<=16&&airJump&&vy<.3f&&gap<11f)
        {
            if(previousRaceKeys.Contains("J")){keys="";return true;}
            raceDouble=frame;
            RacePress("J",10);
            Note("PRESSURE_WAKE_LIFT "+raceName+" "+(frame-raceStart)+" age="+age);
            return true;
        }
        int liftFrame=longLift?(int)Mathf.Clamp((raceBuild.Contains("chargelate")?24f:21f)+(gap-6f)*.9f,17f,29f):13;
        bool directSoon=(state=="Charge Break"&&age>=liftFrame&&gap<(racePolicy%2==0?10.2f:12.4f))||
                        (state=="Charge"&&gap<9.7f);
        if(grounded&&directSoon)
        {
            raceJump=frame;
            RacePress(longLift?"J":away+"J",longLift?5:10);
            if(longLift)Note("CHARGE_LIFT "+raceName+" "+(frame-raceStart)+" "+state+" age="+age+" gap="+gap);
            return true;
        }
        bool reserveHigh=wanderRace&&raceBuild.Contains("jumpcut")&&state=="Charge Break"&&age>=23;
        if((state=="Charge"||reserveHigh)&&airJump&&vy<(longLift?1.9f:1.25f)&&
           y<(longLift?9.2f:8.15f)&&gap<(longLift?8.0f:5.5f))
        {
            raceDouble=frame; RacePress(away+"J",7);return true;
        }
        if(state=="Charge Break"&&age<=9&&gap<=4.3f)return false;
        if(longLift&&!ground&&frame-raceJump<22)
            keys=(y>8.7f&&gap<9f?towards:gap<5f?away:"")+"J";
        else if(gap<9f)keys=y>9.4f?towards:away;
        return true;
    }

    private bool TryPositionGuard(HeroController hero,string state,int age,float dx,float dy,bool ground)
    {
        bool stab=state=="Downstab Antic"||state=="Downstab"||state=="Dstab Multihit";
        bool aerial=state=="Tendril A Antic"||state=="Tendril A Whip"||state=="Tendril A Dash";
        bool slash=state=="J Slash M Antic"||state=="J Slash Multi"||state=="J Slash Multihit";
        bool cross=state=="Crossup Antic"||state=="Charge Strike";
        string move=stab?"stab":aerial?"aerial":slash?"slash":cross?"cross":"";
        if(move.Length==0)
        {
            positioningMove="";
            return false;
        }

        var pos=hero.transform.position;
        float distance=Mathf.Abs(dx);
        if(positioningMove!=move)
        {
            positioningMove=move; positioningSince=frame;
            positioningDash=-9000;
            positioningDirection=dx>=0f?"L":"R";
            float roomToWall=positioningDirection=="L"?pos.x-3.73f:54.69f-pos.x;
            if(roomToWall<4.25f&&distance<6.3f)
                positioningDirection=positioningDirection=="L"?"R":"L";
            if(raceBuild.Contains("scoutguard"))
                Note("POS_GUARD "+raceName+" "+(frame-raceStart)+" "+move+" "+positioningDirection+" x="+pos.x+" dx="+dx+" dy="+dy);
        }
        int activeAge=frame-positioningSince;
        bool edge=(pos.x<8f&&positioningDirection=="L")||(pos.x>51f&&positioningDirection=="R");
        if(edge&&activeAge<16&&distance<5.2f)positioningDirection=pos.x<30f?"R":"L";
        string travel=positioningDirection;
        keys=travel;
        bool dash=hero.CanDash()&&frame-raceDash>24&&frame-positioningDash>45;
        bool jump=hero.CanJump()&&ground&&frame-raceJump>18;
        if(stab)
        {
            // The attack tracks during its rise. Keep the same exit direction through the impact.
            if(wanderRace&&(raceBuild.Contains("needlepress")||raceBuild.Contains("attackzones"))&&
               state=="Downstab"&&age>=3&&distance>6.2f)
            {
                keys="";return true;
            }
            if(dash&&distance<7.8f&&state=="Downstab"&&age>=1)
            {
                raceDash=positioningDash=frame; RacePress(travel+"S",9); return true;
            }
            if(state=="Dstab Multihit"&&age>=10&&distance>5.5f)return false;
            return true;
        }
        if(aerial)
        {
            // Outside its sweep, continuing to retreat is safer than following the airborne origin.
            if(dash&&distance<12.0f&&((state=="Tendril A Antic"&&age>=18)||state!="Tendril A Antic"))
            {
                raceDash=positioningDash=frame; RacePress(travel+"S",9); return true;
            }
            if(distance>=13.5f)keys="";
            return true;
        }
        if(slash)
        {
            if(dash&&distance<11.2f&&((state=="J Slash M Antic"&&age>=10)||state!="J Slash M Antic"))
            {
                raceDash=positioningDash=frame; RacePress(travel+"S",9); return true;
            }
            if(jump&&distance<5f&&dx*(travel=="R"?1f:-1f)>0f&&state=="J Slash M Antic"&&age<=12)
            {
                raceJump=frame; RacePress(travel+"J",10); return true;
            }
            if(distance>=12f)keys="";
            return true;
        }
        if(dash&&distance<8.7f&&((state=="Crossup Antic"&&age>=20)||state=="Charge Strike"))
        {
            raceDash=positioningDash=frame; RacePress(travel+"S",9); return true;
        }
        if(jump&&distance<6.5f&&state=="Crossup Antic"&&age>=23)
        {
            raceJump=frame; RacePress(travel+"J",12); return true;
        }
        if(distance>=11.5f)keys="";
        return true;
    }

    private bool GuardTelegraphedStrike(HeroController hero, string state, int age, float dx,
                                       float dy, bool ground, string away, string center, bool roomEdge)
    {
        float x=Mathf.Abs(dx);
        bool groundWhip=state=="Tendril G Antic"||state=="Tendril G Dash"||state=="Tendril G Whip";
        if(groundWhip && x<14.6f)
        {
            if(wanderRace&&raceBuild.Contains("groundpress")&&x<7.8f)
            {
                string toward=dx>0f?"R":"L";
                bool telegraph=state=="Tendril G Antic";
                float y=hero.transform.position.y-currentFloor;
                if(telegraph&&age<20){keys=x>3.65f?toward:"";return true;}
                if(telegraph&&age<23){keys="";return true;}
                if(ground&&hero.CanJump()&&frame-raceJump>18)
                {
                    raceJump=frame;pressureLiftUntil=frame+16;
                    RacePress("J",12);
                    return true;
                }
                float vy=hero.GetComponent<Rigidbody2D>().linearVelocity.y;
                if(!ground&&y<3.0f&&vy<4f&&hero.CanDoubleJump(false)&&frame-raceDouble>20&&frame-raceJump>=13&&
                   (state!="Tendril G Whip"||age<23))
                {
                    raceDouble=frame;
                    RacePress((x>4.2f?toward:"")+"J",10);
                    return true;
                }
                keys=x>3.65f&&y>2.5f?toward:"";
                if(!ground&&frame-raceJump<17)keys+="J";
                return true;
            }
            bool wideWhip=raceBuild.Contains("whipwalk");
            bool pressureLift=wanderRace&&raceBuild.Contains("attackzones");
            int liftAt=pressureLift?23:wideWhip?35:raceBuild.Contains("earlyhop")?29:33;
            if(wideWhip && !roomEdge)
            {
                if(state=="Tendril G Antic"&&age>=9&&age<=21&&x<11.9f&&(!pressureLift||x>10.9f)&&
                   hero.CanDash()&&frame-raceDash>25)
                {
                    raceDash=frame;RacePress(away+"S",10);
                    Note("WHIP_EXIT "+raceName+" "+(frame-raceStart)+" age="+age+" gap="+x+" "+away);
                    return true;
                }
                if(x>=13.3f||state=="Tendril G Antic"&&age<liftAt)
                {
                    keys=x<14.1f?away:"";return true;
                }
            }
            if(state=="Tendril G Antic" && age<liftAt)
            {
                if(x<9.2f) {keys=roomEdge?"":away;return true;}
                if(age>=17) {keys="";return true;}
                return false;
            }
            if(state=="Tendril G Whip" && age>=28 && x>=6.0f) return false;
            string retreat=roomEdge?center:away;
            if(ground && hero.CanJump() && frame-raceJump>=21)
            {
                raceJump=frame;
                if(pressureLift)pressureLiftUntil=frame+15;
                RacePress(retreat+"J",wideWhip?19:14);
                return true;
            }
            float v=hero.GetComponent<Rigidbody2D>().linearVelocity.y;
            if(!ground && v<4.4f && frame-raceJump>=12 && frame-raceDouble>=20 &&
                hero.CanDoubleJump(false))
            {
                raceDouble=frame;
                RacePress((roomEdge?center:away)+"J",10);
                return true;
            }
            keys=roomEdge?"":away;
            return true;
        }

        bool jumpingSlash=state=="J Slash M Antic"||state=="J Slash Multi"||state=="J Slash Multihit";
        if (!jumpingSlash)
        {
            if((state=="Downstab Antic" || state=="Downstab") && raceBuild.Contains("wall") &&
                (hero.transform.position.x<20f || hero.transform.position.x>53f))
            {
                keys=center;
                if(age>=9 && hero.CanDash() && frame-raceDash>23)
                {
                    raceDash=frame;
                    RacePress(center+"S",8);
                }
                return true;
            }
            if((state=="ComboSlash 1" || state=="ComboSlash 3" || state=="ComboSlash 5") &&
                raceBuild.Contains("comboguard") && ground && x<6.0f &&
                age>= (state=="ComboSlash 1"?22:2) && hero.CanJump() && frame-raceJump>20)
            {
                raceJump=frame;
                RacePress((roomEdge?center:away)+"J",14);
                return true;
            }
            return false;
        }
        if (state=="J Slash M Antic" && age<12 && x>=6.5f) return false;
        if(x>=12.4f)
        {
            keys="";
            return true;
        }
        string retreatSlash=roomEdge?center:away;
        if(roomEdge && raceBuild.Contains("arenaedges") && state=="J Slash M Antic" &&
            !ground && age>=9 && age<=27 && dy< -2.75f && x<7.2f &&
            hero.CanDash() && frame-raceDash>23)
        {
            raceDash=frame;
            RacePress(center+"S",8);
            return true;
        }
        if (roomEdge && x<8.3f && dy<2.2f && ground && hero.CanJump() &&
            frame-raceJump>=16 && state=="J Slash M Antic" && age<=(raceBuild.Contains("arenaedges")?21:25))
        {
            raceJump=frame;
            RacePress(center+"J",14);
            return true;
        }
        if ((age>=20||state!="J Slash M Antic") && hero.CanDash() && frame-raceDash>=24 &&
            (!roomEdge || (x<4.2f && dy>2.35f)) && x<10.5f)
        {
            raceDash=frame;
            RacePress(retreatSlash+"S",9);
            return true;
        }
        if (roomEdge && !ground && hero.CanDoubleJump(false) && frame-raceDouble>=18 && x<8f)
        {
            raceDouble=frame;
            RacePress(center+"J",9);
            return true;
        }
        keys=roomEdge?"":away;
        return true;
    }

    private bool GuardMovingHits(HeroController hero, string state, int age, float dx, float dy,
                                 bool ground, string away, string center)
    {
        var playerCollider=hero.heroBox ? hero.heroBox.GetComponent<BoxCollider2D>() : null;
        if(!playerCollider || !playerCollider.isActiveAndEnabled || hero.cState.invulnerable) return false;
        var hb=playerCollider.bounds;
        var body=hero.GetComponent<Rigidbody2D>();
        ReadHitboxes(hero);
        bool summon=frame<summonUntil;
        float floor=ground?hb.center.y:5.99f;
        float speed=Mathf.Max(7.8f,hero.GetWalkSpeed());
        float urgent=0f, targetX=0f, approach=0f;
        string hitName="";
        foreach(var box in threats)
        {
            if(box.Label=="Lost Lace Boss" || OwnHitbox(box.Label)) continue;
            var d=box.Shape.center-hb.center;
            if(Mathf.Abs(d.x)>box.Shape.extents.x+14f || Mathf.Abs(d.y)>box.Shape.extents.y+11f) continue;
            for(int n=0;n<=15;n+=3)
            {
                var pos=d+(Vector3)((box.Velocity-body.linearVelocity)*(n/60f));
                float sx=Mathf.Abs(pos.x)-box.Shape.extents.x-hb.extents.x;
                float sy=Mathf.Abs(pos.y)-box.Shape.extents.y-hb.extents.y;
                if(sx>.32f || sy>.27f) continue;
                float score=16f-n;
                if(score>urgent) {urgent=score; hitName=box.Label;targetX=box.Shape.center.x;approach=box.Velocity.x;}
                break;
            }
        }
        if(urgent<4f && !summon) return false;
        bool summoningColumn=state=="Summon Thwip" || state=="Summon Turn" ||
                              (summon && (state=="Dive In 1"||state=="Dive In 2"));
        bool unsafeClose=Mathf.Abs(dx)<5.8f && (state.Contains("Combo") || state.Contains("Charge") ||
            state.Contains("Quick Slash") || state.Contains("Counter"));
        if(urgent<4f && unsafeClose) return false;

        string route="";
        float best=float.PositiveInfinity;
        string cause;
        bool dash=hero.CanDash() && frame-lastFlowDodge>=20;
        bool jump=ground?hero.CanJump():hero.CanDoubleJump(false);
        for(int n=0;n<9;n++)
        {
            int direction=n%3-1;
            bool jumping=n>=3&&n<6;
            bool dashing=n>=6;
            if(jumping&&!jump || dashing&&(!dash||direction==0)) continue;
            if(hb.center.x<18f&&direction<0 || hb.center.x>57f&&direction>0) continue;
            float velocity=direction*(dashing?Mathf.Max(speed*2.5f,24f):speed);
            float cost=Forecast(hb,body.linearVelocity,floor,velocity,jumping?(ground?18.5f:17f):0,
                                jumping?12:0,dashing,!ground,out cause);
            float px=hb.center.x+velocity*.18f;
            if(summon && direction==0) cost+=1.2f;
            if(summoningColumn && raceBuild.Contains("pillar"))
            {
                float leave=Mathf.Abs(px-summonAimX);
                if(leave<5.5f) cost+=(5.5f-leave)*1.25f;
            }
            if(summon && hb.center.x < 22f && direction>0 || summon && hb.center.x > 52f && direction<0) cost-=1.1f;
            if (summon && !unsafeClose && !dashing && !jumping)
                cost+=Mathf.Abs(Mathf.Abs(hb.center.x+dx-px)-3.3f)*.16f;
            if(jumping) cost+=1.4f;
            if(dashing) cost+=urgent>12f?.7f:2.7f;
            if(cost<best){best=cost;route=(direction<0?"L":direction>0?"R":"")+(jumping?"J":dashing?"S":"");}
        }
        if (urgent<4f && summon && !unsafeClose)
        {
            if(hero.CanAttack() && frame-raceAttack>=8 && Mathf.Abs(dx)<3.75f &&
                Mathf.Abs(dy)<1.8f && Mathf.Abs(dx)>1.3f && !boss.IsInvincible &&
                (state=="Summon Antic" || state=="Summon Turn" || state=="Summon Thwip" ||
                 state=="Idle" || state=="Evade Recover"))
                return false;
            if (summoningColumn && raceBuild.Contains("pillar") &&
                (state=="Dive In 1" || state=="Dive In 2") && Mathf.Abs(hb.center.x-summonAimX)<6.5f)
            {
                keys=route.Length>0?route:center;
                return true;
            }
            if (state.Contains("Dive In") || state.Contains("Dive Out") || state.Contains("Downstab") ||
                state.Contains("Tendril") || state.Contains("Slash") || state.Contains("Charge"))
                return false;
            if(route.Length==0) route=center;
        }
        if(route.Length==0) return false;
        if (urgent>=4f && route.Length==1 && (route=="L"||route=="R") && Mathf.Abs(targetX-hb.center.x)<2.6f &&
            urgent>=13f && dash && Mathf.Abs(approach)>4f)
            route=(approach>0?"L":"R")+"S";
        if(route.EndsWith("S",StringComparison.Ordinal) || route.EndsWith("J",StringComparison.Ordinal))
        {
            lastFlowDodge=frame;
            Note("MERGE_DODGE "+raceName+" "+(frame-raceStart)+" "+state+" move="+route+
                " threat="+hitName+" score="+best.ToString("F1",CultureInfo.InvariantCulture));
            RacePress(route,route.EndsWith("S",StringComparison.Ordinal)?6:9);
        }
        else keys=route;
        return true;
    }

    private bool TryCrossStitch(HeroController hero, string state, int age, float dx, float dy, float risk, string cause)
    {
        if (!raceBuild.Contains("crossreact") || !hero.CanCast() || hero.cState.invulnerable ||
            frame-raceCast<34 || PlayerData.instance.silk<PlayerData.instance.SilkSkillCost ||
            wanderRace&&(!raceBuild.Contains("wspellcross")||!CanWanderSpendSilk()))
            return false;
        float x = Mathf.Abs(dx);
        int offset = racePolicy%5;
        bool attackSoon =
            (state=="ComboSlash 1" && age>=24+offset && age<=33 && x<5.1f && Mathf.Abs(dy)<2.7f) ||
            (state=="ComboSlash 3" && age>=4+offset && age<=11 && x<5.1f && Mathf.Abs(dy)<2.7f) ||
            (state=="ComboSlash 5" && age>=3+offset && age<=11 && x<5.7f && Mathf.Abs(dy)<2.7f) ||
            (state=="Quick Slash 1" && age<=2 && x<5.1f && Mathf.Abs(dy)<2.7f) ||
            (state=="Tendril G Antic" && age>=29+offset && age<=38 && x<10.8f && Mathf.Abs(dy)<2.5f) ||
            (state=="Tendril G Dash" && age<=3 && x<8.4f && Mathf.Abs(dy)<2.5f) ||
            (state=="Tendril A Antic" && age>=20+offset && age<=27 && x<5f && dy>2f && dy<9f) ||
            (state=="Downstab" && age>=1 && age<=10 && x<8.2f && dy<8.7f) ||
            (state=="J Slash M Antic" && age>=25+offset && age<=36 && x<4.8f && dy<5f) ||
            (state=="Charge" && x<6.6f && age>=2 && Mathf.Abs(dy)<2.5f) ||
            (state=="Crossup Antic" && x<4.5f && age>=23+offset && Mathf.Abs(dy)<2.5f);
        bool liveThreat=risk>=17f && cause.Length>0 && cause!="Lost Lace Boss" &&
                        !cause.StartsWith("telegraph:",StringComparison.Ordinal) && x<7.8f;
        if ((!attackSoon && !liveThreat) || state.Contains("Counter")) return false;
        raceCast=frame;
        Note("CROSS_CAST "+raceName+" "+(frame-raceStart)+" "+state+" age="+age+" silk="+PlayerData.instance.silk+
            " risk="+risk.ToString("F1",CultureInfo.InvariantCulture)+" source="+cause);
        string face=wanderRace&&hero.cState.facingRight!=(dx>=0f)?(dx<0f?"L":"R"):"";
        RacePress(face+"C",2);
        return true;
    }

    private bool DriveGeometry(HeroController hero, string state, int age, float dx, float dy,
                               bool ground, bool danger, bool vanished, bool counter)
    {
        if (raceStart<0) return false;
        if(!geometryEntered)
        {
            geometryEntered=true;
            Note("BOX_ENTER "+raceName+" heroBox="+(bool)hero.heroBox+" damageField="+(DamageCollider!=null)+" bossField="+(LaceCollider!=null));
        }
        if (!hero.heroBox) return false;
        var playerCollider=hero.heroBox.GetComponent<BoxCollider2D>();
        if (!playerCollider || !playerCollider.isActiveAndEnabled)
        {
            if(frame-raceStart<8) Note("BOX_COLLIDER "+raceName+" active="+(bool)playerCollider);
            return false;
        }
        var playerBox=playerCollider.bounds;
        var lace=(Collider2D)LaceCollider.GetValue(boss);
        if (!lace) lace=boss.GetComponent<Collider2D>();
        var victim=lace?lace.bounds:new Bounds(boss.transform.position,Vector3.zero);
        bool bodyValid=lace && lace.isActiveAndEnabled && victim.size.x>.05f && victim.size.y>.05f;
        if (!bodyValid) victim=new Bounds(boss.transform.position,new Vector3(1.6f,2.0f,1f));
        float separation=bodyValid?playerCollider.Distance(lace).distance:
            Mathf.Abs(dx)-playerBox.extents.x-victim.extents.x;
        var rb=hero.GetComponent<Rigidbody2D>();
        var v=rb.linearVelocity;
        if(frame-lastGeometryLog>=120) Note("BOX_BOUNDS "+raceName+" "+playerBox+" "+victim+" physical="+bodyValid+" object="+(lace?lace.name:"-"));
        ReadHitboxes(hero);
        PredictTelegraph(state,age,victim,playerBox,dx);
        bool closeLane=Mathf.Abs(playerBox.center.y-victim.center.y) < playerBox.extents.y+victim.extents.y+.7f;
        float gap=Mathf.Abs(dx)-playerBox.extents.x-victim.extents.x;
        float side=dx<0?-1f:1f;
        string toward = side<0?"L":"R";
        string face=hero.cState.facingRight==(side>0)?"":toward;
        float speed=Mathf.Max(4.5f,hero.GetWalkSpeed());
        float floor=ground?playerBox.center.y:6.37f+(playerBox.center.y-hero.transform.position.y);
        bool hop = ground?hero.CanJump()&&frame-aimJump>15:hero.CanDoubleJump(false)&&frame-aimJump>19;
        bool dart = hero.CanDash() && frame-aimDash>31 && !hero.cState.dashing;
        string selected="";
        float best=100000f;
        float neutralRisk=0f;
        string causing="";
        int mode=0;
        string away = side<0?"R":"L";
        bool hybrid=raceBuild.Contains("hybrid");
        float attackGap=raceBuild.Contains("close")?1.65f:2.75f;
        if(TryCrossStitch(hero,state,age,dx,dy,0f,"")) return true;
        bool bankCounter=raceBuild.Contains("crosshold") && hero.CanCast() &&
            PlayerData.instance.silk>=PlayerData.instance.SilkSkillCost && frame-raceCast>=34;
        bool horizontalWindup=state=="ComboSlash 1"||state=="Tendril G Antic"||state=="J Slash M Antic";
        if (horizontalWindup && closeLane && gap<(state=="Tendril G Antic"?12f:5.9f) &&
            age>=14+(racePolicy%4) && age<35 && ground && hop && !bankCounter)
        {
            aimJump=frame;
            geomHoldJump=frame+13;
            bool canCross=state=="ComboSlash 1" && gap<3f && hero.CanDash() &&
                !(victim.center.x<20f || victim.center.x>55f);
            RacePress((canCross&&raceBuild.Contains("through")?toward:away)+"J",13);
            return true;
        }
        if ((state=="Downstab" || state=="Downstab Antic" || state=="Dstab Multihit") && Mathf.Abs(dx)<10f &&
            (!bankCounter || state=="Dstab Multihit" || age>9))
        {
            var bossBody=boss.GetComponent<Rigidbody2D>();
            float endX=victim.center.x+(bossBody?bossBody.linearVelocity.x:0f)*.12f;
            string dodge=playerBox.center.x>53f?"L":playerBox.center.x<20f?"R":
                state=="Downstab Antic"?(raceBuild.Contains("under")?toward:away):
                bossBody && Mathf.Abs(bossBody.linearVelocity.x)>10f
                    ? (bossBody.linearVelocity.x>0?"L":"R") : playerBox.center.x>endX?"R":"L";
            if (dart && (state!="Downstab Antic" || age>=23))
            {
                aimDash=frame;
                geomHoldDash=frame+10;
                RacePress(dodge+"S",10);
            }
            else if (state!="Downstab Antic" || age>=6) keys=dodge;
            return true;
        }
        if ((state=="Charge" || state=="Quick Slash 1") && ground && gap<7f && hop)
        {
            aimJump=frame;
            geomHoldJump=frame+14;
            RacePress((playerBox.center.x<22f?"R":playerBox.center.x>52f?"L":away)+"J",14);
            return true;
        }
        if (state.StartsWith("ComboSlash ",StringComparison.Ordinal) && state!="ComboSlash 1" &&
            closeLane && gap<4.4f && ground && hop)
        {
            aimJump=frame;
            geomHoldJump=frame+13;
            string escape=playerBox.center.x<23f?"R":playerBox.center.x>52f?"L":away;
            RacePress(escape+"J",13);
            return true;
        }
        float desired=state.StartsWith("P2 Shift",StringComparison.Ordinal)&&age>24?10.1f:1.30f;
        for(int i=0;i<9;i++)
        {
            bool jump=i>=3&&i<=5;
            bool dash=i>=6&&i<=8;
            if(jump&&!hop || dash&&!dart || i==6) continue;
            int sign=i%3==0?0:i%3==1?-1:1;
            if((playerBox.center.x<15.8f&&sign<0)||(playerBox.center.x>58.2f&&sign>0)) continue;
            string label;
            float sx=sign*(dash?Mathf.Max(speed*2.5f,23f):speed);
            float cost=Forecast(playerBox,v,floor,sx,jump?(ground?19.5f:17.2f):0f,jump?12:0,dash,!ground,out label);
            if(i==0){neutralRisk=cost;causing=label;}
            float projectedX=playerBox.center.x+sx*.22f;
            float projectedGap=Mathf.Abs(victim.center.x-projectedX)-playerBox.extents.x-victim.extents.x;
            float distance=Mathf.Abs(projectedGap-desired);
            bool beyond=Mathf.Sign(victim.center.x-projectedX)!=side;
            if(vanished) distance=0.24f*Mathf.Abs(projectedX-36f);
            cost+=distance*(raceBuild.Contains("rush")?1.11f:.79f);
            if (!vanished && closeLane && projectedGap<.26f && (beyond || !jump)) cost+=4.2f;
            if (jump) cost+=danger?.51f:2.8f;
            if (dash) cost+=danger?1.2f:2.4f;
            if (i==0 && hero.CanAttack() && closeLane && gap>=.36f && gap<=attackGap && !vanished && !boss.IsInvincible)
                cost-=1.7f;
            if (cost>=best) continue;
            best=cost;
            selected=(sign<0?"L":sign>0?"R":"")+(jump?"J":dash?"S":"");
            mode=jump?1:dash?2:0;
        }
        if(raceVideo && (frame-lastGeometryLog>=120 || (neutralRisk>7.5f && causing!=aimName && frame-lastGeometryLog>=18)))
        {
            lastGeometryLog=frame;
            aimName=causing;
            Note(string.Format(CultureInfo.InvariantCulture,
                "BOX_PLAN {0} {1} state={2} hulls={3} clearance={4:F2} forecast={5:F2} move={6} cause={7} H={8:F2},{9:F2} B={10:F2},{11:F2}",
                raceName,frame-raceStart,state,threats.Count,separation,neutralRisk,selected,causing,
                playerBox.center.x,playerBox.center.y,victim.center.x,victim.center.y));
        }
        if (frame<geomHoldDash || (frame<geomHoldJump&&neutralRisk<4.9f)) return false;
        if(TryCrossStitch(hero,state,age,dx,dy,neutralRisk,causing)) return true;

        if (raceRing && raceBuild.Contains("loop") && !ground && !vanished && !counter &&
            dy < -1.42f && dy > -4.35f && Mathf.Abs(dx)<3.1f && neutralRisk < 9f)
        {
            bool canRing=hero.CanDoubleJump(false) && frame-raceDouble>12 &&
                (frame-ringPogoFrame<20 || (Mathf.Abs(dx)<1.9f && dy< -2.4f));
            if (canRing)
            {
                raceDouble=ringJumpFrame=frame;
                RacePress(toward+"J",1);
                return true;
            }
            if (hero.CanAttack() && frame-raceAttack>5)
            {
                raceAttack=ringPogoFrame=frame;
                RacePress(toward+"DA",2);
                return true;
            }
            if (raceBuild.Contains("clawloop") && hero.CanHarpoonDash() &&
                frame-ringPogoFrame>=3 && frame-ringPogoFrame<=8 && frame-raceClaw>18)
            {
                raceClaw=frame;
                RacePress(toward+"H",2);
                return true;
            }
        }
        if(raceBuild.Contains("post") && shiftedPhase && neutralRisk>4f &&
            (mode>0 || selected.Length>0)) 
        {
            if(mode==1) {aimJump=frame; RacePress(selected,13);}
            else if(mode==2) {aimDash=frame; RacePress(selected,7);}
            else keys=selected;
            return true;
        }
        if (hybrid && neutralRisk<9f) return false;
        if (!wanderRace && !raceBuild.Contains("crossreact") && neutralRisk>6f && closeLane && !vanished && Mathf.Abs(dx)<8f && hero.CanCast() &&
            PlayerData.instance.silk>=PlayerData.instance.SilkSkillCost && frame-raceCast>=22 &&
            !raceBuild.Contains("nails"))
        {
            raceCast=frame;
            RacePress(face+"C",3);
            return true;
        }
        if (neutralRisk>2.55f && mode>0 && (!bankCounter||neutralRisk>13f))
        {
            if (mode==1)
            {
                aimJump=frame;
                geomHoldJump=frame+12;
                RacePress(selected,12);
            }
            else
            {
                aimDash=frame;
                geomHoldDash=frame+6;
                RacePress(selected,6);
            }
            return true;
        }
        if (neutralRisk>4.1f && mode==0 && selected.Length>0)
        {
            keys=selected;
            return true;
        }
        if (hero.CanAttack() && frame-raceAttack>6 && !vanished && !boss.IsInvincible && !counter)
        {
            if (closeLane && gap>=.12f && gap<=attackGap &&
                (neutralRisk<3.4f || hero.cState.invulnerable || (bankCounter && neutralRisk<8f)))
            {
                raceAttack=frame;
                RacePress(face+"A",3);
                return true;
            }
            if (dy>1.4f && dy<4.6f && Mathf.Abs(dx)<2.4f && neutralRisk<2f)
            {
                raceAttack=frame;
                RacePress("UA",3);
                return true;
            }
            if (!ground && dy < -1.1f && dy > -5.65f && Mathf.Abs(dx)<4.2f && neutralRisk<2.8f)
            {
                if (raceRing && frame-raceDouble>15 && hero.CanDoubleJump(false) && frame-raceJump>4)
                {
                    raceDouble=frame;
                    RacePress(toward+"DJ",2);
                    return true;
                }
                raceAttack=frame;
                RacePress(toward+"DA",3);
                return true;
            }
        }
        bool reserveClaw=raceBuild.Contains("reserveclaw");
        bool clawWindow=!reserveClaw||(PlayerData.instance.silk>PlayerData.instance.SilkSkillCost&&
            (state=="Downstab Land"||state=="Stunned"||state=="Charge Recover"||state=="Tendril G Whip End")&&
            frame-lastVomit>160&&frame-lastBulletAt>155);
        if (!wanderRace && clawWindow && neutralRisk<4f && !counter && !vanished && Mathf.Abs(dx)>5f && Mathf.Abs(dx)<12f &&
            Mathf.Abs(dy)<2.1f && hero.CanHarpoonDash() && frame-raceClaw>53 &&
            !danger && !state.Contains("Antic") && !state.Contains("Hop") &&
            (raceBuild.Contains("rush") || Mathf.Abs(dx)>7f))
        {
            raceClaw=frame;
            RacePress(toward+"H",3);
            return true;
        }
        if (neutralRisk<2.55f && !danger && mode==0 && selected.Length>0)
        {
            keys=selected;
            return true;
        }
        return false;
    }}





