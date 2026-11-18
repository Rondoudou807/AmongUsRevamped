using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using AmongUs.GameOptions;
using InnerNet;
using UnityEngine;

namespace AmongUsRevamped;

/// <summary>
/// Host-only Rondoudou ability:
/// - The Shapeshift button selects a target but never changes anybody's outfit.
/// - The selected player's vanilla name is temporarily set to "Rondoudou"
///   through the game's normal SetName RPC, so unmodded Crewmates see it too.
/// - No vision, movement, or audio settings are modified.
/// - Modded clients can show a small role badge above the renamed player.
/// </summary>
internal static class RondoudouRole
{
    private const byte NoPlayer = byte.MaxValue;
    private const string DisplayName = "Rondoudou";
    private const string BadgeResource = "AmongUsRevamped.Resources.Images.RondoudouRoleBadge.png";

    private static byte _playerId = NoPlayer;
    private static byte _targetId = NoPlayer;
    private static int _usesRemaining;
    private static bool _effectActive;
    private static float _timeRemaining;
    private static int _lastBroadcastSecond = -1;
    private static string _targetOriginalName;

    private static Sprite _badgeSprite;
    private static readonly Dictionary<byte, GameObject> Badges = new();

    public static bool IsRondoudou(PlayerControl player)
        => player != null && _playerId != NoPlayer && player.PlayerId == _playerId;

    public static void Reset()
    {
        if (_effectActive)
            EndEffect(broadcast: false);

        _playerId = NoPlayer;
        _targetId = NoPlayer;
        _usesRemaining = 0;
        _timeRemaining = 0f;
        _lastBroadcastSecond = -1;
        _targetOriginalName = null;
        ClearBadges();
    }

    public static void AssignFromVanillaRoles()
    {
        if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost ||
            !Utils.InGame || Utils.IsMeeting ||
            Options.Gamemode.GetValue() != 0 || Utils.isHideNSeek)
            return;

        if (_playerId != NoPlayer)
            return;

        var candidates = new List<PlayerControl>();
        foreach (PlayerControl candidate in PlayerControl.AllPlayerControls)
        {
            if (candidate != null && candidate.Data != null && !candidate.Data.IsDead &&
                candidate.Data.RoleType == RoleTypes.Shapeshifter)
                candidates.Add(candidate);
        }

        if (candidates.Count == 0)
            return;

        PlayerControl selected = candidates[new System.Random().Next(candidates.Count)];
        _playerId = selected.PlayerId;
        _usesRemaining = Options.RondoudouUses.GetInt();

