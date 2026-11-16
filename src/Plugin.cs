using System;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime.Attributes;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;

// In-game trainer for My Dystopian Robot Girlfriend.
// F10 opens the menu. Hotkeys also work with the menu closed:
//   F1 +100,000 money   F2 refill needs   F3 freeze needs on/off   F4 +10 casino tokens
// The menu uses plain GUI.* calls only: this game's Unity build strips GUILayout.BeginArea & co.
[BepInPlugin("local.mdrg.trainer", "MDRG Trainer", "1.1.0")]
public class Plugin : BasePlugin
{
    internal static ManualLogSource L;

    public override void Load()
    {
        L = Log;
        ClassInjector.RegisterTypeInIl2Cpp<TrainerUI>();
        AddComponent<TrainerUI>();
        try { new Harmony("local.mdrg.trainer").PatchAll(typeof(PointerOverPatch)); Log.LogInfo("Pointer-over-UI patch applied"); }
        catch (Exception ex) { Log.LogError("Pointer-over-UI patch failed: " + ex.Message); }
        Log.LogInfo("MDRG Trainer loaded: F10 opens the menu");
    }
}

// While the trainer is open the game's own "is the pointer over UI?" check answers yes, so the room
// (Live2D interactions) ignores clicks. The original is skipped so it cannot throw on the way.
[HarmonyPatch(typeof(UIManager), nameof(UIManager.IsPointerOverGameObject))]
static class PointerOverPatch
{
    internal static int Hits;   // how often the game asked while blocking (shows the patch is live)

    static bool Prefix(ref bool __result)
    {
        if (!TrainerUI.Blocking) return true;
        Hits++;
        __result = true;
        return false;
    }
}

public class TrainerUI : MonoBehaviour
{
    const int MoneyCap = 2000000000; // the game saturates money here
    const float RowH = 24f;
    bool _open, _freeze, _browser, _infStamina;

    // Climax lock: while on, horniness is held at 0 (so climax never triggers) and the cum amount is
    // kept full. The values from the moment it was switched on are put back when it is switched off.
    // It switches itself off if a different save is loaded, since those saved values would be stale.
    bool _climaxLock; float _origHorny, _origCum; IntPtr _clPtr;

    [HideFromIl2Cpp]
    void SetClimaxLock(bool on)
    {
        var g = Gv;
        if (on)
        {
            if (g == null) { _status = "Load a save first"; return; }
            _clPtr = g.Pointer; _origHorny = g._currentHorniness; _origCum = g._remainingCum;
            _climaxLock = true; _status = "Climax lock ON";
        }
        else
        {
            if (_climaxLock && g != null && g.Pointer == _clPtr)
            {
                g._currentHorniness = _origHorny; g._remainingCum = _origCum;
                _status = "Climax lock off: original values restored";
            }
            else _status = "Climax lock off";
            _climaxLock = false;
        }
    }
    string _status = "";
    string _lastError = "";

    public TrainerUI(IntPtr ptr) : base(ptr) { }

    static GameVariables Gv => GameVariables.Current;

    static void AddMoney(int n)
    {
        var g = Gv; if (g == null) return;
        g.money = (int)Math.Min(MoneyCap, (long)g.money + n);
    }

    static void RefillNeeds()
    {
        var g = Gv; if (g == null) return;
        g._health = 1f; g._stamina = 1f; g._satiation = 1f; g._mentalHealth = 1f; g._mood = 1f;
    }

    // ---- Item browser (creative mode) --------------------------------------
    class Entry { public GameId Id; public string Name; public string Cat; public bool Mod; public bool Stack; public Item Inst; public int Count; public bool Eq; public bool Colorable; public int Slots = 1; }

    // Colour applied to colourable items (clothing etc.) when they are added from the catalogue.
    // Slot 0 of _slotSel means "all slots"; 1..3 edit one colour slot.
    bool _useColor; int _slotSel;
    readonly Color[] _cols = { Color.white, Color.white, Color.white };
    int _slDrag;                                   // which RGB slider is being dragged (0 = none)
    static readonly Color[] Palette =
    {
        Color.white, new Color(0.75f, 0.75f, 0.78f), new Color(0.12f, 0.12f, 0.14f), new Color(0.90f, 0.12f, 0.14f),
        new Color(0.83f, 0.06f, 0.31f), new Color(1f, 0.55f, 0.10f), new Color(1f, 0.88f, 0.20f), new Color(0.55f, 0.90f, 0.25f),
        new Color(0.10f, 0.65f, 0.30f), new Color(0.10f, 0.75f, 0.70f), new Color(0.15f, 0.85f, 1f), new Color(0.20f, 0.40f, 0.95f),
        new Color(0.50f, 0.25f, 0.85f), new Color(1f, 0.50f, 0.80f)
    };

    static int SlotCount(ItemPrefab p)
    {
        var s = p.ColorSlots;
        return s == null ? 1 : Math.Max(1, s.Length);
    }

    [HideFromIl2Cpp]
    void Paint(Item it, Entry e)
    {
        if (!_useColor || !e.Colorable) return;
        it.EnsureColorsSizeAtLeast(e.Slots);
        for (int i = 0; i < e.Slots; i++) it.SetColor(i, _cols[Math.Min(i, 2)]);
    }

    [HideFromIl2Cpp]
    void SetColorChoice(Color c)
    {
        if (_slotSel == 0) { _cols[0] = c; _cols[1] = c; _cols[2] = c; }
        else _cols[_slotSel - 1] = c;
    }
    static readonly int[] Amounts = { 1, 5, 10, 50 };
    static readonly string[] Cats = { "All", "Other", "Modules", "Clothing", "Food", "FoodCookedDish", "Usable", "Package", "Meds", "Room", "AnonEquip", "Fish", "CookingIngredient", "FishingTrash", "Soap" };
    const int PerPage = 12;
    readonly System.Collections.Generic.List<Entry> _all = new System.Collections.Generic.List<Entry>();
    readonly System.Collections.Generic.List<Entry> _view = new System.Collections.Generic.List<Entry>();
    bool _loaded, _loadedInGame, _stable, _typing;
    string _search = "", _viewSearch = null;
    int _amountIdx, _catIdx, _viewCat = -1, _page;

    [HideFromIl2Cpp]
    void LoadItems()
    {
        _all.Clear();
        try
        {
            var seen = new System.Collections.Generic.HashSet<string>();
            int a = Collect(ItemPrefab.ItemPrefabManager.AllItems, seen);
            int v = Collect(ItemPrefab.ItemPrefabManager.AllVanillaItems, seen);
            int f = CollectByEnum(seen);
            Plugin.L.LogInfo("Items: AllItems " + a + ", AllVanillaItems " + v + ", by ItemEnum " + f + ", total " + _all.Count + ", IsLoaded " + ItemPrefab.ItemPrefabManager._IsLoaded);
            _all.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            _loaded = _all.Count > 0;
            _viewSearch = null; // force the filtered view to rebuild
            _status = "Loaded " + _all.Count + " items";
        }
        catch (Exception ex) { Fail("Item load", ex); }
    }

    [HideFromIl2Cpp]
    int Collect(Il2CppSystem.Collections.Generic.IEnumerable<Il2CppSystem.ValueTuple<GameId, ItemPrefab>> source, System.Collections.Generic.HashSet<string> seen)
    {
        int added = 0;
        var it = source.GetEnumerator();
        var step = it.Cast<Il2CppSystem.Collections.IEnumerator>();
        while (step.MoveNext())
        {
            var pair = it.Current;
            var prefab = pair.Item2;
            if (prefab == null || prefab.IsMissingItem) continue;
            string name = prefab.Name;
            if (string.IsNullOrEmpty(name)) continue;
            var id = pair.Item1;
            if (!seen.Add(id.Guid.ToString() + ":" + id.Id)) continue;
            _all.Add(new Entry { Id = id, Name = name, Cat = prefab.Category.ToString(), Mod = id.IsModded, Stack = prefab.IsStackable, Colorable = prefab.CanChangeColor, Slots = SlotCount(prefab) });
            added++;
        }
        return added;
    }

    // Fallback for vanilla items: look every ItemEnum value up by id.
    [HideFromIl2Cpp]
    int CollectByEnum(System.Collections.Generic.HashSet<string> seen)
    {
        int added = 0;
        foreach (GameItems.ItemEnum v in Enum.GetValues(typeof(GameItems.ItemEnum)))
        {
            var id = GameId.CreateVanilla(v);
            if (seen.Contains(id.Guid.ToString() + ":" + id.Id)) continue;
            ItemPrefab prefab;
            try { prefab = ItemPrefab.ItemPrefabManager.GetItemById(id); } catch { continue; }
            if (prefab == null || prefab.IsMissingItem) continue;
            string name = prefab.Name;
            if (string.IsNullOrEmpty(name)) name = v.ToString();
            seen.Add(id.Guid.ToString() + ":" + id.Id);
            _all.Add(new Entry { Id = id, Name = name, Cat = prefab.Category.ToString(), Mod = false, Stack = prefab.IsStackable, Colorable = prefab.CanChangeColor, Slots = SlotCount(prefab) });
            added++;
        }
        return added;
    }

