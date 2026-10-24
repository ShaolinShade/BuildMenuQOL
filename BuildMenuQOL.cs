
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
    public const string PluginVersion = "0.2.0";

    internal static ManualLogSource Log = null!;
    internal static ConfigEntry<KeyboardShortcut> RepairHotkey = null!;
    internal static ConfigEntry<KeyboardShortcut> FilterHotkey = null!;
    internal static ConfigEntry<bool> ShowRepairHotkeyBadge = null!;
    internal static ConfigEntry<bool> ShowFilterHotkeyBadge = null!;

    private static Component? _filterFieldComponent;
    private static GameObject? _repairBadge;

    private Harmony? _harmony;

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

    private static readonly MethodInfo? SetTagListActiveMethod =
        BuildUiType == null ? null :
        AccessTools.Method(BuildUiType, "SetTagListActive",
            new[] { typeof(bool), typeof(bool) });

    private static readonly MethodInfo? ConfigureButtonNavigationMethod =
        BuildUiType == null ? null :
        AccessTools.Method(BuildUiType, "ConfigureButtonNavigation");

    private void Awake()
    {
        Log = Logger;

        RepairHotkey = Config.Bind(
            "Hotkeys",
            "RepairMode",
            new KeyboardShortcut(KeyCode.BackQuote),
            "Repair hotkey. Selects the vanilla Repair piece; requires the hammer to be equipped. Works with the build menu open or closed. For manual config edits, use Unity KeyCode names (e.g., 'Backspace' or 'LeftControl' + 'P'). See the 'Properties' list on this webpage for references to every usable KeyCode: https://docs.unity3d.com/ScriptReference/KeyCode.html");

        FilterHotkey = Config.Bind(
            "Hotkeys",
            "FocusFilter",
            new KeyboardShortcut(KeyCode.F),
            "Filter hotkey. Focuses the Filter field while the hammer is equipped and the build menu is open. For manual config edits, use Unity KeyCode names (see above)");

        ShowFilterHotkeyBadge = Config.Bind(
            "Hotkeys",
            "ShowFilterHotkeyBadge",
            true,
            "Show the Filter hotkey icon/badge in the build menu. Disable these if you have memorized your shortcut and prefer a cleaner UI.");

        ShowRepairHotkeyBadge = Config.Bind(
            "Hotkeys",
            "ShowRepairHotkeyBadge",
            true,
            "Show the Repair hotkey icon/badge in the build menu.");

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

    private void OnDestroy()
    {
        _harmony?.UnpatchSelf();
        _harmony = null;
    }

    private void Update()
    {
        UpdateHotkeyBadges();

        if (!RepairHotkey.Value.IsDown())
            return;

        Player player = Player.m_localPlayer;
        if (player == null)
            return;

        // Never equip a hammer here. It must already be equipped.
        if (!HasEquippedCraftingHammer(player))
            return;

        List<Piece> pieces = player.GetBuildPieces();
        if (pieces == null)
            return;

        Piece? repairPiece =
            pieces.FirstOrDefault(piece => piece != null && piece.m_repairPiece);

        if (repairPiece == null)
        {
            Log.LogDebug(
                "Repair hotkey pressed, but no vanilla Repair piece " +
                "was found in the current build category.");
            return;
        }

        if (player.SetSelectedPiece(repairPiece))
        {
            Log.LogDebug("Selected the vanilla Repair piece.");

            Hud.CloseBuildUi();
        }
    }

    private static bool HasEquippedCraftingHammer(Player player)
    {
        var inventory = player.GetInventory();
        if (inventory == null)
            return false;

        foreach (ItemDrop.ItemData item in inventory.GetAllItemsInGridOrder())
        {
            if (item == null || !item.m_equipped || item.m_shared == null)
                continue;

            if (item.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Tool &&
                item.m_shared.m_skillType == Skills.SkillType.Crafting &&
                item.m_shared.m_name.IndexOf(
                    "hammer", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
        }

        return false;
    }

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
    }

    // Recent has no tags, so vanilla hides its sidebar and special-button area.
    // Re-enables the existing sidebar only when the game hasn't hidden advanced UI:
    private static void UpdateTagButtonsPostfix(object __instance)
    {
        if (!IsRecentTab(__instance))
            return;

        object? buildTool = CurrentBuildToolField?.GetValue(__instance);
        if (buildTool == null || IsAdvancedMenuHidden(buildTool))
            return;

        SetTagListActiveMethod?.Invoke(__instance, new object[] { true, true });
    }

    // RecentPieceList contains ordinary history entries only.
    // Reuses the vanilla special-button prefab and gives it the vanilla Repair piece:
    private static void UpdatePieceButtonsPostfix(object __instance)
    {
        // Cache the Filter field on every tab (incl. Recent) so its' displayed
        // shortcut is corrected as soon as the build menu refreshes:
        object? searchField = SearchFieldField?.GetValue(__instance);
        if (searchField is Component searchComponent)
            _filterFieldComponent = searchComponent;

        if (!IsRecentTab(__instance))
        {
            // Other tabs already have the vanilla repair button. Adds repair hotkey badge to these
            // Recent creates its own special button below:
            if (searchField is Component filterComponent &&
                SpecialPieceButtonField?.GetValue(__instance) is Component existingVanillaButton)
            {
                if (_repairBadge == null ||
                    _repairBadge.transform.parent != existingVanillaButton.transform)
                {
                    CreateRepairHotkeyBadge(
                        existingVanillaButton.gameObject, filterComponent);
                }
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
            CreateRepairHotkeyBadge(existingButton.gameObject, searchField as Component);
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

        CreateRepairHotkeyBadge(buttonObject, searchField as Component);

        // The vanilla method has already configured navigation by this point.
        ConfigureButtonNavigationMethod?.Invoke(__instance, null);
    }

    private static void UpdateHotkeyBadges()
    {
        if (_filterFieldComponent != null)
        {
            GameObject? filterBadge = FindChildByName(
                _filterFieldComponent.transform, "Mouse")?.gameObject;

            if (filterBadge != null)
            {
                SetBadgeVisibility(filterBadge, ShowFilterHotkeyBadge.Value);
                if (ShowFilterHotkeyBadge.Value)
                    SetTextInChildren(filterBadge, FormatShortcut(FilterHotkey.Value));
            }
        }

        if (_repairBadge != null)
        {
            if (_repairButtonTransform != null)
                PositionRepairBadge(_repairButtonTransform.gameObject);
            SetRepairBadgeVisibility(_repairBadge, ShowRepairHotkeyBadge.Value);
            if (ShowRepairHotkeyBadge.Value)
                SetTextInChildren(_repairBadge, FormatShortcut(RepairHotkey.Value));
        }
    }

    private static Transform? _repairButtonTransform;
    private static void CreateRepairHotkeyBadge(GameObject repairButton, Component? searchField)
    {
        if (searchField == null || repairButton == null)
            return;

        // Keeps a single badge, but recreates it if the active repair button changes:
        if (_repairBadge != null &&
            _repairButtonTransform == repairButton.transform &&
            _repairBadge.transform.parent == repairButton.transform.parent)
        {
            PositionRepairBadge(repairButton);
            SetTextInChildren(_repairBadge, FormatShortcut(RepairHotkey.Value));
            SetRepairBadgeVisibility(_repairBadge, ShowRepairHotkeyBadge.Value);
            return;
        }

        if (_repairBadge != null)
            UnityEngine.Object.Destroy(_repairBadge);

        _repairBadge = null;
        _repairButtonTransform = null;

        Transform? sourceBadge = FindChildByName(searchField.transform, "Mouse");
        if (sourceBadge == null)
        {
            Log.LogWarning("Couldn't find the Filter field's keyboard badge to copy its style.");
            return;
        }

        // Made the badge a sibling of the repair button here instead of its child. This fixed
        // an issue where the hierarchy was clipping the repair button outside visible bounds:
        Transform? buttonParent = repairButton.transform.parent;
        if (buttonParent == null)
            return;

        GameObject badge = UnityEngine.Object.Instantiate(
            sourceBadge.gameObject, buttonParent, false);
        badge.name = "BuildMenuQOLRepairHotkeyBadge";
        badge.SetActive(true);

        foreach (Transform child in badge.GetComponentsInChildren<Transform>(true))
            child.gameObject.SetActive(true);

        // Disables only the input-hint controller on the clone. Fixed an issue where
        // other UI components needed to render the badge were being disabled,
        // making its appearance depend on the Filter toggle.
        foreach (Behaviour behaviour in badge.GetComponentsInChildren<Behaviour>(true))
        {
            string typeName = behaviour.GetType().Name;
            if (typeName.Contains("UIInputHint"))
                behaviour.enabled = false;
        }

        if (badge.transform is RectTransform rect)
        {
            if (sourceBadge is RectTransform sourceRect)
                rect.sizeDelta = sourceRect.sizeDelta;

            rect.localScale = Vector3.one;
        }

            SetTextInChildren(badge, FormatShortcut(RepairHotkey.Value));
        SetRepairBadgeVisibility(badge, ShowRepairHotkeyBadge.Value);
        _repairBadge = badge;
        _repairButtonTransform = repairButton.transform;
        PositionRepairBadge(repairButton);
        badge.transform.SetAsLastSibling();
        Log.LogDebug("Created the Repair hotkey badge.");
    }

    private static void PositionRepairBadge(GameObject repairButton)
    {
        if (_repairBadge == null ||
            _repairBadge.transform is not RectTransform badgeRect ||
            repairButton.transform is not RectTransform buttonRect)
            return;

        // World-space corners let the badge sit outside the button without being
        // clipped by the button's own hierarchy. Moves it slightly up and left:
        var corners = new Vector3[4];
        buttonRect.GetWorldCorners(corners);
        badgeRect.position = corners[1] + new Vector3(-12f, 12f, 0f);
        badgeRect.SetAsLastSibling();
    }

    // UIInputHint controls whether the Mouse object itself is active based on the
    // current input device. Disables only its visual components for the config toggle:
    private static void SetBadgeVisibility(GameObject badge, bool visible)
    {
        // Preserve the vanilla Filter badge's component-level visibility behavior.
        foreach (Component component in badge.GetComponentsInChildren<Component>(true))
        {
            if (component is not Behaviour behaviour)
                continue;

            Type type = component.GetType();
            PropertyInfo? textProperty = type.GetProperty(
                "text",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            bool isText = textProperty != null &&
                          textProperty.PropertyType == typeof(string);
            bool isImage = type.FullName == "UnityEngine.UI.Image";

            if (isText || isImage)
                behaviour.enabled = visible;
        }
    }

    private static void SetRepairBadgeVisibility(GameObject badge, bool visible)
    {
        // Control the cloned badge's visuals independently of the Filter badge.
        foreach (Component component in badge.GetComponentsInChildren<Component>(true))
        {
            if (component == null)
                continue;

            string typeName = component.GetType().Name;
            if (typeName == "Image" || typeName == "TextMeshProUGUI")
            {
                if (component is Behaviour behaviour)
                    behaviour.enabled = visible;
            }
        }

        // The Repair badge is independent of the Filter badge. This section
        // toggles its root as well so the badge is fully hidden when disabled.
        badge.SetActive(visible);
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

    private static void SetTextInChildren(GameObject root, string text)
    {
        foreach (Component component in root.GetComponentsInChildren<Component>(true))
        {
            if (component == null)
                continue;

            PropertyInfo? textProperty = component.GetType().GetProperty(
                "text",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            if (textProperty == null ||
                textProperty.PropertyType != typeof(string) ||
                textProperty.GetIndexParameters().Length != 0 ||
                !textProperty.CanWrite)
                continue;

            try
            {
                textProperty.SetValue(component, text);
            }
            catch
            {
                // Ignore components whose text property cannot be changed.
            }
        }
    }

    private static string FormatShortcut(KeyboardShortcut shortcut)
    {
        var parts = new List<string>();

        foreach (KeyCode modifier in shortcut.Modifiers.OrderBy(key => key.ToString()))
        {
            string name = modifier switch
            {
                KeyCode.LeftControl or KeyCode.RightControl => "Ctrl",
                KeyCode.LeftShift or KeyCode.RightShift => "Shift",
                KeyCode.LeftAlt or KeyCode.RightAlt => "Alt",
                _ => modifier.ToString()
            };

            if (!parts.Contains(name))
                parts.Add(name);
        }

        parts.Add(FormatKeyName(shortcut.MainKey));
        return string.Join(" + ", parts);
    }

    private static string FormatKeyName(KeyCode key)
    {
        return key switch
        {
            KeyCode.BackQuote => "`",
            KeyCode.Backslash => "\\",
            KeyCode.Return or KeyCode.KeypadEnter => "Enter",
            KeyCode.Escape => "Esc",
            KeyCode.Space => "Space",
            KeyCode.Tab => "Tab",
            KeyCode.Delete => "Del",
            KeyCode.Backspace => "Backspace",
            KeyCode.LeftArrow => "←",
            KeyCode.RightArrow => "→",
            KeyCode.UpArrow => "↑",
            KeyCode.DownArrow => "↓",
            _ when key.ToString().StartsWith("Alpha") => key.ToString()[5..],
            _ when key.ToString().StartsWith("Keypad") => "Num " + key.ToString()[6..],
            _ => key.ToString()
        };
    }

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
            Log.LogWarning("Could not find ZInput.GetKey(KeyCode); Filter hotkey remains vanilla.");
            return instructions;
        }

        List<CodeInstruction> code = instructions.ToList();
        int replacements = 0;

        for (int i = 2; i < code.Count; i++)
        {
            // The compiled optional argument logWarning=true is pushed
            // between KeyCode.F and the call to ZInput.GetKey
            if (IsKeyCodeFConstant(code[i - 2]) &&
                code[i - 1].opcode == OpCodes.Ldc_I4_1 &&
                code[i].operand is MethodInfo calledMethod &&
                calledMethod == getKey)
            {
                code[i].operand = helper;
                replacements++;
            }
        }

        if (replacements == 0)
            Log.LogWarning("Couldn't find the vanilla F-key test in NavigationUpdate.");

        return code;
    }

    private static bool IsKeyCodeFConstant(CodeInstruction instruction)
    {
        // KeyCode.F is 102.
        if (instruction.opcode == OpCodes.Ldc_I4_S &&
            instruction.operand is sbyte shortValue)
            return shortValue == (sbyte)KeyCode.F;

        if (instruction.opcode == OpCodes.Ldc_I4 &&
            instruction.operand is int value)
            return value == (int)KeyCode.F;

        return false;
    }

    private static bool IsConfiguredFilterShortcutPressed(KeyCode _, bool __)
    {
        return FilterHotkey != null && FilterHotkey.Value.IsPressed();
    }
}
