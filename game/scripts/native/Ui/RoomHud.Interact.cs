using System;
using System.Linq;
using Godot;
using GarageGame.Native.Kernel;
using GarageGame.Native.Ui;

namespace GarageGame.Native;

/// <summary>
/// One interaction key for the world (MECHANICS.md decisions 86 and 90): E, with a short prompt beside what it works on. The HUD decides
/// nothing about the world: it reads what the host already says (PlayerCanRest, FractalsInReach, the held thing, PlayerFocus) and E sends
/// the command the matching key always sent (PlayerRest and PlayerStand, item.pickup, item.gather, the hands' grab and release) and, with
/// the inventory (decision 113), the pockets' and the chest's own (inventory.store; item.pickup of a plant's food). In order, E:
/// <list type="number">
/// <item>sitting by a fire, stands you up ("E: stand up");</item>
/// <item>with empty hands beside a Fractal in reach, picks it up ("E: pick up the fractal");</item>
/// <item>holding something (the hands first, wherever you stand): at a camp stores it in the chest ("E: store the cup in the chest");
/// elsewhere puts it in the pockets when it fits ("E: put the cup in your pockets"; a log or stone is the wallet's away from a camp);
/// else a held loose log or stone is gathered into the wallet, and anything else is set on what you face, or down in front;</item>
/// <item>with empty hands facing food within reach, picks it into the pockets ("E: pick an apple"), or says why the pockets cannot hold
/// it (full, too heavy) beside it;</item>
/// <item>by a lit campfire, sits you down ("E: sit by the fire"); beside one it cannot (in its flames, unlit, out), says the host's words;</item>
/// <item>facing something to pick up, picks it up ("E: pick up the cup · V push").</item>
/// </list>
/// The prompt rides the focus tag when E works on the thing the hands' focus lights, and otherwise stands beside the campfire, the Fractal
/// or the body. Z still sits you down for this round (decision 90), F and V still pick up and push. The key is the input map's item_gather
/// action (Act.Interact): its name is from when E only gathered, and no default binding changed.
/// </summary>
public partial class RoomHud
{
    /// <summary>What E would do now.</summary>
    public enum InteractKind { None, Stand, Sit, Camp, Fractal, Gather, SetOn, SetDown, PickUp, Store, Pocket, PickFood, FoodFull }

    /// <summary>What E would do now, the prompt's words, and where the prompt stands (null: beside the body, or on the focus tag).</summary>
    public readonly record struct InteractTarget(InteractKind Kind, string Words, Vector3? At = null, string? Thing = null)
    {
        /// <summary>Whether the prompt rides the focus tag (E works on the thing the focus lights).</summary>
        public bool OnFocus => Kind is InteractKind.PickUp or InteractKind.SetOn;
    }

    /// <summary>A Fractal this near the feet (flat) is asked about (FractalsInReach); farther ones are not looked at.</summary>
    public const float InteractFractalNearM = 0.3f;

    private Label _interactPrompt = null!;
    private InteractTarget _interact;
    private string _focusId = "";
    private string _focusKernelTag = "";

    /// <summary>What E would do now, as the prompt says it (tests).</summary>
    public InteractTarget Interaction => _interact;
    /// <summary>The prompt beside what E works on when it is not the focused thing (the campfire, a Fractal, the body).</summary>
    public Label InteractPrompt => _interactPrompt;

    /// <summary>The H help's first line: E, and what it works on (decision 86: the help names it first).</summary>
    private string InteractKeysLine() =>
        $"{K(Act.Interact)} uses what is in front of you: sits you by a lit campfire and stands you up again, picks up a {MeterWords.One(Host, "fractals")} or a thing, picks food into your pockets, puts what you hold away (in the chest at a camp, else in your pockets), gathers a log or stone you hold into your wallet, sets down anything else you hold · {K(Act.Rest)} sits you by the fire too, this round (moving stands you up)";