    [HideFromIl2Cpp]
    void Fail(string what, Exception ex)
    {
        _status = what + " failed (see log)";
        if (ex.Message != _lastError) { _lastError = ex.Message; Plugin.L.LogError(what + ": " + ex); }
    }

    [HideFromIl2Cpp]
    void Give(Entry e)
    {
        var g = Gv; if (g == null) return;
        int n = Amounts[_amountIdx];
        try
        {
            if (e.Stack)
            {
                var item = new Item(e.Id, 1f);
                item.SetCount(n);
                Paint(item, e);
                g.itemManager.AddItem(item, Item.ItemLocationEnum.AtHome);
            }
            else
            {
                for (int i = 0; i < n; i++)
                {
                    var item = new Item(e.Id, 1f);
                    Paint(item, e);
                    g.itemManager.AddItem(item, Item.ItemLocationEnum.AtHome);
                }
            }
            _status = "Added " + n + " x " + e.Name + (_useColor && e.Colorable ? " (colored)" : "");
            var m = RawMouse; Float(m.x, m.y - 14f, "+" + n + "  " + e.Name, CGood); Burst(m.x, m.y, CGood, 16, 110f);
        }
        catch (Exception ex) { Fail("Add item", ex); }
    }

    // ---- Inventory (item deleter) ---------------------------------------------
    // The browser can show what you own instead of the catalogue; each row then has Remove / All.
    readonly System.Collections.Generic.List<Entry> _inv = new System.Collections.Generic.List<Entry>();
    bool _invMode, _viewInv, _invDirty;
    float _invT;

    [HideFromIl2Cpp]
    void LoadInventory()
    {
        _inv.Clear();
        _invT = Time.realtimeSinceStartup; _invDirty = false;
        var g = Gv; if (g == null) return;
        try
        {
            var list = g.itemManager.items;
            for (int i = 0; i < list.Count; i++)
            {
                var it = list[i];
                if (it == null) continue;
                var pf = it.GetItemPrefab();
                _inv.Add(new Entry { Inst = it, Name = it.GetName(), Cat = pf != null ? pf.Category.ToString() : "Other", Stack = it.IsStackable, Count = it.GetCount(), Eq = it.IsEquipped() });
            }
            _inv.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) { Fail("Inventory load", ex); }
    }

    // Remove one stack row: all of it, or the selected amount. Equipped items are unequipped first.
    // "Keep equipped" protects whatever the character is wearing from every removal, single or bulk.
    bool _keepEquipped = true;
    float _confirmT = -10f; int _confirmN;

    [HideFromIl2Cpp]
    void RemoveAllShown()
    {
        var g = Gv; if (g == null) return;
        int removed = 0, kept = 0;
        foreach (var e in _view.ToArray())
        {
            if (e.Inst == null) continue;
            try
            {
                if (_keepEquipped && e.Inst.IsEquipped()) { kept++; continue; }
                if (e.Inst.IsEquipped()) g.itemManager.UnequipItemWithDependencies(e.Inst);
                g.itemManager.RemoveItem(e.Inst);
                removed++;
            }
            catch (Exception ex) { Fail("Remove item", ex); }
        }
        _status = "Removed " + removed + " items" + (kept > 0 ? ", kept " + kept + " equipped" : "");
        var m = RawMouse; Float(m.x, m.y - 14f, "-" + removed + " items", CAccentHover); Burst(m.x, m.y, CAccentHover, 24, 130f);
        _confirmT = -10f;
        _invDirty = true;
    }

    [HideFromIl2Cpp]
    void Remove(Entry e, bool all)
    {
        var g = Gv; if (g == null || e.Inst == null) return;
        if (_keepEquipped && e.Inst.IsEquipped()) { _status = "Kept equipped: " + e.Name; return; }
        try
        {
            int have = e.Inst.GetCount();
            int n = all ? have : Math.Min(have, Amounts[_amountIdx]);
            if (e.Stack && n < have) e.Inst.SetCount(have - n);
            else
            {
                if (e.Inst.IsEquipped()) g.itemManager.UnequipItemWithDependencies(e.Inst);
                g.itemManager.RemoveItem(e.Inst);
                n = have;
            }
            _status = "Removed " + n + " x " + e.Name;
            var m = RawMouse; Float(m.x, m.y - 14f, "-" + n + "  " + e.Name, CAccentHover); Burst(m.x, m.y, CAccentHover, 14, 100f);
            _invDirty = true;
        }
        catch (Exception ex) { Fail("Remove item", ex); }
    }

    [HideFromIl2Cpp]
    void RebuildView(bool keepPage = false)
    {
        _view.Clear();
        string q = _search.Trim();
        foreach (var e in _invMode ? _inv : _all)
        {
            if (_catIdx != 0 && e.Cat != Cats[_catIdx]) continue;
            if (q.Length > 0 && e.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0) continue;
            _view.Add(e);
        }
        _viewSearch = _search; _viewCat = _catIdx; _viewInv = _invMode;
        if (!keepPage) { _page = 0; _listT = 0f; }
    }

    // ---- Theme: palette and shapes taken from the game's own equipment screen --
    // Layout is written in design units; everything is multiplied by S when drawn,
    // and the mouse is divided by S, so the whole UI scales without GUI.matrix.
    const float S = 1.25f;
    // Values measured from screenshots of the game's own equipment screen (RGB 0-255 in comments).
    static readonly Color CBg = new Color(0.18f, 0.105f, 0.325f, 0.92f);       // window body (44,26,80): muted dusty purple
    static readonly Color CHead = new Color(0.10f, 0.016f, 0.176f, 0.97f);     // header bands (26,4,45)
    static readonly Color CBtn = new Color(0.125f, 0.043f, 0.22f, 0.98f);      // buttons / rows (32,11,56)
    static readonly Color CBtnHover = new Color(0.21f, 0.10f, 0.36f, 1f);
    static readonly Color CGreen = new Color(0.075f, 0.19f, 0.157f, 0.98f);    // selected / equipped (19,48,40)
    static readonly Color CGreenHover = new Color(0.10f, 0.29f, 0.22f, 1f);
    static readonly Color CBrown = new Color(0.19f, 0.086f, 0.11f, 0.98f);     // action buttons (48,22,28)
    static readonly Color CBrownHover = new Color(0.34f, 0.15f, 0.17f, 1f);
    static readonly Color CAccent = new Color(0.83f, 0.06f, 0.31f, 1f);        // crimson
    static readonly Color CAccentHover = new Color(0.95f, 0.20f, 0.45f, 1f);
    static readonly Color CText = Color.white;
    static readonly Color CDim = new Color(0.78f, 0.72f, 0.95f, 1f);
    static readonly Color CGood = new Color(0.45f, 0.95f, 0.65f, 1f);
    static readonly Color CGold = new Color(1f, 0.84f, 0.30f, 1f);
    const float RH = 30f;

    GUIStyle _text, _center, _right, _bold, _big, _link, _linkSel;
    bool _styleFailed;

    [HideFromIl2Cpp]
    void EnsureStyles()
    {
        if (_text != null || _styleFailed) return;
        try
        {
            _text = new GUIStyle(GUI.skin.label) { fontSize = (int)(15 * S), alignment = TextAnchor.MiddleLeft, wordWrap = false, clipping = TextClipping.Clip, fontStyle = FontStyle.Bold };
            _center = new GUIStyle(_text) { alignment = TextAnchor.MiddleCenter };
            _right = new GUIStyle(_text) { alignment = TextAnchor.MiddleRight };
            _bold = new GUIStyle(_text) { fontSize = (int)(16 * S) };
            _big = new GUIStyle(_text) { fontSize = (int)(26 * S) };
            _link = new GUIStyle(_text) { fontSize = (int)(14 * S) };
            _linkSel = new GUIStyle(_link) { fontStyle = FontStyle.BoldAndItalic };
        }
        catch (Exception ex) { _styleFailed = true; Fail("Styles", ex); }
        // Prefer a geometric sans like the game's Montserrat; fall back to Segoe UI, else the default font.
        try
        {
            string pick = null;
            foreach (var n in Font.GetOSInstalledFontNames())
            {
                if (n == "Montserrat") { pick = n; break; }
                if (n == "Segoe UI") pick = n;
            }
            if (pick != null)
            {
                var f = Font.CreateDynamicFontFromOSFont(pick, 16);
                _text.font = f; _center.font = f; _right.font = f; _bold.font = f; _big.font = f; _link.font = f; _linkSel.font = f;
            }
        }
        catch (Exception ex) { Plugin.L.LogInfo("Custom font unavailable: " + ex.Message); }
    }

