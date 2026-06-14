using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Newtonsoft.Json;
using Oxide.Core;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Finisher", "jerky+claude", "0.1.0")]
    [Description("Plays a finisher effect at the kill location when a player kills (not just downs) an enemy")]
    public class Finisher : RustPlugin
    {
        private const string PermSee = "finisher.see";   // エフェクトが見える人(空なら全員)
        private const string PermTrigger = "finisher.trigger"; // キルしたときに発生させられる人

        #region 設定

        private Configuration config;

        private class Configuration
        {
            [JsonProperty("Effect Duration (seconds)")]
            public float Duration = 2.6f;

            [JsonProperty("Update Interval (seconds, smaller = smoother/heavier)")]
            public float FrameInterval = 0.08f;

            [JsonProperty("View Distance (m)")]
            public float ViewDistance = 60f;

            [JsonProperty("Occluded By Terrain/Buildings (zTest)")]
            public bool ZTest = true;

            [JsonProperty("Require Permission To See (finisher.see)")]
            public bool RequireSeePermission = false;

            [JsonProperty("Require Permission To Trigger (finisher.trigger)")]
            public bool RequireTriggerPermission = false;

            [JsonProperty("Trigger On NPC Kills (animals/scientists/etc.)")]
            public bool TriggerOnNpcKills = true;

            [JsonProperty("Players And NPCs Only (no buildings/etc.)")]
            public bool PlayersAndNpcsOnly = true;

            [JsonProperty("Skip Suicide And Team Kills")]
            public bool SkipSelfAndTeam = true;

            [JsonProperty("Headshot Kills Only")]
            public bool HeadshotOnly = false;

            [JsonProperty("Primary Color RGBA")]
            public float[] PrimaryColor = { 1f, 0.12f, 0.16f, 1f };

            [JsonProperty("Accent Color RGBA")]
            public float[] AccentColor = { 1f, 0.35f, 0.30f, 1f };

            [JsonProperty("Show Ground Rune Rings")]
            public bool ShowRuneRings = true;

            [JsonProperty("Show Shockwave Ring")]
            public bool ShowShockwave = true;

            [JsonProperty("Show Rising Light Pillars")]
            public bool ShowPillars = true;

            [JsonProperty("Show Sparks")]
            public bool ShowSparks = true;

            [JsonProperty("Spark Count")]
            public int SparkCount = 26;

            [JsonProperty("Show Text")]
            public bool ShowText = true;

            [JsonProperty("Banner Text")]
            public string BannerText = "ELIMINATED";

            [JsonProperty("Ring Max Radius (m)")]
            public float RingRadius = 1.6f;

            [JsonProperty("Effect Height (m)")]
            public float Height = 2.4f;

            // ---- Optional: 3D Emblem (OBJ) ----
            [JsonProperty("Emblem OBJ Name (empty = off)")]
            public string EmblemModel = "";

            [JsonProperty("Tint Emblem With Primary Color (ignore texture)")]
            public bool EmblemTint = true;

            [JsonProperty("Emblem Scale")]
            public float EmblemScale = 1f;

            [JsonProperty("Emblem Max Lines")]
            public int EmblemMaxLines = 8000;

            [JsonProperty("Emblem Line Spacing (m, smaller = denser)")]
            public float EmblemSpacing = 0.02f;
        }

        protected override void LoadDefaultConfig() => config = new Configuration();

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                config = Config.ReadObject<Configuration>();
                if (config == null)
                {
                    throw new Exception();
                }
            }
            catch
            {
                PrintWarning("設定の読み込みに失敗。既定値で再生成します。");
                LoadDefaultConfig();
            }
            SaveConfig();
        }

        protected override void SaveConfig() => Config.WriteObject(config);

        private Color Primary => ToColor(config.PrimaryColor, Color.red);
        private Color Accent => ToColor(config.AccentColor, Color.yellow);

        private static Color ToColor(float[] c, Color fallback)
        {
            if (c == null || c.Length < 3)
            {
                return fallback;
            }
            return new Color(c[0], c[1], c[2], c.Length > 3 ? c[3] : 1f);
        }

        #endregion

        #region ライフサイクル

        private Timer frameTimer;
        private readonly List<Effect> effects = new List<Effect>();

        // 受け手ごとの admin 昇格をエフェクト間で共有(参照カウント)。
        // MeshSurfaceDraw と同様、ddraw は admin フラグが立った相手にしか描けない。
        private readonly Dictionary<ulong, int> adminHold = new Dictionary<ulong, int>();
        private readonly Dictionary<ulong, bool> adminWas = new Dictionary<ulong, bool>();

        private void Init()
        {
            permission.RegisterPermission(PermSee, this);
            permission.RegisterPermission(PermTrigger, this);
        }

        private void OnServerInitialized()
        {
            float dt = Mathf.Clamp(config.FrameInterval, 0.03f, 0.3f);
            frameTimer = timer.Every(dt, UpdateEffects);
        }

        private void Unload()
        {
            frameTimer?.Destroy();
            // すべての昇格を元に戻す
            foreach (var kv in adminWas)
            {
                var p = BasePlayer.FindByID(kv.Key);
                if (p != null && p.IsConnected && !kv.Value)
                {
                    p.SetPlayerFlag(BasePlayer.PlayerFlags.IsAdmin, false);
                    p.SendNetworkUpdateImmediate();
                }
            }
            adminHold.Clear();
            adminWas.Clear();
            effects.Clear();
        }

        private void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            // 切断した相手は受け手リストから外す(復帰処理も不要)
            foreach (var e in effects)
            {
                e.Viewers.Remove(player);
            }
            adminHold.Remove(player.userID);
            adminWas.Remove(player.userID);
        }

        #endregion

        #region キル検出

        // OnEntityDeath は Die() でのみ発火する = ダウンでは来ない = これがキル。
        private void OnEntityDeath(BaseCombatEntity entity, HitInfo info)
        {
            TryFinish(entity, info);
        }

        private void TryFinish(BaseCombatEntity victim, HitInfo info)
        {
            if (victim == null || info == null)
            {
                return;
            }

            var killer = info.InitiatorPlayer;
            if (killer == null || killer.IsNpc)
            {
                return; // 倒したのが本物のプレイヤーでなければ出さない
            }

            var victimPlayer = victim as BasePlayer;
            if (config.PlayersAndNpcsOnly && victimPlayer == null)
            {
                return; // 建物/乗り物などのキルでは出さない
            }
            if (victimPlayer != null)
            {
                if (victimPlayer.IsNpc && !config.TriggerOnNpcKills)
                {
                    return;
                }
                if (config.SkipSelfAndTeam)
                {
                    if (victimPlayer == killer)
                    {
                        return; // 自殺
                    }
                    if (!victimPlayer.IsNpc && killer.currentTeam != 0 &&
                        victimPlayer.currentTeam == killer.currentTeam)
                    {
                        return; // 同じチーム(同士討ち)
                    }
                }
            }

            if (config.HeadshotOnly && !info.isHeadshot)
            {
                return;
            }

            if (config.RequireTriggerPermission &&
                !permission.UserHasPermission(killer.UserIDString, PermTrigger))
            {
                return;
            }

            // キル地点(被害者の体)
            Vector3 pos = victim.transform.position;
            string killerName = killer.displayName ?? "Unknown";
            SpawnEffect(pos, killerName, info.isHeadshot);
        }

        #endregion

        #region エフェクト生成・更新

        private class Effect
        {
            public Vector3 Pos;
            public string KillerName;
            public bool Headshot;
            public float Start;
            public List<BasePlayer> Viewers = new List<BasePlayer>();

            // 火花は生成時に方向/速度を固定し、フレーム間で連続的に動かす
            public Vector3[] SparkDir;
            public float[] SparkSpeed;

            // エンブレム(任意): 立ち上がりで1回だけ構築し、スパイクを避けるため
            // 複数フレームに分割して各受け手へ送る(静止表示・再構築しない)
            public bool EmblemBuilt;
            public QLine[] EmblemLines;
            public int EmblemIndex;
            public float EmblemDur;
        }

        private void SpawnEffect(Vector3 pos, string killerName, bool headshot)
        {
            var viewers = new List<BasePlayer>();
            foreach (var p in BasePlayer.activePlayerList)
            {
                if (p == null || !p.IsConnected)
                {
                    continue;
                }
                if (Vector3.Distance(p.transform.position, pos) > config.ViewDistance)
                {
                    continue;
                }
                if (config.RequireSeePermission &&
                    !permission.UserHasPermission(p.UserIDString, PermSee))
                {
                    continue;
                }
                viewers.Add(p);
            }
            if (viewers.Count == 0)
            {
                return;
            }

            var eff = new Effect
            {
                Pos = pos,
                KillerName = killerName,
                Headshot = headshot,
                Start = Time.realtimeSinceStartup,
                Viewers = viewers,
            };

            // 火花の初期方向: ほぼ上向き＋外向きにばらける
            int n = Mathf.Clamp(config.SparkCount, 0, 80);
            eff.SparkDir = new Vector3[n];
            eff.SparkSpeed = new float[n];
            var rng = new System.Random(pos.GetHashCode() ^ (int)(eff.Start * 1000f));
            for (int i = 0; i < n; i++)
            {
                float ang = (float)(rng.NextDouble() * Math.PI * 2.0);
                float up = 0.6f + (float)rng.NextDouble() * 0.9f;
                float out2 = 0.4f + (float)rng.NextDouble() * 0.8f;
                eff.SparkDir[i] = new Vector3(Mathf.Cos(ang) * out2, up, Mathf.Sin(ang) * out2).normalized;
                eff.SparkSpeed[i] = 3.5f + (float)rng.NextDouble() * 3.5f;
            }

            foreach (var v in viewers)
            {
                AcquireAdmin(v);
            }
            effects.Add(eff);
        }

        private void UpdateEffects()
        {
            if (effects.Count == 0)
            {
                return;
            }
            float now = Time.realtimeSinceStartup;
            float dur = Mathf.Max(config.Duration, 0.3f);

            for (int i = effects.Count - 1; i >= 0; i--)
            {
                var eff = effects[i];
                float t = (now - eff.Start) / dur; // 0→1
                if (t >= 1f)
                {
                    foreach (var v in eff.Viewers)
                    {
                        ReleaseAdmin(v);
                    }
                    effects.RemoveAt(i);
                    continue;
                }
                DrawFrame(eff, t);
                FlushEmblem(eff);
            }
        }

        // 各フレームの ddraw 寿命は更新間隔より少し長くして、フレーム間で消える瞬間を無くす
        private float LineDur => Mathf.Clamp(config.FrameInterval, 0.03f, 0.3f) * 1.7f;

        private void DrawFrame(Effect eff, float t)
        {
            Color pri = Primary;
            Color acc = Accent;
            float dur = LineDur;
            Vector3 ground = eff.Pos + Vector3.up * 0.05f;

            // フェード: 序盤で立ち上がり、終盤で消える
            float fade = Mathf.Clamp01(Mathf.Min(t * 6f, (1f - t) * 4f));
            Color priF = Fade(pri, fade);
            Color accF = Fade(acc, fade);

            foreach (var p in eff.Viewers)
            {
                if (p == null || !p.IsConnected)
                {
                    continue;
                }

                if (config.ShowShockwave)
                {
                    // 序盤に外へ広がって消える衝撃波
                    float st = Mathf.Clamp01(t / 0.45f);
                    if (st < 1f)
                    {
                        float r = config.RingRadius * (0.2f + EaseOut(st) * 1.6f);
                        Color sc = Fade(acc, (1f - st) * 0.9f);
                        DrawCircle(p, ground, r, sc, dur, 40, 0f);
                    }
                }

                if (config.ShowRuneRings)
                {
                    // 逆回転する二重の魔法陣＋六芒星
                    float a = t * 90f;
                    DrawCircle(p, ground, config.RingRadius, priF, dur, 48, a);
                    DrawCircle(p, ground, config.RingRadius * 0.66f, accF, dur, 40, -a * 1.6f);
                    DrawHexagram(p, ground, config.RingRadius * 0.92f, priF, dur, a * 0.5f);
                    DrawHexagram(p, ground, config.RingRadius * 0.92f, priF, dur, a * 0.5f + 30f);
                }

                if (config.ShowPillars)
                {
                    DrawPillars(p, eff.Pos, t, accF, dur);
                }

                if (config.ShowSparks && eff.SparkDir != null)
                {
                    DrawSparks(p, eff, t, priF, accF, dur);
                }

                if (config.ShowText)
                {
                    DrawBanner(p, eff, t);
                }
            }

            // エンブレム(任意): 立ち上がりで1回だけ構築する(送信は FlushEmblem が分割実行)
            if (!eff.EmblemBuilt && !string.IsNullOrEmpty(config.EmblemModel) && t > 0.1f)
            {
                eff.EmblemBuilt = true;
                BuildEmblem(eff);
            }
        }

        private static Color Fade(Color c, float a)
        {
            c.a = Mathf.Clamp01(c.a * a);
            return c;
        }

        private static float EaseOut(float x) => 1f - (1f - x) * (1f - x);

        #endregion

        #region 手続き的プリミティブ

        private void DrawCircle(BasePlayer p, Vector3 center, float radius, Color c, float dur, int segs, float angleOffsetDeg)
        {
            if (radius <= 0.001f || segs < 3)
            {
                return;
            }
            float off = angleOffsetDeg * Mathf.Deg2Rad;
            Vector3 prev = center + new Vector3(Mathf.Cos(off), 0f, Mathf.Sin(off)) * radius;
            for (int i = 1; i <= segs; i++)
            {
                float ang = off + (float)i / segs * Mathf.PI * 2f;
                Vector3 cur = center + new Vector3(Mathf.Cos(ang), 0f, Mathf.Sin(ang)) * radius;
                UnityEngine.DDraw.Line(p, prev, cur, c, dur, false, config.ZTest);
                prev = cur;
            }
        }

        // 六芒星(2つの三角形)を地面に描く
        private void DrawHexagram(BasePlayer p, Vector3 center, float radius, Color c, float dur, float angleOffsetDeg)
        {
            Vector3[] pts = new Vector3[3];
            float off = angleOffsetDeg * Mathf.Deg2Rad;
            for (int k = 0; k < 3; k++)
            {
                float ang = off + k * (Mathf.PI * 2f / 3f);
                pts[k] = center + new Vector3(Mathf.Cos(ang), 0f, Mathf.Sin(ang)) * radius;
            }
            UnityEngine.DDraw.Line(p, pts[0], pts[1], c, dur, false, config.ZTest);
            UnityEngine.DDraw.Line(p, pts[1], pts[2], c, dur, false, config.ZTest);
            UnityEngine.DDraw.Line(p, pts[2], pts[0], c, dur, false, config.ZTest);
        }

        // 中心を囲む光の柱が立ち上がる
        private void DrawPillars(BasePlayer p, Vector3 basePos, float t, Color c, float dur)
        {
            const int count = 6;
            float rise = Mathf.Clamp01(t / 0.4f);
            float h = config.Height * EaseOut(rise);
            float r = config.RingRadius * 0.5f;
            float spin = t * 120f * Mathf.Deg2Rad;
            for (int i = 0; i < count; i++)
            {
                float ang = spin + i * (Mathf.PI * 2f / count);
                Vector3 b = basePos + new Vector3(Mathf.Cos(ang), 0.05f, Mathf.Sin(ang)) * r;
                Vector3 top = b + Vector3.up * h;
                UnityEngine.DDraw.Line(p, b, top, c, dur, false, config.ZTest);
            }
            // 中央の太い柱
            Vector3 cb = basePos + Vector3.up * 0.05f;
            UnityEngine.DDraw.Line(p, cb, cb + Vector3.up * h, Fade(c, 0.8f), dur, false, config.ZTest);
        }

        // 上空へ飛び散って重力で落ちる火花(線分の尾)
        private void DrawSparks(BasePlayer p, Effect eff, float t, Color a, Color b, float dur)
        {
            float life = t;                       // 0→1
            float speedT = life * config.Duration; // 経過秒
            const float g = 6.5f;
            Vector3 origin = eff.Pos + Vector3.up * 0.4f;
            for (int i = 0; i < eff.SparkDir.Length; i++)
            {
                Vector3 vel0 = eff.SparkDir[i] * eff.SparkSpeed[i];
                // 位置 = origin + v0*t - 0.5*g*t^2 (上方向のみ重力)
                Vector3 pos = origin + vel0 * speedT;
                pos.y -= 0.5f * g * speedT * speedT;
                if (pos.y < eff.Pos.y)
                {
                    continue; // 着地した火花は描かない
                }
                // 速度方向に短い尾を引く
                Vector3 vel = vel0;
                vel.y -= g * speedT;
                Vector3 tail = pos - vel.normalized * 0.25f;
                Color c = (i % 2 == 0) ? a : b;
                UnityEngine.DDraw.Line(p, tail, pos, Fade(c, 1f - life), dur, false, config.ZTest);
            }
        }

        private void DrawBanner(BasePlayer p, Effect eff, float t)
        {
            // 文字は寿命を長めにし、毎フレーム上書きはしない(チラつき防止)。
            // ここでは立ち上がりに1回だけ描けば十分だが、視点で位置は変わらないので
            // フレームごとに薄く出し直しても良い。負荷を抑えるため序盤に1度だけ出す。
            float dur = LineDur;
            Vector3 head = eff.Pos + Vector3.up * (config.Height + 0.4f);
            // 文字もすべて赤系で統一
            string sub = $"<size=16><color=#ff6666>{eff.KillerName}</color></size>";
            string hs = eff.Headshot ? " <color=#ff8080>★HEADSHOT</color>" : "";
            string txt = $"<size=26><color=#ff2233>{config.BannerText}</color>{hs}</size>\n{sub}";
            UnityEngine.DDraw.Text(p, head, txt, Color.red, dur, false, false);
        }

        #endregion

        #region admin昇格(MeshSurfaceDraw流用)

        private void AcquireAdmin(BasePlayer player)
        {
            if (player == null)
            {
                return;
            }
            if (adminHold.TryGetValue(player.userID, out int n))
            {
                adminHold[player.userID] = n + 1;
                return;
            }
            adminHold[player.userID] = 1;
            adminWas[player.userID] = player.IsAdmin;
            if (!player.IsAdmin)
            {
                player.SetPlayerFlag(BasePlayer.PlayerFlags.IsAdmin, true);
                player.SendNetworkUpdateImmediate();
            }
        }

        private void ReleaseAdmin(BasePlayer player)
        {
            if (player == null || !adminHold.TryGetValue(player.userID, out int n))
            {
                return;
            }
            if (n > 1)
            {
                adminHold[player.userID] = n - 1;
                return;
            }
            adminHold.Remove(player.userID);
            bool was = adminWas.TryGetValue(player.userID, out bool w) && w;
            adminWas.Remove(player.userID);
            if (!was && player.IsConnected)
            {
                player.SetPlayerFlag(BasePlayer.PlayerFlags.IsAdmin, false);
                player.SendNetworkUpdateImmediate();
            }
        }

        #endregion

        #region 3Dエンブレム(MeshSurfaceDrawのスキャンライン充填を流用)

        // フィニッシャーは短時間なので、エンブレムは「キル地点で正面固定・静止」表示にする。
        // エフェクト中央から見た固定基準で遠→近にソートして1回だけ構築し(再構築なし=軽い)、
        // 大量の ddraw を一度に投げるスパイクを避けるため数フレームに分割して送る(FlushEmblem)。
        //
        // 対応フォーマット(MeshSurfaceDraw / model2mdraw.py と同一):
        //   面色モード : v / "c R G B"(面色) / f
        //   テクスチャ : v / vt / "tex W H" + "tx <hex>" / f v/vt  ← 塗り線を色の変わり目で分割
        // 重ね描き(passes)やインナーコアは使わない(=1層)。

        private class Mesh
        {
            public Vector3[] Verts;
            public List<int[]> Faces;
            public List<Color> FaceColors; // 面色モード(無ければnull)
            public float Height;

            public Vector2[] UVs;          // v=0が下
            public List<int[]> FaceUVs;    // UVの無い面はnull
            public Color32[] Tex;          // 行0=画像上端
            public int TexW, TexH;

            public Color SampleTex(Vector2 uv)
            {
                int x = Mathf.Clamp(Mathf.RoundToInt(uv.x * (TexW - 1)), 0, TexW - 1);
                int y = Mathf.Clamp(Mathf.RoundToInt((1f - uv.y) * (TexH - 1)), 0, TexH - 1);
                return Tex[y * TexW + x];
            }
        }

        private readonly Dictionary<string, Mesh> meshCache = new Dictionary<string, Mesh>(StringComparer.OrdinalIgnoreCase);
        private static readonly Vector3 LightDir = new Vector3(0.4f, 0.8f, 0.3f).normalized;
        private const int TexSegMax = 8;       // 1本の塗り線の最大色分割数
        private const int EmblemPerFrame = 1400; // 1フレームに送るエンブレム線数(スパイク防止)

        // エンブレムの線を構築して eff に蓄える(送信は FlushEmblem が分割実行)
        private void BuildEmblem(Effect eff)
        {
            Mesh mesh = LoadObj(config.EmblemModel);
            if (mesh == null)
            {
                return;
            }
            float scale = Mathf.Max(0.01f, config.EmblemScale);
            // キル地点の少し上、胸〜頭の高さに浮かべる
            Vector3 center = eff.Pos + Vector3.up * (config.Height * 0.55f);

            // 中央から「最も近い受け手」の方を向ける(複数いても見栄え優先で代表1人)
            Vector3 faceTo = center + Vector3.forward;
            float best = float.MaxValue;
            foreach (var v in eff.Viewers)
            {
                if (v == null || !v.IsConnected)
                {
                    continue;
                }
                float d = Vector3.SqrMagnitude(v.transform.position - center);
                if (d < best)
                {
                    best = d;
                    faceTo = v.eyes != null ? v.eyes.position : v.transform.position;
                }
            }
            Vector3 fwd = faceTo - center;
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 0.001f)
            {
                fwd = Vector3.forward;
            }
            fwd.Normalize();
            float yaw = Mathf.Atan2(-fwd.x, -fwd.z) * Mathf.Rad2Deg;
            Quaternion rot = Quaternion.Euler(0f, yaw, 0f);

            var world = new Vector3[mesh.Verts.Length];
            for (int i = 0; i < world.Length; i++)
            {
                world[i] = center + rot * (mesh.Verts[i] * scale);
            }

            // ソート基準の視点(正面・少し離れた固定点)
            Vector3 eye = center + fwd * Mathf.Max(2f, mesh.Height * scale * 1.5f) + Vector3.up * 0.3f;

            float spacing = Mathf.Max(0.004f, config.EmblemSpacing);
            long wanted = 0;
            for (int fi = 0; fi < mesh.Faces.Count; fi++)
            {
                var f = mesh.Faces[fi];
                for (int t = 2; t < f.Length; t++)
                {
                    wanted += EstimateLines(world[f[0]], world[f[t - 1]], world[f[t]], spacing);
                }
            }
            if (wanted == 0)
            {
                return;
            }
            float factor = wanted > config.EmblemMaxLines ? (float)config.EmblemMaxLines / wanted : 1f;

            // EmblemTint = true なら(赤一色など)メインカラーの単色で塗る → テクスチャは無視
            bool textured = mesh.Tex != null && mesh.FaceUVs != null && !config.EmblemTint;
            Color tint = Primary;
            var lines = new List<QLine>(Mathf.Min((int)(wanted * 1.4f) + 8, config.EmblemMaxLines * 3));
            float acc = 0f;
            for (int fi = 0; fi < mesh.Faces.Count; fi++)
            {
                var f = mesh.Faces[fi];
                Vector3 nrm = Vector3.Cross(world[f[1]] - world[f[0]], world[f[2]] - world[f[0]]).normalized;
                float bright = 0.35f + 0.65f * Mathf.Abs(Vector3.Dot(nrm, LightDir));
                int[] faceUV = textured ? mesh.FaceUVs[fi] : null;
                Color flat = Color.white;
                if (faceUV == null)
                {
                    Color baseCol = config.EmblemTint ? tint
                        : (mesh.FaceColors != null ? mesh.FaceColors[fi] : Color.white);
                    flat = baseCol * bright;
                    flat.a = 1f;
                }
                for (int t = 2; t < f.Length; t++)
                {
                    Vector2 uvA = Vector2.zero, uvB = Vector2.zero, uvC = Vector2.zero;
                    if (faceUV != null)
                    {
                        uvA = mesh.UVs[faceUV[0]];
                        uvB = mesh.UVs[faceUV[t - 1]];
                        uvC = mesh.UVs[faceUV[t]];
                    }
                    FillTriangle(lines, mesh, world[f[0]], world[f[t - 1]], world[f[t]],
                        faceUV != null, uvA, uvB, uvC, flat, factor, ref acc, spacing);
                }
            }

            // 画家のアルゴリズム: 遠い線を先に送る
            var arr = lines.ToArray();
            var keys = new float[arr.Length];
            for (int i = 0; i < arr.Length; i++)
            {
                keys[i] = -(((arr[i].A + arr[i].B) * 0.5f) - eye).sqrMagnitude;
            }
            Array.Sort(keys, arr);

            eff.EmblemLines = arr;
            eff.EmblemIndex = 0;
            eff.EmblemDur = Mathf.Max(0.5f, config.Duration * 0.9f);
        }

        // 蓄えたエンブレム線を1フレームぶんずつ各受け手へ送る
        private void FlushEmblem(Effect eff)
        {
            if (eff.EmblemLines == null || eff.EmblemIndex >= eff.EmblemLines.Length)
            {
                return;
            }
            int end = Mathf.Min(eff.EmblemIndex + EmblemPerFrame, eff.EmblemLines.Length);
            foreach (var v in eff.Viewers)
            {
                if (v == null || !v.IsConnected)
                {
                    continue;
                }
                for (int i = eff.EmblemIndex; i < end; i++)
                {
                    var l = eff.EmblemLines[i];
                    UnityEngine.DDraw.Line(v, l.A, l.B, l.C, eff.EmblemDur, false, config.ZTest);
                }
            }
            eff.EmblemIndex = end;
        }

        private struct QLine
        {
            public Vector3 A, B;
            public Color C;
        }

        private static int EstimateLines(Vector3 a, Vector3 b, Vector3 c, float spacing)
        {
            float ab = (a - b).sqrMagnitude, bc = (b - c).sqrMagnitude, ca = (c - a).sqrMagnitude;
            Vector3 p, q, r;
            if (ab >= bc && ab >= ca) { p = a; q = b; r = c; }
            else if (bc >= ca) { p = b; q = c; r = a; }
            else { p = c; q = a; r = b; }
            Vector3 pq = (q - p).normalized;
            float height = (p + pq * Vector3.Dot(r - p, pq) - r).magnitude;
            return Mathf.Clamp(Mathf.CeilToInt(height / spacing), 1, 300);
        }

        // 三角形を最長辺に平行な線で塗る(1層)。textured ならUVに沿って色を採る
        private void FillTriangle(List<QLine> lines, Mesh mesh, Vector3 a, Vector3 b, Vector3 c,
            bool textured, Vector2 uvA, Vector2 uvB, Vector2 uvC,
            Color flat, float factor, ref float acc, float spacing)
        {
            float ab = (a - b).sqrMagnitude, bc = (b - c).sqrMagnitude, ca = (c - a).sqrMagnitude;
            int k = (ab >= bc && ab >= ca) ? 0 : (bc >= ca ? 1 : 2);
            Vector3 p, q, r;
            int ip, iq, ir;
            if (k == 0) { p = a; q = b; r = c; ip = 0; iq = 1; ir = 2; }
            else if (k == 1) { p = b; q = c; r = a; ip = 1; iq = 2; ir = 0; }
            else { p = c; q = a; r = b; ip = 2; iq = 0; ir = 1; }

            Vector3 pq = (q - p).normalized;
            float height = (p + pq * Vector3.Dot(r - p, pq) - r).magnitude;
            int n = Mathf.Clamp(Mathf.CeilToInt(height / spacing), 1, 300);

            float want = n * factor;
            int nn = Mathf.FloorToInt(want);
            acc += want - nn;
            if (acc >= 1f)
            {
                nn++;
                acc -= 1f;
            }
            if (nn <= 0)
            {
                return;
            }

            Vector2 uvP = Vector2.zero, uvQ = Vector2.zero, uvR = Vector2.zero;
            if (textured)
            {
                var corner = new[] { uvA, uvB, uvC };
                uvP = corner[ip];
                uvQ = corner[iq];
                uvR = corner[ir];
            }

            for (int i = 1; i <= nn; i++)
            {
                float t = Mathf.Clamp((i - 0.5f) / nn, 0.01f, 0.99f);
                Vector3 s = Vector3.Lerp(r, p, t);
                Vector3 e = Vector3.Lerp(r, q, t);
                if (!textured)
                {
                    lines.Add(new QLine { A = s, B = e, C = flat });
                    continue;
                }
                Vector2 uvS = Vector2.Lerp(uvR, uvP, t);
                Vector2 uvE = Vector2.Lerp(uvR, uvQ, t);
                EmitTexturedLine(lines, mesh, s, e, uvS, uvE, spacing);
            }
        }

        // 線間隔と同じ間隔でテクスチャを採色し、色の変わり目で線を分割する
        private void EmitTexturedLine(List<QLine> lines, Mesh mesh,
            Vector3 a, Vector3 b, Vector2 uvA, Vector2 uvB, float spacing)
        {
            int samples = Mathf.Clamp(Mathf.CeilToInt(Vector3.Distance(a, b) / spacing), 1, 48);
            float runT0 = 0f;
            Color runCol = mesh.SampleTex(Vector2.Lerp(uvA, uvB, 0.5f / samples));
            int runKey = QuantKey(runCol);
            int segs = 1;
            for (int s2 = 1; s2 < samples; s2++)
            {
                Color col = mesh.SampleTex(Vector2.Lerp(uvA, uvB, (s2 + 0.5f) / samples));
                int key = QuantKey(col);
                if (key != runKey && segs < TexSegMax)
                {
                    float tSplit = (float)s2 / samples;
                    AddSeg(lines, a, b, runT0, tSplit, runCol);
                    runT0 = tSplit;
                    runCol = col;
                    runKey = key;
                    segs++;
                }
            }
            AddSeg(lines, a, b, runT0, 1f, runCol);
        }

        private static void AddSeg(List<QLine> lines, Vector3 a, Vector3 b, float t0, float t1, Color c)
        {
            c.a = 1f;
            lines.Add(new QLine { A = Vector3.Lerp(a, b, t0), B = Vector3.Lerp(a, b, t1), C = c });
        }

        private static int QuantKey(Color c)
        {
            return (Mathf.RoundToInt(Mathf.Clamp01(c.r) * 15f) << 8)
                 | (Mathf.RoundToInt(Mathf.Clamp01(c.g) * 15f) << 4)
                 | Mathf.RoundToInt(Mathf.Clamp01(c.b) * 15f);
        }

        private static int HexVal(char ch)
        {
            if (ch >= '0' && ch <= '9') return ch - '0';
            if (ch >= 'a' && ch <= 'f') return ch - 'a' + 10;
            if (ch >= 'A' && ch <= 'F') return ch - 'A' + 10;
            return 0;
        }

        // OBJ を読む。oxide/data/Finisher/<name>.obj → 無ければ oxide/data/MeshDraw/<name>.obj。
        // 面色 "c R G B" とテクスチャ(tex/tx/vt)の両方に対応(MeshSurfaceDraw と同じ簡易フォーマット)。
        private Mesh LoadObj(string name)
        {
            if (meshCache.TryGetValue(name, out var cached))
            {
                return cached;
            }
            string path = Path.Combine(Interface.Oxide.DataDirectory, "Finisher", name + ".obj");
            if (!File.Exists(path))
            {
                path = Path.Combine(Interface.Oxide.DataDirectory, "MeshDraw", name + ".obj");
            }
            if (!File.Exists(path))
            {
                PrintWarning($"エンブレム '{name}.obj' が見つかりません (oxide/data/Finisher/ または oxide/data/MeshDraw/)");
                meshCache[name] = null;
                return null;
            }

            var verts = new List<Vector3>();
            var uvs = new List<Vector2>();
            var faces = new List<int[]>();
            var faceUVs = new List<int[]>();
            bool anyUV = false;
            List<Color> faceColors = null;
            Color current = Color.white;
            Color32[] tex = null;
            int texW = 0, texH = 0, texRow = 0;
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.StartsWith("tex "))
                {
                    var tt = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (tt.Length >= 3 && int.TryParse(tt[1], out texW) && int.TryParse(tt[2], out texH))
                    {
                        tex = new Color32[texW * texH];
                    }
                }
                else if (line.StartsWith("tx ") && tex != null && texRow < texH)
                {
                    string hex = line.Substring(3).Trim();
                    int count = Mathf.Min(texW, hex.Length / 6);
                    int baseIdx = texRow * texW;
                    for (int x = 0; x < count; x++)
                    {
                        int o = x * 6;
                        tex[baseIdx + x] = new Color32(
                            (byte)((HexVal(hex[o]) << 4) | HexVal(hex[o + 1])),
                            (byte)((HexVal(hex[o + 2]) << 4) | HexVal(hex[o + 3])),
                            (byte)((HexVal(hex[o + 4]) << 4) | HexVal(hex[o + 5])),
                            255);
                    }
                    texRow++;
                }
                else if (line.StartsWith("vt "))
                {
                    var tt = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (tt.Length >= 3)
                    {
                        uvs.Add(new Vector2(
                            float.Parse(tt[1], CultureInfo.InvariantCulture),
                            float.Parse(tt[2], CultureInfo.InvariantCulture)));
                    }
                }
                else if (line.StartsWith("c "))
                {
                    var ct = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (ct.Length >= 4)
                    {
                        if (faceColors == null)
                        {
                            faceColors = new List<Color>();
                            for (int i = 0; i < faces.Count; i++)
                            {
                                faceColors.Add(Color.white);
                            }
                        }
                        current = new Color(
                            float.Parse(ct[1], CultureInfo.InvariantCulture),
                            float.Parse(ct[2], CultureInfo.InvariantCulture),
                            float.Parse(ct[3], CultureInfo.InvariantCulture));
                    }
                }
                else if (line.StartsWith("v "))
                {
                    var tok = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (tok.Length >= 4)
                    {
                        verts.Add(new Vector3(
                            -float.Parse(tok[1], CultureInfo.InvariantCulture), // OBJは右手系→X反転
                            float.Parse(tok[2], CultureInfo.InvariantCulture),
                            float.Parse(tok[3], CultureInfo.InvariantCulture)));
                    }
                }
                else if (line.StartsWith("f "))
                {
                    var tok = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    var idx = new List<int>();
                    var uvIdx = new List<int>();
                    bool hasUV = true;
                    for (int i = 1; i < tok.Length; i++)
                    {
                        var parts = tok[i].Split('/');
                        if (int.TryParse(parts[0], out var vi))
                        {
                            idx.Add(vi > 0 ? vi - 1 : verts.Count + vi);
                        }
                        if (parts.Length > 1 && int.TryParse(parts[1], out var ti))
                        {
                            uvIdx.Add(ti > 0 ? ti - 1 : uvs.Count + ti);
                        }
                        else
                        {
                            hasUV = false;
                        }
                    }
                    if (idx.Count >= 3)
                    {
                        idx.Reverse(); // X反転に合わせて巻き順も反転
                        faces.Add(idx.ToArray());
                        faceColors?.Add(current);
                        if (hasUV && uvIdx.Count == idx.Count)
                        {
                            uvIdx.Reverse();
                            faceUVs.Add(uvIdx.ToArray());
                            anyUV = true;
                        }
                        else
                        {
                            faceUVs.Add(null);
                        }
                    }
                }
            }
            if (verts.Count == 0 || faces.Count == 0)
            {
                meshCache[name] = null;
                return null;
            }
            float minY = float.MaxValue, maxY = float.MinValue;
            foreach (var v in verts)
            {
                minY = Mathf.Min(minY, v.y);
                maxY = Mathf.Max(maxY, v.y);
            }
            var mesh = new Mesh
            {
                Verts = verts.ToArray(),
                Faces = faces,
                FaceColors = faceColors,
                UVs = anyUV ? uvs.ToArray() : null,
                FaceUVs = anyUV ? faceUVs : null,
                Tex = (tex != null && anyUV && texRow == texH) ? tex : null,
                TexW = texW,
                TexH = texH,
                Height = Mathf.Max(maxY - minY, 0.01f),
            };
            meshCache[name] = mesh;
            return mesh;
        }

        #endregion
    }
}