    /// <summary>The words E says when there is nothing for it here.</summary>
    public string InteractNothingWords() => $"Nothing here for {K(Act.Interact)}: walk up to a lit campfire, a {MeterWords.One(Host, "fractals")}, some food or something to pick up.";

    private void BuildInteractPrompt(Theme compactTheme)
    {
        _interactPrompt = new Label { Name = "InteractPrompt", Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore, Theme = compactTheme };
        _interactPrompt.AddThemeFontSizeOverride("font_size", 16);
        AddChild(_interactPrompt);
    }

    /// <summary>
    /// What E would do now, from what the host says: the sit's end first, then a Fractal in reach (empty hands), the thing held (the hands
    /// first: put away in the chest or the pockets, gathered or set down), food within reach (empty hands), the camp, the thing faced.
    /// focus is the hands' focus as the host gave it (the thing F would act on).
    /// </summary>
    private InteractTarget DecideInteract(CommandHost host, CommandHost.HandsFocus focus)
    {
        var e = K(Act.Interact);
        if (_sitting) return new(InteractKind.Stand, $"{e}: stand up");
        var held = host.HeldBy(CommandHost.PlayerAvatar);
        if (held == null && FractalInReach(host) is { } fractal)
            return new(InteractKind.Fractal, $"{e}: pick up the {MeterWords.One(host, "fractals").ToLowerInvariant()}", fractal.PositionM + Vector3.Up * 0.03f, fractal.Id);
        if (held != null)
        {
            var name = KernelJson.DisplayText(host.HeldName(CommandHost.PlayerAvatar), 40);
            var material = host.Room.Objects.FirstOrDefault(o => o.Id == held) is { } item && CommandHost.GatherMaterial(item) != null;
            // The inventory first (decision 113), in the host's own words: at a camp the chest; elsewhere the pockets, for a unique thing or
            // a unit of food when it fits. A log or stone is the wallet's away from a camp (it would never reach the wallet from the pockets).
            // When neither fits, the host's refusal is not shown: E sets the thing down or gathers it, as it always did.
            if (host.HasInventory && host.PlayerCanStore() is { Ok: true } store && (store.To == "chest" || !material))
                return new(store.To == "chest" ? InteractKind.Store : InteractKind.Pocket, $"{e}: {LowerFirst(store.Words)}", null, held);
            if (material)
                return new(InteractKind.Gather, $"{e}: gather {name} into your wallet", null, held);
            if (focus.Id is { Length: > 0 } support)
                return new(InteractKind.SetOn, $"{e}: set {name} on {ObjectName(host, support)}", null, support);
            return new(InteractKind.SetDown, $"{e}: set {name} down", null, held);
        }
        // Food within reach (P-Food's reader): "E: pick an apple" beside it; with the pockets unable to hold it, the pockets' own words there.
        if (host.HasInventory && host.PlayerCanPick() is { Source: { } plant } pick)
            return pick.Ok ? new(InteractKind.PickFood, $"{e}: {LowerFirst(pick.Words)}", pick.At, plant) : new(InteractKind.FoodFull, pick.Words, pick.At, plant);
        var rest = host.PlayerCanRest();
        if (rest.Ok) return new(InteractKind.Sit, $"{e}: sit by the fire", CampfireTop(host, rest.Campfire), rest.Campfire);
        // Beside a campfire that cannot be sat at now, the host's own words (in its flames, unlit, out): E says them too.
        if (rest.Words is CommandHost.RestInFlames or CommandHost.RestUnlit or CommandHost.RestOut)
            return new(InteractKind.Camp, rest.Words, CampfireTop(host, rest.Campfire ?? CampfireNear(host)), rest.Campfire);
        // The thing faced, when the hands' focus says it can be picked up (the kernel's tag says which: carry, push only, too heavy).
        if (focus.Id is { Length: > 0 } thing && focus.Tag == Sandbox.SandboxControls.FocusWords.PickUpOrPush)
            return new(InteractKind.PickUp, $"{e}: pick up {ObjectName(host, thing)} · {K(Act.Push)} push", null, thing);
        return default;
    }