    // ---- Animation state ---------------------------------------------------------
    static float _alpha = 1f;                       // global fade multiplier for everything drawn
    static float _winA = 1f;                        // alpha of the window currently being drawn
    static float _xoff, _yoff;                      // global draw offset (window pop-in, row slide-in)
    static float Dt { get { return Mathf.Min(Time.unscaledDeltaTime, 0.05f); } }
    static float Clk { get { return Time.unscaledTime; } }
    static float Ease(float t) { t = Mathf.Clamp01(t); return 1f - (1f - t) * (1f - t) * (1f - t); }
    // Overshoots past 1 and settles back: the springy "pop".
    static float EaseBack(float t)
    {
        t = Mathf.Clamp01(t);
        const float c1 = 1.70158f, c3 = c1 + 1f;
        float u = t - 1f;
        return 1f + c3 * u * u * u + c1 * u * u;
    }
    // Mouse position in design units, in the same space the current element is drawn in.
    static Vector2 Mouse { get { return Event.current.mousePosition / S - new Vector2(_xoff, _yoff); } }
    static Vector2 RawMouse { get { return Event.current.mousePosition / S; } }

    // Layering: windows are drawn bottom to top. Everything in a window only reacts to the mouse
    // when no window above it covers that point, so clicks never fall through to the layer below.
    static readonly System.Collections.Generic.List<Rect> _occl = new System.Collections.Generic.List<Rect>();
    static bool Occluded()
    {
        var m = RawMouse;
        for (int i = 0; i < _occl.Count; i++) if (_occl[i].Contains(m)) return true;
        return false;
    }
    static bool Hit(Rect r) { return r.Contains(Mouse) && !Occluded(); }

    float _vis, _brVis;                             // 0..1 open state of each window
    bool _wasOpen, _wasBr;
    readonly System.Collections.Generic.Dictionary<int, float> _hov = new System.Collections.Generic.Dictionary<int, float>();
    readonly System.Collections.Generic.Dictionary<int, float> _flash = new System.Collections.Generic.Dictionary<int, float>();
    readonly System.Collections.Generic.Dictionary<int, float> _sweep = new System.Collections.Generic.Dictionary<int, float>();
    readonly System.Collections.Generic.Dictionary<string, float> _barV = new System.Collections.Generic.Dictionary<string, float>();
    readonly System.Collections.Generic.Dictionary<string, float> _barFlash = new System.Collections.Generic.Dictionary<string, float>();
    double _moneyDisp = -1; long _lastMoney = -1; float _moneyFlash;
    Vector2 _moneyPos;
    float _listT = 1f, _thumb;
    float _ulX = -1f, _ulW, _ulY;                         // sliding underline under the active category
    bool _sbDrag;

    // Eased 0..1 level that follows a boolean target (hover fades etc). Call on Repaint only.
    float Anim(int key, bool target, float speed = 12f)
    {
        if (_hov.Count > 800) _hov.Clear();
        float v; _hov.TryGetValue(key, out v);
        v = Mathf.MoveTowards(v, target ? 1f : 0f, Dt * speed);
        _hov[key] = v;
        return v;
    }

    // ---- Particles and floating numbers --------------------------------------------
    class Part { public float X, Y, Vx, Vy, Age, Life, Size; public Color C; }
    class Floater { public string T; public float X, Y, Age; public Color C; }
    readonly System.Collections.Generic.List<Part> _parts = new System.Collections.Generic.List<Part>();
    readonly System.Collections.Generic.List<Floater> _floats = new System.Collections.Generic.List<Floater>();
    static readonly System.Random Rng = new System.Random();
    static float Rnd(float a, float b) { return a + (float)Rng.NextDouble() * (b - a); }

    [HideFromIl2Cpp]
    void Burst(float x, float y, Color c, int n, float speed = 95f)
    {
        if (_parts.Count > 300) return;
        for (int i = 0; i < n; i++)
        {
            float ang = Rnd(0f, Mathf.PI * 2f), v = Rnd(0.35f, 1f) * speed;
            _parts.Add(new Part { X = x, Y = y, Vx = Mathf.Cos(ang) * v, Vy = Mathf.Sin(ang) * v - 40f, Life = Rnd(0.45f, 0.95f), Size = Rnd(2f, 4.6f), C = c });
        }
    }

    [HideFromIl2Cpp]
    void Float(float x, float y, string text, Color c)
    {
        if (_floats.Count > 12) _floats.RemoveAt(0);
        _floats.Add(new Floater { T = text, X = x, Y = y, C = c });
    }

    // Advances and draws all particles / floaters on top of everything (screen space).
    [HideFromIl2Cpp]
    void DrawFx()
    {
        if (Event.current.type != EventType.Repaint) return;
        float dt = Dt;
        _alpha = 1f; _xoff = 0f; _yoff = 0f;
        for (int i = _parts.Count - 1; i >= 0; i--)
        {
            var p = _parts[i];
            p.Age += dt;
            if (p.Age >= p.Life) { _parts.RemoveAt(i); continue; }
            p.X += p.Vx * dt; p.Y += p.Vy * dt; p.Vy += 240f * dt; p.Vx *= 0.985f;
            float k = 1f - p.Age / p.Life;
            var c = p.C; c.a = k * k;
            Fill(new Rect(p.X, p.Y, p.Size, p.Size), c);
        }
        for (int i = _floats.Count - 1; i >= 0; i--)
        {
            var f = _floats[i];
            f.Age += dt;
            if (f.Age >= 1.3f) { _floats.RemoveAt(i); continue; }
            float k = f.Age / 1.3f;
            var c = f.C; c.a = k < 0.6f ? 1f : 1f - (k - 0.6f) / 0.4f;
            float rise = 46f * Ease(k);
            Txt(new Rect(f.X - 120f, f.Y - rise, 240f, 28f), f.T, c, _center);
        }
    }

    // ---- Drawing primitives (design units in, pixels out) ------------------------
    static Rect P(Rect r) { return new Rect((r.x + _xoff) * S, (r.y + _yoff) * S, r.width * S, r.height * S); }

    static void Fill(Rect r, Color c)
    {
        c.a *= _alpha;
        if (c.a <= 0.003f) return;
        var old = GUI.color; GUI.color = c;
        GUI.DrawTexture(P(r), Texture2D.whiteTexture);
        GUI.color = old;
    }

    // Antialiased right triangles, generated once. _triBL has its right angle at the bottom-left
    // (the game's light "shard" on every button); _triTL at the top-left (what is left of a button's
    // bottom-right corner once it is cut diagonally). If textures cannot be made, Shape falls back
    // to stepped rectangles.
    static Texture2D _triBL, _triTL;
    static bool _triFailed;
    static readonly Color CShard = new Color(0.84f, 0.84f, 0.87f, 1f);   // the game's pale grey shard

    static Texture2D MakeTri(bool rightAngleTop)
    {
        const int N = 32;
        var t = new Texture2D(N, N, TextureFormat.RGBA32, false);
        t.filterMode = FilterMode.Bilinear;
        t.wrapMode = TextureWrapMode.Clamp;
        for (int y = 0; y < N; y++)
            for (int x = 0; x < N; x++)
            {
                float d = rightAngleTop ? (N - 1 - y) : y;          // distance from the right-angle corner
                float a = Mathf.Clamp01(N - (x + 0.5f + d + 0.5f) + 0.5f);
                t.SetPixel(x, y, new Color(1f, 1f, 1f, a));
            }
        t.Apply();
        return t;
    }

    static void EnsureTris()
    {
        if (_triBL != null || _triFailed) return;
        try { _triBL = MakeTri(false); _triTL = MakeTri(true); Plugin.L.LogInfo("Triangle textures ready (smooth shard and corner)"); }
        catch (Exception ex) { _triFailed = true; _triBL = null; _triTL = null; Plugin.L.LogInfo("Triangle textures unavailable, using stepped shapes: " + ex.Message); }
    }

    static void Tri(Rect r, Texture2D tex, Color c)
    {
        c.a *= _alpha;
        if (c.a <= 0.003f) return;
        var old = GUI.color; GUI.color = c;
        GUI.DrawTexture(P(r), tex);
        GUI.color = old;
    }

