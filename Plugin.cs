using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace NonCriticalDamage
{
    [BepInPlugin(Guid, Name, Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "nuclearoption.noncriticaldamage";
        public const string Name = "NonCriticalDamage";
        public const string Version = "1.26.0";

        internal static ManualLogSource Log;
        internal static ConfigEntry<bool> AffectAIAircraft;
        internal static ConfigEntry<bool> ShowHurtOverlay;
        internal static ConfigEntry<float> CameraShake;
        internal static ConfigEntry<bool> DebugLogging;

        private void Awake()
        {
            Log = Logger;

            AffectAIAircraft = Config.Bind(
                "General", "AffectAIAircraft", false,
                "Protect AI-flown aircraft as well as player-flown ones.");

            ShowHurtOverlay = Config.Bind(
                "Effects", "ShowHurtOverlay", false,
                "Show the red screen flash the game uses when the pilot is hurt. " +
                "false = never show it. Only affects your own screen.");

            CameraShake = Config.Bind(
                "Effects", "CameraShake", 5f,
                new ConfigDescription(
                    "Strength of the camera shake when a protected aircraft is hit by bullets or " +
                    "explosions, in the cockpit and the orbit and chase views. 0 = off, 5 = full " +
                    "strength, 1 = a fifth as strong.",
                    new AcceptableValueRange<float>(0f, 5f)));

            DebugLogging = Config.Bind(
                "Debug", "DebugLogging", false,
                "Write a line to the BepInEx log every time the mod reduces something. " +
                "Pierce and fire are shown as the amount that gets past the part's armour. " +
                "Turn on when reporting a problem.");

            var harmony = new Harmony(Guid);
            ApplyPatch(harmony, typeof(TakeDamagePatch));
            ApplyPatch(harmony, typeof(ShockwavePatch));
            ApplyPatch(harmony, typeof(ReportedDamagePatch));
            ApplyPatch(harmony, typeof(FuelIgnitionPatch));
            ApplyPatch(harmony, typeof(NuclearBlastPatch));
            ApplyPatch(harmony, typeof(HurtOverlayPatch));
            ApplyPatch(harmony, typeof(DamageShakePatch));
            ApplyPatch(harmony, typeof(ThirdPersonShakePatch));
            ApplyPatch(harmony, typeof(VersionTagPatch));

            foreach (var method in harmony.GetPatchedMethods())
                Log.LogInfo($"Patched {method.DeclaringType?.Name}.{method.Name}");
            // Players can only see and join each other when this line matches exactly on both machines.
            Log.LogInfo($"Lobby version: {Application.version}");
            Log.LogInfo($"{Name} {Version} loaded.");
        }

        // Each patch class is applied on its own so one failing (after a game update,
        // say) is reported clearly and does not take the others down with it.
        private static void ApplyPatch(Harmony harmony, System.Type patchClass)
        {
            try
            {
                harmony.CreateClassProcessor(patchClass).Patch();
            }
            catch (System.Exception e)
            {
                Log.LogError($"Failed to apply {patchClass.Name}: {e}");
            }
        }

        /// <summary>Finds a method whose name starts with a prefix (for generated RPC handler names).</summary>
        internal static MethodInfo FindMethodStartingWith(System.Type type, string prefix)
        {
            foreach (MethodInfo method in AccessTools.GetDeclaredMethods(type))
                if (method.Name.StartsWith(prefix))
                    return method;
            return null;
        }
    }

    /// <summary>The fixed damage, shove and effect values. Change these and rebuild to retune.</summary>
    internal static class Tuning
    {
        /// <summary>Bullet, fire, collision and pilot G-force damage is divided by this.</summary>
        internal const float HitPointMultiplier = 10f;

        /// <summary>Blast damage from explosions is divided by this instead.</summary>
        internal const float BlastDamageMultiplier = 10f;

        /// <summary>The most pierce, blast or fire damage one hit can do to one part, after
        /// dividing and after armour. Collisions and G-force are not capped.</summary>
        internal const float MaxDamagePerHit = 25f;

        /// <summary>The physical shove from explosions is divided by this.</summary>
        internal const float BlastShoveMultiplier = 3f;

        /// <summary>The most overpressure (shove) one explosion can apply to one part, after dividing.</summary>
        internal const float MaxOverpressurePerHit = 1000f;

        /// <summary>Engines and the parts they depend on take this much less again.</summary>
        internal const float EngineToughness = 4f;

        /// <summary>Explosions of at least this yield, in kilograms of TNT, count as nuclear (0.1 kt).</summary>
        internal const float NukeYieldKg = 100000f;

        /// <summary>Camera shake per hit point lost: the strongest shake at 4 hit points.</summary>
        internal const float ShakePerHitPoint = 0.25f;
    }

    internal static class Protection
    {
        /// <summary>Set while the game is applying a nuclear shockwave to something.</summary>
        internal static bool NuclearBlastInProgress;

        // How long after a nuclear blast reaches an aircraft it stays unprotected, so the
        // follow-on effects (G-force on the pilot, fuel fires, collisions) are vanilla too.
        private const float NukedSeconds = 5f;
        private static readonly Dictionary<int, float> nukedUntil = new Dictionary<int, float>();

        internal static void MarkNuked(Aircraft aircraft)
        {
            int id = aircraft.GetInstanceID();
            if (Plugin.DebugLogging.Value && !IsNuked(id))
                Plugin.Log.LogInfo($"Nuclear blast reached {aircraft.name}: protection off for {NukedSeconds:0}s");
            nukedUntil[id] = Time.time + NukedSeconds;
        }

        private static bool IsNuked(int id)
        {
            if (!nukedUntil.TryGetValue(id, out float until))
                return false;
            if (Time.time <= until)
                return true;
            nukedUntil.Remove(id);
            return false;
        }

        /// <summary>True if this unit is an aircraft the mod looks after right now.</summary>
        internal static bool IsProtected(Unit unit)
        {
            if (!(unit is Aircraft aircraft))
                return false;

            if (!Plugin.AffectAIAircraft.Value && aircraft.Player == null)
                return false;

            return nukedUntil.Count == 0 || !IsNuked(aircraft.GetInstanceID());
        }
    }

    /// <summary>
    /// Weapon damage. Every damageable thing on an aircraft has its own TakeDamage method.
    /// It runs on the host only, turns the raw damage into "net" damage using that part's
    /// armour, decides whether the hit kills the aircraft or tears the part off, and then
    /// sends the net damage to all players. We shrink the incoming damage just before that
    /// happens, so those checks and what every client sees all stay in agreement.
    /// </summary>
    [HarmonyPatch]
    internal static class TakeDamagePatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            var types = new[]
            {
                typeof(UnitPart),       // fuselage, wings, tail, control surfaces, gear...
                typeof(Turbofan),       // jet engines
                typeof(Pilot),          // the pilot in the seat
                typeof(SwashRotor),     // helicopter rotors
                typeof(SoftBodyRotor),
                typeof(MountedCargo),   // carried cargo
            };

            foreach (var type in types)
            {
                var method = AccessTools.DeclaredMethod(type, "TakeDamage");
                if (method != null)
                    yield return method;
                else
                    Plugin.Log.LogWarning($"{type.Name}.TakeDamage not found - game update? That part type keeps vanilla damage.");
            }
        }

        // TakeDamage(float pierceDamage, float blastDamage, float amountAffected,
        //            float fireDamage, float impactDamage, PersistentID dealerID)
        // Arguments are taken by position so a renamed parameter in a game update can't break us.
        private static void Prefix(object __instance, ref float __0, ref float __1, ref float __2, ref float __3, ref float __4)
        {
            if (!(__instance is IDamageable damageable) || !(damageable.GetUnit() is Aircraft aircraft))
                return;

            // Nuclear blast: leave the damage alone and drop protection for a few seconds.
            if (Protection.NuclearBlastInProgress)
            {
                Protection.MarkNuked(aircraft);
                return;
            }

            if (!Protection.IsProtected(aircraft))
                return;

            ArmorProperties armor = damageable.GetArmorProperties();
            if (armor == null)
                return;

            // Engines: a turbofan has hit points of its own, and every engine fails when
            // certain airframe parts are badly damaged or torn off. Protect all of those extra.
            var part = __instance as UnitPart;
            bool enginePart = __instance is Turbofan || (part != null && EngineParts.IsEnginePart(part));
            float multiplier = enginePart ? Tuning.HitPointMultiplier * Tuning.EngineToughness : Tuning.HitPointMultiplier;
            float blastMultiplier = enginePart ? Tuning.BlastDamageMultiplier * Tuning.EngineToughness : Tuning.BlastDamageMultiplier;
            bool rotor = __instance is SwashRotor || __instance is SoftBodyRotor;

            // For the log: the damage that actually gets past the armour, before and after.
            bool log = Plugin.DebugLogging.Value;
            float pierceBefore = 0f, fireBefore = 0f, blastBefore = 0f, impactBefore = 0f;
            if (log)
            {
                pierceBefore = Mathf.Max(__0 - armor.pierceArmor, 0f);
                fireBefore = Mathf.Max(__3 - armor.fireArmor, 0f);
                blastBefore = rotor ? Mathf.Max(__1 - armor.blastArmor, 0f) : __1 * __2;
                impactBefore = __4;
            }

            // Pierce and fire: the game uses max(damage - armor, 0) / tolerance. Only the part
            // that gets through the armour is divided and then capped, so weak hits that
            // vanilla armour already stops still do nothing, and nothing new gets stopped.
            __0 = ReduceAndCap(__0, armor.pierceArmor, multiplier);
            __3 = ReduceAndCap(__3, armor.fireArmor, multiplier);

            // Blast: most parts use blastDamage * amountAffected, so shrinking
            // amountAffected is exact. Rotors ignore amountAffected and subtract
            // blast armour instead, so they get the same treatment as pierce.
            if (rotor)
            {
                __1 = ReduceAndCap(__1, armor.blastArmor, blastMultiplier);
            }
            else
            {
                __2 /= blastMultiplier;
                if (__1 * __2 > Tuning.MaxDamagePerHit)
                    __2 = Tuning.MaxDamagePerHit / __1;
            }

            // Impact (collisions), not capped: airframe parts, engines and cargo send theirs
            // straight to the clients. Pilot and rotors hand theirs to Unit.Damage, which
            // ReportedDamagePatch looks after.
            if (part != null || __instance is Turbofan || __instance is MountedCargo)
                __4 /= multiplier;

            if (log)
            {
                float pierceAfter = Mathf.Max(__0 - armor.pierceArmor, 0f);
                float fireAfter = Mathf.Max(__3 - armor.fireArmor, 0f);
                float blastAfter = rotor ? Mathf.Max(__1 - armor.blastArmor, 0f) : __1 * __2;
                Plugin.Log.LogInfo(
                    $"TakeDamage {NameOf(__instance)}{(enginePart ? " (engine)" : "")}: pierce {pierceBefore:0.##}->{pierceAfter:0.##}, " +
                    $"blast {blastBefore:0.#}->{blastAfter:0.##}, fire {fireBefore:0.##}->{fireAfter:0.##}, " +
                    $"impact {impactBefore:0.#}->{__4:0.##}");
            }
        }

        private static float ReduceAndCap(float damage, float armor, float multiplier)
        {
            return damage > armor ? armor + Mathf.Min((damage - armor) / multiplier, Tuning.MaxDamagePerHit) : damage;
        }

        internal static string NameOf(object part)
        {
            return part is Component component ? component.name : part.GetType().Name;
        }
    }

    /// <summary>
    /// The physical shove from an explosion. This is not hit point damage: the game pushes
    /// the airframe directly, which can snap parts off and gives the pilot a G-force spike.
    /// It runs on whichever machine is simulating the aircraft (the pilot's own).
    /// </summary>
    [HarmonyPatch]
    internal static class ShockwavePatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (var type in new[] { typeof(UnitPart), typeof(AeroPart) })
            {
                var method = AccessTools.DeclaredMethod(type, "TakeShockwave");
                if (method != null)
                    yield return method;
                else
                    Plugin.Log.LogWarning($"{type.Name}.TakeShockwave not found - game update?");
            }
        }

        // TakeShockwave(Vector3 origin, float overpressure, float blastPower)
        private static void Prefix(UnitPart __instance, ref float __1)
        {
            if (Protection.NuclearBlastInProgress || !Protection.IsProtected(__instance.parentUnit))
                return;

            float before = __1;
            __1 = Mathf.Min(__1 / Tuning.BlastShoveMultiplier, Tuning.MaxOverpressurePerHit);

            if (Plugin.DebugLogging.Value)
                Plugin.Log.LogInfo($"Shockwave {__instance.name}: overpressure {before:0.#}->{__1:0.#}");
        }
    }

    /// <summary>
    /// Damage that is reported rather than calculated on the host: pilot G-force damage and
    /// collisions. On the host it goes through Unit.Damage; from a client it arrives in the
    /// CmdDamage handler. Both are patched and both only act on the host, so the reduction
    /// is applied exactly once whoever is flying.
    /// </summary>
    [HarmonyPatch]
    internal static class ReportedDamagePatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            var damage = AccessTools.DeclaredMethod(typeof(Unit), "Damage", new[] { typeof(byte), typeof(DamageInfo) });
            if (damage != null)
                yield return damage;
            else
                Plugin.Log.LogWarning("Unit.Damage not found - game update?");

            // The handler's real name carries a generated number, e.g. UserCode_CmdDamage_-2104360672.
            var handler = Plugin.FindMethodStartingWith(typeof(Unit), "UserCode_CmdDamage");
            if (handler != null)
                yield return handler;
            else
                Plugin.Log.LogWarning("Unit CmdDamage handler not found - game update?");
        }

        // Damage(byte index, DamageInfo damageInfo)
        private static void Prefix(Unit __instance, byte __0, ref DamageInfo __1)
        {
            if (!__instance.IsServer || !Protection.IsProtected(__instance))
                return;

            List<DamageablePart> parts = __instance.damageables;
            if (parts == null || __0 >= parts.Count)
                return;

            // Only the pilot and ordinary airframe parts. Rotors reuse the impact
            // field for something else, so they are left alone.
            IDamageable target = parts[__0].Damageable;
            if (!(target is Pilot) && !(target is UnitPart))
                return;

            float impact = NetworkFloatHelper.Decompress(__1.impactDamage);
            if (impact <= 0f)
                return;

            float reduced = impact / Tuning.HitPointMultiplier;
            __1 = new DamageInfo(
                NetworkFloatHelper.Decompress(__1.pierceDamage),
                NetworkFloatHelper.Decompress(__1.blastDamage),
                NetworkFloatHelper.Decompress(__1.fireDamage),
                reduced);

            if (Plugin.DebugLogging.Value)
                Plugin.Log.LogInfo($"Reported damage {TakeDamagePatch.NameOf(target)}: impact {impact:0.#}->{reduced:0.##}");
        }
    }

    /// <summary>
    /// Fuel tank ignition from hits. When a part is damaged, a leaking fuel tank in it checks
    /// whether this hit's pierce, blast or fire damage beats a random ignition threshold. It
    /// sees the damage after this mod has divided it, so it would almost never ignite; here
    /// the check is shown the damage scaled back up by the multipliers. Only that check sees
    /// the larger numbers; the part's hit points are unchanged.
    /// </summary>
    [HarmonyPatch]
    internal static class FuelIgnitionPatch
    {
        private static AccessTools.FieldRef<FuelTank, Aircraft> tankAircraft;
        private static AccessTools.FieldRef<FuelTank, UnitPart> tankPart;

        private static IEnumerable<MethodBase> TargetMethods()
        {
            var method = AccessTools.DeclaredMethod(typeof(FuelTank), "CheckDamageStatus");
            if (method != null && AccessTools.Field(typeof(FuelTank), "aircraft") != null && AccessTools.Field(typeof(FuelTank), "part") != null)
            {
                tankAircraft = AccessTools.FieldRefAccess<FuelTank, Aircraft>("aircraft");
                tankPart = AccessTools.FieldRefAccess<FuelTank, UnitPart>("part");
                yield return method;
            }
            else
            {
                Plugin.Log.LogWarning("FuelTank.CheckDamageStatus not found - game update? Fuel fires will use reduced damage.");
            }
        }

        // CheckDamageStatus(UnitPart.OnApplyDamage e)
        private static void Prefix(FuelTank __instance, ref UnitPart.OnApplyDamage __0)
        {
            if (!Protection.IsProtected(tankAircraft(__instance)))
                return;

            UnitPart part = tankPart(__instance);
            float toughness = part != null && EngineParts.IsEnginePart(part) ? Tuning.EngineToughness : 1f;

            __0.pierceDamage *= Tuning.HitPointMultiplier * toughness;
            __0.fireDamage *= Tuning.HitPointMultiplier * toughness;
            __0.blastDamage *= Tuning.BlastDamageMultiplier * toughness;
        }
    }

    /// <summary>
    /// Large explosions (anything too big for the game's fragment model, which includes all
    /// nuclear weapons) are applied by Shockwave.InfluencedObject.HasShockwaveReached. While
    /// that runs for a nuclear-sized yield we raise a flag so TakeDamagePatch stands aside.
    /// This is fixed rather than a setting: the shove part runs on each player's own machine,
    /// so a setting there would let a client protect itself without the host's agreement.
    /// </summary>
    [HarmonyPatch]
    internal static class NuclearBlastPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            var inner = AccessTools.Inner(typeof(Shockwave), "InfluencedObject");
            var method = inner != null ? AccessTools.DeclaredMethod(inner, "HasShockwaveReached") : null;
            if (method != null)
                yield return method;
            else
                Plugin.Log.LogWarning("Shockwave.InfluencedObject.HasShockwaveReached not found - game update? Nukes will be reduced like other weapons.");
        }

        // HasShockwaveReached(Vector3 blastOrigin, float blastPropagation, float overpressure,
        //                     float blastYield, float blastPower, PersistentID ownerID)
        // blastYield is in kilograms of TNT.
        private static void Prefix(float __3)
        {
            Protection.NuclearBlastInProgress = __3 >= Tuning.NukeYieldKg;
        }

        private static void Finalizer()
        {
            Protection.NuclearBlastInProgress = false;
        }
    }

    /// <summary>
    /// Works out which airframe parts each engine depends on. An engine fails once one of
    /// those parts drops below a damage threshold, or is torn off, so they are the parts
    /// that need extra protection. The list is read from the engines the first time an
    /// aircraft takes damage and then kept.
    /// </summary>
    internal static class EngineParts
    {
        private static readonly FieldInfo turbojetCriticalParts = AccessTools.Field(typeof(Turbojet), "criticalParts");
        private static readonly FieldInfo turbofanCriticalParts = AccessTools.Field(typeof(Turbofan), "criticalParts");
        private static readonly FieldInfo turbofanPart = AccessTools.Field(typeof(Turbofan), "part");
        private static readonly FieldInfo criticalPartPart = FindCriticalPartField();

        private static readonly Dictionary<int, HashSet<int>> partsByAircraft = new Dictionary<int, HashSet<int>>();

        private static FieldInfo FindCriticalPartField()
        {
            System.Type criticalPart = AccessTools.Inner(typeof(Turbofan), "CriticalPart");
            return criticalPart != null ? AccessTools.Field(criticalPart, "part") : null;
        }

        internal static bool IsEnginePart(UnitPart part)
        {
            if (!(part.parentUnit is Aircraft aircraft))
                return false;

            int id = aircraft.GetInstanceID();
            if (!partsByAircraft.TryGetValue(id, out HashSet<int> parts))
            {
                parts = Collect(aircraft);
                partsByAircraft[id] = parts;
            }
            return parts.Contains(part.GetInstanceID());
        }

        private static HashSet<int> Collect(Aircraft aircraft)
        {
            var result = new HashSet<int>();
            var names = Plugin.DebugLogging.Value ? new List<string>() : null;

            void Add(UnitPart p)
            {
                if (p != null && result.Add(p.GetInstanceID()))
                    names?.Add(p.name);
            }

            if (aircraft.engineStates != null)
            {
                foreach (IEngine engine in aircraft.engineStates)
                {
                    if (engine is Turbojet turbojet)
                    {
                        if (turbojetCriticalParts?.GetValue(turbojet) is UnitPart[] critical)
                            foreach (UnitPart p in critical)
                                Add(p);
                    }
                    else if (engine is Turbofan turbofan)
                    {
                        Add(turbofanPart?.GetValue(turbofan) as UnitPart);
                        if (criticalPartPart != null && turbofanCriticalParts?.GetValue(turbofan) is System.Array critical)
                            foreach (object c in critical)
                                if (c != null)
                                    Add(criticalPartPart.GetValue(c) as UnitPart);
                    }
                }
            }

            if (names != null)
                Plugin.Log.LogInfo(names.Count > 0
                    ? $"Engine parts on {aircraft.name}: {string.Join(", ", names)}"
                    : $"No engine parts found on {aircraft.name} - engine toughness will only affect turbofans.");

            return result;
        }
    }

    /// <summary>
    /// The red flash on screen when the pilot is hurt. The game calls GameplayUI.FlashHurt,
    /// which records the pilot's hit points and turns the overlay on; it then fades by itself.
    /// When the overlay is switched off we keep the hit point record and skip the flash.
    /// </summary>
    [HarmonyPatch(typeof(GameplayUI), nameof(GameplayUI.FlashHurt))]
    internal static class HurtOverlayPatch
    {
        // FlashHurt(float damage, float remainingHitPoints)
        private static bool Prefix(GameplayUI __instance, float __1)
        {
            if (Plugin.ShowHurtOverlay.Value)
                return true;

            __instance.pilotHitPoints = __1;
            return false;
        }
    }

    /// <summary>
    /// Camera shake on damage. Every machine receives each hit through the aircraft's damage
    /// RPC, and the game already shakes the aircraft there by 1% of the damage. With damage
    /// divided down that is barely felt, so we add more on top, scaled by the hit points lost.
    /// Bullets and explosions only: fire burns continuously, and impact is also used for
    /// collisions and pilot G-force.
    /// </summary>
    [HarmonyPatch]
    internal static class DamageShakePatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            // The real name carries a generated number, e.g. UserCode_RpcDamage_870168505.
            var method = Plugin.FindMethodStartingWith(typeof(Aircraft), "UserCode_RpcDamage");
            if (method != null)
                yield return method;
            else
                Plugin.Log.LogWarning("Aircraft RpcDamage handler not found - game update? Damage camera shake will do nothing.");
        }

        // (byte index, DamageInfo damageInfo)
        private static void Postfix(Aircraft __instance, DamageInfo __1)
        {
            float strength = Plugin.CameraShake.Value * 0.2f;
            if (strength <= 0f)
                return;

            float blast = NetworkFloatHelper.Decompress(__1.blastDamage);
            float pierce = NetworkFloatHelper.Decompress(__1.pierceDamage);
            if (blast + pierce <= 0f || !Protection.IsProtected(__instance))
                return;

            float low = blast * Tuning.ShakePerHitPoint;
            float high = pierce * Tuning.ShakePerHitPoint;
            __instance.ShakeAircraft(low * strength, high * strength);
            ThirdPersonShakePatch.Add(__instance, low, high);
        }
    }

    /// <summary>
    /// Shake for the outside cameras. The orbit and chase cameras place the camera again
    /// every frame and have no shake of their own, so after the game has placed it we turn
    /// it by a small wobble, and put the rotation back before the next frame so the camera
    /// never drifts. Only rotation is touched, so a floating-origin shift can't move it.
    /// </summary>
    [HarmonyPatch(typeof(CameraStateManager), "LateUpdate")]
    internal static class ThirdPersonShakePatch
    {
        private const float MaxLowDegrees = 2.5f;   // slow sway at full shake
        private const float MaxHighDegrees = 0.8f;  // fast jitter at full shake
        private const float DecayPerSecond = 3f;
        private const float MinShake = 0.001f;

        private static int aircraftId;
        private static float low, high;
        private static bool active;   // any shake left to play out

        private static bool applied;  // a wobble is on the camera right now
        private static Transform shaken;
        private static Quaternion before, after;

        internal static void Add(Aircraft aircraft, float lowAmount, float highAmount)
        {
            int id = aircraft.GetInstanceID();
            if (id != aircraftId)
            {
                aircraftId = id;
                low = high = 0f;
            }
            low = Mathf.Min(low + lowAmount, 1f);
            high = Mathf.Min(high + highAmount, 1f);
            active = true;
        }

        private static void Prefix()
        {
            if (!applied)
                return;

            // Undo last frame's wobble, unless something else has moved the camera since.
            applied = false;
            if (shaken != null && shaken.localRotation == after)
                shaken.localRotation = before;
        }

        private static void Postfix(CameraStateManager __instance)
        {
            if (!active)
                return;

            float decay = Mathf.Exp(-DecayPerSecond * Time.unscaledDeltaTime);
            low *= decay;
            high *= decay;
            if (low < MinShake && high < MinShake)
            {
                low = high = 0f;
                active = false;
                return;
            }

            CameraBaseState state = __instance.currentState;
            if (state != __instance.orbitState && state != __instance.chaseState)
                return;

            Unit following = __instance.followingUnit;
            if (following == null || following.GetInstanceID() != aircraftId)
                return;

            Camera camera = __instance.mainCamera;
            if (camera == null)
                return;

            float strength = Plugin.CameraShake.Value * 0.2f;
            float t = Time.time;
            float lowDeg = low * MaxLowDegrees * strength;
            float highDeg = high * MaxHighDegrees * strength;
            float pitch = Noise(t * 2f, 0f) * lowDeg + Noise(t * 25f, 10f) * highDeg;
            float yaw = Noise(t * 2f, 20f) * lowDeg + Noise(t * 25f, 30f) * highDeg;
            float roll = Noise(t * 1.5f, 40f) * lowDeg * 0.5f;

            shaken = camera.transform;
            before = shaken.localRotation;
            after = before * Quaternion.Euler(pitch, yaw, roll);
            shaken.localRotation = after;
            applied = true;
        }

        // Smooth noise between -1 and 1.
        private static float Noise(float x, float seed)
        {
            return Mathf.PerlinNoise(x, seed) * 2f - 1f;
        }
    }

    /// <summary>
    /// Appends a mod tag to Application.version, the same way Ace Combat Reloads does, so the
    /// game's own lobby filter, lobby join check and dedicated-server filter only match players
    /// running a compatible copy of this mod. Runs last, so with Ace Combat Reloads installed too
    /// the tags always end up in the same order on every machine, whatever the load order.
    /// </summary>
    [HarmonyPatch(typeof(Application), nameof(Application.version), MethodType.Getter)]
    internal static class VersionTagPatch
    {
        internal const string Tag = "_noncriticaldamage-v1.26";

        private static string lastBase;
        private static string lastTagged;

        [HarmonyPriority(Priority.Last)]
        private static void Postfix(ref string __result)
        {
            if (__result == null || __result.EndsWith(Tag, System.StringComparison.Ordinal))
                return;

            if (!string.Equals(__result, lastBase, System.StringComparison.Ordinal))
            {
                lastBase = __result;
                lastTagged = __result + Tag;
            }
            __result = lastTagged;
        }
    }
}