    /// <summary>A reader's words (a sentence from the host) as the middle of a prompt: its first letter in lower case.</summary>
    private static string LowerFirst(string words) => words.Length == 0 ? words : char.ToLowerInvariant(words[0]) + words[1..];

    /// <summary>A room object's name as the screen shows it (a world's name is untrusted: plain display text).</summary>
    private static string ObjectName(CommandHost host, string id) =>
        host.Room.Objects.FirstOrDefault(o => o.Id == id) is { } item ? KernelJson.DisplayText(item.DisplayName ?? item.Asset.DisplayName, 40) : "it";

    /// <summary>The nearest Fractal in the hands' reach (the host's FractalsInReach), asked only when one is near the feet at all.</summary>
    private IslandFractal? FractalInReach(CommandHost host)
    {
        if (host.AllFractals.Count == 0 || Player == null || !IsInstanceValid(Player)) return null;
        var feet = Player.GlobalPosition;
        IslandFractal? near = null;
        foreach (var fractal in host.Fractals)
            if (CampLaws.Flat(fractal.PositionM, feet) <= InteractFractalNearM && (near == null || fractal.PositionM.DistanceSquaredTo(feet) < near.PositionM.DistanceSquaredTo(feet))) near = fractal;
        if (near == null) return null;
        var reach = host.FractalsInReach();
        return reach.Count == 0 ? null : host.Fractals.Where(f => reach.Contains(f.Id)).OrderBy(f => f.PositionM.DistanceSquaredTo(feet)).FirstOrDefault();
    }

    /// <summary>The camp site within the rest's reach of the feet (to stand the prompt over a firepit the host's refusal names no id for).</summary>
    private string? CampfireNear(CommandHost host)
    {
        if (Player == null || !IsInstanceValid(Player)) return null;
        var feet = Player.GlobalPosition;
        return host.CampSites.Where(c => CampLaws.Flat(c.PositionM, feet) <= CampLaws.RestRadiusM).OrderBy(c => CampLaws.Flat(c.PositionM, feet)).FirstOrDefault()?.Id;
    }

    /// <summary>The prompt's place over a campfire: its stones' middle, a little up; null when the host names none.</summary>
    private static Vector3? CampfireTop(CommandHost host, string? campfire) =>
        campfire != null && host.CampSites.FirstOrDefault(c => c.Id == campfire) is { } camp ? camp.PositionM + Vector3.Up * 0.06f : null;

    /// <summary>
    /// E: what the prompt says, done through the command the matching key always sent. Asked again now, so it does what the host would
    /// let it do this moment, and the prompt is asked again next frame.
    /// </summary>
    public InteractTarget Interact()
    {
        var host = Host;
        if (host == null) { _noticeText = "The command host is not attached; the interaction key is off."; return default; }
        var target = DecideInteract(host, host.PlayerFocus());
        switch (target.Kind)
        {
            case InteractKind.Stand:
            case InteractKind.Sit:
            case InteractKind.Camp:
                RestKey();
                break;
            case InteractKind.Fractal: _noticeText = HandsOnFractal() ?? InteractNothingWords(); break;
            case InteractKind.Gather: Gather(); break;
            case InteractKind.Store:
            case InteractKind.Pocket:
                _noticeText = PutAway(host, target);
                break;
            case InteractKind.PickFood:
            case InteractKind.FoodFull:
                _noticeText = PickFood(host);
                break;
            case InteractKind.SetOn:
            case InteractKind.SetDown:
            case InteractKind.PickUp:
                Hands();
                break;
            default: _noticeText = InteractNothingWords(); break;
        }
        _focusAge = double.MaxValue;
        return target;
    }