    // The game's button shape: bottom-right corner cut off diagonally, a pale shard at the bottom-left.
    static void Shape(Rect r, Color c, bool notch = true, float cut = 9f)
    {
        cut = Mathf.Min(cut, r.height / 2f);
        const float shard = 8f;
        if (_triTL != null)
        {
            Fill(new Rect(r.x, r.y, r.width, r.height - cut), c);
            Fill(new Rect(r.x, r.y + r.height - cut, r.width - cut, cut), c);
            Tri(new Rect(r.x + r.width - cut, r.y + r.height - cut, cut, cut), _triTL, c);
            if (notch) Tri(new Rect(r.x, r.y + r.height - shard, shard, shard), _triBL, CShard);
            return;
        }
        Fill(new Rect(r.x, r.y, r.width, r.height - cut), c);
        for (float k = 0f; k < cut; k += 2f)
            Fill(new Rect(r.x, r.y + r.height - cut + k, r.width - k - 1f, Mathf.Min(2f, cut - k)), c);
        if (notch)
            for (float k = 0f; k < shard; k += 1f) Fill(new Rect(r.x, r.y + r.height - 1f - k, shard - k, 1f), CShard);
    }

    void Txt(Rect r, string s, Color c, GUIStyle st = null)
    {
        if (Event.current.type != EventType.Repaint) return;
        c.a *= _alpha;
        if (c.a <= 0.003f) return;
        var old = GUI.contentColor; GUI.contentColor = c;
        st = st ?? _text;
        if (st != null) GUI.Label(P(r), s, st); else GUI.Label(P(r), s);
        GUI.contentColor = old;
    }

    // kind: 0 normal (plum), 1 selected/active (green), 2 action (brown).
    // Hover fades the colour and runs a light sweep across the button; a click presses it in,
    // flashes it, sends out a ripple ring and throws a few sparks.
    bool Btn(Rect r, string text, int kind = 0, int keyHint = 0)
    {
        var e = Event.current;
        bool hover = Hit(r);
        int key = keyHint != 0 ? keyHint : unchecked(((int)r.x * 73856093) ^ ((int)r.y * 19349663) ^ text.GetHashCode());
        if (e.type == EventType.Repaint)
        {
            float h = Anim(key, hover);
            float sel = kind == 2 ? 0f : Anim(key ^ 0x5bd1e99, kind == 1, 9f);
            float f; if (_flash.TryGetValue(key, out f) && f > 0f) { f = Mathf.MoveTowards(f, 0f, Dt * 3f); _flash[key] = f; }
            Color a = kind == 2 ? CBrown : Color.Lerp(CBtn, CGreen, sel);
            Color b = kind == 2 ? CBrownHover : Color.Lerp(CBtnHover, CGreenHover, sel);
            float inset = f * 2.5f;
            var rr = new Rect(r.x + inset, r.y + inset, r.width - 2f * inset, r.height - 2f * inset);
            Shape(rr, Color.Lerp(a, b, h));

            float sw; _sweep.TryGetValue(key, out sw);
            sw = hover ? Mathf.Min(1.3f, sw + Dt * 2.4f) : 0f;
            if (_sweep.Count > 800) _sweep.Clear();
            _sweep[key] = sw;
            if (sw > 0f && sw < 1f)
            {
                float bx = r.x - 16f + (r.width + 32f) * sw;
                float x0 = Mathf.Max(bx, r.x), x1 = Mathf.Min(bx + 16f, r.x + r.width - 6f);
                if (x1 > x0) Fill(new Rect(x0, r.y, x1 - x0, r.height - 4f), new Color(1f, 1f, 1f, 0.22f * Mathf.Sin(sw * Mathf.PI)));
            }
            if (f > 0.01f)
            {
                Shape(rr, new Color(1f, 1f, 1f, f * 0.28f), false);
                float g = (1f - f) * 10f; var rc = new Color(CAccentHover.r, CAccentHover.g, CAccentHover.b, f * 0.85f);
                Fill(new Rect(r.x - g, r.y - g, r.width + 2f * g, 2f), rc);
                Fill(new Rect(r.x - g, r.y + r.height + g - 2f, r.width + 2f * g, 2f), rc);
                Fill(new Rect(r.x - g, r.y - g, 2f, r.height + 2f * g), rc);
                Fill(new Rect(r.x + r.width + g - 2f, r.y - g, 2f, r.height + 2f * g), rc);
            }
            Txt(rr, text, CText, _center);
        }
        if (e.type == EventType.MouseDown && e.button == 0 && hover)
        {
            if (_flash.Count > 800) _flash.Clear();
            _flash[key] = 1f;
            var m = RawMouse;
            Burst(m.x, m.y, kind == 2 ? CAccentHover : new Color(0.75f, 0.55f, 1f, 1f), 9, 70f);
            try { e.Use(); } catch { }
            return true;
        }
        return false;
    }

    // Plain text link (the game's category filter): white bold, active one crimson italic.
    bool Link(Rect r, string text, bool selected, int key)
    {
        var e = Event.current;
        bool hover = Hit(r);
        if (e.type == EventType.Repaint)
        {
            float h = Anim(key, hover);
            Txt(r, text, selected ? CAccentHover : Color.Lerp(CText, CDim, h), selected ? _linkSel : _link);
        }
        if (e.type == EventType.MouseDown && e.button == 0 && hover) { var m = RawMouse; Burst(m.x, m.y, CAccentHover, 6, 55f); try { e.Use(); } catch { } return true; }
        return false;
    }

    // Colour swatch: grows a little on hover, white frame when it is the current colour.
    [HideFromIl2Cpp]
    bool Swatch(Rect r, Color c, bool selected, int key)
    {
        var e = Event.current;
        bool hover = Hit(r);
        if (e.type == EventType.Repaint)
        {
            float g = Anim(key, hover) * 2f;
            if (selected) Fill(new Rect(r.x - 2f - g, r.y - 2f - g, r.width + 4f + 2f * g, r.height + 4f + 2f * g), Color.white);
            Fill(new Rect(r.x - g, r.y - g, r.width + 2f * g, r.height + 2f * g), c);
        }
        if (e.type == EventType.MouseDown && e.button == 0 && hover)
        {
            var m = RawMouse; Burst(m.x, m.y, c, 7, 55f);
            try { e.Use(); } catch { }
            return true;
        }
        return false;
    }

    // Horizontal 0..1 slider: click or drag along the track. Returns true when the value changed.
    [HideFromIl2Cpp]
    bool Slider(Rect r, ref float v, Color tint, int id)
    {
        var e = Event.current;
        bool hover = Hit(r);
        bool changed = false;
        if (e.type == EventType.MouseDown && e.button == 0 && hover) { _slDrag = id; try { e.Use(); } catch { } }
        if ((e.type == EventType.MouseDown || e.type == EventType.MouseDrag) && _slDrag == id)
        {
            float nv = Mathf.Clamp01((Mouse.x - r.x) / r.width);
            if (Mathf.Abs(nv - v) > 0.0005f) { v = nv; changed = true; }
            if (e.type == EventType.MouseDrag) { try { e.Use(); } catch { } }
        }
        if (e.type == EventType.Repaint)
        {
            Fill(new Rect(r.x, r.y + r.height / 2f - 3f, r.width, 6f), CHead);
            Fill(new Rect(r.x, r.y + r.height / 2f - 3f, r.width * v, 6f), tint);
            float k = Anim(9300 + id, hover || _slDrag == id);
            float kw = 8f + 2f * k;
            Fill(new Rect(r.x + r.width * v - kw / 2f, r.y + 1f - k, kw, r.height - 2f + 2f * k), Color.white);
        }
        return changed;
    }

    static bool SameColor(Color a, Color b) { return Mathf.Abs(a.r - b.r) + Mathf.Abs(a.g - b.g) + Mathf.Abs(a.b - b.b) < 0.02f; }

    // ---- Draggable, resizable windows -------------------------------------------------
    // Position and size are in design units. FixedH windows size their height to fit their content.
    class Win { public float X, Y, W, H, MinW, MinH, T, Lift; public bool FixedH; }
    readonly Win _mainWin = new Win { X = 12, Y = 30, W = 330, H = 520, MinW = 300, MinH = 100, FixedH = true };
    readonly Win _brWin = new Win { X = 370, Y = 30, W = 560, H = 760, MinW = 480, MinH = 460 };
    Win _drag, _resize; float _dragDx, _dragDy, _rsX, _rsY, _rsW, _rsH;
    float _mainH = 520;