        UpdateRoleLabel(selected);
        Logger.Info($"Rondoudou assigned to {selected.Data.PlayerName} ({selected.PlayerId}) with {_usesRemaining} uses.", "Rondoudou");
    }

    public static void TryActivate(PlayerControl player, PlayerControl target)
    {
        if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost || !IsRondoudou(player))
            return;

        // Cancel the vanilla Shapeshift in every case: this role never changes a player's skin.
        player.RpcRejectShapeshift();

        if (target == null || target.Data == null || target.PlayerId == player.PlayerId)
        {
            Broadcast("Choose another player as the target.");
            return;
        }

        if (Utils.IsActive(SystemTypes.Comms))
        {
            Broadcast("The power cannot be activated during Communications sabotage.");
            return;
        }

        if (_effectActive)
        {
            Broadcast("Rondoudou's power is already active.");
            return;
        }

        if (_usesRemaining <= 0)
        {
            Broadcast("Rondoudou has no uses remaining.");
            return;
        }

        if (!Utils.InGame || Utils.IsMeeting || !ShipStatus.Instance || target.Data.IsDead)
            return;

        _targetId = target.PlayerId;
        _targetOriginalName = target.Data.PlayerName;
        _usesRemaining--;

        // Vanilla SetName RPC: all clients, including unmodded Crewmates, receive the
        // temporary label. No SetSkin / SetColor / cosmetic RPC is sent.
        target.RpcSetName(DisplayName);

        _effectActive = true;
        _timeRemaining = Options.RondoudouDuration.GetInt();
        _lastBroadcastSecond = (int)Math.Ceiling(_timeRemaining);

        UpdateRoleLabel(player);
        Broadcast($"{_targetOriginalName} is now displayed as {DisplayName} for {_lastBroadcastSecond} seconds.");
        Logger.Info($"Rondoudou renamed player {_targetId} for {_timeRemaining}s. Uses remaining: {_usesRemaining}.", "Rondoudou");
    }

    public static void Tick(float deltaTime)
    {
        if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost || !_effectActive)
            return;

        if (!Utils.InGame || Utils.IsMeeting || !ShipStatus.Instance)
        {
            EndEffect(broadcast: true);
            return;
        }

        _timeRemaining -= deltaTime;
        int seconds = Math.Max(0, (int)Math.Ceiling(_timeRemaining));

        if (seconds != _lastBroadcastSecond && seconds > 0 && seconds % 5 == 0)
        {
            _lastBroadcastSecond = seconds;
            Broadcast($"{DisplayName}: {seconds} seconds remaining.");
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

        PlayerControl target = FindPlayer(_targetId);
        if (target != null && target.Data != null && !string.IsNullOrEmpty(_targetOriginalName))
            target.RpcSetName(_targetOriginalName);

        string restoredName = _targetOriginalName;
        _targetId = NoPlayer;
        _targetOriginalName = null;

        if (broadcast)
            Broadcast(string.IsNullOrEmpty(restoredName)
                ? "The effect has ended."
                : $"{restoredName}'s name has been restored.");
    }

    public static bool HandleAbilityCommand(PlayerControl player, PlayerControl target)
    {
        if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost || !IsRondoudou(player))
            return true;

        TryActivate(player, target);
        return false;
    }

    private static PlayerControl FindPlayer(byte playerId)
    {
        if (playerId == NoPlayer)
            return null;

        foreach (PlayerControl player in PlayerControl.AllPlayerControls)
        {
            if (player != null && player.PlayerId == playerId)
                return player;
        }

        return null;
    }

    private static void UpdateRoleLabel(PlayerControl player)
    {
        if (player == null || player.Data == null || player.cosmetics == null || player.cosmetics.nameText == null)
            return;

        player.cosmetics.nameText.text =
            $"<color=#ff8fd6>{player.Data.PlayerName}</color>\n<color=#ff8fd6>Rondoudou ({_usesRemaining}/{Options.RondoudouUses.GetInt()})</color>";
    }

    /// <summary>
    /// Runs on each client with the mod installed. The badge is only a small
    /// world-space marker; it does not replace or alter the player's outfit.
    /// </summary>
    public static void UpdateBadge(PlayerControl player)
    {
        if (player == null || player.Data == null)
            return;

        bool shouldShow = Utils.InGame && !player.Data.IsDead &&
            string.Equals(player.Data.PlayerName, DisplayName, StringComparison.Ordinal);

        if (!shouldShow)
        {
            RemoveBadge(player.PlayerId);
            return;
        }

        if (Badges.ContainsKey(player.PlayerId) || player.transform == null)
            return;

        Sprite sprite = LoadBadgeSprite();
        if (sprite == null)
            return;

        var badgeObject = new GameObject("RondoudouRoleBadge");
        badgeObject.transform.SetParent(player.transform, false);
        badgeObject.transform.localPosition = new Vector3(0f, 1.05f, -0.15f);
        badgeObject.transform.localScale = new Vector3(0.34f, 0.34f, 1f);

        var renderer = badgeObject.AddComponent<SpriteRenderer>();
        renderer.sprite = sprite;
        renderer.sortingOrder = 100;

        Badges[player.PlayerId] = badgeObject;
    }

    private static Sprite LoadBadgeSprite()
    {
        if (_badgeSprite != null)
            return _badgeSprite;

        try
        {
            using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(BadgeResource);
            if (stream == null)
            {
                Logger.Warn($"Embedded badge resource not found: {BadgeResource}", "Rondoudou");
                return null;
            }

            byte[] bytes = new byte[stream.Length];
            stream.Read(bytes, 0, bytes.Length);

            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!ImageConversion.LoadImage(texture, bytes))
            {
                UnityEngine.Object.Destroy(texture);
                Logger.Warn("Could not decode Rondoudou role badge.", "Rondoudou");
                return null;
            }

            texture.filterMode = FilterMode.Bilinear;
            texture.wrapMode = TextureWrapMode.Clamp;
            _badgeSprite = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height),
                new Vector2(0.5f, 0.5f), 128f);
            return _badgeSprite;
        }
        catch (Exception exception)
        {
            Logger.Error($"Could not load Rondoudou role badge: {exception}", "Rondoudou");
            return null;
        }
    }

    private static void RemoveBadge(byte playerId)
    {
        if (!Badges.TryGetValue(playerId, out GameObject badge))
            return;

        Badges.Remove(playerId);
        if (badge != null)
            UnityEngine.Object.Destroy(badge);
    }

    private static void ClearBadges()
    {
        foreach (var badge in Badges.Values)
        {
            if (badge != null)
                UnityEngine.Object.Destroy(badge);
        }
        Badges.Clear();
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

[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.CheckShapeshift))]
internal static class RondoudouCheckShapeshiftPatch
{
    public static bool Prefix(PlayerControl __instance, [HarmonyArgument(0)] PlayerControl target,
        [HarmonyArgument(1)] bool shouldAnimate)
        => RondoudouRole.HandleAbilityCommand(__instance, target);
}

[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.CmdCheckShapeshift))]
internal static class RondoudouCmdCheckShapeshiftPatch
{
    public static bool Prefix(PlayerControl __instance, [HarmonyArgument(0)] PlayerControl target,
        [HarmonyArgument(1)] bool shouldAnimate)
        => RondoudouRole.HandleAbilityCommand(__instance, target);
}

[HarmonyPatch(typeof(InnerNetClient), nameof(InnerNetClient.FixedUpdate))]
internal static class RondoudouEffectTickPatch
{
    public static void Postfix()
    {
        RondoudouRole.Tick(Time.fixedDeltaTime);
    }
}

[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.FixedUpdate))]
internal static class RondoudouBadgePatch
{
    public static void Postfix(PlayerControl __instance)
    {
        RondoudouRole.UpdateBadge(__instance);
    }
}