    /// <summary>
    /// E with a thing in hand and the inventory's room for it (decision 113): the host's inventory.store, to the chest at a camp, else to
    /// the pockets (PlayerStore asks the host's PlayerCanStore itself). The words say where it went, or are the host's refusal.
    /// </summary>
    private static string PutAway(CommandHost host, InteractTarget target)
    {
        var name = KernelJson.DisplayText(host.HeldName(CommandHost.PlayerAvatar), 40);
        var result = host.PlayerStore();
        return result["ok"]?.GetValue<bool>() == true
            ? InventoryPages.StoredWords(name, target.Kind == InteractKind.Store ? "chest" : "pockets")
            : InventoryPages.Failed(result, "That could not be put away.");
    }

    /// <summary>
    /// E facing food: the host's item.pickup of the plant, one unit into the pockets. Full or too heavy, the host refuses in the pockets'
    /// own words and nothing is picked: those are the notice.
    /// </summary>
    private static string PickFood(CommandHost host)
    {
        var pick = host.PlayerCanPick();
        var result = host.PlayerPick();
        return result["ok"]?.GetValue<bool>() == true ? InventoryPages.PickedWords(host, pick) : InventoryPages.Failed(result, CommandHost.NoFoodInReach);
    }

    /// <summary>What the record says E did (its action's detail): the prompt's kind, before E is handled.</summary>
    private string InteractDetail() => _interact.Kind switch
    {
        InteractKind.Stand => "stand up", InteractKind.Sit => "sit by the fire", InteractKind.Camp => "at a campfire", InteractKind.Fractal => "pick up a fractal",
        InteractKind.Gather => "gather", InteractKind.SetOn => "set it on", InteractKind.SetDown => "set it down", InteractKind.PickUp => "pick up",
        InteractKind.Store => "store in the chest", InteractKind.Pocket => "put in the pockets", InteractKind.PickFood => "pick food", InteractKind.FoodFull => "food, pockets full",
        _ => "nothing here",
    };

    /// <summary>Each frame: the prompt beside what E works on (unless it rides the focus tag), hidden while a panel, the wheel or a spot being chosen holds the screen.</summary>
    private void PlaceInteractPrompt()
    {
        var shown = _interact.Kind != InteractKind.None && !_interact.OnFocus && !ReadingPanelOpen && !IntroPlaying && !_wheel.Held && !ChoosingMagicSpot && !GubblePaused;
        if (shown && _interactPrompt.Text != _interact.Words) _interactPrompt.Text = _interact.Words;
        if (shown) shown = PlaceAt(_interactPrompt, _interact.At ?? Player.GlobalPosition + Vector3.Up * Player.BodyHeightM, besideBody: _interact.At == null);
        if (_interactPrompt.Visible != shown) _interactPrompt.Visible = shown;
    }

    /// <summary>
    /// A tag beside a point in the world, as the focus tag stands: right of it and above, kept on the screen and out of the minimap's
    /// corner. A point behind the view (or the body's own, seen from its eye) puts the tag in the lower middle of the screen instead.
    /// </summary>
    private bool PlaceAt(Label label, Vector3 at, bool besideBody)
    {
        var camera = GetViewport().GetCamera3D();
        if (camera == null) return false;
        var screen = GetViewport().GetVisibleRect().Size;
        var size = label.GetCombinedMinimumSize();
        Vector2 spot;
        if (camera.IsPositionBehind(at) || (besideBody && ViewMode == 0)) spot = new Vector2(screen.X * 0.5f - size.X * 0.5f, screen.Y * 0.72f);
        else spot = camera.UnprojectPosition(at) + new Vector2(Sandbox.SandboxControls.FocusWords.TagRightPx, -Sandbox.SandboxControls.FocusWords.TagUpPx - size.Y);
        spot = new Vector2(Mathf.Clamp(spot.X, 0, Mathf.Max(0, screen.X - size.X)), Mathf.Clamp(spot.Y, 0, Mathf.Max(0, screen.Y - size.Y)));
        if (spot.X + size.X > screen.X - Sandbox.SandboxControls.FocusWords.MinimapWidthPx && spot.Y < Sandbox.SandboxControls.FocusWords.MinimapHeightPx)
            spot.Y = Sandbox.SandboxControls.FocusWords.MinimapHeightPx;
        label.Position = spot;
        return true;
    }
}