    // Draws the window frame: drop shadow (deeper while dragging), body, title band with an
    // entrance light-sweep, resize grip. Returns true when its close button was clicked.
    [HideFromIl2Cpp]
    bool Window(Win w, float height, string title)
    {
        var e = Event.current;
        float width = w.W;
        var head = new Rect(w.X, w.Y, width, 30);
        var close = new Rect(w.X + width - 34, w.Y + 3, 28, 24);
        var grip = new Rect(w.X + width - 20, w.Y + height - 20, 20, 20);
        if (e.type == EventType.Repaint)
        {
            w.Lift = Mathf.MoveTowards(w.Lift, (_drag == w || _resize == w) ? 1f : 0f, Dt * 8f);
            float so = 3f + 10f * w.Lift;
            Fill(new Rect(w.X + so * 0.45f, w.Y + so, width, height), new Color(0f, 0f, 0f, 0.20f + 0.10f * w.Lift));
            Fill(new Rect(w.X + so * 0.20f, w.Y + so * 0.5f, width, height), new Color(0f, 0f, 0f, 0.16f));
            Fill(new Rect(w.X, w.Y, width, height), CBg);
            Fill(head, CHead);
            Txt(new Rect(w.X + 10, w.Y, width - 50, 30), title, CText, _bold);

            // entrance: a bright band sweeps across the title, and a crimson line draws in under it
            float t = Mathf.Clamp01(w.T / 0.9f);
            if (t < 1f)
            {
                float bx = w.X - 90f + (width + 180f) * Ease(t);
                float x0 = Mathf.Max(bx, w.X), x1 = Mathf.Min(bx + 80f, w.X + width);
                if (x1 > x0) Fill(new Rect(x0, w.Y, x1 - x0, 30), new Color(CAccentHover.r, CAccentHover.g, CAccentHover.b, 0.6f * (1f - t)));
            }
            float la = 1f - Mathf.Clamp01((w.T - 0.7f) / 0.6f);
            if (la > 0.01f) Fill(new Rect(w.X, w.Y + 28f, width * Ease(w.T / 0.7f), 2f), new Color(CAccent.r, CAccent.g, CAccent.b, la));

            // resize grip: a little triangle of dots, pink while hovered or dragged
            float gh = Anim(9200 + (int)w.MinW, Hit(grip) || _resize == w);
            var gc = Color.Lerp(new Color(1f, 1f, 1f, 0.35f), CAccentHover, gh);
            for (int r = 0; r < 3; r++)
                for (int c = 0; c < 3 - r; c++)
                    Fill(new Rect(w.X + width - 6f - c * 5f, w.Y + height - 6f - r * 5f, 3f, 3f), gc);
        }
        bool closed = Btn(close, "x");
        if (e.type == EventType.MouseDown && e.button == 0)
        {
            var m = Mouse;
            if (Hit(grip))
            {
                _resize = w; _rsX = m.x; _rsY = m.y; _rsW = w.W; _rsH = height;
                try { e.Use(); } catch { }
            }
            else if (Hit(head) && !close.Contains(m))
            {
                _drag = w; _dragDx = m.x - w.X; _dragDy = m.y - w.Y;
                try { e.Use(); } catch { }
            }
        }
        return closed;
    }

    // Moves / resizes whichever window is being dragged, clamped to the screen.
    void HandleDrag()
    {
        var e = Event.current;
        if (e.type == EventType.MouseUp) { _drag = null; _resize = null; return; }
        if (e.type != EventType.MouseDrag) return;
        var m = Mouse;
        if (_resize != null)
        {
            var w = _resize;
            w.W = Mathf.Clamp(_rsW + (m.x - _rsX), w.MinW, Mathf.Max(w.MinW, Screen.width / S - w.X));
            if (!w.FixedH) w.H = Mathf.Clamp(_rsH + (m.y - _rsY), w.MinH, Mathf.Max(w.MinH, Screen.height / S - w.Y));
            try { e.Use(); } catch { }
        }
        else if (_drag != null)
        {
            _drag.X = Mathf.Clamp(m.x - _dragDx, 0f, Screen.width / S - 120f);
            _drag.Y = Mathf.Clamp(m.y - _dragDy, 0f, Screen.height / S - 40f);
            try { e.Use(); } catch { }
        }
    }

    // Staggered entrance: block #i of a window slides in from the left with a springy overshoot.
    [HideFromIl2Cpp]
    void Stag(Win w, int i)
    {
        float k = EaseBack((w.T - i * 0.05f) / 0.5f);
        _xoff = (1f - k) * -36f;
        _alpha = _winA * Mathf.Clamp01(k * 1.5f);
    }

    // Dark header band with a section title, like "ITEM DESCRIPTION" in the game.
    void Section(float x, ref float y, float w, string title, string right = null)
    {
        Fill(new Rect(x, y, w, 24), CHead);
        Txt(new Rect(x + 8, y, w - 16, 24), title, CText, _link);
        if (right != null) Txt(new Rect(x + w - 150, y, 142, 24), right, CDim, _right);
        y += 30;
    }

    // Progress bar: a shimmer glides along the fill, a white flash runs when it rises,
    // and it pulses red when low.
    void Bar(Rect r, float v, float flash)
    {
        if (Event.current.type != EventType.Repaint) return;
        v = Mathf.Clamp01(v);
        Fill(r, CHead);
        float fw = r.width * v;
        var fc = Color.Lerp(CAccent, CGood, v);
        if (v < 0.25f) fc.a = 0.65f + 0.35f * Mathf.Sin(Clk * 6f);
        var fr = new Rect(r.x, r.y, fw, r.height);
        Fill(fr, fc);
        if (flash > 0.01f) Fill(fr, new Color(1f, 1f, 1f, flash * 0.6f));
        if (fw > 8f)
        {
            float cyc = (Clk * 0.7f) % 1.8f;
            float bx = r.x + (cyc - 0.25f) * fw * 1.2f;
            float x0 = Mathf.Max(bx, r.x), x1 = Mathf.Min(bx + 22f, r.x + fw);
            if (x1 > x0) Fill(new Rect(x0, r.y, x1 - x0, r.height), new Color(1f, 1f, 1f, 0.28f));
        }
    }

    [HideFromIl2Cpp]
    void NeedRow(float x, ref float y, float w, string label, float target)
    {
        float v, fl;
        if (!_barV.TryGetValue(label, out v)) v = target;
        _barFlash.TryGetValue(label, out fl);
        if (Event.current.type == EventType.Repaint)
        {
            if (target > v + 0.012f) fl = 1f;
            fl = Mathf.MoveTowards(fl, 0f, Dt * 2.2f);
            v += (target - v) * Mathf.Min(1f, Dt * 7f);
            if (Mathf.Abs(target - v) < 0.002f) v = target;
            _barV[label] = v; _barFlash[label] = fl;
        }
        Txt(new Rect(x, y, 70, 24), label, CDim, _link);
        Bar(new Rect(x + 76, y + 6, w - 76 - 54, 12), v, fl);
        Txt(new Rect(x + w - 50, y, 50, 24), ((int)Math.Round(v * 100)).ToString() + "%", CText, _right);
        y += 27;
    }

    [HideFromIl2Cpp]
    void StatRow(float x, ref float y, float w, string label, int value, string btn, Action add)
    {
        Txt(new Rect(x, y, 110, RH), label, CDim, _link);
        Txt(new Rect(x + 110, y, w - 110 - 92, RH), value.ToString("N0"), CText);
        if (Btn(new Rect(x + w - 88, y + 1, 88, RH - 2), btn, 2)) add();
        y += RH + 3;
    }

    // Pop a window in/out: fades, and springs up from below with overshoot when opening.
    [HideFromIl2Cpp]
    void Animated(Win w, float vis, bool opening, Action inner)
    {
        float v = Ease(vis), a = _alpha;
        float pop = opening ? EaseBack(vis) : v;
        _winA = a * v; _alpha = _winA; _yoff = (1f - pop) * 30f; _xoff = 0f;
        try { inner(); } finally { _alpha = a; _xoff = 0f; _yoff = 0f; }
    }

    [HideFromIl2Cpp]
    void DrawMain() { Animated(_mainWin, _vis, _open, DrawMainInner); }

    [HideFromIl2Cpp]
    void DrawBrowser() { Animated(_brWin, Mathf.Min(_brVis, _vis), _open && _browser, DrawBrowserInner); }

