using System;
using System.Linq;
using AmongUs.GameOptions;
using UnityEngine;

namespace AmongUsRevamped;

/// <summary>
/// Rondoudou is assigned to one vanilla Shapeshifter. The Shapeshifter entry in
/// the game's Vanilla Roles tab therefore controls whether the role can spawn.
/// </summary>
internal static class RondoudouRole
{
    private const byte NoPlayer = byte.MaxValue;

    private static byte _playerId = NoPlayer;
    private static int _usesRemaining;
    private static bool _effectActive;
    private static float _timeRemaining;
    private static int _lastBroadcastSecond = -1;

    private static float _oldPlayerSpeed;
    private static float _oldCrewVision;
    private static float _oldImpostorVision;

    public static bool IsRondoudou(PlayerControl player)
        => player != null && _playerId != NoPlayer && player.PlayerId == _playerId;

    public static void Reset()
    {
        if (_effectActive)
            EndEffect(broadcast: false);

        _playerId = NoPlayer;
        _usesRemaining = 0;
        _timeRemaining = 0f;
        _lastBroadcastSecond = -1;
    }

    public static void AssignFromVanillaRoles()
    {
        if (!AmongUsClient.Instance.AmHost || !Utils.InGame || Utils.IsMeeting ||
            Options.Gamemode.GetValue() != 0 || Utils.isHideNSeek)
            return;

        if (_playerId != NoPlayer)
            return;

        // The player is still a normal Impostor-aligned Shapeshifter.
        // The vanilla role count/chance is configured in the game's "Vanilla Roles" tab.
        var candidates = PlayerControl.AllPlayerControls
            .Where(p => p != null && p.Data != null && !p.Data.IsDead &&
                        p.Data.RoleType == RoleTypes.Shapeshifter)
            .ToArray();

        if (candidates.Length == 0)
            return;

        var selected = candidates[new Random().Next(candidates.Length)];
        _playerId = selected.PlayerId;
        _usesRemaining = Options.RondoudouUses.GetInt();

        // This label is a local-only visual cue. No cosmetic/name RPC is sent,
        // so other players continue to see the player's ordinary outfit and name.
        selected.cosmetics.nameText.text =
            $"<color=#ff8fd6>{selected.Data.PlayerName}</color>\n<color=#ff8fd6>Rondoudou</color>";

        Logger.Info($"Rondoudou assigned to {selected.Data.PlayerName} ({selected.PlayerId}) with {_usesRemaining} uses.", "Rondoudou");
    }

    public static void TryActivate(PlayerControl player)
    {
        if (!AmongUsClient.Instance.AmHost || !IsRondoudou(player))
            return;

        if (Utils.IsActive(SystemTypes.Comms))
        {
            player.Notify("Rondoudou cannot use its power while Communications is sabotaged.");
            player.RpcResetAbilityCooldown();
            return;
        }

        if (_effectActive)
        {
            player.Notify("Rondoudou's effect is already active.");
            player.RpcResetAbilityCooldown();
            return;
        }

        if (_usesRemaining <= 0)
        {
            player.Notify("Rondoudou has no uses remaining.");
            player.RpcResetAbilityCooldown();
            return;
        }

        if (!Utils.InGame || Utils.IsMeeting || !ShipStatus.Instance)
        {
            player.RpcResetAbilityCooldown();
            return;
        }

        _usesRemaining--;

        var options = Main.NormalOptions;
        _oldPlayerSpeed = options.GetFloat(FloatOptionNames.PlayerSpeedMod);
        _oldCrewVision = options.GetFloat(FloatOptionNames.CrewLightMod);
        _oldImpostorVision = options.GetFloat(FloatOptionNames.ImpostorLightMod);

        _effectActive = true;
        _timeRemaining = Options.RondoudouDuration.GetInt();
        _lastBroadcastSecond = (int)Math.Ceiling(_timeRemaining);

        // These are ordinary game options synchronized by the host. This freezes
        // players and darkens vision for vanilla clients without a custom protocol.
        options.SetFloat(FloatOptionNames.PlayerSpeedMod, 0f);
        options.SetFloat(FloatOptionNames.CrewLightMod, 0.01f);
        options.SetFloat(FloatOptionNames.ImpostorLightMod, 0.01f);
        OptionManager.SyncGameOptions();

        Broadcast($"Rondoudou activated its power: everyone is frozen for {_lastBroadcastSecond} seconds.");
        Logger.Info($"Rondoudou activated. Uses remaining: {_usesRemaining}. Duration: {_timeRemaining}s.", "Rondoudou");
        player.RpcResetAbilityCooldown();
    }

