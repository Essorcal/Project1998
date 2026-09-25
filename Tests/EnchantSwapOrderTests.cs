using System.Buffers.Binary;
using System.Linq;
using System.Reflection;
using System.Text;
using Protocol.Tk495;
using Server;
using Shared;
using Tests.Support;
using Xunit;

namespace Tests;

/// <summary>
/// What a weapon swap and a weapon unequip put on the wire, byte for byte, and the numbers they leave behind.
///
/// <para>Written for the #286 follow-up that would move <c>DropEnchantIfWeapon</c> in <c>EquipFromSlot</c> to after
/// the new weapon is added and the gear sum is invalidated. The enchant drop pushes a stats frame, and on master
/// that frame is computed from the cached gear sum from BEFORE the swap: it shows the old weapon's max HP (and
/// clamps current HP against it) and is corrected by the stats push at the end of the swap. Moving the drop
/// changes that interim frame (and, depending on where it lands, the frame order), which the HUD shows. That is a
/// player-visible change and Caleb's call, so these facts pin master's exact sequence: any reordering of the swap
/// goes red here, and the diff of the expectation is the change to be decided on.</para>
///
/// <para>Each recording is every frame the player's own client receives for one 0x1C equip (or 0x1F unequip),
/// entered through <c>Session.Receive</c> like the read loop does, followed by current and max HP/MP, the enchant
/// multiplier and the cached gear sum. The one varying field, the player's own id inside each 0x33 look frame
/// (the world hands out ids in test order), is checked against the character and then masked.</para>
///
/// <para>The enchant is armed through <c>LuaSetEnchant</c>, which is what the enchant stance's <c>armEnchant</c>
/// calls, with Ingress's own multiplier (3). The weapons are the ones <see cref="EquipCacheInvalidationTests"/>
/// uses: rusty shortsword (+100 vita, hit 3, dam 3) and might spear (+150 vita, might 5, armor 2, dam 1), on base
/// max HP 100.</para>
/// </summary>
[Collection("world")]
public sealed class EnchantSwapOrderTests
{
    private readonly SessionFixture _fx;

    public EnchantSwapOrderTests(SessionFixture fx) => _fx = fx;

    /// <summary>Ingress up, shortsword worn, spear equipped over it, HP 200/200. The interim 0x08 (inc 4, after
    /// the "glimmer" line) reads max HP 200: the shortsword's, from the cache primed before the swap.</summary>
    [Fact]
    public void EnchantedSwapToAHigherVitaWeapon() =>
        Assert.Equal(Lf(EnchantedSwapUp), RecordSwap("enchswap_up", Shortsword, Spear, hp: 200, enchant: true));

    /// <summary>Ingress up, spear worn, shortsword equipped over it, HP 250/250. The interim 0x08 still reads
    /// 250/250 (the spear's cap); the final one brings current HP down to the shortsword's 200.</summary>
    [Fact]
    public void EnchantedSwapToALowerVitaWeapon() =>
        Assert.Equal(Lf(EnchantedSwapDown), RecordSwap("enchswap_down", Spear, Shortsword, hp: 250, enchant: true));

    /// <summary>No enchant: the same swap sends no glimmer line and one stats frame.</summary>
    [Fact]
    public void PlainSwap() =>
        Assert.Equal(Lf(PlainSwapUp), RecordSwap("enchswap_plain", Shortsword, Spear, hp: 200, enchant: false));

    /// <summary>The 0x1F unequip with Ingress up. <c>HandleUnequip</c> invalidates before the drop, so both of
    /// its stats frames already read the bare cap.</summary>
    [Fact]
    public void EnchantedUnequip() =>
        Assert.Equal(Lf(EnchantedOff), RecordUnequip("enchswap_off", Spear, hp: 250, enchant: true));

    /// <summary>The 0x1F unequip with no enchant.</summary>
    [Fact]
    public void PlainUnequip() =>
        Assert.Equal(Lf(PlainOff), RecordUnequip("enchswap_offplain", Spear, hp: 250, enchant: false));

    // ---- recordings (upstream/master @ 1e95345) ------------------------------------------------------