    // ---- Main window ---------------------------------------------------------
    [HideFromIl2Cpp]
    void DrawMainInner()
    {
        var g = Gv;
        float W = _mainWin.W;
        if (Window(_mainWin, g == null ? 100f : _mainH, "MDRG TRAINER")) _open = false;
        float x = _mainWin.X + 12, w = W - 24, y = _mainWin.Y + 40;
        int s = 0;
        Stag(_mainWin, s++);
        if (g == null) { Txt(new Rect(x, y, w, RH), "Load a save to use the trainer.", CDim); StagEnd(); return; }

        Section(x, ref y, w, "MONEY", g.casinoTokens.ToString("N0") + " tokens");
        _moneyPos = new Vector2(x + 120f, y + 10f);
        bool rp = Event.current.type == EventType.Repaint;
        if (_moneyDisp < 0) _moneyDisp = g.money;
        if (rp)
        {
            // earning money: floating +N, a burst of gold coins and a glow on the number
            if (_lastMoney >= 0 && g.money > _lastMoney)
            {
                long d = (long)g.money - _lastMoney;
                Float(x + 110f + Rnd(-20f, 20f), y + 6f, "+" + d.ToString("N0") + "$", CGold);
                Burst(x + 100f, y + 16f, CGold, 26, 120f);
                _moneyFlash = 1f;
            }
            _lastMoney = g.money;
            _moneyFlash = Mathf.MoveTowards(_moneyFlash, 0f, Dt * 1.4f);
            _moneyDisp += (g.money - _moneyDisp) * Mathf.Min(1f, Dt * 7f);
            if (Math.Abs(g.money - _moneyDisp) < 1.0) _moneyDisp = g.money;
            if (_moneyFlash > 0.01f) Fill(new Rect(x, y, w, 38f), new Color(CGold.r, CGold.g, CGold.b, _moneyFlash * 0.16f));
        }
        Txt(new Rect(x + 4, y, w - 8, 38), ((long)Math.Round(_moneyDisp)).ToString("N0") + "$", Color.Lerp(CText, CGold, _moneyFlash), _big);
        y += 44;
        Stag(_mainWin, s++);
        float bw = (w - 18) / 4f;
        if (Btn(new Rect(x, y, bw, RH), "+100k")) AddMoney(100000);
        if (Btn(new Rect(x + (bw + 6), y, bw, RH), "+1M")) AddMoney(1000000);
        if (Btn(new Rect(x + (bw + 6) * 2, y, bw, RH), "+10M")) AddMoney(10000000);
        if (Btn(new Rect(x + (bw + 6) * 3, y, bw, RH), "Max", 2)) g.money = MoneyCap;
        y += RH + 6;
        Stag(_mainWin, s++);
        if (Btn(new Rect(x, y, w, RH), "+10 casino tokens")) g.casinoTokens += 10;
        y += RH + 12;

        Stag(_mainWin, s++);
        Section(x, ref y, w, "NEEDS");
        Stag(_mainWin, s++); NeedRow(x, ref y, w, "Health", g._health);
        Stag(_mainWin, s++); NeedRow(x, ref y, w, "Stamina", g._stamina);
        Stag(_mainWin, s++); NeedRow(x, ref y, w, "Food", g._satiation);
        Stag(_mainWin, s++); NeedRow(x, ref y, w, "Mind", g._mentalHealth);
        Stag(_mainWin, s++); NeedRow(x, ref y, w, "Mood", g._mood);
        y += 4;
        Stag(_mainWin, s++);
        float hw = (w - 6) / 2f;
        if (Btn(new Rect(x, y, hw, RH), "Refill all")) RefillNeeds();
        if (Btn(new Rect(x + hw + 6, y, hw, RH), _freeze ? "Freeze: ON" : "Freeze: off", _freeze ? 1 : 0)) _freeze = !_freeze;
        y += RH + 6;
        if (Btn(new Rect(x, y, hw, RH), "Refill stamina")) { g._stamina = 1f; _status = "Stamina refilled"; }
        if (Btn(new Rect(x + hw + 6, y, hw, RH), _infStamina ? "Inf. stamina: ON" : "Inf. stamina: off", _infStamina ? 1 : 0)) _infStamina = !_infStamina;
        y += RH + 6;
        if (Btn(new Rect(x, y, w, RH), _climaxLock ? "Climax lock: ON" : "Climax lock: off", _climaxLock ? 1 : 0)) SetClimaxLock(!_climaxLock);
        y += RH + 12;

        Stag(_mainWin, s++);
        Section(x, ref y, w, "STATS");
        Stag(_mainWin, s++); StatRow(x, ref y, w, "Intelligence", g.inteligence, "+1", () => g.inteligence += 1);
        Stag(_mainWin, s++); StatRow(x, ref y, w, "Sympathy", g.sympathy, "+100", () => g.sympathy += 100);
        Stag(_mainWin, s++); StatRow(x, ref y, w, "Followers", g.followers, "+1,000", () => g.followers += 1000);
        Stag(_mainWin, s++); StatRow(x, ref y, w, "Subs", g.subs, "+100", () => g.subs += 100);
        y += 8;

        Stag(_mainWin, s++);
        if (Btn(new Rect(x, y, w, RH + 4), _browser ? "Close item browser" : "Open item browser", 2)) _browser = !_browser;
        y += RH + 12;
        Stag(_mainWin, s++);
        Txt(new Rect(x, y, w, 22), _status.Length > 0 ? _status : "Ready", CGood, _link); y += 24;
        Txt(new Rect(x, y, w, 20), "F1 money  F2 refill  F3 freeze", new Color(1f, 1f, 1f, 0.40f), _link); y += 20;
        Txt(new Rect(x, y, w, 20), "F4 tokens  F5 stamina  F6 climax", new Color(1f, 1f, 1f, 0.40f), _link);
        y += 28;
        StagEnd();
        if (rp) _mainH = y - _mainWin.Y;
    }

    void StagEnd() { _xoff = 0f; _alpha = _winA; }

