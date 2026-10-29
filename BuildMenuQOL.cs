using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace BuildMenuQOL;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class BuildMenuQOLPlugin : BaseUnityPlugin
{
    public const string PluginGuid = "ShaolinShade.BuildMenuQOL";
    public const string PluginName = "BuildMenuQOL";
    public const string PluginVersion = "0.3.0";

    internal static ManualLogSource Log = null!;
    internal static ConfigEntry<KeyboardShortcut> RepairHotkey = null!;
    internal static ConfigEntry<KeyboardShortcut> FilterHotkey = null!;
    internal static ConfigEntry<KeyboardShortcut> PreviousTabHotkey = null!;
    internal static ConfigEntry<KeyboardShortcut> NextTabHotkey = null!;
    internal static ConfigEntry<bool> ShowTabSwitchHotkeyBadges = null!;
    internal static ConfigEntry<bool> ShowRepairHotkeyBadge = null!;
    internal static ConfigEntry<bool> ShowFilterHotkeyBadge = null!;
    internal static ConfigEntry<bool> VerboseLogging = null!;
    internal static ConfigEntry<bool> EquipToggle = null!;
    internal static ConfigEntry<bool> ReequipPreviousGear = null!;

    private const BindingFlags AnyInstance =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    // Reflection handles. Everything is resolved once, here, never per frame.
    // (Declaration order matters: later initializers depend on earlier ones.)

    private static readonly Type? BuildUiType =
        AccessTools.TypeByName("BuildUi");

    private static readonly FieldInfo? PieceListsField =
        BuildUiType == null ? null : AccessTools.Field(BuildUiType, "m_pieceLists");

    private static readonly FieldInfo? RecentPieceListField =
        BuildUiType == null ? null : AccessTools.Field(BuildUiType, "m_recentPieceList");

    private static readonly FieldInfo? CurrentPieceListField =
        BuildUiType == null ? null : AccessTools.Field(BuildUiType, "m_currentPieceList");

    private static readonly FieldInfo? CurrentBuildToolField =
        BuildUiType == null ? null : AccessTools.Field(BuildUiType, "m_currentBuildTool");

    private static readonly FieldInfo? SpecialPieceButtonField =
        BuildUiType == null ? null : AccessTools.Field(BuildUiType, "m_specialPieceButton");

    private static readonly FieldInfo? SpecialButtonsContainerField =
        BuildUiType == null ? null : AccessTools.Field(BuildUiType, "m_specialButtonsContainer");

    private static readonly FieldInfo? PieceButtonPrefabField =
        BuildUiType == null ? null : AccessTools.Field(BuildUiType, "m_pieceButtonPrefab");

    private static readonly FieldInfo? SearchFieldField =
        BuildUiType == null ? null : AccessTools.Field(BuildUiType, "m_searchField");

    private static readonly FieldInfo? BuildUiTabHandlerField =
        BuildUiType == null ? null : AccessTools.Field(BuildUiType, "m_tabHandler");

    // UIInputHint (assembly_guiutils) and ZInput (assembly_utils) aren't referenced by
    // this project, so they're reached by name. The hint objects are public fields.
    private static readonly Type? InputHintType =
        AccessTools.TypeByName("UIInputHint");

    private static readonly Type? ZInputType =
        AccessTools.TypeByName("ZInput");

    private static readonly Func<bool>? IsGamepadActive =
        MakeStaticBoolDelegate(ZInputType, "IsGamepadActive");

    private static readonly Func<bool>? IsMouseActive =
        MakeStaticBoolDelegate(ZInputType, "IsMouseActive");

    private static readonly Func<KeyCode, bool>? ZInputKeyDown =
        MakeZInputKeyDelegate(ZInputType, "GetKeyDown");

    private static readonly Func<KeyCode, bool>? ZInputKeyHeld =
        MakeZInputKeyDelegate(ZInputType, "GetKey");

    private static readonly MethodInfo? SetTagListActiveMethod =
        BuildUiType == null ? null :
        AccessTools.Method(BuildUiType, "SetTagListActive",
            new[] { typeof(bool), typeof(bool) });

    private static readonly MethodInfo? ConfigureButtonNavigationMethod =
        BuildUiType == null ? null :
        AccessTools.Method(BuildUiType, "ConfigureButtonNavigation");

    // ---------------------------------------------------------------------
    // Cached runtime state
    // ---------------------------------------------------------------------

    private static BuildUi? _buildUi;
    private static TabHandler? _tabHandler;
    private static float _nextBuildUiLookup;
    private static float _nextTabBadgeSearch;

    private static Component? _filterField;
    private static BadgeVisuals? _filterBadge;
    private static BadgeVisuals? _repairBadge;
    private static Transform? _repairButtonTransform;
    private static BadgeVisuals? _previousTabBadge;
    private static BadgeVisuals? _nextTabBadge;

    // Formatted shortcut strings, refreshed only when a config entry changes.
    private static string _repairText = "";
    private static string _filterText = "";
    private static string _previousTabText = "";
    private static string _nextTabText = "";

    private static readonly Vector3[] WorldCorners = new Vector3[4];

    private static readonly HashSet<string> LoggedErrors = new();
    private static int _tabBadgeAttempts;

    private const int MaxTabBadgeAttempts = 3;

    private EventHandler? _shortcutChangedHandler;
    private Harmony? _harmony;

    // A badge plus everything we need to restyle it, discovered once up front
    // so that per-frame syncing never walks the hierarchy or reflects over types.
    private sealed class BadgeVisuals
    {
        public readonly GameObject Root;
        public readonly List<KeyValuePair<Component, PropertyInfo>> Texts = new();
        public readonly List<Behaviour> Visuals = new();

        public BadgeVisuals(GameObject root)
        {
            Root = root;

            foreach (Component component in root.GetComponentsInChildren<Component>(true))
            {
                if (component == null)
                    continue;

                Type type = component.GetType();

                bool isText = TryGetTextProperty(type, out PropertyInfo textProperty);
                if (isText)
                    Texts.Add(new KeyValuePair<Component, PropertyInfo>(component, textProperty));

                bool isImage = type.FullName == "UnityEngine.UI.Image";
                if (component is Behaviour behaviour && (isText || isImage))
                    Visuals.Add(behaviour);
            }
        }
    }

    private void Awake()
    {
        Log = Logger;

        RepairHotkey = Config.Bind(
            "Hotkeys",
            "Repair Mode",
            new KeyboardShortcut(KeyCode.BackQuote),
            "Repair hotkey. Requires the hammer to be equipped unless 'EquipToggle' is enabled. Works with the build menu open or closed. #See the readme or mod description page for keycode references if editing the config file manually");

        FilterHotkey = Config.Bind(
            "Hotkeys",
            "Focus Filter",
            new KeyboardShortcut(KeyCode.F),
            "Filter hotkey. Focuses the Filter field while the hammer is equipped and the build menu is open. #See the readme or mod description page for keycode references if editing the config file manually");

        PreviousTabHotkey = Config.Bind(
            "Hotkeys",
            "Previous Tab",
            new KeyboardShortcut(KeyCode.Q),
            "Previous build menu tab hotkey. #See the readme or mod description page for keycode references if editing the config file manually");

        NextTabHotkey = Config.Bind(
            "Hotkeys",
            "Next Tab",
            new KeyboardShortcut(KeyCode.E),
            "Next build menu tab hotkey. #See the readme or mod description page for keycode references if editing the config file manually");

        ShowTabSwitchHotkeyBadges = Config.Bind(
            "Hotkeys",
            "Show Tab Switch Hotkey Badges",
            true,
            "Show or hide both the Previous Tab and Next Tab hotkey badges in the build menu. #Only applies when using keyboard/mouse, hotkey badges are always shown when using controller");

        ShowFilterHotkeyBadge = Config.Bind(
            "Hotkeys",
            "Show Filter Hotkey Badge",
            true,
            "Show the Filter hotkey icon/badge in the build menu. #Only applies when using keyboard/mouse, hotkey badges are always shown when using controller");

        ShowRepairHotkeyBadge = Config.Bind(
            "Hotkeys",
            "Show Repair Hotkey Badge",
            true,
            "Show the Repair hotkey icon/badge in the build menu. #Only applies when using keyboard/mouse, hotkey badges are always shown when using controller");

        EquipToggle = Config.Bind(
            "Hotkeys",
            "Equip Toggle",
            true,
            "If true, the Repair hotkey equips your hammer first if it isn't equipped (it must be in your inventory), then switches to Repair; pressing it again while already in Repair mode unequips the hammer.");

        ReequipPreviousGear = Config.Bind(
            "Hotkeys",
            "Re-equip Previous Gear",
            true,
            "Re-equips previous gear when unequipping the hammer with the repair hotkey. Only has an effect if Equip Toggle is enabled.");

        VerboseLogging = Config.Bind(
            "Debug",
            "Verbose Logging",
            false,
            "Write extra diagnostic information to the BepInEx log (patch results, badge discovery, tab-switch timings). Leave off unless you're troubleshooting.");

        RefreshShortcutText();
        _shortcutChangedHandler = (_, _) => RefreshShortcutText();
        RepairHotkey.SettingChanged += _shortcutChangedHandler;
        FilterHotkey.SettingChanged += _shortcutChangedHandler;
        PreviousTabHotkey.SettingChanged += _shortcutChangedHandler;
        NextTabHotkey.SettingChanged += _shortcutChangedHandler;

        try
        {
            _harmony = new Harmony(PluginGuid);
            InstallPatches();

            Log.LogInfo(
                $"{PluginName} {PluginVersion} loaded. " +
                $"Repair hotkey: {RepairHotkey.Value}; " +
                $"Filter hotkey: {FilterHotkey.Value}");
        }
        catch (Exception ex)
        {
            Log.LogError($"Could not install patches: {ex}");
            _harmony?.UnpatchSelf();
            _harmony = null;
        }
    }

    // If something else ever disables this component, Update/LateUpdate stop and every
    // hotkey goes dead without any error. With VerboseLogging on, that now shows up.
    private void OnDisable()
    {
        Dbg("Plugin component disabled; hotkeys and badge syncing are not running.");
    }

    private void OnDestroy()
    {
        Dbg("Plugin component destroyed; unpatching.");
        if (_shortcutChangedHandler != null)
        {
            RepairHotkey.SettingChanged -= _shortcutChangedHandler;
            FilterHotkey.SettingChanged -= _shortcutChangedHandler;
            PreviousTabHotkey.SettingChanged -= _shortcutChangedHandler;
            NextTabHotkey.SettingChanged -= _shortcutChangedHandler;
            _shortcutChangedHandler = null;
        }

        _harmony?.UnpatchSelf();
        _harmony = null;
    }

    internal static void Dbg(string message)
    {
        if (VerboseLogging != null && VerboseLogging.Value)
            Log.LogInfo($"[verbose] {message}");
    }

    // Logs each distinct failure once, so an error that repeats every frame
    // can't flood the log (or hide the line that matters).
    private static void LogOnce(string where, Exception ex)
    {
        if (LoggedErrors.Add(where))
            Log.LogError($"{where} failed (further errors from it suppressed): {ex}");
    }

    private static void RefreshShortcutText()
    {
        _textLanguage = CurrentLanguage();
        _repairText = FormatShortcut(RepairHotkey.Value);
        _filterText = FormatShortcut(FilterHotkey.Value);
        _previousTabText = FormatShortcut(PreviousTabHotkey.Value);
        _nextTabText = FormatShortcut(NextTabHotkey.Value);

        Dbg($"Badge text: repair '{_repairText}', filter '{_filterText}', " +
            $"previous tab '{_previousTabText}', next tab '{_nextTabText}'");
    }

    // Per-frame work. Everything expensive is gated behind a cached BuildUi
    // that is only returned while the menu is actually open.

    private void Update()
    {
        _updateFrames++;
        if (VerboseLogging.Value && Time.unscaledTime >= _nextHeartbeat)
        {
            _nextHeartbeat = Time.unscaledTime + 60f;
            Dbg($"heartbeat: frames={_updateFrames}, enabled={enabled}, buildUiCached={_buildUi != null}, " +
                $"legacyAnyKey={Input.anyKey}, gameKeyApi={(ZInputKeyDown != null)}, timeScale={Time.timeScale}");
        }

        // Each handler is isolated: one failing can't silently take the others down.
        BuildUi? ui = null;
        try
        {
            ui = GetActiveBuildUi();
        }
        catch (Exception ex)
        {
            LogOnce("Build menu lookup", ex);
        }

        if (ui != null)
        {
            try
            {
                HandleTabSwitchHotkeys(ui);
            }
            catch (Exception ex)
            {
                LogOnce("Tab hotkeys", ex);
            }
        }

        try
        {
            HandleRepairHotkey(ui);
        }
        catch (Exception ex)
        {
            LogOnce("Repair hotkey", ex);
        }
    }

    // Badges are synced in LateUpdate, after every Update this frame has run, so
    // they're corrected before rendering even when the game re-localizes or
    // rebuilds a hint during the frame (no one-frame flash of the vanilla key).
    private void LateUpdate()
    {
        try
        {
            BuildUi? ui = GetActiveBuildUi();
            if (ui != null)
                SyncBadges(ui);
        }
        catch (Exception ex)
        {
            LogOnce("Badge sync", ex);
        }
    }

    // Returns the cached BuildUi only while it is active (menu open).
    // The expensive scene-wide lookup is a rarely-needed fallback: the
    // UpdateTagButtons/UpdatePieceButtons postfixes normally populate the cache.
    private static BuildUi? GetActiveBuildUi()
    {
        BuildUi? ui = _buildUi;

        if (ui == null)
        {
            // BuildUi only exists in a game world; skip the scene-wide scan at the main menu.
            if (Player.m_localPlayer == null || Time.unscaledTime < _nextBuildUiLookup)
                return null;

            _nextBuildUiLookup = Time.unscaledTime + 2f;

            foreach (BuildUi candidate in Resources.FindObjectsOfTypeAll<BuildUi>())
            {
                if (candidate != null && candidate.gameObject.scene.IsValid())
                {
                    CacheBuildUi(candidate);
                    ui = candidate;
                    break;
                }
            }

            if (ui == null)
                return null;
        }

        return ui.gameObject.activeInHierarchy ? ui : null;
    }

    private static void CacheBuildUi(object instance)
    {
        if (instance is not BuildUi ui)
            return;

        if (ReferenceEquals(_buildUi, ui) && _tabHandler != null)
            return;

        // New (or first) BuildUi: forget anything cached from a previous instance.
        if (!ReferenceEquals(_buildUi, ui))
        {
            _previousTabBadge = null;
            _nextTabBadge = null;
            _tabBadgeAttempts = 0;
        }

        _buildUi = ui;
        _tabHandler = BuildUiTabHandlerField?.GetValue(ui) as TabHandler;
    }

    // ---- Equip toggle state -------------------------------------------------
    // After asking the game to equip the hammer we keep waiting while that equip is still in
    // the game's equip queue (other gear may be ahead of it). All times are game time.
    private const float PendingGraceSeconds = 1.5f;     // after leaving the queue unequipped
    private const float PendingHardCapSeconds = 90f;    // absolute cap

    private static ItemDrop.ItemData? _pendingHammer;   // hammer we asked the game to equip
    private static float _pendingStarted;               // game time of that request
    private static float _pendingGraceUntil;            // 0 while the equip is still queued
    private static ItemDrop.ItemData? _savedHammer;     // hammer that our hotkey equipped
    private static ItemDrop.ItemData? _savedRight;      // right hand before it equipped
    private static ItemDrop.ItemData? _savedLeft;       // left hand before it equipped

    // Player.TakeInput and Player.ToggleEquipped are protected; resolved once.
    private static readonly MethodInfo? TakeInputMethod =
        AccessTools.Method(typeof(Player), "TakeInput");

    private static readonly MethodInfo? ToggleEquippedMethod =
        AccessTools.Method(typeof(Player), "ToggleEquipped", new[] { typeof(ItemDrop.ItemData) });

    // The game's named-button API (e.g. "JoyButtonY"), used by the controller combo.
    private static readonly Func<string, bool>? ZInputButton =
        MakeZInputButtonDelegate(ZInputType, "GetButton");

    private static readonly Func<string, bool>? ZInputButtonDown =
        MakeZInputButtonDelegate(ZInputType, "GetButtonDown");

    private static readonly Action<string>? ZInputResetButton =
        MakeZInputResetDelegate(ZInputType);

    private static Func<string, bool>? MakeZInputButtonDelegate(Type? type, string methodName)
    {
        if (type == null)
            return null;

        MethodInfo? method = AccessTools.Method(type, methodName, new[] { typeof(string) });
        if (method == null || !method.IsStatic || method.ReturnType != typeof(bool))
            return null;

        return (Func<string, bool>)Delegate.CreateDelegate(typeof(Func<string, bool>), method);
    }

    private static Action<string>? MakeZInputResetDelegate(Type? type)
    {
        if (type == null)
            return null;

        MethodInfo? method = AccessTools.Method(type, "ResetButtonStatus", new[] { typeof(string) });
        if (method == null || !method.IsStatic || method.ReturnType != typeof(void))
            return null;

        return (Action<string>)Delegate.CreateDelegate(typeof(Action<string>), method);
    }

    private static void HandleRepairHotkey(BuildUi? ui)
    {
        // Finish an equip started on an earlier frame (the game may queue equips).
        if (_pendingHammer != null)
            ContinuePendingRepair(Player.m_localPlayer);

        // Forget remembered gear once the hammer is gone for any other reason.
        if (_savedHammer != null && _pendingHammer == null && !_savedHammer.m_equipped)
            ClearSavedGear("the hammer is no longer equipped");

        if (!ShortcutDown(RepairHotkey.Value))
            return;

        Dbg("Repair hotkey press detected.");
        PerformRepairAction(ui);
    }

    // The whole Repair hotkey behaviour, shared by the keyboard hotkey and the controller
    // combo. Returns true when the press was used (even if only ignored because the hammer
    // is still equipping), so the controller combo knows to swallow the Y press.
    private static bool PerformRepairAction(BuildUi? ui)
    {
        string? textBlocker = GetTextInputBlocker(ui);
        if (textBlocker != null)
        {
            Dbg($"Repair hotkey ignored: {textBlocker} is active.");
            return false;
        }

        Player player = Player.m_localPlayer;
        if (player == null)
        {
            Dbg("Repair hotkey ignored: no local player.");
            return false;
        }

        if (_pendingHammer != null)
        {
            // A second press while the hammer is still equipping would cancel the queued equip.
            Dbg("Repair hotkey ignored: still waiting for the hammer to finish equipping.");
            return true;
        }

        bool equipToggle = EquipToggle.Value;

        if (!HasEquippedCraftingHammer(player))
        {
            if (!equipToggle)
            {
                Dbg("Repair hotkey ignored: no hammer equipped (EquipToggle is off).");
                return false;
            }

            if (!CanTakeInput(player))
            {
                Dbg("Repair hotkey ignored: the game isn't taking player input (inventory, map or a menu is open).");
                return false;
            }

            return BeginEquipHammer(player);
        }

        // With the build menu open (however it was opened) the hotkey does what the
        // on-screen Repair button does: close the menu into Repair mode. It only ever
        // unequips with the menu closed, while already in Repair mode, with EquipToggle on.
        bool menuOpen = ui != null || Hud.IsPieceSelectionVisible();
        if (equipToggle && !menuOpen && IsRepairSelected(player))
        {
            if (!CanTakeInput(player))
            {
                Dbg("Repair hotkey ignored: the game isn't taking player input (inventory, map or a menu is open).");
                return false;
            }

            UnequipHammer(player);
            return true;
        }

        return SelectRepairPiece(player);
    }

    private static bool IsRepairSelected(Player player)
    {
        Piece? selected = player.GetSelectedPiece();
        return selected != null && selected.m_repairPiece;
    }

    // Same selection logic as before the Equip toggle existed.
    private static bool SelectRepairPiece(Player player)
    {
        List<Piece> pieces = player.GetBuildPieces();
        if (pieces == null)
            return false;

        Piece? repairPiece =
            pieces.FirstOrDefault(piece => piece != null && piece.m_repairPiece);

        if (repairPiece == null)
        {
            Dbg("Repair hotkey pressed, but no vanilla Repair piece was found in the current build category.");
            return false;
        }

        if (player.SetSelectedPiece(repairPiece))
        {
            Dbg("Selected the vanilla Repair piece.");
            Hud.CloseBuildUi();
            return true;
        }

        return false;
    }

    private static bool CanTakeInput(Player player)
    {
        if (TakeInputMethod == null)
            return true;

        try
        {
            return TakeInputMethod.Invoke(player, null) is not bool takeInput || takeInput;
        }
        catch
        {
            return true;
        }
    }

    private static bool IsCraftingHammer(ItemDrop.ItemData item)
    {
        return item.m_shared != null &&
               item.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Tool &&
               item.m_shared.m_skillType == Skills.SkillType.Crafting &&
               item.m_shared.m_name.IndexOf("hammer", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    // Prefers a hammer that is already equipped, otherwise the first one in the inventory.
    private static ItemDrop.ItemData? FindCraftingHammer(Player player)
    {
        var inventory = player.GetInventory();
        if (inventory == null)
            return null;

        ItemDrop.ItemData? firstFound = null;
        foreach (ItemDrop.ItemData item in inventory.GetAllItemsInGridOrder())
        {
            if (item == null || !IsCraftingHammer(item))
                continue;

            if (item.m_equipped)
                return item;

            if (firstFound == null)
                firstFound = item;
        }

        return firstFound;
    }

    // Goes through the game's own equip handling when possible, so equip durations and
    // the equip queue behave exactly as if the item had been used from the inventory.
    private static bool RequestEquip(Player player, ItemDrop.ItemData item)
    {
        try
        {
            if (ToggleEquippedMethod != null)
                return ToggleEquippedMethod.Invoke(player, new object[] { item }) is bool handled && handled;

            return player.EquipItem(item);
        }
        catch (Exception ex)
        {
            Log.LogWarning($"Could not equip '{item.m_shared.m_name}': {ex.GetBaseException().Message}");
            return false;
        }
    }

    private static bool BeginEquipHammer(Player player)
    {
        ItemDrop.ItemData? hammer = FindCraftingHammer(player);
        if (hammer == null)
        {
            Dbg("Repair hotkey ignored: no hammer in the inventory.");
            return false;
        }

        if (player.IsEquipActionQueued(hammer))
        {
            Dbg("Repair hotkey ignored: the hammer's equip is already queued.");
            return true;
        }

        // Equipping a tool empties both hands, so remember both.
        _savedHammer = hammer;
        _savedRight = player.RightItem;
        _savedLeft = player.LeftItem;

        if (!RequestEquip(player, hammer))
        {
            Dbg("Equip toggle: the game refused to equip the hammer.");
            ClearSavedGear(null);
            return false;
        }

        _pendingHammer = hammer;
        _pendingStarted = Time.time;
        _pendingGraceUntil = 0f;
        Dbg($"Equip toggle: equipping '{hammer.m_shared.m_name}' (remembered right: " +
            $"{_savedRight?.m_shared.m_name ?? "none"}, left: {_savedLeft?.m_shared.m_name ?? "none"}).");

        // Equips with no delay are already finished; queued ones finish on a later frame.
        ContinuePendingRepair(player);
        return true;
    }

    private static void ContinuePendingRepair(Player? player)
    {
        ItemDrop.ItemData? hammer = _pendingHammer;
        if (hammer == null)
            return;

        if (player == null || Time.time - _pendingStarted > PendingHardCapSeconds)
        {
            Dbg("Equip toggle: gave up waiting for the hammer to equip.");
            _pendingHammer = null;
            ClearSavedGear(null);
            return;
        }

        if (hammer.m_equipped && player.InPlaceMode())
        {
            _pendingHammer = null;
            Dbg("Equip toggle: the hammer is equipped; selecting Repair.");
            SelectRepairPiece(player);
            return;
        }

        // Still in the equip queue (possibly behind other gear): keep waiting.
        if (player.IsEquipActionQueued(hammer))
        {
            _pendingGraceUntil = 0f;
            return;
        }

        // Out of the queue but not equipped yet: give the equip a moment to land, then stop
        // waiting so the hotkey isn't ignored forever (e.g. the player was mid-attack).
        if (_pendingGraceUntil <= 0f)
            _pendingGraceUntil = Time.time + PendingGraceSeconds;

        if (Time.time > _pendingGraceUntil)
        {
            Dbg("Equip toggle: the hammer is no longer queued and isn't equipped; giving up.");
            _pendingHammer = null;
            ClearSavedGear(null);
        }
    }

    private static void UnequipHammer(Player player)
    {
        ItemDrop.ItemData? hammer = FindCraftingHammer(player);
        if (hammer == null || !hammer.m_equipped)
            return;

        ItemDrop.ItemData? right = null;
        ItemDrop.ItemData? left = null;
        if (ReequipPreviousGear.Value && ReferenceEquals(_savedHammer, hammer))
        {
            right = _savedRight;
            left = _savedLeft;
        }

        ClearSavedGear(null);

        player.UnequipItem(hammer);
        Dbg("Equip toggle: unequipped the hammer.");

        if (Hud.IsPieceSelectionVisible())
            Hud.CloseBuildUi();

        if (right != null)
            ReequipSaved(player, right, "right hand");

        if (left != null)
            ReequipSaved(player, left, "left hand");
    }

    private static void ReequipSaved(Player player, ItemDrop.ItemData item, string hand)
    {
        var inventory = player.GetInventory();
        if (inventory == null || !inventory.ContainsItem(item))
        {
            Dbg($"Equip toggle: previous {hand} item is no longer in the inventory; not re-equipping.");
            return;
        }

        if (player.IsItemEquiped(item))
            return;

        bool requested = RequestEquip(player, item);
        Dbg($"Equip toggle: re-equipping '{item.m_shared.m_name}' ({hand}): {(requested ? "requested" : "refused")}.");
    }

    private static void ClearSavedGear(string? reason)
    {
        if (reason != null && (_savedRight != null || _savedLeft != null))
            Dbg($"Equip toggle: forgetting remembered gear ({reason}).");

        _savedHammer = null;
        _savedRight = null;
        _savedLeft = null;
    }

    // ---- Controller combo: hold the game's Alt button and press Y ----------------
    private static bool ControllerRepairComboPressed()
    {
        if (ZInputButtonDown == null || ZInputButton == null || IsGamepadActive == null)
            return false;

        try
        {
            // Y first: it's false on almost every frame, so this stays essentially free.
            return ZInputButtonDown("JoyButtonY") && ZInputButton("JoyAltKeys") && IsGamepadActive();
        }
        catch
        {
            return false;
        }
    }

    // Harmony prefix on InventoryGui.Update. Vanilla opens the inventory on any Y press
    // (it ignores Alt), so the combo has to run first and consume the press.
    private static void InventoryGuiUpdatePrefix()
    {
        try
        {
            if (!ControllerRepairComboPressed())
                return;

            // With the build menu open vanilla already maps Y to Repair; with the
            // inventory open Y closes it. Leave both alone.
            if (Hud.IsPieceSelectionVisible())
                return;

            Player player = Player.m_localPlayer;
            if (player == null || !CanTakeInput(player))
                return;

            Dbg("Controller Alt + Y press detected.");

            // Nothing for the combo to do (no hammer / EquipToggle off): let the game
            // treat it as an ordinary Y press.
            if (!PerformRepairAction(null))
                return;

            // Swallow the Y press the same way the game itself consumes it in InventoryGui.
            ZInputResetButton?.Invoke("JoyButtonY");
            ZInputResetButton?.Invoke("Inventory");
            ZInputResetButton?.Invoke("JoyInventory");
        }
        catch (Exception ex)
        {
            LogOnce("Controller repair combo", ex);
        }
    }

    private static void HandleTabSwitchHotkeys(BuildUi ui)
    {
        // Cheapest checks first: bail out on frames where no tab key was pressed.
        bool previous = ShortcutDown(PreviousTabHotkey.Value);
        bool next = ShortcutDown(NextTabHotkey.Value);
        if (!previous && !next)
            return;

        Dbg($"Tab hotkey press detected (previous: {previous}, next: {next}).");

        // Don't switch tabs while typing in the filter, chat, or another popup.
        string? textBlocker = GetTextInputBlocker(ui);
        if (textBlocker != null)
        {
            Dbg($"Tab hotkey ignored: {textBlocker} is active.");
            return;
        }

        // Mirror vanilla: a disabled TabHandler (restricted build tools hide the
        // tab bar) or an active blocking element means no tab switching.
        TabHandler? handler = _tabHandler;
        if (handler == null || !handler.isActiveAndEnabled)
        {
            Dbg("Tab hotkey ignored: the build menu's tab handler is missing or disabled.");
            return;
        }

        foreach (GameObject blocker in handler.m_blockingElements)
        {
            if (blocker != null && blocker.activeSelf)
            {
                Dbg($"Tab hotkey ignored: blocking element '{blocker.name}' is visible.");
                return;
            }
        }

        List<TabHandler.Tab> tabs = handler.m_tabs;
        int count = tabs.Count;
        if (count < 2)
            return;

        // Same stepping rules as TabHandler.Update: wrap if cycling, skip button-less tabs.
        int step = previous ? -1 : 1;
        int current = handler.GetActiveTab();
        int target = current;
        for (int i = 0; i < count; i++)
        {
            int candidate = target + step;
            if (candidate < 0 || candidate >= count)
            {
                if (!handler.m_cycling)
                    return;

                candidate = (candidate + count) % count;
            }

            target = candidate;
            if (tabs[target].m_button != null)
                break;
        }

        if (target == current || tabs[target].m_button == null)
            return;

        try
        {
            long start = VerboseLogging.Value ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;

            // Same path as clicking the tab, which is what vanilla's Q/E handling does.
            tabs[target].m_button.onClick.Invoke();

            if (VerboseLogging.Value)
            {
                double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1000.0 /
                            System.Diagnostics.Stopwatch.Frequency;
                Dbg($"Tab {current} -> {target}: tab click took {ms:F2} ms");
            }
        }
        catch (Exception ex)
        {
            Log.LogWarning($"Could not switch build-menu tab: {ex.GetBaseException().Message}");
        }
    }

    // Names whatever is currently taking keyboard input (chat, console, text popups,
    // minimap pin naming, or the build menu's own filter field), or null if nothing is.
    private static string? GetTextInputBlocker(BuildUi? ui)
    {
        if (UnifiedPopup.IsVisible())
            return "a popup";

        if (Console.IsVisible())
            return "the console";

        if (Chat.instance != null && Chat.instance.HasFocus())
            return "chat";

        if (TextInput.IsVisible())
            return "a text input window";

        if (Minimap.InTextInput())
            return "map text input";

        if (ui != null && ui.SearchFieldFocused)
            return "the build menu filter field";

        return null;
    }

    private static bool HasEquippedCraftingHammer(Player player)
    {
        var inventory = player.GetInventory();
        if (inventory == null)
            return false;

        foreach (ItemDrop.ItemData item in inventory.GetAllItemsInGridOrder())
        {
            if (item != null && item.m_equipped && IsCraftingHammer(item))
                return true;
        }

        return false;
    }

    // ---------------------------------------------------------------------
    // Patches
    // ---------------------------------------------------------------------

    private void InstallPatches()
    {
        if (BuildUiType == null)
            throw new MissingMemberException("Could not find game type BuildUi.");

        MethodInfo updateTags = AccessTools.Method(
            BuildUiType, "UpdateTagButtons", new[] { typeof(bool) })
            ?? throw new MissingMethodException("BuildUi.UpdateTagButtons not found.");

        MethodInfo updatePieces = AccessTools.Method(
            BuildUiType, "UpdatePieceButtons")
            ?? throw new MissingMethodException("BuildUi.UpdatePieceButtons not found.");

        MethodInfo navigationUpdate = AccessTools.Method(
            BuildUiType, "NavigationUpdate")
            ?? throw new MissingMethodException("BuildUi.NavigationUpdate not found.");

        _harmony!.Patch(
            updateTags,
            postfix: new HarmonyMethod(
                typeof(BuildMenuQOLPlugin), nameof(UpdateTagButtonsPostfix)));

        _harmony.Patch(
            updatePieces,
            postfix: new HarmonyMethod(
                typeof(BuildMenuQOLPlugin), nameof(UpdatePieceButtonsPostfix)));

        _harmony.Patch(
            navigationUpdate,
            transpiler: new HarmonyMethod(
                typeof(BuildMenuQOLPlugin), nameof(NavigationUpdateTranspiler)));

        // Prevent vanilla TabHandler keyboard-left/right inputs from duplicating
        // BuildMenuQOL's configurable previous/next-tab shortcuts.
        MethodInfo? tabHandlerUpdate = AccessTools.Method(typeof(TabHandler), "Update");

        if (tabHandlerUpdate != null)
        {
            _harmony.Patch(
                tabHandlerUpdate,
                prefix: new HarmonyMethod(typeof(BuildMenuQOLPlugin), nameof(TabHandlerUpdatePrefix)),
                postfix: new HarmonyMethod(typeof(BuildMenuQOLPlugin), nameof(TabHandlerUpdatePostfix)));
            Dbg("Installed TabHandler keyboard-navigation interception.");
        }
        else
        {
            Log.LogWarning("Could not find TabHandler.Update; vanilla tab-key interception was not installed.");
        }

        // Controller: Alt + Y with the build menu closed. The prefix runs before InventoryGui's
        // own Update, so the Y press can be swallowed before it opens the inventory.
        MethodInfo? inventoryGuiUpdate = AccessTools.Method(typeof(InventoryGui), "Update");
        if (inventoryGuiUpdate != null &&
            ZInputButton != null && ZInputButtonDown != null && ZInputResetButton != null)
        {
            _harmony.Patch(
                inventoryGuiUpdate,
                prefix: new HarmonyMethod(typeof(BuildMenuQOLPlugin), nameof(InventoryGuiUpdatePrefix)));
            Dbg("Installed the controller repair combo (Alt + Y).");
        }
        else
        {
            Log.LogWarning("Could not hook InventoryGui.Update or the ZInput button API; the controller repair combo was not installed.");
        }
    }

    // Temporarily turns off the build menu's own TabHandler keyboard navigation
    // for the duration of its Update, so vanilla never double-handles our keys.
    // m_keybaordInput (the game's own spelling) is what gates vanilla's
    // "TabLeft"/"TabRight" key handling in TabHandler.Update.
    private static void TabHandlerUpdatePrefix(TabHandler __instance, ref bool __state)
    {
        __state = false;

        // Only the build menu's handler matters; this is a cheap reference check.
        if (!ReferenceEquals(__instance, _tabHandler))
            return;

        if (__instance.m_keybaordInput)
        {
            __instance.m_keybaordInput = false;
            __state = true;
        }
    }

    private static void TabHandlerUpdatePostfix(TabHandler __instance, bool __state)
    {
        if (__state)
            __instance.m_keybaordInput = true;
    }

    // Recent has no tags, so vanilla hides its sidebar and special-button area.
    // Re-enables the existing sidebar only when the game hasn't hidden advanced UI.
    private static void UpdateTagButtonsPostfix(object __instance)
    {
        CacheBuildUi(__instance);

        if (!IsRecentTab(__instance))
            return;

        object? buildTool = CurrentBuildToolField?.GetValue(__instance);
        if (buildTool == null || IsAdvancedMenuHidden(buildTool))
            return;

        SetTagListActiveMethod?.Invoke(__instance, new object[] { true, true });
    }

    // RecentPieceList contains ordinary history entries only.
    // Reuses the vanilla special-button prefab and gives it the vanilla Repair piece.
    private static void UpdatePieceButtonsPostfix(object __instance)
    {
        CacheBuildUi(__instance);

        // Cache the Filter field's badge on every tab (incl. Recent) so its
        // displayed shortcut is corrected as soon as the build menu refreshes.
        Component? filterComponent = SearchFieldField?.GetValue(__instance) as Component;
        if (filterComponent != null)
            EnsureFilterBadge(filterComponent);

        if (!IsRecentTab(__instance))
        {
            // Other tabs already have the vanilla repair button; just give it a badge.
            // Recent creates its own special button below.
            if (filterComponent != null &&
                SpecialPieceButtonField?.GetValue(__instance) is Component existingVanillaButton)
            {
                CreateRepairHotkeyBadge(existingVanillaButton.gameObject, filterComponent);
            }

            return;
        }

        object? buildTool = CurrentBuildToolField?.GetValue(__instance);
        if (buildTool == null || IsAdvancedMenuHidden(buildTool))
            return;

        var availablePiecesField =
            AccessTools.Field(buildTool.GetType(), "m_availablePieces");

        if (availablePiecesField?.GetValue(buildTool) is not IEnumerable<Piece> pieces)
            return;

        Piece? repairPiece =
            pieces.FirstOrDefault(piece => piece != null && piece.m_repairPiece);

        // Some build tools omit the Repair piece from m_availablePieces while the
        // Recent tab is selected. Fall back to the player's normal build-piece list.
        if (repairPiece == null && Player.m_localPlayer != null)
        {
            List<Piece>? playerPieces = Player.m_localPlayer.GetBuildPieces();
            repairPiece = playerPieces?.FirstOrDefault(
                piece => piece != null && piece.m_repairPiece);
        }

        if (repairPiece == null)
            return;

        // If vanilla or another refresh left a special button assigned, repurpose
        // that button for Repair instead of silently skipping the Recent-tab fix.
        if (SpecialPieceButtonField?.GetValue(__instance) is BuildUiPieceButton existingButton)
        {
            existingButton.Setup(repairPiece, (BuildUi)__instance);
            existingButton.gameObject.SetActive(true);
            if (SpecialButtonsContainerField?.GetValue(__instance) is Transform existingContainer)
                existingContainer.gameObject.SetActive(true);
            CreateRepairHotkeyBadge(existingButton.gameObject, filterComponent);
            ConfigureButtonNavigationMethod?.Invoke(__instance, null);
            return;
        }

        if (PieceButtonPrefabField?.GetValue(__instance) is not GameObject prefab ||
            SpecialButtonsContainerField?.GetValue(__instance) is not Transform container)
        {
            Log.LogWarning("Couldn't access the vanilla special-button prefab/container.");
            return;
        }

        GameObject buttonObject = UnityEngine.Object.Instantiate(prefab, container);
        buttonObject.transform.SetAsFirstSibling();

        BuildUiPieceButton? button = buttonObject.GetComponent<BuildUiPieceButton>();
        if (button == null)
        {
            UnityEngine.Object.Destroy(buttonObject);
            Log.LogWarning("Vanilla special-button prefab has no BuildUiPieceButton.");
            return;
        }

        button.Setup(repairPiece, (BuildUi)__instance);
        buttonObject.SetActive(true);
        container.gameObject.SetActive(true);
        SpecialPieceButtonField?.SetValue(__instance, button);

        CreateRepairHotkeyBadge(buttonObject, filterComponent);

        // The vanilla method has already configured navigation by this point.
        ConfigureButtonNavigationMethod?.Invoke(__instance, null);
    }

    // ---------------------------------------------------------------------
    // Hotkey badges
    // ---------------------------------------------------------------------

    // Runs every frame while the build menu is open. It's deliberately cheap:
    // only cached components are touched, and writes happen only when a value
    // actually differs (vanilla's input-hint code can reset badge text/state).
    private static void SyncBadges(BuildUi ui)
    {
        // The game's language can change, or only become available after we loaded.
        if (Time.unscaledTime >= _nextLanguageCheck)
        {
            _nextLanguageCheck = Time.unscaledTime + 1f;
            if (!string.Equals(CurrentLanguage(), _textLanguage, StringComparison.Ordinal))
                RefreshShortcutText();
        }

        ApplyBadge(_filterBadge, _filterText, ShowFilterHotkeyBadge.Value, toggleRoot: false);

        if (_repairBadge != null && _repairBadge.Root != null)
        {
            if (_repairButtonTransform != null)
                PositionRepairBadge(_repairButtonTransform.gameObject);

            // Our clone isn't driven by UIInputHint, so apply the same rule it uses:
            // keyboard/mouse hints only show while no gamepad is active.
            ApplyBadge(_repairBadge, _repairText,
                ShowRepairHotkeyBadge.Value && KeyboardHintsActive(), toggleRoot: true);
        }

        EnsureTabBadges(ui);

        bool showTabBadges = ShowTabSwitchHotkeyBadges.Value;
        ApplyBadge(_previousTabBadge, _previousTabText, showTabBadges, toggleRoot: false);
        ApplyBadge(_nextTabBadge, _nextTabText, showTabBadges, toggleRoot: false);
    }

    private static void ApplyBadge(BadgeVisuals? badge, string text, bool visible, bool toggleRoot)
    {
        if (badge == null || badge.Root == null)
            return;

        if (toggleRoot && badge.Root.activeSelf != visible)
            badge.Root.SetActive(visible);

        // UIInputHint owns whether the root is active (input-device dependent),
        // so for non-cloned badges only the visual components are toggled.
        foreach (Behaviour visual in badge.Visuals)
        {
            if (visual != null && visual.enabled != visible)
                visual.enabled = visible;
        }

        if (!visible)
            return;

        foreach (KeyValuePair<Component, PropertyInfo> entry in badge.Texts)
        {
            if (entry.Key == null)
                continue;

            try
            {
                if ((entry.Value.GetValue(entry.Key) as string) != text)
                    entry.Value.SetValue(entry.Key, text);
            }
            catch
            {
                // Ignore components whose text property can't be read or changed.
            }
        }
    }

    private static void EnsureFilterBadge(Component searchField)
    {
        if (ReferenceEquals(_filterField, searchField) &&
            _filterBadge != null && _filterBadge.Root != null)
            return;

        _filterField = searchField;

        Transform? mouse = FindChildByName(searchField.transform, "Mouse");
        _filterBadge = mouse != null ? new BadgeVisuals(mouse.gameObject) : null;
    }

    private static void CreateRepairHotkeyBadge(GameObject repairButton, Component? searchField)
    {
        if (searchField == null || repairButton == null)
            return;

        // The badge is a sibling of the button (not a child) so the button's own
        // hierarchy can't clip it outside its visible bounds.
        Transform? buttonParent = repairButton.transform.parent;
        if (buttonParent == null)
            return;

        // Keep a single badge, but recreate it if the active repair button changes.
        if (_repairBadge != null && _repairBadge.Root != null &&
            _repairButtonTransform == repairButton.transform &&
            _repairBadge.Root.transform.parent == buttonParent)
        {
            PositionRepairBadge(repairButton);
            return;
        }

        if (_repairBadge != null && _repairBadge.Root != null)
            UnityEngine.Object.Destroy(_repairBadge.Root);

        _repairBadge = null;
        _repairButtonTransform = null;

        Transform? sourceBadge = FindChildByName(searchField.transform, "Mouse");
        if (sourceBadge == null)
        {
            Log.LogWarning("Couldn't find the Filter field's keyboard badge to copy its style.");
            return;
        }

        GameObject badge = UnityEngine.Object.Instantiate(
            sourceBadge.gameObject, buttonParent, false);
        badge.name = "BuildMenuQOLRepairHotkeyBadge";
        badge.SetActive(true);

        foreach (Transform child in badge.GetComponentsInChildren<Transform>(true))
            child.gameObject.SetActive(true);

        // Disable only the input-hint controller on the clone, so the badge stays
        // visible regardless of input device while its rendering components keep working.
        foreach (Behaviour behaviour in badge.GetComponentsInChildren<Behaviour>(true))
        {
            if (behaviour.GetType().Name.Contains("UIInputHint"))
                behaviour.enabled = false;
        }

        if (badge.transform is RectTransform rect)
        {
            if (sourceBadge is RectTransform sourceRect)
                rect.sizeDelta = sourceRect.sizeDelta;

            rect.localScale = Vector3.one;
        }

        _repairBadge = new BadgeVisuals(badge);
        _repairButtonTransform = repairButton.transform;
        PositionRepairBadge(repairButton);
        badge.transform.SetAsLastSibling();

        ApplyBadge(_repairBadge, _repairText,
            ShowRepairHotkeyBadge.Value && KeyboardHintsActive(), toggleRoot: true);
        Dbg("Created the Repair hotkey badge.");
    }

    private static void PositionRepairBadge(GameObject repairButton)
    {
        if (_repairBadge == null || _repairBadge.Root == null ||
            _repairBadge.Root.transform is not RectTransform badgeRect ||
            repairButton.transform is not RectTransform buttonRect)
            return;

        // World-space corners let the badge sit outside the button without being
        // clipped by the button's own hierarchy. Nudged slightly up and left.
        buttonRect.GetWorldCorners(WorldCorners);
        badgeRect.position = WorldCorners[1] + new Vector3(-12f, 12f, 0f);
        badgeRect.SetAsLastSibling();
    }

    private static void EnsureTabBadges(BuildUi ui)
    {
        if (_previousTabBadge != null && _previousTabBadge.Root != null &&
            _nextTabBadge != null && _nextTabBadge.Root != null)
            return;

        // A few tries (the menu may not be fully built on the first frames), then
        // stop until the BuildUi instance changes instead of retrying forever.
        if (_tabBadgeAttempts >= MaxTabBadgeAttempts || Time.unscaledTime < _nextTabBadgeSearch)
            return;

        _nextTabBadgeSearch = Time.unscaledTime + 1f;
        _tabBadgeAttempts++;
        CacheTabHotkeyBadges(ui);

        if (_tabBadgeAttempts >= MaxTabBadgeAttempts &&
            (_previousTabBadge == null || _nextTabBadge == null))
        {
            Log.LogWarning("Could not locate the tab-switch hotkey badges; they will keep showing the vanilla keys. Enable VerboseLogging for details.");
        }
    }

    // The Q/E badges belong to the tab bar's UIInputHint (".../TabContainer/InputHelp").
    // Its m_mouseKeyboardHint object is the keyboard/mouse layout and holds both
    // badges: the left one is "previous", the right one is "next". They're located
    // through the game's own hint component rather than by object name or by the
    // text they currently display.
    private static void CacheTabHotkeyBadges(BuildUi ui)
    {
        Component? tabHint = FindTabInputHint(ui);
        if (tabHint == null)
        {
            Dbg("Tab badge discovery: no UIInputHint found for the tab bar.");
            return;
        }

        GameObject? keyboardHint = GetHintObject(tabHint, "m_mouseKeyboardHint");
        GameObject? gamepadHint = GetHintObject(tabHint, "m_gamepadHint");
        GameObject? gamepadMouseHint = GetHintObject(tabHint, "m_gamepadMouseHint");
        Transform searchRoot = keyboardHint != null ? keyboardHint.transform : tabHint.transform;

        if (VerboseLogging.Value)
        {
            string keyboardHintName = keyboardHint != null ? keyboardHint.name : "none";
            Dbg($"Tab badge discovery: hint at {GetPath(tabHint.transform)}; keyboard hint object: {keyboardHintName}");
            DumpHierarchy(searchRoot);
        }

        var texts = new List<Component>();
        foreach (Component component in searchRoot.GetComponentsInChildren<Component>(true))
        {
            if (component == null || !TryGetTextProperty(component.GetType(), out _))
                continue;

            Transform t = component.transform;
            if ((gamepadHint != null && t.IsChildOf(gamepadHint.transform)) ||
                (gamepadMouseHint != null && t.IsChildOf(gamepadMouseHint.transform)))
                continue;

            texts.Add(component);
        }

        if (texts.Count < 2)
        {
            Dbg($"Tab badge discovery found {texts.Count} text element(s) under the tab hint; expected at least 2.");
            return;
        }

        texts.Sort((a, b) => a.transform.position.x.CompareTo(b.transform.position.x));

        Transform previousText = texts[0].transform;
        Transform nextText = texts[texts.Count - 1].transform;

        Transform previousRoot = ClimbToBadgeRoot(previousText, nextText, searchRoot);
        Transform nextRoot = ClimbToBadgeRoot(nextText, previousText, searchRoot);

        if (previousRoot == nextRoot)
        {
            Dbg("Tab badge discovery could not separate the previous and next badges.");
            return;
        }

        _previousTabBadge = new BadgeVisuals(previousRoot.gameObject);
        _nextTabBadge = new BadgeVisuals(nextRoot.gameObject);

        Dbg($"Cached tab badges. Previous: {GetPath(previousRoot)}; Next: {GetPath(nextRoot)}");
    }

    private static Component? FindTabInputHint(BuildUi ui)
    {
        if (InputHintType == null)
            return null;

        foreach (Component hint in ui.GetComponentsInChildren(InputHintType, true))
        {
            if (hint != null && (hint.name == "InputHelp" || IsUnder(hint.transform, "TabContainer")))
                return hint;
        }

        return null;
    }

    private static GameObject? GetHintObject(Component hint, string fieldName)
    {
        FieldInfo? field = InputHintType == null ? null : AccessTools.Field(InputHintType, fieldName);
        return field?.GetValue(hint) as GameObject;
    }

    private static bool IsUnder(Transform node, string ancestorName)
    {
        for (Transform? current = node.parent; current != null; current = current.parent)
        {
            if (current.name == ancestorName)
                return true;
        }

        return false;
    }

    // Walks up from a badge's text to the object that wraps just that badge
    // (background + text): it stops below the hint root, or just before reaching
    // an ancestor that also contains the other badge's text.
    private static Transform ClimbToBadgeRoot(Transform text, Transform other, Transform limit)
    {
        Transform current = text;
        while (current.parent != null && current.parent != limit && !other.IsChildOf(current.parent))
            current = current.parent;

        return current;
    }

    // Mirrors UIInputHint's own rule for showing the keyboard/mouse hint objects.
    private static bool KeyboardHintsActive()
    {
        // If the game's input API can't be reached, assume keyboard/mouse.
        if (IsGamepadActive == null || IsMouseActive == null)
            return true;

        return !IsGamepadActive() && IsMouseActive();
    }

    private static int _fallbackWarnings;
    private static float _nextHeartbeat;
    private static long _updateFrames;

    // Wraps ZInput.GetKeyDown/GetKey (static, KeyCode[, bool logWarning]) as Func<KeyCode,bool>.
    private static Func<KeyCode, bool>? MakeZInputKeyDelegate(Type? type, string methodName)
    {
        if (type == null)
            return null;

        MethodInfo? one = AccessTools.Method(type, methodName, new[] { typeof(KeyCode) });
        if (one != null && one.IsStatic && one.ReturnType == typeof(bool))
            return (Func<KeyCode, bool>)Delegate.CreateDelegate(typeof(Func<KeyCode, bool>), one);

        MethodInfo? two = AccessTools.Method(type, methodName, new[] { typeof(KeyCode), typeof(bool) });
        if (two != null && two.IsStatic && two.ReturnType == typeof(bool))
        {
            var d = (Func<KeyCode, bool, bool>)Delegate.CreateDelegate(typeof(Func<KeyCode, bool, bool>), two);
            return key => d(key, false);
        }

        return null;
    }

    private static bool ModifiersHeldViaGame(KeyboardShortcut shortcut)
    {
        foreach (KeyCode modifier in shortcut.Modifiers)
        {
            if (!ZInputKeyHeld!(modifier))
                return false;
        }

        return true;
    }

    // BepInEx's KeyboardShortcut reads the legacy UnityEngine.Input. If that ever stops
    // reporting keys while the game (which reads the new Input System) still does, this
    // falls back to the game's own key API and logs a warning (first few times only).
    internal static bool ShortcutDown(KeyboardShortcut shortcut)
    {
        if (shortcut.IsDown())
            return true;

        if (ZInputKeyDown == null || ZInputKeyHeld == null || shortcut.MainKey == KeyCode.None)
            return false;

        try
        {
            if (!ZInputKeyDown(shortcut.MainKey) || !ModifiersHeldViaGame(shortcut))
                return false;
        }
        catch
        {
            return false;
        }

        if (_fallbackWarnings < 5)
        {
            _fallbackWarnings++;
            Log.LogWarning($"Legacy input missed '{shortcut}' but the game's input saw it; using the game's input (warning {_fallbackWarnings}/5).");
        }

        return true;
    }

    internal static bool ShortcutPressed(KeyboardShortcut shortcut)
    {
        if (shortcut.IsPressed())
            return true;

        if (ZInputKeyHeld == null || shortcut.MainKey == KeyCode.None)
            return false;

        try
        {
            return ZInputKeyHeld(shortcut.MainKey) && ModifiersHeldViaGame(shortcut);
        }
        catch
        {
            return false;
        }
    }

    private static Func<bool>? MakeStaticBoolDelegate(Type? type, string methodName)
    {
        if (type == null)
            return null;

        MethodInfo? method = AccessTools.Method(type, methodName, Type.EmptyTypes);
        if (method == null || !method.IsStatic || method.ReturnType != typeof(bool))
            return null;

        return (Func<bool>)Delegate.CreateDelegate(typeof(Func<bool>), method);
    }

    private static bool TryGetTextProperty(Type type, out PropertyInfo property)
    {
        property = null!;

        PropertyInfo? found;
        try
        {
            found = type.GetProperty("text", AnyInstance);
        }
        catch (AmbiguousMatchException)
        {
            return false;
        }

        if (found == null || found.PropertyType != typeof(string) ||
            found.GetIndexParameters().Length != 0 || !found.CanRead || !found.CanWrite)
            return false;

        property = found;
        return true;
    }

    private static string? ReadText(Component component)
    {
        if (!TryGetTextProperty(component.GetType(), out PropertyInfo property))
            return null;

        try
        {
            return property.GetValue(component) as string;
        }
        catch
        {
            return null;
        }
    }

    // Verbose-only: logs a subtree (names, active state, components, text) so a
    // wrong assumption about the UI layout can be diagnosed from one log.
    private static void DumpHierarchy(Transform node, int depth = 0)
    {
        if (!VerboseLogging.Value || depth > 8)
            return;

        var parts = new List<string>();
        foreach (Component component in node.GetComponents<Component>())
        {
            if (component == null)
                continue;

            string label = component.GetType().Name;
            string? text = ReadText(component);
            if (text != null)
                label += $"(\"{text}\")";

            parts.Add(label);
        }

        string state = node.gameObject.activeSelf ? "" : " [inactive]";
        Dbg($"  {new string(' ', depth * 2)}{node.name}{state}: {string.Join(", ", parts)}");

        foreach (Transform child in node)
            DumpHierarchy(child, depth + 1);
    }

    private static string GetPath(Transform transform)
    {
        var names = new List<string>();
        for (Transform? current = transform; current != null; current = current.parent)
            names.Add(current.name);

        names.Reverse();
        return string.Join("/", names);
    }

    private static Transform? FindChildByName(Transform root, string name)
    {
        foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
        {
            if (child.name == name)
                return child;
        }

        return null;
    }

    // ---------------------------------------------------------------------
    // Shortcut formatting
    // ---------------------------------------------------------------------

    // ---- Key-name localization ----------------------------------------------
    // Localization lives in assembly_guiutils, which this project doesn't reference, so it is
    // reached by name like ZInput and UIInputHint. These are the game's own key-name tokens
    // (the $button_* entries in ZInput's key map). English keeps the short names below.
    private static readonly Type? LocalizationType =
        AccessTools.TypeByName("Localization");

    private static readonly PropertyInfo? LocalizationInstanceProperty =
        LocalizationType == null ? null : AccessTools.Property(LocalizationType, "instance");

    private static readonly MethodInfo? LocalizeMethod =
        LocalizationType == null ? null :
        AccessTools.Method(LocalizationType, "Localize", new[] { typeof(string) });

    private static readonly MethodInfo? GetSelectedLanguageMethod =
        LocalizationType == null ? null :
        AccessTools.Method(LocalizationType, "GetSelectedLanguage", Type.EmptyTypes);

    private static readonly Dictionary<string, string> KeyLocalizationTokens = new()
    {
        ["Mouse0"] = "$button_mouse0", ["Mouse1"] = "$button_mouse1", ["Mouse2"] = "$button_mouse2",
        ["Space"] = "$button_space", ["Return"] = "$button_return",
        ["LeftShift"] = "$button_lshift", ["RightShift"] = "$button_rshift",
        ["LeftAlt"] = "$button_lalt", ["RightAlt"] = "$button_ralt",
        ["LeftControl"] = "$button_lctrl", ["RightControl"] = "$button_rctrl",
    };

    private static string _textLanguage = "";
    private static float _nextLanguageCheck;

    // The game's selected language name (e.g. "English"), or "" while it isn't available yet.
    private static string CurrentLanguage()
    {
        try
        {
            object? localization = LocalizationInstanceProperty?.GetValue(null);
            if (localization == null || GetSelectedLanguageMethod == null)
                return "";

            return GetSelectedLanguageMethod.Invoke(localization, null) as string ?? "";
        }
        catch
        {
            return "";
        }
    }

    // Translated key name, or null to keep the English short name (English selected, language
    // not available yet, no token for this key, or the game has no translation for it).
    private static string? LocalizedKeyName(KeyCode key)
    {
        if (!KeyLocalizationTokens.TryGetValue(key.ToString(), out string? token))
            return null;

        return LocalizeToken(token);
    }

    private static string? LocalizeToken(string token)
    {
        string language = CurrentLanguage();
        if (language.Length == 0 || language.Equals("English", StringComparison.OrdinalIgnoreCase))
            return null;

        try
        {
            object? localization = LocalizationInstanceProperty?.GetValue(null);
            if (localization == null || LocalizeMethod == null)
                return null;

            string? translated = LocalizeMethod.Invoke(localization, new object[] { token }) as string;

            // A missing translation comes back as "[token]" or with the "$token" left in.
            if (string.IsNullOrEmpty(translated) ||
                translated!.StartsWith("[", StringComparison.Ordinal) ||
                translated.IndexOf('$') >= 0)
                return null;

            return translated;
        }
        catch
        {
            return null;
        }
    }

    private static string FormatShortcut(KeyboardShortcut shortcut)
    {
        var parts = new List<string>();

        foreach (KeyCode modifier in shortcut.Modifiers.OrderBy(key => key.ToString()))
        {
            string name = LocalizedKeyName(modifier) ?? (modifier switch
            {
                KeyCode.LeftControl or KeyCode.RightControl => "Ctrl",
                KeyCode.LeftShift or KeyCode.RightShift => "Shift",
                KeyCode.LeftAlt or KeyCode.RightAlt => "Alt",
                _ => modifier.ToString()
            });

            if (!parts.Contains(name))
                parts.Add(name);
        }

        parts.Add(FormatKeyName(shortcut.MainKey));
        return string.Join(" + ", parts);
    }

    // Punctuation keys are shown as the symbol on the keycap. Keyed by KeyCode name
    // (not the enum member) so keys missing from some Unity versions can't break the build.
    private static readonly Dictionary<string, string> KeySymbols = new()
    {
        ["Exclaim"] = "!", ["DoubleQuote"] = "\"", ["Hash"] = "#", ["Dollar"] = "$",
        ["Percent"] = "%", ["Ampersand"] = "&", ["Quote"] = "'", ["LeftParen"] = "(",
        ["RightParen"] = ")", ["Asterisk"] = "*", ["Plus"] = "+", ["Comma"] = ",",
        ["Minus"] = "-", ["Period"] = ".", ["Slash"] = "/", ["Colon"] = ":",
        ["Semicolon"] = ";", ["Less"] = "<", ["Equals"] = "=", ["Greater"] = ">",
        ["Question"] = "?", ["At"] = "@", ["LeftBracket"] = "[", ["Backslash"] = "\\",
        ["RightBracket"] = "]", ["Caret"] = "^", ["Underscore"] = "_", ["BackQuote"] = "`",
        ["LeftCurlyBracket"] = "{", ["Pipe"] = "|", ["RightCurlyBracket"] = "}", ["Tilde"] = "~",
    };

    // Longer key names, shortened so they fit the badge.
    private static readonly Dictionary<string, string> KeyShortNames = new()
    {
        ["Return"] = "Enter", ["Escape"] = "Esc", ["Delete"] = "Del", ["Insert"] = "Ins",
        ["Backspace"] = "Bksp", ["PageUp"] = "PgUp", ["PageDown"] = "PgDn", ["CapsLock"] = "Caps",
        ["LeftArrow"] = "←", ["RightArrow"] = "→", ["UpArrow"] = "↑", ["DownArrow"] = "↓",
        ["LeftShift"] = "LShift", ["RightShift"] = "RShift",
        ["LeftControl"] = "LCtrl", ["RightControl"] = "RCtrl",
        ["LeftAlt"] = "LAlt", ["RightAlt"] = "RAlt",
    };

    private static string FormatKeyName(KeyCode key)
    {
        string? localized = LocalizedKeyName(key);
        if (localized != null)
            return localized;

        string name = key.ToString();

        if (KeySymbols.TryGetValue(name, out string? symbol))
            return symbol;

        if (KeyShortNames.TryGetValue(name, out string? shortName))
            return shortName;

        if (name.StartsWith("Alpha", StringComparison.Ordinal))
            return name.Substring(5);

        if (name.StartsWith("Keypad", StringComparison.Ordinal))
        {
            string rest = name.Substring(6);
            return "Num " + (rest switch
            {
                "Period" => ".",
                "Divide" => "/",
                "Multiply" => "*",
                "Minus" => "-",
                "Plus" => "+",
                "Equals" => "=",
                _ => rest
            });
        }

        if (name.StartsWith("Mouse", StringComparison.Ordinal) &&
            int.TryParse(name.Substring(5), out int button))
        {
            return button switch
            {
                0 => "LMB",
                1 => "RMB",
                2 => "MMB",
                _ => "Mouse " + (button + 1)
            };
        }

        return name;
    }

    // ---------------------------------------------------------------------
    // Build-menu helpers
    // ---------------------------------------------------------------------

    private static bool IsRecentTab(object buildUi)
    {
        if (PieceListsField?.GetValue(buildUi) is not IList pieceLists ||
            CurrentPieceListField?.GetValue(buildUi) is not int currentIndex ||
            RecentPieceListField?.GetValue(buildUi) is not object recentList)
        {
            return false;
        }

        return currentIndex >= 0 &&
               currentIndex < pieceLists.Count &&
               ReferenceEquals(pieceLists[currentIndex], recentList);
    }

    private static bool IsAdvancedMenuHidden(object buildTool)
    {
        FieldInfo? field = AccessTools.Field(buildTool.GetType(), "m_hideAdvancedMenu");
        return field?.GetValue(buildTool) is bool hidden && hidden;
    }

    // Replace only the hard-coded F test in NavigationUpdate. The helper checks
    // the configured shortcut, including any modifiers.
    private static IEnumerable<CodeInstruction> NavigationUpdateTranspiler(
        IEnumerable<CodeInstruction> instructions)
    {
        Type? zInputType = AccessTools.TypeByName("ZInput");
        MethodInfo? getKey = zInputType == null
            ? null
            : AccessTools.Method(zInputType, "GetKey",
                new[] { typeof(KeyCode), typeof(bool) });

        MethodInfo helper = AccessTools.Method(
            typeof(BuildMenuQOLPlugin), nameof(IsConfiguredFilterShortcutPressed))!;

        if (getKey == null)
        {
            Log.LogWarning("Could not find ZInput.GetKey(KeyCode, bool); Filter hotkey remains vanilla.");
            return instructions;
        }

        List<CodeInstruction> code = instructions.ToList();

        int filterReplacements = 0;

        for (int i = 2; i < code.Count; i++)
        {
            // The compiled optional argument logWarning=true is pushed
            // between the KeyCode constant and ZInput.GetKey call.
            if (code[i - 1].opcode == OpCodes.Ldc_I4_1 &&
                code[i].operand is MethodInfo calledMethod &&
                calledMethod == getKey)
            {
                if (IsKeyCodeConstant(code[i - 2], KeyCode.F))
                {
                    code[i].operand = helper;
                    filterReplacements++;
                }
            }
        }

        Dbg($"Filter shortcut transpiler replaced {filterReplacements} key check(s).");

        if (filterReplacements == 0)
            Log.LogWarning("Filter shortcut transpiler found no key check to replace; Filter hotkey remains vanilla.");

        return code;
    }

    private static bool IsKeyCodeConstant(CodeInstruction instruction, KeyCode key)
    {
        int expected = (int)key;

        // Mono/Harmony can expose the short integer operand in different CLR forms.
        if (instruction.opcode == OpCodes.Ldc_I4_S)
        {
            if (instruction.operand is sbyte shortValue)
                return shortValue == expected;
            if (instruction.operand is byte byteValue)
                return byteValue == expected;
            if (instruction.operand is int shortIntValue)
                return shortIntValue == expected;
        }

        if (instruction.opcode == OpCodes.Ldc_I4 &&
            instruction.operand is int value)
            return value == expected;

        if (instruction.opcode == OpCodes.Ldc_I4_M1) return expected == -1;
        if (instruction.opcode == OpCodes.Ldc_I4_0) return expected == 0;
        if (instruction.opcode == OpCodes.Ldc_I4_1) return expected == 1;
        if (instruction.opcode == OpCodes.Ldc_I4_2) return expected == 2;
        if (instruction.opcode == OpCodes.Ldc_I4_3) return expected == 3;
        if (instruction.opcode == OpCodes.Ldc_I4_4) return expected == 4;
        if (instruction.opcode == OpCodes.Ldc_I4_5) return expected == 5;
        if (instruction.opcode == OpCodes.Ldc_I4_6) return expected == 6;
        if (instruction.opcode == OpCodes.Ldc_I4_7) return expected == 7;
        if (instruction.opcode == OpCodes.Ldc_I4_8) return expected == 8;

        return false;
    }

    private static bool IsConfiguredFilterShortcutPressed(KeyCode _, bool __)
    {
        return FilterHotkey != null && ShortcutPressed(FilterHotkey.Value);
    }
}