    private const string EnchantedSwapUp = """
frame op=0x10 inc=1 body=010C0000
frame op=0x0F inc=2 body=01C0011052757374792073686F727473776F72641052757374792073686F727473776F726400000001000000C3500000000000
frame op=0x0A inc=3 body=03003454686520676C696D6D657220737562736964657320696E746F2061207468726F6220616E64207468656E2076616E69736865732E
frame op=0x08 inc=4 body=7801040063000000C800000032FF03000003000000000000000000C8000000320000000000000000000000000000000000000000000000000000
frame op=0x38 inc=5 body=0100
frame op=0x33 inc=6 body=0005000A00<self-id>000100000000FFFF010B656E6368737761705F7570
frame op=0x37 inc=7 body=01C0340B4D696768742073706561720B4D6967687420737065617200009C400000
frame op=0x33 inc=8 body=0005000A00<self-id>00010000000080FF010B656E6368737761705F7570
frame op=0x08 inc=9 body=7801040063000000FA00000032FF03000003000000000000000000C8000000320000000000000000000000000000000000000000000000000000
frame op=0x19 inc=10 body=0003019B6403000100000000
hp=200 mp=50 maxHp=250 maxMp=50
enchant=1 active=False
gear=(150, 0, 5, 0, 0, 2, 0, 1)
worn=1:123
bag=0:520
""";

    private const string EnchantedSwapDown = """
frame op=0x10 inc=1 body=010C0000
frame op=0x0F inc=2 body=01C0340B4D696768742073706561720B4D69676874207370656172000000010000009C400000000000
frame op=0x0A inc=3 body=03003454686520676C696D6D657220737562736964657320696E746F2061207468726F6220616E64207468656E2076616E69736865732E
frame op=0x08 inc=4 body=7801040063000000FA00000032FF03000003000000000000000000FA000000320000000000000000000000000000000000000000000000000000
frame op=0x38 inc=5 body=0100
frame op=0x33 inc=6 body=0005000A00<self-id>000100000000FFFF010D656E6368737761705F646F776E
frame op=0x37 inc=7 body=01C0011052757374792073686F727473776F72641052757374792073686F727473776F72640000C3500000
frame op=0x33 inc=8 body=0005000A00<self-id>00010000000000FF010D656E6368737761705F646F776E
frame op=0x08 inc=9 body=7801040063000000C800000032FF03000003000000000000000000C8000000320000000000000000000000000000000000000000000000000000
frame op=0x19 inc=10 body=0003019B6403000100000000
hp=200 mp=50 maxHp=200 maxMp=50
enchant=1 active=False
gear=(100, 0, 0, 0, 0, 0, 3, 3)
worn=1:520
bag=0:123
""";

    private const string PlainSwapUp = """
frame op=0x10 inc=0 body=010C0000
frame op=0x0F inc=1 body=01C0011052757374792073686F727473776F72641052757374792073686F727473776F726400000001000000C3500000000000
frame op=0x38 inc=2 body=0100
frame op=0x33 inc=3 body=0005000A00<self-id>000100000000FFFF010E656E6368737761705F706C61696E
frame op=0x37 inc=4 body=01C0340B4D696768742073706561720B4D6967687420737065617200009C400000
frame op=0x33 inc=5 body=0005000A00<self-id>00010000000080FF010E656E6368737761705F706C61696E
frame op=0x08 inc=6 body=7801040063000000FA00000032FF03000003000000000000000000C8000000320000000000000000000000000000000000000000000000000000
frame op=0x19 inc=7 body=0003019B6403000100000000
hp=200 mp=50 maxHp=250 maxMp=50
enchant=1 active=False
gear=(150, 0, 5, 0, 0, 2, 0, 1)
worn=1:123
bag=0:520
""";

    private const string EnchantedOff = """
frame op=0x0F inc=1 body=01C0340B4D696768742073706561720B4D69676874207370656172000000010000009C400000000000
frame op=0x0A inc=2 body=03003454686520676C696D6D657220737562736964657320696E746F2061207468726F6220616E64207468656E2076616E69736865732E
frame op=0x08 inc=3 body=78010400630000006400000032FF0300000300000000000000000064000000320000000000000000000000000000000000000000000000000000
frame op=0x38 inc=4 body=0100
frame op=0x33 inc=5 body=0005000A00<self-id>000100000000FFFF010C656E6368737761705F6F6666
frame op=0x08 inc=6 body=78010400630000006400000032FF0300000300000000000000000064000000320000000000000000000000000000000000000000000000000000
frame op=0x19 inc=7 body=0003019A6403000100000000
hp=100 mp=50 maxHp=100 maxMp=50
enchant=1 active=False
gear=(0, 0, 0, 0, 0, 0, 0, 0)
worn=
bag=0:123
""";

    private const string PlainOff = """
frame op=0x0F inc=0 body=01C0340B4D696768742073706561720B4D69676874207370656172000000010000009C400000000000
frame op=0x38 inc=1 body=0100
frame op=0x33 inc=2 body=0005000A00<self-id>000100000000FFFF0111656E6368737761705F6F6666706C61696E
frame op=0x08 inc=3 body=78010400630000006400000032FF0300000300000000000000000064000000320000000000000000000000000000000000000000000000000000
frame op=0x19 inc=4 body=0003019A6403000100000000
hp=100 mp=50 maxHp=100 maxMp=50
enchant=1 active=False
gear=(0, 0, 0, 0, 0, 0, 0, 0)
worn=
bag=0:123
""";