    // ---- Item browser window -------------------------------------------------
    [HideFromIl2Cpp]
    void DrawBrowserInner()
    {
        if (!_loaded) LoadItems();
        float W = _brWin.W;
        var e = Event.current;
        string title = _invMode ? "INVENTORY  -  " + _inv.Count + " owned items" : "ITEM BROWSER  -  " + _all.Count + " items, mods included";
        if (Window(_brWin, _brWin.H, title)) _browser = false;
        float x = _brWin.X + 12, w = W - 24, y = _brWin.Y + 40;
        int s = 0;

        // search: white field like the game's, plus Reload
        Stag(_brWin, s++);
        var sbox = new Rect(x, y, w - 84, RH);
        if (e.type == EventType.Repaint)
        {
            if (_typing) Fill(new Rect(sbox.x - 2, sbox.y - 2, sbox.width + 4, sbox.height + 4), CAccentHover);
            Fill(sbox, new Color(0.97f, 0.97f, 0.99f, 1f));
            bool blink = ((int)(Time.realtimeSinceStartup * 2f)) % 2 == 0;
            string shown = _search.Length == 0 && !_typing ? "Search for item" : _search + (_typing && blink ? "|" : "");
            Txt(new Rect(sbox.x + 8, sbox.y, sbox.width - 16, sbox.height), shown, _search.Length == 0 && !_typing ? new Color(0.62f, 0.60f, 0.70f, 1f) : new Color(0.12f, 0.05f, 0.25f, 1f), _link);
        }
        if (e.type == EventType.MouseDown && e.button == 0)
        {
            bool inside = Hit(sbox);
            if (inside) { _typing = true; try { e.Use(); } catch { } }
            else if (_typing && !new Rect(x + w - 80, y, 80, RH).Contains(Mouse)) _typing = false;
        }
        if (Btn(new Rect(x + w - 80, y, 80, RH), "Reload")) { if (_invMode) _invDirty = true; else LoadItems(); }
        y += RH + 8;

        // switch between the catalogue (add items) and your inventory (remove items)
        Stag(_brWin, s++);
        Txt(new Rect(x, y, 70, 28), "View", CDim, _link);
        if (Btn(new Rect(x + 76, y, 110, 28), "Catalog", !_invMode ? 1 : 0, 7200)) _invMode = false;
        if (Btn(new Rect(x + 76 + 116, y, 130, 28), "Inventory", _invMode ? 1 : 0, 7201)) { _invMode = true; _invDirty = true; }
        if (_invMode && Btn(new Rect(x + w - 190, y, 190, 28), _keepEquipped ? "Keep equipped: ON" : "Keep equipped: off", _keepEquipped ? 1 : 0, 7202)) _keepEquipped = !_keepEquipped;
        y += 38;

        // category filters: plain text links that wrap; one crimson underline slides to the active one
        Stag(_brWin, s++);
        float cx = x, cy = y;
        Rect sel = new Rect(x, y, 10, 22);
        for (int i = 0; i < Cats.Length; i++)
        {
            float cw = 14f + Cats[i].Length * 8f;
            if (cx + cw > x + w) { cx = x; cy += 24; }
            var lr = new Rect(cx, cy, cw, 22);
            if (i == _catIdx) sel = lr;
            if (Link(lr, Cats[i], i == _catIdx, 8000 + i)) _catIdx = i;
            cx += cw + 2;
        }
        if (e.type == EventType.Repaint)
        {
            if (_ulX < 0f) { _ulX = sel.x; _ulW = sel.width; _ulY = sel.y; }
            float k = Mathf.Min(1f, Dt * 14f);
            _ulX = Mathf.Lerp(_ulX, sel.x, k); _ulW = Mathf.Lerp(_ulW, sel.width, k); _ulY = Mathf.Lerp(_ulY, sel.y, k);
            Fill(new Rect(_ulX + 2f, _ulY + 19f, _ulW - 4f, 2f), CAccentHover);
        }
        y = cy + 30;

        // amount selector
        Stag(_brWin, s++);
        Txt(new Rect(x, y, 70, 28), "Amount", CDim, _link);
        for (int i = 0; i < Amounts.Length; i++)
            if (Btn(new Rect(x + 76 + i * 58, y, 54, 28), "x" + Amounts[i], i == _amountIdx ? 1 : 0, 7100 + i)) _amountIdx = i;
        if (_invMode)
        {
            // bulk remove everything the current search / category filter shows; asks for a second click
            int removable = 0;
            foreach (var v in _view) if (!(_keepEquipped && v.Eq)) removable++;
            bool armed = Time.realtimeSinceStartup - _confirmT < 3f && _confirmN == removable;
            string label = removable == 0 ? "Nothing to remove" : armed ? "Click again: remove " + removable + "!" : "Remove all shown (" + removable + ")";
            if (Btn(new Rect(x + w - 210, y, 210, 28), label, armed ? 2 : 0, 7500) && removable > 0)
            {
                if (armed) RemoveAllShown();
                else { _confirmT = Time.realtimeSinceStartup; _confirmN = removable; }
            }
        }
        y += 38;

        // item colour (catalogue only): used for clothing and other colourable items when added
        if (!_invMode)
        {
            Stag(_brWin, s++);
            Txt(new Rect(x, y, 70, 28), "Color", CDim, _link);
            if (Btn(new Rect(x + 76, y, 120, 28), _useColor ? "Custom: ON" : "Custom: off", _useColor ? 1 : 0, 7300)) _useColor = !_useColor;
            if (_useColor)
            {
                string[] names = { "All", "1", "2", "3" };
                for (int i = 0; i < 4; i++)
                    if (Btn(new Rect(x + 204 + i * 44, y, 40, 28), names[i], i == _slotSel ? 1 : 0, 7310 + i)) _slotSel = i;
                var shown = _cols[Math.Max(0, _slotSel - 1)];
                Fill(new Rect(x + w - 36, y - 1, 34, 30), Color.white);
                Fill(new Rect(x + w - 35, y, 32, 28), shown);
            }
            y += 36;
            if (_useColor)
            {
                var cur = _cols[Math.Max(0, _slotSel - 1)];
                for (int i = 0; i < Palette.Length; i++)
                    if (Swatch(new Rect(x + 4 + i * 36f, y, 28, 24), Palette[i], SameColor(Palette[i], cur), 7400 + i)) { SetColorChoice(Palette[i]); cur = Palette[i]; }
                y += 34;
                float rr = cur.r, gg = cur.g, bb = cur.b;
                float sw = (w - 30f) / 3f;
                bool changed = false;
                Txt(new Rect(x, y, 16, 22), "R", CDim, _link);
                changed |= Slider(new Rect(x + 16, y, sw - 22, 22), ref rr, new Color(0.95f, 0.25f, 0.30f), 1);
                Txt(new Rect(x + sw + 8, y, 16, 22), "G", CDim, _link);
                changed |= Slider(new Rect(x + sw + 24, y, sw - 22, 22), ref gg, new Color(0.30f, 0.90f, 0.40f), 2);
                Txt(new Rect(x + 2 * sw + 16, y, 16, 22), "B", CDim, _link);
                changed |= Slider(new Rect(x + 2 * sw + 32, y, sw - 22, 22), ref bb, new Color(0.35f, 0.55f, 1f), 3);
                if (changed) SetColorChoice(new Color(rr, gg, bb, 1f));
                y += 32;
            }
        }
        StagEnd();

        // owned items change while you play: refresh the inventory view after a removal and every 2 s
        if (_invMode && (_invDirty || Time.realtimeSinceStartup - _invT > 2f)) { LoadInventory(); RebuildView(true); }
        if (_viewSearch != _search || _viewCat != _catIdx || _viewInv != _invMode) RebuildView();
        // as many rows as fit in the window; the footer needs 74 units below the list
        int per = Mathf.Max(3, (int)((_brWin.Y + _brWin.H - y - 74f) / RH));
        int rows = _view.Count;
        int maxTop = Math.Max(0, rows - per);
        _page = Mathf.Clamp(_page, 0, maxTop);

        var list = new Rect(x, y, w, per * RH);
        if (e.type == EventType.ScrollWheel && Hit(list))
        {
            _page = Mathf.Clamp(_page + (e.delta.y > 0 ? 3 : -3), 0, maxTop);
            try { e.Use(); } catch { }
        }

        // scrollbar: click the track to jump, drag the thumb to scroll
        var track = new Rect(x + w - 12, list.y, 12, list.height);
        float frac = rows <= per ? 1f : (float)per / rows;
        float th = Mathf.Max(34f, track.height * frac);
        Action<Vector2> seek = m =>
        {
            float f = Mathf.Clamp01((m.y - track.y - th / 2f) / Mathf.Max(1f, track.height - th));
            _page = Mathf.RoundToInt(f * maxTop);
        };
        if (maxTop > 0)
        {
            if (e.type == EventType.MouseDown && e.button == 0 && Hit(track)) { _sbDrag = true; seek(Mouse); try { e.Use(); } catch { } }
            else if (_sbDrag && e.type == EventType.MouseDrag) { seek(Mouse); try { e.Use(); } catch { } }
        }
        if (e.type == EventType.MouseUp) { _sbDrag = false; _slDrag = 0; }

        // rows spring in one after another whenever the list changes; hovered rows slide right
        // and light a crimson edge
        if (e.type == EventType.Repaint && _listT < 1f) _listT = Mathf.Min(1f, _listT + Dt * 1.1f);
        for (int i = 0; i < per; i++)
        {
            int idx = _page + i;
            var row = new Rect(x, y, w - 18, RH - 3);
            float k = EaseBack((_listT * 1.25f - i * 0.05f) / 0.5f);
            _xoff = (1f - k) * -44f;
            _alpha = _winA * Mathf.Clamp01(k * 1.5f);
            if (idx < rows)
            {
                var it = _view[idx];
                float hv = 0f;
                if (e.type == EventType.Repaint)
                {
                    hv = Anim(9000 + i, Hit(row));
                    var rr = new Rect(row.x + hv * 5f, row.y, row.width - hv * 5f, row.height);
                    Shape(rr, Color.Lerp(CBtn, CBtnHover, hv), true, 8f);
                    if (hv > 0.01f) Fill(new Rect(row.x, row.y, 3f * hv, row.height - 6f), CAccentHover);
                }
                float tx = x + 12 + hv * 5f;
                if (it.Mod) { Txt(new Rect(tx, y, 46, RH - 3), "[mod]", CAccentHover, _link); tx += 48; }
                if (_invMode)
                {
                    // inventory row: name, count, equipped tag; Remove takes the selected amount, All the whole stack
                    string label = it.Name + (it.Count > 1 ? "  x" + it.Count : "") + (it.Eq ? "  (equipped)" : "");
                    Txt(new Rect(tx, y, w - 18 - 130 - (tx - x), RH - 3), label, it.Eq ? CGood : CText, _link);
                    if (_keepEquipped && it.Eq)
                    {
                        // protected: no buttons, just a lock
                        Txt(new Rect(x + w - 18 - 90, y, 84, RH - 3), "kept", CDim, _right);
                    }
                    else
                    {
                        if (Btn(new Rect(x + w - 18 - 72, y + 2, 68, RH - 8), "Remove", 2, 6000 + i)) Remove(it, false);
                        if (it.Count > 1 && Btn(new Rect(x + w - 18 - 72 - 44, y + 2, 40, RH - 8), "All", 2, 6500 + i)) Remove(it, true);
                    }
                }
                else
                {
                    Txt(new Rect(tx, y, w - 18 - 200 - (tx - x), RH - 3), it.Name, CText, _link);
                    // colourable items carry a small swatch: your chosen colour, or an empty frame when custom colour is off
                    if (it.Colorable)
                    {
                        Fill(new Rect(x + w - 18 - 192, y + 6, 16, 16), Color.white);
                        Fill(new Rect(x + w - 18 - 191, y + 7, 14, 14), _useColor ? _cols[0] : new Color(0.15f, 0.06f, 0.30f, 1f));
                    }
                    Txt(new Rect(x + w - 18 - 170, y, 104, RH - 3), it.Cat, CDim, _right);
                    if (Btn(new Rect(x + w - 18 - 62, y + 2, 56, RH - 8), "Add", 2, 6000 + i)) Give(it);
                }
            }
            y += RH;
        }
        StagEnd();

        if (e.type == EventType.Repaint)
        {
            float target = maxTop == 0 ? 0f : (float)_page / maxTop;
            _thumb = _sbDrag ? target : Mathf.Lerp(_thumb, target, Mathf.Min(1f, Dt * 14f));
            Fill(new Rect(track.x + 2, track.y, 8, track.height), CHead);
            float ty = track.y + (track.height - th) * _thumb;
            float hv = Anim(9100, _sbDrag || Hit(track));
            var thumb = new Rect(track.x + 2, ty, 8, th);
            Fill(thumb, Color.Lerp(CAccent, CAccentHover, hv));
            // three white diamonds on the thumb, like the game's scrollbar
            for (int d = -1; d <= 1; d++) Fill(new Rect(thumb.x + 2.5f, thumb.y + th / 2f - 2f + d * 8f, 3f, 3f), Color.white);
        }
        y += 6;

        // footer
        Stag(_brWin, s + 6);
        if (Btn(new Rect(x, y, 96, RH), "< Page")) { _page = Math.Max(0, _page - per); _listT = 0.2f; }
        int from = rows == 0 ? 0 : _page + 1, to = Math.Min(rows, _page + per);
        Txt(new Rect(x + 102, y, w - 204, RH), from + "-" + to + " of " + rows + "   (wheel or drag bar)", CDim, _center);
        if (Btn(new Rect(x + w - 96, y, 96, RH), "Page >")) { _page = Math.Min(maxTop, _page + per); _listT = 0.2f; }
        y += RH + 8;
        Txt(new Rect(x, y, w, 22), _status.Length > 0 ? _status : "Ready", CGood, _link);
        StagEnd();
    }