    public static void Tick(float deltaTime)
    {
        if (!AmongUsClient.Instance || !AmongUsClient.Instance.AmHost || !_effectActive)
            return;

        if (!Utils.InGame || Utils.IsMeeting || !ShipStatus.Instance)
        {
            EndEffect(broadcast: true);
            return;
        }

        _timeRemaining -= deltaTime;
        int seconds = Math.Max(0, (int)Math.Ceiling(_timeRemaining));

        // Vanilla clients receive shared countdown checkpoints in chat. The
        // host-only build cannot draw a custom centre-screen HUD on unmodded clients.
        if (seconds != _lastBroadcastSecond && seconds is 10 or 5 or 3 or 2 or 1)
        {
            _lastBroadcastSecond = seconds;
            Broadcast($"Rondoudou effect: {seconds} seconds remaining.");
        }

        if (_timeRemaining <= 0f)
            EndEffect(broadcast: true);
    }

    private static void EndEffect(bool broadcast)
    {
        if (!_effectActive)
            return;

        _effectActive = false;
        _timeRemaining = 0f;

        var options = Main.NormalOptions;
        if (options != null)
        {
            options.SetFloat(FloatOptionNames.PlayerSpeedMod, _oldPlayerSpeed);
            options.SetFloat(FloatOptionNames.CrewLightMod, _oldCrewVision);
            options.SetFloat(FloatOptionNames.ImpostorLightMod, _oldImpostorVision);
            OptionManager.SyncGameOptions();
        }

        if (broadcast)
            Broadcast("Rondoudou's effect has ended. Movement and vision are restored.");
    }

    private static void Broadcast(string message)
    {
        if (PlayerControl.LocalPlayer != null)
            PlayerControl.LocalPlayer.RpcSendChat($"<color=#ff8fd6>Rondoudou</color>: {message}");
    }
}

[HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.CoStartGame))]
internal static class RondoudouRoundResetPatch
{
    public static void Prefix()
    {
        if (AmongUsClient.Instance != null && AmongUsClient.Instance.AmHost)
            RondoudouRole.Reset();
    }
}

[HarmonyPatch(typeof(HudManager), nameof(HudManager.OnGameStart))]
internal static class RondoudouRoleStartPatch
{
    public static void Postfix()
    {
        RondoudouRole.AssignFromVanillaRoles();
    }
}

// The vanilla Shapeshifter ability is the button shown beside Kill. The host
// intercepts its command and performs the host-only role effect instead.
[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.CmdCheckShapeshift))]
internal static class RondoudouAbilityButtonPatch
{
    public static bool Prefix(PlayerControl __instance, [HarmonyArgument(0)] PlayerControl target,
        [HarmonyArgument(1)] bool shouldAnimate)
    {
        if (!AmongUsClient.Instance.AmHost || !RondoudouRole.IsRondoudou(__instance))
            return true;

        RondoudouRole.TryActivate(__instance);
        return false;
    }
}

[HarmonyPatch(typeof(InnerNetClient), nameof(InnerNetClient.FixedUpdate))]
internal static class RondoudouEffectTickPatch
{
    public static void Postfix()
    {
        RondoudouRole.Tick(Time.fixedDeltaTime);
    }
}