    // ---- fixture -------------------------------------------------------------------------------------

    /// <summary>A raw literal takes the source file's line endings; the recording always uses \n.</summary>
    private static string Lf(string recording) => recording.ReplaceLineEndings("\n");

    private static ItemDef Shortsword => Content.Items.First(i => i.Key == "rusty_shortsword");
    private static ItemDef Spear => Content.Items.First(i => i.Key == "might_spear");

    private string RecordSwap(string name, ItemDef worn, ItemDef incoming, uint hp, bool enchant)
    {
        var (session, outbound, character) = _fx.PlayerWith(name, c =>
        {
            Shape(c, hp);
            c.Equipment.Add(new InvItem { Slot = worn.EquipSlot, ItemId = worn.Id, Dura = worn.Durability });
            c.Inventory.Add(new InvItem { Slot = 0, ItemId = incoming.Id, Dura = incoming.Durability });
        });
        Arm(session, enchant);
        outbound.Clear();

        session.Receive(SessionFixture.Frame(ClientOp.UseItem, new byte[] { 1 }));   // bag slot 1, 1-based

        return Render(session, outbound, character);
    }

    private string RecordUnequip(string name, ItemDef worn, uint hp, bool enchant)
    {
        var (session, outbound, character) = _fx.PlayerWith(name, c =>
        {
            Shape(c, hp);
            c.Equipment.Add(new InvItem { Slot = worn.EquipSlot, ItemId = worn.Id, Dura = worn.Durability });
        });
        Arm(session, enchant);
        outbound.Clear();

        session.Receive(SessionFixture.Frame(ClientOp.Unequip, new byte[] { worn.EquipSlot }));

        return Render(session, outbound, character);
    }

    /// <summary>Level 99 and might 255 clear every wear gate; base max HP 100 puts current HP above the bare cap,
    /// so a stats frame computed against the wrong gear would clamp it.</summary>
    private static void Shape(Character c, uint hp)
    {
        Assert.Equal(Shortsword.EquipSlot, Spear.EquipSlot);
        c.Level = 99;
        c.Might = 255;
        c.MaxHp = 100;
        c.Hp = hp;
        c.MaxMp = 50;
        c.Mp = 50;
        if (Spear.Sex < 2) c.Sex = Spear.Sex;
    }

    /// <summary>Arms Ingress (or, without an enchant, just reads the effective cap), either way leaving the gear sum
    /// primed with the worn weapon counted, as any stats push in play would.</summary>
    private static void Arm(Session session, bool enchant)
    {
        double ingress = Content.EnchantFor(Content.SpellByKey("ingress_warrior")!)!.Value.Amt;
        Assert.Equal(3.0, ingress);
        session.WithState(() =>
        {
            if (enchant) session.LuaSetEnchant(ingress);
            else _ = session.LuaMaxHp;
        });
        Assert.Equal(enchant, session.LuaEnchantActive);
    }

    private static string Render(Session session, RecordingOutbound outbound, Character character)
    {
        var sb = new StringBuilder();
        foreach (var frame in outbound.Frames)
        {
            Assert.True(TkPacket.TryParse(frame, out var pkt, out _));
            var body = TkCrypt.Crypt(pkt.Body, pkt.Increment, TkCrypt.LoginKey);
            var hex = Convert.ToHexString(body);
            if (pkt.Opcode == ServerOp.PlayerLook)
            {
                // x(2) y(2) dir(1) id(4 BE), Session.Entity's 0x33 writer
                Assert.Equal(character.Id, BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(5)));
                hex = hex[..10] + "<self-id>" + hex[18..];
            }
            sb.Append($"frame op=0x{pkt.Opcode:X2} inc={pkt.Increment} body={hex}\n");
        }
        session.WithState(() =>
        {
            var enchant = (double)typeof(Session).GetField("_enchantAmount", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!;
            var totals = typeof(Session).GetMethod("EquipTotals", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(session, null);
            sb.Append($"hp={character.Hp} mp={character.Mp} maxHp={session.LuaMaxHp} maxMp={session.LuaMaxMp}\n");
            sb.Append($"enchant={enchant} active={session.LuaEnchantActive}\n");
            sb.Append($"gear={totals}\n");
            sb.Append($"worn={string.Join(",", character.Equipment.Select(e => $"{e.Slot}:{e.ItemId}"))}\n");
            sb.Append($"bag={string.Join(",", character.Inventory.Select(e => $"{e.Slot}:{e.ItemId}"))}");
        });
        return sb.ToString();
    }
}