    // ---- Game input blocking ---------------------------------------------------
    // While the trainer is open, the game's UI input modules are switched off (so its buttons get no
    // clicks or hovers) and UIManager.IsPointerOverGameObject() reports true (so the room ignores
    // clicks too, see PointerOverPatch). The EventSystem itself stays enabled: disabling it makes
    // EventSystem.current null and the game throws a NullReferenceException every frame.
    // Every input module AND every raycaster (UI canvases, physics) in the scene is switched off, and
    // the scene is rescanned twice a second so canvases that appear while the menu is open are caught.
    internal static bool Blocking;
    readonly System.Collections.Generic.List<Behaviour> _off = new System.Collections.Generic.List<Behaviour>();
    int _nMods, _nRays;

    [HideFromIl2Cpp]
    void SwitchOff(Behaviour b, ref int counter)
    {
        if (b == null || !b.enabled) return;
        b.enabled = false; _off.Add(b); counter++;
    }

    [HideFromIl2Cpp]
    void UpdateInputBlock(bool block)
    {
        try
        {
            if (block)
            {
                if (!Blocking || Time.frameCount % 30 == 0)
                {
                    foreach (var m in UnityEngine.Object.FindObjectsOfType<UnityEngine.EventSystems.BaseInputModule>()) SwitchOff(m, ref _nMods);
                    foreach (var r in UnityEngine.Object.FindObjectsOfType<UnityEngine.EventSystems.BaseRaycaster>()) SwitchOff(r, ref _nRays);
                }
            }
            else if (_off.Count > 0)
            {
                foreach (var b in _off) if (b != null) b.enabled = true;
                _off.Clear();
            }
            if (block != Blocking)
            {
                Blocking = block;
                if (block) PointerOverPatch.Hits = 0;
                Plugin.L.LogInfo("Game input " + (block ? "blocked" : "restored") + " (" + _nMods + " input modules, " + _nRays + " raycasters switched off)");
                if (!block) { _nMods = 0; _nRays = 0; }
            }
        }
        catch (Exception ex) { Fail("Input block", ex); }
    }

    // ---- Update / input ------------------------------------------------------
    void Update()
    {
        if (_freeze) RefillNeeds();
        else if (_infStamina) { var gv = Gv; if (gv != null) gv._stamina = 1f; }
        if (_climaxLock)
        {
            var cg = Gv;
            if (cg == null || cg.Pointer != _clPtr) { _climaxLock = false; _status = "Climax lock off (save changed)"; }
            else { cg._currentHorniness = 0f; cg._remainingCum = cg._maxCum; }
        }
        UpdateInputBlock(_open || _vis > 0.001f);
        if (Blocking && Time.frameCount % 600 == 0)
            Plugin.L.LogInfo("Input block active: " + _off.Count + " components off, " + PointerOverPatch.Hits + " pointer checks answered");
        bool brNow = _open && _browser && Gv != null;
        _vis = Mathf.MoveTowards(_vis, _open ? 1f : 0f, Dt * 3.2f);
        _brVis = Mathf.MoveTowards(_brVis, brNow ? 1f : 0f, Dt * 3.2f);
        // restart each window's entrance clock when it opens
        if (_open && !_wasOpen) _mainWin.T = 0f;
        if (brNow && !_wasBr) _brWin.T = 0f;
        _wasOpen = _open; _wasBr = brNow;
        _mainWin.T = Mathf.Min(10f, _mainWin.T + Dt);
        _brWin.T = Mathf.Min(10f, _brWin.T + Dt);
        // The base game's items may only register once a save is loaded: preload at the
        // title screen (mods), then reload once when a save first becomes available.
        if (Time.frameCount % 300 != 0) return;
        if (!_loadedInGame && Gv != null) { _loadedInGame = true; LoadItems(); return; }
        // Until a save is loaded, keep re-reading until the registry count stops changing.
        if (!_stable && Gv == null)
        {
            int before = _all.Count;
            LoadItems();
            _stable = before > 1 && _all.Count == before;
        }
    }

    void OnGUI()
    {
        try { EnsureStyles(); EnsureTris(); Draw(); }
        catch (Exception ex) { Fail("Menu", ex); }
    }

    void Draw()
    {
        var e = Event.current;
        if (e.type == EventType.KeyDown && _typing && _open && _browser)
        {
            if (e.keyCode == KeyCode.Backspace) { if (_search.Length > 0) _search = _search.Substring(0, _search.Length - 1); }
            else if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.Escape) _typing = false;
            else if (e.character >= ' ' && e.character != (char)127 && _search.Length < 40) _search += e.character;
            try { e.Use(); } catch { }
        }
        else if (e.type == EventType.KeyDown)
        {
            switch (e.keyCode)
            {
                case KeyCode.F10: _open = !_open; break;
                case KeyCode.F1: AddMoney(100000); _status = "+100,000 money"; break;
                case KeyCode.F2: RefillNeeds(); _status = "Needs refilled"; break;
                case KeyCode.F3: _freeze = !_freeze; _status = "Freeze needs: " + (_freeze ? "ON" : "OFF"); break;
                case KeyCode.F4: if (Gv != null) Gv.casinoTokens += 10; _status = "+10 casino tokens"; break;
                case KeyCode.F5: _infStamina = !_infStamina; _status = "Infinite stamina: " + (_infStamina ? "ON" : "OFF"); break;
                case KeyCode.F6: SetClimaxLock(!_climaxLock); break;
            }
        }

        // small hint fades out while the menu is open, back in when it closes
        _alpha = 1f - Ease(_vis);
        Txt(new Rect(8, 4, 240, 22), "Trainer  -  F10 menu", CDim, _link);
        _alpha = 1f;
        if (_vis > 0.001f)
        {
            HandleDrag();
            DrawWindows();
        }
        _occl.Clear();
        DrawFx();
    }

    // ---- Window layering -------------------------------------------------------
    readonly System.Collections.Generic.List<Win> _order = new System.Collections.Generic.List<Win>();   // bottom to top

    [HideFromIl2Cpp]
    bool Visible(Win w) { return w == _mainWin ? _vis > 0.001f : _brVis > 0.001f; }

    [HideFromIl2Cpp]
    Rect WinRect(Win w) { return new Rect(w.X, w.Y, w.W, w == _mainWin ? (Gv == null ? 100f : _mainH) : w.H); }

    // Draws the windows bottom to top. A click brings the window under the cursor to the front, and
    // each window is told which windows cover it so it ignores the mouse where it is covered.
    [HideFromIl2Cpp]
    void DrawWindows()
    {
        if (_order.Count == 0) { _order.Add(_mainWin); _order.Add(_brWin); }
        var e = Event.current;
        if (e.type == EventType.MouseDown && e.button == 0)
        {
            var m = RawMouse;
            for (int i = _order.Count - 1; i >= 0; i--)
            {
                var w = _order[i];
                if (!Visible(w) || !WinRect(w).Contains(m)) continue;
                if (i != _order.Count - 1) { _order.RemoveAt(i); _order.Add(w); }
                break;
            }
        }
        for (int i = 0; i < _order.Count; i++)
        {
            var w = _order[i];
            if (!Visible(w)) continue;
            _occl.Clear();
            for (int j = i + 1; j < _order.Count; j++)
                if (Visible(_order[j])) _occl.Add(WinRect(_order[j]));
            if (w == _mainWin) DrawMain(); else DrawBrowser();
        }
        _occl.Clear();
    }
}
